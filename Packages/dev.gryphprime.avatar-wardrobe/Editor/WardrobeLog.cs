using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace OutfitToggleGenerator
{
    // Bounded, asynchronous diagnostic log; never performs disk I/O on Unity's thread.
    internal static class WardrobeLog
    {
        private static readonly object Gate = new object();
        private static readonly Queue<string> Pending = new Queue<string>();
        private static readonly Dictionary<string, DateTime> Recent = new Dictionary<string, DateTime>();
        private static string path;
        private static bool draining;
        internal static void Initialize(string projectRoot)
        {
            lock (Gate) path = Path.Combine(projectRoot, "Library", "AvatarWardrobe", "wardrobe.log");
        }
        internal static string Clean(string value, int limit = 240)
        {
            value = (value ?? "").Replace('\r', ' ').Replace('\n', ' ');
            return value.Length > limit ? value.Substring(0, limit) + "…" : value;
        }
        internal static void Write(string category, string message, string repeatKey = null)
        {
            lock (Gate)
            {
                if (path == null) return;
                var now = DateTime.UtcNow;
                if (repeatKey != null)
                {
                    DateTime previous;
                    if (Recent.TryGetValue(repeatKey, out previous) && (now - previous).TotalSeconds < 30) return;
                    if (Recent.Count >= 256) Recent.Clear();
                    Recent[repeatKey] = now;
                }
                if (Pending.Count >= 256) return;
                Pending.Enqueue(now.ToString("o") + " [" + Clean(category) + "] " + Clean(message, 1200));
                if (draining) return;
                draining = true;
                ThreadPool.QueueUserWorkItem(_ => Drain());
            }
        }
        private static void Drain()
        {
            while (true)
            {
                string file;
                string[] lines;
                lock (Gate)
                {
                    if (Pending.Count == 0) { draining = false; return; }
                    file = path; lines = Pending.ToArray(); Pending.Clear();
                }
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    if (File.Exists(file) && new FileInfo(file).Length >= 1024 * 1024)
                    {
                        var previous = file + ".1";
                        if (File.Exists(previous)) File.Delete(previous);
                        File.Move(file, previous);
                    }
                    File.AppendAllLines(file, lines);
                }
                catch (Exception) { /* Diagnostics must never break an action or spam the Console. */ }
            }
        }
    }
}
