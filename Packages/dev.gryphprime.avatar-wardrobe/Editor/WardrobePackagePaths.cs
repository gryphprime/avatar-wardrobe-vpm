using System.IO;
using UnityEditor.PackageManager;
using UnityEngine;

namespace OutfitToggleGenerator
{
    internal static class WardrobePackagePaths
    {
        internal static string AssetRoot
        {
            get
            {
                var package = PackageInfo.FindForAssembly(typeof(WardrobePackagePaths).Assembly);
                return package != null ? package.assetPath : "Assets/OutfitToggleGenerator";
            }
        }
        internal static string Root
        {
            get
            {
                var package = PackageInfo.FindForAssembly(typeof(WardrobePackagePaths).Assembly);
                return package != null ? package.resolvedPath : Path.Combine(Application.dataPath, "OutfitToggleGenerator");
            }
        }
        internal static string File(string relative) { return Path.Combine(Root, relative); }
    }
}
