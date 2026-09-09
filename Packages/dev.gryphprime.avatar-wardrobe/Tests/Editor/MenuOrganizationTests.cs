using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using nadena.dev.modular_avatar.core;
using nadena.dev.modular_avatar.core.menu;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using Object = UnityEngine.Object;

namespace OutfitToggleGenerator
{
    public sealed class MenuOrganizationTests
    {
        private Scene scene, previousScene;
        private VRCAvatarDescriptor avatar, previousAvatar;
        private GameObject root, menu, group, firstTarget, secondTarget;
        private ModularAvatarMenuItem first, second;
        private string presets, overrides, uploads, folder;
        private Object selection;

        [SetUp] public void SetUp()
        {
            previousScene = WardrobeTestSceneFixture.RequireSavedActiveScene(); previousAvatar = AvatarWardrobeServer.SceneAvatar; selection = Selection.activeObject;
            presets = AvatarWardrobePresets.CaptureSettings(); overrides = AvatarWardrobeCatalog.CaptureOverrides(); uploads = ShiroTools.OutfitProjectData.CaptureSettings();
            folder = "Assets/MenuOrganizationFixture_" + Guid.NewGuid().ToString("N");
            System.IO.Directory.CreateDirectory(folder);
            scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            root = new GameObject("Menu fixture"); SceneManager.MoveGameObjectToScene(root, scene);
            avatar = root.AddComponent<VRCAvatarDescriptor>(); root.AddComponent<Animator>();
            AvatarWardrobeServer.SceneAvatar = avatar;
            firstTarget = Child("Coat", root); secondTarget = Child("Coat", root);
            firstTarget.transform.localPosition = new Vector3(1, 2, 3); secondTarget.transform.localScale = Vector3.one * 0.7f;
            menu = Child("Wardrobe", root); Mark(menu, "menu-groups", "test-owner");
            menu.AddComponent<ModularAvatarMenuInstaller>(); menu.AddComponent<ModularAvatarMenuGroup>();
            group = Child("Clothes", menu); Mark(group, "menu-group", "test-group");
            var submenu = group.AddComponent<ModularAvatarMenuItem>(); submenu.MenuSource = SubmenuSource.Children;
            submenu.Control = new VRCExpressionsMenu.Control { name = "Clothes", type = VRCExpressionsMenu.Control.ControlType.SubMenu };
            first = Control("First", group, firstTarget, false); second = Control("Second", group, secondTarget, true);
            EditorSceneManager.SaveScene(scene, folder + "/Scene.unity");
        }
        [TearDown] public void TearDown()
        {
            Undo.ClearAll(); AvatarWardrobeServer.SceneAvatar = previousAvatar; Selection.activeObject = selection;
            if (previousScene.IsValid()) SceneManager.SetActiveScene(previousScene);
            if (scene.IsValid()) EditorSceneManager.CloseScene(scene, true);
            if (!string.IsNullOrEmpty(folder)) AssetDatabase.DeleteAsset(folder);
            AvatarWardrobePresets.RestoreSettings(presets); AvatarWardrobeCatalog.RestoreOverrides(overrides); ShiroTools.OutfitProjectData.RestoreSettings(uploads);
        }
        private static GameObject Child(string name, GameObject parent) { var go = new GameObject(name); go.transform.SetParent(parent.transform, false); return go; }
        private static void Mark(GameObject go, string kind, string owner) { var marker = go.AddComponent<OutfitToggleGeneratedMenu>(); marker.generatedKind = kind; marker.ownerId = owner; }
        private ModularAvatarMenuItem Control(string name, GameObject parent, GameObject target, bool isDefault)
        {
            var go = Child(name, parent); Mark(go, "menu-group-option", "test-group");
            var item = go.AddComponent<ModularAvatarMenuItem>(); item.automaticValue = true; item.isDefault = isDefault;
            item.Control = new VRCExpressionsMenu.Control { name = name, type = VRCExpressionsMenu.Control.ControlType.Toggle,
                parameter = new VRCExpressionsMenu.Control.Parameter { name = "AW_MenuTestGroup" }, value = 1 };
            var toggle = go.AddComponent<ModularAvatarObjectToggle>(); toggle.Inverted = true;
            toggle.Objects.Add(new ToggledObject { Object = new AvatarObjectReference(target), Active = false }); return item;
        }
        private WardrobeMenuOrganization.CommandDto Command(string action)
        {
            var snapshot = WardrobeMenuOrganization.Snapshot(avatar); Assert.AreEqual(1, snapshot.ok, snapshot.message);
            return new WardrobeMenuOrganization.CommandDto { action = action, avatarId = avatar.GetInstanceID(), rootId = menu.GetInstanceID(), revision = snapshot.revision };
        }
        private void Success(WardrobeMenuOrganization.CommandDto command) { var result = WardrobeMenuOrganization.Execute(command); Assert.AreEqual(1, result.ok, result.message); }
        private WardrobeMenuLayout Initialize() { Success(Command("initialize")); return root.GetComponentInChildren<WardrobeMenuLayout>(true); }
        private WardrobeMenuLayout.Node AddFolder(WardrobeMenuLayout layout, string name = "Favorites", string parent = "")
        {
            var command = Command("folder"); command.label = name; command.parentId = parent; Success(command); return layout.nodes.Single(x => x.folder && x.label == name);
        }

