using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using VRC.SDK3.Avatars.Components;

namespace OutfitToggleGenerator
{
    public sealed class PresetPlayModeTests
    {
        private Scene previousScene, scene;
        private VRCAvatarDescriptor previousAvatar, avatar;
        private string folder, settings, owner;
        private bool optionsEnabled;
        private EnterPlayModeOptions options;

        [SetUp] public void SetUp()
        {
            previousScene = WardrobeTestSceneFixture.RequireSavedActiveScene();
            previousAvatar = AvatarWardrobeServer.SceneAvatar;
            settings = AvatarWardrobePresets.CaptureSettings();
            optionsEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            options = EditorSettings.enterPlayModeOptions;
            folder = "Assets/PresetPlayModeFixture_" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(folder);
            scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            var source = new GameObject("Preset avatar", typeof(Animator), typeof(VRCAvatarDescriptor));
            SceneManager.MoveGameObjectToScene(source, scene);
            var prefab = PrefabUtility.SaveAsPrefabAsset(source, folder + "/Avatar.prefab");
            UnityEngine.Object.DestroyImmediate(source);
            avatar = ((GameObject)PrefabUtility.InstantiatePrefab(prefab, scene)).GetComponent<VRCAvatarDescriptor>();
            avatar.gameObject.name = "Preset avatar";
            EditorSceneManager.SaveScene(scene, folder + "/Scene.unity");
            AvatarWardrobeServer.SceneAvatar = avatar;
            AvatarWardrobePresets.CurrentBase(out owner, out _);
            AvatarWardrobePresets.RestoreSettings("{\"presets\":[{\"id\":\"first\",\"name\":\"First\",\"baseKey\":\"" + owner + "\"}],\"assignments\":[{\"guid\":\"coat\",\"target\":\"first\",\"baseKey\":\"" + owner + "\"}]}");
        }

        [UnityTearDown] public IEnumerator TearDown()
        {
            if (EditorApplication.isPlaying) yield return new ExitPlayMode();
            EditorSettings.enterPlayModeOptionsEnabled = optionsEnabled;
            EditorSettings.enterPlayModeOptions = options;
            AvatarWardrobeServer.SceneAvatar = previousAvatar;
            if (previousScene.IsValid()) SceneManager.SetActiveScene(previousScene);
            var loaded = SceneManager.GetSceneByPath(folder + "/Scene.unity");
            if (loaded.IsValid()) EditorSceneManager.CloseScene(loaded, true);
            AssetDatabase.DeleteAsset(folder);
            AvatarWardrobePresets.RestoreSettings(settings);
        }

        [Test] public void ReturningToEditModeDiscardsCachedRuntimePrefabIdentity()
        {
            var cache = (IDictionary)typeof(AvatarWardrobePresets).GetField("scopeLookups", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            var lookup = cache[avatar];
            lookup.GetType().GetField("key").SetValue(lookup, "scene-object:runtime-prefab-id");
            AvatarWardrobePresets.InvalidatePlayModeLookups(PlayModeStateChange.EnteredEditMode);
            AssertOwnerAndAssignments();
            var duplicate = UnityEngine.Object.Instantiate(avatar.gameObject).GetComponent<VRCAvatarDescriptor>();
            AvatarWardrobeServer.SceneAvatar = duplicate;
            AvatarWardrobePresets.CurrentBase(out var other, out _);
            Assert.AreNotEqual(owner, other);
            Assert.IsEmpty(AvatarWardrobePresets.PresetsForBase(other));
        }

        [UnityTest] public IEnumerator PrefabPresetsSurvivePlayModeWithoutDomainReload()
        {
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
            var before = AvatarWardrobePresets.CaptureSettings();
            yield return new EnterPlayMode(false);
            AvatarWardrobeServer.SceneAvatar = SceneManager.GetSceneByPath(folder + "/Scene.unity")
                .GetRootGameObjects().Single(go => go.name == "Preset avatar").GetComponent<VRCAvatarDescriptor>();
            AvatarWardrobePresets.CurrentBase(out var runtimeOwner, out _);
            Assert.AreEqual("none", runtimeOwner);
            AvatarWardrobePresets.MigrateCurrentOwner();
            Assert.AreEqual(before, AvatarWardrobePresets.CaptureSettings());
            yield return new ExitPlayMode();
            yield return null;
            avatar = SceneManager.GetSceneByPath(folder + "/Scene.unity")
                .GetRootGameObjects().Single(go => go.name == "Preset avatar").GetComponent<VRCAvatarDescriptor>();
            Assert.AreEqual(avatar, AvatarWardrobeServer.SceneAvatar, "The edit-mode pin must survive the runtime ID.");
            AssertOwnerAndAssignments();
            Assert.AreEqual(before, AvatarWardrobePresets.CaptureSettings());
        }

        private void AssertOwnerAndAssignments()
        {
            AvatarWardrobePresets.CurrentBase(out var restored, out _);
            Assert.AreEqual(owner, restored);
            Assert.AreEqual("first", AvatarWardrobePresets.PresetsForBase(restored).Single().id);
            Assert.AreEqual("first", AvatarWardrobePresets.GetAssignment("coat", restored));
        }
    }
}
