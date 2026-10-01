import hashlib
import io
import json
import os
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'Packages/dev.gryphprime.avatar-wardrobe/Desktop'))

from wardrobe_desktop import Handler, read_json  # noqa: E402
from wardrobe_library import Library  # noqa: E402
from wardrobe_shadow import copy_checked  # noqa: E402


class DesktopHostTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name)

    def make_project(self):
        project = self.root / 'project'
        (project / 'ProjectSettings').mkdir(parents=True)
        (project / 'ProjectSettings/ProjectVersion.txt').write_text('m_EditorVersion: 2022.3.22f1\n')
        (project / 'Assets').mkdir()
        return project

    def make_archive(self):
        archive = self.root / 'outfit.zip'
        with zipfile.ZipFile(archive, 'w') as output:
            output.writestr('Assets/Outfit.meta', 'fileFormatVersion: 2\nguid: ' + 'a' * 32 + '\nfolderAsset: yes\n')
            output.writestr('Assets/Outfit/Empty.meta', 'fileFormatVersion: 2\nguid: ' + 'b' * 32 + '\nfolderAsset: yes\n')
            output.writestr('Assets/Outfit/Shirt.prefab', '%YAML 1.1\n--- !u!1 &1\nGameObject: {}\n')
            output.writestr('Assets/Outfit/Shirt.prefab.meta', 'fileFormatVersion: 2\nguid: ' + 'c' * 32 + '\n')
        return archive

    def test_library_imports_reviewed_files_and_empty_folders(self):
        library = Library(self.root / 'library')
        added = library.add(self.make_archive(), 'Creator', 'Outfit')
        self.assertEqual(added['files'], 4)
        self.assertTrue(library.add(self.make_archive())['duplicate'])
        project = self.make_project()
        plan = library.import_plan(added['hash'], project)
        self.assertEqual({entry['state'] for entry in plan['files']}, {'new'})
        checks = []
        result = library.apply_import(added['hash'], project, plan, cancel_check=lambda: checks.append(1))
        self.assertEqual(result['copied'], 4)
        self.assertTrue(checks)
        self.assertTrue((project / 'Assets/Outfit/Empty').is_dir())
        self.assertIn('GameObject', (project / 'Assets/Outfit/Shirt.prefab').read_text())
        listed = library.list()
        self.assertEqual(len(listed), 1)
        self.assertEqual(listed[0]['files'][0]['guid'], 'c' * 32)
        self.assertEqual(len(listed[0]['usage']), 1)
        again = library.import_plan(added['hash'], project)
        self.assertEqual({entry['state'] for entry in again['files']}, {'unchanged'})

    def test_cancelled_import_rolls_back_created_files(self):
        library = Library(self.root / 'library')
        sha = library.add(self.make_archive())['hash']
        project = self.make_project()
        plan = library.import_plan(sha, project)
        def cancel():
            # Lose the lease after the first project file and its folders were created.
            if (project / 'Assets/Outfit.meta').exists():
                raise ValueError('lost')
        with self.assertRaises(ValueError):
            library.apply_import(sha, project, plan, cancel_check=cancel)
        self.assertEqual(list((project / 'Assets').iterdir()), [])

    def test_copy_checked_verifies_streamed_bytes(self):
        source, destination = self.root / 'source', self.root / 'destination'
        (source / 'Assets').mkdir(parents=True)
        data = b'mesh' * 1000
        (source / 'Assets/Mesh.asset').write_bytes(data)
        entry = {'path': 'Assets/Mesh.asset', 'sha256': hashlib.sha256(data).hexdigest(), 'bytes': len(data)}
        copy_checked(source, entry, destination)
        self.assertEqual((destination / 'Assets/Mesh.asset').read_bytes(), data)
        with self.assertRaises(ValueError):
            copy_checked(source, dict(entry, sha256='0' * 64), self.root / 'other')

    def test_read_json_rejects_oversized_responses(self):
        self.assertEqual(read_json(io.BytesIO(b'{"ok":1}'), 16, 'too large'), {'ok': 1})
        with self.assertRaisesRegex(ValueError, 'too large'):
            read_json(io.BytesIO(b'{"ok":1}'), 4, 'too large')

    def test_cache_prune_keeps_newest_entries(self):
        cache = self.root / 'cache'
        cache.mkdir()
        for index in range(4100):
            path = cache / ('%05d.json' % index)
            path.write_text('{}')
            os.utime(path, (index, index))
        Handler.prune_cache(SimpleNamespace(server=SimpleNamespace(cache=cache)))
        remaining = sorted(path.name for path in cache.glob('*.json'))
        self.assertEqual(len(remaining), 4096)
        self.assertEqual(remaining[0], '00004.json')


if __name__ == '__main__':
    unittest.main()
