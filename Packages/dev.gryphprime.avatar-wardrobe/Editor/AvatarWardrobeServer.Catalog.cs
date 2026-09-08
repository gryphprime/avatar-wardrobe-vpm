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

// Catalog projections, query caches, and browser response DTOs.
namespace OutfitToggleGenerator
{
    internal static partial class AvatarWardrobeServer
    {
        // ---- API bodies (all run on the main thread) ----

        [Serializable]
        private sealed class NameJobDto
        {
            public int ok;
            public string message;
            public string job = string.Empty;
            public string name = string.Empty;
        }

        [Serializable]
        private sealed class NameResultDto
        {
            public int pending;
            public int ok;
            public string message;
            public string name = string.Empty;
        }

        [Serializable]
        private sealed class InstalledItemDto
        {
            public int instanceId;
            public string setupWarning = "";
            public string path = string.Empty;
            public string guid = string.Empty;
            public string family = string.Empty;
            public string variant = string.Empty;
            public string familyId = string.Empty;
            public string target = string.Empty;
            public string targetName = string.Empty;
        }

        [Serializable]
        private sealed class InstalledListDto
        {
            public List<InstalledItemDto> items = new List<InstalledItemDto>();
        }

        private sealed class NameJobState
        {
            public bool done;
            public bool ok;
            public string message;
            public string name;
        }

        private static readonly Dictionary<string, NameJobState> nameJobs = new Dictionary<string, NameJobState>();
        private static readonly object nameJobsLock = new object();

        [Serializable]
        private sealed class BaseAvatarChoice { public string guid; public string name; public string path; }

        [Serializable]
        internal sealed class ResultDto
        {
            public int ok;
            public string message;
            public string id = string.Empty;
        }

        [Serializable]
        private sealed class MenuGroupsDto
        {
            public int ok;
            public string id = "";
            public string message = "";
            public List<AvatarWardrobePresets.MenuGroup> groups = new List<AvatarWardrobePresets.MenuGroup>();
        }

        [Serializable]
        private sealed class StateDto
        {
            public int separateAvatarUploads;
            public string wardrobeMode;
            public string selectedPreset;
            public List<PresetDto> workflowPresets;
            public bool sdkLoggedIn;
            public bool sdkUploadReady;
            public string server = "wardrobe-refactor-1";
            public string wardrobeVersion = WardrobeVersion.Current;
            public int avatarInstanceId;
            public string projectPath, scenePath;
            public TargetChoice[] sceneTargets;
            public string avatarName = string.Empty;
            public string avatarLabel = string.Empty;
            public string avatarGuid = string.Empty;
            public string avatarSourceGuid = "";
            public bool avatarBaseEditable;
            public string avatarOverrideGuid = "";
            public List<BaseAvatarChoice> baseAvatars = new List<BaseAvatarChoice>();
            public int families;
            public int outfits;
            public int avatars;
            public int indexing;
            public string indexPhase;
            public int done;
            public int total;
            public int dirty;
            public int ai;
            public int hiBaked;
            public int hiTotal;
            public string session = string.Empty;
            public int previewEpoch;
            public string epoch = string.Empty;
        }

        [Serializable]
        private sealed class FamilyDto
        {
            public string id = string.Empty;
            public string name = string.Empty;
            public int variants;
            public string thumb = string.Empty;
            public int hi;
            public string shop = string.Empty;
            public string product = string.Empty;
            public int phys;
            public int contact;
            public int compat;
            public bool compatibleOverride;
            public string compatText = string.Empty;
            public int installed;
            public string category = string.Empty;
            public string categoryConfidence = string.Empty;
            public List<string> variantGuids = new List<string>();
        }

        [Serializable]
        private sealed class FamilyListDto
        {
            public int total;
            public int page;
            public int pageSize;
            public int pageCount;
            public List<FamilyDto> items = new List<FamilyDto>();
        }

