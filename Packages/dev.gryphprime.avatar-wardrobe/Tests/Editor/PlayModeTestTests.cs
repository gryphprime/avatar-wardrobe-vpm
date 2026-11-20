using System;
using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using VRC.SDK3.Avatars.Components;

namespace OutfitToggleGenerator
{
    public sealed class PlayModeTestTests
    {
        private Scene previousScene, scene;
        private VRCAvatarDescriptor previousAvatar, avatar;
        private string folder, settings, owner, stagingPath;
        private bool optionsEnabled;
        private EnterPlayModeOptions options;

        [SetUp] public void SetUp()
        {
            previousScene = WardrobeTestSceneFixture.RequireSavedActiveScene();
            previousAvatar = AvatarWardrobeServer.SceneAvatar;
            settings = AvatarWardrobePresets.CaptureSettings();
            optionsEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            options = EditorSettings.enterPlayModeOptions;
            folder = "Assets/PlayModeTestFixture_" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(folder);
            scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.SetActiveScene(scene);
            var root = new GameObject("PlayTest avatar", typeof(Animator), typeof(VRCAvatarDescriptor));
            var outfits = new GameObject("Outfits");
            outfits.transform.SetParent(root.transform, false);
            foreach (var name in new[] { "First", "Second" })
            {
                var holder = new GameObject(name);
                holder.transform.SetParent(outfits.transform, false);
                // A renderer keeps MA's unused-object GC from pruning it in Play Mode.
                new GameObject(name + " coat", typeof(MeshFilter), typeof(MeshRenderer)).transform.SetParent(holder.transform, false);
            }
            avatar = root.GetComponent<VRCAvatarDescriptor>();
            EditorSceneManager.SaveScene(scene, folder + "/Scene.unity");
            AvatarWardrobeServer.SceneAvatar = avatar;
            AvatarWardrobePresets.CurrentBase(out owner, out _);
            // The staging scene must live under WardrobeUploads for upload keys; use a unique folder.
            stagingPath = "Assets/Generated/WardrobeUploads/PlayModeTest_" + Guid.NewGuid().ToString("N") + "/Upload.unity";
            string Preset(string id, string name) =>
                "{\"id\":\"" + id + "\",\"name\":\"" + name + "\",\"baseKey\":\"" + owner + "\",\"baseName\":\"PlayTest avatar\"," +
                "\"avatarRootName\":\"PlayTest_Wardrobe_" + id + "\",\"outfitName\":\"" + name + "\",\"scenePath\":\"" + stagingPath + "\"," +
                "\"legacyPath\":\"Outfits/" + name + "\"}";
            AvatarWardrobePresets.RestoreSettings("{\"presets\":[" + Preset("first", "First") + "," + Preset("second", "Second") + "],\"assignments\":[]}");
        }

        [UnityTearDown] public IEnumerator TearDown()
        {
            if (EditorApplication.isPlaying) yield return new ExitPlayMode();
            WardrobePlayModeTest.Restore();
            EditorSettings.enterPlayModeOptionsEnabled = optionsEnabled;
            EditorSettings.enterPlayModeOptions = options;
            AvatarWardrobeServer.SceneAvatar = previousAvatar;
            if (previousScene.IsValid()) SceneManager.SetActiveScene(previousScene);
            var staging = SceneManager.GetSceneByPath(stagingPath);
            if (staging.IsValid() && staging.isLoaded) EditorSceneManager.CloseScene(staging, true);
            var loaded = SceneManager.GetSceneByPath(folder + "/Scene.unity");
            if (loaded.IsValid()) EditorSceneManager.CloseScene(loaded, true);
            AssetDatabase.DeleteAsset(folder);
            AssetDatabase.DeleteAsset(Path.GetDirectoryName(stagingPath).Replace('\\', '/'));
            AvatarWardrobePresets.RestoreSettings(settings);
        }

        private static GameObject StagedRoot(string path) =>
            SceneManager.GetSceneByPath(path).GetRootGameObjects().Single(go => go.GetComponent<VRCAvatarDescriptor>() != null);

        [UnityTest] public IEnumerator StagesOnlyTheSelectedPresetAndRestoresTheScene()
        {
            var result = WardrobePlayModeTest.Start("first", false);
            Assert.AreEqual(1, result.ok, result.message);
            Assert.IsTrue(WardrobePlayModeTest.Active);
            Assert.IsFalse(avatar.gameObject.activeSelf, "The source avatar is hidden during the test.");
            var staged = StagedRoot(stagingPath);
            Assert.AreEqual("PlayTest_Wardrobe_first", staged.name);
            Assert.IsTrue(staged.activeSelf);
            var outfits = staged.transform.Find("Outfits");
            Assert.AreEqual(new[] { "First" }, outfits.Cast<Transform>().Select(t => t.name).ToArray());
            Assert.IsNotNull(outfits.Find("First/First coat"));
            Assert.AreEqual("Untagged", outfits.Find("First").tag);

            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
            yield return new EnterPlayMode(false);
            var runtime = StagedRoot(stagingPath);
            var tree = string.Join(", ", runtime.GetComponentsInChildren<Transform>(true).Select(t => AnimationUtility.CalculateTransformPath(t, runtime.transform)));
            Assert.IsNotNull(runtime.transform.Find("Outfits/First/First coat"), "Runtime hierarchy: " + tree);
            Assert.IsNull(runtime.transform.Find("Outfits/Second/Second coat"), "Runtime hierarchy: " + tree);
            var source = SceneManager.GetSceneByPath(folder + "/Scene.unity").GetRootGameObjects().Single(go => go.name == "PlayTest avatar");
            Assert.IsFalse(source.activeSelf, "Only the staged avatar is active in Play Mode.");

            yield return new ExitPlayMode();
            for (int i = 0; i < 5 && WardrobePlayModeTest.Active; i++) yield return null;
            Assert.IsFalse(WardrobePlayModeTest.Active);
            var staging = SceneManager.GetSceneByPath(stagingPath);
            Assert.IsFalse(staging.IsValid() && staging.isLoaded, "The staging scene is closed after Play Mode.");
            source = SceneManager.GetSceneByPath(folder + "/Scene.unity").GetRootGameObjects().Single(go => go.name == "PlayTest avatar");
            Assert.IsTrue(source.activeSelf, "The source avatar is restored.");
            Assert.AreEqual(2, source.transform.Find("Outfits").childCount, "The source keeps every preset folder.");
        }

        [Test] public void RefusesAPresetOfAnotherAvatar()
        {
            var other = new GameObject("Other", typeof(Animator), typeof(VRCAvatarDescriptor)).GetComponent<VRCAvatarDescriptor>();
            SceneManager.MoveGameObjectToScene(other.gameObject, scene);
            AvatarWardrobeServer.SceneAvatar = other;
            var result = WardrobePlayModeTest.Start("first", false);
            Assert.AreEqual(0, result.ok);
            Assert.IsFalse(WardrobePlayModeTest.Active);
            Assert.IsTrue(avatar.gameObject.activeSelf);
        }
    }
}
