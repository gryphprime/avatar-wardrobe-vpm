"""Authenticated loopback desktop host. Unity work runs outside request handlers."""
import argparse
from concurrent.futures import ThreadPoolExecutor
from contextlib import contextmanager
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import mimetypes
import os
from pathlib import Path
import secrets
import signal
import threading
import time
from urllib.parse import parse_qs, urlsplit
import webbrowser

from . import PROTOCOL_VERSION, __version__
from .adapters import load_manifest
from .core import Conflict, Store, canonical, identifier
from .library import Library
from .project_runtime import UnityWorker, inspect_project, provision_bridge

ROOT = Path(__file__).resolve().parents[1]
STATIC = {'/': 'index.html', '/index.html': 'index.html', '/app.js': 'app.js', '/styles.css': 'styles.css'}
MAX_BODY = 128 * 1024


@contextmanager
def host_lock(root):
    """One desktop process owns the database and dispatcher for this data root."""
    root = Path(root).expanduser().resolve()
    root.mkdir(parents=True, exist_ok=True)
    handle = (root / 'host.lock').open('a+b')
    try:
        if os.name == 'nt':
            import msvcrt
            handle.write(b'0'); handle.flush(); handle.seek(0)
            msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
        else:
            import fcntl
            fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except OSError:
        handle.close()
        raise RuntimeError('Atelier is already running with this data directory.')
    try:
        yield root
    finally:
        handle.close()


