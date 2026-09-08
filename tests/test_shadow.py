import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import Mock, patch
import uuid

MODULE_PATH = Path(__file__).resolve().parents[1] / 'Packages/dev.gryphprime.avatar-wardrobe/Desktop/wardrobe_shadow.py'
spec = importlib.util.spec_from_file_location('wardrobe_shadow', MODULE_PATH)
shadow = importlib.util.module_from_spec(spec); spec.loader.exec_module(shadow)


class ShadowTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.source = self.root / 'source'; self.source.mkdir()
        self.capture = self.root / 'capture'; (self.capture / 'files/Assets/Owned').mkdir(parents=True)
        (self.capture / 'files/ProjectSettings').mkdir()
        (self.capture / 'files/Assets/Owned/Avatar.unity').write_text('unsaved avatar copy')
        (self.capture / 'files/Assets/Owned/Avatar.unity.meta').write_text('guid: fixture')
        (self.capture / 'files/ProjectSettings/ProjectVersion.txt').write_text('m_EditorVersion: 2022.3.22f1\n')
        self.manifest = {
            'schemaVersion': 1, 'captureId': uuid.uuid4().hex, 'projectPath': str(self.source),
            'unityVersion': shadow.UNITY_VERSION, 'sourceRevision': 'source-v1', 'recipeRevision': 'recipe-v1',
            'environmentRevision': 'environment-v1', 'scenePath': 'Assets/Owned/Avatar.unity',
            'files': [self.record(path, self.capture / 'files') for path in (self.capture / 'files').rglob('*') if path.is_file()],
            'packages': []}
        for name, version in {'dev.gryphprime.avatar-wardrobe': '1.0.0', 'nadena.dev.ndmf': '1.14.8',
                              'nadena.dev.modular-avatar': '1.18.7', 'com.vrchat.avatars': '3.10.5', 'com.vrchat.base': '3.10.5'}.items():
            package = self.root / 'packages' / name; package.mkdir(parents=True)
            (package / 'package.json').write_text(json.dumps({'name': name, 'version': version}))
            (package / 'fixture.cs').write_text('// owned synthetic package')
            (package / 'fixture.cs.meta').write_text('guid: fixture-' + name)
            self.manifest['packages'].append({'name': name, 'version': version, 'sourcePath': str(package),
                'builtIn': False, 'files': [self.record(path, package) for path in package.iterdir()]})
        self.path = self.capture / 'manifest.json'; self.write_manifest()
        self.worker = shadow.ShadowWorker(self.root / 'cache')

    def tearDown(self): self.temp.cleanup()
    def record(self, path, root):
        return {'path': path.relative_to(root).as_posix(), 'bytes': path.stat().st_size, 'sha256': shadow.sha256(path)}
    def write_manifest(self): self.path.write_text(json.dumps(self.manifest))

    def test_staged_project_copies_inputs_and_never_shares_library_or_assets(self):
        owner = self.worker.stage(self.path)
        copied = self.worker.project / self.manifest['scenePath']
        self.assertEqual('unsaved avatar copy', copied.read_text())
        self.assertFalse(copied.is_symlink())
        self.assertEqual(owner, self.worker.stage(self.path))
        original = self.capture / 'files' / self.manifest['scenePath']
        original.write_text('changed original')
        self.assertEqual('unsaved avatar copy', copied.read_text())
        dependencies = json.loads((self.worker.project / 'Packages/manifest.json').read_text())['dependencies']
        self.assertTrue(all(value.startswith('file:' + str(self.worker.project)) for value in dependencies.values()))
        self.assertFalse((self.source / 'Library').exists())

    def test_checksum_failure_does_not_publish_partial_project(self):
        (self.capture / 'files' / self.manifest['scenePath']).write_text('changed')
        with self.assertRaisesRegex(ValueError, 'checksum'): self.worker.stage(self.path)
        self.assertFalse(self.worker.project.exists())
        self.assertEqual([], list(self.worker.cache.glob('.stage-*')))

    def test_rejects_traversal_and_package_links(self):
        self.manifest['files'][0]['path'] = '../outside'
        self.write_manifest()
        with self.assertRaisesRegex(ValueError, 'inside'): self.worker.stage(self.path)
        self.setUpSecondCapture()
        package = Path(self.manifest['packages'][0]['sourcePath'])
        fixture = package / 'fixture.cs'; fixture.unlink(); fixture.symlink_to(self.capture / 'files' / self.manifest['scenePath'])
        with self.assertRaisesRegex(ValueError, 'Linked'): self.worker.stage(self.path)

    def setUpSecondCapture(self):
        self.manifest['files'] = [self.record(path, self.capture / 'files') for path in (self.capture / 'files').rglob('*') if path.is_file()]
        self.write_manifest()

    def test_source_project_cannot_be_reused_as_worker(self):
        self.manifest['projectPath'] = str(self.worker.project)
        self.write_manifest()
        with self.assertRaisesRegex(ValueError, 'separate'): self.worker.stage(self.path)

    def test_launch_is_persistent_graphics_enabled_and_single_flight(self):
        self.worker.stage(self.path)
        executable = self.root / 'Unity'; executable.write_text('owned mock')
        process = Mock(pid=1234); process.poll.return_value = None
        with patch.object(shadow.subprocess, 'Popen', return_value=process) as popen:
            self.worker.start(executable); self.worker.start(executable)
        self.assertEqual(1, popen.call_count)
        command = popen.call_args.args[0]
        self.assertIn('-batchmode', command)
        self.assertNotIn('-nographics', command)
        self.assertNotIn('-quit', command)
        self.assertEqual(str(self.worker.project), command[command.index('-projectPath') + 1])

    def test_render_protocol_is_typed_and_image_checksums_are_verified(self):
        self.worker.stage(self.path)
        shadow.atomic_json(self.worker.runtime / 'state.json', {'state': 'ready'})
        receipt = self.worker.render('three-quarter', before=True, zoom=1.5)
        command = json.loads((self.worker.runtime / 'commands' / (receipt['id'] + '.json')).read_text())
        self.assertEqual('render', command['type']); self.assertTrue(command['before'])
        with self.assertRaises(ValueError): self.worker.render('../../scene')
        with self.assertRaises(ValueError): self.worker.render(zoom=float('nan'))
        image = self.worker.runtime / 'images' / (receipt['id'] + '.png'); image.write_bytes(b'owned synthetic image')
        result = {'id': receipt['id'], 'status': 'succeeded', 'image': 'images/' + image.name, 'imageSha256': shadow.sha256(image)}
        shadow.atomic_json(self.worker.runtime / 'results' / (receipt['id'] + '.json'), result)
        self.assertEqual(str(image), self.worker.result(receipt['id'])['imagePath'])
        image.write_bytes(b'changed')
        with self.assertRaisesRegex(ValueError, 'checksum'): self.worker.result(receipt['id'])

    def test_changed_capture_waits_for_live_worker_to_stop(self):
        self.worker.stage(self.path)
        shadow.atomic_json(self.worker.runtime / 'state.json', {'state': 'ready'})
        self.manifest['captureId'] = uuid.uuid4().hex; self.write_manifest()
        with self.assertRaisesRegex(ValueError, 'Stop'): self.worker.stage(self.path)
        receipt = self.worker.stop()
        self.assertEqual('stopping', receipt['state'])
        self.assertTrue((self.worker.runtime / 'commands' / (receipt['id'] + '.json')).exists())





