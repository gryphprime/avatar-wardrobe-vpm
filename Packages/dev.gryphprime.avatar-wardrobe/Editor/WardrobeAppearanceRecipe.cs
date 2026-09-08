using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.modular_avatar.core;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace OutfitToggleGenerator
{
    // Resolve source identity once, then apply only to owned clones. No scene/preset writer is called.
    internal static class WardrobeAppearanceRecipe
    {
        [Serializable] internal sealed class Rule { public int[] path; public bool active; }
        [Serializable] internal sealed class Recipe
        {
            public string version = "wardrobe-appearance-v1", scopeId = "", menuGroup = "";
            public bool createToggles;
            public int[] candidateParent = new int[0];
            public List<Rule> scopeRules = new List<Rule>();
            public string Identity => Hash128.Compute(JsonUtility.ToJson(this)).ToString();
        }
        internal sealed class Bound
        {
            private readonly Dictionary<GameObject, bool> values = new Dictionary<GameObject, bool>();
            internal void SetDefault(GameObject target, bool active)
            {
                if (values.TryGetValue(target, out var prior) && prior != active)
                    throw new InvalidOperationException("Generated wardrobe controls disagree about an object's default visibility. Repair the controls before previewing.");
                values[target] = active;
            }
            internal void SetScope(GameObject target, bool active)
            {
                // Selecting a preset admits its items; it must not enable every menu alternative.
                if (!active || !values.ContainsKey(target)) values[target] = active;
            }
            internal void Apply()
            {
                foreach (var pair in values)
                {
                    if (pair.Key == null) throw new InvalidOperationException("NDMF removed an object needed to resolve the requested snapshot visibility. This recipe cannot be previewed faithfully.");
                    pair.Key.SetActive(pair.Value);
                }
            }
        }
        internal static Recipe Resolve(VRCAvatarDescriptor avatar, string scopeId = "", bool createToggles = false, string menuGroup = "")
        {
            if (avatar == null) throw new InvalidOperationException("Pin an avatar before resolving its appearance.");
            var recipe = new Recipe { scopeId = scopeId ?? "", createToggles = createToggles, menuGroup = menuGroup ?? "" };
            CheckSupported(recipe);
            if (string.IsNullOrEmpty(recipe.scopeId)) return recipe;
            if (AvatarWardrobeServer.SceneAvatar != avatar)
                throw new InvalidOperationException("The requested appearance must be resolved from the pinned avatar.");
            AvatarWardrobePresets.CurrentBase(out var baseKey, out var unused);
            var preset = AvatarWardrobePresets.GetPreset(recipe.scopeId);
            var common = recipe.scopeId == AvatarWardrobePresets.CommonTarget;
            if (!common && (preset == null || preset.baseKey != baseKey))
                throw new InvalidOperationException("The requested preset does not belong to the pinned avatar.");
            var selected = common ? new List<GameObject>() : AvatarWardrobePresets.SceneMembers(preset, avatar);
            if (!common)
            {
                if (string.IsNullOrEmpty(preset.legacyPath))
                    throw new InvalidOperationException("Create this preset's scene folder before taking a scoped snapshot.");
                var parent = UniqueNamedPath(avatar.transform, preset.legacyPath);
                recipe.candidateParent = WardrobeTryOnWorker.SiblingPath(avatar.transform, parent);
            }
            var states = new Dictionary<GameObject, bool>();
            foreach (var root in AvatarWardrobePresets.PresetsForBase(baseKey).SelectMany(value => AvatarWardrobePresets.SceneMembers(value, avatar)).Distinct())
                states[root] = selected.Any(item => item == root || item.transform.IsChildOf(root.transform));
            foreach (var root in selected)
                for (var parent = root.transform.parent; parent != null && parent != avatar.transform; parent = parent.parent)
                    states[parent.gameObject] = true;
            foreach (var pair in states)
                recipe.scopeRules.Add(new Rule { path = WardrobeTryOnWorker.SiblingPath(avatar.transform, pair.Key.transform), active = pair.Value });
            return recipe;
        }
        internal static void CheckSupported(Recipe recipe)
        {
            if (recipe == null) return;
            if (recipe.version != "wardrobe-appearance-v1") throw new InvalidOperationException("This appearance recipe version is unsupported.");
            if (recipe.createToggles || !string.IsNullOrEmpty(recipe.menuGroup))
                throw new InvalidOperationException("Snapshot preview cannot yet reproduce newly generated part controls or a new Menu Group assignment. Turn off Generate part toggles and choose no new Menu Group, or apply those controls to a separate working copy before previewing it.");
        }
        internal static void PlaceCandidate(GameObject root, GameObject candidate, Recipe recipe, bool replacement)
        {
            if (candidate == null) return;
            var partHosts = candidate.GetComponentsInChildren<OutfitToggleGeneratedMenu>(true)
                .Where(marker => marker.generatedKind == "part-toggles").ToArray();
            if (partHosts.Length > 0)
            {
                // Wear with createToggles=false removes these generated controls and restores their defaults.
                Bind(candidate, null);
                foreach (var host in partHosts)
                {
                    if (host.GetComponentsInChildren<Transform>(true).Any(child => child.GetComponent<OutfitToggleGeneratedMenu>() == null))
                        throw new InvalidOperationException("Generated part controls contain user content and cannot be removed from this preview safely.");
                    UnityEngine.Object.DestroyImmediate(host.gameObject);
                }
            }
            if (replacement) return;
            var parent = WardrobeTryOnWorker.AtSiblingPath(root.transform, recipe?.candidateParent ?? new int[0]);
            // Match explicit install placement while retaining the prefab's avatar-relative position.
            candidate.transform.SetParent(parent, true);
            candidate.SetActive(true);
        }
        internal static Bound Bind(GameObject root, Recipe recipe)
        {
            CheckSupported(recipe);
            var result = new Bound();
            foreach (var marker in root.GetComponentsInChildren<OutfitToggleGeneratedMenu>(true))
            {
                if (marker.generatedKind != "menu-group-option" && marker.generatedKind != "part-toggle") continue;
                var item = marker.GetComponent<ModularAvatarMenuItem>();
                var toggle = marker.GetComponent<ModularAvatarObjectToggle>();
                if (item == null || toggle == null || !toggle.Inverted || toggle.Objects.Count == 0 || toggle.Objects.Any(value => value.Active))
                    throw new InvalidOperationException("A generated wardrobe control has an unsupported visibility rule. Repair the control before taking a snapshot.");
                foreach (var entry in toggle.Objects)
                {
                    var target = entry.Object?.Get(toggle);
                    if (target == null || (target != root && !target.transform.IsChildOf(root.transform)))
                        throw new InvalidOperationException("A generated wardrobe control refers outside the captured avatar.");
                    result.SetDefault(target, item.isDefault);
                }
            }
            if (recipe != null)
                foreach (var rule in recipe.scopeRules)
                    result.SetScope(WardrobeTryOnWorker.AtSiblingPath(root.transform, rule.path).gameObject, rule.active);
            result.Apply();
            return result;
        }
        private static Transform UniqueNamedPath(Transform root, string path)
        {
            foreach (var segment in path.Split('/'))
            {
                var matches = root.Cast<Transform>().Where(child => child.name == segment).ToArray();
                if (matches.Length != 1) throw new InvalidOperationException("The preset folder path is missing or ambiguous. Give its sibling folders distinct names before taking a scoped snapshot.");
                root = matches[0];
            }
            return root;
        }
    }
}
