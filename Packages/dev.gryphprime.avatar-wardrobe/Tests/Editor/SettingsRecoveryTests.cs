using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;

namespace OutfitToggleGenerator
{
    // All file operations in this fixture use a private temporary directory.
    public sealed class SettingsRecoveryTests
    {
        private string folder, path;
        private const string Saved = "{\"presets\":[{\"id\":\"coat\",\"baseKey\":\"avatar\"}],\"assignments\":[]}";
        private const string Empty = "{\"presets\":[],\"assignments\":[]}";
        private static void Validate(string json)
        {
            try
            {
                typeof(AvatarWardrobePresets).GetMethod("ParseFile", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { json });
            }
            catch (TargetInvocationException error) { throw error.InnerException; }
        }
        [SetUp] public void SetUp()
        {
            folder = Path.Combine(Path.GetTempPath(), "WardrobeRecoveryTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            path = Path.Combine(folder, "presets.json");
        }
        [TearDown] public void TearDown() { if (Directory.Exists(folder)) Directory.Delete(folder, true); }

        [Test] public void NewProjectIsEmptyWithoutCreatingAFile()
        {
            Assert.IsNull(WardrobeAtomicFile.ReadRecoverableText(path, Validate));
            Assert.IsFalse(File.Exists(path));
        }
        [Test] public void MissingPrimaryRecoversBackupOnDisk()
        {
            File.WriteAllText(path + ".bak", Saved);
            Assert.AreEqual(Saved, WardrobeAtomicFile.ReadRecoverableText(path, Validate));
            Assert.AreEqual(Saved, File.ReadAllText(path));
            Assert.AreEqual(Saved, File.ReadAllText(path + ".bak"));
        }
        [Test] public void CorruptPrimaryIsPreservedAndNeverReplacesBackup()
        {
            File.WriteAllText(path, "truncated"); File.WriteAllText(path + ".bak", Saved);
            Assert.AreEqual(Saved, WardrobeAtomicFile.ReadRecoverableText(path, Validate));
            Assert.AreEqual("truncated", File.ReadAllText(Directory.GetFiles(folder, "*.corrupt-*")[0]));
            Assert.AreEqual(Saved, File.ReadAllText(path + ".bak"));
        }
        [Test] public void MissingCollectionIsCorruptionRatherThanAnEmptyWardrobe()
        {
            File.WriteAllText(path, "{}"); File.WriteAllText(path + ".bak", Saved);
            Assert.AreEqual(Saved, WardrobeAtomicFile.ReadRecoverableText(path, Validate));
        }
        [Test] public void UnrecoverableStoreBlocksReplacementAndPreservesBothCopies()
        {
            File.WriteAllText(path, "bad-primary"); File.WriteAllText(path + ".bak", "bad-backup");
            Assert.Throws<IOException>(() => WardrobeAtomicFile.ReadRecoverableText(path, Validate));
            Assert.Throws<IOException>(() => WardrobeAtomicFile.RestoreText(path, Empty, Validate));
            Assert.AreEqual("bad-primary", File.ReadAllText(path));
            Assert.AreEqual("bad-backup", File.ReadAllText(path + ".bak"));
        }
        [Test] public void InvalidBackupAloneDoesNotBecomeANewStore()
        {
            File.WriteAllText(path + ".bak", "{}");
            Assert.Throws<IOException>(() => WardrobeAtomicFile.ReadRecoverableText(path, Validate));
            Assert.IsFalse(File.Exists(path));
        }
        [Test] public void ExplicitEmptyStoreDoesNotResurrectDeletedPresets()
        {
            File.WriteAllText(path, Empty); File.WriteAllText(path + ".bak", Saved);
            Assert.AreEqual(Empty, WardrobeAtomicFile.ReadRecoverableText(path, Validate));
        }
        [Test] public void RestoreKeepsPreviousValidSnapshotAsBackup()
        {
            File.WriteAllText(path, Saved);
            WardrobeAtomicFile.RestoreText(path, Empty, Validate);
            Assert.AreEqual(Empty, File.ReadAllText(path));
            Assert.AreEqual(Saved, File.ReadAllText(path + ".bak"));
        }
        [Test] public void UndoCreationArchivesBothCopiesWithoutResurrectingThem()
        {
            File.WriteAllText(path, Empty); File.WriteAllText(path + ".bak", Saved);
            WardrobeAtomicFile.RestoreText(path, null, Validate);
            Assert.IsNull(WardrobeAtomicFile.ReadRecoverableText(path, Validate));
            Assert.AreEqual(2, Directory.GetFiles(folder, "*.removed-*").Length);
        }
        [Test] public void InvalidSnapshotCannotDamageAnExistingStore()
        {
            File.WriteAllText(path, Saved);
            Assert.Throws<InvalidDataException>(() => WardrobeAtomicFile.RestoreText(path, "{}", Validate));
            Assert.AreEqual(Saved, File.ReadAllText(path));
        }

        [Test] public void UnrelatedUndoDoesNotReplayAStaleHistoryObject()
        {
            var flags = BindingFlags.Static | BindingFlags.NonPublic;
            var type = typeof(WardrobeEditHistory);
            var current = type.GetField("current", flags);
            var applied = type.GetField("appliedPresets", flags);
            var history = type.GetField("appliedHistory", flags);
            var oldCurrent = current.GetValue(null);
            var oldApplied = applied.GetValue(null);
            var oldHistory = history.GetValue(null);
            var stale = UnityEngine.ScriptableObject.CreateInstance<WardrobeEditHistory>();
            var errors = 0;
            UnityEngine.Application.LogCallback onLog = (message, stack, kind) =>
            { if (kind == UnityEngine.LogType.Error) errors++; };
            try
            {
                current.SetValue(null, stale);
                // This sentinel also prevents the old implementation from writing
                // project settings: its optimistic concurrency check would fail.
                applied.SetValue(null, "untracked-test-baseline");
                history.SetValue(null, UnityEngine.JsonUtility.ToJson(stale));
                UnityEngine.Application.logMessageReceived += onLog;
                type.GetMethod("Restore", flags).Invoke(null, null);
                Assert.AreEqual(0, errors, "Unrelated Undo must not even attempt a settings restore.");
                Assert.AreEqual("untracked-test-baseline", applied.GetValue(null));
            }
            finally
            {
                UnityEngine.Application.logMessageReceived -= onLog;
                current.SetValue(null, oldCurrent); applied.SetValue(null, oldApplied); history.SetValue(null, oldHistory);
                UnityEngine.Object.DestroyImmediate(stale);
            }
        }
    }
}
