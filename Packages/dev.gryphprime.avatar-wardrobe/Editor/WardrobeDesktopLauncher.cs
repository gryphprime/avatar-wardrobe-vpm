using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
namespace OutfitToggleGenerator
{
    internal static class WardrobeDesktopLauncher
    {
        private static readonly ConcurrentQueue<Action> callbacks = new ConcurrentQueue<Action>();
        private static bool launching;
        [Serializable] private sealed class Identity { public string project; public int protocol; }
        [InitializeOnLoadMethod] private static void Initialize() { EditorApplication.update += Pump; }
        private static void Pump() { while (callbacks.TryDequeue(out var work)) work(); }
        [MenuItem("Tools/Avatar Wardrobe Desktop")]
        internal static void Open()
        {
            if (launching) return;
            var previous = SessionState.GetString("Wardrobe.DesktopUrl", "");
            if (string.IsNullOrEmpty(previous)) { StartNew(); return; }
            var project = Directory.GetParent(Application.dataPath).FullName;
            launching = true;
            Task.Run(() =>
            {
                string identity = null;
                try
                {
                    var request = (HttpWebRequest)WebRequest.Create(previous + "/api/desktop_identity");
                    request.Timeout = 2000;
                    using (var response = request.GetResponse())
                    using (var reader = new StreamReader(response.GetResponseStream())) identity = reader.ReadToEnd();
                }
                catch { }
                callbacks.Enqueue(() =>
                {
                    launching = false;
                    Identity parsed = null;
                    try { if (identity != null) parsed = JsonUtility.FromJson<Identity>(identity); } catch (ArgumentException) { }
                    if (parsed != null && parsed.project == project && parsed.protocol == AvatarWardrobeServer.DesktopProtocol) { AvatarWardrobeServer.Start(); OpenDesktopWindow(previous); }
                    else StartNew();
                });
            });
        }
        private static void StartNew()
        {
            if (!AvatarWardrobeServer.Start()) { UnityEngine.Debug.LogError(AvatarWardrobeServer.LastError); return; }
            var script = WardrobePackagePaths.File("Desktop/wardrobe_desktop.py");
            var project = Directory.GetParent(Application.dataPath).FullName;
            var python = WardrobePackagePaths.File("Editor/WardrobeIndexer/Runtime/Windows-x64/python.exe");
            var start = new ProcessStartInfo {
                FileName = Application.platform == RuntimePlatform.WindowsEditor && File.Exists(python) ? python : "python3",
                Arguments = Quote(script) + " --project " + Quote(project) + " --bridge " + Quote(AvatarWardrobeServer.Url.TrimEnd('/')) + " --port 0 --unity " + Quote(EditorApplication.applicationPath),
                WorkingDirectory = project, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            launching = true;
            try
            {
                var process = new Process { StartInfo = start, EnableRaisingEvents = true };
                process.OutputDataReceived += (_, args) => {
                    const string prefix = "Avatar Wardrobe: ";
                    if (args.Data != null && args.Data.StartsWith(prefix, StringComparison.Ordinal)) {
                        var url = args.Data.Substring(prefix.Length);
                        callbacks.Enqueue(() => { launching = false; SessionState.SetString("Wardrobe.DesktopUrl", url); OpenDesktopWindow(url); });
                    }
                };
                process.ErrorDataReceived += (_, args) => { if (!string.IsNullOrWhiteSpace(args.Data)) callbacks.Enqueue(() => UnityEngine.Debug.LogWarning("Wardrobe desktop: " + args.Data)); };
                process.Exited += (_, __) => callbacks.Enqueue(() => { launching = false; process.Dispose(); });
                if (!process.Start()) throw new IOException("Python did not start.");
                process.BeginOutputReadLine(); process.BeginErrorReadLine();
            }
            catch (Exception error) { launching = false; UnityEngine.Debug.LogError("Could not open Avatar Wardrobe Desktop. Install Python 3.10 or newer on macOS/Linux, then retry. " + error.Message); }
        }
        private static void OpenDesktopWindow(string url)
        {
            // The UI remains the existing localhost web app.  Application mode
            // removes browser chrome without adding a bundled Chromium runtime.
            // Every fallback leaves the normal browser path available.
            if (string.IsNullOrEmpty(url)) return;
            try
            {
                if (Application.platform == RuntimePlatform.WindowsEditor)
                {
                    if (TryStart("msedge.exe", "--app=" + Quote(url))) return;
                    if (TryStart("chrome.exe", "--app=" + Quote(url))) return;
                }
                else if (Application.platform == RuntimePlatform.OSXEditor)
                {
                    if (TryStart("open", "-a \"Google Chrome\" --args --app=" + Quote(url))) return;
                    if (TryStart("open", "-a \"Chromium\" --args --app=" + Quote(url))) return;
                }
                else if (Application.platform == RuntimePlatform.LinuxEditor)
                {
                    if (TryStart("google-chrome", "--app=" + Quote(url))) return;
                    if (TryStart("chromium", "--app=" + Quote(url))) return;
                    if (TryStart("chromium-browser", "--app=" + Quote(url))) return;
                }
            }
            catch (Exception error)
            {
                UnityEngine.Debug.LogWarning("Could not open Avatar Wardrobe application mode. Falling back to the default browser. " + error.Message);
            }
            Application.OpenURL(url);
        }
        private static bool TryStart(string fileName, string arguments)
        {
            try
            {
                using (var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = fileName,
                        Arguments = arguments,
                        UseShellExecute = true,
                        CreateNoWindow = true
                    }
                })
                {
                    if (!process.Start()) return false;
                    // `/usr/bin/open` itself is always present on macOS, but
                    // exits non-zero when the requested browser is absent. A
                    // short wait lets the Chromium fallback (or normal browser
                    // fallback) run instead of treating that command as a
                    // successful launch.
                    if (string.Equals(fileName, "open", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!process.WaitForExit(3000)) return true;
                        return process.ExitCode == 0;
                    }
                    return true;
                }
            }
            catch (System.ComponentModel.Win32Exception) { return false; }
            catch (FileNotFoundException) { return false; }
            catch (InvalidOperationException) { return false; }
        }
        private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}
