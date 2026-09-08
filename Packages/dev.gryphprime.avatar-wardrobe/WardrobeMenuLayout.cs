using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.modular_avatar.core;
using nadena.dev.modular_avatar.core.menu;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace OutfitToggleGenerator
{
    // Presentation only: original MA items remain in their original hierarchy so
    // automatic parameter values and default controls retain their identity.
    [AddComponentMenu("")]
    public sealed class WardrobeMenuLayout : MonoBehaviour, MenuSource, VRC.SDKBase.IEditorOnly
    {
        [Serializable] public sealed class Node
        {
            public string id, parentId, label, sourceKind, sourceOwner;
            public bool folder;
            public ModularAvatarMenuItem source;
            public GameObject target;
        }
        [HideInInspector] public GameObject sourceRoot, sourceParent;
        [HideInInspector] public string sourceKind, sourceOwner;
        [HideInInspector] public List<Node> nodes = new List<Node>();

        public void ValidateLayout()
        {
            if (nodes == null || nodes.Count > 512) throw new InvalidOperationException("Wardrobe menu layout exceeds 512 controls.");
            var byId = new Dictionary<string, Node>(StringComparer.Ordinal);
            var sources = new HashSet<ModularAvatarMenuItem>();
            foreach (var node in nodes)
            {
                if (node == null || string.IsNullOrEmpty(node.id) || byId.ContainsKey(node.id) || node.source == null || !sources.Add(node.source))
                    throw new InvalidOperationException("Wardrobe menu layout has a missing or duplicate control. Refresh the Menu view.");
                if (node.folder && (node.source.Control?.type != VRCExpressionsMenu.Control.ControlType.SubMenu ||
                    !string.IsNullOrEmpty(node.source.Control.parameter?.name) || node.source.automaticValue))
                    throw new InvalidOperationException("A logical folder must be a parameterless submenu.");
                byId.Add(node.id, node);
            }
            foreach (var node in nodes)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal) { node.id };
                var parentId = node.parentId;
                while (!string.IsNullOrEmpty(parentId))
                {
                    if (!byId.TryGetValue(parentId, out var parent) || !seen.Add(parentId) || seen.Count > 24 ||
                        parent.source.Control?.type != VRCExpressionsMenu.Control.ControlType.SubMenu)
                        throw new InvalidOperationException("Wardrobe menu layout contains a cycle, invalid folder, or more than 24 levels.");
                    parentId = parent.parentId;
                }
            }
            if (sourceRoot == null) throw new InvalidOperationException("The original Wardrobe menu is missing. Regenerate its controls before building.");
            foreach (var child in sourceRoot.GetComponentsInChildren<Transform>(true))
            {
                if (child.gameObject == sourceRoot) continue;
                var marker = child.GetComponent<OutfitToggleGeneratedMenu>();
                if (marker == null || child.GetComponent<ModularAvatarMenuItem>() == null ||
                    child.GetComponents<MonoBehaviour>().OfType<MenuSource>().Count() != 1 ||
                    (marker.generatedKind != "menu-group" && marker.generatedKind != "menu-group-option" && marker.generatedKind != "part-toggle"))
                    throw new InvalidOperationException("A Wardrobe Menu tree now contains creator controls. Resolve its layout in Unity before building.");
            }
            var original = sourceRoot.GetComponentsInChildren<ModularAvatarMenuItem>(true).Where(x => x.gameObject != sourceRoot).ToArray();
            if (original.Length != nodes.Count(x => !x.folder) || original.Any(x => !sources.Contains(x)))
                throw new InvalidOperationException("Wardrobe menu controls changed. Refresh or regenerate the Menu layout before building.");
        }

        public void Visit(NodeContext context)
        {
            ValidateLayout();
            new Children(this, "").Visit(context);
        }

        private sealed class Children : MenuSource
        {
            private readonly WardrobeMenuLayout layout;
            private readonly string parent;
            internal Children(WardrobeMenuLayout layout, string parent) { this.layout = layout; this.parent = parent ?? ""; }
            public void Visit(NodeContext context)
            {
                foreach (var node in layout.nodes.Where(x => (x.parentId ?? "") == parent))
                {
                    if (node.source.Control?.type == VRCExpressionsMenu.Control.ControlType.SubMenu)
                        node.source.Visit(new SubmenuContext(context, layout, node));
                    else node.source.Visit(context);
                }
            }
            public override bool Equals(object obj) => obj is Children other && other.layout == layout && other.parent == parent;
            public override int GetHashCode() => (layout == null ? 0 : layout.GetHashCode()) * 397 ^ parent.GetHashCode();
        }

        // MA creates its VirtualControl through its own public visitor. Redirect
        // only submenu contents; every parameter/value/icon remains MA's value.
        private sealed class SubmenuContext : NodeContext
        {
            private readonly NodeContext inner;
            private readonly WardrobeMenuLayout layout;
            private readonly Node node;
            internal SubmenuContext(NodeContext inner, WardrobeMenuLayout layout, Node node) { this.inner = inner; this.layout = layout; this.node = node; }
            public VirtualMenuNode NodeFor(MenuSource unused) => inner.NodeFor(new Children(layout, node.id));
            public VirtualMenuNode NodeFor(VRCExpressionsMenu unused) => inner.NodeFor(new Children(layout, node.id));
            public void PushControl(VirtualControl control)
            {
                if (node.folder) control.name = node.label;
                control.SubmenuNode = inner.NodeFor(new Children(layout, node.id));
                inner.PushControl(control);
            }
            public void PushControl(VRCExpressionsMenu.Control control) => inner.PushControl(control);
            public void PushMenuContents(VRCExpressionsMenu menu) => inner.PushMenuContents(menu);
            public void PushNode(MenuSource source) => inner.PushNode(source);
            public void PushNode(ModularAvatarMenuInstaller installer) => inner.PushNode(installer);
        }
    }
}
