using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace OutfitToggleGenerator
{
    // Classification signals for Avatar Wardrobe: hair vs outfit vs gimmick.
    // Extracted move-only from AvatarWardrobeCatalog; behavior unchanged.
    // Weights mirror Tools/WardrobeIndexer/wardrobe_classify.py category_scores.
    // No Unity dependencies, so this Module is testable outside the Editor.
    internal static class WardrobeClassify
    {
        internal static readonly HashSet<string> HairTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "hair", "ahoge", "bangs", "bang", "twintail", "twin", "ponytail", "braid", "braided",
            "mitsuami", "bun", "hime", "odango", "forelock", "sidelock", "tied",
        };

        internal static readonly HashSet<string> OutfitTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "skirt", "skirts", "dress", "dresses", "jacket", "jackets", "shirt", "shirts", "blouse",
            "pants", "jeans", "trousers", "leggings", "bikini", "bra", "bras", "panty", "panties",
            "underwear", "underwears", "shoes", "boots", "loafer", "loafers", "sneaker", "sneakers",
            "heel", "heels", "suit", "suits", "coat", "coats", "sweater", "knit", "hoodie", "parka",
            "uniform", "sailor", "swimsuit", "swimsuits", "leotard", "corset", "cardigan", "blazer",
            "garter", "apron", "kimono", "yukata", "maid", "bunny", "onepiece", "overalls", "vest",
            "gown", "robe", "polo", "tee", "sleeve", "sleeves", "bottoms", "tights", "pantyhose",
            "stocking", "stockings", "socks", "bodysuit", "outer",
            "キャミソール", "靴下", "パンツ", "ドルフィンパンツ", "サンダル", "ジャケット",
            "スカート", "インナースカート", "アウタースカート", "ガントレット",
            "チョーカー", "ヘルメット", "ベルト", "ズボン",
        };

        internal static readonly HashSet<string> GimmickTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "gun", "pistol", "watergun", "cigarette", "lighter", "bullet",
            "タバコ", "ライター",
        };

        private static readonly Regex UnicodeEscapeRegex =
            new Regex(@"\\[uU]([0-9a-fA-F]{4})", RegexOptions.Compiled);

        internal static string DecodePrefabName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.IndexOf('\\') < 0) return name;
            try
            {
                return UnicodeEscapeRegex.Replace(name,
                    match => ((char)Convert.ToInt32(match.Groups[1].Value, 16)).ToString());
            }
            catch (Exception)
            {
                return name;
            }
        }

        internal static string BoneStem(string bone)
        {
            var stem = bone ?? string.Empty;
            var end = stem.Length;
            while (end > 0 && char.IsDigit(stem[end - 1])) end--;
            if (end < stem.Length && end > 0 && (stem[end - 1] == 'l' || stem[end - 1] == 'r')) end--;
            while (end > 0 && char.IsDigit(stem[end - 1])) end--;
            stem = stem.Substring(0, end);
            if (stem.Length > 3 && (stem.EndsWith("l") || stem.EndsWith("r")))
                stem = stem.Substring(0, stem.Length - 1);
            return stem;
        }

        internal static HashSet<string> CategoryTokens(IEnumerable<string> names)
        {
            var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (names == null) return tokens;
            foreach (var name in names)
                foreach (var token in WardrobeText.Tokenize(DecodePrefabName(name)))
                {
                    var stripped = token.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
                    if (!string.IsNullOrEmpty(stripped)) tokens.Add(stripped);
                }
            return tokens;
        }

        internal static int CountHits(HashSet<string> tokens, HashSet<string> signals)
        {
            var hits = 0;
            foreach (var token in tokens)
                if (signals.Contains(token)) hits++;
            return hits;
        }

        internal static void CategoryScores(WardrobeAssetRecord record, out float hair, out float outfit, out float gimmick)
        {
            var parts = CategoryTokens(record.partGroups);
            var bones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (record.boneNames != null)
                foreach (var bone in record.boneNames)
                {
                    var stem = BoneStem(bone);
                    if (!string.IsNullOrEmpty(stem)) bones.Add(stem);
                }
            var path = CategoryTokens(new[] { record.assetPath });
            var family = CategoryTokens(new[] { record.familyName, record.displayName });
            var materials = CategoryTokens(record.materialNames);
            hair = 3 * CountHits(parts, HairTokens)
                + 2 * CountHits(bones, HairTokens)
                + 2 * CountHits(path, HairTokens)
                + 1.5f * CountHits(family, HairTokens)
                + CountHits(materials, HairTokens);
            outfit = 3 * CountHits(parts, OutfitTokens)
                + 2 * CountHits(bones, OutfitTokens)
                + 2 * CountHits(path, OutfitTokens)
                + 1.5f * CountHits(family, OutfitTokens)
                + CountHits(materials, OutfitTokens);
            gimmick = 3 * CountHits(parts, GimmickTokens)
                + 2 * CountHits(bones, GimmickTokens)
                + 2 * CountHits(path, GimmickTokens)
                + 1.5f * CountHits(family, GimmickTokens)
                + CountHits(materials, GimmickTokens);
        }

        internal static void ClassifyCategoryScores(float hair, float outfit, float gimmick, out string label, out float confidence)
        {
            var total = hair + outfit + gimmick;
            if (total <= 0f)
            {
                label = "unknown";
                confidence = 0f;
                return;
            }
            if (gimmick > hair && gimmick > outfit && gimmick / total >= 0.5f)
            {
                label = "gimmick";
                confidence = gimmick / total;
                return;
            }
            if (hair > outfit && hair / total >= 0.6f)
            {
                label = "hair";
                confidence = hair / total;
                return;
            }
            if (outfit > hair && outfit / total >= 0.6f)
            {
                label = "outfit";
                confidence = outfit / total;
                return;
            }
            label = "unknown";
            var best = hair > outfit ? hair : outfit;
            best = best > gimmick ? best : gimmick;
            confidence = best / total;
        }

        internal static string FamilyCategory(WardrobeFamily family, out float confidence)
        {
            float hair = 0f, outfit = 0f, gimmick = 0f, unknown = 0f;
            if (family != null)
                foreach (var variant in family.variants)
                {
                    if (variant == null) continue;
                    var label = string.IsNullOrEmpty(variant.category) ? "unknown" : variant.category;
                    var weight = variant.categoryConfidence > 0f ? variant.categoryConfidence : 0.5f;
                    if (label == "hair") hair += weight;
                    else if (label == "outfit") outfit += weight;
                    else if (label == "gimmick") gimmick += weight;
                    else unknown += weight;
                }
            var total = hair + outfit + gimmick + unknown;
            if (total <= 0f)
            {
                confidence = 0f;
                return "unknown";
            }
            if (hair > outfit && hair > gimmick && hair > unknown)
            {
                confidence = hair / total;
                return "hair";
            }
            if (outfit > hair && outfit > gimmick && outfit > unknown)
            {
                confidence = outfit / total;
                return "outfit";
            }
            if (gimmick > hair && gimmick > outfit && gimmick > unknown)
            {
                confidence = gimmick / total;
                return "gimmick";
            }
            confidence = unknown / total;
            return "unknown";
        }
    }
}
