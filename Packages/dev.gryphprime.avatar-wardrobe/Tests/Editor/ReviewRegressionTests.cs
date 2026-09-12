using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using VRC.SDK3.Avatars.Components;
using ShiroTools;

namespace OutfitToggleGenerator
{
    public class PreviewSchedulingTests
    {
        [Test]
        public void PreviewEpochValidationIgnoresNonPixelSegmentsButRejectsStaleKeys()
        {
            var session = (string)typeof(AvatarWardrobeServer)
                .GetField("serverSession", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).GetValue(null);
            var revision = (int)typeof(AvatarWardrobeServer)
                .GetField("previewRevision", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).GetValue(null);
            var catalog = AvatarWardrobeCatalog.CatalogEpoch;
            var current = session + "|" + catalog + "|overrides|base|" + revision;
            Assert.IsTrue(AvatarWardrobeServer.IsCurrentPreviewEpoch(current));
            Assert.IsFalse(AvatarWardrobeServer.IsCurrentPreviewEpoch("stale|" + catalog + "|overrides|base|" + revision));
            Assert.IsFalse(AvatarWardrobeServer.IsCurrentPreviewEpoch(session + "|999999|overrides|base|" + revision));
            Assert.IsFalse(AvatarWardrobeServer.IsCurrentPreviewEpoch(session + "|" + catalog + "|overrides|base|999999"));
        }

        [TestCase(.01, .1, 1)]
        [TestCase(.5, .5, 5)]
        [TestCase(2, 2, 20)]
        public void VisibleRendersHaveShorterAdaptiveCooldown(double cost, double visible, double prefetched)
        {
            Assert.AreEqual(visible, AvatarWardrobeServer.PreviewIdleDelay(cost, true), .00001);
            Assert.AreEqual(prefetched, AvatarWardrobeServer.PreviewIdleDelay(cost, false), .00001);
        }
    }

