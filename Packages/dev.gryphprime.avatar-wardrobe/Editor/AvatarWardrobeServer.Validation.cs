using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
namespace OutfitToggleGenerator
{
    internal static partial class AvatarWardrobeServer
    {
        [MenuItem("Tools/Avatar Outfit Toggles/Diagnostics/Validate Wardrobe Editing")]
        internal static void ValidateWardrobeEditing()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || UploadTargetLocked || ShiroTools.OutfitBatchUploader.BatchActiveNow)
                throw new InvalidOperationException("Finish Play Mode or the active upload before validation.");
            var previousAvatar = SceneAvatar;
            var previousSelection = Selection.activeObject;
            var previousScene = SceneManager.GetActiveScene();
            var settings = AvatarWardrobePresets.CaptureSettings();
            var overrides = AvatarWardrobeCatalog.CaptureOverrides();
            var folder = "Assets/Generated/WardrobeValidation_" + Guid.NewGuid().ToString("N");
            Scene scene = default;
            int assertions = 0;
            Undo.IncrementCurrentGroup();
            var initialGroup = Undo.GetCurrentGroup();
            void Check(bool condition, string label)
            {
                if (!condition) throw new Exception(label);
                assertions++;
            }
            try
            {
                scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                SceneManager.SetActiveScene(scene);
                var root = new GameObject("Wardrobe validation avatar");
                var avatar = root.AddComponent<VRCAvatarDescriptor>();
                SceneAvatar = avatar;
                AvatarWardrobePresets.RestoreSettings("{\"presets\":[],\"commonPresets\":[],\"assignments\":[]}");
                Directory.CreateDirectory(folder);
                var source = new GameObject("Copy");
                var prefab = PrefabUtility.SaveAsPrefabAsset(source, folder + "/Copy.prefab");
                UnityEngine.Object.DestroyImmediate(source);
                var guid = AssetDatabase.AssetPathToGUID(folder + "/Copy.prefab");
                var first = (GameObject)PrefabUtility.InstantiatePrefab(prefab, root.transform);
                var second = (GameObject)PrefabUtility.InstantiatePrefab(prefab, root.transform);
                var secondId = second.GetInstanceID();
                var path = AnimationUtility.CalculateTransformPath(second.transform, root.transform);
                Check(RemovePresetItem(guid, "common", path, "").ok == 0, "Missing copy identity must fail.");
                Check(RemovePresetItem(guid, "common", path, "2147483647").ok == 0, "Foreign copy identity must fail.");
                var removed = RemovePresetItem(guid, "common", path, secondId.ToString());
                Check(removed.ok == 1, "Second copy removal failed: " + removed.message);
                Check(first != null && EditorUtility.InstanceIDToObject(secondId) == null, "Removing one copy affected its sibling.");
                Undo.PerformUndo();
                Check(AvatarWardrobePresets.PrefabInstances(avatar, guid).Count == 2, "Undo did not restore the removed copy.");
                Undo.PerformRedo();
                Check(AvatarWardrobePresets.PrefabInstances(avatar, guid).Count == 1 && first != null, "Redo removed the wrong copy.");
                var before = AvatarWardrobePresets.CaptureSettings();
                var beforeName = root.name;
                var changed = EditAvatar("Validate coherent Undo", () => {
                    Undo.RecordObject(root, "Rename test avatar"); root.name = "Edited validation avatar";
                    AvatarWardrobePresets.SetSeparateAvatarUploads(!AvatarWardrobePresets.SeparateAvatarUploads);
                    return new ResultDto { ok = 1 };
                });
                Undo.FlushUndoRecordObjects();
                var after = AvatarWardrobePresets.CaptureSettings();
                Check(changed.ok == 1 && before != after, "Test settings did not change.");
                Undo.PerformUndo();
                Check(root.name == beforeName && AvatarWardrobePresets.CaptureSettings() == before, "Undo must restore both scene and settings.");
                Undo.PerformRedo();
                Check(root.name == "Edited validation avatar" && AvatarWardrobePresets.CaptureSettings() == after, "Redo must restore both scene and settings.");
                var failed = EditAvatar("Validate failed edit", () => {
                    Undo.RecordObject(root, "Test failure"); root.name = "Must roll back";
                    AvatarWardrobePresets.SetSeparateAvatarUploads(!AvatarWardrobePresets.SeparateAvatarUploads);
                    return new ResultDto { ok = 0, message = "Expected validation failure" };
                });
                Check(failed.ok == 0 && root.name == "Edited validation avatar" && AvatarWardrobePresets.CaptureSettings() == after,
                    "A failed result must restore both scene and settings.");
                var report = "PASS: " + assertions + " Wardrobe scene editing assertions; Unity " + Application.unityVersion;
                Directory.CreateDirectory("Library/AvatarWardrobe");
                File.WriteAllText("Library/AvatarWardrobe/edit-validation.txt", report);
                Debug.Log(report);
            }
            catch (Exception error)
            {
                Directory.CreateDirectory("Library/AvatarWardrobe");
                File.WriteAllText("Library/AvatarWardrobe/edit-validation.txt", "FAIL: " + error);
                Debug.LogException(error);
            }
            finally
            {
                Undo.RevertAllDownToGroup(initialGroup);
                if (scene.IsValid()) EditorSceneManager.CloseScene(scene, true);
                if (previousScene.IsValid()) SceneManager.SetActiveScene(previousScene);
                SceneAvatar = previousAvatar;
                Selection.activeObject = previousSelection;
                AvatarWardrobePresets.RestoreSettings(settings);
                AvatarWardrobeCatalog.RestoreOverrides(overrides);
                WardrobeEditHistory.Capture();
                AssetDatabase.DeleteAsset(folder);
                InvalidateInstalled();
            }
        }
    }
}
