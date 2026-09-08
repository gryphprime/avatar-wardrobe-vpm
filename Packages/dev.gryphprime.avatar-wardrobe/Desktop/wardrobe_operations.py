"""Durable intents and an at-most-once dispatch boundary for the Unity bridge.

Only a project lease holder drives work. HTTP handlers merely commit intent. A
committed dispatch is *always* recovered by receipt query, even if its POST never
returned: the user must review an unknown receipt instead of replaying an edit.
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

TYPES = {'wear-outfit', 'replace-outfit', 'remove-outfit', 'prepare-preview', 'capture-source', 'render-snapshot'}
STATES = {'queued', 'running', 'succeeded', 'failed', 'cancelled', 'superseded', 'needs-review'}
TERMINAL = STATES - {'queued', 'running'}
PREVIEWS = {'prepare-preview', 'capture-source', 'render-snapshot'}
MAX_COMMAND_BYTES = 65536
_UUID = re.compile(r'[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\Z')
_GUID = re.compile(r'[0-9a-fA-F]{32}\Z')
_GLOBAL_ID = re.compile(r'GlobalObjectId_V1-[0-9]+-[0-9a-fA-F]{32}-[0-9]+-[0-9]+\Z')


def _json(value):
    return json.dumps(value, sort_keys=True, separators=(',', ':'), ensure_ascii=False, allow_nan=False)


def _string(value, name, limit, empty=True):
    if not isinstance(value, str) or len(value) > limit or (not empty and not value) or any(ord(c) < 32 for c in value):
        raise ValueError('Invalid ' + name + '.')


def validate_command(command, project_id):
    if not isinstance(command, dict) or set(command) != {'id', 'type', 'target', 'precondition', 'payload'}:
        raise ValueError('A command requires only id, type, target, precondition and payload.')
    identifier = command['id']
    if not isinstance(identifier, str) or not _UUID.fullmatch(identifier):
        raise ValueError('Operation id must be a lowercase dashed UUID.')
    if not isinstance(command['type'], str) or command['type'] not in TYPES:
        raise ValueError('Unsupported operation type.')
    target = command['target']
    if not isinstance(target, dict) or set(target) != {'projectId', 'sceneGuid', 'avatarId', 'avatarInstanceId', 'session', 'scopeId'}:
        raise ValueError('Malformed operation target.')
    _string(target['projectId'], 'project identity', 4096, False)
    if target['projectId'] != project_id or str(Path(target['projectId']).resolve()) != target['projectId']:
        raise ValueError('Operation target project mismatch or noncanonical path.')
    _string(target['sceneGuid'], 'scene GUID', 32)
    if target['sceneGuid'] and not _GUID.fullmatch(target['sceneGuid']):
        raise ValueError('Invalid scene GUID.')
    _string(target['avatarId'], 'avatar identity', 256, False)
    if not _GLOBAL_ID.fullmatch(target['avatarId']):
        raise ValueError('Avatar identity must be a Unity GlobalObjectId.')
    if type(target['avatarInstanceId']) is not int or not -(2**31) <= target['avatarInstanceId'] < 2**31 or target['avatarInstanceId'] == 0:
        raise ValueError('Avatar instance identity must be a nonzero signed integer.')
    _string(target['session'], 'session', 256, False)
    _string(target['scopeId'], 'scope identity', 512, False)
    precondition = command['precondition']
    if not isinstance(precondition, dict) or set(precondition) != {'observedRevision', 'afterOperationId'}:
        raise ValueError('Malformed operation precondition.')
    _string(precondition['observedRevision'], 'observed revision', 256, False)
    predecessor = precondition['afterOperationId']
    if not isinstance(predecessor, str) or (predecessor and not _UUID.fullmatch(predecessor)) or predecessor == identifier:
        raise ValueError('Invalid predecessor operation id.')
    payload = command['payload']
    strings = {'variantId': 32, 'assetVersion': 256, 'instanceId': 256, 'previewToken': 256,
               'view': 32, 'menuGroup': 512, 'itemPath': 4096}
    booleans = {'addCopy', 'allowUnverified', 'createToggles', 'before'}
    if not isinstance(payload, dict) or set(payload) - (set(strings) | booleans | {'zoom'}):
        raise ValueError('Malformed operation payload.')
    for name, limit in strings.items():
        if name in payload:
            _string(payload[name], name, limit)
    for name in booleans:
        if name in payload and type(payload[name]) is not bool:
            raise ValueError(name + ' must be a boolean.')
    variant = payload.get('variantId', '')
    if variant and not _GUID.fullmatch(variant):
        raise ValueError('Variant identity must be a 32-character asset GUID.')
    if command['type'] in {'wear-outfit', 'replace-outfit', 'remove-outfit'} and not variant:
        raise ValueError('An outfit operation requires a variant GUID.')
    if command['type'] in {'replace-outfit', 'remove-outfit'} and not payload.get('instanceId'):
        raise ValueError('An exact worn instance is required.')
    if 'view' in payload and payload['view'] not in {'front', 'three-quarter', 'back'}:
        raise ValueError('Unsupported snapshot view.')
    if command['type'] == 'render-snapshot' and not payload.get('previewToken'):
        raise ValueError('A prepared preview token is required.')
    zoom = payload.get('zoom', 1)
    if type(zoom) not in (int, float) or not math.isfinite(zoom) or not 0 < zoom <= 3:
        raise ValueError('Snapshot zoom must be finite and between zero and three.')
    raw = _json(command)
    if len(raw.encode('utf-8')) > MAX_COMMAND_BYTES:
        raise ValueError('Operation exceeds 64 KiB.')
    return raw


class OperationQueue:
    def __init__(self, path, project_id, bridge=None, max_active=128, max_completed=256,
                 retention_seconds=7 * 86400, lease_seconds=30, clock=time.time):
        self.project_id = str(Path(project_id).resolve()) if project_id else ''
        self.bridge = bridge
        self.max_active, self.max_completed = max_active, max_completed
        self.retention_seconds, self.lease_seconds, self.clock = retention_seconds, lease_seconds, clock
        self.owner = str(uuid.uuid4())
        self.lock = threading.RLock()
        self.closed = False
        self._observed_session = None
        Path(path).parent.mkdir(parents=True, exist_ok=True)
        self.db = sqlite3.connect(path, timeout=5, check_same_thread=False, isolation_level=None)
        self.db.row_factory = sqlite3.Row
        self.db.execute('PRAGMA journal_mode=WAL')
        self.db.execute('PRAGMA synchronous=FULL')
        self.db.execute('PRAGMA busy_timeout=5000')
        with self._transaction():
            old_columns = {r['name'] for r in self.db.execute('PRAGMA table_info(operations)')}
            legacy = old_columns and 'command' not in old_columns
            if legacy:
                self.db.execute('ALTER TABLE operations RENAME TO operations_legacy')
            self.db.execute('''CREATE TABLE IF NOT EXISTS operations (
                id TEXT PRIMARY KEY, project TEXT NOT NULL, type TEXT NOT NULL,
                command TEXT NOT NULL, fingerprint TEXT NOT NULL, target_key TEXT NOT NULL,
                state TEXT NOT NULL, created REAL NOT NULL, updated REAL NOT NULL,
                result TEXT, error TEXT, waiting_reason TEXT NOT NULL DEFAULT '',
                cancel_requested INTEGER NOT NULL DEFAULT 0, dispatched INTEGER NOT NULL DEFAULT 0,
                execute_revision TEXT, cancel_sent INTEGER NOT NULL DEFAULT 0)''')
            self.db.execute('CREATE INDEX IF NOT EXISTS operation_order ON operations(project, state, created)')
            self.db.execute('''CREATE TABLE IF NOT EXISTS operation_tombstones (
                id TEXT PRIMARY KEY, project TEXT NOT NULL, fingerprint TEXT NOT NULL,
                target_key TEXT NOT NULL, receipt TEXT NOT NULL)''')
            self.db.execute('''CREATE TABLE IF NOT EXISTS operation_leases (
                project TEXT PRIMARY KEY, owner TEXT NOT NULL, expires REAL NOT NULL)''')
            if legacy:
                for row in self.db.execute('SELECT * FROM operations_legacy').fetchall():
                    raw = row['payload']
                    command = json.loads(raw)
                    raw = _json(command)
                    state = row['state'] if row['state'] in TERMINAL else 'needs-review'
                    self.db.execute('''INSERT INTO operations
                        (id,project,type,command,fingerprint,target_key,state,created,updated,result,error,dispatched)
                        VALUES (?,?,?,?,?,?,?,?,?,?,?,?)''', (row['id'], row['project'], row['type'], raw,
                        self._fingerprint(raw), _json(command.get('target', {})), state, row['created'],
                        self.clock(), row['result'], 'Legacy receipt: review the scene before creating another command.', 1))
                self.db.execute('DROP TABLE operations_legacy')
            self._prune()

    @contextmanager
    def _transaction(self):
        with self.lock:
            self.db.execute('BEGIN IMMEDIATE')
            try:
                yield
            except BaseException:
                self.db.execute('ROLLBACK')
                raise
            else:
                self.db.execute('COMMIT')

    @staticmethod
    def _fingerprint(raw):
        return hashlib.sha256(raw.encode('utf-8')).hexdigest()

    def submit(self, command):
        raw = validate_command(command, self.project_id)
        # All subsequent reads use the serialized copy, never a caller-owned object.
        command = json.loads(raw)
        identifier, now = command['id'], self.clock()
        fingerprint, target_key = self._fingerprint(raw), _json(command['target'])
        with self._transaction():
            old = (self.db.execute('SELECT * FROM operations WHERE id=?', (identifier,)).fetchone()
                   or self.db.execute('SELECT * FROM operation_tombstones WHERE id=?', (identifier,)).fetchone())
            if old:
                if old['project'] != self.project_id or old['fingerprint'] != fingerprint:
                    raise ValueError('Operation id already exists with different payload.')
                return self._receipt(old)
            pred_id = command['precondition']['afterOperationId']
            if pred_id:
                predecessor = self._lookup(pred_id)
                if not predecessor or predecessor['target_key'] != target_key:
                    raise ValueError('Predecessor must already exist for this exact project, scene, avatar, session and scope.')
            if command['type'] in PREVIEWS:
                # Preserve the complete explicit dependency chain of the replacement.
                protected, ancestor_id = set(), pred_id
                while ancestor_id and ancestor_id not in protected:
                    protected.add(ancestor_id)
                    ancestor = self._lookup(ancestor_id)
                    ancestor_command = self._receipt(ancestor).get('command') if ancestor else None
                    ancestor_id = ancestor_command['precondition']['afterOperationId'] if ancestor_command else ''
                candidates = self.db.execute("SELECT id FROM operations WHERE project=? AND state='queued' AND dispatched=0 AND target_key=? AND type IN ('prepare-preview','capture-source','render-snapshot')", (self.project_id, target_key)).fetchall()
                for candidate in candidates:
                    if candidate['id'] not in protected:
                        self._update(candidate['id'], state='superseded', waiting_reason='')
            count = self.db.execute("SELECT count(*) FROM operations WHERE project=? AND state IN ('queued','running')", (self.project_id,)).fetchone()[0]
            if count >= self.max_active:
                raise OverflowError('Operation queue is full.')
            waiting = 'Waiting for the previous operation' if pred_id else ''
            self.db.execute('''INSERT INTO operations (id,project,type,command,fingerprint,target_key,state,created,updated,waiting_reason)
                VALUES (?,?,?,?,?,?,'queued',?,?,?)''', (identifier, self.project_id, command['type'], raw, fingerprint, target_key, now, now, waiting))
            self._prune()
            return self._receipt(self._lookup(identifier))

    def _lookup(self, identifier):
        return (self.db.execute('SELECT * FROM operations WHERE id=? AND project=?', (identifier, self.project_id)).fetchone()
                or self.db.execute('SELECT * FROM operation_tombstones WHERE id=? AND project=?', (identifier, self.project_id)).fetchone())

    @staticmethod
    def _unknown(identifier):
        return {'id': identifier, 'state': 'needs-review', 'command': None, 'result': None,
                'error': 'Unknown receipt. Review the scene; this operation will not be replayed.',
                'waitingReason': '', 'cancelRequested': False}

    def _receipt(self, row):
        if 'receipt' in row.keys():
            return json.loads(row['receipt'])
        return {'id': row['id'], 'type': row['type'], 'state': row['state'], 'command': json.loads(row['command']),
                'result': json.loads(row['result']) if row['result'] else None, 'error': row['error'],
                'waitingReason': row['waiting_reason'], 'cancelRequested': bool(row['cancel_requested']),
                'createdAt': row['created'], 'updatedAt': row['updated']}

    def get(self, identifier):
        _string(identifier, 'receipt id', 256)
        with self.lock:
            row = self._lookup(identifier)
            return self._receipt(row) if row else self._unknown(identifier)

    def list(self, limit=256):
        with self.lock:
            rows = self.db.execute('SELECT * FROM operations WHERE project=? ORDER BY created DESC, rowid DESC LIMIT ?',
                                   (self.project_id, max(1, min(int(limit), 512)))).fetchall()
            return [self._receipt(row) for row in rows]

    def has_pending_mutations(self):
        with self.lock:
            return self.db.execute("SELECT 1 FROM operations WHERE project=? AND state IN ('queued','running') AND type IN ('wear-outfit','replace-outfit','remove-outfit') LIMIT 1", (self.project_id,)).fetchone() is not None

    def cancel(self, identifier):
        _string(identifier, 'receipt id', 256)
        with self._transaction():
            row = self._lookup(identifier)
            if row and 'receipt' not in row.keys():
                if row['state'] == 'queued' and not row['dispatched']:
                    self.db.execute("UPDATE operations SET state='cancelled',cancel_requested=1,waiting_reason='',updated=? WHERE id=?", (self.clock(), identifier))
                elif row['state'] == 'running':
                    self.db.execute('UPDATE operations SET cancel_requested=1,updated=? WHERE id=?', (self.clock(), identifier))
                self._prune()
            return self._receipt(self._lookup(identifier)) if row else self._unknown(identifier)

    def reconcile_session(self, context):
        """A reopened Unity session cannot confirm that an unsaved edit survived."""
        if not isinstance(context, dict) or context.get('projectId') != self.project_id:
            raise ValueError('The operation context belongs to another project.')
        session = context.get('session')
        _string(session, 'Unity context session', 256, False)
        with self._transaction():
            if session == self._observed_session:
                return 0
            changed = 0
            error = 'Unity restarted after this unsaved edit. Review the scene to confirm whether it survived; this operation will not be replayed.'
            rows = self.db.execute("SELECT * FROM operations WHERE project=? AND state='succeeded' AND type IN ('wear-outfit','replace-outfit','remove-outfit')", (self.project_id,)).fetchall()
            for row in rows:
                result = json.loads(row['result']) if row['result'] else {}
                if result.get('unsaved') is True and json.loads(row['target_key']).get('session') != session:
                    self._update(row['id'], state='needs-review', error=error, waiting_reason='')
                    changed += 1
            # Compact receipts preserve the same guarantee after detailed retention expires.
            rows = self.db.execute('SELECT * FROM operation_tombstones WHERE project=?', (self.project_id,)).fetchall()
            for row in rows:
                receipt = json.loads(row['receipt'])
                if (receipt['state'] == 'succeeded' and receipt['type'] in {'wear-outfit', 'replace-outfit', 'remove-outfit'} and
                        (receipt.get('result') or {}).get('unsaved') is True and json.loads(row['target_key']).get('session') != session):
                    receipt.update(state='needs-review', error=error, waitingReason='', updatedAt=self.clock())
                    self.db.execute('UPDATE operation_tombstones SET receipt=? WHERE id=? AND project=?', (_json(receipt), row['id'], self.project_id))
                    changed += 1
            self._prune()
        self._observed_session = session
        return changed

    def acquire_lease(self):
        with self._transaction():
            now = self.clock()
            row = self.db.execute('SELECT * FROM operation_leases WHERE project=?', (self.project_id,)).fetchone()
            if row and row['owner'] != self.owner and row['expires'] > now:
                return False
            self.db.execute('INSERT OR REPLACE INTO operation_leases(project,owner,expires) VALUES(?,?,?)',
                            (self.project_id, self.owner, now + self.lease_seconds))
            return True

    def _owns_lease(self):
        row = self.db.execute('SELECT owner,expires FROM operation_leases WHERE project=?', (self.project_id,)).fetchone()
        return bool(row and row['owner'] == self.owner and row['expires'] > self.clock())

    def release_lease(self):
        with self._transaction():
            self.db.execute('DELETE FROM operation_leases WHERE project=? AND owner=?', (self.project_id, self.owner))

    def next(self):
        """Find work without crossing the dispatch boundary; requires a writer lease."""
        with self._transaction():
            if not self._owns_lease():
                return None
            rows = self.db.execute("SELECT * FROM operations WHERE project=? AND state IN ('queued','running') ORDER BY dispatched DESC, created, rowid", (self.project_id,)).fetchall()
            for row in rows:
                receipt = self._receipt(row)
                receipt['dispatched'] = bool(row['dispatched'])
                receipt['executeRevision'] = row['execute_revision']
                receipt['cancelSent'] = bool(row['cancel_sent'])
                if row['dispatched']:
                    return receipt
                command = receipt['command']
                pred_id = command['precondition']['afterOperationId']
                if pred_id:
                    predecessor = self._lookup(pred_id)
                    prior = self._receipt(predecessor) if predecessor else self._unknown(pred_id)
                    if prior['state'] in ('queued', 'running'):
                        self._update(row['id'], waiting_reason='Waiting for the previous operation')
                        continue
                    if not predecessor or predecessor['target_key'] != row['target_key'] or prior['state'] != 'succeeded':
                        self._update(row['id'], state='failed', error='The predecessor did not succeed for this exact target.', waiting_reason='')
                        continue
                    revision = (prior.get('result') or {}).get('confirmedRevision')
                    if not isinstance(revision, str) or not revision:
                        self._update(row['id'], state='needs-review', error='The predecessor has no confirmed revision.', waiting_reason='')
                        continue
                    receipt['executeRevision'] = revision
                else:
                    receipt['executeRevision'] = command['precondition']['observedRevision']
                return receipt
            self._prune()
            return None

    def _update(self, identifier, **fields):
        fields['updated'] = self.clock()
        self.db.execute('UPDATE operations SET ' + ','.join(key + '=?' for key in fields) + ' WHERE id=? AND project=?',
                        tuple(fields.values()) + (identifier, self.project_id))

    def dispatched(self, identifier, revision):
        with self._transaction():
            if not self._owns_lease():
                return False
            row = self._lookup(identifier)
            if not row or 'receipt' in row.keys() or row['state'] != 'queued' or row['dispatched']:
                return False
            self._update(identifier, state='running', dispatched=1, execute_revision=revision,
                         waiting_reason='Waiting for Unity to accept the operation')
            return True

    def waiting(self, identifier, reason):
        with self._transaction():
            if self._owns_lease():
                self.db.execute("UPDATE operations SET waiting_reason=?,updated=? WHERE id=? AND project=? AND state IN ('queued','running')",
                                (reason[:1024], self.clock(), identifier, self.project_id))
        return self.get(identifier)

    def request_bridge_cancel(self, identifier):
        with self._transaction():
            if not self._owns_lease():
                return False
            row = self._lookup(identifier)
            if not row or 'receipt' in row.keys() or row['state'] != 'running' or not row['cancel_requested'] or row['cancel_sent']:
                return False
            self._update(identifier, cancel_sent=1)
            return True

    def record_bridge(self, identifier, receipt):
        with self._transaction():
            if not self._owns_lease():
                return self.get(identifier)
            row = self._lookup(identifier)
            if not row or 'receipt' in row.keys() or row['state'] in TERMINAL:
                return self.get(identifier)
            if (not isinstance(receipt, dict) or receipt.get('id') != identifier or
                    not isinstance(receipt.get('state'), str) or receipt['state'] not in STATES):
                receipt = dict(self._unknown(identifier), error='Invalid or expired Unity receipt. Review the scene before continuing.')
            state = receipt['state']
            result = receipt.get('result')
            try:
                valid_result = result is None or (isinstance(result, dict) and len(_json(result).encode()) <= 1024 * 1024)
            except (ValueError, TypeError, RecursionError):
                valid_result = False
            if not valid_result:
                state, result = 'needs-review', None
                receipt = dict(receipt, error='Unity returned an invalid or oversized result.')
            error = receipt.get('error')
            if error is not None and not isinstance(error, str):
                error = 'Unity returned an invalid error.'
            waiting = receipt.get('waitingReason') or ('Waiting for Unity' if state in ('queued', 'running') else '')
            self._update(identifier, state='running' if state == 'queued' else state,
                         result=_json(result) if result is not None else None, error=error[:4096] if error else None,
                         waiting_reason=waiting[:1024] if isinstance(waiting, str) else '',
                         cancel_requested=int(bool(row['cancel_requested'] or receipt.get('cancelRequested'))))
            self._prune()
            return self.get(identifier)

    def finish(self, identifier, state, result=None, error=None):
        """Record a locally determined outcome, fenced by the project writer lease."""
        if state not in TERMINAL:
            raise ValueError('Invalid terminal state.')
        return self.record_bridge(identifier, {'id': identifier, 'state': state, 'result': result, 'error': error})

    def _prune(self):
        rows = self.db.execute("SELECT * FROM operations WHERE project=? AND state NOT IN ('queued','running') ORDER BY updated DESC, rowid DESC", (self.project_id,)).fetchall()
        for index, row in enumerate(rows):
            if index < self.max_completed and row['updated'] >= self.clock() - self.retention_seconds:
                continue
            receipt = self._receipt(row)
            receipt.update(command=None, receiptExpired=True)
            result = receipt['result'] or {}
            receipt['result'] = {key: result[key] for key in ('confirmedRevision', 'unsaved') if key in result} or None
            receipt['error'] = (receipt['error'] or 'Detailed receipt expired. Review the scene before repeating this action.')[:512]
            self.db.execute('INSERT OR REPLACE INTO operation_tombstones VALUES(?,?,?,?,?)',
                            (row['id'], self.project_id, row['fingerprint'], row['target_key'], _json(receipt)))
            self.db.execute('DELETE FROM operations WHERE id=? AND project=?', (row['id'], self.project_id))

    def close(self):
        with self.lock:
            if not self.closed:
                self.release_lease()
                self.db.close()
                self.closed = True


class OperationDriver:
    def __init__(self, queue, bridge):
        self.queue, self.bridge = queue, bridge
        self._step_lock = threading.Lock()

    def step(self):
        # No bridge/network work occurs under a SQLite transaction or queue lock.
        if not self._step_lock.acquire(blocking=False):
            return None
        try:
            return self._step()
        finally:
            self._step_lock.release()

    def _step(self):
        queue = self.queue
        if not queue.acquire_lease():
            return None
        operation = queue.next()
        if not operation:
            return None
        identifier, command = operation['id'], operation['command']
        if operation['dispatched']:
            try:
                if operation['cancelRequested'] and not operation['cancelSent'] and queue.request_bridge_cancel(identifier):
                    receipt = self.bridge.cancel(identifier)
                else:
                    receipt = self.bridge.poll(identifier)
            except Exception:
                return queue.waiting(identifier, 'Unity is unavailable; waiting to reconcile its receipt')
            return queue.record_bridge(identifier, receipt)
        try:
            context = self.bridge.context()
        except ValueError as error:
            return queue.finish(identifier, 'needs-review', error=str(error))
        except Exception:
            return queue.waiting(identifier, 'Unity is offline; waiting to connect before dispatch')
        if not isinstance(context, dict) or context.get('projectId') != queue.project_id:
            return queue.finish(identifier, 'needs-review', error='The bridge belongs to another project. Review the operation target.')
        queue.reconcile_session(context)
        target = command['target']
        if any(context.get(key) != target[key] for key in ('sceneGuid', 'avatarId', 'avatarInstanceId', 'session')):
            return queue.finish(identifier, 'needs-review', error='The Unity session, scene or avatar changed. Review this operation.')
        if context.get('waitingReason'):
            return queue.waiting(identifier, str(context['waitingReason']))
        if not queue.dispatched(identifier, operation['executeRevision']):
            return queue.get(identifier)
        try:
            receipt = self.bridge.submit(command, execute_revision=operation['executeRevision'])
        except Exception:
            # POST may have reached Unity. The durable marker forbids resubmission.
            return queue.waiting(identifier, 'Dispatch outcome is unknown; waiting to reconcile the Unity receipt')
        return queue.record_bridge(identifier, receipt)
