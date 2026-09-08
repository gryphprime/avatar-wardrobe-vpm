"""Exercise the actual local HTTP host, including a fresh offline process/cache."""
import base64
from contextlib import contextmanager
import hashlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
from pathlib import Path
import sys
import tempfile
import threading
import time
import unittest
from unittest.mock import patch
import urllib.error
import urllib.request
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'Packages/dev.gryphprime.avatar-wardrobe/Desktop'))
from wardrobe_desktop import Host
from wardrobe_library import Library

class DesktopTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name).resolve()
        self.project = self.root/'project'
        (self.project/'ProjectSettings').mkdir(parents=True)
        (self.project/'ProjectSettings/ProjectVersion.txt').write_text('m_EditorVersion: 2022.3.22f1')
        # Reserve a valid bridge port without responding: urlopen timeout is replaced
        # by a closed local port for immediate offline responses.
        for port in range(8929, 8908, -1):
            try:
                probe = ThreadingHTTPServer(('127.0.0.1', port), BaseHTTPRequestHandler)
                probe.server_close()
                self.bridge_port = port
                break
            except OSError:
                continue
        else:
            self.skipTest('Loopback ports 8909-8929 are unavailable in this environment')
        self.library = Library(self.root/'library')
        self.host = Host(0, self.library, self.project, 'http://localhost:'+str(self.bridge_port))
        self.thread = threading.Thread(target=self.host.serve_forever, daemon=True)
        self.thread.start()
        self.url = 'http://localhost:'+str(self.host.server_port)

    def tearDown(self):
        self.host.shutdown(); self.host.server_close(); self.thread.join()
        self.temp.cleanup()

    def request(self, path, method='GET', headers=None):
        request = urllib.request.Request(self.url+path, method=method, headers=headers or {})
        with urllib.request.urlopen(request, timeout=3) as response:
            return response.status, json.load(response)

    @contextmanager
    def bridge(self, responses):
        calls = []
        class Bridge(BaseHTTPRequestHandler):
            def handle_route(self):
                calls.append((self.command, self.path))
                value = responses[self.path]
                status, headers, payload = value if isinstance(value, tuple) else (200, {}, value)
                body = json.dumps(payload).encode()
                self.send_response(status)
                self.send_header('Content-Type', 'application/json')
                self.send_header('Content-Length', str(len(body)))
                for key, content in headers.items(): self.send_header(key, content)
                self.end_headers(); self.wfile.write(body)
            do_GET = handle_route
            do_POST = handle_route
            def log_message(self, *args): pass
        server = ThreadingHTTPServer(('127.0.0.1', self.bridge_port), Bridge)
        thread = threading.Thread(target=server.serve_forever, daemon=True); thread.start()
        try: yield calls
        finally:
            server.shutdown(); server.server_close(); thread.join()

    def test_offline_library_and_initial_state(self):
        status, state = self.request('/api/state?lang=en')
        self.assertEqual(status, 200)
        self.assertFalse(state['bridgeOnline'])
        self.assertEqual(state['avatarInstanceId'], 0)
        self.assertEqual(state['session'], 'offline')
        self.assertEqual(self.request('/api/library')[1]['items'], [])
        self.assertEqual(self.request('/api/desktop_identity')[1]['project'], str(self.project))

    def test_cold_offline_cache_and_mutation_no_retry(self):
        path='/api/families?lang=en&search=blue'
        key=hashlib.sha256((str(self.project)+'|'+path).encode()).hexdigest()
        (self.host.cache/(key+'.json')).write_text(json.dumps({
            'project': str(self.project), 'route': '/api/families', 'path': path,
            'mime':'application/json',
            'body':base64.b64encode(b'{"items":[{"name":"Blue"}]}').decode()}))
        self.assertEqual(self.request(path)[1]['items'][0]['name'], 'Blue')
        with self.assertRaises(urllib.error.HTTPError) as result:
            self.request('/api/install?guid=missing', 'POST', {'X-Wardrobe-Request':'1'})
        self.assertEqual(result.exception.code, 503)

    def test_origin_and_method_boundaries(self):
        for headers in [{'Origin':'https://external.example'}, {'Host':'external.example'}]:
            with self.assertRaises(urllib.error.HTTPError) as result:
                self.request('/api/library',headers=headers)
            self.assertEqual(result.exception.code,403)
        with self.assertRaises(urllib.error.HTTPError) as result:
            self.request('/api/library/import_review?hash=unknown', 'POST')
        self.assertEqual(result.exception.code,403)
        with self.assertRaises(urllib.error.HTTPError):
            self.request('/api/library/import_apply?token=unknown')

    def test_open_unreachable_project_blocks_import(self):
        (self.project/'Temp').mkdir()
        (self.project/'Temp/UnityLockfile').touch()
        self.host.plans['test']={'hash':'unused','codeFiles':0}
        with self.assertRaises(urllib.error.HTTPError) as result:
            self.request('/api/library/import_apply?token=test','POST',{'X-Wardrobe-Request':'1'})
        self.assertEqual(result.exception.code,400)
        self.assertIn('bridge is unavailable', result.exception.read().decode())
        self.assertFalse((self.project/'Assets').exists())

    def test_corrupt_cold_cache_is_safe_miss(self):
        path = '/api/families?lang=en'
        key = hashlib.sha256((str(self.project) + '|' + path).encode()).hexdigest()
        (self.host.cache / (key + '.json')).write_text('{not-json')
        with self.assertRaises(urllib.error.HTTPError) as result:
            self.request(path)
        self.assertEqual(result.exception.code, 503)

    def test_closed_unity_allows_offline_import(self):
        self.host.plans['offline'] = {'hash': 'unused', 'codeFiles': 0}
        self.host.library.apply_import = lambda *args: {'ok': 1, 'imported': 1}
        status, result = self.request('/api/library/import_apply?token=offline', 'POST', {'X-Wardrobe-Request': '1'})
        self.assertEqual(status, 200)
        self.assertEqual(result['imported'], 1)

    def test_cross_project_proxy_refused_before_endpoint(self):
        calls = []
        other_project = str(self.root / 'other')
        class Bridge(BaseHTTPRequestHandler):
            def do_GET(self):
                calls.append(self.path)
                body = json.dumps({'projectPath': other_project}).encode()
                self.send_response(200); self.send_header('Content-Type', 'application/json'); self.send_header('Content-Length', str(len(body))); self.end_headers(); self.wfile.write(body)
            def log_message(self, *args): pass
        bridge = ThreadingHTTPServer(('127.0.0.1', self.bridge_port), Bridge)
        self.host.bridge = 'http://localhost:' + str(self.bridge_port)
        t = threading.Thread(target=bridge.serve_forever, daemon=True); t.start()
        try:
            with self.assertRaises(urllib.error.HTTPError) as result:
                self.request('/api/families?lang=en')
            self.assertEqual(result.exception.code, 400)
            self.assertEqual(calls, ['/api/state'])
        finally:
            bridge.shutdown(); bridge.server_close(); t.join()

    def test_cross_project_mutation_is_never_forwarded(self):
        with self.bridge({'/api/state': {'projectPath': str(self.root/'other')}}) as calls:
            with self.assertRaises(urllib.error.HTTPError):
                self.request('/api/install?guid=missing', 'POST', {'X-Wardrobe-Request': '1'})
            self.assertEqual(calls, [('GET', '/api/state')])

    def test_bridge_redirect_is_not_followed(self):
        with self.bridge({'/api/state': (302, {'Location': self.url+'/redirect-target'}, {})}) as calls:
            with self.assertRaises(urllib.error.HTTPError) as result:
                self.request('/api/state')
            self.assertEqual(result.exception.code, 502)
            self.assertEqual(calls, [('GET', '/api/state')])

    def test_apply_failure_releases_lease(self):
        responses = {'/api/library_import_begin': {'ok': 1, 'id': 'lease'},
                     '/api/library_import_end?token=lease': {'ok': 1}}
        self.host.plans['test'] = {'hash': 'unused', 'codeFiles': 0}
        def fail(*args, **kwargs): raise ValueError('simulated copy rollback')
        self.host.library.apply_import = fail
        with self.bridge(responses) as calls:
            with self.assertRaises(urllib.error.HTTPError) as result:
                self.request('/api/library/import_apply?token=test', 'POST', {'X-Wardrobe-Request': '1'})
            self.assertIn('simulated copy rollback', result.exception.read().decode())
            self.assertEqual(calls, [('POST', path) for path in responses])

    def test_invalid_lease_never_copies(self):
        copied = []
        self.host.library.apply_import = lambda *args, **kwargs: copied.append(args)
        for response in ({'ok': 1}, ['not a lease']):
            self.host.plans['test'] = {'hash': 'unused', 'codeFiles': 0}
            with self.bridge({'/api/library_import_begin': response}):
                with self.assertRaises(urllib.error.HTTPError):
                    self.request('/api/library/import_apply?token=test', 'POST', {'X-Wardrobe-Request': '1'})
        self.assertEqual(copied, [])

    def test_failed_refresh_confirmation_keeps_success_with_attention(self):
        self.host.plans['test'] = {'hash': 'unused', 'codeFiles': 0}
        self.host.library.apply_import = lambda *args, **kwargs: {'ok': 1, 'copied': 2}
        with self.bridge({'/api/library_import_begin': {'ok': 1, 'id': 'lease'},
                          '/api/library_import_end?token=lease': []}):
            status, result = self.request('/api/library/import_apply?token=test', 'POST', {'X-Wardrobe-Request': '1'})
        self.assertEqual(status, 200)
        self.assertEqual(result['copied'], 2)
        self.assertIn('Do not repeat', result['message'])

    def test_corrupt_cached_json_body_is_safe_miss(self):
        path = '/api/families?lang=en'
        key = hashlib.sha256((str(self.project)+'|'+path).encode()).hexdigest()
        (self.host.cache/(key+'.json')).write_text(json.dumps({'project': str(self.project), 'route': '/api/families', 'path': path,
            'mime': 'application/json', 'body': base64.b64encode(b'not json').decode()}))
        with self.assertRaises(urllib.error.HTTPError) as result:
            self.request(path)
        self.assertEqual(result.exception.code, 503)

    def test_import_renews_lease_and_stops_on_renewal_failure(self):
        responses = {'/api/library_import_begin': {'ok': 1, 'id': 'lease'},
                     '/api/library_import_renew?token=lease': {'ok': 0},
                     '/api/library_import_end?token=lease': {'ok': 1}}
        def apply(*args, cancel_check):
            for _ in range(100):
                time.sleep(0.01)
                cancel_check()
            self.fail('The failed lease renewal did not stop copying.')
        self.host.library.apply_import = apply
        self.host.plans['test'] = {'hash': 'unused', 'codeFiles': 0}
        with self.bridge(responses) as calls, patch('wardrobe_desktop.LEASE_RENEW_SECONDS', 0.01):
            with self.assertRaises(urllib.error.HTTPError) as result:
                self.request('/api/library/import_apply?token=test', 'POST', {'X-Wardrobe-Request': '1'})
            self.assertIn('protection was lost', result.exception.read().decode())
            self.assertIn(('POST', '/api/library_import_renew?token=lease'), calls)
            self.assertEqual(calls[-1], ('POST', '/api/library_import_end?token=lease'))

    def test_library_only_host_never_proxies_or_reads_project_cache(self):
        self.host.project = ''
        with self.bridge({}) as calls:
            self.assertFalse(self.request('/api/state')[1]['bridgeOnline'])
            self.assertEqual(self.request('/api/library')[1]['items'], [])
            for method in ('GET', 'POST'):
                with self.assertRaises(urllib.error.HTTPError) as result:
                    self.request('/api/families', method, {'X-Wardrobe-Request': '1'})
                self.assertEqual(result.exception.code, 409)
            self.assertEqual(calls, [])

if __name__ == '__main__': unittest.main()