class ShadowHostTests(unittest.TestCase):
    record = ShadowTests.record
    write_manifest = ShadowTests.write_manifest

    def setUp(self):
        ShadowTests.setUp(self)
        import sys
        sys.modules['wardrobe_shadow'] = shadow
        host_path = MODULE_PATH.with_name('wardrobe_shadow_host.py')
        host_spec = importlib.util.spec_from_file_location('wardrobe_shadow_host', host_path)
        self.host = importlib.util.module_from_spec(host_spec); host_spec.loader.exec_module(self.host)
        self.fake = FakeShadowWorker(self.root / 'service/worker')
        self.patch = patch.object(self.host, 'ShadowWorker', return_value=self.fake); self.patch.start()
        self.service = self.host.ShadowSnapshotService(self.root / 'service', self.root / 'Unity', self.source, timeout=3)

    def tearDown(self):
        self.service.close(); self.service.executor.shutdown(wait=True)
        self.patch.stop(); self.temp.cleanup()

    def settled(self, receipt):
        import time
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline:
            result = self.service.get(receipt['id'])
            if result['state'] in ('succeeded', 'failed', 'cancelled'): return result
            time.sleep(0.01)
        self.fail('Snapshot facade did not finish its synthetic operation.')

    def test_cache_reuses_completed_matching_capture(self):
        first = self.settled(self.service.submit(self.path))
        second = self.settled(self.service.submit(self.path))
        self.assertEqual('succeeded', first['state'], first)
        self.assertEqual('succeeded', second['state'], second)
        self.assertTrue(second['cached'])
        self.assertEqual(1, self.fake.calls.count('render'))

    def test_changed_source_automatically_stops_restages_and_restarts(self):
        self.assertEqual('succeeded', self.settled(self.service.submit(self.path))['state'])
        self.manifest['captureId'] = uuid.uuid4().hex; self.manifest['sourceRevision'] = 'source-v2'; self.write_manifest()
        self.assertEqual('succeeded', self.settled(self.service.submit(self.path))['state'])
        self.assertEqual(['stage', 'start', 'render', 'stop', 'stage', 'start', 'render'], self.fake.calls)

    def test_mismatched_result_fails_without_removing_previous_photo(self):
        first = self.settled(self.service.submit(self.path))
        self.fake.mismatch = True
        failed = self.settled(self.service.submit(self.path, 'back'))
        self.assertEqual('failed', failed['state'])
        self.assertIn('sourceRevision', failed['message'])
        self.assertTrue(Path(first['imagePath']).exists())

    def test_cross_project_capture_is_rejected_before_dispatch(self):
        self.manifest['projectPath'] = str(self.root / 'other'); self.write_manifest()
        with self.assertRaisesRegex(ValueError, 'another'): self.service.submit(self.path)
        self.assertEqual([], self.fake.calls)


