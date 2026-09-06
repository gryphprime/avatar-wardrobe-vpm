using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace OutfitToggleGenerator
{
    internal enum WardrobeAssetKind
    {
        Avatar,
        Outfit,
        Candidate,
        Ignored,
    }

    internal enum WardrobeCompatibilityState
    {
        Compatible,
        ProbablyCompatible,
        Untested,
        Incompatible,
    }

    [Serializable]
    internal sealed class WardrobeAssetRecord
    {
        public string guid;
        public string assetPath;
        public string displayName;
        public WardrobeAssetKind kind;
        public float confidence;
        public bool hasAvatarDescriptor;
        public bool hasHumanoidAnimator;
        public bool hasMergeArmature;
        public bool hasOutfitRoot;
        public bool hasObjectToggle;
        public bool hasMenuItem;
        public int rendererCount;
        public int skinnedRendererCount;
        public List<string> meshIds = new List<string>();
        public List<string> materialIds = new List<string>();
        public List<string> textureIds = new List<string>();
        public List<string> boneNames = new List<string>();
        public List<string> dependencies = new List<string>();
        public string dependencyFingerprint;
        public List<string> rendererNames = new List<string>();
        public string familyId;
        public string familyName;
        public string variantName;
        // Extracted by the external indexer from prefab text (catalog v3+):
        // UV-island part labels, dynamics counts, material palette, colorway.
        // Null on pre-v3 records until a full reindex; readers must null-check.
        public List<string> partGroups = new List<string>();
        public int physBoneCount;
        public int contactCount;
        public List<string> materialNames = new List<string>();
        public string colorway;
        // Hair-vs-outfit verdict from part-name scoring (indexer v4+).
        // "hair", "outfit", or "unknown"; empty on pre-v4 records.
        public string category;
        public float categoryConfidence;
        // Written by the external indexer and preserved verbatim on save, so its
        // incremental runs keep working. Optional for hand-made records.
        public string sourceModified;
        public string sourceSize;
        public List<string> sourceGuids = new List<string>();
        public bool modMaterials;
        public bool modBones;
        public bool hasModelMods;
    }

    [Serializable]
    internal sealed class WardrobeExternalProgress
    {
        public int done;
        public int total;
        public bool running;
        public int pid;
        public string phase;
    }

    internal sealed class WardrobeFamily
    {
        public string id;
        public string displayName;
        public List<WardrobeAssetRecord> variants = new List<WardrobeAssetRecord>();
    }

    internal sealed class WardrobeCompatibility
    {
        public WardrobeCompatibilityState state;
        public bool compatibleOverride;
        public float boneOverlap;
        public string explanation;
    }

    internal sealed class WardrobeInstallResult
    {
        public bool success;
        public bool alreadyInstalled;
        public bool setupApplied;
        public GameObject instance;
        public string message;

        public static WardrobeInstallResult Failure(string message)
        {
            return new WardrobeInstallResult { message = message };
        }
    }

    [Serializable]
    internal sealed class WardrobeCatalogCache
    {
        public int version;
        public List<WardrobeAssetRecord> records = new List<WardrobeAssetRecord>();
        public List<string> dirtyPaths = new List<string>();
    }

    [Serializable]
    internal sealed class WardrobeOverridesFile
    {
        public List<WardrobeAssetOverride> entries = new List<WardrobeAssetOverride>();
        public List<WardrobeAvatarOverride> avatarOverrides = new List<WardrobeAvatarOverride>();
    }

    [Serializable]
    internal sealed class WardrobeAvatarOverride
    {
        public string sourceGuid;
        public string avatarGuid;
    }

    [Serializable]
    internal sealed class WardrobeAssetOverride
    {
        public string guid;
        // -1 means automatic classification.
        public int kind = -1;
        public string familyName;
        public string variantName;
        // Missing in older override files defaults to automatic compatibility.
        public bool compatibleOverride;
    }

    internal static class AvatarWardrobeCatalog
    {
        private const int CacheVersion = 4;
        // Token sets live in WardrobeText (generic/variant) and WardrobeClassify
        // (hair/outfit/gimmick): single source of truth, no duplication.


        private static WardrobeCatalogCache cache;
        private static WardrobeOverridesFile overrides;
        private static bool cacheLoaded;
        private static bool overridesLoaded;

        private static int overridesVersion;
        private static System.Diagnostics.Process externalProcess;
        private static DateTime loadedCatalogStamp = DateTime.MinValue;
        private static int catalogEpoch;

        internal static int CatalogEpoch
        {
            get { return catalogEpoch; }
        }

        private static List<WardrobeAssetRecord> avatarCache;
        private static Dictionary<string, WardrobeAssetRecord> guidCache;
        private static int lookupCacheEpoch = -1;
        private static int lookupCacheOverrides = -1;

        // Avatar/guid lookups run per outfit per repaint; scanning 13k records
        // each time froze filter switches. Rebuilt only when the catalog or
        // overrides change.
        private static void EnsureLookupCaches()
        {
            LoadCache();
            if (lookupCacheEpoch == catalogEpoch && lookupCacheOverrides == overridesVersion &&
                avatarCache != null && guidCache != null)
                return;
            lookupCacheEpoch = catalogEpoch;
            lookupCacheOverrides = overridesVersion;
            avatarCache = cache.records
                .Where(record => EffectiveKind(record) == WardrobeAssetKind.Avatar)
                .OrderBy(record => record.displayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            guidCache = new Dictionary<string, WardrobeAssetRecord>(StringComparer.Ordinal);
            foreach (var record in cache.records)
            {
                if (record == null || string.IsNullOrEmpty(record.guid)) continue;
                if (!guidCache.ContainsKey(record.guid)) guidCache.Add(record.guid, record);
            }
        }

        private static string ScanJournalPath
        {
            get { return Path.Combine(CacheDirectory, "scan.log"); }
        }

        internal static string LastScannedPath()
        {
            try
            {
                if (!File.Exists(ScanJournalPath)) return null;
                using (var reader = new StreamReader(File.Open(ScanJournalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
                {
                    string line = null, last = null;
                    while ((line = reader.ReadLine()) != null) last = line;
                    return last;
                }
            }
            catch (Exception) { return null; }
        }

        private static string ProjectRoot
        {
            get { return Directory.GetParent(Application.dataPath).FullName; }
        }

        private static string CacheDirectory
        {
            get { return Path.Combine(ProjectRoot, "Library", "AvatarWardrobe"); }
        }

        private static string CachePath
        {
            get { return Path.Combine(CacheDirectory, "catalog.json"); }
        }

        private static string OverridesPath
        {
            get { return Path.Combine(ProjectRoot, "ProjectSettings", "AvatarWardrobeOverrides.json"); }
        }

        internal static IReadOnlyList<WardrobeAssetRecord> Records
        {
            get
            {
                EnsureIndexed();
                return cache.records;
            }
        }

        internal static int OverridesVersion
        {
            get { return overridesVersion; }
        }

        [Serializable]
        private sealed class ScanReceipt { public string dirtyToken; public bool success; }

        // Only the indexer can acknowledge a dirty generation. Prefab timestamps alone
        // say nothing about a changed material, a removed dependency, or an interrupted scan.
        internal static void ReconcileDirty()
        {
            LoadCache();
            if (cache.dirtyPaths.Count == 0) return;
            try
            {
                var receiptPath = Path.Combine(CacheDirectory, "scan-result.json");
                if (!File.Exists(receiptPath) || !File.Exists(DirtySidecarPath)) return;
                var receipt = JsonUtility.FromJson<ScanReceipt>(File.ReadAllText(receiptPath));
                if (receipt == null || !receipt.success || string.IsNullOrEmpty(receipt.dirtyToken) ||
                    !string.Equals(receipt.dirtyToken, WardrobeAtomicFile.HashFile(DirtySidecarPath), StringComparison.Ordinal)) return;
                File.Delete(DirtySidecarPath);
                cache.dirtyPaths.Clear();
            }
            catch (IOException) { /* A concurrent atomic replacement will be retried next poll. */ }
        }

        internal static int DirtyCount
        {
            get
            {
                LoadCache();
                return cache.dirtyPaths.Count;
            }
        }

        private static double nextIndexRecovery;
        private static string RebuildMarker => Path.Combine(CacheDirectory, "rebuild.pending");

        internal static bool ExternalRunning
        {
            get
            {
                try
                {
                    if (externalProcess != null && !externalProcess.HasExited) return true;
                    // Domain reloads discard the Process object, not the Python
                    // process. Recover its handle from the owner's progress file.
                    var path = Path.Combine(CacheDirectory, "progress.json");
                    if (!File.Exists(path)) return false;
                    var progress = JsonUtility.FromJson<WardrobeExternalProgress>(File.ReadAllText(path));
                    if (progress == null || !progress.running || progress.pid <= 0) return false;
                    var process = System.Diagnostics.Process.GetProcessById(progress.pid);
                    if (process.HasExited || process.ProcessName.IndexOf("python", StringComparison.OrdinalIgnoreCase) < 0 ||
                        process.StartTime.ToUniversalTime() > File.GetLastWriteTimeUtc(path))
                    { process.Dispose(); return false; }
                    externalProcess?.Dispose();
                    externalProcess = process;
                    return true;
                }
                catch (Exception) { return false; }
            }
        }

        internal static void RecoverMissingIndex()
        {
            if (File.Exists(CachePath) && !File.Exists(RebuildMarker)) return;
            if (ExternalRunning || EditorApplication.timeSinceStartup < nextIndexRecovery) return;
            nextIndexRecovery = EditorApplication.timeSinceStartup + 30;
            Directory.CreateDirectory(CacheDirectory);
            File.WriteAllText(RebuildMarker, "full");
            RunExternalIndexer(true);
        }

        internal static WardrobeExternalProgress ExternalProgress()
        {
            var progress = new WardrobeExternalProgress();
            try
            {
                var path = Path.Combine(CacheDirectory, "progress.json");
                if (!File.Exists(path)) return progress;
                var data = JsonUtility.FromJson<WardrobeExternalProgress>(File.ReadAllText(path));
                if (data == null) return progress;
                // A run that died without finishing leaves a stale file behind.
                if (data.running && !ExternalRunning) data.running = false;
                return data;
            }
            catch (Exception) { return progress; }
        }

        internal static string LastIndexError { get; private set; }

        internal static bool RunExternalIndexer(bool full)
        {
            LoadCache();
            if (ExternalRunning)
            {
                LastIndexError = WardrobeStrings.T("msg.indexrunning");
                return false;
            }
            var root = ProjectRoot;
            var indexerRoot = WardrobePackagePaths.File("Editor/WardrobeIndexer");
            var script = Path.Combine(indexerRoot, "wardrobe_index.py");
            if (!File.Exists(script))
            {
                LastIndexError = WardrobeStrings.T("msg.nondexer", script);
                return false;
            }
            try
            {
                var start = new System.Diagnostics.ProcessStartInfo();
                var bundledPython = Path.Combine(indexerRoot, "Runtime", "Windows-x64", "python.exe");
                start.FileName = Application.platform == RuntimePlatform.WindowsEditor && File.Exists(bundledPython)
                    ? bundledPython
                    : "python3";
                start.Arguments = "\"" + script + "\" --project \"" + root + "\"" +
                                  (full ? " --full" : string.Empty);
                start.WorkingDirectory = root;
                start.UseShellExecute = false;
                start.CreateNoWindow = true;
                if (externalProcess != null) externalProcess.Dispose();
                externalProcess = System.Diagnostics.Process.Start(start);
                LastIndexError = null;
                return externalProcess != null;
            }
            catch (Exception exception)
            {
                LastIndexError = WardrobeStrings.T("msg.nopython", exception.Message, root);
                Debug.LogError(LastIndexError);
                return false;
            }
        }

        internal static void WipeCache()
        {
            CancelExternalIndexer();
            if (Directory.Exists(CacheDirectory))
                foreach (var path in Directory.GetFileSystemEntries(CacheDirectory))
                {
                    if (Path.GetFileName(path).StartsWith("wardrobe.log", StringComparison.Ordinal)) continue;
                    if (Directory.Exists(path)) Directory.Delete(path, true);
                    else File.Delete(path);
                }
            cache = null;
            cacheLoaded = false;
            loadedCatalogStamp = DateTime.MinValue;
            LoadCache();
            catalogEpoch++;
            File.WriteAllText(RebuildMarker, "full");
            nextIndexRecovery = 0;
            LastIndexError = null;
            WardrobeLog.Write("cache", "Cleared catalog and thumbnail caches");
        }

        internal static void CancelExternalIndexer()
        {
            try
            {
                if (ExternalRunning)
                { externalProcess.Kill(); externalProcess.WaitForExit(); }
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Avatar Wardrobe could not stop the indexer: " + exception.Message);
            }
            finally { if (externalProcess != null) externalProcess.Dispose(); externalProcess = null; }
        }

        // Reloads records written by the external indexer. Returns true when the
        // file changed so the window can drop its caches.
        internal static bool PollExternalUpdates()
        {
            LoadCache();
            DateTime stamp;
            try
            {
                if (!File.Exists(CachePath))
                {
                    if (loadedCatalogStamp == DateTime.MinValue) return false;
                    cacheLoaded = false;
                    loadedCatalogStamp = DateTime.MinValue;
                    LoadCache();
                    catalogEpoch++;
                    return true;
                }
                stamp = File.GetLastWriteTimeUtc(CachePath);
            }
            catch (Exception) { return false; }
            if (stamp == loadedCatalogStamp) return false;
            cacheLoaded = false;
            LoadCache();
            loadedCatalogStamp = stamp;
            catalogEpoch++;
            ReconcileDirty();
            // Derived catalog has a single writer (Python). Never serialize its records
            // back through JsonUtility: it drops fields it does not know about.
            return true;
        }

        internal static void EnsureIndexed()
        {
            LoadCache();
        }

        internal static List<WardrobeAssetRecord> Avatars()
        {
            EnsureLookupCaches();
            return avatarCache;
        }

        internal static WardrobeAssetRecord GetRecord(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return null;
            if (guid.StartsWith("base:", StringComparison.Ordinal))
                return CanonicalBaseChoices().FirstOrDefault(record => record.guid == guid);
            EnsureLookupCaches();
            WardrobeAssetRecord record;
            return guidCache.TryGetValue(guid, out record) ? record : null;
        }

        [Serializable]
        private sealed class WardrobeBaseAvatarEntry
        {
            public string name;
            public string[] aliases;
            public int count;
        }

        [Serializable]
        private sealed class WardrobeBaseAvatarFile
        {
            public string[] stopwords;
            public WardrobeBaseAvatarEntry[] avatars;
        }

        private sealed class WardrobeBaseAvatar
        {
            public string name;
            public HashSet<string> aliases;
            public int count;
        }

        private static List<WardrobeBaseAvatar> baseAvatarCache;
        private static DateTime baseAvatarFileStamp = DateTime.MinValue;
        private static double baseAvatarLastStat;
        // Hot-path memos for the filter scan (runs per variant over ~10k
        // records): base-name matches, variant labels, and the avatar side
        // of compatibility. Cleared whenever their inputs change.
        private static readonly Dictionary<string, string> baseMatchMemo =
            new Dictionary<string, string>();
        private static DateTime baseMatchMemoStamp = DateTime.MinValue;
        private static readonly Dictionary<string, string> displayVariantMemo =
            new Dictionary<string, string>();
        private static int displayVariantMemoEpoch = -1;
        private static int displayVariantMemoOverrides = -1;

        internal static long BaseAvatarStamp
        {
            get
            {
                BaseAvatarEntries();
                return baseAvatarFileStamp.Ticks;
            }
        }

        private static List<WardrobeBaseAvatar> BaseAvatarEntries()
        {
            try
            {
                // Stat the file at most once a second; matching runs per card.
                var now = EditorApplication.timeSinceStartup;
                if (baseAvatarCache != null && now - baseAvatarLastStat < 1.0) return baseAvatarCache;
                baseAvatarLastStat = now;
                var path = WardrobePackagePaths.File("Editor/AvatarBaseNames.json");
                var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
                if (baseAvatarCache != null && stamp == baseAvatarFileStamp) return baseAvatarCache;
                baseAvatarFileStamp = stamp;
                var parsed = new List<WardrobeBaseAvatar>();
                if (File.Exists(path))
                {
                    var file = JsonUtility.FromJson<WardrobeBaseAvatarFile>(File.ReadAllText(path));
                    var stops = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (file != null && file.stopwords != null)
                        foreach (var stop in file.stopwords)
                            if (!string.IsNullOrEmpty(stop)) stops.Add(stop);
                    if (file != null && file.avatars != null)
                        foreach (var entry in file.avatars)
                        {
                            if (entry == null || string.IsNullOrWhiteSpace(entry.name)) continue;
                            var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var word in Tokenize(entry.name))
                                if (!stops.Contains(word)) aliases.Add(word);
                            if (entry.aliases != null)
                                foreach (var extra in entry.aliases)
                                {
                                    var clean = string.IsNullOrEmpty(extra)
                                        ? string.Empty
                                        : extra.Trim().ToLowerInvariant();
                                    if (!string.IsNullOrEmpty(clean)) aliases.Add(clean);
                                }
                            if (aliases.Count == 0)
                                foreach (var word in Tokenize(entry.name))
                                    aliases.Add(word);
                            parsed.Add(new WardrobeBaseAvatar
                            {
                                name = entry.name.Trim(),
                                aliases = aliases,
                                count = entry.count,
                            });
                        }
                }
                baseAvatarCache = parsed;
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Avatar Wardrobe could not read its base avatar list: " + exception.Message);
                baseAvatarCache = new List<WardrobeBaseAvatar>();
            }
            return baseAvatarCache;
        }

        // Matches text against the canonical base list ("ShinanoS6 (Main) (3)"
        // -> "Shinano"). Whole-word tokens only; most aliases, then highest
        // count, then alphabetical. Returns null when nothing matches.
        internal static string MatchBaseAvatar(string text)
        {
            var entries = BaseAvatarEntries();
            if (entries.Count == 0 || string.IsNullOrEmpty(text)) return null;
            if (baseMatchMemoStamp != baseAvatarFileStamp)
            {
                baseMatchMemoStamp = baseAvatarFileStamp;
                baseMatchMemo.Clear();
            }
            string memoized;
            if (baseMatchMemo.TryGetValue(text, out memoized)) return memoized;
            var tokens = Tokenize(text);
            string best = null;
            var bestScore = 0;
            var bestCount = -1;
            foreach (var entry in entries)
            {
                var score = 0;
                foreach (var alias in entry.aliases)
                    if (tokens.Contains(alias)) score++;
                if (score == 0) continue;
                if (score > bestScore ||
                    (score == bestScore && (entry.count > bestCount ||
                     (entry.count == bestCount && string.Compare(entry.name, best, StringComparison.Ordinal) < 0))))
                {
                    best = entry.name;
                    bestScore = score;
                    bestCount = entry.count;
                }
            }
            if (baseMatchMemo.Count >= 40000) baseMatchMemo.Clear();
            baseMatchMemo[text] = best;
            return best;
        }

        internal static List<WardrobeAssetRecord> CanonicalBaseChoices()
        {
            return BaseAvatarEntries().Select(entry => new WardrobeAssetRecord {
                guid = "base:" + entry.name,
                displayName = entry.name,
                assetPath = "",
                kind = WardrobeAssetKind.Avatar,
                hasAvatarDescriptor = true
            }).ToList();
        }

        internal static string AvatarSourceGuid(VRCAvatarDescriptor avatar)
        {
            if (avatar == null) return "";
            var path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(avatar.gameObject);
            return string.IsNullOrEmpty(path) ? "" : AssetDatabase.AssetPathToGUID(path);
        }

        internal static string AvatarOverrideKey(VRCAvatarDescriptor avatar)
        {
            var prefab = AvatarSourceGuid(avatar);
            if (!string.IsNullOrEmpty(prefab)) return prefab;
            if (avatar == null || string.IsNullOrEmpty(avatar.gameObject.scene.path)) return "";
            return "scene-object:" + GlobalObjectId.GetGlobalObjectIdSlow(avatar.gameObject);
        }

        internal static string AvatarOverrideGuid(VRCAvatarDescriptor avatar)
        {
            LoadOverrides();
            var source = AvatarOverrideKey(avatar);
            var saved = overrides.avatarOverrides.FirstOrDefault(entry => entry.sourceGuid == source)?.avatarGuid ?? "";
            if (string.IsNullOrEmpty(saved) || saved.StartsWith("base:", StringComparison.Ordinal)) return saved;
            // Older selections stored catalog prefab GUIDs. Resolve them to the
            // predefined base without requiring the user to select again.
            var old = GetRecord(saved);
            var name = old == null ? null : MatchBaseAvatar(old.displayName + " " + old.assetPath);
            return string.IsNullOrEmpty(name) ? saved : "base:" + name;
        }

        internal static void SetAvatarOverride(VRCAvatarDescriptor avatar, string guid)
        {
            LoadOverrides();
            var source = AvatarOverrideKey(avatar);
            if (string.IsNullOrEmpty(source)) throw new InvalidOperationException("Select an avatar and save its scene before choosing a base avatar.");
            if (!string.IsNullOrEmpty(guid) && !CanonicalBaseChoices().Any(record => record.guid == guid))
                throw new InvalidOperationException("Select a base from the predefined avatar list.");
            var previous = overrides.avatarOverrides.ToList();
            overrides.avatarOverrides.RemoveAll(entry => entry.sourceGuid == source);
            if (!string.IsNullOrEmpty(guid)) overrides.avatarOverrides.Add(new WardrobeAvatarOverride { sourceGuid = source, avatarGuid = guid });
            try { SaveOverrides(); }
            catch { overrides.avatarOverrides = previous; throw; }
            overridesVersion++;
        }

        internal static WardrobeAssetRecord GetAvatarRecord(VRCAvatarDescriptor avatar)
        {
            if (avatar == null) return null;
            var chosen = GetRecord(AvatarOverrideGuid(avatar));
            if (chosen != null && EffectiveKind(chosen) == WardrobeAssetKind.Avatar) return chosen;
            var sourcePaths = new List<string>();
            var nearestPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(avatar.gameObject);
            if (!string.IsNullOrEmpty(nearestPath)) sourcePaths.Add(nearestPath);
            var visited = new HashSet<GameObject>();
            var source = PrefabUtility.GetCorrespondingObjectFromSource(avatar.gameObject);
            while (source != null && visited.Add(source))
            {
                var path = AssetDatabase.GetAssetPath(source);
                if (!string.IsNullOrEmpty(path) && !sourcePaths.Contains(path)) sourcePaths.Add(path);
                source = PrefabUtility.GetCorrespondingObjectFromSource(source);
            }
            // Prefer stable asset GUIDs throughout the variant/base chain over
            // any display-name heuristic. Asset paths are resolved live by Unity.
            foreach (var path in sourcePaths)
            {
                var record = GetRecord(AssetDatabase.AssetPathToGUID(path));
                if (record != null && EffectiveKind(record) == WardrobeAssetKind.Avatar) return record;
            }
            foreach (var path in sourcePaths)
            {
                var record = FindAvatarByName(Path.GetFileNameWithoutExtension(path)) ?? FindAvatarByName(path);
                if (record != null) return record;
            }
            // Unpacked or manually assembled avatars have no prefab link.
            return FindAvatarByName(avatar.gameObject.name);
        }

        private static WardrobeAssetRecord FindAvatarByName(string name)
        {
            var direct = Avatars().FirstOrDefault(candidate =>
                string.Equals(candidate.displayName, name, StringComparison.OrdinalIgnoreCase));
            if (direct != null) return direct;

            // Strip Unity-generated suffixes: "ShinanoS6 (Main) (3)" -> "ShinanoS6".
            var stripped = name;
            string shrunk;
            do
            {
                shrunk = StripTrailingBracketSuffix(stripped);
                if (shrunk != null) stripped = shrunk;
            } while (shrunk != null);
            if (!string.Equals(stripped, name, StringComparison.Ordinal))
            {
                direct = Avatars().FirstOrDefault(candidate =>
                    string.Equals(candidate.displayName, stripped, StringComparison.OrdinalIgnoreCase));
                if (direct != null) return direct;
            }

            // Canonical base list ("ShinanoS6 (Main) (3)" -> "Shinano").
            // Variants can never hijack this: only base names are candidates.
            var baseName = MatchBaseAvatar(name);
            if (string.IsNullOrEmpty(baseName)) return null;
            direct = Avatars().FirstOrDefault(candidate =>
                string.Equals(candidate.displayName, baseName, StringComparison.OrdinalIgnoreCase));
            if (direct != null) return direct;
            return Avatars().FirstOrDefault(candidate =>
                string.Equals(
                    MatchBaseAvatar(candidate.displayName + " " + Path.GetFileNameWithoutExtension(candidate.assetPath)),
                    baseName,
                    StringComparison.Ordinal));
        }

        private static string StripTrailingBracketSuffix(string name)
        {
            if (string.IsNullOrEmpty(name) || !name.EndsWith(")", StringComparison.Ordinal)) return null;
            var open = name.LastIndexOf(" (", StringComparison.Ordinal);
            if (open < 0) return null;
            return name.Substring(0, open);
        }

        internal static List<WardrobeFamily> Families()
        {
            EnsureIndexed();
            LoadOverrides();
            var groups = new Dictionary<string, WardrobeFamily>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in cache.records.Where(record => IsBrowsablePrefab(record) && EffectiveKind(record) == WardrobeAssetKind.Outfit))
            {
                var assetOverride = GetOverride(record.guid);
                var manualFamily = assetOverride == null ? null : assetOverride.familyName;
                var familyId = string.IsNullOrWhiteSpace(manualFamily)
                    ? record.familyId
                    : "manual:" + Normalize(manualFamily);
                if (string.IsNullOrEmpty(familyId)) familyId = "asset:" + record.guid;

                WardrobeFamily family;
                if (!groups.TryGetValue(familyId, out family))
                {
                    family = new WardrobeFamily
                    {
                        id = familyId,
                        displayName = string.IsNullOrWhiteSpace(manualFamily)
                            ? (string.IsNullOrWhiteSpace(record.familyName) ? record.displayName : record.familyName)
                            : manualFamily.Trim(),
                    };
                    groups.Add(familyId, family);
                }
                family.variants.Add(record);
            }

            foreach (var family in groups.Values)
            {
                family.variants = family.variants
                    .OrderBy(DisplayVariant, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(record => record.assetPath, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            return groups.Values
                .OrderBy(family => family.displayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // Classification is advisory: only a descriptor excludes a prefab from browsing.
        internal static bool IsBrowsablePrefab(WardrobeAssetRecord record)
        {
            return record != null && !record.hasAvatarDescriptor;
        }

        internal static List<WardrobeFamily> CandidateFamilies()
        {
            EnsureIndexed();
            return cache.records
                .Where(record => IsBrowsablePrefab(record) && EffectiveKind(record) != WardrobeAssetKind.Outfit)
                .OrderBy(record => record.displayName, StringComparer.OrdinalIgnoreCase)
                .Select(record => new WardrobeFamily
                {
                    id = "candidate:" + record.guid,
                    displayName = record.displayName,
                    variants = new List<WardrobeAssetRecord> { record },
                })
                .ToList();
        }

        // Avatar-side inputs never change during a scan, but Compatibility
        // runs per variant: resolve them once per avatar/catalog instead of
        // ~10k times (record lookup, base-name match, bone set).
        private sealed class WardrobeCompatScope
        {
            public WardrobeAssetRecord record;
            public string baseName;
            public HashSet<string> bones;
        }

        private static WardrobeCompatScope compatScope;
        private static string compatScopeKey;

        internal static WardrobeCompatibility Compatibility(WardrobeAssetRecord outfit, string avatarGuid)
        {
            LoadOverrides();
            if (outfit != null && GetOverride(outfit.guid)?.compatibleOverride == true)
                return new WardrobeCompatibility
                {
                    state = WardrobeCompatibilityState.Compatible,
                    compatibleOverride = true,
                    explanation = WardrobeStrings.T("compat.override"),
                };
            var scopeKey = (avatarGuid ?? string.Empty) + "|" + catalogEpoch + "|" + overridesVersion;
            if (compatScope == null || !string.Equals(compatScopeKey, scopeKey, StringComparison.Ordinal))
            {
                var avatar = GetRecord(avatarGuid);
                compatScope = new WardrobeCompatScope
                {
                    record = avatar,
                    baseName = avatar == null
                        ? null
                        : MatchBaseAvatar(avatar.displayName + " " + Path.GetFileNameWithoutExtension(avatar.assetPath)),
                    bones = avatar == null || avatar.boneNames == null
                        ? null
                        : new HashSet<string>(
                            avatar.boneNames.Where(name => !string.IsNullOrEmpty(name)),
                            StringComparer.OrdinalIgnoreCase),
                };
                compatScopeKey = scopeKey;
            }
            var avatarRecord = compatScope.record;
            if (outfit == null || avatarRecord == null)
                return new WardrobeCompatibility
                {
                    state = WardrobeCompatibilityState.Untested,
                    explanation = WardrobeStrings.T("compat.sel"),
                };

            var overlap = BoneOverlap(outfit.boneNames, compatScope.bones);
            var outfitBase = MatchBaseAvatar(outfit.displayName + " " + outfit.assetPath);
            var avatarBase = compatScope.baseName;
            if (!string.IsNullOrEmpty(outfitBase) && !string.IsNullOrEmpty(avatarBase))
            {
                if (!string.Equals(outfitBase, avatarBase, StringComparison.Ordinal))
                    return new WardrobeCompatibility
                    {
                        state = WardrobeCompatibilityState.Incompatible,
                        boneOverlap = overlap,
                        explanation = WardrobeStrings.T("compat.designed", outfitBase),
                    };

                if (outfit.hasMergeArmature || outfit.hasOutfitRoot || overlap >= 0.35f)
                    return new WardrobeCompatibility
                    {
                        state = WardrobeCompatibilityState.Compatible,
                        boneOverlap = overlap,
                        explanation = WardrobeStrings.T("compat.setup", avatarBase),
                    };

                return new WardrobeCompatibility
                {
                    state = WardrobeCompatibilityState.ProbablyCompatible,
                    boneOverlap = overlap,
                    explanation = WardrobeStrings.T("compat.limited", avatarBase),
                };
            }

            if (overlap >= 0.70f)
                return new WardrobeCompatibility
                {
                    state = WardrobeCompatibilityState.ProbablyCompatible,
                    boneOverlap = overlap,
                    explanation = WardrobeStrings.T("compat.overlap"),
                };

            return new WardrobeCompatibility
            {
                state = WardrobeCompatibilityState.Untested,
                boneOverlap = overlap,
                explanation = WardrobeStrings.T("compat.none"),
            };
        }

        internal static WardrobeCompatibility BestCompatibility(WardrobeFamily family, string avatarGuid)
        {
            if (family == null || family.variants.Count == 0)
                return new WardrobeCompatibility
                {
                    state = WardrobeCompatibilityState.Untested,
                    explanation = WardrobeStrings.T("compat.novariant"),
                };

            return family.variants
                .Select(variant => Compatibility(variant, avatarGuid))
                .OrderByDescending(result => CompatibilityRank(result.state))
                .ThenByDescending(result => result.compatibleOverride)
                .First();
        }

        internal static string DisplayVariant(WardrobeAssetRecord record)
        {
            LoadOverrides();
            var memoKey = record == null ? null : record.guid;
            var memoOk = !string.IsNullOrEmpty(memoKey) &&
                         displayVariantMemoEpoch == catalogEpoch &&
                         displayVariantMemoOverrides == overridesVersion;
            if (memoOk)
            {
                string memoized;
                if (displayVariantMemo.TryGetValue(memoKey, out memoized)) return memoized;
            }
            var assetOverride = record == null ? null : GetOverride(record.guid);
            string result;
            if (assetOverride != null && !string.IsNullOrWhiteSpace(assetOverride.variantName))
                result = assetOverride.variantName.Trim();
            else
            {
                var label = string.IsNullOrWhiteSpace(record == null ? null : record.variantName)
                    ? "Default" : record.variantName.Trim();
                result = label;
                if (record != null && (string.Equals(label, "Default", StringComparison.OrdinalIgnoreCase) ||
                                       Regex.IsMatch(label, @"^Variant\s+\d+$", RegexOptions.IgnoreCase)))
                {
                    var inferred = InferredVariantName(record);
                    if (!string.IsNullOrEmpty(inferred)) result = inferred;
                }
            }
            if (!string.IsNullOrEmpty(memoKey))
            {
                if (!memoOk)
                {
                    displayVariantMemo.Clear();
                    displayVariantMemoEpoch = catalogEpoch;
                    displayVariantMemoOverrides = overridesVersion;
                }
                if (displayVariantMemo.Count >= 40000) displayVariantMemo.Clear();
                displayVariantMemo[memoKey] = result;
            }
            return result;
        }

        private static string InferredVariantName(WardrobeAssetRecord record)
        {
            if (record == null) return null;
            if (!string.IsNullOrWhiteSpace(record.colorway)) return Humanize(record.colorway);
            var fileName = Path.GetFileNameWithoutExtension(record.assetPath);
            var token = Tokenize(fileName).FirstOrDefault(word =>
                WardrobeText.VariantTokens.Contains(word) && !string.Equals(word, "default", StringComparison.OrdinalIgnoreCase));
            return string.IsNullOrEmpty(token) ? null : Humanize(token);
        }

        internal static WardrobeAssetKind EffectiveKind(WardrobeAssetRecord record)
        {
            LoadOverrides();
            var assetOverride = record == null ? null : GetOverride(record.guid);
            return assetOverride != null && assetOverride.kind >= 0
                ? (WardrobeAssetKind)assetOverride.kind
                : record == null ? WardrobeAssetKind.Ignored : record.kind;
        }

        internal static WardrobeAssetOverride OverrideFor(string guid)
        {
            LoadOverrides();
            return GetOverride(guid);
        }

        internal static void SetOverride(
            string guid,
            int kind,
            string familyName,
            string variantName,
            bool? compatibleOverride = null)
        {
            if (string.IsNullOrEmpty(guid)) return;
            LoadOverrides();
            var previous = overrides;
            overrides = JsonUtility.FromJson<WardrobeOverridesFile>(JsonUtility.ToJson(previous));
            try
            {
                var assetOverride = GetOverride(guid);
                if (assetOverride == null)
                {
                    assetOverride = new WardrobeAssetOverride { guid = guid };
                    overrides.entries.Add(assetOverride);
                }
                if (compatibleOverride.HasValue) assetOverride.compatibleOverride = compatibleOverride.Value;
                assetOverride.kind = kind;
                assetOverride.familyName = string.IsNullOrWhiteSpace(familyName) ? null : familyName.Trim();
                assetOverride.variantName = string.IsNullOrWhiteSpace(variantName) ? null : variantName.Trim();
                if (assetOverride.kind < 0 && string.IsNullOrEmpty(assetOverride.familyName) &&
                    string.IsNullOrEmpty(assetOverride.variantName) && !assetOverride.compatibleOverride) overrides.entries.Remove(assetOverride);
                SaveOverrides();
                overridesVersion++;
            }
            catch { overrides = previous; throw; }
        }

        internal static bool TryFindInstalled(
            VRCAvatarDescriptor avatar,
            WardrobeAssetRecord outfit,
            out GameObject instance)
        {
            instance = null;
            if (avatar == null || outfit == null || string.IsNullOrEmpty(outfit.guid)) return false;

            var seen = new HashSet<GameObject>();
            foreach (var transform in avatar.GetComponentsInChildren<Transform>(true))
            {
                var root = PrefabUtility.GetNearestPrefabInstanceRoot(transform.gameObject);
                if (root == null || root == avatar.gameObject ||
                    !root.transform.IsChildOf(avatar.transform) || !seen.Add(root)) continue;
                var path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root);
                if (AssetDatabase.AssetPathToGUID(path) != outfit.guid) continue;
                instance = root;
                return true;
            }
            return false;
        }

        internal static WardrobeInstallResult Install(
            VRCAvatarDescriptor avatar,
            WardrobeAssetRecord outfit,
            bool allowIncompatible,
            bool createToggles,
            bool ownUndoGroup = true,
            string presetTarget = null)
        {
            if (avatar == null) return WardrobeInstallResult.Failure(WardrobeStrings.T("msg.noavatar"));
            if (EditorUtility.IsPersistent(avatar.gameObject))
                return WardrobeInstallResult.Failure(WardrobeStrings.T("msg.openavatar"));
            if (outfit == null) return WardrobeInstallResult.Failure(WardrobeStrings.T("msg.nooutfit"));

            var avatarRecord = GetAvatarRecord(avatar);
            var compatibility = Compatibility(outfit, avatarRecord == null ? null : avatarRecord.guid);
            if (compatibility.state == WardrobeCompatibilityState.Incompatible && !allowIncompatible)
                return WardrobeInstallResult.Failure(WardrobeStrings.T("msg.incompatible"));

            var source = AssetDatabase.LoadAssetAtPath<GameObject>(outfit.assetPath);
            if (source == null) return WardrobeInstallResult.Failure(WardrobeStrings.T("msg.nosource"));
            if (source.GetComponent<VRCAvatarDescriptor>() != null)
                return WardrobeInstallResult.Failure("A base avatar cannot be installed as an outfit.");
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return WardrobeInstallResult.Failure("Leave Play Mode before editing the avatar.");

            if (ownUndoGroup) Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Install wardrobe outfit");
            var succeeded = false;
            try
            {
                GameObject installed = null;
                if (presetTarget == null) TryFindInstalled(avatar, outfit, out installed);
                else installed = AvatarWardrobePresets.PrefabInstances(avatar, outfit.guid)
                    .FirstOrDefault(item => AvatarWardrobePresets.ItemPreset(item, avatar) == presetTarget);
                if (installed != null)
                {
                    if (createToggles)
                        OutfitToggleGenerator.CreateOrUpdateWardrobeToggle(avatar, installed, InstallationLabel(outfit));
                    succeeded = true;
                    return new WardrobeInstallResult
                    {
                        success = true, alreadyInstalled = true, instance = installed,
                        message = WardrobeStrings.T(createToggles ? "msg.already" : "msg.already.notoggles"),
                    };
                }
                var instance = PrefabUtility.InstantiatePrefab(source, avatar.transform) as GameObject;
                if (instance == null) throw new InvalidOperationException(WardrobeStrings.T("msg.nocreate"));
                Undo.RegisterCreatedObjectUndo(instance, "Install wardrobe outfit");
                Undo.RegisterFullObjectHierarchyUndo(instance, "Configure wardrobe outfit");

                var creatorSetup = HasCreatorSetup(instance);
                var setupApplied = false;
                var warning = string.Empty;
                if (!creatorSetup)
                {
                    if (compatibility.state == WardrobeCompatibilityState.Compatible &&
                        HasSafeSetupEvidence(instance, avatar, outfit, avatarRecord))
                    {
                        setupApplied = TrySetupOutfit(instance, out warning);
                    }
                    else
                    {
                        warning = WardrobeStrings.T("msg.nosetup");
                    }
                }

                if (createToggles)
                    OutfitToggleGenerator.CreateOrUpdateWardrobeToggle(avatar, instance, InstallationLabel(outfit));
                succeeded = true;
                Selection.activeGameObject = instance;
                EditorGUIUtility.PingObject(instance);
                return new WardrobeInstallResult
                {
                    success = true,
                    setupApplied = setupApplied,
                    instance = instance,
                    message = string.IsNullOrEmpty(warning)
                        ? WardrobeStrings.T(createToggles ? "msg.installed" : "msg.installed.notoggles")
                        : WardrobeStrings.T(createToggles ? "msg.installed" : "msg.installed.notoggles") + " " + warning,
                };
            }
            catch (Exception exception)
            {
                if (!ownUndoGroup) throw;
                Undo.RevertAllDownToGroup(undoGroup);
                return WardrobeInstallResult.Failure(WardrobeStrings.T("msg.reverted", exception.Message));
            }
            finally
            {
                if (ownUndoGroup && succeeded) Undo.CollapseUndoOperations(undoGroup);
            }
        }

        internal static void Remove(GameObject instance)
        {
            if (instance == null) return;
            if (EditorUtility.IsPersistent(instance) || instance.GetComponent<VRCAvatarDescriptor>() != null)
                throw new InvalidOperationException("Wardrobe will not remove an avatar root or source asset.");
            var avatar = instance.GetComponentInParent<VRCAvatarDescriptor>();
            if (avatar == null) throw new InvalidOperationException("The outfit is not inside an avatar.");
            if (avatar != null) OutfitToggleGenerator.RemoveWardrobeOutfit(avatar, instance);
            Undo.DestroyObjectImmediate(instance);
        }

        internal static void RebuildToggle(VRCAvatarDescriptor avatar, WardrobeAssetRecord outfit, GameObject instance)
        {
            if (avatar == null || outfit == null || instance == null) return;
            OutfitToggleGenerator.CreateOrUpdateWardrobeToggle(avatar, instance, InstallationLabel(outfit));
        }

        internal static void Invalidate(IEnumerable<string> changedPaths)
        {
            LoadCache();
            var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in changedPaths ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrEmpty(raw) || IsHiddenAssetPath(raw)) continue;
                var path = NormalizePath(raw);
                if (path.StartsWith("Assets/Generated/OutfitToggleIcons/", StringComparison.OrdinalIgnoreCase)) continue;
                changed.Add(path);
                if (path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) changed.Add(path.Substring(0, path.Length - 5));
            }
            if (changed.Count == 0) return;
            var affected = cache.records.Where(record => record != null &&
                (changed.Contains(NormalizePath(record.assetPath)) ||
                 (record.dependencies != null && record.dependencies.Any(path => changed.Contains(NormalizePath(path)))))).ToList();
            // Script/config changes can affect classification even without a prefab reference.
            var relevant = affected.Count > 0 || changed.Any(path => IsIndexedPrefabPath(path) ||
                path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith("wardrobe_config.json") ||
                path.EndsWith("AvatarBaseNames.json"));
            if (!relevant) return;
            foreach (var path in changed)
                if (!cache.dirtyPaths.Contains(path, StringComparer.OrdinalIgnoreCase)) cache.dirtyPaths.Add(path);
            WriteDirtySidecar();
            AvatarWardrobeServer.InvalidatePreviews(affected);
        }

        private static string DirtySidecarPath
        {
            get { return Path.Combine(CacheDirectory, "dirty.json"); }
        }

        [Serializable]
        private sealed class WardrobeDirtyPaths
        {
            public List<string> paths = new List<string>();
            public string revision;
        }

        private static void WriteDirtySidecar()
        {
            try
            {
                Directory.CreateDirectory(CacheDirectory);
                var sidecar = new WardrobeDirtyPaths();
                sidecar.paths = cache.dirtyPaths;
                sidecar.revision = Guid.NewGuid().ToString("N");
                WardrobeAtomicFile.WriteText(DirtySidecarPath, JsonUtility.ToJson(sidecar));
            }
            catch (Exception error) { Debug.LogWarning("Wardrobe could not record pending changes: " + error.Message); }
        }

        [MenuItem("Tools/Avatar Outfit Toggles/Diagnostics/Run Discovery Self Check")]
        private static void RunDiscoverySelfCheck()
        {
            var avatar = new WardrobeAssetRecord
            {
                assetPath = "Assets/Shinano/Prefab/Shinano.prefab",
                displayName = "Shinano",
                hasAvatarDescriptor = true,
                boneNames = new List<string> { "hips", "spine", "chest", "leftupperleg", "rightupperleg" },
            };
            var outfit = new WardrobeAssetRecord
            {
                assetPath = "Assets/WHITE/WhiteCardigan/Shinano/Black_Shinano_WhiteCardigan.prefab",
                displayName = "Black Shinano White Cardigan",
                skinnedRendererCount = 1,
                rendererCount = 1,
                boneNames = new List<string> { "hips", "spine", "chest", "leftupperleg", "rightupperleg" },
            };
            foreach (WardrobeAssetKind kind in Enum.GetValues(typeof(WardrobeAssetKind)))
            {
                var browsable = new WardrobeAssetRecord { kind = kind };
                Debug.Assert(IsBrowsablePrefab(browsable), "Classification must not hide a descriptor-free prefab.");
                browsable.hasAvatarDescriptor = true;
                Debug.Assert(!IsBrowsablePrefab(browsable), "Descriptor prefabs must not appear in the browser.");
            }
            ClassifyRecord(outfit, new[] { "shinano" });
            Debug.Assert(outfit.kind == WardrobeAssetKind.Outfit,
                "A skinned prefab in an explicit avatar folder must be an outfit.");
            var previewRig = new WardrobeAssetRecord
            {
                assetPath = "Assets/VERTEX/Smooth_Shading/Shinano/Prefab/(a)Preview (Transform 0).prefab",
                displayName = "A Preview Transform 0",
                hasAvatarDescriptor = true,
                skinnedRendererCount = 96,
                rendererCount = 96,
            };
            ClassifyRecord(previewRig, new[] { "shinano" });
            Debug.Assert(previewRig.kind != WardrobeAssetKind.Avatar,
                "Preview-utility prefabs must not hijack the avatar list.");
            Debug.Assert(Mathf.Approximately(BoneOverlap(outfit.boneNames, avatar.boneNames), 1f),
                "Identical armature fingerprints must fully overlap.");
            Debug.Assert(VariantName("CyberDress_Black") == "Black",
                "Recognized color suffixes must become variants.");
            Debug.Assert(VariantName("Beige_Shinano_WhiteCardigan") == "Beige",
                "Earlier color tokens must not be mistaken for a later outfit-name color.");
            Debug.Assert(FamilyName(outfit, new[] { "shinano" }) == "Cardigan",
                "Family names must remove target and color-only tokens.");
            var twinTail = new WardrobeAssetRecord
            {
                assetPath = "Assets/#MARIYURI/MY Ponytail/Airi/Airi.prefab",
                displayName = "Airi",
                rendererCount = 6,
                skinnedRendererCount = 6,
                partGroups = new List<string> { "Other: Hair Bang Side, Hair_pony, Twin" },
                boneNames = new List<string> { "hairlong1l", "ahoge", "chest", "head" },
            };
            ClassifyCategory(twinTail);
            Debug.Assert(twinTail.category == "hair",
                "Part names must identify hair even when the path says nothing.");
            var overdress = new WardrobeAssetRecord
            {
                assetPath = "Assets/Shop/Belladia/Prefabs/Shinano/Belladia_For_Shinano 1.prefab",
                displayName = "Belladia For Shinano 1",
                rendererCount = 12,
                skinnedRendererCount = 12,
                partGroups = new List<string> { "Other: hair accessory, Bikini, Corset, Skirt, Shoes, Stockings" },
            };
            ClassifyCategory(overdress);
            Debug.Assert(overdress.category == "outfit",
                "One hair accessory must not flip an outfit to hair.");
            var watergun = new WardrobeAssetRecord
            {
                assetPath = "Assets/HINO shop/Stargazer's Dream/Prefab/Shinano/StargazersDream_Shinano.prefab",
                displayName = "Stargazers Dream Shinano",
                rendererCount = 4,
                skinnedRendererCount = 0,
                partGroups = new List<string> { "Other: sunoil, watergun, pouch, bag" },
            };
            ClassifyCategory(watergun);
            Debug.Assert(watergun.category == "gimmick",
                "Prop parts must identify gimmicks, not outfits.");
            Debug.Assert(MatchBaseAvatar("ShinanoS6 (Main) (3)") == "Shinano",
                "Scene instances must resolve to their canonical base avatar.");
            Debug.Assert(MatchBaseAvatar("Bk Shinano S6 Main") == "Shinano",
                "Variant names must resolve to their canonical base avatar.");
            Debug.Assert(MatchBaseAvatar("Assets/Eku_Milfy/Eku/Prefab/Eku_Hair.prefab") == "Milphy/Eku",
                "Multi-name bases must match any of their aliases.");
            Debug.Assert(MatchBaseAvatar("Assets/Generic/Props/Crate.prefab") == null,
                "Unrelated paths must not match any base avatar.");
            Debug.Log("Avatar Wardrobe discovery self-check passed.");
        }

        [MenuItem("Tools/Avatar Outfit Toggles/Diagnostics/Validate Project Discovery")]
        private static void ValidateProjectDiscovery()
        {
            EnsureIndexed();
            var avatars = Avatars();
            var outfits = Records.Count(record => EffectiveKind(record) == WardrobeAssetKind.Outfit);
            Debug.Assert(avatars.Count > 0, "Avatar Wardrobe did not find an avatar prefab.");
            Debug.Assert(outfits > 0, "Avatar Wardrobe did not find an outfit prefab.");

            var samplePaths = new[]
            {
                "Shinano/Prefab/Shinano.prefab",
                "WHITE/WhiteCardigan/Shinano/White_Shinano_WhiteCardigan.prefab",
                "#LookVook/Noir_Lair/Shinano/Prefab/Modular/Noir_Lair.prefab",
            };
            var report = new StringBuilder();
            report.AppendLine("# Avatar Wardrobe validation");
            report.AppendLine();
            report.AppendLine("Avatars: " + avatars.Count);
            report.AppendLine("Outfits: " + outfits);
            report.AppendLine("Candidates: " + Records.Count(record => EffectiveKind(record) == WardrobeAssetKind.Candidate));
            report.AppendLine();
            foreach (var samplePath in samplePaths)
            {
                var record = Records.FirstOrDefault(candidate =>
                    candidate.assetPath.EndsWith(samplePath, StringComparison.OrdinalIgnoreCase));
                if (record == null)
                {
                    report.AppendLine("- missing: " + samplePath);
                    continue;
                }

                report.AppendLine("- " + record.assetPath + " → " + EffectiveKind(record) +
                                  " (" + record.confidence.ToString("0.00") + ")");
            }

            Directory.CreateDirectory(CacheDirectory);
            var reportPath = Path.Combine(CacheDirectory, "validation-report.md");
            File.WriteAllText(reportPath, report.ToString());
            Debug.Log("Avatar Wardrobe validation passed. Report: " + reportPath);
        }

        // Preview/test rigs can carry a descriptor without being playable avatars;
        // without this they sort first alphabetically and hijack the catalog avatar.
        private static bool IsPreviewUtilityAsset(string path)
        {
            var file = string.IsNullOrEmpty(path)
                ? string.Empty
                : Path.GetFileNameWithoutExtension(path);
            return file.IndexOf("preview", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void ClassifyRecord(WardrobeAssetRecord record, IEnumerable<string> avatarTokens)
        {
            if (record.hasAvatarDescriptor && !IsPreviewUtilityAsset(record.assetPath))
            {
                record.kind = WardrobeAssetKind.Avatar;
                record.confidence = 1f;
                return;
            }

            var evidence = 0;
            if (record.skinnedRendererCount > 0) evidence += 2;
            if (record.rendererCount > 0) evidence++;
            if (record.boneNames.Count >= 8) evidence++;
            if (record.hasHumanoidAnimator) evidence++;
            if (record.hasMergeArmature || record.hasOutfitRoot) evidence += 4;
            else if (record.hasObjectToggle || record.hasMenuItem) evidence++;
            if (avatarTokens.Any(token => PathContainsToken(record.assetPath, token))) evidence += 3;

            record.kind = evidence >= 5 ? WardrobeAssetKind.Outfit : WardrobeAssetKind.Candidate;
            record.confidence = Mathf.Clamp01(evidence / 8f);
            ClassifyCategory(record);
        }

        // Per-record hair-vs-outfit evidence. Families smooth via
        // FamilyCategory so model-instance colorways (no geometry in the
        // record) inherit their family's verdict.
        internal static void ClassifyCategory(WardrobeAssetRecord record)
        {
            float hair, outfit, gimmick;
            CategoryScores(record, out hair, out outfit, out gimmick);
            string label;
            float confidence;
            ClassifyCategoryScores(hair, outfit, gimmick, out label, out confidence);
            record.category = label;
            record.categoryConfidence = confidence;
        }

        internal static void ClassifyCategoryScores(float hair, float outfit, float gimmick, out string label, out float confidence)
        {
            WardrobeClassify.ClassifyCategoryScores(hair, outfit, gimmick, out label, out confidence);
        }

        // Confidence-weighted majority over a family's variants. Ties and
        // empty/pre-v4 families are "unknown".
        internal static string FamilyCategory(WardrobeFamily family, out float confidence)
        {
            return WardrobeClassify.FamilyCategory(family, out confidence);
        }

        private static void CategoryScores(WardrobeAssetRecord record, out float hair, out float outfit, out float gimmick)
        {
            WardrobeClassify.CategoryScores(record, out hair, out outfit, out gimmick);
        }

        private static int CountHits(HashSet<string> tokens, HashSet<string> signals)
        {
            return WardrobeClassify.CountHits(tokens, signals);
        }

        private static HashSet<string> CategoryTokens(IEnumerable<string> names)
        {
            return WardrobeClassify.CategoryTokens(names);
        }

        private static string DecodePrefabName(string name)
        {
            return WardrobeClassify.DecodePrefabName(name);
        }

        private static string BoneStem(string bone)
        {
            return WardrobeClassify.BoneStem(bone);
        }

        private static string FamilyName(WardrobeAssetRecord record, IEnumerable<string> avatarTokens)
        {
            return WardrobeText.FamilyName(record, avatarTokens);
        }

        private static string VariantName(string assetName)
        {
            return WardrobeText.VariantName(assetName);
        }

        private static string DisplayName(string path)
        {
            return WardrobeText.DisplayName(path);
        }

        private static bool IsGenericAssetName(string name)
        {
            return WardrobeText.IsGenericAssetName(name);
        }

        private static List<string> BoneNames(GameObject root, IEnumerable<SkinnedMeshRenderer> skinned)
        {
            var bones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var renderer in skinned)
            {
                foreach (var bone in renderer.bones)
                    if (bone != null) bones.Add(Normalize(bone.name));
                if (renderer.rootBone != null) bones.Add(Normalize(renderer.rootBone.name));
            }

            foreach (var animator in root.GetComponentsInChildren<Animator>(true))
            {
                if (animator.avatar == null || !animator.avatar.isHuman) continue;
                foreach (HumanBodyBones bone in Enum.GetValues(typeof(HumanBodyBones)))
                {
                    if (bone == HumanBodyBones.LastBone) continue;
                    if (animator.GetBoneTransform(bone) != null) bones.Add(Normalize(bone.ToString()));
                }
            }
            bones.Remove(string.Empty);
            return bones.OrderBy(name => name, StringComparer.Ordinal).ToList();
        }

        private static float BoneOverlap(IEnumerable<string> left, IEnumerable<string> right)
        {
            var leftSet = new HashSet<string>(left.Where(name => !string.IsNullOrEmpty(name)),
                StringComparer.OrdinalIgnoreCase);
            var rightSet = new HashSet<string>(right.Where(name => !string.IsNullOrEmpty(name)),
                StringComparer.OrdinalIgnoreCase);
            if (leftSet.Count == 0 || rightSet.Count == 0) return 0f;
            return (float)leftSet.Intersect(rightSet, StringComparer.OrdinalIgnoreCase).Count() /
                   Mathf.Min(leftSet.Count, rightSet.Count);
        }

        // Overload for scans that reuse one side (e.g. the avatar bone set).
        private static float BoneOverlap(IEnumerable<string> left, HashSet<string> rightSet)
        {
            var leftSet = new HashSet<string>(
                (left ?? Enumerable.Empty<string>()).Where(name => !string.IsNullOrEmpty(name)),
                StringComparer.OrdinalIgnoreCase);
            if (leftSet.Count == 0 || rightSet == null || rightSet.Count == 0) return 0f;
            return (float)leftSet.Intersect(rightSet, StringComparer.OrdinalIgnoreCase).Count() /
                   Mathf.Min(leftSet.Count, rightSet.Count);
        }

        private static bool HasCreatorSetup(GameObject instance)
        {
            return instance.GetComponentInChildren<ModularAvatarMergeArmature>(true) != null ||
                   instance.GetComponentInChildren<ModularAvatarOutfitRoot>(true) != null ||
                   instance.GetComponentInChildren<ModularAvatarObjectToggle>(true) != null ||
                   instance.GetComponentInChildren<ModularAvatarMenuItem>(true) != null;
        }

        private static bool HasSafeSetupEvidence(
            GameObject instance,
            VRCAvatarDescriptor avatar,
            WardrobeAssetRecord outfit,
            WardrobeAssetRecord avatarRecord)
        {
            if (instance.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length == 0) return false;
            var avatarBones = avatarRecord == null
                ? BoneNames(avatar.gameObject, avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                : avatarRecord.boneNames;
            return BoneOverlap(outfit.boneNames, avatarBones) >= 0.35f;
        }

        private static bool TrySetupOutfit(GameObject instance, out string warning)
        {
            warning = string.Empty;
            try
            {
                var type = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(assembly => assembly.GetType("nadena.dev.modular_avatar.core.editor.SetupOutfit"))
                    .FirstOrDefault(candidate => candidate != null);
                var method = type == null
                    ? null
                    : type.GetMethod("SetupOutfitUI", BindingFlags.Public | BindingFlags.Static);
                if (method == null)
                {
                    warning = WardrobeStrings.T("msg.noapi");
                    return false;
                }

                method.Invoke(null, new object[] { instance });
                return true;
            }
            catch (TargetInvocationException exception)
            {
                warning = WardrobeStrings.T("msg.setupfailed",
                          exception.InnerException == null ? exception.Message : exception.InnerException.Message);
                return false;
            }
            catch (Exception exception)
            {
                warning = WardrobeStrings.T("msg.setupfailed", exception.Message);
                return false;
            }
        }

        private static string InstallationLabel(WardrobeAssetRecord outfit)
        {
            var family = string.IsNullOrWhiteSpace(outfit.familyName) ? outfit.displayName : outfit.familyName;
            var variant = DisplayVariant(outfit);
            return variant == "Default" || string.Equals(variant, family, StringComparison.OrdinalIgnoreCase)
                ? family
                : family + " " + variant;
        }

        private static int CompatibilityRank(WardrobeCompatibilityState state)
        {
            switch (state)
            {
                case WardrobeCompatibilityState.Compatible: return 3;
                case WardrobeCompatibilityState.ProbablyCompatible: return 2;
                case WardrobeCompatibilityState.Untested: return 1;
                default: return 0;
            }
        }

        private static IEnumerable<string> AvatarTokens(WardrobeAssetRecord record)
        {
            if (record == null) return Enumerable.Empty<string>();
            return Tokenize(record.displayName)
                .Concat(Tokenize(Path.GetFileNameWithoutExtension(record.assetPath)))
                .Where(token => token.Length >= 4 && !WardrobeText.GenericTokens.Contains(token))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static bool PathContainsToken(string path, string token)
        {
            return Tokenize(path).Any(candidate => string.Equals(candidate, token, StringComparison.OrdinalIgnoreCase));
        }

        private static List<string> Tokenize(string value)
        {
            return WardrobeText.Tokenize(value);
        }

        private static string Humanize(string value)
        {
            return WardrobeText.Humanize(value);
        }

        private static string Title(IEnumerable<string> words)
        {
            return WardrobeText.Title(words);
        }

        private static string Normalize(string value)
        {
            return WardrobeText.Normalize(value);
        }

        private static bool IsIndexedPrefabPath(string path)
        {
            return !string.IsNullOrEmpty(path) &&
                   path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) &&
                   !IsHiddenAssetPath(path);
        }

        private static bool IsHiddenAssetPath(string path)
        {
            return NormalizePath(path).Split('/').Any(part => part.StartsWith(".", StringComparison.Ordinal));
        }

        private static string NormalizePath(string path)
        {
            return string.IsNullOrEmpty(path) ? string.Empty : path.Replace('\\', '/');
        }

        private static WardrobeAssetOverride GetOverride(string guid)
        {
            return string.IsNullOrEmpty(guid)
                ? null
                : overrides.entries.FirstOrDefault(entry => entry.guid == guid);
        }

        private static void LoadCache()
        {
            if (cacheLoaded) return;
            cacheLoaded = true;
            try
            {
                cache = File.Exists(CachePath)
                    ? JsonUtility.FromJson<WardrobeCatalogCache>(File.ReadAllText(CachePath))
                    : null;
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Avatar Wardrobe ignored its unreadable cache: " + exception.Message);
            }

            if (cache == null || cache.version != CacheVersion)
                cache = new WardrobeCatalogCache { version = CacheVersion };
            if (cache.records == null) cache.records = new List<WardrobeAssetRecord>();
            cache.records.RemoveAll(record => record == null || string.IsNullOrEmpty(record.guid));
            foreach (var record in cache.records)
            {
                if (record.meshIds == null) record.meshIds = new List<string>();
                if (record.materialIds == null) record.materialIds = new List<string>();
                if (record.boneNames == null) record.boneNames = new List<string>();
                if (record.dependencies == null) record.dependencies = new List<string>();
            }
            if (cache.dirtyPaths == null) cache.dirtyPaths = new List<string>();
            try
            {
                if (File.Exists(CachePath)) loadedCatalogStamp = File.GetLastWriteTimeUtc(CachePath);
            }
            catch (Exception) { }
            try
            {
                if (File.Exists(DirtySidecarPath))
                {
                    var sidecar = JsonUtility.FromJson<WardrobeDirtyPaths>(File.ReadAllText(DirtySidecarPath));
                    if (sidecar != null && sidecar.paths != null)
                        foreach (var path in sidecar.paths)
                            if (!cache.dirtyPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                                cache.dirtyPaths.Add(path);
                }
            }
            catch (Exception) { }
        }

        private static void LoadOverrides()
        {
            if (overridesLoaded) return;
            try
            {
                overrides = File.Exists(OverridesPath)
                    ? JsonUtility.FromJson<WardrobeOverridesFile>(File.ReadAllText(OverridesPath))
                    : null;
            }
            catch (Exception exception)
            {
                throw new IOException("Avatar Wardrobe could not read its overrides. Restore the file or its backup before editing classifications.", exception);
            }

            if (overrides == null) overrides = new WardrobeOverridesFile();
            if (overrides.avatarOverrides == null) overrides.avatarOverrides = new List<WardrobeAvatarOverride>();
            overrides.avatarOverrides.RemoveAll(entry => entry == null || string.IsNullOrEmpty(entry.sourceGuid));
            if (overrides.entries == null) overrides.entries = new List<WardrobeAssetOverride>();
            overrides.entries.RemoveAll(entry => entry == null || string.IsNullOrEmpty(entry.guid));
            overridesLoaded = true;
        }

        private static void SaveOverrides()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(OverridesPath));
            WardrobeAtomicFile.WriteText(OverridesPath, JsonUtility.ToJson(overrides, true), true);
        }
    }

    internal interface IAssetMetadataProvider
    {
        bool IsAvailable { get; }
        void CleanFamilyName(
            string avatarName,
            WardrobeFamily family,
            Action<string> success,
            Action<string> failure);
    }

    internal static class WardrobeMetadataProvider
    {
        private static readonly IAssetMetadataProvider Apple = new AppleIntelligenceAssetMetadataProvider();
        private static readonly IAssetMetadataProvider None = new NoOpAssetMetadataProvider();

        internal static IAssetMetadataProvider Current
        {
            get { return Apple.IsAvailable ? Apple : None; }
        }
    }

    internal sealed class NoOpAssetMetadataProvider : IAssetMetadataProvider
    {
        public bool IsAvailable
        {
            get { return false; }
        }

        public void CleanFamilyName(
            string avatarName,
            WardrobeFamily family,
            Action<string> success,
            Action<string> failure)
        {
            failure(WardrobeStrings.T("name.unavailable"));
        }
    }

    internal sealed class AppleIntelligenceAssetMetadataProvider : IAssetMetadataProvider
    {
        public bool IsAvailable
        {
            get { return AppleIntelligenceNameCleaner.IsAvailable(); }
        }

        public void CleanFamilyName(
            string avatarName,
            WardrobeFamily family,
            Action<string> success,
            Action<string> failure)
        {
            if (!IsAvailable)
            {
                failure("Apple Intelligence is unavailable.");
                return;
            }

            var paths = string.Join("\n", family.variants.Select(variant => variant.assetPath));
            AppleIntelligenceNameCleaner.Clean(
                avatarName,
                family.displayName,
                paths,
                new[]
                {
                    new ToggleNameCandidate { id = 0, path = paths, name = family.displayName },
                },
                labels =>
                {
                    string name;
                    if (!labels.TryGetValue(0, out name) || string.IsNullOrWhiteSpace(name))
                    {
                        failure("Apple Intelligence returned no usable family name.");
                        return;
                    }
                    success(name.Trim());
                },
                failure);
        }
    }

    public sealed class AvatarWardrobeAssetPostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            AvatarWardrobeCatalog.Invalidate(importedAssets
                .Concat(deletedAssets)
                .Concat(movedAssets)
                .Concat(movedFromAssetPaths));
        }
    }
}
