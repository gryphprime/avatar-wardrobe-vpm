using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using nadena.dev.modular_avatar.core;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

namespace OutfitToggleGenerator
{
    public sealed class OrganizationTests
    {
        private string saved;
        private bool existed;

        [SetUp] public void SetUp()
        {
            existed = File.Exists(WardrobeOrganization.StorePath);
            saved = existed ? File.ReadAllText(WardrobeOrganization.StorePath) : null;
            if (existed) File.Delete(WardrobeOrganization.StorePath);
            if (File.Exists(WardrobeOrganization.StorePath + ".bak")) File.Move(WardrobeOrganization.StorePath + ".bak", WardrobeOrganization.StorePath + ".testbak");
            WardrobeOrganization.Invalidate();
        }

        [TearDown] public void TearDown()
        {
            if (File.Exists(WardrobeOrganization.StorePath)) File.Delete(WardrobeOrganization.StorePath);
            if (File.Exists(WardrobeOrganization.StorePath + ".bak")) File.Delete(WardrobeOrganization.StorePath + ".bak");
            if (File.Exists(WardrobeOrganization.StorePath + ".testbak")) File.Move(WardrobeOrganization.StorePath + ".testbak", WardrobeOrganization.StorePath + ".bak");
            if (existed) File.WriteAllText(WardrobeOrganization.StorePath, saved);
            WardrobeOrganization.Invalidate();
        }

        private static WardrobeFamily Family(string id, params string[] paths)
        {
            return new WardrobeFamily { id = id, displayName = id, variants = paths.Select(path => new WardrobeAssetRecord { guid = Guid.NewGuid().ToString("N"), assetPath = path }).ToList() };
        }

        [Test] public void TagsPersistAndDeletingATagClearsAssignments()
        {
            var summer = WardrobeOrganization.SaveTag(null, " Summer ", "#ABCDEF");
            Assert.AreEqual("Summer", summer.name);
            Assert.AreEqual("#abcdef", summer.color);
            Assert.Throws<ArgumentException>(() => WardrobeOrganization.SaveTag(null, "summer", "#000000"));
            Assert.AreEqual("#8a8f98", WardrobeOrganization.SaveTag(null, "Formal", "red").color);

            WardrobeOrganization.SetFamilyTag(new[] { "family:a", "family:b" }, summer.id, true);
            WardrobeOrganization.SetFamilyTag(new[] { "family:b" }, summer.id, false);
            WardrobeOrganization.Invalidate();
            CollectionAssert.AreEqual(new[] { summer.id }, WardrobeOrganization.TagsFor("family:a"));
            CollectionAssert.IsEmpty(WardrobeOrganization.TagsFor("family:b"));

            WardrobeOrganization.SaveTag(summer.id, "Beach", "#123456");
            Assert.AreEqual("Beach", WardrobeOrganization.Tags().Single(tag => tag.id == summer.id).name);
            WardrobeOrganization.DeleteTag(summer.id);
            CollectionAssert.IsEmpty(WardrobeOrganization.TagsFor("family:a"));
            Assert.Throws<ArgumentException>(() => WardrobeOrganization.SetFamilyTag(new[] { "family:a" }, summer.id, true));
        }

        [Test] public void HidingItemsAndFoldersIsReversible()
        {
            var helper = Family("asset:depth", "Assets/_PoiyomiShaders/Prefabs/DepthGet.prefab");
            var mixed = Family("family:mixed", "Assets/_PoiyomiShaders/A.prefab", "Assets/Outfits/A.prefab");
            var outfit = Family("family:dress", "Assets/Outfits/Dress.prefab");

            WardrobeOrganization.SetHidden(new[] { outfit.id }, true);
            Assert.IsTrue(WardrobeOrganization.IsHidden(outfit));
            WardrobeOrganization.SetHidden(new[] { outfit.id }, false);
            Assert.IsFalse(WardrobeOrganization.IsHidden(outfit));

            WardrobeOrganization.SetHiddenPath("Assets\\_PoiyomiShaders/", true);
            Assert.IsTrue(WardrobeOrganization.IsHidden(helper));
            Assert.AreEqual("Assets/_PoiyomiShaders/", WardrobeOrganization.HiddenByPath(helper));
            Assert.IsFalse(WardrobeOrganization.IsHidden(mixed), "A family stays visible while any variant is outside the folder.");
            WardrobeOrganization.SetHiddenPath("Assets/_PoiyomiShaders", false);
            Assert.IsFalse(WardrobeOrganization.IsHidden(helper));

            Assert.Throws<ArgumentException>(() => WardrobeOrganization.SetHiddenPath("Assets/../Library", true));
            Assert.Throws<ArgumentException>(() => WardrobeOrganization.SetHiddenPath("/Users/someone", true));
        }
    }

