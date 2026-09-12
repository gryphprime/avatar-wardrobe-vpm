"""Desktop-owned state, receipts and artifacts.

Carries forward Avatar Wardrobe's persisted-before-dispatch and receipt-only
recovery boundaries. The domain is independent of Unity and of the AW package.
Confirmed state advances through matching receipts or explicitly reviewed,
revision-checked observations when recovering an external change.
"""
from contextlib import contextmanager
import hashlib
import json
import math
from pathlib import Path
import re
import sqlite3
import threading
import time
import uuid

ACTIVE = ('queued', 'dispatching', 'running')
GUID = re.compile(r'^[a-fA-F0-9]{32}$')
OBJECT_ID = re.compile(r'^GlobalObjectId_V1-\d+-([a-fA-F0-9]{32})-\d+-\d+$')
VIEWS = ('front', 'three-quarter', 'back')


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(',', ':'), ensure_ascii=False, allow_nan=False)


def cache_key(inputs):
    """Callers include target, recipe, source/environment and rendering revisions."""
    return hashlib.sha256(canonical(inputs).encode()).hexdigest()


def identifier():
    return str(uuid.uuid4())


def validate_recipe(value):
    if not isinstance(value, dict) or set(value) - {'items', 'appearance'}:
        raise ValueError('A recipe contains items and appearance only.')
    items, appearance = value.get('items', []), value.get('appearance', {})
    if not isinstance(items, list) or len(items) > 128 or not isinstance(appearance, dict):
        raise ValueError('Invalid recipe.')
    seen = set()
    for item in items:
        if not isinstance(item, dict) or set(item) - {'id', 'assetId', 'name', 'prefabGuid'}:
            raise ValueError('Invalid item.')
        for key in ('id', 'assetId', 'name', 'prefabGuid'):
            if not isinstance(item.get(key), str) or not item[key] or len(item[key]) > 512:
                raise ValueError('An item requires a bounded ' + key + '.')
        if item['id'] in seen or not GUID.fullmatch(item['prefabGuid']):
            raise ValueError('Each copy needs a unique identity and an exact prefab GUID.')
        seen.add(item['id'])
    if set(appearance) - {'materials', 'blendshapes'}:
        raise ValueError('Appearance supports material colors and static blendshapes only.')
    for kind in ('materials', 'blendshapes'):
        entries = appearance.get(kind, [])
        if not isinstance(entries, list) or len(entries) > 256:
            raise ValueError('Appearance controls must be a bounded list.')
        identities = set()
        for entry in entries:
            allowed = {'rendererId', 'slot', 'property', 'color'} if kind == 'materials' else {'rendererId', 'index', 'value'}
            if not isinstance(entry, dict) or set(entry) != allowed or not OBJECT_ID.fullmatch(str(entry.get('rendererId', ''))):
                raise ValueError('Appearance requires an exact renderer and supported control fields.')
            index = entry.get('slot' if kind == 'materials' else 'index')
            if type(index) is not int or not 0 <= index <= 65535:
                raise ValueError('Appearance control index is invalid.')
            key = (entry['rendererId'], index, entry.get('property'))
            if key in identities:
                raise ValueError('Each appearance control must appear only once.')
            identities.add(key)
            if kind == 'materials':
                if entry['property'] not in ('_Color', '_BaseColor'):
                    raise ValueError('Unsupported material color property.')
                color = entry['color']
                if not isinstance(color, list) or len(color) != 4 or any(type(v) not in (int, float) or not math.isfinite(v) or not 0 <= v <= 1 for v in color):
                    raise ValueError('Material colors require four finite channels between zero and one.')
            elif type(entry['value']) not in (int, float) or not math.isfinite(entry['value']) or not -100 <= entry['value'] <= 100:
                raise ValueError('Static blendshape weights must be between -100 and 100.')
    encoded = canonical({'items': items, 'appearance': appearance})
    if len(encoded.encode()) > 65536:
        raise ValueError('Recipe exceeds 64 KiB.')
    return json.loads(encoded)


class Conflict(ValueError):
    pass


