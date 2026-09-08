using UnityEditor;
using UnityEngine;

namespace OutfitToggleGenerator
{
    internal sealed class WardrobeSettingsUndo : ScriptableObject
    {
        [SerializeField] private string presets;
        [SerializeField] private bool hadPresets;
        private static WardrobeSettingsUndo state;
        private static string lastFlushed;
        internal static void Begin(string label)
        {
            if (state == null)
            {
                state = CreateInstance<WardrobeSettingsUndo>();
                state.hideFlags = HideFlags.HideAndDontSave;
                Undo.undoRedoPerformed += Flush;
            }
            Capture();
            Undo.RegisterCompleteObjectUndo(state, label);
        }
        internal static void Capture()
        {
            if (state == null) return;
            var p = AvatarWardrobePresets.CaptureSettings();
            state.hadPresets = p != null; state.presets = p ?? "";
            lastFlushed = p;
            EditorUtility.SetDirty(state);
        }
        private static void Flush()
        {
            if (state == null) return;
            var next = state.hadPresets ? state.presets : null;
            if (next == lastFlushed) return;
            try { AvatarWardrobePresets.RestoreSettings(next); lastFlushed = next; }
            catch (System.Exception e) { Debug.LogException(e); }
        }
    }
}
