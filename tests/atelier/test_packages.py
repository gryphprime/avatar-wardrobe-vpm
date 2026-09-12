import json
import os
import stat
import tempfile
import unittest
from pathlib import Path
from atelier.packages import MAX_OUTPUT, PackageRuntime


class PackageTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(); self.root = Path(self.tmp.name)
        (self.root / "Packages").mkdir()
        (self.root / "Packages/manifest.json").write_text(json.dumps({"dependencies": {"com.example.tool": "1.0.0"}}))
        self.exe = self.root / "fake-vrc-get"
        self.exe.write_text("#!/bin/sh\nprintf 'ok\\n'\n")
        self.exe.chmod(self.exe.stat().st_mode | stat.S_IXUSR)
        self.runtime = PackageRuntime(str(self.exe))
    def tearDown(self): self.tmp.cleanup()
    def test_plan_is_exact_and_bounded(self):
        plan = self.runtime.plan(self.root, "install", "com.example.tool", "1.2.0")
        self.assertEqual(plan["command"], ["vrc-get", "install", "--yes", "com.example.tool", "1.2.0"])
        with self.assertRaises(ValueError): self.runtime.plan(self.root, "install", "evil;touch", "1")
    def test_changed_manifest_and_unity_lock_refuse_apply(self):
        plan = self.runtime.plan(self.root, "remove", "com.example.tool")
        (self.root / "Packages/manifest.json").write_text('{"dependencies":{}}')
        with self.assertRaises(ValueError): self.runtime.apply(plan)
        plan = self.runtime.plan(self.root, "remove", "com.example.tool")
        (self.root / "Library").mkdir(); (self.root / "Library/UnityLockfile").write_text("")
        with self.assertRaises(RuntimeError): self.runtime.apply(plan)
    def test_fake_apply_no_shell_and_bounded_result(self):
        plan = self.runtime.plan(self.root, "install", "com.example.tool", "1.2.0")
        result = self.runtime.apply(plan)
        self.assertTrue(result["ok"]); self.assertEqual(result["returncode"], 0)

    def test_output_is_bounded_and_lock_is_exclusive(self):
        self.exe.write_text("#!/bin/sh\nprintf '%*s' 1000000 x\n")
        self.exe.chmod(self.exe.stat().st_mode | stat.S_IXUSR)
        result = self.runtime.apply(self.runtime.plan(self.root, "install", "com.example.tool", "1.2.0"))
        self.assertLessEqual(len(result["stdout"]), MAX_OUTPUT)
        import fcntl
        lock = (self.root / ".atelier-package.lock").open("a+b")
        try:
            fcntl.flock(lock.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
            with self.assertRaises(RuntimeError): self.runtime.apply(self.runtime.plan(self.root, "remove", "com.example.tool"))
        finally:
            fcntl.flock(lock.fileno(), fcntl.LOCK_UN); lock.close()


if __name__ == "__main__": unittest.main()
