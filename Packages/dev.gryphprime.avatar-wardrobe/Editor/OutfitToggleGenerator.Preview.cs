using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

// Unity-only rendering and final menu-icon import. Callers own returned textures.
namespace OutfitToggleGenerator
{
    internal static partial class OutfitToggleGenerator
    {
        // All generated menu paths use the same renderer and persistent PNG cache.
        private static void EnsureGeneratedMenuIcons(GameObject avatarRoot, Transform host)
        {
            var requests = new List<KeyValuePair<ModularAvatarMenuItem, IEnumerable<GameObject>>>();
            var paths = new Dictionary<ModularAvatarMenuItem, string>();
            foreach (var item in host.GetComponentsInChildren<ModularAvatarMenuItem>(true))
            {
                if (item.Control == null) continue;
                var marker = item.GetComponent<OutfitToggleGeneratedMenu>();
                if (marker != null && (marker.generatedKind == "menu-group" || marker.generatedKind == "menu-groups"))
                {
                    if (item.Control.icon != null)
                    {
                        Undo.RecordObject(item, "Clear menu group parent icon");
                        item.Control.icon = null;
                        EditorUtility.SetDirty(item);
                    }
                    continue;
                }
                if (item.Control.icon != null)
                {
                    var oldPath = AssetDatabase.GetAssetPath(item.Control.icon);
                    if (!oldPath.StartsWith(IconFolder + "/Generated_", StringComparison.Ordinal) ||
                        oldPath.StartsWith(IconFolder + "/Generated_Neutral_", StringComparison.Ordinal)) continue;
                }
                var targets = item.GetComponentsInChildren<ModularAvatarObjectToggle>(true)
                    .SelectMany(toggle => toggle.Objects.Select(obj => obj.Object?.Get(toggle)))
                    .Where(target => target != null && target.GetComponentsInChildren<Renderer>(true).Length > 0)
                    .Distinct().ToList();
                if (targets.Count == 0)
                {
                    // Non-rendering parts (e.g. PhysBone) use the nearest outfit image.
                    var parent = host.parent;
                    while (parent != null && parent.GetComponentsInChildren<Renderer>(true).Length == 0) parent = parent.parent;
                    if (parent != null) targets.Add(parent.gameObject);
                }
                if (targets.Count == 0) continue;
                var key = avatarRoot.name + "|" + AnimationUtility.CalculateTransformPath(item.transform, avatarRoot.transform)
                    + "|" + string.Join("|", targets.Select(target => AnimationUtility.CalculateTransformPath(target.transform, avatarRoot.transform)));
                var path = IconFolder + "/Generated_Neutral_" + Hash128.Compute(key) + ".png";
                var cached = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                Undo.RecordObject(item, "Set generated menu icon");
                if (cached != null) { item.Control.icon = cached; EditorUtility.SetDirty(item); continue; }
                requests.Add(new KeyValuePair<ModularAvatarMenuItem, IEnumerable<GameObject>>(item, targets));
                paths[item] = path;
            }
            var rendered = RenderIconGroups(avatarRoot, requests);
            foreach (var entry in rendered)
            {
                entry.Key.Control.icon = SaveIcon(entry.Value, entry.Key.gameObject, paths[entry.Key]);
                EditorUtility.SetDirty(entry.Key);
            }
        }

        private static Texture2D SaveIcon(Texture2D icon, GameObject target, string assetPath = null)
        {
            if (icon == null) return null;

            try
            {
                Directory.CreateDirectory(IconFolder);
                if (string.IsNullOrEmpty(assetPath))
                    assetPath = AssetDatabase.GenerateUniqueAssetPath($"{IconFolder}/{SafeFileName(target.name)}.png");
                WardrobeAtomicFile.WriteBytes(assetPath, icon.EncodeToPNG());
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);

                var importer = (TextureImporter)AssetImporter.GetAtPath(assetPath);
                if (importer != null && (!importer.alphaIsTransparency || importer.mipmapEnabled ||
                    importer.wrapMode != TextureWrapMode.Clamp || importer.maxTextureSize != IconSize))
                {
                    importer.alphaIsTransparency = true;
                    importer.mipmapEnabled = false;
                    importer.wrapMode = TextureWrapMode.Clamp;
                    importer.maxTextureSize = IconSize;
                    importer.SaveAndReimport();
                }
                return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Could not create an icon for '{target.name}': {exception.Message}", target);
                return null;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(icon);
            }
        }

