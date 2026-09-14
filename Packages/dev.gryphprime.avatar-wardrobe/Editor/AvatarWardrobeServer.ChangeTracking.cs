using System;
using System.IO;
using System.Threading;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OutfitToggleGenerator
{
    internal static partial class AvatarWardrobeServer
    {
        internal const int DesktopProtocol = 4;

        [Serializable]
        private sealed class BridgeRecord
        {
            public int protocol = DesktopProtocol;
            public string project = string.Empty;
            public int port;
            public string session = string.Empty;
            public bool online;
        }

        [Serializable]
        private sealed class RevisionDto
        {
            public int ok = 1;
            public string session = string.Empty;
            public long unityRevision;
            public string wardrobeVersion = WardrobeVersion.Current;
        }

        private static long unityRevision;
        private static int unityRevisionPending;
        private static bool changeTracking;

        private static string BridgeRecordPath => string.IsNullOrEmpty(serverProjectPath)
            ? string.Empty
            : Path.Combine(serverProjectPath, "Library", "AvatarWardrobe", "bridge.json");

        private static void WriteBridgeRecord(bool online)
        {
            var path = BridgeRecordPath;
            if (string.IsNullOrEmpty(path) || Port < 8909 || Port > 8929) return;
            try
            {
                var record = new BridgeRecord
                {
                    protocol = DesktopProtocol,
                    project = serverProjectPath,
                    port = Port,
                    session = serverSession,
                    online = online
                };
                WardrobeAtomicFile.WriteText(path, JsonUtility.ToJson(record, true));
            }
            catch (Exception error)
            {
                WardrobeLog.Write("bridge", "Could not publish bridge state: " + error.Message);
            }
        }

        private static void MarkUnityChanged()
        {
            if (Running) Volatile.Write(ref unityRevisionPending, 1);
        }

        private static void ApplyPendingUnityRevision()
        {
            if (Interlocked.Exchange(ref unityRevisionPending, 0) != 0)
                Interlocked.Increment(ref unityRevision);
        }

        private static void OnObjectChanges(ref ObjectChangeEventStream changes) => MarkUnityChanged();
        private static void OnPlayModeChanged(PlayModeStateChange state) => MarkUnityChanged();
        private static void OnSceneOpened(Scene scene, OpenSceneMode mode) => MarkUnityChanged();
        private static void OnSceneClosed(Scene scene) => MarkUnityChanged();
        private static void OnActiveSceneChanged(Scene previous, Scene next) => MarkUnityChanged();
        private static void OnSceneSaved(Scene scene) => MarkUnityChanged();

        private static void StartChangeTracking()
        {
            if (changeTracking) return;
            changeTracking = true;
            ObjectChangeEvents.changesPublished -= OnObjectChanges;
            ObjectChangeEvents.changesPublished += OnObjectChanges;
            EditorApplication.hierarchyChanged -= MarkUnityChanged;
            EditorApplication.hierarchyChanged += MarkUnityChanged;
            EditorApplication.projectChanged -= MarkUnityChanged;
            EditorApplication.projectChanged += MarkUnityChanged;
            Undo.undoRedoPerformed -= MarkUnityChanged;
            Undo.undoRedoPerformed += MarkUnityChanged;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorSceneManager.sceneOpened -= OnSceneOpened;
            EditorSceneManager.sceneOpened += OnSceneOpened;
            EditorSceneManager.sceneClosed -= OnSceneClosed;
            EditorSceneManager.sceneClosed += OnSceneClosed;
            EditorSceneManager.activeSceneChangedInEditMode -= OnActiveSceneChanged;
            EditorSceneManager.activeSceneChangedInEditMode += OnActiveSceneChanged;
            EditorSceneManager.sceneSaved -= OnSceneSaved;
            EditorSceneManager.sceneSaved += OnSceneSaved;
        }

        private static void StopChangeTracking()
        {
            if (!changeTracking) return;
            changeTracking = false;
            ObjectChangeEvents.changesPublished -= OnObjectChanges;
            EditorApplication.hierarchyChanged -= MarkUnityChanged;
            EditorApplication.projectChanged -= MarkUnityChanged;
            Undo.undoRedoPerformed -= MarkUnityChanged;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorSceneManager.sceneOpened -= OnSceneOpened;
            EditorSceneManager.sceneClosed -= OnSceneClosed;
            EditorSceneManager.activeSceneChangedInEditMode -= OnActiveSceneChanged;
            EditorSceneManager.sceneSaved -= OnSceneSaved;
        }

        private static RevisionDto GetRevision()
        {
            ApplyPendingUnityRevision();
            return new RevisionDto { session = serverSession, unityRevision = Interlocked.Read(ref unityRevision) };
        }

        internal static long CurrentUnityRevision
        {
            get
            {
                ApplyPendingUnityRevision();
                return Interlocked.Read(ref unityRevision);
            }
        }
    }
}
