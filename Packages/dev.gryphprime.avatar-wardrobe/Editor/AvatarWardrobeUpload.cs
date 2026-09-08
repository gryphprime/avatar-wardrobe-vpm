// Whole-avatar build-copy lifecycle and shared preset staging result types.
// Unity API entry points run on the Editor main thread.
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
            public GameObject root;
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

        internal static string Safe(string name)
        {
            string safe = Regex.Replace(name ?? "", "[^A-Za-z0-9_-]+", "_").Trim('_');
            return string.IsNullOrEmpty(safe) ? "unnamed" : safe;
        }
        private static string FullPath(string assetPath) => Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));
    }
}