        [Test] public void SnapshotDoesNotCreateLayoutAndCreatorMenuIsReadOnly()
        {
            var creator = Child("Creator controls", root); creator.AddComponent<ModularAvatarMenuInstaller>(); creator.AddComponent<ModularAvatarMenuGroup>();
            var before = EditorJsonUtility.ToJson(first);
            var snapshot = WardrobeMenuOrganization.Snapshot(avatar);
            Assert.AreEqual(1, snapshot.ok, snapshot.message); Assert.IsNull(root.GetComponentInChildren<WardrobeMenuLayout>());
            Assert.IsFalse(snapshot.roots.Single(x => x.id == creator.GetInstanceID()).editable);
            Assert.AreEqual(before, EditorJsonUtility.ToJson(first)); Assert.AreEqual(presets, AvatarWardrobePresets.CaptureSettings());
            var command = Command("initialize"); command.rootId = creator.GetInstanceID(); Assert.AreEqual(0, WardrobeMenuOrganization.Execute(command).ok);
        }
        [Test] public void MoveAndFolderPreserveExactControlsDefaultsAndClothingHierarchy()
        {
            var firstJson = EditorJsonUtility.ToJson(first); var secondJson = EditorJsonUtility.ToJson(second);
            var firstTransform = EditorJsonUtility.ToJson(firstTarget.transform); var secondTransform = EditorJsonUtility.ToJson(secondTarget.transform);
            var layout = Initialize(); var folderNode = AddFolder(layout);
            var command = Command("move"); command.nodeId = layout.nodes.Single(x => x.source == second).id; command.parentId = folderNode.id; Success(command);
            Assert.AreEqual(firstJson, EditorJsonUtility.ToJson(first)); Assert.AreEqual(secondJson, EditorJsonUtility.ToJson(second));
            Assert.AreEqual(firstTransform, EditorJsonUtility.ToJson(firstTarget.transform)); Assert.AreEqual(secondTransform, EditorJsonUtility.ToJson(secondTarget.transform));
            Assert.AreEqual(0, first.transform.GetSiblingIndex()); Assert.AreEqual(1, second.transform.GetSiblingIndex());
            Assert.AreEqual(presets, AvatarWardrobePresets.CaptureSettings());
            Undo.PerformUndo(); Assert.AreEqual(layout.nodes.Single(x => x.source == group.GetComponent<ModularAvatarMenuItem>()).id, layout.nodes.Single(x => x.source == second).parentId);
        }
        [Test] public void CycleInvalidInsertionAndNonemptyDeletionRollback()
        {
            var layout = Initialize(); var a = AddFolder(layout, "A"); var b = AddFolder(layout, "B", a.id);
            var aId = a.id; var bId = b.id;
            var before = EditorJsonUtility.ToJson(layout);
            var cycle = Command("move"); cycle.nodeId = aId; cycle.parentId = bId;
            Assert.AreEqual(0, WardrobeMenuOrganization.Execute(cycle).ok); Assert.AreEqual(before, EditorJsonUtility.ToJson(layout));
            var deletion = Command("deleteFolder"); deletion.nodeId = aId;
            Assert.AreEqual(0, WardrobeMenuOrganization.Execute(deletion).ok); Assert.AreEqual(before, EditorJsonUtility.ToJson(layout));
            var bad = Command("folder"); bad.label = "Lost"; bad.beforeId = "missing";
            Assert.AreEqual(0, WardrobeMenuOrganization.Execute(bad).ok); Assert.AreEqual(before, EditorJsonUtility.ToJson(layout));
            Assert.IsFalse(layout.GetComponentsInChildren<Transform>().Any(x => x.name == "Lost"));
        }
        [Test] public void StaleRevisionExactRootAndGeneratedDeletionAreRejected()
        {
            var layout = Initialize(); var stale = Command("folder"); stale.label = "Stale"; first.isDefault = true;
            Assert.AreEqual(0, WardrobeMenuOrganization.Execute(stale).ok);
            var foreign = Command("folder"); foreign.label = "Foreign"; foreign.rootId = root.GetInstanceID(); Assert.AreEqual(0, WardrobeMenuOrganization.Execute(foreign).ok);
            var deletion = Command("deleteFolder"); deletion.nodeId = layout.nodes.Single(x => x.source == first).id; Assert.AreEqual(0, WardrobeMenuOrganization.Execute(deletion).ok);
            Assert.AreEqual(3, layout.nodes.Count);
        }
        [Test] public void ProviderEmitsLogicalOrderAndUnchangedParameterValues()
        {
            var layout = Initialize(); var groupNode = layout.nodes.Single(x => x.source == group.GetComponent<ModularAvatarMenuItem>());
            var move = Command("move"); move.nodeId = layout.nodes.Single(x => x.source == second).id; move.parentId = groupNode.id; move.beforeId = layout.nodes.Single(x => x.source == first).id; Success(move);
            first.Control.value = 7; second.Control.value = 13; // Values already resolved by MA at the visitor boundary.
            var context = new FakeContext(); layout.Visit(context);
            var submenu = context.controls.Single(); Assert.AreEqual("Clothes", submenu.name);
            CollectionAssert.AreEqual(new[] { "Second", "First" }, submenu.SubmenuNode.Controls.Select(x => x.name));
            CollectionAssert.AreEqual(new[] { 13f, 7f }, submenu.SubmenuNode.Controls.Select(x => x.value));
            Assert.IsTrue(submenu.SubmenuNode.Controls.All(x => x.parameter.name == "AW_MenuTestGroup")); Assert.IsTrue(second.isDefault);
        }
        [Test] public void RegeneratedControlsRebindByTargetIdentityInsteadOfDuplicateNames()
        {
            var layout = Initialize(); var folderNode = AddFolder(layout);
            var secondId = layout.nodes.Single(x => x.source == second).id;
            var move = Command("move"); move.nodeId = secondId; move.parentId = folderNode.id; Success(move);
            Object.DestroyImmediate(first.gameObject); Object.DestroyImmediate(second.gameObject);
            second = Control("Same name", group, secondTarget, true); first = Control("Same name", group, firstTarget, false);
            WardrobeMenuOrganization.Rebind(avatar);
            Assert.AreSame(second, layout.nodes.Single(x => x.id == secondId).source); Assert.AreEqual(folderNode.id, layout.nodes.Single(x => x.id == secondId).parentId);
            Assert.IsTrue(second.isDefault); Assert.AreEqual(1, layout.nodes.Count(x => x.source == first)); layout.ValidateLayout();
        }
        [Test] public void NdmfBuildKeepsParameterAndDefaultIdentityAfterPresentationReorder()
        {
            var baseline = BuildControls();
            var layout = Initialize(); var groupNode = layout.nodes.Single(x => x.source == group.GetComponent<ModularAvatarMenuItem>());
            var move = Command("move"); move.nodeId = layout.nodes.Single(x => x.source == second).id; move.parentId = groupNode.id; move.beforeId = layout.nodes.Single(x => x.source == first).id; Success(move);
            var folderNode = AddFolder(layout);
            move = Command("move"); move.nodeId = groupNode.id; move.parentId = folderNode.id; Success(move);
            var organized = BuildControls();
            Assert.AreEqual(baseline["First"], organized["First"]); Assert.AreEqual(baseline["Second"], organized["Second"]);
            Assert.AreEqual(baseline["$default"], organized["$default"]);
            Assert.AreNotEqual(baseline["First"], baseline["Second"]);
            Assert.AreEqual(organized["Second"], organized["$default"]);
            Assert.AreEqual("Second,First", organized["$order"]);
            Assert.IsTrue(organized.ContainsKey("Favorites"));
        }
        private Dictionary<string, string> BuildControls()
        {
            var clone = Object.Instantiate(root); clone.name = "Disposable menu build";
            try
            {
                nadena.dev.ndmf.AvatarProcessor.ProcessAvatar(clone);
                var built = clone.GetComponent<VRCAvatarDescriptor>();
                Assert.IsNotNull(built.expressionsMenu, "NDMF must emit an expressions menu.");
                var result = new Dictionary<string, string>(); var order = new List<string>();
                void Visit(VRCExpressionsMenu source)
                {
                    foreach (var control in source.controls)
                    {
                        result[control.name] = (control.parameter?.name ?? "") + ":" + control.value;
                        if (control.name == "First" || control.name == "Second") order.Add(control.name);
                        if (control.subMenu != null) Visit(control.subMenu);
                    }
                }
                Visit(built.expressionsMenu);
                var parameterName = result["Second"].Split(':')[0];
                var parameter = built.expressionParameters.parameters.Single(x => x.name == parameterName);
                result["$default"] = parameter.name + ":" + parameter.defaultValue; result["$order"] = string.Join(",", order);
                Assert.IsTrue(clone.GetComponentsInChildren<WardrobeMenuLayout>(true).All(x => x is VRC.SDKBase.IEditorOnly), "Any layout remaining after NDMF is marked for the SDK editor-only stripping pass.");
                return result;
            }
            finally { Object.DestroyImmediate(clone); }
        }
        private sealed class FakeContext : NodeContext
        {
            internal readonly List<VirtualControl> controls = new List<VirtualControl>();
            private readonly Dictionary<MenuSource, VirtualMenuNode> nodes = new Dictionary<MenuSource, VirtualMenuNode>();
            public void PushNode(MenuSource source) => source.Visit(this);
            public void PushNode(ModularAvatarMenuInstaller installer) => installer.GetComponent<MenuSource>().Visit(this);
            public void PushControl(VirtualControl control) => controls.Add(control);
            public void PushControl(VRCExpressionsMenu.Control control) => throw new NotSupportedException();
            public void PushMenuContents(VRCExpressionsMenu menu) => throw new NotSupportedException();
            public VirtualMenuNode NodeFor(VRCExpressionsMenu menu) => throw new NotSupportedException();
            public VirtualMenuNode NodeFor(MenuSource source)
            {
                if (nodes.TryGetValue(source, out var existing)) return existing;
                // MA deliberately keeps the concrete constructor internal; only
                // the test fake constructs it, production uses NodeContext.
                var node = (VirtualMenuNode)System.Activator.CreateInstance(typeof(VirtualMenuNode), BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { source }, null);
                nodes.Add(source, node); var context = new FakeContext(); source.Visit(context); node.Controls.AddRange(context.controls); return node;
            }
        }
    }
}
