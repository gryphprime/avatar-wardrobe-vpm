using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

// Blendshape Sync for worn items: item meshes follow the avatar's body shapes through MA Blendshape Sync.
namespace OutfitToggleGenerator
{
    internal static partial class OutfitToggleGenerator
    {
        private const string BlendshapeSyncKind = "blendshape-sync";
        private const string BlendshapeSyncName = "Avatar Wardrobe Blendshape Sync";

        internal static bool HasBlendshapeSync(GameObject item)
        {
            return item != null && FindGeneratedHost(item.transform, BlendshapeSyncKind, BlendshapeSyncName) != null;
        }

        // Finds the body shape an item shape should follow. Body meshes are ranked by how
        // many of the item's shapes they carry, so the mesh with the body-shape keys wins over
        // a face mesh that merely happens to be named "Body". Exact names first, then a
        // normalized key (case, separators, word order, plurals, chest/bust = breast).
        internal sealed class ShapeMatcher
        {
            private readonly Dictionary<string, (SkinnedMeshRenderer mesh, string shape)> exact =
                new Dictionary<string, (SkinnedMeshRenderer, string)>(StringComparer.Ordinal);
            private readonly Dictionary<string, (SkinnedMeshRenderer mesh, string shape)?> loose =
                new Dictionary<string, (SkinnedMeshRenderer, string)?>(StringComparer.Ordinal);

            internal ShapeMatcher(IEnumerable<SkinnedMeshRenderer> sources, IEnumerable<string> itemShapes)
            {
                var wanted = itemShapes.Where(IsSyncableShape).ToList();
                var wantedExact = new HashSet<string>(wanted, StringComparer.Ordinal);
                var wantedLoose = new HashSet<string>(wanted.Select(ShapeKey), StringComparer.Ordinal);
                var ranked = sources.Where(mesh => mesh != null && mesh.sharedMesh != null)
                    .Select((mesh, order) => (mesh, order, names: ShapeNames(mesh).ToList()))
                    .OrderByDescending(entry => entry.names.Count(wantedExact.Contains))
                    .ThenByDescending(entry => entry.names.Count(name => wantedLoose.Contains(ShapeKey(name))))
                    .ThenBy(entry => entry.order);
                foreach (var (mesh, _, names) in ranked)
                {
                    var meshLoose = new Dictionary<string, string>(StringComparer.Ordinal);
                    var ambiguous = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var name in names.Where(IsSyncableShape))
                    {
                        if (!exact.ContainsKey(name)) exact.Add(name, (mesh, name));
                        var key = ShapeKey(name);
                        if (key.Length == 0) continue;
                        if (meshLoose.TryGetValue(key, out var other) && other != name) ambiguous.Add(key);
                        else meshLoose[key] = name;
                    }
                    foreach (var pair in meshLoose)
                    {
                        if (loose.ContainsKey(pair.Key)) continue;
                        loose.Add(pair.Key, ambiguous.Contains(pair.Key) ? ((SkinnedMeshRenderer, string)?)null : (mesh, pair.Value));
                    }
                }
            }

            internal bool TryMatch(string shape, out SkinnedMeshRenderer mesh, out string bodyShape)
            {
                mesh = null;
                bodyShape = null;
                if (!IsSyncableShape(shape)) return false;
                if (!exact.TryGetValue(shape, out var hit))
                {
                    if (!loose.TryGetValue(ShapeKey(shape), out var found) || !found.HasValue) return false;
                    hit = found.Value;
                }
                mesh = hit.mesh;
                bodyShape = hit.shape;
                return true;
            }
        }

        private static IEnumerable<string> ShapeNames(SkinnedMeshRenderer renderer)
        {
            var mesh = renderer == null ? null : renderer.sharedMesh;
            if (mesh == null) yield break;
            for (var index = 0; index < mesh.blendShapeCount; index++) yield return mesh.GetBlendShapeName(index);
        }

        // Visemes and "----Section----" divider shapes are never synced.
        internal static bool IsSyncableShape(string name)
        {
            return !string.IsNullOrEmpty(name) && !name.StartsWith("vrc.", StringComparison.OrdinalIgnoreCase) &&
                   name.Any(char.IsLetterOrDigit);
        }

