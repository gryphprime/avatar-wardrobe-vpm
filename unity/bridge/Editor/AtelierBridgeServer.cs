using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace GryphPrime.AtelierBridge
{
    [InitializeOnLoad]
    public static class AtelierBridgeServer
    {
        private const int MaxPayloadBytes = 256 * 1024;
        [Serializable] private sealed class Config { public string token, projectPath, outputRoot; public int port; }
        [Serializable] private sealed class Target { public string sceneGuid, objectId; }
        [Serializable] private sealed class Item { public string id, assetId, name, prefabGuid; }
        [Serializable] private sealed class Recipe { public Item[] items; }
        [Serializable] private sealed class Payload { public Recipe recipe; public string view; }
        [Serializable] private sealed class Command { public string id, workspaceId, expectedRevision, action; public int desiredRevision; public Target target; public Payload payload; }
        [Serializable] private sealed class Artifact { public string path, view; }
        [Serializable] private sealed class Result { public string revision; public Artifact artifact; }
        [Serializable] private sealed class Receipt { public string id, fingerprint, state, revision, error; public Result result; }
        [Serializable] private sealed class TargetDto { public string sceneGuid, objectId, name, revision; }
        [Serializable] private sealed class ContextDto { public string projectPath, revision; public TargetDto[] targets; public string[] capabilities = { "context", "reconcile", "snapshot", "durable-receipts" }; }
        private sealed class PendingRequest { public HttpListenerContext context; public string body; }

        private static readonly object QueueLock = new object();
        private static readonly Queue<PendingRequest> Requests = new Queue<PendingRequest>();
        private static readonly Dictionary<string, Receipt> Receipts = new Dictionary<string, Receipt>(StringComparer.Ordinal);
        private static HttpListener listener;
        private static string token, outputRoot, projectPath;
        private static bool configured;

        static AtelierBridgeServer()
        {
            EditorApplication.update += ProcessOneRequest;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting += Stop;
            EditorApplication.delayCall += ConfigureFromEnvironment;
        }

        private static void ConfigureFromEnvironment()
        {
            if (configured) return;
            var raw = Environment.GetEnvironmentVariable("ATELIER_BRIDGE_CONFIG");
            if (string.IsNullOrEmpty(raw)) return;
            try
            {
                if (!raw.TrimStart().StartsWith("{")) raw = File.ReadAllText(raw);
                var config = JsonUtility.FromJson<Config>(raw);
                if (config == null || string.IsNullOrEmpty(config.token) || config.port < 1 || config.port > 65535 || string.IsNullOrEmpty(config.outputRoot) || string.IsNullOrEmpty(config.projectPath))
                    throw new InvalidOperationException("ATELIER_BRIDGE_CONFIG is incomplete.");
                token = config.token;
                outputRoot = Path.GetFullPath(config.outputRoot);
                projectPath = Path.GetFullPath(config.projectPath);
                var actualProject = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                if (!string.Equals(actualProject.TrimEnd(Path.DirectorySeparatorChar), projectPath.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Bridge configuration projectPath does not identify this Unity project.");
                Directory.CreateDirectory(Path.Combine(outputRoot, "receipts"));
                LoadReceipts();
                listener = new HttpListener();
                listener.Prefixes.Add("http://127.0.0.1:" + config.port + "/");
                listener.Start();
                var worker = new Thread(Listen) { IsBackground = true, Name = "Atelier bridge listener" };
                worker.Start();
                configured = true;
            }
            catch (Exception error) { Debug.LogError("Atelier bridge did not start: " + error.Message); }
        }

        private static void Stop()
        {
            configured = false;
            try { if (listener != null) listener.Stop(); } catch (Exception) { }
            try { if (listener != null) listener.Close(); } catch (Exception) { }
            listener = null;
        }

        private static void Listen()
        {
            while (listener != null && listener.IsListening)
            {
                try
                {
                    var context = listener.GetContext();
                    if (!Authorized(context)) { Write(context, 401, new ErrorDto { error = "unauthorized" }); continue; }
                    if (context.Request.ContentLength64 > MaxPayloadBytes) { Write(context, 413, new ErrorDto { error = "payload too large" }); continue; }
                    var body = "";
                    if (context.Request.HasEntityBody)
                    {
                        using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8, true, MaxPayloadBytes, false))
                        {
                            var buffer = new char[MaxPayloadBytes + 1];
                            var read = reader.Read(buffer, 0, buffer.Length);
                            if (read > MaxPayloadBytes) { Write(context, 413, new ErrorDto { error = "payload too large" }); continue; }
                            body = new string(buffer, 0, read);
                        }
                    }
                    lock (QueueLock) Requests.Enqueue(new PendingRequest { context = context, body = body });
                }
                catch (HttpListenerException) { return; }
                catch (ObjectDisposedException) { return; }
                catch (Exception error) { Debug.LogWarning("Atelier bridge listener: " + error.Message); }
            }
        }

        [Serializable] private sealed class ErrorDto { public string id, state, error; }
        [Serializable] private sealed class ShutdownDto { public string state; }
        private static bool Authorized(HttpListenerContext context) => context.Request.Headers["Authorization"] == "Bearer " + token && context.Request.Headers["X-Atelier-Protocol"] == "1";

        private static void ProcessOneRequest()
        {
            PendingRequest pending = null;
            lock (QueueLock) if (Requests.Count != 0) pending = Requests.Dequeue();
            if (pending == null) return;
            try
            {
                var path = pending.context.Request.Url.AbsolutePath;
                if (pending.context.Request.HttpMethod == "GET" && path == "/context") Write(pending.context, 200, BuildContext());
                else if (pending.context.Request.HttpMethod == "POST" && path == "/commands") Submit(pending.context, pending.body);
                else if (pending.context.Request.HttpMethod == "POST" && path == "/shutdown") Shutdown(pending.context);
                else if (pending.context.Request.HttpMethod == "GET" && path.StartsWith("/commands/", StringComparison.Ordinal)) Poll(pending.context, Uri.UnescapeDataString(path.Substring(10)));
                else Write(pending.context, 404, new ErrorDto { error = "not found" });
            }
            catch (Exception error) { Debug.LogException(error); Write(pending.context, 500, new ErrorDto { state = "failed", error = error.Message }); }
        }

        private static void Submit(HttpListenerContext context, string raw)
        {
            Command command;
            try { command = JsonUtility.FromJson<Command>(raw); }
            catch (Exception error) { Write(context, 400, new ErrorDto { error = "invalid command: " + error.Message }); return; }
            if (command == null || !Guid.TryParse(command.id, out _) || !Guid.TryParse(command.workspaceId, out _) || command.target == null || string.IsNullOrEmpty(command.target.sceneGuid) || string.IsNullOrEmpty(command.target.objectId) || (command.action != "reconcile" && command.action != "snapshot"))
            { Write(context, 400, new ErrorDto { error = "command requires UUID id/workspaceId, exact target, and supported action" }); return; }
            var fingerprint = Hash(raw);
            if (Receipts.TryGetValue(command.id, out var previous))
            {
                if (previous.fingerprint != fingerprint) { Write(context, 409, new ErrorDto { id = command.id, state = "failed", error = "command ID was reused with a different payload" }); return; }
                Write(context, 200, previous); return;
            }
            var receipt = new Receipt { id = command.id, fingerprint = fingerprint, state = "accepted", revision = Revision(), result = new Result() };
            Receipts.Add(receipt.id, receipt);
            Persist(receipt); // durable acceptance precedes every Unity mutation
            receipt.state = "executing";
            Persist(receipt);
            try
            {
                if (command.action == "reconcile") Reconcile(command, raw); else Snapshot(command);
                receipt.state = "succeeded";
                receipt.revision = Revision();
                receipt.result.revision = receipt.revision;
            }
            catch (Exception error)
            {
                receipt.state = "failed";
                receipt.error = error.Message;
                receipt.revision = Revision();
                receipt.result = new Result { revision = receipt.revision };
            }
            Persist(receipt); // terminal receipt is on disk before the HTTP response
            Write(context, 200, receipt);
        }

        private static void Poll(HttpListenerContext context, string id)
        {
            if (!Guid.TryParse(id, out _)) { Write(context, 400, new ErrorDto { error = "invalid command ID" }); return; }
            if (Receipts.TryGetValue(id, out var receipt)) Write(context, 200, receipt);
            else Write(context, 200, new Receipt { id = id, state = "needs-review", error = "no durable receipt; operation was never replayed" });
        }

        private static void Shutdown(HttpListenerContext context)
        {
            // Write and close the authenticated response before Unity begins its
            // normal shutdown so the desktop can distinguish a graceful request.
            Write(context, 200, new ShutdownDto { state = "shutting-down" });
            EditorApplication.delayCall += () => EditorApplication.Exit(0);
        }

        private static ContextDto BuildContext()
        {
            EnsureSavedSceneForBatchWorker();
            var targets = new List<TargetDto>();
            var revision = Revision();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded || string.IsNullOrEmpty(scene.path)) continue;
                var guid = AssetDatabase.AssetPathToGUID(scene.path);
                if (string.IsNullOrEmpty(guid)) continue;
                foreach (var root in scene.GetRootGameObjects()) targets.Add(new TargetDto { sceneGuid = guid, objectId = GlobalObjectId.GetGlobalObjectIdSlow(root).ToString(), name = root.name, revision = revision });
            }
            return new ContextDto { projectPath = projectPath, revision = revision, targets = targets.ToArray() };
        }

        private static void EnsureSavedSceneForBatchWorker()
        {
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && !string.IsNullOrEmpty(scene.path)) return;
                if (scene.isLoaded && scene.isDirty) return; // never replace unsaved editor work
            }
            if (!Application.isBatchMode) return;
            var paths = new List<string>();
            foreach (var guid in AssetDatabase.FindAssets("t:Scene")) paths.Add(AssetDatabase.GUIDToAssetPath(guid));
            paths.Sort(StringComparer.Ordinal);
            if (paths.Count != 0) EditorSceneManager.OpenScene(paths[0], OpenSceneMode.Single);
        }

        private static Transform ExactTarget(Command command)
        {
            if (!GlobalObjectId.TryParse(command.target.objectId, out var global)) throw new InvalidOperationException("target objectId is not a Unity GlobalObjectId");
            var target = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(global) as GameObject;
            if (target == null || !target.scene.IsValid() || string.IsNullOrEmpty(target.scene.path)) throw new InvalidOperationException("target no longer resolves to a saved scene object");
            if (!string.Equals(AssetDatabase.AssetPathToGUID(target.scene.path), command.target.sceneGuid, StringComparison.Ordinal)) throw new InvalidOperationException("target scene GUID does not match the resolved object");
            return target.transform;
        }

        private static void Reconcile(Command command, string raw)
        {
            RequireCleanSavedScenes();
            if (!string.IsNullOrEmpty(command.expectedRevision) && command.expectedRevision != Revision()) throw new InvalidOperationException("revision mismatch; refresh context before reconciling");
            if (HasUnsupportedAppearance(raw)) throw new InvalidOperationException("appearance is not supported by this bridge version");
            if (command.payload == null || command.payload.recipe == null) throw new InvalidOperationException("reconcile requires payload.recipe");
            var target = ExactTarget(command);
            var desired = new Dictionary<string, Item>(StringComparer.Ordinal);
            foreach (var item in command.payload.recipe.items ?? new Item[0])
            {
                if (item == null || string.IsNullOrEmpty(item.id) || string.IsNullOrEmpty(item.prefabGuid)) throw new InvalidOperationException("every recipe item requires id and prefabGuid");
                if (desired.ContainsKey(item.id)) throw new InvalidOperationException("recipe contains duplicate item ID: " + item.id);
                var assetPath = AssetDatabase.GUIDToAssetPath(item.prefabGuid);
                if (string.IsNullOrEmpty(assetPath) || AssetDatabase.LoadAssetAtPath<GameObject>(assetPath) == null) throw new InvalidOperationException("prefab GUID is unavailable: " + item.prefabGuid);
                desired.Add(item.id, item);
            }
            var ownerId = command.target.objectId;
            var existing = new Dictionary<string, AtelierOwnedItem>(StringComparer.Ordinal);
            foreach (var marker in target.GetComponentsInChildren<AtelierOwnedItem>(true))
            {
                if (marker == null) continue;
                if (marker.ownerObjectId != ownerId)
                    throw new InvalidOperationException("Atelier ownership marker belongs to a different target; review this target in Unity.");
                if (marker.transform == target || marker.transform.parent != target)
                    throw new InvalidOperationException("Atelier ownership marker is nested or attached to the target; review this target in Unity.");
                if (existing.ContainsKey(marker.itemId))
                    throw new InvalidOperationException("duplicate Atelier item ID exists under this target; review it in Unity.");
                existing.Add(marker.itemId, marker);
            }
            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Atelier reconcile");
            try
            {
                foreach (var pair in existing) if (!desired.TryGetValue(pair.Key, out var wanted) || pair.Value.sourcePrefabGuid != wanted.prefabGuid) Undo.DestroyObjectImmediate(pair.Value.gameObject);
                foreach (var pair in desired)
                {
                    if (existing.TryGetValue(pair.Key, out var found) && found != null && found.sourcePrefabGuid == pair.Value.prefabGuid) continue;
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(pair.Value.prefabGuid));
                    var instance = PrefabUtility.InstantiatePrefab(prefab, target) as GameObject;
                    if (instance == null) throw new InvalidOperationException("Unity could not instantiate prefab " + pair.Value.prefabGuid);
                    Undo.RegisterCreatedObjectUndo(instance, "Atelier reconcile");
                    instance.name = string.IsNullOrEmpty(pair.Value.name) ? prefab.name : pair.Value.name;
                    var marker = Undo.AddComponent<AtelierOwnedItem>(instance);
                    marker.itemId = pair.Value.id; marker.sourcePrefabGuid = pair.Value.prefabGuid; marker.ownerObjectId = ownerId;
                }
                EditorSceneManager.MarkSceneDirty(target.gameObject.scene);
                if (!EditorSceneManager.SaveScene(target.gameObject.scene)) throw new IOException("Unity could not save target scene");
                Undo.CollapseUndoOperations(group);
            }
            catch { Undo.RevertAllDownToGroup(group); throw; }
        }

        private static bool HasUnsupportedAppearance(string raw)
        {
            var marker = "\"appearance\"";
            var position = raw.IndexOf(marker, StringComparison.Ordinal);
            if (position < 0) return false;
            position = raw.IndexOf(':', position + marker.Length) + 1;
            while (position > 0 && position < raw.Length && char.IsWhiteSpace(raw[position])) position++;
            return position <= 0 || position >= raw.Length || !raw.Substring(position).StartsWith("{}", StringComparison.Ordinal);
        }

        private static void Snapshot(Command command)
        {
            RequireCleanSavedScenes();
            if (!string.IsNullOrEmpty(command.expectedRevision) && command.expectedRevision != Revision()) throw new InvalidOperationException("revision mismatch; refresh context before capturing");
            var view = command.payload == null || string.IsNullOrEmpty(command.payload.view) ? "front" : command.payload.view.ToLowerInvariant();
            if (view != "front" && view != "three-quarter" && view != "back") throw new InvalidOperationException("snapshot view must be front, three-quarter, or back");
            var path = Capture(ExactTarget(command), command.id, view);
            Receipts[command.id].result = new Result { artifact = new Artifact { path = path, view = view } };
        }

        private static string Capture(Transform target, string operationId, string view)
        {
            var bounds = new Bounds(target.position, Vector3.one); var found = false;
            foreach (var renderer in target.GetComponentsInChildren<Renderer>(true)) if (renderer != null) { if (!found) { bounds = renderer.bounds; found = true; } else bounds.Encapsulate(renderer.bounds); }
            var radius = Mathf.Max(0.5f, bounds.extents.magnitude);
            var direction = view == "back" ? Vector3.back : view == "three-quarter" ? new Vector3(1f, .2f, 1f).normalized : Vector3.forward;
            var cameraGo = new GameObject("Atelier snapshot camera") { hideFlags = HideFlags.HideAndDontSave };
            var keyGo = new GameObject("Atelier snapshot key") { hideFlags = HideFlags.HideAndDontSave };
            var fillGo = new GameObject("Atelier snapshot fill") { hideFlags = HideFlags.HideAndDontSave };
            var camera = cameraGo.AddComponent<Camera>(); var key = keyGo.AddComponent<Light>(); var fill = fillGo.AddComponent<Light>();
            var previousActive = RenderTexture.active; var previousAmbient = RenderSettings.ambientLight;
            var hiddenRenderers = new List<Renderer>(); var hiddenRendererStates = new List<bool>();
            var hiddenLights = new List<Light>(); var hiddenLightStates = new List<bool>();
            var texture = RenderTexture.GetTemporary(512, 512, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB); Texture2D image = null;
            try
            {
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.08f, .09f, .12f, 1f); camera.fieldOfView = 35f;
                camera.transform.position = bounds.center + direction * (radius / Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * .5f) + radius * 1.25f); camera.transform.LookAt(bounds.center); camera.targetTexture = texture;
                camera.nearClipPlane = .01f; camera.farClipPlane = Mathf.Max(100f, radius * 8f);
                foreach (var renderer in Object.FindObjectsOfType<Renderer>()) if (renderer != null && !renderer.transform.IsChildOf(target)) { hiddenRenderers.Add(renderer); hiddenRendererStates.Add(renderer.enabled); renderer.enabled = false; }
                foreach (var light in Object.FindObjectsOfType<Light>()) if (light != key && light != fill) { hiddenLights.Add(light); hiddenLightStates.Add(light.enabled); light.enabled = false; }
                key.type = LightType.Directional; key.intensity = 1.1f; key.transform.rotation = Quaternion.Euler(35f, -35f, 0f);
                fill.type = LightType.Directional; fill.intensity = .45f; fill.color = new Color(.65f, .78f, 1f); fill.transform.rotation = Quaternion.Euler(20f, 140f, 0f);
                RenderSettings.ambientLight = new Color(.22f, .22f, .25f); camera.Render(); RenderTexture.active = texture;
                image = new Texture2D(512, 512, TextureFormat.RGBA32, false, false); image.ReadPixels(new Rect(0, 0, 512, 512), 0, 0); image.Apply(false, false);
                var directory = Path.Combine(outputRoot, "artifacts"); Directory.CreateDirectory(directory); var path = Path.Combine(directory, operationId + ".png"); File.WriteAllBytes(path, image.EncodeToPNG()); return path;
            }
            finally
            {
                RenderSettings.ambientLight = previousAmbient; RenderTexture.active = previousActive; camera.targetTexture = null;
                for (var i = 0; i < hiddenRenderers.Count; i++) if (hiddenRenderers[i] != null) hiddenRenderers[i].enabled = hiddenRendererStates[i];
                for (var i = 0; i < hiddenLights.Count; i++) if (hiddenLights[i] != null) hiddenLights[i].enabled = hiddenLightStates[i];
                if (image != null) Object.DestroyImmediate(image); RenderTexture.ReleaseTemporary(texture); Object.DestroyImmediate(cameraGo); Object.DestroyImmediate(keyGo); Object.DestroyImmediate(fillGo);
            }
        }

        private static string Revision()
        {
            var entries = new List<string>();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i); if (!scene.isLoaded || string.IsNullOrEmpty(scene.path)) continue;
                var file = Path.Combine(projectPath, scene.path); entries.Add(scene.path + "=" + (File.Exists(file) ? File.GetLastWriteTimeUtc(file).Ticks + ":" + new FileInfo(file).Length : "missing"));
            }
            entries.Sort(StringComparer.Ordinal); return Hash(string.Join("|", entries.ToArray()));
        }

        private static void RequireCleanSavedScenes()
        {
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && !string.IsNullOrEmpty(scene.path) && scene.isDirty)
                    throw new InvalidOperationException("A saved scene has unsaved changes; save or revert it in Unity before Atelier changes it.");
            }
        }

        private static string Hash(string text)
        {
            using (var hash = SHA256.Create()) { var bytes = hash.ComputeHash(Encoding.UTF8.GetBytes(text ?? "")); var result = new StringBuilder(bytes.Length * 2); foreach (var value in bytes) result.Append(value.ToString("x2")); return result.ToString(); }
        }

        private static void LoadReceipts()
        {
            foreach (var path in Directory.GetFiles(Path.Combine(outputRoot, "receipts"), "*.json"))
                try
                {
                    var receipt = JsonUtility.FromJson<Receipt>(File.ReadAllText(path));
                    if (receipt == null || !Guid.TryParse(receipt.id, out _)) continue;
                    if (receipt.state != "succeeded" && receipt.state != "failed" && receipt.state != "needs-review") { receipt.state = "needs-review"; receipt.error = "Unity restarted while this command was executing; it was not replayed."; Persist(receipt); }
                    Receipts[receipt.id] = receipt;
                }
                catch (Exception error)
                {
                    var id = Path.GetFileNameWithoutExtension(path);
                    if (Guid.TryParse(id, out _))
                    {
                        var tombstone = new Receipt { id = id, fingerprint = "unreadable", state = "needs-review", error = "durable receipt could not be read; operation was not replayed" };
                        Receipts[id] = tombstone;
                        Persist(tombstone);
                    }
                    Debug.LogWarning("Atelier could not read receipt " + path + ": " + error.Message);
                }
        }

        private static void Persist(Receipt receipt)
        {
            var path = Path.Combine(outputRoot, "receipts", receipt.id + ".json"); var temporary = path + ".tmp"; var bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(receipt));
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }

        private static void Write(HttpListenerContext context, int status, object value)
        {
            try { var bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(value)); context.Response.StatusCode = status; context.Response.ContentType = "application/json"; context.Response.ContentEncoding = Encoding.UTF8; context.Response.ContentLength64 = bytes.Length; context.Response.OutputStream.Write(bytes, 0, bytes.Length); }
            finally { try { context.Response.Close(); } catch (Exception) { } }
        }
    }
}