class Application:
    def __init__(self, root, unity=None, worker_factory=UnityWorker):
        self.store = Store(root)
        self.library = Library(self.store.root / 'library')
        self.unity, self.worker_factory = unity, worker_factory
        self.workers, self.jobs, self.errors, self.plans = {}, {}, {}, {}
        self.lock = threading.RLock()
        self.stopping = threading.Event()
        self.executor = ThreadPoolExecutor(max_workers=4, thread_name_prefix='atelier-background')
        self.drivers = {}

    def worker(self, workspace_id):
        with self.lock:
            if workspace_id not in self.workers:
                workspace = self.store.workspace(workspace_id)
                self.workers[workspace_id] = self.worker_factory(workspace['projectPath'], self.unity, state_dir=self.store.root / 'workers' / workspace_id)
            return self.workers[workspace_id]

    def worker_status(self, workspace_id):
        with self.lock:
            worker = self.workers.get(workspace_id)
            status = worker.status() if worker else {'state': 'offline'}
            # Credentials remain in the desktop process, never in product state.
            status = {key: value for key, value in status.items() if key not in ('token', 'endpoint', 'config')}
            if workspace_id in self.jobs and not self.jobs[workspace_id].done():
                status['phase'] = 'Working in the background'
            if workspace_id in self.errors:
                status['error'] = self.errors[workspace_id]
            return status

    def state(self, workspace_id=None):
        workspaces = self.store.workspaces()
        workspace = self.store.workspace(workspace_id) if workspace_id else (workspaces[0] if workspaces else None)
        workspace_id = workspace['id'] if workspace else None
        integrations = [load_manifest(path).data for path in sorted((ROOT / 'adapters').glob('*.json'))]
        return {'protocolVersion': PROTOCOL_VERSION, 'version': __version__, 'workspaces': workspaces,
                'workspace': workspace, 'library': self.library.list(),
                'operations': self.store.operations(workspace_id),
                'worker': self.worker_status(workspace_id) if workspace else {'state': 'offline'},
                'integrations': integrations, 'photos': self.store.artifact_history(workspace_id) if workspace else [],
                'buildGate': self.store.build_gate(workspace_id) if workspace else None,
                'backgroundJobs': self.job_statuses()}

    def job_statuses(self):
        with self.lock:
            return [{'id': key, 'state': 'running' if not job.done() else ('failed' if job.exception() else 'succeeded'),
                     'error': str(job.exception()) if job.done() and job.exception() else None}
                    for key, job in self.jobs.items()]

    def job(self, job_id):
        with self.lock:
            job = self.jobs.get(job_id)
            if job is None:
                raise KeyError('Background action not found. Review the operation again after restarting Atelier.')
            if not job.done():
                return {'id': job_id, 'state': 'running'}
            if job.exception():
                return {'id': job_id, 'state': 'failed', 'error': str(job.exception())}
            return {'id': job_id, 'state': 'succeeded', 'result': job.result()}

    def background(self, key, function):
        with self.lock:
            if key in self.jobs and not self.jobs[key].done():
                raise Conflict('This background action is already running.')
            def run():
                try:
                    result = function()
                    with self.lock:
                        self.errors.pop(key, None)
                    return result
                except Exception as error:
                    with self.lock:
                        self.errors[key] = str(error)
                    raise
            self.jobs[key] = self.executor.submit(run)
            return {'id': key, 'state': 'running'}

    def register(self, data):
        info = inspect_project(data['projectPath'])
        project = info.to_dict() if hasattr(info, 'to_dict') else info
        project['projectPath'] = project.get('projectPath', project.get('path'))
        project['name'] = str(data.get('name', ''))[:200]
        return self.store.register(project)

    def plan_import(self, workspace_id, asset_id):
        workspace = self.store.workspace(workspace_id)
        job_id = identifier()
        def plan():
            result = self.library.import_plan(asset_id, workspace['projectPath'])
            with self.lock:
                self.plans[job_id] = {'workspaceId': workspace_id, 'assetId': asset_id, 'plan': result}
            return result
        return self.background(job_id, plan)

    def apply_import(self, workspace_id, plan_id, allow_code=False):
        with self.lock:
            saved = self.plans.get(plan_id)
            if not saved or saved['workspaceId'] != workspace_id:
                raise ValueError('Review this import again before applying it.')
            if any(entry['kind'] == 'code' for entry in saved['plan']['files']) and allow_code is not True:
                raise ValueError('This import contains executable Unity code. Include code explicitly in the review to proceed.')
            if any(op['state'] in ('queued', 'dispatching', 'running', 'needs-review') for op in self.store.operations(workspace_id)):
                raise Conflict('Finish or review pending changes before importing project assets.')
            worker = self.worker(workspace_id)
            if worker.status().get('state') == 'interactive':
                raise Conflict('Close interactive Unity before importing assets.')
            def apply():
                worker.stop()
                project = Path(self.store.workspace(workspace_id)['projectPath'])
                from .project_runtime import _locked
                if _locked(project):
                    raise Conflict('Close Unity before changing project files.')
                result = self.library.apply_import(saved['assetId'], str(project), saved['plan'])
                with self.lock:
                    self.plans.pop(plan_id, None)
                return result
            return self.background(workspace_id, apply)

    def context(self, workspace_id):
        worker = self.worker(workspace_id)
        if worker.status().get('state') not in ('online', 'starting', 'interactive'):
            raise ValueError('Start Unity in the background to discover saved avatar targets.')
        context = worker.client().context()
        if str(Path(context.get('projectPath', '')).resolve()) != self.store.workspace(workspace_id)['projectPath']:
            raise Conflict('Unity is connected to a different project.')
        return context

    def worker_action(self, workspace_id, action):
        workspace = self.store.workspace(workspace_id)
        if action not in ('start', 'stop', 'open-unity', 'provision'):
            raise ValueError('Unknown worker action.')
        with self.lock:
            if action in ('open-unity', 'provision', 'stop') and any(op['state'] in ('queued', 'dispatching', 'running') for op in self.store.operations(workspace_id)):
                raise Conflict('Finish pending operations before changing project ownership.')
            worker = self.worker(workspace_id)
            def perform():
                if action == 'provision':
                    return str(provision_bridge(workspace['projectPath']))
                if action == 'start':
                    worker.start()
                    return {'state': 'starting'}
                if action == 'stop':
                    worker.stop()
                    return {'state': 'offline'}
                worker.open_interactive()
                return {'state': 'interactive'}
            self.background(workspace_id, perform)

    def start_driver(self, workspace_id):
        with self.lock:
            if workspace_id in self.drivers and self.drivers[workspace_id].is_alive():
                return
            driver = threading.Thread(target=self._drive, args=(workspace_id,), daemon=True, name='atelier-sync-' + workspace_id[:8])
            self.drivers[workspace_id] = driver
            driver.start()

    def _drive(self, workspace_id):
        while not self.stopping.is_set():
            try:
                self.step(workspace_id)
            except Exception as error:
                # A connection failure is not an execution failure. Preserve intent.
                with self.lock:
                    self.errors[workspace_id] = str(error)
            self.stopping.wait(0.5)

    def step(self, workspace_id):
        # Finish artifact ingestion if the desktop stopped after recording the
        # Unity receipt but before copying the immutable PNG into its own store.
        for finished in self.store.operations(workspace_id):
            if (finished['action'] == 'snapshot' and finished['state'] == 'succeeded'
                    and finished.get('result', {}).get('artifact') and not finished['result'].get('artifactId')):
                self.ingest_artifact(finished)
        operation = self.store.next_operation(workspace_id)
        if not operation:
            return
        with self.lock:
            if workspace_id in self.jobs and not self.jobs[workspace_id].done():
                return
        worker = self.worker(workspace_id)
        status = worker.status().get('state')
        if status == 'interactive':
            return
        if status not in ('online', 'starting'):
            project = self.store.workspace(workspace_id)
            info = inspect_project(project['projectPath'])
            packages = info.packages if hasattr(info, 'packages') else info.get('packages', {})
            if 'dev.gryphprime.atelier-bridge' not in packages:
                raise ValueError('Install the Atelier bridge before synchronizing this workspace.')
            worker.start()
        bridge = worker.client()
        if operation['state'] in ('dispatching', 'running'):
            receipt = bridge.poll(operation['id'])
        else:
            context = bridge.context()
            project_path = self.store.workspace(workspace_id)['projectPath']
            if str(Path(context.get('projectPath', '')).resolve()) != project_path:
                raise Conflict('Unity bridge project identity mismatch.')
            targets = context.get('targets', [])
            exact = next((t for t in targets if t.get('objectId') == operation['target']['objectId'] and t.get('sceneGuid') == operation['target']['sceneGuid']), None)
            if not exact:
                raise Conflict('The saved avatar target is unavailable. Open its scene in Unity.')
            operation = self.store.dispatch(operation['id'], exact.get('revision') or context['revision'])
            if operation['state'] != 'dispatching':
                return
            receipt = bridge.submit(operation)
        if hasattr(receipt, '__dict__'):
            receipt = receipt.__dict__
        if operation['action'] == 'snapshot' and receipt.get('state') == 'succeeded':
            # Missing or invalid output is recoverable by polling the same
            # receipt; do not strand a successful operation without a photograph.
            self.artifact_bytes(workspace_id, (receipt.get('result') or {}).get('artifact'))
        finished = self.store.receipt(operation['id'], receipt)
        with self.lock:
            self.errors.pop(workspace_id, None)
        if finished['state'] == 'succeeded':
            if operation['action'] == 'snapshot':
                self.ingest_artifact(finished)
            elif operation['action'] == 'reconcile':
                self.store.enqueue(workspace_id, 'snapshot')

    def artifact_bytes(self, workspace_id, artifact):
        if not isinstance(artifact, dict) or not isinstance(artifact.get('path'), str):
            raise ValueError('Unity has not returned a photograph artifact.')
        path = Path(artifact['path']).resolve()
        allowed = (self.store.root / 'workers' / workspace_id).resolve()
        if allowed not in path.parents or not path.is_file() or path.stat().st_size > 32 * 1024 * 1024:
            raise ValueError('Unity photograph is missing, outside the workspace output directory, or too large.')
        png = path.read_bytes()
        if not png.startswith(b'\x89PNG\r\n\x1a\n'):
            raise ValueError('Unity photograph is not a PNG.')
        return png

    def ingest_artifact(self, operation):
        artifact = operation['result']['artifact']
        self.store.add_artifact(operation['id'], self.artifact_bytes(operation['workspaceId'], artifact),
                                {k: v for k, v in artifact.items() if k != 'path'})

    def close(self):
        self.stopping.set()
        for driver in self.drivers.values():
            driver.join(timeout=12)
        self.executor.shutdown(wait=True)
        for worker in self.workers.values():
            if worker.status().get('state') != 'interactive':
                worker.stop()
        self.store.close()


