"""Owned, copy-based Unity shadow projects and a fixed snapshot command protocol.

No network, source-project edits, shared Library, asset symlinks, or headless graphics
fallback. Unity stays open for subsequent Front/Three-quarter/Back requests. A new
source capture is staged only after the prior worker has stopped.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import time
import uuid
from functools import wraps

UNITY_VERSION = '2022.3.22f1'
SETTINGS = {'ProjectVersion.txt', 'ProjectSettings.asset', 'GraphicsSettings.asset',
            'QualitySettings.asset', 'TagManager.asset', 'TimeManager.asset',
            'DynamicsManager.asset', 'Physics2DSettings.asset'}
NAME = re.compile(r'^[a-z0-9][a-z0-9._-]*$')
HEX = re.compile(r'^[0-9a-f]{64}$')
EXCLUDED = {'.git', 'Library', 'Temp', 'obj', 'node_modules', '__Generated', '__pycache__', '.DS_Store'}


def sha256(path):
    digest = hashlib.sha256()
    with Path(path).open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(block)
    return digest.hexdigest()


def atomic_json(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + '.' + uuid.uuid4().hex + '.tmp')
    try:
        with temporary.open('x', encoding='utf-8') as stream:
            json.dump(value, stream, sort_keys=True, indent=2)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def relative_path(value):
    if not isinstance(value, str) or not value or '\\' in value or '\x00' in value:
        raise ValueError('Invalid capture-relative path.')
    path = PurePosixPath(value)
    if path.is_absolute() or any(part in ('', '.', '..') for part in value.split('/')):
        raise ValueError('Capture paths must stay inside their declared root.')
    return Path(*path.parts)


def no_links(path, root):
    path, root = Path(path), Path(root)
    if not path.is_relative_to(root):
        raise ValueError('A capture input escaped its root.')
    for entry in (path, *path.parents):
        if entry.is_symlink():
            raise ValueError('Linked capture inputs cannot be isolated: ' + str(entry))
        if entry == root:
            break
    if not path.is_file():
        raise ValueError('Capture input is missing: ' + str(path))


def copy_checked(root, entry, destination):
    relative = relative_path(entry.get('path'))
    expected, size = entry.get('sha256'), entry.get('bytes')
    if not isinstance(expected, str) or not HEX.fullmatch(expected) or not isinstance(size, int) or size < 0:
        raise ValueError('Capture checksum metadata is invalid.')
    source = root / relative
    no_links(source, root)
    if source.stat().st_size != size or sha256(source) != expected:
        raise ValueError('Capture input changed or failed its checksum: ' + str(relative))
    target = destination / relative
    target.parent.mkdir(parents=True, exist_ok=True)
    with source.open('rb') as incoming, target.open('xb') as outgoing:
        shutil.copyfileobj(incoming, outgoing, 1024 * 1024)
    if target.stat().st_size != size or sha256(target) != expected or sha256(source) != expected:
        raise ValueError('Capture input changed while copying: ' + str(relative))


def load_manifest(path):
    path = Path(path).absolute()
    no_links(path, path.parent)
    manifest = json.loads(path.read_text(encoding='utf-8'))
    if not isinstance(manifest, dict) or manifest.get('schemaVersion') != 1:
        raise ValueError('Unsupported shadow capture schema.')
    if manifest.get('unityVersion') != UNITY_VERSION:
        raise ValueError('Shadow capture requires Unity ' + UNITY_VERSION + '.')
    capture_id = manifest.get('captureId', '')
    if not isinstance(capture_id, str) or len(capture_id) != 32 or uuid.UUID(capture_id).hex != capture_id:
        raise ValueError('Invalid capture identity.')
    for key in ('sourceRevision', 'recipeRevision', 'environmentRevision'):
        if not isinstance(manifest.get(key), str) or not manifest[key]:
            raise ValueError('Missing capture revision: ' + key)
    if not isinstance(manifest.get('projectPath'), str) or not manifest['projectPath']:
        raise ValueError('Missing source project identity.')
    seen = set()
    for record in manifest.get('files', []):
        relative = relative_path(record.get('path'))
        if relative.as_posix() in seen:
            raise ValueError('Duplicate capture file.')
        seen.add(relative.as_posix())
        if relative.parts[0] != 'Assets' and not (len(relative.parts) == 2 and relative.parts[0] == 'ProjectSettings' and relative.name in SETTINGS):
            raise ValueError('Unexpected capture output root.')
    scene = relative_path(manifest.get('avatarPrefabPath') or manifest.get('scenePath'))
    if scene.as_posix() not in seen or scene.suffix not in ('.unity', '.prefab'):
        raise ValueError('Capture is missing its serialized avatar input.')
    if 'ProjectSettings/ProjectVersion.txt' not in seen:
        raise ValueError('Capture is missing the Unity version pin.')
    packages = set()
    for package in manifest.get('packages', []):
        name = package.get('name', '')
        if not isinstance(name, str) or not NAME.fullmatch(name) or name in packages:
            raise ValueError('Invalid or duplicate captured package.')
        packages.add(name)
        if not isinstance(package.get('version'), str) or not package['version']:
            raise ValueError('Missing captured package version.')
        if package.get('builtIn'):
            if not name.startswith('com.unity.'):
                raise ValueError('Only Unity packages may be declared built in.')
            continue
        file_names = set()
        for record in package.get('files', []):
            relative = relative_path(record.get('path'))
            if relative.as_posix() in file_names or any(part in EXCLUDED or part.startswith('._') for part in relative.parts):
                raise ValueError('Unexpected or duplicate package file.')
            file_names.add(relative.as_posix())
        if 'package.json' not in file_names:
            raise ValueError('Captured package has no package.json: ' + name)
    required = {'dev.gryphprime.avatar-wardrobe', 'nadena.dev.ndmf', 'nadena.dev.modular-avatar', 'com.vrchat.avatars', 'com.vrchat.base'}
    if not required.issubset(packages):
        raise ValueError('Capture is missing the supported Wardrobe/NDMF/MA/VRChat package environment.')
    return path, manifest


def process_identity(pid):
    """Non-signalling query: (alive/dead/unknown, OS process start identity)."""
    if not isinstance(pid, int) or pid <= 0: return 'unknown', None
    if os.name == 'nt':
        import ctypes
        from ctypes import wintypes
        kernel = ctypes.WinDLL('kernel32', use_last_error=True)
        kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        kernel.OpenProcess.restype = wintypes.HANDLE
        kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        kernel.GetExitCodeProcess.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]
        kernel.GetProcessTimes.argtypes = [wintypes.HANDLE] + [ctypes.POINTER(wintypes.FILETIME)] * 4
        handle = kernel.OpenProcess(0x1000, False, pid) # PROCESS_QUERY_LIMITED_INFORMATION
        if not handle: return ('dead' if ctypes.get_last_error() == 87 else 'unknown'), None
        try:
            code = wintypes.DWORD()
            if not kernel.GetExitCodeProcess(handle, ctypes.byref(code)): return 'unknown', None
            if code.value != 259: return 'dead', None
            times = [wintypes.FILETIME() for _ in range(4)]
            if not kernel.GetProcessTimes(handle, *[ctypes.byref(t) for t in times]): return 'unknown', None
            return 'alive', str((times[0].dwHighDateTime << 32) | times[0].dwLowDateTime)
        finally: kernel.CloseHandle(handle)
    try:
        os.kill(pid, 0) # POSIX only; never used on Windows.
    except ProcessLookupError: return 'dead', None
    except OSError: return 'unknown', None
    try:
        if Path('/proc').is_dir():
            fields = Path('/proc', str(pid), 'stat').read_text().rsplit(')', 1)[1].split()
            return 'alive', fields[19]
        result = subprocess.run(['ps', '-p', str(pid), '-o', 'lstart='], capture_output=True, text=True, timeout=2)
        stamp = result.stdout.strip()
        return ('alive', stamp) if result.returncode == 0 and stamp else ('unknown', None)
    except (OSError, ValueError, IndexError, subprocess.TimeoutExpired): return 'unknown', None


def lifecycle_locked(function):
    @wraps(function)
    def invoke(self, *args, **kwargs):
        self.cache.mkdir(parents=True, exist_ok=True)
        with (self.cache / 'lifecycle.lock').open('a+b') as lock:
            lock.seek(0)
            if os.name == 'nt':
                import msvcrt
                if lock.read(1) == b'':
                    lock.write(b'0'); lock.flush()
                lock.seek(0)
                try: msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
                except OSError as error: raise RuntimeError('Another shadow lifecycle operation is active.') from error
            else:
                import fcntl
                try: fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
                except BlockingIOError as error: raise RuntimeError('Another shadow lifecycle operation is active.') from error
            try:
                return function(self, *args, **kwargs)
            finally:
                if os.name == 'nt':
                    lock.seek(0); msvcrt.locking(lock.fileno(), msvcrt.LK_UNLCK, 1)
                else: fcntl.flock(lock, fcntl.LOCK_UN)
    return invoke


class ShadowWorker:
    def __init__(self, cache):
        raw_cache = Path(cache).absolute()
        if raw_cache.is_symlink():
            raise ValueError('The shadow cache must not be a link.')
        self.cache = raw_cache.resolve()
        self.project = self.cache / 'project'
        self.runtime = self.project / 'Library' / 'AvatarWardrobeShadow'
        self.process = None

    def _owner(self):
        marker = self.project / '.wardrobe-shadow-owner.json'
        no_links(marker, self.project)
        owner = json.loads(marker.read_text())
        if owner.get('projectPath') != str(self.project.resolve()) or not owner.get('ownerToken'):
            raise ValueError('This directory is not an owned Wardrobe shadow project.')
        return owner

    @lifecycle_locked
    def stage(self, capture):
        capture_path, manifest = load_manifest(capture)
        source = Path(manifest['projectPath']).resolve()
        destination = self.project.resolve()
        if source == destination or source.is_relative_to(destination):
            raise ValueError('The shadow project must be separate from the source project.')
        if destination.is_relative_to(source / 'Assets') or destination.is_relative_to(source / 'Packages'):
            raise ValueError('The shadow project cannot be inside source Assets or Packages.')
        self.cache.mkdir(parents=True, exist_ok=True)
        preserve_library = False
        if self.project.exists():
            owner = self._owner()
            preserve_library = owner.get("environmentRevision") == manifest["environmentRevision"] and owner.get("sourceProject") == str(source)
            if owner.get('captureId') == manifest['captureId'] and owner.get('environmentRevision') == manifest['environmentRevision']:
                return owner
            if (self.project / 'Temp/UnityLockfile').exists() or self.status().get('state') not in ('stopped', 'not-started', 'failed'):
                raise ValueError('Stop the owned shadow worker before staging a changed capture.')
        temporary = self.cache / ('.stage-' + uuid.uuid4().hex)
        temporary.mkdir()
        token = uuid.uuid4().hex
        try:
            for entry in manifest['files']:
                copy_checked(capture_path.parent / 'files', entry, temporary)
            dependencies = {}
            for package in manifest['packages']:
                if package.get('builtIn'):
                    dependencies[package['name']] = package['version']
                    continue
                package_source = Path(package['sourcePath']).absolute()
                package_target = temporary / 'Packages' / package['name']
                if preserve_library:
                    package_target = self.project / 'Packages' / package['name']
                    for entry in package['files']:
                        existing = package_target / relative_path(entry['path'])
                        no_links(existing, package_target)
                        if existing.stat().st_size != entry['bytes'] or sha256(existing) != entry['sha256']:
                            raise ValueError('The preserved package environment changed. Reset the private worker before staging.')
                else:
                    for entry in package['files']:
                        copy_checked(package_source, entry, package_target)
                data = json.loads((package_target / 'package.json').read_text(encoding='utf-8'))
                if data.get('name') != package['name'] or data.get('version') != package['version']:
                    raise ValueError('Copied package identity does not match the capture.')
                dependencies[package['name']] = 'file:' + str(destination / 'Packages' / package['name'])
            if not preserve_library:
                atomic_json(temporary / 'Packages/manifest.json', {'dependencies': dependencies})
            runtime_relative = Path('Library/AvatarWardrobeShadow')
            for folder in ('commands', 'results', 'images'):
                (temporary / runtime_relative / folder).mkdir(parents=True, exist_ok=True)
            atomic_json(temporary / runtime_relative / 'capture.json', manifest)
            owner = {'schemaVersion': 1, 'ownerToken': token, 'projectPath': str(destination),
                     'sourceProject': str(source), 'captureId': manifest['captureId'],
                     'environmentRevision': manifest['environmentRevision']}
            atomic_json(temporary / '.wardrobe-shadow-owner.json', owner)
            atomic_json(temporary / runtime_relative / 'run.json', {
                'ownerToken': token, 'projectPath': str(destination),
                'manifestPath': str(destination / runtime_relative / 'capture.json'),
                'runtimePath': str(destination / runtime_relative)})
            atomic_json(temporary / runtime_relative / 'state.json', {'state': 'not-started', 'captureId': manifest['captureId']})
            if self.project.exists():
                self._owner()  # Recheck immediately before replacing only our owned output.
                if preserve_library:
                    if (self.project / 'Packages').is_symlink(): raise ValueError('Linked worker packages cannot be preserved.')
                    os.replace(self.project / 'Packages', temporary / 'Packages')
                if preserve_library and (self.project / 'Library').is_dir():
                    library = self.project / 'Library'
                    if library.is_symlink(): raise ValueError('Linked worker Library cannot be preserved.')
                    # Runtime receipts belong to the old capture; import artifacts do not.
                    old_runtime = library / 'AvatarWardrobeShadow'
                    if old_runtime.is_symlink(): raise ValueError('Linked worker runtime cannot be replaced.')
                    if old_runtime.exists(): shutil.rmtree(old_runtime)
                    os.replace(temporary / runtime_relative, old_runtime)
                    (temporary / 'Library').rmdir()
                    os.replace(library, temporary / 'Library')
                shutil.rmtree(self.project)
            os.replace(temporary, self.project)
            self.process = None
            return owner
        finally:
            if temporary.exists():
                shutil.rmtree(temporary)

    @lifecycle_locked
    def start(self, unity):
        self._owner()
        current = self.status()
        if current.get('state') == 'unknown':
            raise ValueError(current.get('message') or 'Worker ownership is unknown.')
        if current.get('state') in ('starting', 'initializing', 'processing', 'ready'):
            return current
        if (self.project / 'Temp/UnityLockfile').exists():
            raise ValueError('The private worker project is already open. Close that editor before restarting it.')
        executable = Path(unity).expanduser().resolve()
        if executable.is_dir() and executable.suffix.lower() == '.app':
            executable = (executable / 'Contents/MacOS/Unity').resolve()
        if not executable.is_file():
            raise ValueError('Choose the Unity ' + UNITY_VERSION + ' executable or a Unity.app bundle containing Contents/MacOS/Unity.')
        command = [str(executable), '-batchmode', '-projectPath', str(self.project),
                   '-executeMethod', 'OutfitToggleGenerator.WardrobeShadowWorker.Start',
                   '-logFile', str(self.runtime / 'Unity.log'),
                   '-wardrobeWorker', str(self.runtime / 'run.json')]
        # No -quit: retain the worker. No -nographics: snapshots require a GPU.
        state = {'state': 'starting', 'message': 'Starting the graphics-enabled private Unity worker.'}
        atomic_json(self.runtime / 'state.json', state)
        try:
            self.process = subprocess.Popen(command, cwd=self.project, stdin=subprocess.DEVNULL,
                                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        except Exception:
            atomic_json(self.runtime / 'state.json', {'state': 'failed', 'message': 'Unity could not be started.'})
            raise
        atomic_json(self.runtime / 'launch.json', {'pid': self.process.pid, 'command': command, 'started': time.time(), 'processIdentity': process_identity(self.process.pid)[1]})
        return {**state, 'pid': self.process.pid}

    def status(self):
        if not self.project.exists():
            return {'state': 'not-started'}
        self._owner()
        path = self.runtime / 'state.json'
        state = json.loads(path.read_text()) if path.exists() else {'state': 'not-started'}
        if self.process is not None and self.process.poll() is not None and state.get('state') != 'stopped':
            return {**state, 'state': 'failed', 'message': 'The Unity worker exited. Inspect its Unity.log.', 'exitCode': self.process.returncode}
        launch = self.runtime / 'launch.json'
        if self.process is None and launch.exists() and state.get('state') not in ('stopped', 'not-started', 'failed'):
            launch_data = json.loads(launch.read_text())
            health, identity = process_identity(launch_data.get('pid'))
            expected = launch_data.get('processIdentity')
            if health == 'dead' or (identity and expected and identity != expected):
                return {**state, 'state': 'failed', 'message': 'The original Unity worker exited. Inspect its Unity.log.'}
            if health != 'alive' or not identity or not expected:
                return {**state, 'state': 'unknown', 'message': 'Worker ownership could not be verified. Close the private editor before recovery.'}
        return state

    def wait_ready(self, timeout=180):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            state = self.status()
            if state.get('state') == 'ready':
                return state
            if state.get('state') in ('failed', 'stopped'):
                raise RuntimeError(state.get('message') or 'The shadow worker stopped.')
            time.sleep(0.1)
        raise TimeoutError('The shadow editor is still importing or processing. Inspect its status and Unity.log.')

    def render(self, view='front', before=False, zoom=1.0):
        self._owner()
        if view not in ('front', 'three-quarter', 'back'):
            raise ValueError('Choose front, three-quarter or back.')
        if isinstance(zoom, bool) or not isinstance(zoom, (int, float)) or not math.isfinite(zoom) or not 0.5 <= zoom <= 2.5:
            raise ValueError('Snapshot zoom must be between 0.5 and 2.5.')
        if self.status().get('state') not in ('ready', 'processing'):
            raise ValueError('Wait for the shadow worker to become ready before requesting a photograph.')
        identifier = uuid.uuid4().hex
        atomic_json(self.runtime / 'commands' / (identifier + '.json'),
                    {'id': identifier, 'type': 'render', 'view': view, 'before': bool(before), 'zoom': zoom})
        return {'id': identifier, 'state': 'queued'}

    def result(self, identifier):
        if not isinstance(identifier, str) or len(identifier) != 32 or uuid.UUID(identifier).hex != identifier:
            raise ValueError('Invalid snapshot request identity.')
        self._owner()
        path = self.runtime / 'results' / (identifier + '.json')
        if not path.exists():
            return {'id': identifier, 'status': 'pending'}
        result = json.loads(path.read_text())
        if result.get('status') == 'succeeded' and result.get('image'):
            image = self.runtime / relative_path(result['image'])
            no_links(image, self.runtime)
            if sha256(image) != result.get('imageSha256'):
                raise ValueError('The completed snapshot failed its checksum.')
            result['imagePath'] = str(image)
        return result

    def stop(self):
        self._owner()
        state = self.status()
        if state.get('state') in ('not-started', 'stopped', 'failed'):
            return state
        identifier = uuid.uuid4().hex
        atomic_json(self.runtime / 'commands' / (identifier + '.json'), {'id': identifier, 'type': 'stop'})
        return {'id': identifier, 'state': 'stopping', 'message': 'The worker stops after its current synchronous capture finishes.'}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=('stage', 'start', 'status', 'render', 'result', 'stop'))
    parser.add_argument('--cache', required=True)
    parser.add_argument('--capture')
    parser.add_argument('--unity')
    parser.add_argument('--view', default='front', choices=('front', 'three-quarter', 'back'))
    parser.add_argument('--before', action='store_true')
    parser.add_argument('--zoom', type=float, default=1.0)
    parser.add_argument('--id')
    arguments = parser.parse_args()
    worker = ShadowWorker(arguments.cache)
    if arguments.action == 'stage':
        if not arguments.capture: parser.error('stage requires --capture')
        value = worker.stage(arguments.capture)
    elif arguments.action == 'start':
        if not arguments.unity: parser.error('start requires --unity')
        value = worker.start(arguments.unity)
    elif arguments.action == 'render': value = worker.render(arguments.view, arguments.before, arguments.zoom)
    elif arguments.action == 'result': value = worker.result(arguments.id)
    elif arguments.action == 'stop': value = worker.stop()
    else: value = worker.status()
    print(json.dumps(value, indent=2))


if __name__ == '__main__':
    main()
