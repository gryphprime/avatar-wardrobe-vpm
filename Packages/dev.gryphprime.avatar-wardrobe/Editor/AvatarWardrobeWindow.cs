using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
namespace OutfitToggleGenerator
{
    // Launcher: pick the install target, then browse + install from the
    // external browser UI served below (Wardrobe and Upload views). The
    // grid/detail used to live here and froze the editor; it now lives at
    // the served URL.
    internal sealed class AvatarWardrobeWindow : EditorWindow
    {
        [SerializeField] private VRCAvatarDescriptor sceneAvatar;
        [SerializeField] private string avatarGuid;
        internal static AvatarWardrobeWindow Instance { get; private set; }
        internal VRCAvatarDescriptor SceneAvatar
        {
            get { return sceneAvatar; }
        }
        internal string AvatarGuid
        {
            get { return avatarGuid ?? string.Empty; }
        }
        [MenuItem("Tools/Avatar Wardrobe")]
        private static void Open()
        {
            WardrobeStrings.EnsureInitialized();
            var window = GetWindow<AvatarWardrobeWindow>();
            window.titleContent = new GUIContent(WardrobeStrings.T("app.title"));
            window.minSize = new Vector2(320f, 300f);
            window.Show();
        }
        private void OnEnable()
        {
            Instance = this;
            FollowSelection();
            AvatarWardrobeServer.SceneAvatar = sceneAvatar;
        }
        private void OnDisable()
        {
            if (Instance == this) Instance = null;
        }
        private void OnGUI()
        {
            GUILayout.Label(WardrobeStrings.T("app.title"), EditorStyles.boldLabel);
            GUILayout.Space(4);
            FollowSelection();
            if (AvatarWardrobeServer.UploadTargetLocked) sceneAvatar = AvatarWardrobeServer.SceneAvatar;
            EditorGUI.BeginChangeCheck();
            using (new EditorGUI.DisabledScope(AvatarWardrobeServer.UploadTargetLocked))
            sceneAvatar = (VRCAvatarDescriptor)EditorGUILayout.ObjectField(
                new GUIContent(WardrobeStrings.T("win.target"), WardrobeStrings.T("win.target.tip")),
                sceneAvatar,
                typeof(VRCAvatarDescriptor),
                true);
            if (EditorGUI.EndChangeCheck())
            {
                if (AvatarWardrobeServer.IsStagingAvatar(sceneAvatar)) sceneAvatar = AvatarWardrobeServer.SceneAvatar;
                UseSceneAvatarRecord();
            }
            AvatarWardrobeServer.SceneAvatar = sceneAvatar;
            if (sceneAvatar != null && EditorUtility.IsPersistent(sceneAvatar.gameObject))
                EditorGUILayout.HelpBox(WardrobeStrings.T("msg.openavatar"), MessageType.Info);
            GUILayout.Space(10);
            GUILayout.Label(WardrobeStrings.T("win.browser"), EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(WardrobeStrings.T("win.open")))
                {
                    if (!AvatarWardrobeServer.Running && !AvatarWardrobeServer.Start())
                        ShowNotification(new GUIContent(WardrobeStrings.T("win.servefail")));
                    else
                        Application.OpenURL(AvatarWardrobeServer.Url);
                }
                using (new EditorGUI.DisabledScope(!AvatarWardrobeServer.Running))
                {
                    if (GUILayout.Button(WardrobeStrings.T("win.stop"))) AvatarWardrobeServer.Stop();
                }
            }
            if (AvatarWardrobeServer.Running)
            {
                EditorGUILayout.SelectableLabel(
                    AvatarWardrobeServer.Url, EditorStyles.miniLabel, GUILayout.Height(16f));
                EditorGUILayout.HelpBox(
                    WardrobeStrings.T("win.hint"),
                    MessageType.Info);
            }
            else if (!string.IsNullOrEmpty(AvatarWardrobeServer.LastError))
            {
                EditorGUILayout.HelpBox(
                    WardrobeStrings.T("win.serveerr", AvatarWardrobeServer.LastError),
                    MessageType.Warning);
            }
        }
        internal void SetTarget(VRCAvatarDescriptor avatar)
        {
            if (AvatarWardrobeServer.UploadTargetLocked) return;
            sceneAvatar = avatar;
            UseSceneAvatarRecord();
            AvatarWardrobeServer.SceneAvatar = avatar;
            Repaint();
        }
        private void FollowSelection()
        {
            if (sceneAvatar != null || AvatarWardrobeServer.UploadTargetLocked) return;
            var candidates = AvatarWardrobeServer.SceneTargets();
            if (candidates.Length == 1) SetTarget(candidates[0]);
        }
        private void UseSceneAvatarRecord()
        {
            var avatar = AvatarWardrobeCatalog.GetAvatarRecord(sceneAvatar);
            if (avatar != null) avatarGuid = avatar.guid;
        }
    }
}
