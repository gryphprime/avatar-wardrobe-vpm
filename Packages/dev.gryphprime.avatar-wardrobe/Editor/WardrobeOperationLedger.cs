using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace OutfitToggleGenerator
{
    [Serializable] internal sealed class WardrobeOperation
    {
        public string id, type;
        public Target target;
        public Precondition precondition;
        public Payload payload;
        [Serializable] internal sealed class Target { public string projectId, sceneGuid, avatarId, session, scopeId; public int avatarInstanceId; }
        [Serializable] internal sealed class Precondition { public string observedRevision, afterOperationId; }
        [Serializable] internal sealed class Payload
        {
            public string variantId, assetVersion, instanceId, previewToken, undoToken, view, menuGroup, itemPath;
            public bool addCopy, allowUnverified, createToggles, before;
            public float zoom = 1;
        }
    }
    [Serializable] internal sealed class WardrobeOperationReceipt
    {
        public string id, state, error, waitingReason;
        public bool cancelRequested;
        public WardrobeOperation command;
        public Outcome result;
        [Serializable] internal sealed class Outcome
        {
            public string confirmedRevision, snapshotKey, message, sourceRevision, visualRevision, captureManifestPath, recipeRevision, environmentRevision;
            public bool unsaved;
            public string[] affectedInstanceIds;
            public int addedInstanceId, removedInstanceId;
            public string addedGlobalObjectId, removedGlobalObjectId, removedVariantId, undoToken, undidOperationId;
            public WardrobeTryOnWorker.Prepared preview;
        }
    }

    // The bridge journal is deliberately smaller than the desktop intent queue. A receipt is written
    // BEFORE dispatch. Interrupted or old-session success is never evidence that the scene survived.
    internal sealed class WardrobeOperationLedger
    {
        [Serializable] private sealed class Store { public int version = 1; public List<WardrobeOperationReceipt> receipts = new List<WardrobeOperationReceipt>(); }
        private readonly object gate = new object();
        private readonly string path, session, project;
        private Store store;
        internal string Error { get; private set; }
        internal bool HasPendingMutations { get { lock (gate) return store.receipts.Any(x =>
            (x.state == "queued" || x.state == "running") &&
            (x.command.type == "wear-outfit" || x.command.type == "replace-outfit" || x.command.type == "remove-outfit" || x.command.type == "undo-operation")); } }
        internal WardrobeOperationLedger(string path, string project, string session)
        {
            this.path = path; this.project = project; this.session = session;
            try
            {
                store = File.Exists(path) ? JsonUtility.FromJson<Store>(File.ReadAllText(path)) : new Store();
                if (store == null || store.version != 1 || store.receipts == null || store.receipts.Any(x => x == null || x.command?.target == null))
                    throw new InvalidDataException("Invalid operation journal.");
                foreach (var entry in store.receipts.Where(x => x.command.target.session != session &&
                    (x.state == "queued" || x.state == "running" || x.state == "succeeded")))
                {
                    entry.state = "needs-review";
                    entry.error = "Unity restarted. Compare the scene with this receipt before retrying; unsaved edits may have been lost.";
                }
                // Old-session commands can never be accepted as new, even after detailed receipts expire.
                var old = store.receipts.Where(x => x.command.target.session != session).ToList();
                foreach (var expired in old.Take(Math.Max(0, old.Count - 512))) store.receipts.Remove(expired);
                Save();
            }
            catch (Exception e) { Error = "The operation journal cannot be read safely. Preserve it and review before editing: " + e.Message; store = new Store(); }
        }
        internal WardrobeOperationReceipt Accept(WardrobeOperation command, out bool fresh)
        {
            Validate(command, project);
            lock (gate)
            {
                fresh = false;
                if (Error != null) throw new InvalidOperationException(Error);
                var prior = store.receipts.FirstOrDefault(x => x.id == command.id);
                if (prior != null)
                {
                    if (JsonUtility.ToJson(prior.command) != JsonUtility.ToJson(command)) throw new InvalidOperationException("Operation ID already has a different command.");
                    return Copy(prior);
                }
                if (command.target.session != session) throw new InvalidOperationException("This command belongs to an expired Unity session. Review the current target before creating a new command.");
                if (store.receipts.Count(x => x.command.target.session == session) >= 2048)
                    throw new InvalidOperationException("This session's operation journal is full. Restart Unity to retain safe retry protection.");
                var entry = new WardrobeOperationReceipt { id = command.id, state = "queued", command = Clone(command), waitingReason = "Waiting for Unity" };
                store.receipts.Add(entry);
                try { Save(); } catch { store.receipts.Remove(entry); throw; }
                fresh = true;
                return Copy(entry);
            }
        }
        internal WardrobeOperationReceipt Get(string id)
        {
            lock (gate) return Copy(store.receipts.FirstOrDefault(x => x.id == id)) ??
                new WardrobeOperationReceipt { id = id, state = "needs-review", error = Error ?? "Unknown or expired operation receipt. Do not replay it automatically." };
        }
        internal WardrobeOperationReceipt Change(string id, string state, WardrobeOperationReceipt.Outcome result = null, string error = null)
        {
            lock (gate)
            {
                var entry = store.receipts.FirstOrDefault(x => x.id == id);
                if (entry == null) return Get(id);
                // A completed receipt cannot be changed by a late executor or a cancellation request.
                if (entry.state != "queued" && entry.state != "running") return Copy(entry);
                entry.state = state; entry.result = result; entry.error = error; entry.waitingReason = "";
                try { Save(); } catch { entry.state = "needs-review"; entry.error = Error; }
                return Copy(entry);
            }
        }
        internal WardrobeOperationReceipt Cancel(string id)
        {
            lock (gate)
            {
                var entry = store.receipts.FirstOrDefault(x => x.id == id);
                if (entry == null) return Get(id);
                if (entry.state == "queued") return Change(id, "cancelled");
                if (entry.state == "running") { entry.cancelRequested = true; Save(); }
                return Copy(entry);
            }
        }
        private void Save()
        {
            try { WardrobeAtomicFile.WriteText(path, JsonUtility.ToJson(store)); }
            catch (Exception e) { Error = "The operation journal could not persist its receipt. Inspect the scene before retrying: " + e.Message; throw; }
        }
        private static WardrobeOperation Clone(WardrobeOperation value) => JsonUtility.FromJson<WardrobeOperation>(JsonUtility.ToJson(value));
        private static WardrobeOperationReceipt Copy(WardrobeOperationReceipt value) => value == null ? null : JsonUtility.FromJson<WardrobeOperationReceipt>(JsonUtility.ToJson(value));
        internal static void Validate(WardrobeOperation c, string project)
        {
            if (c == null || !Guid.TryParseExact(c.id, "D", out _) || c.target == null || c.precondition == null || c.payload == null)
                throw new ArgumentException("A typed command, UUID, target, precondition and payload are required.");
            if (c.type != "wear-outfit" && c.type != "replace-outfit" && c.type != "remove-outfit" && c.type != "prepare-preview" && c.type != "capture-source" && c.type != "render-snapshot" && c.type != "undo-operation")
                throw new ArgumentException("Unsupported operation type.");
            if (c.target.projectId != project || string.IsNullOrEmpty(c.target.session) || c.target.avatarInstanceId == 0 ||
                string.IsNullOrEmpty(c.target.avatarId) || string.IsNullOrEmpty(c.target.scopeId) || string.IsNullOrEmpty(c.precondition.observedRevision))
                throw new ArgumentException("The command target and observed revision are incomplete or belong to another project.");
            if (!string.IsNullOrEmpty(c.precondition.afterOperationId) && (!Guid.TryParseExact(c.precondition.afterOperationId, "D", out _) || c.precondition.afterOperationId == c.id))
                throw new ArgumentException("Invalid predecessor identity.");
            if (c.type != "render-snapshot" && c.type != "prepare-preview" && c.type != "capture-source" && c.type != "undo-operation" && string.IsNullOrEmpty(c.payload.variantId))
                throw new ArgumentException("Choose a resolved outfit variant.");
            if ((c.type == "replace-outfit" || c.type == "remove-outfit") && string.IsNullOrEmpty(c.payload.instanceId))
                throw new ArgumentException("Choose the exact worn instance.");
            if (!string.IsNullOrEmpty(c.payload.variantId) && !System.Text.RegularExpressions.Regex.IsMatch(c.payload.variantId, "\\A[0-9a-fA-F]{32}\\z"))
                throw new ArgumentException("Invalid variant identity.");
            if (c.type == "undo-operation" && !Guid.TryParseExact(c.payload.undoToken, "D", out _))
                throw new ArgumentException("A session-only undo token is required.");
            if (c.type == "render-snapshot" && (string.IsNullOrEmpty(c.payload.previewToken) ||
                (c.payload.view != "front" && c.payload.view != "three-quarter" && c.payload.view != "back")))
                throw new ArgumentException("Choose a prepared snapshot and supported view.");
            if (float.IsNaN(c.payload.zoom) || float.IsInfinity(c.payload.zoom) || c.payload.zoom < 0 || c.payload.zoom > 3 || JsonUtility.ToJson(c).Length > 32768)
                throw new ArgumentException("Invalid or oversized command payload.");
        }
    }
}
