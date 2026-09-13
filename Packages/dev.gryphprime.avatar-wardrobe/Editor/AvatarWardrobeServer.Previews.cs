using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
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
        private static Task previewCacheInitialization;
        private static string previewGridPriority = "";
        private static string queuedPreviewGrid;
        private static string queuedPreviewEpoch;
        private static HashSet<string> queuedPreviewVisible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static HashSet<string> previewVisiblePriority = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static double lastBackgroundPreviewFinished, lastBackgroundPreviewDuration;

        // Visible work uses at most ~50% of main-thread time; speculative work
        // stays at ~9%.  Visible high-res cards are explicit user demand, so
        // waiting three render durations made first-view loading unnecessarily
        // sluggish while background prefetch remains conservative.
        internal static double PreviewIdleDelay(double renderSeconds, bool visible)
            => visible ? Math.Max(.1, renderSeconds) : Math.Max(1, renderSeconds * 10);
        private static Queue<string> backgroundPreviews = new Queue<string>();
        private static readonly HashSet<string> attemptedBackgroundPreviews = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static bool HasEncodingHeadroom(string path)
        {
            var highPrefix = string.IsNullOrEmpty(hiDir) ? null : hiDir + Path.DirectorySeparatorChar;
            var isHigh = highPrefix != null && path.StartsWith(highPrefix, StringComparison.Ordinal);
            var max = isHigh ? 4 : 8;
            var highCount = highPrefix == null ? 0 : previewEncoding.Keys.Count(key => key.StartsWith(highPrefix, StringComparison.Ordinal));
            return (isHigh ? highCount : previewEncoding.Count - highCount) < max;
        }

        internal static bool IsCurrentPreviewEpoch(string requested)
        {
            if (string.IsNullOrEmpty(requested)) return false;
            var parts = requested.Split('|');
            if (parts.Length < 5) return false;
            return string.Equals(parts[0], serverSession, StringComparison.Ordinal) &&
                string.Equals(parts[1], AvatarWardrobeCatalog.CatalogEpoch.ToString(), StringComparison.Ordinal) &&
                string.Equals(parts[4], previewRevision.ToString(), StringComparison.Ordinal);
        }

        internal static IEnumerable<string> PrioritizedPreviewGuids(IEnumerable<string> priority, IEnumerable<string> remainder)
            => priority.Where(IsAssetGuid).Distinct(StringComparer.OrdinalIgnoreCase).Take(120)
                .Concat(remainder.Where(IsAssetGuid)).Distinct(StringComparer.OrdinalIgnoreCase);

        // Warm the current grid first, then the remaining catalog, while the
        // browser has an open-page lease.
        // A single prefab render can exceed the dispatcher budget; leave a long
        // idle interval after expensive renders and process only one per tick.
        private static void BakeNextBackgroundPreview()
        {
            if (!WebActive || UploadTargetLocked || ShiroTools.OutfitBatchUploader.BatchActiveNow || EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode) return;
            RefreshPreviewVersions();
            string grid;
            HashSet<string> visible;
            lock (webActiveLock) { grid = previewGridPriority; visible = previewVisiblePriority; }
            var epoch = AvatarWardrobeCatalog.CatalogEpoch + "|" + previewRevision;
            if (queuedPreviewEpoch != epoch || queuedPreviewGrid != grid || !queuedPreviewVisible.SetEquals(visible))
            {
                if (queuedPreviewEpoch != epoch) attemptedBackgroundPreviews.Clear();
                queuedPreviewEpoch = epoch; queuedPreviewGrid = grid;
                queuedPreviewVisible = new HashSet<string>(visible, StringComparer.OrdinalIgnoreCase);
                var priority = grid.Split(',').OrderByDescending(visible.Contains);
                var remainder = AvatarWardrobeCatalog.Records
                    .Where(r => r != null && (AvatarWardrobeCatalog.EffectiveKind(r) == WardrobeAssetKind.Outfit ||
                        AvatarWardrobeCatalog.EffectiveKind(r) == WardrobeAssetKind.Candidate))
                    .Select(r => r.guid);
                backgroundPreviews = new Queue<string>(PrioritizedPreviewGuids(priority, remainder));
            }
            for (var checkedCount = 0; checkedCount < 16 && backgroundPreviews.Count > 0; checkedCount++)
            {
                var guid = backgroundPreviews.Peek();
                if (HasHiThumb(guid) || thumbDead.Contains("hi:" + guid) || attemptedBackgroundPreviews.Contains(guid))
                { backgroundPreviews.Dequeue(); continue; }
                // Re-evaluate against current visibility, so an old prefetch cooldown
                // cannot hold newly visible cards behind it after scrolling.
                if (EditorApplication.timeSinceStartup < lastBackgroundPreviewFinished +
                    PreviewIdleDelay(lastBackgroundPreviewDuration, visible.Contains(guid))) return;
                // Do not consume/mark a candidate until its encoder has room.
                // Otherwise a saturated queue discards the rendered texture and
                // the GUID stays permanently attempted until the grid changes.
                if (!HasEncodingHeadroom(ThumbPath(guid, true))) return;
                backgroundPreviews.Dequeue(); attemptedBackgroundPreviews.Add(guid);
                var started = EditorApplication.timeSinceStartup;
                BakeThumbHi(guid);
                var finished = EditorApplication.timeSinceStartup;
                lastBackgroundPreviewDuration = finished - started;
                lastBackgroundPreviewFinished = finished;
                return;
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
        private static readonly ConcurrentDictionary<string, Task<byte[]>> previewEncoding = new ConcurrentDictionary<string, Task<byte[]>>(StringComparer.Ordinal);
        private static byte[] QueueThumbEncoding(Texture2D texture, string path)
        {
            // Keep low-res scroll work bounded, but do not let a burst of
            // 128px encodes starve the 512px path.  Hi-res previews are the
            // user's explicit quality request and get their own headroom.
            if (previewEncoding.ContainsKey(path) || !HasEncodingHeadroom(path)) return null;
            // Copy pixels while the texture is alive; the worker owns only a managed array.
            var pixels = texture.GetPixels32();
            var width = (uint)texture.width; var height = (uint)texture.height;
            previewEncoding[path] = Task.Run(() =>
            {
                var png = ImageConversion.EncodeArrayToPNG(pixels, UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm, width, height);
                WardrobeAtomicFile.WriteBytes(path, png);
                return png;
            });
            return null;
        }
        private static void PumpPreviewEncoding()
        {
            foreach (var path in previewEncoding.Keys.ToArray())
            {
                var task = previewEncoding[path];
                if (!task.IsCompleted) continue;
                previewEncoding.TryRemove(path, out _);
                if (task.IsFaulted) WardrobeLog.Write("preview", "Could not cache preview: " + task.Exception.GetBaseException().Message);
                else if (task.Result != null && path.StartsWith(hiDir + Path.DirectorySeparatorChar, StringComparison.Ordinal)) TrackHighPreview(path, true);
            }
        }

        private static byte[] BakeThumb(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return new byte[0];
            var path = ThumbPath(guid);
            if (previewEncoding.ContainsKey(path) || File.Exists(path)) return null;
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
                    thumbSightings.Remove(guid);
                    return QueueThumbEncoding(preview, path);
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
                // Serialize deletion after any encoding already queued for this exact cache key.
                previewEncoding.TryGetValue(path, out var prior);
                previewEncoding[path] = Task.Run(async () =>
                {
                    if (prior != null) { try { await prior; } catch (Exception) { } }
                    if (File.Exists(path)) File.Delete(path);
                    return (byte[])null;
                });
                if (hi) TrackHighPreview(path, false);
            }
            thumbSightings.Remove(guid);
            thumbWarned.Remove(guid);
        }

        // Icon-pipeline render, same frame as toggle icons: 512px neutral on
        // transparency, baked synchronously. Empty when the prefab cannot
        // render (client 404s it dead on the first try, no polling loop).
        // Encoding already queued or temporarily at capacity returns null.
        private static byte[] BakeThumbHi(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return new byte[0];
            var path = ThumbPath(guid, true);
            if (previewEncoding.ContainsKey(path) || File.Exists(path)) return null;
            if (thumbDead.Contains("hi:" + guid)) return new byte[0];
            var record = AvatarWardrobeCatalog.GetRecord(guid);
            if (record == null) return new byte[0];
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(record.assetPath);
            if (prefab == null) return new byte[0];
            if (!HasEncodingHeadroom(path)) return null;
            Texture2D icon = null;
            try
            {
                icon = OutfitToggleGenerator.RenderOutfitThumb(prefab);
                if (icon == null)
                {
                    thumbDead.Add("hi:" + guid);
                    return new byte[0];
                }
                return QueueThumbEncoding(icon, path);
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
            return dto;
        }

        private static DiagDto PopulateDiskDiagnostics(DiagDto dto, string highDirectory)
        {
            try
            {
                dto.thumbDirExists = Directory.Exists(dto.thumbDir) ? 1 : 0;
                dto.thumbsOnDisk = dto.thumbDirExists == 1 ? Directory.EnumerateFiles(dto.thumbDir, "*.png").Count() : 0;
                dto.hiThumbsOnDisk = Directory.Exists(highDirectory) ? Directory.EnumerateFiles(highDirectory, "*.png").Count() : 0;
            }
            catch (IOException) { }
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
                // Import notifications also occur when Unity reopens or reimports
                // unchanged assets. Keep the fingerprinted PNGs: the completed
                // catalog scan selects a new path only when source content changes.
                // Deleting here also lets a render overwrite the old fingerprint
                // with new pixels before that scan has finished.
                thumbDead.Remove("hi:" + r.guid);
                thumbDead.Remove("lo:" + r.guid);
                thumbSightings.Remove(r.guid);
                thumbWarned.Remove(r.guid);
                any = true;
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
