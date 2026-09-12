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
import re
import itertools
import math
from pathlib import Path
import shutil
import threading
import time
import uuid

from wardrobe_shadow import ShadowWorker, atomic_json, load_manifest, sha256
from wardrobe_snapshot_cache import CaptureRetention


class ShadowSnapshotService:
    def __init__(self, cache, unity, project, *, timeout=180, history_bytes=256 * 1024 * 1024, max_active=16, max_completed=128):
        self.cache = Path(cache).expanduser().resolve()
        self.cache.mkdir(parents=True, exist_ok=True)
        self.unity = str(Path(unity).expanduser().resolve())
        self.project = str(Path(project).resolve())
        self.timeout, self.history_bytes = timeout, history_bytes
        self.max_active, self.max_completed = max_active, max_completed
        self.worker = ShadowWorker(self.cache / 'worker')
        self.retention = CaptureRetention(self.project)
        self.executor = ThreadPoolExecutor(max_workers=1, thread_name_prefix='wardrobe-shadow')
        self.lock = threading.RLock()
        self.requests = {}
        self.closed = False
        for name in ('receipts', 'images'):
            if (self.cache / name).is_symlink(): raise ValueError('Snapshot cache directories cannot be linked.')
            (self.cache / name).mkdir(exist_ok=True)
        # A host restart does not silently replay work whose previous worker outcome is unknown.
        for path in (self.cache / 'receipts').glob('*.json'):
            try:
                receipt = json.loads(path.read_text())
                if receipt.get('state') in ('queued', 'waiting-for-worker', 'rendering', 'cancelling'):
                    receipt.update(state='needs-review', message='The desktop host restarted before this snapshot completed.')
                    atomic_json(path, receipt)
                    try: self.retention.release(receipt.get('captureManifestPath', ''), receipt.get('id', ''))
                    except (OSError, ValueError, RuntimeError): pass
            except (OSError, ValueError):
                continue
        self._prune_receipts()

    def submit(self, manifest_path, view='front', *, before=False, zoom=1.0, confirmed_revision='', target=None, operation_id='', source_input=None):
        path, manifest = load_manifest(manifest_path)
        if str(Path(manifest['projectPath']).resolve()) != self.project:
            raise ValueError('The capture belongs to another source project.')
        if view not in ('front', 'three-quarter', 'back') or not isinstance(zoom, (int, float)) or not 0.5 <= zoom <= 2.5:
            raise ValueError('Choose a supported photograph view and zoom between 0.5 and 2.5.')
        specification = {'project': self.project, 'avatarId': manifest.get('avatarId'), 'sourceRevision': manifest['sourceRevision'],
                         'recipeRevision': manifest['recipeRevision'], 'environmentRevision': manifest['environmentRevision'],
                         'visualRevision': manifest.get('visualRevision') or manifest['sourceRevision'],
                         'renderSpecification': manifest.get('renderSpecification', ''), 'view': view, 'before': bool(before), 'zoom': float(zoom)}
        image_specification = dict(specification, sourceRevision=specification['visualRevision'])
        key = hashlib.sha256(json.dumps(image_specification, sort_keys=True, separators=(',', ':')).encode()).hexdigest()
        with self.lock:
            if self.closed:
                raise RuntimeError('The snapshot service is closed.')
            active = [r for r in self.requests.values() if r['receipt']['state'] in ('queued', 'waiting-for-worker', 'rendering', 'cancelling')]
            for request in active:
                receipt = request['receipt']
                if (not request['cancel'].is_set() and receipt['snapshotKey'] == key and
                        receipt['captureId'] == manifest['captureId'] and receipt['confirmedRevision'] == confirmed_revision and
                        receipt['target'] == dict(target or {'projectId': self.project, 'avatarInstanceId': manifest.get('avatarId')}) and
                        receipt['operationId'] == operation_id and receipt['input'] == dict(source_input or {})):
                    return dict(receipt)
            if len(active) >= self.max_active:
                raise OverflowError('Snapshot queue is full. Wait for a photograph to finish.')
            self._prune_receipts()
            identifier = uuid.uuid4().hex
            self.retention.acquire(path, identifier, max(3600, self.timeout * 3))
            receipt = {'id': identifier, 'state': 'queued', 'snapshotKey': key, 'captureId': manifest['captureId'],
                       'created': time.time(), 'captureManifestPath': str(path), 'confirmedRevision': confirmed_revision,
                       'target': dict(target or {'projectId': self.project, 'avatarInstanceId': manifest.get('avatarId')}),
                       'operationId': operation_id, 'input': dict(source_input or {}), **specification}
            cancellation = threading.Event()
            self.requests[identifier] = {'receipt': receipt, 'cancel': cancellation}
            try:
                self._write(receipt)
                future = self.executor.submit(self._run, identifier, path, manifest)
                self.requests[identifier]['future'] = future
                future.add_done_callback(lambda _: self._completed(identifier))
            except BaseException:
                self.requests.pop(identifier, None)
                try: (self.cache / 'receipts' / (identifier + '.json')).unlink(missing_ok=True)
                finally: self.retention.release(path, identifier)
                raise
            return dict(receipt)

    def _completed(self, identifier):
        with self.lock:
            self._prune_receipts()

    def _prune_receipts(self):
        terminal = []
        for path in (self.cache / 'receipts').glob('*.json'):
            if not re.fullmatch('[a-f0-9]{32}', path.stem) or path.is_symlink(): continue
            try:
                value = json.loads(path.read_text())
                if value.get('state') in ('succeeded', 'failed', 'cancelled', 'needs-review'):
                    request = self.requests.get(path.stem)
                    if request and request.get('future') and not request['future'].done(): continue
                    terminal.append((value.get('updated', value.get('created', 0)), path))
            except (OSError, ValueError, TypeError): continue
        terminal.sort(key=lambda item: item[0], reverse=True)
        for _, path in terminal[self.max_completed:]:
            path.unlink(missing_ok=True)
            self.requests.pop(path.stem, None)

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
            if queued:
                try: self.retention.release(request['receipt']['captureManifestPath'], identifier)
                except (OSError, ValueError, RuntimeError): pass  # Expiry also releases abandoned leases.
            self._set(identifier, state='cancelled' if queued else 'cancelling',
                      message='' if queued else 'Discarding this photograph when the current synchronous worker step finishes.')
            return dict(request['receipt'])

    def pin(self, snapshot_key, pinned=True):
        if not isinstance(snapshot_key, str) or not re.fullmatch('[a-f0-9]{64}', snapshot_key):
            raise ValueError('Invalid snapshot identity.')
        with self.lock:
            path = self.cache / 'images' / (snapshot_key + '.json')
            value = self._cached(snapshot_key)
            if value is None: raise ValueError('This photograph is unavailable or does not belong to this project.')
            if pinned and not value.get('pinned') and sum(bool(item.get('pinned')) for item in self._history_metadata()) >= 100:
                raise ValueError('Keep up to 100 photographs. Unkeep a photo before keeping another.')
            capture_path = value.get('captureManifestPath')
            if not capture_path and isinstance(value.get('captureId'), str) and len(value['captureId']) == 32:
                capture_path = str(self.retention.captures / value['captureId'] / 'manifest.json')
            if capture_path and Path(capture_path).exists():
                retention_key = hashlib.sha256((str(self.cache) + ':' + snapshot_key).encode()).hexdigest()
                self.retention.pin(capture_path, retention_key, bool(pinned))
                value['captureRetained'] = bool(pinned)
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
                          preview=cached.get('preview'), imageSourceRevision=cached.get('sourceRevision'), cached=True)
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
        finally:
            try:
                self.retention.release(path, identifier)
                self.retention.prune()
            except (OSError, ValueError, RuntimeError):
                pass  # Retention never changes a successful photograph into a failed request.

    def _metadata(self, key, verify=False):
        if not isinstance(key, str) or not re.fullmatch('[a-f0-9]{64}', key): return None
        directory = self.cache / 'images'
        metadata, image = directory / (key + '.json'), directory / (key + '.png')
        try:
            if (directory.is_symlink() or metadata.is_symlink() or image.is_symlink() or
                    not metadata.is_file() or not image.is_file() or metadata.stat().st_size > 2 * 1024 * 1024 or
                    image.stat().st_size > 64 * 1024 * 1024): return None
            with metadata.open('r', encoding='utf-8') as stream: raw = stream.read(2 * 1024 * 1024 + 1)
            if len(raw) > 2 * 1024 * 1024: return None
            value = json.loads(raw)
            if (not isinstance(value, dict) or value.get('snapshotKey') != key or value.get('project') != self.project or
                    value.get('state') != 'succeeded' or not isinstance(value.get('imageSha256'), str) or
                    not re.fullmatch('[a-f0-9]{64}', value['imageSha256']) or not isinstance(value.get('target'), dict) or
                    value['target'].get('projectId') != self.project): return None
            for field in ('created', 'completed'):
                stamp = value.get(field, 0)
                if not isinstance(stamp, (int, float)) or isinstance(stamp, bool) or not math.isfinite(stamp) or stamp < 0: return None
            if not isinstance(value.get('pinned', False), bool): return None
            if value.get('view') not in ('front', 'three-quarter', 'back') or not isinstance(value.get('before'), bool): return None
            for field in ('sourceRevision', 'visualRevision', 'confirmedRevision', 'recipeRevision', 'environmentRevision'):
                if not isinstance(value.get(field, ''), str) or len(value.get(field, '')) > 4096: return None
            if verify and value['imageSha256'] != sha256(image): return None
            value['imagePath'] = str(image)
            return value
        except (OSError, ValueError, TypeError): return None

    def _cached(self, key):
        return self._metadata(key, verify=True)

    def _history_metadata(self):
        # Generated metadata is bounded per file and cache retention caps new entries.
        # Unexpected extra files are left untouched rather than followed or deleted.
        result = []
        for path in itertools.islice((self.cache / 'images').glob('*.json'), 4096):
            value = self._metadata(path.stem)
            if value is not None: result.append(value)
        return sorted(result, key=lambda value: (value.get('completed', value.get('created', 0)), value['snapshotKey']), reverse=True)

    @staticmethod
    def public_photo(value, preview=False):
        fields = ('snapshotKey', 'pinned', 'completed', 'created', 'view', 'before', 'zoom', 'target',
                  'sourceRevision', 'visualRevision', 'confirmedRevision', 'recipeRevision', 'environmentRevision',
                  'operationId', 'input', 'captureRetained')
        result = {field: value[field] for field in fields if field in value}
        result['target'] = {key: item for key, item in value.get('target', {}).items()
                            if key in ('projectId', 'sceneGuid', 'avatarId', 'avatarInstanceId', 'session', 'scopeId') and
                            (isinstance(item, int) or isinstance(item, str) and len(item) <= 4096)}
        result['input'] = {key: item for key, item in value.get('input', {}).items()
                           if key in ('variantId', 'assetVersion', 'instanceId', 'scopeId', 'addCopy', 'allowUnverified', 'createToggles') and
                           (isinstance(item, bool) or isinstance(item, str) and len(item) <= 4096)} if isinstance(value.get('input'), dict) else {}
        if not isinstance(value.get('input'), dict): result.pop('input', None)
        if not isinstance(result.get('operationId'), str) or len(result.get('operationId', '')) > 36: result.pop('operationId', None)
        result['imageUrl'] = '/api/shadow/image?key=' + value['snapshotKey']
        result['exportUrl'] = '/api/shadow/export?key=' + value['snapshotKey']
        if preview: result['preview'] = value.get('preview')
        return result

    def history(self, *, pinned=True, limit=50, offset=0, avatar_id=None, scene_guid=None, scope_id=None):
        if not isinstance(limit, int) or not 1 <= limit <= 100 or not isinstance(offset, int) or not 0 <= offset <= 4096:
            raise ValueError('Choose a history page of 1–100 photographs.')
        with self.lock:
            values = [value for value in self._history_metadata() if (not pinned or value.get('pinned')) and
                      (avatar_id is None or value['target'].get('avatarId') == avatar_id) and
                      (scene_guid is None or value['target'].get('sceneGuid') == scene_guid) and
                      (scope_id is None or value['target'].get('scopeId') == scope_id)]
            selected = values[offset:offset + limit]
            next_offset = offset + len(selected)
            return {'ok': 1, 'projectId': self.project, 'items': [self.public_photo(value) for value in selected],
                    'nextOffset': next_offset if next_offset < len(values) else None, 'truncated': len(values) > offset + limit}

    def photo(self, key):
        with self.lock:
            value = self._cached(key)
            return self.public_photo(value, preview=True) if value else None

    def image_bytes(self, key):
        with self.lock:
            value = self._metadata(key)
            if value is None: return None
            # Verify the exact bytes returned; never use a path supplied in metadata.
            with Path(value['imagePath']).open('rb') as stream:
                data = stream.read(64 * 1024 * 1024 + 1)
            if len(data) > 64 * 1024 * 1024 or hashlib.sha256(data).hexdigest() != value['imageSha256']: return None
            return data

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
        with self.lock: self._prune_locked(keep)

    def _prune_locked(self, keep):
        entries = []
        for path in (self.cache / 'images').glob('*.json'):
            image = path.with_suffix('.png')
            if not image.exists(): continue
            try:
                value = self._metadata(path.stem)
                if value is not None: entries.append((value, path, image, image.stat().st_size))
            except (OSError, ValueError): continue
        # Preserve one latest photo per avatar/view/before combination, plus explicitly pinned photos.
        latest = {}
        for value, path, image, size in entries:
            slot = (value.get('avatarId'), value.get('view'), value.get('before'))
            if slot not in latest or value.get('completed', 0) > latest[slot].get('completed', 0): latest[slot] = value
        protected = {value.get('snapshotKey') for value in latest.values()} | {keep}
        total = sum(entry[3] for entry in entries)
        for value, path, image, size in sorted(entries, key=lambda entry: entry[0].get('completed', 0)):
            if total <= self.history_bytes and len(entries) <= 1000: break
            if value.get('pinned') or value.get('snapshotKey') == keep: continue
            if len(entries) <= 1000 and value.get('snapshotKey') in protected: continue
            image.unlink(missing_ok=True); path.unlink(missing_ok=True); total -= size
            entries.remove((value, path, image, size))
