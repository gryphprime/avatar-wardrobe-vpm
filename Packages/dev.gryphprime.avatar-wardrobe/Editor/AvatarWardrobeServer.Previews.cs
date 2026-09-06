using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

// Demand-driven Unity previews and fingerprinted disk-cache bookkeeping.
namespace OutfitToggleGenerator
{
    internal static partial class AvatarWardrobeServer
    {
        private static string previewGridPriority = "";
        private static string queuedPreviewGrid;
        private static string queuedPreviewEpoch;
        private static double nextBackgroundPreviewPass;
        private static Queue<string> backgroundPreviews = new Queue<string>();
        private static readonly HashSet<string> attemptedBackgroundPreviews = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Runs only in an idle dispatcher slot, after requested previews and avatar edits.
        // Continue through the entire catalog even when the browser loses focus.
        private static void BakeNextBackgroundPreview()
        {
            if (UploadTargetLocked || ShiroTools.OutfitBatchUploader.BatchActiveNow || EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode) return;
            RefreshPreviewVersions();
            string grid;
            lock (webActiveLock) grid = previewGridPriority;
            var avatar = AvatarWardrobeCatalog.GetAvatarRecord(SceneAvatar);
            var epoch = AvatarWardrobeCatalog.CatalogEpoch + "|" + previewRevision + "|" + (avatar == null ? "" : avatar.guid);
            if (backgroundPreviews.Count == 0 && EditorApplication.timeSinceStartup >= nextBackgroundPreviewPass)
            {
                attemptedBackgroundPreviews.Clear();
                queuedPreviewEpoch = null;
                nextBackgroundPreviewPass = EditorApplication.timeSinceStartup + 60;
            }
            if (queuedPreviewEpoch != epoch || queuedPreviewGrid != grid)
            {
                if (queuedPreviewEpoch != epoch) attemptedBackgroundPreviews.Clear();
                queuedPreviewEpoch = epoch; queuedPreviewGrid = grid;
                var ordered = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Action<string> add = guid => { if (IsAssetGuid(guid) && seen.Add(guid)) ordered.Add(guid); };
                var gridGuids = grid.Split(',').Where(IsAssetGuid).Take(120).ToList();
                // Grid covers first, then their variants, then the remaining catalog.
                foreach (var guid in gridGuids) add(guid);
                var families = CachedFamilies();
                foreach (var guid in gridGuids)
                {
                    var family = families.FirstOrDefault(f => f.variants.Any(v => v.guid == guid));
                    if (family != null) foreach (var variant in family.variants) add(variant.guid);
                }
                var records = AvatarWardrobeCatalog.Records.Where(record => record != null &&
                    (AvatarWardrobeCatalog.EffectiveKind(record) == WardrobeAssetKind.Outfit || AvatarWardrobeCatalog.EffectiveKind(record) == WardrobeAssetKind.Candidate)).ToList();
                if (avatar != null)
                    foreach (var record in records)
                    {
                        var state = AvatarWardrobeCatalog.Compatibility(record, avatar.guid).state;
                        if (state == WardrobeCompatibilityState.Compatible || state == WardrobeCompatibilityState.ProbablyCompatible)
                            add(record.guid);
                    }
                foreach (var record in records) add(record.guid);
                backgroundPreviews = new Queue<string>(ordered.Where(guid => !attemptedBackgroundPreviews.Contains(guid)));
            }
            // Bound disk probes too: a fully cached catalog must not stall an editor update.
            for (var checkedCount = 0; checkedCount < 64 && backgroundPreviews.Count > 0; checkedCount++)
            {
                var guid = backgroundPreviews.Dequeue();
                if (HasHiThumb(guid) || thumbDead.Contains("hi:" + guid) || !attemptedBackgroundPreviews.Add(guid)) continue;
                BakeThumbHi(guid);
                return; // Never more than one render per editor update.
            }
        }

        // Fast async preview (128px AssetPreview): null while Unity is still
        // baking (client retries), empty when the prefab cannot render, PNG
        // bytes otherwise. Dead only after repeated fruitless observations,
        // so a slow first bake is never mistaken for an unrenderable prefab.
        // Thumbnails are served straight off pool threads now, so a bake must
        // never leave a half-written file for a concurrent reader: write
        // aside and swap into place. Bytes are already in hand, so a lost
        // race just means the other writer's identical file wins.
        private static bool WriteThumbAtomically(string path, byte[] png)
        {
            try { WardrobeAtomicFile.WriteBytes(path, png); return true; }
            catch (IOException exception) { Debug.LogWarning("Wardrobe could not cache preview: " + exception.Message); return false; }
        }