        private static Dictionary<GameObject, Texture2D> RenderIcons(GameObject avatarRoot, IEnumerable<GameObject> targets)
        {
            return RenderIconGroups(avatarRoot, targets.Distinct()
                .Select(target => new KeyValuePair<GameObject, IEnumerable<GameObject>>(target, new[] { target })));
        }

        // Full-prefab render for the wardrobe web UI: same camera, lights,
        // framing as toggle icons, with 512px output, but the whole prefab on
        // transparency instead of an isolated part. Main thread only.
        // Caller owns the returned texture. Null when nothing is renderable.
        internal static Texture2D RenderOutfitThumb(GameObject prefab)
        {
            if (prefab == null) return null;
            GameObject instance = null;
            var preview = new PreviewRenderUtility();
            var lighting = new WardrobePreviewLighting();
            try
            {
                instance = (GameObject)UnityEngine.Object.Instantiate(prefab);
                instance.name = prefab.name;
                preview.AddSingleGO(instance);
                instance.SetActive(true);
                // Respect creator-selected children. Enabling every variant at once
                // produces overlapping geometry and z-fighting in the thumbnail.
                var renderers = instance.GetComponentsInChildren<Renderer>(false).Where(r => r.enabled).ToList();
                if (renderers.Count == 0) return null;
                var bounds = ShowOnly(renderers, renderers.Select(renderer => renderer.transform));
                if (bounds.size == Vector3.zero) return null;
                var icon = CaptureIcon(preview, instance.transform, bounds, 512);
                if (ContentPixelCount(icon) < 4)
                {
                    UnityEngine.Object.DestroyImmediate(icon);
                    return null;
                }
                return icon;
            }
            finally
            {
                lighting.Dispose();
                preview.Cleanup();
                if (instance != null) UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        // Shared by catalog previews and generated menu icons, regardless of
        // the user's scene skybox, ambient exposure, or fog settings.
        private sealed class WardrobePreviewLighting : IDisposable
        {
            private readonly UnityEngine.Rendering.AmbientMode mode = RenderSettings.ambientMode;
            private readonly Color light = RenderSettings.ambientLight;
            private readonly float intensity = RenderSettings.ambientIntensity;
            private readonly bool fog = RenderSettings.fog;
            internal WardrobePreviewLighting()
            {
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.55f, 0.55f, 0.55f);
                RenderSettings.ambientIntensity = 1f;
                RenderSettings.fog = false;
            }
            public void Dispose()
            {
                RenderSettings.ambientMode = mode;
                RenderSettings.ambientLight = light;
                RenderSettings.ambientIntensity = intensity;
                RenderSettings.fog = fog;
            }
        }

        private static Dictionary<TKey, Texture2D> RenderIconGroups<TKey>(
            GameObject avatarRoot,
            IEnumerable<KeyValuePair<TKey, IEnumerable<GameObject>>> groups)
        {
            var icons = new Dictionary<TKey, Texture2D>();
            var sourceGroups = groups.Select(group => new KeyValuePair<TKey, List<GameObject>>(
                    group.Key,
                    group.Value.Where(target => target != null).Distinct().ToList()))
                .Where(group => group.Value.Count > 0)
                .ToList();
            if (sourceGroups.Count == 0) return icons;

            var preview = new PreviewRenderUtility();
            GameObject avatarCopy = null;
            var lighting = new WardrobePreviewLighting();

            try
            {
                avatarCopy = UnityEngine.Object.Instantiate(avatarRoot);
                avatarCopy.name = avatarRoot.name;
                avatarCopy.SetActive(true);
                preview.AddSingleGO(avatarCopy);
                var renderers = avatarCopy.GetComponentsInChildren<Renderer>(true);

                foreach (var group in sourceGroups)
                {
                    try
                    {
                        var targetCopies = group.Value.Select(target =>
                        {
                            var path = AnimationUtility.CalculateTransformPath(target.transform, avatarRoot.transform);
                            return string.IsNullOrEmpty(path) ? avatarCopy.transform : avatarCopy.transform.Find(path);
                        }).Where(target => target != null).ToList();
                        if (targetCopies.Count == 0) continue;

                        foreach (var targetCopy in targetCopies) SetAncestorsActive(targetCopy, avatarCopy.transform);
                        var bounds = ShowOnly(renderers, targetCopies);
                        if (bounds.size == Vector3.zero) continue;
                        icons[group.Key] = CaptureIcon(preview, avatarCopy.transform, bounds);
                    }
                    catch (Exception exception)
                    {
                        Debug.LogWarning($"Could not render an icon for '{group.Value[0].name}': {exception.Message}", group.Value[0]);
                    }
                }
            }
            finally
            {
                lighting.Dispose();
                preview.Cleanup();
                if (avatarCopy != null) UnityEngine.Object.DestroyImmediate(avatarCopy);
            }

            return icons;
        }

        private static void ConfigurePreview(PreviewRenderUtility preview, Transform avatarRoot, float yaw = 180f)
        {
            var camera = preview.camera;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.clear;
            camera.fieldOfView = 30f;
            camera.nearClipPlane = 0.01f;
            camera.farClipPlane = 1000f;
            camera.transform.rotation = avatarRoot.rotation * Quaternion.Euler(10f, yaw, 0f);
            preview.lights[0].intensity = 1.2f;
            preview.lights[0].transform.rotation = Quaternion.Euler(30f, 30f, 0f);
            preview.lights[1].intensity = 1.0f;
        }

        private static Texture2D Capture(PreviewRenderUtility preview, Bounds bounds, int size = IconSize)
        {
            var camera = preview.camera;
            camera.aspect = 1f;
            camera.transform.position = bounds.center - camera.transform.forward * (CameraDistance(camera, bounds) * 1.05f);
            var renderTexture = RenderTexture.GetTemporary(size, size, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            try
            {
                camera.targetTexture = renderTexture;
                camera.Render();
                RenderTexture.active = renderTexture;
                var icon = new Texture2D(size, size, TextureFormat.RGBA32, false);
                try
                {
                    icon.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                    icon.Apply();
                    // The sRGB render target already performs the display conversion.
                    return icon;
                }
                catch { UnityEngine.Object.DestroyImmediate(icon); throw; }
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(renderTexture);
            }
        }

        private static Texture2D CaptureIcon(PreviewRenderUtility preview, Transform avatarRoot, Bounds bounds, int size = IconSize)
        {
            ConfigurePreview(preview, avatarRoot);
            var best = Capture(preview, bounds, size);
            var bestPixels = ContentPixelCount(best);
            if (bestPixels < size * size / 50)
            {
                foreach (var yaw in new[] { 90f, -90f })
                {
                    ConfigurePreview(preview, avatarRoot, yaw);
                    var candidate = Capture(preview, bounds, size);
                    var candidatePixels = ContentPixelCount(candidate);
                    if (candidatePixels > bestPixels)
                    {
                        UnityEngine.Object.DestroyImmediate(best);
                        best = candidate;
                        bestPixels = candidatePixels;
                    }
                    else
                    {
                        UnityEngine.Object.DestroyImmediate(candidate);
                    }
                }
            }

            return FitIconToContent(best);
        }

        private static Texture2D FitIconToContent(Texture2D icon)
        {
            var size = icon.width;
            var content = ContentBounds(icon.GetPixels32(), size, size);
            if (content.width == 0 || content.height == 0) return icon;

            var scale = Mathf.Min(size * 0.875f / content.width, size * 0.875f / content.height);
            if (scale <= 1f) return icon;

            var width = Mathf.Max(1, Mathf.RoundToInt(content.width * scale));
            var height = Mathf.Max(1, Mathf.RoundToInt(content.height * scale));
            var fitted = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var source = icon.GetPixels();
            var pixels = new Color[size * size];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var sourceX = Mathf.Clamp(content.x + (x + 0.5f) * content.width / width - 0.5f, content.x, content.xMax - 1);
                var sourceY = Mathf.Clamp(content.y + (y + 0.5f) * content.height / height - 0.5f, content.y, content.yMax - 1);
                var x0 = Mathf.Clamp(Mathf.FloorToInt(sourceX), content.x, content.xMax - 1);
                var y0 = Mathf.Clamp(Mathf.FloorToInt(sourceY), content.y, content.yMax - 1);
                var x1 = Mathf.Min(x0 + 1, content.xMax - 1);
                var y1 = Mathf.Min(y0 + 1, content.yMax - 1);
                var xLerp = sourceX - x0;
                var yLerp = sourceY - y0;
                var color = Color.Lerp(Color.Lerp(source[y0 * size + x0], source[y0 * size + x1], xLerp),
                    Color.Lerp(source[y1 * size + x0], source[y1 * size + x1], xLerp), yLerp);
                pixels[((size - height) / 2 + y) * size + (size - width) / 2 + x] = color;
            }
            fitted.SetPixels(pixels);
            fitted.Apply();
            UnityEngine.Object.DestroyImmediate(icon);
            return fitted;
        }

        private static int ContentPixelCount(Texture2D icon)
        {
            return icon.GetPixels32().Count(pixel => pixel.a > 8);
        }

        private static RectInt ContentBounds(Color32[] pixels, int width, int height)
        {
            var minX = width;
            var minY = height;
            var maxX = -1;
            var maxY = -1;
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                if (pixels[y * width + x].a <= 8) continue;
                minX = Mathf.Min(minX, x);
                minY = Mathf.Min(minY, y);
                maxX = Mathf.Max(maxX, x);
                maxY = Mathf.Max(maxY, y);
            }

            return maxX < minX ? new RectInt() : new RectInt(minX, minY, maxX - minX + 1, maxY - minY + 1);
        }