    public sealed class PartsEditTests
    {
        private Scene scene, previousScene;
        private VRCAvatarDescriptor avatar, previousAvatar;
        private GameObject root, item, top, skirt;
        private SkinnedMeshRenderer body;
        private readonly List<Object> assets = new List<Object>();

        private Mesh MeshWith(params string[] shapes)
        {
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
            foreach (var shape in shapes) mesh.AddBlendShapeFrame(shape, 100f, new Vector3[3], null, null);
            assets.Add(mesh);
            return mesh;
        }

        private static GameObject Child(string name, GameObject parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            return go;
        }

        [SetUp] public void SetUp()
        {
            previousScene = WardrobeTestSceneFixture.RequireSavedActiveScene();
            previousAvatar = AvatarWardrobeServer.SceneAvatar;
            scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            root = new GameObject("Parts fixture");
            SceneManager.MoveGameObjectToScene(root, scene);
            avatar = root.AddComponent<VRCAvatarDescriptor>();
            root.AddComponent<Animator>();
            AvatarWardrobeServer.SceneAvatar = avatar;
            body = Child("Body", root).AddComponent<SkinnedMeshRenderer>();
            body.sharedMesh = MeshWith("Shrink_Chest", "Chest_Big", "Hide_Feet");
            item = Child("Sailor Dress", root);
            Child("Armature", item);
            top = Child("Top", item);
            top.AddComponent<SkinnedMeshRenderer>().sharedMesh = MeshWith("Chest_Big", "Top_Only");
            skirt = Child("Skirt", item);
            skirt.AddComponent<SkinnedMeshRenderer>().sharedMesh = MeshWith("Skirt_Flare");
        }

        [TearDown] public void TearDown()
        {
            Undo.ClearAll();
            AvatarWardrobeServer.SceneAvatar = previousAvatar;
            if (previousScene.IsValid()) SceneManager.SetActiveScene(previousScene);
            if (scene.IsValid()) EditorSceneManager.CloseScene(scene, true);
            foreach (var asset in assets) Object.DestroyImmediate(asset);
            assets.Clear();
        }

        private WardrobePartsEdit Edit(int sync, bool skirtWorn, params WardrobeShapeEdit[] topShapes)
        {
            return new WardrobePartsEdit
            {
                sync = sync,
                parts = new List<WardrobePartEdit>
                {
                    new WardrobePartEdit { key = "", shapes = new List<WardrobeShapeEdit> { new WardrobeShapeEdit { mesh = "Body", shape = "Hide_Feet", delete = 1 } } },
                    new WardrobePartEdit { key = "Top", worn = 1, shapes = topShapes.ToList() },
                    new WardrobePartEdit { key = "Skirt", worn = skirtWorn ? 1 : 0 },
                },
            };
        }

        [Test] public void BodyMeshesExcludeTheEditedItem()
        {
            var meshes = OutfitToggleGenerator.BodyMeshes(avatar, item);
            CollectionAssert.AreEqual(new[] { body }, meshes);
            CollectionAssert.AreEqual(new[] { "Top", "Skirt" }, OutfitToggleGenerator.ItemParts(item).Select(part => part.name));
        }

        [Test] public void ApplyingPartsWritesShapeChangersSyncAndWearDefaults()
        {
            var edit = Edit(1, false, new WardrobeShapeEdit { mesh = "Body", shape = "Shrink_Chest", value = 140 });
            OutfitToggleGenerator.ValidatePartsEdit(avatar, item, edit);
            OutfitToggleGenerator.ApplyPartsEdit(avatar, item, edit);

            Assert.IsFalse(skirt.activeSelf);
            var topShapes = OutfitToggleGenerator.ShapeChanges(avatar, top);
            Assert.AreEqual(1, topShapes.Count);
            Assert.AreEqual("Shrink_Chest", topShapes[0].shape);
            Assert.AreEqual(100f, topShapes[0].value, "Values are clamped to the blendshape range.");
            var whole = OutfitToggleGenerator.ShapeChanges(avatar, item).Single();
            Assert.AreEqual(1, whole.delete);
            Assert.AreEqual("Body", whole.mesh);

            var sync = top.GetComponent<ModularAvatarBlendshapeSync>();
            Assert.IsNotNull(sync);
            CollectionAssert.AreEqual(new[] { "Chest_Big" }, sync.Bindings.Select(binding => binding.Blendshape));
            Assert.IsNull(skirt.GetComponent<ModularAvatarBlendshapeSync>(), "Meshes without shared shapes are left alone.");
            Assert.IsTrue(OutfitToggleGenerator.HasBlendshapeSync(item));
            CollectionAssert.AreEqual(new[] { "Top", "Skirt" }, OutfitToggleGenerator.ItemParts(item).Select(part => part.name),
                "Generated helpers never become parts.");

            OutfitToggleGenerator.ApplyPartsEdit(avatar, item, new WardrobePartsEdit
            {
                parts = new List<WardrobePartEdit> { new WardrobePartEdit { key = "" }, new WardrobePartEdit { key = "Top" }, new WardrobePartEdit { key = "Skirt" } },
            });
            Assert.IsTrue(skirt.activeSelf);
            Assert.IsNull(top.GetComponent<ModularAvatarBlendshapeSync>());
            Assert.IsFalse(OutfitToggleGenerator.HasBlendshapeSync(item));
            Assert.IsEmpty(item.GetComponentsInChildren<ModularAvatarShapeChanger>(true));
            Assert.IsEmpty(item.GetComponentsInChildren<OutfitToggleGeneratedMenu>(true));
        }

