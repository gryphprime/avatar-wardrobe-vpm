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

namespace OutfitToggleGenerator
{
    // Localhost HTTP backend for the external wardrobe browser
    // (Assets/OutfitToggleGenerator/Web/wardrobe.html). Browsing, search, and
    // thumbnails move to the browser; Unity only does what only Unity can:
    // prefab instantiation and icon-pipeline thumbnail baking, both on the main thread.
    //
    // Mutations use same-origin POST; read endpoints use GET. Anything that
    // touches Unity APIs runs on the main thread via a dispatch queue pumped
    // by EditorApplication.update. Thumbnails persist to
    // Library/AvatarWardrobe/thumbs, keyed by its indexed dependency fingerprint.
    internal static partial class AvatarWardrobeServer
    {
        private static HttpListener listener;
        private static Thread listenerThread;
        private static WardrobeWorkQueue dispatcher;
        private static readonly SemaphoreSlim requestSlots = new SemaphoreSlim(24);
        private static string serverSession = Guid.NewGuid().ToString("N");
        private static readonly Dictionary<string, WardrobeCompatibility> compatCache =
            new Dictionary<string, WardrobeCompatibility>();
        private static readonly Dictionary<string, int> thumbSightings = new Dictionary<string, int>();
        private static readonly HashSet<string> thumbDead = new HashSet<string>();
        private static readonly HashSet<string> thumbWarned = new HashSet<string>();
        private static List<WardrobeFamily> familiesCache;
        private static string familiesCacheKey;
        private static List<WardrobeFamily> candidatesCache;
        private static string candidatesCacheKey;
        // Stale-first serving: the full-catalog filter scan (search +
        // compatibility over every family) is reused across pages and
        // prefetch crawls instead of recomputed per request. Cleared
        // whenever the catalog rebuilds; keyed per query below.
        private static readonly Dictionary<string, List<WardrobeFamily>> matchesCache =
            new Dictionary<string, List<WardrobeFamily>>();
        private static readonly Dictionary<string, string> familyShopCache =
            new Dictionary<string, string>();
        private static readonly Dictionary<string, string> familyProductCache =
            new Dictionary<string, string>();
        // Scene traversal per request stalls prefetch bursts, so installed
        // guids are reused briefly and only recomputed on expiry or after
        // an install/remove. The version bumps on every recompute so
        // installed-filter matches stay fresh.
        private static HashSet<string> installedCache;
        private static DateTime installedCacheAt = DateTime.MinValue;
        private static int installedCacheVersion;
        // Counts and hi-res file tallies are recomputed rarely instead of on
        // every 5s state poll.
        private static int outfitsCountCache;
        private static int avatarsCountCache;
        private static string countsCacheKey;
        private static int hiBakedCache;
        private static int hiTotalCache;
        private static DateTime hiCacheAt = DateTime.MinValue;

        internal static int Port { get; private set; }
        internal static string LastError { get; private set; }
        private static VRCAvatarDescriptor sceneAvatar;
        private static int uploadTargetLocks;
        internal static bool UploadTargetLocked => uploadTargetLocks > 0;
        internal static bool IsStagingAvatar(VRCAvatarDescriptor avatar)
        {
            if (avatar == null) return false;
            var path = avatar.gameObject.scene.path ?? "";
            return path.StartsWith("Assets/Generated/WardrobeUploads/", StringComparison.Ordinal) &&
                !path.StartsWith("Assets/Generated/WardrobeUploads/SourceScenes/", StringComparison.Ordinal);
        }
        private sealed class UploadTargetLock : IDisposable
        {
            private bool disposed;
            public void Dispose() { if (!disposed) { disposed = true; uploadTargetLocks--; } }
        }
        internal static IDisposable LockUploadTarget()
        {
            uploadTargetLocks++;
            return new UploadTargetLock();
        }
        internal static VRCAvatarDescriptor SceneAvatar
        {
            get { return sceneAvatar; }
            set { if (uploadTargetLocks > 0 || IsStagingAvatar(value) || sceneAvatar == value) return; sceneAvatar = value; InvalidateInstalled(); WardrobeLog.Write("avatar", value == null ? "Selected none" : "Selected " + value.name + " instance=" + value.GetInstanceID()); }
        }
        // Resolved on Start (main thread) so the listener thread can serve
        // cached bytes without ever touching Unity APIs.
        private static string htmlPath;
        private static string langPath;
        private static string thumbDir;
        private static string hiDir;
        // Baking runs only while the web UI holds a focus lease, so thumbnail
        // renders never steal the main thread during Unity work.
        private static DateTime webActiveUntil = DateTime.MinValue;
        private static readonly object webActiveLock = new object();
        [ThreadStatic] private static string requestSession;
        [ThreadStatic] private static int requestAvatarId;
        [ThreadStatic] private static bool requestWritesAvatar;
        [ThreadStatic] private static long requestMainMs;
        [ThreadStatic] private static string requestFailure;
        private static bool WebActive { get { lock (webActiveLock) { return DateTime.UtcNow < webActiveUntil; } } }
        internal static bool Running
        {
            get { return listener != null && listener.IsListening; }
        }

