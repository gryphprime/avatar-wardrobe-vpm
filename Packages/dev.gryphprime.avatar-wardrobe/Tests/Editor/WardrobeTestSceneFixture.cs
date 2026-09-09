using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OutfitToggleGenerator
{
    internal static class WardrobeTestSceneFixture
    {
        internal const string MarkerName = ".wardrobe-test-fixture";
        internal const string MarkerValue = "Avatar Wardrobe isolated test fixture v1";

        internal static Scene RequireSavedActiveScene()
        {
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !string.IsNullOrEmpty(scene.path)) return scene;
            var project = Directory.GetParent(Application.dataPath).FullName;
            var marker = Path.Combine(project, MarkerName);
            if (!File.Exists(marker) || File.ReadAllText(marker).Trim() != MarkerValue)
                throw new InvalidOperationException("Save the active scene before running these tests, or run them in an explicitly marked disposable Wardrobe fixture. Tests never save an unmarked user's scene.");
            // The marker is provisioned by the fixture runner, never by these tests.
            // Keep the bootstrap scene alive across test classes so no loaded scene points at a deleted asset.
            var folder = "Assets/WardrobeTestBootstrap_" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(folder);
            AssetDatabase.Refresh();
            if (!EditorSceneManager.SaveScene(scene, folder + "/Previous.unity"))
                throw new InvalidOperationException("The disposable fixture's bootstrap scene could not be saved.");
            return scene;
        }
    }
}
