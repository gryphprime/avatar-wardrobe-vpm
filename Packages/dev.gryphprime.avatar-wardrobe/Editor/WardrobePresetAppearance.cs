using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

namespace OutfitToggleGenerator
{
    // An additive snapshot on the existing preset record. It contains references and values, never purchased assets.
    internal static class WardrobePresetAppearance
    {
        [Serializable] internal sealed class AssetRef
        {
            public string guid = "", version = "", name = "";
            public long localId;
            public bool empty;
        }
        [Serializable] internal sealed class Garment
        {
            public string objectId, parentId, path;
            public AssetRef source;
            public Vector3 position, scale;
            public Quaternion rotation;
            public int sibling;
        }
        [Serializable] internal sealed class Shape { public string name; public int index; public float value; }
        [Serializable] internal sealed class Surface
        {
            public string rendererId, path;
            public AssetRef mesh;
            public List<AssetRef> materials = new List<AssetRef>();
            public List<Shape> shapes = new List<Shape>();
        }
        [Serializable] internal sealed class Visibility { public string objectId, path; public bool active; }
        [Serializable] internal sealed class Default
        {
            public string itemId, path, ownerId, kind, parameter;
            public int controlType;
            public float value;
            public bool isDefault, automaticValue;
        }
        [Serializable] internal sealed class Recipe
        {
            public int version = 1;
            public string savedAt, sourceAvatarId, sourceSceneGuid, sourceRevision, bodyFitRevision, defaultsRevision;
            public List<Garment> garments = new List<Garment>();
            public List<Surface> surfaces = new List<Surface>();
            public List<Visibility> visibility = new List<Visibility>();
            public List<Default> defaults = new List<Default>();
        }
        [Serializable] internal sealed class SummaryDto
        {
            public int ok, garments, materials, shapes, defaults;
            public bool hasSaved;
            public string presetId, revision, savedAt, sourceAvatarId, sourceSceneGuid, bodyFitRevision, defaultsRevision, message;
        }
        [Serializable] internal sealed class ReviewDto
        {
            public int ok;
            public string presetId, token, revision, message;
            public List<string> changes = new List<string>();
        }
        [Serializable] internal sealed class ExportDto { public int ok; public bool containsAssets; public string fileName, json, message; }
        private sealed class Pending
        {
            internal int avatarId;
            internal string presetId, revision, recipeHash;
            internal DateTime expires;
        }
        private static readonly Dictionary<string, Pending> Reviews = new Dictionary<string, Pending>();
        private const int MaximumObjects = 4096, MaximumRenderers = 256, MaximumShapes = 8192, MaximumBytes = 2 * 1024 * 1024;

