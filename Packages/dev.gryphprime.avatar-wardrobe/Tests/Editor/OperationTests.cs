using System;
using System.Collections.Generic;
using System.IO;
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