        private static readonly Dictionary<string, string> ShapeSynonyms = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "breasts", "breast" }, { "chest", "breast" }, { "bust", "breast" }, { "boob", "breast" }, { "boobs", "breast" },
            { "oppai", "breast" }, { "large", "big" }, { "legs", "leg" }, { "hips", "hip" }, { "thighs", "thigh" },
        };

        // "Breasts_big", "Big Breast" and "BreastBig" all become "big breast".
        internal static string ShapeKey(string name)
        {
            var tokens = new List<string>();
            var token = new StringBuilder();
            void Flush()
            {
                if (token.Length == 0) return;
                var word = token.ToString();
                token.Clear();
                tokens.Add(ShapeSynonyms.TryGetValue(word, out var mapped) ? mapped : word);
            }
            for (var i = 0; i < (name ?? "").Length; i++)
            {
                var c = name[i];
                if (!char.IsLetterOrDigit(c)) { Flush(); continue; }
                if (i > 0 && char.IsUpper(c) && char.IsLower(name[i - 1])) Flush();
                if (i > 0 && char.IsDigit(c) != char.IsDigit(name[i - 1]) && token.Length > 0) Flush();
                token.Append(char.ToLowerInvariant(c));
            }
            Flush();
            tokens.Sort(StringComparer.Ordinal);
            return string.Join(" ", tokens);
        }

        private sealed class SyncPlan
        {
            // Item meshes without any Blendshape Sync: a new component with these bindings.
            public readonly List<(SkinnedMeshRenderer renderer, List<BlendshapeBinding> bindings)> added =
                new List<(SkinnedMeshRenderer, List<BlendshapeBinding>)>();
            // Creator components whose bindings point at meshes or shapes this avatar lacks.
            public readonly List<(ModularAvatarBlendshapeSync sync, int index, BlendshapeBinding binding)> repaired =
                new List<(ModularAvatarBlendshapeSync, int, BlendshapeBinding)>();
            public int Count => added.Sum(entry => entry.bindings.Count) + repaired.Count;
        }

        private static SyncPlan PlanBlendshapeSync(VRCAvatarDescriptor avatar, GameObject item, SkinnedMeshRenderer onlySource)
        {
            var plan = new SyncPlan();
            if (avatar == null || item == null) return plan;
            var renderers = item.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(renderer => renderer.sharedMesh != null).ToList();
            var sources = onlySource != null ? new List<SkinnedMeshRenderer> { onlySource } : BodyMeshes(avatar, item);
            var matcher = new ShapeMatcher(sources, renderers.SelectMany(ShapeNames));
            // An item slider animates its shape directly; a sync binding would fight it.
            var sliderShapes = new HashSet<string>(Sliders(avatar, item).Select(slider => SliderKey(slider.mesh, slider.shape)), StringComparer.Ordinal);
            var ours = OwnSyncPaths(item);
            foreach (var renderer in renderers)
            {
                var path = AvatarPath(avatar, renderer.transform);
                var names = ShapeNames(renderer).Distinct().ToList();
                var local = new HashSet<string>(names, StringComparer.Ordinal);
                var existing = renderer.GetComponent<ModularAvatarBlendshapeSync>();
                var itemPath = AnimationUtility.CalculateTransformPath(renderer.transform, item.transform);
                if (existing != null && !ours.Contains(itemPath))
                {
                    for (var index = 0; index < existing.Bindings.Count; index++)
                    {
                        var binding = existing.Bindings[index];
                        if (BindingResolves(existing, binding)) continue;
                        var localShape = string.IsNullOrEmpty(binding.LocalBlendshape) ? binding.Blendshape : binding.LocalBlendshape;
                        if (!local.Contains(localShape ?? "") || sliderShapes.Contains(SliderKey(path, localShape))) continue;
                        if (!RetargetInsideItem(item, binding, out var target, out var targetShape) &&
                            !matcher.TryMatch(binding.Blendshape, out target, out targetShape)) continue;
                        binding.ReferenceMesh = new AvatarObjectReference(target.gameObject);
                        binding.Blendshape = targetShape;
                        binding.LocalBlendshape = localShape;
                        plan.repaired.Add((existing, index, binding));
                    }
                    continue;
                }
                var bindings = new List<BlendshapeBinding>();
                foreach (var name in names)
                {
                    if (sliderShapes.Contains(SliderKey(path, name)) || !matcher.TryMatch(name, out var source, out var bodyShape)) continue;
                    bindings.Add(new BlendshapeBinding
                    {
                        ReferenceMesh = new AvatarObjectReference(source.gameObject),
                        Blendshape = bodyShape,
                        LocalBlendshape = name,
                    });
                }
                if (bindings.Count > 0) plan.added.Add((renderer, bindings));
            }
            return plan;
        }

        private static bool BindingResolves(ModularAvatarBlendshapeSync sync, BlendshapeBinding binding)
        {
            GameObject target;
            try { target = binding.ReferenceMesh?.Get(sync); }
            catch (Exception) { return false; }
            var renderer = target == null ? null : target.GetComponent<SkinnedMeshRenderer>();
            return renderer != null && renderer.sharedMesh != null && renderer.sharedMesh.GetBlendShapeIndex(binding.Blendshape ?? "") >= 0;
        }

        // Creator syncs between an item's own meshes store a path from the creator's avatar
        // root ("(MA)Dress/Front"); worn under Outfits/<look>, the same mesh sits deeper.
        private static bool RetargetInsideItem(GameObject item, BlendshapeBinding binding, out SkinnedMeshRenderer target, out string shape)
        {
            target = null;
            shape = binding.Blendshape;
            var segments = (binding.ReferenceMesh?.referencePath ?? "").Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            for (var skip = 0; skip < segments.Length; skip++)
            {
                var found = item.transform.Find(string.Join("/", segments.Skip(skip)));
                var renderer = found == null ? null : found.GetComponent<SkinnedMeshRenderer>();
                if (renderer == null || renderer.sharedMesh == null || renderer.sharedMesh.GetBlendShapeIndex(shape ?? "") < 0) continue;
                target = renderer;
                return true;
            }
            return false;
        }

        private static HashSet<string> OwnSyncPaths(GameObject item)
        {
            var host = item == null ? null : FindGeneratedHost(item.transform, BlendshapeSyncKind, BlendshapeSyncName);
            var marker = host == null ? null : host.GetComponent<OutfitToggleGeneratedMenu>();
            return new HashSet<string>(marker?.outfitPaths ?? new string[0], StringComparer.Ordinal);
        }

        // Removes what Avatar Wardrobe added or retargeted, then, when enabled, adds MA Blendshape
        // Sync for every item shape that has a body counterpart and repairs creator bindings that
        // point at meshes this avatar doesn't have. Working creator bindings are never touched.
        private static void SetBlendshapeSync(VRCAvatarDescriptor avatar, GameObject item, bool enabled, SkinnedMeshRenderer onlySource = null)
        {
            RemoveBlendshapeSync(item);
            if (!enabled) return;
            var plan = PlanBlendshapeSync(avatar, item, onlySource);
            if (plan.Count == 0) return;
            var added = new List<string>();
            foreach (var (renderer, bindings) in plan.added)
            {
                var sync = Undo.AddComponent<ModularAvatarBlendshapeSync>(renderer.gameObject);
                sync.Bindings = bindings;
                EditorUtility.SetDirty(sync);
                added.Add(AnimationUtility.CalculateTransformPath(renderer.transform, item.transform));
            }
            var restore = new List<string>();
            foreach (var (sync, index, binding) in plan.repaired)
            {
                var original = sync.Bindings[index];
                restore.Add(string.Join("\t", AnimationUtility.CalculateTransformPath(sync.transform, item.transform),
                    index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    original.ReferenceMesh?.referencePath ?? "", original.Blendshape ?? "", original.LocalBlendshape ?? ""));
                Undo.RecordObject(sync, "Repair blendshape sync");
                sync.Bindings[index] = binding;
                EditorUtility.SetDirty(sync);
                PrefabUtility.RecordPrefabInstancePropertyModifications(sync);
            }
            var record = new GameObject(BlendshapeSyncName);
            Undo.RegisterCreatedObjectUndo(record, "Record wardrobe blendshape sync");
            record.transform.SetParent(item.transform, false);
            var generated = TagGenerated(record, BlendshapeSyncKind);
            generated.outfitPaths = added.ToArray();
            generated.restoreBindings = restore.ToArray();
            EditorUtility.SetDirty(generated);
        }

        private static void RemoveBlendshapeSync(GameObject item)
        {
            var host = FindGeneratedHost(item.transform, BlendshapeSyncKind, BlendshapeSyncName);
            if (host == null) return;
            var marker = host.GetComponent<OutfitToggleGeneratedMenu>();
            foreach (var path in marker.outfitPaths ?? new string[0])
            {
                var target = string.IsNullOrEmpty(path) ? item.transform : item.transform.Find(path);
                var sync = target == null ? null : target.GetComponent<ModularAvatarBlendshapeSync>();
                if (sync != null) Undo.DestroyObjectImmediate(sync);
            }
            foreach (var row in marker.restoreBindings ?? new string[0])
            {
                var cells = row.Split('\t');
                if (cells.Length < 5 || !int.TryParse(cells[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var index)) continue;
                var target = string.IsNullOrEmpty(cells[0]) ? item.transform : item.transform.Find(cells[0]);
                var sync = target == null ? null : target.GetComponent<ModularAvatarBlendshapeSync>();
                if (sync == null || index < 0 || index >= sync.Bindings.Count) continue;
                var binding = sync.Bindings[index];
                Undo.RecordObject(sync, "Restore blendshape sync");
                binding.ReferenceMesh = new AvatarObjectReference { referencePath = cells[2] };
                binding.Blendshape = cells[3];
                binding.LocalBlendshape = cells[4];
                sync.Bindings[index] = binding;
                EditorUtility.SetDirty(sync);
                PrefabUtility.RecordPrefabInstancePropertyModifications(sync);
            }
            Undo.DestroyObjectImmediate(host.gameObject);
        }

        // Used when wearing an item: follow the body unless the item already does, so a later
        // Customize choice is never overwritten.
        internal static void EnsureBlendshapeSync(VRCAvatarDescriptor avatar, GameObject item)
        {
            if (avatar == null || item == null || HasBlendshapeSync(item)) return;
            SetBlendshapeSync(avatar, item, true);
        }

        // Bindings turning sync on would add or repair (what Customize shows next to the switch).
        internal static int BlendshapeSyncCandidates(VRCAvatarDescriptor avatar, GameObject item)
        {
            var host = item == null ? null : FindGeneratedHost(item.transform, BlendshapeSyncKind, BlendshapeSyncName);
            var repaired = host == null ? 0 : host.GetComponent<OutfitToggleGeneratedMenu>().restoreBindings?.Length ?? 0;
            return PlanBlendshapeSync(avatar, item, null).Count + repaired;
        }
    }
}