class Store:
    def __init__(self, root):
        self.root = Path(root).resolve()
        self.root.mkdir(parents=True, exist_ok=True)
        self.artifacts = self.root / 'artifacts'
        self.artifacts.mkdir(exist_ok=True)
        self.lock = threading.RLock()
        self.db = sqlite3.connect(self.root / 'atelier.sqlite3', check_same_thread=False, isolation_level=None)
        self.db.row_factory = sqlite3.Row
        self.db.execute('PRAGMA journal_mode=WAL')
        self.db.execute('PRAGMA synchronous=FULL')
        self.db.executescript('''
            CREATE TABLE IF NOT EXISTS workspaces(id TEXT PRIMARY KEY, project TEXT UNIQUE, data TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS history(id INTEGER PRIMARY KEY AUTOINCREMENT, workspace TEXT, recipe TEXT);
            CREATE TABLE IF NOT EXISTS operations(seq INTEGER PRIMARY KEY AUTOINCREMENT, id TEXT UNIQUE,
                workspace TEXT, action TEXT, state TEXT, data TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS artifacts(id TEXT PRIMARY KEY, workspace TEXT, data TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS events(id INTEGER PRIMARY KEY AUTOINCREMENT, created REAL, data TEXT);
            CREATE TABLE IF NOT EXISTS reviews(id TEXT PRIMARY KEY, workspace TEXT, data TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS project_jobs(id TEXT PRIMARY KEY, workspace TEXT, state TEXT, data TEXT NOT NULL);
        ''')
        # A project mutation is never replayed on restart. Its durable journal
        # remains visible until the project has been inspected and acknowledged.
        with self.transaction():
            for row in self.db.execute("SELECT data FROM project_jobs WHERE state='running'").fetchall():
                job = json.loads(row[0])
                job.update(state='needs-review', error='Atelier stopped during this project operation. Inspect the project before continuing.')
                self.db.execute('UPDATE project_jobs SET state=?,data=? WHERE id=?', (job['state'], canonical(job), job['id']))

    @contextmanager
    def transaction(self):
        with self.lock:
            self.db.execute('BEGIN IMMEDIATE')
            try:
                yield
            except BaseException:
                self.db.execute('ROLLBACK')
                raise
            else:
                self.db.execute('COMMIT')

    def close(self):
        with self.lock:
            self.db.close()

    def _workspace(self, workspace_id):
        row = self.db.execute('SELECT data FROM workspaces WHERE id=?', (workspace_id,)).fetchone()
        if not row:
            raise KeyError('Workspace not found.')
        return json.loads(row['data'])

    def _save(self, workspace):
        self.db.execute('UPDATE workspaces SET data=? WHERE id=?', (canonical(workspace), workspace['id']))

    def register(self, project):
        path = str(Path(project['projectPath']).resolve())
        with self.transaction():
            existing = self.db.execute('SELECT data FROM workspaces WHERE project=?', (path,)).fetchone()
            if existing:
                return json.loads(existing['data'])
            empty = {'items': [], 'appearance': {}}
            workspace = {
                'id': identifier(), 'name': project.get('name') or Path(path).name,
                'projectPath': path, 'unityVersion': project.get('unityVersion', ''),
                'packages': project.get('packages', {}), 'target': None,
                'desired': {'revision': 0, 'recipe': empty},
                'confirmed': {'revision': 0, 'recipe': empty, 'unityRevision': None},
                'rendered': None, 'view': 'front', 'syncStatus': 'choose-target',
            }
            self.db.execute('INSERT INTO workspaces VALUES (?,?,?)', (workspace['id'], path, canonical(workspace)))
            self._event('workspace-registered', workspace['id'])
            return workspace

    def _active(self, workspace_id):
        return self.db.execute("SELECT 1 FROM operations WHERE workspace=? AND state IN ('queued','dispatching','running','needs-review') LIMIT 1", (workspace_id,)).fetchone() is not None

    def set_target(self, workspace_id, target):
        match = OBJECT_ID.fullmatch(target.get('objectId', '')) if isinstance(target, dict) else None
        if not match or not GUID.fullmatch(target.get('sceneGuid', '')) or match[1].lower() != target['sceneGuid'].lower():
            raise ValueError('Select an exact object in a saved scene.')
        with self.transaction():
            workspace = self._workspace(workspace_id)
            if self._active(workspace_id):
                raise Conflict('Resolve pending work before changing the avatar target.')
            normalized = {key: target[key] for key in ('sceneGuid', 'objectId')}
            if workspace['target'] and workspace['target'] != normalized and workspace['confirmed']['revision']:
                raise Conflict('Register a separate workspace for a different avatar.')
            if workspace['target'] != normalized:
                workspace['rendered'] = None
                workspace['confirmed']['unityRevision'] = None
            workspace['target'] = normalized
            workspace['syncStatus'] = 'draft' if workspace['desired']['revision'] else 'ready'
            if target.get('name'):
                workspace['name'] = str(target['name'])[:200]
            self._save(workspace)
            return workspace

    def workspace(self, workspace_id):
        with self.lock:
            return self._workspace(workspace_id)

    def workspaces(self):
        with self.lock:
            return [json.loads(row[0]) for row in self.db.execute('SELECT data FROM workspaces ORDER BY rowid')]

    def desired(self, workspace_id, recipe, expected_revision):
        recipe = validate_recipe(recipe)
        with self.transaction():
            workspace = self._workspace(workspace_id)
            if type(expected_revision) is not int or workspace['desired']['revision'] != expected_revision:
                raise Conflict('The draft changed. Refresh before editing it again.')
            if recipe == workspace['desired']['recipe']:
                return workspace
            self.db.execute('INSERT INTO history(workspace,recipe) VALUES (?,?)', (workspace_id, canonical(workspace['desired']['recipe'])))
            self.db.execute('DELETE FROM history WHERE workspace=? AND id NOT IN (SELECT id FROM history WHERE workspace=? ORDER BY id DESC LIMIT 100)', (workspace_id, workspace_id))
            workspace['desired'] = {'revision': expected_revision + 1, 'recipe': recipe}
            workspace['syncStatus'] = 'draft'
            self._save(workspace)
            self._event('desired-accepted', workspace_id, revision=expected_revision + 1)
            return workspace

    def undo(self, workspace_id, expected_revision):
        with self.transaction():
            workspace = self._workspace(workspace_id)
            if expected_revision != workspace['desired']['revision']:
                raise Conflict('The draft changed. Refresh before undoing.')
            row = self.db.execute('SELECT * FROM history WHERE workspace=? ORDER BY id DESC LIMIT 1', (workspace_id,)).fetchone()
            if not row:
                raise ValueError('There is no earlier draft to restore.')
            self.db.execute('DELETE FROM history WHERE id=?', (row['id'],))
            workspace['desired'] = {'revision': expected_revision + 1, 'recipe': json.loads(row['recipe'])}
            workspace['syncStatus'] = 'draft'
            self._save(workspace)
            return workspace

    def enqueue(self, workspace_id, action='reconcile', expected_revision=None, view=None, adapter=None):
        if action not in ('reconcile', 'snapshot', 'inspect'):
            raise ValueError('Unsupported operation.')
        with self.transaction():
            workspace = self._workspace(workspace_id)
            if self.project_jobs(workspace_id, unresolved=True):
                raise Conflict('Finish or review the pending project operation first.')
            if not workspace['target']:
                raise ValueError('Choose an avatar target before synchronizing.')
            if action == 'reconcile' and expected_revision != workspace['desired']['revision']:
                raise Conflict('The draft changed. Refresh before synchronizing.')
            if action != 'inspect' and self.db.execute("SELECT 1 FROM operations WHERE workspace=? AND state IN ('failed','needs-review') AND action='reconcile'", (workspace_id,)).fetchone():
                raise Conflict('Review the failed change before synchronizing again.')
            operations = self._operations(workspace_id)
            if action == 'reconcile':
                for existing in operations:
                    if existing['action'] == action and existing['desiredRevision'] == expected_revision and existing['state'] in ACTIVE + ('succeeded',):
                        return existing
                if workspace['desired']['revision'] == workspace['confirmed']['revision']:
                    raise ValueError('The avatar is already synchronized.')
            if sum(op['state'] in ACTIVE for op in operations) >= 128:
                raise ValueError('The workspace queue is full.')
            if action == 'snapshot':
                view = view or workspace['view']
                if view not in VIEWS:
                    raise ValueError('Unsupported camera view.')
                # Only transient, undispatched preview work may be superseded.
                for old in operations:
                    if old['action'] == 'snapshot' and old['state'] == 'queued':
                        old['state'] = 'superseded'
                        self._save_operation(old)
                workspace['view'] = view
            state = workspace['desired'] if action == 'reconcile' else workspace['confirmed']
            predecessor = None if action == 'inspect' else next((op['id'] for op in reversed(operations) if op['action'] == 'reconcile' and op['state'] in ACTIVE), None)
            operation = {
                'id': identifier(), 'workspaceId': workspace_id, 'target': workspace['target'],
                'action': action, 'state': 'queued', 'desiredRevision': state['revision'],
                'expectedRevision': workspace['confirmed']['unityRevision'], 'afterOperationId': predecessor,
                'payload': {'recipe': state['recipe'], 'view': view or workspace['view']},
                'created': time.time(), 'updated': time.time(), 'error': None, 'phase': 'Waiting for Unity',
                'priority': 'interactive', 'result': None,
            }
            if adapter:
                operation['adapter'] = dict(adapter)
            self.db.execute('INSERT INTO operations(id,workspace,action,state,data) VALUES (?,?,?,?,?)', (operation['id'], workspace_id, action, 'queued', canonical(operation)))
            if action == 'reconcile':
                workspace['syncStatus'] = 'pending'
            self._save(workspace)
            return operation

    def _operations(self, workspace_id):
        return [json.loads(row[0]) for row in self.db.execute('SELECT data FROM operations WHERE workspace=? ORDER BY seq', (workspace_id,))]

    def operations(self, workspace_id=None):
        with self.lock:
            if workspace_id:
                return self._operations(workspace_id)
            return [json.loads(row[0]) for row in self.db.execute('SELECT data FROM operations ORDER BY seq DESC LIMIT 256')]

    def operation(self, operation_id):
        with self.lock:
            row = self.db.execute('SELECT data FROM operations WHERE id=?', (operation_id,)).fetchone()
            if not row:
                raise KeyError('Operation not found.')
            return json.loads(row[0])

    def _save_operation(self, operation):
        operation['updated'] = time.time()
        self.db.execute('UPDATE operations SET state=?,data=? WHERE id=?', (operation['state'], canonical(operation), operation['id']))

    def next_operation(self, workspace_id):
        with self.lock:
            operations = self._operations(workspace_id)
            # A sent command is recovered before any further command is dispatched.
            for op in operations:
                if op['state'] in ('dispatching', 'running'):
                    return op
            if any(op['state'] in ('failed', 'needs-review') and op['action'] == 'reconcile' for op in operations):
                return next((op for op in operations if op['state'] == 'queued' and op['action'] == 'inspect'), None)
            return next((op for op in operations if op['state'] == 'queued'), None)

    def dispatch(self, operation_id, observed_revision):
        with self.transaction():
            op = self.operation(operation_id)
            if op['state'] != 'queued':
                raise Conflict('Only an undispatched operation can be sent.')
            workspace = self._workspace(op['workspaceId'])
            if op['afterOperationId']:
                previous = self.operation(op['afterOperationId'])
                if previous['state'] != 'succeeded':
                    raise Conflict('The previous change must finish first.')
                op['expectedRevision'] = previous['result']['revision']
            if op['expectedRevision'] is None:
                op['expectedRevision'] = observed_revision
            if op['action'] == 'inspect':
                # Diagnostics may inspect an externally changed avatar while a
                # mutation awaits review. They never confirm or rebase a recipe.
                op['expectedRevision'] = observed_revision
            if not isinstance(observed_revision, str) or not observed_revision or op['expectedRevision'] != observed_revision:
                op['state'], op['error'] = 'needs-review', 'Unity changed outside this draft. Review the avatar in Unity.'
                workspace['syncStatus'] = 'needs-review'
            else:
                if op['action'] == 'snapshot':
                    op['desiredRevision'] = workspace['confirmed']['revision']
                    op['payload']['recipe'] = workspace['confirmed']['recipe']
                op['state'], op['phase'] = 'dispatching', 'Sending to Unity'
                op['started'] = time.time()
            self._save_operation(op)
            self._save(workspace)
            return op

    def receipt(self, operation_id, receipt):
        with self.transaction():
            op = self.operation(operation_id)
            if receipt.get('id') != operation_id:
                raise ValueError('Receipt identity does not match the submitted operation.')
            state = receipt.get('state')
            if state not in ('queued', 'running', 'succeeded', 'failed', 'needs-review'):
                raise ValueError('Invalid receipt state.')
            if op['state'] not in ('dispatching', 'running'):
                return op
            workspace = self._workspace(op['workspaceId'])
            op['state'] = 'running' if state == 'queued' else state
            op['phase'] = receipt.get('phase', 'Applying in Unity' if op['state'] == 'running' else op['state'])
            op['error'] = receipt.get('error')
            op['result'] = dict(receipt.get('result') or {})
            if state == 'succeeded':
                revision = receipt.get('revision') or op['result'].get('revision')
                if not isinstance(revision, str) or not revision:
                    raise ValueError('A successful receipt requires a confirmed Unity revision.')
                op['result']['revision'] = revision
                if op['action'] == 'reconcile':
                    if workspace['target'] != op['target']:
                        raise Conflict('Receipt belongs to a different avatar target.')
                    workspace['confirmed'] = {'revision': op['desiredRevision'], 'recipe': op['payload']['recipe'], 'unityRevision': revision}
                    workspace['syncStatus'] = 'synced' if workspace['desired']['revision'] == op['desiredRevision'] else 'draft'
                elif (op['action'] == 'snapshot' and workspace['confirmed']['unityRevision'] is None and workspace['target'] == op['target']
                      and workspace['confirmed']['revision'] == op['desiredRevision']):
                    # A photograph establishes the observed base revision, without
                    # claiming that any of the desired edits have been applied.
                    workspace['confirmed']['unityRevision'] = revision
                op['completed'] = time.time()
                self._event('operation-completed', op['workspaceId'], action=op['action'],
                            queueWaitMs=1000 * (op.get('started', op['created']) - op['created']),
                            pendingMs=1000 * (op['completed'] - op['created']))
            elif state in ('failed', 'needs-review') and op['action'] == 'reconcile':
                workspace['syncStatus'] = state
            self._save(workspace)
            self._save_operation(op)
            return op

    def review(self, operation_id, decision):
        """Never automatically replay a possibly applied mutation."""
        with self.transaction():
            op = self.operation(operation_id)
            if decision == 'retry':
                if op['state'] not in ('needs-review', 'failed'):
                    raise Conflict('Only an interrupted operation needs receipt recovery.')
                op['state'], op['phase'] = 'running', 'Recovering the original Unity receipt'
            elif decision == 'dismiss':
                if op['state'] != 'failed':
                    raise Conflict('Ambiguous work must be reviewed in Unity; it cannot be silently dismissed.')
                op['state'] = 'dismissed'
                workspace = self._workspace(op['workspaceId'])
                workspace['syncStatus'] = 'draft'
                self._save(workspace)
                # Dependent edits cannot jump past an unsuccessful predecessor.
                for dependent in self._operations(op['workspaceId']):
                    if dependent['state'] == 'queued':
                        dependent['state'], dependent['error'] = 'cancelled', 'Previous change failed; draft preserved. Synchronize the current draft again.'
                        self._save_operation(dependent)
            else:
                raise ValueError('Unknown review action.')
            self._save_operation(op)
            return op

    def recovery_review(self, workspace_id, observation):
        """Persist the exact observation and draft presented for human review."""
        actual = validate_recipe(observation.get('recipe'))
        revision = observation.get('revision')
        if not isinstance(revision, str) or not revision or len(revision) > 256:
            raise ValueError('Unity inspection requires a current revision.')
        with self.transaction():
            workspace = self._workspace(workspace_id)
            target = observation.get('target', {})
            if {k: target.get(k) for k in ('sceneGuid', 'objectId')} != workspace['target']:
                raise Conflict('Unity inspection belongs to another target.')
            if str(Path(observation.get('projectPath', '')).resolve()) != workspace['projectPath']:
                raise Conflict('Unity inspection belongs to another project.')
            operations = self._operations(workspace_id)
            if any(op['state'] in ('dispatching', 'running') for op in operations):
                raise Conflict('Recover the original running receipt before reviewing actual state.')
            review = {'id': identifier(), 'workspaceId': workspace_id, 'target': workspace['target'],
                      'unityRevision': revision, 'desiredRevision': workspace['desired']['revision'],
                      'actualRecipe': actual, 'desiredRecipe': workspace['desired']['recipe'],
                      'operations': [{'id': op['id'], 'action': op['action'], 'state': op['state'], 'updated': op['updated']}
                                     for op in operations if op['state'] in ACTIVE + ('needs-review', 'failed')],
                      'warnings': observation.get('warnings', []), 'created': time.time(), 'consumed': False}
            self.db.execute('INSERT INTO reviews VALUES (?,?,?)', (review['id'], workspace_id, canonical(review)))
            return review

    def recover(self, workspace_id, review_id, decision, observation):
        if decision not in ('keep-draft', 'use-unity'):
            raise ValueError('Choose whether to keep the draft or use the observed Unity state.')
        with self.transaction():
            row = self.db.execute('SELECT data FROM reviews WHERE id=? AND workspace=?', (review_id, workspace_id)).fetchone()
            if not row:
                raise Conflict('Inspect the avatar again before resolving this change.')
            review = json.loads(row[0])
            workspace = self._workspace(workspace_id)
            operations = self._operations(workspace_id)
            pending = [{'id': op['id'], 'action': op['action'], 'state': op['state'], 'updated': op['updated']}
                       for op in operations if op['state'] in ACTIVE + ('needs-review', 'failed')]
            target = observation.get('target', {})
            if (review['consumed'] or review['target'] != workspace['target']
                    or review['desiredRevision'] != workspace['desired']['revision']
                    or review['operations'] != pending
                    or observation.get('revision') != review['unityRevision']
                    or {k: target.get(k) for k in ('sceneGuid', 'objectId')} != review['target']
                    or str(Path(observation.get('projectPath', '')).resolve()) != workspace['projectPath']
                    or validate_recipe(observation.get('recipe')) != review['actualRecipe']):
                raise Conflict('The draft, queue, or Unity avatar changed since review. Inspect again.')
            actual = review['actualRecipe']
            desired = workspace['desired']['recipe'] if decision == 'keep-draft' else actual
            if desired != workspace['desired']['recipe']:
                self.db.execute('INSERT INTO history(workspace,recipe) VALUES (?,?)', (workspace_id, canonical(workspace['desired']['recipe'])))
            # Reserve a fresh baseline revision; old successful operation IDs may
            # never deduplicate away the new intent after an external Unity edit.
            baseline = workspace['desired']['revision'] + 1
            workspace['confirmed'] = {'revision': baseline, 'recipe': actual, 'unityRevision': review['unityRevision']}
            workspace['desired'] = {'revision': baseline + (desired != actual), 'recipe': desired}
            workspace['syncStatus'] = 'synced' if desired == actual else 'draft'
            for op in operations:
                if op['state'] in ACTIVE + ('needs-review', 'failed'):
                    op['state'] = 'cancelled' if op['state'] == 'queued' else 'resolved'
                    op['phase'] = 'Resolved against inspected Unity state'
                    op['resolution'] = {'reviewId': review_id, 'decision': decision}
                    self._save_operation(op)
            review.update(consumed=True, decision=decision, completed=time.time())
            self.db.execute('UPDATE reviews SET data=? WHERE id=?', (canonical(review), review_id))
            self._save(workspace)
            self._event('workspace-recovered', workspace_id, reviewId=review_id, decision=decision)
            return workspace

    def project_jobs(self, workspace_id=None, unresolved=False):
        with self.lock:
            jobs = [json.loads(row[0]) for row in self.db.execute('SELECT data FROM project_jobs ORDER BY rowid DESC')]
            return [job for job in jobs if (not workspace_id or job['workspaceId'] == workspace_id)
                    and (not unresolved or job['state'] in ('running', 'needs-review'))]

    def begin_project_job(self, workspace_id, kind, plan):
        with self.transaction():
            self._workspace(workspace_id)
            if self.project_jobs(workspace_id, unresolved=True) or self._active(workspace_id):
                raise Conflict('Finish or review pending project and avatar operations first.')
            job = {'id': identifier(), 'workspaceId': workspace_id, 'kind': kind, 'plan': plan,
                   'state': 'running', 'created': time.time(), 'result': None, 'error': None}
            self.db.execute('INSERT INTO project_jobs VALUES (?,?,?,?)', (job['id'], workspace_id, job['state'], canonical(job)))
            return job

    def finish_project_job(self, job_id, result=None, error=None):
        with self.transaction():
            row = self.db.execute('SELECT data FROM project_jobs WHERE id=?', (job_id,)).fetchone()
            if not row:
                raise KeyError('Project operation not found.')
            job = json.loads(row[0])
            job.update(state='needs-review' if error else 'succeeded', result=result, error=str(error) if error else None, completed=time.time())
            self.db.execute('UPDATE project_jobs SET state=?,data=? WHERE id=?', (job['state'], canonical(job), job_id))
            return job

    def acknowledge_project_job(self, workspace_id, job_id, manifest_sha):
        with self.transaction():
            row = self.db.execute('SELECT data FROM project_jobs WHERE id=? AND workspace=?', (job_id, workspace_id)).fetchone()
            if not row:
                raise KeyError('Project operation not found.')
            job = json.loads(row[0])
            if job['state'] != 'needs-review':
                raise Conflict('Only an interrupted project operation needs review.')
            job.update(state='reviewed', reviewed=time.time(), reviewedManifest=manifest_sha)
            self.db.execute('UPDATE project_jobs SET state=?,data=? WHERE id=?', (job['state'], canonical(job), job_id))
            workspace = self._workspace(workspace_id)
            workspace['syncStatus'] = 'needs-review' if workspace['target'] else 'choose-target'
            # External package/import changes invalidate the observed avatar base.
            self._save(workspace)
            return job

    def refresh_packages(self, workspace_id, packages):
        with self.transaction():
            workspace = self._workspace(workspace_id)
            workspace['packages'] = dict(packages)
            self._save(workspace)
            return workspace

    def add_artifact(self, operation_id, png, metadata):
        if not isinstance(png, bytes) or len(png) > 32 * 1024 * 1024 or not png.startswith(b'\x89PNG\r\n\x1a\n'):
            raise ValueError('Expected a PNG artifact of at most 32 MiB.')
        with self.transaction():
            op = self.operation(operation_id)
            if op['action'] != 'snapshot' or op['state'] != 'succeeded':
                raise ValueError('Artifacts require a successful snapshot receipt.')
            workspace = self._workspace(op['workspaceId'])
            inputs = {'target': op['target'], 'revision': op['desiredRevision'],
                      'unityRevision': op['result']['revision'], 'recipe': op['payload']['recipe'],
                      'view': op['payload']['view'], 'environment': workspace['packages'],
                      'handler': 'atelier-snapshot-v1', 'metadata': metadata,
                      'content': hashlib.sha256(png).hexdigest()}
            key = cache_key(inputs)
            destination = self.artifacts / (key + '.png')
            if not destination.exists():
                temporary = destination.with_suffix('.tmp')
                temporary.write_bytes(png)
                temporary.replace(destination)
            artifact = {'id': key, 'artifactId': key, 'revision': op['desiredRevision'],
                        'url': '/api/artifacts/' + key, 'view': op['payload']['view'],
                        'created': time.time(), 'inputs': inputs}
            self.db.execute('INSERT OR IGNORE INTO artifacts VALUES (?,?,?)', (key, op['workspaceId'], canonical(artifact)))
            op['result']['artifactId'] = key
            self._save_operation(op)
            if (workspace['confirmed']['revision'] == op['desiredRevision'] and workspace['target'] == op['target']
                    and workspace['confirmed']['unityRevision'] == op['result']['revision'] and workspace['view'] == artifact['view']):
                workspace['rendered'] = artifact
                self._save(workspace)
            return artifact

    def artifact_history(self, workspace_id):
        with self.lock:
            return [json.loads(row[0]) for row in self.db.execute('SELECT data FROM artifacts WHERE workspace=? ORDER BY rowid DESC LIMIT 100', (workspace_id,))]

    def build_gate(self, workspace_id):
        with self.lock:
            workspace = self._workspace(workspace_id)
            reasons = []
            if not workspace['target']:
                reasons.append('Choose an avatar.')
            if workspace['desired']['revision'] != workspace['confirmed']['revision']:
                reasons.append('Synchronize the current draft.')
            if self._active(workspace_id):
                reasons.append('Resolve pending operations.')
            reasons.append('Final SDK validation and build are available through Open in Unity in this alpha.')
            return {'ready': False, 'reasons': reasons, 'nativeHandoff': True}

    def _event(self, kind, workspace_id, **values):
        self.db.execute('INSERT INTO events(created,data) VALUES (?,?)', (time.time(), canonical(dict(kind=kind, workspaceId=workspace_id, **values))))
        self.db.execute('DELETE FROM events WHERE id NOT IN (SELECT id FROM events ORDER BY id DESC LIMIT 1000)')

    def events(self):
        with self.lock:
            return [json.loads(row[0]) for row in self.db.execute('SELECT data FROM events ORDER BY id DESC LIMIT 100')]
