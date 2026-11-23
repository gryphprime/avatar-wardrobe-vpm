// Per-Look VRChat thumbnails.
//
// Every Look has a thumbnail plan: a source and an update rule. A Look with no
// source of its own follows the project default.
//   source  auto    head-and-shoulders portrait on a solid background
//           camera  a WardrobeThumbnailCamera in the scene (backdrop and lights kept)
//           image   a picture the user uploaded, cropped to 4:3
//   update  always  render and send on every upload (skipped when the image is
//                   identical to the last one VRChat accepted for that avatar ID)
//           keep    leave the current VRChat thumbnail alone
// New avatars always get a thumbnail, whatever the update rule says.
//
// Captures run on the staged copy after the Look is activated (blendshapes,
// Items, FaceEmo), right before BuildAndUpload, so the picture matches the
// upload. Uploaded originals live in ProjectSettings/AvatarWardrobe/Thumbnails
// (kept with the project, never imported as assets); the 4:3 crop is rendered
// from the original at upload time so it can be re-cropped without loss.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
using ThumbCamera = OutfitToggleGenerator.WardrobeThumbnailCamera;

namespace ShiroTools
{
    public partial class OutfitBatchUploader
    {
        internal const int ThumbWidth = 1200, ThumbHeight = 900;
        internal const string DefaultLookId = "default";
        private const string SESSION_EXPRESS_THUMB_HASH = "Shiro_Express_ThumbHash";
        private const string StagingPrefix = "Assets/Generated/WardrobeUploads/";
        private static readonly Regex StoreName = new Regex("^[0-9a-f]{32}\\.(png|jpg)$", RegexOptions.CultureInvariant);

