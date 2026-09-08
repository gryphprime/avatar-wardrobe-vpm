using System;
using System.IO;
using System.Linq;
using nadena.dev.modular_avatar.core;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using Object = UnityEngine.Object;

namespace OutfitToggleGenerator
{
    public sealed class PresetAppearanceTests
    {
        private string folder, presets, overrides, uploads;
        private Scene previous, scene;
        private VRCAvatarDescriptor avatar, previousAvatar;
        private GameObject root, coat;
        private SkinnedMeshRenderer body;
        private Material original, alternate;
        private ModularAvatarMenuItem option;
        private Vector3 savedPosition;
        [SetUp] public void Setup()
        {
            previous = WardrobeTestSceneFixture.RequireSavedActiveScene(); previousAvatar = AvatarWardrobeServer.SceneAvatar;
            presets = AvatarWardrobePresets.CaptureSettings(); overrides = AvatarWardrobeCatalog.CaptureOverrides(); uploads = ShiroTools.OutfitProjectData.CaptureSettings();
            folder = "Assets/PresetAppearanceFixture_" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(folder); AssetDatabase.Refresh();
            scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive); SceneManager.SetActiveScene(scene);
            root = new GameObject("Saved appearance fixture"); avatar = root.AddComponent<VRCAvatarDescriptor>(); root.AddComponent<Animator>(); AvatarWardrobeServer.SceneAvatar = avatar;
            original = new Material(Shader.Find("Standard")) { name = "Original", color = Color.white };
            alternate = new Material(Shader.Find("Standard")) { name = "Alternate", color = Color.red };
            AssetDatabase.CreateAsset(original, folder + "/Original.mat"); AssetDatabase.CreateAsset(alternate, folder + "/Alternate.mat");
            var mesh = new Mesh { name = "Body fit", vertices = new[] { Vector3.zero, Vector3.up, Vector3.right }, triangles = new[] { 0, 1, 2 } };
            mesh.AddBlendShapeFrame("Fit", 100, new[] { Vector3.up * .1f, Vector3.zero, Vector3.zero }, new Vector3[3], new Vector3[3]);
            AssetDatabase.CreateAsset(mesh, folder + "/Body.asset");
            var bodyObject = new GameObject("Body"); bodyObject.transform.SetParent(root.transform, false); body = bodyObject.AddComponent<SkinnedMeshRenderer>();
            body.sharedMesh = mesh; body.sharedMaterial = original; body.SetBlendShapeWeight(0, 35);
            var source = new GameObject("Coat"); source.AddComponent<MeshFilter>().sharedMesh = mesh; source.AddComponent<MeshRenderer>().sharedMaterial = original;
            var prefab = PrefabUtility.SaveAsPrefabAsset(source, folder + "/Coat.prefab"); Object.DestroyImmediate(source);
            coat = (GameObject)PrefabUtility.InstantiatePrefab(prefab, root.transform); savedPosition = new Vector3(.2f, .3f, .4f); coat.transform.localPosition = savedPosition;
            var control = new GameObject("Generated default"); control.transform.SetParent(root.transform, false);
            var marker = control.AddComponent<OutfitToggleGeneratedMenu>(); marker.generatedKind = "part-toggle"; marker.ownerId = "saved-look-control";
            option = control.AddComponent<ModularAvatarMenuItem>(); option.automaticValue = false; option.isDefault = true;
            option.Control = new VRCExpressionsMenu.Control { name = "Coat", type = VRCExpressionsMenu.Control.ControlType.Toggle,
                parameter = new VRCExpressionsMenu.Control.Parameter { name = "SavedCoat" }, value = 1 };
            EditorSceneManager.SaveScene(scene, folder + "/Scene.unity"); AssetDatabase.SaveAssets();
        }
        [TearDown] public void Cleanup()
        {
            Undo.ClearAll(); AvatarWardrobeServer.SceneAvatar = previousAvatar;
            if (previous.IsValid()) SceneManager.SetActiveScene(previous);
            if (scene.IsValid()) EditorSceneManager.CloseScene(scene, true);
            if (!string.IsNullOrEmpty(folder)) AssetDatabase.DeleteAsset(folder);
            AvatarWardrobePresets.RestoreSettings(presets); AvatarWardrobeCatalog.RestoreOverrides(overrides); ShiroTools.OutfitProjectData.RestoreSettings(uploads);
        }
        private void Save(string id = "common")
        {
            var state = WardrobePresetAppearance.Describe(avatar, id); Assert.AreEqual(1, state.ok, state.message);
            var result = WardrobePresetAppearance.Save(avatar, id, state.revision); Assert.AreEqual(1, result.ok, result.message);
        }
        [Test] public void SaveAndReviewedRestoreKeepMaterialsBodyFitPlacementAndDefaultsTogether()
        {
            ShiroTools.OutfitProjectData.GetOutfit("Saved appearance fixture", "Remote preset").blueprintId = "avtr-existing"; ShiroTools.OutfitProjectData.Save();
            var uploadBefore = ShiroTools.OutfitProjectData.CaptureSettings(); Save(); var savedSettings = AvatarWardrobePresets.CaptureSettings();
            var state = WardrobePresetAppearance.Describe(avatar, "common"); Assert.IsTrue(state.hasSaved); Assert.AreEqual(1, state.garments);
            Assert.IsNotEmpty(state.bodyFitRevision); Assert.IsNotEmpty(state.defaultsRevision);
            body.sharedMaterial = alternate; body.SetBlendShapeWeight(0, 90); coat.transform.localPosition = Vector3.one * 8; coat.transform.SetSiblingIndex(0); coat.SetActive(false); option.isDefault = false;
            var review = WardrobePresetAppearance.Review(avatar, "common"); Assert.AreEqual(1, review.ok, review.message); Assert.GreaterOrEqual(review.changes.Count, 5);
            Assert.IsTrue(body.sharedMaterial == alternate); Assert.AreEqual(90, body.GetBlendShapeWeight(0)); Assert.IsFalse(coat.activeSelf);
            var result = WardrobePresetAppearance.Apply(avatar, "common", review.token); Assert.AreEqual(1, result.ok, result.message);
            Assert.IsTrue(body.sharedMaterial == original); Assert.AreEqual(35, body.GetBlendShapeWeight(0)); Assert.AreEqual(savedPosition, coat.transform.localPosition);
            Assert.IsTrue(coat.activeSelf); Assert.IsTrue(option.isDefault); Assert.AreEqual(savedSettings, AvatarWardrobePresets.CaptureSettings());
            Assert.AreEqual(uploadBefore, ShiroTools.OutfitProjectData.CaptureSettings());
            Undo.PerformUndo(); Assert.IsTrue(body.sharedMaterial == alternate); Assert.AreEqual(90, body.GetBlendShapeWeight(0)); Assert.IsFalse(coat.activeSelf);
            Assert.AreEqual(Vector3.one * 8, coat.transform.localPosition); Assert.AreEqual(0, coat.transform.GetSiblingIndex()); Assert.IsFalse(option.isDefault);
            Undo.PerformRedo(); Assert.IsTrue(body.sharedMaterial == original); Assert.AreEqual(35, body.GetBlendShapeWeight(0)); Assert.IsTrue(coat.activeSelf);
            Assert.AreEqual(savedPosition, coat.transform.localPosition); Assert.AreEqual(1, coat.transform.GetSiblingIndex()); Assert.IsTrue(option.isDefault);
        }
        [Test] public void RestoreIncludesCommonBodyAndSelectedPresetButLeavesOtherPresetCopiesAlone()
        {
            var selected = AvatarWardrobePresets.SavePreset("", "Saved selection"); Assert.IsTrue(selected.ok, selected.message);
            var other = AvatarWardrobePresets.SavePreset("", "Other selection"); Assert.IsTrue(other.ok, other.message);
            var selectedHolder = root.transform.Find(selected.preset.legacyPath); var otherHolder = root.transform.Find(other.preset.legacyPath);
            Assert.IsNotNull(selectedHolder); Assert.IsNotNull(otherHolder);
            coat.transform.SetParent(selectedHolder, false);
            var otherCoat = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(folder + "/Coat.prefab"), otherHolder);
            EditorSceneManager.SaveScene(scene);
            Save(selected.preset.id);
            var recipe = AvatarWardrobePresets.GetAppearance(selected.preset.id);
            Assert.AreEqual(1, recipe.garments.Count); Assert.AreEqual(GlobalObjectId.GetGlobalObjectIdSlow(coat).ToString(), recipe.garments[0].objectId);
            Assert.IsFalse(recipe.visibility.Any(value => value.objectId == GlobalObjectId.GetGlobalObjectIdSlow(otherCoat).ToString()));
            body.SetBlendShapeWeight(0, 88); coat.GetComponent<MeshRenderer>().sharedMaterial = alternate;
            otherCoat.GetComponent<MeshRenderer>().sharedMaterial = alternate; otherCoat.transform.localPosition = Vector3.one * 9;
            var review = WardrobePresetAppearance.Review(avatar, selected.preset.id); Assert.AreEqual(1, review.ok, review.message);
            var restored = WardrobePresetAppearance.Apply(avatar, selected.preset.id, review.token); Assert.AreEqual(1, restored.ok, restored.message);
            Assert.AreEqual(35, body.GetBlendShapeWeight(0)); Assert.IsTrue(coat.GetComponent<MeshRenderer>().sharedMaterial == original);
            Assert.IsTrue(otherCoat.GetComponent<MeshRenderer>().sharedMaterial == alternate); Assert.AreEqual(Vector3.one * 9, otherCoat.transform.localPosition);
        }
        [Test] public void MissingSavedCopyRejectsWithoutChangingOtherAppearanceValues()
        {
            Save(); body.sharedMaterial = alternate; Object.DestroyImmediate(coat);
            var review = WardrobePresetAppearance.Review(avatar, "common"); Assert.AreEqual(0, review.ok); StringAssert.Contains("existing saved copies", review.message);
            Assert.IsTrue(body.sharedMaterial == alternate); Assert.AreEqual(0, root.GetComponentsInChildren<MeshRenderer>(true).Length);
        }
        [Test] public void AssetVersionChangeIsExplicitAndDoesNotMutateTheScene()
        {
            Save(); original.color = Color.blue; EditorUtility.SetDirty(original); AssetDatabase.SaveAssetIfDirty(original);
            AssetDatabase.ImportAsset(folder + "/Original.mat", ImportAssetOptions.ForceUpdate);
            var before = body.GetBlendShapeWeight(0);
            var review = WardrobePresetAppearance.Review(avatar, "common"); Assert.AreEqual(0, review.ok); StringAssert.Contains("changed version", review.message);
            Assert.AreEqual(before, body.GetBlendShapeWeight(0));
        }
        [Test] public void ChangedAvatarOrUploadSettingsInvalidateReviewAndConsumedTokenCannotReplay()
        {
            Save(); body.SetBlendShapeWeight(0, 70);
            var review = WardrobePresetAppearance.Review(avatar, "common"); Assert.AreEqual(1, review.ok, review.message);
            ShiroTools.OutfitProjectData.GetOutfit("Saved appearance fixture", "New remote upload").blueprintId = "avtr-new"; ShiroTools.OutfitProjectData.Save();
            var newest = ShiroTools.OutfitProjectData.CaptureSettings();
            Assert.AreEqual(0, WardrobePresetAppearance.Apply(avatar, "common", review.token).ok); Assert.AreEqual(70, body.GetBlendShapeWeight(0));
            Assert.AreEqual(newest, ShiroTools.OutfitProjectData.CaptureSettings());
            Assert.AreEqual(0, WardrobePresetAppearance.Apply(avatar, "common", review.token).ok);
        }
        [Test] public void MetadataExportAndStoreRoundTripRetainExactReferencesWithoutAssets()
        {
            Save(); var serialized = AvatarWardrobePresets.CaptureSettings(); AvatarWardrobePresets.RestoreSettings(serialized);
            var exported = WardrobePresetAppearance.Export(avatar, "common"); Assert.AreEqual(1, exported.ok, exported.message); Assert.IsFalse(exported.containsAssets);
            var recipe = JsonUtility.FromJson<WardrobePresetAppearance.Recipe>(exported.json);
            Assert.AreEqual(1, recipe.version); Assert.AreEqual(1, recipe.garments.Count); Assert.IsNotEmpty(recipe.garments[0].source.guid);
            Assert.AreEqual(GlobalObjectId.GetGlobalObjectIdSlow(coat).ToString(), recipe.garments[0].objectId);
            Assert.AreEqual(AssetDatabase.AssetPathToGUID(folder + "/Original.mat"), recipe.surfaces.First(value => value.path == "Body").materials[0].guid);
            Assert.IsFalse(exported.json.Contains("m_SavedProperties")); Assert.IsFalse(exported.json.Contains("data:image"));
        }
        [Test] public void SaveRejectsStaleRevisionAndUnsupportedSavedSchemaIsActionable()
        {
            var before = WardrobePresetAppearance.Describe(avatar, "common"); body.SetBlendShapeWeight(0, 80);
            Assert.AreEqual(0, WardrobePresetAppearance.Save(avatar, "common", before.revision).ok);
            Save(); var recipe = AvatarWardrobePresets.GetAppearance("common"); recipe.version = 99; AvatarWardrobePresets.SetAppearance("common", recipe);
            var review = WardrobePresetAppearance.Review(avatar, "common"); Assert.AreEqual(0, review.ok); StringAssert.Contains("unsupported", review.message);
        }
    }
}