        private static void SetAncestorsActive(Transform target, Transform root)
        {
            for (var current = target; current != null; current = current.parent)
            {
                current.gameObject.SetActive(true);
                if (current == root) return;
            }
        }

        private static Bounds ShowOnly(IEnumerable<Renderer> renderers, IEnumerable<Transform> targets)
        {
            var targetList = targets.ToList();
            var bounds = new Bounds();
            var hasBounds = false;
            foreach (var renderer in renderers)
            {
                renderer.enabled = targetList.Any(target => renderer.transform == target || renderer.transform.IsChildOf(target));
                if (!renderer.enabled) continue;
                var rendererBounds = GeometryBounds(renderer);
                if (!hasBounds)
                {
                    bounds = rendererBounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(rendererBounds);
                }
            }

            return bounds;
        }

        private static float CameraDistance(Camera camera, Bounds bounds)
        {
            var inverseRotation = Quaternion.Inverse(camera.transform.rotation);
            var maxX = 0f;
            var maxY = 0f;
            var maxZ = 0f;
            foreach (var corner in BoundsCorners(bounds))
            {
                var local = inverseRotation * (corner - bounds.center);
                maxX = Mathf.Max(maxX, Mathf.Abs(local.x));
                maxY = Mathf.Max(maxY, Mathf.Abs(local.y));
                maxZ = Mathf.Max(maxZ, Mathf.Abs(local.z));
            }

            var halfVerticalFov = camera.fieldOfView * Mathf.Deg2Rad * 0.5f;
            var halfHorizontalFov = Mathf.Atan(Mathf.Tan(halfVerticalFov) * camera.aspect);
            return Mathf.Max(maxY / Mathf.Tan(halfVerticalFov), maxX / Mathf.Tan(halfHorizontalFov)) + maxZ;
        }

