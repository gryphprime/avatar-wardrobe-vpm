using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
namespace OutfitToggleGenerator
{
    // One hidden serialized object joins the same Unity Undo group as the scene edit.
    // Like Unity scene Undo, history lasts for this Editor session (not a restart).
    internal sealed class WardrobeEditHistory : ScriptableObject
    {
        [SerializeField] private string presets, overrides;
        [SerializeField] private bool presetsExist, overridesExist;
        private static WardrobeEditHistory current;
        private static string appliedPresets, appliedOverrides;
        private static string appliedHistory;
        private static WardrobeEditHistory Current
        {
            get
            {
                if (current == null) current = Resources.FindObjectsOfTypeAll<WardrobeEditHistory>().FirstOrDefault();
                if (current == null) { current = CreateInstance<WardrobeEditHistory>(); current.hideFlags = HideFlags.HideAndDontSave; }
                return current;
            }
        }
        [InitializeOnLoadMethod] private static void Initialize()
        {
            Undo.undoRedoPerformed -= Restore;
            Undo.undoRedoPerformed += Restore;
            appliedPresets = AvatarWardrobePresets.CaptureSettings();
            appliedOverrides = AvatarWardrobeCatalog.CaptureOverrides();
            current = Resources.FindObjectsOfTypeAll<WardrobeEditHistory>().FirstOrDefault();
            appliedHistory = current == null ? null : JsonUtility.ToJson(current);
        }
        internal static void Begin(string label)
        {
            Capture();
            Undo.RegisterCompleteObjectUndo(Current, label);
        }
        internal static void Capture()
        {
            var state = Current;
            appliedPresets = AvatarWardrobePresets.CaptureSettings();
            appliedOverrides = AvatarWardrobeCatalog.CaptureOverrides();
            state.presetsExist = appliedPresets != null; state.presets = appliedPresets ?? "";
            state.overridesExist = appliedOverrides != null; state.overrides = appliedOverrides ?? "";
            EditorUtility.SetDirty(state);
            appliedHistory = JsonUtility.ToJson(state);
        }
        private static void Restore()
        {
            if (current == null) current = Resources.FindObjectsOfTypeAll<WardrobeEditHistory>().FirstOrDefault();
            if (current == null) return;
            // Ignore unrelated Undo events when the serialized history has not changed.
            var nextHistory = JsonUtility.ToJson(current);
            if (nextHistory == appliedHistory) return;
            try
            {
                var nextPresets = current.presetsExist ? current.presets : null;
                var nextOverrides = current.overridesExist ? current.overrides : null;
                if (nextPresets != appliedPresets)
                {
                    if (AvatarWardrobePresets.CaptureSettings() != appliedPresets)
                        throw new InvalidOperationException("Preset settings changed outside this operation. Scene Undo completed; restore settings from your backup or redo and review.");
                    AvatarWardrobePresets.RestoreSettings(nextPresets); appliedPresets = nextPresets;
                }
                if (nextOverrides != appliedOverrides)
                {
                    if (AvatarWardrobeCatalog.CaptureOverrides() != appliedOverrides)
                        throw new InvalidOperationException("Compatibility settings changed outside this operation. Scene Undo completed; review settings before continuing.");
                    AvatarWardrobeCatalog.RestoreOverrides(nextOverrides); appliedOverrides = nextOverrides;
                }
                appliedHistory = nextHistory;
            }
            catch (Exception error) { Debug.LogError("Wardrobe Undo needs attention: " + error.Message); }
        }
    }
}
