"""External Wardrobe host: local library and cache survive Unity being closed.

python3 wardrobe_desktop.py --library ~/AvatarWardrobeLibrary --project /path/to/project
"""
import argparse
import base64
import time
import hashlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import sqlite3
import socket
from pathlib import Path
import tempfile
import threading
import urllib.error
import urllib.parse
import urllib.request
import uuid
from wardrobe_library import Library, MAX_BYTES
from wardrobe_shadow_host import ShadowSnapshotService
from wardrobe_operations import OperationQueue, OperationDriver, MAX_COMMAND_BYTES

WEB = Path(__file__).resolve().parents[1] / 'Web'
STATIC = {'/': 'wardrobe.html', '/wardrobe.html': 'wardrobe.html', '/lang.json': 'lang.json',
          '/wardrobe.css': 'wardrobe.css', '/wardrobe.js': 'wardrobe.js', '/runtime.js': 'runtime.js',
          '/upload.js': 'upload.js', '/previews.js': 'previews.js', '/library.js': 'library.js', '/reporting.js': 'reporting.js',
          '/scene-editor.js': 'scene-editor.js', '/operations.js': 'operations.js',
          '/snapshots.js': 'snapshots.js', '/photo-history.js': 'photo-history.js', '/photo-history.css': 'photo-history.css', '/drag-drop.js': 'drag-drop.js', '/menu-organizer.js': 'menu-organizer.js', '/preset-appearance.js': 'preset-appearance.js', '/appearance-editor.js': 'appearance-editor.js', '/appearance-editor.css': 'appearance-editor.css'}
READ_CACHE = {'/api/state', '/api/families', '/api/family', '/api/installed', '/api/thumb', '/api/snapshot'}
LEASE_RENEW_SECONDS = 30
BUILD_ROUTES = {'/api/scene_upload_review', '/api/scene_upload', '/api/upload', '/api/batch_upload_one',
                '/api/batch_upload_scene', '/api/batch_upload_presets', '/api/batch_dryrun'}


class UnityOperationBridge:
    """Fixed loopback transport; operation reads never depend on the main thread."""
    def __init__(self, base_url, project, timeout=8):
        self.base_url, self.project, self.timeout = base_url, project, timeout
        class NoRedirect(urllib.request.HTTPRedirectHandler):
            def redirect_request(self, req, fp, code, msg, headers, newurl):
                raise urllib.error.HTTPError(req.full_url, code, 'Bridge redirects are not allowed.', headers, fp)
        self.opener = urllib.request.build_opener(NoRedirect, urllib.request.ProxyHandler({}))

    def _request(self, path, payload=None, command=None, post=False):
        headers = {'X-Wardrobe-Project': urllib.parse.quote(self.project, safe='')}
        if post:
            headers['X-Wardrobe-Request'] = '1'
        if command:
            headers.update({'X-Wardrobe-Session': command['target']['session'],
                            'X-Wardrobe-Avatar': str(command['target']['avatarInstanceId'])})
        data = json.dumps(payload, allow_nan=False).encode() if payload is not None else None
        if data is not None:
            headers['Content-Type'] = 'application/json'
        request = urllib.request.Request(self.base_url + path, data=data, headers=headers, method='POST' if post else 'GET')
        with self.opener.open(request, timeout=self.timeout) as response:
            raw = response.read(2 * 1024 * 1024 + 1)
            if len(raw) > 2 * 1024 * 1024:
                raise ValueError('Unity operation response is too large.')
            value = json.loads(raw)
            if not isinstance(value, dict):
                raise ValueError('Invalid Unity operation response.')
            return value

    def context(self):
        value = self._request('/api/operation_context')
        if value.get('projectId') != self.project:
            raise ValueError('The Unity operation bridge belongs to another project.')
        return value

    def submit(self, command, execute_revision=None):
        return self._request('/api/operation_accept', {'command': command, 'executeRevision': execute_revision or ''}, command, post=True)

    def poll(self, identifier):
        try:
            return self._request('/api/operation_result?id=' + urllib.parse.quote(identifier, safe=''))
        except urllib.error.HTTPError as error:
            if error.code in (404, 410):
                return {'id': identifier, 'state': 'needs-review', 'error': 'Unity no longer has this receipt. Review the scene before continuing.'}
            raise

    def cancel(self, identifier):
        return self._request('/api/operation_cancel?id=' + urllib.parse.quote(identifier, safe=''), post=True)


