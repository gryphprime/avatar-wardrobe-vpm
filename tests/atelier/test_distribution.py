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

    def test_mac_app_launcher_is_installed_python_and_safe_to_rebuild(self):
        with tempfile.TemporaryDirectory() as folder:
            app = build(Path(folder) / 'Atelier.app', format='app', python_executable='/opt/Python With Space/bin/python3')
            launcher = app / 'Contents' / 'MacOS' / 'Atelier'
            self.assertTrue(launcher.stat().st_mode & 0o100)
            text = launcher.read_text()
            self.assertIn("DEFAULT_PYTHON='/opt/Python With Space/bin/python3'", text)
            self.assertNotIn('codesign', text)
            self.assertEqual(app, build(app, format='app', python_executable='/opt/Python With Space/bin/python3'))

    def test_app_builder_does_not_replace_arbitrary_directory(self):
        with tempfile.TemporaryDirectory() as folder:
            destination = Path(folder) / 'Atelier.app'
            destination.mkdir()
            (destination / 'keep.txt').write_text('user data')
            with self.assertRaises(ValueError):
                build(destination, format='app')
            self.assertTrue((destination / 'keep.txt').exists())