        [Test] public void CreatorBlendshapeSyncIsKept()
        {
            var creator = top.AddComponent<ModularAvatarBlendshapeSync>();
            OutfitToggleGenerator.ApplyPartsEdit(avatar, item, Edit(1, true));
            OutfitToggleGenerator.ApplyPartsEdit(avatar, item, Edit(0, true));
            Assert.IsTrue(creator != null && creator == top.GetComponent<ModularAvatarBlendshapeSync>());
        }

        [Test] public void WearingWithFollowBodyAddsSyncOnceAndKeepsCustomizeChoices()
        {
            OutfitToggleGenerator.EnsureBlendshapeSync(avatar, item);
            var sync = top.GetComponent<ModularAvatarBlendshapeSync>();
            Assert.IsNotNull(sync);
            Assert.AreSame(body.gameObject, sync.Bindings.Single().ReferenceMesh.Get(top.transform));
            OutfitToggleGenerator.EnsureBlendshapeSync(avatar, item);
            Assert.AreEqual(1, top.GetComponents<ModularAvatarBlendshapeSync>().Length, "Wearing again does not stack components.");
            Assert.AreEqual(1, item.GetComponentsInChildren<OutfitToggleGeneratedMenu>(true).Length);

            OutfitToggleGenerator.ApplyPartsEdit(avatar, item, Edit(0, true));
            Assert.IsFalse(OutfitToggleGenerator.HasBlendshapeSync(item), "Customize can turn wear-time sync off again.");
        }

        [Test] public void ValidationRejectsUnknownShapesAndParts()
        {
            Assert.Throws<ArgumentException>(() => OutfitToggleGenerator.ValidatePartsEdit(avatar, item,
                Edit(0, true, new WardrobeShapeEdit { mesh = "Body", shape = "Missing" })));
            Assert.Throws<ArgumentException>(() => OutfitToggleGenerator.ValidatePartsEdit(avatar, item,
                Edit(0, true, new WardrobeShapeEdit { mesh = "Sailor Dress/Top", shape = "Top_Only" })));
            var renamed = Edit(0, true);
            renamed.parts[1].key = "Jacket";
            Assert.Throws<ArgumentException>(() => OutfitToggleGenerator.ValidatePartsEdit(avatar, item, renamed));
        }

        private static WardrobeSliderEdit Slider(string mesh, string shape, float value, string label = "")
        {
            return new WardrobeSliderEdit { mesh = mesh, shape = shape, value = value, label = label };
        }

        private void DeleteControllers()
        {
            foreach (var merge in item.GetComponentsInChildren<ModularAvatarMergeAnimator>(true))
                if (merge.animator != null) AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(merge.animator));
        }

