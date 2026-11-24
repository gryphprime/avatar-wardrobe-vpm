// ============================================================
//  Pre-build auto-fix for SDK validation errors that have a safe,
//  non-destructive fix (the same change the SDK's Auto Fix button
//  makes), so batch and headless uploads don't stop on them:
//    - mipmapped textures without Streaming Mip Maps
//    - textures above the SDK's maximum size
//  Also records the SDK's validation lines during a build, because
//  BuildAndUpload only reports "Avatar validation failed".
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ShiroTools
{
    public partial class OutfitBatchUploader
    {
        private const string SdkValidationHeader = "Encountered the following validation issues during build:";
        private List<string> _sdkValidationErrors = new List<string>();
        private bool _capturingSdkValidation;

        internal static List<string> AutoFixUploadTextures(GameObject root)
        {
            var fixedPaths = new List<string>();
            if (root == null) return fixedPaths;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            // Asset paths whose texture dependencies the SDK may validate:
            // renderer materials, plus assets referenced by the avatar's
            // components (MA Material Setter/Swap, merged controllers and their
            // clips), which NDMF applies during the build. Paths only:
            // AssetDatabase.GetDependencies reads the dependency cache and
            // doesn't load meshes, clips or textures.
            var roots = new HashSet<string>(StringComparer.Ordinal);
            void AddAsset(UnityEngine.Object asset)
            {
                if (asset == null) return;
                string path = AssetDatabase.GetAssetPath(asset);
                if (!string.IsNullOrEmpty(path)) roots.Add(path);
            }
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                if (!IsEditorOnly(renderer.transform, root.transform))
                    foreach (var material in renderer.sharedMaterials) AddAsset(material);
            foreach (var animator in root.GetComponentsInChildren<Animator>(true)) AddAsset(animator.runtimeAnimatorController);
            foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour == null || IsEditorOnly(behaviour.transform, root.transform)) continue;
                using (var serialized = new SerializedObject(behaviour))
                {
                    var property = serialized.GetIterator();
                    while (property.Next(true))
                    {
                        if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                        var value = property.objectReferenceValue;
                        if (value != null && !(value is Component) && !(value is GameObject) && !(value is MonoScript))
                            AddAsset(value);
                    }
                }
            }
            var candidates = AssetDatabase.GetDependencies(roots.ToArray(), true);
            var changed = new List<TextureImporter>();
            foreach (var path in candidates)
            {
                if (!(AssetImporter.GetAtPath(path) is TextureImporter importer)) continue;
                bool dirty = false;
                if (importer.mipmapEnabled && !importer.streamingMipmaps)
                {
                    importer.streamingMipmaps = true;
                    importer.streamingMipmapsPriority = 0;
                    dirty = true;
                }
                if (importer.maxTextureSize > VRCSdkControlPanel.MAX_SDK_TEXTURE_SIZE)
                {
                    importer.maxTextureSize = VRCSdkControlPanel.MAX_SDK_TEXTURE_SIZE;
                    dirty = true;
                }
                if (!dirty) continue;
                changed.Add(importer);
                fixedPaths.Add(path);
            }
            if (changed.Count > 0)
            {
                // One import pass for all changed textures instead of one each.
                AssetDatabase.StartAssetEditing();
                try
                {
                    foreach (var importer in changed)
                    {
                        EditorUtility.SetDirty(importer);
                        importer.SaveAndReimport();
                    }
                }
                finally { AssetDatabase.StopAssetEditing(); }
                Debug.Log("[OutfitBatchUploader] Auto-fixed texture import settings for upload: " + string.Join(", ", fixedPaths));
            }
            OutfitToggleGenerator.WardrobeLog.Write("upload", "Texture check: " + candidates.Length + " dependencies, "
                + fixedPaths.Count + " fixed in " + timer.ElapsedMilliseconds + " ms"
                + (fixedPaths.Count > 0 ? " (" + string.Join(", ", fixedPaths) + ")" : ""));
            return fixedPaths;
        }

        private static bool IsEditorOnly(Transform transform, Transform root)
        {
            for (var t = transform; t != null; t = t.parent)
            {
                if (t.CompareTag("EditorOnly")) return true;
                if (t == root) break;
            }
            return false;
        }

        private void BeginSdkValidationCapture()
        {
            _sdkValidationErrors = new List<string>();
            _capturingSdkValidation = false;
            Application.logMessageReceived += CaptureSdkValidation;
        }

        private void EndSdkValidationCapture()
        {
            Application.logMessageReceived -= CaptureSdkValidation;
            _capturingSdkValidation = false;
        }

        private void CaptureSdkValidation(string message, string stackTrace, LogType type)
        {
            if (message == SdkValidationHeader) { _capturingSdkValidation = true; return; }
            // Only the SDK's own validation lines; shader compile errors and
            // other noise logged during the build are left out.
            if (_capturingSdkValidation && type == LogType.Error && !string.IsNullOrEmpty(message) &&
                (stackTrace ?? "").Contains("VRCSdkControlPanelAvatarBuilder"))
                _sdkValidationErrors.Add(message.Trim());
        }
    }
}
