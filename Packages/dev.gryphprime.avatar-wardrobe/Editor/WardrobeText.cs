using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace OutfitToggleGenerator
{
    // Pure text and naming helpers for Avatar Wardrobe.
    // Extracted move-only from AvatarWardrobeCatalog; behavior unchanged.
    // Mirrors Tools/WardrobeIndexer/wardrobe_text.py 1:1: keep both in sync.
    // No Unity dependencies, so this Module is testable outside the Editor.
    internal static class WardrobeText
    {
        internal static readonly HashSet<string> GenericTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "asset", "assets", "avatar", "avatars", "base", "fbx", "model", "models", "modular", "outfit",
            "prefab", "prefabs", "prefeb", "ver", "version", "variant",
        };

        internal static readonly HashSet<string> VariantTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "beige", "black", "blue", "brown", "cyan", "gray", "grey", "green", "ivory", "lime", "navy", "orange",
            "pink", "purple", "red", "silver", "violet", "white", "yellow", "short", "long", "lite", "dark", "light",
            "with", "without", "on", "off", "default",
        };

        internal static List<string> Tokenize(string value)
        {
            var words = new List<string>();
            if (string.IsNullOrEmpty(value)) return words;
            var buffer = new StringBuilder();
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (index > 0 && char.IsUpper(character) && char.IsLower(value[index - 1]) && buffer.Length > 0)
                {
                    words.Add(buffer.ToString().ToLowerInvariant());
                    buffer.Length = 0;
                }
                if (char.IsLetterOrDigit(character))
                {
                    buffer.Append(character);
                    continue;
                }
                if (buffer.Length == 0) continue;
                words.Add(buffer.ToString().ToLowerInvariant());
                buffer.Length = 0;
            }
            if (buffer.Length > 0) words.Add(buffer.ToString().ToLowerInvariant());
            return words;
        }

        internal static string Title(IEnumerable<string> words)
        {
            return string.Join(" ", words.Select(word =>
                string.IsNullOrEmpty(word) || word.Length == 1
                    ? word
                    : char.ToUpperInvariant(word[0]) + word.Substring(1)));
        }

        internal static string Humanize(string value)
        {
            return Title(Tokenize(value));
        }

        internal static string Normalize(string value)
        {
            return string.Concat(Tokenize(value));
        }

        internal static bool IsGenericAssetName(string name)
        {
            var words = Tokenize(name);
            return words.Count == 0 || words.All(token => GenericTokens.Contains(token) || VariantTokens.Contains(token) ||
                                                        int.TryParse(token, out _));
        }

        internal static string DisplayName(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (!IsGenericAssetName(name)) return Humanize(name);

            for (var folder = Path.GetDirectoryName(path); !string.IsNullOrEmpty(folder); folder = Path.GetDirectoryName(folder))
            {
                var candidate = Path.GetFileName(folder);
                if (IsGenericAssetName(candidate)) continue;
                return Humanize(candidate);
            }
            return Humanize(name);
        }

        internal static string VariantName(string assetName)
        {
            var words = Tokenize(assetName);
            var color = words.FirstOrDefault(VariantTokens.Contains);
            if (!string.IsNullOrEmpty(color)) return Title(new[] { color });
            var number = words.FirstOrDefault(word => int.TryParse(word, out _));
            return string.IsNullOrEmpty(number) ? "Default" : "Variant " + number;
        }

        internal static string FamilyName(WardrobeAssetRecord record, IEnumerable<string> avatarTokens)
        {
            var words = Tokenize(Path.GetFileNameWithoutExtension(record.assetPath))
                .Where(token => !VariantTokens.Contains(token) && !avatarTokens.Contains(token) &&
                                !GenericTokens.Contains(token) && !int.TryParse(token, out _))
                .ToList();
            return words.Count == 0 ? record.displayName : Title(words);
        }
    }
}