        internal static string ThumbStoreDir =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "ProjectSettings", "AvatarWardrobe", "Thumbnails"));

        // ============================================================
        //  Plan resolution
        // ============================================================
        internal sealed class ThumbPlan
        {
            public string source = "auto", update = "keep", camera = "", original = "";
            public float cropX = 0.5f, cropY = 0.5f, cropZoom = 1f;
            public bool inherited;
        }

        internal static bool IsThumbSource(string s) => s == "auto" || s == "camera" || s == "image";

        internal static ThumbPlan EffectiveThumb(OutfitProjectData.OutfitData data)
        {
            var own = data?.thumb;
            if (own != null && IsThumbSource(own.source)) return ToPlan(own, false);
            return ToPlan(EffectiveThumbDefault(), true);
        }

        private static ThumbPlan ToPlan(OutfitProjectData.ThumbSettings s, bool inherited) => new ThumbPlan
        {
            source = IsThumbSource(s.source) ? s.source : "auto",
            update = s.update == "always" ? "always" : "keep",
            camera = s.camera ?? "",
            original = s.original ?? "",
            cropX = s.cropX, cropY = s.cropY, cropZoom = s.cropZoom,
            inherited = inherited,
        };

        // The stored default, or (before anyone set one) what the old global
        // new-avatar preference meant: a default image, otherwise the portrait.
        internal static OutfitProjectData.ThumbSettings EffectiveThumbDefault()
        {
            var stored = OutfitProjectData.ThumbDefault;
            if (IsThumbSource(stored.source)) return stored;
            var legacy = new OutfitProjectData.ThumbSettings
            {
                source = "auto",
                update = stored.update == "always" ? "always" : "keep",
            };
            if (EditorPrefs.GetString(NS_THUMB_MODE, "scene") == "image")
            {
                var path = EditorPrefs.GetString(NS_THUMB_IMG, "");
                try
                {
                    var abs = string.IsNullOrEmpty(path) ? "" : Path.GetFullPath(path);
                    if (abs.Length > 0 && File.Exists(abs)) { legacy.source = "image"; legacy.original = abs; }
                }
                catch (Exception) { }
            }
            return legacy;
        }

        internal static string ThumbOriginalPath(string original)
        {
            if (string.IsNullOrEmpty(original)) return null;
            if (StoreName.IsMatch(original))
            {
                var stored = Path.Combine(ThumbStoreDir, original);
                return File.Exists(stored) ? stored : null;
            }
            // Only the legacy default image is referenced by absolute path.
            return Path.IsPathRooted(original) && File.Exists(original) ? original : null;
        }

        // Normalized crop window of a w×h image inside the 4:3 frame. Zoom 1
        // covers the frame; (cx, cy) is the window center from the top-left.
        // The web crop editor uses the same math.
        internal static void ThumbCropWindow(int w, int h, float zoom, ref float cx, ref float cy, out float u, out float v)
        {
            zoom = Mathf.Clamp(float.IsNaN(zoom) ? 1f : zoom, 1f, 8f);
            float a = (float)Math.Max(1, w) / Math.Max(1, h), frame = (float)ThumbWidth / ThumbHeight;
            if (a > frame) { u = frame / a; v = 1f; } else { u = 1f; v = a / frame; }
            u /= zoom; v /= zoom;
            cx = Mathf.Clamp(float.IsNaN(cx) ? 0.5f : cx, u * 0.5f, 1f - u * 0.5f);
            cy = Mathf.Clamp(float.IsNaN(cy) ? 0.5f : cy, v * 0.5f, 1f - v * 0.5f);
        }

        // ============================================================
        //  Rendering
        // ============================================================

        /// <summary>Renders a Look's thumbnail to a temp PNG and returns its path.
        /// When the chosen camera or image is unavailable, <paramref name="allowFallback"/>
        /// renders the automatic portrait instead; otherwise returns null.</summary>
        internal static string RenderThumbnail(GameObject root, ThumbPlan plan, Color background, bool allowFallback,
            out string used, out string warning)
        {
            used = plan.source; warning = null;
            byte[] png = null;
            try
            {
                if (plan.source == "image")
                {
                    var path = ThumbOriginalPath(plan.original);
                    if (path == null) warning = "The thumbnail image is missing.";
                    else png = RenderCroppedThumbnail(File.ReadAllBytes(path), plan.cropX, plan.cropY, plan.cropZoom);
                }
                else if (plan.source == "camera")
                {
                    var camera = FindThumbCamera(plan.camera, root);
                    if (camera == null) warning = "The thumbnail camera is not in the scene.";
                    else png = RenderCameraThumbnail(camera, root);
                }
            }
            catch (Exception ex)
            {
                warning = ex.Message;
                png = null;
            }
            if (png == null)
            {
                if (plan.source != "auto" && !allowFallback) return null;
                if (root == null) return null;
                used = "auto";
                png = RenderPortraitThumbnail(root, background, null);
            }
            if (png == null) return null;
            var file = Path.Combine(Path.GetTempPath(), "shiro_thumb_" + DateTime.Now.Ticks + ".png");
            File.WriteAllBytes(file, png);
            return file;
        }

        /// <summary>The original capture: a temporary camera, solid background, only
        /// the avatar visible. <paramref name="view"/> copies a camera's angle
        /// (the Scene view preview); null frames a head-and-upper-body portrait.</summary>
        internal static byte[] RenderPortraitThumbnail(GameObject root, Color background, Camera view)
        {
            GameObject camGo = null;
            RenderTexture rt = null;
            RenderTexture prevActive = RenderTexture.active;
            try
            {
                camGo = new GameObject("~ShiroThumbCam") { hideFlags = HideFlags.HideAndDontSave };
                var cam = camGo.AddComponent<Camera>();
                cam.enabled = false;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = background;
                cam.fieldOfView = 30f;
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = 100f;
                if (view != null)
                {
                    cam.transform.SetPositionAndRotation(view.transform.position, view.transform.rotation);
                    cam.fieldOfView = view.fieldOfView;
                    cam.orthographic = view.orthographic;
                    cam.orthographicSize = view.orthographicSize;
                }
                else FrameThumbnailPortrait(cam, root);

                rt = new RenderTexture(ThumbWidth, ThumbHeight, 24, RenderTextureFormat.ARGB32);
                cam.targetTexture = rt;
                using (new ThumbnailIsolationScope(root))
                    cam.Render();
                return ReadPng(rt);
            }
            finally
            {
                RenderTexture.active = prevActive;
                if (rt != null) { rt.Release(); UnityEngine.Object.DestroyImmediate(rt); }
                if (camGo != null) UnityEngine.Object.DestroyImmediate(camGo);
            }
        }

        /// <summary>Renders through the user's own camera (so its clear flags, skybox
        /// and post-processing apply) at 4:3, 2× supersampled. Other avatars are
        /// hidden, including the source avatar that overlaps an upload staging copy.</summary>
        internal static byte[] RenderCameraThumbnail(Camera camera, GameObject root)
        {
            if (camera == null) throw new ArgumentNullException(nameof(camera));
            if (!camera.gameObject.activeInHierarchy)
                throw new InvalidOperationException("The thumbnail camera '" + camera.name + "' is inactive.");
            var marker = camera.GetComponent<ThumbCamera>();
            bool avatarOnly = marker != null && marker.avatarOnly && root != null;
            var big = RenderTexture.GetTemporary(ThumbWidth * 2, ThumbHeight * 2, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            var small = RenderTexture.GetTemporary(ThumbWidth, ThumbHeight, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            var prevTarget = camera.targetTexture;
            var prevActive = RenderTexture.active;
            try
            {
                using (avatarOnly ? (IDisposable)new ThumbnailIsolationScope(root) : new OtherAvatarsHiddenScope(root))
                {
                    camera.targetTexture = big;
                    camera.aspect = (float)ThumbWidth / ThumbHeight;
                    camera.Render();
                }
                Graphics.Blit(big, small);
                return ReadPng(small);
            }
            finally
            {
                camera.targetTexture = prevTarget;
                camera.ResetAspect();
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(big);
                RenderTexture.ReleaseTemporary(small);
            }
        }

        internal static byte[] RenderCroppedThumbnail(byte[] image, float cx, float cy, float zoom)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
            RenderTexture rt = null;
            var prevActive = RenderTexture.active;
            try
            {
                if (!tex.LoadImage(image, false)) throw new InvalidDataException("The thumbnail image could not be read.");
                if (tex.mipmapCount > 1) tex.Apply(true);
                tex.filterMode = FilterMode.Trilinear;
                tex.wrapMode = TextureWrapMode.Clamp;
                ThumbCropWindow(tex.width, tex.height, zoom, ref cx, ref cy, out var u, out var v);
                rt = RenderTexture.GetTemporary(ThumbWidth, ThumbHeight, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
                // Texture UVs start bottom-left; the crop center is measured from the top.
                Graphics.Blit(tex, rt, new Vector2(u, v), new Vector2(cx - u * 0.5f, 1f - cy - v * 0.5f));
                return ReadPng(rt);
            }
            finally
            {
                RenderTexture.active = prevActive;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        private static byte[] ReadPng(RenderTexture rt)
        {
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            try
            {
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                return tex.EncodeToPNG();
            }
            finally { UnityEngine.Object.DestroyImmediate(tex); }
        }

        /// <summary>Finds the camera for a plan. The upload staging copy carries a
        /// clone of cameras placed under the avatar, so prefer one under the render
        /// root, then a free-standing scene camera, then any match.</summary>
        internal static Camera FindThumbCamera(string id, GameObject root)
        {
            if (string.IsNullOrEmpty(id)) return null;
            var found = UnityEngine.Object.FindObjectsOfType<ThumbCamera>(true)
                .Where(m => m != null && m.id == id && m.GetComponent<Camera>() != null).ToList();
            var pick = found.FirstOrDefault(m => root != null && m.transform.IsChildOf(root.transform))
                ?? found.FirstOrDefault(m => m.GetComponentInParent<VRCAvatarDescriptor>(true) == null)
                ?? found.FirstOrDefault();
            return pick != null ? pick.GetComponent<Camera>() : null;
        }

        // Hides renderers of every avatar except the one being captured. The
        // environment (backdrop, props, lights) stays visible.
        internal sealed class OtherAvatarsHiddenScope : IDisposable
        {
            private readonly List<Renderer> hidden = new List<Renderer>();
            internal OtherAvatarsHiddenScope(GameObject root)
            {
                foreach (var descriptor in UnityEngine.Object.FindObjectsOfType<VRCAvatarDescriptor>(true))
                {
                    if (descriptor == null) continue;
                    if (root != null && (descriptor.gameObject == root || root.transform.IsChildOf(descriptor.transform) ||
                        descriptor.transform.IsChildOf(root.transform))) continue;
                    foreach (var renderer in descriptor.GetComponentsInChildren<Renderer>(true))
                    {
                        if (renderer == null || renderer.forceRenderingOff) continue;
                        renderer.forceRenderingOff = true;
                        hidden.Add(renderer);
                    }
                }
            }
            public void Dispose()
            {
                foreach (var renderer in hidden)
                    if (renderer != null) renderer.forceRenderingOff = false;
                hidden.Clear();
            }
        }

        // ============================================================
        //  Upload integration
        // ============================================================

        /// <summary>Thumbnail to send with an upload of <paramref name="entry"/>, or null
        /// to keep VRChat's current one. Must run after the Look is activated.</summary>
        private string ResolveUploadThumbnail(OutfitEntry entry, bool isNew, string blueprintId, out string hash)
        {
            LoadNewSetupDefaults();
            return ResolveUploadThumbnail(_avatarRoot, entry?.Data, entry?.Name, isNew, blueprintId, _nsBgColor, out hash);
        }

        internal static string ResolveUploadThumbnail(GameObject root, OutfitProjectData.OutfitData data, string lookName,
            bool isNew, string blueprintId, Color background, out string hash)
        {
            hash = null;
            var plan = EffectiveThumb(data);
            if (!isNew && plan.update != "always") return null;
            var path = RenderThumbnail(root, plan, background, isNew, out var used, out var warning);
            if (warning != null)
                Debug.LogWarning("[OutfitBatchUploader] Thumbnail for '" + lookName + "': " + warning +
                    (path == null ? " The current VRChat thumbnail is kept." : " Using the automatic portrait."));
            if (path == null) return null;
            hash = HashThumbFile(path);
            if (!isNew && data != null && data.thumbSentHash == hash && data.thumbSentBlueprint == (blueprintId ?? ""))
            {
                CleanupTempThumb(path);
                return null;
            }
            return path;
        }

        internal static void RecordThumbSent(OutfitProjectData.OutfitData data, string hash, string blueprintId)
        {
            if (data == null || string.IsNullOrEmpty(hash)) return;
            data.thumbSentHash = hash;
            data.thumbSentBlueprint = blueprintId ?? "";
            OutfitProjectData.Save();
        }

        internal static string HashThumbFile(string path)
        {
            try
            {
                using (var sha = SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant();
            }
            catch (Exception) { return null; }
        }

        // ============================================================
        //  Image store
        // ============================================================
        internal static bool IsPng(byte[] b) => b != null && b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47;
        internal static bool IsJpeg(byte[] b) => b != null && b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;

        internal static string StoreThumbOriginal(byte[] bytes)
        {
            string ext = IsPng(bytes) ? "png" : IsJpeg(bytes) ? "jpg" : null;
            if (ext == null) throw new InvalidDataException("Use a PNG or JPEG image.");
            var dir = ThumbStoreDir;
            Directory.CreateDirectory(dir);
            var name = Guid.NewGuid().ToString("N") + "." + ext;
            var path = Path.Combine(dir, name);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path);
            return name;
        }

        private static void DeleteUnreferencedOriginal(string name)
        {
            if (string.IsNullOrEmpty(name) || !StoreName.IsMatch(name)) return;
            if (OutfitProjectData.AllThumbSettings().Any(s => s != null && s.original == name)) return;
            try { File.Delete(Path.Combine(ThumbStoreDir, name)); }
            catch (Exception ex) { Debug.LogWarning("[OutfitBatchUploader] Could not delete an old thumbnail image: " + ex.Message); }
        }

        // ============================================================
        //  Cameras
        // ============================================================
        internal static ThumbCamera CreateThumbCamera(GameObject avatar, bool fromSceneView, Color background)
        {
            if (avatar == null) throw new InvalidOperationException("Choose an avatar first.");
            var go = new GameObject("Thumbnail Camera");
            // EditorOnly: NDMF strips the object from every build, so the Camera never ships.
            go.tag = "EditorOnly";
            go.transform.SetParent(avatar.transform, false);
            var cam = go.AddComponent<Camera>();
            cam.enabled = false;
            cam.fieldOfView = 30f;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 100f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = background;
            var marker = go.AddComponent<ThumbCamera>();
            marker.id = Guid.NewGuid().ToString("N");
            var view = fromSceneView && SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.camera : null;
            if (view != null)
            {
                go.transform.SetPositionAndRotation(view.transform.position, view.transform.rotation);
                cam.fieldOfView = view.fieldOfView;
                cam.orthographic = view.orthographic;
                cam.orthographicSize = view.orthographicSize;
            }
            else FrameThumbnailPortrait(cam, avatar);
            Undo.RegisterCreatedObjectUndo(go, "Create thumbnail camera");
            EditorSceneManager.MarkSceneDirty(go.scene);
            Selection.activeGameObject = go;
            EditorGUIUtility.PingObject(go);
            return marker;
        }

        // Turns a picked scene camera into a thumbnail camera. Duplicated
        // cameras share an id; the picked one gets a fresh id in that case.
        private static string AdoptThumbCamera(string reference)
        {
            if (string.IsNullOrEmpty(reference)) return "";
            if (!reference.StartsWith("iid:", StringComparison.Ordinal)) return reference;
            if (!int.TryParse(reference.Substring(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var instanceId))
                throw new InvalidOperationException("Unknown camera.");
            var cam = EditorUtility.InstanceIDToObject(instanceId) as Camera;
            if (cam == null || EditorUtility.IsPersistent(cam) || IsStagingScene(cam.gameObject.scene))
                throw new InvalidOperationException("That camera is no longer in the scene.");
            var marker = cam.GetComponent<ThumbCamera>();
            if (marker == null)
            {
                marker = Undo.AddComponent<ThumbCamera>(cam.gameObject);
                marker.id = "";
            }
            bool shared = !string.IsNullOrEmpty(marker.id) && UnityEngine.Object.FindObjectsOfType<ThumbCamera>(true)
                .Any(m => m != null && m != marker && m.id == marker.id && !IsStagingScene(m.gameObject.scene));
            if (string.IsNullOrEmpty(marker.id) || shared)
            {
                Undo.RecordObject(marker, "Thumbnail camera");
                marker.id = Guid.NewGuid().ToString("N");
                EditorUtility.SetDirty(marker);
            }
            EditorSceneManager.MarkSceneDirty(cam.gameObject.scene);
            return marker.id;
        }

        private static bool IsStagingScene(Scene scene) =>
            scene.IsValid() && (scene.path ?? "").StartsWith(StagingPrefix, StringComparison.Ordinal);

        private static string HierarchyPath(Transform t)
        {
            var parts = new List<string>();
            for (var cur = t; cur != null; cur = cur.parent) parts.Add(cur.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        // ============================================================
        //  Web API
        // ============================================================
        [Serializable]
        public class WebThumbCameraDto { public string id = ""; public string reference = ""; public string name = ""; public string path = ""; public int underAvatar; }
        [Serializable]
        public class WebLookThumbDto
        {
            public int ok; public string message = "";
            public string id = ""; public string name = "";
            public string source = ""; public string update = ""; public string camera = "";
            public string original = ""; public float cropX = 0.5f, cropY = 0.5f, cropZoom = 1f;
            public int inherited; public int isNew;
            public string defSource = ""; public string defUpdate = "";
            public List<WebThumbCameraDto> cameras = new List<WebThumbCameraDto>();
        }
        [Serializable]
        public class WebLookThumbPreviewDto { public int ok; public string message = ""; public string token = ""; public string used = ""; }

        private static bool LookThumbSettings(string id, bool forWrite, out OutfitProjectData.ThumbSettings settings,
            out OutfitProjectData.OutfitData data, out string message)
        {
            settings = null; data = null; message = null;
            if (id == DefaultLookId)
            {
                settings = OutfitProjectData.ThumbDefault;
                if (forWrite && !IsThumbSource(settings.source))
                {
                    // First edit of the default: start from what it effectively was.
                    var effective = EffectiveThumbDefault();
                    settings.source = effective.source; settings.update = effective.update; settings.original = effective.original;
                    settings.camera = effective.camera; settings.cropX = effective.cropX; settings.cropY = effective.cropY; settings.cropZoom = effective.cropZoom;
                }
                return true;
            }
            if (!PresetKeys(id, out var avatarKey, out var outfitName, out message)) return false;
            data = OutfitProjectData.GetOutfit(avatarKey, outfitName);
            settings = data.thumb ??= new OutfitProjectData.ThumbSettings();
            return true;
        }

        internal static WebLookThumbDto WebLookThumb(string id)
        {
            using var readScope = OutfitProjectData.ReadOnly();
            var r = new WebLookThumbDto { id = id ?? "" };
            try
            {
                if (!LookThumbSettings(id, false, out var settings, out var data, out var message))
                    return new WebLookThumbDto { id = id ?? "", message = message };
                var def = EffectiveThumbDefault();
                var plan = id == DefaultLookId ? ToPlan(def, false) : EffectiveThumb(data);
                r.source = plan.source; r.update = plan.update; r.camera = plan.camera;
                r.original = ThumbOriginalPath(plan.original) != null ? plan.original : "";
                if (Path.IsPathRooted(r.original)) r.original = "legacy";
                r.cropX = plan.cropX; r.cropY = plan.cropY; r.cropZoom = plan.cropZoom;
                r.inherited = plan.inherited ? 1 : 0;
                r.defSource = def.source; r.defUpdate = def.update == "always" ? "always" : "keep";
                if (id == DefaultLookId) r.name = "default";
                else
                {
                    var preset = OutfitToggleGenerator.AvatarWardrobePresets.GetPreset(id);
                    r.name = preset?.name ?? "";
                    r.isNew = IsValidBlueprintId(data?.blueprintId) ? 0 : 1;
                }
                r.cameras = ListThumbCameras();
                r.ok = 1;
            }
            catch (Exception ex) { r.message = ex.Message; }
            return r;
        }

        internal static List<WebThumbCameraDto> ListThumbCameras()
        {
            var avatar = WebParentAvatar();
            var list = new List<WebThumbCameraDto>();
            foreach (var cam in UnityEngine.Object.FindObjectsOfType<Camera>(true))
            {
                if (cam == null || (cam.hideFlags & (HideFlags.HideInHierarchy | HideFlags.DontSave)) != 0) continue;
                if (EditorUtility.IsPersistent(cam) || !cam.gameObject.scene.IsValid() || IsStagingScene(cam.gameObject.scene)) continue;
                var marker = cam.GetComponent<ThumbCamera>();
                list.Add(new WebThumbCameraDto
                {
                    id = marker != null ? marker.id ?? "" : "",
                    reference = "iid:" + cam.GetInstanceID().ToString(CultureInfo.InvariantCulture),
                    name = cam.name,
                    path = HierarchyPath(cam.transform),
                    underAvatar = avatar != null && cam.transform.IsChildOf(avatar.transform) ? 1 : 0,
                });
            }
            return list.OrderBy(c => c.id.Length == 0).ThenBy(c => c.underAvatar == 0).ThenBy(c => c.path, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Applies the fields present in the query to one or more Looks (ids,
        /// newline-separated; "default" for the project default). source "" makes a
        /// Look follow the default again.</summary>
        internal static WebResultDto WebLookThumbSet(Dictionary<string, string> q)
        {
            try
            {
                q.TryGetValue("id", out var ids);
                var targets = SplitNames(ids);
                if (targets.Count == 0) return new WebResultDto { message = "No look selected." };
                string cameraId = null;
                if (q.TryGetValue("camera", out var cameraRef)) cameraId = AdoptThumbCamera(cameraRef);
                foreach (var id in targets)
                {
                    if (!LookThumbSettings(id, true, out var s, out _, out var message)) return new WebResultDto { message = message };
                    bool wasOwn = IsThumbSource(s.source);
                    if (!wasOwn && id != DefaultLookId)
                    {
                        // Leaving the default: start from the default's plan.
                        var def = EffectiveThumbDefault();
                        s.update = def.update == "always" ? "always" : "keep";
                        s.camera = def.camera ?? ""; s.original = def.original ?? "";
                        s.cropX = def.cropX; s.cropY = def.cropY; s.cropZoom = def.cropZoom;
                        s.source = IsThumbSource(def.source) ? def.source : "auto";
                    }
                    if (q.TryGetValue("source", out var source))
                    {
                        if (source == "" && id != DefaultLookId) { s.source = ""; continue; }
                        if (!IsThumbSource(source)) return new WebResultDto { message = "Unknown thumbnail source." };
                        s.source = source;
                    }
                    if (cameraId != null) { s.camera = cameraId; if (!q.ContainsKey("source")) s.source = "camera"; }
                    if (q.TryGetValue("update", out var update)) s.update = update == "always" ? "always" : "keep";
                    if (q.TryGetValue("cropX", out var cx) && TryFloat(cx, out var fx)) s.cropX = Mathf.Clamp01(fx);
                    if (q.TryGetValue("cropY", out var cy) && TryFloat(cy, out var fy)) s.cropY = Mathf.Clamp01(fy);
                    if (q.TryGetValue("cropZoom", out var cz) && TryFloat(cz, out var fz)) s.cropZoom = Mathf.Clamp(fz, 1f, 8f);
                }
                OutfitProjectData.Save();
                return new WebResultDto { ok = 1 };
            }
            catch (Exception ex) { return new WebResultDto { message = ex.Message }; }
        }

        private static bool TryFloat(string value, out float result) =>
            float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) && !float.IsNaN(result) && !float.IsInfinity(result);

        internal static WebResultDto WebLookThumbUpload(string id, byte[] body)
        {
            try
            {
                if (!IsPng(body) && !IsJpeg(body)) return new WebResultDto { message = "Use a PNG or JPEG image." };
                if (!LookThumbSettings(id, true, out var s, out _, out var message)) return new WebResultDto { message = message };
                var old = s.original;
                if (!IsThumbSource(s.source)) s.update = EffectiveThumbDefault().update == "always" ? "always" : "keep";
                s.original = StoreThumbOriginal(body);
                s.source = "image";
                s.cropX = 0.5f; s.cropY = 0.5f; s.cropZoom = 1f;
                OutfitProjectData.Save();
                DeleteUnreferencedOriginal(old);
                return new WebResultDto { ok = 1, message = s.original };
            }
            catch (Exception ex) { return new WebResultDto { message = ex.Message }; }
        }

        internal static string WebLookThumbOriginalPath(string id)
        {
            using var readScope = OutfitProjectData.ReadOnly();
            try
            {
                if (!LookThumbSettings(id, false, out _, out var data, out _)) return null;
                var plan = id == DefaultLookId ? ToPlan(EffectiveThumbDefault(), false) : EffectiveThumb(data);
                return ThumbOriginalPath(plan.original);
            }
            catch (Exception) { return null; }
        }

        internal static WebResultDto WebLookThumbCamera(string id, string from)
        {
            try
            {
                var avatar = WebParentAvatar();
                if (avatar == null) return new WebResultDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("install.noavatar") };
                var eng = WebEngine();
                eng.LoadNewSetupDefaults();
                var marker = CreateThumbCamera(avatar, from == "view", eng._nsBgColor);
                var set = WebLookThumbSet(new Dictionary<string, string> { { "id", id }, { "source", "camera" }, { "camera", marker.id } });
                if (set.ok == 0) return set;
                return new WebResultDto { ok = 1, message = marker.id };
            }
            catch (Exception ex) { return new WebResultDto { message = ex.Message }; }
        }

        private static readonly Dictionary<string, string> _lookPreviewTokens = new Dictionary<string, string>();

        /// <summary>Renders what this Look would send. Camera and auto plans stage
        /// the Look exactly like an upload (as the Play Mode test does), render,
        /// then restore the scene.</summary>
        internal static WebLookThumbPreviewDto WebLookThumbPreview(string id)
        {
            try
            {
                var preset = id == DefaultLookId ? null : OutfitToggleGenerator.AvatarWardrobePresets.GetPreset(id);
                if (preset == null) return new WebLookThumbPreviewDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("preset.notfound") };
                if (!LookThumbSettings(id, false, out _, out var data, out var message)) return new WebLookThumbPreviewDto { message = message };
                var plan = EffectiveThumb(data);
                var eng = WebEngine();
                eng.LoadNewSetupDefaults();
                string path, used, warning;
                if (plan.source == "image")
                    path = RenderThumbnail(null, plan, eng._nsBgColor, false, out used, out warning);
                else
                {
                    var start = OutfitToggleGenerator.WardrobePlayModeTest.Start(id, false);
                    if (start == null || start.ok == 0) return new WebLookThumbPreviewDto { message = start?.message ?? "Could not stage the look." };
                    try
                    {
                        var staging = SceneManager.GetSceneByPath(preset.scenePath);
                        var root = global::OutfitToggleGenerator.AvatarWardrobePresets.FindStagedRoot(staging);
                        if (root == null) return new WebLookThumbPreviewDto { message = "Could not stage the look." };
                        path = RenderThumbnail(root, plan, eng._nsBgColor, true, out used, out warning);
                    }
                    finally { OutfitToggleGenerator.WardrobePlayModeTest.Restore(); }
                }
                if (path == null) return new WebLookThumbPreviewDto { message = warning ?? "Could not render the thumbnail." };
                string token = Guid.NewGuid().ToString("N");
                lock (_webThumbsLock)
                {
                    if (_lookPreviewTokens.TryGetValue(id, out var previous) && _webThumbs.TryGetValue(previous, out var old))
                    {
                        _webThumbs.Remove(previous);
                        try { CleanupTempThumb(old.path); } catch { }
                    }
                    _webThumbs[token] = new WebThumbEntry { outfit = id, path = path };
                    _lookPreviewTokens[id] = token;
                }
                return new WebLookThumbPreviewDto { ok = 1, token = token, used = used, message = warning ?? "" };
            }
            catch (Exception ex) { return new WebLookThumbPreviewDto { message = ex.Message }; }
        }

        /// <summary>Keeps a rendered preview as this Look's image.</summary>
        internal static WebResultDto WebLookThumbFreeze(string id, string token)
        {
            byte[] bytes = WebThumbBytes(token);
            if (bytes == null) return new WebResultDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.unknown.or.expired.thumbnail") };
            return WebLookThumbUpload(id, bytes);
        }
    }
}
