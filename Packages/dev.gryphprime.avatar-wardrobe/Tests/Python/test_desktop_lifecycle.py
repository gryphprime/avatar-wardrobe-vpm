import json
from pathlib import Path
import sys
import tempfile
import unittest

DESKTOP = Path(__file__).resolve().parents[2] / 'Desktop'
sys.path.insert(0, str(DESKTOP))

from wardrobe_bridge import BRIDGE_PROTOCOL, BridgeLocator
from wardrobe_desktop import _write_json_atomic, desktop_port_candidates


class BridgeLocatorTests(unittest.TestCase):
    def test_reads_and_notices_port_and_session_changes(self):
        with tempfile.TemporaryDirectory() as folder:
            project = Path(folder) / 'Project'
            project.mkdir()
            record = project / 'Library' / 'AvatarWardrobe' / 'bridge.json'
            record.parent.mkdir(parents=True)
            record.write_text(json.dumps({'protocol': BRIDGE_PROTOCOL, 'project': str(project.resolve()),
                                          'port': 8910, 'session': 'a', 'online': True}))
            locator = BridgeLocator(str(project), record_path=record)
            locator._verify = lambda base, value: value['session']
            self.assertEqual(locator.endpoint(), 'http://localhost:8910')
            record.write_text(json.dumps({'protocol': BRIDGE_PROTOCOL, 'project': str(project.resolve()),
                                          'port': 8911, 'session': 'b', 'online': True}))
            self.assertEqual(locator.endpoint(), 'http://localhost:8911')

    def test_rejects_malformed_or_offline_records(self):
        with tempfile.TemporaryDirectory() as folder:
            project = Path(folder) / 'Project'
            project.mkdir()
            record = project / 'Library' / 'AvatarWardrobe' / 'bridge.json'
            record.parent.mkdir(parents=True)
            record.write_text('{not json')
            locator = BridgeLocator(str(project), fallback='http://localhost:8909', record_path=record)
            with self.assertRaises(OSError):
                locator.endpoint()

    def test_stable_desktop_port_prefers_saved_project_port(self):
        with tempfile.TemporaryDirectory() as folder:
            project = Path(folder) / 'Project'
            state = project / 'Library' / 'AvatarWardrobe' / 'desktop.json'
            _write_json_atomic(state, {'port': 8941})
            self.assertEqual(desktop_port_candidates(str(project), 0)[0], 8941)


if __name__ == '__main__':
    unittest.main()
