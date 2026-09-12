import tempfile
import unittest
from pathlib import Path

from atelier.core import Conflict, Store
from atelier.host import Application


GUID = "abcdefabcdefabcdefabcdefabcdefab"
TARGET = {"sceneGuid": GUID, "objectId": f"GlobalObjectId_V1-1-{GUID}-1-0", "name": "Avatar"}


class CoreStateTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.store = Store(self.tmp.name)
        self.workspace = self.store.register({"projectPath": str(Path(self.tmp.name) / "Project"), "name": "P"})
        self.wid = self.workspace["id"]
        self.store.set_target(self.wid, TARGET)

    def tearDown(self):
        self.store.close()
        self.tmp.cleanup()

    def test_stale_desired_revision_is_rejected(self):
        self.store.desired(self.wid, {"items": [], "appearance": {"tone": "warm"}}, 0)
        with self.assertRaises(Conflict):
            self.store.desired(self.wid, {"items": [], "appearance": {"tone": "cold"}}, 0)

    def test_reconcile_is_durable_and_predecessor_ordered(self):
        self.store.desired(self.wid, {"items": [], "appearance": {"tone": "warm"}}, 0)
        first = self.store.enqueue(self.wid, expected_revision=1)
        # A later draft can be accepted while the first Unity operation is pending.
        self.store.desired(self.wid, {"items": [], "appearance": {"tone": "cold"}}, 1)
        second = self.store.enqueue(self.wid, expected_revision=2)
        self.assertEqual(second["afterOperationId"], first["id"])
        reopened = Store(self.tmp.name)
        try:
            self.assertEqual(reopened.next_operation(self.wid)["id"], first["id"])
            dispatched = reopened.dispatch(first["id"], "unity-0")
            self.assertEqual(dispatched["state"], "dispatching")
            with self.assertRaises(Conflict): reopened.dispatch(second["id"], "unity-0")
        finally:
            reopened.close()

    def test_receipt_only_recovery_retry_and_dismissal(self):
        self.store.desired(self.wid, {"items": [], "appearance": {"tone": "warm"}}, 0)
        op = self.store.enqueue(self.wid, expected_revision=1)
        self.store.dispatch(op["id"], "unity-0")
        self.store.receipt(op["id"], {"id": op["id"], "state": "failed", "error": "lost response"})
        recovered = self.store.review(op["id"], "retry")
        self.assertEqual(recovered["state"], "running")
        # Dismissal is only allowed for an explicitly failed operation, never an
        # ambiguous recovery state; this prevents silently losing a Unity mutation.
        self.assertRaises(Conflict, self.store.review, op["id"], "dismiss")

    def test_stale_snapshot_artifact_cannot_replace_rendered_state(self):
        self.store.desired(self.wid, {"items": [], "appearance": {"tone": "warm"}}, 0)
        sync = self.store.enqueue(self.wid, expected_revision=1)
        self.store.dispatch(sync["id"], "unity-0")
        self.store.receipt(sync["id"], {"id": sync["id"], "state": "succeeded", "revision": "unity-1"})
        snap = self.store.enqueue(self.wid, "snapshot", view="front")
        self.store.dispatch(snap["id"], "unity-1")
        self.store.receipt(snap["id"], {"id": snap["id"], "state": "succeeded", "revision": "unity-1"})
        self.store.desired(self.wid, {"items": [], "appearance": {"new": 1}}, 1)
        sync2 = self.store.enqueue(self.wid, expected_revision=2)
        self.store.dispatch(sync2["id"], "unity-1")
        self.store.receipt(sync2["id"], {"id": sync2["id"], "state": "succeeded", "revision": "unity-2"})
        old = self.store.add_artifact(snap["id"], b"\x89PNG\r\n\x1a\nbytes", {})
        self.assertIsNone(self.store.workspace(self.wid)["rendered"])


class _Bridge:
    def __init__(self, target):
        self.target = target
        self.polls = 0

    def context(self):
        return {"projectPath": self.target["projectPath"], "revision": "unity-0", "targets": [TARGET]}

    def submit(self, operation):
        return {"id": operation["id"], "state": "queued"}

    def poll(self, operation_id):
        self.polls += 1
        return {"id": operation_id, "state": "succeeded", "revision": "unity-1"}


class _Worker:
    def __init__(self, project, unity, state_dir=None):
        self.project = project
        self.bridge = _Bridge({"projectPath": project})

    def status(self): return {"state": "online"}
    def client(self): return self.bridge
    def stop(self): pass


class HostStepTests(unittest.TestCase):
    def test_host_step_recovers_running_receipt_and_preserves_queue(self):
        tmp = tempfile.TemporaryDirectory()
        app = Application(tmp.name, worker_factory=_Worker)
        try:
            workspace = app.store.register({"projectPath": str(Path(tmp.name) / "Project"), "name": "P"})
            wid = workspace["id"]
            app.store.set_target(wid, TARGET)
            app.store.desired(wid, {"items": [], "appearance": {"tone": "warm"}}, 0)
            op = app.store.enqueue(wid, expected_revision=1)
            app.step(wid)  # submit returned only a queued receipt: operation remains running
            self.assertEqual(app.store.operation(op["id"])["state"], "running")
            app.step(wid)  # poll supplies the eventual receipt
            self.assertEqual(app.store.operation(op["id"])["state"], "succeeded")
            self.assertTrue(any(x["action"] == "snapshot" for x in app.store.operations(wid)))
        finally:
            app.close()
            tmp.cleanup()


if __name__ == "__main__": unittest.main()
