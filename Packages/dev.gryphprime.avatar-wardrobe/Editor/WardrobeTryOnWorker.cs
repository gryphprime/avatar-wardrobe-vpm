using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using nadena.dev.modular_avatar.core;
using nadena.dev.ndmf;
using nadena.dev.ndmf.platform;
using nadena.dev.ndmf.util;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using Object = UnityEngine.Object;

namespace OutfitToggleGenerator
{
    // Main-thread, in-memory processed previews. Neither Install nor MA's SetupOutfitUI is safe here:
    // both change editor Undo/selection state. Only creator-prepared skinned outfits are supported.
    [InitializeOnLoad]
    internal static class WardrobeTryOnWorker
    {
        [Serializable]
        internal sealed class TextureContribution
        {
            public string name;
            public int width, height;
            public long estimatedBytes;
        }

        [Serializable]
        internal sealed class Metrics
        {
            public int renderers, visibleRenderers, materials, textures, physBones, contacts;
            public long triangles;
            public int expressionParameters, expressionMenuControls;
            public long estimatedTextureBytes;
            public string textureEstimateScope = "Unity runtime allocation estimate for distinct referenced textures; includes editor allocations and is not exact platform VRAM.";
            public TextureContribution[] largestTextures = new TextureContribution[0];
        }

        [Serializable]
        internal sealed class Prepared
        {
            public string token, guid, sourceFingerprint, configurationId, previewFingerprint, scope, processorVersion;
            public string sourceRevision, recipeRevision, environmentRevision, renderSpecification;
            public int avatarId, replaceInstanceId;
            public bool processed;
            public long elapsedMilliseconds;
            public string[] limitations, menuControls, parameters;
            public WardrobeAppearanceRecipe.Recipe appearanceRecipe;
            public Metrics beforeMetrics, afterMetrics;
        }

        private sealed class View : IDisposable
        {
            internal readonly PreviewRenderUtility preview = new PreviewRenderUtility();
            internal readonly HashSet<Object> owned = new HashSet<Object>();
            internal GameObject container, root;
            internal Bounds bounds;
            internal WardrobeAppearanceRecipe.Bound appearance;

            public void Dispose()
            {
                // Destroy the hierarchy before its transient materials/meshes/controllers.
                if (container != null) Object.DestroyImmediate(container);
                preview.Cleanup();
                foreach (var asset in owned)
                    if (asset != null && !EditorUtility.IsPersistent(asset) && !(asset is GameObject) && !(asset is Component))
                        Object.DestroyImmediate(asset);
                owned.Clear();
            }
        }

        private sealed class Session : IDisposable
        {
            internal Prepared result;
            internal VRCAvatarDescriptor source;
            internal string assetPath, assetVersion;
            internal View before, after;
            internal Bounds frame;
            internal double touched;
            public void Dispose() { before?.Dispose(); after?.Dispose(); }
        }

        private static readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>();
        private static readonly int mainThread = Thread.CurrentThread.ManagedThreadId;
        private const double LifetimeSeconds = 300;
        private const int ImageSize = 640;

        static WardrobeTryOnWorker()
        {
            AssemblyReloadEvents.beforeAssemblyReload += CancelAll;
            EditorApplication.quitting += CancelAll;
            EditorApplication.playModeStateChanged += _ => CancelAll();
            EditorApplication.update += Expire;
        }