        private static string Global(Object value) => GlobalObjectId.GetGlobalObjectIdSlow(value).ToString();
        private static string Hash(string value) => Hash128.Compute(value ?? "").ToString();
        private static string Revision(VRCAvatarDescriptor avatar) => Hash(WardrobeTryOnWorker.SourceFingerprint(avatar) + "\n" +
            AvatarWardrobePresets.CaptureSettings() + "\n" + AvatarWardrobeCatalog.CaptureOverrides() + "\n" + ShiroTools.OutfitProjectData.CaptureSettings());
        private static string PathOf(Transform value, VRCAvatarDescriptor avatar) => AnimationUtility.CalculateTransformPath(value, avatar.transform);
        private static void Target(VRCAvatarDescriptor avatar, string presetId)
        {
            if (avatar == null || AvatarWardrobeServer.SceneAvatar != avatar) throw new InvalidOperationException("Pin this preset's avatar before saving or restoring its appearance.");
            var state = WardrobeSceneEditor.Snapshot(avatar);
            if (state.ok != 1) throw new InvalidOperationException(state.message);
            AvatarWardrobePresets.CurrentBase(out var baseKey, out var unused);
            var preset = presetId == AvatarWardrobePresets.CommonTarget ? AvatarWardrobePresets.CommonPreset(baseKey) : AvatarWardrobePresets.GetPreset(presetId);
            if (preset == null || preset.baseKey != baseKey) throw new InvalidOperationException("Choose an existing preset belonging to the pinned avatar.");
            if (string.IsNullOrEmpty(avatar.gameObject.scene.path)) throw new InvalidOperationException("Save this scene in Unity before saving a reusable appearance.");
        }
        private static Recipe Saved(VRCAvatarDescriptor avatar, string presetId)
        {
            Target(avatar, presetId);
            var recipe = AvatarWardrobePresets.GetAppearance(presetId);
            if (recipe == null) throw new InvalidOperationException("Save this preset's current appearance first.");
            if (recipe.version != 1 || recipe.garments == null || recipe.surfaces == null || recipe.visibility == null || recipe.defaults == null)
                throw new InvalidOperationException("This saved appearance version is unsupported or incomplete. Preserve its metadata and save a new appearance.");
            if (recipe.sourceAvatarId != Global(avatar) || recipe.sourceSceneGuid != AssetDatabase.AssetPathToGUID(avatar.gameObject.scene.path))
                throw new InvalidOperationException("This appearance belongs to another saved scene avatar. Open its source scene and avatar before restoring it.");
            Bounds(recipe);
            return recipe;
        }
        private static bool InScope(GameObject value, VRCAvatarDescriptor avatar, string presetId)
        {
            if (value == avatar.gameObject) return true;
            var scope = AvatarWardrobePresets.ItemPreset(value, avatar);
            return scope == AvatarWardrobePresets.CommonTarget || scope == presetId;
        }
        private static AssetRef Reference(Object value, string description)
        {
            if (value == null) return new AssetRef { empty = true, name = "None" };
            if (!EditorUtility.IsPersistent(value) || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value, out string guid, out long localId) || string.IsNullOrEmpty(guid))
                throw new InvalidOperationException(description + " is an unsaved scene-only asset. Save it as a project asset before saving this appearance.");
            var path = AssetDatabase.GetAssetPath(value);
            return new AssetRef { guid = guid, localId = localId, version = AssetDatabase.GetAssetDependencyHash(path).ToString(), name = value.name };
        }
        private static T Asset<T>(AssetRef reference, string description) where T : Object
        {
            if (reference == null) throw new InvalidOperationException(description + " has incomplete saved asset metadata.");
            if (reference.empty) return null;
            var path = AssetDatabase.GUIDToAssetPath(reference.guid);
            if (string.IsNullOrEmpty(path)) throw new InvalidOperationException(description + " is missing: " + reference.name + ". Restore the original asset before applying this appearance.");
            if (AssetDatabase.GetAssetDependencyHash(path).ToString() != reference.version)
                throw new InvalidOperationException(description + " changed version: " + reference.name + ". Review the new asset and save a new appearance.");
            var asset = AssetDatabase.LoadAllAssetsAtPath(path).OfType<T>().FirstOrDefault(value =>
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value, out string guid, out long id) && guid == reference.guid && id == reference.localId);
            if (asset == null) throw new InvalidOperationException(description + " no longer contains the saved object: " + reference.name + ".");
            return asset;
        }
        private static T SceneObject<T>(string id, string description, VRCAvatarDescriptor avatar, string presetId) where T : Object
        {
            if (!GlobalObjectId.TryParse(id, out var global)) throw new InvalidOperationException(description + " has invalid scene identity.");
            var value = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(global) as T;
            var gameObject = value is GameObject go ? go : (value as Component)?.gameObject;
            if (gameObject == null || (gameObject != avatar.gameObject && !gameObject.transform.IsChildOf(avatar.transform)) || !InScope(gameObject, avatar, presetId))
                throw new InvalidOperationException(description + " is missing or moved outside this preset. This restore requires the existing saved copies; reinstall/reselect them and save a new appearance.");
            return value;
        }
        private static Mesh MeshOf(Renderer renderer) => renderer is SkinnedMeshRenderer skin ? skin.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        private static bool Finite(Quaternion value) => Finite(value.x) && Finite(value.y) && Finite(value.z) && Finite(value.w);
        private static void Bounds(Recipe recipe)
        {
            if (recipe.garments.Count > MaximumObjects || recipe.visibility.Count > MaximumObjects || recipe.defaults.Count > MaximumObjects ||
                recipe.surfaces.Count > MaximumRenderers || recipe.surfaces.Sum(value => value?.shapes?.Count ?? MaximumShapes + 1) > MaximumShapes ||
                System.Text.Encoding.UTF8.GetByteCount(JsonUtility.ToJson(recipe)) > MaximumBytes)
                throw new InvalidOperationException("This appearance exceeds the supported recipe size. Use Unity Inspector for this avatar.");
        }
        private static Recipe Capture(VRCAvatarDescriptor avatar, string presetId)
        {
            Target(avatar, presetId);
            var recipe = new Recipe { savedAt = DateTime.UtcNow.ToString("o"), sourceAvatarId = Global(avatar),
                sourceSceneGuid = AssetDatabase.AssetPathToGUID(avatar.gameObject.scene.path), sourceRevision = Revision(avatar) };
            var transforms = avatar.GetComponentsInChildren<Transform>(true).Where(value => InScope(value.gameObject, avatar, presetId)).ToArray();
            var roots = transforms.Select(value => PrefabUtility.GetNearestPrefabInstanceRoot(value.gameObject))
                .Where(value => value != null && value != avatar.gameObject && value.transform.IsChildOf(avatar.transform) && InScope(value, avatar, presetId)).Distinct().ToArray();
            foreach (var root in roots.Where(value => !roots.Any(parent => parent != value && value.transform.IsChildOf(parent.transform))))
            {
                var path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root);
                recipe.garments.Add(new Garment { objectId = Global(root), parentId = Global(root.transform.parent.gameObject), path = PathOf(root.transform, avatar),
                    source = Reference(AssetDatabase.LoadAssetAtPath<GameObject>(path), "Garment source"), position = root.transform.localPosition,
                    rotation = root.transform.localRotation, scale = root.transform.localScale, sibling = root.transform.GetSiblingIndex() });
            }
            foreach (var transform in transforms)
                recipe.visibility.Add(new Visibility { objectId = Global(transform.gameObject), path = PathOf(transform, avatar), active = transform.gameObject.activeSelf });
            foreach (var renderer in avatar.GetComponentsInChildren<Renderer>(true).Where(value => InScope(value.gameObject, avatar, presetId) && (value is MeshRenderer || value is SkinnedMeshRenderer)))
            {
                var surface = new Surface { rendererId = Global(renderer), path = PathOf(renderer.transform, avatar), mesh = Reference(MeshOf(renderer), "Renderer mesh") };
                foreach (var material in renderer.sharedMaterials) surface.materials.Add(Reference(material, "Material slot on " + surface.path));
                if (renderer is SkinnedMeshRenderer skin && skin.sharedMesh != null)
                    for (var index = 0; index < skin.sharedMesh.blendShapeCount; index++)
                    {
                        var value = skin.GetBlendShapeWeight(index);
                        if (!Finite(value)) throw new InvalidOperationException("A blendshape on " + surface.path + " has a non-finite value.");
                        surface.shapes.Add(new Shape { index = index, name = skin.sharedMesh.GetBlendShapeName(index), value = value });
                    }
                recipe.surfaces.Add(surface);
            }
            foreach (var item in avatar.GetComponentsInChildren<ModularAvatarMenuItem>(true).Where(value => InScope(value.gameObject, avatar, presetId)))
            {
                var marker = item.GetComponent<OutfitToggleGeneratedMenu>();
                if (marker == null || (marker.generatedKind != "part-toggle" && marker.generatedKind != "menu-group-option") || item.Control == null) continue;
                recipe.defaults.Add(new Default { itemId = Global(item), path = PathOf(item.transform, avatar), ownerId = marker.ownerId, kind = marker.generatedKind,
                    parameter = item.Control.parameter?.name ?? "", controlType = (int)item.Control.type, value = item.Control.value,
                    automaticValue = item.automaticValue, isDefault = item.isDefault });
            }
            recipe.bodyFitRevision = Hash(string.Join("|", recipe.surfaces.Select(value => value.rendererId + ":" + JsonUtility.ToJson(value.mesh) + ":" + string.Join(",", value.shapes.Select(shape => shape.name + "=" + shape.value.ToString("R", CultureInfo.InvariantCulture))))));
            recipe.defaultsRevision = Hash(string.Join("|", recipe.visibility.Select(value => value.objectId + ":" + value.active)) + string.Join("|", recipe.defaults.Select(value => value.itemId + ":" + value.isDefault)));
            Bounds(recipe); return recipe;
        }
        private sealed class Plan
        {
            internal readonly List<Action> edits = new List<Action>();
            internal readonly List<string> changes = new List<string>();
            internal readonly HashSet<Object> targets = new HashSet<Object>();
            internal void Add(bool changed, string label, Object target, Action edit)
            {
                if (changed) { changes.Add(label); targets.Add(target); edits.Add(edit); }
            }
            internal void RecordUndo()
            {
                // A prefab's property override table is shared by its Transform,
                // GameObject and components. Snapshot its hierarchy once before any
                // property changes, so separate records cannot restore competing tables.
                var roots = targets.Select(value => value is GameObject go ? go : (value as Component)?.gameObject)
                    .Where(value => value != null).Select(PrefabUtility.GetOutermostPrefabInstanceRoot).Where(value => value != null).Distinct().ToArray();
                foreach (var root in roots) Undo.RegisterFullObjectHierarchyUndo(root, "Restore preset appearance");
                var standalone = targets.Where(value =>
                {
                    var go = value is GameObject gameObject ? gameObject : (value as Component)?.gameObject;
                    return go == null || !roots.Any(root => go == root || go.transform.IsChildOf(root.transform));
                }).ToArray();
                if (standalone.Length > 0) Undo.RegisterCompleteObjectUndo(standalone, "Restore preset appearance");
            }
        }
        private static Plan Validate(Recipe recipe, VRCAvatarDescriptor avatar, string presetId)
        {
            var plan = new Plan();
            foreach (var garment in recipe.garments)
            {
                if (garment == null || !Finite(garment.position) || !Finite(garment.rotation) || !Finite(garment.scale) || garment.sibling < 0)
                    throw new InvalidOperationException("A saved garment placement is invalid.");
                var root = SceneObject<GameObject>(garment.objectId, "Garment " + garment.path, avatar, presetId);
                var parent = SceneObject<GameObject>(garment.parentId, "Garment parent " + garment.path, avatar, presetId);
                if (root == avatar.gameObject || parent == root || parent.transform.IsChildOf(root.transform)) throw new InvalidOperationException("A saved garment parent is invalid.");
                var source = Asset<GameObject>(garment.source, "Garment " + garment.path);
                if (source == null || PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root) != AssetDatabase.GetAssetPath(source))
                    throw new InvalidOperationException("Garment " + garment.path + " is a different prefab now. Restore requires the saved original copy.");
                var changed = root.transform.parent != parent.transform || root.transform.localPosition != garment.position ||
                    root.transform.localRotation != garment.rotation || root.transform.localScale != garment.scale || root.transform.GetSiblingIndex() != garment.sibling;
                plan.Add(changed, "Restore placement: " + garment.path, root.transform, () =>
                {
                    Undo.SetTransformParent(root.transform, parent.transform, "Restore preset garment placement");
                    root.transform.localPosition = garment.position; root.transform.localRotation = garment.rotation; root.transform.localScale = garment.scale;
                    root.transform.SetSiblingIndex(garment.sibling); PrefabUtility.RecordPrefabInstancePropertyModifications(root.transform);
                });
            }
            foreach (var surface in recipe.surfaces)
            {
                if (surface == null || surface.materials == null || surface.shapes == null) throw new InvalidOperationException("A saved renderer is incomplete.");
                var renderer = SceneObject<Renderer>(surface.rendererId, "Renderer " + surface.path, avatar, presetId);
                var mesh = Asset<Mesh>(surface.mesh, "Mesh on " + surface.path);
                if (MeshOf(renderer) != mesh) throw new InvalidOperationException("The mesh on " + surface.path + " changed. Review its new body-fit layout and save a new appearance.");
                var materials = surface.materials.Select((value, index) => Asset<Material>(value, "Material slot " + index + " on " + surface.path)).ToArray();
                if (renderer.sharedMaterials.Length != materials.Length) throw new InvalidOperationException("The material-slot layout on " + surface.path + " changed. Save a new appearance after reviewing it.");
                plan.Add(!renderer.sharedMaterials.SequenceEqual(materials), "Restore materials: " + surface.path, renderer, () =>
                {
                    renderer.sharedMaterials = materials;
                    PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
                });
                var skin = renderer as SkinnedMeshRenderer;
                foreach (var shape in surface.shapes)
                {
                    if (shape == null || skin == null || mesh == null || shape.index < 0 || shape.index >= mesh.blendShapeCount ||
                        mesh.GetBlendShapeName(shape.index) != shape.name || !Finite(shape.value))
                        throw new InvalidOperationException("A saved blendshape on " + surface.path + " is missing or its layout changed.");
                    plan.Add(skin.GetBlendShapeWeight(shape.index) != shape.value, "Restore shape " + shape.name + ": " + surface.path, skin, () =>
                    {
                        skin.SetBlendShapeWeight(shape.index, shape.value);
                        PrefabUtility.RecordPrefabInstancePropertyModifications(skin);
                    });
                }
            }
            foreach (var state in recipe.visibility)
            {
                if (state == null) throw new InvalidOperationException("A saved visibility state is incomplete.");
                var target = SceneObject<GameObject>(state.objectId, "Visibility target " + state.path, avatar, presetId);
                plan.Add(target.activeSelf != state.active, "Restore visibility: " + state.path, target, () =>
                {
                    target.SetActive(state.active);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(target);
                });
            }
            foreach (var state in recipe.defaults)
            {
                if (state == null) throw new InvalidOperationException("A saved default control is incomplete.");
                var item = SceneObject<ModularAvatarMenuItem>(state.itemId, "Default control " + state.path, avatar, presetId);
                var marker = item.GetComponent<OutfitToggleGeneratedMenu>();
                if (marker == null || marker.ownerId != state.ownerId || marker.generatedKind != state.kind || item.Control == null ||
                    item.automaticValue != state.automaticValue || (int)item.Control.type != state.controlType ||
                    (item.Control.parameter?.name ?? "") != state.parameter || item.Control.value != state.value)
                    throw new InvalidOperationException("The generated default control " + state.path + " changed. Review it and save a new appearance.");
                plan.Add(item.isDefault != state.isDefault, "Restore default: " + state.path, item, () =>
                {
                    item.isDefault = state.isDefault;
                    PrefabUtility.RecordPrefabInstancePropertyModifications(item);
                });
            }
            return plan;
        }

        internal static SummaryDto Describe(VRCAvatarDescriptor avatar, string presetId)
        {
            try
            {
                Target(avatar, presetId); var recipe = AvatarWardrobePresets.GetAppearance(presetId);
                return new SummaryDto { ok = 1, presetId = presetId, revision = Revision(avatar), hasSaved = recipe != null,
                    savedAt = recipe?.savedAt, garments = recipe?.garments?.Count ?? 0, materials = recipe?.surfaces?.Sum(value => value.materials.Count) ?? 0,
                    shapes = recipe?.surfaces?.Sum(value => value.shapes.Count) ?? 0, defaults = recipe?.defaults?.Count ?? 0,
                    sourceAvatarId = recipe?.sourceAvatarId, sourceSceneGuid = recipe?.sourceSceneGuid,
                    bodyFitRevision = recipe?.bodyFitRevision, defaultsRevision = recipe?.defaultsRevision,
                    message = "Saved appearances restore materials, body fit, placement and defaults on existing saved garment copies. They do not include asset files." };
            }
            catch (Exception error) { return new SummaryDto { presetId = presetId, message = error.Message }; }
        }
        internal static AvatarWardrobeServer.ResultDto Save(VRCAvatarDescriptor avatar, string presetId, string revision)
        {
            try
            {
                Target(avatar, presetId);
                if (Revision(avatar) != revision) throw new InvalidOperationException("The avatar or preset changed. Refresh before saving this appearance.");
                var recipe = Capture(avatar, presetId);
                // Validate every reference immediately; no unrecoverable snapshot is presented as saved.
                Validate(recipe, avatar, presetId);
                return AvatarWardrobeServer.EditAvatar("Save preset appearance", () =>
                {
                    AvatarWardrobePresets.SetAppearance(presetId, recipe);
                    return new AvatarWardrobeServer.ResultDto { ok = 1, id = presetId, message = "Saved this preset's appearance references and values. Existing copies are required for restore." };
                }, false);
            }
            catch (Exception error) { return new AvatarWardrobeServer.ResultDto { message = error.Message }; }
        }
        internal static ReviewDto Review(VRCAvatarDescriptor avatar, string presetId)
        {
            try
            {
                var recipe = Saved(avatar, presetId); var plan = Validate(recipe, avatar, presetId);
                foreach (var key in Reviews.Where(pair => pair.Value.expires < DateTime.UtcNow).Select(pair => pair.Key).ToArray()) Reviews.Remove(key);
                if (Reviews.Count >= 20) throw new InvalidOperationException("Too many appearance reviews are open. Wait a few minutes and review again.");
                var token = Guid.NewGuid().ToString("N"); var revision = Revision(avatar);
                Reviews.Add(token, new Pending { avatarId = avatar.GetInstanceID(), presetId = presetId, revision = revision,
                    recipeHash = Hash(JsonUtility.ToJson(recipe)), expires = DateTime.UtcNow.AddMinutes(5) });
                return new ReviewDto { ok = 1, presetId = presetId, token = token, revision = revision, changes = plan.changes,
                    message = plan.changes.Count == 0 ? "This appearance already matches the saved recipe." : "Review " + plan.changes.Count + " appearance changes. Existing garment copies and source assets will be preserved." };
            }
            catch (Exception error) { return new ReviewDto { presetId = presetId, message = error.Message }; }
        }
        internal static AvatarWardrobeServer.ResultDto Apply(VRCAvatarDescriptor avatar, string presetId, string token)
        {
            try
            {
                if (token == null || !Reviews.TryGetValue(token, out var pending)) throw new InvalidOperationException("This appearance review expired. Review again before applying.");
                Reviews.Remove(token);
                if (avatar == null || pending.avatarId != avatar.GetInstanceID() || pending.presetId != presetId || pending.expires < DateTime.UtcNow)
                    throw new InvalidOperationException("The reviewed preset or avatar changed. Review again before applying.");
                var recipe = Saved(avatar, presetId);
                if (Revision(avatar) != pending.revision || Hash(JsonUtility.ToJson(recipe)) != pending.recipeHash)
                    throw new InvalidOperationException("The avatar, settings or saved recipe changed after review. Nothing was restored.");
                var plan = Validate(recipe, avatar, presetId);
                return AvatarWardrobeServer.EditAvatar("Restore preset appearance", () =>
                {
                    plan.RecordUndo();
                    foreach (var edit in plan.edits) edit();
                    // Complete delayed property records before the transaction closes. In
                    // particular, GameObject activation must join the same Undo as materials.
                    Undo.FlushUndoRecordObjects();
                    return new AvatarWardrobeServer.ResultDto { ok = 1, id = presetId, message = "Restored " + plan.edits.Count + " appearance values. The scene has unsaved changes." };
                }, false);
            }
            catch (Exception error) { return new AvatarWardrobeServer.ResultDto { message = error.Message }; }
        }
        internal static ExportDto Export(VRCAvatarDescriptor avatar, string presetId)
        {
            try
            {
                var recipe = Saved(avatar, presetId);
                return new ExportDto { ok = 1, containsAssets = false, fileName = "wardrobe-appearance-" + (presetId == "common" ? "common" : Hash(presetId)) + ".json",
                    json = JsonUtility.ToJson(recipe, true), message = "Metadata only: asset references and appearance values. No models, textures or material files are included." };
            }
            catch (Exception error) { return new ExportDto { message = error.Message }; }
        }
    }

    internal static partial class AvatarWardrobePresets
    {
        internal static WardrobePresetAppearance.Recipe GetAppearance(string presetId)
        {
            CurrentBase(out var baseKey, out var unused);
            var preset = presetId == CommonTarget ? CommonPreset(baseKey) : GetPreset(presetId);
            if (preset == null || preset.baseKey != baseKey) return null;
            return preset.appearance == null ? null : JsonUtility.FromJson<WardrobePresetAppearance.Recipe>(JsonUtility.ToJson(preset.appearance));
        }
        internal static void SetAppearance(string presetId, WardrobePresetAppearance.Recipe recipe)
        {
            CurrentBase(out var baseKey, out var baseName);
            var file = CloneFile(LoadFile());
            var preset = presetId == CommonTarget ? file.commonPresets.FirstOrDefault(value => value.baseKey == baseKey)
                : file.presets.FirstOrDefault(value => value.id == presetId && value.baseKey == baseKey);
            if (preset == null && presetId == CommonTarget)
            {
                preset = new WardrobePreset { id = CommonTarget, name = "Common Preset", baseKey = baseKey, baseName = baseName };
                file.commonPresets.Add(preset);
            }
            if (preset == null) throw new InvalidOperationException("The existing preset no longer belongs to this avatar.");
            preset.appearance = JsonUtility.FromJson<WardrobePresetAppearance.Recipe>(JsonUtility.ToJson(recipe));
            preset.updated = DateTime.UtcNow.ToString("o");
            SaveFile(file);
        }
    }
}
