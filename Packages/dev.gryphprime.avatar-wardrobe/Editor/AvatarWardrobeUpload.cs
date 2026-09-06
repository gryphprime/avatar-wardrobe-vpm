// Wardrobe upload service (v1): stages one outfit variant as its own avatar in
// a saved scene (Assets/Generated/WardrobeUploads/...) and uploads it through
// the outfit batch uploader's headless bridge. Windows only, single variant.
// All entry points must run on Unity's main thread (they touch scene state).
// TODO(v1): user-facing strings below are plain English; move them to lang.json
// once upload keys are added (see WardrobeStrings).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;

namespace OutfitToggleGenerator
{
    internal static class AvatarWardrobeUpload
    {
        internal sealed class StagingInfo
        {
            public string scenePath = "";
            public string avatarRootName = "";
            public string outfitName = "";
        }

        internal sealed class WardrobeUploadOutcome
        {
            public bool ok;
            public string message = "";
            public string blueprintId = "";
            public bool isNew;
        }

        internal sealed class WardrobeUploadStatus
        {
            public bool known;
            public string blueprintId = "";
            public string lastUpload = "";
        }

        // No preset discovery, folder creation, activation changes or toggle generation.
        // The delegate seam permits exercising staging and cleanup without network uploads.
        internal static async Task RunOnAvatarCopy(VRCAvatarDescriptor source, Func<GameObject, Task> build)
        {
            if (source == null || EditorUtility.IsPersistent(source)) throw new InvalidOperationException("Select a scene avatar.");
            const string stagingFolder = "Assets/Generated/WardrobeUploads/Temporary";
            Directory.CreateDirectory(FullPath(stagingFolder));
            var stagingPath = stagingFolder + "/Avatar_" + Guid.NewGuid().ToString("N") + ".unity";
            var previous = SceneManager.GetActiveScene();
            var staging = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.SetActiveScene(staging);
            GameObject clone = null;
            try
            {
                clone = UnityEngine.Object.Instantiate(source.gameObject);
                clone.name = source.name;
                clone.transform.SetParent(null, true);
                SceneManager.MoveGameObjectToScene(clone, staging);
                // The SDK may save the build scene. Never hand it an untitled scene,
                // which would open Save As in the middle of the upload.
                if (!EditorSceneManager.SaveScene(staging, stagingPath))
                    throw new IOException("Could not save the temporary avatar build scene.");
                await build(clone);
            }
            finally
            {
                if (clone != null) UnityEngine.Object.DestroyImmediate(clone);
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                if (staging.IsValid() && staging.isLoaded) EditorSceneManager.CloseScene(staging, true);
                if (File.Exists(FullPath(stagingPath))) AssetDatabase.DeleteAsset(stagingPath);
            }
        }

        internal static string ResolveSceneUploadThumbnail(GameObject avatar, string cacheKey, bool hasRemoteThumbnail)
        {
            // Null tells the SDK to preserve the existing remote image.
            if (hasRemoteThumbnail) return null;
            var folder = FullPath("Library/AvatarWardrobe/upload-thumbnails");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, Hash128.Compute(cacheKey).ToString() + ".png");
            if (File.Exists(path) && new FileInfo(path).Length > 8) return path;
            var image = OutfitToggleGenerator.RenderOutfitThumb(avatar);
            if (image == null) throw new InvalidOperationException("Could not generate an avatar thumbnail: no visible renderable content.");
            try { File.WriteAllBytes(path, image.EncodeToPNG()); }
            finally { UnityEngine.Object.DestroyImmediate(image); }
            return path;
        }

