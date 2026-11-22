using System;
using System.Collections.Generic;
using System.Net;
using System.IO;
using System.Threading;
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
            "/api/item_parts", "/api/cache_clear", "/api/install", "/api/remove", "/api/preset_remove_item", "/api/part_toggles", "/api/item_settings",
            "/api/menu_groups", "/api/menu_execute", "/api/scene_execute", "/api/appearance_apply",
            "/api/appearance_tool", "/api/appearance_optimizer_apply", "/api/regenerate_toggles",
            "/api/migrate_avatar", "/api/preset_save", "/api/preset_delete", "/api/preset_assign",
            "/api/preset_show", "/api/preset_play", "/api/preset_include", "/api/avatar_base", "/api/workflow",
            "/api/preset_appearance_save", "/api/preset_appearance_apply",
            "/api/batch_preset_config", "/api/batch_preset_blends", "/api/batch_preset_items",
            "/api/batch_preset_faceemo", "/api/batch_preset_from_scene", "/api/batch_outfit_set",
            "/api/batch_import", "/api/batch_config_set", "/api/batch_defaults_set", "/api/batch_blendshape", "/api/batch_item", "/api/batch_faceemo",
            "/api/preset_copy"
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
            if (!requestWritesAvatar || context.Request.Headers["X-Wardrobe-Queue"] != "1" || context.Request.HttpMethod != "POST" ||
                !queuedWriteRoutes.Contains(context.Request.Url.AbsolutePath))
            {
                var direct = PrepareMainWork(work, code);
                long mainMs = 0;
                // Measure only the synchronous main-thread part, so slow logs separate
                // Unity work from time spent waiting in the queue.
                Func<Task<T>> action = () =>
                {
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    try { return direct(); }
                    finally { Interlocked.Exchange(ref mainMs, timer.ElapsedMilliseconds); }
                };
                var task = requestWritesAvatar ? dispatcher.EnqueueOperationAsync(action) : dispatcher.Invoke(action, background);
                try { WriteJson(context, 200, task.GetAwaiter().GetResult()); }
                finally { requestMainMs += Interlocked.Read(ref mainMs); }
                return;
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
        // ---- Reads that never wait on a busy Unity ----
        // Reads that need the main thread (state, worn list, family, memberships, menu groups)
        // answer from their last result when Unity has not served them within a moment, while
        // the fresh read keeps running in the background and replaces the cached copy. The page
        // never blocks on an import, compile, long edit or preview bake.
        private sealed class StaleCapture { internal string key; internal int state; } // 0 open, 1 handler wrote, 2 stale served
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<HttpListenerContext, StaleCapture> staleCaptures =
            new System.Runtime.CompilerServices.ConditionalWeakTable<HttpListenerContext, StaleCapture>();
        private static readonly Dictionary<string, KeyValuePair<DateTime, string>> staleReads = new Dictionary<string, KeyValuePair<DateTime, string>>(StringComparer.Ordinal);
        private static readonly HashSet<string> staleRefreshing = new HashSet<string>(StringComparer.Ordinal);
        private static readonly object staleGate = new object();
        private static readonly HashSet<string> staleRoutes = new HashSet<string>(StringComparer.Ordinal)
        {
            "/api/state", "/api/installed", "/api/family", "/api/families", "/api/prefab_presets", "/api/menu_groups",
            "/api/batch_state", "/api/presets", "/api/shops", "/api/item_parts"
        };
        private const int StaleWaitMs = 350;
        private static bool StaleEligible(HttpListenerRequest request) =>
            request.HttpMethod == "GET" && staleRoutes.Contains(request.Url.AbsolutePath);

        private static void HandleFreshOrStale(HttpListenerContext context, string session)
        {
            // Same host/origin checks as Handle before any cached answer leaves the server.
            if (!ValidateRequest(context)) return;
            var key = context.Request.RawUrl;
            KeyValuePair<DateTime, string> cached;
            bool have, refreshing;
            lock (staleGate)
            {
                have = staleReads.TryGetValue(key, out cached) && DateTime.UtcNow - cached.Key < TimeSpan.FromMinutes(10);
                refreshing = staleRefreshing.Contains(key);
                if (!refreshing) staleRefreshing.Add(key);
            }
            // One fresh read per URL at a time; everyone else gets the last answer.
            if (refreshing && have) { WriteStale(context, cached.Value); return; }
            var capture = new StaleCapture { key = key };
            staleCaptures.Add(context, capture);
            var task = Task.Run(() =>
            {
                requestSession = session; requestAvatarId = 0; requestWritesAvatar = false; requestMainMs = 0; requestFailure = null;
                try { Handle(context); }
                finally { if (!refreshing) lock (staleGate) staleRefreshing.Remove(key); }
            });
            if (!have || task.Wait(StaleWaitMs) || Interlocked.CompareExchange(ref capture.state, 2, 0) != 0)
            {
                task.GetAwaiter().GetResult();
                return;
            }
            WriteStale(context, cached.Value);
            // The fresh read finishes later and only refreshes the cache.
            task.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }
        // Called from WriteText: true when the body should be written to the response.
        private static bool CaptureRead(HttpListenerContext context, int status, string mime, string body)
        {
            if (!staleCaptures.TryGetValue(context, out var capture)) return true;
            var write = Interlocked.CompareExchange(ref capture.state, 1, 0) != 2;
            if (status == 200 && mime == "application/json" && body != null && body.Length < 4 * 1024 * 1024)
                lock (staleGate)
                {
                    staleReads[capture.key] = new KeyValuePair<DateTime, string>(DateTime.UtcNow, body);
                    if (staleReads.Count > 256)
                    {
                        string oldest = null; var when = DateTime.MaxValue;
                        foreach (var entry in staleReads) if (entry.Value.Key < when) { when = entry.Value.Key; oldest = entry.Key; }
                        if (oldest != null) staleReads.Remove(oldest);
                    }
                }
            return write;
        }
        private static void WriteStale(HttpListenerContext context, string body)
        {
            context.Response.Headers["X-Wardrobe-Stale"] = "1";
            WriteTextDirect(context, 200, "application/json", body);
        }
        // A new session (Unity reload) or avatar invalidates every cached answer.
        private static void ForgetStaleReads() { lock (staleGate) staleReads.Clear(); }

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
