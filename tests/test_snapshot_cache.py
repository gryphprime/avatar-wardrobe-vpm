import json
import os
from pathlib import Path
import sys
import tempfile
import time
import unittest
import uuid

DESKTOP = Path(__file__).resolve().parents[1] / 'Packages/dev.gryphprime.avatar-wardrobe/Desktop'
sys.path.insert(0, str(DESKTOP))
from wardrobe_snapshot_cache import CaptureRetention


class SnapshotRetentionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.project = Path(self.temp.name).resolve()
        self.store = CaptureRetention(self.project, max_captures=1, capture_bytes=1, package_bytes=0, grace_seconds=0)

    def tearDown(self): self.temp.cleanup()

    def capture(self, age, packages=None):
        identifier = uuid.uuid4().hex
        folder = self.store.captures / identifier; folder.mkdir(parents=True)
        path = folder / 'manifest.json'
        path.write_text(json.dumps({'schemaVersion': 1, 'captureId': identifier, 'projectPath': str(self.project),
                                    'files': [{'bytes': 10}], 'packages': packages or []}))
        os.utime(path, (time.time() - age, time.time() - age))
        return path

    def package(self, name, age):
        key = 'a' * 64 if name == 'retained' else 'b' * 64
        path = self.store.packages / (name + '-' + key); path.mkdir(parents=True)
        (path / '.wardrobe-package-snapshot.json').write_text(json.dumps({
            'kind': 'wardrobe-package-snapshot-v1', 'name': name, 'key': key, 'bytes': 100, 'lastUsed': time.time() - age}))
        return path

    def test_count_and_byte_limits_preserve_active_pinned_and_newest_inputs(self):
        package = self.package('retained', 500)
        orphan = self.package('orphan', 500)
        foreign = self.store.packages / 'unmarked'; foreign.mkdir()
        active = self.capture(400, [{'sourcePath': str(package)}])
        pinned = self.capture(300)
        old = self.capture(200)
        newest = self.capture(100)
        lease = uuid.uuid4().hex
        self.store.acquire(active, lease, 3600)
        self.store.pin(pinned, 'c' * 64)
        result = self.store.prune()
        self.assertEqual([old.parent.name], result['captures'])
        self.assertTrue(active.exists()); self.assertTrue(pinned.exists()); self.assertTrue(newest.exists())
        self.assertTrue(package.exists()); self.assertFalse(orphan.exists()); self.assertTrue(foreign.exists())
        self.store.release(active, lease)
        result = self.store.prune()
        self.assertIn(active.parent.name, result['captures'])
        self.assertFalse(package.exists())
        self.store.pin(pinned, 'c' * 64, False)
        self.store.prune()
        self.assertFalse(pinned.exists()); self.assertTrue(newest.exists())

    def test_grace_period_protects_new_publication_and_unreferenced_package(self):
        self.store.grace_seconds = 3600
        old = self.capture(100)
        self.capture(1)
        package = self.package('orphan', 1)
        self.assertEqual({'captures': [], 'packages': []}, self.store.prune())
        self.assertTrue(old.exists()); self.assertTrue(package.exists())

    def test_foreign_and_linked_paths_are_never_retained_or_deleted(self):
        path = self.capture(100)
        value = json.loads(path.read_text()); value['projectPath'] = str(self.project / 'other')
        path.write_text(json.dumps(value))
        with self.assertRaisesRegex(ValueError, 'another'): self.store.acquire(path, uuid.uuid4().hex)
        self.store.prune(); self.assertTrue(path.exists())
        target = self.project / 'outside'; target.mkdir()
        link = self.store.captures / uuid.uuid4().hex; link.symlink_to(target, target_is_directory=True)
        (target / 'manifest.json').write_text('{}')
        with self.assertRaisesRegex(ValueError, 'Linked'): self.store.acquire(link / 'manifest.json', uuid.uuid4().hex)
        self.store.prune(); self.assertTrue(target.exists())


if __name__ == '__main__': unittest.main()