    public class ReviewRegressionTests
    {
        private Scene scene, previous;
        private GameObject root;
        private VRCAvatarDescriptor avatar;
        private string presets, uploads;
        [SetUp] public void SetUp()
        {
            presets = AvatarWardrobePresets.CaptureSettings();
            uploads = OutfitProjectData.CaptureSettings();
            previous = WardrobeTestSceneFixture.RequireSavedActiveScene();
            scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            root = new GameObject("Review fixture");
            SceneManager.MoveGameObjectToScene(root, scene);
            avatar = root.AddComponent<VRCAvatarDescriptor>();
            EditorSceneManager.SaveScene(scene, "Assets/ReviewFixture.unity");
            AvatarWardrobeServer.SceneAvatar = avatar;
        }
        [TearDown] public void TearDown()
        {
            Undo.ClearAll();
            AvatarWardrobeServer.SceneAvatar = null;
            if (previous.IsValid()) SceneManager.SetActiveScene(previous);
            EditorSceneManager.CloseScene(scene, true);
            AssetDatabase.DeleteAsset("Assets/ReviewFixture.unity");
            AvatarWardrobePresets.RestoreSettings(presets);
            OutfitProjectData.RestoreSettings(uploads);
        }
        [Test] public void LibraryImportLeaseReleasesReloadAndEditLocks()
        {
            var lease = AvatarWardrobeServer.BeginLibraryImport();
            Assert.AreEqual(1, lease.ok, lease.message);
            try
            {
                Assert.IsTrue(AvatarWardrobeServer.UploadTargetLocked);
                Assert.AreEqual(0, AvatarWardrobeServer.BeginLibraryImport().ok);
                Assert.AreEqual(0, AvatarWardrobeServer.EndLibraryImport("wrong-token").ok);
                Assert.AreEqual(0, AvatarWardrobeServer.RenewLibraryImport("wrong-token").ok);
                Assert.AreEqual(1, AvatarWardrobeServer.RenewLibraryImport(lease.id).ok);
                Assert.IsTrue(AvatarWardrobeServer.UploadTargetLocked);
            }
            finally { Assert.AreEqual(1, AvatarWardrobeServer.EndLibraryImport(lease.id).ok); }
            Assert.IsFalse(AvatarWardrobeServer.UploadTargetLocked);
            Assert.AreEqual(0, AvatarWardrobeServer.EndLibraryImport(lease.id).ok);
        }
        [Test] public void ExactCopyRemovalAndSettingsUndoRemainCoherent()
        {
            const string report = "Library/AvatarWardrobe/edit-validation.txt";
            if (File.Exists(report)) File.Delete(report);
            AvatarWardrobeServer.ValidateWardrobeEditing();
            StringAssert.StartsWith("PASS: 10 ", File.ReadAllText(report));
        }
        [Test] public void MixedPartTreesAreRejectedBeforeChangingTargets()
        {
            var prefab = new GameObject("Garment"); prefab.transform.SetParent(root.transform);
            var part = new GameObject("Part"); part.transform.SetParent(prefab.transform);
            OutfitToggleGenerator.GeneratePartToggles(avatar, prefab);
            Transform host = null;
            foreach (Transform t in prefab.transform) if (t != part.transform) host = t;
            var custom = new GameObject("User content"); custom.transform.SetParent(host);
            bool active = part.activeSelf;
            Assert.Throws<InvalidOperationException>(() => OutfitToggleGenerator.RemovePartToggles(prefab));
            Assert.Throws<InvalidOperationException>(() => OutfitToggleGenerator.GeneratePartToggles(avatar, prefab));
            Assert.IsTrue(custom != null); Assert.AreEqual(host, custom.transform.parent);
            Assert.AreEqual(active, part.activeSelf);
            UnityEngine.Object.DestroyImmediate(custom);
            OutfitToggleGenerator.RemovePartToggles(prefab);
            Assert.IsFalse(OutfitToggleGenerator.HasPartToggles(prefab));
        }
        [Test] public void ItemSettingsRejectsUnspecifiedOrUnmatchedEditsWithoutMutation()
        {
            var before = AvatarWardrobePresets.CaptureSettings();
            var missing = AvatarWardrobeServer.SetItemSettings("", "missing", false, null, false, false);
            Assert.AreEqual(0, missing.ok); StringAssert.Contains("required", missing.message);
            var unspecified = AvatarWardrobeServer.SetItemSettings("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "missing", false, null, false, false);
            Assert.AreEqual(0, unspecified.ok); StringAssert.Contains("Specify", unspecified.message);
            var unmatched = AvatarWardrobeServer.SetItemSettings("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "missing", true, "", true, false);
            Assert.AreEqual(0, unmatched.ok); StringAssert.Contains("not installed", unmatched.message);
            Assert.AreEqual(before, AvatarWardrobePresets.CaptureSettings());
        }
        [Test] public void RenameAllocatesUniqueSanitizedHolderAndUndoRestoresIdentity()
        {
            var a = AvatarWardrobeServer.EditAvatar("Create", () => AvatarWardrobeServer.SavePreset("", "A/B"));
            Assert.AreEqual(1, a.ok, a.message);
            var b = AvatarWardrobeServer.EditAvatar("Create", () => AvatarWardrobeServer.SavePreset("", "Second"));
            Assert.AreEqual(1, b.ok, b.message);
            var before = AvatarWardrobePresets.CaptureSettings();
            var renamed = AvatarWardrobeServer.EditAvatar("Rename", () => AvatarWardrobeServer.SavePreset(b.id, "A-B"));
            Assert.AreEqual(1, renamed.ok, renamed.message);
            var after = AvatarWardrobePresets.CaptureSettings();
            Assert.AreNotEqual(AvatarWardrobePresets.GetPreset(a.id).legacyPath, AvatarWardrobePresets.GetPreset(b.id).legacyPath);
            Undo.FlushUndoRecordObjects(); Undo.PerformUndo();
            Assert.AreEqual(before, AvatarWardrobePresets.CaptureSettings());
            OutfitBatchUploader.WebGetState();
            Undo.PerformRedo();
            Assert.AreEqual(after, AvatarWardrobePresets.CaptureSettings());
            Assert.AreEqual(b.id, AvatarWardrobePresets.GetPreset(b.id).id);
        }
        [Test] public void RejectedEditRestoresSceneAndSettings()
        {
            var before = AvatarWardrobePresets.CaptureSettings();
            var children = root.transform.childCount;
            var result = AvatarWardrobeServer.EditAvatar("Reject after creation", () =>
            {
                var saved = AvatarWardrobeServer.SavePreset("", "Rejected");
                Assert.AreEqual(1, saved.ok, saved.message);
                return new AvatarWardrobeServer.ResultDto { message = "Rejected" };
            });
            Assert.AreEqual(0, result.ok);
            Assert.AreEqual(before, AvatarWardrobePresets.CaptureSettings());
            Assert.AreEqual(children, root.transform.childCount);
        }
        [Test] public void RepeatedStateReadsDoNotMigrateLegacyFolders()
        {
            var outfits = new GameObject("Outfits"); outfits.transform.SetParent(root.transform);
            var legacy = new GameObject("Legacy preset"); legacy.transform.SetParent(outfits.transform);
            var before = AvatarWardrobePresets.CaptureSettings();
            var uploadBefore = OutfitProjectData.CaptureSettings();
            var children = root.GetComponentsInChildren<Transform>(true).Length;
            for (int i = 0; i < 3; i++) OutfitBatchUploader.WebGetState();
            Assert.AreEqual(before, AvatarWardrobePresets.CaptureSettings());
            Assert.AreEqual(uploadBefore, OutfitProjectData.CaptureSettings());
            Assert.AreEqual(children, root.GetComponentsInChildren<Transform>(true).Length);
        }
        [Test] public void CorruptUploadStateBlocksWritesAndImport()
        {
            const string path = "ProjectSettings/ShiroOutfit_data.json";
            OutfitProjectData.RestoreSettings(null);
            if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
            File.WriteAllText(path, "broken");
            Assert.Throws<IOException>(() => OutfitProjectData.GetOutfit("Avatar", "Preset"));
            Assert.Throws<IOException>(() => OutfitProjectData.ImportRaw("{\"avatars\":[{\"name\":\"Other\",\"outfits\":[]}]}"));
            Assert.AreEqual("broken", File.ReadAllText(path));
        }
        [Test] public void SaveAndImportFailureDoNotCommitInMemoryState()
        {
            const string path = "ProjectSettings/ShiroOutfit_data.json";
            OutfitProjectData.RestoreSettings(null);
            var data = OutfitProjectData.GetOutfit("Avatar", "Preset");
            data.blueprintId = "avtr-original"; OutfitProjectData.Save();
            var original = File.ReadAllText(path);
            File.Delete(path); Directory.CreateDirectory(path);
            try
            {
                data.blueprintId = "avtr-unsaved";
                Assert.Throws<IOException>(() => OutfitProjectData.Save());
                Assert.AreEqual("avtr-original", OutfitProjectData.GetOutfit("Avatar", "Preset").blueprintId);
                Assert.Throws<IOException>(() => OutfitProjectData.ImportRaw("{\"avatars\":[{\"name\":\"Other\",\"outfits\":[]}]}"));
                Assert.AreEqual("avtr-original", OutfitProjectData.GetOutfit("Avatar", "Preset").blueprintId);
            }
            finally { Directory.Delete(path); OutfitProjectData.RestoreSettings(original); }
        }
        [Test] public void SameNamedLiveRootsAndHoldersKeepIndependentSettingsAfterRename()
        {
            var other = new GameObject(root.name);
            SceneManager.MoveGameObjectToScene(other, scene);
            try
            {
                var holderA = new GameObject("Casual"); holderA.transform.SetParent(root.transform);
                var holderB = new GameObject("Casual"); holderB.transform.SetParent(other.transform);
                var aKey = OutfitProjectData.SceneAvatarKey(root);
                var bKey = OutfitProjectData.SceneAvatarKey(other);
                Assert.AreNotEqual(aKey, bKey);
                var a = OutfitProjectData.SceneOutfit(aKey, holderA);
                var b = OutfitProjectData.SceneOutfit(bKey, holderB);
                a.blueprintId = "avtr-a"; b.blueprintId = "avtr-b";
                a.buildAndroid = true; b.buildAndroid = false;
                root.name = "Renamed"; holderA.name = "Renamed holder";
                EditorSceneManager.SaveScene(scene);
                Assert.AreEqual(aKey, OutfitProjectData.SceneAvatarKey(root));
                Assert.AreSame(a, OutfitProjectData.SceneOutfit(aKey, holderA));
                Assert.AreEqual("avtr-b", b.blueprintId); Assert.IsFalse(b.buildAndroid);
                OutfitProjectData.Save();
                OutfitProjectData.RestoreSettings(OutfitProjectData.CaptureSettings());
                Assert.AreEqual("avtr-a", OutfitProjectData.SceneOutfit(OutfitProjectData.SceneAvatarKey(root), holderA).blueprintId);
                var generated = OutfitProjectData.GetOutfit("Shinano_Wardrobe_fixture", "Casual");
                generated.blueprintId = "avtr-generated";
                Assert.AreEqual("avtr-generated", OutfitProjectData.GetOutfit("Shinano_Wardrobe_fixture", "Casual").blueprintId);
            }
            finally { UnityEngine.Object.DestroyImmediate(other); }
        }
        [Test] public void InvalidVersionsRejectEntireSettingsBundle()
        {
            var before = OutfitProjectData.CaptureSettings();
            var bundle = "{\"data\":\"{\\\"avatars\\\":[]}\",\"versions\":\"{}\"}";
            Assert.Throws<InvalidDataException>(() => OutfitBatchUploader.ImportSettingsBundle(bundle));
            Assert.AreEqual(before, OutfitProjectData.CaptureSettings());
        }
        [Test] public void VersionPersistenceFailureRestoresMainSettings()
        {
            const string path = "ProjectSettings/ShiroOutfit_versions.json";
            var disk = AvatarVersionManager.CaptureSettings();
            var memory = AvatarVersionManager.ExportRaw();
            var before = OutfitProjectData.CaptureSettings();
            if (File.Exists(path)) File.Delete(path);
            Directory.CreateDirectory(path);
            try
            {
                var bundle = "{\"data\":\"{\\\"avatars\\\":[]}\",\"versions\":\"{\\\"versions\\\":[]}\"}";
                Assert.Throws<AggregateException>(() => OutfitBatchUploader.ImportSettingsBundle(bundle));
                Assert.AreEqual(before, OutfitProjectData.CaptureSettings());
                Assert.AreEqual(memory, AvatarVersionManager.ExportRaw());
            }
            finally { Directory.Delete(path); AvatarVersionManager.RestoreSettings(disk, memory); }
        }
        [Test] public void BackupRecoveryPreservesCorruptPrimary()
        {
            const string path = "ProjectSettings/ShiroOutfit_data.json";
            OutfitProjectData.RestoreSettings(null);
            File.WriteAllText(path, "broken");
            File.WriteAllText(path + ".bak", "{\"avatars\":[]}");
            try
            {
                OutfitProjectData.GetOutfit("Avatar", "Preset");
                var evidence = Directory.GetFiles("ProjectSettings", "ShiroOutfit_data.json.corrupt-*");
                Assert.IsTrue(Array.Exists(evidence, f => File.ReadAllText(f) == "broken"));
                OutfitProjectData.Save();
                Assert.IsTrue(Array.Exists(evidence, f => File.ReadAllText(f) == "broken"));
            }
            finally
            {
                foreach (var f in Directory.GetFiles("ProjectSettings", "ShiroOutfit_data.json.corrupt-*")) File.Delete(f);
            }
        }
        [Test] public void CompletedJobCanBeReadByTwoPollers()
        {
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            var uploader = typeof(OutfitBatchUploader);
            var create = uploader.GetMethod("NewWebJob", flags);
            var finish = uploader.GetMethod("WebJobFinish", flags);
            var job = (string)create.Invoke(null, null);
            finish.Invoke(null, new object[] {job, true, "Uploaded", "avtr-fixture"});
            var first = OutfitBatchUploader.WebJobResult(job);
            var second = OutfitBatchUploader.WebJobResult(job);
            Assert.AreEqual(1, first.done); Assert.AreEqual(1, second.ok);
            Assert.AreEqual(first.blueprintId, second.blueprintId);
            Assert.AreEqual(1, OutfitBatchUploader.WebJobResult("expired").done);
        }
        [Test] public void GeneratedThumbnailIsCachedAndRemoteThumbnailIsPreserved()
        {
            Assert.IsNull(AvatarWardrobeUpload.ResolveSceneUploadThumbnail(root, "remote", true));
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube); cube.transform.SetParent(root.transform);
            string file = null;
            try
            {
                var key = Guid.NewGuid().ToString("N");
                file = AvatarWardrobeUpload.ResolveSceneUploadThumbnail(root, key, false);
                var bytes = File.ReadAllBytes(file);
                Assert.AreEqual(137, bytes[0]); Assert.AreEqual(80, bytes[1]);
                Assert.AreEqual(file, AvatarWardrobeUpload.ResolveSceneUploadThumbnail(null, key, false));
            }
            finally { if (file != null) File.Delete(file); }
        }
        [Test] public void UploadOutcomesBelongToTheirRunAndCancellationIsNotSuccess()
        {
            var prior = SessionState.GetString("Shiro_BatchRunId", "");
            try
            {
                SessionState.SetString("Shiro_BatchRunId", "old-failure");
                OutfitBatchUploader.RecordBatchOutcome("failed");
                SessionState.SetString("Shiro_BatchRunId", "new-failure");
                OutfitBatchUploader.RecordBatchOutcome("failed");
                Assert.IsFalse(OutfitBatchUploader.BatchRunSucceeded("new-failure"));
                SessionState.SetString("Shiro_BatchRunId", "retry");
                OutfitBatchUploader.RecordBatchOutcome("running");
                Assert.IsFalse(OutfitBatchUploader.BatchRunSucceeded("retry"));
                OutfitBatchUploader.RecordBatchOutcome("cancelled");
                Assert.IsFalse(OutfitBatchUploader.BatchRunSucceeded("retry"));
                OutfitBatchUploader.RecordBatchOutcome("success");
                Assert.IsTrue(OutfitBatchUploader.BatchRunSucceeded("retry"));
                Assert.IsFalse(OutfitBatchUploader.BatchRunSucceeded("new-failure"));
                Assert.IsFalse(OutfitBatchUploader.BatchRunSucceeded("refused"));
            }
            finally { SessionState.SetString("Shiro_BatchRunId", prior); }
        }
        [Test] public void SceneUploadResultReadsAreRepeatable()
        {
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            var server = typeof(AvatarWardrobeServer);
            var jobs = (System.Collections.IDictionary)server.GetField("uploadJobs", flags).GetValue(null);
            var stateType = server.GetNestedType("UploadJobState", System.Reflection.BindingFlags.NonPublic);
            var state = Activator.CreateInstance(stateType);
            stateType.GetField("done").SetValue(state, true);
            stateType.GetField("ok").SetValue(state, true);
            stateType.GetField("blueprintId").SetValue(state, "avtr-result");
            var job = Guid.NewGuid().ToString("N"); jobs.Add(job, state);
            try
            {
                var query = new System.Collections.Generic.Dictionary<string,string> {{"job",job}};
                var get = server.GetMethod("GetUploadResult", flags);
                var first = get.Invoke(null, new object[] {query});
                var second = get.Invoke(null, new object[] {query});
                Assert.AreEqual(1, second.GetType().GetField("ok").GetValue(second));
                Assert.AreEqual(first.GetType().GetField("blueprintId").GetValue(first), second.GetType().GetField("blueprintId").GetValue(second));
            }
            finally { jobs.Remove(job); }
        }
        [UnityTest] public IEnumerator BuildCopyIsIsolatedAndCleanedOnSuccessFailureAndCancellation()
        {
            var part = new GameObject("Part"); part.transform.SetParent(root.transform); part.SetActive(false);
            var duplicate = new GameObject(root.name); SceneManager.MoveGameObjectToScene(duplicate, previous);
            int count = SceneManager.sceneCount;
            try
            {
                for (int mode = 0; mode < 3; mode++)
                {
                    string stagingPath = null; int outcome = mode;
                    Task task = AvatarWardrobeUpload.RunOnAvatarCopy(avatar, async clone =>
                    {
                        stagingPath = clone.scene.path;
                        Assert.AreNotSame(root, clone); Assert.AreNotSame(duplicate, clone);
                        Assert.AreNotEqual(scene, clone.scene); Assert.IsTrue(File.Exists(stagingPath));
                        Assert.IsFalse(clone.transform.Find("Part").gameObject.activeSelf);
                        clone.transform.Find("Part").gameObject.SetActive(true);
                        await Task.Yield();
                        if (outcome == 1) throw new InvalidOperationException("SDK failed");
                        if (outcome == 2) throw new OperationCanceledException();
                    });
                    while (!task.IsCompleted) yield return null;
                    Assert.AreEqual(mode == 0, task.Status == TaskStatus.RanToCompletion);
                    Assert.AreEqual(count, SceneManager.sceneCount);
                    Assert.IsFalse(part.activeSelf); Assert.IsFalse(File.Exists(stagingPath));
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(duplicate); }
        }
    }
}
