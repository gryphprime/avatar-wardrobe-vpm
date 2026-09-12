using System.Threading.Tasks;
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
            public string sourceRevision, visualRevision, recipeRevision, environmentRevision, renderSpecification;
            public int avatarId, replaceInstanceId;
            public bool processed;
            public long elapsedMilliseconds;
            public string[] limitations, menuControls, parameters, parameterProblems;
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
                session.result.parameterProblems = ParameterDiagnostics(descriptor);
                if (session.result.sourceFingerprint != SourceFingerprint(avatar) || session.assetVersion != AssetVersion(path))
                    throw new InvalidOperationException("The avatar or candidate changed during preparation. Try On again before wearing it.");
                session.result.sourceRevision = session.result.sourceFingerprint;
                session.result.visualRevision = VisualFingerprint(avatar);
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

        internal static Task<byte[]> RenderViewAsync(string token, string view, float zoom = 1f, bool before = false)
        {
            var yaw = view == "front" ? 180f : view == "three-quarter" ? 135f : view == "back" ? 0f : throw new ArgumentException("Choose front, three-quarter or back.");
            var pixels = RenderPixels(token, yaw, zoom, before);
            return Task.Run(() => EncodePixels(pixels));
        }
        private static byte[] EncodePixels(Color32[] pixels) => ImageConversion.EncodeArrayToPNG(pixels,
            UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm, ImageSize, ImageSize);

        // Both views use the union bounds and the same camera, so before/after is a stable comparison.
        internal static byte[] Render(string token, float yaw = 180f, float zoom = 1f, bool before = false)
            => EncodePixels(RenderPixels(token, yaw, zoom, before));
        private static Color32[] RenderPixels(string token, float yaw, float zoom, bool before)
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
                return image.GetPixels32();
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
        internal static readonly string[] SettingsFingerprintFiles = { "AvatarWardrobePresets.json", "AvatarWardrobeOverrides.json", ShiroTools.OutfitProjectData.FILE_NAME };
        internal static long FingerprintTraversals, FingerprintEditorMilliseconds;
        internal static string SourceFingerprint(VRCAvatarDescriptor avatar) => Fingerprint(avatar, false);
        internal static string VisualFingerprint(VRCAvatarDescriptor avatar) => Fingerprint(avatar, true);
        private static string Fingerprint(VRCAvatarDescriptor avatar, bool visual)
        {
            if (avatar == null) return "";
            Func<string> finish = null;
            foreach (var step in FingerprintSteps(avatar, visual, value => finish = value)) { }
            return finish();
        }
        internal static async Task<string> FingerprintAsync(VRCAvatarDescriptor avatar, bool visual)
        {
            if (avatar == null) return "";
            Func<string> finish = null;
            var budget = System.Diagnostics.Stopwatch.StartNew();
            var generation = AvatarWardrobeServer.FingerprintGeneration;
            foreach (var step in FingerprintSteps(avatar, visual, value => finish = value))
            {
                if (budget.ElapsedMilliseconds < 2) continue;
                FingerprintEditorMilliseconds += budget.ElapsedMilliseconds;
                await Task.Yield(); // Resume Unity object access on its captured synchronization context.
                budget.Restart();
                if (avatar == null || generation != AvatarWardrobeServer.FingerprintGeneration) throw new InvalidOperationException("Avatar changed during inspection.");
            }
            FingerprintEditorMilliseconds += budget.ElapsedMilliseconds;
            return await Task.Run(finish);
        }
        internal static async Task<string[]> FingerprintPairAsync(VRCAvatarDescriptor avatar)
        {
            if (avatar == null) return new[] { "", "" };
            Func<string[]> finish = null;
            var generation = AvatarWardrobeServer.FingerprintGeneration;
            var budget = System.Diagnostics.Stopwatch.StartNew();
            foreach (var step in FingerprintSteps(avatar, false, null, value => finish = value))
            {
                if (budget.ElapsedMilliseconds < 2) continue;
                FingerprintEditorMilliseconds += budget.ElapsedMilliseconds;
                await Task.Yield(); budget.Restart();
                if (avatar == null || generation != AvatarWardrobeServer.FingerprintGeneration)
                    throw new InvalidOperationException("Avatar changed during inspection.");
            }
            FingerprintEditorMilliseconds += budget.ElapsedMilliseconds;
            return await Task.Run(finish);
        }
        private static IEnumerable<bool> FingerprintSteps(VRCAvatarDescriptor avatar, bool visual, Action<Func<string>> completed, Action<Func<string[]>> paired = null)
        {
            FingerprintTraversals++;
            var text = new StringBuilder();
            text.Append(avatar.GetInstanceID()).Append('|').Append(avatar.gameObject.scene.handle).Append('|');
            var visualText = paired == null ? null : new StringBuilder(text.ToString());
            var assets = new HashSet<Object>();
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var transform in avatar.GetComponentsInChildren<Transform>(true))
            {
                Append(transform.gameObject);
                foreach (var component in transform.GetComponents<Component>())
                {
                    if (component == null) { text.Append("missing;"); visualText?.Append("missing;"); continue; }
                    Append(component);
                    foreach (var step in References(component)) yield return step;
                    yield return true;
                }
            }
            for (var parent = avatar.transform.parent; parent != null; parent = parent.parent) Append(parent);
            foreach (var path in paths.OrderBy(path => path, StringComparer.Ordinal))
            {
                var version = AssetVersion(path);
                text.Append(path).Append('=').Append(version);
                visualText?.Append(path).Append('=').Append(version);
                yield return true;
            }
            var project = visual || paired != null ? System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "..")) : "";
            var capturedText = text.ToString();
            var capturedVisual = visualText?.ToString();
            string Finish(string value, bool includeSettings)
            {
                var captured = new StringBuilder(value);
                if (includeSettings)
                    foreach (var name in SettingsFingerprintFiles)
                    {
                        var settings = System.IO.Path.Combine(project, "ProjectSettings", name);
                        if (System.IO.File.Exists(settings)) captured.Append(name).Append('=').Append(Hash128.Compute(System.IO.File.ReadAllText(settings)));
                    }
                return Hash128.Compute(captured.ToString()).ToString();
            }
            completed?.Invoke(() => Finish(capturedText, visual));
            paired?.Invoke(() => new[] { Finish(capturedText, false), Finish(capturedVisual, true) });
            void Append(Object obj)
            {
                var json = EditorJsonUtility.ToJson(obj);
                var id = obj.GetInstanceID(); var dirty = EditorUtility.GetDirtyCount(obj);
                AppendTo(text, visual);
                if (visualText != null) AppendTo(visualText, true);
                void AppendTo(StringBuilder builder, bool isVisual)
                {
                    var presentation = isVisual && obj is WardrobeMenuLayout;
                    var value = presentation ? System.Text.RegularExpressions.Regex.Replace(json,
                        "\"label\"\\s*:\\s*\"(?:\\\\.|[^\"\\\\])*\"", "\"label\":\"\"") : json;
                    builder.Append(id).Append(':').Append(presentation ? 0 : dirty).Append(':').Append(value).Append(';');
                }
            }
            IEnumerable<bool> References(Object obj)
            {
                // Mesh/avatar numeric payloads have no referenced Unity assets. Walking their
                // serialized vertex/bone arrays here stalls large avatars on every status poll.
                // Identity, native dirty count and imported dependency hash are recorded by Visit.
                if (obj is Mesh || obj is Avatar) yield break;
                if (obj is AnimationClip clip)
                {
                    foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                        foreach (var keyframe in AnimationUtility.GetObjectReferenceCurve(clip, binding))
                            foreach (var step in Visit(keyframe.value)) yield return step;
                    yield break;
                }
                using (var serialized = new SerializedObject(obj))
                {
                    foreach (var property in serialized.ObjectProperties())
                    {
                        if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                        foreach (var step in Visit(property.objectReferenceValue)) yield return step;
                        yield return true;
                    }
                }
            }
            IEnumerable<bool> Visit(Object reference)
            {
                if (reference == null || reference is Component || reference is GameObject || !assets.Add(reference)) yield break;
                var referenceId = reference.GetInstanceID(); var referenceDirty = EditorUtility.GetDirtyCount(reference);
                text.Append(referenceId).Append(':').Append(referenceDirty);
                visualText?.Append(referenceId).Append(':').Append(referenceDirty);
                var path = AssetDatabase.GetAssetPath(reference);
                if (!string.IsNullOrEmpty(path)) paths.Add(path);
                if (reference is Shader || reference is Texture || reference is MonoScript || reference is AudioClip || reference is ComputeShader) yield break;
                if (!(reference is Mesh) && !(reference is AnimationClip) && !(reference is Avatar)) Append(reference);
                yield return true;
                foreach (var step in References(reference)) yield return step;
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
        internal static string[] ParameterDiagnostics(VRCAvatarDescriptor descriptor)
        {
            var findings = new List<string>();
            if (descriptor == null) return new[] { "No avatar descriptor was produced." };
            var asset = descriptor.expressionParameters;
            var declared = (asset?.parameters ?? new VRCExpressionParameters.Parameter[0])
                .Where(value => value != null && !string.IsNullOrEmpty(value.name)).ToArray();
            var names = declared.GroupBy(value => value.name).ToDictionary(group => group.Key, group => group.First().valueType);
            if (asset != null)
            {
                var cost = asset.CalcTotalCost();
                findings.Add("Synced parameter budget: " + cost + "/" + VRCExpressionParameters.MAX_PARAMETER_COST + " bits" +
                    (cost > VRCExpressionParameters.MAX_PARAMETER_COST ? " — over budget." : "."));
                foreach (var group in declared.GroupBy(value => value.name).Where(group => group.Count() > 1))
                    findings.Add("Duplicate parameter '" + group.Key + "'" + (group.Select(value => value.valueType).Distinct().Count() > 1 ? " has conflicting types." : "."));
            }
            else if (descriptor.expressionsMenu != null) findings.Add("The built menu has no expression parameter asset.");
            var visited = new HashSet<VRCExpressionsMenu>();
            var menus = new Queue<VRCExpressionsMenu>();
            if (descriptor.expressionsMenu != null) menus.Enqueue(descriptor.expressionsMenu);
            while (menus.Count > 0)
            {
                var menu = menus.Dequeue();
                if (!visited.Add(menu) || menu.controls == null) continue;
                if (menu.controls.Count > VRCExpressionsMenu.MAX_CONTROLS)
                    findings.Add("Menu '" + menu.name + "' exceeds " + VRCExpressionsMenu.MAX_CONTROLS + " controls.");
                foreach (var control in menu.controls.Where(value => value != null))
                {
                    Check(control.parameter?.name, control.name, false);
                    if (control.subParameters != null)
                        foreach (var parameter in control.subParameters) Check(parameter?.name, control.name, true);
                    if (control.subMenu != null) menus.Enqueue(control.subMenu);
                }
            }
            // Compare declared types with the actual generated animator parameter definitions.
            foreach (var layer in (descriptor.baseAnimationLayers ?? new VRCAvatarDescriptor.CustomAnimLayer[0])
                .Concat(descriptor.specialAnimationLayers ?? new VRCAvatarDescriptor.CustomAnimLayer[0]))
            {
                RuntimeAnimatorController runtime = layer.animatorController;
                var seen = new HashSet<RuntimeAnimatorController>();
                while (runtime is AnimatorOverrideController overrides && seen.Add(runtime)) runtime = overrides.runtimeAnimatorController;
                if (!(runtime is UnityEditor.Animations.AnimatorController controller)) continue;
                foreach (var parameter in controller.parameters)
                    if (names.TryGetValue(parameter.name, out var kind) && !string.Equals(kind.ToString(), parameter.type.ToString(), StringComparison.Ordinal))
                        findings.Add("Animator parameter '" + parameter.name + "' is " + parameter.type + " but the expression parameter is " + kind + ".");
            }
            findings.Add("These checks cover the processed menu and declared parameters. Use Test in Appearance for switching behavior and Build Check for full SDK validation.");
            return findings.Distinct().Take(100).ToArray();
            void Check(string name, string control, bool puppet)
            {
                if (string.IsNullOrEmpty(name)) return;
                if (!names.TryGetValue(name, out var kind)) findings.Add("Control '" + control + "' references missing parameter '" + name + "'.");
                else if (puppet && kind != VRCExpressionParameters.ValueType.Float)
                    findings.Add("Puppet control '" + control + "' requires Float parameter '" + name + "', currently " + kind + ".");
            }
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
