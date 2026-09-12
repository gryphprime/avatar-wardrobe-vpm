import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / 'scripts/prepare_unity_tests.py'

class FixtureBuilderTests(unittest.TestCase):
    def test_writes_scene_safety_marker(self):
        with tempfile.TemporaryDirectory() as temp:
            source = Path(temp); (source / 'ProjectSettings').mkdir(); (source / 'Packages').mkdir(); (source / 'Library/PackageCache').mkdir(parents=True)
            (source / 'ProjectSettings/ProjectVersion.txt').write_text('m_EditorVersion: 2022.3.22f1\n')
            (source / 'Packages/manifest.json').write_text(json.dumps({'dependencies': {}}))
            packages = {'com.vrchat.avatars':'3.10.5','com.vrchat.base':'3.10.5','nadena.dev.modular-avatar':'1.18.7','nadena.dev.ndmf':'1.14.8','com.unity.test-framework':'1.3.9','com.unity.ugui':'1.0.0','com.unity.modules.imgui':'1.0.0','com.unity.modules.imageconversion':'1.0.0','com.unity.modules.physics':'1.0.0','com.unity.modules.cloth':'1.0.0','com.unity.modules.animation':'1.0.0','com.unity.modules.audio':'1.0.0','com.unity.modules.unitywebrequest':'1.0.0','com.unity.modules.unitywebrequesttexture':'1.0.0','com.unity.modules.unitywebrequestaudio':'1.0.0','com.unity.modules.unitywebrequestassetbundle':'1.0.0'}
            for name, version in packages.items():
                path = source / 'Library/PackageCache' / (name + '@test'); path.mkdir(); (path / 'package.json').write_text(json.dumps({'name':name,'version':version}))
            with tempfile.TemporaryDirectory() as output_root:
                output = str(Path(output_root) / 'fixture')
                result = subprocess.run([sys.executable, str(SCRIPT), '--dependencies', str(source), '--output', output], capture_output=True, text=True)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertEqual((Path(output) / '.wardrobe-test-fixture').read_text(), 'Avatar Wardrobe isolated test fixture v1\n')

if __name__ == '__main__': unittest.main()
