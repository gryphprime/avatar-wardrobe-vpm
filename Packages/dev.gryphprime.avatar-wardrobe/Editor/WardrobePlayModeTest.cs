// Play Mode test for one preset. The scene avatar carries every preset's
// outfit folder, and Modular Avatar installs menus from inactive objects too,
// so playing it directly shows toggles from all presets. This stages the
// preset exactly as an upload does (EnsurePresetStaging + the batch
// uploader's activation), hides the source avatar, and enters Play Mode with
// only the staged copy active. NDMF/MA/VRCFury and the emulator then process
// the same avatar the upload would build. Leaving Play Mode closes the staging
// scene and restores the source avatar; nothing in the user's scene is saved.

using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OutfitToggleGenerator
{
    [InitializeOnLoad]
    internal static class WardrobePlayModeTest
    {
        private const string StateKey = "Wardrobe.PlayModeTest";

        [Serializable]
        private sealed class State
        {
            public string presetId = "";
            public string stagingScenePath = "";
            public string sourceGlobalId = "";
            public int sourceInstanceId;
            public bool sourceWasActive = true;
            public string activeScenePath = "";
        }

        static WardrobePlayModeTest()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            // Recover after a domain reload or an interrupted start.
            EditorApplication.delayCall += () =>
            {
                if (!EditorApplication.isPlayingOrWillChangePlaymode) Restore();
            };
        }

        internal static bool Active => !string.IsNullOrEmpty(SessionState.GetString(StateKey, ""));

        internal static AvatarWardrobeServer.ResultDto Start(string presetId, bool enterPlayMode = true)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return Fail("Leave Play Mode first.");
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return Fail("Wait for Unity to finish compiling or importing.");
            if (AvatarWardrobeServer.UploadTargetLocked || ShiroTools.OutfitBatchUploader.BatchActiveNow)
                return Fail("Wait for the active upload to finish.");
            if (Active) Restore();
            var preset = AvatarWardrobePresets.GetPreset(presetId);
            if (preset == null) return Fail(WardrobeStrings.T("preset.notfound"));
            var source = AvatarWardrobeServer.SceneAvatar;
            if (source == null) return Fail(WardrobeStrings.T("install.noavatar"));
            AvatarWardrobePresets.CurrentBase(out var baseKey, out _);
            if (preset.baseKey != baseKey) return Fail("Preset does not belong to the selected avatar.");
            var existing = SceneManager.GetSceneByPath(preset.scenePath);
            if (existing.IsValid() && existing.isLoaded)
                return Fail("Close the generated staging scene before testing this preset.");

            var state = new State
            {
                presetId = preset.id,
                stagingScenePath = preset.scenePath,
                sourceInstanceId = source.gameObject.GetInstanceID(),
                sourceWasActive = source.gameObject.activeSelf,
                activeScenePath = SceneManager.GetActiveScene().path,
            };
            try { state.sourceGlobalId = GlobalObjectId.GetGlobalObjectIdSlow(source.gameObject).ToString(); }
            catch (Exception) { state.sourceGlobalId = ""; }
            // Persist before touching anything so a failure part-way can be undone.
            SessionState.SetString(StateKey, JsonUtility.ToJson(state));

            GameObject spare = null;
            var targetLock = AvatarWardrobeServer.LockUploadTarget();
            try
            {
                spare = UnityEngine.Object.Instantiate(source.gameObject);
                spare.name = source.gameObject.name;
                spare.SetActive(false);
                Scene staging;
                if (AssetDatabase.LoadAssetAtPath<SceneAsset>(preset.scenePath) != null)
                    staging = EditorSceneManager.OpenScene(preset.scenePath, OpenSceneMode.Additive);
                else
                {
                    Directory.CreateDirectory(Path.GetFullPath(Path.Combine(Application.dataPath, "..", Path.GetDirectoryName(preset.scenePath))));
                    staging = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                }
                SceneManager.SetActiveScene(staging);
                SceneManager.MoveGameObjectToScene(spare, staging);
                var staged = AvatarWardrobePresets.EnsurePresetStaging(spare, preset);
                spare = null;
                // Saved under WardrobeUploads so per-preset uploader settings
                // (blendshapes, Items, FaceEmo) resolve with the upload's keys.
                if (!EditorSceneManager.SaveScene(staging, preset.scenePath))
                    throw new InvalidOperationException("Could not save the generated staging scene.");
                var activation = ShiroTools.OutfitBatchUploader.ActivateStagedPreset(staged.root, staged.outfitName);
                if (activation != null) throw new InvalidOperationException(activation);
                staged.root.SetActive(true);
                source.gameObject.SetActive(false);
                Selection.activeGameObject = staged.root;
                WardrobeLog.Write("playtest", "Starting preset=" + preset.id + " root=" + staged.root.name);
                if (enterPlayMode) EditorApplication.EnterPlaymode();
                return new AvatarWardrobeServer.ResultDto { ok = 1, message = "Entering Play Mode with " + preset.name + " as it would upload." };
            }
            catch (Exception exception)
            {
                if (spare != null) UnityEngine.Object.DestroyImmediate(spare);
                Debug.LogWarning("Avatar Wardrobe Play Mode test failed: " + exception);
                targetLock.Dispose();
                targetLock = null;
                Restore();
                return Fail(exception.Message);
            }
            finally
            {
                targetLock?.Dispose();
            }
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.EnteredEditMode)
                EditorApplication.delayCall += Restore;
        }

        internal static void Restore()
        {
            var json = SessionState.GetString(StateKey, "");
            if (string.IsNullOrEmpty(json)) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            SessionState.EraseString(StateKey);
            State state;
            try { state = JsonUtility.FromJson<State>(json); }
            catch (Exception) { return; }
            if (state == null) return;
            try
            {
                var active = SceneManager.GetSceneByPath(state.activeScenePath);
                var staging = SceneManager.GetSceneByPath(state.stagingScenePath);
                if (active.IsValid() && active.isLoaded && active != staging) SceneManager.SetActiveScene(active);
                else if (staging.IsValid() && SceneManager.GetActiveScene() == staging)
                    for (int i = 0; i < SceneManager.sceneCount; i++)
                    {
                        var other = SceneManager.GetSceneAt(i);
                        if (other != staging && other.isLoaded) { SceneManager.SetActiveScene(other); break; }
                    }
                if (staging.IsValid() && staging.isLoaded && SceneManager.sceneCount > 1)
                    EditorSceneManager.CloseScene(staging, true);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Avatar Wardrobe could not close the Play Mode test scene: " + exception.Message);
            }
            GameObject source = null;
            if (!string.IsNullOrEmpty(state.sourceGlobalId) && GlobalObjectId.TryParse(state.sourceGlobalId, out var id))
                source = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id) as GameObject;
            if (source == null) source = EditorUtility.InstanceIDToObject(state.sourceInstanceId) as GameObject;
            if (source != null && source.activeSelf != state.sourceWasActive) source.SetActive(state.sourceWasActive);
            else if (source == null)
                Debug.LogWarning("Avatar Wardrobe could not find the avatar hidden for the Play Mode test. Re-enable it in the Hierarchy.");
            if (source != null)
            {
                var avatar = source.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
                if (avatar != null) AvatarWardrobeServer.SceneAvatar = avatar;
                Selection.activeGameObject = source;
            }
            WardrobeLog.Write("playtest", "Restored preset=" + state.presetId);
        }

        private static AvatarWardrobeServer.ResultDto Fail(string message)
        {
            return new AvatarWardrobeServer.ResultDto { message = message };
        }
    }
}
