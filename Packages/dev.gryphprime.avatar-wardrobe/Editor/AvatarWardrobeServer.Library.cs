using System;
using System.IO;
using UnityEditor;
using UnityEngine;
namespace OutfitToggleGenerator
{
    internal static partial class AvatarWardrobeServer
    {
        private static string importLease;
        private static double importLeaseUntil;
        private static IDisposable importTargetLock;
        internal static ResultDto BeginLibraryImport()
        {
            if (importLease != null || UploadTargetLocked || ShiroTools.OutfitBatchUploader.BatchActiveNow ||
                EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
                return new ResultDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.finish.the.active.unity.operation.before.importing") };
            EditorApplication.LockReloadAssemblies();
            try { AssetDatabase.DisallowAutoRefresh(); }
            catch { EditorApplication.UnlockReloadAssemblies(); throw; }
            importTargetLock = LockUploadTarget();
            importLease = Guid.NewGuid().ToString("N");
            importLeaseUntil = EditorApplication.timeSinceStartup + 300;
            EditorApplication.update += CheckImportLease;
            return new ResultDto { ok = 1, id = importLease };
        }
        internal static ResultDto RenewLibraryImport(string token)
        {
            if (string.IsNullOrEmpty(importLease) || importLease != token)
                return new ResultDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.the.import.lease.expired.stop.copying.and.review.the.project") };
            importLeaseUntil = EditorApplication.timeSinceStartup + 300;
            return new ResultDto { ok = 1, id = importLease };
        }
        internal static ResultDto EndLibraryImport(string token)
        {
            if (string.IsNullOrEmpty(importLease) || importLease != token)
                return new ResultDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.the.import.lease.expired.review.the.imported.files.before.retrying") };
            importLease = null;
            EditorApplication.update -= CheckImportLease;
            try { AssetDatabase.AllowAutoRefresh(); }
            finally
            {
                EditorApplication.UnlockReloadAssemblies();
                importTargetLock?.Dispose(); importTargetLock = null;
            }
            EditorApplication.delayCall += AssetDatabase.Refresh;
            return new ResultDto { ok = 1, message = global::OutfitToggleGenerator.WardrobeStrings.T("server.unity.import.queued") };
        }
        private static void CheckImportLease()
        {
            if (importLease != null && EditorApplication.timeSinceStartup > importLeaseUntil)
            {
                EndLibraryImport(importLease);
                Debug.LogWarning("Wardrobe import connection timed out. Unity refresh resumed; review imported files before retrying.");
            }
        }
    }
}
