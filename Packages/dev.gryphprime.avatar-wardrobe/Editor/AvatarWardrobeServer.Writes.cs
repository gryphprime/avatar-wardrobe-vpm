using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using UnityEngine;

namespace OutfitToggleGenerator
{
    internal static partial class AvatarWardrobeServer
    {
        // Receipts contain serialized values only; polling never touches Unity objects.
        private sealed class WriteReceipt
        {
            internal string state = "queued", result, error;
            internal DateTime completed;
        }
        private static readonly object writeGate = new object();
        private static readonly Dictionary<string, WriteReceipt> writes = new Dictionary<string, WriteReceipt>();
        private static readonly HashSet<string> queuedWriteRoutes = new HashSet<string>(StringComparer.Ordinal)
        {
            "/api/cache_clear", "/api/install", "/api/remove", "/api/preset_remove_item", "/api/part_toggles",
            "/api/menu_groups", "/api/menu_execute", "/api/scene_execute", "/api/appearance_apply",
            "/api/appearance_tool", "/api/appearance_optimizer_apply", "/api/regenerate_toggles",
            "/api/migrate_avatar", "/api/preset_save", "/api/preset_delete", "/api/preset_assign",
            "/api/preset_show", "/api/preset_include", "/api/avatar_base", "/api/workflow",
            "/api/preset_appearance_save", "/api/preset_appearance_apply",
            "/api/batch_preset_config", "/api/batch_preset_blends", "/api/batch_preset_items",
            "/api/batch_preset_faceemo", "/api/batch_preset_from_scene", "/api/batch_outfit_set",
            "/api/batch_config_set", "/api/batch_defaults_set", "/api/batch_blendshape", "/api/batch_item", "/api/batch_faceemo"
        };
        private static void WriteMainJson<T>(HttpListenerContext context, Func<T> work, string code, bool background = false)
        {
            WriteMainJsonAsync(context, () => Task.FromResult(work()), code, background);
        }
        private static void WriteMainJsonAsync<T>(HttpListenerContext context, Func<Task<T>> work, string code, bool background = false)
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
            var id = serverSession + ":" + Guid.NewGuid().ToString("N");
            var receipt = new WriteReceipt();
            lock (writeGate)
            {
                foreach (var old in new List<string>(writes.Keys))
                    if (writes[old].completed != default(DateTime) && DateTime.UtcNow - writes[old].completed > TimeSpan.FromMinutes(30)) writes.Remove(old);
                if (writes.Count >= 2048) throw new InvalidOperationException("Write history is full. Try again later.");
                writes.Add(id, receipt);
            }
            try
            {
                dispatcher.EnqueueOperationAsync(async () =>
                {
                    lock (writeGate) receipt.state = "running";
                    var result = await prepared();
                    var json = await Task.Run(() => JsonUtility.ToJson(result));
                    if (expectedQueue.IsClosed || expectedSession != serverSession)
                        throw new OperationCanceledException("Unity stopped before confirming this change. Inspect the scene before retrying.");
                    lock (writeGate) { receipt.result = json; receipt.state = "completed"; receipt.completed = DateTime.UtcNow; }
                    return true;
                }).ContinueWith(task =>
                {
                    if (!task.IsFaulted && !task.IsCanceled) return;
                    lock (writeGate)
                    {
                        receipt.state = "failed";
                        receipt.error = task.Exception?.GetBaseException().Message ?? "Unity stopped before the write completed.";
                        receipt.completed = DateTime.UtcNow;
                    }
                }, TaskScheduler.Default);
            }
            catch { lock (writeGate) writes.Remove(id); throw; }
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
                if (id == null || !writes.TryGetValue(id, out var receipt))
                { WriteText(context, 409, "text/plain", "Write receipt unavailable after a restart or expiry. Refresh and check Unity before retrying; the change may have applied."); return true; }
                json = "{\"state\":" + JsonString(receipt.state) + ",\"result\":" + (receipt.result ?? "null") + ",\"error\":" + JsonString(receipt.error) + "}";
            }
            WriteText(context, 200, "application/json", json); return true;
        }
    }
}
