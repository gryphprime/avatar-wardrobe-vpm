"""The real HTTP boundary accepts capture receipt IDs, never browser paths."""
import json
from pathlib import Path
import sys
import tempfile
import threading
from types import SimpleNamespace
import unittest
from unittest.mock import Mock
import urllib.error
import urllib.request
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'Packages/dev.gryphprime.avatar-wardrobe/Desktop'))
from wardrobe_desktop import Host
from wardrobe_library import Library


class ShadowHttpTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name).resolve()
        self.project = self.root / 'source'; self.project.mkdir()
        self.host = Host(0, Library(self.root / 'library'), self.project, 'http://localhost:8929', unity='/owned/Unity')
        self.service = Mock()
        self.host.snapshots = self.service
        self.identifier = uuid.uuid4().hex; self.operation_id = str(uuid.uuid4()); self.key = 'a' * 64
        self.capture_id = uuid.uuid4().hex
        self.manifest = self.project / 'Library/AvatarWardrobe/captures' / self.capture_id / 'manifest.json'
        self.operation = {'type': 'capture-source', 'state': 'succeeded', 'command': {'target': {'projectId': str(self.project)}},
                          'result': {'captureManifestPath': str(self.manifest)}}
        self.host.operations = SimpleNamespace(get=lambda identifier: self.operation)
        self.service.submit.return_value = {'id': self.identifier, 'state': 'queued', 'snapshotKey': self.key}
        self.thread = threading.Thread(target=self.host.serve_forever, daemon=True); self.thread.start()
        self.url = 'http://localhost:' + str(self.host.server_port)

    def tearDown(self):
        self.host.shutdown(); self.host.close_shadow(); self.host.server_close(); self.thread.join(); self.temp.cleanup()

    def request(self, route, payload=None):
        headers = {'X-Wardrobe-Request': '1'}
        data = None if payload is None else json.dumps(payload).encode()
        request = urllib.request.Request(self.url + route, data=data, headers=headers)
        try:
            with urllib.request.urlopen(request, timeout=3) as response: return response.status, response.read(), response.headers.get_content_type()
        except urllib.error.HTTPError as response: return response.code, response.read(), response.headers.get_content_type()

    def test_receipt_resolves_manifest_internally(self):
        status, body, _ = self.request('/api/shadow/submit', {'operationId': self.operation_id, 'view': 'front'})
        self.assertEqual(202, status, body)
        self.assertEqual(self.operation_id, json.loads(body)['operationId'])
        self.service.submit.assert_called_once_with(self.manifest, 'front', before=False, zoom=1.0,
            confirmed_revision='', target={'projectId': str(self.project)})

    def test_browser_path_and_foreign_capture_are_rejected(self):
        status, body, _ = self.request('/api/shadow/submit', {'manifestPath': '/private/file'})
        self.assertEqual(400, status); self.service.submit.assert_not_called()
        self.operation['command']['target']['projectId'] = str(self.root / 'other')
        status, body, _ = self.request('/api/shadow/submit', {'operationId': self.operation_id})
        self.assertEqual(400, status); self.service.submit.assert_not_called()
        self.operation['command']['target']['projectId'] = str(self.project)
        self.operation['result']['captureManifestPath'] = '/private/file'
        status, body, _ = self.request('/api/shadow/submit', {'operationId': self.operation_id})
        self.assertEqual(400, status); self.service.submit.assert_not_called()

    def test_result_exposes_image_route_without_local_image_path(self):
        self.service.get.return_value = {'id': self.identifier, 'state': 'succeeded', 'snapshotKey': self.key, 'imagePath': '/private/image.png'}
        status, body, _ = self.request('/api/shadow/result?id=' + self.identifier)
        self.assertEqual(200, status)
        result = json.loads(body)
        self.assertNotIn('imagePath', result)
        self.assertEqual('/api/shadow/image?key=' + self.key, result['imageUrl'])

    def test_image_has_fixed_key_and_project_scope(self):
        status, _, _ = self.request('/api/shadow/image?key=../secret')
        self.assertEqual(400, status); self.service._cached.assert_not_called()
        image = self.root / 'photo.png'; image.write_bytes(b'owned snapshot')
        self.service._cached.return_value = {'imagePath': str(image), 'project': str(self.project)}
        status, body, mime = self.request('/api/shadow/image?key=' + self.key)
        self.assertEqual((200, b'owned snapshot', 'image/png'), (status, body, mime))
        self.service._cached.return_value['project'] = str(self.root / 'other')
        self.assertEqual(404, self.request('/api/shadow/image?key=' + self.key)[0])


if __name__ == '__main__': unittest.main()
