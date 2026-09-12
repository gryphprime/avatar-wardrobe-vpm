import hashlib
import tempfile
import unittest
import zipfile
from pathlib import Path

from atelier.library import Library


class LibraryTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        self.archive = self.root / "product.zip"
        with zipfile.ZipFile(self.archive, "w") as z:
            z.writestr("Assets/Hat.prefab", "prefab")
            z.writestr("Assets/Hat.prefab.meta", "fileFormatVersion: 2\nguid: abcdefabcdefabcdefabcdefabcdefab\n")
        self.before = hashlib.sha256(self.archive.read_bytes()).hexdigest()

    def tearDown(self): self.tmp.cleanup()

    def test_list_prefabs_duplicate_and_restart(self):
        lib = Library(self.root / "library")
        first = lib.add(self.archive, creator="A")
        second = lib.add(self.archive, creator="A")
        self.assertFalse(first["duplicate"])
        self.assertTrue(second["duplicate"])
        self.assertEqual(lib.list()[0]["prefabs"], [{"guid": "abcdefabcdefabcdefabcdefabcdefab", "path": "Assets/Hat.prefab"}])
        self.assertEqual(Library(self.root / "library").list()[0]["sha256"], first["sha256"])
        self.assertEqual(hashlib.sha256(self.archive.read_bytes()).hexdigest(), self.before)

    def test_archive_traversal_rejected(self):
        bad = self.root / "bad.zip"
        with zipfile.ZipFile(bad, "w") as z: z.writestr("../escape.txt", "x")
        with self.assertRaises(ValueError): Library(self.root / "library").add(bad)

    def test_import_plan_reports_conflict_without_mutating(self):
        lib = Library(self.root / "library")
        item = lib.add(self.archive)
        project = self.root / "project"
        (project / "ProjectSettings").mkdir(parents=True)
        (project / "ProjectSettings/ProjectVersion.txt").write_text("m_EditorVersion: 2022.3\n")
        (project / "Assets").mkdir()
        (project / "Assets/Hat.prefab").write_text("different")
        plan = lib.import_plan(item["id"], project)
        self.assertGreater(plan["conflicts"], 0)
        self.assertEqual((project / "Assets/Hat.prefab").read_text(), "different")


if __name__ == "__main__": unittest.main()
