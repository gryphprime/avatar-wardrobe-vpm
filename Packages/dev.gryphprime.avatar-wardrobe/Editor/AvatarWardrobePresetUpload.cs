// Preset staging and upload: a preset is materialized by cloning the
// parent scene avatar (common installs ride along), stripping outfits
// assigned to other presets or to nothing, then uploading the preset
// outfit folder as its own avatar through the outfit batch uploader.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;

namespace OutfitToggleGenerator
{
    internal static partial class AvatarWardrobePresets
    {
        internal sealed class PresetStatus
        {
            public string blueprintId = "";
            public string lastUpload = "";
        }

        // Full job: stage + upload + stamp. Caller must be on the main thread.
        internal static async Task<AvatarWardrobeUpload.WardrobeUploadOutcome> UploadPresetAsync(string presetId)
        {
            var preset = GetPreset(presetId);
            if (preset == null)
                return new AvatarWardrobeUpload.WardrobeUploadOutcome { message = WardrobeStrings.T("preset.notfound") };
            var baseAvatar = AvatarWardrobeServer.SceneAvatar;
            if (baseAvatar == null)
                return new AvatarWardrobeUpload.WardrobeUploadOutcome { message = WardrobeStrings.T("install.noavatar") };
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
                return new AvatarWardrobeUpload.WardrobeUploadOutcome { message = "Switch the build target to Windows first (File > Build Settings)." };
            var readinessError = ShiroTools.OutfitBatchUploader.WardrobeUploadReadinessError();
            if (readinessError != null) return new AvatarWardrobeUpload.WardrobeUploadOutcome { message = readinessError };
            var saveError = AvatarWardrobeUpload.SaveUploadSourceScenes();
            if (saveError != null) return new AvatarWardrobeUpload.WardrobeUploadOutcome { message = saveError };
            // Saving an untitled source scene can migrate its preset ownership key.
            CurrentBase(out var currentBaseKey, out var unusedBaseName);
            preset = GetPreset(presetId);
            if (preset == null || preset.baseKey != currentBaseKey)
                return new AvatarWardrobeUpload.WardrobeUploadOutcome { message = "Preset does not belong to the selected avatar." };
            var previousScene = EditorSceneManager.GetActiveScene();
            var previousSetup = EditorSceneManager.GetSceneManagerSetup();
            var previousAvatarId = GlobalObjectId.GetGlobalObjectIdSlow(baseAvatar.gameObject);
            Scene stagingScene = default(Scene);
            bool openedStaging = false;
            WardrobeLog.Write("upload", "Starting " + "preset=" + presetId);
            GameObject spare = null;
            var targetLock = AvatarWardrobeServer.LockUploadTarget();
            try
            {
                spare = (GameObject)UnityEngine.Object.Instantiate(baseAvatar.gameObject);
                spare.name = preset.avatarRootName;
                spare.SetActive(false);
                stagingScene = SceneManager.GetSceneByPath(preset.scenePath);
                if (stagingScene.IsValid() && stagingScene.isLoaded)
                    throw new InvalidOperationException("Close the generated staging scene before starting this upload.");
                if (AssetDatabase.LoadAssetAtPath<SceneAsset>(preset.scenePath) != null)
                    stagingScene = EditorSceneManager.OpenScene(preset.scenePath, OpenSceneMode.Additive);
                else
                {
                    Directory.CreateDirectory(FullPath(Path.GetDirectoryName(preset.scenePath)));
                    stagingScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                }
                openedStaging = true;
                SceneManager.SetActiveScene(stagingScene);
                SceneManager.MoveGameObjectToScene(spare, stagingScene);
                var staging = EnsurePresetStaging(spare, preset);
                spare = null;
                if (!EditorSceneManager.SaveScene(stagingScene, staging.scenePath))
                    throw new InvalidOperationException("Could not save the generated upload scene.");
                AssetDatabase.Refresh();
                var bridge = await ShiroTools.OutfitBatchUploader.UploadOutfitHeadless(staging.root, staging.outfitName);
                WardrobeLog.Write("upload", (bridge.ok ? "Completed " : "Failed ") + "preset=" + presetId + " " + bridge.message);
                var outcome = new AvatarWardrobeUpload.WardrobeUploadOutcome
                {
                    ok = bridge.ok,
                    message = bridge.message ?? "",
                    blueprintId = bridge.blueprintId ?? "",
                    isNew = bridge.isNew,
                };
                if (bridge.ok)
                {
                    preset.updated = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                    var file = LoadFile();
                    var stored = file.presets.FirstOrDefault(p => p != null && p.id == preset.id);
                    if (stored != null) stored.updated = preset.updated;
                    SaveFile(file);
                }
                return outcome;
            }
            catch (Exception exception)
            {
                WardrobeLog.Write("upload", "Failed " + "preset=" + presetId + " " + exception.GetType().Name + ": " + exception.Message);
                Debug.LogError("Avatar Wardrobe preset upload failed: " + exception);
                return new AvatarWardrobeUpload.WardrobeUploadOutcome { message = exception.Message };
            }
            finally
            {
                targetLock.Dispose();
                if (spare != null)
                {
                    try { UnityEngine.Object.DestroyImmediate(spare); }
                    catch (Exception) { }
                }
                try
                {
                    if (openedStaging && stagingScene.IsValid() && stagingScene.isLoaded)
                        EditorSceneManager.CloseScene(stagingScene, true);
                    if (previousScene.IsValid() && previousScene.isLoaded)
                    {
                        SceneManager.SetActiveScene(previousScene);
                        AvatarWardrobeServer.SceneAvatar = baseAvatar;
                    }
                    else
                    {
                        EditorSceneManager.RestoreSceneManagerSetup(previousSetup);
                        var restored = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(previousAvatarId) as GameObject;
                        AvatarWardrobeServer.SceneAvatar = restored == null ? null : restored.GetComponent<VRCAvatarDescriptor>();
                    }
                }
                catch (Exception exception)
                {
                    Debug.LogWarning("Avatar Wardrobe preset upload restore failed: " + exception.Message);
                }
            }
        }

