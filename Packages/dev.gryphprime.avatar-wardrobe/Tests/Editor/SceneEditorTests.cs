using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;

namespace OutfitToggleGenerator
{
    public class SceneEditorTests
    {
        private Scene scene, previous;
        private VRCAvatarDescriptor avatar, previousAvatar;
        private GameObject root;
        private UnityEngine.Object previousSelection;
        private string presets, overrides, uploads, folder;

        [SetUp] public void SetUp()
        {
            previous = SceneManager.GetActiveScene();
            previousAvatar = AvatarWardrobeServer.SceneAvatar;
            previousSelection = Selection.activeObject;
            presets = AvatarWardrobePresets.CaptureSettings();
            overrides = AvatarWardrobeCatalog.CaptureOverrides();
            uploads = ShiroTools.OutfitProjectData.CaptureSettings();
            scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            root = new GameObject("Advanced scene fixture");
            SceneManager.MoveGameObjectToScene(root, scene);
            avatar = root.AddComponent<VRCAvatarDescriptor>();
            AvatarWardrobeServer.SceneAvatar = avatar;
            folder = "Assets/SceneEditorFixture_" + Guid.NewGuid().ToString("N");
            System.IO.Directory.CreateDirectory(folder);
            EditorSceneManager.SaveScene(scene, folder + "/Scene.unity");
        }
        [TearDown] public void TearDown()
        {
            Undo.ClearAll();
            AvatarWardrobeServer.SceneAvatar = previousAvatar;
            Selection.activeObject = previousSelection;
            if (previous.IsValid()) SceneManager.SetActiveScene(previous);
            EditorSceneManager.CloseScene(scene, true);
            AssetDatabase.DeleteAsset(folder);
            AvatarWardrobePresets.RestoreSettings(presets);
            AvatarWardrobeCatalog.RestoreOverrides(overrides);
            ShiroTools.OutfitProjectData.RestoreSettings(uploads);
        }
        private GameObject Child(string name, GameObject parent = null)
        {
            var child = new GameObject(name); child.transform.SetParent((parent ?? root).transform, false); return child;
        }
        private WardrobeSceneEditor.CommandDto Command(string action, GameObject target)
        {
            var snapshot = WardrobeSceneEditor.Snapshot(avatar);
            Assert.AreEqual(1, snapshot.ok, snapshot.message);
            return new WardrobeSceneEditor.CommandDto { action = action, objectId = target.GetInstanceID(), avatarId = avatar.GetInstanceID(), revision = snapshot.revision };
        }
        private void Success(WardrobeSceneEditor.CommandDto command)
        {
            var result = WardrobeSceneEditor.Execute(command); Assert.AreEqual(1, result.ok, result.message);
        }