        internal static Prepared Prepare(VRCAvatarDescriptor avatar, string guid, GameObject replaceInstance = null,
            Action<GameObject> configureAfterClone = null, string configurationId = null, WardrobeAppearanceRecipe.Recipe recipe = null)
        {
            AssertMainThread();
            WardrobeAppearanceRecipe.CheckSupported(recipe);
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
                throw new InvalidOperationException("Try On is available when Unity has finished importing and is outside Play Mode.");
            if (avatar == null || EditorUtility.IsPersistent(avatar) || !avatar.gameObject.scene.IsValid())
                throw new InvalidOperationException("Pin an avatar in an open scene before using Try On.");
            if (configureAfterClone != null && string.IsNullOrEmpty(configurationId))
                throw new ArgumentException("A configured Try On requires a stable configuration identity.");
            var path = string.IsNullOrEmpty(guid) ? "" : AssetDatabase.GUIDToAssetPath(guid);
            var prefab = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (!string.IsNullOrEmpty(guid) && prefab == null) throw new InvalidOperationException("The selected prefab is no longer available. Refresh the library.");
            if (prefab != null && prefab.GetComponentInChildren<VRCAvatarDescriptor>(true) != null)
                throw new InvalidOperationException("Try On requires a wardrobe item, not another avatar root.");
            if (replaceInstance != null && (replaceInstance == avatar.gameObject || !replaceInstance.transform.IsChildOf(avatar.transform)))
                throw new InvalidOperationException("The selected worn instance does not belong to the pinned avatar.");
            CheckSupported(avatar.gameObject, false);
            if (prefab != null) CheckSupported(prefab, true);
            if (sessions.Count >= 2) Cancel(sessions.OrderBy(pair => pair.Value.touched).First().Key);

            var stopwatch = Stopwatch.StartNew();
            var session = new Session
            {
                source = avatar, assetPath = path, assetVersion = AssetVersion(path),
                result = new Prepared
                {
                    token = Guid.NewGuid().ToString("N"), guid = guid, avatarId = avatar.GetInstanceID(),
                    replaceInstanceId = replaceInstance == null ? 0 : replaceInstance.GetInstanceID(),
                    sourceFingerprint = SourceFingerprint(avatar), configurationId = configurationId ?? "", appearanceRecipe = recipe,
                    environmentRevision = EnvironmentRevision(),
                    renderSpecification = "wardrobe-snapshot-v1; 640px; source-pose; neutral-light; front=180; three-quarter=135; back=0",
                    scope = "NDMF processed avatar with resolved wardrobe defaults; " + (string.IsNullOrEmpty(recipe?.scopeId) ? "current scene visibility" : "preset " + recipe.scopeId) + "; static editor pose",
                    processorVersion = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(AvatarProcessor).Assembly)?.version ?? "unknown",
                    limitations = new[]
                    {
                        "This previews the copied source pose and processed static object states. Generated animator states are not evaluated. Gesture animations, expression controls, PhysBone motion and VRChat runtime behavior are not simulated.",
                        "NDMF processing is included. SDK upload callbacks and non-NDMF creator build pipelines are not included.",
                        "Preparation processes two complete avatar copies on Unity's main thread. Cancel becomes available when preparation finishes. Temporary previews expire after five idle minutes."
                    }
                }
            };
            try
            {
                session.before = CreateView(avatar.gameObject, null, null, recipe);
                session.after = CreateView(avatar.gameObject, prefab,
                    replaceInstance == null ? null : SiblingPath(avatar.transform, replaceInstance.transform), recipe);
                if (configureAfterClone != null)
                {
                    configureAfterClone(session.after.root);
                    CheckSupported(session.after.root, false);
                    CloneMutableAssets(session.after);
                }
                session.before.appearance = WardrobeAppearanceRecipe.Bind(session.before.root, recipe);
                session.after.appearance = WardrobeAppearanceRecipe.Bind(session.after.root, recipe);
                Process(session.before);
                Process(session.after);
                session.frame = session.before.bounds;
                session.frame.Encapsulate(session.after.bounds);
                CheckShaders(session.before.root);
                CheckShaders(session.after.root);
                var extraComponents = AdditionalCreatorComponents(avatar.gameObject)
                    .Concat(prefab == null ? Enumerable.Empty<string>() : AdditionalCreatorComponents(prefab)).Distinct().ToArray();
                if (extraComponents.Length > 0)
                    session.result.limitations = session.result.limitations.Concat(new[]
                    { "Additional creator components are present: " + string.Join(", ", extraComponents) +
                        ". Only their registered NDMF passes are processed; separate editor or SDK callbacks are outside this snapshot." }).ToArray();
                session.result.beforeMetrics = ReadMetrics(session.before.root);
                session.result.afterMetrics = ReadMetrics(session.after.root);
                var descriptor = session.after.root.GetComponent<VRCAvatarDescriptor>();
                session.result.menuControls = MenuControls(descriptor?.expressionsMenu).ToArray();
                session.result.parameters = descriptor?.expressionParameters?.parameters?.Where(parameter => parameter != null)
                    .Select(parameter => parameter.name + " (" + parameter.valueType + ")").ToArray() ?? new string[0];
                if (session.result.sourceFingerprint != SourceFingerprint(avatar) || session.assetVersion != AssetVersion(path))
                    throw new InvalidOperationException("The avatar or candidate changed during preparation. Try On again before wearing it.");
                session.result.sourceRevision = session.result.sourceFingerprint;
                session.result.recipeRevision = Hash128.Compute(session.assetVersion + "|" + session.result.guid + "|" +
                    session.result.replaceInstanceId + "|" + session.result.configurationId + "|" + (recipe?.Identity ?? "")).ToString();
                session.result.previewFingerprint = Hash128.Compute(session.result.sourceRevision + "|" + session.result.recipeRevision + "|" +
                    session.result.environmentRevision + "|" + session.result.renderSpecification).ToString();
                session.result.processed = true;
                session.result.elapsedMilliseconds = stopwatch.ElapsedMilliseconds;
                session.touched = EditorApplication.timeSinceStartup;
                sessions.Add(session.result.token, session);
                return session.result;
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }

