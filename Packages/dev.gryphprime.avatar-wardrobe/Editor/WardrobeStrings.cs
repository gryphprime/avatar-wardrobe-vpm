using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace OutfitToggleGenerator
{
    // Single-source localization: strings live in Web/lang.json (array shape,
    // because JsonUtility cannot do dictionaries). The browser fetches the same
    // file directly; this class serves the Unity window and server messages.
    // Language follows the OS (Application.systemLanguage); anything unmapped
    // falls back to English, then to the key itself.
    [Serializable]
    internal sealed class WardrobeLangFile
    {
        public WardrobeLang[] langs;
    }

    [Serializable]
    internal sealed class WardrobeLang
    {
        public string code;
        public string name;
        public WardrobeLangEntry[] strings;
    }

    [Serializable]
    internal sealed class WardrobeLangEntry
    {
        public string k;
        public string v;
    }

    internal sealed class WardrobeLanguageAssetPostprocessor : UnityEditor.AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            foreach (var path in imported)
                if (path.EndsWith("/Web/lang.json", StringComparison.OrdinalIgnoreCase)) WardrobeStrings.Invalidate();
        }
    }

    internal static class WardrobeStrings
    {
        private static readonly object initLock = new object();
        private static Dictionary<string, Dictionary<string, string>> table;
        private static string code;
        private static volatile bool reloadRequired;
        internal static void Invalidate() { reloadRequired = true; }
        private static readonly System.Threading.AsyncLocal<string> requestCode = new System.Threading.AsyncLocal<string>();
        internal static string RequestCode { get => requestCode.Value; set => requestCode.Value = value; }

        internal static string Code
        {
            get
            {
                EnsureInitialized();
                return code ?? "en";
            }
        }

        internal static void EnsureInitialized()
        {
            if (table != null && !reloadRequired) return;
            lock (initLock)
            {
                if (table != null && !reloadRequired) return;
                var built = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var path = WardrobePackagePaths.File("Web/lang.json");
                    if (File.Exists(path))
                    {
                        var file = JsonUtility.FromJson<WardrobeLangFile>(File.ReadAllText(path));
                        if (file != null && file.langs != null)
                            foreach (var lang in file.langs)
                            {
                                if (lang == null || string.IsNullOrEmpty(lang.code) || lang.strings == null) continue;
                                var dict = new Dictionary<string, string>(StringComparer.Ordinal);
                                foreach (var entry in lang.strings)
                                {
                                    if (entry == null || string.IsNullOrEmpty(entry.k)) continue;
                                    dict[entry.k] = entry.v ?? string.Empty;
                                }
                                built[lang.code] = dict;
                            }
                    }
                }
                catch (Exception exception)
                {
                    Debug.LogWarning("Avatar Wardrobe could not read its language file: " + exception.Message);
                }
                table = built;
                reloadRequired = false;
                code = "en";
                try
                {
                    if (Application.systemLanguage == SystemLanguage.Japanese && built.ContainsKey("ja"))
                        code = "ja";
                    else if (Application.systemLanguage == SystemLanguage.Korean && built.ContainsKey("ko"))
                        code = "ko";
                    else if ((Application.systemLanguage == SystemLanguage.Chinese || Application.systemLanguage == SystemLanguage.ChineseSimplified || Application.systemLanguage == SystemLanguage.ChineseTraditional) && built.ContainsKey("zh"))
                        code = "zh";
                }
                catch (Exception) { }
                if (!built.ContainsKey(code)) code = "en";
            }
        }

        internal static string T(string key, params object[] args)
        {
            EnsureInitialized();
            var active = RequestCode ?? code;
            string value = null;
            Dictionary<string, string> lang;
            if (active != null && table.TryGetValue(active, out lang)) lang.TryGetValue(key, out value);
            Dictionary<string, string> english;
            if (value == null && table.TryGetValue("en", out english)) english.TryGetValue(key, out value);
            if (value == null) value = key;
            if (args != null)
                for (var i = 0; i < args.Length; i++)
                    value = value.Replace("{" + i + "}", args[i] == null ? string.Empty : args[i].ToString());
            return value;
        }

        internal static string ResolveCode(string tag)
        {
            EnsureInitialized();
            if (string.IsNullOrEmpty(tag)) return null;
            var clean = tag.Split(';')[0].Trim().ToLowerInvariant().Replace('_', '-');
            if (table.ContainsKey(clean)) return clean;
            var dash = clean.IndexOf('-');
            if (dash > 0)
            {
                var bare = clean.Substring(0, dash);
                if (table.ContainsKey(bare)) return bare;
            }
            return null;
        }
    }
}
