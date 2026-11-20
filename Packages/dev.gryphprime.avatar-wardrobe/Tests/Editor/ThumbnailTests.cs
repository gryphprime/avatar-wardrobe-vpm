using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using ShiroTools;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;

namespace OutfitToggleGenerator
{
    // Per-Look thumbnail plans, the crop math shared with the web editor, and
    // the renderers. Rendering cases need graphics (no -nographics).
    public sealed class ThumbnailTests
    {
        private Scene previousScene, scene;
        private string uploads;
        private readonly List<string> temps = new List<string>();

        [SetUp] public void SetUp()
        {
            previousScene = WardrobeTestSceneFixture.RequireSavedActiveScene();
            uploads = OutfitProjectData.CaptureSettings();
            scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.SetActiveScene(scene);
        }

        [TearDown] public void TearDown()
        {
            foreach (var path in temps) try { File.Delete(path); } catch { }
            temps.Clear();
            if (previousScene.IsValid()) SceneManager.SetActiveScene(previousScene);
            if (scene.IsValid() && scene.isLoaded) EditorSceneManager.CloseScene(scene, true);
            // Restore exactly what was there, including "no settings file".
            OutfitProjectData.RestoreSettings(uploads);
        }

        private static Color Pixel(byte[] png, float x, float y)
        {
            var tex = new Texture2D(2, 2);
            try
            {
                Assert.IsTrue(tex.LoadImage(png));
                Assert.AreEqual(OutfitBatchUploader.ThumbWidth, tex.width);
                Assert.AreEqual(OutfitBatchUploader.ThumbHeight, tex.height);
                // GetPixel's y starts at the bottom; callers pass y from the top.
                return tex.GetPixel(Mathf.RoundToInt(x * (tex.width - 1)), Mathf.RoundToInt((1f - y) * (tex.height - 1)));
            }
            finally { UnityEngine.Object.DestroyImmediate(tex); }
        }

