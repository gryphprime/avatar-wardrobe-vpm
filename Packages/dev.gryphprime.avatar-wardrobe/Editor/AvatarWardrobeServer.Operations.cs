using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace OutfitToggleGenerator
{
    internal static partial class AvatarWardrobeServer
    {
        [Serializable] internal sealed class OperationContext
        {
            public string projectId, session, sceneGuid, avatarId, scopeId, revision, visualRevision, waitingReason;
            public int avatarInstanceId;
            public bool unsaved;
        }
        [Serializable] private sealed class OperationEnvelope { public WardrobeOperation command; public string executeRevision; }
        private static volatile WardrobeOperationLedger operationLedger;
        private static Task<WardrobeOperationLedger> operationInitialization;
        private static string operationContextJson = "{}", snapshotDirectory;
        private static double nextOperationContext;
        private static string activeOperationId;
        private sealed class OperationUndoCheckpoint
        {
            internal string token, operationId, label, afterRevision, sceneStructure;
            internal int group;
            internal WardrobeOperation.Target target;
            internal string presets, overrides, uploads, afterPresets, afterOverrides, afterUploads;
            internal WardrobeOperationReceipt.Outcome outcome;
        }
        // Native Undo is session-local. Only the latest proven operation group is eligible.
        private static OperationUndoCheckpoint lastOperationUndo;

        [InitializeOnLoadMethod]
        private static void ObserveOperationUndoChanges()
        {
            Undo.postprocessModifications -= InvalidateOperationUndo;
            Undo.postprocessModifications += InvalidateOperationUndo;
        }
        private static UndoPropertyModification[] InvalidateOperationUndo(UndoPropertyModification[] changes)
        {
            if (changes != null && changes.Length > 0)
            {
                lastOperationUndo = null;
                nextOperationContext = 0; // Inspector edits should invalidate the published revision promptly.
            }
            return changes;
        }
        private static string OperationSceneStructure()
        {
            var objects = new System.Collections.Generic.List<int>();
            for (var index = 0; index < UnityEngine.SceneManagement.SceneManager.sceneCount; index++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(index);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                    foreach (var component in root.GetComponentsInChildren<Component>(true))
                        if (component != null) { objects.Add(component.GetInstanceID()); objects.Add(component.gameObject.GetInstanceID()); }
            }
            return string.Join(",", objects.Distinct().OrderBy(value => value));
        }

        private static void InitializeOperations()
        {
            lastOperationUndo = null;
            contextGeneration++; cachedSourceFingerprint = ""; cachedSourceAvatar = 0;
            var project = serverProjectPath; var session = serverSession;
            operationLedger = null;
            operationInitialization = Task.Run(() => new WardrobeOperationLedger(Path.Combine(project, "UserSettings", "AvatarWardrobeOperations.json"), project, session));
            snapshotDirectory = Path.Combine(serverProjectPath, "Library", "AvatarWardrobe", "snapshots");
            PublishOperationContext(true);
        }
        private static void AssertOperationsSettled()
        {
            if (operationLedger == null) throw new InvalidOperationException("The operation journal is still initializing. Try again shortly.");
            lock (writeGate)
                if (writes.Values.Any(write => write.state == "queued" || write.state == "running"))
                    throw new InvalidOperationException("Wait for queued changes to finish before reviewing or building the avatar.");
            if (operationLedger != null && operationLedger.HasPendingMutations)
                throw new InvalidOperationException("Wait for pending outfit changes to settle before reviewing or building the avatar.");
        }
        private static string StableObjectId(UnityEngine.Object obj) => obj == null ? "" : GlobalObjectId.GetGlobalObjectIdSlow(obj).ToString();
        internal static int FingerprintGeneration => contextGeneration;
        private static async Task<string> RevisionAsync(VRCAvatarDescriptor avatar)
        {
            if (avatar == null) return "";
            var generation = contextGeneration;
            var source = await WardrobeTryOnWorker.FingerprintAsync(avatar, false);
            var project = serverProjectPath;
            var revision = await Task.Run(() =>
            {
                var text = source;
                foreach (var name in WardrobeTryOnWorker.SettingsFingerprintFiles)
                    text += ":" + WardrobeAtomicFile.HashFile(Path.Combine(project, "ProjectSettings", name));
                return Digest(text);
            });
            if (avatar == null || generation != contextGeneration)
                throw new InvalidOperationException("The avatar changed during revision inspection. Review again.");
            return revision;
        }
        private static string Revision(VRCAvatarDescriptor avatar)
        {
            if (avatar == null) return "";
            var text = WardrobeTryOnWorker.SourceFingerprint(avatar);
            foreach (var name in WardrobeTryOnWorker.SettingsFingerprintFiles)
                text += ":" + WardrobeAtomicFile.HashFile(Path.Combine(serverProjectPath, "ProjectSettings", name));
            return Digest(text);
        }
        private static string Digest(string text)
        {
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }
        private static string OperationWaitingReason()
        {
            if (EditorApplication.isCompiling) return "Compiling scripts";
            if (EditorApplication.isUpdating) return "Importing assets";
            if (EditorApplication.isPlayingOrWillChangePlaymode) return "Leave Play Mode to apply changes";
            if (importLease != null) return "Importing library files";
            if (UploadTargetLocked || ShiroTools.OutfitBatchUploader.BatchActiveNow || SceneUploadActive) return "Waiting for build or upload";
            return "";
        }
        private static Task contextPublication;
        private static int contextGeneration;
        private static string cachedSourceFingerprint = "";
        private static int cachedSourceAvatar;
        private static string cachedVisualFingerprint = "";
        private static int cachedFingerprintGeneration = -1;
        private static double nextFullFingerprint;
        private static string cachedVisualSettings = "";
        internal static string DisplaySourceFingerprint(VRCAvatarDescriptor avatar)
            => avatar != null && cachedSourceAvatar == avatar.GetInstanceID() ? cachedSourceFingerprint : "";
        private static void PublishOperationContext(bool force = false)
        {
            if (dispatcher == null || dispatcher.IsClosed) return;
            if (contextPublication != null && !contextPublication.IsCompleted) return;
            if (!force && EditorApplication.timeSinceStartup < nextOperationContext) return;
            nextOperationContext = EditorApplication.timeSinceStartup + 2;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            contextPublication = PublishOperationContextAsync();
        }
        private static async Task PublishOperationContextAsync()
        {
            var session = serverSession;
            var avatar = SceneAvatar;
            var generation = contextGeneration;
            try
            {
                var context = new OperationContext
                {
                    projectId = serverProjectPath, session = session, scopeId = "common", waitingReason = OperationWaitingReason(),
                    avatarId = StableObjectId(avatar), avatarInstanceId = avatar == null ? 0 : avatar.GetInstanceID(),
                    sceneGuid = avatar == null ? "" : AssetDatabase.AssetPathToGUID(avatar.gameObject.scene.path),
                    unsaved = avatar != null && avatar.gameObject.scene.isDirty
                };
                // Chunk scene reads across editor updates; no full-avatar traversal in Pump.
                var project = context.projectId;
                var visualSettings = await Task.Run(() => string.Join(":", WardrobeTryOnWorker.SettingsFingerprintFiles
                    .Select(name => WardrobeAtomicFile.HashFile(Path.Combine(project, "ProjectSettings", name)))));
                var reuse = cachedSourceAvatar == context.avatarInstanceId && cachedFingerprintGeneration == generation &&
                    EditorApplication.timeSinceStartup < nextFullFingerprint && !string.IsNullOrEmpty(cachedSourceFingerprint);
                var pair = reuse ? null : await WardrobeTryOnWorker.FingerprintPairAsync(avatar);
                var source = reuse ? cachedSourceFingerprint : pair[0];
                context.visualRevision = !reuse ? pair[1] : cachedVisualSettings == visualSettings ? cachedVisualFingerprint : await WardrobeTryOnWorker.FingerprintAsync(avatar, true);
                context.revision = avatar == null ? "" : await Task.Run(() =>
                {
                    var text = source;
                    foreach (var name in WardrobeTryOnWorker.SettingsFingerprintFiles)
                        text += ":" + WardrobeAtomicFile.HashFile(Path.Combine(project, "ProjectSettings", name));
                    return Digest(text);
                });
                if (session != serverSession || avatar != SceneAvatar || generation != contextGeneration) return;
                cachedSourceAvatar = context.avatarInstanceId; cachedSourceFingerprint = source;
                cachedVisualFingerprint = context.visualRevision; cachedVisualSettings = visualSettings;
                cachedFingerprintGeneration = generation;
                if (!reuse) nextFullFingerprint = EditorApplication.timeSinceStartup + 30;
                context.waitingReason = OperationWaitingReason();
                Volatile.Write(ref operationContextJson, JsonUtility.ToJson(context));
            }
            catch (Exception error) { WardrobeLog.Write("context", "Inspection will retry: " + error.Message); }
            finally { nextOperationContext = EditorApplication.timeSinceStartup + 2; }
        }
        [InitializeOnLoadMethod]
        private static void ObserveContextChanges()
        {
            ObjectChangeEvents.changesPublished -= ContextChanged;
            ObjectChangeEvents.changesPublished += ContextChanged;
            EditorApplication.projectChanged -= InvalidateContext;
            EditorApplication.projectChanged += InvalidateContext;
            Undo.undoRedoPerformed -= InvalidateContext;
            Undo.undoRedoPerformed += InvalidateContext;
        }
        private static void ContextChanged(ref ObjectChangeEventStream changes) => InvalidateContext();
        private static void InvalidateContext()
        {
            contextGeneration++;
            cachedSourceFingerprint = "";
            nextOperationContext = 0;
        }
        // HTTP request threads may wait for the startup disk read; Editor update never does.
        private static WardrobeOperationLedger ReadyOperationLedger()
        {
            var ready = operationLedger;
            if (ready != null) return ready;
            var pending = operationInitialization;
            if (pending == null) throw new InvalidOperationException("Operation journal is unavailable.");
            var loaded = pending.GetAwaiter().GetResult();
            if (!ReferenceEquals(pending, operationInitialization)) throw new OperationCanceledException("Unity restarted while opening the operation journal.");
            operationLedger = loaded;
            return loaded;
        }

        private static bool HandleOperations(HttpListenerContext context, string path)
        {
            if (path == "/api/operation_context")
            { WriteText(context, 200, "application/json", Volatile.Read(ref operationContextJson)); return true; }
            if (path == "/api/operation_result" || path == "/api/operation_cancel")
            {
                Query(context.Request.Url.Query).TryGetValue("id", out var id);
                var ledger = ReadyOperationLedger();
                WriteJson(context, 200, path.EndsWith("_cancel", StringComparison.Ordinal) ? ledger.Cancel(id) : ledger.Get(id)); return true;
            }
            if (path == "/api/snapshot")
            {
                Query(context.Request.Url.Query).TryGetValue("key", out var key);
                if (key == null || !System.Text.RegularExpressions.Regex.IsMatch(key, "\\A[0-9a-f]{64}\\z"))
                { WriteText(context, 400, "text/plain", "Invalid snapshot identity."); return true; }
                var file = Path.Combine(snapshotDirectory, key + ".png");
                if (!File.Exists(file)) WriteText(context, 404, "text/plain", "Snapshot is not cached. Request a render operation.");
                else WriteBytes(context, 200, "image/png", File.ReadAllBytes(file));
                return true;
            }
            if (path != "/api/operation_accept") return false;
            try
            {
                var bytes = new MemoryStream(); var buffer = new byte[8192]; int count;
                using (bytes)
                {
                    while ((count = context.Request.InputStream.Read(buffer, 0, buffer.Length)) != 0)
                    { if (bytes.Length + count > 65536) throw new ArgumentException("Command body exceeds 64 KiB."); bytes.Write(buffer, 0, count); }
                    var envelope = JsonUtility.FromJson<OperationEnvelope>(Encoding.UTF8.GetString(bytes.ToArray()));
                    if (envelope?.command == null) throw new ArgumentException("A command envelope is required.");
                    WriteJson(context, 202, AcceptOperation(envelope.command, requestAvatarId));
                }
            }
            catch (Exception e) { WriteJson(context, 409, new ResultDto { message = e.Message }); }
            return true;
        }
        // This typed entry point is shared by HTTP and isolated integration tests.
        internal static WardrobeOperationReceipt AcceptOperation(WardrobeOperation command, int pinnedAvatarId)
        {
            if (command?.target == null || command.target.avatarInstanceId != pinnedAvatarId)
                throw new ArgumentException("The command does not match the request's pinned avatar.");
            var ledger = ReadyOperationLedger();
            var receipt = ledger.Accept(command, out var fresh);
            if (!fresh) return receipt;
            var id = command.id;
            try
            {
                // Execute a fresh journal copy: neither the caller nor the returned receipt owns it.
                dispatcher.EnqueueOperationAsync(() => ExecuteOperation(ledger.Get(id).command)).ContinueWith(task =>
                {
                    if (task.IsFaulted || task.IsCanceled)
                        ledger.Change(id, "needs-review", error: task.Exception?.GetBaseException().Message ?? "Unity stopped before the command completed.");
                }, System.Threading.Tasks.TaskScheduler.Default);
            }
            catch (Exception e) { receipt = ledger.Change(id, "needs-review", error: "Accepted but not dispatched: " + e.Message); }
            return receipt;
        }

        private static GameObject ResolveOperationInstance(WardrobeOperation c)
        {
            var id = c.payload.instanceId;
            if (string.IsNullOrEmpty(id)) return null;
            GameObject found = null;
            if (GlobalObjectId.TryParse(id, out var global)) found = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(global) as GameObject;
            else if (int.TryParse(id, out var instance)) found = EditorUtility.InstanceIDToObject(instance) as GameObject;
            if (found == null || found == SceneAvatar.gameObject || !found.transform.IsChildOf(SceneAvatar.transform) ||
                AvatarWardrobePresets.ItemPreset(found, SceneAvatar) != c.target.scopeId)
                throw new InvalidOperationException("The exact worn instance moved or no longer belongs to this avatar and preset.");
            return found;
        }
        private static async Task<WardrobeOperationReceipt> ExecuteOperation(WardrobeOperation c)
        {
            if (operationLedger.Get(c.id).state != "queued") return operationLedger.Get(c.id);
            var ledger = operationLedger;
            var session = serverSession;
            var operationQueue = dispatcher;
            OperationUndoCheckpoint checkpoint = null;
            try
            {
                var wait = OperationWaitingReason();
                if (!string.IsNullOrEmpty(wait)) throw new InvalidOperationException(wait + ". Nothing was changed; review before retrying.");
                var avatar = SceneAvatar;
                if (avatar == null || c.target.session != serverSession || c.target.projectId != serverProjectPath ||
                    avatar.GetInstanceID() != c.target.avatarInstanceId || StableObjectId(avatar) != c.target.avatarId ||
                    AssetDatabase.AssetPathToGUID(avatar.gameObject.scene.path) != (c.target.sceneGuid ?? ""))
                    throw new InvalidOperationException("The pinned project, scene or avatar changed. Review this command.");
                var expected = c.precondition.observedRevision;
                if (!string.IsNullOrEmpty(c.precondition.afterOperationId))
                {
                    var prior = operationLedger.Get(c.precondition.afterOperationId);
                    if (prior.state != "succeeded" || prior.command == null || prior.result == null ||
                        JsonUtility.ToJson(prior.command.target) != JsonUtility.ToJson(c.target))
                        throw new InvalidOperationException("The predecessor is not a confirmed change for this exact target.");
                    expected = prior.result.confirmedRevision;
                }
                if (await RevisionAsync(avatar) != expected) throw new InvalidOperationException("The avatar or wardrobe settings changed since this command was prepared. Refresh and review.");
                var asset = string.IsNullOrEmpty(c.payload.variantId) ? "" : AssetDatabase.GUIDToAssetPath(c.payload.variantId);
                if (!string.IsNullOrEmpty(c.payload.variantId) && (string.IsNullOrEmpty(asset) ||
                    (!string.IsNullOrEmpty(c.payload.assetVersion) && AssetDatabase.GetAssetDependencyHash(asset).ToString() != c.payload.assetVersion)))
                    throw new InvalidOperationException("The outfit asset changed or is missing. Refresh and review.");
                var exact = ResolveOperationInstance(c);
                if (c.type == "remove-outfit" && AssetDatabase.AssetPathToGUID(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(exact)) != c.payload.variantId)
                    throw new InvalidOperationException("The worn copy is no longer the requested variant.");
                if ((await Task.Run(() => ledger.Change(c.id, "running"))).state != "running") return ledger.Get(c.id);
                if (operationQueue.IsClosed || session != serverSession || avatar != SceneAvatar || await RevisionAsync(avatar) != expected)
                    throw new InvalidOperationException("The target changed while the operation was being recorded. Review before retrying.");
                activeOperationId = c.id;
                var outcome = new WardrobeOperationReceipt.Outcome { sourceRevision = expected, affectedInstanceIds = new string[0] };
                if (c.type == "undo-operation")
                {
                    UndoOperation(c, outcome);
                }
                else if (c.type == "capture-source")
                {
                    var captured = await WardrobeShadowCapture.CaptureAsync(avatar, c.payload.variantId, exact, recipe: WardrobeAppearanceRecipe.Resolve(avatar, c.target.scopeId, c.payload.createToggles, c.payload.menuGroup), canContinue: () => !operationQueue.IsClosed && session == serverSession && avatar == SceneAvatar);
                    outcome.captureManifestPath = captured.manifestPath;
                    outcome.visualRevision = captured.visualRevision;
                    outcome.sourceRevision = captured.sourceRevision;
                    outcome.recipeRevision = captured.recipeRevision;
                    outcome.environmentRevision = captured.environmentRevision;
                    outcome.message = global::OutfitToggleGenerator.WardrobeStrings.T("server.source.captured.for.the.isolated.snapshot.worker");
                }
                else if (c.type == "prepare-preview")
                {
                    outcome.preview = WardrobeTryOnWorker.Prepare(avatar, c.payload.variantId, exact, recipe: WardrobeAppearanceRecipe.Resolve(avatar, c.target.scopeId, c.payload.createToggles, c.payload.menuGroup));
                    outcome.message = global::OutfitToggleGenerator.WardrobeStrings.T("server.try.on.prepared.from.the.complete.avatar.the.working.scene");
                }
                else if (c.type == "render-snapshot")
                {
                    if (!WardrobeTryOnWorker.Validate(c.payload.previewToken, avatar, out var reason)) throw new InvalidOperationException(reason);
                    var key = Digest(c.payload.previewToken + ":" + c.payload.view + ":" + c.payload.before + ":" + c.payload.zoom);
                    var file = Path.Combine(snapshotDirectory, key + ".png");
                    if (!await Task.Run(() => File.Exists(file)))
                    {
                        // Validate again after yielding; a manual Unity edit may have changed the avatar.
                        if (!WardrobeTryOnWorker.Validate(c.payload.previewToken, avatar, out var changed)) throw new InvalidOperationException(changed);
                        var image = await WardrobeTryOnWorker.RenderViewAsync(c.payload.previewToken, c.payload.view, c.payload.zoom <= 0 ? 1 : c.payload.zoom, c.payload.before);
                        await Task.Run(() =>
                        {
                            WardrobeAtomicFile.WriteBytes(file, image);
                            WardrobeAtomicFile.WriteText(Path.Combine(snapshotDirectory, key + ".json"), JsonUtility.ToJson(c));
                        });
                    }
                    outcome.snapshotKey = key;
                }
                else
                {
                    checkpoint = new OperationUndoCheckpoint
                    {
                        token = Guid.NewGuid().ToString(), operationId = c.id,
                        target = JsonUtility.FromJson<WardrobeOperation.Target>(JsonUtility.ToJson(c.target)),
                        presets = AvatarWardrobePresets.CaptureSettings(), overrides = AvatarWardrobeCatalog.CaptureOverrides(),
                        uploads = ShiroTools.OutfitProjectData.CaptureSettings()
                    };
                    var before = avatar.GetComponentsInChildren<Transform>(true).Select(x => x.gameObject.GetInstanceID()).ToArray();
                    var removesExact = exact != null && (c.type == "replace-outfit" || c.type == "remove-outfit");
                    var removedId = removesExact ? StableObjectId(exact) : "";
                    var removedInstanceId = removesExact ? exact.GetInstanceID() : 0;
                    var removedVariantId = removesExact ? AssetDatabase.AssetPathToGUID(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(exact)) : "";
                    ResultDto result;
                    if (c.type == "remove-outfit") result = RemovePresetItem(c.payload.variantId, c.target.scopeId,
                        AnimationUtility.CalculateTransformPath(exact.transform, avatar.transform), exact.GetInstanceID().ToString());
                    else result = Install(c.payload.variantId, c.payload.allowUnverified, c.payload.createToggles,
                        c.type == "replace-outfit", c.target.scopeId, c.payload.menuGroup ?? "", exact == null ? "" : exact.GetInstanceID().ToString(), c.payload.addCopy);
                    if (result.ok != 1) throw new InvalidOperationException(result.message);
                    outcome.message = result.message;
                    // Only the installed outfit root is an edit result; nested prefab parts are not independent copies.
                    var added = c.type == "remove-outfit" ? null : AvatarWardrobePresets.PrefabInstances(avatar, c.payload.variantId)
                        .FirstOrDefault(x => !before.Contains(x.GetInstanceID()) && AvatarWardrobePresets.ItemPreset(x, avatar) == c.target.scopeId);
                    outcome.addedInstanceId = added == null ? 0 : added.GetInstanceID();
                    outcome.addedGlobalObjectId = StableObjectId(added);
                    outcome.removedInstanceId = removedInstanceId;
                    outcome.removedGlobalObjectId = removedId;
                    outcome.removedVariantId = removedVariantId;
                    outcome.affectedInstanceIds = new[] { outcome.addedGlobalObjectId, removedId }.Where(x => !string.IsNullOrEmpty(x)).ToArray();
                }
                if (operationQueue.IsClosed || session != serverSession || avatar == null || avatar != SceneAvatar)
                    throw new InvalidOperationException("The Unity session or target changed while working. Review the result.");
                outcome.confirmedRevision = await RevisionAsync(avatar); outcome.unsaved = avatar.gameObject.scene.isDirty;
                if (checkpoint != null)
                {
                    Undo.FlushUndoRecordObjects();
                    checkpoint.group = Undo.GetCurrentGroup(); checkpoint.label = "Wardrobe operation " + c.id;
                    Undo.SetCurrentGroupName(checkpoint.label);
                    checkpoint.afterRevision = outcome.confirmedRevision; checkpoint.outcome = outcome;
                    checkpoint.sceneStructure = OperationSceneStructure();
                    checkpoint.afterPresets = AvatarWardrobePresets.CaptureSettings();
                    checkpoint.afterOverrides = AvatarWardrobeCatalog.CaptureOverrides();
                    checkpoint.afterUploads = ShiroTools.OutfitProjectData.CaptureSettings();
                    outcome.undoToken = checkpoint.token;
                }
                var completed = await Task.Run(() => ledger.Change(c.id, "succeeded", outcome));
                if (checkpoint != null && completed.state == "succeeded") lastOperationUndo = checkpoint;
                return completed;
            }
            catch (Exception e) { return await Task.Run(() => ledger.Change(c.id, "needs-review", error: e.Message)); }
            finally { activeOperationId = null; PublishOperationContext(true); }
        }
        private static void UndoOperation(WardrobeOperation command, WardrobeOperationReceipt.Outcome outcome)
        {
            // Flush delayed native property edits before trusting the captured group.
            Undo.FlushUndoRecordObjects();
            var checkpoint = lastOperationUndo;
            if (checkpoint == null || checkpoint.token != command.payload.undoToken ||
                JsonUtility.ToJson(checkpoint.target) != JsonUtility.ToJson(command.target))
                throw new InvalidOperationException("This Undo belongs to an expired or different scene operation. Review the current avatar.");
            if (Revision(SceneAvatar) != checkpoint.afterRevision ||
                AvatarWardrobePresets.CaptureSettings() != checkpoint.afterPresets ||
                AvatarWardrobeCatalog.CaptureOverrides() != checkpoint.afterOverrides ||
                ShiroTools.OutfitProjectData.CaptureSettings() != checkpoint.afterUploads)
                throw new InvalidOperationException("The avatar or settings changed after this operation. Nothing was undone.");
            if (Undo.GetCurrentGroup() != checkpoint.group || Undo.GetCurrentGroupName() != checkpoint.label ||
                OperationSceneStructure() != checkpoint.sceneStructure)
                throw new InvalidOperationException("Unity's latest Undo group changed. Nothing was undone; use Unity's history to review later edits.");
            // No await, callback or other mutation is allowed between these ownership guards and the exact group revert.
            lastOperationUndo = null;
            Undo.RevertAllDownToGroup(checkpoint.group);
            try { AvatarWardrobePresets.RestoreSettings(checkpoint.presets); }
            finally
            {
                try { AvatarWardrobeCatalog.RestoreOverrides(checkpoint.overrides); }
                finally { ShiroTools.OutfitProjectData.RestoreSettings(checkpoint.uploads); }
            }
            WardrobeEditHistory.Capture();
            outcome.undidOperationId = checkpoint.operationId;
            outcome.addedInstanceId = checkpoint.outcome.removedInstanceId;
            outcome.addedGlobalObjectId = checkpoint.outcome.removedGlobalObjectId;
            outcome.removedInstanceId = checkpoint.outcome.addedInstanceId;
            outcome.removedGlobalObjectId = checkpoint.outcome.addedGlobalObjectId;
            outcome.affectedInstanceIds = checkpoint.outcome.affectedInstanceIds;
            outcome.message = global::OutfitToggleGenerator.WardrobeStrings.T("server.undid.this.operation.and.restored.its.wardrobe.settings.undo.is");
            InvalidateInstalled();
        }
    }
}
