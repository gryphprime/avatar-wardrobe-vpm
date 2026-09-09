using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

namespace OutfitToggleGenerator
{
    public sealed class AppearanceEditorTests
    {
        private Scene scene, previousScene;
        private VRCAvatarDescriptor avatar, previousAvatar;
        private GameObject root;
        private MeshRenderer first, second;
        private Material source;
        private string folder, presets, overrides, uploads;
        private readonly List<string> generated = new List<string>();

        [SetUp] public void SetUp()
        {
            previousScene = WardrobeTestSceneFixture.RequireSavedActiveScene(); previousAvatar = AvatarWardrobeServer.SceneAvatar;
            presets = AvatarWardrobePresets.CaptureSettings(); overrides = AvatarWardrobeCatalog.CaptureOverrides(); uploads = ShiroTools.OutfitProjectData.CaptureSettings();
            folder = "Assets/AppearanceFixture_" + Guid.NewGuid().ToString("N"); System.IO.Directory.CreateDirectory(folder);
            scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            root = new GameObject("Appearance fixture"); SceneManager.MoveGameObjectToScene(root, scene);
            avatar = root.AddComponent<VRCAvatarDescriptor>(); root.AddComponent<Animator>(); AvatarWardrobeServer.SceneAvatar = avatar;
            source = new Material(Shader.Find("Standard")) { name = "Creator material", color = Color.white };
            AssetDatabase.CreateAsset(source, folder + "/Original.mat"); AssetDatabase.SaveAssetIfDirty(source);
            first = Child("Same name").AddComponent<MeshRenderer>(); second = Child("Same name").AddComponent<MeshRenderer>();
            first.sharedMaterials = new[] { source, source }; second.sharedMaterial = source;
            EditorSceneManager.SaveScene(scene, folder + "/Scene.unity");
        }
        [TearDown] public void TearDown()
        {
            Undo.ClearAll(); AvatarWardrobeServer.SceneAvatar = previousAvatar;
            if (previousScene.IsValid()) SceneManager.SetActiveScene(previousScene);
            if (scene.IsValid()) EditorSceneManager.CloseScene(scene, true);
            foreach (var path in generated) AssetDatabase.DeleteAsset(path); generated.Clear();
            if (!string.IsNullOrEmpty(folder)) AssetDatabase.DeleteAsset(folder);
            AvatarWardrobePresets.RestoreSettings(presets); AvatarWardrobeCatalog.RestoreOverrides(overrides); ShiroTools.OutfitProjectData.RestoreSettings(uploads);
        }
        private GameObject Child(string name) { var go = new GameObject(name); go.transform.SetParent(root.transform, false); return go; }
        private WardrobeAppearanceEditor.CommandDto Command()
        {
            var snapshot = WardrobeAppearanceEditor.Snapshot(avatar); Assert.AreEqual(1, snapshot.ok, snapshot.message);
            return new WardrobeAppearanceEditor.CommandDto { action = "material", avatarId = avatar.GetInstanceID(), rendererId = first.GetInstanceID(), slot = 1,
                materialId = source.GetInstanceID(), revision = snapshot.revision, property = "_Color", kind = "color", color = new WardrobeAppearanceEditor.ColorDto(Color.red) };
        }
        private WardrobeAppearanceEditor.ApplyDto Review(WardrobeAppearanceEditor.CommandDto command)
        {
            var result = WardrobeAppearanceEditor.Review(command); Assert.AreEqual(1, result.ok, result.message);
            return new WardrobeAppearanceEditor.ApplyDto { avatarId = avatar.GetInstanceID(), token = result.token };
        }
        private AvatarWardrobeServer.ResultDto Apply(WardrobeAppearanceEditor.ApplyDto request)
        {
            var result = WardrobeAppearanceEditor.Apply(request); if (!string.IsNullOrEmpty(result.id)) generated.Add(result.id); return result;
        }
        [Test] public void ReviewIsReadOnlyAndDoesNotCreateMaterialAssets()
        {
            var json = EditorJsonUtility.ToJson(source); var materials = AssetDatabase.FindAssets("t:Material");
            Review(Command());
            Assert.AreEqual(json, EditorJsonUtility.ToJson(source)); CollectionAssert.AreEquivalent(materials, AssetDatabase.FindAssets("t:Material"));
            Assert.AreEqual(source, first.sharedMaterials[1]); Assert.AreEqual(presets, AvatarWardrobePresets.CaptureSettings());
        }
        [Test] public void ApplyClonesOnlyExactSlotAndUndoRedoPreserveSharedSource()
        {
            var json = EditorJsonUtility.ToJson(source);
            var result = Apply(Review(Command())); Assert.AreEqual(1, result.ok, result.message);
            var copy = first.sharedMaterials[1]; Assert.AreNotEqual(source, copy); Assert.AreEqual(Color.red, copy.color);
            Assert.AreEqual(source, first.sharedMaterials[0]); Assert.AreEqual(source, second.sharedMaterial); Assert.AreEqual(json, EditorJsonUtility.ToJson(source));
            Assert.IsTrue(AssetDatabase.GetAssetPath(copy).StartsWith("Assets/AvatarWardrobeGenerated/Appearance/"));
            Undo.PerformUndo(); Assert.AreEqual(source, first.sharedMaterials[1]); Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(result.id));
            Undo.PerformRedo(); Assert.AreEqual(copy, first.sharedMaterials[1]); Assert.AreEqual(Color.red, copy.color);
            Assert.AreEqual(presets, AvatarWardrobePresets.CaptureSettings()); Assert.AreEqual(uploads, ShiroTools.OutfitProjectData.CaptureSettings());
        }
        [Test] public void ChangedSharedMaterialInvalidatesReviewAndTokensCannotReplay()
        {
            var request = Review(Command()); source.color = Color.green;
            Assert.AreEqual(0, Apply(request).ok); Assert.AreEqual(source, first.sharedMaterials[1]);
            source.color = Color.white; Assert.AreEqual(0, Apply(request).ok);
            request = Review(Command()); Assert.AreEqual(1, Apply(request).ok); Assert.AreEqual(0, Apply(request).ok);
        }
        [Test] public void WrongRendererSlotIdentityAndUnsafePropertyAreRejected()
        {
            var command = Command(); command.slot = 5; Assert.AreEqual(0, WardrobeAppearanceEditor.Review(command).ok);
            command = Command(); command.materialId++; Assert.AreEqual(0, WardrobeAppearanceEditor.Review(command).ok);
            command = Command(); command.rendererId = root.GetInstanceID(); Assert.AreEqual(0, WardrobeAppearanceEditor.Review(command).ok);
            command = Command(); command.property = "_Mode"; command.kind = "float"; Assert.AreEqual(0, WardrobeAppearanceEditor.Review(command).ok);
            command = Command(); command.color.r = float.NaN; Assert.AreEqual(0, WardrobeAppearanceEditor.Review(command).ok);
            Assert.AreEqual(source, first.sharedMaterials[1]);
        }
        [Test] public void TextureReplacementPreservesOtherSlotsAndOriginalTexture()
        {
            var original = new Texture2D(2, 2); var replacement = new Texture2D(4, 4);
            AssetDatabase.CreateAsset(original, folder + "/OriginalTexture.asset"); AssetDatabase.CreateAsset(replacement, folder + "/ReplacementTexture.asset"); source.mainTexture = original;
            var command = Command(); command.property = "_MainTex"; command.kind = "texture"; command.textureGuid = AssetDatabase.AssetPathToGUID(folder + "/ReplacementTexture.asset");
            var result = Apply(Review(command)); Assert.AreEqual(1, result.ok, result.message);
            Assert.AreEqual(replacement, first.sharedMaterials[1].mainTexture); Assert.AreEqual(original, source.mainTexture); Assert.AreEqual(original, second.sharedMaterial.mainTexture);
        }
        [Test] public void StaticShapeTargetsExactRendererAndSupportsUndo()
        {
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
            mesh.AddBlendShapeFrame("Smile", 100, new[] { Vector3.up, Vector3.zero, Vector3.zero }, new Vector3[3], new Vector3[3]);
            AssetDatabase.CreateAsset(mesh, folder + "/Shape.asset");
            var a = Child("Face").AddComponent<SkinnedMeshRenderer>(); var b = Child("Face").AddComponent<SkinnedMeshRenderer>(); a.sharedMesh = mesh; b.sharedMesh = mesh;
            var command = Command(); command.action = "blendshape"; command.rendererId = b.GetInstanceID(); command.shapeIndex = 0; command.shapeName = "Smile"; command.value = 42;
            var result = Apply(Review(command)); Assert.AreEqual(1, result.ok, result.message); Assert.AreEqual(0, a.GetBlendShapeWeight(0)); Assert.AreEqual(42, b.GetBlendShapeWeight(0));
            Undo.PerformUndo(); Assert.AreEqual(0, b.GetBlendShapeWeight(0));
            command = Command(); command.action = "blendshape"; command.rendererId = b.GetInstanceID(); command.shapeIndex = 0; command.shapeName = "Other"; command.value = 42;
            Assert.AreEqual(0, WardrobeAppearanceEditor.Review(command).ok);
        }
        [Test] public void DefaultOptimizerPreviewUsesCopiesAndReviewedApplySupportsUndo()
        {
            var tool = WardrobeAppearanceTools.Snapshot(avatar).tools.Single(x => x.id == "optimizer");
            if (!tool.available) Assert.Ignore("Install AAO 1.9.18 in the isolated fixture to run the optional public-adapter integration test.");
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube); cube.transform.SetParent(root.transform, false); cube.GetComponent<Renderer>().sharedMaterial = source;
            first.gameObject.AddComponent<MeshFilter>().sharedMesh = cube.GetComponent<MeshFilter>().sharedMesh;
            second.gameObject.AddComponent<MeshFilter>().sharedMesh = cube.GetComponent<MeshFilter>().sharedMesh;
            var before = WardrobeAppearanceEditor.Snapshot(avatar); var materialJson = EditorJsonUtility.ToJson(source);
            var preview = WardrobeAppearanceTools.ReviewOptimizer(new WardrobeAppearanceTools.CommandDto { avatarId = avatar.GetInstanceID(), revision = before.revision, id = "optimizer" });
            Assert.AreEqual(1, preview.ok, preview.message); Assert.IsNotEmpty(preview.beforeImage); Assert.IsNotEmpty(preview.afterImage);
            Assert.IsNotNull(preview.preview.beforeMetrics); Assert.IsNotNull(preview.preview.afterMetrics);
            Assert.AreEqual(before.revision, WardrobeAppearanceEditor.Snapshot(avatar).revision); Assert.AreEqual(materialJson, EditorJsonUtility.ToJson(source));
            var result = WardrobeAppearanceTools.ApplyOptimizer(new WardrobeAppearanceEditor.ApplyDto { avatarId = avatar.GetInstanceID(), token = preview.token });
            Assert.AreEqual(1, result.ok, result.message); Assert.IsTrue(root.GetComponents<Component>().Any(x => x.GetType().FullName == "Anatawa12.AvatarOptimizer.TraceAndOptimize"));
            Undo.PerformUndo(); Assert.IsFalse(root.GetComponents<Component>().Any(x => x.GetType().FullName == "Anatawa12.AvatarOptimizer.TraceAndOptimize"));
        }
        [Test] public void GestureManagerPublicFavouritePinsExactAvatarAndSupportsUndo()
        {
            var tool = WardrobeAppearanceTools.Snapshot(avatar).tools.Single(x => x.id == "gesture");
            if (!tool.available) Assert.Ignore("Install Gesture Manager 3.9.9 in the isolated fixture to test its public target setting.");
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("BlackStartX.GestureManager.GestureManager", false)).First(x => x != null);
            var manager = Child("Gesture fixture").AddComponent(type);
            var settingsField = type.GetField("settings"); var settings = settingsField.GetValue(manager) ?? Activator.CreateInstance(settingsField.FieldType);
            settingsField.SetValue(manager, settings);
            var favourite = settingsField.FieldType.GetField("favourite"); var userIndex = settingsField.FieldType.GetField("userIndex"); userIndex.SetValue(settings, 7);
            WardrobeAppearanceTools.PinGestureFavourite(manager, avatar);
            Assert.AreEqual(avatar, favourite.GetValue(settingsField.GetValue(manager))); Assert.AreEqual(7, userIndex.GetValue(settingsField.GetValue(manager)));
            Undo.FlushUndoRecordObjects(); Undo.PerformUndo(); Assert.IsNull(favourite.GetValue(settingsField.GetValue(manager))); Assert.AreEqual(7, userIndex.GetValue(settingsField.GetValue(manager)));
        }
        [Test] public void GestureNativeHandoffPreservesAnotherAvatarEmulator()
        {
            var tool = WardrobeAppearanceTools.Snapshot(avatar).tools.Single(x => x.id == "gesture");
            if (!tool.available) Assert.Ignore("Install Gesture Manager 3.9.9 with its VRChat SDK target contract in the isolated fixture.");
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("BlackStartX.GestureManager.GestureManager", false)).First(x => x != null);
            var other = new GameObject("Other avatar"); SceneManager.MoveGameObjectToScene(other, scene); var otherAvatar = other.AddComponent<VRCAvatarDescriptor>();
            var managerRoot = new GameObject("Existing emulator"); SceneManager.MoveGameObjectToScene(managerRoot, scene); var existing = managerRoot.AddComponent(type);
            WardrobeAppearanceTools.PinGestureFavourite(existing, otherAvatar); Undo.ClearAll();
            var settings = type.GetField("settings"); var favourite = settings.FieldType.GetField("favourite");
            var before = WardrobeAppearanceEditor.Snapshot(avatar);
            var result = WardrobeAppearanceTools.Execute(new WardrobeAppearanceTools.CommandDto { avatarId = avatar.GetInstanceID(), revision = before.revision, id = "gesture" });
            Assert.AreEqual(1, result.ok, result.message); Assert.IsFalse(EditorApplication.isPlaying);
            Assert.AreEqual(otherAvatar, favourite.GetValue(settings.GetValue(existing)));
            var created = scene.GetRootGameObjects().SelectMany(go => go.GetComponentsInChildren(type, true)).Single(x => x != existing);
            Assert.AreEqual(avatar, favourite.GetValue(settings.GetValue(created)));
            Undo.PerformUndo(); Assert.IsTrue(created == null); Assert.AreEqual(otherAvatar, favourite.GetValue(settings.GetValue(existing)));
        }
        [Test] public void OptionalToolsAreVersionGatedAndCannotInstallImplicitly()
        {
            var before = WardrobeAppearanceEditor.Snapshot(avatar).revision;
            var tools = WardrobeAppearanceTools.Snapshot(avatar);
            Assert.AreEqual(1, tools.ok);
            foreach (var tool in tools.tools.Where(x => x.available))
                Assert.IsTrue((tool.id == "optimizer" && tool.version == "1.9.18") || (tool.id == "gesture" && tool.version == "3.9.9") || (tool.id == "fitting" && tool.installed));
            var rejected = WardrobeAppearanceTools.Execute(new WardrobeAppearanceTools.CommandDto { avatarId = avatar.GetInstanceID(), revision = before, id = "textrans" });
            Assert.AreEqual(0, rejected.ok); Assert.AreEqual(before, WardrobeAppearanceEditor.Snapshot(avatar).revision);
        }
        [Test] public void MochiHandoffRequiresItsExactPublicNativeMenuContract()
        {
            Assert.IsFalse(WardrobeAppearanceTools.MochiMenuContract(null));
            Assert.IsFalse(WardrobeAppearanceTools.MochiMenuContract(typeof(EditorWindow)));
            Assert.IsFalse(WardrobeAppearanceTools.MochiMenuContract(typeof(GameObject)));
            var type = AppDomain.CurrentDomain.GetAssemblies().Where(assembly => assembly.GetName().Name == "OutfitRetargetingSystem")
                .Select(assembly => assembly.GetType("OutfitRetargetingSystem", false)).FirstOrDefault(value => value != null);
            var tool = WardrobeAppearanceTools.Snapshot(avatar).tools.Single(value => value.id == "fitting");
            Assert.AreEqual(type != null, tool.installed); Assert.AreEqual(WardrobeAppearanceTools.MochiMenuContract(type), tool.available);
            Assert.IsFalse(tool.requiresReview); Assert.IsTrue(string.IsNullOrEmpty(tool.version));
        }
        [Test] public void UnsupportedShaderIsReadOnlyAndCannotBeApplied()
        {
            source.shader = Shader.Find("Unlit/Color");
            var snapshot = WardrobeAppearanceEditor.Snapshot(avatar); Assert.AreEqual(1, snapshot.ok, snapshot.message);
            Assert.IsNotEmpty(snapshot.renderers.Single(x => x.id == first.GetInstanceID()).slots[1].reason);
            Assert.AreEqual(0, WardrobeAppearanceEditor.Review(Command()).ok);
        }
    }
}
