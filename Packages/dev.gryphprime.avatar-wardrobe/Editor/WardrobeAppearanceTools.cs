using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;

namespace OutfitToggleGenerator
{
    // Optional adapters use public component types or documented native menus.
    // They never configure private fields or enter Play Mode automatically.
    internal static class WardrobeAppearanceTools
    {
        private sealed class OptimizerPending { internal int avatarId; internal string revision, previewToken; internal DateTime expires; }
        private static readonly Dictionary<string, OptimizerPending> OptimizerReviews = new Dictionary<string, OptimizerPending>();
        [Serializable] internal sealed class OptimizerReviewDto
        {
            public int ok; public string token, message, beforeImage, afterImage;
            public WardrobeTryOnWorker.Prepared preview;
        }
        private const string Optimizer = "Anatawa12.AvatarOptimizer.TraceAndOptimize";
        private const string Gesture = "BlackStartX.GestureManager.GestureManager";
        private const string MochiMenu = "Tools/MochiFitter";
        private static Type MochiType() => AppDomain.CurrentDomain.GetAssemblies().Where(assembly => assembly.GetName().Name == "OutfitRetargetingSystem")
            .Select(assembly => assembly.GetType("OutfitRetargetingSystem", false)).FirstOrDefault(type => type != null);
        internal static bool MochiMenuContract(Type type)
        {
            if (type == null || !type.IsPublic || !typeof(EditorWindow).IsAssignableFrom(type)) return false;
            var method = type.GetMethod("ShowWindow", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            return method != null && method.ReturnType == typeof(void) && method.GetCustomAttributes(typeof(MenuItem), false).Cast<MenuItem>()
                .Any(menu => menu.menuItem == MochiMenu && !menu.validate);
        }
        [Serializable] internal sealed class ToolDto
        {
            public string id, name, version, message, url, actionLabel;
            public bool installed, available, requiresReview;
        }
        [Serializable] internal sealed class SnapshotDto { public int ok; public string message; public List<ToolDto> tools = new List<ToolDto>(); }
        [Serializable] internal sealed class CommandDto { public int avatarId; public string revision, id; }
        private static Type PublicType(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(name, false)).FirstOrDefault(type => type != null && type.IsPublic && typeof(Component).IsAssignableFrom(type));
        private static string Version(Type type) => type == null ? "" : UnityEditor.PackageManager.PackageInfo.FindForAssembly(type.Assembly)?.version ?? "";
        internal static SnapshotDto Snapshot(VRCAvatarDescriptor avatar)
        {
            var result = new SnapshotDto { ok = 1 };
            var optimizer = PublicType(Optimizer); var optimizerVersion = Version(optimizer);
            var existingOptimizer = avatar != null && optimizer != null && avatar.GetComponentsInChildren(optimizer, true).Length > 0;
            result.tools.Add(new ToolDto { id = "optimizer", name = "Avatar Optimizer", version = optimizerVersion, installed = optimizer != null,
                available = optimizerVersion == "1.9.18", requiresReview = !existingOptimizer, actionLabel = existingOptimizer ? "Open optimizer in Unity" : "Preview default optimizer",
                message = optimizer == null ? "Not installed. Optional optimization uses a separately installed Avatar Optimizer." : optimizerVersion != "1.9.18" ? "This installed version has not been validated by the adapter. Open its Inspector directly in Unity." : existingOptimizer ? "Keep the existing configuration. Use its native Inspector to change options." : "Add AAO Trace And Optimize with its public default configuration. Build measurements are needed to assess the result; no performance improvement is assumed.",
                url = "https://vpm.anatawa12.com/avatar-optimizer/en/" });
            var gesture = PublicType(Gesture); var gestureVersion = Version(gesture); var gestureContract = GestureContract(gesture);
            result.tools.Add(new ToolDto { id = "gesture", name = "Gesture Manager", version = gestureVersion, installed = gesture != null, available = gestureVersion == "3.9.9" && gestureContract,
                actionLabel = "Open Gesture Manager in Unity", message = gesture == null ? "Not installed. Gesture testing requires a separately installed emulator." : gestureVersion != "3.9.9" ? "This installed version has not been validated by the adapter. Use its native Tools menu." : !gestureContract ? "Gesture Manager is not compiled with its VRChat avatar target contract. Check the SDK setup in Unity and use its native Tools menu." : "Create or select its emulator in this scene and set the pinned avatar as its public Favourite Avatar. Enter Play Mode yourself. Emulator checks do not prove live-game behavior.",
                url = "https://github.com/BlackStartx/VRC-Gesture-Manager" });
            result.tools.Add(new ToolDto { id = "textrans", name = "TexTransTool", message = "No validated installed adapter was found. Texture layers, decals, and region mapping require its native workflow.", url = "https://ttt.rs64.net/en/docs/Tutorial" });
            var mochi = MochiType(); var mochiAvailable = MochiMenuContract(mochi);
            result.tools.Add(new ToolDto { id = "fitting", name = "MochiFitter", installed = mochi != null, available = mochiAvailable, actionLabel = "Open MochiFitter in Unity",
                message = mochi == null ? "No loaded MochiFitter installation was found. Fitting requires its separately installed native tool and conversion profiles." : !mochiAvailable ? "MochiFitter is installed, but its public native-menu contract has not been validated. Use its Tools menu directly in Unity." : "MochiFitter is installed; its release version is unavailable. Open its native window, then choose the source outfit, target avatar and required conversion profiles there. Wardrobe does not configure or run fitting.",
                url = "https://booth.pm/ja/items/7657840" });
            result.tools.Add(new ToolDto { id = "android", name = "VRCQuestTools", message = "No validated installed adapter was found. Android conversion needs a separate appearance and build review.", url = "https://kurotu.github.io/VRCQuestTools/" });
            return result;
        }
        internal static OptimizerReviewDto ReviewOptimizer(CommandDto command)
        {
            string previewToken = null;
            try
            {
                var avatar = AvatarWardrobeServer.SceneAvatar; var snapshot = WardrobeAppearanceEditor.Snapshot(avatar);
                if (snapshot.ok != 1) throw new InvalidOperationException(snapshot.message);
                if (command == null || command.avatarId != snapshot.avatarId || command.revision != snapshot.revision)
                    throw new InvalidOperationException("The pinned avatar changed. Refresh and review again.");
                var type = PublicType(Optimizer);
                if (Version(type) != "1.9.18") throw new InvalidOperationException("Default optimization preview requires installed Avatar Optimizer 1.9.18.");
                if (avatar.GetComponentsInChildren(type, true).Length > 0) throw new InvalidOperationException("This avatar already has an optimizer. Keep its configuration and use its native Inspector.");
                var preview = WardrobeTryOnWorker.Prepare(avatar, "", configureAfterClone: clone => clone.AddComponent(type), configurationId: "aao-public-default-1.9.18");
                previewToken = preview.token;
                var before = WardrobeTryOnWorker.RenderView(preview.token, "front", before: true);
                var after = WardrobeTryOnWorker.RenderView(preview.token, "front", before: false);
                if (before.Length + after.Length > 8 * 1024 * 1024) throw new InvalidOperationException("The optimization preview exceeds its image limit.");
                if (WardrobeAppearanceEditor.Snapshot(avatar).revision != snapshot.revision) throw new InvalidOperationException("Appearance changed while preparing optimization. Review again.");
                foreach (var key in OptimizerReviews.Where(x => x.Value.expires < DateTime.UtcNow).Select(x => x.Key).ToArray()) OptimizerReviews.Remove(key);
                while (OptimizerReviews.Count >= 4) OptimizerReviews.Remove(OptimizerReviews.OrderBy(x => x.Value.expires).First().Key);
                var token = Guid.NewGuid().ToString("N");
                OptimizerReviews[token] = new OptimizerPending { avatarId = avatar.GetInstanceID(), revision = snapshot.revision, previewToken = preview.token, expires = DateTime.UtcNow.AddMinutes(2) };
                return new OptimizerReviewDto { ok = 1, token = token, preview = preview, beforeImage = Convert.ToBase64String(before), afterImage = Convert.ToBase64String(after),
                    message = "Before and after process separate copies with NDMF. After adds AAO's public default component. Compare appearance and measured counts; the working avatar has not changed. SDK upload callbacks and runtime animation are outside this preview." };
            }
            catch (Exception error) { if (previewToken != null) WardrobeTryOnWorker.Cancel(previewToken); return new OptimizerReviewDto { message = error.Message }; }
        }
        internal static AvatarWardrobeServer.ResultDto ApplyOptimizer(WardrobeAppearanceEditor.ApplyDto request)
        {
            try
            {
                if (request == null || string.IsNullOrEmpty(request.token) || !OptimizerReviews.TryGetValue(request.token, out var pending)) throw new InvalidOperationException("Review the default optimizer before applying it.");
                OptimizerReviews.Remove(request.token);
                var avatar = AvatarWardrobeServer.SceneAvatar; var snapshot = WardrobeAppearanceEditor.Snapshot(avatar);
                if (snapshot.ok != 1 || request.avatarId != snapshot.avatarId || pending.avatarId != snapshot.avatarId || pending.revision != snapshot.revision || pending.expires < DateTime.UtcNow)
                    throw new InvalidOperationException("The optimization review expired or its avatar changed. Review again.");
                if (!WardrobeTryOnWorker.Validate(pending.previewToken, avatar, out var reason)) throw new InvalidOperationException(reason);
                var type = PublicType(Optimizer);
                if (Version(type) != "1.9.18" || avatar.GetComponentsInChildren(type, true).Length > 0) throw new InvalidOperationException("The optimizer environment changed. Review again.");
                return AvatarWardrobeServer.EditAvatar("Apply reviewed default optimizer", () => {
                    Undo.AddComponent(avatar.gameObject, type); EditorSceneManager.MarkSceneDirty(avatar.gameObject.scene);
                    return new AvatarWardrobeServer.ResultDto { ok = 1, message = "Reviewed default optimizer added. Save the scene to keep it; Undo is available in Unity." };
                }, migratePresets: false);
            }
            catch (Exception error) { return new AvatarWardrobeServer.ResultDto { message = error.Message }; }
        }
        internal static AvatarWardrobeServer.ResultDto Execute(CommandDto command)
        {
            try
            {
                var avatar = AvatarWardrobeServer.SceneAvatar; var snapshot = WardrobeAppearanceEditor.Snapshot(avatar);
                if (snapshot.ok != 1) throw new InvalidOperationException(snapshot.message);
                if (command == null || command.avatarId != snapshot.avatarId || command.revision != snapshot.revision)
                    throw new InvalidOperationException("The pinned avatar or appearance changed. Refresh before opening a tool.");
                var tool = Snapshot(avatar).tools.SingleOrDefault(x => x.id == command.id && x.available);
                if (tool == null) throw new InvalidOperationException("This tool version has no validated adapter. Use its native Unity workflow.");
                if (command.id == "fitting")
                {
                    if (!MochiMenuContract(MochiType()) || !EditorApplication.ExecuteMenuItem(MochiMenu))
                        throw new InvalidOperationException("MochiFitter's native menu is unavailable. Open Tools > MochiFitter directly in Unity.");
                    return new AvatarWardrobeServer.ResultDto { ok = 1, message = "MochiFitter opened. Choose the source outfit and " + avatar.name + " as the target in its native window, using the required profiles. No conversion was requested." };
                }
                return AvatarWardrobeServer.EditAvatar("Open appearance tool", () => {
                    if (AvatarWardrobeServer.SceneAvatar != avatar) return new AvatarWardrobeServer.ResultDto { message = "The pinned avatar changed." };
                    if (command.id == "optimizer")
                    {
                        var type = PublicType(Optimizer); var existing = avatar.GetComponentsInChildren(type, true);
                        var component = existing.FirstOrDefault();
                        if (component == null) return new AvatarWardrobeServer.ResultDto { message = "Preview the default optimizer and apply its reviewed result first." };
                        Selection.activeObject = component; EditorGUIUtility.PingObject(component); EditorApplication.ExecuteMenuItem("Window/General/Inspector");
                        EditorSceneManager.MarkSceneDirty(avatar.gameObject.scene);
                        return new AvatarWardrobeServer.ResultDto { ok = 1, message = existing.Length > 0 ? "Existing optimizer selected in Unity. Its configuration is unchanged." : "Default AAO optimizer added. Undo is available; build and compare measurements before accepting its result." };
                    }
                    var gestureType = PublicType(Gesture);
                    var managers = avatar.gameObject.scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren(gestureType, true)).ToArray();
                    var manager = managers.FirstOrDefault(candidate => GestureFavourite(candidate) == avatar) ?? managers.FirstOrDefault(candidate => GestureFavourite(candidate) == null);
                    if (manager == null)
                    {
                        var previous = SceneManager.GetActiveScene(); var before = new HashSet<int>(avatar.gameObject.scene.GetRootGameObjects().Select(x => x.GetInstanceID()));
                        try
                        {
                            SceneManager.SetActiveScene(avatar.gameObject.scene);
                            if (!EditorApplication.ExecuteMenuItem("Tools/Gesture Manager Emulator")) throw new InvalidOperationException("Gesture Manager's native menu is unavailable.");
                        }
                        finally
                        {
                            foreach (var created in avatar.gameObject.scene.GetRootGameObjects().Where(x => !before.Contains(x.GetInstanceID()))) Undo.RegisterCreatedObjectUndo(created, "Create gesture emulator");
                            if (previous.IsValid()) SceneManager.SetActiveScene(previous);
                        }
                        manager = avatar.gameObject.scene.GetRootGameObjects().Where(root => !before.Contains(root.GetInstanceID())).SelectMany(root => root.GetComponentsInChildren(gestureType, true)).FirstOrDefault();
                        if (manager == null) throw new InvalidOperationException("The emulator was not created in the pinned scene.");
                    }
                    PinGestureFavourite(manager, avatar);
                    Selection.activeObject = manager; EditorGUIUtility.PingObject(manager); EditorApplication.ExecuteMenuItem("Window/General/Inspector");
                    EditorSceneManager.MarkSceneDirty(avatar.gameObject.scene);
                    return new AvatarWardrobeServer.ResultDto { ok = 1, message = "Gesture Manager targets " + avatar.name + " as its Favourite Avatar. Enter Play Mode when ready. No tests have run yet." };
                }, migratePresets: false);
            }
            catch (Exception error) { return new AvatarWardrobeServer.ResultDto { message = error.Message }; }
        }
        private static bool GestureContract(Type type)
        {
            var settings = type?.GetField("settings", BindingFlags.Public | BindingFlags.Instance);
            var favourite = settings?.FieldType.GetField("favourite", BindingFlags.Public | BindingFlags.Instance);
            return favourite != null && favourite.FieldType == typeof(VRC.SDKBase.VRC_AvatarDescriptor);
        }
        private static VRC.SDKBase.VRC_AvatarDescriptor GestureFavourite(Component manager)
        {
            var settingsField = manager.GetType().GetField("settings", BindingFlags.Public | BindingFlags.Instance);
            var favouriteField = settingsField?.FieldType.GetField("favourite", BindingFlags.Public | BindingFlags.Instance);
            if (settingsField == null || favouriteField == null || !typeof(VRC.SDKBase.VRC_AvatarDescriptor).IsAssignableFrom(favouriteField.FieldType))
                throw new InvalidOperationException("Gesture Manager's public target contract changed. Use its native Inspector.");
            var settings = settingsField.GetValue(manager);
            return settings == null ? null : favouriteField.GetValue(settings) as VRC.SDKBase.VRC_AvatarDescriptor;
        }
        internal static void PinGestureFavourite(Component manager, VRCAvatarDescriptor avatar)
        {
            if (manager == null || manager.GetType() != PublicType(Gesture) || Version(manager.GetType()) != "3.9.9")
                throw new InvalidOperationException("Exact target pinning requires Gesture Manager 3.9.9.");
            GestureFavourite(manager); // Validate only the documented public settings/favourite fields.
            var settingsField = manager.GetType().GetField("settings", BindingFlags.Public | BindingFlags.Instance);
            var favouriteField = settingsField.FieldType.GetField("favourite", BindingFlags.Public | BindingFlags.Instance);
            Undo.RegisterCompleteObjectUndo(manager, "Pin gesture emulator avatar");
            var settings = settingsField.GetValue(manager) ?? Activator.CreateInstance(settingsField.FieldType);
            favouriteField.SetValue(settings, avatar); settingsField.SetValue(manager, settings);
            EditorUtility.SetDirty(manager); PrefabUtility.RecordPrefabInstancePropertyModifications(manager);
        }
    }
}
