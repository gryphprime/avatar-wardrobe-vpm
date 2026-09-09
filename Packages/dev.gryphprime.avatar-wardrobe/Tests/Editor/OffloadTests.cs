using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using VRC.SDK3.Avatars.Components;

namespace OutfitToggleGenerator
{
    public sealed class OffloadTests
    {
        [UnityTest] public IEnumerator ChunkedFingerprintMatchesStrictFingerprint()
        {
            var root = new GameObject("Fingerprint fixture");
            try
            {
                var avatar = root.AddComponent<VRCAvatarDescriptor>();
                for (var i = 0; i < 100; i++) new GameObject("Child " + i).transform.SetParent(root.transform);
                foreach (var visual in new[] { false, true })
                {
                    var expected = visual ? WardrobeTryOnWorker.VisualFingerprint(avatar) : WardrobeTryOnWorker.SourceFingerprint(avatar);
                    var result = WardrobeTryOnWorker.FingerprintAsync(avatar, visual);
                    while (!result.IsCompleted) yield return null;
                    Assert.That(result.IsFaulted, Is.False, result.Exception?.ToString());
                    Assert.That(result.Result, Is.EqualTo(expected), "Offloading must preserve existing revision identities.");
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }
        [UnityTest] public IEnumerator WorkerPngPreservesPixelOrientationAndAlpha()
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            var decoded = new Texture2D(2, 2);
            try
            {
                var colors = new[] { new Color32(255, 0, 0, 255), new Color32(0, 255, 0, 128), new Color32(0, 0, 255, 0), new Color32(8, 16, 32, 255) };
                texture.SetPixels32(colors); texture.Apply();
                var captured = texture.GetPixels32();
                var encoded = Task.Run(() => ImageConversion.EncodeArrayToPNG(captured, UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm, 2, 2));
                while (!encoded.IsCompleted) yield return null;
                Assert.That(encoded.IsFaulted, Is.False, encoded.Exception?.ToString());
                Assert.That(decoded.LoadImage(encoded.Result), Is.True);
                Assert.That(decoded.GetPixels32(), Is.EqualTo(colors));
            }
            finally { UnityEngine.Object.DestroyImmediate(texture); UnityEngine.Object.DestroyImmediate(decoded); }
        }
        [Test] public void CatalogWorkerHandlesReplacementDeletionAndInvalidSchema()
        {
            var directory = Path.Combine(Path.GetTempPath(), "wardrobe-catalog-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "catalog.json"); var dirty = Path.Combine(directory, "dirty.json");
            var method = typeof(AvatarWardrobeCatalog).GetMethod("ReadCatalogSnapshot", BindingFlags.NonPublic | BindingFlags.Static);
            object Read(DateTime prior) => method.Invoke(null, new object[] { file, dirty, prior });
            try
            {
                File.WriteAllText(file, "{\"version\":4,\"records\":[{\"guid\":\"fixture\"}],\"dirtyPaths\":[]}");
                File.WriteAllText(dirty, "{\"paths\":[\"Assets/changed.prefab\"]}");
                var snapshot = Task.Run(() => Read(DateTime.MinValue)).GetAwaiter().GetResult();
                var type = snapshot.GetType();
                var value = (WardrobeCatalogCache)type.GetField("value", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(snapshot);
                var stamp = (DateTime)type.GetField("stamp", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(snapshot);
                Assert.That(value.records.Count, Is.EqualTo(1));
                Assert.That(value.records[0].meshIds, Is.Not.Null);
                Assert.That(value.dirtyPaths, Does.Contain("Assets/changed.prefab"));
                Assert.That(Task.Run(() => Read(stamp)).Result, Is.Null);
                File.Delete(file);
                var deleted = Task.Run(() => Read(stamp)).Result;
                Assert.That(((WardrobeCatalogCache)type.GetField("value", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(deleted)).records, Is.Empty);
                File.WriteAllText(file, "{\"version\":999}");
                Assert.Throws<TargetInvocationException>(() => Read(DateTime.MinValue));
            }
            finally { Directory.Delete(directory, true); }
        }
    }
}
