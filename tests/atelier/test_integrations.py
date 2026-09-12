import json
from pathlib import Path
import tempfile
import unittest

from adapters_sdk import AdapterRegistry, ManifestError, load_manifest
from atelier.core import Conflict
from atelier.host import Application
from atelier.packages import PackageRuntime


class Worker:
    def __init__(self, project, unity=None, state_dir=None):
        self.project = project
        self.stopped = False
    def status(self): return {'state': 'offline'}
    def stop(self): self.stopped = True


class Packages(PackageRuntime):
    def __init__(self):
        super().__init__()
        self.calls = []
        self.fail = False
    def available(self): return True
    def apply(self, plan):
        # Preserve the production boundary's stale-plan contract.
        from atelier.packages import _manifest
        if _manifest(plan['cwd'])[2] != plan['manifestSha256']:
            raise ValueError('Package manifest changed since review.')
        self.calls.append(plan)
        path = Path(plan['cwd']) / 'Packages/manifest.json'
        data = json.loads(path.read_text())
        if plan['action'] == 'install': data['dependencies'][plan['package']] = plan['version']
        else: data['dependencies'].pop(plan['package'], None)
        path.write_text(json.dumps(data))
        if self.fail:
            raise RuntimeError('Interrupted after changing package files')
        return {'ok': True, 'returncode': 0, 'stdout': 'Applied', 'stderr': ''}


class IntegrationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.project = Path(self.temp.name) / 'Project'
        (self.project / 'ProjectSettings').mkdir(parents=True)
        (self.project / 'ProjectSettings/ProjectVersion.txt').write_text('m_EditorVersion: 2022.3.22f1\n')
        (self.project / 'Packages').mkdir()
        (self.project / 'Packages/manifest.json').write_text('{"dependencies":{"com.vrchat.base":"3.10.5","com.vrchat.avatars":"3.10.5"}}')
        self.runtime = Packages()
        self.app = Application(Path(self.temp.name) / 'data', worker_factory=Worker, package_runtime=self.runtime)
        self.wid = self.app.register({'projectPath': str(self.project)})['id']
        self.app.start_driver = lambda workspace_id: None
    def tearDown(self):
        self.app.close()
        self.temp.cleanup()
    def wait(self, job):
        return self.app.jobs[job['id']].result(timeout=5)
    def plan(self, action='install'):
        job = self.app.integration_plan(self.wid, 'atelier.modular-avatar', action)
        result = self.wait(job)
        self.assertEqual(job['id'], result['id'])
        return result

    def test_reviewed_install_remove_and_shared_adapter_queue(self):
        plan = self.plan()
        self.assertEqual([], self.runtime.calls)
        self.assertEqual('1.18.7', plan['version'])
        self.wait(self.app.integration_apply(self.wid, plan['id']))
        self.assertTrue(self.app.worker(self.wid).stopped)
        integration = next(item for item in self.app.state(self.wid)['integrations'] if item['id'] == 'atelier.modular-avatar')
        self.assertTrue(integration['installed'])
        self.assertEqual('1.18.7', integration['installedVersion'])
        guid = 'a' * 32
        self.app.store.set_target(self.wid, {'sceneGuid': guid, 'objectId': 'GlobalObjectId_V1-1-' + guid + '-1-0'})
        op = self.app.adapter_action(self.wid, 'atelier.modular-avatar', 'inspect', {})
        self.assertEqual('inspect', op['action'])
        self.assertEqual('atelier.modular-avatar', op['adapter']['id'])
        self.assertEqual(op['id'], self.app.store.next_operation(self.wid)['id'])
        remove = self.plan('remove')
        with self.assertRaises(Conflict): self.app.integration_apply(self.wid, remove['id'])
        self.app.store.dispatch(op['id'], 'base')
        self.app.store.receipt(op['id'], {'id': op['id'], 'state': 'succeeded', 'revision': 'base', 'result': {'inspection': {}}})
        self.wait(self.app.integration_apply(self.wid, remove['id']))
        self.assertNotIn('nadena.dev.modular-avatar', self.app.store.workspace(self.wid)['packages'])
        with self.assertRaises(Conflict): self.app.adapter_action(self.wid, 'atelier.modular-avatar', 'preview', {})

    def test_partial_failure_survives_restart_and_review_cannot_be_stale(self):
        self.runtime.fail = True
        with self.assertRaises(RuntimeError): self.wait(self.app.integration_apply(self.wid, self.plan()['id']))
        job = self.app.store.project_jobs(self.wid)[0]
        self.assertEqual('needs-review', job['state'])
        self.app.close()
        self.app = Application(Path(self.temp.name) / 'data', worker_factory=Worker, package_runtime=self.runtime)
        self.assertEqual(1, len(self.runtime.calls))
        review = self.app.project_review(self.wid, job['id'])
        path = self.project / 'Packages/manifest.json'
        previous = path.read_text()
        path.write_text('{"dependencies":{}}')
        with self.assertRaises(Conflict): self.app.project_acknowledge(self.wid, review['id'])
        path.write_text(previous)
        acknowledged = self.app.project_acknowledge(self.wid, review['id'])
        self.assertEqual('reviewed', acknowledged['state'])
        self.assertEqual(1, len(self.runtime.calls))

    def test_registry_cannot_self_grant_official_status_or_execute_unknown_code(self):
        registry = AdapterRegistry()
        declaration = load_manifest(Path('adapters/bridge_snapshot.json'))
        registry.register(declaration, {'inspect': lambda ctx, inputs: ctx, 'preview': lambda ctx, inputs: ctx})
        self.assertEqual('community', registry.manifests()[0]['origin'])
        self.assertEqual({'exact': 1}, registry.execute(declaration.data['id'], 'inspect', {'exact': 1}, {}))
        with self.assertRaises(ManifestError): registry.execute(declaration.data['id'], 'execute-script', {}, {})
        with self.assertRaises(ManifestError): registry.execute(declaration.data['id'], 'inspect', {}, {'code': 'anything'})

    def test_package_install_requires_supported_avatar_sdk_project(self):
        path = self.project / 'Packages/manifest.json'
        for dependencies in ({}, {'com.vrchat.base': '3.0.0', 'com.vrchat.avatars': '3.0.0'}):
            path.write_text(json.dumps({'dependencies': dependencies}))
            with self.assertRaises(Conflict): self.app.integration_plan(self.wid, 'atelier.modular-avatar', 'install')
        self.assertEqual([], self.runtime.calls)
