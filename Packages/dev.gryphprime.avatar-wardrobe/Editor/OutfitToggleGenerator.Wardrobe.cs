using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

// Explicit avatar/outfit services shared by the browser and Unity tools.
namespace OutfitToggleGenerator
{
    internal static partial class OutfitToggleGenerator
    {
        // Used by Avatar Wardrobe. Keeping the menu creation here lets Wardrobe reuse
        // the existing MA control, parameter, and icon conventions.
        // Wardrobe menu model: one submenu per outfit under the Wardrobe root.
        // Each submenu holds a master toggle (whole outfit, shared radio parameter —
        // MA allocates values automatically and the isDefault toggle wins) plus one
        // toggle per renderer-bearing part. Masters are inverted with Active=false:
        // an unselected outfit is driven to 0 explicitly, while the selected one
        // falls through to scene state — so every managed outfit instance stays
        // active in the scene (visible while editing, switching only at runtime).
        private const string WardrobeOutfitParameter = GeneratedParameterPrefix + "Wardrobe";
        // Display label of the per-outfit master toggle. Submenus keep the outfit
        // name (and carry its full render as their icon); parts keep their own names.
        private static string WardrobeMasterLabel
        {
            get { return WardrobeStrings.T("toggle.master"); }
        }

        private static bool GeneratedKind(OutfitToggleGeneratedMenu marker, string kind, string legacyName)
        {
            return marker != null && (marker.generatedKind == kind ||
                (string.IsNullOrEmpty(marker.generatedKind) && marker.name == legacyName));
        }

        private static Transform FindGeneratedHost(Transform parent, string kind, string legacyName)
        {
            if (parent == null) return null;
            return parent.GetComponentsInChildren<OutfitToggleGeneratedMenu>(true)
                .Where(marker => marker.transform.parent == parent && GeneratedKind(marker, kind, legacyName))
                .Select(marker => marker.transform).FirstOrDefault() ?? parent.Find(legacyName);
        }

        private static OutfitToggleGeneratedMenu TagGenerated(GameObject target, string kind, string owner = "")
        {
            var marker = target.GetComponent<OutfitToggleGeneratedMenu>() ?? Undo.AddComponent<OutfitToggleGeneratedMenu>(target);
            Undo.RecordObject(marker, "Identify generated wardrobe object");
            marker.generatedKind = kind;
            marker.ownerId = owner;
            EditorUtility.SetDirty(marker);
            return marker;
        }

        private const string PresetSelectorName = "Avatar Wardrobe Preset Selector";

        internal static void RegeneratePresetToggles(VRCAvatarDescriptor avatar)
        {
            SyncPresetSelection(avatar, AvatarWardrobePresets.SeparateAvatarUploads);
            SyncMenuGroups(avatar);
            if (avatar != null)
                foreach (var marker in avatar.GetComponentsInChildren<OutfitToggleGeneratedMenu>(true)
                    .Where(marker => GeneratedKind(marker, "part-toggles", PartTogglesHost) && marker.transform.parent != null).ToArray())
                    GeneratePartToggles(avatar, marker.transform.parent.gameObject);
        }

        // Compatibility entry point for existing callers. Presets organize content;
        // only explicit menu groups generate outfit/hair switching controls.
        internal static void SyncPresetSelection(VRCAvatarDescriptor avatar, bool separate)
        {
            if (avatar == null) return;
            foreach (var marker in avatar.GetComponentsInChildren<OutfitToggleGeneratedMenu>(true)
                .Where(m => GeneratedKind(m, "preset-selector", PresetSelectorName)).ToArray())
                Undo.DestroyObjectImmediate(marker.gameObject);
            var legacyMenu = FindGeneratedHost(avatar.transform, "wardrobe-menu", WardrobeMenuName);
            if (legacyMenu == null || legacyMenu.GetComponent<OutfitToggleGeneratedMenu>() == null) return;
            foreach (var item in legacyMenu.GetComponentsInChildren<ModularAvatarMenuItem>(true)
                .Where(item => item.Control?.parameter?.name == WardrobeOutfitParameter &&
                    item.GetComponent<ModularAvatarObjectToggle>() != null).ToArray())
                Undo.DestroyObjectImmediate(item.gameObject);
        }

        private const string MenuGroupsHost = "Avatar Wardrobe Menu Groups";
        private const int MenuGroupsLayoutVersion = 4;
        internal static void MigrateMenuGroups(VRCAvatarDescriptor avatar)
        {
            if (avatar == null) return;
            SyncPresetSelection(avatar, AvatarWardrobePresets.SeparateAvatarUploads);
            foreach (var marker in avatar.GetComponentsInChildren<OutfitToggleGeneratedMenu>(true))
            {
                if (!string.IsNullOrEmpty(marker.generatedKind)) continue;
                if (marker.name == PartTogglesHost) TagGenerated(marker.gameObject, "part-toggles");
                else if (marker.name == "Avatar Wardrobe Preset Selector") TagGenerated(marker.gameObject, "preset-selector");
                else if (marker.name == "__OutfitToggleGenerator") TagGenerated(marker.gameObject, "outfit-menu-marker");
            }
            foreach (var marker in avatar.GetComponentsInChildren<OutfitToggleGeneratedMenu>(true)
                .Where(m => GeneratedKind(m, "part-toggles", PartTogglesHost) && m.transform.parent != null).ToArray())
            {
                if (marker.name == PartTogglesHost || marker.transform.Cast<Transform>().Any(child => IsArmaturePart(child.name)))
                    GeneratePartToggles(avatar, marker.transform.parent.gameObject);
                else EnsureGeneratedMenuIcons(avatar.gameObject, marker.transform);
            }
            if (avatar != null && avatar.GetComponentsInChildren<OutfitToggleGeneratedMenu>(true)
                .Any(marker => GeneratedKind(marker, "menu-groups", MenuGroupsHost) && marker.menuGroupsLayoutVersion < MenuGroupsLayoutVersion))
                SyncMenuGroups(avatar);
        }

