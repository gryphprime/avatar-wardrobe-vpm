import tempfile
import unittest
from pathlib import Path

from scripts.package_preflight import check_package


class PackagePreflightTests(unittest.TestCase):
    def make_package(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        root = Path(directory.name)
        (root / 'package.json').write_text('{"name":"dev.gryphprime.avatar-wardrobe","version":"1.2.3","url":"https://x/dev.gryphprime.avatar-wardrobe-1.2.3.zip"}')
        (root / 'package.json.meta').write_text('guid: ' + 'c' * 32 + '\n')
        (root / 'Editor').mkdir(); (root / 'Editor.meta').write_text('guid: ' + 'a' * 32 + '\n')
        (root / 'Editor' / 'WardrobeVersion.json').write_text('{"version":"1.2.3"}')
        (root / 'Editor' / 'WardrobeVersion.json.meta').write_text('guid: ' + 'd' * 32 + '\n')
        (root / 'Editor' / 'Tool.cs').write_text('class Tool {}')
        (root / 'Editor' / 'Tool.cs.meta').write_text('guid: ' + 'b' * 32 + '\n')
        return root

    def test_generated_caches_are_excluded(self):
        root = self.make_package(); cache = root / 'Editor' / '__pycache__'; cache.mkdir()
        (cache / 'Tool.cpython-313.pyc').write_bytes(b'cache'); (root / 'Editor' / '._Tool.cs').write_bytes(b'appledouble')
        self.assertEqual(check_package(root), '1.2.3')

    def test_real_metadata_and_guid_errors_remain_fatal(self):
        root = self.make_package(); (root / 'Editor' / 'Missing.cs').write_text('class Missing {}')
        with self.assertRaises(AssertionError): check_package(root)
        (root / 'Editor' / 'Missing.cs.meta').write_text('guid: ' + 'b' * 32 + '\n')
        with self.assertRaises(AssertionError): check_package(root)
