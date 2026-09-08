"""Asynchronous desktop facade for immutable captures and the owned Unity worker.

The desktop operation queue publishes a completed capture's manifestPath. submit()
accepts it immediately, preserves the last completed image, and serializes worker
work. Captured inputs changing triggers a supervised stop/restage/restart after the
current render finishes; callers do not need to ask the user to restart Unity.
"""
from __future__ import annotations

from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
from pathlib import Path
import shutil
import threading
import time
import uuid

from wardrobe_shadow import ShadowWorker, atomic_json, load_manifest, sha256


class ShadowSnapshotService:
    def __init__(self, cache, unity, project, *, timeout=180, history_bytes=256 * 1024 * 1024):
        self.cache = Path(cache).expanduser().resolve()
        self.cache.mkdir(parents=True, exist_ok=True)
        self.unity = str(Path(unity).expanduser().resolve())
        self.project = str(Path(project).resolve())
        self.timeout, self.history_bytes = timeout, history_bytes
        self.worker = ShadowWorker(self.cache / 'worker')
        self.executor = ThreadPoolExecutor(max_workers=1, thread_name_prefix='wardrobe-shadow')
        self.lock = threading.RLock()
        self.requests = {}
        self.closed = False
        for name in ('receipts', 'images'):
            (self.cache / name).mkdir(exist_ok=True)
        # A host restart does not silently replay work whose previous worker outcome is unknown.
        for path in (self.cache / 'receipts').glob('*.json'):
            try:
                receipt = json.loads(path.read_text())
                if receipt.get('state') in ('queued', 'waiting-for-worker', 'rendering', 'cancelling'):
                    receipt.update(state='needs-review', message='The desktop host restarted before this snapshot completed.')
                    atomic_json(path, receipt)
            except (OSError, ValueError):
                continue

    def submit(self, manifest_path, view='front', *, before=False, zoom=1.0, confirmed_revision='', target=None):
        path, manifest = load_manifest(manifest_path)
        if str(Path(manifest['projectPath']).resolve()) != self.project:
            raise ValueError('The capture belongs to another source project.')
        if view not in ('front', 'three-quarter', 'back') or not isinstance(zoom, (int, float)) or not 0.5 <= zoom <= 2.5:
            raise ValueError('Choose a supported photograph view and zoom between 0.5 and 2.5.')
        specification = {'project': self.project, 'avatarId': manifest.get('avatarId'), 'sourceRevision': manifest['sourceRevision'],
                         'recipeRevision': manifest['recipeRevision'], 'environmentRevision': manifest['environmentRevision'],
                         'renderSpecification': manifest.get('renderSpecification', ''), 'view': view, 'before': bool(before), 'zoom': float(zoom)}
        key = hashlib.sha256(json.dumps(specification, sort_keys=True, separators=(',', ':')).encode()).hexdigest()
        with self.lock:
            if self.closed:
                raise RuntimeError('The snapshot service is closed.')
            identifier = uuid.uuid4().hex
            receipt = {'id': identifier, 'state': 'queued', 'snapshotKey': key, 'captureId': manifest['captureId'],
                       'created': time.time(), 'confirmedRevision': confirmed_revision,
                       'target': dict(target or {'projectId': self.project, 'avatarInstanceId': manifest.get('avatarId')}), **specification}
            cancellation = threading.Event()
            self.requests[identifier] = {'receipt': receipt, 'cancel': cancellation}
            self._write(receipt)
            future = self.executor.submit(self._run, identifier, path, manifest)
            self.requests[identifier]['future'] = future
            return dict(receipt)

    def get(self, identifier):
        if not isinstance(identifier, str) or len(identifier) != 32 or uuid.UUID(identifier).hex != identifier:
            raise ValueError('Invalid snapshot request identity.')
        with self.lock:
            if identifier in self.requests:
                return dict(self.requests[identifier]['receipt'])
        path = self.cache / 'receipts' / (identifier + '.json')
        return json.loads(path.read_text()) if path.exists() else None

    def cancel(self, identifier):
        with self.lock:
            request = self.requests.get(identifier)
            if request is None:
                return self.get(identifier)
            if request['receipt']['state'] in ('succeeded', 'failed', 'cancelled'):
                return dict(request['receipt'])
            request['cancel'].set()
            queued = request['future'].cancel()
            self._set(identifier, state='cancelled' if queued else 'cancelling',
                      message='' if queued else 'Discarding this photograph when the current synchronous worker step finishes.')
            return dict(request['receipt'])

    def pin(self, snapshot_key, pinned=True):
        if len(snapshot_key) != 64 or any(character not in '0123456789abcdef' for character in snapshot_key):
            raise ValueError('Invalid snapshot identity.')
        with self.lock:
            path = self.cache / 'images' / (snapshot_key + '.json')
            value = json.loads(path.read_text())
            value['pinned'] = bool(pinned)
            atomic_json(path, value)
            return value

    def close(self):
        with self.lock:
            if self.closed: return
            self.closed = True
            for identifier in tuple(self.requests):
                self.cancel(identifier)
        # Queue the stop behind the running capture; never switch inputs underneath a render.
        self.executor.submit(self._stop_worker)
        self.executor.shutdown(wait=False)

    def _write(self, receipt):
        atomic_json(self.cache / 'receipts' / (receipt['id'] + '.json'), receipt)

    def _set(self, identifier, **changes):
        with self.lock:
            request = self.requests[identifier]
            receipt = request['receipt']
            if changes.get('state') == 'succeeded' and request['cancel'].is_set():
                changes = {'state': 'cancelled', 'message': ''}
            receipt.update(changes, updated=time.time())
            self._write(receipt)

    def _run(self, identifier, path, manifest):
        request = self.requests[identifier]
        receipt = request['receipt']
        cancellation = request['cancel']
        try:
            if cancellation.is_set():
                self._set(identifier, state='cancelled'); return
            cached = self._cached(receipt['snapshotKey'])
            if cached:
                self._set(identifier, state='succeeded', imagePath=cached['imagePath'], imageSha256=cached['imageSha256'],
                          preview=cached.get('preview'), cached=True)
                return
            self._set(identifier, state='waiting-for-worker', message='Preparing the private Unity snapshot worker.')
            self._ensure_capture(path, manifest)
            if cancellation.is_set():
                self._set(identifier, state='cancelled', message=''); return
            self._set(identifier, state='rendering', message='Taking a processed avatar photograph.')
            command = self.worker.render(receipt['view'], receipt['before'], receipt['zoom'])
            deadline = time.monotonic() + self.timeout
            while time.monotonic() < deadline:
                result = self.worker.result(command['id'])
                if result.get('status') != 'pending': break
                if self.worker.status().get('state') in ('failed', 'stopped'):
                    raise RuntimeError('The snapshot worker stopped before finishing the photograph.')
                time.sleep(0.1)
            else: raise TimeoutError('The shadow editor is still rendering. The last completed photograph remains available.')
            if cancellation.is_set():
                self._set(identifier, state='cancelled', message=''); return
            if result.get('status') != 'succeeded':
                raise RuntimeError(result.get('message') or 'The processed photograph failed.')
            for field in ('captureId', 'sourceRevision', 'recipeRevision', 'environmentRevision'):
                if result.get(field) != manifest[field]:
                    raise RuntimeError('The returned photograph does not match its captured ' + field + '.')
            if result.get('view') != receipt['view'] or result.get('before') != receipt['before']:
                raise RuntimeError('The returned photograph has a different view specification.')
            image = Path(result['imagePath'])
            if not image.is_relative_to(self.worker.runtime) or image.stat().st_size > 64 * 1024 * 1024:
                raise ValueError('Unexpected worker image output.')
            destination = self.cache / 'images' / (receipt['snapshotKey'] + '.png')
            temporary = destination.with_name(destination.name + '.' + uuid.uuid4().hex + '.tmp')
            try:
                shutil.copyfile(image, temporary)
                if sha256(temporary) != result['imageSha256']:
                    raise ValueError('The photograph changed while being cached.')
                temporary.replace(destination)
            finally: temporary.unlink(missing_ok=True)
            metadata = {**dict(receipt), 'state': 'succeeded', 'imagePath': str(destination),
                        'imageSha256': result['imageSha256'], 'preview': result.get('preview'), 'completed': time.time(), 'pinned': False}
            atomic_json(destination.with_suffix('.json'), metadata)
            self._set(identifier, state='succeeded', message='', imagePath=str(destination), imageSha256=result['imageSha256'],
                      preview=result.get('preview'), cached=False)
            self._prune(receipt['snapshotKey'])
        except Exception as error:
            self._set(identifier, state='cancelled' if cancellation.is_set() else 'failed', message=str(error))

    def _cached(self, key):
        metadata = self.cache / 'images' / (key + '.json')
        image = metadata.with_suffix('.png')
        if not metadata.exists() or not image.exists(): return None
        try:
            value = json.loads(metadata.read_text())
            if value.get('snapshotKey') == key and value.get('imageSha256') == sha256(image):
                value['imagePath'] = str(image)
                return value
        except (OSError, ValueError): pass
        return None

    def _ensure_capture(self, path, manifest):
        current = self.worker._owner() if self.worker.project.exists() else None
        if current and (current.get('captureId') != manifest['captureId'] or current.get('environmentRevision') != manifest['environmentRevision']):
            self._stop_worker()
        self.worker.stage(path)
        self.worker.start(self.unity)
        self.worker.wait_ready(self.timeout)

    def _stop_worker(self):
        if not self.worker.project.exists(): return
        self.worker.stop()
        deadline = time.monotonic() + self.timeout
        while time.monotonic() < deadline:
            state = self.worker.status().get('state')
            lock = self.worker.project / 'Temp/UnityLockfile'
            if state in ('stopped', 'failed', 'not-started') and not lock.exists(): return
            time.sleep(0.1)
        # A child launched by this host can be supervised without signaling a recovered/reused PID.
        process = self.worker.process
        if process is not None and process.poll() is None:
            process.terminate()
            try: process.wait(timeout=10)
            except Exception:
                process.kill(); process.wait(timeout=10)
            lock = self.worker.project / 'Temp/UnityLockfile'
            lock.unlink(missing_ok=True)  # This is only the validated, private worker project.
            atomic_json(self.worker.runtime / 'state.json', {'state': 'stopped', 'message': 'The private worker exceeded its stop timeout.'})
            return
        raise TimeoutError('The prior shadow editor has not stopped. The source project has not been changed.')

    def _prune(self, keep):
        entries = []
        for path in (self.cache / 'images').glob('*.json'):
            image = path.with_suffix('.png')
            if not image.exists(): continue
            try:
                value = json.loads(path.read_text())
                entries.append((value, path, image, image.stat().st_size))
            except (OSError, ValueError): continue
        # Preserve one latest photo per avatar/view/before combination, plus explicitly pinned photos.
        latest = {}
        for value, path, image, size in entries:
            slot = (value.get('avatarId'), value.get('view'), value.get('before'))
            if slot not in latest or value.get('completed', 0) > latest[slot].get('completed', 0): latest[slot] = value
        protected = {value.get('snapshotKey') for value in latest.values()} | {keep}
        total = sum(entry[3] for entry in entries)
        for value, path, image, size in sorted(entries, key=lambda entry: entry[0].get('completed', 0)):
            if total <= self.history_bytes: break
            if value.get('pinned') or value.get('snapshotKey') in protected: continue
            image.unlink(missing_ok=True); path.unlink(missing_ok=True); total -= size