        [Serializable]
        private sealed class VariantDto
        {
            public string assetVersion = string.Empty;
            public string guid = string.Empty;
            public int hi;
            public string variant = string.Empty;
            public string colorway = string.Empty;
            public string shop = string.Empty;
            public string product = string.Empty;
            public string source = string.Empty;
            public string category = string.Empty;
            public string confidence = string.Empty;
            public int setup;
            public int phys;
            public int contact;
            public List<string> parts = new List<string>();
            public List<string> mats = new List<string>();
            public int compat;
            public bool compatibleOverride;
            public string compatText = string.Empty;
            public string explanation = string.Empty;
            public int installed;
            public string wear = string.Empty;
            public string assigned = string.Empty;
            public string assignedName = string.Empty;
        }

        [Serializable]
        private sealed class FamilyDetailDto
        {
            public string id = string.Empty;
            public string name = string.Empty;
            public List<VariantDto> variants = new List<VariantDto>();
        }

        [Serializable]
        private sealed class PresetDto
        {
            public string id = string.Empty;
            public string name = string.Empty;
            public int outfits;
            public string blueprintId = string.Empty;
            public string lastUpload = string.Empty;
        }

        [Serializable]
        private sealed class PresetAssignDto
        {
            public string guid = string.Empty;
            public string target = string.Empty;
        }

        [Serializable]
        private sealed class PresetListDto
        {
            public string baseKey = string.Empty;
            public string baseName = string.Empty;
            public List<PresetDto> presets = new List<PresetDto>();
            public List<PresetAssignDto> assignments = new List<PresetAssignDto>();
        }

        private static string WindowAvatarGuid()
        {
            var window = AvatarWardrobeWindow.Instance;
            return window == null ? string.Empty : window.AvatarGuid ?? string.Empty;
        }

        private static string ActiveAvatarGuid()
        {
            var avatar = AvatarWardrobeCatalog.GetAvatarRecord(SceneAvatar);
            if (avatar != null) return avatar.guid ?? string.Empty;
            return WindowAvatarGuid();
        }

