import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import urllib.request


class ProcessRecoveryTests(unittest.TestCase):
    def test_host_kill_preserves_accepted_draft(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            project = root / 'project'
            (project / 'ProjectSettings').mkdir(parents=True)
            (project / 'ProjectSettings/ProjectVersion.txt').write_text('m_EditorVersion: 2022.3.22f1\n')
            (project / 'Packages').mkdir()
            (project / 'Packages/manifest.json').write_text('{"dependencies":{}}')
            def launch():
                process = subprocess.Popen([sys.executable, '-m', 'atelier', '--data', str(root / 'data'), '--no-browser'],
                                           cwd=Path(__file__).resolve().parents[2], stdout=subprocess.PIPE,
                                           stderr=subprocess.PIPE, text=True)
                process.stdout.readline()
                link = process.stdout.readline().strip()
                if '#token=' not in link:
                    process.kill()
                    self.fail('Host failed to start: ' + process.stderr.read())
                origin, token = link.split('/#token=')
                return process, origin, token
            def request(origin, token, route, value=None):
                body = None if value is None else json.dumps(value).encode()
                req = urllib.request.Request(origin + route, data=body,
                    headers={'X-Atelier-Token': token, 'Content-Type': 'application/json'})
                with urllib.request.urlopen(req, timeout=5) as response:
                    return json.load(response)
            process, origin, token = launch()
            try:
                workspace = request(origin, token, '/api/workspaces', {'projectPath': str(project)})
                request(origin, token, '/api/workspaces/' + workspace['id'] + '/desired',
                        {'expectedRevision': 0, 'recipe': {'items': [], 'appearance': {'draftParameter': 1}}})
                process.kill()
                process.wait(timeout=5)
                process.stdout.close(); process.stderr.close()
                process, origin, token = launch()
                state = request(origin, token, '/api/state')
                self.assertEqual(1, state['workspace']['desired']['revision'])
                self.assertEqual(0, state['workspace']['confirmed']['revision'])
                self.assertEqual('offline', state['worker']['state'])
            finally:
                process.terminate()
                process.wait(timeout=10)
                process.stdout.close(); process.stderr.close()