        private static byte[] BakeThumb(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return new byte[0];
            var path = ThumbPath(guid);
            if (File.Exists(path))
            {
                try
                {
                    return File.ReadAllBytes(path);
                }
                catch (Exception) { }
            }
            if (thumbDead.Contains("lo:" + guid)) return new byte[0];
            var record = AvatarWardrobeCatalog.GetRecord(guid);
            if (record == null) return new byte[0];
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(record.assetPath);
            if (prefab == null) return new byte[0];
            try
            {
                var preview = AssetPreview.GetAssetPreview(prefab);
                if (preview != null)
                {
                    var png = preview.EncodeToPNG();
                    if (png == null) return new byte[0];
                    WriteThumbAtomically(path, png);
                    thumbSightings.Remove(guid);
                    return png;
                }
            }
            catch (Exception exception)
            {
                if (thumbWarned.Add(guid))
                    Debug.LogWarning("Avatar Wardrobe could not bake a thumbnail: " + exception.Message);
                return new byte[0];
            }
            if (AssetPreview.IsLoadingAssetPreview(prefab.GetInstanceID()))
            {
                thumbSightings[guid] = 0;
                return null;
            }
            int sightings;
            thumbSightings.TryGetValue(guid, out sightings);
            sightings++;
            if (sightings >= 5)
            {
                thumbSightings.Remove(guid);
                thumbDead.Add("lo:" + guid);
                return new byte[0];
            }
            thumbSightings[guid] = sightings;
            return null;
        }

        private static void ResetThumb(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return;
            thumbDead.Remove("hi:" + guid);
            thumbDead.Remove("lo:" + guid);
            foreach (var hi in new[] { false, true })
            {
                var path = ThumbPath(guid, hi);
                try { if (File.Exists(path)) File.Delete(path); if (hi) TrackHighPreview(path, false); } catch (IOException) { }
            }
            thumbSightings.Remove(guid);
            thumbWarned.Remove(guid);
        }

        // Icon-pipeline render, same frame as toggle icons: 512px neutral on
        // transparency, baked synchronously. Empty when the prefab cannot
        // render (client 404s it dead on the first try, no polling loop).
        // Null only while the web UI holds no focus lease (Unity stays idle).
        private static byte[] BakeThumbHi(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return new byte[0];
            var path = ThumbPath(guid, true);
            if (File.Exists(path))
            {
                try
                {
                    return File.ReadAllBytes(path);
                }
                catch (Exception) { }
            }
            if (thumbDead.Contains("hi:" + guid)) return new byte[0];
            var record = AvatarWardrobeCatalog.GetRecord(guid);
            if (record == null) return new byte[0];
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(record.assetPath);
            if (prefab == null) return new byte[0];
            Texture2D icon = null;
            try
            {
                icon = OutfitToggleGenerator.RenderOutfitThumb(prefab);
                if (icon == null)
                {
                    thumbDead.Add("hi:" + guid);
                    return new byte[0];
                }
                var png = icon.EncodeToPNG();
                if (png == null) return new byte[0];
                if (WriteThumbAtomically(path, png)) TrackHighPreview(path, true);
                return png;
            }
            catch (Exception exception)
            {
                if (thumbWarned.Add(guid))
                    Debug.LogWarning("Avatar Wardrobe could not bake a thumbnail: " + exception.Message);
                return new byte[0];
            }
            finally
            {
                if (icon != null) UnityEngine.Object.DestroyImmediate(icon);
            }
        }

        [Serializable]
        private sealed class DiagDto
        {
            public string thumbDir = string.Empty;
            public int thumbDirExists;
            public int thumbsOnDisk;
            public int sightingCount;
            public int deadCount;
            public string sampleGuid = string.Empty;
            public string sampleAsset = string.Empty;
            public int sampleRecord;
            public int samplePrefab;
            public int samplePreview;
            public int sampleLoading;
            public int sampleSightings;
            public int sampleThumbExists;
            public int hiThumbsOnDisk;
        }

        private static DiagDto GetDiag()
        {
            var dto = new DiagDto { thumbDir = thumbDir ?? string.Empty };
            try
            {
                dto.thumbDirExists = Directory.Exists(thumbDir) ? 1 : 0;
                dto.thumbsOnDisk = Directory.Exists(thumbDir)
                    ? Directory.GetFiles(thumbDir, "*.png").Length
                    : 0;
            }
            catch (Exception) { }
            dto.sightingCount = thumbSightings.Count;
            dto.deadCount = thumbDead.Count;
            var sample = CachedFamilies().SelectMany(family => family.variants).FirstOrDefault();
            if (sample == null) return dto;
            dto.sampleGuid = sample.guid;
            dto.sampleAsset = sample.assetPath;
            dto.sampleRecord = 1;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(sample.assetPath);
            dto.samplePrefab = prefab != null ? 1 : 0;
            if (prefab == null) return dto;
            dto.samplePreview = AssetPreview.GetAssetPreview(prefab) != null ? 1 : 0;
            dto.sampleLoading = AssetPreview.IsLoadingAssetPreview(prefab.GetInstanceID()) ? 1 : 0;
            int sightings;
            thumbSightings.TryGetValue(sample.guid, out sightings);
            dto.sampleSightings = sightings;
            dto.sampleThumbExists = File.Exists(ThumbPath(sample.guid)) ? 1 : 0;
            try
            {
                dto.hiThumbsOnDisk = hiDir != null && Directory.Exists(hiDir)
                    ? Directory.GetFiles(hiDir, "*.png").Length
                    : 0;
            }
            catch (Exception) { }
            return dto;
        }