class Host(ThreadingHTTPServer):
    daemon_threads = True
    def __init__(self, port, library, project, bridge, unity=""):
        parsed = urllib.parse.urlparse(bridge)
        if (parsed.scheme != 'http' or parsed.hostname != 'localhost' or parsed.username or parsed.password or
                parsed.path not in ('', '/') or parsed.query or parsed.fragment or not (8909 <= (parsed.port or 0) <= 8929)):
            raise ValueError('The Unity bridge must use http://localhost:8909 through :8929.')
        super().__init__(('127.0.0.1', port), Handler)
        self.library = library
        self.project = str(Path(project).resolve()) if project else ''
        self.bridge = bridge.rstrip('/')
        self.cache = library.root / 'preview-cache'
        self.cache.mkdir(exist_ok=True)
        self.plans = {}
        self.plan_lock = threading.Lock()
        self.import_lock = threading.Lock()
        self.cache_lock = threading.Lock()
        self.session = uuid.uuid4().hex
        self.bridge_project = None
        self.unity = str(unity)
        self.snapshots = None
        self.snapshot_lock = threading.Lock()
        self.operations = OperationQueue(library.root / 'operations.sqlite3', self.project)
        self._operation_queue = self.operations
        self.operation_bridge = UnityOperationBridge(self.bridge, self.project)
        self.operation_driver = OperationDriver(self.operations, self.operation_bridge)
        self._operation_stop = threading.Event()
        self._operation_thread = threading.Thread(target=self._drive_operations, name='wardrobe-operations', daemon=True)
        self._operation_thread.start()

    def _drive_operations(self):
        try:
            while not self._operation_stop.is_set():
                try:
                    self.operation_driver.step()
                except (OSError, ValueError, RuntimeError, sqlite3.Error):
                    # Persisted intents survive a transient local/bridge failure.
                    pass
                self._operation_stop.wait(0.25)
        finally:
            self._operation_queue.close()

    def server_close(self):
        # TCPServer calls this override when binding fails, before queue fields exist.
        try:
            stop = getattr(self, '_operation_stop', None)
            thread = getattr(self, '_operation_thread', None)
            queue = getattr(self, '_operation_queue', None)
            if stop is not None:
                stop.set()
            if thread is not None and thread.ident is not None:
                thread.join(timeout=2 * self.operation_bridge.timeout + 2)
            if queue is not None and (thread is None or not thread.is_alive()):
                queue.close()
        finally:
            super().server_close()


    def shadow_service(self):
        if not self.project:
            raise ValueError('Choose a source project before requesting avatar photographs.')
        with self.snapshot_lock:
            if self.snapshots is None:
                project_key = hashlib.sha256(self.project.encode()).hexdigest()[:24]
                self.snapshots = ShadowSnapshotService(self.library.root / 'snapshots' / project_key, self.unity, self.project)
            return self.snapshots

    def close_shadow(self):
        with self.snapshot_lock:
            if self.snapshots is not None:
                self.snapshots.close()