        private static Transform MenuGroupsParent(VRCAvatarDescriptor avatar,
            AvatarWardrobePresets.WardrobePreset preset, bool staging)
        {
            if (preset.id == AvatarWardrobePresets.CommonTarget) return avatar.transform;
            if (!staging && string.IsNullOrEmpty(preset.legacyPath))
            {
                AvatarWardrobePresets.EnsureSceneHolder(preset.id);
                preset = AvatarWardrobePresets.GetPreset(preset.id);
            }
            var path = staging ? "Outfits/" + preset.outfitName : preset.legacyPath;
            var parent = string.IsNullOrEmpty(path) ? null : avatar.transform.Find(path);
            if (parent == null || parent == avatar.transform)
                throw new InvalidOperationException("The preset folder is missing. Refresh the preset before generating its menu groups.");
            return parent;
        }

        internal static void SyncMenuGroups(VRCAvatarDescriptor avatar, AvatarWardrobePresets.WardrobePreset staging = null, Dictionary<string, GameObject> stagedTargets = null)
        {
            if (avatar == null) return;
            AvatarWardrobePresets.CurrentBase(out var key, out var unused);
            var presets = staging != null ? new List<AvatarWardrobePresets.WardrobePreset> { staging } : AvatarWardrobePresets.PresetsForBase(key);
            presets.Add(AvatarWardrobePresets.CommonPreset(staging != null ? staging.baseKey : key));
            // Resolve destinations before removing any existing controls.
            var parents = presets.Where(p => (p.menuGroups?.Count ?? 0) > 0)
                .ToDictionary(p => p.id, p => MenuGroupsParent(avatar, p, staging != null));
            foreach (var parent in parents.Values.Concat(new[] { avatar.transform }).Distinct())
            {
                var collision = parent.Find(MenuGroupsHost);
                if (collision != null && collision.GetComponent<OutfitToggleGeneratedMenu>() == null)
                    throw new InvalidOperationException("Rename the existing " + MenuGroupsHost + " object first.");
            }
            var oldHosts = avatar.GetComponentsInChildren<OutfitToggleGeneratedMenu>(true)
                .Where(marker => GeneratedKind(marker, "menu-groups", MenuGroupsHost)).Select(marker => marker.transform).ToArray();
            var previousTargets = new HashSet<GameObject>();
            var previousDefaults = new Dictionary<string, GameObject>();
            var managedTargets = new HashSet<GameObject>();
            foreach (var old in oldHosts)
            {
                foreach (var toggle in old.GetComponentsInChildren<ModularAvatarObjectToggle>(true))
                    foreach (var obj in toggle.Objects)
                    {
                        var target = obj.Object?.Get(toggle);
                        if (target != null) previousTargets.Add(target);
                    }
                foreach (var item in old.GetComponentsInChildren<ModularAvatarMenuItem>(true))
                {
                    var parameter = item.Control?.parameter?.name;
                    var toggle = item.GetComponent<ModularAvatarObjectToggle>();
                    if (!item.isDefault || string.IsNullOrEmpty(parameter) || toggle == null) continue;
                    var target = toggle.Objects.Select(obj => obj.Object?.Get(toggle)).FirstOrDefault(obj => obj != null);
                    if (target != null) previousDefaults[parameter] = target;
                }
                Undo.DestroyObjectImmediate(old.gameObject);
            }
            foreach (var preset in presets)
            {
                GameObject host = null;
                foreach (var group in preset.menuGroups ?? new List<AvatarWardrobePresets.MenuGroup>())
                {
                    var targets = new List<GameObject>();
                    foreach (var path in group.paths)
                    {
                        string resolved = path;
                        if (staging != null && !string.IsNullOrEmpty(preset.legacyPath) &&
                            (path == preset.legacyPath || path.StartsWith(preset.legacyPath + "/", StringComparison.Ordinal)))
                            resolved = "Outfits/" + preset.outfitName + path.Substring(preset.legacyPath.Length);
                        var target = avatar.transform.Find(resolved);
                        if (stagedTargets != null && stagedTargets.TryGetValue(path, out var stagedTarget))
                            target = stagedTarget != null ? stagedTarget.transform : null;
                        if (target != null && target != avatar.transform) targets.Add(target.gameObject);
                    }
                    if (targets.Count == 0) continue;
                    foreach (var target in targets) managedTargets.Add(target);
                    if (host == null)
                    {
                        host = new GameObject(preset.id == AvatarWardrobePresets.CommonTarget ? "Avatar Wardrobe" : preset.name);
                        Undo.RegisterCreatedObjectUndo(host, "Generate menu groups");
                        host.transform.SetParent(parents[preset.id], false);
                        TagGenerated(host, "menu-groups", preset.id).menuGroupsLayoutVersion = MenuGroupsLayoutVersion;
                        Undo.AddComponent<ModularAvatarMenuInstaller>(host);
                        if (preset.id == AvatarWardrobePresets.CommonTarget)
                            Undo.AddComponent<ModularAvatarMenuGroup>(host);
                        else
                        {
                            var presetMenu = Undo.AddComponent<ModularAvatarMenuItem>(host);
                            presetMenu.MenuSource = SubmenuSource.Children;
                            presetMenu.label = preset.name;
                            presetMenu.Control = new VRCExpressionsMenu.Control {
                                name = preset.name, type = VRCExpressionsMenu.Control.ControlType.SubMenu
                            };
                        }
                    }
                    var folder = new GameObject(group.name);
                    Undo.RegisterCreatedObjectUndo(folder, "Generate menu group");
                    folder.transform.SetParent(host.transform, false);
                    TagGenerated(folder, "menu-group", group.id);
                    var submenu = Undo.AddComponent<ModularAvatarMenuItem>(folder);
                    submenu.MenuSource = SubmenuSource.Children;
                    submenu.Control = new VRCExpressionsMenu.Control { name = group.name, type = VRCExpressionsMenu.Control.ControlType.SubMenu };
                    var parameterName = GeneratedParameterPrefix + "Group_" + group.id;
                    GameObject previousDefault;
                    var defaultTarget = previousDefaults.TryGetValue(parameterName, out previousDefault) && targets.Contains(previousDefault)
                        ? previousDefault : targets.FirstOrDefault(t => t.activeSelf) ?? targets[0];
                    foreach (var peer in targets.Distinct())
                    {
                        Undo.RecordObject(peer, "Set menu group default");
                        // An inverse off-rule leaves the selected item at its enabled baseline.
                        peer.SetActive(true);
                        PrefabUtility.RecordPrefabInstancePropertyModifications(peer);
                    }
                    foreach (var target in targets.Distinct())
                    {
                        // Remove our ordinary item toggle to avoid two menus driving the same object.
                        RemoveWardrobeOutfit(avatar, target);
                        var control = new GameObject(target.name);
                        Undo.RegisterCreatedObjectUndo(control, "Generate menu group option");
                        control.transform.SetParent(folder.transform, false);
                        TagGenerated(control, "menu-group-option", group.id);
                        var item = Undo.AddComponent<ModularAvatarMenuItem>(control);
                        item.automaticValue = true;
                        item.isDefault = target == defaultTarget;
                        item.Control = new VRCExpressionsMenu.Control {
                            name = target.name, type = VRCExpressionsMenu.Control.ControlType.Toggle,
                            parameter = new VRCExpressionsMenu.Control.Parameter { name = parameterName }, value = 1
                        };
                        var toggle = Undo.AddComponent<ModularAvatarObjectToggle>(control);
                        toggle.Inverted = true;
                        toggle.Objects.Add(new ToggledObject { Object = new AvatarObjectReference(target), Active = false });
                    }
                }
                if (host != null) EnsureGeneratedMenuIcons(avatar.gameObject, host.transform);
            }
            foreach (var target in previousTargets.Where(t => t != null && !managedTargets.Contains(t)))
            {
                Undo.RecordObject(target, "Remove menu group restriction");
                target.SetActive(true);
                PrefabUtility.RecordPrefabInstancePropertyModifications(target);
            }
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(avatar.gameObject.scene);
        }