        internal static bool Validate(string token, VRCAvatarDescriptor avatar, out string reason)
        {
            AssertMainThread();
            if (!TrySession(token, out var session)) { reason = "This Try On has expired. Prepare it again before wearing it."; return false; }
            if (avatar == null || session.source != avatar || session.result.avatarId != avatar.GetInstanceID())
            { reason = "The pinned avatar changed. Try On again for this avatar."; return false; }
            if (session.result.sourceFingerprint != SourceFingerprint(avatar))
            { reason = "The avatar changed since this Try On. Prepare it again before wearing it."; return false; }
            if ((!string.IsNullOrEmpty(session.result.guid) && AssetDatabase.GUIDToAssetPath(session.result.guid) != session.assetPath) ||
                session.assetVersion != AssetVersion(session.assetPath))
            { reason = "The candidate prefab changed since this Try On. Prepare it again before wearing it."; return false; }
            if (session.result.environmentRevision != EnvironmentRevision())
            { reason = "The Unity processing or rendering environment changed. Prepare this Try On again."; return false; }
            session.touched = EditorApplication.timeSinceStartup;
            reason = "";
            return true;
        }

        internal static byte[] RenderView(string token, string view, float zoom = 1f, bool before = false)
        {
            switch (view)
            {
                case "front": return Render(token, 180f, zoom, before);
                case "three-quarter": return Render(token, 135f, zoom, before);
                case "back": return Render(token, 0f, zoom, before);
                default: throw new ArgumentException("Choose front, three-quarter or back.");
            }
        }