class FakeShadowWorker:
    def __init__(self, cache):
        self.project = cache / 'project'; self.project.mkdir(parents=True)
        self.runtime = self.project / 'Library/AvatarWardrobeShadow'; self.runtime.mkdir(parents=True)
        self.owner = None; self.state = 'not-started'; self.calls = []; self.commands = {}; self.mismatch = False
        self.process = None
    def _owner(self): return self.owner
    def stage(self, path):
        self.calls.append('stage'); self.manifest = json.loads(Path(path).read_text())
        self.owner = {key: self.manifest[key] for key in ('captureId', 'environmentRevision')}
        return self.owner
    def start(self, executable): self.calls.append('start'); self.state = 'ready'; return self.status()
    def wait_ready(self, timeout): return self.status()
    def status(self): return {'state': self.state}
    def stop(self): self.calls.append('stop'); self.state = 'stopped'; return self.status()
    def render(self, view, before, zoom):
        self.calls.append('render'); identifier = uuid.uuid4().hex
        image = self.runtime / (identifier + '.png'); image.write_bytes(b'owned synthetic photograph')
        self.commands[identifier] = {'id': identifier, 'status': 'succeeded', 'view': view, 'before': before,
            'imagePath': str(image), 'imageSha256': shadow.sha256(image),
            **{key: self.manifest[key] for key in ('captureId', 'sourceRevision', 'recipeRevision', 'environmentRevision')}}
        if self.mismatch: self.commands[identifier]['sourceRevision'] = 'obsolete'
        return {'id': identifier}
    def result(self, identifier): return self.commands[identifier]


if __name__ == '__main__': unittest.main()
