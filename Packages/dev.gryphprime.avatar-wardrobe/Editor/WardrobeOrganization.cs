using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace OutfitToggleGenerator
{
    [Serializable]
    internal sealed class WardrobeTag
    {
        public string id;
        public string name;
        public string color;
    }

    [Serializable]
    internal sealed class WardrobeFamilyTags
    {
        public string familyId;
        public List<string> tags = new List<string>();
    }

    [Serializable]
    internal sealed class WardrobeOrganizationFile
    {
        public int version = 1;
        public List<WardrobeTag> tags = new List<WardrobeTag>();
        public List<WardrobeFamilyTags> families = new List<WardrobeFamilyTags>();
        public List<string> hiddenFamilies = new List<string>();
        // Asset folder prefixes ("Assets/_PoiyomiShaders/") hidden from the catalog.
        public List<string> hiddenPaths = new List<string>();
    }

    // Library-level organization (tags, hidden items). Stored with the project, not the
    // browser, and deliberately outside avatar Undo: it never changes the scene.
    internal static class WardrobeOrganization
    {
        internal const int MaxTags = 200;
        internal const int MaxTagName = 40;
        private static readonly object Gate = new object();
        private static WardrobeOrganizationFile data;
        private static int version;
        private static string path;

        internal static int Version { get { lock (Gate) return version; } }

        internal static string StorePath
        {
            get
            {
                if (path == null)
                    path = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "ProjectSettings", "AvatarWardrobeOrganization.json");
                return path;
            }
        }

        private static WardrobeOrganizationFile Parse(string json)
        {
            var parsed = JsonUtility.FromJson<WardrobeOrganizationFile>(json);
            if (parsed == null || parsed.tags == null) throw new InvalidDataException("Invalid wardrobe organization file.");
            return parsed;
        }

        private static WardrobeOrganizationFile Load()
        {
            if (data != null) return data;
            var json = WardrobeAtomicFile.ReadRecoverableText(StorePath, text => Parse(text));
            data = json == null ? new WardrobeOrganizationFile() : Parse(json);
            if (data.tags == null) data.tags = new List<WardrobeTag>();
            if (data.families == null) data.families = new List<WardrobeFamilyTags>();
            if (data.hiddenFamilies == null) data.hiddenFamilies = new List<string>();
            if (data.hiddenPaths == null) data.hiddenPaths = new List<string>();
            data.tags.RemoveAll(tag => tag == null || string.IsNullOrEmpty(tag.id) || string.IsNullOrWhiteSpace(tag.name));
            data.families.RemoveAll(entry => entry == null || string.IsNullOrEmpty(entry.familyId) || entry.tags == null || entry.tags.Count == 0);
            return data;
        }

        // Mutations copy, edit, then persist; a failed write keeps the previous state.
        private static T Edit<T>(Func<WardrobeOrganizationFile, T> change)
        {
            lock (Gate)
            {
                var previous = Load();
                var next = JsonUtility.FromJson<WardrobeOrganizationFile>(JsonUtility.ToJson(previous));
                var result = change(next);
                Directory.CreateDirectory(Path.GetDirectoryName(StorePath));
                WardrobeAtomicFile.RestoreText(StorePath, JsonUtility.ToJson(next, true), text => Parse(text));
                data = next;
                version++;
                return result;
            }
        }

        internal static void Invalidate()
        {
            lock (Gate) { data = null; version++; }
        }

        internal static List<WardrobeTag> Tags()
        {
            lock (Gate) return Load().tags.Select(tag => new WardrobeTag { id = tag.id, name = tag.name, color = tag.color }).ToList();
        }

        internal static List<string> HiddenPaths()
        {
            lock (Gate) return Load().hiddenPaths.ToList();
        }

        internal static int HiddenFamilyCount()
        {
            lock (Gate) return Load().hiddenFamilies.Count;
        }

        internal static List<string> TagsFor(string familyId)
        {
            lock (Gate)
            {
                var entry = Load().families.FirstOrDefault(item => item.familyId == familyId);
                return entry == null ? new List<string>() : entry.tags.ToList();
            }
        }

        internal static bool IsHidden(WardrobeFamily family)
        {
            if (family == null) return false;
            lock (Gate)
            {
                var file = Load();
                if (file.hiddenFamilies.Contains(family.id)) return true;
                if (file.hiddenPaths.Count == 0) return false;
                return family.variants.Count > 0 && family.variants.All(variant => variant != null &&
                    file.hiddenPaths.Any(prefix => (variant.assetPath ?? "").StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
            }
        }

        internal static string HiddenByPath(WardrobeFamily family)
        {
            if (family == null || family.variants.Count == 0) return "";
            lock (Gate)
                return Load().hiddenPaths.FirstOrDefault(prefix => family.variants.All(variant =>
                    (variant.assetPath ?? "").StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) ?? "";
        }

        internal static string NormalizeFolder(string folder)
        {
            folder = (folder ?? "").Replace('\\', '/').Trim().TrimEnd('/');
            if (folder.Length == 0 || folder.Contains("..") ||
                !(folder.StartsWith("Assets/", StringComparison.Ordinal) || folder.StartsWith("Packages/", StringComparison.Ordinal)))
                return null;
            return folder + "/";
        }

        internal static string CleanColor(string color)
        {
            color = (color ?? "").Trim();
            if (color.Length == 7 && color[0] == '#' && color.Skip(1).All(Uri.IsHexDigit)) return color.ToLowerInvariant();
            return "#8a8f98";
        }

        internal static WardrobeTag SaveTag(string id, string name, string color)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) throw new ArgumentException(WardrobeStrings.T("org.tag.nameRequired"));
            if (name.Length > MaxTagName) name = name.Substring(0, MaxTagName);
            return Edit(file =>
            {
                var duplicate = file.tags.FirstOrDefault(tag => tag.id != id && string.Equals(tag.name, name, StringComparison.OrdinalIgnoreCase));
                if (duplicate != null) throw new ArgumentException(WardrobeStrings.T("org.tag.duplicate", duplicate.name));
                var existing = string.IsNullOrEmpty(id) ? null : file.tags.FirstOrDefault(tag => tag.id == id);
                if (existing == null)
                {
                    if (!string.IsNullOrEmpty(id)) throw new ArgumentException(WardrobeStrings.T("org.tag.missing"));
                    if (file.tags.Count >= MaxTags) throw new ArgumentException(WardrobeStrings.T("org.tag.limit", MaxTags));
                    existing = new WardrobeTag { id = Guid.NewGuid().ToString("N").Substring(0, 12) };
                    file.tags.Add(existing);
                }
                existing.name = name;
                existing.color = CleanColor(color);
                return new WardrobeTag { id = existing.id, name = existing.name, color = existing.color };
            });
        }

        internal static void DeleteTag(string id)
        {
            Edit(file =>
            {
                file.tags.RemoveAll(tag => tag.id == id);
                foreach (var entry in file.families) entry.tags.Remove(id);
                file.families.RemoveAll(entry => entry.tags.Count == 0);
                return true;
            });
        }

        internal static void SetFamilyTag(IEnumerable<string> familyIds, string tagId, bool on)
        {
            var ids = familyIds.Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();
            Edit(file =>
            {
                if (!file.tags.Any(tag => tag.id == tagId)) throw new ArgumentException(WardrobeStrings.T("org.tag.missing"));
                foreach (var familyId in ids)
                {
                    var entry = file.families.FirstOrDefault(item => item.familyId == familyId);
                    if (entry == null)
                    {
                        if (!on) continue;
                        entry = new WardrobeFamilyTags { familyId = familyId };
                        file.families.Add(entry);
                    }
                    if (on && !entry.tags.Contains(tagId)) entry.tags.Add(tagId);
                    if (!on) entry.tags.Remove(tagId);
                }
                file.families.RemoveAll(entry => entry.tags.Count == 0);
                return true;
            });
        }

        internal static void SetHidden(IEnumerable<string> familyIds, bool hidden)
        {
            var ids = familyIds.Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();
            Edit(file =>
            {
                foreach (var id in ids)
                {
                    if (hidden && !file.hiddenFamilies.Contains(id)) file.hiddenFamilies.Add(id);
                    if (!hidden) file.hiddenFamilies.Remove(id);
                }
                return true;
            });
        }

        internal static void SetHiddenPath(string folder, bool hidden)
        {
            var prefix = NormalizeFolder(folder);
            if (prefix == null) throw new ArgumentException(WardrobeStrings.T("org.hide.badFolder"));
            Edit(file =>
            {
                file.hiddenPaths.RemoveAll(existing => string.Equals(existing, prefix, StringComparison.OrdinalIgnoreCase));
                if (hidden) file.hiddenPaths.Add(prefix);
                return true;
            });
        }
    }
}
