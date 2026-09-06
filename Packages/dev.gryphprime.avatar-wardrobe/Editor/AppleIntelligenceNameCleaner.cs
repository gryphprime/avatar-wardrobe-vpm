using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine;

namespace OutfitToggleGenerator
{
    // Keep AI features opt-in while the Wardrobe release is validated. All
    // callers must use IsAvailable(), which makes this a single UI/API gate.
    internal static class WardrobeFeatureFlags
    {
        internal const bool AppleIntelligence = false;
    }

    [Serializable]
    internal class ToggleNameCandidate
    {
        public int id;
        public string path;
        public string name;
    }

    [Serializable]
    internal class ToggleNameRequest
    {
        public string projectType;
        public string avatarName;
        public string outfitName;
        public string outfitPrefabPath;
        public ToggleNameCandidate[] toggles;
    }

    [Serializable]
    internal class CleanedToggleName
    {
        public int id;
        public string label;
    }

    [Serializable]
    internal class ToggleNameResponse
    {
        public CleanedToggleName[] labels;
    }

    internal static class AppleIntelligenceNameCleaner
    {
#if UNITY_EDITOR_OSX
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void Completion(IntPtr responseJson, IntPtr errorMessage);

        [DllImport("OutfitToggleAppleIntelligence", CallingConvention = CallingConvention.Cdecl)]
        private static extern int OutfitToggleAppleIntelligence_IsAvailable();

        [DllImport("OutfitToggleAppleIntelligence", CallingConvention = CallingConvention.Cdecl)]
        private static extern void OutfitToggleAppleIntelligence_CleanNames(string requestJson, Completion completion);

        [DllImport("OutfitToggleAppleIntelligence", CallingConvention = CallingConvention.Cdecl)]
        private static extern void OutfitToggleAppleIntelligence_FreeString(IntPtr value);

        private static readonly Completion NativeCompletion = ReceiveNativeCompletion;
        private static readonly Queue<NativeResult> Results = new Queue<NativeResult>();
        private static Action<Dictionary<int, string>> onSuccess;
        private static Action<string> onFailure;
        private static bool isRunning;

        private struct NativeResult
        {
            public string responseJson;
            public string errorMessage;
        }
#endif

        internal static bool IsAvailable()
        {
            if (!WardrobeFeatureFlags.AppleIntelligence) return false;
#if UNITY_EDITOR_OSX
            try
            {
                return OutfitToggleAppleIntelligence_IsAvailable() != 0;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
            catch (BadImageFormatException)
            {
                return false;
            }
#else
            return false;
#endif
        }

        internal static void Clean(
            string avatarName,
            string outfitName,
            string outfitPrefabPath,
            ToggleNameCandidate[] toggles,
            Action<Dictionary<int, string>> success,
            Action<string> failure)
        {
#if UNITY_EDITOR_OSX
            if (isRunning)
            {
                failure("Apple Intelligence cleanup is already running.");
                return;
            }

            try
            {
                isRunning = true;
                onSuccess = success;
                onFailure = failure;
                var request = new ToggleNameRequest
                {
                    projectType = "VRChat avatar outfit toggle menu",
                    avatarName = avatarName,
                    outfitName = outfitName,
                    outfitPrefabPath = outfitPrefabPath,
                    toggles = toggles,
                };
                OutfitToggleAppleIntelligence_CleanNames(JsonUtility.ToJson(request), NativeCompletion);
                EditorApplication.update += DeliverResult;
            }
            catch (Exception exception)
            {
                Reset();
                failure(exception.Message);
            }
#else
            failure("Apple Intelligence cleanup is available only in the macOS Editor.");
#endif
        }

#if UNITY_EDITOR_OSX
        private static void ReceiveNativeCompletion(IntPtr responseJson, IntPtr errorMessage)
        {
            var result = new NativeResult
            {
                responseJson = ReadAndFree(responseJson),
                errorMessage = ReadAndFree(errorMessage),
            };
            lock (Results) Results.Enqueue(result);
        }

        private static string ReadAndFree(IntPtr value)
        {
            if (value == IntPtr.Zero) return null;
            try
            {
                return Marshal.PtrToStringUTF8(value);
            }
            finally
            {
                OutfitToggleAppleIntelligence_FreeString(value);
            }
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

            try
            {
                var response = JsonUtility.FromJson<ToggleNameResponse>(result.responseJson);
                var labels = new Dictionary<int, string>();
                if (response?.labels != null)
                    foreach (var label in response.labels)
                        if (!string.IsNullOrWhiteSpace(label.label))
                            labels[label.id] = label.label.Trim();
                success(labels);
            }
            catch (Exception exception)
            {
                failure($"Apple Intelligence returned invalid data: {exception.Message}");
            }
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
