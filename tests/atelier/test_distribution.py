import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import zipfile

from scripts.build_atelier import build


class DistributionTests(unittest.TestCase):
    def test_extracted_atelier_launches_without_avatar_wardrobe_package(self):
        with tempfile.TemporaryDirectory() as folder:
            archive = build(Path(folder) / 'atelier.zip')
            with zipfile.ZipFile(archive) as bundle:
                self.assertFalse(any('/Packages/dev.gryphprime.avatar-wardrobe/' in name for name in bundle.namelist()))
                bundle.extractall(folder)
            environment = dict(os.environ)
            environment.pop('PYTHONPATH', None)
            result = subprocess.run([sys.executable, '-m', 'atelier', '--help'], cwd=Path(folder) / 'atelier',
                                    env=environment, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, timeout=15)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertIn('--data', result.stdout)
