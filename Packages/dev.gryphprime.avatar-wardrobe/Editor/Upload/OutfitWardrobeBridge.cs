// Wardrobe bridge (v1): headless single-variant upload driven by the Avatar
// Wardrobe web UI. Same partial class, so it reuses the uploader's private
// state directly — no reflection (same Assembly-CSharp-Editor assembly).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using VRC.Core;
using VRC.SDK3A.Editor;
using VRC.SDKBase.Editor;

namespace ShiroTools
{
    public partial class OutfitBatchUploader
    {
        public class WardrobeBridgeResult
        {
            public bool ok;
            public string message = "";
            public string blueprintId = "";
            public bool isNew;
        }

        // True while a Wardrobe web upload drives this window. The two guarded
        // edits marked "Wardrobe bridge" below answer modal dialogs automatically.
        internal static bool WardrobeHeadless;
        private GameObject _wardrobeBuildTarget;

        private static OutfitBatchUploader WardrobeUploadWindow()
        {
            // GetWindow can return a hidden CreateInstance browser drawer. Its
            // avatar follows browser reads, so it must never own an upload.
            var win = Resources.FindObjectsOfTypeAll<OutfitBatchUploader>()
                .FirstOrDefault(candidate => !candidate.IsEmbeddedDrawer);
            if (win == null) win = ScriptableObject.CreateInstance<OutfitBatchUploader>();
            win.Show();
            return win;
        }

        private void ValidateWardrobeBuildTarget()
        {
            if (!WardrobeHeadless) return;
            if (_wardrobeBuildTarget == null || _avatarRoot != _wardrobeBuildTarget || IsEmbeddedDrawer)
                throw new InvalidOperationException("The preset upload target changed before building.");
            OutfitToggleGenerator.WardrobeLog.Write("upload", "Building avatar=" + _avatarRoot.name +
                " scene=" + _avatarRoot.scene.path);
        }

        internal static bool TryGetWardrobeBuilder(out IVRCSdkAvatarBuilderApi builder)
        {
            // SDK's static window reference can be lost after a reload even
            // while the docked panel still exists. Rebind it without opening
            // or focusing a window, and avoid SDK error spam on status polls.
            if (VRCSdkControlPanel.window == null)
                VRCSdkControlPanel.window = Resources.FindObjectsOfTypeAll<VRCSdkControlPanel>().FirstOrDefault();
            if (VRCSdkControlPanel.window == null)
            {
                builder = null;
                return false;
            }
            return VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out builder);
        }

        internal static string WardrobeUploadReadinessError()
        {
            if (!APIUser.IsLoggedIn)
                return "Not logged in. Open the VRChat SDK Control Panel, log in, then retry the upload in Avatar Wardrobe.";
            if (!TryGetWardrobeBuilder(out _))
                return "VRC SDK builder not available. Open the VRChat SDK Control Panel, then retry the upload in Avatar Wardrobe.";
            return null;
        }

        /// <summary>Uploads one preset child headless (Windows only, v1).
        /// Re-uploads when the preset already has a valid Blueprint ID,
        /// otherwise runs the Express first-time setup quietly.</summary>
        public static async Task<WardrobeBridgeResult> UploadOutfitHeadless(string avatarRootName, string outfitName)
        {
            var result = new WardrobeBridgeResult();
            try
            {
                if (string.IsNullOrEmpty(avatarRootName) || string.IsNullOrEmpty(outfitName))
                    return Fail(result, "Upload needs an avatar root name and an preset name.");

                var readinessError = WardrobeUploadReadinessError();
                if (readinessError != null) return Fail(result, readinessError);

                // Visible on purpose: SessionState resume after a domain reload
                // needs a live window, and the status line shows progress.
                var win = WardrobeUploadWindow();

                var root = GameObject.Find(avatarRootName);
                if (root == null)
                    return Fail(result, $"Avatar root '{avatarRootName}' not found in the open scene.");
                win._avatarRoot = root;
                win._wardrobeBuildTarget = root;
                win.AutoDetectSkin();
                win.RebuildOutfitList();

                var entry = win._outfits.FirstOrDefault(o => o != null && o.Go != null && o.Name == outfitName);
                if (entry == null)
                    return Fail(result, $"Preset '{outfitName}' not found under '{avatarRootName}/Outfits'.");

                int failedBefore = LoadQueue(SESSION_FAILED).Count;
                bool isNew = !IsValidBlueprintId(entry.BlueprintId);
                result.isNew = isNew;

                WardrobeHeadless = true;
                try
                {
                    if (!isNew)
                    {
                        await win.StartBatchAsync(new List<OutfitEntry> { entry });
                        if (!SessionState.GetBool(SESSION_BATCH_ACTIVE, false))
                            return Fail(result, string.IsNullOrEmpty(win._statusMessage)
                                ? "Batch did not start." : win._statusMessage);
                        if (!await WaitForBatchAsync())
                            return Fail(result, "Timed out waiting for the upload to finish.");
                    }
                    else
                    {
                        await win.ExpressSetupAsync(entry, null, true);
                        if (!await WaitForExpressAsync())
                            return Fail(result, "Timed out waiting for the first-time upload to finish.");
                    }
                }
                finally
                {
                    WardrobeHeadless = false;
                    win._wardrobeBuildTarget = null;
                }

                var after = OutfitProjectData.GetOutfit(avatarRootName, outfitName);
                string afterId = after != null ? after.blueprintId ?? "" : "";
                bool failedNow = LoadQueue(SESSION_FAILED).Skip(failedBefore)
                    .Any(f => f != null && f.outfit == outfitName);

                result.blueprintId = afterId;
                if (!IsValidBlueprintId(afterId))
                    return Fail(result, string.IsNullOrEmpty(win._statusMessage)
                        ? "Upload finished without a Blueprint ID." : win._statusMessage);
                if (failedNow)
                    return Fail(result, string.IsNullOrEmpty(win._statusMessage)
                        ? $"Upload failed for '{outfitName}'." : win._statusMessage);

                result.ok = true;
                result.message = isNew
                    ? $"Created new avatar for '{outfitName}' → {afterId}"
                    : $"Uploaded '{outfitName}' → {afterId}";
                return result;
            }
            catch (Exception ex)
            {
                WardrobeHeadless = false;
                Debug.LogError("[OutfitBatchUploader] Wardrobe headless upload threw: " + ex);
                return Fail(result, ex.Message);
            }
        }

        // StartBatchAsync arms a SessionState queue and returns while the upload
        // still runs: poll for the drain (single Windows preset, no platform
        // switch, so no domain reload is expected).
        private static async Task<bool> WaitForBatchAsync()
        {
            var start = DateTime.UtcNow;
            while (SessionState.GetBool(SESSION_BATCH_ACTIVE, false))
            {
                if ((DateTime.UtcNow - start).TotalMinutes >= 30) return false;
                await Task.Delay(2000);
            }
            return true;
        }

        private static async Task<bool> WaitForExpressAsync()
        {
            var start = DateTime.UtcNow;
            while (SessionState.GetBool(SESSION_EXPRESS_PENDING, false))
            {
                if ((DateTime.UtcNow - start).TotalMinutes >= 30) return false;
                await Task.Delay(2000);
            }
            return true;
        }

        private static WardrobeBridgeResult Fail(WardrobeBridgeResult result, string message)
        {
            result.ok = false;
            result.message = message;
            Debug.LogWarning("[OutfitBatchUploader] Wardrobe headless upload: " + message);
            return result;
        }
    }
}
