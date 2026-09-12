import copy
from pathlib import Path
import tempfile
import unittest

from atelier.core import Conflict, Store, validate_recipe

GUID = 'c' * 32
TARGET = {'sceneGuid': GUID, 'objectId': 'GlobalObjectId_V1-1-' + GUID + '-1-0'}


def recipe(weight):
    return {'items': [], 'appearance': {'blendshapes': [{'rendererId': TARGET['objectId'], 'index': 0, 'value': weight}]}}


class RecoveryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.store = Store(self.temp.name)
        self.project = str(Path(self.temp.name) / 'Project')
        self.wid = self.store.register({'projectPath': self.project})['id']
        self.store.set_target(self.wid, TARGET)
        self.store.desired(self.wid, recipe(60), 0)
        self.operation = self.store.enqueue(self.wid, expected_revision=1)
        self.store.dispatch(self.operation['id'], 'base')
        self.store.receipt(self.operation['id'], {'id': self.operation['id'], 'state': 'needs-review'})
        self.observation = {'projectPath': self.project, 'target': TARGET, 'revision': 'manual-edit', 'recipe': recipe(30), 'warnings': []}

    def tearDown(self):
        self.store.close()
        self.temp.cleanup()

    def test_keep_draft_rebases_without_replaying_or_claiming_it_applied(self):
        review = self.store.recovery_review(self.wid, self.observation)
        self.store.close()
        self.store = Store(self.temp.name)
        workspace = self.store.recover(self.wid, review['id'], 'keep-draft', self.observation)
        self.assertEqual(recipe(60), workspace['desired']['recipe'])
        self.assertEqual(recipe(30), workspace['confirmed']['recipe'])
        self.assertNotEqual(workspace['desired']['revision'], workspace['confirmed']['revision'])
        self.assertEqual('resolved', self.store.operation(self.operation['id'])['state'])
        self.assertIsNone(self.store.next_operation(self.wid))
        new = self.store.enqueue(self.wid, expected_revision=workspace['desired']['revision'])
        self.assertNotEqual(self.operation['id'], new['id'])
        self.assertEqual('manual-edit', new['expectedRevision'])
        self.assertEqual('dispatching', self.store.dispatch(new['id'], 'manual-edit')['state'])

    def test_using_unity_keeps_prior_draft_undoable(self):
        review = self.store.recovery_review(self.wid, self.observation)
        workspace = self.store.recover(self.wid, review['id'], 'use-unity', self.observation)
        self.assertEqual(workspace['desired']['recipe'], workspace['confirmed']['recipe'])
        self.assertEqual(workspace['desired']['revision'], workspace['confirmed']['revision'])
        restored = self.store.undo(self.wid, workspace['desired']['revision'])
        self.assertEqual(recipe(60), restored['desired']['recipe'])
        self.assertEqual(recipe(30), restored['confirmed']['recipe'])
        with self.assertRaises(Conflict):
            self.store.recover(self.wid, review['id'], 'keep-draft', self.observation)

    def test_stale_unity_draft_queue_or_wrong_target_invalidates_review(self):
        review = self.store.recovery_review(self.wid, self.observation)
        for key, value in [('revision', 'later'), ('target', dict(TARGET, objectId=TARGET['objectId'].replace('-1-0', '-2-0'))), ('recipe', recipe(45))]:
            changed = dict(self.observation, **{key: value})
            with self.assertRaises(Conflict):
                self.store.recover(self.wid, review['id'], 'use-unity', changed)
        self.store.desired(self.wid, recipe(90), 1)
        with self.assertRaises(Conflict):
            self.store.recover(self.wid, review['id'], 'keep-draft', self.observation)

    def test_running_receipt_cannot_be_silently_abandoned(self):
        self.store.review(self.operation['id'], 'retry')
        with self.assertRaises(Conflict):
            self.store.recovery_review(self.wid, self.observation)

    def test_diagnostics_can_inspect_unresolved_mutation_without_confirming_it(self):
        before = self.store.workspace(self.wid)['confirmed']
        op = self.store.enqueue(self.wid, 'inspect')
        self.assertEqual(op['id'], self.store.next_operation(self.wid)['id'])
        self.store.dispatch(op['id'], 'external-change')
        self.store.receipt(op['id'], {'id': op['id'], 'state': 'succeeded', 'revision': 'external-change', 'result': {'inspection': self.observation}})
        self.assertEqual(before, self.store.workspace(self.wid)['confirmed'])
        self.assertEqual('needs-review', self.store.operation(self.operation['id'])['state'])

    def test_appearance_requires_exact_supported_finite_controls(self):
        with self.assertRaises(ValueError): validate_recipe({'appearance': {'shaderCode': 'x'}})
        bad = recipe(float('nan'))
        with self.assertRaises(ValueError): validate_recipe(bad)
        duplicate = recipe(5)
        duplicate['appearance']['blendshapes'] *= 2
        with self.assertRaises(ValueError): validate_recipe(duplicate)
        color = {'items': [], 'appearance': {'materials': [{'rendererId': TARGET['objectId'], 'slot': 0, 'property': '_Color', 'color': [1, .5, .25, 1]}]}}
        self.assertEqual(color, validate_recipe(color))


class ProjectJournalTests(unittest.TestCase):
    def test_interrupted_mutation_is_never_replayed_and_blocks_new_work(self):
        with tempfile.TemporaryDirectory() as root:
            store = Store(root)
            wid = store.register({'projectPath': root})['id']
            store.set_target(wid, TARGET)
            job = store.begin_project_job(wid, 'package', {'package': 'com.example.tool'})
            store.close()
            reopened = Store(root)
            try:
                self.assertEqual('needs-review', reopened.project_jobs(wid)[0]['state'])
                with self.assertRaises(Conflict): reopened.enqueue(wid, 'snapshot')
                with self.assertRaises(Conflict): reopened.begin_project_job(wid, 'asset-import', {})
                reopened.acknowledge_project_job(wid, job['id'], 'current-manifest')
                self.assertEqual('queued', reopened.enqueue(wid, 'snapshot')['state'])
                self.assertEqual('reviewed', reopened.project_jobs(wid)[0]['state'])
            finally:
                reopened.close()
