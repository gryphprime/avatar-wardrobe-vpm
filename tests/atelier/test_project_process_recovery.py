"""Crash an actual owner process after a partial import, then inspect its journal."""
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

from atelier.host import Application


class ProjectProcessRecoveryTests(unittest.TestCase):
    def test_partial_project_import_is_reviewed_after_owner_is_killed(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            project = root / 'Project'
            (project / 'Assets').mkdir(parents=True)
            (project / 'ProjectSettings').mkdir()
            (project / 'ProjectSettings/ProjectVersion.txt').write_text('m_EditorVersion: 2022.3.22f1\n')
            (project / 'Packages').mkdir()
            (project / 'Packages/manifest.json').write_text('{"dependencies":{}}')
            script = '''
import json,sys,time
from pathlib import Path
from atelier.core import Store
root=Path(sys.argv[1]); store=Store(root/'data')
workspace=store.register({'projectPath':str(root/'Project')})
plan={'files':[{'path':'Assets/one.txt','destination':str(root/'Project/Assets/one.txt'),'sha256':sys.argv[2]}, {'path':'Assets/two.txt','destination':str(root/'Project/Assets/two.txt'),'sha256':sys.argv[2]}]}
job=store.begin_project_job(workspace['id'],'asset-import',plan)
(root/'Project/Assets/one.txt').write_text('fixture')
print(json.dumps({'workspace':workspace['id'],'job':job['id']}),flush=True)
time.sleep(30)
'''
            process = subprocess.Popen([sys.executable, '-c', script, str(root), hashlib.sha256(b'fixture').hexdigest()],
                                       stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
            try:
                ready = json.loads(process.stdout.readline())
                process.kill()
                process.wait(timeout=5)
                app = Application(root / 'data')
                try:
                    self.assertEqual('needs-review', app.store.project_jobs(ready['workspace'])[0]['state'])
                    report = app.project_review(ready['workspace'], ready['job'])
                    self.assertEqual(['unchanged', 'missing'], [item['state'] for item in report['currentFiles']])
                    app.project_acknowledge(ready['workspace'], report['id'])
                    self.assertFalse((project / 'Assets/two.txt').exists())
                    self.assertEqual('reviewed', app.store.project_jobs(ready['workspace'])[0]['state'])
                finally:
                    app.close()
            finally:
                if process.poll() is None:
                    process.kill(); process.wait(timeout=5)
                process.stdout.close(); process.stderr.close()
