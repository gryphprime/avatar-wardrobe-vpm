"""Real desktop HTTP accepts durable intent while the loopback Unity bridge stalls."""
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import sys
import tempfile
import threading
import time
import unittest
import urllib.error
import urllib.parse
import urllib.request
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'Packages/dev.gryphprime.avatar-wardrobe/Desktop'))
from wardrobe_desktop import Host
from wardrobe_library import Library
from test_operations import command


class OperationHttpTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name).resolve()
        self.project = self.root / 'project with spaces'; self.project.mkdir()
        self.sample = command(self.project)
        self.context = dict(self.sample['target'], revision='revision-0', waitingReason='')
        self.calls, self.receipts = [], {}
        self.accept_entered, self.accept_release = threading.Event(), threading.Event()
        self.accept_release.set()
        self.fail_accept = False
        self.redirect_context = False
        owner = self
        class Bridge(BaseHTTPRequestHandler):
            def log_message(self, *args): pass
            def route(self):
                owner.calls.append((self.command, self.path, dict(self.headers)))
                path = urllib.parse.urlsplit(self.path)
                query = urllib.parse.parse_qs(path.query)
                status, mime = 200, 'application/json'
                if path.path == '/api/operation_context':
                    if owner.redirect_context:
                        self.send_response(302); self.send_header('Location', 'http://example.invalid/'); self.end_headers(); return
                    value = owner.context
                elif path.path == '/api/operation_accept':
                    body = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
                    identifier = body['command']['id']
                    owner.receipts[identifier] = {'id': identifier, 'state': 'running', 'waitingReason': 'Unity is applying the edit'}
                    owner.accept_entered.set(); owner.accept_release.wait(4)
                    if owner.fail_accept:
                        self.close_connection = True
                        return
                    value, status = owner.receipts[identifier], 202
                elif path.path == '/api/operation_result':
                    identifier = query['id'][0]
                    value = owner.receipts.get(identifier, {'id': identifier, 'state': 'needs-review', 'error': 'Unknown receipt'})
                elif path.path == '/api/operation_cancel':
                    identifier = query['id'][0]
                    value = dict(owner.receipts[identifier], cancelRequested=True)
                elif path.path == '/api/state':
                    value = {'projectPath': str(owner.project)}
                elif path.path in ('/api/scene_upload_review', '/api/batch_dryrun'):
                    value = {'ok': 1}
                elif path.path == '/api/snapshot':
                    value, mime = b'PNG-test-image', 'image/png'
                else:
                    value, status = {'error': 'The driver must not ask for /api/state'}, 500
                data = json.dumps(value).encode() if mime == 'application/json' else value
                self.send_response(status); self.send_header('Content-Type', mime)
                self.send_header('Content-Length', str(len(data))); self.end_headers()
                try: self.wfile.write(data)
                except (BrokenPipeError, ConnectionResetError): pass
            do_GET = route
            do_POST = route
        for port in range(8929, 8908, -1):
            try:
                self.bridge = ThreadingHTTPServer(('127.0.0.1', port), Bridge)
                break
            except OSError: continue
        else:
            self.temp.cleanup()
            self.skipTest('No available Wardrobe loopback bridge port')
        self.bridge.daemon_threads = True
        self.bridge_thread = threading.Thread(target=self.bridge.serve_forever, daemon=True); self.bridge_thread.start()
        self.library = Library(self.root / 'library')
        self.host = Host(0, self.library, self.project, 'http://localhost:' + str(self.bridge.server_port), unity='/owned/Unity')
        self.host_thread = threading.Thread(target=self.host.serve_forever, daemon=True); self.host_thread.start()
        self.url = 'http://localhost:' + str(self.host.server_port)
    def tearDown(self):
        self.accept_release.set()
        self.host.shutdown(); self.host.server_close(); self.host_thread.join(3)
        self.bridge.shutdown(); self.bridge.server_close(); self.bridge_thread.join(3)
        self.temp.cleanup()
    def request(self, path, payload=None, raw=None, method=None, headers=None):
        data = json.dumps(payload).encode() if payload is not None else raw
        request = urllib.request.Request(self.url + path, data=data, method=method,
                    headers=dict({'X-Wardrobe-Request': '1'}, **(headers or {})))
        try:
            with urllib.request.urlopen(request, timeout=2) as response:
                data = response.read()
                return response.status, json.loads(data) if response.headers.get_content_type() == 'application/json' else data
        except urllib.error.HTTPError as response:
            return response.code, json.loads(response.read())
    def wait_for(self, predicate, timeout=3):
        end = time.monotonic() + timeout
        while time.monotonic() < end:
            value = predicate()
            if value: return value
            time.sleep(0.02)
        self.fail('Timed out waiting for asynchronous operation')

    def test_durable_accept_and_status_remain_available_during_slow_bridge(self):
        self.accept_release.clear()
        status, first = self.request('/api/operations', self.sample)
        self.assertEqual(202, status); self.assertEqual('queued', first['state'])
        self.assertTrue(self.accept_entered.wait(2))
        started = time.monotonic()
        second = command(self.project)
        self.assertEqual(202, self.request('/api/operations', second)[0])
        listed = self.request('/api/operations')[1]['items']
        self.assertEqual(2, len(listed))
        self.assertEqual('running', self.request('/api/operations?id=' + self.sample['id'])[1]['state'])
        self.assertEqual([], self.request('/api/library')[1]['items'])
        self.assertEqual(200, self.request('/api/operation_context')[0])
        self.assertLess(time.monotonic() - started, 1)
        self.assertFalse(any(path.startswith('/api/state') for _, path, _ in self.calls))
        self.accept_release.set()
        result = self.wait_for(lambda: self.host.operations.get(self.sample['id']).get('waitingReason') == 'Unity is applying the edit')
        self.assertTrue(result)
        headers = next(headers for method, path, headers in self.calls if path == '/api/operation_accept')
        self.assertEqual(urllib.parse.quote(str(self.project), safe=''), headers['X-Wardrobe-Project'])
        self.assertEqual(self.sample['target']['session'], headers['X-Wardrobe-Session'])
        self.assertEqual('42', headers['X-Wardrobe-Avatar'])
        self.assertEqual('1', headers['X-Wardrobe-Request'])

    def test_http_duplicate_and_malformed_nested_commands(self):
        self.context['waitingReason'] = 'Compiling scripts'
        self.assertEqual(202, self.request('/api/operations', self.sample)[0])
        self.assertEqual(202, self.request('/api/operations', self.sample)[0])
        changed = json.loads(json.dumps(self.sample)); changed['payload']['zoom'] = 2
        self.assertEqual(400, self.request('/api/operations', changed)[0])
        changed['id'] = str(uuid.uuid4()); changed['payload']['zoom'] = {'deep': ['invalid']}
        self.assertEqual(400, self.request('/api/operations', changed)[0])
        self.assertEqual(400, self.request('/api/operations', raw=b'{' + b'x' * 65536)[0])
        duplicate = json.dumps(self.sample).replace('"zoom": 1', '"zoom": 1, "zoom": 2').encode()
        self.assertEqual(400, self.request('/api/operations', raw=duplicate)[0])
        self.assertEqual(1, len(self.host.operations.list()))

    def test_cancel_states_and_unknown_receipt_over_http(self):
        self.context['waitingReason'] = 'Compiling scripts'
        self.request('/api/operations', self.sample)
        path = '/api/operations/cancel?id=' + self.sample['id']
        self.assertEqual('cancelled', self.request(path, method='POST')[1]['state'])
        self.assertEqual(405, self.request(path)[0])
        unknown = self.request('/api/operations?id=' + str(uuid.uuid4()))[1]
        self.assertEqual('needs-review', unknown['state'])

    def test_offline_persisted_intent_survives_restart_but_not_new_session(self):
        self.host.operation_bridge.base_url = 'http://localhost:1'
        self.request('/api/operations', self.sample)
        self.wait_for(lambda: 'offline' in self.host.operations.get(self.sample['id'])['waitingReason'])
        self.host.shutdown(); self.host.server_close(); self.host_thread.join(3)
        self.assertFalse(self.host._operation_thread.is_alive()); self.assertTrue(self.host._operation_queue.closed)
        self.context['session'] = 'reopened-unity-session'
        self.host = Host(0, self.library, self.project, 'http://localhost:' + str(self.bridge.server_port))
        self.host_thread = threading.Thread(target=self.host.serve_forever, daemon=True); self.host_thread.start()
        self.url = 'http://localhost:' + str(self.host.server_port)
        self.wait_for(lambda: self.host.operations.get(self.sample['id'])['state'] == 'needs-review')
        self.assertFalse(any(path == '/api/operation_accept' for _, path, _ in self.calls))

    def test_ambiguous_accept_reconciles_without_a_second_post(self):
        self.fail_accept = True
        self.request('/api/operations', self.sample)
        self.assertTrue(self.accept_entered.wait(2))
        self.receipts[self.sample['id']] = {'id': self.sample['id'], 'state': 'succeeded', 'result': {'confirmedRevision': 'confirmed'}}
        self.wait_for(lambda: self.host.operations.get(self.sample['id'])['state'] == 'succeeded')
        self.assertEqual(1, sum(path == '/api/operation_accept' for _, path, _ in self.calls))
        self.assertTrue(any(path.startswith('/api/operation_result') for _, path, _ in self.calls))

    def test_fast_context_snapshot_proxy_and_project_identity(self):
        key = 'a' * 64
        self.assertEqual((200, b'PNG-test-image'), self.request('/api/snapshot?key=' + key))
        self.assertFalse(any(path.startswith('/api/state') for _, path, _ in self.calls))
        self.context['projectId'] = '/another-project'
        self.assertEqual(409, self.request('/api/operation_context')[0])
        self.assertEqual(400, self.request('/api/snapshot?key=' + key)[0])

    def test_pending_edits_block_build_review_but_photos_do_not(self):
        self.context['waitingReason'] = 'Compiling scripts'
        self.request('/api/operations', self.sample)
        for path, method in (('/api/scene_upload_review', 'GET'), ('/api/batch_dryrun', 'POST')):
            status, body = self.request(path, method=method)
            self.assertEqual(409, status)
            self.assertIn('pending wardrobe changes', body['message'])
        self.assertFalse(any(path in ('/api/scene_upload_review', '/api/batch_dryrun') for _, path, _ in self.calls))
        self.request('/api/operations/cancel?id=' + self.sample['id'], method='POST')
        preview = command(self.project, 'prepare-preview')
        self.request('/api/operations', preview)
        self.assertEqual(200, self.request('/api/scene_upload_review')[0])
        self.assertTrue(any(path == '/api/scene_upload_review' for _, path, _ in self.calls))

    def test_context_get_reconciles_completed_unsaved_edit_after_restart(self):
        self.request('/api/operations', self.sample)
        self.assertTrue(self.accept_entered.wait(2))
        self.receipts[self.sample['id']] = {'id': self.sample['id'], 'state': 'succeeded',
                                            'result': {'confirmedRevision': 'after-edit', 'unsaved': True}}
        self.wait_for(lambda: self.host.operations.get(self.sample['id'])['state'] == 'succeeded')
        self.context['session'] = 'new-unity-session'
        self.assertEqual(200, self.request('/api/operation_context')[0])
        receipt = self.request('/api/operations?id=' + self.sample['id'])[1]
        self.assertEqual('needs-review', receipt['state'])
        self.assertEqual('after-edit', receipt['result']['confirmedRevision'])
        self.assertEqual(1, sum(path == '/api/operation_accept' for _, path, _ in self.calls))

    def test_redirects_are_rejected_and_identity_advertises_protocol(self):
        identity = self.request('/api/desktop_identity')[1]
        self.assertEqual(2, identity['protocol']); self.assertEqual('/owned/Unity', identity['unity'])
        self.redirect_context = True
        self.assertEqual(502, self.request('/api/operation_context')[0])
        for name in ('operations', 'snapshots', 'drag-drop', 'menu-organizer'):
            self.assertEqual(200, self.request('/' + name + '.js')[0])


if __name__ == '__main__': unittest.main()