        private static Bounds GeometryBounds(Renderer renderer)
        {
            Mesh mesh = null;
            if (renderer is SkinnedMeshRenderer skinned) mesh = skinned.sharedMesh;
            else if (renderer is MeshRenderer) mesh = renderer.GetComponent<MeshFilter>()?.sharedMesh;
            return mesh == null ? renderer.bounds : TransformBounds(mesh.bounds, renderer.localToWorldMatrix);
        }

        private static Bounds TransformBounds(Bounds bounds, Matrix4x4 matrix)
        {
            var corners = BoundsCorners(bounds).Select(matrix.MultiplyPoint3x4).ToArray();
            var transformed = new Bounds(corners[0], Vector3.zero);
            foreach (var corner in corners.Skip(1)) transformed.Encapsulate(corner);
            return transformed;
        }

        private static IEnumerable<Vector3> BoundsCorners(Bounds bounds)
        {
            var min = bounds.min;
            var max = bounds.max;
            yield return new Vector3(min.x, min.y, min.z);
            yield return new Vector3(min.x, min.y, max.z);
            yield return new Vector3(min.x, max.y, min.z);
            yield return new Vector3(min.x, max.y, max.z);
            yield return new Vector3(max.x, min.y, min.z);
            yield return new Vector3(max.x, min.y, max.z);
            yield return new Vector3(max.x, max.y, min.z);
            yield return new Vector3(max.x, max.y, max.z);
        }

    }
}
