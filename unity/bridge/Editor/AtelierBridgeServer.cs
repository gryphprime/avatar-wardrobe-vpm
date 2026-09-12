using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
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
        [Serializable] private sealed class MaterialAppearance { public string rendererId, property; public int slot; public float[] color; }
        [Serializable] private sealed class BlendShapeAppearance { public string rendererId; public int index; public float value; }
        [Serializable] private sealed class Appearance { public MaterialAppearance[] materials; public BlendShapeAppearance[] blendshapes; }
        [Serializable] private sealed class Recipe { public Item[] items; public Appearance appearance; }
        [Serializable] private sealed class Payload { public Recipe recipe; public string view; }
        [Serializable] private sealed class Command { public string id, workspaceId, expectedRevision, action; public int desiredRevision; public Target target; public Payload payload; }
        [Serializable] private sealed class Artifact { public string path, view; }
        [Serializable] private sealed class Result { public string revision; public Artifact artifact; public InspectDto inspection; }
        [Serializable] private sealed class Receipt { public string id, fingerprint, state, revision, error; public Result result; }
        [Serializable] private sealed class TargetDto { public string sceneGuid, objectId, name, revision; }
        [Serializable] private sealed class ContextDto { public string projectPath, revision; public TargetDto[] targets; public string[] capabilities = { "context", "inspect", "reconcile", "appearance", "snapshot", "durable-receipts" }; }
        [Serializable] private sealed class InspectTargetDto { public string sceneGuid, objectId, name; }
        [Serializable] private sealed class RecipeItemDto { public string id, assetId, name, prefabGuid; }
        // Keep the observed recipe intentionally lean: the desktop validator
        // accepts these controls as a desired recipe. Editable option metadata
        // belongs only to AppearanceMaterialDto/AppearanceBlendShapeDto below.
        [Serializable] private sealed class ActualMaterialDto { public string rendererId, property; public int slot; public float[] color; }
        [Serializable] private sealed class ActualBlendShapeDto { public string rendererId; public int index; public float value; }
        [Serializable] private sealed class AppearancePropertyDto { public string name; public float[] color; }
        [Serializable] private sealed class AppearanceMaterialDto { public string rendererId, name, property; public int slot; public float[] color; public AppearancePropertyDto[] properties; }
        [Serializable] private sealed class AppearanceBlendShapeDto { public string rendererId, name; public int index; public float min, max, value; }
        [Serializable] private sealed class AppearanceDto { public ActualMaterialDto[] materials; public ActualBlendShapeDto[] blendshapes; }
        [Serializable] private sealed class AppearanceOptionsDto { public AppearanceMaterialDto[] materials; public AppearanceBlendShapeDto[] blendshapes; }
        [Serializable] private sealed class InspectRecipeDto { public RecipeItemDto[] items; public AppearanceDto appearance; }
        [Serializable] private sealed class InspectDto { public string projectPath, revision; public InspectTargetDto target; public InspectRecipeDto recipe; public AppearanceOptionsDto appearanceOptions; public string[] warnings; }
        private sealed class PendingRequest { public HttpListenerContext context; public string body; }

        private sealed class MaterialEdit
        {
            internal Renderer renderer;
            internal AtelierOwnedItem marker;
            internal string rendererPath;
            internal int slot;
            internal string property;
            internal Color color;
        }

        private sealed class BlendShapeEdit
        {
            internal SkinnedMeshRenderer renderer;
            internal AtelierOwnedItem marker;
            internal string rendererPath;
            internal int index;
            internal float value;
        }

        private sealed class AssetTransaction
        {
            internal readonly HashSet<string> created = new HashSet<string>(StringComparer.Ordinal);
            internal readonly Dictionary<string, string> changed = new Dictionary<string, string>(StringComparer.Ordinal);
            internal readonly HashSet<string> pendingDelete = new HashSet<string>(StringComparer.Ordinal);
            internal void Remember(Material material)
            {
                if (material == null || !EditorUtility.IsPersistent(material)) return;
                var path = AssetDatabase.GetAssetPath(material);
                if (string.IsNullOrEmpty(path) || changed.ContainsKey(path) || created.Contains(path)) return;
                changed[path] = EditorJsonUtility.ToJson(material);
            }
            internal void Created(string path) { if (!string.IsNullOrEmpty(path)) created.Add(path); }
            internal void DeleteAfterCommit(string path) { if (!string.IsNullOrEmpty(path)) pendingDelete.Add(path); }
            internal void Rollback()
            {
                foreach (var pair in changed)
                {
                    var material = AssetDatabase.LoadAssetAtPath<Material>(pair.Key);
                    if (material != null) EditorJsonUtility.FromJsonOverwrite(pair.Value, material);
                }
                foreach (var path in created.ToArray()) if (AssetDatabase.LoadAssetAtPath<Object>(path) != null) AssetDatabase.DeleteAsset(path);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
        }

        private static readonly object QueueLock = new object();
        private static readonly Queue<PendingRequest> Requests = new Queue<PendingRequest>();
        private static readonly Dictionary<string, Receipt> Receipts = new Dictionary<string, Receipt>(StringComparer.Ordinal);
        private static readonly string[] SupportedColorProperties = { "_Color", "_BaseColor" };
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
                else if (pending.context.Request.HttpMethod == "GET" && path == "/inspect") Inspect(pending.context);
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
            if (command == null || !Guid.TryParse(command.id, out _) || !Guid.TryParse(command.workspaceId, out _) || command.target == null || string.IsNullOrEmpty(command.target.sceneGuid) || string.IsNullOrEmpty(command.target.objectId) || (command.action != "reconcile" && command.action != "snapshot" && command.action != "inspect"))
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
                if (command.action == "reconcile") Reconcile(command, raw);
                else if (command.action == "snapshot") Snapshot(command);
                else InspectCommand(command);
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

        private static void Inspect(HttpListenerContext context)
        {
            var sceneGuid = context.Request.QueryString["sceneGuid"];
            var objectId = context.Request.QueryString["objectId"];
            if (string.IsNullOrEmpty(sceneGuid) || string.IsNullOrEmpty(objectId))
            {
                Write(context, 400, new ErrorDto { error = "inspect requires sceneGuid and objectId query parameters" });
                return;
            }
            try
            {
                var target = ExactTarget(sceneGuid, objectId);
                Write(context, 200, BuildInspect(target, sceneGuid, objectId));
            }
            catch (Exception error) { Write(context, 400, new ErrorDto { error = error.Message }); }
        }

        private static void InspectCommand(Command command)
        {
            if (!string.IsNullOrEmpty(command.expectedRevision) && command.expectedRevision != Revision()) throw new InvalidOperationException("revision mismatch; refresh context before inspecting");
            var target = ExactTarget(command);
            var inspection = BuildInspect(target, command.target.sceneGuid, command.target.objectId);
            Receipts[command.id].result = new Result { revision = inspection.revision, inspection = inspection };
        }

        private static InspectDto BuildInspect(Transform target, string sceneGuid, string objectId)
        {
            var warnings = new List<string>();
            var recipeItems = new List<RecipeItemDto>();
            var actualMaterials = new List<ActualMaterialDto>();
            var optionsMaterials = new List<AppearanceMaterialDto>();
            var actualShapes = new List<ActualBlendShapeDto>();
            var optionsShapes = new List<AppearanceBlendShapeDto>();
            var seenItems = new HashSet<string>(StringComparer.Ordinal);
            var warnedAmbiguousPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var marker in target.GetComponentsInChildren<AtelierOwnedItem>(true))
            {
                if (marker == null) continue;
                if (marker.transform == target || marker.transform.parent != target)
                {
                    warnings.Add("Atelier marker " + marker.name + " is not a direct child of the target and was excluded.");
                    continue;
                }
                if (string.IsNullOrEmpty(marker.itemId) || !seenItems.Add(marker.itemId))
                {
                    warnings.Add("Target contains a duplicate or empty Atelier item ID.");
                    continue;
                }
                var assetId = marker.assetId;
                if (string.IsNullOrEmpty(assetId))
                {
                    assetId = string.IsNullOrEmpty(marker.sourcePrefabGuid) ? "legacy-unknown" : "legacy-" + marker.sourcePrefabGuid;
                    warnings.Add("Atelier item " + marker.itemId + " has no persisted assetId; inspection used a legacy identity until the next reconcile.");
                }
                recipeItems.Add(new RecipeItemDto { id = marker.itemId, assetId = assetId, name = marker.name, prefabGuid = marker.sourcePrefabGuid ?? "" });
                foreach (var renderer in marker.GetComponentsInChildren<Renderer>(true))
                {
                    // A malformed scene may contain a nested Atelier marker.
                    // Its renderers belong to that nested owner and must not be
                    // advertised as controls on the outer item.
                    if (renderer.GetComponentInParent<AtelierOwnedItem>() != marker) continue;
                    var rendererPath = AnimationUtility.CalculateTransformPath(renderer.transform, marker.transform);
                    if (!IsSupportedRenderer(renderer)) continue;
                    var pathMatches = FindRenderers(marker.transform, rendererPath);
                    if (pathMatches.Length != 1)
                    {
                        var warningKey = marker.itemId + "|" + rendererPath;
                        if (warnedAmbiguousPaths.Add(warningKey))
                            warnings.Add("Renderer path " + rendererPath + " on Atelier item " + marker.itemId + " is ambiguous; appearance controls were omitted from inspection.");
                        continue;
                    }
                    var rendererId = GlobalObjectId.GetGlobalObjectIdSlow(renderer).ToString();
                    var materials = renderer.sharedMaterials ?? new Material[0];
                    for (var slot = 0; slot < materials.Length; slot++)
                    {
                        var material = materials[slot];
                        var properties = new List<AppearancePropertyDto>();
                        foreach (var property in SupportedColorProperties)
                        {
                            if (!SupportsColor(material, property)) continue;
                            var color = material.GetColor(property);
                            if (ValidColor(color))
                            {
                                properties.Add(new AppearancePropertyDto { name = property, color = ColorArray(color) });
                                actualMaterials.Add(new ActualMaterialDto { rendererId = rendererId, slot = slot, property = property, color = ColorArray(color) });
                            }
                            else warnings.Add("Color " + property + " on " + rendererPath + " is HDR or outside the supported 0-1 range and was omitted from the editable recipe.");
                            if (AnimationDrives(marker.transform, renderer, property, slot, -1)) warnings.Add("Animation drives " + property + " on " + rendererPath + "; Atelier will reject edits to that field.");
                        }
                        optionsMaterials.Add(new AppearanceMaterialDto { rendererId = rendererId, name = renderer.name, slot = slot, properties = properties.ToArray() });
                        if (material == null) warnings.Add("Renderer " + rendererPath + " has an empty material slot " + slot + ".");
                        else if (properties.Count == 0) warnings.Add("Material " + material.name + " on " + rendererPath + " has no supported _Color or _BaseColor property.");
                    }
                    var skin = renderer as SkinnedMeshRenderer;
                    if (skin == null || skin.sharedMesh == null) continue;
                    var mesh = skin.sharedMesh;
                    for (var index = 0; index < mesh.blendShapeCount; index++)
                    {
                        var name = mesh.GetBlendShapeName(index);
                        var value = skin.GetBlendShapeWeight(index);
                        var option = new AppearanceBlendShapeDto { rendererId = rendererId, name = name, index = index, min = -100f, max = 100f, value = value };
                        optionsShapes.Add(option);
                        if (Finite(value) && value >= -100f && value <= 100f) actualShapes.Add(new ActualBlendShapeDto { rendererId = rendererId, index = index, value = value });
                        else warnings.Add("Blendshape " + name + " on " + rendererPath + " is outside the supported -100 to 100 range and was omitted from the editable recipe.");
                        if (AnimationDrives(marker.transform, renderer, "blendShape." + name, -1, index)) warnings.Add("Animation drives blendshape " + name + " on " + rendererPath + "; Atelier will reject edits to that field.");
                    }
                }
            }
            return new InspectDto
            {
                projectPath = projectPath,
                revision = Revision(),
                target = new InspectTargetDto { sceneGuid = sceneGuid, objectId = objectId, name = target.name },
                recipe = new InspectRecipeDto { items = recipeItems.ToArray(), appearance = new AppearanceDto { materials = actualMaterials.ToArray(), blendshapes = actualShapes.ToArray() } },
                appearanceOptions = new AppearanceOptionsDto { materials = optionsMaterials.ToArray(), blendshapes = optionsShapes.ToArray() },
                warnings = warnings.ToArray()
            };
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
            if (command == null || command.target == null) throw new InvalidOperationException("target is required");
            return ExactTarget(command.target.sceneGuid, command.target.objectId);
        }

        private static Transform ExactTarget(string sceneGuid, string objectId)
        {
            if (string.IsNullOrEmpty(sceneGuid) || string.IsNullOrEmpty(objectId) || !GlobalObjectId.TryParse(objectId, out var global)) throw new InvalidOperationException("target objectId is not a Unity GlobalObjectId");
            var target = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(global) as GameObject;
            if (target == null || !target.scene.IsValid() || string.IsNullOrEmpty(target.scene.path)) throw new InvalidOperationException("target no longer resolves to a saved scene object");
            if (!string.Equals(AssetDatabase.AssetPathToGUID(target.scene.path), sceneGuid, StringComparison.Ordinal)) throw new InvalidOperationException("target scene GUID does not match the resolved object");
            return target.transform;
        }

        private static void Reconcile(Command command, string raw)
        {
            RequireCleanSavedScenes();
            if (!string.IsNullOrEmpty(command.expectedRevision) && command.expectedRevision != Revision()) throw new InvalidOperationException("revision mismatch; refresh context before reconciling");
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
                EnsureMarkerLists(marker);
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
            var assets = new AssetTransaction();
            try
            {
                foreach (var pair in existing)
                    if (!desired.TryGetValue(pair.Key, out var wanted) || pair.Value.sourcePrefabGuid != wanted.prefabGuid)
                    {
                        RememberMarker(pair.Value);
                        QueueMarkerGeneratedCleanup(pair.Value, assets);
                        Undo.DestroyObjectImmediate(pair.Value.gameObject);
                    }
                foreach (var pair in desired)
                {
                    if (existing.TryGetValue(pair.Key, out var found) && found != null && found.sourcePrefabGuid == pair.Value.prefabGuid) continue;
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(pair.Value.prefabGuid));
                    var instance = PrefabUtility.InstantiatePrefab(prefab, target) as GameObject;
                    if (instance == null) throw new InvalidOperationException("Unity could not instantiate prefab " + pair.Value.prefabGuid);
                    Undo.RegisterCreatedObjectUndo(instance, "Atelier reconcile");
                    instance.name = string.IsNullOrEmpty(pair.Value.name) ? prefab.name : pair.Value.name;
                    var marker = Undo.AddComponent<AtelierOwnedItem>(instance);
                    marker.itemId = pair.Value.id; marker.assetId = pair.Value.assetId ?? ""; marker.sourcePrefabGuid = pair.Value.prefabGuid; marker.ownerObjectId = ownerId;
                }
                // Item metadata is part of the desired state even when the prefab
                // itself did not change. This makes inspection authoritative after
                // a desktop restart and repairs markers written by older builds.
                foreach (var marker in target.GetComponentsInChildren<AtelierOwnedItem>(true))
                {
                    if (!desired.TryGetValue(marker.itemId, out var wanted)) continue;
                    RememberMarker(marker);
                    marker.assetId = wanted.assetId ?? "";
                    marker.sourcePrefabGuid = wanted.prefabGuid;
                    marker.ownerObjectId = ownerId;
                    if (!string.IsNullOrEmpty(wanted.name) && marker.name != wanted.name) marker.name = wanted.name;
                }
                ApplyAppearance(target, ownerId, command.payload.recipe.appearance, assets);
                EditorSceneManager.MarkSceneDirty(target.gameObject.scene);
                if (!EditorSceneManager.SaveScene(target.gameObject.scene)) throw new IOException("Unity could not save target scene");
                CommitGeneratedCleanup(assets);
                AssetDatabase.SaveAssets();
                Undo.CollapseUndoOperations(group);
            }
            catch
            {
                assets.Rollback();
                Undo.RevertAllDownToGroup(group);
                throw;
            }
        }

        private static void ApplyAppearance(Transform target, string ownerId, Appearance appearance, AssetTransaction assets)
        {
            var materialEdits = new Dictionary<string, MaterialEdit>(StringComparer.Ordinal);
            var shapeEdits = new Dictionary<string, BlendShapeEdit>(StringComparer.Ordinal);
            var markers = target.GetComponentsInChildren<AtelierOwnedItem>(true).Where(marker => marker != null && marker.transform.parent == target).ToArray();
            foreach (var marker in markers) EnsureMarkerLists(marker);
            ValidateExistingAppearancePaths(markers);
            foreach (var entry in appearance == null ? new MaterialAppearance[0] : appearance.materials ?? new MaterialAppearance[0])
            {
                if (entry == null || string.IsNullOrEmpty(entry.rendererId) || string.IsNullOrEmpty(entry.property) || entry.slot < 0 || entry.color == null || entry.color.Length != 4)
                    throw new InvalidOperationException("Every appearance material requires rendererId, slot, property and a four-channel color.");
                if (!SupportedColorProperties.Contains(entry.property, StringComparer.Ordinal)) throw new InvalidOperationException("Only _Color and _BaseColor material properties are supported.");
                var color = ParseColor(entry.color);
                if (!ValidColor(color)) throw new InvalidOperationException("Appearance colors must be finite channels in the 0-1 range.");
                ResolveOwnedRenderer(target, ownerId, entry.rendererId, out var renderer, out var marker, out var rendererPath);
                var materials = renderer.sharedMaterials ?? new Material[0];
                if (entry.slot >= materials.Length) throw new InvalidOperationException("Appearance material slot is no longer present on renderer " + rendererPath + ".");
                var source = OriginalMaterial(marker, rendererPath, entry.slot, materials[entry.slot], true);
                if (source == null || !SupportsColor(source, entry.property)) throw new InvalidOperationException("Material property " + entry.property + " is not a supported Color on renderer " + rendererPath + ".");
                if (!ValidColor(source.GetColor(entry.property))) throw new InvalidOperationException("The original " + entry.property + " on renderer " + rendererPath + " is HDR or outside the supported 0-1 range; review it in Unity before applying an Atelier color.");
                if (AnimationDrives(marker.transform, renderer, entry.property, entry.slot, -1)) throw new InvalidOperationException("Animation drives " + entry.property + " on renderer " + rendererPath + "; edit the animation in Unity before applying this appearance.");
                var key = entry.rendererId + "|" + entry.slot + "|" + entry.property;
                if (materialEdits.ContainsKey(key)) throw new InvalidOperationException("Appearance contains duplicate material edit " + key + ".");
                materialEdits.Add(key, new MaterialEdit { renderer = renderer, marker = marker, rendererPath = rendererPath, slot = entry.slot, property = entry.property, color = color });
            }
            foreach (var entry in appearance == null ? new BlendShapeAppearance[0] : appearance.blendshapes ?? new BlendShapeAppearance[0])
            {
                if (entry == null || string.IsNullOrEmpty(entry.rendererId) || entry.index < 0 || !Finite(entry.value) || entry.value < -100f || entry.value > 100f)
                    throw new InvalidOperationException("Every appearance blendshape requires a finite value in the supported -100 to 100 range.");
                ResolveOwnedRenderer(target, ownerId, entry.rendererId, out var renderer, out var marker, out var rendererPath);
                var skin = renderer as SkinnedMeshRenderer;
                if (skin == null || skin.sharedMesh == null || entry.index >= skin.sharedMesh.blendShapeCount) throw new InvalidOperationException("Appearance blendshape index is no longer present on renderer " + rendererPath + ".");
                if (AnimationDrives(marker.transform, renderer, "blendShape." + skin.sharedMesh.GetBlendShapeName(entry.index), -1, entry.index)) throw new InvalidOperationException("Animation drives blendshape " + skin.sharedMesh.GetBlendShapeName(entry.index) + " on renderer " + rendererPath + "; edit the animation in Unity before applying this appearance.");
                var key = entry.rendererId + "|" + entry.index;
                if (shapeEdits.ContainsKey(key)) throw new InvalidOperationException("Appearance contains duplicate blendshape edit " + key + ".");
                shapeEdits.Add(key, new BlendShapeEdit { renderer = skin, marker = marker, rendererPath = rendererPath, index = entry.index, value = entry.value });
            }

            foreach (var marker in markers)
            {
                foreach (var renderer in marker.GetComponentsInChildren<Renderer>(true))
                {
                    if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer)) continue;
                    var rendererId = GlobalObjectId.GetGlobalObjectIdSlow(renderer).ToString();
                    var rendererPath = AnimationUtility.CalculateTransformPath(renderer.transform, marker.transform);
                    var materials = renderer.sharedMaterials ?? new Material[0];
                    for (var slot = 0; slot < materials.Length; slot++)
                    {
                        var edits = materialEdits.Values.Where(edit => edit.renderer == renderer && edit.slot == slot).ToArray();
                        var record = marker.originalMaterials.FirstOrDefault(value => value != null && value.rendererPath == rendererPath && value.slot == slot);
                        if (edits.Length > 0)
                        {
                            RememberMarker(marker);
                            var source = OriginalMaterial(marker, rendererPath, slot, materials[slot], true);
                            if (record == null)
                            {
                                record = CaptureOriginalMaterial(rendererPath, slot, source);
                                marker.originalMaterials.Add(record);
                            }
                            source = ResolveOriginalMaterial(record);
                            if (source == null) throw new InvalidOperationException("The original material for renderer " + rendererPath + " is missing; review the target in Unity.");
                            var generated = EnsureGeneratedMaterial(target, marker, record, source, assets);
                            Undo.RegisterCompleteObjectUndo(renderer, "Atelier appearance");
                            var assigned = renderer.sharedMaterials;
                            assigned[slot] = generated;
                            renderer.sharedMaterials = assigned;
                            Undo.RegisterCompleteObjectUndo(generated, "Atelier appearance");
                            foreach (var edit in edits) generated.SetColor(edit.property, edit.color);
                            EditorUtility.SetDirty(generated);
                        }
                        else if (record != null)
                        {
                            RememberMarker(marker);
                            var source = ResolveOriginalMaterial(record);
                            if (source == null) throw new InvalidOperationException("The original material for renderer " + rendererPath + " is missing; review the target in Unity.");
                            Undo.RegisterCompleteObjectUndo(renderer, "Restore Atelier appearance");
                            var assigned = renderer.sharedMaterials;
                            assigned[slot] = source;
                            renderer.sharedMaterials = assigned;
                            if (!string.IsNullOrEmpty(record.generatedPath)) assets.DeleteAfterCommit(record.generatedPath);
                            marker.originalMaterials.Remove(record);
                        }
                    }
                }

                foreach (var record in marker.originalMaterials.ToArray())
                {
                    if (record == null) continue;
                    var renderer = FindRenderer(marker.transform, record.rendererPath);
                    var edits = renderer == null ? new MaterialEdit[0] : materialEdits.Values.Where(edit => edit.renderer == renderer && edit.slot == record.slot).ToArray();
                    if (edits.Length > 0) continue;
                    if (renderer == null) throw new InvalidOperationException("The renderer for an Atelier material record is missing: " + record.rendererPath);
                    var source = ResolveOriginalMaterial(record);
                    if (source == null) throw new InvalidOperationException("The original material for renderer " + record.rendererPath + " is missing; review the target in Unity.");
                    Undo.RegisterCompleteObjectUndo(renderer, "Restore Atelier appearance");
                    var assigned = renderer.sharedMaterials;
                    if (record.slot < 0 || record.slot >= assigned.Length) throw new InvalidOperationException("The material slot for renderer " + record.rendererPath + " changed; review the target in Unity.");
                    assigned[record.slot] = source;
                    renderer.sharedMaterials = assigned;
                    if (!string.IsNullOrEmpty(record.generatedPath)) assets.DeleteAfterCommit(record.generatedPath);
                    RememberMarker(marker);
                    marker.originalMaterials.Remove(record);
                }
            }

            foreach (var marker in markers)
            {
                foreach (var renderer in marker.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    var rendererPath = AnimationUtility.CalculateTransformPath(renderer.transform, marker.transform);
                    foreach (var record in marker.originalBlendShapes.ToArray())
                    {
                        if (record == null || record.rendererPath != rendererPath) continue;
                        var rendererId = GlobalObjectId.GetGlobalObjectIdSlow(renderer).ToString();
                        var key = rendererId + "|" + record.index;
                        if (shapeEdits.ContainsKey(key)) continue;
                        if (renderer.sharedMesh == null || record.index < 0 || record.index >= renderer.sharedMesh.blendShapeCount) throw new InvalidOperationException("The blendshape layout changed on renderer " + rendererPath + ".");
                        Undo.RegisterCompleteObjectUndo(renderer, "Restore Atelier appearance");
                        renderer.SetBlendShapeWeight(record.index, record.originalValue);
                        RememberMarker(marker);
                        marker.originalBlendShapes.Remove(record);
                    }
                }
                foreach (var edit in shapeEdits.Values.Where(value => value.marker == marker))
                {
                    var record = marker.originalBlendShapes.FirstOrDefault(value => value != null && value.rendererPath == edit.rendererPath && value.index == edit.index);
                    if (record == null)
                    {
                        RememberMarker(marker);
                        record = new AtelierOwnedBlendShape { rendererPath = edit.rendererPath, index = edit.index, name = edit.renderer.sharedMesh.GetBlendShapeName(edit.index), originalValue = edit.renderer.GetBlendShapeWeight(edit.index) };
                        marker.originalBlendShapes.Add(record);
                    }
                    Undo.RegisterCompleteObjectUndo(edit.renderer, "Atelier appearance");
                    edit.renderer.SetBlendShapeWeight(edit.index, edit.value);
                }
            }
        }

        private static void ResolveOwnedRenderer(Transform target, string ownerId, string rendererId, out Renderer renderer, out AtelierOwnedItem marker, out string rendererPath)
        {
            renderer = null; marker = null; rendererPath = "";
            if (!GlobalObjectId.TryParse(rendererId, out var global)) throw new InvalidOperationException("appearance rendererId is not a Unity GlobalObjectId");
            renderer = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(global) as Renderer;
            if (renderer == null || renderer.gameObject.scene != target.gameObject.scene || !renderer.transform.IsChildOf(target)) throw new InvalidOperationException("appearance renderer is no longer under the exact target");
            marker = renderer.GetComponentInParent<AtelierOwnedItem>();
            if (marker == null || marker.ownerObjectId != ownerId || marker.transform.parent != target) throw new InvalidOperationException("appearance renderer is not an Atelier-owned item under this target");
            if (!IsSupportedRenderer(renderer)) throw new InvalidOperationException("only MeshRenderer and SkinnedMeshRenderer appearance is supported");
            rendererPath = AnimationUtility.CalculateTransformPath(renderer.transform, marker.transform);
            var matches = FindRenderers(marker.transform, rendererPath);
            if (matches.Length != 1) throw new InvalidOperationException("appearance renderer path " + rendererPath + " is ambiguous on Atelier item " + marker.itemId + "; rename duplicate children or use one supported renderer per object");
        }

        private static Renderer FindRenderer(Transform marker, string path)
        {
            var matches = FindRenderers(marker, path);
            return matches.Length == 1 ? matches[0] : null;
        }

        private static Renderer[] FindRenderers(Transform marker, string path)
        {
            if (marker == null || path == null) return new Renderer[0];
            return marker.GetComponentsInChildren<Renderer>(true)
                .Where(renderer => IsSupportedRenderer(renderer) && AnimationUtility.CalculateTransformPath(renderer.transform, marker) == path)
                .ToArray();
        }

        private static bool IsSupportedRenderer(Renderer renderer)
        {
            return renderer is MeshRenderer || renderer is SkinnedMeshRenderer;
        }

        private static void ValidateExistingAppearancePaths(AtelierOwnedItem[] markers)
        {
            foreach (var marker in markers)
            {
                foreach (var record in marker.originalMaterials ?? new List<AtelierOwnedMaterial>())
                {
                    if (record == null) continue;
                    var matches = FindRenderers(marker.transform, record.rendererPath);
                    if (matches.Length == 0) throw new InvalidOperationException("The renderer for an Atelier material record is missing: " + record.rendererPath);
                    if (matches.Length > 1) throw new InvalidOperationException("The renderer path for an Atelier material record is ambiguous: " + record.rendererPath + ". Rename duplicate children or use one supported renderer per object.");
                }
                foreach (var record in marker.originalBlendShapes ?? new List<AtelierOwnedBlendShape>())
                {
                    if (record == null) continue;
                    var matches = FindRenderers(marker.transform, record.rendererPath);
                    if (matches.Length == 0) throw new InvalidOperationException("The renderer for an Atelier blendshape record is missing: " + record.rendererPath);
                    if (matches.Length > 1) throw new InvalidOperationException("The renderer path for an Atelier blendshape record is ambiguous: " + record.rendererPath + ". Rename duplicate children or use one supported renderer per object.");
                }
            }
        }

        private static AtelierOwnedMaterial CaptureOriginalMaterial(string rendererPath, int slot, Material material)
        {
            if (material == null) throw new InvalidOperationException("An Atelier appearance cannot edit an empty material slot.");
            var path = AssetDatabase.GetAssetPath(material);
            if (string.IsNullOrEmpty(path) || path.StartsWith("Assets/AtelierGenerated/", StringComparison.Ordinal)) throw new InvalidOperationException("The original material must be a saved project asset and cannot be an untracked Atelier output.");
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(material, out string guid, out long localId) || string.IsNullOrEmpty(guid)) throw new InvalidOperationException("The original material has no stable asset identity.");
            var properties = new List<string>();
            var values = new List<float>();
            foreach (var property in SupportedColorProperties)
            {
                if (!SupportsColor(material, property)) continue;
                properties.Add(property);
                values.AddRange(ColorArray(material.GetColor(property)));
            }
            return new AtelierOwnedMaterial { rendererPath = rendererPath, slot = slot, originalGuid = guid, originalLocalId = localId, originalPath = path, generatedPath = "", originalColorProperties = properties.ToArray(), originalColorValues = values.ToArray() };
        }

        private static Material OriginalMaterial(AtelierOwnedItem marker, string rendererPath, int slot, Material current, bool allowRecord)
        {
            var record = allowRecord ? marker.originalMaterials.FirstOrDefault(value => value != null && value.rendererPath == rendererPath && value.slot == slot) : null;
            if (record != null) return ResolveOriginalMaterial(record);
            return current;
        }

        private static Material ResolveOriginalMaterial(AtelierOwnedMaterial record)
        {
            if (record == null) return null;
            var path = !string.IsNullOrEmpty(record.originalGuid) ? AssetDatabase.GUIDToAssetPath(record.originalGuid) : record.originalPath;
            if (string.IsNullOrEmpty(path)) return null;
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (!(asset is Material)) continue;
                if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string guid, out long localId) && guid == record.originalGuid && localId == record.originalLocalId) return asset as Material;
            }
            // A saved record with a GUID/local ID must resolve the same asset;
            // falling back by path could silently restore a different subasset.
            return string.IsNullOrEmpty(record.originalGuid) ? AssetDatabase.LoadAssetAtPath<Material>(path) : null;
        }

        private static Material EnsureGeneratedMaterial(Transform target, AtelierOwnedItem marker, AtelierOwnedMaterial record, Material source, AssetTransaction assets)
        {
            var hadOwnedPath = !string.IsNullOrEmpty(record.generatedPath);
            var path = record.generatedPath;
            if (string.IsNullOrEmpty(path)) path = "Assets/AtelierGenerated/" + Hash(marker.ownerObjectId ?? "") + "/materials/" + Hash(marker.itemId + "|" + record.rendererPath + "|" + record.slot) + ".mat";
            if (!path.StartsWith("Assets/AtelierGenerated/", StringComparison.Ordinal) || path.IndexOf("..", StringComparison.Ordinal) >= 0) throw new InvalidOperationException("Atelier material metadata points outside the generated asset directory; review this target in Unity.");
            if (GeneratedPathReferencedByOtherRecord(path, marker, record)) throw new InvalidOperationException("Atelier generated material path is already owned by another appearance record: " + path);
            var absoluteDirectory = Path.Combine(projectPath, Path.GetDirectoryName(path).Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(absoluteDirectory);
            AssetDatabase.Refresh();
            var existingAsset = AssetDatabase.LoadAssetAtPath<Object>(path);
            if (!hadOwnedPath && existingAsset != null) throw new InvalidOperationException("Atelier generated material path already exists but is not owned by this appearance record: " + path);
            var generated = existingAsset as Material;
            if (existingAsset != null && generated == null) throw new InvalidOperationException("Atelier generated material path is occupied by a non-material asset: " + path);
            if (generated == null)
            {
                generated = new Material(source) { name = marker.itemId + " Atelier material " + record.slot };
                AssetDatabase.CreateAsset(generated, path);
                assets.Created(path);
            }
            else
            {
                assets.Remember(generated);
                EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(source), generated);
            }
            record.generatedPath = path;
            return generated;
        }

        private static bool GeneratedPathReferencedByOtherRecord(string path, AtelierOwnedItem marker, AtelierOwnedMaterial record)
        {
            foreach (var candidate in Resources.FindObjectsOfTypeAll<AtelierOwnedItem>())
            {
                if (candidate == null || candidate.originalMaterials == null) continue;
                foreach (var candidateRecord in candidate.originalMaterials)
                {
                    if (candidateRecord == null || string.IsNullOrEmpty(candidateRecord.generatedPath)) continue;
                    if (!string.Equals(candidateRecord.generatedPath, path, StringComparison.Ordinal)) continue;
                    if (candidate != marker || candidateRecord != record) return true;
                }
            }
            return false;
        }

        private static void RememberMarker(AtelierOwnedItem marker)
        {
            if (marker != null)
            {
                Undo.RegisterCompleteObjectUndo(marker, "Atelier appearance metadata");
                Undo.RegisterCompleteObjectUndo(marker.gameObject, "Atelier appearance metadata");
            }
        }

        private static void EnsureMarkerLists(AtelierOwnedItem marker)
        {
            if (marker == null) return;
            if (marker.originalMaterials == null) marker.originalMaterials = new List<AtelierOwnedMaterial>();
            if (marker.originalBlendShapes == null) marker.originalBlendShapes = new List<AtelierOwnedBlendShape>();
        }

        private static void QueueMarkerGeneratedCleanup(AtelierOwnedItem marker, AssetTransaction assets)
        {
            if (marker == null || marker.originalMaterials == null) return;
            foreach (var record in marker.originalMaterials) if (record != null && !string.IsNullOrEmpty(record.generatedPath)) assets.DeleteAfterCommit(record.generatedPath);
        }

        private static void CommitGeneratedCleanup(AssetTransaction assets)
        {
            var retained = new HashSet<string>(StringComparer.Ordinal);
            foreach (var marker in Resources.FindObjectsOfTypeAll<AtelierOwnedItem>())
                if (marker != null && marker.gameObject.scene.IsValid() && marker.originalMaterials != null)
                    foreach (var record in marker.originalMaterials) if (record != null && !string.IsNullOrEmpty(record.generatedPath)) retained.Add(record.generatedPath);
            // A user may have assigned an Atelier-generated material to another
            // live renderer after the recipe was applied. Keep that asset even
            // when no Atelier marker still records ownership; deleting it would
            // silently break the manual assignment.
            foreach (var renderer in Resources.FindObjectsOfTypeAll<Renderer>())
            {
                if (renderer == null || !renderer.gameObject.scene.IsValid()) continue;
                foreach (var material in renderer.sharedMaterials ?? new Material[0])
                {
                    var path = AssetDatabase.GetAssetPath(material);
                    if (!string.IsNullOrEmpty(path) && assets.pendingDelete.Contains(path)) retained.Add(path);
                }
            }
            foreach (var path in assets.pendingDelete) if (!retained.Contains(path) && AssetDatabase.LoadAssetAtPath<Object>(path) != null) AssetDatabase.DeleteAsset(path);
        }

        private static bool SupportsColor(Material material, string property)
        {
            if (material == null || material.shader == null || !material.HasProperty(property)) return false;
            try
            {
                var index = material.shader.FindPropertyIndex(property);
                return index >= 0 && material.shader.GetPropertyType(index) == ShaderPropertyType.Color;
            }
            catch { return false; }
        }

        private static bool AnimationDrives(Transform root, Renderer renderer, string property, int slot, int shapeIndex)
        {
            var clips = new HashSet<AnimationClip>();
            foreach (var animation in root.GetComponentsInChildren<Animation>(true))
                foreach (AnimationState state in animation) if (state != null && state.clip != null) clips.Add(state.clip);
            foreach (var animator in root.GetComponentsInChildren<Animator>(true))
                if (animator.runtimeAnimatorController != null)
                    foreach (var clip in animator.runtimeAnimatorController.animationClips) if (clip != null) clips.Add(clip);
            var rendererPath = AnimationUtility.CalculateTransformPath(renderer.transform, root);
            foreach (var clip in clips)
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    if (!PathMatches(binding.path, rendererPath)) continue;
                    var field = binding.propertyName ?? "";
                    if (shapeIndex >= 0 && field == property) return true;
                    if (shapeIndex < 0 && field.IndexOf(property, StringComparison.Ordinal) >= 0) return true;
                    if (shapeIndex < 0 && slot >= 0 && field.IndexOf("m_Materials.Array.data[" + slot + "]", StringComparison.Ordinal) >= 0) return true;
                }
            return false;
        }

        private static bool PathMatches(string binding, string rendererPath)
        {
            if (binding == rendererPath) return true;
            if (string.IsNullOrEmpty(binding) || string.IsNullOrEmpty(rendererPath)) return false;
            return binding.EndsWith("/" + rendererPath, StringComparison.Ordinal) || rendererPath.EndsWith("/" + binding, StringComparison.Ordinal);
        }

        private static Color ParseColor(float[] value)
        {
            if (value == null || value.Length != 4) throw new InvalidOperationException("Color values require exactly four channels.");
            return new Color(value[0], value[1], value[2], value[3]);
        }

        private static float[] ColorArray(Color color) => new[] { color.r, color.g, color.b, color.a };
        private static bool ValidColor(Color color) => Finite(color.r) && Finite(color.g) && Finite(color.b) && Finite(color.a) && color.r >= 0f && color.r <= 1f && color.g >= 0f && color.g <= 1f && color.b >= 0f && color.b <= 1f && color.a >= 0f && color.a <= 1f;
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

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
                var file = Path.Combine(projectPath, scene.path);
                entries.Add("scene:" + scene.path + "|dirty=" + scene.isDirty + "|bytes=" + (File.Exists(file) ? HashFile(file) : "missing"));
                foreach (var root in scene.GetRootGameObjects())
                    foreach (var marker in root.GetComponentsInChildren<AtelierOwnedItem>(true))
                    {
                        if (marker == null) continue;
                        entries.Add("marker:" + GlobalObjectId.GetGlobalObjectIdSlow(marker) + "|" + marker.itemId + "|" + marker.assetId + "|" + marker.sourcePrefabGuid + "|" + marker.ownerObjectId + "|" + EditorJsonUtility.ToJson(marker));
                        foreach (var renderer in marker.GetComponentsInChildren<Renderer>(true))
                        {
                            if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer)) continue;
                            entries.Add("renderer:" + GlobalObjectId.GetGlobalObjectIdSlow(renderer) + "|" + AnimationUtility.CalculateTransformPath(renderer.transform, marker.transform));
                            foreach (var material in renderer.sharedMaterials ?? new Material[0])
                            {
                                if (material == null) { entries.Add("material:null"); continue; }
                                var path = AssetDatabase.GetAssetPath(material);
                                entries.Add("material:" + path + "|" + (string.IsNullOrEmpty(path) ? EditorJsonUtility.ToJson(material) : HashFile(Path.Combine(projectPath, path))) + "|" + EditorJsonUtility.ToJson(material));
                            }
                            var skin = renderer as SkinnedMeshRenderer;
                            if (skin != null && skin.sharedMesh != null)
                                for (var shape = 0; shape < skin.sharedMesh.blendShapeCount; shape++)
                                    entries.Add("shape:" + skin.sharedMesh.GetBlendShapeName(shape) + "=" + skin.GetBlendShapeWeight(shape).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                        }
                    }
            }
            foreach (var relative in new[] { "Packages/manifest.json", "Packages/packages-lock.json" })
            {
                var path = Path.Combine(projectPath, relative.Replace('/', Path.DirectorySeparatorChar));
                entries.Add("package:" + relative + "=" + (File.Exists(path) ? HashFile(path) : "missing"));
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

        private static string HashFile(string path)
        {
            if (!File.Exists(path)) return "missing";
            using (var hash = SHA256.Create()) using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var bytes = hash.ComputeHash(stream); var result = new StringBuilder(bytes.Length * 2); foreach (var value in bytes) result.Append(value.ToString("x2")); return result.ToString();
            }
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
