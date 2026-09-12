using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using nadena.dev.ndmf;
using nadena.dev.ndmf.util;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace OutfitToggleGenerator
{
    // A data capture, not a build. Serialized copies are the only assets saved by this path.
    [InitializeOnLoad]
    internal static class WardrobeShadowCapture
    {
        [Serializable] internal sealed class StagingRecord { public string captureId, projectPath; }
        private static bool capturing;
        static WardrobeShadowCapture() { EditorApplication.delayCall += CleanupInterruptedCaptures; }
        internal static void CleanupInterruptedCaptures()
        {
            if (capturing) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            { EditorApplication.delayCall += CleanupInterruptedCaptures; return; }
            var project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var journal = Path.Combine(project, "Library", "AvatarWardrobe", "capture-staging");
            if (!Directory.Exists(journal)) return;
            foreach (var file in Directory.GetFiles(journal, "*.json"))
            {
                try
                {
                    var record = JsonUtility.FromJson<StagingRecord>(File.ReadAllText(file));
                    var id = Path.GetFileNameWithoutExtension(file);
                    if (!Guid.TryParseExact(id, "N", out _) || record?.captureId != id || record.projectPath != project) continue;
                    var staging = "Assets/__WardrobeCapture_" + id;
                    if (Directory.Exists(Path.Combine(project, staging))) RejectLinks(Path.Combine(project, staging), project);
                    if (AssetDatabase.IsValidFolder(staging)) AssetDatabase.DeleteAsset(staging);
                    var output = Path.Combine(project, "Library", "AvatarWardrobe", "captures", id);
                    if (Directory.Exists(output) && !File.Exists(Path.Combine(output, "manifest.json")))
                    { RejectLinks(output, project); Directory.Delete(output, true); }
                    File.Delete(file);
                }
                catch (Exception) { /* Keep an unverified journal entry; never guess at paths to delete. */ }
            }
        }
        [Serializable] internal sealed class FileRecord { public string path, sha256; public long bytes; }
        [Serializable] internal sealed class PackageRecord
        {
            public string name, version, sourcePath;
            public bool builtIn;
            public List<FileRecord> files = new List<FileRecord>();
        }
        [Serializable] internal sealed class Manifest
        {
            public int schemaVersion = 1;
            public string captureId, projectPath, sourceRevision, visualRevision, recipeRevision, environmentRevision;
            public string unityVersion, buildTarget, colorSpace, scenePath, avatarPrefabPath, candidateGuid, originalCandidateGuid;
            public string captureFormat = "serialized-avatar-prefab-v1";
            public string manifestPath, status = "captured", renderSpecification = "wardrobe-snapshot-v1; source-pose; neutral-light; 640px";
            public int avatarId, qualityLevel;
            public int[] replacePath;
            public WardrobeAppearanceRecipe.Recipe appearanceRecipe;
            public List<FileRecord> files = new List<FileRecord>();
            public List<PackageRecord> packages = new List<PackageRecord>();
            public string[] limitations = { "Static NDMF snapshots; generated animator states, runtime motion and SDK callbacks are not evaluated.",
                "Only packaged creator code is captured. Preview scenes isolate rendering; installed plugin code remains trusted Unity code." };
        }
        private static readonly string[] Settings = { "ProjectVersion.txt", "ProjectSettings.asset", "GraphicsSettings.asset",
            "QualitySettings.asset", "TagManager.asset", "TimeManager.asset", "DynamicsManager.asset", "Physics2DSettings.asset" };

        // Synchronous compatibility entry point for isolated Editor tests. Production uses CaptureAsync.
        internal static Manifest Capture(VRCAvatarDescriptor avatar, string candidateGuid = "", GameObject replaceInstance = null, WardrobeAppearanceRecipe.Recipe recipe = null)
            => CaptureCore(avatar, candidateGuid, replaceInstance, recipe, false, null).GetAwaiter().GetResult();
        internal static Task<Manifest> CaptureAsync(VRCAvatarDescriptor avatar, string candidateGuid = "", GameObject replaceInstance = null, WardrobeAppearanceRecipe.Recipe recipe = null, Func<bool> canContinue = null)
            => CaptureCore(avatar, candidateGuid, replaceInstance, recipe, true, canContinue);
        private static Task Disk(Action action, bool offload)
        {
            if (offload) return Task.Run(action);
            action(); return Task.CompletedTask;
        }
        private static async Task<Manifest> CaptureCore(VRCAvatarDescriptor avatar, string candidateGuid, GameObject replaceInstance, WardrobeAppearanceRecipe.Recipe recipe, bool offload, Func<bool> canContinue)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
                throw new InvalidOperationException("Wait for Unity to finish importing and leave Play Mode before capturing a shadow snapshot.");
            if (avatar == null || EditorUtility.IsPersistent(avatar) || !avatar.gameObject.scene.IsValid())
                throw new InvalidOperationException("Pin an avatar in an open scene before capturing it.");
            WardrobeAppearanceRecipe.CheckSupported(recipe);
            WardrobeTryOnWorker.CheckSupported(avatar.gameObject, false);
            CheckPackagedCode(avatar.gameObject);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                foreach (var plugin in assembly.GetCustomAttributes<ExportsPlugin>())
                    if (PackageInfo.FindForAssembly(plugin.PluginType.Assembly) == null)
                        throw new InvalidOperationException("Shadow capture cannot reproduce project-local NDMF plugin '" +
                            plugin.PluginType.FullName + "'. Use the active-project snapshot fallback.");
            var candidatePath = string.IsNullOrEmpty(candidateGuid) ? "" : AssetDatabase.GUIDToAssetPath(candidateGuid);
            var candidate = string.IsNullOrEmpty(candidatePath) ? null : AssetDatabase.LoadAssetAtPath<GameObject>(candidatePath);
            if (!string.IsNullOrEmpty(candidateGuid) && candidate == null) throw new InvalidOperationException("The candidate prefab is no longer available.");
            if (candidate != null)
            {
                if (candidate.GetComponentInChildren<VRCAvatarDescriptor>(true) != null) throw new InvalidOperationException("An avatar root is not a wardrobe candidate.");
                WardrobeTryOnWorker.CheckSupported(candidate, true);
                CheckPackagedCode(candidate);
            }
            if (replaceInstance != null && (replaceInstance == avatar.gameObject || !replaceInstance.transform.IsChildOf(avatar.transform)))
                throw new InvalidOperationException("The replacement must be an exact instance under the pinned avatar.");
            if (candidate == null && replaceInstance != null) throw new InvalidOperationException("A replacement requires a candidate prefab.");

            var project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var id = Guid.NewGuid().ToString("N");
            var staging = "Assets/__WardrobeCapture_" + id;
            var output = Path.Combine(project, "Library", "AvatarWardrobe", "captures", id);
            var journalPath = Path.Combine(project, "Library", "AvatarWardrobe", "capture-staging", id + ".json");
            var fingerprints = offload ? await WardrobeTryOnWorker.FingerprintPairAsync(avatar) :
                new[] { WardrobeTryOnWorker.SourceFingerprint(avatar), WardrobeTryOnWorker.VisualFingerprint(avatar) };
            var manifest = new Manifest
            {
                captureId = id, projectPath = project, avatarId = avatar.GetInstanceID(), appearanceRecipe = recipe,
                sourceRevision = fingerprints[0], visualRevision = fingerprints[1],
                unityVersion = Application.unityVersion, buildTarget = EditorUserBuildSettings.activeBuildTarget.ToString(),
                colorSpace = QualitySettings.activeColorSpace.ToString(), qualityLevel = QualitySettings.GetQualityLevel(),
                originalCandidateGuid = candidateGuid ?? "", candidateGuid = "",
                replacePath = replaceInstance == null ? null : WardrobeTryOnWorker.SiblingPath(avatar.transform, replaceInstance.transform)
            };
            manifest.recipeRevision = HashText(manifest.originalCandidateGuid + "|" +
                (string.IsNullOrEmpty(candidatePath) ? "" : AssetDatabase.GetAssetDependencyHash(candidatePath).ToString()) + "|" + string.Join(",", manifest.replacePath ?? new int[0]) + "|" + (recipe?.Identity ?? ""));
            var previous = SceneManager.GetActiveScene();
            var sourceDirty = avatar.gameObject.scene.isDirty;
            Scene captureScene = default;
            var owned = new List<Object>();
            var succeeded = false;
            try
            {
                capturing = true;
                await Disk(() => WriteJson(journalPath, new StagingRecord { captureId = id, projectPath = project }), offload);
                if (avatar == null || (canContinue != null && !canContinue())) throw new OperationCanceledException("Capture session ended.");
                AssetDatabase.CreateFolder("Assets", "__WardrobeCapture_" + id);
                captureScene = EditorSceneManager.NewPreviewScene();
                var host = new GameObject("Capture host");
                host.SetActive(false);
                SceneManager.MoveGameObjectToScene(host, captureScene);
                var clone = Object.Instantiate(avatar.gameObject, host.transform, false);
                clone.name = avatar.gameObject.name;
                clone.transform.SetPositionAndRotation(avatar.transform.position, avatar.transform.rotation);
                clone.transform.localScale = avatar.transform.lossyScale;
                // A parent-induced shear cannot be represented by a stand-alone serialized root transform.
                var expected = Matrix4x4.TRS(avatar.transform.position, avatar.transform.rotation, avatar.transform.lossyScale);
                for (var row = 0; row < 4; row++) for (var column = 0; column < 4; column++)
                    if (Mathf.Abs(expected[row, column] - avatar.transform.localToWorldMatrix[row, column]) > 0.0001f)
                        throw new InvalidOperationException("The avatar has a sheared parent transform. Use the active-project snapshot fallback.");
                PersistAssets(clone, staging, owned);
                clone.transform.SetParent(null, true);
                Object.DestroyImmediate(host);
                if (candidate != null)
                {
                    var candidateHost = new GameObject("Candidate host"); candidateHost.SetActive(false);
                    SceneManager.MoveGameObjectToScene(candidateHost, captureScene);
                    var candidateClone = Object.Instantiate(candidate, candidateHost.transform, false);
                    candidateClone.name = candidate.name;
                    PersistAssets(candidateClone, staging, owned);
                    var capturedPrefab = staging + "/Candidate.prefab";
                    if (PrefabUtility.SaveAsPrefabAsset(candidateClone, capturedPrefab) == null)
                        throw new InvalidOperationException("Unity could not serialize the candidate copy.");
                    manifest.candidateGuid = AssetDatabase.AssetPathToGUID(capturedPrefab);
                    Object.DestroyImmediate(candidateHost);
                }
                manifest.avatarPrefabPath = staging + "/Avatar.prefab";
                if (PrefabUtility.SaveAsPrefabAsset(clone, manifest.avatarPrefabPath) == null)
                    throw new InvalidOperationException("Unity could not serialize the complete isolated avatar capture.");
                var roots = new List<string> { manifest.avatarPrefabPath };
                if (!string.IsNullOrEmpty(manifest.candidateGuid)) roots.Add(AssetDatabase.GUIDToAssetPath(manifest.candidateGuid));
                var pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
                if (pipeline != null) roots.Add(AssetDatabase.GetAssetPath(pipeline));
                var paths = AssetDatabase.GetDependencies(roots.ToArray(), true)
                    .Where(path => path.StartsWith("Assets/", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
                foreach (var package in PackageInfo.GetAllRegisteredPackages().OrderBy(package => package.name, StringComparer.Ordinal))
                {
                    var entry = new PackageRecord { name = package.name, version = package.version,
                        sourcePath = package.resolvedPath, builtIn = package.source == UnityEditor.PackageManager.PackageSource.BuiltIn };
                    manifest.packages.Add(entry);
                }
                await Disk(() =>
                {
                foreach (var setting in Settings)
                {
                    var path = "ProjectSettings/" + setting;
                    if (File.Exists(Path.Combine(project, path))) paths.Add(path);
                }
                foreach (var path in paths.ToArray())
                    if (File.Exists(Path.Combine(project, path + ".meta"))) paths.Add(path + ".meta");
                Directory.CreateDirectory(Path.Combine(output, "files"));
                foreach (var path in paths.OrderBy(value => value, StringComparer.Ordinal))
                {
                    var source = Path.Combine(project, path);
                    if (Directory.Exists(source)) continue;
                    if (!File.Exists(source)) throw new InvalidOperationException("Missing capture dependency: " + path);
                    RejectLinks(source, project);
                    var destination = Path.Combine(output, "files", path);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    var beforeCopy = new FileInfo(source);
                    var length = beforeCopy.Length; var modified = beforeCopy.LastWriteTimeUtc;
                    File.Copy(source, destination, false);
                    var copied = FileInfoFor(destination, path);
                    beforeCopy.Refresh();
                    if (!beforeCopy.Exists || beforeCopy.Length != length || beforeCopy.LastWriteTimeUtc != modified || HashFile(source) != copied.sha256)
                        throw new IOException("A capture dependency changed while copying: " + path);
                    manifest.files.Add(copied);
                }
                foreach (var entry in manifest.packages)
                    if (!entry.builtIn) SnapshotPackage(project, entry);
                manifest.environmentRevision = HashText(manifest.unityVersion + "|" + manifest.buildTarget + "|" +
                    manifest.colorSpace + "|" + manifest.qualityLevel + "|" +
                    string.Join(";", manifest.packages.Select(package => package.name + "@" + package.version + ":" +
                        string.Join(",", package.files.Select(file => file.path + "=" + file.sha256)))) + "|" +
                    string.Join(";", manifest.files.Where(file => file.path.StartsWith("ProjectSettings/", StringComparison.Ordinal))
                        .Select(file => file.path + "=" + file.sha256)));
                }, offload);
                if ((canContinue != null && !canContinue()) || avatar == null || manifest.sourceRevision != (offload ? await WardrobeTryOnWorker.FingerprintAsync(avatar, false) : WardrobeTryOnWorker.SourceFingerprint(avatar)) || sourceDirty != avatar.gameObject.scene.isDirty)
                    throw new InvalidOperationException("The source avatar changed during capture. Capture it again.");
                manifest.manifestPath = Path.Combine(output, "manifest.json");
                await Disk(() => WriteJson(manifest.manifestPath, manifest), offload);
                succeeded = true;
                return manifest;
            }
            finally
            {
                capturing = false;
                if (SceneManager.GetActiveScene() == captureScene && previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                if (captureScene.IsValid()) EditorSceneManager.ClosePreviewScene(captureScene);
                // No global SaveAssets or generated-asset cleanup: these paths belong solely to this capture.
                if (AssetDatabase.IsValidFolder(staging)) AssetDatabase.DeleteAsset(staging);
                foreach (var obj in owned) if (obj != null && !EditorUtility.IsPersistent(obj)) Object.DestroyImmediate(obj);
                await Disk(() =>
                {
                    if (!succeeded && Directory.Exists(output)) Directory.Delete(output, true);
                    if (File.Exists(journalPath)) File.Delete(journalPath);
                }, offload);
            }
        }

        private static void CheckPackagedCode(GameObject root)
        {
            foreach (var component in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (component == null) continue;
                var name = component.GetType().FullName ?? "";
                if (name.StartsWith("VRC.", StringComparison.Ordinal) || name.StartsWith("nadena.dev.", StringComparison.Ordinal) ||
                    name.StartsWith("UnityEngine.", StringComparison.Ordinal)) continue;
                var script = MonoScript.FromMonoBehaviour(component);
                var path = script == null ? "" : AssetDatabase.GetAssetPath(script);
                if (!path.StartsWith("Packages/", StringComparison.Ordinal))
                    throw new InvalidOperationException("Shadow capture cannot reproduce project-local creator script '" + name +
                        "'. Use the active-project snapshot fallback or a packaged version of this creator tool.");
            }
        }
        private static void PersistAssets(GameObject root, string folder, List<Object> owned)
        {
            var copies = new Dictionary<Object, Object>();
            var visited = new HashSet<Object>();
            var links = new List<(Object target, string path, Object value)>();
            foreach (var component in root.GetComponentsInChildren<Component>(true)) Visit(component);
            // Unity may discard links to transient assets during CreateAsset. Persist every node first,
            // then restore the collected links. Animator native strong references must be subassets
            // in the same file; a shared resource container preserves those cyclic graphs.
            var containerPath = folder + "/Resources_" + Guid.NewGuid().ToString("N") + ".asset";
            Object container = null;
            foreach (var copy in copies.Values)
            {
                if (container == null) { AssetDatabase.CreateAsset(copy, containerPath); container = copy; }
                else AssetDatabase.AddObjectToAsset(copy, container);
            }
            foreach (var group in links.GroupBy(link => link.target))
                using (var serialized = new SerializedObject(group.Key))
                {
                    foreach (var link in group)
                    {
                        var property = serialized.FindProperty(link.path);
                        if (property == null) throw new InvalidOperationException("A captured asset changed its serialized reference layout.");
                        property.objectReferenceValue = link.value;
                    }
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
            foreach (var copy in copies.Values) AssetDatabase.SaveAssetIfDirty(copy);
            void Visit(Object obj)
            {
                if (obj == null || obj is Transform || !visited.Add(obj)) return;
                using (var serialized = new SerializedObject(obj))
                {
                    foreach (var property in serialized.ObjectProperties())
                    {
                        if (property.propertyPath == "m_Script") continue;
                        var reference = property.objectReferenceValue;
                        if (reference == null) continue;
                        var transform = (reference as GameObject)?.transform ?? (reference as Component)?.transform;
                        if (transform != null)
                        {
                            if (transform != root.transform && !transform.IsChildOf(root.transform))
                                throw new InvalidOperationException("Unsupported scene-external or prefab reference in '" + obj.name + "." + property.propertyPath + "'.");
                            continue;
                        }
                        if (reference is Texture || reference is Shader || reference is MonoScript || reference is AudioClip || reference is ComputeShader)
                        {
                            if (!EditorUtility.IsPersistent(reference)) throw new InvalidOperationException("Shadow capture cannot serialize nonpersistent " +
                                reference.GetType().Name + " '" + reference.name + "'. Save that resource as an asset or use the active-project fallback.");
                            var resourcePath = AssetDatabase.GetAssetPath(reference);
                            var builtInShader = reference is Shader && (resourcePath == "Resources/unity_builtin_extra" || resourcePath == "Library/unity default resources");
                            if (!builtInShader && EditorUtility.IsDirty(reference)) throw new InvalidOperationException("Imported resource '" + reference.name +
                                "' has unsaved changes that cannot be captured safely. Save it or use the active-project fallback.");
                            continue;
                        }
                        if (owned.Contains(reference))
                        {
                            links.Add((obj, property.propertyPath, reference));
                            continue;
                        }
                        if (!copies.TryGetValue(reference, out var copy))
                        {
                            copy = WardrobeTryOnWorker.CloneAssetShell(reference);
                            EditorUtility.CopySerialized(reference, copy);
                            copy.name = reference.name;
                            copy.hideFlags = HideFlags.None; copies.Add(reference, copy); owned.Add(copy);
                            Visit(copy);
                        }
                        links.Add((obj, property.propertyPath, copy));
                        property.objectReferenceValue = copy;
                    }
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
            }
        }
        [Serializable] private sealed class PackageSnapshot
        {
            public string kind = "wardrobe-package-snapshot-v1", key, name, version;
            public long bytes, lastUsed;
        }
        private sealed class SnapshotCacheLock : IDisposable
        {
            private static readonly object Gate = new object();
            private FileStream stream;
            internal SnapshotCacheLock(string project)
            {
                System.Threading.Monitor.Enter(Gate);
                try
                {
                    var folder = Path.Combine(project, "Library", "AvatarWardrobe");
                    Directory.CreateDirectory(folder);
                    stream = new FileStream(Path.Combine(folder, "snapshot-retention-v2.lock"),
                        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
                    for (var attempt = 0; attempt < 200; attempt++)
                    {
                        try { stream.Lock(0, 1); return; }
                        catch (IOException) { System.Threading.Thread.Sleep(10); }
                    }
                    throw new InvalidOperationException("Snapshot retention is busy. Try the capture again.");
                }
                catch { stream?.Dispose(); stream = null; System.Threading.Monitor.Exit(Gate); throw; }
            }
            public void Dispose()
            {
                if (stream == null) return;
                try { stream.Unlock(0, 1); }
                finally { stream.Dispose(); stream = null; System.Threading.Monitor.Exit(Gate); }
            }
        }
        private static void SnapshotPackage(string project, PackageRecord entry)
        {
            var source = entry.sourcePath;
            entry.files = PackageFiles(source).Select(file => FileInfoFor(file, Relative(source, file))).ToList();
            var key = HashText(entry.name + "@" + entry.version + "|" + string.Join(";", entry.files.Select(file => file.path + "=" + file.sha256)));
            var cache = Path.Combine(project, "Library", "AvatarWardrobe", "package-snapshots");
            Directory.CreateDirectory(cache);
            var destination = Path.Combine(cache, entry.name + "-" + key);
            var stamp = new PackageSnapshot { key = key, name = entry.name, version = entry.version,
                bytes = entry.files.Sum(file => file.bytes), lastUsed = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
            using (new SnapshotCacheLock(project))
            {
                if (Directory.Exists(destination))
                {
                    var marker = Path.Combine(destination, ".wardrobe-package-snapshot.json");
                    if (!File.Exists(marker) || JsonUtility.FromJson<PackageSnapshot>(File.ReadAllText(marker))?.key != key)
                        throw new InvalidOperationException("An unrecognized directory occupies the package snapshot cache.");
                    WriteJson(marker, stamp); entry.sourcePath = destination; return;
                }
            }
            var temporary = Path.Combine(cache, ".package-stage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            try
            {
                foreach (var file in entry.files)
                {
                    var target = Path.Combine(temporary, file.path);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Copy(Path.Combine(source, file.path), target, false);
                    if (HashFile(target) != file.sha256)
                        throw new InvalidOperationException("A package changed while it was being captured. Capture again: " + entry.name);
                }
                WriteJson(Path.Combine(temporary, ".wardrobe-package-snapshot.json"), stamp);
                using (new SnapshotCacheLock(project))
                {
                    if (!Directory.Exists(destination)) Directory.Move(temporary, destination);
                    else if (!File.Exists(Path.Combine(destination, ".wardrobe-package-snapshot.json")))
                        throw new InvalidOperationException("An unrecognized directory occupies the package snapshot cache.");
                    WriteJson(Path.Combine(destination, ".wardrobe-package-snapshot.json"), stamp);
                    entry.sourcePath = destination;
                }
            }
            finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
        }

        private static IEnumerable<string> PackageFiles(string root)
        {
            if (!Directory.Exists(root)) throw new InvalidOperationException("Package source is unavailable: " + root);
            foreach (var entry in Directory.GetFileSystemEntries(root).OrderBy(path => path, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(entry);
                if (name == ".git" || name == "Library" || name == "Temp" || name == "obj" || name == "node_modules" ||
                    name == "__Generated" || name == "__pycache__" || name == ".DS_Store" || name.StartsWith("._", StringComparison.Ordinal)) continue;
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("A package contains a link that cannot be isolated safely: " + entry);
                if (Directory.Exists(entry)) foreach (var nested in PackageFiles(entry)) yield return nested;
                else yield return entry;
            }
        }
        private static void RejectLinks(string path, string root)
        {
            for (var entry = path; !string.Equals(entry, root, StringComparison.Ordinal); entry = Path.GetDirectoryName(entry))
            {
                if (string.IsNullOrEmpty(entry)) throw new InvalidOperationException("A capture dependency escaped the project.");
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Linked capture dependencies are unsupported: " + path);
            }
        }
        private static string Relative(string root, string path) => path.Substring(root.TrimEnd(Path.DirectorySeparatorChar).Length + 1).Replace('\\', '/');
        private static FileRecord FileInfoFor(string path, string relative) => new FileRecord { path = relative, bytes = new FileInfo(path).Length, sha256 = HashFile(path) };
        internal static string HashFile(string path) { using (var stream = File.OpenRead(path)) using (var hash = SHA256.Create()) return Hex(hash.ComputeHash(stream)); }
        internal static string HashText(string value) { using (var hash = SHA256.Create()) return Hex(hash.ComputeHash(Encoding.UTF8.GetBytes(value))); }
        private static string Hex(byte[] value) => BitConverter.ToString(value).Replace("-", "").ToLowerInvariant();
        internal static void WriteJson(string path, object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonUtility.ToJson(value, true), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
    }

    // Persistent, graphics-enabled shadow-editor entry point. The desktop process owns the project
    // and filesystem queue; this entry point executes only fixed snapshot commands on that project.
    [InitializeOnLoad]
    public static class WardrobeShadowWorker
    {
        [Serializable] private sealed class RunConfig { public string ownerToken, projectPath, manifestPath, runtimePath; }
        [Serializable] private sealed class State { public string state, message, captureId, sourceRevision, environmentRevision; public int pid; }
        [Serializable] private sealed class Command { public string id, type, view; public float zoom = 1f; public bool before; }
        [Serializable] private sealed class Result
        {
            public string id, status, message, image, imageSha256, captureId, sourceRevision, recipeRevision, environmentRevision, view;
            public bool before;
            public WardrobeTryOnWorker.Prepared preview;
        }
        private static RunConfig config;
        private static WardrobeShadowCapture.Manifest manifest;
        private static WardrobeTryOnWorker.Prepared prepared;
        private static VRCAvatarDescriptor sourceAvatar;
        private static Scene sourceScene;
        private static bool initialized, busy;
        private static double nextPoll;
        private static string configPath;
        static WardrobeShadowWorker()
        {
            var arguments = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(arguments, "-wardrobeWorker");
            if (index >= 0 && index + 1 < arguments.Length)
            {
                configPath = arguments[index + 1];
                EditorApplication.update += Tick;
                AssemblyReloadEvents.beforeAssemblyReload += CleanupSource;
                EditorApplication.quitting += CleanupSource;
            }
        }
        public static void Start() { if (string.IsNullOrEmpty(configPath)) throw new InvalidOperationException("The shadow worker requires an owned -wardrobeWorker configuration."); }
        private static void Tick()
        {
            if (busy || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.timeSinceStartup < nextPoll) return;
            nextPoll = EditorApplication.timeSinceStartup + 0.15;
            busy = true;
            try
            {
                if (!initialized) { Initialize(); return; }
                foreach (var path in Directory.GetFiles(Path.Combine(config.runtimePath, "commands"), "*.json").OrderBy(path => path, StringComparer.Ordinal).Take(1))
                {
                    var command = JsonUtility.FromJson<Command>(File.ReadAllText(path));
                    if (command == null || !Guid.TryParseExact(command.id, "N", out _) || Path.GetFileNameWithoutExtension(path) != command.id)
                        throw new InvalidOperationException("Invalid shadow-worker command identity.");
                    var resultPath = Path.Combine(config.runtimePath, "results", command.id + ".json");
                    if (File.Exists(resultPath)) { File.Delete(path); continue; }
                    var result = new Result { id = command.id, captureId = manifest.captureId, sourceRevision = manifest.sourceRevision,
                        recipeRevision = manifest.recipeRevision, environmentRevision = manifest.environmentRevision, view = command.view, before = command.before };
                    try
                    {
                        if (command.type == "stop")
                        {
                            WardrobeTryOnWorker.CancelAll(); result.status = "succeeded";
                            WardrobeShadowCapture.WriteJson(resultPath, result); File.Delete(path);
                            WriteState("stopped", ""); EditorApplication.Exit(0); return;
                        }
                        if (command.type != "render") throw new InvalidOperationException("Only render and stop commands are supported by the shadow worker.");
                        if (prepared == null || !WardrobeTryOnWorker.Validate(prepared.token, sourceAvatar, out _)) Prepare();
                        var bytes = WardrobeTryOnWorker.RenderView(prepared.token, command.view, command.zoom, command.before);
                        result.image = Path.Combine("images", command.id + ".png").Replace('\\', '/');
                        var destination = Path.Combine(config.runtimePath, result.image);
                        File.WriteAllBytes(destination + ".tmp", bytes); File.Move(destination + ".tmp", destination);
                        result.imageSha256 = WardrobeShadowCapture.HashFile(destination);
                        result.preview = prepared; result.status = "succeeded";
                    }
                    catch (Exception exception) { result.status = "failed"; result.message = exception.Message; }
                    WardrobeShadowCapture.WriteJson(resultPath, result); File.Delete(path);
                    WriteState("ready", "");
                }
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogError("Wardrobe shadow worker: " + exception.Message);
                WriteState("failed", exception.Message); EditorApplication.update -= Tick;
                EditorApplication.Exit(2);
            }
            finally { busy = false; }
        }
        private static void Initialize()
        {
            var proposed = JsonUtility.FromJson<RunConfig>(File.ReadAllText(configPath));
            var current = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            if (proposed == null || string.IsNullOrEmpty(proposed.ownerToken) || Path.GetFullPath(proposed.projectPath) != current ||
                !File.Exists(Path.Combine(current, ".wardrobe-shadow-owner.json"))) throw new InvalidOperationException("The worker is not running in its owned shadow project.");
            var owner = JsonUtility.FromJson<RunConfig>(File.ReadAllText(Path.Combine(current, ".wardrobe-shadow-owner.json")));
            if (owner == null || owner.ownerToken != proposed.ownerToken || owner.projectPath != proposed.projectPath)
                throw new InvalidOperationException("Shadow project ownership does not match its launch configuration.");
            if (!Path.GetFullPath(proposed.runtimePath).StartsWith(current + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                !Path.GetFullPath(proposed.manifestPath).StartsWith(current + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidOperationException("Shadow inputs and output must be inside the owned worker project.");
            config = proposed;
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                throw new InvalidOperationException("Shadow photographs require a graphics-enabled Unity launch; -nographics is unsupported.");
            manifest = JsonUtility.FromJson<WardrobeShadowCapture.Manifest>(File.ReadAllText(config.manifestPath));
            if (manifest == null || manifest.schemaVersion != 1 || manifest.unityVersion != Application.unityVersion)
                throw new InvalidOperationException("The worker Unity version does not match this capture.");
            if (string.Equals(Path.GetFullPath(manifest.projectPath), current, StringComparison.Ordinal))
                throw new InvalidOperationException("A shadow worker cannot run against the active source project.");
            WriteState("initializing", "Checking the captured environment.");
            foreach (var package in manifest.packages)
            {
                var actual = PackageInfo.GetAllRegisteredPackages().FirstOrDefault(value => value.name == package.name);
                if (actual == null || actual.version != package.version) throw new InvalidOperationException("Worker package mismatch: " + package.name);
            }
            if (!Enum.TryParse(manifest.buildTarget, out BuildTarget target)) throw new InvalidOperationException("Unsupported captured build target.");
            if (EditorUserBuildSettings.activeBuildTarget != target)
            {
                WriteState("initializing", "Switching the private worker to the captured build platform.");
                if (!EditorUserBuildSettings.SwitchActiveBuildTarget(BuildPipeline.GetBuildTargetGroup(target), target))
                    throw new InvalidOperationException("The required platform module is not available in this Unity editor.");
                return;
            }
            QualitySettings.SetQualityLevel(manifest.qualityLevel, false);
            if (QualitySettings.activeColorSpace.ToString() != manifest.colorSpace) throw new InvalidOperationException("Worker color-space mismatch.");
            if (!string.IsNullOrEmpty(manifest.avatarPrefabPath))
            {
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(manifest.avatarPrefabPath);
                if (asset == null) throw new InvalidOperationException("The captured avatar prefab was not imported correctly.");
                sourceScene = EditorSceneManager.NewPreviewScene();
                var host = new GameObject("Captured avatar host"); host.SetActive(false);
                SceneManager.MoveGameObjectToScene(host, sourceScene);
                var source = Object.Instantiate(asset, host.transform, false);
                source.transform.SetParent(null, false); Object.DestroyImmediate(host);
            }
            else sourceScene = EditorSceneManager.OpenScene(manifest.scenePath, OpenSceneMode.Additive);
            Prepare(); initialized = true; WriteState("ready", "");
        }
        private static void Prepare()
        {
            var avatars = sourceScene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<VRCAvatarDescriptor>(true)).ToArray();
            if (avatars.Length != 1) throw new InvalidOperationException("The captured scene must contain exactly one avatar descriptor.");
            WriteState("processing", "Preparing processed before/after copies in the shadow editor.");
            var replacement = manifest.replacePath == null || manifest.replacePath.Length == 0 ? null :
                WardrobeTryOnWorker.AtSiblingPath(avatars[0].transform, manifest.replacePath).gameObject;
            sourceAvatar = avatars[0];
            prepared = WardrobeTryOnWorker.Prepare(sourceAvatar, manifest.candidateGuid, replacement, recipe: manifest.appearanceRecipe);
        }
        private static void CleanupSource()
        {
            WardrobeTryOnWorker.CancelAll();
            if (sourceScene.IsValid())
            {
                if (EditorSceneManager.IsPreviewScene(sourceScene)) EditorSceneManager.ClosePreviewScene(sourceScene);
                else if (sourceScene.isLoaded) EditorSceneManager.CloseScene(sourceScene, true);
            }
            sourceScene = default;
        }
        private static void WriteState(string state, string message)
        {
            if (config == null || string.IsNullOrEmpty(config.runtimePath)) return;
            WardrobeShadowCapture.WriteJson(Path.Combine(config.runtimePath, "state.json"), new State { state = state, message = message,
                pid = System.Diagnostics.Process.GetCurrentProcess().Id, captureId = manifest?.captureId,
                sourceRevision = manifest?.sourceRevision, environmentRevision = manifest?.environmentRevision });
        }
    }
}
