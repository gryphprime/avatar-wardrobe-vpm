import json
from pathlib import Path
import tempfile
import threading
import unittest
import urllib.error
import urllib.request
import zipfile

from atelier.host import Application, Server, host_lock


class HostTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.app = Application(Path(self.temp.name) / 'data')
        self.server = Server(self.app)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    def tearDown(self):
        self.server.shutdown()
        self.thread.join()
        self.server.server_close()
        self.app.close()
        self.temp.cleanup()

    def request(self, path, body=None, token=True, headers=None):
        request_headers = {'Content-Type': 'application/json'}
        if token:
            request_headers['X-Atelier-Token'] = self.server.token
        request_headers.update(headers or {})
        request = urllib.request.Request(self.server.origin + path, headers=request_headers,
                                         data=json.dumps(body).encode() if body is not None else None)
        return urllib.request.urlopen(request)

    def test_offline_launch_no_unity_and_authentication(self):
        with self.request('/') as response:
            self.assertIn(b'ATELIER', response.read())
        with self.request('/api/state') as response:
            state = json.load(response)
        self.assertEqual('offline', state['worker']['state'])
        self.assertEqual({}, self.app.workers)
        self.assertEqual([], state['workspaces'])
        with self.assertRaises(urllib.error.HTTPError) as denied:
            self.request('/api/state', token=False)
        self.assertEqual(401, denied.exception.code)
        with self.assertRaises(urllib.error.HTTPError) as denied:
            self.request('/api/state', headers={'Origin': 'https://external.invalid'})
        self.assertEqual(403, denied.exception.code)
        with self.assertRaises(urllib.error.HTTPError) as denied:
            self.request('/api/state', headers={'Host': 'external.invalid'})
        self.assertEqual(403, denied.exception.code)

    def test_registration_and_draft_do_not_touch_unity_project(self):
        project = Path(self.temp.name) / 'project'
        (project / 'ProjectSettings').mkdir(parents=True)
        (project / 'ProjectSettings' / 'ProjectVersion.txt').write_text('m_EditorVersion: 2022.3.22f1\n')
        (project / 'Packages').mkdir()
        manifest = project / 'Packages' / 'manifest.json'
        manifest.write_text('{"dependencies":{}}')
        before = manifest.read_bytes()
        with self.request('/api/workspaces', {'projectPath': str(project), 'name': 'Avatar'}) as response:
            workspace = json.load(response)
        recipe = {'items': [{'id': 'copy-1', 'name': 'Jacket', 'assetId': 'source-1', 'prefabGuid': 'a' * 32}], 'appearance': {}}
        with self.request('/api/workspaces/' + workspace['id'] + '/desired', {'expectedRevision': 0, 'recipe': recipe}) as response:
            edited = json.load(response)
        self.assertEqual(1, edited['desired']['revision'])
        self.assertEqual(0, edited['confirmed']['revision'])
        self.assertEqual(before, manifest.read_bytes())
        self.assertEqual({}, self.app.workers)

    def test_second_host_cannot_own_same_data(self):
        root = Path(self.temp.name) / 'lock-test'
        with host_lock(root):
            with self.assertRaises(RuntimeError):
                with host_lock(root):
                    self.fail('Second owner acquired the lock.')

    def test_reviewed_import_is_complete_and_code_needs_explicit_selection(self):
        project = Path(self.temp.name) / 'import-project'
        (project / 'ProjectSettings').mkdir(parents=True)
        (project / 'ProjectSettings/ProjectVersion.txt').write_text('m_EditorVersion: 2022.3.22f1\n')
        (project / 'Packages').mkdir()
        (project / 'Packages/manifest.json').write_text('{"dependencies":{}}')
        archive = Path(self.temp.name) / 'fixture.zip'
        with zipfile.ZipFile(archive, 'w') as bundle:
            bundle.writestr('Assets/Example.txt', 'fixture asset')
            bundle.writestr('Assets/Editor/Fixture.cs', '// fixture code, never launched')
        asset = self.app.library.add(str(archive))
        workspace = self.app.register({'projectPath': str(project)})
        job = self.app.plan_import(workspace['id'], asset['id'])
        self.app.jobs[job['id']].result(timeout=5)
        self.assertEqual('succeeded', self.app.job(job['id'])['state'])
        self.assertFalse((project / 'Assets/Example.txt').exists())
        with self.assertRaises(ValueError):
            self.app.apply_import(workspace['id'], job['id'])
        imported = self.app.apply_import(workspace['id'], job['id'], allow_code=True)
        self.app.jobs[imported['id']].result(timeout=5)
        self.assertEqual('fixture asset', (project / 'Assets/Example.txt').read_text())
        self.assertTrue((project / 'Assets/Editor/Fixture.cs').exists())
        self.assertIsNone(self.app.worker(workspace['id']).process)


if __name__ == '__main__':
    unittest.main()
