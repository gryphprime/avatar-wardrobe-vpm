"""Descriptor detection must not hide prefabs that only contain SDK constraints."""
import json
from pathlib import Path
import sys
import unittest

INDEXER = Path(__file__).resolve().parents[1] / 'Packages/dev.gryphprime.avatar-wardrobe/Editor/WardrobeIndexer'
sys.path.insert(0, str(INDEXER))
from wardrobe_index import Indexer


class DescriptorDetectionTests(unittest.TestCase):
    def setUp(self):
        self.config = json.loads((INDEXER / 'wardrobe_config.json').read_text())
        self.indexer = Indexer('.', self.config)

    def test_actual_descriptor(self):
        self.assertTrue(self.indexer.is_avatar_scripts({
            (self.indexer.vrc_guid, self.indexer.vrc_fileid)}))

    def test_other_component_in_descriptor_assembly(self):
        self.assertFalse(self.indexer.is_avatar_scripts({
            (self.indexer.vrc_guid, '123')}))

    def test_constraint_dll_is_not_a_descriptor_even_with_legacy_mapping(self):
        guid = '58e2f01a24261a14cb82e6d3399e8b16'
        self.assertNotEqual(self.config['scripts'].get(guid), 'VRCAvatarDescriptor')
        self.indexer.script_names[guid] = 'VRCAvatarDescriptor'
        for file_id in ('575728033', '1788371120'):
            with self.subTest(file_id=file_id):
                self.assertFalse(self.indexer.is_avatar_scripts({(guid, file_id)}))

    def test_source_script_descriptor_is_supported(self):
        self.indexer.script_names['source-script'] = 'VRCAvatarDescriptor'
        self.assertTrue(self.indexer.is_avatar_scripts({('source-script', '11500000')}))


if __name__ == '__main__':
    unittest.main()
