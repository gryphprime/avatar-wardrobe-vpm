using System.Threading;
using System.Collections.Concurrent;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using VRC.SDK3.Avatars.Components;
using ShiroTools;
using NUnit.Framework;

namespace OutfitToggleGenerator
{
    public sealed class OperationTests
    {
        private string folder, file;
        [SetUp] public void Setup() { folder = Path.Combine(Path.GetTempPath(), "wardrobe-ledger-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder); file = Path.Combine(folder, "receipts.json"); }
        [TearDown] public void Cleanup() { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        private WardrobeOperation Command() => new WardrobeOperation
        {
            id = Guid.NewGuid().ToString(), type = "wear-outfit",
            target = new WardrobeOperation.Target { projectId = "fixture", sceneGuid = "scene", avatarId = "avatar", avatarInstanceId = 1, session = "session", scopeId = "common" },
            precondition = new WardrobeOperation.Precondition { observedRevision = "revision" },
            payload = new WardrobeOperation.Payload { variantId = "12345678901234567890123456789012", addCopy = true }
        };
        [Test] public void AcceptanceIsDurableAndDuplicateIdentityNeverDispatchesTwice()
        {
            var ledger = new WardrobeOperationLedger(file, "fixture", "session"); var command = Command();
            Assert.That(ledger.Accept(command, out var fresh).state, Is.EqualTo("queued")); Assert.That(fresh, Is.True);
            Assert.That(ledger.Accept(command, out fresh).state, Is.EqualTo("queued")); Assert.That(fresh, Is.False);
            var restarted = new WardrobeOperationLedger(file, "fixture", "session");
            Assert.That(restarted.Accept(command, out fresh).id, Is.EqualTo(command.id)); Assert.That(fresh, Is.False);
            command.payload.addCopy = false;
            Assert.Throws<InvalidOperationException>(() => restarted.Accept(command, out _));
        }
        [Test] public void ReloadDoesNotClaimUnsavedSuccessOrReplayUnknownCommands()
        {
            var ledger = new WardrobeOperationLedger(file, "fixture", "session"); var command = Command(); ledger.Accept(command, out _);
            ledger.Change(command.id, "running"); ledger.Change(command.id, "succeeded", new WardrobeOperationReceipt.Outcome { unsaved = true, confirmedRevision = "applied" });
            var restarted = new WardrobeOperationLedger(file, "fixture", "new-session");
            Assert.That(restarted.Get(command.id).state, Is.EqualTo("needs-review"));
            Assert.That(restarted.Accept(command, out var fresh).state, Is.EqualTo("needs-review")); Assert.That(fresh, Is.False);
            Assert.Throws<InvalidOperationException>(() => restarted.Accept(Command(), out _));
            Assert.That(restarted.Get(Guid.NewGuid().ToString()).state, Is.EqualTo("needs-review"));
        }
        [Test] public void CancellationOnlyStopsWorkThatHasNotStarted()
        {
            var ledger = new WardrobeOperationLedger(file, "fixture", "session"); var queued = Command(); ledger.Accept(queued, out _);
            Assert.That(ledger.Cancel(queued.id).state, Is.EqualTo("cancelled"));
            Assert.That(ledger.Change(queued.id, "running").state, Is.EqualTo("cancelled"));
            var running = Command(); ledger.Accept(running, out _); ledger.Change(running.id, "running");
            Assert.That(ledger.Cancel(running.id).cancelRequested, Is.True);
            Assert.That(ledger.Get(running.id).state, Is.EqualTo("running"));
            ledger.Change(running.id, "succeeded", new WardrobeOperationReceipt.Outcome());
            Assert.That(ledger.Cancel(running.id).state, Is.EqualTo("succeeded"));
        }
        [Test] public void CorruptJournalFailsClosedAndIsNotOverwritten()
        {
            File.WriteAllText(file, "{broken"); var ledger = new WardrobeOperationLedger(file, "fixture", "session");
            Assert.That(ledger.Error, Is.Not.Null); Assert.Throws<InvalidOperationException>(() => ledger.Accept(Command(), out _));
            Assert.That(File.ReadAllText(file), Is.EqualTo("{broken"));
        }
        [Test] public void ReturnedReceiptsCannotMutateJournal()
        {
            var ledger = new WardrobeOperationLedger(file, "fixture", "session"); var command = Command(); var receipt = ledger.Accept(command, out _);
            receipt.command.payload.variantId = "tampered"; command.target.avatarInstanceId = 2;
            Assert.That(ledger.Get(command.id).command.payload.variantId, Is.EqualTo("12345678901234567890123456789012"));
            Assert.That(ledger.Get(command.id).command.target.avatarInstanceId, Is.EqualTo(1));
        }
        [Test] public void OperationDispatchReturnsBeforePumpAndHonorsBarrierAndFifo()
        {
            var queue = new WardrobeWorkQueue(); var applied = new List<int>();
            var first = queue.EnqueueOperation(() => { applied.Add(1); return 1; });
            var second = queue.EnqueueOperation(() => { applied.Add(2); return 2; });
            Assert.That(first.IsCompleted, Is.False); queue.Pump(false); Assert.That(applied, Is.Empty);
            queue.Pump(true); Assert.That(first.Result, Is.EqualTo(1)); Assert.That(second.IsCompleted, Is.False);
            queue.Pump(true); Assert.That(second.Result, Is.EqualTo(2)); Assert.That(applied, Is.EqualTo(new[] { 1, 2 })); queue.Close();
        }
        [Test] public void ShutdownCancelsUndispatchedOperationWithoutExecutingIt()
        {
            var queue = new WardrobeWorkQueue(); var ran = false;
            var result = queue.EnqueueOperation(() => { ran = true; return 1; }); queue.Close();
            Assert.That(result.IsFaulted, Is.True); queue.Pump(true); Assert.That(ran, Is.False);
        }
    }
}


namespace OutfitToggleGenerator
{
    // Every asset and journal belongs to this disposable fixture, never to a user's avatar.
    public sealed class OperationExecutorTests
    {
        private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
        private readonly Dictionary<FieldInfo, object> saved = new Dictionary<FieldInfo, object>();
        private Scene previousScene, scene;
        private VRCAvatarDescriptor previousAvatar, avatar;
        private UnityEngine.Object previousSelection;
        private string folder, settings, uploadSettings, overrides, project, session, firstGuid, secondGuid;
        private WardrobeWorkQueue queue;
        private WardrobeOperationLedger ledger;
        private GameObject root;

        private void ReplaceField(Type type, string name, object value)
        {
            var field = type.GetField(name, PrivateStatic);
            Assert.That(field, Is.Not.Null, name);
            if (!saved.ContainsKey(field)) saved.Add(field, field.GetValue(null));
            field.SetValue(null, value);
        }
        private void Server(string name, object value) => ReplaceField(typeof(AvatarWardrobeServer), name, value);
        private void Catalog(string name, object value) => ReplaceField(typeof(AvatarWardrobeCatalog), name, value);
        private static object Invoke(string name, params object[] args) => typeof(AvatarWardrobeServer).GetMethod(name, PrivateStatic).Invoke(null, args);
        private string Revision() => (string)Invoke("Revision", avatar);
        private static string Global(UnityEngine.Object value) => GlobalObjectId.GetGlobalObjectIdSlow(value).ToString();

        private sealed class TestContext : SynchronizationContext
        {
            private readonly ConcurrentQueue<Action> callbacks = new ConcurrentQueue<Action>();
            public override void Post(SendOrPostCallback callback, object state) => callbacks.Enqueue(() => callback(state));
            internal void Drain() { for (var n = 0; n < 100 && callbacks.TryDequeue(out var action); n++) action(); }
        }
        private SynchronizationContext previousContext;
        private TestContext testContext;
        private void DrainUntil(Func<bool> complete)
        {
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!complete() && DateTime.UtcNow < deadline) { testContext.Drain(); Thread.Sleep(1); }
            Assert.That(complete(), Is.True, "Asynchronous Unity fixture work did not settle.");
        }
        private void PumpOperation()
        {
            queue.Pump(true);
            DrainUntil(() => !queue.OperationRunning);
            var publication = typeof(AvatarWardrobeServer).GetField("contextPublication", PrivateStatic).GetValue(null) as Task;
            if (publication != null) DrainUntil(() => publication.IsCompleted);
        }
        [SetUp] public void Setup()
        {
            settings = AvatarWardrobePresets.CaptureSettings();
            uploadSettings = OutfitProjectData.CaptureSettings();
            overrides = AvatarWardrobeCatalog.CaptureOverrides();
            previousAvatar = AvatarWardrobeServer.SceneAvatar;
            previousSelection = Selection.activeObject;
            previousContext = SynchronizationContext.Current;
            testContext = new TestContext(); SynchronizationContext.SetSynchronizationContext(testContext);
            Server("contextPublication", null); Server("contextGeneration", 0);
            Server("cachedSourceFingerprint", ""); Server("cachedVisualFingerprint", ""); Server("cachedSourceAvatar", 0);
            Server("cachedFingerprintGeneration", -1); Server("cachedVisualSettings", ""); Server("nextFullFingerprint", 0d);
            Catalog("catalogLoad", null); Catalog("nextCatalogPoll", double.MaxValue);
            previousScene = WardrobeTestSceneFixture.RequireSavedActiveScene();
            folder = "Assets/OperationFixture_" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(folder); AssetDatabase.Refresh();
            scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.SetActiveScene(scene);
            root = new GameObject("Operation fixture avatar");
            avatar = root.AddComponent<VRCAvatarDescriptor>();
            EditorSceneManager.SaveScene(scene, folder + "/Avatar.unity");
            AvatarWardrobeServer.SceneAvatar = avatar;
            AvatarWardrobePresets.RestoreSettings("{\"presets\":[],\"commonPresets\":[],\"assignments\":[]}");
            OutfitProjectData.RestoreSettings(null);
            AvatarWardrobeCatalog.RestoreOverrides("{\"entries\":[],\"avatarOverrides\":[],\"fitTrust\":[]}");

            var partSource = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var part = PrefabUtility.SaveAsPrefabAsset(partSource, folder + "/NestedPart.prefab");
            UnityEngine.Object.DestroyImmediate(partSource);
            var first = MakePrefab("Coat Blue", part);
            var second = MakePrefab("Coat Red", part);
            firstGuid = first.guid; secondGuid = second.guid;
            Catalog("cache", new WardrobeCatalogCache { version = 4, records = new List<WardrobeAssetRecord> { first, second } });
            Catalog("cacheLoaded", true);
            Catalog("lookupCacheEpoch", -1); Catalog("lookupCacheOverrides", -1);
            // Lookup builders may replace these derived caches; restore them as well.
            Catalog("avatarCache", null); Catalog("guidCache", null);
            Server("familiesCache", null); Server("familiesCacheKey", null);
            project = Directory.GetParent(Application.dataPath).FullName;
            session = "operation-fixture-" + Guid.NewGuid().ToString("N");
            queue = new WardrobeWorkQueue();
            ledger = new WardrobeOperationLedger(Path.Combine(folder, "receipts.json"), project, session);
            Server("serverProjectPath", project); Server("serverSession", session);
            Server("operationLedger", ledger); Server("dispatcher", queue);
            Server("operationContextJson", "{}"); Server("nextOperationContext", 0d); Server("activeOperationId", null); Server("lastOperationUndo", null);
            Invoke("PublishOperationContext", true);
            DrainUntil(() => ((Task)typeof(AvatarWardrobeServer).GetField("contextPublication", PrivateStatic).GetValue(null)).IsCompleted);
        }

        private WardrobeAssetRecord MakePrefab(string name, GameObject nested)
        {
            var source = new GameObject(name);
            PrefabUtility.InstantiatePrefab(nested, source.transform);
            var path = folder + "/" + name + ".prefab";
            PrefabUtility.SaveAsPrefabAsset(source, path);
            UnityEngine.Object.DestroyImmediate(source);
            return new WardrobeAssetRecord { guid = AssetDatabase.AssetPathToGUID(path), assetPath = path,
                displayName = name, kind = WardrobeAssetKind.Outfit, confidence = 1, familyId = "fixture-coat",
                familyName = "Fixture Coat", variantName = name, rendererCount = 1, meshIds = new List<string> { "fixture-cube" } };
        }

        [TearDown] public void Cleanup()
        {
            queue?.Close(); Undo.ClearAll();
            AvatarWardrobeServer.SceneAvatar = previousAvatar;
            Selection.activeObject = previousSelection;
            if (previousScene.IsValid()) SceneManager.SetActiveScene(previousScene);
            if (scene.IsValid()) EditorSceneManager.CloseScene(scene, true);
            AvatarWardrobePresets.RestoreSettings(settings);
            OutfitProjectData.RestoreSettings(uploadSettings);
            AvatarWardrobeCatalog.RestoreOverrides(overrides);
            foreach (var pair in saved.Reverse()) pair.Key.SetValue(null, pair.Value);
            saved.Clear();
            SynchronizationContext.SetSynchronizationContext(previousContext);
            if (!string.IsNullOrEmpty(folder)) AssetDatabase.DeleteAsset(folder);
        }

        private WardrobeOperation Command(string type = "wear-outfit", string guid = null, string instance = null) => new WardrobeOperation
        {
            id = Guid.NewGuid().ToString(), type = type,
            target = new WardrobeOperation.Target { projectId = project, sceneGuid = AssetDatabase.AssetPathToGUID(scene.path),
                avatarId = Global(avatar), avatarInstanceId = avatar.GetInstanceID(), session = session, scopeId = "common" },
            precondition = new WardrobeOperation.Precondition { observedRevision = Revision(), afterOperationId = "" },
            payload = new WardrobeOperation.Payload { variantId = guid ?? firstGuid,
                assetVersion = AssetDatabase.GetAssetDependencyHash(AssetDatabase.GUIDToAssetPath(guid ?? firstGuid)).ToString(),
                instanceId = instance ?? "", addCopy = true, allowUnverified = true, zoom = 1 }
        };
        private WardrobeOperationReceipt Accept(WardrobeOperation command) => AvatarWardrobeServer.AcceptOperation(command, command.target.avatarInstanceId);
        private WardrobeOperationReceipt Apply(WardrobeOperation command)
        {
            Assert.That(Accept(command).state, Is.EqualTo("queued"));
            for (var remaining = 128; remaining > 0 && ledger.Get(command.id).state == "queued" && queue.Count > 0; remaining--) PumpOperation();
            return ledger.Get(command.id);
        }
        private GameObject Added(WardrobeOperationReceipt receipt)
        {
            Assert.That(receipt.state, Is.EqualTo("succeeded"), receipt.error);
            Assert.That(receipt.result.addedInstanceId, Is.Not.Zero);
            var added = EditorUtility.InstanceIDToObject(receipt.result.addedInstanceId) as GameObject;
            Assert.That(added, Is.Not.Null);
            Assert.That(receipt.result.addedGlobalObjectId, Is.EqualTo(Global(added)));
            return added;
        }

        [Test] public void TwoQueuedAddsWithSameObservedRevisionUseConfirmedPredecessor()
        {
            var first = Command(); var second = Command();
            Assert.That(first.precondition.observedRevision, Is.EqualTo(second.precondition.observedRevision));
            second.precondition.afterOperationId = first.id;
            Accept(first); Accept(second);
            Assert.That(AvatarWardrobePresets.PrefabInstances(avatar, firstGuid), Is.Empty);
            Assert.That(queue.Count, Is.EqualTo(2));
            PumpOperation();
            var appliedFirst = ledger.Get(first.id); var firstRoot = Added(appliedFirst);
            Assert.That(ledger.Get(second.id).state, Is.EqualTo("queued"));
            PumpOperation();
            var appliedSecond = ledger.Get(second.id); var secondRoot = Added(appliedSecond);
            Assert.That(appliedSecond.result.sourceRevision, Is.EqualTo(appliedFirst.result.confirmedRevision));
            Assert.That(firstRoot, Is.Not.SameAs(secondRoot));
            Assert.That(AvatarWardrobePresets.PrefabInstances(avatar, firstGuid).Count, Is.EqualTo(2));
            Assert.That(appliedSecond.result.affectedInstanceIds, Is.EqualTo(new[] { Global(secondRoot) }), "Nested prefab children must not be reported as separate outfit copies.");
            Assert.That(appliedSecond.result.unsaved, Is.True);
        }

        [Test] public void AcceptedCommandAndReceiptMutationCannotChangeDispatchedEditOrDuplicateIt()
        {
            var command = Command(); var original = JsonUtility.FromJson<WardrobeOperation>(JsonUtility.ToJson(command));
            var receipt = Accept(command);
            command.payload.variantId = secondGuid; receipt.command.payload.variantId = secondGuid;
            Assert.That(Accept(original).state, Is.EqualTo("queued"));
            Assert.That(queue.Count, Is.EqualTo(1)); PumpOperation();
            var result = ledger.Get(original.id); Added(result);
            Assert.That(AvatarWardrobePresets.PrefabInstances(avatar, firstGuid).Count, Is.EqualTo(1));
            Assert.That(AvatarWardrobePresets.PrefabInstances(avatar, secondGuid), Is.Empty);
            Assert.That(Accept(original).state, Is.EqualTo("succeeded")); PumpOperation();
            Assert.That(AvatarWardrobePresets.PrefabInstances(avatar, firstGuid).Count, Is.EqualTo(1));
            Assert.Throws<InvalidOperationException>(() => Accept(command));
        }

        [Test] public void ExactCopyReplaceAndRemovePreserveOtherCopyAndPlacement()
        {
            var untouched = Added(Apply(Command()));
            var replaced = Added(Apply(Command()));
            replaced.transform.localPosition = new Vector3(0.2f, 0.4f, -0.1f);
            replaced.transform.localScale = Vector3.one * 0.8f; replaced.SetActive(false);
            var replacedId = replaced.GetInstanceID(); var replacedGlobal = Global(replaced);
            var replacement = Apply(Command("replace-outfit", secondGuid, replacedId.ToString()));
            var next = Added(replacement);
            Assert.That(replaced == null, Is.True); Assert.That(untouched != null, Is.True);
            Assert.That(next.transform.localPosition, Is.EqualTo(new Vector3(0.2f, 0.4f, -0.1f)));
            Assert.That(next.transform.localScale, Is.EqualTo(Vector3.one * 0.8f)); Assert.That(next.activeSelf, Is.False);
            Assert.That(replacement.result.removedInstanceId, Is.EqualTo(replacedId));
            Assert.That(replacement.result.removedGlobalObjectId, Is.EqualTo(replacedGlobal));
            Assert.That(replacement.result.removedVariantId, Is.EqualTo(firstGuid));
            var nextId = next.GetInstanceID(); var nextGlobal = Global(next);
            var removal = Apply(Command("remove-outfit", secondGuid, nextGlobal));
            Assert.That(removal.state, Is.EqualTo("succeeded"), removal.error);
            Assert.That(removal.result.addedInstanceId, Is.Zero);
            Assert.That(removal.result.removedInstanceId, Is.EqualTo(nextId));
            Assert.That(removal.result.affectedInstanceIds, Is.EqualTo(new[] { nextGlobal }));
            Assert.That(next == null, Is.True); Assert.That(untouched != null, Is.True);
            Assert.That(AvatarWardrobePresets.PrefabInstances(avatar, firstGuid), Is.EqualTo(new[] { untouched }));
        }

        [Test] public void AddFromExistingInstanceDoesNotClaimThatSourceWasRemoved()
        {
            var source = Added(Apply(Command()));
            var result = Apply(Command("wear-outfit", firstGuid, Global(source)));
            var copy = Added(result);
            Assert.That(source != null, Is.True); Assert.That(copy, Is.Not.SameAs(source));
            Assert.That(result.result.removedInstanceId, Is.Zero);
            Assert.That(result.result.removedGlobalObjectId, Is.Empty);
            Assert.That(result.result.affectedInstanceIds, Is.EqualTo(new[] { Global(copy) }));
        }

        [Test] public void ConfirmedPredecessorCannotAuthorizeAnotherScope()
        {
            var first = Command(); Added(Apply(first));
            var second = Command(); second.target.scopeId = "another-preset";
            second.precondition.afterOperationId = first.id;
            var result = Apply(second);
            Assert.That(result.state, Is.EqualTo("needs-review"));
            StringAssert.Contains("exact target", result.error);
            Assert.That(AvatarWardrobePresets.PrefabInstances(avatar, firstGuid).Count, Is.EqualTo(1));
        }

        [TestCase("revision")]
        [TestCase("scene")]
        [TestCase("avatar")]
        [TestCase("asset")]
        public void StalePreconditionsRejectBeforeChangingSceneOrSettings(string stale)
        {
            var command = Command();
            if (stale == "revision") command.precondition.observedRevision = "stale";
            if (stale == "scene") command.target.sceneGuid = new string('f', 32);
            if (stale == "avatar") command.target.avatarId = "GlobalObjectId_V1-2-" + new string('f', 32) + "-42-0";
            if (stale == "asset") command.payload.assetVersion = "stale";
            var before = AvatarWardrobePresets.CaptureSettings();
            var result = Apply(command);
            Assert.That(result.state, Is.EqualTo("needs-review"));
            Assert.That(root.transform.childCount, Is.Zero);
            Assert.That(AvatarWardrobePresets.CaptureSettings(), Is.EqualTo(before));
        }

        [Test] public void MissingOrWrongTargetPredecessorNeverExecutesDependentEdit()
        {
            var first = Command(); Accept(first); ledger.Cancel(first.id);
            var failed = Command(); failed.precondition.afterOperationId = first.id;
            Assert.That(Apply(failed).state, Is.EqualTo("needs-review"));
            var missing = Command(); missing.precondition.afterOperationId = Guid.NewGuid().ToString();
            Assert.That(Apply(missing).state, Is.EqualTo("needs-review"));
            Assert.That(root.transform.childCount, Is.Zero);
        }

        private WardrobeOperation UndoCommand(WardrobeOperationReceipt original)
        {
            var command = Command("undo-operation");
            command.payload.variantId = ""; command.payload.assetVersion = "";
            command.payload.undoToken = original.result.undoToken;
            return command;
        }

        [TestCase("wear-outfit")]
        [TestCase("remove-outfit")]
        [TestCase("replace-outfit")]
        public void ScopedUndoRestoresExactSceneAndAllThreeSettingsStores(string type)
        {
            GameObject original = null;
            if (type != "wear-outfit") original = Added(Apply(Command()));
            var originalId = original == null ? 0 : original.GetInstanceID();
            if (original != null) original.transform.localPosition = new Vector3(0.3f, 0.7f, 0.2f);
            var remote = OutfitProjectData.GetOutfit("Operation fixture avatar", "Existing uploaded preset");
            remote.blueprintId = "avtr-existing-remote-record"; OutfitProjectData.Save();
            var beforePresets = AvatarWardrobePresets.CaptureSettings();
            var beforeOverrides = AvatarWardrobeCatalog.CaptureOverrides();
            var beforeUploads = OutfitProjectData.CaptureSettings();
            var command = Command(type, type == "replace-outfit" ? secondGuid : firstGuid, originalId == 0 ? "" : originalId.ToString());
            var applied = Apply(command);
            Assert.That(applied.state, Is.EqualTo("succeeded"), applied.error);
            Assert.That(applied.result.undoToken, Is.Not.Empty);
            var undone = Apply(UndoCommand(applied));
            Assert.That(undone.state, Is.EqualTo("succeeded"), undone.error);
            Assert.That(undone.result.undidOperationId, Is.EqualTo(command.id));
            Assert.That(AvatarWardrobePresets.CaptureSettings(), Is.EqualTo(beforePresets));
            Assert.That(AvatarWardrobeCatalog.CaptureOverrides(), Is.EqualTo(beforeOverrides));
            Assert.That(OutfitProjectData.CaptureSettings(), Is.EqualTo(beforeUploads));
            if (type == "wear-outfit") Assert.That(AvatarWardrobePresets.PrefabInstances(avatar, firstGuid), Is.Empty);
            else
            {
                var restored = EditorUtility.InstanceIDToObject(originalId) as GameObject;
                Assert.That(restored, Is.Not.Null); Assert.That(restored.transform.localPosition, Is.EqualTo(new Vector3(0.3f, 0.7f, 0.2f)));
                Assert.That(AvatarWardrobePresets.PrefabInstances(avatar, firstGuid), Is.EqualTo(new[] { restored }));
                Assert.That(AvatarWardrobePresets.PrefabInstances(avatar, secondGuid), Is.Empty);
            }
            Assert.That(Apply(UndoCommand(applied)).state, Is.EqualTo("needs-review"), "A session Undo token cannot be consumed twice.");
        }

        [Test] public void UnrelatedNativeSceneEditBlocksScopedUndoWithoutRevertingEitherEdit()
        {
            var applied = Apply(Command()); var added = Added(applied);
            Undo.IncrementCurrentGroup();
            var unrelated = new GameObject("Unrelated Unity edit");
            Undo.RegisterCreatedObjectUndo(unrelated, "Unrelated Unity edit");
            var undone = Apply(UndoCommand(applied));
            Assert.That(undone.state, Is.EqualTo("needs-review")); StringAssert.Contains("Undo group changed", undone.error);
            Assert.That(unrelated != null, Is.True); Assert.That(added != null, Is.True);
        }

        [Test] public void UnrelatedCreationInSameNativeGroupAlsoBlocksScopedUndo()
        {
            var applied = Apply(Command()); var added = Added(applied);
            var unrelated = new GameObject("Unrelated same-group edit");
            Undo.RegisterCreatedObjectUndo(unrelated, "Another edit in the same group");
            var undone = Apply(UndoCommand(applied));
            Assert.That(undone.state, Is.EqualTo("needs-review"));
            Assert.That(unrelated != null, Is.True); Assert.That(added != null, Is.True);
        }

        [Test] public void DelayedNativePropertyEditInSameGroupInvalidatesUndoCapability()
        {
            var unrelated = new GameObject("Other scene object");
            var applied = Apply(Command()); var added = Added(applied);
            Undo.RecordObject(unrelated.transform, "Move another scene object");
            unrelated.transform.localPosition = Vector3.up * 4;
            var undone = Apply(UndoCommand(applied));
            Assert.That(undone.state, Is.EqualTo("needs-review"));
            Assert.That(unrelated.transform.localPosition, Is.EqualTo(Vector3.up * 4)); Assert.That(added != null, Is.True);
        }

        [Test] public void NewRemoteUploadRecordBlocksUndoAndIsNeverRolledBack()
        {
            var applied = Apply(Command()); var added = Added(applied);
            OutfitProjectData.GetOutfit("Operation fixture avatar", "Remote upload").blueprintId = "avtr-new-upload";
            OutfitProjectData.Save(); var newest = OutfitProjectData.CaptureSettings();
            var undone = Apply(UndoCommand(applied));
            Assert.That(undone.state, Is.EqualTo("needs-review"));
            Assert.That(OutfitProjectData.CaptureSettings(), Is.EqualTo(newest)); Assert.That(added != null, Is.True);
        }

        [Test] public void ExpiredUndoCheckpointFailsWithoutFallingBackToNativeGlobalUndo()
        {
            var applied = Apply(Command()); var added = Added(applied);
            Server("lastOperationUndo", null);
            var undone = Apply(UndoCommand(applied));
            Assert.That(undone.state, Is.EqualTo("needs-review")); Assert.That(added != null, Is.True);
        }

        [UnityTest] public IEnumerator IdleFramesDoNotBroadenNativeUndoGroupOwnership()
        {
            var applied = Apply(Command()); var added = Added(applied);
            var capturedGroup = Undo.GetCurrentGroup();
            yield return null; yield return null;
            var currentGroup = Undo.GetCurrentGroup();
            var undone = Apply(UndoCommand(applied));
            Debug.Log("Wardrobe scoped Undo idle group: " + capturedGroup + " -> " + currentGroup + "; result=" + undone.state);
            Assert.That(undone.state, Is.EqualTo(currentGroup == capturedGroup ? "succeeded" : "needs-review"), undone.error);
            if (currentGroup != capturedGroup) Assert.That(added != null, Is.True);
        }

        [Test] public void PublishedContextHttpReadCompletesWhileMainThreadOperationIsBusy()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            using (var listener = new HttpListener())
            {
                var url = "http://localhost:" + port + "/"; listener.Prefixes.Add(url); listener.Start();
                var serve = Task.Run(() =>
                {
                    var request = listener.GetContext();
                    try { return (bool)Invoke("HandleOperations", request, "/api/operation_context"); }
                    finally { request.Response.Close(); }
                });
                var work = queue.EnqueueOperation(() =>
                {
                    var client = Task.Run(() =>
                    {
                        var request = (HttpWebRequest)WebRequest.Create(url + "api/operation_context");
                        request.Proxy = null; request.Timeout = 2000;
                        using (var response = request.GetResponse())
                        using (var reader = new StreamReader(response.GetResponseStream())) return reader.ReadToEnd();
                    });
                    Assert.That(client.Wait(TimeSpan.FromSeconds(3)), Is.True, "Cached operation context waited behind the busy Unity operation.");
                    return client.Result;
                });
                PumpOperation();
                var context = JsonUtility.FromJson<AvatarWardrobeServer.OperationContext>(work.Result);
                Assert.That(context.projectId, Is.EqualTo(project)); Assert.That(context.avatarInstanceId, Is.EqualTo(avatar.GetInstanceID()));
                Assert.That(serve.Wait(TimeSpan.FromSeconds(3)), Is.True); Assert.That(serve.Result, Is.True);
            }
        }
    }
}