        // Both views use the union bounds and the same camera, so before/after is a stable comparison.
        internal static byte[] Render(string token, float yaw = 180f, float zoom = 1f, bool before = false)
        {
            AssertMainThread();
            if (!TrySession(token, out var session)) throw new InvalidOperationException("This Try On has expired. Prepare it again.");
            if (float.IsNaN(yaw) || float.IsInfinity(yaw) || float.IsNaN(zoom) || float.IsInfinity(zoom))
                throw new ArgumentException("Preview rotation and zoom must be finite numbers.");
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                throw new InvalidOperationException("Try On photographs require a graphics-enabled Unity editor. This worker was started without a graphics device.");
            session.touched = EditorApplication.timeSinceStartup;
            var view = before ? session.before : session.after;
            var camera = view.preview.camera;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.12f, 0.13f, 0.16f, 1f);
            camera.fieldOfView = 30f;
            camera.aspect = 1f;
            camera.nearClipPlane = 0.01f;
            camera.farClipPlane = 1000f;
            camera.transform.rotation = Quaternion.Euler(8f, yaw % 360f, 0f);
            var distance = CameraDistance(camera, session.frame) * 1.1f / Mathf.Clamp(zoom, 0.5f, 2.5f);
            camera.transform.position = session.frame.center - camera.transform.forward * distance;
            view.preview.lights[0].intensity = 1.2f;
            view.preview.lights[0].transform.rotation = Quaternion.Euler(30f, 30f, 0f);
            view.preview.lights[1].intensity = 1f;
            var target = RenderTexture.GetTemporary(ImageSize, ImageSize, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            Texture2D image = null;
            var previewStarted = false;
            try
            {
                view.preview.BeginPreview(new Rect(0, 0, ImageSize, ImageSize), GUIStyle.none);
                previewStarted = true;
                camera.targetTexture = target;
                // PreviewRenderUtility applies preview-scene ambient lighting without altering the source scene.
                view.preview.ambientColor = new Color(0.55f, 0.55f, 0.55f);
                view.preview.Render(true, false);
                RenderTexture.active = target;
                image = new Texture2D(ImageSize, ImageSize, TextureFormat.RGBA32, false);
                image.ReadPixels(new Rect(0, 0, ImageSize, ImageSize), 0, 0);
                image.Apply();
                return image.EncodeToPNG();
            }
            finally
            {
                if (image != null) Object.DestroyImmediate(image);
                if (previewStarted) view.preview.EndPreview();
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(target);
            }
        }