        private static StateDto GetState()
        {
            // Discover legacy presets before the sidebar and modal request their assignments.
            // Reuse discovery until the avatar hierarchy changes; polling never generates icons.
            if (SceneAvatar != null && !EditorApplication.isPlayingOrWillChangePlaymode)
                ShiroTools.OutfitBatchUploader.WebEngine();
            AvatarWardrobeCatalog.RecoverMissingIndex();
            AvatarWardrobeCatalog.PollExternalUpdates();
            // Settles the dirty flag even when a run changed nothing on disk
            // (no catalog rewrite, so no reload to reconcile against above).
            AvatarWardrobeCatalog.ReconcileDirty();
            var progress = AvatarWardrobeCatalog.ExternalProgress();
            var avatarName = SceneAvatar == null ? string.Empty : SceneAvatar.name;
            var detail = new StateDto
            {
                separateAvatarUploads = AvatarWardrobePresets.SeparateAvatarUploads ? 1 : 0,
                sdkLoggedIn = VRC.Core.APIUser.IsLoggedIn,
                sdkUploadReady = ShiroTools.OutfitBatchUploader.WardrobeUploadReadinessError() == null,
                wardrobeMode = ReadWorkflow().wardrobeMode,
                selectedPreset = ReadWorkflow().selectedPreset,
                workflowPresets = AvatarWardrobePresets.PresetsForBase(WorkflowBaseKey()).Select(p => new PresetDto { id = p.id, name = p.name }).ToList(),
                projectPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "..")),
                scenePath = SceneAvatar == null ? "" : SceneAvatar.gameObject.scene.path,
                sceneTargets = SceneTargets().Select(a => new TargetChoice { id = a.GetInstanceID(), name = a.name,
                    scene = a.gameObject.scene.path, identity = GlobalObjectId.GetGlobalObjectIdSlow(a).ToString() }).ToArray(),
                avatarName = avatarName,
                avatarInstanceId = SceneAvatar == null ? 0 : SceneAvatar.GetInstanceID(),
                avatarGuid = ActiveAvatarGuid(),
            };
            var avatar = AvatarWardrobeCatalog.GetRecord(detail.avatarGuid);
            detail.avatarLabel = avatarName;
            detail.avatarSourceGuid = AvatarWardrobeCatalog.AvatarSourceGuid(SceneAvatar);
            detail.avatarBaseEditable = !string.IsNullOrEmpty(AvatarWardrobeCatalog.AvatarOverrideKey(SceneAvatar));
            detail.avatarOverrideGuid = AvatarWardrobeCatalog.AvatarOverrideGuid(SceneAvatar);
            detail.baseAvatars = AvatarWardrobeCatalog.CanonicalBaseChoices()
                .Select(record => new BaseAvatarChoice { guid = record.guid, name = record.displayName, path = record.assetPath }).ToList();
            detail.families = CachedFamilies().Count;
            EnsureCounts();
            detail.outfits = outfitsCountCache;
            detail.avatars = avatarsCountCache;
            detail.indexPhase = progress.phase ?? "discovery";
            detail.indexing = AvatarWardrobeCatalog.ExternalRunning || progress.running ? 1 : 0;
            detail.done = progress.done;
            detail.total = progress.total;
            detail.dirty = AvatarWardrobeCatalog.DirtyCount;
            RefreshPreviewVersions();
            UpdatePreviewCounts();
            detail.hiBaked = hiBakedCache;
            detail.hiTotal = hiTotalCache;
            detail.session = serverSession;
            detail.previewEpoch = previewRevision;
            try { detail.ai = WardrobeMetadataProvider.Current.IsAvailable ? 1 : 0; }
            catch (Exception) { detail.ai = 0; }
            // Catalog identity for client caches: any reindex, correction, or
            // base-list edit changes it, telling the page to drop cached data.
            detail.epoch = AvatarWardrobeCatalog.CatalogEpoch + "|" +
                AvatarWardrobeCatalog.OverridesVersion + "|" + AvatarWardrobeCatalog.BaseAvatarStamp;
            return detail;
        }

        private static List<WardrobeFamily> CachedFamilies()
        {
            var key = AvatarWardrobeCatalog.CatalogEpoch + "|" + AvatarWardrobeCatalog.OverridesVersion;
            if (familiesCache == null || !string.Equals(key, familiesCacheKey, StringComparison.Ordinal))
            {
                familiesCache = AvatarWardrobeCatalog.Families();
                familiesCacheKey = key;
                matchesCache.Clear();
                familyShopCache.Clear();
                familyProductCache.Clear();
            }
            return familiesCache;
        }

        private static void EnsureCounts()
        {
            var families = CachedFamilies();
            if (string.Equals(countsCacheKey, familiesCacheKey, StringComparison.Ordinal)) return;
            countsCacheKey = familiesCacheKey;
            var outfits = 0;
            foreach (var family in families) outfits += family.variants.Count;
            outfitsCountCache = outfits + CachedCandidates().Sum(family => family.variants.Count);
            avatarsCountCache = AvatarWardrobeCatalog.Avatars().Count;
        }

        private static List<WardrobeFamily> CachedCandidates()
        {
            var key = AvatarWardrobeCatalog.CatalogEpoch + "|" + AvatarWardrobeCatalog.OverridesVersion;
            if (candidatesCache == null || !string.Equals(key, candidatesCacheKey, StringComparison.Ordinal))
            {
                candidatesCache = AvatarWardrobeCatalog.CandidateFamilies();
                candidatesCacheKey = key;
            }
            return candidatesCache;
        }

        private static HashSet<string> InstalledGuids()
        {
            if (installedCache != null && (DateTime.UtcNow - installedCacheAt).TotalSeconds < 2)
                return installedCache;
            var installed = new HashSet<string>();
            if (SceneAvatar != null)
                foreach (var transform in SceneAvatar.GetComponentsInChildren<Transform>(true))
                {
                    var path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(transform.gameObject);
                    var guid = AssetDatabase.AssetPathToGUID(path);
                    if (!string.IsNullOrEmpty(guid)) installed.Add(guid);
                }
            if (installedCache == null || !installedCache.SetEquals(installed)) installedCacheVersion++;
            installedCache = installed;
            installedCacheAt = DateTime.UtcNow;
            return installed;
        }

        private static void InvalidateInstalled()
        {
            installedCache = null;
            installedCacheVersion++;
            matchesCache.Clear();
        }

        private static List<WardrobeFamily> CachedMatches(string key, Func<List<WardrobeFamily>> compute)
        {
            List<WardrobeFamily> cached;
            if (matchesCache.TryGetValue(key, out cached) && cached != null) return cached;
            var fresh = compute();
            if (matchesCache.Count >= 20) matchesCache.Clear();
            matchesCache[key] = fresh;
            return fresh;
        }

        private static string CachedFamilyShop(WardrobeFamily family)
        {
            string shop;
            if (family != null && familyShopCache.TryGetValue(family.id, out shop)) return shop;
            shop = FamilyShop(family);
            if (family != null)
            {
                if (familyShopCache.Count >= 5000) familyShopCache.Clear();
                familyShopCache[family.id] = shop;
            }
            return shop;
        }

        private static string CachedFamilyProduct(WardrobeFamily family)
        {
            string product;
            if (family != null && familyProductCache.TryGetValue(family.id, out product)) return product;
            product = FamilyProduct(family);
            if (family != null)
            {
                if (familyProductCache.Count >= 5000) familyProductCache.Clear();
                familyProductCache[family.id] = product;
            }
            return product;
        }

        private static WardrobeCompatibility CachedCompatibility(string scope, Func<WardrobeCompatibility> compute)
        {
            // Compatibility includes a localized explanation, so the locale
            // must be part of the cache key or a language switch can reuse a
            // sentence generated for the previous locale.
            var language = WardrobeStrings.RequestCode ?? WardrobeStrings.Code;
            var key = scope + "|" + language + "|" + ActiveAvatarGuid() + "|" +
                      AvatarWardrobeCatalog.CatalogEpoch + "|" + AvatarWardrobeCatalog.OverridesVersion + "|" +
                      AvatarWardrobeCatalog.BaseAvatarStamp;
            WardrobeCompatibility cached;
            if (compatCache.TryGetValue(key, out cached) && cached != null && !cached.compatibleOverride) return cached;
            var fresh = compute();
            if (compatCache.Count >= 4096) compatCache.Clear();
            compatCache[key] = fresh;
            return fresh;
        }

        // Recency for newest-first sorting: the indexer's sourceModified is
        // the prefab file's mtime (ns), so a fresh import sorts first. A
        // re-saved prefab also resurfaces; true first-seen tracking would
        // need a new catalog field + migration, skipped until asked for.
        // Unparseable/missing stamps count as oldest.
        private static long RecordRecency(WardrobeAssetRecord record)
        {
            if (record == null || string.IsNullOrEmpty(record.sourceModified)) return 0L;
            long stamp;
            return long.TryParse(record.sourceModified, out stamp) ? stamp : 0L;
        }

        private static long FamilyRecency(WardrobeFamily family)
        {
            var best = 0L;
            if (family != null)
                foreach (var variant in family.variants)
                    best = Math.Max(best, RecordRecency(variant));
            return best;
        }

        private static WardrobeAssetRecord NewestVariant(WardrobeFamily family)
        {
            WardrobeAssetRecord best = null;
            var bestStamp = -1L;
            foreach (var variant in family.variants)
            {
                var stamp = RecordRecency(variant);
                if (best == null || stamp > bestStamp)
                {
                    best = variant;
                    bestStamp = stamp;
                }
            }
            return best;
        }

        private static bool MatchesSearch(WardrobeFamily family, string query)
        {
            if (string.IsNullOrEmpty(query)) return true;
            if (family.displayName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return family.variants.Any(variant =>
                variant.displayName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                variant.assetPath.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                AvatarWardrobeCatalog.DisplayVariant(variant).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static FamilyListDto GetFamilies(Dictionary<string, string> query)
        {
            string search;
            query.TryGetValue("search", out search);
            search = (search ?? string.Empty).Trim();
            string filter;
            query.TryGetValue("filter", out filter);
            string shop;
            query.TryGetValue("shop", out shop);
            string category;
            query.TryGetValue("category", out category);
            category = (category ?? string.Empty).Trim().ToLowerInvariant();
            string hideEmpty;
            query.TryGetValue("hideEmpty", out hideEmpty);
            var needThumb = filter != "all" && hideEmpty == "1";
            string sort;
            query.TryGetValue("sort", out sort);
            sort = (sort ?? string.Empty).Trim().ToLowerInvariant();
            // Newest first = family recency (newest variant's import mtime).
            // Anything else keeps the long-standing name order.
            var recentFirst = sort == "recent" || sort == "new" || sort == "newest";
            string pageText;
            query.TryGetValue("page", out pageText);
            string sizeText;
            query.TryGetValue("pageSize", out sizeText);
            int page;
            int pageSize;
            if (!int.TryParse(pageText, out page) || page < 0) page = 0;
            if (!int.TryParse(sizeText, out pageSize) || pageSize <= 0) pageSize = 96;
            pageSize = Math.Min(pageSize, 200);

            var avatarGuid = ActiveAvatarGuid();
            var installed = InstalledGuids();
            string target;
            if (query.TryGetValue("target", out target) && !string.IsNullOrEmpty(target))
                installed = new HashSet<string>(installed.Where(guid => AvatarWardrobePresets.PrefabInstances(SceneAvatar, guid)
                    .Any(instance => AvatarWardrobePresets.ItemPreset(instance, SceneAvatar) == target)));
            // One full-catalog scan per distinct query: pages and prefetch
            // crawls reuse it. Compatibility itself stays cached per family;
            // the key mirrors that scope plus anything else the scan reads.
            var matchKey = target + "|" + string.Join(",", installed.OrderBy(g => g)) + "|" + filter + "|" + search + "|" + shop + "|" + category + "|" + hideEmpty + "|" + sort + "|" +
                             avatarGuid + "|" + AvatarWardrobeCatalog.CatalogEpoch + "|" +
                             AvatarWardrobeCatalog.OverridesVersion + "|" + AvatarWardrobeCatalog.BaseAvatarStamp + "|" +
                             installedCacheVersion + "|" + thumbDead.Count;
            var matches = CachedMatches(matchKey, () =>
            {
                var found = new List<WardrobeFamily>();
                // Untested FBX/model candidates are still browsable assets.
                // Compatibility filters below decide whether they match.
                var source = filter == "unknown"
                    ? CachedCandidates()
                    : CachedFamilies().Concat(CachedCandidates()).ToList();
                foreach (var family in source)
                {
                    if (!MatchesSearch(family, search)) continue;
                    var familyShop = CachedFamilyShop(family);
                    if (!string.IsNullOrEmpty(shop) &&
                        !string.Equals(familyShop, shop, StringComparison.Ordinal)) continue;
                    float familyConfidence;
                    var familyCategory = AvatarWardrobeCatalog.FamilyCategory(family, out familyConfidence);
                    if (!string.IsNullOrEmpty(category) && category != "all" &&
                        !string.Equals(familyCategory, category, StringComparison.Ordinal)) continue;
                    if (filter == "compatible")
                    {
                        var compatibility = CachedCompatibility(
                            "f:" + family.id,
                            () => AvatarWardrobeCatalog.BestCompatibility(family, avatarGuid));
                        if (compatibility.state != WardrobeCompatibilityState.Compatible &&
                            compatibility.state != WardrobeCompatibilityState.ProbablyCompatible) continue;
                    }
                    else if (filter == "installed")
                    {
                        if (!family.variants.Any(variant => installed.Contains(variant.guid))) continue;
                    }
                    if (needThumb && !FamilyHasThumb(family)) continue;
                    found.Add(family);
                }
                if (recentFirst && found.Count > 1)
                    found.Sort((a, b) =>
                    {
                        var recency = FamilyRecency(b).CompareTo(FamilyRecency(a));
                        return recency != 0
                            ? recency
                            : string.Compare(a.displayName, b.displayName, StringComparison.OrdinalIgnoreCase);
                    });
                return found;
            });

            var list = new FamilyListDto
            {
                total = matches.Count,
                page = page,
                pageSize = pageSize,
                pageCount = Math.Max(1, Mathf.CeilToInt(matches.Count / (float)pageSize)),
            };
            list.page = Math.Min(list.page, list.pageCount - 1);
            foreach (var family in matches.Skip(list.page * pageSize).Take(pageSize))
            {
                float familyConfidence;
                var familyCategory = AvatarWardrobeCatalog.FamilyCategory(family, out familyConfidence);
                // Recent-first spotlights the arrival: the card previews the
                // newest variant instead of the alphabetically-first one.
                var representative = recentFirst && family.variants.Count > 0
                    ? NewestVariant(family)
                    : (family.variants.Count > 0 ? family.variants[0] : null);
                var representativeGuid = representative == null ? string.Empty : representative.guid;
                var dto = new FamilyDto
                {
                    id = family.id,
                    name = family.displayName,
                    variants = family.variants.Count,
                    thumb = representativeGuid,
                    variantGuids = family.variants.Select(variant => variant.guid).ToList(),
                    hi = !string.IsNullOrEmpty(representativeGuid) && HasHiThumb(representativeGuid) ? 1 : 0,
                    shop = CachedFamilyShop(family),
                    product = CachedFamilyProduct(family),
                    phys = family.variants.Count > 0
                        ? family.variants.Max(variant => variant.physBoneCount) : 0,
                    contact = family.variants.Count > 0
                        ? family.variants.Max(variant => variant.contactCount) : 0,
                    installed = family.variants.Any(variant => installed.Contains(variant.guid)) ? 1 : 0,
                    category = familyCategory,
                    categoryConfidence = familyConfidence.ToString("0%"),
                };
                {
                    var compatibility = CachedCompatibility(
                        "f:" + family.id,
                        () => AvatarWardrobeCatalog.BestCompatibility(family, avatarGuid));
                    dto.compat = (int)compatibility.state;
                    dto.compatibleOverride = compatibility.compatibleOverride;
                    dto.compatText = CompatibilityText(compatibility);
                }
                list.items.Add(dto);
            }
            return list;
        }

        // Representative-variant thumbnail check for the hide-empty filter.
        // Disk hit = visible. Confirmed-dead or missing record = hidden.
        // Unknown (never baked) stays visible so its first bake can happen.
        private static bool FamilyHasThumb(WardrobeFamily family)
        {
            if (family == null || family.variants.Count == 0) return false;
            var guid = family.variants[0].guid;
            if (string.IsNullOrEmpty(guid)) return false;
            try
            {
                if (File.Exists(ThumbPath(guid))) return true;
            }
            catch (Exception) { }
            if (thumbDead.Contains("lo:" + guid)) return false;
            return AvatarWardrobeCatalog.GetRecord(guid) != null;
        }

        private static FamilyDetailDto GetFamily(string id, string target = null)
        {
            if (string.IsNullOrEmpty(id)) return null;
            var family = CachedFamilies()
                .Concat(CachedCandidates())
                .FirstOrDefault(candidate => candidate.id == id);
            if (family == null) return null;
            var avatarGuid = ActiveAvatarGuid();
            var installed = InstalledGuids();
            if (!string.IsNullOrEmpty(target))
                installed = new HashSet<string>(installed.Where(guid => AvatarWardrobePresets.PrefabInstances(SceneAvatar, guid)
                    .Any(instance => AvatarWardrobePresets.ItemPreset(instance, SceneAvatar) == target)));
            var detail = new FamilyDetailDto { id = family.id, name = family.displayName };
            string assignBaseKey;
            string assignBaseName;
            AvatarWardrobePresets.CurrentBase(out assignBaseKey, out assignBaseName);
            var assignByGuid = new Dictionary<string, string>();
            try
            {
                foreach (var assignment in AvatarWardrobePresets.AssignmentsForBase(assignBaseKey))
                    if (!string.IsNullOrEmpty(assignment.guid) && !assignByGuid.ContainsKey(assignment.guid))
                        assignByGuid.Add(assignment.guid, assignment.target ?? string.Empty);
            }
            catch (Exception) { }
            foreach (var variant in family.variants)
            {
                var compatibility = CachedCompatibility(
                    "o:" + variant.guid,
                    () => AvatarWardrobeCatalog.Compatibility(variant, avatarGuid));
                detail.variants.Add(new VariantDto
                {
                    guid = variant.guid,
                    assetVersion = AssetDatabase.GetAssetDependencyHash(variant.assetPath).ToString(),
                    hi = HasHiThumb(variant.guid) ? 1 : 0,
                    variant = AvatarWardrobeCatalog.DisplayVariant(variant),
                    colorway = variant.colorway ?? string.Empty,
                    shop = ShopOf(variant.assetPath),
                    product = ProductOf(variant.assetPath),
                    source = variant.assetPath,
                    category = AvatarWardrobeCatalog.EffectiveKind(variant).ToString(),
                    confidence = variant.confidence.ToString("0%"),
                    setup = variant.hasMergeArmature || variant.hasOutfitRoot ||
                            variant.hasObjectToggle || variant.hasMenuItem ? 1 : 0,
                    phys = variant.physBoneCount,
                    contact = variant.contactCount,
                    parts = variant.partGroups ?? new List<string>(),
                    mats = variant.materialNames ?? new List<string>(),
                    compat = (int)compatibility.state,
                    compatibleOverride = compatibility.compatibleOverride,
                    compatText = CompatibilityText(compatibility),
                    explanation = compatibility.explanation,
                    installed = installed.Contains(variant.guid) ? 1 : 0,
                    assigned = assignByGuid.ContainsKey(variant.guid) ? assignByGuid[variant.guid] : string.Empty,
                    assignedName = PresetDisplayName(assignByGuid.ContainsKey(variant.guid) ? assignByGuid[variant.guid] : string.Empty),
                    wear = string.IsNullOrEmpty(variant.category) ? "unknown" : variant.category,
                });
            }
            return detail;
        }

        [Serializable]
        private sealed class PrefabPresetDto
        {
            public int partToggles;
            public int partTogglesMixed;
            public string id = "";
            public string name = "";
            public List<string> paths = new List<string>();
            public List<int> instanceIds = new List<int>();
        }
        [Serializable]
        private sealed class PrefabPresetsDto
        {
            public int ok = 1;
            public List<PrefabPresetDto> presets = new List<PrefabPresetDto>();
        }
        private static PrefabPresetsDto GetPrefabPresets(string guid)
        {
            var result = new PrefabPresetsDto();
            foreach (var group in AvatarWardrobePresets.PrefabInstances(SceneAvatar, guid)
                .GroupBy(item => AvatarWardrobePresets.ItemPreset(item, SceneAvatar)))
                result.presets.Add(new PrefabPresetDto {
                    id = group.Key,
                    partToggles = group.All(OutfitToggleGenerator.HasPartToggles) ? 1 : 0,
                    partTogglesMixed = group.Any(OutfitToggleGenerator.HasPartToggles) && !group.All(OutfitToggleGenerator.HasPartToggles) ? 1 : 0,
                    name = group.Key == "common" ? "Common Preset" : AvatarWardrobePresets.GetPresetName(group.Key),
                    instanceIds = group.Select(item => item.GetInstanceID()).ToList(),
                    paths = group.Select(item => AnimationUtility.CalculateTransformPath(item.transform, SceneAvatar.transform)).ToList()
                });
            return result;
        }

        private static string WorkflowBaseKey()
        {
            string key, name;
            AvatarWardrobePresets.CurrentBase(out key, out name);
            return key;
        }

        private static InstalledListDto GetInstalled()
        {
            var list = new InstalledListDto();
            var installed = InstalledGuids();
            if (installed.Count == 0) return list;
            foreach (var family in CachedFamilies().Concat(CachedCandidates()))
            {
                foreach (var variant in family.variants)
                {
                    if (!installed.Contains(variant.guid)) continue;
                    foreach (var instance in AvatarWardrobePresets.PrefabInstances(SceneAvatar, variant.guid))
                    {
                        var target = AvatarWardrobePresets.ItemPreset(instance, SceneAvatar);
                        list.items.Add(new InstalledItemDto {
                            instanceId = instance.GetInstanceID(),
                            setupWarning = instance.GetComponent<WardrobeSetupStatus>()?.warning ?? "",
                            path = AnimationUtility.CalculateTransformPath(instance.transform, SceneAvatar.transform),
                            guid = variant.guid, family = family.displayName,
                            variant = AvatarWardrobeCatalog.DisplayVariant(variant), familyId = family.id,
                            target = target, targetName = PresetDisplayName(target)
                        });
                    }
                }
            }
            list.items.Sort((a, b) => string.Compare(a.family + " " + a.variant, b.family + " " + b.variant, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        private static string CompatibilityText(WardrobeCompatibility compatibility)
        {
            if (compatibility.compatibleOverride) return WardrobeStrings.T("compat.override");
            switch (compatibility.state)
            {
                case WardrobeCompatibilityState.Compatible: return WardrobeStrings.T("compat.ok");
                case WardrobeCompatibilityState.ProbablyCompatible: return WardrobeStrings.T("compat.likely");
                case WardrobeCompatibilityState.Incompatible: return WardrobeStrings.T("compat.no");
                default: return WardrobeStrings.T("compat.unknown");
            }
        }

        private static readonly HashSet<string> GenericFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "prefab", "prefabs", "fbx", "fbxs", "models", "model", "materials", "material",
            "mat", "mats", "textures", "texture", "tex", "shaders", "shader", "editor",
            "resources", "scenes", "scene", "avatar", "avatars", "ma",
        };

        // Shop is the top-level folder (the creator); product is the first
        // meaningful folder below it, skipping pipeline folders (Prefab, FBX,
        // Materials...) and avatar-name folders. Pure path splitting, so it
        // works on pre-v3 records with no reindex.
        private static string ShopOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            var parts = path.Split('/');
            return parts.Length > 1 ? parts[1] : string.Empty;
        }

        private static string ProductOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            var parts = path.Split('/');
            for (var i = 2; i < parts.Length - 1; i++)
            {
                var segment = parts[i];
                if (string.IsNullOrEmpty(segment)) continue;
                if (GenericFolders.Contains(segment)) continue;
                if (AvatarWardrobeCatalog.MatchBaseAvatar(segment) != null) continue;
                return segment;
            }
            return string.Empty;
        }

        private static string FamilyShop(WardrobeFamily family)
        {
            return Mode(family.variants.Select(variant => ShopOf(variant.assetPath)));
        }

        private static string FamilyProduct(WardrobeFamily family)
        {
            return Mode(family.variants.Select(variant => ProductOf(variant.assetPath)));
        }

        private static string Mode(IEnumerable<string> values)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var value in values)
            {
                if (string.IsNullOrEmpty(value)) continue;
                int count;
                counts.TryGetValue(value, out count);
                counts[value] = count + 1;
            }
            var best = string.Empty;
            var bestCount = 0;
            foreach (var entry in counts)
                if (entry.Value > bestCount)
                {
                    best = entry.Key;
                    bestCount = entry.Value;
                }
            return best;
        }

        [Serializable]
        private sealed class ShopDto
        {
            public string name = string.Empty;
            public int count;
        }

        [Serializable]
        private sealed class ShopListDto
        {
            public List<ShopDto> items = new List<ShopDto>();
        }

        private static ShopListDto GetShops()
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var family in CachedFamilies())
            {
                var shop = CachedFamilyShop(family);
                if (string.IsNullOrEmpty(shop)) continue;
                int count;
                counts.TryGetValue(shop, out count);
                counts[shop] = count + 1;
            }
            var list = new ShopListDto();
            foreach (var entry in counts.OrderByDescending(entry => entry.Value).ThenBy(entry => entry.Key))
                list.items.Add(new ShopDto { name = entry.Key, count = entry.Value });
            return list;
        }

        private static string ShortPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            var parts = path.Split('/');
            return string.Join("/", parts.Skip(Mathf.Max(0, parts.Length - 3)).ToArray());
        }

    }
}