class Server(ThreadingHTTPServer):
    daemon_threads = True

    def __init__(self, application, port=0):
        self.application, self.token = application, secrets.token_urlsafe(32)
        super().__init__(('127.0.0.1', port), Handler)

    @property
    def origin(self):
        return 'http://127.0.0.1:' + str(self.server_port)


class Handler(BaseHTTPRequestHandler):
    def log_message(self, format, *args):
        pass  # Paths/tokens and user project names do not belong in access logs.

    def send(self, status, value, mime='application/json'):
        raw = value if isinstance(value, bytes) else canonical(value).encode()
        self.send_response(status)
        self.send_header('Content-Type', mime)
        self.send_header('Content-Length', str(len(raw)))
        self.send_header('Cache-Control', 'no-store')
        self.send_header('X-Content-Type-Options', 'nosniff')
        self.send_header('Referrer-Policy', 'no-referrer')
        self.send_header('Content-Security-Policy', "default-src 'self'; img-src 'self' blob:; style-src 'self'; script-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'")
        self.end_headers()
        self.wfile.write(raw)

    def do_GET(self):
        self.route(False)

    def do_POST(self):
        self.route(True)

    def route(self, mutation):
        try:
            expected_host = '127.0.0.1:' + str(self.server.server_port)
            if self.headers.get('Host') != expected_host or self.headers.get('Origin') not in (None, self.server.origin):
                return self.send(403, {'error': 'This host accepts only its own local origin.'})
            parsed = urlsplit(self.path)
            if not mutation and parsed.path in STATIC:
                path = ROOT / 'apps' / 'desktop' / STATIC[parsed.path]
                return self.send(200, path.read_bytes(), mimetypes.guess_type(str(path))[0] or 'application/octet-stream')
            if not secrets.compare_digest(self.headers.get('X-Atelier-Token', ''), self.server.token):
                return self.send(401, {'error': 'Open Atelier using the launch link from its desktop host.'})
            data = {}
            if mutation:
                if self.headers.get('Transfer-Encoding'):
                    raise ValueError('Streaming requests are not supported.')
                length = int(self.headers.get('Content-Length', '0'))
                if length < 0 or length > MAX_BODY:
                    return self.send(413, {'error': 'Request exceeds 128 KiB.'})
                if length:
                    if self.headers.get_content_type() != 'application/json':
                        raise ValueError('Expected a JSON request.')
                    self.connection.settimeout(5)
                    data = json.loads(self.rfile.read(length))
                    if not isinstance(data, dict):
                        raise ValueError('Expected a JSON object.')
            app = self.server.application
            pieces = parsed.path.strip('/').split('/')
            if not mutation and parsed.path == '/api/state':
                return self.send(200, app.state(parse_qs(parsed.query).get('workspace', [None])[0]))
            if mutation and parsed.path == '/api/workspaces':
                return self.send(201, app.register(data))
            if mutation and parsed.path == '/api/library':
                path = str(data['path'])
                job = app.background('library-import', lambda: app.library.add(path, data.get('creator', ''), data.get('product', '')))
                return self.send(202, job)
            if len(pieces) == 4 and pieces[:2] == ['api', 'workspaces']:
                workspace_id, action = pieces[2:]
                if action == 'context' and not mutation:
                    return self.send(200, app.context(workspace_id))
                if mutation:
                    if action == 'target':
                        return self.send(200, app.store.set_target(workspace_id, data))
                    if action == 'desired':
                        return self.send(200, app.store.desired(workspace_id, data['recipe'], data['expectedRevision']))
                    if action == 'undo':
                        return self.send(200, app.store.undo(workspace_id, data['expectedRevision']))
                    if action in ('sync', 'snapshot'):
                        result = app.store.enqueue(workspace_id, 'reconcile' if action == 'sync' else 'snapshot', data.get('expectedRevision'), data.get('view'))
                        app.start_driver(workspace_id)
                        return self.send(202, result)
                    if action == 'worker':
                        app.worker_action(workspace_id, data['action'])
                        return self.send(202, app.state(workspace_id))
                    if action == 'import-plan':
                        return self.send(202, app.plan_import(workspace_id, data['assetId']))
                    if action == 'import':
                        return self.send(202, app.apply_import(workspace_id, data['planId'], data.get('allowCode', False)))
            if not mutation and len(pieces) == 3 and pieces[:2] == ['api', 'jobs']:
                return self.send(200, app.job(pieces[2]))
            if mutation and len(pieces) == 4 and pieces[:2] == ['api', 'operations']:
                result = app.store.review(pieces[2], pieces[3])
                if pieces[3] == 'retry':
                    app.start_driver(result['workspaceId'])
                return self.send(202, result)
            if not mutation and len(pieces) == 3 and pieces[:2] == ['api', 'artifacts']:
                key = pieces[2]
                if len(key) != 64 or any(c not in '0123456789abcdef' for c in key):
                    raise KeyError('Artifact not found.')
                path = app.store.artifacts / (key + '.png')
                if not path.is_file():
                    raise KeyError('Artifact not found.')
                return self.send(200, path.read_bytes(), 'image/png')
            self.send(404, {'error': 'Route not found.'})
        except KeyError as error:
            self.send(404, {'error': str(error)})
        except Conflict as error:
            self.send(409, {'error': str(error)})
        except (ValueError, TypeError, OSError, RuntimeError) as error:
            self.send(400, {'error': str(error)})


def main(argv=None):
    parser = argparse.ArgumentParser(description='Atelier standalone avatar workspace')
    parser.add_argument('--data', default=str(Path.home() / '.atelier'), help='Atelier-owned local data directory')
    parser.add_argument('--port', type=int, default=0, help='Loopback port; default chooses an available port')
    parser.add_argument('--unity', help='Unity Editor executable')
    parser.add_argument('--no-browser', action='store_true')
    args = parser.parse_args(argv)
    with host_lock(args.data) as root:
        application = Application(root, args.unity)
        server = Server(application, args.port)
        for workspace in application.store.workspaces():
            if application.store.next_operation(workspace['id']):
                application.start_driver(workspace['id'])
        link = server.origin + '/#token=' + server.token
        print('Atelier ' + __version__ + '\n' + link, flush=True)
        if not args.no_browser:
            webbrowser.open(link)
        def stop(signum, frame):
            threading.Thread(target=server.shutdown, daemon=True).start()
        signal.signal(signal.SIGTERM, stop)
        signal.signal(signal.SIGINT, stop)
        try:
            server.serve_forever(poll_interval=0.25)
        finally:
            server.server_close()
            application.close()


if __name__ == '__main__':
    main()