        internal static async Task<WardrobeUploadOutcome> UploadSceneAvatarAsync(VRCAvatarDescriptor source,
            string blueprint, string name, string thumbnail, bool buildOnly, System.Threading.CancellationToken cancellation,
            Action<string> progress)
        {
            if (!ShiroTools.OutfitBatchUploader.TryGetWardrobeBuilder(out var builder))
                throw new InvalidOperationException("Open the VRChat SDK Control Panel first.");
            if (!source.gameObject.activeInHierarchy) throw new InvalidOperationException("Enable the source avatar before building.");
            var originalPipeline = source.GetComponent<VRC.Core.PipelineManager>();
            if ((originalPipeline?.blueprintId ?? "") != blueprint) throw new InvalidOperationException("The Blueprint ID changed. Review the avatar again.");
            bool isNew = string.IsNullOrEmpty(blueprint);
            var metadata = new VRC.SDKBase.Editor.Api.VRCAvatar();
            if (!buildOnly)
            {
                if (isNew)
                    metadata = new VRC.SDKBase.Editor.Api.VRCAvatar { Name = name.Trim(), Description = "",
                        ReleaseStatus = "private", Tags = new List<string>() };
                else
                {
                    progress("Loading existing avatar metadata…");
                    metadata = await VRC.SDKBase.Editor.Api.VRCApi.GetAvatar(blueprint, cancellationToken: cancellation);
                    if (metadata.AuthorId != VRC.Core.APIUser.CurrentUser.id)
                        throw new InvalidOperationException("The selected avatar's Blueprint ID belongs to another user.");
                }
            }
            string uploadedId = "";
            var thumbnailKey = GlobalObjectId.GetGlobalObjectIdSlow(source.gameObject).ToString();
            EventHandler<string> buildProgress = (sender, message) => progress(message);
            EventHandler<(string status, float percentage)> uploadProgress = (sender, value) => progress(value.status + " " + (value.percentage * 100).ToString("0") + "%");
            builder.OnSdkBuildProgress += buildProgress;
            builder.OnSdkUploadProgress += uploadProgress;
            try
            {
                await RunOnAvatarCopy(source, async clone =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (buildOnly)
                    {
                        progress("Building and validating locally. Nothing will be uploaded…");
                        await builder.Build(clone);
                        cancellation.ThrowIfCancellationRequested();
                    }
                    else
                    {
                        progress("Preparing avatar thumbnail…");
                        thumbnail = ResolveSceneUploadThumbnail(clone, thumbnailKey,
                            !isNew && (!string.IsNullOrEmpty(metadata.ImageUrl) || !string.IsNullOrEmpty(metadata.ThumbnailImageUrl)));
                        progress("Building and uploading. Complete any SDK confirmation in Unity…");
                        using (ShiroTools.OutfitBatchUploader.BeginSceneUploadConsent())
                            await builder.BuildAndUpload(clone, metadata, thumbnail, cancellationToken: cancellation);
                        uploadedId = clone.GetComponent<VRC.Core.PipelineManager>()?.blueprintId ?? "";
                        if (string.IsNullOrEmpty(uploadedId)) throw new InvalidOperationException("SDK returned without a Blueprint ID. Check VRChat before retrying.");
                    }
                });
            }
            finally { builder.OnSdkBuildProgress -= buildProgress; builder.OnSdkUploadProgress -= uploadProgress; }
            if (!buildOnly)
            {
                if (source == null) return new WardrobeUploadOutcome { ok = true, blueprintId = uploadedId, isNew = isNew,
                    message = "Uploaded, but the source avatar was removed. Save this Blueprint ID: " + uploadedId };
                var pipeline = source.GetComponent<VRC.Core.PipelineManager>() ?? Undo.AddComponent<VRC.Core.PipelineManager>(source.gameObject);
                if ((pipeline.blueprintId ?? "") != blueprint)
                    return new WardrobeUploadOutcome { ok = true, blueprintId = uploadedId, isNew = isNew,
                        message = "Uploaded. Source Blueprint ID changed during upload and was not overwritten. Uploaded ID: " + uploadedId };
                Undo.RecordObject(pipeline, "Save uploaded avatar ID");
                pipeline.blueprintId = uploadedId;
                PrefabUtility.RecordPrefabInstancePropertyModifications(pipeline);
                EditorUtility.SetDirty(pipeline);
                EditorSceneManager.MarkSceneDirty(source.gameObject.scene);
            }
            return new WardrobeUploadOutcome { ok = true, blueprintId = uploadedId, isNew = isNew,
                message = buildOnly ? "Build check passed. Nothing uploaded; the source avatar is unchanged."
                    : "Upload complete. Save your scene to retain the Blueprint ID: " + uploadedId };
        }

        internal static string SaveUploadSourceScenes()
        {
            try
            {
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (!scene.isLoaded || (!scene.isDirty && !string.IsNullOrEmpty(scene.path))) continue;
                    var path = scene.path;
                    if (string.IsNullOrEmpty(path))
                    {
                        const string folder = "Assets/Generated/WardrobeUploads/SourceScenes";
                        Directory.CreateDirectory(FullPath(folder));
                        path = folder + "/Source_" + Guid.NewGuid().ToString("N") + ".unity";
                    }
                    if (!EditorSceneManager.SaveScene(scene, path)) return "Could not save source scene: " + path;
                    WardrobeLog.Write("upload", "Saved source scene " + path);
                }
                return null;
            }
            catch (Exception error) { return "Could not save source scenes: " + error.Message; }
        }

        // ---- Full job: stage + upload + map. Caller must already be on the main thread. ----
        internal static async Task<WardrobeUploadOutcome> UploadVariantAsync(string guid)
        {
            var record = AvatarWardrobeCatalog.GetRecord(guid);
            if (record == null) return Outcome(false, "Outfit not found.");
            if (AvatarWardrobeCatalog.EffectiveKind(record) != WardrobeAssetKind.Outfit)
                return Outcome(false, "Only outfits can be uploaded as avatars.");
            var baseAvatar = AvatarWardrobeServer.SceneAvatar;
            if (baseAvatar == null) return Outcome(false, "Open an avatar scene first — no avatar selected.");
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
                return Outcome(false, "Switch Unity's build target to Windows first (File > Build Settings).");

            string baseName = baseAvatar.gameObject.name;
            string variantName = AvatarWardrobeCatalog.DisplayVariant(record);
            string scenePath = StagingScenePath(baseName, guid);

            var readinessError = ShiroTools.OutfitBatchUploader.WardrobeUploadReadinessError();
            if (readinessError != null) return Outcome(false, readinessError);
            var saveError = SaveUploadSourceScenes();
            if (saveError != null) return Outcome(false, saveError);

            var previousScene = EditorSceneManager.GetActiveScene();
            var previousSetup = EditorSceneManager.GetSceneManagerSetup();
            var previousAvatarId = GlobalObjectId.GetGlobalObjectIdSlow(baseAvatar.gameObject);
            Scene stagingScene = default(Scene);
            bool openedStaging = false;
            WardrobeLog.Write("upload", "Starting " + "outfit=" + guid);
            GameObject spare = null;
            var targetLock = AvatarWardrobeServer.LockUploadTarget();
            try
            {
                // Additive editor staging keeps the source scene alive while the clone is transferred.
                spare = (GameObject)UnityEngine.Object.Instantiate(baseAvatar.gameObject);
                spare.name = baseName + "_Wardrobe";
                spare.SetActive(false);

                stagingScene = SceneManager.GetSceneByPath(scenePath);
                if (stagingScene.IsValid() && stagingScene.isLoaded)
                    throw new InvalidOperationException("Close the generated staging scene before starting this upload.");
                if (AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath) != null)
                    stagingScene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
                else
                {
                    Directory.CreateDirectory(FullPath(Path.GetDirectoryName(scenePath)));
                    stagingScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                }
                openedStaging = true;
                SceneManager.SetActiveScene(stagingScene);
                SceneManager.MoveGameObjectToScene(spare, stagingScene);

                var staging = EnsureStaging(spare, guid, variantName, record);
                spare = null; // adopted as the staging root, or destroyed on reuse
                if (!EditorSceneManager.SaveScene(stagingScene, staging.scenePath))
                    throw new InvalidOperationException("Could not save the generated upload scene.");
                AssetDatabase.Refresh();

                var bridge = await ShiroTools.OutfitBatchUploader.UploadOutfitHeadless(
                    staging.avatarRootName, staging.outfitName);
                WardrobeLog.Write("upload", (bridge.ok ? "Completed " : "Failed ") + "outfit=" + guid + " " + bridge.message);
                var outcome = new WardrobeUploadOutcome
                {
                    ok = bridge.ok,
                    message = bridge.message ?? "",
                    blueprintId = bridge.blueprintId ?? "",
                    isNew = bridge.isNew,
                };
                if (bridge.ok)
                    UpsertMap(guid, baseName, staging.avatarRootName, staging.outfitName, staging.scenePath);
                return outcome;
            }
            catch (Exception exception)
            {
                WardrobeLog.Write("upload", "Failed " + "outfit=" + guid + " " + exception.GetType().Name + ": " + exception.Message);
                Debug.LogError("Avatar Wardrobe upload failed: " + exception);
                return Outcome(false, exception.Message);
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
                    Debug.LogWarning("Avatar Wardrobe upload: could not restore previous scene: " + exception.Message);
                }
            }
        }

        // ---- Staging: adopt the clone (or reuse the saved root), install the
        // outfit with no toggles, and park it under Outfits/<variantName> so the
        // batch uploader sees exactly one outfit. The staging scene is active. ----
        internal static StagingInfo EnsureStaging(
            GameObject baseAvatar, string variantKey, string variantName, WardrobeAssetRecord record)
        {
            if (baseAvatar == null) throw new ArgumentNullException("baseAvatar");
            if (record == null) throw new ArgumentNullException("record");
            if (string.IsNullOrEmpty(variantName)) variantName = "Default";

            // Caller names the clone "<BaseName>_Wardrobe" (deterministic avatarKey
            // for the uploader's project data); an existing saved root wins.
            string avatarRootName = baseAvatar.name;
            var scene = EditorSceneManager.GetActiveScene();
            GameObject existing = null;
            foreach (var top in scene.GetRootGameObjects())
                if (top != null && top != baseAvatar && top.name == avatarRootName) { existing = top; break; }
            if (existing == null)
            {
                var found = GameObject.Find(avatarRootName);
                if (found != null && found != baseAvatar) existing = found;
            }
            var root = existing != null ? existing : baseAvatar;
            if (existing != null) UnityEngine.Object.DestroyImmediate(baseAvatar);
            root.SetActive(true);

            var desc = root.GetComponent<VRCAvatarDescriptor>();
            if (desc == null) throw new InvalidOperationException("Staging avatar is missing its VRCAvatarDescriptor.");

            // One variant per avatar: drop other family members' installs.
            foreach (var sibling in FamilyVariants(record))
            {
                if (sibling == null || sibling.guid == record.guid) continue;
                GameObject installed;
                while (AvatarWardrobeCatalog.TryFindInstalled(desc, sibling, out installed) && installed != null)
                    AvatarWardrobeCatalog.Remove(installed);
            }

            GameObject instance;
            if (!AvatarWardrobeCatalog.TryFindInstalled(desc, record, out instance) || instance == null)
            {
                var installed = AvatarWardrobeCatalog.Install(desc, record, true, false);
                if (!installed.success || installed.instance == null)
                    throw new InvalidOperationException(installed.message ?? "Outfit install failed.");
                instance = installed.instance;
            }

            var outfits = FindOrCreate(root.transform, "Outfits");
            var holder = FindOrCreate(outfits.transform, variantName);
            if (instance.transform.parent != holder.transform)
                instance.transform.SetParent(holder.transform, true);
            instance.SetActive(true);

            string scenePath = EditorSceneManager.GetActiveScene().path;
            if (string.IsNullOrEmpty(scenePath))
            {
                const string suffix = "_Wardrobe";
                string baseName = avatarRootName.EndsWith(suffix, StringComparison.Ordinal)
                    ? avatarRootName.Substring(0, avatarRootName.Length - suffix.Length)
                    : avatarRootName;
                scenePath = StagingScenePath(baseName, variantKey);
            }
            return new StagingInfo { scenePath = scenePath, avatarRootName = avatarRootName, outfitName = variantName };
        }

        // ---- Live status: map entry + the uploader's project record. ----
        internal static WardrobeUploadStatus GetStatus(string guid)
        {
            var status = new WardrobeUploadStatus();
            var entry = LoadMap().FirstOrDefault(e => e != null && e.guid == guid);
            if (entry == null) return status;
            status.known = true;
            try
            {
                var data = ShiroTools.OutfitProjectData.GetOutfit(entry.avatarRootName, entry.outfitName);
                if (data != null)
                {
                    status.blueprintId = data.blueprintId ?? "";
                    status.lastUpload = data.lastUploadWindows ?? "";
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Avatar Wardrobe upload status: " + exception.Message);
            }
            return status;
        }

        // ---- Pure helpers ----

        internal static string Safe(string name)
        {
            string safe = Regex.Replace(name ?? "", "[^A-Za-z0-9_-]+", "_").Trim('_');
            return string.IsNullOrEmpty(safe) ? "unnamed" : safe;
        }

        internal static string StagingScenePath(string baseName, string variantKey)
        {
            return "Assets/Generated/WardrobeUploads/"
                + Safe(baseName) + "_" + Safe(variantKey) + "/Upload.unity";
        }

        private static string FullPath(string assetPath)
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));
        }

        private static List<WardrobeAssetRecord> FamilyVariants(WardrobeAssetRecord record)
        {
            foreach (var family in AvatarWardrobeCatalog.Families())
            {
                if (family == null || family.variants == null) continue;
                if (family.variants.Any(v => v != null && v.guid == record.guid))
                    return family.variants;
            }
            return new List<WardrobeAssetRecord> { record };
        }

        private static GameObject FindOrCreate(Transform parent, string name)
        {
            foreach (Transform child in parent)
                if (child.name == name) return child.gameObject;
            var created = new GameObject(name);
            created.transform.SetParent(parent, false);
            return created;
        }

        private static WardrobeUploadOutcome Outcome(bool ok, string message)
        {
            return new WardrobeUploadOutcome { ok = ok, message = message ?? "" };
        }

        // ---- Upload map (guid -> staging), project-local like the uploader's store ----

        private static string MapFilePath
        {
            get { return Path.Combine("ProjectSettings", "AvatarWardrobeUploads.json"); }
        }

        [Serializable]
        private sealed class UploadMapEntry
        {
            public string guid = "";
            public string baseName = "";
            public string avatarRootName = "";
            public string outfitName = "";
            public string scenePath = "";
            public string updated = "";
        }

        [Serializable]
        private sealed class UploadMapFile
        {
            public List<UploadMapEntry> entries = new List<UploadMapEntry>();
        }

        private static List<UploadMapEntry> LoadMap()
        {
            try
            {
                if (File.Exists(MapFilePath))
                {
                    var parsed = JsonUtility.FromJson<UploadMapFile>(File.ReadAllText(MapFilePath));
                    if (parsed != null && parsed.entries != null) return parsed.entries;
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Avatar Wardrobe could not read its upload map: " + exception.Message);
            }
            return new List<UploadMapEntry>();
        }

        private static void UpsertMap(string guid, string baseName, string avatarRootName, string outfitName, string scenePath)
        {
            var entries = LoadMap();
            var entry = entries.FirstOrDefault(e => e != null && e.guid == guid);
            if (entry == null)
            {
                entry = new UploadMapEntry { guid = guid };
                entries.Add(entry);
            }
            entry.baseName = baseName ?? "";
            entry.avatarRootName = avatarRootName ?? "";
            entry.outfitName = outfitName ?? "";
            entry.scenePath = scenePath ?? "";
            entry.updated = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            try
            {
                WardrobeAtomicFile.WriteText(MapFilePath, JsonUtility.ToJson(new UploadMapFile { entries = entries }, true));
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Avatar Wardrobe could not write its upload map: " + exception.Message);
            }
        }
    }
}
