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
        [SerializeField] private string presets, overrides, uploads;
        [SerializeField] private bool presetsExist, overridesExist, uploadsExist, uploadsTracked;
        private static WardrobeEditHistory current;
        private static string appliedPresets, appliedOverrides, appliedUploads;
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
            appliedUploads = ShiroTools.OutfitProjectData.CaptureSettings();
        }
        internal static void Begin(string label, bool trackUploads = false)
        {
            Capture(trackUploads);
            Undo.RegisterCompleteObjectUndo(Current, label);
        }
        internal static void Capture(bool trackUploads = false)
        {
            var state = Current;
            appliedPresets = AvatarWardrobePresets.CaptureSettings();
            appliedOverrides = AvatarWardrobeCatalog.CaptureOverrides();
            state.presetsExist = appliedPresets != null; state.presets = appliedPresets ?? "";
            state.overridesExist = appliedOverrides != null; state.overrides = appliedOverrides ?? "";
            state.uploadsTracked = trackUploads;
            if (trackUploads)
            {
                appliedUploads = ShiroTools.OutfitProjectData.CaptureSettings();
                state.uploadsExist = appliedUploads != null; state.uploads = appliedUploads ?? "";
            }
            else { state.uploadsExist = false; state.uploads = ""; }
            EditorUtility.SetDirty(state);
        }
        private static void Restore()
        {
            if (current == null) current = Resources.FindObjectsOfTypeAll<WardrobeEditHistory>().FirstOrDefault();
            if (current == null) return;
            try
            {
                var nextPresets = current.presetsExist ? current.presets : null;
                var nextOverrides = current.overridesExist ? current.overrides : null;
                var nextUploads = current.uploadsExist ? current.uploads : null;
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
                if (current.uploadsTracked && nextUploads != appliedUploads)
                {
                    if (ShiroTools.OutfitProjectData.CaptureSettings() != appliedUploads)
                        throw new InvalidOperationException("Upload settings changed outside this operation. Scene Undo completed; review settings before continuing.");
                    ShiroTools.OutfitProjectData.RestoreSettings(nextUploads); appliedUploads = nextUploads;
                }
            }
            catch (Exception error) { Debug.LogError("Wardrobe Undo needs attention: " + error.Message); }
        }
    }
}