        private static byte[] SplitImage(int width, int height, Color left, Color right)
        {
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                var pixels = new Color[width * height];
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++) pixels[y * width + x] = x < width / 2 ? left : right;
                tex.SetPixels(pixels);
                tex.Apply();
                return tex.EncodeToPNG();
            }
            finally { UnityEngine.Object.DestroyImmediate(tex); }
        }

        private static bool Near(Color a, Color b) => Mathf.Abs(a.r - b.r) < 0.08f && Mathf.Abs(a.g - b.g) < 0.08f && Mathf.Abs(a.b - b.b) < 0.08f;

        [Test] public void CropWindowCoversTheFrameAndClampsTheCenter()
        {
            float cx = 0.5f, cy = 0.5f;
            OutfitBatchUploader.ThumbCropWindow(2000, 1000, 1f, ref cx, ref cy, out var u, out var v);
            Assert.AreEqual(2f / 3f, u, 1e-4f, "A 2:1 image shows two thirds of its width at 4:3.");
            Assert.AreEqual(1f, v, 1e-4f);
            cx = 0f; cy = 0f;
            OutfitBatchUploader.ThumbCropWindow(2000, 1000, 2f, ref cx, ref cy, out u, out v);
            Assert.AreEqual(1f / 3f, u, 1e-4f);
            Assert.AreEqual(0.5f, v, 1e-4f);
            Assert.AreEqual(u / 2f, cx, 1e-4f, "The window never leaves the image.");
            Assert.AreEqual(v / 2f, cy, 1e-4f);
            cx = 0.5f; cy = 0.9f;
            OutfitBatchUploader.ThumbCropWindow(900, 1600, 1f, ref cx, ref cy, out u, out v);
            Assert.AreEqual(1f, u, 1e-4f, "A portrait image keeps its full width.");
            Assert.AreEqual((900f / 1600f) / (4f / 3f), v, 1e-4f);
            Assert.AreEqual(1f - v / 2f, cy, 1e-4f);
        }

        [Test] public void LooksFollowTheDefaultUntilTheyHaveTheirOwnSource()
        {
            var data = new OutfitProjectData.OutfitData { name = "Look" };
            OutfitProjectData.ThumbDefault.source = "camera";
            OutfitProjectData.ThumbDefault.camera = "cam";
            OutfitProjectData.ThumbDefault.update = "always";
            var plan = OutfitBatchUploader.EffectiveThumb(data);
            Assert.IsTrue(plan.inherited);
            Assert.AreEqual("camera", plan.source);
            Assert.AreEqual("cam", plan.camera);
            Assert.AreEqual("always", plan.update);
            data.thumb.source = "auto";
            data.thumb.update = "keep";
            plan = OutfitBatchUploader.EffectiveThumb(data);
            Assert.IsFalse(plan.inherited);
            Assert.AreEqual("auto", plan.source);
            Assert.AreEqual("keep", plan.update);
        }

        [Test] public void OldSettingsFilesLoadWithEmptyThumbnailPlans()
        {
            OutfitProjectData.RestoreSettings("{\"avatars\":[{\"name\":\"A\",\"outfits\":[{\"name\":\"Look\",\"blueprintId\":\"\"}]}]}");
            var data = OutfitProjectData.GetOutfit("A", "Look");
            Assert.IsNotNull(data.thumb);
            Assert.AreEqual("", data.thumb.source);
            Assert.AreEqual(0.5f, data.thumb.cropX, 1e-6f);
            Assert.AreEqual(1f, data.thumb.cropZoom, 1e-6f);
            Assert.IsNotNull(OutfitProjectData.ThumbDefault);
        }

        [Test] public void CroppedImagesRenderTheChosenWindow()
        {
            var image = SplitImage(1600, 900, Color.red, Color.blue);
            var centered = OutfitBatchUploader.RenderCroppedThumbnail(image, 0.5f, 0.5f, 1f);
            Assert.IsTrue(Near(Pixel(centered, 0.05f, 0.5f), Color.red));
            Assert.IsTrue(Near(Pixel(centered, 0.95f, 0.5f), Color.blue));
            // Window 0..0.75 of the width: the red half fills two thirds of the frame.
            var left = OutfitBatchUploader.RenderCroppedThumbnail(image, 0f, 0.5f, 1f);
            Assert.IsTrue(Near(Pixel(left, 0.6f, 0.5f), Color.red));
            Assert.IsTrue(Near(Pixel(left, 0.72f, 0.5f), Color.blue));
            // Zoomed far into the right edge: only blue.
            var zoomed = OutfitBatchUploader.RenderCroppedThumbnail(image, 1f, 0.5f, 4f);
            Assert.IsTrue(Near(Pixel(zoomed, 0.02f, 0.5f), Color.blue));
        }

        private static GameObject Avatar(string name, Color color, Vector3 position)
        {
            var root = new GameObject(name, typeof(Animator), typeof(VRCAvatarDescriptor));
            root.transform.position = position;
            var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.transform.SetParent(root.transform, false);
            body.transform.localPosition = new Vector3(0f, 1f, 0f);
            body.transform.localScale = new Vector3(0.6f, 2f, 0.6f);
            var material = new Material(Shader.Find("Unlit/Color")) { color = color };
            body.GetComponent<Renderer>().sharedMaterial = material;
            return root;
        }

        private static Camera ThumbCamera(GameObject parent, string id, bool avatarOnly = false)
        {
            var go = new GameObject("Thumbnail Camera");
            if (parent != null) go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = new Vector3(0f, 1f, 3f);
            go.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            var cam = go.AddComponent<Camera>();
            cam.enabled = false;
            cam.fieldOfView = 60f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.green;
            var marker = go.AddComponent<WardrobeThumbnailCamera>();
            marker.id = id;
            marker.avatarOnly = avatarOnly;
            return cam;
        }

        [Test] public void CameraCapturesKeepTheBackdropAndHideOtherAvatars()
        {
            var avatar = Avatar("Target", Color.white, Vector3.zero);
            // An overlapping copy, like the source avatar under an upload staging copy.
            var other = Avatar("Other", Color.red, new Vector3(0f, 0f, 1f));
            var backdrop = GameObject.CreatePrimitive(PrimitiveType.Quad);
            backdrop.transform.position = new Vector3(0f, 1f, -2f);
            backdrop.transform.rotation = Quaternion.Euler(0f, 180f, 0f); // a Quad renders from its -Z side only
            backdrop.transform.localScale = new Vector3(20f, 20f, 1f);
            backdrop.GetComponent<Renderer>().sharedMaterial = new Material(Shader.Find("Unlit/Color")) { color = Color.blue };
            var cam = ThumbCamera(avatar, "cam-a");

            var png = OutfitBatchUploader.RenderCameraThumbnail(cam, avatar);
            Assert.IsTrue(Near(Pixel(png, 0.5f, 0.5f), Color.white), "The target avatar is drawn, not the overlapping one.");
            Assert.IsTrue(Near(Pixel(png, 0.05f, 0.5f), Color.blue), "The backdrop stays: " + Pixel(png, 0.05f, 0.5f));
            Assert.IsFalse(other.GetComponentInChildren<Renderer>().forceRenderingOff, "Hidden avatars are restored.");
            Assert.IsNull(cam.targetTexture, "The user's camera target is restored.");

            cam.GetComponent<WardrobeThumbnailCamera>().avatarOnly = true;
            png = OutfitBatchUploader.RenderCameraThumbnail(cam, avatar);
            Assert.IsTrue(Near(Pixel(png, 0.05f, 0.5f), Color.green), "Avatar only clears to the camera background.");
            Assert.IsFalse(backdrop.GetComponent<Renderer>().forceRenderingOff);
        }

        [Test] public void CameraLookupPrefersTheCopyUnderTheRenderedAvatar()
        {
            var source = Avatar("Source", Color.white, Vector3.zero);
            var staged = Avatar("Staged", Color.white, Vector3.zero);
            var sourceCam = ThumbCamera(source, "shared");
            var stagedCam = ThumbCamera(staged, "shared");
            var free = ThumbCamera(null, "free");
            Assert.AreSame(stagedCam, OutfitBatchUploader.FindThumbCamera("shared", staged));
            Assert.AreSame(sourceCam, OutfitBatchUploader.FindThumbCamera("shared", source));
            Assert.AreSame(free, OutfitBatchUploader.FindThumbCamera("free", staged));
            Assert.IsNull(OutfitBatchUploader.FindThumbCamera("missing", staged));
        }

        [Test] public void ReuploadsKeepOrSkipUnchangedThumbnails()
        {
            var avatar = Avatar("Target", Color.white, Vector3.zero);
            var data = new OutfitProjectData.OutfitData { name = "Look" };
            data.thumb.source = "auto";
            data.thumb.update = "keep";
            const string id = "avtr_00000000-0000-0000-0000-000000000001";

            Assert.IsNull(OutfitBatchUploader.ResolveUploadThumbnail(avatar, data, "Look", false, id, Color.black, out _),
                "Keep sends nothing on a re-upload.");

            var first = OutfitBatchUploader.ResolveUploadThumbnail(avatar, data, "Look", true, "", Color.black, out var newHash);
            Assert.IsNotNull(first, "New avatars always get a thumbnail.");
            temps.Add(first);

            data.thumb.update = "always";
            var path = OutfitBatchUploader.ResolveUploadThumbnail(avatar, data, "Look", false, id, Color.black, out var hash);
            Assert.IsNotNull(path);
            temps.Add(path);
            data.thumbSentHash = hash;
            data.thumbSentBlueprint = id;
            Assert.IsNull(OutfitBatchUploader.ResolveUploadThumbnail(avatar, data, "Look", false, id, Color.black, out _),
                "An identical image is not sent again.");
            Assert.IsNotNull(OutfitBatchUploader.ResolveUploadThumbnail(avatar, data, "Look", false, id.Replace('1', '2'), Color.black, out _),
                "A different avatar ID gets the image.");
        }

        [Test] public void MissingCameraKeepsTheCurrentThumbnailButNewAvatarsFallBack()
        {
            var avatar = Avatar("Target", Color.white, Vector3.zero);
            var data = new OutfitProjectData.OutfitData { name = "Look" };
            data.thumb.source = "camera";
            data.thumb.camera = "gone";
            data.thumb.update = "always";
            LogAssertWarnings(2);
            Assert.IsNull(OutfitBatchUploader.ResolveUploadThumbnail(avatar, data, "Look", false,
                "avtr_00000000-0000-0000-0000-000000000001", Color.black, out _));
            var path = OutfitBatchUploader.ResolveUploadThumbnail(avatar, data, "Look", true, "", Color.black, out _);
            Assert.IsNotNull(path);
            temps.Add(path);
        }

        private static void LogAssertWarnings(int count)
        {
            for (int i = 0; i < count; i++)
                UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("thumbnail camera is not in the scene"));
        }

        [Test] public void StoredOriginalsAcceptOnlyPngAndJpeg()
        {
            Assert.Throws<InvalidDataException>(() => OutfitBatchUploader.StoreThumbOriginal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }));
            var name = OutfitBatchUploader.StoreThumbOriginal(SplitImage(8, 6, Color.red, Color.blue));
            try
            {
                StringAssert.EndsWith(".png", name);
                Assert.IsNotNull(OutfitBatchUploader.ThumbOriginalPath(name));
                Assert.IsNull(OutfitBatchUploader.ThumbOriginalPath("../ShiroOutfit_data.json"), "Store names cannot escape the folder.");
            }
            finally { File.Delete(Path.Combine(OutfitBatchUploader.ThumbStoreDir, name)); }
        }
    }
}
