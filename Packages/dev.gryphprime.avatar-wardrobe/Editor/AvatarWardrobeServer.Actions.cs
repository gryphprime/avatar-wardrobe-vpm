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

// Explicit avatar edits and existing name/upload job integration.
namespace OutfitToggleGenerator
{
    internal static partial class AvatarWardrobeServer
    {
        private static NameJobDto StartNameJob(string familyId)
        {
            var result = new NameJobDto();
            var family = CachedFamilies()
                .Concat(CachedCandidates())
                .FirstOrDefault(candidate => candidate.id == familyId);
            if (family == null) return new NameJobDto { message = WardrobeStrings.T("err.notfound") };
            var provider = WardrobeMetadataProvider.Current;
            if (!provider.IsAvailable)
                return new NameJobDto { message = WardrobeStrings.T("name.unavailable") };
            var jobId = Guid.NewGuid().ToString("N");
            lock (nameJobsLock) nameJobs[jobId] = new NameJobState();
            var avatar = AvatarWardrobeCatalog.GetRecord(ActiveAvatarGuid());
            var avatarName = avatar == null ? (SceneAvatar == null ? string.Empty : SceneAvatar.name) : avatar.displayName;
            try
            {
                provider.CleanFamilyName(
                    avatarName,
                    family,
                    name =>
                    {
                        var clean = string.IsNullOrEmpty(name) ? string.Empty : name.Trim();
                        if (string.IsNullOrEmpty(clean))
                        {
                            lock (nameJobsLock) nameJobs[jobId] = new NameJobState { done = true, message = WardrobeStrings.T("name.empty") };
                            return;
                        }
                        foreach (var variant in family.variants)
                        {
                            var assetOverride = AvatarWardrobeCatalog.OverrideFor(variant.guid);
                            AvatarWardrobeCatalog.SetOverride(
                                variant.guid,
                                assetOverride == null ? -1 : assetOverride.kind,
                                clean,
                                assetOverride == null ? null : assetOverride.variantName);
                        }
                        familiesCache = null;
                        lock (nameJobsLock) nameJobs[jobId] = new NameJobState { done = true, ok = true, name = clean, message = WardrobeStrings.T("name.renamed", clean) };
                    },
                    error =>
                    {
                        lock (nameJobsLock) nameJobs[jobId] = new NameJobState { done = true, message = error };
                    });
            }
            catch (Exception exception)
            {
                lock (nameJobsLock) nameJobs[jobId] = new NameJobState { done = true, message = exception.Message };
            }
            result.ok = 1;
            result.job = jobId;
            return result;
        }

        private static NameResultDto GetNameResult(Dictionary<string, string> query)
        {
            string job;
            query.TryGetValue("job", out job);
            NameJobState state;
            lock (nameJobsLock)
            {
                if (string.IsNullOrEmpty(job) || !nameJobs.TryGetValue(job, out state))
                    return new NameResultDto { message = WardrobeStrings.T("name.unknownjob") };
                if (!state.done) return new NameResultDto { pending = 1 };
                nameJobs.Remove(job);
            }
            return new NameResultDto { ok = state.ok ? 1 : 0, message = state.message, name = state.name ?? string.Empty };
        }

        // ---- Variant upload as separate avatar (v1: single variant, Windows) ----
        // TODO(v1): busy/notfound strings below are plain English; move them to
        // lang.json once upload keys are added (see WardrobeStrings).

        [Serializable]
        private sealed class UploadJobDto
        {
            public int ok;
            public string message;
            public string job = string.Empty;
        }

        [Serializable]
        private sealed class UploadResultDto
        {
            public int pending;
            public int ok;
            public string message;
            public string blueprintId = string.Empty;
            public int isNew;
        }

        [Serializable]
        private sealed class UploadStatusDto
        {
            public string blueprintId = string.Empty;
            public string lastUpload = string.Empty;
        }

