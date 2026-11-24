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
            // Renderer materials plus every material the avatar references
            // (MA Material Setter/Swap, animations, controllers): NDMF applies
            // those during the build, and the SDK validates the result.
            var materials = new HashSet<Material>();
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                if (!IsEditorOnly(renderer.transform, root.transform))
                    foreach (var material in renderer.sharedMaterials)
                        if (material != null) materials.Add(material);
            foreach (var dependency in EditorUtility.CollectDependencies(new UnityEngine.Object[] { root }))
                if (dependency is Material material) materials.Add(material);
            var importers = new Dictionary<string, TextureImporter>(StringComparer.Ordinal);
            foreach (var material in materials)
                foreach (int id in material.GetTexturePropertyNameIDs() ?? Array.Empty<int>())
                {
                    var texture = material.GetTexture(id);
                    if (texture == null) continue;
                    string path = AssetDatabase.GetAssetPath(texture);
                    if (string.IsNullOrEmpty(path) || importers.ContainsKey(path)) continue;
                    if (AssetImporter.GetAtPath(path) is TextureImporter importer) importers[path] = importer;
                }
            foreach (var pair in importers)
            {
                var importer = pair.Value;
                bool changed = false;
                if (importer.mipmapEnabled && !importer.streamingMipmaps)
                {
                    importer.streamingMipmaps = true;
                    importer.streamingMipmapsPriority = 0;
                    changed = true;
                }
                if (importer.maxTextureSize > VRCSdkControlPanel.MAX_SDK_TEXTURE_SIZE)
                {
                    importer.maxTextureSize = VRCSdkControlPanel.MAX_SDK_TEXTURE_SIZE;
                    changed = true;
                }
                if (!changed) continue;
                EditorUtility.SetDirty(importer);
                importer.SaveAndReimport();
                fixedPaths.Add(pair.Key);
            }
            if (fixedPaths.Count > 0)
            {
                Debug.Log("[OutfitBatchUploader] Auto-fixed texture import settings for upload: " + string.Join(", ", fixedPaths));
                OutfitToggleGenerator.WardrobeLog.Write("upload", "Auto-fixed textures: " + string.Join(", ", fixedPaths));
            }
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