        private static bool IsArmaturePart(string name)
        {
            return !string.IsNullOrEmpty(name) && name.IndexOf("armature", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private const string PartTogglesHost = "Avatar Wardrobe Part Toggles";
        internal static bool HasPartToggles(GameObject prefab)
        {
            var host = prefab != null ? FindGeneratedHost(prefab.transform, "part-toggles", PartTogglesHost) : null;
            return host != null && host.GetComponent<OutfitToggleGeneratedMenu>() != null;
        }

        internal static void RemovePartToggles(GameObject prefab)
        {
            if (!HasPartToggles(prefab)) return;
            var host = FindGeneratedHost(prefab.transform, "part-toggles", PartTogglesHost);
            foreach (var item in host.GetComponentsInChildren<ModularAvatarMenuItem>(true))
            {
                var toggle = item.GetComponent<ModularAvatarObjectToggle>();
                if (toggle == null) continue;
                foreach (var obj in toggle.Objects)
                {
                    var target = obj.Object?.Get(toggle);
                    if (target == null) continue;
                    Undo.RecordObject(target, "Restore part default");
                    target.SetActive(item.isDefault);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(target);
                }
            }
            Undo.DestroyObjectImmediate(host.gameObject);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(prefab.scene);
        }

        internal static void GeneratePartToggles(VRCAvatarDescriptor avatar, GameObject prefab)
        {
            if (avatar == null || prefab == null) return;
            var old = FindGeneratedHost(prefab.transform, "part-toggles", PartTogglesHost);
            var previous = new Dictionary<GameObject, ModularAvatarMenuItem>();
            if (old != null)
            {
                if (old.GetComponent<OutfitToggleGeneratedMenu>() == null)
                    throw new InvalidOperationException("Rename the existing " + PartTogglesHost + " object first.");
                foreach (var item in old.GetComponentsInChildren<ModularAvatarMenuItem>(true))
                {
                    var toggle = item.GetComponent<ModularAvatarObjectToggle>();
                    if (toggle == null) continue;
                    foreach (var obj in toggle.Objects)
                    {
                        var target = obj.Object?.Get(toggle);
                        if (target != null) previous[target] = item;
                    }
                }
            }
            var parts = new List<GameObject>();
            foreach (Transform child in prefab.transform)
                if (child != old && !IsArmaturePart(child.name) && child.GetComponent<OutfitToggleGeneratedMenu>() == null) parts.Add(child.gameObject);
            var parameters = parts.ToDictionary(part => part, part => previous.ContainsKey(part)
                ? previous[part].Control.parameter.name : GeneratedParameterPrefix + "Part_" + Guid.NewGuid().ToString("N"));
            var defaults = parts.ToDictionary(part => part, part => previous.ContainsKey(part) ? previous[part].isDefault : part.activeSelf);
            if (old != null) Undo.DestroyObjectImmediate(old.gameObject);
            var host = new GameObject(prefab.name);
            Undo.RegisterCreatedObjectUndo(host, "Generate prefab part toggles");
            host.transform.SetParent(prefab.transform, false);
            TagGenerated(host, "part-toggles");
            Undo.AddComponent<ModularAvatarMenuInstaller>(host);
            var submenu = Undo.AddComponent<ModularAvatarMenuItem>(host);
            submenu.MenuSource = SubmenuSource.Children;
            submenu.label = prefab.name;
            submenu.Control = new VRCExpressionsMenu.Control { name = prefab.name, type = VRCExpressionsMenu.Control.ControlType.SubMenu };
            foreach (var part in parts)
            {
                var control = new GameObject(part.name);
                Undo.RegisterCreatedObjectUndo(control, "Generate independent part toggle");
                control.transform.SetParent(host.transform, false);
                TagGenerated(control, "part-toggle");
                var item = Undo.AddComponent<ModularAvatarMenuItem>(control);
                item.automaticValue = false;
                item.isDefault = defaults[part];
                item.Control = new VRCExpressionsMenu.Control {
                    name = part.name, type = VRCExpressionsMenu.Control.ControlType.Toggle, value = 1,
                    parameter = new VRCExpressionsMenu.Control.Parameter { name = parameters[part] }
                };
                var toggle = Undo.AddComponent<ModularAvatarObjectToggle>(control);
                toggle.Inverted = true;
                toggle.Objects.Add(new ToggledObject { Object = new AvatarObjectReference(part), Active = false });
                Undo.RecordObject(part, "Enable part toggle baseline");
                part.SetActive(true);
                PrefabUtility.RecordPrefabInstancePropertyModifications(part);
            }
            EnsureGeneratedMenuIcons(avatar.gameObject, host.transform);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(avatar.gameObject.scene);
        }

        internal static void CreateOrUpdateWardrobeToggle(
            VRCAvatarDescriptor avatar,
            GameObject outfitRoot,
            string label)
        {
            if (avatar == null || outfitRoot == null) return;

            // Legacy callers requesting generated controls get part toggles only.
            // Whole-item switching is owned exclusively by SyncMenuGroups.
            GeneratePartToggles(avatar, outfitRoot);
        }

        internal static void RemoveWardrobeOutfit(VRCAvatarDescriptor avatar, GameObject instance)
        {
            if (avatar == null || instance == null) return;
            var menuRoot = FindGeneratedHost(avatar.transform, "wardrobe-menu", WardrobeMenuName);
            if (menuRoot == null) return;
            var path = new AvatarObjectReference(instance).referencePath;

            var removedDefault = false;
            var dead = new List<GameObject>();
            foreach (Transform child in menuRoot)
            {
                var menu = child.GetComponent<ModularAvatarMenuItem>();
                if (menu == null) continue;
                if (menu.Control?.type == VRCExpressionsMenu.Control.ControlType.SubMenu)
                {
                    if (SubmenuReferences(child, path))
                    {
                        if (SubmenuHasDefault(child)) removedDefault = true;
                        dead.Add(child.gameObject);
                    }
                    continue;
                }
                var toggle = child.GetComponent<ModularAvatarObjectToggle>();
                if (toggle != null && IsGeneratedToggle(toggle) && ToggleReferences(toggle, path))
                {
                    if (menu.isDefault) removedDefault = true;
                    dead.Add(child.gameObject);
                }
            }
            foreach (var lifeless in dead) Undo.DestroyObjectImmediate(lifeless);

            if (!removedDefault) return;
            var lastMaster = LastMasterMenuItem(menuRoot);
            if (lastMaster != null)
            {
                Undo.RecordObject(lastMaster, "Set default wardrobe outfit");
                lastMaster.isDefault = true;
                EditorUtility.SetDirty(lastMaster);
            }
        }

        private static bool ToggleReferences(ModularAvatarObjectToggle toggle, string path)
        {
            return toggle.Objects.Any(entry =>
            {
                var referencePath = entry.Object?.referencePath;
                return referencePath == path ||
                       referencePath != null && referencePath.StartsWith(path + "/", StringComparison.Ordinal);
            });
        }

        private static bool SubmenuReferences(Transform submenu, string path)
        {
            return submenu.Cast<Transform>().Any(grandchild =>
            {
                var toggle = grandchild.GetComponent<ModularAvatarObjectToggle>();
                return toggle != null && IsGeneratedToggle(toggle) && ToggleReferences(toggle, path);
            });
        }

        private static bool SubmenuHasDefault(Transform submenu)
        {
            return submenu.Cast<Transform>().Any(grandchild =>
            {
                var menu = grandchild.GetComponent<ModularAvatarMenuItem>();
                var toggle = grandchild.GetComponent<ModularAvatarObjectToggle>();
                return menu != null && toggle != null && IsGeneratedToggle(toggle) &&
                       menu.Control?.parameter?.name == WardrobeOutfitParameter && menu.isDefault;
            });
        }

        private static ModularAvatarMenuItem LastMasterMenuItem(Transform menuRoot)
        {
            ModularAvatarMenuItem last = null;
            foreach (Transform submenu in menuRoot)
            foreach (Transform child in submenu)
            {
                var menu = child.GetComponent<ModularAvatarMenuItem>();
                var toggle = child.GetComponent<ModularAvatarObjectToggle>();
                if (menu == null || toggle == null || !IsGeneratedToggle(toggle)) continue;
                if (menu.Control?.parameter?.name != WardrobeOutfitParameter) continue;
                last = menu;
            }
            return last;
        }

        private static void MigrateLegacyFlatToggles(VRCAvatarDescriptor avatar, Transform menuRoot)
        {
            var flats = new List<ModularAvatarObjectToggle>();
            foreach (Transform child in menuRoot)
            {
                var toggle = child.GetComponent<ModularAvatarObjectToggle>();
                var menu = child.GetComponent<ModularAvatarMenuItem>();
                if (toggle == null || menu == null || !IsGeneratedToggle(toggle)) continue;
                if (menu.Control?.type == VRCExpressionsMenu.Control.ControlType.SubMenu) continue;
                flats.Add(toggle);
            }
            foreach (var flat in flats)
            {
                var instance = ResolveOutfitInstance(avatar, flat);
                var label = flat.GetComponent<ModularAvatarMenuItem>()?.label;
                if (string.IsNullOrWhiteSpace(label)) label = flat.name;
                Undo.DestroyObjectImmediate(flat.gameObject);
                if (instance == null) continue;
                var submenu = FindOrCreateOutfitSubmenu(menuRoot, label);
                SyncOutfitSubmenu(avatar, submenu, instance, label);
            }
        }

        private static GameObject ResolveOutfitInstance(VRCAvatarDescriptor avatar, ModularAvatarObjectToggle toggle)
        {
            foreach (var entry in toggle.Objects)
            {
                var referencePath = entry.Object?.referencePath;
                if (string.IsNullOrEmpty(referencePath)) continue;
                var found = avatar.transform.Find(referencePath);
                if (found != null) return found.gameObject;
            }
            return null;
        }

        private static Transform FindSubmenuForOutfit(Transform menuRoot, string path)
        {
            foreach (Transform child in menuRoot)
            {
                var menu = child.GetComponent<ModularAvatarMenuItem>();
                if (menu == null || menu.Control?.type != VRCExpressionsMenu.Control.ControlType.SubMenu) continue;
                if (SubmenuReferences(child, path)) return child;
            }
            return null;
        }

        private static Transform FindOrCreateOutfitSubmenu(Transform menuRoot, string label)
        {
            foreach (Transform child in menuRoot)
            {
                var menu = child.GetComponent<ModularAvatarMenuItem>();
                if (menu == null || menu.Control?.type != VRCExpressionsMenu.Control.ControlType.SubMenu) continue;
                if (child.name != label || menu.MenuSource != SubmenuSource.Children) continue;
                // Never adopt a submenu with foreign (non-generated) toggles.
                var foreign = child.Cast<Transform>().Any(grandchild =>
                    grandchild.GetComponent<ModularAvatarObjectToggle>() != null &&
                    !IsGeneratedToggle(grandchild.GetComponent<ModularAvatarObjectToggle>()));
                if (!foreign) return child;
            }
            var submenuObject = new GameObject(label);
            Undo.RegisterCreatedObjectUndo(submenuObject, "Create wardrobe outfit menu");
            Undo.SetTransformParent(submenuObject.transform, menuRoot, "Create wardrobe outfit menu");
            var submenuItem = Undo.AddComponent<ModularAvatarMenuItem>(submenuObject);
            submenuItem.label = label;
            submenuItem.Control = new VRCExpressionsMenu.Control
            {
                type = VRCExpressionsMenu.Control.ControlType.SubMenu,
                name = label,
            };
            submenuItem.MenuSource = SubmenuSource.Children;
            EditorUtility.SetDirty(submenuItem);
            return submenuObject.transform;
        }

        private static void SyncOutfitSubmenu(
            VRCAvatarDescriptor avatar,
            Transform submenu,
            GameObject outfitRoot,
            string label)
        {
            var path = new AvatarObjectReference(outfitRoot).referencePath;
            var master = FindMasterToggle(submenu, path);
            Texture2D masterIcon = null;
            if (master == null)
            {
                var toggleObject = new GameObject(WardrobeMasterLabel);
                Undo.RegisterCreatedObjectUndo(toggleObject, "Create wardrobe toggle");
                Undo.SetTransformParent(toggleObject.transform, submenu, "Create wardrobe toggle");
                master = Undo.AddComponent<ModularAvatarObjectToggle>(toggleObject);
                master.Inverted = true;
                master.Objects.Add(new ToggledObject
                {
                    Object = new AvatarObjectReference(outfitRoot),
                    Active = false,
                });
                var menuItem = Undo.AddComponent<ModularAvatarMenuItem>(toggleObject);
                menuItem.label = WardrobeMasterLabel;
                menuItem.Control = new VRCExpressionsMenu.Control
                {
                    type = VRCExpressionsMenu.Control.ControlType.Toggle,
                    name = WardrobeMasterLabel,
                    value = 1,
                    parameter = new VRCExpressionsMenu.Control.Parameter { name = WardrobeOutfitParameter },
                };
                menuItem.automaticValue = true;
                var icons = RenderIcons(avatar.gameObject, new[] { outfitRoot });
                Texture2D fresh;
                if (icons.TryGetValue(outfitRoot, out fresh))
                    masterIcon = menuItem.Control.icon = SaveIcon(fresh, outfitRoot);
                EditorUtility.SetDirty(master);
                EditorUtility.SetDirty(menuItem);
            }
            else
            {
                var menuItem = master.GetComponent<ModularAvatarMenuItem>();
                Undo.RecordObject(master, "Update wardrobe toggle");
                Undo.RecordObject(master.gameObject, "Update wardrobe toggle");
                master.Inverted = true;
                master.Objects.Clear();
                master.Objects.Add(new ToggledObject
                {
                    Object = new AvatarObjectReference(outfitRoot),
                    Active = false,
                });
                if (menuItem.Control == null)
                    menuItem.Control = new VRCExpressionsMenu.Control
                    {
                        type = VRCExpressionsMenu.Control.ControlType.Toggle,
                        value = 1,
                    };
                menuItem.Control.parameter = new VRCExpressionsMenu.Control.Parameter { name = WardrobeOutfitParameter };
                menuItem.automaticValue = true;
                menuItem.label = WardrobeMasterLabel;
                menuItem.Control.name = WardrobeMasterLabel;
                menuItem.gameObject.name = WardrobeMasterLabel;
                if (menuItem.Control.icon != null) masterIcon = menuItem.Control.icon;
                EditorUtility.SetDirty(master);
                EditorUtility.SetDirty(menuItem);
            }
            EnsureSubmenuIcon(avatar.gameObject, submenu, outfitRoot, master, masterIcon);
            SyncComponentToggles(avatar.gameObject, submenu, outfitRoot, path, label);
        }

        // The submenu button shows the whole-outfit render; the master toggle keeps
        // its own copy. Missing icons are filled once and then left alone, so
        // re-installs never stack duplicate PNGs or re-render for free.
        private static void EnsureSubmenuIcon(
            GameObject avatarRoot,
            Transform submenu,
            GameObject outfitRoot,
            ModularAvatarObjectToggle master,
            Texture2D masterIcon)
        {
            var submenuItem = submenu.GetComponent<ModularAvatarMenuItem>();
            if (submenuItem == null || submenuItem.Control == null) return;
            var icon = submenuItem.Control.icon != null ? submenuItem.Control.icon : masterIcon;
            if (icon == null)
            {
                var icons = RenderIcons(avatarRoot, new[] { outfitRoot });
                Texture2D fresh;
                if (icons.TryGetValue(outfitRoot, out fresh))
                    icon = SaveIcon(fresh, outfitRoot);
            }
            if (icon == null) return;
            if (submenuItem.Control.icon != icon)
            {
                Undo.RecordObject(submenuItem, "Set wardrobe submenu icon");
                submenuItem.Control.icon = icon;
                EditorUtility.SetDirty(submenuItem);
            }
            var masterItem = master == null ? null : master.GetComponent<ModularAvatarMenuItem>();
            if (masterItem != null && masterItem.Control != null && masterItem.Control.icon == null)
            {
                Undo.RecordObject(masterItem, "Set wardrobe toggle icon");
                masterItem.Control.icon = icon;
                EditorUtility.SetDirty(masterItem);
            }
        }

        private static ModularAvatarObjectToggle FindMasterToggle(Transform submenu, string path)
        {
            foreach (Transform child in submenu)
            {
                var toggle = child.GetComponent<ModularAvatarObjectToggle>();
                if (toggle == null || !IsGeneratedToggle(toggle) || toggle.Objects.Count != 1) continue;
                if (toggle.Objects[0].Object?.referencePath == path) return toggle;
            }
            return null;
        }

        private static void SyncComponentToggles(
            GameObject avatarRoot,
            Transform submenu,
            GameObject outfitRoot,
            string outfitPath,
            string outfitLabel)
        {
            var wanted = new Dictionary<string, GameObject>(StringComparer.Ordinal);
            foreach (Transform child in outfitRoot.transform)
            {
                if (IsArmaturePart(child.name)) continue;
                if (child.GetComponentsInChildren<Renderer>(true).Length == 0) continue;
                var partPath = new AvatarObjectReference(child.gameObject).referencePath;
                if (partPath == outfitPath) continue;
                wanted[partPath] = child.gameObject;
            }
            var stale = new List<GameObject>();
            foreach (Transform child in submenu)
            {
                var toggle = child.GetComponent<ModularAvatarObjectToggle>();
                if (toggle == null || !IsGeneratedToggle(toggle) || toggle.Objects.Count != 1) continue;
                var referencePath = toggle.Objects[0].Object?.referencePath;
                if (referencePath == outfitPath) continue;
                if (!wanted.ContainsKey(referencePath)) stale.Add(child.gameObject);
            }
            foreach (var lifeless in stale) Undo.DestroyObjectImmediate(lifeless);
            var missing = wanted.Where(entry => FindMasterToggle(submenu, entry.Key) == null).ToList();
            // One scene copy for all new parts; re-installs with nothing missing skip the render entirely.
            var icons = RenderIcons(avatarRoot, missing.Select(entry => entry.Value));
            foreach (var entry in missing)
            {
                var partLabel = FriendlyToggleLabel(entry.Value.name, outfitLabel);
                if (string.IsNullOrWhiteSpace(partLabel)) partLabel = entry.Value.name;
                if (partLabel.Length > 32) partLabel = partLabel.Substring(0, 32).TrimEnd();
                var toggleObject = new GameObject(partLabel);
                Undo.RegisterCreatedObjectUndo(toggleObject, "Create wardrobe part toggle");
                Undo.SetTransformParent(toggleObject.transform, submenu, "Create wardrobe part toggle");
                var objectToggle = Undo.AddComponent<ModularAvatarObjectToggle>(toggleObject);
                objectToggle.Objects.Add(new ToggledObject
                {
                    Object = new AvatarObjectReference(entry.Value),
                    Active = !entry.Value.activeSelf,
                });
                var menuItem = Undo.AddComponent<ModularAvatarMenuItem>(toggleObject);
                menuItem.label = partLabel;
                Texture2D partIcon;
                menuItem.Control = new VRCExpressionsMenu.Control
                {
                    type = VRCExpressionsMenu.Control.ControlType.Toggle,
                    name = partLabel,
                    value = 1,
                    icon = icons.TryGetValue(entry.Value, out partIcon)
                        ? SaveIcon(partIcon, entry.Value)
                        : null,
                    parameter = new VRCExpressionsMenu.Control.Parameter
                    {
                        name = ComponentParameter(outfitLabel, entry.Value.name),
                    },
                };
                menuItem.automaticValue = false;
                menuItem.isDefault = false;
                EditorUtility.SetDirty(objectToggle);
                EditorUtility.SetDirty(menuItem);
            }
        }

        private static string ComponentParameter(string outfitLabel, string partName)
        {
            var clean = new string((outfitLabel + "/" + partName)
                .Select(c => char.IsLetterOrDigit(c) || c == '/' ? c : '_').ToArray());
            return GeneratedParameterPrefix + "Wardrobe/" + clean;
        }

        private static void SetDefaultOutfit(Transform menuRoot, Transform defaultSubmenu)
        {
            foreach (Transform submenu in menuRoot)
            foreach (Transform child in submenu)
            {
                var menu = child.GetComponent<ModularAvatarMenuItem>();
                var toggle = child.GetComponent<ModularAvatarObjectToggle>();
                if (menu == null || toggle == null || !IsGeneratedToggle(toggle)) continue;
                if (menu.Control?.parameter?.name != WardrobeOutfitParameter) continue;
                var isDefault = submenu == defaultSubmenu;
                if (menu.isDefault == isDefault) continue;
                Undo.RecordObject(menu, "Set default wardrobe outfit");
                menu.isDefault = isDefault;
                EditorUtility.SetDirty(menu);
            }
        }

        // Repairs masters installed by older Wardrobe versions (own parameter,
        // non-inverted): every generated outfit toggle converges on the shared
        // radio parameter. Part toggles (parameter "…Wardrobe/…") are never touched.
        private static void NormalizeMasterToggles(Transform menuRoot)
        {
            var partPrefix = GeneratedParameterPrefix + "Wardrobe/";
            foreach (Transform submenu in menuRoot)
            {
                var submenuItem = submenu.GetComponent<ModularAvatarMenuItem>();
                if (submenuItem == null || submenuItem.Control?.type != VRCExpressionsMenu.Control.ControlType.SubMenu) continue;
                ModularAvatarObjectToggle master = null;
                string masterPath = null;
                foreach (Transform child in submenu)
                {
                    var toggle = child.GetComponent<ModularAvatarObjectToggle>();
                    if (toggle == null || !IsGeneratedToggle(toggle) || toggle.Objects.Count != 1) continue;
                    var parameter = child.GetComponent<ModularAvatarMenuItem>()?.Control?.parameter?.name;
                    if (!string.IsNullOrEmpty(parameter) && parameter.StartsWith(partPrefix, StringComparison.Ordinal)) continue;
                    var referencePath = toggle.Objects[0].Object?.referencePath;
                    if (string.IsNullOrEmpty(referencePath)) continue;
                    if (master == null || referencePath.Length < masterPath.Length)
                    {
                        master = toggle;
                        masterPath = referencePath;
                    }
                }
                if (master == null) continue;
                var masterItem = master.GetComponent<ModularAvatarMenuItem>();
                var masterLabelOk = master.gameObject.name == WardrobeMasterLabel &&
                    (masterItem == null || masterItem.label == WardrobeMasterLabel);
                if (masterItem != null &&
                    masterItem.Control?.parameter?.name == WardrobeOutfitParameter &&
                    masterItem.automaticValue && master.Inverted && !master.Objects[0].Active &&
                    masterLabelOk) continue;
                Undo.RecordObject(master, "Normalize wardrobe toggle");
                Undo.RecordObject(master.gameObject, "Normalize wardrobe toggle");
                master.Inverted = true;
                master.gameObject.name = WardrobeMasterLabel;
                var entry = master.Objects[0];
                entry.Active = false;
                master.Objects[0] = entry;
                EditorUtility.SetDirty(master);
                if (masterItem == null) continue;
                Undo.RecordObject(masterItem, "Normalize wardrobe toggle");
                masterItem.label = WardrobeMasterLabel;
                if (masterItem.Control == null)
                    masterItem.Control = new VRCExpressionsMenu.Control
                    {
                        type = VRCExpressionsMenu.Control.ControlType.Toggle,
                        value = 1,
                    };
                masterItem.Control.parameter = new VRCExpressionsMenu.Control.Parameter { name = WardrobeOutfitParameter };
                masterItem.automaticValue = true;
                EditorUtility.SetDirty(masterItem);
            }
        }

        private static void ActivateManagedOutfits(VRCAvatarDescriptor avatar, Transform menuRoot)
        {
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (Transform submenu in menuRoot)
            foreach (Transform child in submenu)
            {
                var menu = child.GetComponent<ModularAvatarMenuItem>();
                var toggle = child.GetComponent<ModularAvatarObjectToggle>();
                if (menu == null || toggle == null || !IsGeneratedToggle(toggle)) continue;
                if (menu.Control?.parameter?.name != WardrobeOutfitParameter) continue;
                foreach (var entry in toggle.Objects)
                {
                    var referencePath = entry.Object?.referencePath;
                    if (!string.IsNullOrEmpty(referencePath)) paths.Add(referencePath);
                }
            }
            foreach (var path in paths)
            {
                var instance = avatar.transform.Find(path);
                if (instance != null && !instance.gameObject.activeSelf)
                {
                    Undo.RecordObject(instance.gameObject, "Activate wardrobe outfit");
                    instance.gameObject.SetActive(true);
                }
            }
        }

        private static Transform FindOrCreateWardrobeMenu(VRCAvatarDescriptor avatar)
        {
            var existing = FindGeneratedHost(avatar.transform, "wardrobe-menu", WardrobeMenuName);
            if (existing != null && existing.GetComponent<ModularAvatarMenuItem>() != null &&
                existing.GetComponent<ModularAvatarMenuInstaller>() != null)
            {
                TagGenerated(existing.gameObject, "wardrobe-menu");
                // Relabel so a system-language switch applies without rebuilding.
                var existingItem = existing.GetComponent<ModularAvatarMenuItem>();
                if (existingItem.label != WardrobeMenuLabel ||
                    existingItem.Control == null || existingItem.Control.name != WardrobeMenuLabel)
                {
                    Undo.RecordObject(existingItem, "Localize wardrobe menu");
                    existingItem.label = WardrobeMenuLabel;
                    if (existingItem.Control == null)
                        existingItem.Control = new VRCExpressionsMenu.Control
                        {
                            type = VRCExpressionsMenu.Control.ControlType.SubMenu,
                        };
                    existingItem.Control.name = WardrobeMenuLabel;
                    EditorUtility.SetDirty(existingItem);
                }
                return existing;
            }

            var menuObject = new GameObject(WardrobeMenuName);
            Undo.RegisterCreatedObjectUndo(menuObject, "Create wardrobe menu");
            Undo.SetTransformParent(menuObject.transform, avatar.transform, "Create wardrobe menu");

            TagGenerated(menuObject, "wardrobe-menu");
            var menuItem = Undo.AddComponent<ModularAvatarMenuItem>(menuObject);
            menuItem.label = WardrobeMenuLabel;
            menuItem.MenuSource = SubmenuSource.Children;
            menuItem.Control = new VRCExpressionsMenu.Control
            {
                type = VRCExpressionsMenu.Control.ControlType.SubMenu,
                name = WardrobeMenuLabel,
            };
            Undo.AddComponent<ModularAvatarMenuInstaller>(menuObject);
            return menuObject.transform;
        }

    }
}
