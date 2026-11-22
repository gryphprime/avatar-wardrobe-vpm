using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

// Per-part wearing defaults, body blendshape reactions and Blendshape Sync for worn items.
namespace OutfitToggleGenerator
{
    [Serializable]
    internal sealed class WardrobeShapeEdit
    {
        public string mesh = "";
        public string shape = "";
        public float value;
        public int delete;
    }

    [Serializable]
    internal sealed class WardrobePartEdit
    {
        // Empty key = the whole item; otherwise the part's name under the item root.
        public string key = "";
        public int worn = 1;
        public List<WardrobeShapeEdit> shapes = new List<WardrobeShapeEdit>();
    }

    [Serializable]
    internal sealed class WardrobePartsEdit
    {
        public List<WardrobePartEdit> parts = new List<WardrobePartEdit>();
        public int sync;
        public string syncMesh = "";
        public List<WardrobeSliderEdit> sliders = new List<WardrobeSliderEdit>();
    }

    internal static partial class OutfitToggleGenerator
    {
        private const string ShapeChangesKind = "shape-changes";
        private const string ShapeChangesName = "Avatar Wardrobe Shape Changes";

        // The same children GeneratePartToggles turns into controls.
        internal static List<GameObject> ItemParts(GameObject item)
        {
            var parts = new List<GameObject>();
            if (item == null) return parts;
            foreach (Transform child in item.transform)
                if (!IsArmaturePart(child.name) && child.GetComponent<OutfitToggleGeneratedMenu>() == null) parts.Add(child.gameObject);
            return parts;
        }

