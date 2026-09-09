using System;
using System.IO;
using NUnit.Framework;

namespace OutfitToggleGenerator
{
    public sealed class LibraryUsageHashTests
    {
        private string folder;
        [SetUp] public void SetUp() { folder = Path.Combine(Path.GetTempPath(), "WardrobeUsageHash_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder); }
        [TearDown] public void TearDown() { if (Directory.Exists(folder)) Directory.Delete(folder, true); }

        [Test] public void UnchangedSourcesUseCacheAndEverySourceVersionPartInvalidatesIt()
        {
            var path = Path.Combine(folder, "Outfit.prefab"); File.WriteAllText(path, "first");
            long budget = 100; var original = AvatarWardrobeServer.LibraryUsagePrefabHash(path, "dependency-one", ref budget);
            Assert.AreEqual("a7937b64b8caa58f03721bb6bacf5c78cb235febe0e70b1b84cd99541461a08e", original); Assert.AreEqual(95, budget);
            budget = 0; Assert.AreEqual(original, AvatarWardrobeServer.LibraryUsagePrefabHash(path, "dependency-one", ref budget));
            Assert.IsEmpty(AvatarWardrobeServer.LibraryUsagePrefabHash(path, "dependency-two", ref budget), "Dependency changes must not reuse unconfirmed provenance.");
            budget = 100; Assert.AreEqual(original, AvatarWardrobeServer.LibraryUsagePrefabHash(path, "dependency-two", ref budget));
            File.WriteAllText(path, "other"); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            budget = 0; Assert.IsEmpty(AvatarWardrobeServer.LibraryUsagePrefabHash(path, "dependency-two", ref budget), "mtime changes invalidate the cache even before import updates dependencies.");
            budget = 100; var changed = AvatarWardrobeServer.LibraryUsagePrefabHash(path, "dependency-two", ref budget); Assert.AreNotEqual(original, changed);
            var modified = File.GetLastWriteTimeUtc(path); File.AppendAllText(path, " longer"); File.SetLastWriteTimeUtc(path, modified);
            budget = 0; Assert.IsEmpty(AvatarWardrobeServer.LibraryUsagePrefabHash(path, "dependency-two", ref budget), "Length changes invalidate the cache independently of timestamp.");
        }

        [Test] public void ModelsOversizeAndExhaustedReadsStayGuidOnly()
        {
            var model = Path.Combine(folder, "Avatar.fbx"); File.WriteAllText(model, "model");
            var large = Path.Combine(folder, "Large.prefab"); using (var file = File.Create(large)) file.SetLength(2 * 1024 * 1024 + 1);
            long budget = 64 * 1024 * 1024;
            Assert.IsEmpty(AvatarWardrobeServer.LibraryUsagePrefabHash(model, "model", ref budget));
            Assert.IsEmpty(AvatarWardrobeServer.LibraryUsagePrefabHash(large, "large", ref budget));
            Assert.AreEqual(64 * 1024 * 1024, budget);
            var small = Path.Combine(folder, "Small.prefab"); File.WriteAllText(small, "prefab");
            budget = 5; Assert.IsEmpty(AvatarWardrobeServer.LibraryUsagePrefabHash(small, "small", ref budget)); Assert.AreEqual(5, budget);
            budget = 6; Assert.AreEqual(64, AvatarWardrobeServer.LibraryUsagePrefabHash(small, "small", ref budget).Length); Assert.AreEqual(0, budget);
            File.Delete(small); Assert.IsEmpty(AvatarWardrobeServer.LibraryUsagePrefabHash(small, "small", ref budget));
        }

        [Test] public void HashCacheEvictsOldestEntriesAtItsBound()
        {
            var first = Path.Combine(folder, "First.prefab"); File.WriteAllText(first, "first"); long budget = 10000;
            Assert.AreEqual(64, AvatarWardrobeServer.LibraryUsagePrefabHash(first, "first", ref budget).Length);
            for (var index = 0; index < 512; index++)
            {
                var path = Path.Combine(folder, index + ".prefab"); File.WriteAllText(path, "x");
                Assert.AreEqual(64, AvatarWardrobeServer.LibraryUsagePrefabHash(path, "version", ref budget).Length);
            }
            budget = 0; Assert.IsEmpty(AvatarWardrobeServer.LibraryUsagePrefabHash(first, "first", ref budget));
        }
    }
}
