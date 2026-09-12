// ============================================================
//  VRC Preset Batch Uploader — project-scoped settings store
//
//  All per-avatar / per-preset data (blueprint IDs, batch and
//  platform toggles, blendshape overrides, item selections and
//  FaceEmo captures) lives in ProjectSettings/ShiroOutfit_data.json.
//
//  Why ProjectSettings and not EditorPrefs:
//    • survives deleting/replacing the plugin folder on updates
//      (ProjectSettings is never touched by that)
//    • scoped to THIS project — two projects with the same avatar
//      name no longer share blueprint IDs
//    • can be committed / backed up together with the project
//
//  Legacy EditorPrefs values supply defaults when a record is missing.
//  Only explicit mutations persist those defaults. Read scopes keep missing
//  records detached from authoritative state.
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ShiroTools
{
    internal static class OutfitProjectData
    {
        internal const string FILE_NAME = "ShiroOutfit_data.json";
        private static string FilePath => Path.Combine("ProjectSettings", FILE_NAME);

        private static int readDepth;
        internal static IDisposable ReadOnly() => new ReadScope();
        private sealed class ReadScope : IDisposable
        {
            public ReadScope() { readDepth++; }
            public void Dispose() { readDepth--; }
        }
        internal static AvatarData FindAvatar(string name) => Data.avatars.FirstOrDefault(a => a.name == name);

        // ---- Legacy EditorPrefs key patterns (for one-time migration) ----
        private const string LEGACY_PREFIX             = "ShiroOutfitUploader_";
        private const string LEGACY_ITEM_PREFIX        = "ShiroItem_";
        private const string LEGACY_ITEM_DEFAULT_PREFIX = "ShiroItemDefault_";
        private const string LEGACY_FACEEMO_PREFIX     = "ShiroFaceEmo_";

        // ============================================================
        //  Data model (JsonUtility-friendly: no dictionaries)
        // ============================================================
        [Serializable]
        internal class BlendShapeOverride
        {
            public string name;
            public float  value;
        }

        [Serializable]
        internal class ItemOverride
        {
            public string name;
            public bool   included;
        }

        [Serializable]
        internal class OutfitData
        {
            public string sceneIdentity, displayName;
            public string name;
            public string blueprintId    = "";
            public bool   includeInBatch = true;
            public bool   buildWindows   = true;
            public bool   buildAndroid;
            public bool   buildIOS;
            public string faceEmoName    = "";
            // Last successful upload per platform ("yyyy-MM-dd HH:mm", empty = never)
            public string lastUploadWindows = "";
            public string lastUploadAndroid = "";
            public string lastUploadIOS     = "";
            public List<BlendShapeOverride> blendShapes   = new List<BlendShapeOverride>();
            public List<ItemOverride>       itemOverrides = new List<ItemOverride>();
        }

        [Serializable]
        internal class AvatarData
        {
            public string sceneIdentity, displayName;
            public string name;
            public List<OutfitData> outfits      = new List<OutfitData>();
            // Item names included on EVERY preset by default (per-preset overrides win)
            public List<string>     itemDefaults = new List<string>();
            // Item names whose default was explicitly decided (so migration runs only once each)
            public List<string>     itemDefaultsDecided = new List<string>();
        }

        [Serializable]
        private class Root
        {
            public List<AvatarData> avatars = new List<AvatarData>();
        }

        private static Root _root;

        // ---- In-memory memo caches (GUI reads these every repaint — keep them O(1)) ----
        //      Tuple keys instead of concatenated strings: no per-lookup allocations.
        private static readonly Dictionary<(string avatar, string outfit), OutfitData> _outfitCache =
            new Dictionary<(string, string), OutfitData>();
        private static readonly Dictionary<(string kind, string avatar, string outfit, string item), bool> _boolMemo =
            new Dictionary<(string, string, string, string), bool>();

        private static void ClearCaches()
        {
            _outfitCache.Clear();
            _boolMemo.Clear();
        }

        // ============================================================
        //  Load / save
        // ============================================================
        private static Root Data
        {
            get
            {
                if (_root == null) Load();
                return _root;
            }
        }

        private static string _durable;
        private static void Load()
        {
            Root loaded;
            if (!File.Exists(FilePath)) loaded = new Root();
            else
            {
                try { loaded = ParseRoot(File.ReadAllText(FilePath)); }
                catch (Exception primary)
                {
                    try
                    {
                        loaded = ParseRoot(File.ReadAllText(FilePath + ".bak"));
                        // Preserve evidence before permitting any replacement of the primary.
                        File.Copy(FilePath, FilePath + ".corrupt-" + Guid.NewGuid().ToString("N"));
                    }
                    catch (Exception backup)
                    {
                        throw new IOException("Upload settings could not be recovered. Writes are blocked; restore " + FilePath,
                            new AggregateException(primary, backup));
                    }
                }
            }
            _root = loaded;
            _durable = JsonUtility.ToJson(loaded, true);
            ClearCaches();
        }

        private static Root ParseRoot(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || Newtonsoft.Json.Linq.JObject.Parse(json)["avatars"]?.Type != Newtonsoft.Json.Linq.JTokenType.Array)
                throw new InvalidDataException("Missing avatars collection.");
            var parsed = JsonUtility.FromJson<Root>(json);
            if (parsed?.avatars == null) throw new InvalidDataException("Invalid avatars collection.");
            foreach (var avatar in parsed.avatars)
            {
                if (avatar == null) throw new InvalidDataException("Invalid avatar settings.");
                avatar.outfits ??= new List<OutfitData>();
                avatar.itemDefaults ??= new List<string>();
                avatar.itemDefaultsDecided ??= new List<string>();
                foreach (var outfit in avatar.outfits)
                {
                    if (outfit == null) throw new InvalidDataException("Invalid preset settings.");
                    outfit.blendShapes ??= new List<BlendShapeOverride>();
                    outfit.itemOverrides ??= new List<ItemOverride>();
                    if (outfit.blendShapes.Any(b => b == null) || outfit.itemOverrides.Any(i => i == null))
                        throw new InvalidDataException("Invalid preset overrides.");
                }
            }
            return parsed;
        }

        internal static void Save()
        {
            if (readDepth > 0) throw new InvalidOperationException("A settings read attempted to save upload configuration.");
            var json = JsonUtility.ToJson(Data, true);
            try { WriteAtomically(FilePath, json); }
            catch
            {
                _root = _durable == null ? null : ParseRoot(_durable);
                ClearCaches();
                throw;
            }
            _durable = json;
            ClearCaches();
        }

        internal static string CaptureSettings() => File.Exists(FilePath) ? File.ReadAllText(FilePath) : null;
        internal static void RestoreSettings(string json)
        {
            if (json == null) { if (File.Exists(FilePath)) File.Delete(FilePath); }
            else { ParseRoot(json); WriteAtomically(FilePath, json); }
            _root = null; _durable = null; ClearCaches();
        }

        /// <summary>Writes critical project state through a sibling temp file and keeps the
        /// previous valid file as .bak. Callers must serialize/validate before invoking.</summary>
        internal static void WriteAtomically(string path, string contents)
        {
            global::OutfitToggleGenerator.WardrobeAtomicFile.WriteText(path, contents, true);
        }

        // ============================================================
        //  Accessors
        // ============================================================
        static OutfitProjectData()
        {
            UnityEditor.SceneManagement.EditorSceneManager.sceneSaved += scene =>
            {
                if (_root == null || readDepth > 0) return;
                bool changed = false;
                var avatars = _root.avatars.Where(a => a.name != null).GroupBy(a => a.name).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
                foreach (var root in scene.GetRootGameObjects())
                    foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                    {
                        var key = SessionState.GetString("Wardrobe.Upload.Identity." + transform.gameObject.GetInstanceID(), "");
                        if (string.IsNullOrEmpty(key)) continue;
                        var identity = SceneIdentity(transform.gameObject);
                        if (string.IsNullOrEmpty(identity)) continue;
                        if (avatars.TryGetValue(key, out var avatar) && avatar.sceneIdentity != identity)
                        { avatar.sceneIdentity = identity; changed = true; }
                        foreach (var record in _root.avatars.SelectMany(a => a.outfits).Where(o => o.name == key && o.sceneIdentity != identity))
                        { record.sceneIdentity = identity; changed = true; }
                    }
                if (changed) Save();
            };
        }
        private static string SceneIdentity(GameObject obj)
        {
            var id = GlobalObjectId.GetGlobalObjectIdSlow(obj);
            return string.IsNullOrEmpty(obj.scene.path) || id.targetObjectId == 0 ? "" : id.ToString();
        }
        internal static string SceneKey(GameObject obj)
        {
            if (obj == null) throw new InvalidOperationException("The scene target is missing.");
            var global = GlobalObjectId.GetGlobalObjectIdSlow(obj).ToString();
            bool saved = !string.IsNullOrEmpty(obj.scene.path) && GlobalObjectId.GetGlobalObjectIdSlow(obj).targetObjectId != 0;
            var sessionKey = "Wardrobe.Upload.Identity." + obj.GetInstanceID();
            var key = SessionState.GetString(sessionKey, "");
            if (string.IsNullOrEmpty(key)) key = saved ? "scene:" + global : "scene-session:" + Guid.NewGuid().ToString("N");
            SessionState.SetString(sessionKey, key);
            return key;
        }
        internal static string SceneAvatarKey(GameObject root)
        {
            var identity = SceneIdentity(root);
            var key = SceneKey(root);
            var existing = Data.avatars.FirstOrDefault(a => a.name == key || (!string.IsNullOrEmpty(identity) && a.sceneIdentity == identity));
            if (existing == null)
            {
                // A legacy label does not establish ownership across saved scenes.
                // Keep that record for explicit recovery instead of copying Blueprint IDs.
                if (Data.avatars.Any(a => a.name == root.name))
                    Debug.LogWarning("[Wardrobe] Legacy upload settings for '" + root.name + "' were retained. Assign settings to this exact avatar before uploading.");
                existing = GetAvatar(key);
            }
            if (readDepth == 0) { existing.sceneIdentity = identity; existing.displayName = root.name; }
            return existing.name;
        }
        internal static OutfitData SceneOutfit(string avatarKey, GameObject holder)
        {
            var identity = SceneIdentity(holder);
            var key = SceneKey(holder);
            var avatar = GetAvatar(avatarKey);
            var entry = avatar.outfits.FirstOrDefault(o => o.name == key || (!string.IsNullOrEmpty(identity) && o.sceneIdentity == identity));
            if (entry == null) entry = GetOutfit(avatarKey, key);
            if (readDepth == 0) { entry.sceneIdentity = identity; entry.displayName = holder.name; }
            return entry;
        }

        internal static AvatarData GetAvatar(string avatarName)
        {
            var av = Data.avatars.FirstOrDefault(a => a.name == avatarName);
            if (av == null)
            {
                av = new AvatarData { name = avatarName };
                if (readDepth == 0) Data.avatars.Add(av);
            }
            return av;
        }

        /// <summary>Returns the preset record, creating it (and importing any legacy
        /// EditorPrefs values for it) if the JSON doesn't know it yet.</summary>
        internal static OutfitData GetOutfit(string avatarName, string outfitName)
        {
            var key = (avatarName, outfitName);
            if (readDepth == 0 && _outfitCache.TryGetValue(key, out var cached)) return cached;

            var av = GetAvatar(avatarName);
            var o = av.outfits.FirstOrDefault(x => x.name == outfitName);
            if (o == null)
            {
                o = new OutfitData { name = outfitName };
                MigrateLegacyOutfit(avatarName, o);
                if (readDepth == 0) av.outfits.Add(o);
            }
            if (readDepth == 0) _outfitCache[key] = o;
            return o;
        }

        // ---- Items ----
        internal static bool GetItemIncluded(string avatarName, string outfitName, string itemName)
        {
            var memoKey = ("I", avatarName, outfitName, itemName);
            if (_boolMemo.TryGetValue(memoKey, out bool memo)) return memo;

            bool result;
            var o = GetOutfit(avatarName, outfitName);
            var ov = o.itemOverrides.FirstOrDefault(x => x.name == itemName);
            if (ov != null) result = ov.included;
            else
            {
                // Legacy per-preset override?
                string legacyKey = LEGACY_ITEM_PREFIX + avatarName + "_" + outfitName + "_" + itemName;
                if (EditorPrefs.HasKey(legacyKey))
                {
                    result = EditorPrefs.GetBool(legacyKey, false);

                }
                else
                    result = GetItemDefault(avatarName, itemName);
            }

            _boolMemo[memoKey] = result;
            return result;
        }

        internal static void SetItemIncluded(string avatarName, string outfitName, string itemName, bool included)
        {
            var o = GetOutfit(avatarName, outfitName);
            var ov = o.itemOverrides.FirstOrDefault(x => x.name == itemName);
            if (ov == null) o.itemOverrides.Add(new ItemOverride { name = itemName, included = included });
            else ov.included = included;
            _boolMemo[("I", avatarName, outfitName, itemName)] = included;
            Save();
        }

        internal static void SetItemsIncluded(string avatarName, string outfitName,
            IEnumerable<string> itemNames, bool included)
        {
            var o = GetOutfit(avatarName, outfitName);
            foreach (string itemName in itemNames.Where(n => !string.IsNullOrEmpty(n)).Distinct())
            {
                var ov = o.itemOverrides.FirstOrDefault(x => x.name == itemName);
                if (ov == null) o.itemOverrides.Add(new ItemOverride { name = itemName, included = included });
                else ov.included = included;
                _boolMemo[("I", avatarName, outfitName, itemName)] = included;
            }
            Save();
        }

        internal static bool GetItemDefault(string avatarName, string itemName)
        {
            var memoKey = ("D", avatarName, itemName, (string)null);
            if (_boolMemo.TryGetValue(memoKey, out bool memo)) return memo;

            bool result;
            var av = GetAvatar(avatarName);
            if (av.itemDefaults.Contains(itemName)) result = true;
            else if (av.itemDefaultsDecided.Contains(itemName)) result = false;
            else
            {
                // Legacy global default (old model was not per avatar)
                string legacyKey = LEGACY_ITEM_DEFAULT_PREFIX + itemName;
                result = EditorPrefs.HasKey(legacyKey) && EditorPrefs.GetBool(legacyKey, false);

            }

            _boolMemo[memoKey] = result;
            return result;
        }

        internal static void SetItemDefault(string avatarName, string itemName, bool included)
        {
            var av = GetAvatar(avatarName);
            if (!av.itemDefaultsDecided.Contains(itemName)) av.itemDefaultsDecided.Add(itemName);
            bool has = av.itemDefaults.Contains(itemName);
            if (included && !has) av.itemDefaults.Add(itemName);
            if (!included && has) av.itemDefaults.Remove(itemName);
            // A default change can affect every preset's effective value → drop all memos.
            _boolMemo.Clear();
            Save();
        }

        // ---- Last upload timestamps ----
        internal static void MarkUploaded(OutfitData o, string platform)
        {
            if (o == null) return;
            string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm",
                System.Globalization.CultureInfo.InvariantCulture);
            switch (platform)
            {
                case "Android": o.lastUploadAndroid = now; break;
                case "iOS":     o.lastUploadIOS     = now; break;
                default:        o.lastUploadWindows = now; break;
            }
            Save();
        }

        // ---- Export / import (whole file, for backups & transferring to another project) ----
        internal static string ExportRaw()
        {
            try
            {
                return File.Exists(FilePath)
                    ? File.ReadAllText(FilePath)
                    : JsonUtility.ToJson(new Root(), true);
            }
            catch { return null; }
        }

        internal static void ValidateImport(string json) => ParseRoot(json);
        internal static bool ImportRaw(string json)
        {
            var parsed = ParseRoot(json);
            var existing = Data; // Refuse import over unrecoverable state.
            var serialized = JsonUtility.ToJson(parsed, true);
            WriteAtomically(FilePath, serialized);
            _root = parsed; _durable = serialized; ClearCaches();
            return true;
        }

        // ---- FaceEmo ----
        internal static string GetFaceEmoName(string avatarName, string outfitName) =>
            GetOutfit(avatarName, outfitName).faceEmoName ?? "";

        internal static void SetFaceEmoName(string avatarName, string outfitName, string value)
        {
            GetOutfit(avatarName, outfitName).faceEmoName = value ?? "";
            Save();
        }

        // ============================================================
        //  Legacy EditorPrefs migration (per preset, runs once)
        // ============================================================
        private static void MigrateLegacyOutfit(string avatarName, OutfitData o)
        {
            try
            {
                string prefKey = LEGACY_PREFIX + avatarName + "_" + o.name;

                if (EditorPrefs.HasKey(prefKey))
                    o.blueprintId = EditorPrefs.GetString(prefKey, "");
                if (EditorPrefs.HasKey(prefKey + "_batch"))
                    o.includeInBatch = EditorPrefs.GetBool(prefKey + "_batch", true);

                // Platform toggles were additionally scoped by a project hash
                string projKey = Hash128.Compute(Application.dataPath).ToString();
                if (EditorPrefs.HasKey(prefKey + "_" + projKey + "_Win"))
                    o.buildWindows = EditorPrefs.GetBool(prefKey + "_" + projKey + "_Win", true);
                if (EditorPrefs.HasKey(prefKey + "_" + projKey + "_And"))
                    o.buildAndroid = EditorPrefs.GetBool(prefKey + "_" + projKey + "_And", false);
                if (EditorPrefs.HasKey(prefKey + "_" + projKey + "_iOS"))
                    o.buildIOS = EditorPrefs.GetBool(prefKey + "_" + projKey + "_iOS", false);

                // Blendshape overrides ("keys" list + one float per name)
                string keys = EditorPrefs.GetString(prefKey + "_BS_keys", "");
                if (!string.IsNullOrEmpty(keys))
                {
                    foreach (var k in keys.Split(';'))
                    {
                        if (string.IsNullOrEmpty(k)) continue;
                        o.blendShapes.Add(new BlendShapeOverride
                        {
                            name  = k,
                            value = EditorPrefs.GetFloat(prefKey + "_BS_" + k, 0f)
                        });
                    }
                }

                // FaceEmo capture name
                string feKey = LEGACY_FACEEMO_PREFIX + avatarName + "_" + o.name;
                if (EditorPrefs.HasKey(feKey))
                    o.faceEmoName = EditorPrefs.GetString(feKey, "");

                if (!string.IsNullOrEmpty(o.blueprintId) || o.blendShapes.Count > 0 || !string.IsNullOrEmpty(o.faceEmoName))
                    Debug.Log($"[OutfitBatchUploader] Migrated legacy settings for '{avatarName}/{o.name}' into ProjectSettings/{FILE_NAME}.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[OutfitBatchUploader] Legacy settings migration for '{avatarName}/{o.name}' failed: {ex.Message}");
            }
        }
    }
}