        private static string ThumbPath(string guid)
        {
            return ThumbPath(guid, false);
        }

        // Hi-res readiness probe for list/detail DTOs: pure File.Exists, no
        // main thread, so paging stays free and the client can prefer hi.
        private static bool HasHiThumb(string guid)
        {
            try
            {
                return !string.IsNullOrEmpty(guid) && File.Exists(ThumbPath(guid, true));
            }
            catch (Exception) { return false; }
        }

        private static string ThumbPath(string guid, bool hi)
        {
            // Fields are set at Start; the fallback keeps pre-Start family
            // listings (which probe thumbnails) resolving identically.
            var dir = hi ? hiDir : thumbDir;
            if (string.IsNullOrEmpty(dir))
                dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "AvatarWardrobe", hi ? "thumbs512" : "thumbs"));
            string version;
            lock (previewVersionsLock) previewVersions.TryGetValue(guid ?? string.Empty, out version);
            return Path.Combine(dir, guid + (string.IsNullOrEmpty(version) ? "" : "_" + version) + ".png");
        }

        private static readonly object previewVersionsLock = new object();
        private static Dictionary<string, string> previewVersions = new Dictionary<string, string>();
        private static int previewCatalogEpoch = -1;
        private static int previewRevision;
        private static Task<HashSet<string>> previewCountTask;
        private static HashSet<string> countedHighPreviews = new HashSet<string>(StringComparer.Ordinal);
        private static HashSet<string> countableHighPreviews = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, bool> previewCountChanges = new Dictionary<string, bool>(StringComparer.Ordinal);
        private static int countedPreviewRevision = -1;

        private static void TrackHighPreview(string path, bool exists)
        {
            if (previewCountTask != null) previewCountChanges[path] = exists;
            if (exists) countedHighPreviews.Add(path); else countedHighPreviews.Remove(path);
            hiBakedCache = countedHighPreviews.Count(countableHighPreviews.Contains);
        }
        private static int previewCountRevision;

        private static void RefreshPreviewVersions()
        {
            if (previewCatalogEpoch == AvatarWardrobeCatalog.CatalogEpoch) return;
            var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in AvatarWardrobeCatalog.Records)
                if (r != null && IsAssetGuid(r.guid))
                    versions[r.guid] = string.IsNullOrEmpty(r.dependencyFingerprint) ? "legacy" : r.dependencyFingerprint.Substring(0, Math.Min(24, r.dependencyFingerprint.Length));
            lock (previewVersionsLock) previewVersions = versions;
            previewCatalogEpoch = AvatarWardrobeCatalog.CatalogEpoch;
            previewRevision++;
            thumbDead.Clear(); thumbSightings.Clear(); thumbWarned.Clear();
            hiCacheAt = DateTime.MinValue;
        }
        internal static void InvalidatePreviews(IEnumerable<WardrobeAssetRecord> changed)
        {
            if (thumbDir == null) return;
            var any = false;
            foreach (var r in changed)
            {
                if (r == null || !IsAssetGuid(r.guid)) continue;
                ResetThumb(r.guid); any = true;
            }
            if (any) { previewRevision++; hiCacheAt = DateTime.MinValue; }
        }
        private static void UpdatePreviewCounts()
        {
            if (countedPreviewRevision != previewRevision)
            {
                countableHighPreviews = new HashSet<string>(AvatarWardrobeCatalog.Records
                    .Where(r => r != null && IsAssetGuid(r.guid) && (AvatarWardrobeCatalog.EffectiveKind(r) == WardrobeAssetKind.Outfit || AvatarWardrobeCatalog.EffectiveKind(r) == WardrobeAssetKind.Candidate))
                    .Select(r => ThumbPath(r.guid, true)), StringComparer.Ordinal);
                countedHighPreviews.IntersectWith(countableHighPreviews);
                hiTotalCache = countableHighPreviews.Count;
                hiBakedCache = countedHighPreviews.Count;
                countedPreviewRevision = previewRevision;
                hiCacheAt = DateTime.MinValue;
            }
            if (previewCountTask != null)
            {
                if (!previewCountTask.IsCompleted) return;
                if (!previewCountTask.IsFaulted && previewCountRevision == previewRevision)
                {
                    var snapshot = previewCountTask.Result;
                    // A disk scan may finish after newer renders or retries. Apply those
                    // changes rather than replacing the live count with an older snapshot.
                    foreach (var change in previewCountChanges)
                        if (change.Value) snapshot.Add(change.Key); else snapshot.Remove(change.Key);
                    snapshot.IntersectWith(countableHighPreviews);
                    countedHighPreviews = snapshot;
                    hiBakedCache = snapshot.Count;
                }
                previewCountTask = null;
                previewCountChanges.Clear();
            }
            if ((DateTime.UtcNow - hiCacheAt).TotalSeconds < 15) return;
            var paths = countableHighPreviews.ToArray();
            previewCountRevision = previewRevision;
            previewCountChanges.Clear();
            previewCountTask = Task.Run(() => new HashSet<string>(paths.Where(File.Exists), StringComparer.Ordinal));
            hiCacheAt = DateTime.UtcNow;
        }
    }
}
