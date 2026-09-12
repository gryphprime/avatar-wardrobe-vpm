import json, tempfile, unittest
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
    def test_lock_refused(self):
        (self.root/'Temp').mkdir(); (self.root/'Temp/UnityLockfile').touch()
        with self.assertRaises(ProjectLockedError): provision_bridge(self.root)

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
        self.assertFalse((self.root / '.atelier' / 'unity-worker.lock').exists())