        private sealed class UploadJobState
        {
            public bool done;
            public bool ok;
            public string message;
            public string blueprintId;
            public bool isNew;
        }

        private static readonly Dictionary<string, UploadJobState> uploadJobs = new Dictionary<string, UploadJobState>();
        private static readonly object uploadJobsLock = new object();
        [Serializable]
        private sealed class SceneUploadReview
        {
            public int ok, avatarId;
            public string name, blueprintId, message;
            public bool isNew;
        }
        private static CancellationTokenSource sceneUploadCancellation;
        private static string sceneUploadJob;
        internal static bool SceneUploadActive => sceneUploadJob != null;

        private static SceneUploadReview ReviewSceneUpload()
        {
            var avatar = SceneAvatar;
            var result = new SceneUploadReview();
            if (AvatarWardrobePresets.SeparateAvatarUploads) { result.message = "Use single-avatar mode for Upload Avatar."; return result; }
            if (avatar == null || EditorUtility.IsPersistent(avatar)) { result.message = "Select a scene avatar first."; return result; }
            if (EditorApplication.isPlayingOrWillChangePlaymode) { result.message = "Leave Play Mode first."; return result; }
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64) { result.message = "This first version supports PC. Switch the build target to Windows in Unity."; return result; }
            if (ShiroTools.OutfitBatchUploader.TryGetWardrobeBuilder(out var sdk) &&
                (sdk.BuildState == VRC.SDKBase.Editor.SdkBuildState.Building || sdk.UploadState == VRC.SDKBase.Editor.SdkUploadState.Uploading))
            { result.message = "The VRChat SDK is already building or uploading."; return result; }
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                if (string.IsNullOrEmpty(UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).path))
                { result.message = "Save untitled scenes before building. AW does not save your scenes automatically."; return result; }
            result.avatarId = avatar.GetInstanceID();
            result.name = avatar.name;
            result.blueprintId = avatar.GetComponent<VRC.Core.PipelineManager>()?.blueprintId ?? "";
            result.isNew = string.IsNullOrEmpty(result.blueprintId);
            result.message = ShiroTools.OutfitBatchUploader.WardrobeUploadReadinessError();
            result.ok = result.message == null ? 1 : 0;
            return result;
        }

        private static UploadJobDto StartSceneUpload(Dictionary<string, string> query)
        {
            var review = ReviewSceneUpload();
            if (review.ok == 0) return new UploadJobDto { message = review.message };
            query.TryGetValue("avatarId", out var id);
            query.TryGetValue("blueprintId", out var expectedBlueprint);
            if (id != review.avatarId.ToString() || expectedBlueprint != review.blueprintId)
                return new UploadJobDto { message = "The avatar or Blueprint ID changed. Close this panel and review it again." };
            query.TryGetValue("check", out var check);
            query.TryGetValue("name", out var name);
            bool buildOnly = check == "1";
            query.TryGetValue("consent", out var consent);
            if (!buildOnly && consent != "1")
                return new UploadJobDto { message = "Confirm the copyright ownership checkbox before uploading." };
            if (!buildOnly && review.isNew && string.IsNullOrWhiteSpace(name))
                return new UploadJobDto { message = "Enter a name for the new avatar." };
            if (uploadRunning || UploadTargetLocked || ShiroTools.OutfitBatchUploader.BatchActiveNow)
                return new UploadJobDto { message = "An upload or batch is already running." };
            string thumbnail = null;
            var avatar = SceneAvatar;
            var job = Guid.NewGuid().ToString("N");
            lock (uploadJobsLock)
            {
                uploadRunning = true;
                uploadJobs[job] = new UploadJobState { message = "Preparing avatar copy…" };
                sceneUploadJob = job;
                sceneUploadCancellation = new CancellationTokenSource();
            }
            RunSceneUpload(job, avatar, review.blueprintId, name, thumbnail, buildOnly, sceneUploadCancellation.Token);
            return new UploadJobDto { ok = 1, job = job };
        }

        private static ResultDto CancelSceneUpload(string job)
        {
            lock (uploadJobsLock)
            {
                if (job != sceneUploadJob || sceneUploadCancellation == null)
                    return new ResultDto { message = "No matching active avatar upload." };
                sceneUploadCancellation.Cancel();
                return new ResultDto { ok = 1, message = "Cancellation requested. A build already in progress may need to finish." };
            }
        }

        private static async void RunSceneUpload(string job, VRC.SDK3.Avatars.Components.VRCAvatarDescriptor source,
            string blueprint, string name, string thumbnail, bool buildOnly, CancellationToken cancellation)
        {
            var targetLock = LockUploadTarget();
            void Progress(string message) { lock (uploadJobsLock) uploadJobs[job].message = message; }
            try
            {
                var result = await AvatarWardrobeUpload.UploadSceneAvatarAsync(source, blueprint, name, thumbnail, buildOnly, cancellation, Progress);
                lock (uploadJobsLock) uploadJobs[job] = new UploadJobState { done = true, ok = result.ok,
                    message = result.message, blueprintId = result.blueprintId, isNew = result.isNew };
            }
            catch (Exception error)
            {
                lock (uploadJobsLock) uploadJobs[job] = new UploadJobState { done = true,
                    message = error is OperationCanceledException ? "Cancelled." : error.Message };
            }
            finally
            {
                targetLock.Dispose();
                lock (uploadJobsLock) { uploadRunning = false; sceneUploadJob = null; sceneUploadCancellation.Dispose(); sceneUploadCancellation = null; }
            }
        }

        private static bool uploadRunning;

        private static UploadJobDto StartUpload(string guid, string preset)
        {
            lock (uploadJobsLock)
            {
                if (uploadRunning)
                    return new UploadJobDto { message = "An upload is already running — wait for it to finish." };
            }
            var cleanPreset = (preset ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(cleanPreset))
            {
                if (AvatarWardrobePresets.GetPreset(cleanPreset) == null)
                    return new UploadJobDto { message = WardrobeStrings.T("preset.notfound") };
                if (SceneAvatar == null)
                    return new UploadJobDto { message = WardrobeStrings.T("install.noavatar") };
                var presetJobId = Guid.NewGuid().ToString("N");
                lock (uploadJobsLock) uploadJobs[presetJobId] = new UploadJobState();
                RunPresetUploadJob(presetJobId, cleanPreset);
                return new UploadJobDto { ok = 1, job = presetJobId };
            }
            var outfit = AvatarWardrobeCatalog.GetRecord(guid);
            if (outfit == null) return new UploadJobDto { message = WardrobeStrings.T("err.notfound") };
            if (AvatarWardrobeCatalog.EffectiveKind(outfit) != WardrobeAssetKind.Outfit)
                return new UploadJobDto { message = WardrobeStrings.T("install.onlyoutfits") };
            if (SceneAvatar == null)
                return new UploadJobDto { message = WardrobeStrings.T("install.noavatar") };
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
                return new UploadJobDto { message = "Switch Unity's build target to Windows first (File > Build Settings)." };
            var jobId = Guid.NewGuid().ToString("N");
            lock (uploadJobsLock) uploadJobs[jobId] = new UploadJobState();
            RunUploadJob(jobId, guid);
            return new UploadJobDto { ok = 1, job = jobId };
        }

        private static async void RunUploadJob(string jobId, string guid)
        {
            // Assumption: started on Unity's main thread via RunOnMain, where the
            // Editor installs a SynchronizationContext — so the awaits inside
            // UploadVariantAsync resume on the main thread and Unity APIs stay legal.
            try
            {
                lock (uploadJobsLock) uploadRunning = true;
                var outcome = await AvatarWardrobeUpload.UploadVariantAsync(guid);
                lock (uploadJobsLock) uploadJobs[jobId] = new UploadJobState
                {
                    done = true,
                    ok = outcome.ok,
                    message = outcome.message,
                    blueprintId = outcome.blueprintId ?? string.Empty,
                    isNew = outcome.isNew,
                };
            }
            catch (Exception exception)
            {
                lock (uploadJobsLock) uploadJobs[jobId] = new UploadJobState { done = true, message = exception.Message };
            }
            finally
            {
                lock (uploadJobsLock) uploadRunning = false;
            }
        }

        private static async void RunPresetUploadJob(string jobId, string presetId)
        {
            // Same main-thread assumption as RunUploadJob: started via
            // RunOnMain, so UploadPresetAsync resumes on the main thread.
            try
            {
                lock (uploadJobsLock) uploadRunning = true;
                var outcome = await AvatarWardrobePresets.UploadPresetAsync(presetId);
                lock (uploadJobsLock) uploadJobs[jobId] = new UploadJobState
                {
                    done = true,
                    ok = outcome.ok,
                    message = outcome.message,
                    blueprintId = outcome.blueprintId ?? string.Empty,
                    isNew = outcome.isNew,
                };
            }
            catch (Exception exception)
            {
                lock (uploadJobsLock) uploadJobs[jobId] = new UploadJobState { done = true, message = exception.Message };
            }
            finally
            {
                lock (uploadJobsLock) uploadRunning = false;
            }
        }

        private static UploadResultDto GetUploadResult(Dictionary<string, string> query)
        {
            string job;
            query.TryGetValue("job", out job);
            UploadJobState state;
            lock (uploadJobsLock)
            {
                if (string.IsNullOrEmpty(job) || !uploadJobs.TryGetValue(job, out state))
                    return new UploadResultDto { message = WardrobeStrings.T("name.unknownjob") };
                if (!state.done) return new UploadResultDto { pending = 1, message = state.message };
                uploadJobs.Remove(job);
            }
            return new UploadResultDto
            {
                ok = state.ok ? 1 : 0,
                message = state.message,
                blueprintId = state.blueprintId ?? string.Empty,
                isNew = state.isNew ? 1 : 0,
            };
        }

        private static UploadStatusDto GetUploadStatus(string guid, string preset)
        {
            var cleanPreset = (preset ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(cleanPreset))
            {
                var presetStatus = AvatarWardrobePresets.GetPresetStatus(cleanPreset);
                return new UploadStatusDto
                {
                    blueprintId = presetStatus.blueprintId ?? string.Empty,
                    lastUpload = presetStatus.lastUpload ?? string.Empty,
                };
            }
            var status = AvatarWardrobeUpload.GetStatus(guid);
            return new UploadStatusDto
            {
                blueprintId = status.blueprintId ?? string.Empty,
                lastUpload = status.lastUpload ?? string.Empty,
            };
        }

        private static string PresetDisplayName(string target)
        {
            if (string.IsNullOrEmpty(target) || target == AvatarWardrobePresets.CommonTarget) return string.Empty;
            try { return AvatarWardrobePresets.GetPresetName(target); }
            catch (Exception) { return string.Empty; }
        }

        private static PresetListDto GetPresets()
        {
            if (SceneAvatar != null && !EditorApplication.isPlayingOrWillChangePlaymode)
                ShiroTools.OutfitBatchUploader.WebEngine(false);
            string baseKey;
            string baseName;
            AvatarWardrobePresets.CurrentBase(out baseKey, out baseName);
            var list = new PresetListDto { baseKey = baseKey ?? string.Empty, baseName = baseName ?? string.Empty };
            try
            {
                foreach (var preset in AvatarWardrobePresets.PresetsForBase(baseKey))
                {
                    var status = AvatarWardrobePresets.GetPresetStatus(preset.id);
                    list.presets.Add(new PresetDto
                    {
                        id = preset.id ?? string.Empty,
                        name = preset.name ?? string.Empty,
                        outfits = AvatarWardrobePresets.CountAssigned(preset.id, baseKey),
                        blueprintId = status.blueprintId ?? string.Empty,
                        lastUpload = status.lastUpload ?? string.Empty,
                    });
                }
                foreach (var assignment in AvatarWardrobePresets.AssignmentsForBase(baseKey))
                    list.assignments.Add(new PresetAssignDto
                    {
                        guid = assignment.guid ?? string.Empty,
                        target = assignment.target ?? string.Empty,
                    });
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Avatar Wardrobe presets failed: " + exception.Message);
            }
            return list;
        }

        // UI preferences deliberately live apart from preset/scene configuration.
        [Serializable]
        private sealed class WorkflowPreferences
        {
            public string wardrobeMode = "one-avatar";
            public string selectedPreset = "common";
        }
        private const string WorkflowPath = "ProjectSettings/AvatarWardrobeUI.json";
        private static WorkflowPreferences ReadWorkflow()
        {
            if (!File.Exists(WorkflowPath)) return new WorkflowPreferences();
            return JsonUtility.FromJson<WorkflowPreferences>(File.ReadAllText(WorkflowPath)) ?? new WorkflowPreferences();
        }
        private static ResultDto SaveWorkflow(string mode, string selected)
        {
            var value = ReadWorkflow();
            if (mode != null && mode != "one-avatar" && mode != "multi-avatar")
                return new ResultDto { message = "Unknown avatar workflow." };
            if (mode != null) value.wardrobeMode = mode;
            if (!string.IsNullOrEmpty(selected)) value.selectedPreset = selected;
            File.WriteAllText(WorkflowPath + ".tmp", JsonUtility.ToJson(value, true));
            if (File.Exists(WorkflowPath)) File.Replace(WorkflowPath + ".tmp", WorkflowPath, null);
            else File.Move(WorkflowPath + ".tmp", WorkflowPath);
            return new ResultDto { ok = 1 };
        }

        private static ResultDto SavePreset(string id, string name)
        {
            var saved = AvatarWardrobePresets.SavePreset(id ?? string.Empty, name ?? string.Empty);
            if (!saved.ok) return new ResultDto { message = saved.message };
            if (string.IsNullOrEmpty(id) && ReadWorkflow().wardrobeMode == "multi-avatar")
            {
                AvatarWardrobePresets.ShowInUnity(saved.preset.id, SceneAvatar);
                SaveWorkflow(null, saved.preset.id);
            }
            InvalidateInstalled();
            return new ResultDto { ok = 1, message = WardrobeStrings.T("preset.saved", saved.preset.name), id = saved.preset.id };
        }

        private static ResultDto DeletePreset(string id)
        {
            if (SceneAvatar == null || string.IsNullOrEmpty(id) || id == AvatarWardrobePresets.CommonTarget)
                return new ResultDto { message = "Select a named preset to remove." };
            AvatarWardrobePresets.CurrentBase(out var key, out var unused);
            var preset = AvatarWardrobePresets.GetPreset(id);
            if (preset == null || preset.baseKey != key) return new ResultDto { message = "Preset not found." };
            return EditAvatar("Remove wardrobe preset", () =>
            {
                foreach (var root in AvatarWardrobePresets.SceneMembers(preset, SceneAvatar))
                    if (root != null) AvatarWardrobeCatalog.Remove(root);
                var deleted = AvatarWardrobePresets.DeletePreset(id);
                if (!deleted.ok) return new ResultDto { message = deleted.message };
                return new ResultDto { ok = 1, message = WardrobeStrings.T("preset.deleted", deleted.preset.name) };
            });
        }

        private static ResultDto AssignPreset(string guid, string target)
        {
            if (SceneAvatar == null)
                return new ResultDto { message = WardrobeStrings.T("install.noavatar") };
            var outfit = AvatarWardrobeCatalog.GetRecord(guid);
            if (outfit == null) return new ResultDto { message = WardrobeStrings.T("err.notfound") };
            if (!AvatarWardrobeCatalog.IsBrowsablePrefab(outfit))
                return new ResultDto { message = WardrobeStrings.T("install.onlyoutfits") };
            string baseKey;
            string baseName;
            AvatarWardrobePresets.CurrentBase(out baseKey, out baseName);
            var clean = (target ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(clean) && clean != AvatarWardrobePresets.CommonTarget)
            {
                var preset = AvatarWardrobePresets.GetPreset(clean);
                if (preset == null || preset.baseKey != baseKey)
                    return new ResultDto { message = WardrobeStrings.T("preset.notfound") };
            }
            AvatarWardrobePresets.SetAssignment(guid, baseKey, clean);
            InvalidateInstalled();
            return new ResultDto { ok = 1, message = WardrobeStrings.T("preset.assigned") };
        }

        private static ResultDto Install(string guid, bool allow, bool createToggles, bool switchVariant, string target, string menuGroup = "")
        {
            if (SceneAvatar == null)
                return new ResultDto { message = WardrobeStrings.T("install.noavatar") };
            var outfit = AvatarWardrobeCatalog.GetRecord(guid);
            if (outfit == null) return new ResultDto { message = WardrobeStrings.T("err.notfound") };
            if (!AvatarWardrobeCatalog.IsBrowsablePrefab(outfit))
                return new ResultDto { message = WardrobeStrings.T("install.onlyoutfits") };
            var cleanTarget = (target ?? string.Empty).Trim();
            string assignBaseKey = string.Empty;
            if (!string.IsNullOrEmpty(cleanTarget))
            {
                string assignBaseName;
                AvatarWardrobePresets.CurrentBase(out assignBaseKey, out assignBaseName);
                if (cleanTarget != AvatarWardrobePresets.CommonTarget)
                {
                    var preset = AvatarWardrobePresets.GetPreset(cleanTarget);
                    if (preset == null || preset.baseKey != assignBaseKey)
                        return new ResultDto { message = WardrobeStrings.T("preset.notfound") };
                }
            }
            if (!string.IsNullOrEmpty(menuGroup) &&
                !AvatarWardrobePresets.MenuGroups(cleanTarget).Any(group => group.id == menuGroup))
                return new ResultDto { message = "The selected Menu Group no longer exists. Select a group and try again." };
            return EditAvatar("Install wardrobe outfit", () =>
            {
                if (!string.IsNullOrEmpty(cleanTarget) && cleanTarget != AvatarWardrobePresets.CommonTarget)
                    AvatarWardrobePresets.EnsureSceneHolder(cleanTarget);
                var result = AvatarWardrobeCatalog.Install(SceneAvatar, outfit, allow, false, false, string.IsNullOrEmpty(cleanTarget) ? null : cleanTarget);
                if (!result.success) return new ResultDto { message = result.message };
                if (switchVariant)
                {
                    var family = CachedFamilies().FirstOrDefault(candidate =>
                        candidate.variants.Any(variant => variant.guid == outfit.guid));
                    if (family != null)
                        foreach (var variant in family.variants)
                        {
                            if (variant.guid == outfit.guid) continue;
                            foreach (var sibling in AvatarWardrobePresets.PrefabInstances(SceneAvatar, variant.guid)
                                .Where(item => string.IsNullOrEmpty(cleanTarget) || AvatarWardrobePresets.ItemPreset(item, SceneAvatar) == cleanTarget))
                                AvatarWardrobeCatalog.Remove(sibling);
                        }
                }
                // Explicit Add to preset actions place the item in that group. Mode changes never reparent items.
                if (result.instance != null && !string.IsNullOrEmpty(cleanTarget))
                {
                    Transform parent = SceneAvatar.transform;
                    if (cleanTarget != AvatarWardrobePresets.CommonTarget)
                    {
                        var preset = AvatarWardrobePresets.GetPreset(cleanTarget);
                        if (!string.IsNullOrEmpty(preset.legacyPath))
                        {
                            parent = SceneAvatar.transform.Find(preset.legacyPath);
                            if (parent == null) throw new InvalidOperationException("The preset folder is missing from this scene.");
                        }
                    }
                    if (result.instance.transform != parent && !parent.IsChildOf(result.instance.transform) &&
                        result.instance.transform.parent != parent)
                    {
                        // Generated per-item controls store hierarchy paths. Remove those controls
                        // before this explicit move; the preset selector is rebuilt below.
                        OutfitToggleGenerator.RemoveWardrobeOutfit(SceneAvatar, result.instance);
                        Undo.SetTransformParent(result.instance.transform, parent, "Add to preset");
                    }
                }
                // Settings are saved last; failures revert scene edits as well.
                if (!string.IsNullOrEmpty(cleanTarget))
                    AvatarWardrobePresets.SetAssignment(guid, assignBaseKey, cleanTarget);
                OutfitToggleGenerator.SyncPresetSelection(SceneAvatar, AvatarWardrobePresets.SeparateAvatarUploads);
                if (!string.IsNullOrEmpty(menuGroup))
                    AvatarWardrobePresets.UpdateMenuGroup(cleanTarget, menuGroup, null,
                        AnimationUtility.CalculateTransformPath(result.instance.transform, SceneAvatar.transform), guid, "assign");
                if (createToggles) OutfitToggleGenerator.GeneratePartToggles(SceneAvatar, result.instance);
                else OutfitToggleGenerator.RemovePartToggles(result.instance);
                InvalidateInstalled();
                return new ResultDto { ok = 1, message = result.message };
            });
        }

        private static ResultDto SetPartToggles(string guid, string target, bool enabled)
        {
            if (SceneAvatar == null) return new ResultDto { message = "Select an avatar first." };
            var instances = AvatarWardrobePresets.PrefabInstances(SceneAvatar, guid)
                .Where(item => AvatarWardrobePresets.ItemPreset(item, SceneAvatar) == target).ToList();
            if (instances.Count == 0) return new ResultDto { message = "The prefab is not installed in this preset." };
            return EditAvatar(enabled ? "Generate prefab part toggles" : "Remove prefab part toggles", () =>
            {
                foreach (var instance in instances)
                    if (enabled) OutfitToggleGenerator.GeneratePartToggles(SceneAvatar, instance);
                    else OutfitToggleGenerator.RemovePartToggles(instance);
                return new ResultDto { ok = 1, message = enabled ? "Part toggles generated." : "Part toggles removed." };
            });
        }

        private static ResultDto RemovePresetItem(string guid, string target, string itemPath)
        {
            if (SceneAvatar == null) return new ResultDto { message = WardrobeStrings.T("install.noavatar") };
            if (string.IsNullOrEmpty(target)) return new ResultDto { message = "Select a preset to remove the item from." };
            var instances = AvatarWardrobePresets.PrefabInstances(SceneAvatar, guid)
                .Where(item => AvatarWardrobePresets.ItemPreset(item, SceneAvatar) == target).ToList();
            if (!string.IsNullOrEmpty(itemPath))
            {
                var item = SceneAvatar.transform.Find(itemPath);
                if (item == null || item == SceneAvatar.transform || AvatarWardrobePresets.ItemPreset(item.gameObject, SceneAvatar) != target)
                    return new ResultDto { message = "The item is no longer in this preset." };
                if (!string.IsNullOrEmpty(guid) && AssetDatabase.AssetPathToGUID(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(item.gameObject)) != guid)
                    return new ResultDto { message = "The item changed. Refresh the preset before removing it." };
                instances = new List<GameObject> { item.gameObject };
            }
            if (instances.Count == 0) return new ResultDto { message = "This prefab is not installed in the selected preset." };
            // Never let an item action delete the preset container itself.
            var preset = AvatarWardrobePresets.GetPreset(target);
            if (preset != null && instances.Any(item => AnimationUtility.CalculateTransformPath(item.transform, SceneAvatar.transform) == preset.legacyPath))
                return new ResultDto { message = "Remove items inside the preset, not the preset folder." };
            return EditAvatar("Remove item from preset", () =>
            {
                var paths = instances.Select(item => AnimationUtility.CalculateTransformPath(item.transform, SceneAvatar.transform)).ToList();
                foreach (var instance in instances) AvatarWardrobeCatalog.Remove(instance);
                AvatarWardrobePresets.ForgetRemovedItems(target, paths);
                OutfitToggleGenerator.SyncPresetSelection(SceneAvatar, AvatarWardrobePresets.SeparateAvatarUploads);
                OutfitToggleGenerator.SyncMenuGroups(SceneAvatar);
                return new ResultDto { ok = 1, message = "Removed from " + (target == "common" ? "Common Preset" : preset?.name ?? target) + "." };
            });
        }

        private static ResultDto Remove(string guid)
        {
            if (SceneAvatar == null)
                return new ResultDto { message = WardrobeStrings.T("install.noavatar") };
            var outfit = AvatarWardrobeCatalog.GetRecord(guid);
            if (outfit == null) return new ResultDto { message = WardrobeStrings.T("err.notfound") };
            GameObject instance;
            if (!AvatarWardrobeCatalog.TryFindInstalled(SceneAvatar, outfit, out instance) || instance == null)
                return new ResultDto { message = WardrobeStrings.T("install.notinstalled") };
            return EditAvatar("Remove wardrobe outfit", () =>
            {
                AvatarWardrobeCatalog.Remove(instance);
                string baseKey, baseName;
                AvatarWardrobePresets.CurrentBase(out baseKey, out baseName);
                AvatarWardrobePresets.SetAssignment(guid, baseKey, string.Empty);
                OutfitToggleGenerator.SyncPresetSelection(SceneAvatar, AvatarWardrobePresets.SeparateAvatarUploads);
                return new ResultDto { ok = 1, message = WardrobeStrings.T("install.removed") };
            });
        }

        private static ResultDto EditAvatar(string label, Func<ResultDto> edit)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return new ResultDto { message = "Leave Play Mode before editing the avatar." };
            // Unity Undo covers scene objects, not ProjectSettings files. Snapshot those
            // for exception rollback; ordinary user Undo still applies to scene changes only.
            var settings = AvatarWardrobePresets.CaptureSettings();
            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(label);
            try
            {
                var result = edit();
                if (result.ok != 1) Undo.RevertAllDownToGroup(group);
                else Undo.CollapseUndoOperations(group);
                return result;
            }
            catch (Exception error)
            {
                try { Undo.RevertAllDownToGroup(group); }
                catch (Exception rollback) { Debug.LogError("Wardrobe scene rollback failed: " + rollback); }
                try { AvatarWardrobePresets.RestoreSettings(settings); }
                catch (Exception rollback) { Debug.LogError("Wardrobe preset rollback failed: " + rollback); }
                Debug.LogException(error);
                return new ResultDto { message = "Avatar edit failed and rollback was attempted: " + error.Message };
            }
            finally { InvalidateInstalled(); }
        }

        private static ResultDto StartIndex(bool full)
        {
            if (AvatarWardrobeCatalog.ExternalRunning)
                return new ResultDto { message = WardrobeStrings.T("index.running") };
            if (!AvatarWardrobeCatalog.RunExternalIndexer(full))
                return new ResultDto { message = AvatarWardrobeCatalog.LastIndexError ?? "Could not start the indexer." };
            return new ResultDto { ok = 1, message = WardrobeStrings.T(full ? "index.started.full" : "index.started.inc") };
        }

    }
}
