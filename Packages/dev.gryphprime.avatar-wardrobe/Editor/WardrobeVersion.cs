using System;
using System.IO;
using UnityEditor.PackageManager;
using UnityEngine;

namespace OutfitToggleGenerator
{
    internal static class WardrobeVersion
    {
        [Serializable] private sealed class VersionFile { public string version; }
        private static string cached;
        internal static string Current
        {
            get
            {
                if (cached != null) return cached;
                cached = "";
                try
                {
                    var package = PackageInfo.FindForAssembly(typeof(WardrobeVersion).Assembly);
                    if (package != null && package.name == "dev.gryphprime.avatar-wardrobe")
                        cached = package.version;
                    else
                    {
                        var path = Path.Combine(Application.dataPath, "OutfitToggleGenerator/Editor/WardrobeVersion.json");
                        if (File.Exists(path)) cached = JsonUtility.FromJson<VersionFile>(File.ReadAllText(path)).version ?? "";
                    }
                }
                catch (Exception) { /* Unversioned development copies do not advertise updates. */ }
                return cached;
            }
        }
    }
}