        [Test] public void SlidersAddRadialMenuItemsSyncedParametersAndAnFxLayer()
        {
            var edit = Edit(0, true);
            edit.sliders = new List<WardrobeSliderEdit>
            {
                Slider("Body", "Chest_Big", 25, " Chest "),
                Slider("Sailor Dress/Skirt", "Skirt_Flare", 0),
            };
            try
            {
                OutfitToggleGenerator.ValidatePartsEdit(avatar, item, edit);
                OutfitToggleGenerator.ApplyPartsEdit(avatar, item, edit);

                var installer = item.GetComponentInChildren<ModularAvatarMenuInstaller>(true);
                Assert.IsNotNull(installer);
                var host = installer.gameObject;
                Assert.AreEqual(SubmenuSource.Children, host.GetComponent<ModularAvatarMenuItem>().MenuSource);
                var parameters = host.GetComponent<ModularAvatarParameters>().parameters;
                Assert.AreEqual(2, parameters.Count);
                Assert.IsTrue(parameters.All(p => p.syncType == ParameterSyncType.Float && p.saved));
                Assert.AreEqual(0.25f, parameters[0].defaultValue, 0.0001f);

                var merge = host.GetComponent<ModularAvatarMergeAnimator>();
                Assert.AreEqual(VRCAvatarDescriptor.AnimLayerType.FX, merge.layerType);
                var controller = (UnityEditor.Animations.AnimatorController)merge.animator;
                Assert.IsTrue(AssetDatabase.Contains(controller));
                Assert.AreEqual(2, controller.layers.Length);
                var state = controller.layers[1].stateMachine.defaultState;
                Assert.IsTrue(state.timeParameterActive);
                Assert.AreEqual(parameters[1].nameOrPrefix, state.timeParameter);
                var binding = AnimationUtility.GetCurveBindings((AnimationClip)state.motion).Single();
                Assert.AreEqual("Sailor Dress/Skirt", binding.path);
                Assert.AreEqual("blendShape.Skirt_Flare", binding.propertyName);

                var radials = host.GetComponentsInChildren<ModularAvatarMenuItem>(true).Where(menu => menu.gameObject != host).ToList();
                CollectionAssert.AreEqual(new[] { "Chest", "Skirt_Flare" }, radials.Select(menu => menu.label));
                Assert.IsTrue(radials.All(menu => menu.Control.type == VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType.RadialPuppet));
                CollectionAssert.AreEqual(parameters.Select(p => p.nameOrPrefix), radials.Select(menu => menu.Control.subParameters[0].name));
                CollectionAssert.AreEqual(new[] { "Top", "Skirt" }, OutfitToggleGenerator.ItemParts(item).Select(part => part.name));

                var read = OutfitToggleGenerator.Sliders(avatar, item);
                CollectionAssert.AreEqual(new[] { 25f, 0f }, read.Select(slider => slider.value));
                CollectionAssert.AreEqual(new[] { "Body", "Sailor Dress/Skirt" }, read.Select(slider => slider.mesh));

                // Reapplying keeps saved parameter names so players keep their in-game values.
                var names = parameters.Select(p => p.nameOrPrefix).ToList();
                edit.sliders[0].value = 60;
                DeleteControllers();
                OutfitToggleGenerator.ApplyPartsEdit(avatar, item, edit);
                var again = item.GetComponentInChildren<ModularAvatarParameters>(true).parameters;
                CollectionAssert.AreEqual(names, again.Select(p => p.nameOrPrefix));
                Assert.AreEqual(0.6f, again[0].defaultValue, 0.0001f);

                DeleteControllers();
                edit.sliders.Clear();
                OutfitToggleGenerator.ApplyPartsEdit(avatar, item, edit);
                Assert.IsNull(item.GetComponentInChildren<ModularAvatarMenuInstaller>(true));
            }
            finally { DeleteControllers(); }
        }

        [Test] public void SliderValidationRejectsConflictsDuplicatesAndTooMany()
        {
            var conflict = Edit(0, true, new WardrobeShapeEdit { mesh = "Body", shape = "Shrink_Chest", value = 50 });
            conflict.sliders = new List<WardrobeSliderEdit> { Slider("Body", "Shrink_Chest", 0) };
            Assert.Throws<ArgumentException>(() => OutfitToggleGenerator.ValidatePartsEdit(avatar, item, conflict));

            var duplicate = Edit(0, true);
            duplicate.sliders = new List<WardrobeSliderEdit> { Slider("Body", "Chest_Big", 0), Slider("Body", "Chest_Big", 10) };
            Assert.Throws<ArgumentException>(() => OutfitToggleGenerator.ValidatePartsEdit(avatar, item, duplicate));

            var missing = Edit(0, true);
            missing.sliders = new List<WardrobeSliderEdit> { Slider("Sailor Dress/Top", "Missing", 0) };
            Assert.Throws<ArgumentException>(() => OutfitToggleGenerator.ValidatePartsEdit(avatar, item, missing));

            var many = Edit(0, true);
            many.sliders = Enumerable.Range(0, OutfitToggleGenerator.MaxSliders + 1).Select(i => Slider("Body", "Chest_Big", i)).ToList();
            Assert.Throws<ArgumentException>(() => OutfitToggleGenerator.ValidatePartsEdit(avatar, item, many));
        }
    }
}