        // Body-side meshes: skinned meshes with blendshapes that belong to the avatar,
        // not to a worn item. Ordered so the main body comes first.
        internal static List<SkinnedMeshRenderer> BodyMeshes(VRCAvatarDescriptor avatar, GameObject item = null)
        {
            if (avatar == null) return new List<SkinnedMeshRenderer>();
            var avatarRoot = PrefabUtility.GetNearestPrefabInstanceRoot(avatar.gameObject);
            return avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(renderer => renderer.sharedMesh != null && renderer.sharedMesh.blendShapeCount > 0)
                .Where(renderer => item == null || !renderer.transform.IsChildOf(item.transform))
                .Where(renderer =>
                {
                    var root = PrefabUtility.GetNearestPrefabInstanceRoot(renderer.gameObject);
                    return root == null || root == avatarRoot || root == avatar.gameObject;
                })
                .OrderByDescending(renderer => string.Equals(renderer.name, "Body", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(renderer => renderer.sharedMesh.blendShapeCount)
                .ToList();
        }

        // Same-named sibling meshes share a path; the first one wins, as MA would resolve it.
        private static Dictionary<string, SkinnedMeshRenderer> MeshMap(VRCAvatarDescriptor avatar, GameObject item)
        {
            return BodyMeshes(avatar, item).GroupBy(renderer => AvatarPath(avatar, renderer.transform))
                .ToDictionary(group => group.Key, group => group.First());
        }

        internal static string AvatarPath(VRCAvatarDescriptor avatar, Transform target)
        {
            return AnimationUtility.CalculateTransformPath(target, avatar.transform);
        }

        private static ModularAvatarMenuItem PartToggleFor(GameObject item, GameObject part)
        {
            var host = FindGeneratedHost(item.transform, "part-toggles", PartTogglesHost);
            if (host == null) return null;
            foreach (var toggle in host.GetComponentsInChildren<ModularAvatarObjectToggle>(true))
                if (toggle.Objects.Any(entry => entry.Object?.Get(toggle) == part)) return toggle.GetComponent<ModularAvatarMenuItem>();
            return null;
        }

        internal static bool PartWorn(GameObject item, GameObject part)
        {
            var toggle = PartToggleFor(item, part);
            return toggle != null ? toggle.isDefault : part.activeSelf;
        }

        internal static bool PartHasToggle(GameObject item, GameObject part) => PartToggleFor(item, part) != null;

        internal static List<WardrobeShapeEdit> ShapeChanges(VRCAvatarDescriptor avatar, GameObject owner)
        {
            var host = owner == null ? null : FindGeneratedHost(owner.transform, ShapeChangesKind, ShapeChangesName);
            var changer = host == null ? null : host.GetComponent<ModularAvatarShapeChanger>();
            if (changer == null) return new List<WardrobeShapeEdit>();
            return changer.Shapes.Select(shape =>
            {
                var mesh = shape.Object?.Get(changer);
                return new WardrobeShapeEdit
                {
                    mesh = mesh == null ? shape.Object?.referencePath ?? "" : AvatarPath(avatar, mesh.transform),
                    shape = shape.ShapeName ?? "",
                    value = shape.Value,
                    delete = shape.ChangeType == ShapeChangeType.Delete ? 1 : 0,
                };
            }).ToList();
        }

        // Validates the whole edit before any scene change so a bad row fails cleanly.
        internal static void ValidatePartsEdit(VRCAvatarDescriptor avatar, GameObject item, WardrobePartsEdit edit)
        {
            if (edit == null || edit.parts == null) throw new ArgumentException(WardrobeStrings.T("parts.invalid"));
            var parts = ItemParts(item);
            var meshes = MeshMap(avatar, item);
            foreach (var part in edit.parts)
            {
                if (!string.IsNullOrEmpty(part.key))
                {
                    var matches = parts.Count(candidate => candidate.name == part.key);
                    if (matches == 0) throw new ArgumentException(WardrobeStrings.T("parts.missing", part.key));
                    if (matches > 1) throw new ArgumentException(WardrobeStrings.T("parts.duplicate", part.key));
                }
                foreach (var shape in part.shapes ?? new List<WardrobeShapeEdit>())
                {
                    if (!meshes.TryGetValue(shape.mesh ?? "", out var renderer))
                        throw new ArgumentException(WardrobeStrings.T("parts.meshMissing", shape.mesh));
                    if (renderer.sharedMesh.GetBlendShapeIndex(shape.shape ?? "") < 0)
                        throw new ArgumentException(WardrobeStrings.T("parts.shapeMissing", shape.shape, renderer.name));
                    if (float.IsNaN(shape.value)) throw new ArgumentException(WardrobeStrings.T("parts.invalid"));
                }
            }
            if (edit.sync == 1 && !string.IsNullOrEmpty(edit.syncMesh) && !meshes.ContainsKey(edit.syncMesh))
                throw new ArgumentException(WardrobeStrings.T("parts.meshMissing", edit.syncMesh));
            ValidateSliders(avatar, item, edit);
        }

        internal static void ApplyPartsEdit(VRCAvatarDescriptor avatar, GameObject item, WardrobePartsEdit edit)
        {
            var parts = ItemParts(item);
            var meshes = MeshMap(avatar, item);
            foreach (var part in edit.parts)
            {
                var owner = string.IsNullOrEmpty(part.key) ? item : parts.First(candidate => candidate.name == part.key);
                if (owner != item) SetPartWorn(item, owner, part.worn != 0);
                SetShapeChanges(owner, (part.shapes ?? new List<WardrobeShapeEdit>()).Select(shape => new ChangedShape
                {
                    Object = new AvatarObjectReference(meshes[shape.mesh].gameObject),
                    ShapeName = shape.shape,
                    ChangeType = shape.delete != 0 ? ShapeChangeType.Delete : ShapeChangeType.Set,
                    Value = Mathf.Clamp(shape.value, 0f, 100f),
                }).ToList());
            }
            // Sliders first: shapes an item slider drives are never synced.
            SetSliders(avatar, item, edit.sliders);
            var syncSource = edit.sync == 1 && !string.IsNullOrEmpty(edit.syncMesh) ? meshes[edit.syncMesh] : null;
            SetBlendshapeSync(avatar, item, edit.sync == 1, syncSource);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(item.scene);
        }

        private static void SetPartWorn(GameObject item, GameObject part, bool worn)
        {
            var toggle = PartToggleFor(item, part);
            if (toggle != null)
            {
                if (toggle.isDefault == worn) return;
                Undo.RecordObject(toggle, "Set part default");
                toggle.isDefault = worn;
                EditorUtility.SetDirty(toggle);
                return;
            }
            if (part.activeSelf == worn) return;
            Undo.RecordObject(part, "Set part worn");
            part.SetActive(worn);
            PrefabUtility.RecordPrefabInstancePropertyModifications(part);
        }

        // The changer sits on a generated child of the part (or item), so MA applies it
        // exactly while that part is worn: by default, from a part toggle or a menu group.
        private static void SetShapeChanges(GameObject owner, List<ChangedShape> shapes)
        {
            var host = FindGeneratedHost(owner.transform, ShapeChangesKind, ShapeChangesName);
            if (shapes.Count == 0)
            {
                if (host != null) Undo.DestroyObjectImmediate(host.gameObject);
                return;
            }
            if (host == null)
            {
                var created = new GameObject(ShapeChangesName);
                Undo.RegisterCreatedObjectUndo(created, "Add wardrobe shape changes");
                created.transform.SetParent(owner.transform, false);
                TagGenerated(created, ShapeChangesKind);
                host = created.transform;
            }
            var changer = host.GetComponent<ModularAvatarShapeChanger>() ?? Undo.AddComponent<ModularAvatarShapeChanger>(host.gameObject);
            Undo.RecordObject(changer, "Set wardrobe shape changes");
            changer.Shapes = shapes;
            EditorUtility.SetDirty(changer);
        }
    }
}
