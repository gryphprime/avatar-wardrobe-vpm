"""Retention for generated captures and content-addressed package inputs.

The lock-file protocol is shared with WardrobeShadowCapture. Active requests take
leases; pinned photographs retain their source manifest. Protected inputs may
exceed the soft byte/count limits. Cleanup only touches marked generated paths.
"""
from contextlib import contextmanager
import json
import os
from pathlib import Path
import re
import shutil
import time
import threading

from wardrobe_shadow import atomic_json

HEX32 = re.compile(r'^[0-9a-f]{32}$')
HEX64 = re.compile(r'^[0-9a-f]{64}$')


_retention_gate = threading.Lock()

@contextmanager
def retention_lock(root):
    # Same byte-range protocol as FileStream.Lock(0, 1) in Unity: POSIX fcntl
    # record locks / Windows LockFile. Never unlink the inode while contenders wait.
    root.mkdir(parents=True, exist_ok=True)
    with _retention_gate, (root / 'snapshot-retention-v2.lock').open('a+b') as stream:
        deadline = time.monotonic() + 2
        if os.name == 'nt':
            import msvcrt
            stream.seek(0)
            if not stream.read(1): stream.write(b'0'); stream.flush()
        else:
            import fcntl
        while True:
            try:
                stream.seek(0)
                if os.name == 'nt': msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
                else: fcntl.lockf(stream, fcntl.LOCK_EX | fcntl.LOCK_NB, 1, 0)
                break
            except OSError:
                if time.monotonic() >= deadline: raise RuntimeError('Snapshot retention is busy. Try again shortly.')
                time.sleep(0.01)
        try: yield
        finally:
            stream.seek(0)
            if os.name == 'nt': msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)
            else: fcntl.lockf(stream, fcntl.LOCK_UN, 1, 0)


class CaptureRetention:
    def __init__(self, project, *, max_captures=20, capture_bytes=2 * 1024 ** 3,
                 package_bytes=4 * 1024 ** 3, grace_seconds=24 * 3600):
        self.project = Path(project).resolve()
        self.root = self.project / 'Library/AvatarWardrobe'
        self.captures = self.root / 'captures'
        self.packages = self.root / 'package-snapshots'
        self.max_captures, self.capture_bytes, self.package_bytes = max_captures, capture_bytes, package_bytes
        self.grace_seconds = grace_seconds

    def _manifest(self, path):
        raw = Path(path).absolute()
        if raw.is_symlink() or raw.parent.is_symlink(): raise ValueError('Linked capture manifests cannot be retained.')
        path = raw.resolve()
        if (path.name != 'manifest.json' or path.parent.parent != self.captures or
                not HEX32.fullmatch(path.parent.name) or path.is_symlink() or path.parent.is_symlink()):
            raise ValueError('Retention requires an owned capture manifest.')
        value = json.loads(path.read_text())
        if (value.get('schemaVersion') != 1 or value.get('captureId') != path.parent.name or
                str(Path(value.get('projectPath', '')).resolve()) != str(self.project)):
            raise ValueError('The retained capture belongs to another project.')
        return path, value

    def acquire(self, path, identifier, seconds=3600):
        if not HEX32.fullmatch(identifier): raise ValueError('Invalid capture lease identity.')
        with retention_lock(self.root):
            path, _ = self._manifest(path)
            atomic_json(path.parent / ('.wardrobe-lease-' + identifier + '.json'),
                        {'expires': time.time() + seconds, 'id': identifier})

    def release(self, path, identifier):
        if not HEX32.fullmatch(identifier): raise ValueError('Invalid capture lease identity.')
        with retention_lock(self.root):
            path, _ = self._manifest(path)
            (path.parent / ('.wardrobe-lease-' + identifier + '.json')).unlink(missing_ok=True)

    def pin(self, path, key, pinned=True):
        if not HEX64.fullmatch(key): raise ValueError('Invalid pinned capture identity.')
        with retention_lock(self.root):
            path, _ = self._manifest(path)
            marker = path.parent / ('.wardrobe-pin-' + key + '.json')
            if pinned: atomic_json(marker, {'snapshotKey': key})
            else: marker.unlink(missing_ok=True)

    def prune(self):
        deleted = {'captures': [], 'packages': []}
        with retention_lock(self.root):
            now = time.time()
            entries = []
            for path in self.captures.glob('*/manifest.json'):
                try:
                    path, value = self._manifest(path)
                    age = now - path.stat().st_mtime
                    protected = age < self.grace_seconds or any(path.parent.glob('.wardrobe-pin-*.json'))
                    for lease in path.parent.glob('.wardrobe-lease-*.json'):
                        try:
                            if json.loads(lease.read_text()).get('expires', 0) > now: protected = True
                            else: lease.unlink()
                        except (OSError, ValueError, TypeError): protected = True
                    size = sum(item.get('bytes', 0) for item in value.get('files', []))
                    # Legacy captures kept their own whole package copies.
                    for package in value.get('packages', []):
                        if str(package.get('sourcePath', '')).startswith(str(path.parent) + os.sep):
                            size += sum(item.get('bytes', 0) for item in package.get('files', []))
                    entries.append((path.stat().st_mtime, path, value, size, protected))
                except (OSError, ValueError, TypeError): continue
            entries.sort(key=lambda value: value[0])
            total = sum(entry[3] for entry in entries)
            count = len(entries)
            retained = []
            # Always preserve the newest usable capture, even when it exceeds the soft limit.
            newest = entries[-1][1] if entries else None
            for modified, path, value, size, protected in entries:
                if not protected and path != newest and (count > self.max_captures or total > self.capture_bytes):
                    shutil.rmtree(path.parent)
                    deleted['captures'].append(path.parent.name)
                    total -= size; count -= 1
                else: retained.append(value)
            references = {str(Path(package.get('sourcePath', '')).absolute())
                          for value in retained for package in value.get('packages', []) if not package.get('builtIn')}
            snapshots = []
            for marker in self.packages.glob('*/.wardrobe-package-snapshot.json'):
                try:
                    value = json.loads(marker.read_text())
                    key = value.get('key', '')
                    expected = value.get('name', '') + '-' + key
                    if (value.get('kind') != 'wardrobe-package-snapshot-v1' or not HEX64.fullmatch(key) or
                            marker.parent.name != expected or marker.parent.is_symlink() or marker.is_symlink()): continue
                    snapshots.append((value.get('lastUsed', 0), marker.parent, value.get('bytes', 0)))
                except (OSError, ValueError, TypeError): continue
            total = sum(value[2] for value in snapshots)
            for used, path, size in sorted(snapshots):
                if total <= self.package_bytes: break
                if str(path) in references or now - used < self.grace_seconds: continue
                shutil.rmtree(path); total -= size; deleted['packages'].append(path.name)
        return deleted