        internal static void Cancel(string token)
        {
            AssertMainThread();
            if (string.IsNullOrEmpty(token) || !sessions.TryGetValue(token, out var session)) return;
            sessions.Remove(token);
            session.Dispose();
        }
        internal static void CancelAll()
        {
            foreach (var session in sessions.Values) session.Dispose();
            sessions.Clear();
        }
        private static void Expire()
        {
            foreach (var token in sessions.Where(pair => pair.Value.source == null ||
                EditorApplication.timeSinceStartup - pair.Value.touched > LifetimeSeconds).Select(pair => pair.Key).ToArray()) Cancel(token);
        }
        private static bool TrySession(string token, out Session session)
        {
            Expire();
            session = null;
            return !string.IsNullOrEmpty(token) && sessions.TryGetValue(token, out session);
        }
        private static void AssertMainThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != mainThread)
                throw new InvalidOperationException("Try On must run on Unity's main thread.");
        }

        internal static void CheckSupported(GameObject root, bool candidate)
        {
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null) throw new InvalidOperationException("'" + root.name + "' contains a missing script. Resolve it before Try On.");
                var name = component.GetType().FullName ?? "";
                if (name.StartsWith("VF.", StringComparison.Ordinal) || name.StartsWith("VRCFury", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("lilycalInventory", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Try On cannot process " + name + " on '" + component.gameObject.name +
                        "'. This creator engine requires a separate build pipeline; inspect it using the creator's preview or VRChat build workflow.");
            }
            if (candidate && root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Any(renderer => renderer.bones.Length > 0) &&
                root.GetComponentInChildren<ModularAvatarMergeArmature>(true) == null &&
                root.GetComponentInChildren<ModularAvatarOutfitRoot>(true) == null)
                throw new InvalidOperationException("'" + root.name + "' has a skinned armature without Modular Avatar outfit setup. " +
                    "Use a creator-prepared Modular Avatar prefab, or set up a separate working copy with Setup Outfit before trying it on.");
        }

        private static IEnumerable<string> AdditionalCreatorComponents(GameObject root)
        {
            foreach (var component in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (component == null) continue;
                var name = component.GetType().FullName ?? component.GetType().Name;
                if (!name.StartsWith("VRC.", StringComparison.Ordinal) && !name.StartsWith("nadena.dev.", StringComparison.Ordinal) &&
                    !name.StartsWith("OutfitToggleGenerator.", StringComparison.Ordinal) && !name.StartsWith("UnityEngine.", StringComparison.Ordinal))
                    yield return name;
            }
        }
        private static void CheckShaders(GameObject root)
        {
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                foreach (var material in renderer.sharedMaterials)
                    if (material != null && (material.shader == null || material.shader.name == "Hidden/InternalErrorShader" ||
                        ShaderUtil.ShaderHasError(material.shader)))
                        throw new InvalidOperationException("Try On found a missing or failed shader on material '" + material.name +
                            "'. Resolve the shader in Unity before requesting a photograph.");
        }

        private static View CreateView(GameObject source, GameObject candidate, int[] replacementPath, WardrobeAppearanceRecipe.Recipe recipe)
        {
            var view = new View();
            try
            {
                view.container = new GameObject("Wardrobe Try On (temporary)") { hideFlags = HideFlags.HideAndDontSave };
                view.container.SetActive(false);
                view.preview.AddSingleGO(view.container);
                view.root = Object.Instantiate(source, view.container.transform, false);
                view.root.name = source.name;
                view.root.transform.localPosition = Vector3.zero;
                view.root.transform.localRotation = Quaternion.identity;
                view.root.transform.localScale = source.transform.lossyScale;
                view.root.SetActive(true);
                if (candidate != null) WardrobeAppearanceRecipe.PlaceCandidate(view.root, ComposeCandidate(view.root, candidate, replacementPath), recipe, replacementPath != null);
                CloneMutableAssets(view);
                view.container.SetActive(true);
                return view;
            }
            catch { view.Dispose(); throw; }
        }

        // Relative sibling indices preserve identity even when siblings have identical names.
        internal static int[] SiblingPath(Transform root, Transform target)
        {
            var result = new List<int>();
            for (var current = target; current != root; current = current.parent)
            {
                if (current == null) throw new InvalidOperationException("The replacement is no longer under the pinned avatar.");
                result.Add(current.GetSiblingIndex());
            }
            result.Reverse();
            return result.ToArray();
        }
        internal static Transform AtSiblingPath(Transform root, int[] path)
        {
            foreach (var index in path)
            {
                if (index < 0 || index >= root.childCount) throw new InvalidOperationException("The worn instance moved. Try On again.");
                root = root.GetChild(index);
            }
            return root;
        }
        internal static GameObject ComposeCandidate(GameObject clone, GameObject candidate, int[] replacementPath)
        {
            var previous = replacementPath == null ? null : AtSiblingPath(clone.transform, replacementPath);
            if (previous == clone.transform) throw new InvalidOperationException("Try On cannot replace the avatar root.");
            var parent = previous == null ? clone.transform : previous.parent;
            var replacement = Object.Instantiate(candidate, parent, false);
            replacement.name = previous == null ? GameObjectUtility.GetUniqueNameForSibling(parent, candidate.name) : previous.name;
            if (previous != null)
            {
                replacement.transform.localPosition = previous.localPosition;
                replacement.transform.localRotation = previous.localRotation;
                replacement.transform.localScale = previous.localScale;
                replacement.transform.SetSiblingIndex(previous.GetSiblingIndex());
                replacement.SetActive(previous.gameObject.activeSelf);
                RemapReplacedRoot(clone, previous, replacement.transform);
                Object.DestroyImmediate(previous.gameObject);
            }
            return replacement;
        }
        private static void RemapReplacedRoot(GameObject clone, Transform previous, Transform replacement)
        {
            foreach (var component in clone.GetComponentsInChildren<Component>(true))
            {
                if (component == null || component is Transform || component.transform == previous || component.transform.IsChildOf(previous)) continue;
                using (var serialized = new SerializedObject(component))
                {
                    foreach (var property in serialized.ObjectProperties())
                    {
                        if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                        var reference = property.objectReferenceValue;
                        var transform = (reference as GameObject)?.transform ?? (reference as Component)?.transform;
                        if (transform == null || (transform != previous && !transform.IsChildOf(previous))) continue;
                        if (reference == previous.gameObject) property.objectReferenceValue = replacement.gameObject;
                        else if (reference == previous) property.objectReferenceValue = replacement;
                        else throw new InvalidOperationException("The avatar references a component or child inside the replaced item ('" +
                            component.gameObject.name + "." + property.propertyPath + "'). Try On cannot safely map it to this different prefab.");
                    }
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
            }
        }

        internal static Object CloneAssetShell(Object source)
        {
            // Instantiate triggers native strong-reference assertions for animator graph nodes.
            // Empty nodes plus CopySerialized preserve their data without Unity's remapping clone pass.
            if (source is UnityEditor.Animations.AnimatorController) return new UnityEditor.Animations.AnimatorController();
            if (source is UnityEditor.Animations.AnimatorStateMachine) return new UnityEditor.Animations.AnimatorStateMachine();
            if (source is UnityEditor.Animations.AnimatorState) return new UnityEditor.Animations.AnimatorState();
            if (source is UnityEditor.Animations.AnimatorStateTransition) return new UnityEditor.Animations.AnimatorStateTransition();
            if (source is UnityEditor.Animations.AnimatorTransition) return new UnityEditor.Animations.AnimatorTransition();
            if (source is UnityEditor.Animations.BlendTree) return new UnityEditor.Animations.BlendTree();
            if (source is AnimatorOverrideController) return new AnimatorOverrideController();
            if (source is ScriptableObject) return ScriptableObject.CreateInstance(source.GetType());
            return Object.Instantiate(source);
        }

        // Instantiate remaps hierarchy references; asset references remain shared. Copy mutable assets
        // recursively before processing, including in-memory material edits and controller sub-assets.
        private static void CloneMutableAssets(View view)
        {
            var copies = new Dictionary<Object, Object>();
            var visited = new HashSet<Object>();
            foreach (var component in view.root.GetComponentsInChildren<Component>(true)) Visit(component);
            void Visit(Object obj)
            {
                if (obj == null || obj is Transform || !visited.Add(obj)) return;
                using (var serialized = new SerializedObject(obj))
                {
                    foreach (var property in serialized.ObjectProperties())
                    {
                        if (property.propertyType != SerializedPropertyType.ObjectReference || property.propertyPath == "m_Script") continue;
                        var reference = property.objectReferenceValue;
                        if (reference == null) continue;
                        var transform = (reference as GameObject)?.transform ?? (reference as Component)?.transform;
                        if (transform != null)
                        {
                            if (transform != view.root.transform && !transform.IsChildOf(view.root.transform))
                                throw new InvalidOperationException("Try On cannot isolate an external object reference in '" + obj.name + "." + property.propertyPath + "'.");
                            continue;
                        }
                        // Immutable imported resources are read-only inputs; importer settings are never changed.
                        if (reference is Shader || reference is Texture || reference is MonoScript || reference is AudioClip || reference is ComputeShader) continue;
                        if (view.owned.Contains(reference)) continue;
                        if (!copies.TryGetValue(reference, out var copy))
                        {
                            copy = CloneAssetShell(reference);
                            EditorUtility.CopySerialized(reference, copy);
                            copy.name = reference.name;
                            copy.hideFlags = HideFlags.HideAndDontSave;
                            copies.Add(reference, copy);
                            view.owned.Add(copy);
                            Visit(copy);
                        }
                        property.objectReferenceValue = copy;
                    }
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
            }
        }

        private static void Process(View view)
        {
            var initial = new HashSet<Object>(view.root.ReferencedAssets(traverseSaved: true, includeScene: false));
            var messages = new List<string>();
            Application.LogCallback log = (message, trace, type) =>
            {
                if (messages.Count < 12 && (type == LogType.Error || type == LogType.Exception ||
                    message.StartsWith("[NDMF] Error Reported:", StringComparison.Ordinal))) messages.Add(message);
            };
            Application.logMessageReceived += log;
            try
            {
                var platform = PlatformRegistry.GetPrimaryPlatformForAvatar(view.root);
                if (platform == null || platform.AvatarRootComponentType != typeof(VRCAvatarDescriptor))
                    throw new InvalidOperationException("The installed NDMF version did not provide the VRChat avatar processor.");
                using (new OverrideTemporaryDirectoryScope(null))
                {
                    var context = AvatarProcessor.ProcessAvatar(view.root, platform);
                    if (!context.Successful)
                        throw new InvalidOperationException("NDMF could not complete this Try On. " + string.Join("\n", messages));
                }
                view.appearance?.Apply();
                view.bounds = VisibleBounds(view.root);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException("Processed Try On failed: " + exception.Message, exception);
            }
            finally
            {
                Application.logMessageReceived -= log;
                // Own the generated graph, including controllers and scriptable sub-assets. Do not sweep
                // the whole editor: NDMF may initialize shared caches or an error-report window.
                if (view.root != null)
                    foreach (var obj in view.root.ReferencedAssets(traverseSaved: false, includeScene: false))
                        if (!initial.Contains(obj) && obj != null && !EditorUtility.IsPersistent(obj)) view.owned.Add(obj);
            }
        }
        internal static string SourceFingerprint(VRCAvatarDescriptor avatar)
        {
            if (avatar == null) return "";
            var text = new StringBuilder();
            text.Append(avatar.GetInstanceID()).Append('|').Append(avatar.gameObject.scene.handle).Append('|');
            var assets = new HashSet<Object>();
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var transform in avatar.GetComponentsInChildren<Transform>(true))
            {
                Append(transform.gameObject);
                foreach (var component in transform.GetComponents<Component>())
                {
                    if (component == null) { text.Append("missing;"); continue; }
                    Append(component);
                    References(component);
                }
            }
            for (var parent = avatar.transform.parent; parent != null; parent = parent.parent) Append(parent);
            foreach (var path in paths.OrderBy(path => path, StringComparer.Ordinal)) text.Append(path).Append('=').Append(AssetVersion(path));
            return Hash128.Compute(text.ToString()).ToString();
            void Append(Object obj)
            {
                text.Append(obj.GetInstanceID()).Append(':').Append(EditorUtility.GetDirtyCount(obj)).Append(':')
                    .Append(EditorJsonUtility.ToJson(obj)).Append(';');
            }
            void References(Object obj)
            {
                using (var serialized = new SerializedObject(obj))
                {
                    foreach (var property in serialized.ObjectProperties())
                    {
                        if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                        var reference = property.objectReferenceValue;
                        if (reference == null || reference is Component || reference is GameObject || !assets.Add(reference)) continue;
                        text.Append(reference.GetInstanceID()).Append(':').Append(EditorUtility.GetDirtyCount(reference));
                        var path = AssetDatabase.GetAssetPath(reference);
                        if (!string.IsNullOrEmpty(path)) paths.Add(path);
                        if (reference is Shader || reference is Texture || reference is MonoScript || reference is AudioClip || reference is ComputeShader) continue;
                        if (!(reference is Mesh) && !(reference is AnimationClip) && !(reference is Avatar)) Append(reference);
                        References(reference);
                    }
                }
            }
        }
        private static string EnvironmentRevision()
        {
            var packages = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
                .OrderBy(package => package.name, StringComparer.Ordinal)
                .Select(package => package.name + "@" + package.version);
            var pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            var pipelinePath = pipeline == null ? "" : AssetDatabase.GetAssetPath(pipeline);
            return Hash128.Compute(Application.unityVersion + "|" + string.Join(";", packages) + "|" +
                EditorUserBuildSettings.activeBuildTarget + "|" + QualitySettings.activeColorSpace + "|" +
                QualitySettings.GetQualityLevel() + "|" + pipelinePath + "|" + AssetVersion(pipelinePath) + "|" +
                SystemInfo.graphicsDeviceType + "|" + SystemInfo.graphicsDeviceName).ToString();
        }
        private static string AssetVersion(string path) => string.IsNullOrEmpty(path) ? "" : AssetDatabase.GetAssetDependencyHash(path).ToString();

        private static Metrics ReadMetrics(GameObject root)
        {
            var result = new Metrics();
            var materials = new HashSet<Material>();
            var textures = new HashSet<Texture>();
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                result.renderers++;
                if (renderer.enabled && renderer.gameObject.activeInHierarchy) result.visibleRenderers++;
                var mesh = (renderer as SkinnedMeshRenderer)?.sharedMesh ?? renderer.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh != null)
                    for (var submesh = 0; submesh < mesh.subMeshCount; submesh++)
                        if (mesh.GetTopology(submesh) == MeshTopology.Triangles) result.triangles += mesh.GetIndexCount(submesh) / 3;
                foreach (var material in renderer.sharedMaterials)
                    if (material != null && materials.Add(material))
                        foreach (var property in material.GetTexturePropertyNames())
                        { var texture = material.GetTexture(property); if (texture != null) textures.Add(texture); }
            }
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                var type = component == null ? "" : component.GetType().Name;
                if (type == "VRCPhysBone") result.physBones++;
                if (type == "VRCContactReceiver" || type == "VRCContactSender") result.contacts++;
            }
            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            result.materials = materials.Count;
            result.textures = textures.Count;
            var contributions = textures.Select(texture => new TextureContribution
            {
                name = texture.name, width = texture.width, height = texture.height,
                estimatedBytes = Math.Max(0, UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(texture))
            }).OrderByDescending(texture => texture.estimatedBytes).ThenBy(texture => texture.name, StringComparer.Ordinal).ToArray();
            result.estimatedTextureBytes = contributions.Sum(texture => texture.estimatedBytes);
            result.largestTextures = contributions.Take(8).ToArray();
            result.expressionParameters = descriptor?.expressionParameters?.parameters?.Length ?? 0;
            result.expressionMenuControls = MenuControls(descriptor?.expressionsMenu).Count();
            return result;
        }
        private static IEnumerable<string> MenuControls(VRCExpressionsMenu root)
        {
            var visited = new HashSet<VRCExpressionsMenu>();
            var queue = new Queue<KeyValuePair<VRCExpressionsMenu, string>>();
            if (root != null) queue.Enqueue(new KeyValuePair<VRCExpressionsMenu, string>(root, ""));
            while (queue.Count > 0)
            {
                var item = queue.Dequeue();
                if (!visited.Add(item.Key) || item.Key.controls == null) continue;
                foreach (var control in item.Key.controls)
                {
                    if (control == null) continue;
                    var name = item.Value + control.name;
                    yield return name + " (" + control.type + ")";
                    if (control.subMenu != null) queue.Enqueue(new KeyValuePair<VRCExpressionsMenu, string>(control.subMenu, name + "/"));
                }
            }
        }
        private static Bounds VisibleBounds(GameObject root)
        {
            var bounds = new Bounds();
            var found = false;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(false))
            {
                if (!renderer.enabled) continue;
                var value = renderer.bounds;
                if (!Finite(value.center) || !Finite(value.size)) continue;
                if (!found) { bounds = value; found = true; } else bounds.Encapsulate(value);
            }
            if (!found || bounds.size.sqrMagnitude < 0.000001f)
                throw new InvalidOperationException("The processed avatar has no visible geometry in its default state.");
            return bounds;
        }
        private static bool Finite(Vector3 value) => !(float.IsNaN(value.x) || float.IsInfinity(value.x) ||
            float.IsNaN(value.y) || float.IsInfinity(value.y) || float.IsNaN(value.z) || float.IsInfinity(value.z));
        private static float CameraDistance(Camera camera, Bounds bounds)
        {
            var inverse = Quaternion.Inverse(camera.transform.rotation);
            var max = Vector3.zero;
            for (var x = -1; x <= 1; x += 2)
            for (var y = -1; y <= 1; y += 2)
            for (var z = -1; z <= 1; z += 2)
            {
                var point = inverse * Vector3.Scale(bounds.extents, new Vector3(x, y, z));
                max = Vector3.Max(max, new Vector3(Mathf.Abs(point.x), Mathf.Abs(point.y), Mathf.Abs(point.z)));
            }
            return Mathf.Max(max.x, max.y) / Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * 0.5f) + max.z;
        }
    }
}