class Handler(BaseHTTPRequestHandler):
    def log_message(self, fmt, *args):
        pass  # Do not log local archive names, paths or user query text.

    def send(self, status, value, mime='application/json', headers=None):
        data = json.dumps(value).encode() if mime == 'application/json' else value
        self.send_response(status)
        self.send_header('Content-Type', mime)
        self.send_header('Content-Length', str(len(data)))
        self.send_header('Cache-Control', 'no-store')
        self.send_header('X-Content-Type-Options', 'nosniff')
        self.send_header('X-Frame-Options', 'DENY')
        self.send_header('Referrer-Policy', 'no-referrer')
        for name, value in (headers or {}).items(): self.send_header(name, value)
        self.end_headers()
        self.wfile.write(data)

    def allowed(self):
        port = self.server.server_port
        origin = self.headers.get('Origin')
        hosts = {f'localhost:{port}', f'127.0.0.1:{port}'}
        if self.headers.get('Host') not in hosts or (origin and origin not in {f'http://{h}' for h in hosts}):
            self.send(403, {'message': 'Use the local Wardrobe window.'})
            return False
        if self.command == 'POST' and self.headers.get('X-Wardrobe-Request') != '1':
            self.send(403, {'message': 'Use the Wardrobe controls for this action.'})
            return False
        return True

    def do_GET(self):
        self.handle_request()

    def do_POST(self):
        self.handle_request()

    def handle_request(self):
        if not self.allowed():
            return
        url = urllib.parse.urlsplit(self.path)
        query = {k: v[-1] for k, v in urllib.parse.parse_qs(url.query).items()}
        route = url.path
        try:
            if route in STATIC and self.command == 'GET':
                file = WEB / STATIC[route]
                mime = {'.html': 'text/html; charset=utf-8', '.js': 'application/javascript', '.css': 'text/css', '.json': 'application/json'}.get(file.suffix)
                if mime == 'application/json':
                    return self.send(200, json.loads(file.read_text()))
                return self.send(200, file.read_bytes(), mime)
            if route.startswith('/assets/') and self.command == 'GET':
                name = route[len('/assets/'):]
                if name in ('header-portrait.webp', 'rail-landscape.webp', 'avatar-placeholder.webp'):
                    return self.send(200, (WEB / 'assets' / name).read_bytes(), 'image/webp')
            if route == '/api/desktop_identity' and self.command == 'GET':
                return self.send(200, {'ok': 1, 'protocol': 3, 'project': self.server.project, 'session': self.server.session, 'unity': self.server.unity})
            if route in ('/api/operations', '/api/operations/cancel'):
                return self.operation_route(route, query)
            if route == '/api/library' and self.command == 'GET':
                return self.send(200, {'ok': 1, 'project': self.server.project, 'items': self.server.library.list()})
            if route == '/api/library/add' and self.command == 'POST':
                length = int(self.headers.get('Content-Length', '0'))
                if not 0 < length <= MAX_BYTES:
                    raise ValueError('Choose a ZIP or unitypackage file smaller than 4 GiB.')
                filename = Path(query.get('filename', 'download.zip')).name
                if not filename.lower().endswith(('.zip', '.unitypackage')):
                    raise ValueError('Choose a ZIP or unitypackage download.')
                with tempfile.TemporaryDirectory(prefix='wardrobe-download-') as folder:
                    source = Path(folder) / filename
                    with source.open('wb') as out:
                        remaining = length
                        while remaining:
                            block = self.rfile.read(min(1024 * 1024, remaining))
                            if not block:
                                raise ValueError('The upload ended early. Add the file again.')
                            out.write(block)
                            remaining -= len(block)
                    result = self.server.library.add(source, query.get('creator', ''), query.get('product', ''), query.get('source', ''))
                return self.send(200, dict(result, ok=1))
            if route == '/api/library/metadata' and self.command == 'POST':
                return self.send(200, self.server.library.update_metadata(query.get('hash', ''), query.get('creator', ''), query.get('product', ''), query.get('source', '')))
            if route == '/api/library/import_review' and self.command == 'POST':
                if not self.server.project:
                    raise ValueError('Start the desktop host with --project to review a project import.')
                plan = self.server.library.import_plan(query.get('hash', ''), self.server.project)
                token = uuid.uuid4().hex
                with self.server.plan_lock:
                    if len(self.server.plans) >= 20:
                        self.server.plans.clear()
                    self.server.plans[token] = plan
                return self.send(200, dict(plan, ok=1, token=token))
            if route == '/api/library/import_apply' and self.command == 'POST':
                with self.server.plan_lock:
                    plan = self.server.plans.pop(query.get('token', ''), None)
                if not plan:
                    raise ValueError('Import review expired. Review again.')
                if plan['codeFiles'] and query.get('allowCode') != '1':
                    raise ValueError('This product contains executable Unity code. Review and explicitly accept it before importing.')
                with self.server.import_lock:
                    return self.send(200, self.apply_import(plan))
            if route == '/api/library/impact' and self.command == 'GET':
                return self.send(200, dict(self.server.library.update_impact(query.get('old', ''), query.get('new', '')), ok=1))
            if route.startswith('/api/shadow/'):
                return self.shadow_route(route, query)
            if route.startswith('/api/'):
                return self.proxy(route)
            self.send(404, {'message': 'Not found.'})
        except (ValueError, OSError, KeyError, TypeError, RuntimeError, OverflowError) as error:
            self.send(400, {'ok': 0, 'message': str(error)})

    def operation_route(self, route, query):
        if route == '/api/operations' and self.command == 'GET':
            return self.send(200, self.server.operations.get(query['id']) if 'id' in query else {'items': self.server.operations.list()})
        if self.command != 'POST':
            return self.send(405, {'ok': 0, 'message': 'Use the supported operation request method.'})
        if not self.server.project:
            raise ValueError('Choose a project before submitting an operation.')
        if self.headers.get('Transfer-Encoding'):
            raise ValueError('Operation controls require a bounded JSON body.')
        length = int(self.headers.get('Content-Length', '0'))
        if route == '/api/operations/cancel':
            if length != 0:
                raise ValueError('Cancel requires only an operation id.')
            return self.send(200, self.server.operations.cancel(query.get('id', '')))
        if not 0 < length <= MAX_COMMAND_BYTES:
            raise ValueError('Operation controls require a JSON body no larger than 64 KiB.')
        raw = self.rfile.read(length)
        if len(raw) != length:
            raise ValueError('The operation request ended early.')
        def unique_object(pairs):
            value = {}
            for key, item in pairs:
                if key in value:
                    raise ValueError('Duplicate command fields are not allowed.')
                value[key] = item
            return value
        command = json.loads(raw, object_pairs_hook=unique_object)
        try:
            receipt = self.server.operations.submit(command)
        except OverflowError as error:
            return self.send(429, {'ok': 0, 'message': str(error)})
        # SQLite commit has completed. Unity work happens only on the driver thread.
        return self.send(202, receipt)

    def shadow_route(self, route, query):
        methods = {'/api/shadow/submit': 'POST', '/api/shadow/result': 'GET', '/api/shadow/image': 'GET',
                   '/api/shadow/pin': 'POST', '/api/shadow/cancel': 'POST', '/api/shadow/history': 'GET',
                   '/api/shadow/photo': 'GET', '/api/shadow/export': 'GET'}
        if route not in methods:
            return self.send(404, {'ok': 0, 'message': 'Unknown shadow snapshot route.'})
        if self.command != methods[route]:
            return self.send(405, {'ok': 0, 'message': 'Use the supported snapshot request method.'})
        payload = {}
        if self.command == 'POST':
            if self.headers.get('Transfer-Encoding'):
                raise ValueError('Snapshot controls require a bounded JSON body.')
            length = int(self.headers.get('Content-Length', '0'))
            if not 0 < length <= 16384:
                raise ValueError('Snapshot controls require a JSON body smaller than 16 KiB.')
            raw = self.rfile.read(length)
            if len(raw) != length:
                raise ValueError('The snapshot request ended early.')
            payload = json.loads(raw)
            if not isinstance(payload, dict):
                raise ValueError('Invalid snapshot request.')
        service = self.server.shadow_service()
        if route == '/api/shadow/submit':
            if set(payload) - {'operationId', 'view', 'before', 'zoom'}:
                raise ValueError('Submit a completed capture operation ID, not a filesystem path.')
            if not self.server.unity:
                raise ValueError('Open Wardrobe from Unity, or start its desktop host with --unity pointing to Unity 2022.3.22f1.')
            operation_id = payload.get('operationId')
            try: uuid.UUID(operation_id)
            except (ValueError, TypeError, AttributeError): raise ValueError('A capture operation ID is required.')
            operations = getattr(self.server, 'operations', None)
            if operations is None:
                raise ValueError('The desktop operation queue is not available yet.')
            operation = operations.get(operation_id)
            if not isinstance(operation, dict) or operation.get('state') != 'succeeded' or operation.get('type') != 'capture-source':
                raise ValueError('Wait for the capture-source operation to finish successfully.')
            target = operation.get('command', {}).get('target', {})
            source_project = target.get('projectId', operation.get('command', {}).get('projectId'))
            if source_project != self.server.project:
                raise ValueError('This capture operation belongs to another source project.')
            result = operation.get('result')
            if not isinstance(result, dict) or not isinstance(result.get('captureManifestPath'), str):
                raise ValueError('The completed capture has no shadow manifest.')
            path = Path(result['captureManifestPath']).resolve()
            capture_root = Path(self.server.project) / 'Library/AvatarWardrobe/captures'
            if (not path.is_relative_to(capture_root) or path.name != 'manifest.json' or
                    path.parent.parent != capture_root or len(path.parent.name) != 32):
                raise ValueError('The capture receipt does not point to an owned source snapshot.')
            try:
                if uuid.UUID(path.parent.name).hex != path.parent.name: raise ValueError()
            except ValueError: raise ValueError('The capture receipt has an invalid snapshot identity.')
            before = payload.get('before', False)
            if not isinstance(before, bool): raise ValueError('The comparison side must be a boolean.')
            receipt = service.submit(path, payload.get('view', 'front'), before=before, zoom=payload.get('zoom', 1.0),
                                     confirmed_revision=result.get('confirmedRevision', ''), target=target, operation_id=operation_id,
                                     source_input=dict(operation.get('command', {}).get('payload', {}), scopeId=target.get('scopeId', 'common')))
            return self.send(202, dict(self.snapshot_public(receipt), ok=1, operationId=operation_id))
        if route == '/api/shadow/result':
            result = service.get(query.get('id', ''))
            if result is None: return self.send(404, {'ok': 0, 'message': 'Unknown snapshot request.'})
            return self.send(200, dict(self.snapshot_public(result), ok=1))
        if route == '/api/shadow/history':
            if query.get('pinned', '1') not in ('0', '1'): raise ValueError('Choose kept photos or all recent photos.')
            return self.send(200, service.history(pinned=query.get('pinned', '1') == '1', limit=int(query.get('limit', '50')),
                             offset=int(query.get('offset', '0')), avatar_id=query.get('avatarId'), scene_guid=query.get('sceneGuid'), scope_id=query.get('scopeId')))
        if route == '/api/shadow/photo':
            result = service.photo(query.get('key', ''))
            if result is None: return self.send(404, {'ok': 0, 'message': 'This photograph is no longer available.'})
            return self.send(200, dict(result, ok=1))
        if route in ('/api/shadow/image', '/api/shadow/export'):
            key = query.get('key', '')
            if len(key) != 64 or any(character not in '0123456789abcdef' for character in key):
                raise ValueError('Invalid snapshot identity.')
            data = service.image_bytes(key)
            if data is None: return self.send(404, {'ok': 0, 'message': 'This photograph is no longer available or its bytes changed.'})
            headers = {'Content-Disposition': 'attachment; filename="wardrobe-photo-' + key[:12] + '.png"'} if route.endswith('/export') else None
            return self.send(200, data, 'image/png', headers=headers)
        if route == '/api/shadow/pin':
            if set(payload) - {'key', 'snapshotKey', 'pinned'} or not isinstance(payload.get('pinned', True), bool):
                raise ValueError('Invalid pin request.')
            if payload.get('key') and payload.get('snapshotKey') and payload['key'] != payload['snapshotKey']:
                raise ValueError('The pin request contains conflicting snapshot identities.')
            result = service.pin(payload.get('key', payload.get('snapshotKey', '')), payload.get('pinned', True))
            return self.send(200, dict(self.snapshot_public(result), ok=1))
        if set(payload) != {'id'}: raise ValueError('A snapshot request ID is required.')
        result = service.cancel(payload['id'])
        if result is None: return self.send(404, {'ok': 0, 'message': 'Unknown snapshot request.'})
        return self.send(200, dict(self.snapshot_public(result), ok=1))

    @staticmethod
    def snapshot_public(value):
        value = dict(value)
        value.pop('captureManifestPath', None)
        if value.pop('imagePath', None):
            value['imageUrl'] = '/api/shadow/image?key=' + value['snapshotKey']
        return value

    def apply_import(self, plan):
        lease = None
        headers = {'X-Wardrobe-Request': '1', 'X-Wardrobe-Project': urllib.parse.quote(self.server.project, safe='')}
        try:
            request = urllib.request.Request(self.server.bridge + '/api/library_import_begin', method='POST', headers=headers)
            with self._bridge_request(request, timeout=8) as response:
                raw = response.read(1024 * 1024 + 1)
                if len(raw) > 1024 * 1024: raise ValueError('Unity lease response is too large.')
                lease = json.loads(raw)
            if not isinstance(lease, dict) or lease.get('ok') != 1 or not isinstance(lease.get('id'), str) or not lease['id']:
                raise ValueError(lease.get('message', 'Unity cannot import right now.') if isinstance(lease, dict) else 'Unity cannot import right now.')
        except urllib.error.HTTPError as error:
            raise ValueError('Open the selected project in Unity and finish its active operation before importing.') from error
        except (urllib.error.URLError, TimeoutError, ConnectionError) as error:
            # A closed Unity project is a supported offline import target. If
            # Unity is open, however, a failed lease means its refresh lock is
            # unknown and copying would make a retry unsafe.
            if (Path(self.server.project) / 'Temp/UnityLockfile').exists():
                raise ValueError('Unity is open but its Wardrobe bridge is unavailable. Open Tools > Avatar Wardrobe before importing.') from error
        result = None
        stopped, lost = threading.Event(), threading.Event()
        heartbeat = None
        def check_lease():
            if lost.is_set():
                raise ValueError('Unity import protection was lost. New files were rolled back; wait for Unity to finish refreshing, then review the project before trying again.')
        def renew():
            while not stopped.wait(LEASE_RENEW_SECONDS):
                try:
                    request = urllib.request.Request(self.server.bridge + '/api/library_import_renew?token=' + urllib.parse.quote(lease['id'], safe=''), method='POST', headers=headers)
                    with self._bridge_request(request, timeout=8) as response:
                        raw = response.read(1024 * 1024 + 1)
                        if len(raw) > 1024 * 1024:
                            raise ValueError('Unity renewal response is too large.')
                        renewed = json.loads(raw)
                        if not isinstance(renewed, dict) or renewed.get('ok') != 1:
                            raise ValueError('Unity import protection expired.')
                except (urllib.error.URLError, TimeoutError, ConnectionError, ValueError):
                    lost.set()
                    return
        if lease:
            heartbeat = threading.Thread(target=renew, daemon=True)
            heartbeat.start()
        try:
            if lease:
                result = self.server.library.apply_import(plan['hash'], self.server.project, plan, cancel_check=check_lease)
            else:
                result = self.server.library.apply_import(plan['hash'], self.server.project, plan)
        finally:
            stopped.set()
            if heartbeat:
                heartbeat.join(timeout=10)
            if lease:
                request = urllib.request.Request(self.server.bridge + '/api/library_import_end?token=' + urllib.parse.quote(lease['id'], safe=''), method='POST', headers=headers)
                try:
                    with self._bridge_request(request, timeout=15) as response:
                        raw = response.read(1024 * 1024 + 1)
                        if len(raw) > 1024 * 1024: raise ValueError('Unity lease response is too large.')
                        ended = json.loads(raw)
                        if not isinstance(ended, dict) or ended.get('ok') != 1:
                            raise ValueError(ended.get('message', 'Import lease expired.') if isinstance(ended, dict) else 'Invalid Unity import confirmation.')
                except (urllib.error.URLError, TimeoutError, ConnectionError, ValueError):
                    # Files may already be present: do not report a safe-to-retry failure.
                    self.send_import_attention = True
        if result is not None and getattr(self, 'send_import_attention', False):
            result['message'] = 'Files were copied, but Unity did not confirm its refresh. Wait for import to finish or reopen Unity, then refresh Wardrobe. Do not repeat the import.'
        return result

    def _bridge_request(self, request, timeout):
        """Open a bridge request without following redirects."""
        class NoRedirect(urllib.request.HTTPRedirectHandler):
            def redirect_request(self, req, fp, code, msg, headers, newurl):
                raise urllib.error.HTTPError(req.full_url, code, 'Bridge redirects are not allowed.', headers, fp)
        opener = urllib.request.build_opener(NoRedirect, urllib.request.ProxyHandler({}))
        return opener.open(request, timeout=timeout)

    def _verify_bridge_project(self):
        if not self.server.project:
            return
        # Verify each request: a bridge port can be reused by another project.
        headers = {'X-Wardrobe-Project': urllib.parse.quote(self.server.project, safe='')}
        request = urllib.request.Request(self.server.bridge + '/api/state', method='GET', headers=headers)
        with self._bridge_request(request, 8) as response:
            raw = response.read(4 * 1024 * 1024 + 1)
            if len(raw) > 4 * 1024 * 1024: raise ValueError('Unity identity response is too large.')
            state = json.loads(raw)
            if not isinstance(state, dict): raise ValueError('Unity identity response is invalid.')
        reported = state.get('projectPath', '')
        if not isinstance(reported, str):
            raise ValueError('Unity identity response is invalid.')
        self.server.bridge_project = str(Path(reported).resolve()) if reported else None
        if self.server.bridge_project != self.server.project:
            raise ValueError('The Unity bridge belongs to another project. Open the project selected for this library host.')

    def proxy(self, route):
        if (route in BUILD_ROUTES and (self.command == 'POST' or route == '/api/scene_upload_review') and
                self.server.operations.has_pending_mutations()):
            return self.send(409, {'ok': 0, 'message': 'Wait for pending wardrobe changes to finish or cancel them before reviewing, building or uploading this project.'})
        if not self.server.project:
            if route == '/api/state' and self.command == 'GET':
                return self.send(200, {'desktop': True, 'bridgeOnline': False, 'libraryProject': '', 'session': 'offline',
                    'avatarInstanceId': 0, 'sceneTargets': [], 'sdkUploadReady': False, 'workflowPresets': [],
                    'baseAvatars': [], 'families': 0, 'outfits': 0, 'avatars': 0, 'epoch': '', 'indexing': 0})
            return self.send(409, {'ok': 0, 'message': 'Start the desktop host with --project before connecting to Unity. Your library remains available.'})
        # The fixed bridge host prevents arbitrary URL forwarding. Mutations retain
        # the browser's reviewed Unity session and avatar identity, never a new one.
        if self.headers.get('Content-Length', '0') != '0':
            raise ValueError('This Unity bridge endpoint does not accept file bodies.')
        headers = {'X-Wardrobe-Project': urllib.parse.quote(self.server.project, safe='')}
        if self.command == 'POST':
            for name in ('X-Wardrobe-Request', 'X-Wardrobe-Session', 'X-Wardrobe-Avatar'):
                if self.headers.get(name):
                    headers[name] = self.headers[name]
        request = urllib.request.Request(self.server.bridge + self.path, method=self.command, headers=headers)
        key = hashlib.sha256((self.server.project + '|' + self.path).encode()).hexdigest()
        cached = self.server.cache / (key + '.json')
        try:
            if route == '/api/snapshot':
                self.server.operations.reconcile_session(self.server.operation_bridge.context())
            elif route not in ('/api/state', '/api/operation_context'):
                self._verify_bridge_project()
            with self._bridge_request(request, 8 if self.command == 'GET' else 50) as response:
                data = response.read(32 * 1024 * 1024 + 1)
                if len(data) > 32 * 1024 * 1024:
                    raise ValueError('Unity response exceeds the preview cache limit.')
                mime = response.headers.get_content_type()
                status = response.status
            if route == '/api/operation_context':
                context = json.loads(data)
                if not isinstance(context, dict) or context.get('projectId') != self.server.project:
                    return self.send(409, {'ok': 0, 'message': 'The Unity bridge belongs to another project.'})
                self.server.operations.reconcile_session(context)
            if route == '/api/state':
                state = json.loads(data)
                if not isinstance(state, dict) or not isinstance(state.get('projectPath', ''), str):
                    raise ValueError('Unity identity response is invalid.')
                reported = state.get('projectPath', '')
                self.server.bridge_project = str(Path(reported).resolve()) if reported else None
                if self.server.project and self.server.bridge_project != self.server.project:
                    return self.send(409, {'ok': 0, 'message': 'The Unity bridge belongs to another project. Open the project selected for this library host.'})
            if route == '/api/installed' and self.command == 'GET' and status == 200:
                self.server.library.reconcile_usage(self.server.project, json.loads(data))
            if route in READ_CACHE and self.command == 'GET' and status == 200:
                # Cache includes project identity and exact request including version parameters.
                record = {'project': self.server.project, 'route': route, 'path': self.path,
                          'mime': mime, 'body': base64.b64encode(data).decode(), 'saved': time.time()}
                temp = cached.with_name(cached.name + '.' + uuid.uuid4().hex + '.tmp')
                with self.server.cache_lock:
                    temp.write_text(json.dumps(record))
                    temp.replace(cached)
                    entries = sorted(self.server.cache.glob('*.json'), key=lambda file: file.stat().st_mtime, reverse=True)
                    total = 0
                    for index, file in enumerate(entries):
                        total += file.stat().st_size
                        if index >= 4096 or total > 512 * 1024 * 1024:
                            file.unlink(missing_ok=True)
            if route == '/api/state':
                value = json.loads(data)
                value.update(desktop=True, bridgeOnline=True, libraryProject=self.server.project)
                return self.send(status, value)
            return self.send(status, json.loads(data) if mime == 'application/json' else data, mime)
        except urllib.error.HTTPError as error:
            if 300 <= error.code < 400:
                return self.send(502, {'ok': 0, 'message': 'The Unity bridge returned a redirect, which is not allowed.'})
            return self.send(error.code, {'ok': 0, 'message': error.read(8000).decode(errors='replace')[:2000]})
        except (urllib.error.URLError, TimeoutError, socket.timeout, ConnectionError):
            if self.command != 'GET':
                return self.send(503, {'ok': 0, 'message': 'Unity is unavailable. Open this project and Tools > Avatar Wardrobe, then review the edit again.'})
            value = None
            if cached.exists() and route in READ_CACHE:
                try:
                    with self.server.cache_lock:
                        if cached.stat().st_size > 45 * 1024 * 1024:
                            raise ValueError('Cached response is too large.')
                        record = json.loads(cached.read_text())
                    if (not isinstance(record, dict) or record.get('project') != self.server.project or record.get('route') != route or
                            record.get('path') != self.path):
                        record = None
                    data = base64.b64decode(record['body'], validate=True) if record is not None else None
                    if data is not None:
                        if len(data) > 32 * 1024 * 1024 or not isinstance(record.get('mime'), str):
                            raise ValueError('Cached response is invalid.')
                        if record['mime'] == 'application/json':
                            data = json.loads(data)
                        if route == '/api/state' and not isinstance(data, dict):
                            raise ValueError('Cached Unity state is invalid.')
                except (OSError, ValueError, KeyError, TypeError):
                    record, data = None, None
                if route != '/api/state':
                    if data is not None:
                        return self.send(200, data, record['mime'])
                elif data is not None:
                    value = data
            if route == '/api/state':
                value = value or {'workflowPresets': [], 'baseAvatars': [], 'families': 0, 'outfits': 0, 'avatars': 0, 'epoch': '', 'indexing': 0}
                value.update(desktop=True, bridgeOnline=False, libraryProject=self.server.project, session='offline', avatarInstanceId=0, sceneTargets=[], sdkUploadReady=False)
                return self.send(200, value)
            return self.send(503, {'ok': 0, 'message': 'No cached result yet. Your Library remains available while Unity is closed.'})


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--library', default=str(Path.home() / 'AvatarWardrobeLibrary'))
    parser.add_argument('--project', default='')
    parser.add_argument('--bridge', default='http://localhost:8909')
    parser.add_argument('--port', type=int, default=8930)
    parser.add_argument('--unity', default='', help='Unity 2022.3.22f1 executable for the isolated photograph worker')
    parser.add_argument('--add', type=Path, help='Add a local purchased archive, then exit')
    args = parser.parse_args()
    library = Library(args.library)
    if args.add:
        print(json.dumps(library.add(args.add)))
        return
    server = Host(args.port, library, args.project, args.bridge, unity=args.unity)
    print(f'Avatar Wardrobe: http://localhost:{server.server_port}', flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.close_shadow()
        server.server_close()


if __name__ == '__main__':
    main()
