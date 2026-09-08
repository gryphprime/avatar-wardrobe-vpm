using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace OutfitToggleGenerator
{
    internal static partial class AvatarWardrobeServer
    {
        [Serializable] internal sealed class OperationContext
        {
            public string projectId, session, sceneGuid, avatarId, scopeId, revision, waitingReason;
            public int avatarInstanceId;
            public bool unsaved;
        }
        [Serializable] private sealed class OperationEnvelope { public WardrobeOperation command; public string executeRevision; }
        private static WardrobeOperationLedger operationLedger;
        private static string operationContextJson = "{}", snapshotDirectory;
        private static double nextOperationContext;
        private static string activeOperationId;

        private static void InitializeOperations()
        {
            operationLedger = new WardrobeOperationLedger(Path.Combine(serverProjectPath, "UserSettings", "AvatarWardrobeOperations.json"), serverProjectPath, serverSession);
            snapshotDirectory = Path.Combine(serverProjectPath, "Library", "AvatarWardrobe", "snapshots");
            PublishOperationContext(true);
        }
        private static void AssertOperationsSettled()
        {
            if (operationLedger != null && operationLedger.HasPendingMutations)
                throw new InvalidOperationException("Wait for pending outfit changes to settle before reviewing or building the avatar.");
        }
        private static string StableObjectId(UnityEngine.Object obj) => obj == null ? "" : GlobalObjectId.GetGlobalObjectIdSlow(obj).ToString();
        private static string Revision(VRCAvatarDescriptor avatar)
        {
            if (avatar == null) return "";
            var text = WardrobeTryOnWorker.SourceFingerprint(avatar);
            foreach (var name in new[] { "AvatarWardrobePresets.json", "AvatarWardrobeOverrides.json", "ShiroOutfit_data.json" })
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
        private static void PublishOperationContext(bool force = false)
        {
            if (!force && EditorApplication.timeSinceStartup < nextOperationContext) return;
            nextOperationContext = EditorApplication.timeSinceStartup + 2;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                var last = JsonUtility.FromJson<OperationContext>(Volatile.Read(ref operationContextJson)) ?? new OperationContext();
                last.waitingReason = OperationWaitingReason();
                Volatile.Write(ref operationContextJson, JsonUtility.ToJson(last)); return;
            }
            var avatar = SceneAvatar;
            var context = new OperationContext
            {
                projectId = serverProjectPath, session = serverSession, scopeId = "common", waitingReason = OperationWaitingReason(),
                avatarId = StableObjectId(avatar), avatarInstanceId = avatar == null ? 0 : avatar.GetInstanceID(),
                sceneGuid = avatar == null ? "" : AssetDatabase.AssetPathToGUID(avatar.gameObject.scene.path),
                revision = Revision(avatar), unsaved = avatar != null && avatar.gameObject.scene.isDirty
            };
            Volatile.Write(ref operationContextJson, JsonUtility.ToJson(context));
        }
        private static bool HandleOperations(HttpListenerContext context, string path)
        {
            if (path == "/api/operation_context")
            { WriteText(context, 200, "application/json", Volatile.Read(ref operationContextJson)); return true; }
            if (path == "/api/operation_result" || path == "/api/operation_cancel")
            {
                Query(context.Request.Url.Query).TryGetValue("id", out var id);
                WriteJson(context, 200, path.EndsWith("_cancel", StringComparison.Ordinal) ? operationLedger.Cancel(id) : operationLedger.Get(id)); return true;
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
            var ledger = operationLedger;
            var receipt = ledger.Accept(command, out var fresh);
            if (!fresh) return receipt;
            var id = command.id;
            try
            {
                // Execute a fresh journal copy: neither the caller nor the returned receipt owns it.
                dispatcher.EnqueueOperation(() => ExecuteOperation(ledger.Get(id).command)).ContinueWith(task =>
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
        private static WardrobeOperationReceipt ExecuteOperation(WardrobeOperation c)
        {
            if (operationLedger.Get(c.id).state != "queued") return operationLedger.Get(c.id);
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
                if (Revision(avatar) != expected) throw new InvalidOperationException("The avatar or wardrobe settings changed since this command was prepared. Refresh and review.");
                var asset = string.IsNullOrEmpty(c.payload.variantId) ? "" : AssetDatabase.GUIDToAssetPath(c.payload.variantId);
                if (!string.IsNullOrEmpty(c.payload.variantId) && (string.IsNullOrEmpty(asset) ||
                    (!string.IsNullOrEmpty(c.payload.assetVersion) && AssetDatabase.GetAssetDependencyHash(asset).ToString() != c.payload.assetVersion)))
                    throw new InvalidOperationException("The outfit asset changed or is missing. Refresh and review.");
                var exact = ResolveOperationInstance(c);
                if (c.type == "remove-outfit" && AssetDatabase.AssetPathToGUID(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(exact)) != c.payload.variantId)
                    throw new InvalidOperationException("The worn copy is no longer the requested variant.");
                if (operationLedger.Change(c.id, "running").state != "running") return operationLedger.Get(c.id);
                activeOperationId = c.id;
                var outcome = new WardrobeOperationReceipt.Outcome { sourceRevision = expected, affectedInstanceIds = new string[0] };
                if (c.type == "capture-source")
                {
                    var captured = WardrobeShadowCapture.Capture(avatar, c.payload.variantId, exact, recipe: WardrobeAppearanceRecipe.Resolve(avatar, c.target.scopeId, c.payload.createToggles, c.payload.menuGroup));
                    outcome.captureManifestPath = captured.manifestPath;
                    outcome.sourceRevision = captured.sourceRevision;
                    outcome.recipeRevision = captured.recipeRevision;
                    outcome.environmentRevision = captured.environmentRevision;
                    outcome.message = "Source captured for the isolated snapshot worker.";
                }
                else if (c.type == "prepare-preview")
                {
                    outcome.preview = WardrobeTryOnWorker.Prepare(avatar, c.payload.variantId, exact, recipe: WardrobeAppearanceRecipe.Resolve(avatar, c.target.scopeId, c.payload.createToggles, c.payload.menuGroup));
                    outcome.message = "Try-on prepared from the complete avatar. The working scene was not changed.";
                }
                else if (c.type == "render-snapshot")
                {
                    if (!WardrobeTryOnWorker.Validate(c.payload.previewToken, avatar, out var reason)) throw new InvalidOperationException(reason);
                    var key = Digest(c.payload.previewToken + ":" + c.payload.view + ":" + c.payload.before + ":" + c.payload.zoom);
                    var file = Path.Combine(snapshotDirectory, key + ".png");
                    if (!File.Exists(file))
                    {
                        var image = WardrobeTryOnWorker.RenderView(c.payload.previewToken, c.payload.view, c.payload.zoom <= 0 ? 1 : c.payload.zoom, c.payload.before);
                        WardrobeAtomicFile.WriteBytes(file, image);
                        WardrobeAtomicFile.WriteText(Path.Combine(snapshotDirectory, key + ".json"), JsonUtility.ToJson(c));
                    }
                    outcome.snapshotKey = key;
                }
                else
                {
                    var before = avatar.GetComponentsInChildren<Transform>(true).Select(x => x.gameObject.GetInstanceID()).ToArray();
                    var removedId = StableObjectId(exact);
                    var removedInstanceId = exact == null ? 0 : exact.GetInstanceID();
                    var removedVariantId = exact == null ? "" : AssetDatabase.AssetPathToGUID(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(exact));
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
                outcome.confirmedRevision = Revision(avatar); outcome.unsaved = avatar.gameObject.scene.isDirty;
                return operationLedger.Change(c.id, "succeeded", outcome);
            }
            catch (Exception e) { return operationLedger.Change(c.id, "needs-review", error: e.Message); }
            finally { activeOperationId = null; PublishOperationContext(true); }
        }
    }
}
