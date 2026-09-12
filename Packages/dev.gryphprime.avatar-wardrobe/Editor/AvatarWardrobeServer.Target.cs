using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
namespace OutfitToggleGenerator
{
    internal static partial class AvatarWardrobeServer
    {
        [Serializable] private sealed class TargetChoice
        {
            public int id;
            public string name, scene, identity;
        }
        internal static VRCAvatarDescriptor[] SceneTargets() => Resources.FindObjectsOfTypeAll<VRCAvatarDescriptor>()
            .Where(a => a != null && !EditorUtility.IsPersistent(a) && a.gameObject.scene.IsValid() &&
                a.gameObject.scene.isLoaded && !UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(a.gameObject.scene) &&
                !IsStagingAvatar(a)).ToArray();
        private static ResultDto SelectTarget(string id)
        {
            if (UploadTargetLocked || ShiroTools.OutfitBatchUploader.BatchActiveNow || EditorApplication.isPlayingOrWillChangePlaymode)
                return new ResultDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.finish.the.active.test.or.upload.before.choosing.an.avatar") };
            var chosen = SceneTargets().FirstOrDefault(a => a.GetInstanceID().ToString() == id);
            if (chosen == null) return new ResultDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.this.avatar.is.no.longer.available.refresh.and.choose.a") };
            SceneAvatar = chosen;
            AvatarWardrobeWindow.Instance?.SetTarget(chosen);
            return new ResultDto { ok = 1 };
        }
    }
}
