using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.modular_avatar.core;
using nadena.dev.modular_avatar.core.menu;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace OutfitToggleGenerator
{
    internal static class WardrobeMenuOrganization
    {
        [Serializable] internal sealed class NodeDto
        {
            public string id, parentId, label, kind, parameter;
            public int sourceId;
            public bool isDefault, automaticValue;
            public float value;
        }
        [Serializable] internal sealed class RootDto
        {
            public int id, layoutId;
            public string name, path, reason;
            public bool editable;
            public List<NodeDto> nodes = new List<NodeDto>();
        }
        [Serializable] internal sealed class SnapshotDto
        {
            public int ok, avatarId;
            public string revision, message;
            public List<RootDto> roots = new List<RootDto>();
        }
        [Serializable] internal sealed class CommandDto
        {
            public int avatarId, rootId;
            public string revision, action, nodeId, parentId, beforeId, label;
        }

        internal static SnapshotDto Snapshot(VRCAvatarDescriptor avatar)
        {
            try
            {
                var scene = WardrobeSceneEditor.Snapshot(avatar);
                if (scene.ok != 1) throw new InvalidOperationException(scene.message);
                var snapshot = new SnapshotDto { ok = 1, avatarId = avatar.GetInstanceID(), revision = scene.revision };
                var roots = avatar.GetComponentsInChildren<ModularAvatarMenuInstaller>(true);
                if (roots.Length > 128) throw new InvalidOperationException("This avatar has too many menu installers for the browser organizer.");
                foreach (var installer in roots)
                {
                    var root = installer.gameObject;
                    var layout = FindLayout(avatar, root);
                    var dto = new RootDto { id = root.GetInstanceID(), name = root.name,
                        path = AnimationUtility.CalculateTransformPath(root.transform, avatar.transform), layoutId = layout == null ? 0 : layout.GetInstanceID() };
                    dto.reason = Reason(root, layout);
                    dto.editable = dto.reason == null;
                    if (dto.editable)
                    {
                        try
                        {
                            if (layout != null) layout.ValidateLayout();
                            foreach (var node in layout == null ? InitialNodes(root) : layout.nodes)
                            {
                                var control = node.source.Control;
                                dto.nodes.Add(new NodeDto { id = node.id, parentId = node.parentId ?? "", label = Label(node),
                                    kind = node.folder ? "folder" : node.sourceKind == "menu-group" ? "switchingGroup" : control.type == VRCExpressionsMenu.Control.ControlType.SubMenu ? "submenu" : "control",
                                    sourceId = node.source.GetInstanceID(), isDefault = node.source.isDefault, automaticValue = node.source.automaticValue,
                                    parameter = control.parameter?.name ?? "", value = control.value });
                            }
                        }
                        catch (Exception error) { dto.editable = false; dto.reason = error.Message; }
                    }
                    // Creator menus remain visible, with an explicit native handoff.
                    if (!dto.editable)
                        foreach (var item in root.GetComponentsInChildren<ModularAvatarMenuItem>(true).Take(512))
                            dto.nodes.Add(new NodeDto { id = "readonly:" + item.GetInstanceID(), label = string.IsNullOrEmpty(item.label) ? item.name : item.label,
                                kind = "readonly", sourceId = item.GetInstanceID(), parameter = item.Control?.parameter?.name ?? "", value = item.Control?.value ?? 0, isDefault = item.isDefault });
                    snapshot.roots.Add(dto);
                }
                return snapshot;
            }
            catch (Exception error) { return new SnapshotDto { message = error.Message }; }
        }

        internal static AvatarWardrobeServer.ResultDto Execute(CommandDto command)
        {
            try
            {
                var avatar = AvatarWardrobeServer.SceneAvatar;
                var snapshot = Snapshot(avatar);
                if (snapshot.ok != 1) throw new InvalidOperationException(snapshot.message);
                if (command == null || command.avatarId != snapshot.avatarId || string.IsNullOrEmpty(command.revision) || command.revision != snapshot.revision)
                    throw new InvalidOperationException("The pinned avatar or scene changed. Refresh Menu before editing.");
                var rootDto = snapshot.roots.SingleOrDefault(x => x.id == command.rootId);
                if (rootDto == null) throw new InvalidOperationException("This exact menu root is no longer present.");
                var root = EditorUtility.InstanceIDToObject(rootDto.id) as GameObject;
                if (command.action == "inspect")
                {
                    Selection.activeGameObject = root; EditorGUIUtility.PingObject(root); EditorApplication.ExecuteMenuItem("Window/General/Inspector");
                    return new AvatarWardrobeServer.ResultDto { ok = 1, message = "Menu selected in Unity Inspector." };
                }
                if (!rootDto.editable) throw new InvalidOperationException(rootDto.reason);
                return AvatarWardrobeServer.EditAvatar("Organize menu", () => {
                    if (AvatarWardrobeServer.SceneAvatar != avatar) return new AvatarWardrobeServer.ResultDto { message = "The pinned avatar changed." };
                    try
                    {
                        var layout = FindLayout(avatar, root);
                        if (command.action == "initialize")
                        {
                            if (layout == null) layout = Initialize(avatar, root);
                        }
                        else
                        {
                            if (layout == null) throw new InvalidOperationException("Choose Organize this menu first.");
                            Edit(layout, command);
                        }
                        layout.ValidateLayout();
                        EditorUtility.SetDirty(layout);
                        PrefabUtility.RecordPrefabInstancePropertyModifications(layout);
                        EditorSceneManager.MarkSceneDirty(avatar.gameObject.scene);
                        return new AvatarWardrobeServer.ResultDto { ok = 1, id = layout.GetInstanceID().ToString(), message = "Menu layout applied. Save the scene to keep it; Undo is available in Unity." };
                    }
                    catch (Exception error) { return new AvatarWardrobeServer.ResultDto { message = error.Message }; }
                }, migratePresets: false);
            }
            catch (Exception error) { return new AvatarWardrobeServer.ResultDto { message = error.Message }; }
        }

        private static string Reason(GameObject root, WardrobeMenuLayout layout)
        {
            var marker = root.GetComponent<OutfitToggleGeneratedMenu>();
            if (marker == null || (marker.generatedKind != "menu-groups" && marker.generatedKind != "part-toggles"))
                return "Creator and other menu controls are read-only. Use Unity Inspector to edit them.";
            if (root.GetComponent<ModularAvatarMenuInstaller>().menuToAppend != null)
                return "This menu includes a menu asset. Use Unity Inspector to preserve its controls.";
            var group = root.GetComponent<ModularAvatarMenuGroup>();
            var item = root.GetComponent<ModularAvatarMenuItem>();
            if ((group == null) == (item == null)) return "This menu has an unsupported source. Use Unity Inspector.";
            var redirect = group != null ? group.targetObject : item.menuSource_otherObjectChildren;
            if (redirect != null && (layout == null || redirect != layout.transform.parent.gameObject))
                return "This menu is routed by another tool. Use Unity Inspector to preserve its routing.";
            if (item != null && (item.MenuSource != SubmenuSource.Children || item.Control?.type != VRCExpressionsMenu.Control.ControlType.SubMenu))
                return "This menu uses a source asset. Use Unity Inspector.";
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
            {
                if (transform.gameObject == root) continue;
                var owned = transform.GetComponent<OutfitToggleGeneratedMenu>();
                var control = transform.GetComponent<ModularAvatarMenuItem>();
                if (owned == null || control == null || control.Control == null ||
                    (owned.generatedKind != "menu-group" && owned.generatedKind != "menu-group-option" && owned.generatedKind != "part-toggle") ||
                    transform.GetComponents<MonoBehaviour>().OfType<MenuSource>().Count() != 1 ||
                    transform.GetComponent<ModularAvatarMenuInstaller>() != null || transform.GetComponent<ModularAvatarMenuGroup>() != null ||
                    (control.Control.type == VRCExpressionsMenu.Control.ControlType.SubMenu && (control.MenuSource != SubmenuSource.Children || control.menuSource_otherObjectChildren != null)))
                    return "This tree contains creator or unsupported controls. It is read-only to preserve their behavior.";
            }
            return null;
        }

        private static WardrobeMenuLayout FindLayout(VRCAvatarDescriptor avatar, GameObject root)
        {
            var matches = avatar.GetComponentsInChildren<WardrobeMenuLayout>(true).Where(x => x.sourceRoot == root).ToArray();
            if (matches.Length > 1) throw new InvalidOperationException("This menu has duplicate layout providers. Resolve them in Unity Inspector.");
            return matches.FirstOrDefault();
        }
        private static List<WardrobeMenuLayout.Node> InitialNodes(GameObject root)
        {
            var items = root.GetComponentsInChildren<ModularAvatarMenuItem>(true).Where(x => x.gameObject != root).ToArray();
            if (items.Length > 512) throw new InvalidOperationException("This menu exceeds 512 controls.");
            return items.Select(item => {
                var marker = item.GetComponent<OutfitToggleGeneratedMenu>();
                var toggle = item.GetComponent<ModularAvatarObjectToggle>();
                return new WardrobeMenuLayout.Node { id = "source:" + item.GetInstanceID(),
                    parentId = item.transform.parent.gameObject == root ? "" : "source:" + item.transform.parent.GetComponent<ModularAvatarMenuItem>().GetInstanceID(),
                    source = item, sourceKind = marker.generatedKind, sourceOwner = marker.ownerId,
                    target = toggle?.Objects.Select(x => x.Object?.Get(toggle)).FirstOrDefault(x => x != null) };
            }).ToList();
        }
        private static WardrobeMenuLayout Initialize(VRCAvatarDescriptor avatar, GameObject root)
        {
            var helper = new GameObject("Avatar Wardrobe Menu Layout");
            Undo.RegisterCreatedObjectUndo(helper, "Organize menu");
            helper.transform.SetParent(avatar.transform, false);
            var source = new GameObject("Logical menu source");
            Undo.RegisterCreatedObjectUndo(source, "Organize menu");
            source.transform.SetParent(helper.transform, false);
            var layout = Undo.AddComponent<WardrobeMenuLayout>(source);
            var marker = root.GetComponent<OutfitToggleGeneratedMenu>();
            layout.sourceRoot = root; layout.sourceParent = root.transform.parent.gameObject;
            layout.sourceKind = marker.generatedKind; layout.sourceOwner = marker.ownerId;
            layout.nodes = InitialNodes(root);
            Route(root, helper);
            return layout;
        }
        private static void Route(GameObject root, GameObject helper)
        {
            var group = root.GetComponent<ModularAvatarMenuGroup>();
            if (group != null) { Undo.RegisterCompleteObjectUndo(group, "Organize menu"); group.targetObject = helper; EditorUtility.SetDirty(group); PrefabUtility.RecordPrefabInstancePropertyModifications(group); }
            else
            {
                var item = root.GetComponent<ModularAvatarMenuItem>();
                Undo.RegisterCompleteObjectUndo(item, "Organize menu"); item.menuSource_otherObjectChildren = helper; EditorUtility.SetDirty(item); PrefabUtility.RecordPrefabInstancePropertyModifications(item);
            }
        }
        private static string Label(WardrobeMenuLayout.Node node) => node.folder ? node.label : string.IsNullOrEmpty(node.source.label) ? node.source.name : node.source.label;
        private static string ValidLabel(string label)
        {
            label = label?.Trim();
            if (string.IsNullOrEmpty(label) || label.Length > 80 || label.Any(char.IsControl)) throw new InvalidOperationException("Enter a folder name of 1–80 characters without control characters.");
            return label;
        }
        private static void Parent(WardrobeMenuLayout layout, string parent)
        {
            if (string.IsNullOrEmpty(parent)) return;
            if (!layout.nodes.Any(x => x.id == parent && x.source.Control.type == VRCExpressionsMenu.Control.ControlType.SubMenu))
                throw new InvalidOperationException("Choose a folder or submenu from this exact menu.");
        }
        private static void Edit(WardrobeMenuLayout layout, CommandDto command)
        {
            Undo.RegisterCompleteObjectUndo(layout, "Organize menu");
            var node = layout.nodes.SingleOrDefault(x => x.id == command.nodeId);
            switch (command.action)
            {
                case "folder":
                    Parent(layout, command.parentId);
                    var label = ValidLabel(command.label);
                    if (layout.nodes.Any(x => x.parentId == (command.parentId ?? "") && Label(x) == label)) throw new InvalidOperationException("A sibling already uses this name.");
                    var template = new GameObject(label);
                    Undo.RegisterCreatedObjectUndo(template, "Create menu folder"); template.transform.SetParent(layout.transform, false);
                    var item = Undo.AddComponent<ModularAvatarMenuItem>(template);
                    item.MenuSource = SubmenuSource.Children; item.automaticValue = false;
                    item.Control = new VRCExpressionsMenu.Control { name = label, type = VRCExpressionsMenu.Control.ControlType.SubMenu };
                    node = new WardrobeMenuLayout.Node { id = Guid.NewGuid().ToString("N"), parentId = command.parentId ?? "", label = label, folder = true, source = item };
                    Insert(layout, node, command.beforeId);
                    break;
                case "rename":
                    if (node == null || !node.folder) throw new InvalidOperationException("Only logical folder names can be changed here.");
                    var renamed = ValidLabel(command.label);
                    if (layout.nodes.Any(x => x != node && x.parentId == node.parentId && Label(x) == renamed)) throw new InvalidOperationException("A sibling already uses this name.");
                    node.label = renamed;
                    break;
                case "move":
                    if (node == null) throw new InvalidOperationException("The exact control is missing.");
                    Parent(layout, command.parentId);
                    if (command.beforeId == node.id) throw new InvalidOperationException("Choose another insertion point.");
                    node.parentId = command.parentId ?? "";
                    layout.nodes.Remove(node); Insert(layout, node, command.beforeId);
                    break;
                case "deleteFolder":
                    if (node == null || !node.folder) throw new InvalidOperationException("Generated controls cannot be deleted in the organizer.");
                    if (layout.nodes.Any(x => x.parentId == node.id)) throw new InvalidOperationException("Move the folder's controls out before removing it.");
                    layout.nodes.Remove(node); Undo.DestroyObjectImmediate(node.source.gameObject);
                    break;
                default: throw new InvalidOperationException("Unknown menu command.");
            }
        }
        private static void Insert(WardrobeMenuLayout layout, WardrobeMenuLayout.Node node, string before)
        {
            if (string.IsNullOrEmpty(before)) layout.nodes.Add(node);
            else
            {
                var index = layout.nodes.FindIndex(x => x.id == before && x.parentId == node.parentId);
                if (index < 0) throw new InvalidOperationException("The insertion point is no longer a sibling. Refresh Menu.");
                layout.nodes.Insert(index, node);
            }
        }

        // Called after Wardrobe regenerates controls. Match their stable owned
        // group IDs and referenced scene targets, never names or current order.
        internal static void Rebind(VRCAvatarDescriptor avatar)
        {
            if (avatar == null) return;
            var roots = avatar.GetComponentsInChildren<ModularAvatarMenuInstaller>(true).Select(x => x.gameObject).ToArray();
            foreach (var layout in avatar.GetComponentsInChildren<WardrobeMenuLayout>(true))
            {
                var matches = roots.Where(root => {
                    var marker = root.GetComponent<OutfitToggleGeneratedMenu>();
                    return marker != null && marker.generatedKind == layout.sourceKind && marker.ownerId == layout.sourceOwner &&
                        (layout.sourceKind != "part-toggles" || root.transform.parent.gameObject == layout.sourceParent);
                }).ToArray();
                if (matches.Length == 0) continue; // Keep layout data if its source menu was deliberately removed.
                if (matches.Length != 1) throw new InvalidOperationException("A saved Menu layout has ambiguous generated roots.");
                var root = matches[0];
                var reason = Reason(root, layout);
                if (reason != null) throw new InvalidOperationException(reason);
                var current = InitialNodes(root);
                var map = new Dictionary<string, string>();
                var used = new HashSet<WardrobeMenuLayout.Node>();
                Undo.RegisterCompleteObjectUndo(layout, "Restore menu layout");
                foreach (var fresh in current)
                {
                    var candidates = layout.nodes.Where(old => !old.folder && old.sourceKind == fresh.sourceKind && old.sourceOwner == fresh.sourceOwner && old.target == fresh.target).ToArray();
                    if (candidates.Length > 1) throw new InvalidOperationException("A saved Menu control has ambiguous ownership. Use Unity Inspector.");
                    var existing = candidates.SingleOrDefault();
                    map[fresh.id] = existing?.id ?? fresh.id;
                    if (existing != null) { existing.source = fresh.source; used.Add(existing); }
                }
                foreach (var fresh in current.Where(x => !used.Any(old => old.source == x.source)))
                {
                    fresh.parentId = string.IsNullOrEmpty(fresh.parentId) ? "" : map[fresh.parentId];
                    layout.nodes.Add(fresh); used.Add(fresh);
                }
                var removed = new HashSet<string>(layout.nodes.Where(x => !x.folder && !used.Contains(x)).Select(x => x.id));
                foreach (var child in layout.nodes.Where(x => removed.Contains(x.parentId ?? ""))) child.parentId = "";
                layout.nodes.RemoveAll(x => removed.Contains(x.id));
                layout.sourceRoot = root; layout.sourceParent = root.transform.parent.gameObject;
                Route(root, layout.transform.parent.gameObject);
                layout.ValidateLayout(); EditorUtility.SetDirty(layout);
            }
        }
    }
}
