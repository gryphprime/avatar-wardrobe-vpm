using System;
using System.IO;
using System.Linq;
using System.Reflection;
using nadena.dev.modular_avatar.core;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using System.Collections;
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
            var describe = WardrobePresetAppearance.Describe(avatar, id); Assert.AreEqual(1, describe.ok, describe.message);
            var revision = typeof(WardrobePresetAppearance).GetMethod("Revision", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { avatar }) as string;
            Assert.IsNotEmpty(revision, "Strict appearance revision unavailable.");
            var result = WardrobePresetAppearance.Save(avatar, id, revision); Assert.AreEqual(1, result.ok, result.message);
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

        [Test] public void ItemSettingsGroupOnlyUpdatesAllCopiesInSelectedPreset()
        {
            var selected = AvatarWardrobePresets.SavePreset("", "Selected settings"); Assert.IsTrue(selected.ok, selected.message);
            var other = AvatarWardrobePresets.SavePreset("", "Other settings"); Assert.IsTrue(other.ok, other.message);
            var selectedHolder = root.transform.Find(selected.preset.legacyPath); var otherHolder = root.transform.Find(other.preset.legacyPath);
            Assert.IsNotNull(selectedHolder); Assert.IsNotNull(otherHolder);
            coat.transform.SetParent(selectedHolder, false);
            var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(folder + "/Coat.prefab");
            var selectedCopy = (GameObject)PrefabUtility.InstantiatePrefab(prefabAsset); selectedCopy.transform.SetParent(selectedHolder, false); selectedCopy.name = "Coat Copy";
            var otherCopy = (GameObject)PrefabUtility.InstantiatePrefab(prefabAsset); otherCopy.transform.SetParent(otherHolder, false); otherCopy.name = "Other Coat";
            var guid = AssetDatabase.AssetPathToGUID(folder + "/Coat.prefab");
            var group = AvatarWardrobePresets.UpdateMenuGroup(selected.preset.id, "", "Settings group", "", guid, "save");
            Assert.IsNotEmpty(group);
            var beforeOther = AvatarWardrobePresets.MenuGroups(other.preset.id).SelectMany(g => g.paths).ToArray();
            var result = AvatarWardrobeServer.SetItemSettings(guid, selected.preset.id, true, group, false, false);
            Assert.AreEqual(1, result.ok, result.message);
            var paths = AvatarWardrobePresets.MenuGroups(selected.preset.id).SelectMany(g => g.paths).ToArray();
            Assert.AreEqual(2, paths.Length);
            Assert.IsTrue(paths.Contains(AnimationUtility.CalculateTransformPath(coat.transform, avatar.transform)));
            Assert.IsTrue(paths.Contains(AnimationUtility.CalculateTransformPath(selectedCopy.transform, avatar.transform)));
            Assert.AreEqual(beforeOther, AvatarWardrobePresets.MenuGroups(other.preset.id).SelectMany(g => g.paths).ToArray());
            Assert.IsNotNull(otherCopy);
            Assert.IsFalse(OutfitToggleGenerator.HasPartToggles(coat));
            Assert.IsFalse(OutfitToggleGenerator.HasPartToggles(selectedCopy));
        }
        [Test] public void ItemSettingsToggleOnlyPreservesGroupsAndOtherPresets()
        {
            var selected = AvatarWardrobePresets.SavePreset("", "Toggle settings"); Assert.IsTrue(selected.ok, selected.message);
            var other = AvatarWardrobePresets.SavePreset("", "Untouched settings"); Assert.IsTrue(other.ok, other.message);
            var holder = root.transform.Find(selected.preset.legacyPath);
            coat.transform.SetParent(holder, false);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(folder + "/Coat.prefab");
            var copy = (GameObject)PrefabUtility.InstantiatePrefab(prefab, holder); copy.name = "Second Coat";
            var untouched = (GameObject)PrefabUtility.InstantiatePrefab(prefab, root.transform.Find(other.preset.legacyPath));
            var guid = AssetDatabase.AssetPathToGUID(folder + "/Coat.prefab");
            var group = AvatarWardrobePresets.UpdateMenuGroup(selected.preset.id, "", "Keep group", "", guid, "save");
            AvatarWardrobePresets.UpdateMenuGroup(selected.preset.id, group, null, AnimationUtility.CalculateTransformPath(coat.transform, avatar.transform), guid, "assign");
            var before = AvatarWardrobePresets.MenuGroups(selected.preset.id).SelectMany(g => g.paths).ToArray();
            var result = AvatarWardrobeServer.SetItemSettings(guid, selected.preset.id, false, null, true, true);
            Assert.AreEqual(1, result.ok, result.message);
            Assert.IsTrue(OutfitToggleGenerator.HasPartToggles(coat));
            Assert.IsTrue(OutfitToggleGenerator.HasPartToggles(copy));
            Assert.IsFalse(OutfitToggleGenerator.HasPartToggles(untouched));
            CollectionAssert.AreEqual(before, AvatarWardrobePresets.MenuGroups(selected.preset.id).SelectMany(g => g.paths).ToArray());
        }
        [Test] public void ItemSettingsFailureRollsBackEarlierMenuAssignment()
        {
            var selected = AvatarWardrobePresets.SavePreset("", "Rollback settings"); Assert.IsTrue(selected.ok, selected.message);
            coat.transform.SetParent(root.transform.Find(selected.preset.legacyPath), false);
            var guid = AssetDatabase.AssetPathToGUID(folder + "/Coat.prefab");
            var group = AvatarWardrobePresets.UpdateMenuGroup(selected.preset.id, "", "Atomic group", "", guid, "save");
            OutfitToggleGenerator.GeneratePartToggles(avatar, coat);
            var host = coat.GetComponentsInChildren<OutfitToggleGeneratedMenu>(true).First(x => x.generatedKind == "part-toggles");
            var custom = new GameObject("User-owned content"); custom.transform.SetParent(host.transform, false);
            Undo.FlushUndoRecordObjects();
            var before = AvatarWardrobePresets.CaptureSettings();
            var invalid = AvatarWardrobeServer.SetItemSettings(guid, selected.preset.id, true, "unknown-group", false, false);
            Assert.AreEqual(0, invalid.ok); Assert.AreEqual(before, AvatarWardrobePresets.CaptureSettings());
            // Group assignment succeeds first; removing a mixed generated/user tree
            // then fails. The transaction must restore the earlier settings write.
            LogAssert.Expect(LogType.Exception, new System.Text.RegularExpressions.Regex("InvalidOperationException: Part toggles contain user content"));
            var result = AvatarWardrobeServer.SetItemSettings(guid, selected.preset.id, true, group, true, false);
            Assert.AreEqual(0, result.ok);
            Assert.AreEqual(before, AvatarWardrobePresets.CaptureSettings());
            Assert.IsEmpty(AvatarWardrobePresets.MenuGroups(selected.preset.id).SelectMany(g => g.paths));
            Assert.IsTrue(custom != null); Assert.AreEqual(host.transform, custom.transform.parent);
            Assert.IsTrue(OutfitToggleGenerator.HasPartToggles(coat));
        }
        [Test] public void ItemSettingsRejectsDuplicateTransformPathsBeforeMutation()
        {
            var selected = AvatarWardrobePresets.SavePreset("", "Duplicate settings"); Assert.IsTrue(selected.ok, selected.message);
            var holder = root.transform.Find(selected.preset.legacyPath); coat.transform.SetParent(holder, false);
            var copy = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(folder + "/Coat.prefab")); copy.transform.SetParent(holder, false); copy.name = coat.name;
            var guid = AssetDatabase.AssetPathToGUID(folder + "/Coat.prefab");
            var group = AvatarWardrobePresets.UpdateMenuGroup(selected.preset.id, "", "Duplicate group", "", guid, "save");
            var result = AvatarWardrobeServer.SetItemSettings(guid, selected.preset.id, true, group, false, false);
            Assert.AreEqual(0, result.ok); StringAssert.Contains("same transform path", result.message);
            Assert.IsEmpty(AvatarWardrobePresets.MenuGroups(selected.preset.id).SelectMany(g => g.paths));
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
            var before = typeof(WardrobePresetAppearance).GetMethod("Revision", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { avatar }) as string; body.SetBlendShapeWeight(0, 80);
            Assert.AreEqual(0, WardrobePresetAppearance.Save(avatar, "common", before).ok);
            Save(); var recipe = AvatarWardrobePresets.GetAppearance("common"); recipe.version = 99; AvatarWardrobePresets.SetAppearance("common", recipe);
            var review = WardrobePresetAppearance.Review(avatar, "common"); Assert.AreEqual(0, review.ok); StringAssert.Contains("unsupported", review.message);
        }

        [UnityTest] public IEnumerator DisplayRevisionPublishesAndSaveAcceptsIt()
        {
            var serverType = typeof(AvatarWardrobeServer);
            var pathField = serverType.GetField("serverProjectPath", BindingFlags.NonPublic | BindingFlags.Static);
            var publish = serverType.GetMethod("PublishOperationContextAsync", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(pathField); Assert.IsNotNull(publish);
            var previousPath = pathField.GetValue(null);
            try
            {
                pathField.SetValue(null, Directory.GetParent(Application.dataPath).FullName);
                var task = (System.Threading.Tasks.Task)publish.Invoke(null, null);
                while (!task.IsCompleted) yield return null;
                var state = WardrobePresetAppearance.Describe(avatar, "common");
                Assert.AreEqual(1, state.ok, state.message); Assert.IsNotEmpty(state.revision);
                var result = WardrobePresetAppearance.Save(avatar, "common", state.revision);
                Assert.AreEqual(1, result.ok, result.message);
            }
            finally { pathField.SetValue(null, previousPath); }
        }
    }
}