        // Staging keeps exactly the outfits assigned to this preset plus
        // common. Unassigned scene installs stay in the parent scene,
        // they are only excluded from the upload.
        internal static AvatarWardrobeUpload.StagingInfo EnsurePresetStaging(GameObject baseAvatar, WardrobePreset preset)
        {
            if (baseAvatar == null) throw new ArgumentNullException("baseAvatar");
            if (preset == null) throw new ArgumentNullException("preset");
            string avatarRootName = preset.avatarRootName;
            baseAvatar.name = avatarRootName;
            var scene = EditorSceneManager.GetActiveScene();
            GameObject existing = null;
            foreach (var top in scene.GetRootGameObjects())
                if (top != null && top != baseAvatar && top.name == avatarRootName) { existing = top; break; }
            // Always use the fresh source clone. Reusing a saved build copy can
            // retain previously generated objects and stale outfit contents.
            if (existing != null) UnityEngine.Object.DestroyImmediate(existing);
            var root = baseAvatar;
            root.SetActive(true);
            var desc = root.GetComponent<VRCAvatarDescriptor>();
            if (desc == null) throw new InvalidOperationException("Staging avatar is missing its VRCAvatarDescriptor.");
            var groupTargets = new Dictionary<string, GameObject>();
            foreach (var group in (preset.menuGroups ?? new List<MenuGroup>()).Concat(CommonPreset(preset.baseKey).menuGroups ?? new List<MenuGroup>()))
                foreach (var path in group.paths)
                {
                    var target = root.transform.Find(path);
                    if (target != null) groupTargets[path] = target.gameObject;
                }
            Transform legacyHolder = null;
            if (!string.IsNullOrEmpty(preset.legacyPath))
            {
                legacyHolder = root.transform.Find(preset.legacyPath);
                if (legacyHolder == null || legacyHolder == root.transform || legacyHolder.parent == root.transform)
                    throw new InvalidOperationException("The legacy preset folder is missing or invalid: " + preset.legacyPath);
                var siblings = new List<GameObject>();
                foreach (Transform sibling in legacyHolder.parent)
                    if (sibling != legacyHolder) siblings.Add(sibling.gameObject);
                foreach (var sibling in siblings) UnityEngine.Object.DestroyImmediate(sibling);
                legacyHolder.name = preset.outfitName;
                legacyHolder.SetParent(FindOrCreate(root.transform, "Outfits").transform, true);
                legacyHolder.gameObject.SetActive(true);
            }
            OutfitToggleGenerator.SyncPresetSelection(desc);
            var wanted = new HashSet<string>(StringComparer.Ordinal);
            var presetGuids = new List<string>();
            var commonGuids = new List<string>();
            foreach (var assignment in AssignmentsForBase(preset.baseKey))
            {
                if (string.IsNullOrEmpty(assignment.guid)) continue;
                if (assignment.target == preset.id) { wanted.Add(assignment.guid); presetGuids.Add(assignment.guid); }
                else if (assignment.target == CommonTarget) { wanted.Add(assignment.guid); commonGuids.Add(assignment.guid); }
            }
            // Single scan: strip other presets outfits and unassigned outfit
            // installs. Non-outfit roots are left alone.
            var seen = new HashSet<GameObject>();
            var installed = new List<KeyValuePair<GameObject, string>>();
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
            {
                if (legacyHolder != null && (transform == legacyHolder || transform.IsChildOf(legacyHolder))) continue;
                var prefabRoot = PrefabUtility.GetNearestPrefabInstanceRoot(transform.gameObject);
                if (prefabRoot == null || prefabRoot == root || !prefabRoot.transform.IsChildOf(root.transform) || !seen.Add(prefabRoot)) continue;
                var path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(prefabRoot);
                installed.Add(new KeyValuePair<GameObject, string>(prefabRoot, AssetDatabase.AssetPathToGUID(path)));
            }
            foreach (var pair in installed)
            {
                if (legacyHolder != null && (pair.Key.transform == legacyHolder || legacyHolder.IsChildOf(pair.Key.transform))) continue;
                if (string.IsNullOrEmpty(pair.Value) || wanted.Contains(pair.Value)) continue;
                var record = AvatarWardrobeCatalog.GetRecord(pair.Value);
                if (record == null || AvatarWardrobeCatalog.EffectiveKind(record) != WardrobeAssetKind.Outfit) continue;
                AvatarWardrobeCatalog.Remove(pair.Key);
            }
            // Scene-backed folders and Common contents already came from the
            // source clone. Instantiate can lose prefab-instance identity, so
            // catalog discovery is not a reliable "missing item" check here.
            foreach (var guid in legacyHolder == null ? presetGuids.Concat(commonGuids) : Enumerable.Empty<string>())
            {
                var record = AvatarWardrobeCatalog.GetRecord(guid);
                if (record == null) continue;
                GameObject instance;
                if (AvatarWardrobeCatalog.TryFindInstalled(desc, record, out instance) && instance != null) continue;
                var result = AvatarWardrobeCatalog.Install(desc, record, true, false);
                if (!result.success || result.instance == null)
                    throw new InvalidOperationException(result.message ?? "Outfit install failed.");
            }
            var outfits = FindOrCreate(root.transform, "Outfits");
            var holder = FindOrCreate(outfits.transform, preset.outfitName);
            foreach (var guid in legacyHolder == null ? presetGuids : Enumerable.Empty<string>())
            {
                var record = AvatarWardrobeCatalog.GetRecord(guid);
                if (record == null) continue;
                GameObject instance;
                AvatarWardrobeCatalog.TryFindInstalled(desc, record, out instance);
                if (instance == null) continue;
                if (instance.transform.parent != holder.transform) instance.transform.SetParent(holder.transform, true);
                instance.SetActive(true);
            }
            // Reconcile the holder: a previous run may have parked outfits
            // that are now common, unassigned, or moved elsewhere.
            var holderChildren = new List<GameObject>();
            foreach (Transform child in holder.transform) holderChildren.Add(child.gameObject);
            var assignmentByGuid = AssignmentsForBase(preset.baseKey)
                .Where(a => !string.IsNullOrEmpty(a.guid))
                .GroupBy(a => a.guid)
                .ToDictionary(g => g.Key, g => g.First().target ?? string.Empty, StringComparer.Ordinal);
            foreach (var child in holderChildren)
            {
                // Preserve the scene folder's exact contents, including non-prefab objects and inactive toggles.
                if (legacyHolder != null) continue;
                var assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(child);
                var guid = AssetDatabase.AssetPathToGUID(assetPath);
                string target;
                if (!string.IsNullOrEmpty(guid) && assignmentByGuid.TryGetValue(guid, out target) && target == preset.id) continue;
                if (!string.IsNullOrEmpty(guid) && assignmentByGuid.TryGetValue(guid, out target) && target == CommonTarget)
                {
                    child.transform.SetParent(root.transform, true);
                    continue;
                }
                if (!string.IsNullOrEmpty(guid))
                {
                    var record = AvatarWardrobeCatalog.GetRecord(guid);
                    if (record != null && AvatarWardrobeCatalog.EffectiveKind(record) == WardrobeAssetKind.Outfit)
                    {
                        AvatarWardrobeCatalog.Remove(child);
                        continue;
                    }
                }
                child.transform.SetParent(root.transform, true);
            }
            OutfitToggleGenerator.SyncMenuGroups(desc, preset, groupTargets);
            string scenePath = EditorSceneManager.GetActiveScene().path;
            if (string.IsNullOrEmpty(scenePath)) scenePath = preset.scenePath;
            return new AvatarWardrobeUpload.StagingInfo
            {
                root = root,
                scenePath = scenePath,
                avatarRootName = avatarRootName,
                outfitName = preset.outfitName,
            };
        }

        internal static PresetStatus GetPresetStatus(string presetId)
        {
            var status = new PresetStatus();
            var preset = GetPreset(presetId);
            if (preset == null) return status;
            try
            {
                // GetOutfit would create and persist entries for presets that
                // never uploaded, so scan the avatar record instead.
                var avatar = ShiroTools.OutfitProjectData.FindAvatar(preset.avatarRootName);
                if (avatar != null && avatar.outfits != null)
                {
                    var data = avatar.outfits.FirstOrDefault(o => o != null && o.name == preset.outfitName);
                    if (data != null)
                    {
                        status.blueprintId = data.blueprintId ?? "";
                        status.lastUpload = data.lastUploadWindows ?? "";
                    }
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Avatar Wardrobe preset status failed: " + exception.Message);
            }
            return status;
        }

        private static GameObject FindOrCreate(Transform parent, string name)
        {
            foreach (Transform child in parent)
                if (child.name == name) return child.gameObject;
            var created = new GameObject(name);
            created.transform.SetParent(parent, false);
            return created;
        }

        private static string FullPath(string assetPath)
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));
        }
    }
}
