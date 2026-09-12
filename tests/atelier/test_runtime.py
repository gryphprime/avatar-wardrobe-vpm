import json, os, stat, tempfile, time, unittest
from pathlib import Path
from unittest.mock import patch
from atelier.project_runtime import inspect_project, provision_bridge, ProjectLockedError, ProjectOwnedError, UnityWorker

class RuntimeTests(unittest.TestCase):
    def setUp(self):
        self.root = Path(tempfile.mkdtemp()); (self.root/'ProjectSettings').mkdir(); (self.root/'Packages').mkdir()
        (self.root/'ProjectSettings/ProjectVersion.txt').write_text('m_EditorVersion: 2022.3.22f1\n')
        (self.root/'Packages/manifest.json').write_text('{"dependencies":{"a":"1.0"}}')
    def test_inspect_and_provision(self):
        self.assertEqual(inspect_project(self.root).unity_version, '2022.3.22f1')
        provision_bridge(self.root, '/tmp/atelier-bridge')
        self.assertTrue(json.loads((self.root/'Packages/manifest.json').read_text())['dependencies']['dev.gryphprime.atelier-bridge'].endswith('/atelier-bridge'))
        self.assertTrue((self.root/'Packages/manifest.json.bak').exists())

    def test_repeated_provision_preserves_original_backup(self):
        manifest = self.root / 'Packages' / 'manifest.json'
        original = manifest.read_bytes()
        provision_bridge(self.root, '/tmp/atelier-bridge')
        provision_bridge(self.root, '/tmp/atelier-bridge')
        self.assertEqual(original, (self.root / 'Packages' / 'manifest.json.bak').read_bytes())

    def test_inspect_ignores_stale_embedded_lock_entry(self):
        (self.root / 'Packages' / 'packages-lock.json').write_text(json.dumps({
            'dependencies': {'com.example.removed': {'version': '1.2.3', 'source': 'embedded'}}}))
        info = inspect_project(self.root)
        self.assertNotIn('com.example.removed', info.packages)
        self.assertTrue(any('directory is missing' in note for note in info.package_notes))
    def test_lock_refused(self):
        (self.root/'Temp').mkdir(); (self.root/'Temp/UnityLockfile').touch()
        with self.assertRaises(ProjectLockedError): provision_bridge(self.root)

    def test_stop_never_deletes_unity_lockfile(self):
        state = self.root / 'state'
        first = UnityWorker(self.root, unity_path='/bin/echo', state_dir=state)
        class Process:
            pid = 123
            def poll(self): return 0
        first.process = Process(); first.state = 'starting'; first._owns_lock = False
        (self.root / 'Temp').mkdir(); lock = self.root / 'Temp/UnityLockfile'; lock.touch()
        first.stop(graceful_seconds=0, terminate_seconds=0)
        self.assertTrue(lock.exists())

    def test_worker_writes_secret_config_and_exclusively_owns_project(self):
        state = self.root / 'state'
        first = UnityWorker(self.root, unity_path='/bin/echo', state_dir=state)
        second = UnityWorker(self.root, unity_path='/bin/echo', state_dir=self.root / 'other-state')
        class Process:
            pid = 123
            running = True
            def poll(self): return None if self.running else 0
            def terminate(self): self.running = False
            def wait(self, timeout=None): return 0
        process = Process()
        class Client:
            def shutdown(self): process.running = False
        with patch('atelier.project_runtime.subprocess.Popen', return_value=process) as launch, patch('atelier.project_runtime.UnityWorker._free_loopback_port', return_value=43123), patch.object(first, 'client', return_value=Client()):
            first.start(graphics=False)
            config = json.loads((state / 'bridge.json').read_text())
            self.assertEqual(config['projectPath'], str(self.root.resolve()))
            self.assertEqual(config['outputRoot'], str(state.resolve()))
            self.assertEqual(first.status()['state'], 'starting')
            environment = launch.call_args.kwargs['env']
            self.assertEqual(json.loads(environment['ATELIER_BRIDGE_CONFIG'])['token'], config['token'])
            with self.assertRaises(ProjectOwnedError): second.start()
            first.stop()
        lock = self.root / '.atelier' / 'unity-worker.lock'
        self.assertTrue(lock.exists())
        self.assertEqual('offline', json.loads(lock.read_text())['state'])

    def _fake_worker(self, name='fake-worker.py', context_project=None, wait=False):
        script = self.root / name
        project = context_project or self.root
        script.write_text('''#!/usr/bin/env python3
import json, os, sys, threading
from http.server import BaseHTTPRequestHandler, HTTPServer
cfg = json.loads(os.environ["ATELIER_BRIDGE_CONFIG"])
project = %r
stop = threading.Event()
class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args): pass
    def auth(self):
        return self.headers.get("Authorization") == "Bearer " + cfg["token"] and self.headers.get("X-Atelier-Protocol") == "1"
    def send(self, code, value):
        raw = json.dumps(value).encode(); self.send_response(code); self.send_header("Content-Length", str(len(raw))); self.end_headers(); self.wfile.write(raw)
    def do_GET(self):
        if not self.auth(): return self.send(401, {"error":"unauthorized"})
        if self.path == "/context": return self.send(200, {"projectPath": project, "revision":"r", "targets":[]})
        self.send(404, {})
    def do_POST(self):
        if not self.auth(): return self.send(401, {"error":"unauthorized"})
        if self.path == "/shutdown":
            self.send(200, {"state":"shutting-down"}); threading.Thread(target=server.shutdown, daemon=True).start(); return
        self.send(404, {})
server = HTTPServer(("127.0.0.1", int(cfg["port"])), Handler)
server.serve_forever()
''' % str(project.resolve()))
        script.chmod(script.stat().st_mode | stat.S_IXUSR)
        return str(script)

    def _wait_probe(self, worker, seconds=5):
        deadline = time.time() + seconds
        while time.time() < deadline:
            if worker.probe(timeout=.2):
                return
            time.sleep(.05)
        self.fail('fake worker did not become reachable: ' + repr(worker.status()))

    def _require_loopback(self):
        try:
            UnityWorker._free_loopback_port()
        except PermissionError:
            self.skipTest('loopback sockets are unavailable in this sandbox')

    def test_process_boundary_adopts_existing_worker_without_second_writer(self):
        self._require_loopback()
        state = self.root / 'state'
        fake = self._fake_worker()
        first = UnityWorker(self.root, unity_path=fake, state_dir=state)
        first.start(graphics=False)
        self._wait_probe(first)
        second = UnityWorker(self.root, unity_path=fake, state_dir=state)
        with self.assertRaises(ProjectOwnedError):
            second.start()
        # Simulate the host disappearing while its child keeps serving. The
        # next host acquires the advisory lock and proves the exact project.
        first._health_stop.set()
        first._release_project(state='offline')
        self.assertIsNone(second.client())
        self.assertTrue(second.resume_existing())
        self.assertIsNone(second.process)
        self.assertEqual('online', second.status()['state'])
        second.stop(graceful_seconds=2, terminate_seconds=1)
        first.process.wait(timeout=5)
        self.assertEqual('offline', second.status()['state'])

    def test_interactive_handoff_keeps_bridge_and_can_restart_worker(self):
        self._require_loopback()
        state = self.root / 'state'
        fake = self._fake_worker()
        worker = UnityWorker(self.root, unity_path=fake, state_dir=state)
        process = worker.open_interactive()
        self.assertEqual('interactive', worker.status()['state'])
        self._wait_probe(worker)
        process.terminate(); process.wait(timeout=5)
        deadline = time.time() + 3
        while time.time() < deadline and worker.status()['state'] != 'offline':
            time.sleep(.05)
        self.assertEqual('offline', worker.status()['state'])
        worker.start(graphics=False)
        self._wait_probe(worker)
        worker.stop(graceful_seconds=2, terminate_seconds=1)

    def test_live_starting_pid_prevents_duplicate_launch_without_bridge(self):
        self._require_loopback()
        state = self.root / 'state'
        sleeper = self.root / 'sleeper.py'
        sleeper.write_text('#!/usr/bin/env python3\nimport time; time.sleep(20)')
        sleeper.chmod(sleeper.stat().st_mode | stat.S_IXUSR)
        first = UnityWorker(self.root, unity_path=str(sleeper), state_dir=state)
        first.start()
        first._health_stop.set()
        first._release_project(state='starting')
        second = UnityWorker(self.root, unity_path=str(sleeper), state_dir=state)
        with self.assertRaises(ProjectOwnedError):
            second.start()
        first.process.terminate(); first.process.wait(timeout=5)

    def test_failed_start_status_includes_bounded_log_hint(self):
        state = self.root / 'state'
        state.mkdir()
        (state / 'unity-worker.log').write_text('error: package compiler failed\n')
        worker = UnityWorker(self.root, unity_path='/bin/echo', state_dir=state)
        worker.state = 'starting'
        worker._started_at = time.monotonic()
        worker.process = type('Exited', (), {'poll': lambda self: 1})()
        status = worker.status()
        self.assertEqual('offline', status['state'])
        self.assertIn('package compiler failed', status['error'])
