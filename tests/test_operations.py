"""Durability, identity and dispatch recovery guarantees of the real SQLite queue."""
import copy
from pathlib import Path
import sqlite3
import sys
import tempfile
import threading
import time
import unittest
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'Packages/dev.gryphprime.avatar-wardrobe/Desktop'))
from wardrobe_operations import OperationQueue, OperationDriver


def command(project, kind='wear-outfit', **target_fields):
    target = {'projectId': str(project), 'sceneGuid': 'a' * 32,
              'avatarId': 'GlobalObjectId_V1-2-' + 'a' * 32 + '-42-0',
              'avatarInstanceId': 42, 'session': 'session-a', 'scopeId': 'common'}
    target.update(target_fields)
    payload = {'variantId': 'b' * 32, 'assetVersion': 'c' * 32, 'zoom': 1}
    if kind in ('remove-outfit', 'replace-outfit'):
        payload['instanceId'] = '123'
    if kind == 'render-snapshot':
        payload.update(previewToken='preview-token', view='front')
    return {'id': str(uuid.uuid4()), 'type': kind, 'target': target,
            'precondition': {'observedRevision': 'revision-0', 'afterOperationId': ''}, 'payload': payload}


class FakeBridge:
    def __init__(self, target):
        self.current = dict(target, revision='revision-0', waitingReason='')
        self.submits, self.polls, self.cancels = [], [], []
        self.receipts = {}
        self.offline = False
        self.submit_error = None
    def context(self):
        if self.offline: raise ConnectionError('offline')
        return self.current
    def submit(self, command, execute_revision=None):
        self.submits.append((copy.deepcopy(command), execute_revision))
        self.receipts[command['id']] = {'id': command['id'], 'state': 'running'}
        if self.submit_error: raise self.submit_error
        return self.receipts[command['id']]
    def poll(self, identifier):
        self.polls.append(identifier)
        if self.offline: raise ConnectionError('offline')
        return self.receipts.get(identifier, {'id': identifier, 'state': 'needs-review', 'error': 'Unknown receipt'})
    def cancel(self, identifier):
        self.cancels.append(identifier)
        return {'id': identifier, 'state': 'running', 'cancelRequested': True}


class OperationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name).resolve()
        self.path = self.root / 'operations.db'
        self.now = [100.0]
        self.queues = []
        self.queue = self.open()
        self.first = command(self.root)
        self.bridge = FakeBridge(self.first['target'])
        self.driver = OperationDriver(self.queue, self.bridge)
    def open(self, **kwargs):
        queue = OperationQueue(self.path, str(self.root), clock=lambda: self.now[0], **kwargs)
        self.queues.append(queue)
        return queue
    def tearDown(self):
        for queue in self.queues: queue.close()
        self.temp.cleanup()
    def submit(self, kind='wear-outfit', **target):
        value = command(self.root, kind, **target)
        self.queue.submit(value)
        return value
    def succeed(self, identifier, revision='revision-1'):
        self.bridge.receipts[identifier] = {'id': identifier, 'state': 'succeeded', 'result': {'confirmedRevision': revision, 'unsaved': True}}
        return self.driver.step()

    def test_uuid_payload_idempotency_and_immutable_caller_object(self):
        accepted = self.queue.submit(self.first)
        self.assertEqual(accepted, self.queue.submit(copy.deepcopy(self.first)))
        self.first['payload']['variantId'] = 'd' * 32
        self.assertEqual('b' * 32, self.queue.get(self.first['id'])['command']['payload']['variantId'])
        with self.assertRaises(ValueError): self.queue.submit(self.first)

    def test_strict_nested_bounds_and_types(self):
        cases = [(['unknown'], 1), (['target', 'other'], 'x'), (['target', 'avatarInstanceId'], True),
                 (['target', 'avatarInstanceId'], 0), (['target', 'avatarInstanceId'], 2**40),
                 (['target', 'avatarId'], '42'), (['target', 'sceneGuid'], '../scene'),
                 (['target', 'session'], 's' * 257), (['target', 'scopeId'], ['common']),
                 (['precondition', 'observedRevision'], {'revision': 1}), (['precondition', 'other'], 1),
                 (['payload', 'extra'], 1), (['payload', 'addCopy'], 'false'), (['payload', 'zoom'], True),
                 (['payload', 'zoom'], float('nan')), (['payload', 'zoom'], float('inf')),
                 (['payload', 'zoom'], 0), (['payload', 'zoom'], 3.1), (['payload', 'view'], 'sideways'),
                 (['payload', 'variantId'], 'not-an-asset'), (['payload', 'itemPath'], 'x' * 4097),
                 (['payload', 'instanceId'], {'arbitrary': ['nested']}), (['id'], uuid.uuid4().hex)]
        for path, value in cases:
            with self.subTest(path=path, value=value):
                invalid = copy.deepcopy(self.first)
                target = invalid
                for name in path[:-1]: target = target[name]
                target[path[-1]] = value
                with self.assertRaises(ValueError): self.queue.submit(invalid)
        invalid = copy.deepcopy(self.first); invalid['target']['projectId'] += '/..'
        with self.assertRaises(ValueError): self.queue.submit(invalid)
        self.assertEqual([], self.queue.list())

    def test_current_avatar_preview_and_capture_allow_empty_variant(self):
        for kind in ('capture-source', 'prepare-preview'):
            value = command(self.root, kind); value['payload']['variantId'] = ''
            self.assertEqual('queued', self.queue.submit(value)['state'])

    def test_cancel_queued_is_terminal_and_running_is_only_a_request(self):
        queued = self.submit()
        self.assertEqual('cancelled', self.queue.cancel(queued['id'])['state'])
        running = self.submit(); self.driver.step()
        result = self.queue.cancel(running['id'])
        self.assertEqual('running', result['state']); self.assertTrue(result['cancelRequested'])
        self.driver.step(); self.driver.step()
        self.assertEqual([running['id']], self.bridge.cancels)
        self.assertEqual([running['id']], self.bridge.polls)
        self.assertEqual('succeeded', self.succeed(running['id'])['state'])

    def test_previews_only_supersede_undispatched_matching_scope_and_target(self):
        first = self.submit('prepare-preview')
        other_scope = self.submit('prepare-preview', scopeId='evening')
        other_avatar = self.submit('prepare-preview', avatarInstanceId=99)
        fresh = self.submit('prepare-preview')
        self.assertEqual('superseded', self.queue.get(first['id'])['state'])
        self.assertEqual('queued', self.queue.get(other_scope['id'])['state'])
        self.assertEqual('queued', self.queue.get(other_avatar['id'])['state'])
        self.queue.cancel(other_scope['id']); self.queue.cancel(other_avatar['id'])
        self.driver.step(); self.submit('prepare-preview')
        self.assertEqual('running', self.queue.get(fresh['id'])['state'])

    def test_predecessor_target_check_and_unknown_rejection(self):
        first = self.submit()
        for fields in ({'scopeId': 'other'}, {'session': 'other'}, {'avatarInstanceId': 7}):
            dependent = command(self.root, **fields)
            dependent['precondition']['afterOperationId'] = first['id']
            with self.assertRaises(ValueError): self.queue.submit(dependent)
        dependent = command(self.root); dependent['precondition']['afterOperationId'] = str(uuid.uuid4())
        with self.assertRaises(ValueError): self.queue.submit(dependent)

    def test_ordered_edits_use_confirmed_revision_not_same_observed_revision(self):
        first = self.submit()
        second = command(self.root); second['precondition']['afterOperationId'] = first['id']
        self.queue.submit(second)
        self.driver.step(); self.driver.step()
        self.assertEqual(1, len(self.bridge.submits))
        self.assertEqual('queued', self.queue.get(second['id'])['state'])
        self.succeed(first['id'], 'new-confirmed-revision')
        self.driver.step()
        self.assertEqual('new-confirmed-revision', self.bridge.submits[1][1])
        self.assertEqual('revision-0', self.bridge.submits[1][0]['precondition']['observedRevision'])

    def test_failed_predecessor_prevents_dependent_execution(self):
        first = self.submit()
        second = command(self.root); second['precondition']['afterOperationId'] = first['id']
        self.queue.submit(second); self.queue.cancel(first['id']); self.driver.step()
        self.assertEqual('failed', self.queue.get(second['id'])['state'])
        self.assertEqual([], self.bridge.submits)

    def test_two_connections_cannot_steal_live_project_writer(self):
        value = self.submit(); self.driver.step()
        second_queue = self.open(); second_driver = OperationDriver(second_queue, self.bridge)
        self.assertEqual('running', second_queue.get(value['id'])['state'])
        self.assertFalse(second_queue.acquire_lease()); self.assertIsNone(second_driver.step())
        self.assertEqual(1, len(self.bridge.submits))
        self.driver.step(); self.assertEqual([value['id']], self.bridge.polls)

    def test_expired_writer_recovers_crash_after_dispatch_without_second_submit(self):
        class ProcessDeath(BaseException): pass
        value = self.submit(); self.bridge.submit_error = ProcessDeath()
        with self.assertRaises(ProcessDeath): self.driver.step()
        reopened = self.open()
        self.assertEqual('running', reopened.get(value['id'])['state'])
        self.now[0] += 31
        self.bridge.receipts[value['id']] = {'id': value['id'], 'state': 'succeeded', 'result': {'confirmedRevision': 'recovered'}}
        result = OperationDriver(reopened, self.bridge).step()
        self.assertEqual('succeeded', result['state'])
        self.assertEqual(1, len(self.bridge.submits)); self.assertEqual([value['id']], self.bridge.polls)
        # A stale owner cannot overwrite the new writer's confirmed result.
        self.queue.finish(value['id'], 'failed', error='stale response')
        self.assertEqual('succeeded', self.queue.get(value['id'])['state'])

    def test_crash_in_gap_before_network_is_not_replayed(self):
        value = self.submit(); self.assertTrue(self.queue.acquire_lease())
        self.assertTrue(self.queue.dispatched(value['id'], 'revision-0'))
        self.now[0] += 31
        result = OperationDriver(self.open(), self.bridge).step()
        self.assertEqual('needs-review', result['state'])
        self.assertEqual([], self.bridge.submits)
        self.assertEqual([value['id']], self.bridge.polls)

    def test_lost_accept_response_only_polls_and_unknown_receipt_needs_review(self):
        value = self.submit(); self.bridge.submit_error = ConnectionError('response lost')
        result = self.driver.step()
        self.assertEqual('running', result['state']); self.assertIn('unknown', result['waitingReason'])
        del self.bridge.receipts[value['id']]
        self.assertEqual('needs-review', self.driver.step()['state'])
        for _ in range(3): self.driver.step()
        self.assertEqual(1, len(self.bridge.submits))

    def test_offline_intent_is_queued_and_stale_session_is_never_replayed(self):
        value = self.submit(); self.bridge.offline = True
        self.assertEqual('queued', self.driver.step()['state'])
        self.assertIn('offline', self.queue.get(value['id'])['waitingReason'])
        self.bridge.offline = False; self.bridge.current['session'] = 'new-session'
        self.assertEqual('needs-review', self.driver.step()['state'])
        self.assertEqual([], self.bridge.submits)

    def test_busy_unity_waits_before_dispatch(self):
        value = self.submit(); self.bridge.current['waitingReason'] = 'Compiling scripts'
        result = self.driver.step()
        self.assertEqual('queued', result['state']); self.assertEqual('Compiling scripts', result['waitingReason'])
        self.assertEqual([], self.bridge.submits)
        self.bridge.current['waitingReason'] = ''; self.driver.step()
        self.assertEqual(1, len(self.bridge.submits))

    def test_retention_preserves_id_tombstone_and_predecessor_confirmation(self):
        queue = self.queue
        queue.max_completed = 1
        first = self.submit(); self.driver.step(); self.succeed(first['id'], 'retained-revision')
        other = self.submit(); self.driver.step(); self.succeed(other['id'], 'other-revision')
        old = queue.get(first['id'])
        self.assertTrue(old['receiptExpired']); self.assertIsNone(old['command'])
        self.assertEqual('retained-revision', old['result']['confirmedRevision'])
        self.assertEqual(old, queue.submit(first))
        changed = copy.deepcopy(first); changed['payload']['zoom'] = 2
        with self.assertRaises(ValueError): queue.submit(changed)
        third = command(self.root); third['precondition']['afterOperationId'] = first['id']
        queue.submit(third); self.driver.step()
        self.assertEqual('retained-revision', self.bridge.submits[-1][1])
        self.assertEqual(1, queue.db.execute("SELECT count(*) FROM operations WHERE state='succeeded'").fetchone()[0])

    def test_unknown_receipt_is_explicitly_needs_review(self):
        identifier = str(uuid.uuid4())
        self.assertEqual('needs-review', self.queue.get(identifier)['state'])
        self.assertEqual('needs-review', self.queue.cancel(identifier)['state'])

    def test_network_wait_does_not_hold_database_lock(self):
        self.submit()
        entered, release = threading.Event(), threading.Event()
        original = self.bridge.submit
        def delayed(*args, **kwargs):
            entered.set(); release.wait(3)
            return original(*args, **kwargs)
        self.bridge.submit = delayed
        worker = threading.Thread(target=self.driver.step); worker.start()
        try:
            self.assertTrue(entered.wait(2))
            second = self.open()
            started = time.monotonic()
            value = command(self.root)
            self.assertEqual('queued', second.submit(value)['state'])
            self.assertEqual('cancelled', second.cancel(value['id'])['state'])
            self.assertLess(time.monotonic() - started, 0.5)
        finally:
            release.set(); worker.join(3)
        self.assertFalse(worker.is_alive())

    def test_new_capture_supersedes_only_same_target_undispatched_preview(self):
        previous = self.submit('prepare-preview')
        other = self.submit('capture-source', scopeId='other')
        latest = self.submit('capture-source')
        self.assertEqual('superseded', self.queue.get(previous['id'])['state'])
        self.assertEqual('queued', self.queue.get(other['id'])['state'])
        self.assertEqual('queued', self.queue.get(latest['id'])['state'])

    def test_preview_supersession_preserves_explicit_predecessor_chain(self):
        previous = self.submit('prepare-preview')
        middle = command(self.root, 'capture-source')
        middle['precondition']['afterOperationId'] = previous['id']
        self.queue.submit(middle)
        last = command(self.root, 'render-snapshot')
        last['precondition']['afterOperationId'] = middle['id']
        self.queue.submit(last)
        self.assertEqual(['queued'] * 3, [self.queue.get(value['id'])['state'] for value in (previous, middle, last)])

    def test_new_session_requires_review_of_unsaved_success_but_keeps_photos(self):
        edit = self.submit(); self.driver.step(); self.succeed(edit['id'])
        photo = self.submit('capture-source'); self.driver.step(); self.succeed(photo['id'])
        before = self.queue.get(edit['id'])['result']
        self.assertEqual(0, self.queue.reconcile_session(self.bridge.current))
        context = dict(self.bridge.current, session='new-session')
        self.assertEqual(1, self.queue.reconcile_session(context))
        self.assertEqual('needs-review', self.queue.get(edit['id'])['state'])
        self.assertEqual(before, self.queue.get(edit['id'])['result'])
        self.assertEqual('succeeded', self.queue.get(photo['id'])['state'])
        self.assertIn('will not be replayed', self.queue.get(edit['id'])['error'])
        self.assertEqual(0, self.queue.reconcile_session(context))
        self.assertEqual(2, len(self.bridge.submits))

    def test_tombstone_unsaved_success_reconciles_and_foreign_context_is_rejected(self):
        self.queue.max_completed = 0
        edit = self.submit(); self.driver.step(); self.succeed(edit['id'])
        self.assertTrue(self.queue.get(edit['id'])['receiptExpired'])
        with self.assertRaises(ValueError):
            self.queue.reconcile_session(dict(self.bridge.current, projectId='/foreign', session='new'))
        self.assertEqual('succeeded', self.queue.get(edit['id'])['state'])
        self.queue.reconcile_session(dict(self.bridge.current, session='new'))
        self.assertEqual('needs-review', self.queue.get(edit['id'])['state'])
        self.assertEqual('needs-review', self.queue.submit(edit)['state'])

    def test_invalid_bridge_receipt_is_needs_review_instead_of_crashing_driver(self):
        for receipt in ({'state': []}, {'state': 'succeeded', 'result': {'bad': float('nan')}}):
            value = self.submit(); self.driver.step()
            self.bridge.receipts[value['id']] = dict(receipt, id=value['id'])
            self.assertEqual('needs-review', self.driver.step()['state'])

    def test_undo_is_a_durable_mutation_and_requires_a_session_token(self):
        invalid = command(self.root, 'undo-operation')
        invalid['payload'] = {'zoom': 1}
        with self.assertRaises(ValueError): self.queue.submit(invalid)
        invalid['payload']['undoToken'] = str(uuid.uuid4())
        self.queue.submit(invalid)
        self.assertTrue(self.queue.has_pending_mutations())
        self.driver.step()
        self.assertEqual('undo-operation', self.bridge.submits[0][0]['type'])

    def test_full_queue_is_bounded(self):
        self.queue.max_active = 1
        self.submit()
        with self.assertRaises(OverflowError): self.submit()
        self.assertEqual(1, len(self.queue.list()))


if __name__ == '__main__': unittest.main()