        [Test] public void SnapshotDoesNotChangeSceneOrPresetStore()
        {
            var child = Child("Object"); child.SetActive(false);
            var first = WardrobeSceneEditor.Snapshot(avatar);
            var second = WardrobeSceneEditor.Snapshot(avatar);
            Assert.AreEqual(1, first.ok, first.message);
            Assert.AreEqual(first.revision, second.revision);
            Assert.AreEqual(2, second.objects.Count);
            Assert.IsFalse(second.objects.Single(x => x.id == child.GetInstanceID()).active);
            Assert.AreEqual(presets, AvatarWardrobePresets.CaptureSettings());
        }
        [Test] public void ExactDuplicateNamesAreEditedAndDeletedIndependently()
        {
            var first = Child("Copy"); var second = Child("Copy"); var id = second.GetInstanceID();
            Selection.activeGameObject = first;
            Success(Command("delete", second));
            Assert.IsNotNull(first); Assert.IsNull(EditorUtility.InstanceIDToObject(id));
            Undo.PerformUndo();
            Assert.AreEqual(2, root.transform.childCount);
            Assert.AreSame(first, Selection.activeGameObject);
        }
        [Test] public void StaleRevisionAndForeignAvatarCannotWrite()
        {
            var child = Child("Before"); var command = Command("rename", child); command.name = "After";
            child.transform.localPosition = Vector3.right;
            Assert.AreEqual(0, WardrobeSceneEditor.Execute(command).ok); Assert.AreEqual("Before", child.name);
            command = Command("rename", child); command.name = "After"; command.avatarId++;
            Assert.AreEqual(0, WardrobeSceneEditor.Execute(command).ok); Assert.AreEqual("Before", child.name);
        }
        [Test] public void NativeComponentEditInvalidatesOldRevision()
        {
            var child = Child("Light"); var light = child.AddComponent<Light>();
            var command = Command("active", child); command.active = false;
            light.intensity = 7;
            Assert.AreEqual(0, WardrobeSceneEditor.Execute(command).ok); Assert.IsTrue(child.activeSelf);
        }
        [Test] public void DuplicatePreservesComponentStateAndUndoRemovesOnlyNewCopy()
        {
            var child = Child("Light"); var light = child.AddComponent<Light>(); light.intensity = 6; light.enabled = false;
            var collider = child.AddComponent<BoxCollider>(); collider.isTrigger = true;
            var result = WardrobeSceneEditor.Execute(Command("duplicate", child)); Assert.AreEqual(1, result.ok, result.message);
            var copy = EditorUtility.InstanceIDToObject(int.Parse(result.id)) as GameObject;
            Assert.IsNotNull(copy); Assert.AreNotSame(child, copy);
            Assert.AreEqual("Light Copy", copy.name); Assert.AreEqual(6, copy.GetComponent<Light>().intensity);
            Assert.IsFalse(copy.GetComponent<Light>().enabled); Assert.IsTrue(copy.GetComponent<BoxCollider>().isTrigger);
            Undo.PerformUndo(); Assert.IsTrue(child != null); Assert.AreEqual(1, root.transform.childCount);
        }
        [Test] public void ReparentRejectsCyclesOutsideObjectsAndRootDeletion()
        {
            var parent = Child("Parent"); var child = Child("Child", parent);
            var cycle = Command("reparent", parent); cycle.parentId = child.GetInstanceID();
            Assert.AreEqual(0, WardrobeSceneEditor.Execute(cycle).ok); Assert.AreSame(root.transform, parent.transform.parent);
            foreach (var action in new[] {"delete", "duplicate", "reparent"}) Assert.AreEqual(0, WardrobeSceneEditor.Execute(Command(action, root)).ok);
            var outside = new GameObject("Outside");
            try
            {
                var command = Command("reparent", child); command.parentId = outside.GetInstanceID();
                Assert.AreEqual(0, WardrobeSceneEditor.Execute(command).ok); Assert.AreSame(parent.transform, child.transform.parent);
            }
            finally { UnityEngine.Object.DestroyImmediate(outside); }
        }
        [Test] public void RenameCollisionAndInvalidTransformLeaveStateUntouched()
        {
            Child("Occupied"); var child = Child("Original");
            var rename = Command("rename", child); rename.name = "Occupied";
            Assert.AreEqual(0, WardrobeSceneEditor.Execute(rename).ok); Assert.AreEqual("Original", child.name);
            var transform = Command("transform", child);
            transform.position = new WardrobeSceneEditor.VectorDto(Vector3.one);
            transform.rotation = new WardrobeSceneEditor.VectorDto(Vector3.zero);
            transform.scale = new WardrobeSceneEditor.VectorDto(new Vector3(float.NaN, 1, 1));
            Assert.AreEqual(0, WardrobeSceneEditor.Execute(transform).ok); Assert.AreEqual(Vector3.zero, child.transform.localPosition);
        }
        [Test] public void CreateRenameReparentAndTransformSupportUndoWithoutPresetMigration()
        {
            var parent = Child("Destination"); var create = Command("create", root); create.name = "New child";
            var result = WardrobeSceneEditor.Execute(create); Assert.AreEqual(1, result.ok, result.message);
            var child = EditorUtility.InstanceIDToObject(int.Parse(result.id)) as GameObject;
            var reparent = Command("reparent", child); reparent.parentId = parent.GetInstanceID(); Success(reparent);
            var edit = Command("transform", child);
            edit.position = new WardrobeSceneEditor.VectorDto(new Vector3(1, 2, 3)); edit.rotation = new WardrobeSceneEditor.VectorDto(Vector3.zero); edit.scale = new WardrobeSceneEditor.VectorDto(Vector3.one);
            Success(edit); Undo.FlushUndoRecordObjects(); Undo.PerformUndo();
            Assert.AreEqual(Vector3.zero, child.transform.localPosition);
            Undo.PerformUndo(); Assert.AreSame(root.transform, child.transform.parent);
            Assert.AreEqual(presets, AvatarWardrobePresets.CaptureSettings());
        }
        [Test] public void SupportedSerializedPrimitiveUsesExactComponentAndUndo()
        {
            var first = Child("Light"); var light = first.AddComponent<Light>(); light.intensity = 1;
            var second = Child("Light"); var other = second.AddComponent<Light>(); other.intensity = 2;
            var snapshot = WardrobeSceneEditor.Snapshot(avatar);
            var properties = snapshot.objects.Single(x => x.id == first.GetInstanceID()).components.Single(x => x.id == light.GetInstanceID()).properties;
            var intensity = properties.Single(x => x.path == "m_Intensity"); Assert.IsTrue(intensity.editable);
            var command = Command("property", first); command.componentId = light.GetInstanceID(); command.propertyPath = intensity.path; command.value = "4.5";
            Success(command); Assert.AreEqual(4.5f, light.intensity); Assert.AreEqual(2, other.intensity);
            Undo.FlushUndoRecordObjects(); Undo.PerformUndo(); Assert.AreEqual(1, light.intensity);
            command = Command("property", first); command.componentId = other.GetInstanceID(); command.propertyPath = intensity.path; command.value = "9";
            Assert.AreEqual(0, WardrobeSceneEditor.Execute(command).ok); Assert.AreEqual(2, other.intensity);
        }
        [Test] public void SourcePrefabAndBuildLockAreRejected()
        {
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, folder + "/Source.prefab");
            Assert.AreEqual(0, WardrobeSceneEditor.Snapshot(prefab.GetComponent<VRCAvatarDescriptor>()).ok);
            var command = Command("active", root); command.active = false;
            using (AvatarWardrobeServer.LockUploadTarget()) Assert.AreEqual(0, WardrobeSceneEditor.Execute(command).ok);
            Assert.IsTrue(root.activeSelf);
        }
    }
}