        internal static bool Start()
        {
            if (Running) return true;
            LastError = null;
            WardrobeLog.Initialize(Directory.GetParent(Application.dataPath).FullName);
            WardrobeLog.Write("server", "Starting; Unity " + Application.unityVersion);
            WardrobeStrings.EnsureInitialized();
            dispatcher = new WardrobeWorkQueue();
            serverSession = Guid.NewGuid().ToString("N");
            for (var port = 8909; port <= 8929; port++)
            {
                var candidate = new HttpListener();
                try
                {
                    candidate.Prefixes.Add("http://localhost:" + port + "/");
                    candidate.Start();
                    listener = candidate;
                    Port = port;
                    break;
                }
                catch (Exception exception)
                {
                    candidate.Close();
                    LastError = exception.Message;
                }
            }
            if (!Running) return false;
            htmlPath = WardrobePackagePaths.File("Web/wardrobe.html");
            langPath = WardrobePackagePaths.File("Web/lang.json");
            thumbDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "AvatarWardrobe", "thumbs"));
            hiDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "AvatarWardrobe", "thumbs512"));
            // Pipeline v5 (fingerprinted previews and explicit sRGB capture):
            // any older cache stock is wrong pixels, so wipe once and stamp.
            try
            {
                var stamp = Path.Combine(Application.dataPath, "..", "Library", "AvatarWardrobe", ".thumbpipe");
                var pipe = File.Exists(stamp) ? File.ReadAllText(stamp) : string.Empty;
                if (pipe.Trim() != "v5")
                {
                    foreach (var dir in new[] { thumbDir, hiDir })
                        if (Directory.Exists(dir)) Directory.Delete(dir, true);
                    Directory.CreateDirectory(Path.GetDirectoryName(stamp));
                    WardrobeAtomicFile.WriteText(stamp, "v5");
                }
            }
            catch (Exception) { }
            RefreshPreviewVersions();
            EditorApplication.hierarchyChanged += InvalidateInstalled;
            Undo.undoRedoPerformed += InvalidateInstalled;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting += Stop;
            EditorApplication.update += Pump;
            listenerThread = new Thread(Loop) { IsBackground = true, Name = "WardrobeServer" };
            listenerThread.Start();
            return true;
        }

        internal static void Stop()
        {
            WardrobeLog.Write("server", "Stopping");
            EditorApplication.update -= Pump;
            EditorApplication.hierarchyChanged -= InvalidateInstalled;
            Undo.undoRedoPerformed -= InvalidateInstalled;
            AssemblyReloadEvents.beforeAssemblyReload -= Stop;
            EditorApplication.quitting -= Stop;
            if (dispatcher != null) dispatcher.Close();
            try
            {
                if (listener != null) listener.Stop();
            }
            catch (Exception) { }
            try
            {
                if (listener != null) listener.Close();
            }
            catch (Exception) { }
            listener = null;
        }

        internal static string Url
        {
            get { return Running ? "http://localhost:" + Port + "/" : string.Empty; }
        }

        private static void Pump()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (dispatcher != null) dispatcher.Pump(!UploadTargetLocked && !ShiroTools.OutfitBatchUploader.BatchActiveNow && !EditorApplication.isPlayingOrWillChangePlaymode, idleBackground: BakeNextBackgroundPreview);
        }

        private static T RunOnMain<T>(Func<T> work, string requestCode, bool background = false)
        {
            var queue = dispatcher;
            if (queue == null) throw new OperationCanceledException("Wardrobe server is not running.");
            var expectedSession = requestSession;
            var expectedAvatar = requestWritesAvatar ? requestAvatarId : 0;
            var writesAvatar = requestWritesAvatar;
            long mainMs = 0;
            try { return queue.Invoke(() =>
            {
                if (!string.IsNullOrEmpty(expectedSession) && expectedSession != serverSession)
                    throw new OperationCanceledException("The Unity session changed. Refresh the wardrobe and retry.");
                if (writesAvatar && expectedAvatar != 0 && !WardrobeEditPolicy.ContextMatches(serverSession, expectedSession, SceneAvatar == null ? 0 : SceneAvatar.GetInstanceID(), expectedAvatar))
                    throw new OperationCanceledException("The target avatar changed. Review the selected avatar and retry.");
                if (writesAvatar && SceneUploadActive)
                    throw new InvalidOperationException("Wait for the avatar build/upload to finish before editing through AW.");
                var previous = WardrobeStrings.RequestCode;
                WardrobeStrings.RequestCode = requestCode;
                var timer = System.Diagnostics.Stopwatch.StartNew();
                try { return work(); }
                finally { mainMs += timer.ElapsedMilliseconds; WardrobeStrings.RequestCode = previous; }
            }, background); }
            finally { requestMainMs += mainMs; }
        }

        private static void Loop()
        {
            var active = listener;
            while (active != null && active.IsListening)
            {
                HttpListenerContext context = null;
                try
                {
                    context = active.GetContext();
                }
                catch (Exception)
                {
                    break;
                }
                // Hand off to the pool: file serves and disk-cache hits never
                // queue behind main-thread renders, and a slow request stops
                // delaying ACCEPT of the next one. Unity work still funnels
                // through RunOnMain onto the single main thread.
                if (!requestSlots.Wait(0))
                {
                    try { WriteText(context, 503, "text/plain", "Wardrobe is busy; try again shortly."); }
                    catch (Exception) { }
                    continue;
                }
                var accepted = context;
                var acceptedSession = serverSession;
                ThreadPool.QueueUserWorkItem(state =>
                {
                    var requestTimer = System.Diagnostics.Stopwatch.StartNew();
                    requestMainMs = 0; requestFailure = null;
                    try
                    {
                        requestSession = acceptedSession;
                        requestAvatarId = 0;
                        requestWritesAvatar = false;
                        Handle(accepted);
                    }
                    catch (Exception exception)
                    {
                        requestFailure = exception.GetType().Name + ": " + exception.Message;
                        try
                        {
                            WriteText(accepted, exception is TimeoutException || exception is OperationCanceledException ? 503 : 500, "text/plain", exception.Message);
                        }
                        catch (Exception) { }
                    }
                    finally { LogRequest(accepted, requestTimer.ElapsedMilliseconds); requestSession = null; requestAvatarId = 0; requestWritesAvatar = false; requestSlots.Release(); }
                });
            }
        }

        private static void LogRequest(HttpListenerContext context, long elapsedMs)
        {
            try
            {
                var route = context.Request.Url.AbsolutePath;
                if (!route.StartsWith("/api/", StringComparison.Ordinal)) return;
                bool quiet = route == "/api/active" || route == "/api/thumb" || route == "/api/nameResult";
                bool failed = requestFailure != null || context.Response.StatusCode >= 400;
                bool action = context.Request.HttpMethod == "POST" && !quiet;
                bool slow = !quiet && elapsedMs >= 2000;
                if (!failed && !action && !slow) return;
                var category = failed ? "failure" : slow ? "slow" : "action";
                var detail = route + " status=" + context.Response.StatusCode + " totalMs=" + elapsedMs +
                    " mainMs=" + requestMainMs + " avatar=" + requestAvatarId;
                var query = Query(context.Request.Url.Query);
                foreach (var key in new[] { "id", "guid", "target", "op", "enabled", "mode" })
                    if (query.TryGetValue(key, out var value)) detail += " " + key + "=" + WardrobeLog.Clean(value);
                if (failed) detail += " reason=" + WardrobeLog.Clean(requestFailure);
                WardrobeLog.Write(category, detail, failed || !action ? category + route + requestFailure : null);
            }
            catch (Exception) { }
        }

        // ---- HTTP plumbing ----

        private static Dictionary<string, string> Query(string raw)
        {
            var parsed = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(raw)) return parsed;
            foreach (var pair in raw.TrimStart('?').Split('&'))
            {
                if (string.IsNullOrEmpty(pair)) continue;
                var index = pair.IndexOf('=');
                if (index < 0) parsed[WebUtility.UrlDecode(pair)] = string.Empty;
                else parsed[WebUtility.UrlDecode(pair.Substring(0, index))] = WebUtility.UrlDecode(pair.Substring(index + 1));
            }
            return parsed;
        }

        private static void WriteJson(HttpListenerContext context, int status, object dto)
        {
            if (dto != null)
            {
                var ok = dto.GetType().GetField("ok");
                var message = dto.GetType().GetField("message")?.GetValue(dto) as string;
                if (ok != null && (Equals(ok.GetValue(dto), 0) || Equals(ok.GetValue(dto), false)) &&
                    !string.IsNullOrEmpty(message) && message != "pending") requestFailure = message;
            }
            WriteText(context, status, "application/json", JsonUtility.ToJson(dto));
        }

        private static void WriteText(HttpListenerContext context, int status, string mime, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body ?? string.Empty);
            context.Response.StatusCode = status;
            context.Response.ContentType = mime + "; charset=utf-8";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            // Never cache: a cached 202-pending or stale page would wedge the UI.
            context.Response.AddHeader("Cache-Control", "no-store");
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            context.Response.OutputStream.Close();
        }

        private static void WriteBytes(HttpListenerContext context, int status, string mime, byte[] bytes)
        {
            context.Response.StatusCode = status;
            context.Response.ContentType = mime;
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            // Never cache: today's 202-pending must not become tomorrow's image.
            context.Response.AddHeader("Cache-Control", "no-store");
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            context.Response.OutputStream.Close();
        }
    }
}
