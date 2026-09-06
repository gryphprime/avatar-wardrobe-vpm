using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine;

namespace OutfitToggleGenerator
{
    [Serializable]
    internal class ToggleClusterCandidate
    {
        public int id;
        public string label;
        public string path;
    }

    [Serializable]
    internal class ToggleClusterRequest
    {
        public string projectType;
        public string avatarName;
        public string outfitName;
        public string outfitPrefabPath;
        public ToggleClusterCandidate[] toggles;
    }

    [Serializable]
    internal class ToggleClusterGroup
    {
        public int[] ids;
        public string label;
    }

    [Serializable]
    internal class ToggleClusterResponse
    {
        public ToggleClusterGroup[] groups;
    }

    internal static class AppleIntelligenceToggleClusterer
    {
#if UNITY_EDITOR_OSX
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void Completion(IntPtr responseJson, IntPtr errorMessage);

        [DllImport("OutfitToggleAppleIntelligence", CallingConvention = CallingConvention.Cdecl)]
        private static extern void OutfitToggleAppleIntelligence_ClusterToggles(string requestJson, Completion completion);

        [DllImport("OutfitToggleAppleIntelligence", CallingConvention = CallingConvention.Cdecl)]
        private static extern void OutfitToggleAppleIntelligence_FreeString(IntPtr value);

        private static readonly Completion NativeCompletion = ReceiveNativeCompletion;
        private static readonly Queue<NativeResult> Results = new Queue<NativeResult>();
        private static Action<ToggleClusterGroup[]> onSuccess;
        private static Action<string> onFailure;
        private static bool isRunning;

        private struct NativeResult
        {
            public string responseJson;
            public string errorMessage;
        }
#endif

        internal static void Cluster(
            string avatarName,
            string outfitNames,
            string outfitPaths,
            ToggleClusterCandidate[] toggles,
            Action<ToggleClusterGroup[]> success,
            Action<string> failure)
        {
#if UNITY_EDITOR_OSX
            if (isRunning)
            {
                failure("Apple Intelligence clustering is already running.");
                return;
            }

            try
            {
                isRunning = true;
                onSuccess = success;
                onFailure = failure;
                OutfitToggleAppleIntelligence_ClusterToggles(JsonUtility.ToJson(new ToggleClusterRequest
                {
                    projectType = "VRChat avatar outfit toggle menu",
                    avatarName = avatarName,
                    outfitName = outfitNames,
                    outfitPrefabPath = outfitPaths,
                    toggles = toggles,
                }), NativeCompletion);
                EditorApplication.update += DeliverResult;
            }
            catch (EntryPointNotFoundException)
            {
                Reset();
                failure("Restart Unity to load Apple Intelligence clustering support.");
            }
            catch (Exception exception)
            {
                Reset();
                failure(exception.Message);
            }
#else
            failure("Apple Intelligence clustering is available only in the macOS Editor.");
#endif
        }

#if UNITY_EDITOR_OSX
        private static void ReceiveNativeCompletion(IntPtr responseJson, IntPtr errorMessage)
        {
            var result = new NativeResult { responseJson = ReadAndFree(responseJson), errorMessage = ReadAndFree(errorMessage) };
            lock (Results) Results.Enqueue(result);
        }

        private static string ReadAndFree(IntPtr value)
        {
            if (value == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUTF8(value); }
            finally { OutfitToggleAppleIntelligence_FreeString(value); }
        }

        private static void DeliverResult()
        {
            NativeResult result;
            lock (Results)
            {
                if (Results.Count == 0) return;
                result = Results.Dequeue();
            }

            var success = onSuccess;
            var failure = onFailure;
            Reset();
            if (!string.IsNullOrEmpty(result.errorMessage))
            {
                failure(result.errorMessage);
                return;
            }

            try { success(JsonUtility.FromJson<ToggleClusterResponse>(result.responseJson)?.groups ?? Array.Empty<ToggleClusterGroup>()); }
            catch (Exception exception) { failure($"Apple Intelligence returned invalid groups: {exception.Message}"); }
        }

        private static void Reset()
        {
            EditorApplication.update -= DeliverResult;
            isRunning = false;
            onSuccess = null;
            onFailure = null;
        }
#endif
    }
}
