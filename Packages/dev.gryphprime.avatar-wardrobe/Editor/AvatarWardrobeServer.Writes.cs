using System;
using System.Collections.Generic;
using System.Net;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace OutfitToggleGenerator
{
    internal static partial class AvatarWardrobeServer
    {
        // Receipts contain serialized values only; polling never touches Unity objects.
        [Serializable] private sealed class WriteReceipt
        {
            public string state = "queued", result, error, fingerprint, session;
            internal DateTime completed;
        }
        private static string WriteReceiptPath(string id) => Path.Combine(serverProjectPath, "UserSettings", "AvatarWardrobeWrites", id + ".json");
        private static void SaveWrite(string id, WriteReceipt receipt) => WardrobeAtomicFile.WriteText(WriteReceiptPath(id), JsonUtility.ToJson(receipt), true);
        private static WriteReceipt LoadWrite(string id)
        {
            if (writes.TryGetValue(id, out var receipt) && receipt.session == serverSession) return receipt;
            var path = WriteReceiptPath(id);
            if (!File.Exists(path)) return null;
            receipt = JsonUtility.FromJson<WriteReceipt>(File.ReadAllText(path));
            if (receipt == null) throw new IOException("Invalid write receipt. Inspect the scene before retrying.");
            if (receipt.state == "queued" || receipt.state == "running")
            {
                receipt.state = "needs-review";
                receipt.error = global::OutfitToggleGenerator.WardrobeStrings.T("server.unity.restarted.before.confirming.this.write.inspect.the.scene.this");
                SaveWrite(id, receipt);
            }
            return receipt;
        }
        private static readonly object writeGate = new object();
        private static readonly Dictionary<string, WriteReceipt> writes = new Dictionary<string, WriteReceipt>();
        private static readonly HashSet<string> queuedWriteRoutes = new HashSet<string>(StringComparer.Ordinal)
        {
            "/api/cache_clear", "/api/install", "/api/remove", "/api/preset_remove_item", "/api/part_toggles", "/api/item_settings",
            "/api/menu_groups", "/api/menu_execute", "/api/scene_execute", "/api/appearance_apply",
            "/api/appearance_tool", "/api/appearance_optimizer_apply", "/api/regenerate_toggles",
            "/api/migrate_avatar", "/api/preset_save", "/api/preset_delete", "/api/preset_assign",
            "/api/preset_show", "/api/preset_include", "/api/avatar_base", "/api/workflow",
            "/api/preset_appearance_save", "/api/preset_appearance_apply",
            "/api/batch_preset_config", "/api/batch_preset_blends", "/api/batch_preset_items",
            "/api/batch_preset_faceemo", "/api/batch_preset_from_scene", "/api/batch_outfit_set",
            "/api/batch_import", "/api/batch_config_set", "/api/batch_defaults_set", "/api/batch_blendshape", "/api/batch_item", "/api/batch_faceemo"
        };
        private static void WriteMainJson<T>(HttpListenerContext context, Func<T> work, string code, bool background = false, string payloadIdentity = "")
        {
            WriteMainJsonAsync(context, () => Task.FromResult(work()), code, background, payloadIdentity);
        }
        private static void WriteMainJsonAsync<T>(HttpListenerContext context, Func<Task<T>> work, string code, bool background = false, string payloadIdentity = "")
        {
            var expectedSession = serverSession;
            var expectedQueue = dispatcher;
            var original = work;
            work = async () =>
            {
                var result = await original();
                if (expectedQueue.IsClosed || expectedSession != serverSession)
                    throw new OperationCanceledException("Unity stopped while applying this change. Inspect the scene before retrying.");
                return result;
            };
            if (context.Request.Headers["X-Wardrobe-Queue"] != "1" || context.Request.HttpMethod != "POST" ||
                !queuedWriteRoutes.Contains(context.Request.Url.AbsolutePath))
            {
                var action = PrepareMainWork(work, code);
                var task = requestWritesAvatar ? dispatcher.EnqueueOperationAsync(action) : dispatcher.Invoke(action, background);
                WriteJson(context, 200, task.GetAwaiter().GetResult()); return;
            }
            var prepared = PrepareMainWork(work, code);
            var id = context.Request.Headers["X-Wardrobe-Write-Id"];
            if (!Guid.TryParseExact(id, "D", out var parsedId) || parsedId.ToString("D") != id)
                throw new InvalidOperationException("A queued write requires a client-generated UUID.");
            var fingerprint = Digest(context.Request.RawUrl + "|" + context.Request.Headers["X-Wardrobe-Session"] + "|" +
                context.Request.Headers["X-Wardrobe-Avatar"] + "|" + payloadIdentity);
            var receipt = new WriteReceipt { fingerprint = fingerprint, session = serverSession };
            lock (writeGate)
            {
                var existing = LoadWrite(id);
                if (existing != null)
                {
                    if (existing.fingerprint != fingerprint) throw new InvalidOperationException("Write ID belongs to a different request.");
                    WriteText(context, 202, "application/json", "{\"writeJob\":" + JsonString(id) + "}"); return;
                }
                foreach (var old in new List<string>(writes.Keys))
                    if (writes[old].completed != default(DateTime) && DateTime.UtcNow - writes[old].completed > TimeSpan.FromMinutes(30)) writes.Remove(old);
                if (writes.Count >= 2048) throw new InvalidOperationException("Write history is full. Try again later.");
                SaveWrite(id, receipt); // Durable acceptance precedes scheduling or response.
                writes.Add(id, receipt);
            }
            try
            {
                dispatcher.EnqueueOperationAsync(async () =>
                {
                    lock (writeGate) { receipt.state = "running"; SaveWrite(id, receipt); }
                    var result = await prepared();
                    var json = await Task.Run(() => JsonUtility.ToJson(result));
                    if (expectedQueue.IsClosed || expectedSession != serverSession)
                        throw new OperationCanceledException("Unity stopped before confirming this change. Inspect the scene before retrying.");
                    lock (writeGate) { receipt.result = json; receipt.state = "completed"; receipt.completed = DateTime.UtcNow; SaveWrite(id, receipt); }
                    return true;
                }).ContinueWith(task =>
                {
                    if (!task.IsFaulted && !task.IsCanceled) return;
                    lock (writeGate)
                    {
                        receipt.state = receipt.state == "running" || receipt.state == "completed" ? "needs-review" : "failed";
                        receipt.error = task.Exception?.GetBaseException().Message ?? "Unity stopped before the write completed.";
                        receipt.completed = DateTime.UtcNow;
                        SaveWrite(id, receipt);
                    }
                }, TaskScheduler.Default);
            }
            catch { lock (writeGate) { receipt.state = "failed"; receipt.error = global::OutfitToggleGenerator.WardrobeStrings.T("server.write.could.not.be.scheduled"); SaveWrite(id, receipt); } throw; }
            WriteText(context, 202, "application/json", "{\"writeJob\":" + JsonString(id) + ",\"state\":\"queued\"}");
        }
        private static string JsonString(string value)
        {
            var text = new System.Text.StringBuilder("\"");
            foreach (var c in value ?? "")
            {
                if (c == '\\' || c == '\"') text.Append('\\').Append(c);
                else if (c < 32) text.Append("\\u").Append(((int)c).ToString("x4"));
                else text.Append(c);
            }
            return text.Append('\"').ToString();
        }
        private static bool HandleWriteResult(HttpListenerContext context, string path)
        {
            if (path != "/api/write_result") return false;
            Query(context.Request.Url.Query).TryGetValue("id", out var id);
            string json;
            lock (writeGate)
            {
                var validId = Guid.TryParseExact(id, "D", out var parsedId) && parsedId.ToString("D") == id;
                var receipt = validId ? LoadWrite(id) : null;
                if (receipt == null)
                { WriteText(context, 409, "text/plain", "Write receipt unavailable after a restart or expiry. Refresh and check Unity before retrying; the change may have applied."); return true; }
                json = WriteResultJson(receipt.state, receipt.result, receipt.error);
            }
            WriteText(context, 200, "application/json", json); return true;
        }

        internal static string WriteResultJson(string state, string result, string error)
        {
            // JsonUtility restores an unset serialized string as empty after a
            // reload. Queued/failed receipts have no result object; emit JSON
            // null so browser polling can observe their terminal state.
            return "{\"state\":" + JsonString(state) + ",\"result\":" +
                (string.IsNullOrWhiteSpace(result) ? "null" : result) +
                ",\"error\":" + JsonString(error) + "}";
        }
    }
}
