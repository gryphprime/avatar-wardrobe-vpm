"""Unity archive source parser and immutable staging store.

This module is vendored from Avatar Wardrobe's proven ``Desktop/wardrobe_library.py``
implementation (preserved under its existing package licence).  Atelier intentionally
copies the implementation so it has no runtime dependency on Avatar Wardrobe being
installed or licensed; changes here should be kept compatible with the source parser's
archive-safety guarantees.
"""
import hashlib
import json
import os
import re
import threading
import time
from pathlib import Path, PurePosixPath
import shutil
import sqlite3
import stat
import tarfile
import tempfile
import zipfile

MAX_BYTES = 4 * 1024**3
MAX_FILES = 60000
MAX_DEPTH = 4
MAX_TEXT_BYTES = 32 * 1024**2
MAX_REFERENCE_BYTES = 256 * 1024**2
GUID_PATTERN = re.compile(rb'\bguid:\s*([a-fA-F0-9]{32})(?![a-fA-F0-9])')
BUILTIN_GUIDS = {'0' * 32} | {'0' * 16 + marker + '0' * 15 for marker in 'def'}
CODE_EXTENSIONS = {'.cs', '.dll', '.bundle', '.so', '.dylib', '.exe', '.jslib', '.jspre',
                   '.asmdef', '.asmref', '.rsp', '.a', '.aar', '.jar', '.winmd',
                   '.m', '.mm', '.c', '.cpp', '.cc', '.h'}
CODE_DIRECTORIES = {'.bundle', '.framework', '.plugin', '.androidlib'}


def is_sidecar(path):
    return any(part == '__MACOSX' or part == '.DS_Store' or part.startswith('._') for part in PurePosixPath(path).parts)


def asset_guid(path):
    """Unity's asset identity is a top-level field near the start of its meta file."""
    with Path(path).open('rb') as stream:
        prefix = stream.read(16384)
    values = re.findall(rb'^guid:([^\r\n]*)', prefix, re.MULTILINE)
    if not values:
        return ''
    if len(values) != 1 or not re.fullmatch(rb'\s*[a-fA-F0-9]{32}\s*', values[0]):
        raise ValueError('Invalid Unity asset identity in ' + str(path))
    return values[0].strip().decode('ascii').lower()


def project_identities(project, check=lambda: None):
    identities = {}
    for folder in ('Assets', 'Packages', 'Library/PackageCache'):
        location = project / folder
        if location.is_symlink() or not location.resolve().is_relative_to(project):
            continue
        for root, dirs, files in os.walk(location, followlinks=False):
            dirs[:] = sorted(name for name in dirs if not (Path(root) / name).is_symlink())
            for name in sorted(files):
                check()
                if not name.endswith('.meta'):
                    continue
                meta = Path(root) / name
                if meta.is_symlink() or not meta.is_file():
                    continue
                guid = asset_guid(meta)
                if guid:
                    identities.setdefault(guid, []).append(meta.with_suffix('').relative_to(project).as_posix())
    return identities


def references(path, allowance, check=lambda: None):
    """Read serialized text in bounded chunks; never interpret archive code."""
    path = Path(path)
    with path.open('rb') as stream:
        prefix = stream.read(512)
        if path.suffix != '.meta' and not prefix.lstrip(b'\xef\xbb\xbf \r\n').startswith((b'%YAML', b'--- !u!')):
            return set(), 0, False
        stream.seek(0)
        limit = min(MAX_TEXT_BYTES, allowance)
        found, tail, consumed = set(), b'', 0
        while consumed < limit:
            check()
            block = stream.read(min(1024 * 1024, limit - consumed))
            if not block:
                break
            consumed += len(block)
            text = tail + block
            found.update(value.decode('ascii').lower() for value in GUID_PATTERN.findall(text))
            tail = text[-80:]
        return found - BUILTIN_GUIDS, consumed, bool(stream.read(1))


def digest(path):
    h = hashlib.sha256()
    with open(path, 'rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            h.update(block)
    return h.hexdigest()


def safe_path(name):
    # Reject Windows drive/ADS paths as well, regardless of the host OS.
    if not name or '\\' in name or ':' in name or any(ord(character) < 32 for character in name):
        raise ValueError('Unsafe archive path: ' + repr(name))
    path = PurePosixPath(name)
    if path.is_absolute() or '..' in path.parts or any(p in ('', '.') or p != p.rstrip(' .') or re.fullmatch(r'(?i)(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\..*)?', p) for p in name.split('/')):
        raise ValueError('Unsafe archive path: ' + repr(name))
    return path


class Budget:
    def __init__(self):
        self.bytes = 0
        self.files = 0

    def take(self, size):
        self.bytes += size
        self.files += 1
        if size < 0 or self.bytes > MAX_BYTES or self.files > MAX_FILES:
            raise ValueError('Archive exceeds the extraction budget. Add a smaller product archive.')


def unpack(source, destination, budget=None, depth=0):
    """Validate before writing. Never follow archive links or extract into a project."""
    budget = budget or Budget()
    if depth > MAX_DEPTH:
        raise ValueError('Too many nested archives. Select the product archive inside the download.')
    destination = Path(destination)
    destination.mkdir(parents=True, exist_ok=True)
    if zipfile.is_zipfile(source):
        with zipfile.ZipFile(source) as archive:
            entries, seen = [], set()
            for info in archive.infolist():
                name = info.filename.rstrip('/')
                path = safe_path(name)
                mode = info.external_attr >> 16
                if stat.S_IFMT(mode) not in (0, stat.S_IFREG, stat.S_IFDIR):
                    raise ValueError('Archive links and special files are not supported: ' + name)
                key = str(path).casefold()
                if key in seen:
                    raise ValueError('Duplicate archive path: ' + name)
                seen.add(key)
                budget.take(info.file_size)
                if info.is_dir():
                    continue
                entries.append((info, destination / str(path)))
            for info, output in entries:
                output.parent.mkdir(parents=True, exist_ok=True)
                with archive.open(info) as stream, output.open('xb') as target:
                    shutil.copyfileobj(stream, target, 1024 * 1024)
                if output.suffix.lower() in ('.zip', '.unitypackage') and not is_sidecar(output.relative_to(destination).as_posix()):
                    unpack(output, output.with_name(output.name + '.contents'), budget, depth + 1)
        return
    # unitypackage is a gzip tar of GUID folders with pathname / asset / asset.meta.
    try:
        archive = tarfile.open(source, 'r:*')
    except tarfile.TarError as error:
        raise ValueError('Use a purchased ZIP or unitypackage file.') from error
    with archive:
        members = {}
        for member in archive:
            path = safe_path(member.name.rstrip('/'))
            if not (member.isfile() or member.isdir()):
                raise ValueError('Archive links and special files are not supported.')
            budget.take(member.size)
            key = str(path).casefold()
            if key in members:
                raise ValueError('Duplicate unitypackage entry.')
            members[key] = member
            if member.isdir():
                continue
        targets, seen = [], set()
        for key, member in members.items():
            if not key.endswith('/pathname'):
                continue
            if member.size > 4096:
                raise ValueError('Invalid unitypackage pathname.')
            name = archive.extractfile(member).read().decode('utf-8-sig').rstrip('\r\n')
            target = safe_path(name)
            if target.parts[0] != 'Assets':
                raise ValueError('Unitypackage must contain only project Assets paths.')
            prefix = key.rsplit('/', 1)[0]
            for suffix, leaf in (('asset', str(target)), ('asset.meta', str(target) + '.meta')):
                data = members.get(prefix + '/' + suffix)
                if data is None:
                    continue
                if leaf.casefold() in seen:
                    raise ValueError('Conflicting unitypackage asset path: ' + leaf)
                seen.add(leaf.casefold())
                targets.append((data, destination / leaf))
        if not targets:
            raise ValueError('This archive has no importable Unity assets.')
        for member, output in targets:
            output.parent.mkdir(parents=True, exist_ok=True)
            with archive.extractfile(member) as stream, output.open('xb') as target:
                shutil.copyfileobj(stream, target, 1024 * 1024)


class Library:
    def __init__(self, root):
        self.write_lock = threading.RLock()
        self.root = Path(root).expanduser().resolve()
        self.root.mkdir(parents=True, exist_ok=True)
        self.db = self.root / 'library.sqlite3'
        with self.connect() as db:
            db.executescript('''
                CREATE TABLE IF NOT EXISTS products (
                    hash TEXT PRIMARY KEY, filename TEXT, creator TEXT, product TEXT,
                    source_url TEXT, added TEXT DEFAULT CURRENT_TIMESTAMP,
                    manifest TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS usage (
                    hash TEXT, project TEXT, avatar TEXT, instance TEXT, paths TEXT,
                    PRIMARY KEY(hash, project, avatar, instance));
                CREATE TABLE IF NOT EXISTS instance_usage (
                    hash TEXT, project TEXT, avatar_id TEXT, instance_id TEXT,
                    avatar TEXT, instance TEXT, scene TEXT, session TEXT,
                    guid TEXT, source_sha256 TEXT, dependency_hash TEXT, match TEXT,
                    observed REAL, PRIMARY KEY(hash, project, avatar_id, instance_id));
            ''')

    def connect(self):
        connection = sqlite3.connect(self.db, timeout=30)
        connection.row_factory = sqlite3.Row
        return connection

    @staticmethod
    def metadata(creator, product, source_url):
        for value, limit in ((creator, 200), (product, 200), (source_url, 2048)):
            if not isinstance(value, str) or len(value) > limit or any(ord(c) < 32 for c in value):
                raise ValueError('Product details exceed their text limits or contain control characters.')
        from urllib.parse import urlsplit
        if source_url:
            url = urlsplit(source_url)
            if url.scheme not in ('https', 'http') or not url.hostname or url.username or url.password:
                raise ValueError('Use an HTTP or HTTPS creator page without embedded credentials.')
        return creator.strip(), product.strip(), source_url.strip()

    def update_metadata(self, sha, creator='', product='', source_url=''):
        creator, product, source_url = self.metadata(creator, product, source_url)
        with self.write_lock, self.connect() as db:
            row = db.execute('SELECT filename FROM products WHERE hash=?', (sha,)).fetchone()
            if row is None:
                raise ValueError('Library version not found. Refresh your library.')
            db.execute('UPDATE products SET creator=?,product=?,source_url=? WHERE hash=?',
                       (creator, product or row['filename'], source_url, sha))
        return {'ok': 1, 'message': 'Product details saved locally. Original files are unchanged.'}

    def add(self, source, creator='', product='', source_url=''):
        with self.write_lock:
            return self._add(source, creator, product, source_url)

    def _add(self, source, creator='', product='', source_url=''):
        creator, product, source_url = self.metadata(creator, product, source_url)
        source = Path(source).expanduser().resolve()
        if not source.is_file() or source.stat().st_size > MAX_BYTES:
            raise ValueError('Select a product archive smaller than 4 GiB.')
        sha = digest(source)
        with self.connect() as db:
            if db.execute('SELECT 1 FROM products WHERE hash=?', (sha,)).fetchone():
                return {'hash': sha, 'duplicate': True}
        versions = self.root / 'versions'
        versions.mkdir(exist_ok=True)
        stage = Path(tempfile.mkdtemp(prefix='.incoming-', dir=versions))
        final = versions / sha
        try:
            original = stage / 'original'
            shutil.copyfile(source, original)
            if digest(original) != sha:
                raise ValueError('The download changed while it was being added. Try again.')
            try:
                unpack(original, stage / 'files')
            except (tarfile.TarError, zipfile.BadZipFile, RuntimeError, NotImplementedError, UnicodeError) as error:
                raise ValueError('The archive is damaged, encrypted, or uses an unsupported format.') from error
            files = [p for p in (stage / 'files').rglob('*') if p.is_file()]
            manifest = []
            for file in sorted(files):
                guid = ''
                meta = file if file.suffix == '.meta' else file.with_name(file.name + '.meta')
                if meta.is_file():
                    guid = asset_guid(meta)
                manifest.append({'path': file.relative_to(stage / 'files').as_posix(),
                                 'sha256': digest(file), 'guid': guid, 'size': file.stat().st_size})
            # A content-addressed directory is immutable. Project edits always use copies.
            if not final.exists():
                stage.rename(final)
            for file in final.rglob('*'):
                if file.is_file():
                    file.chmod(0o444)
            with self.connect() as db:
                db.execute('INSERT OR IGNORE INTO products(hash,filename,creator,product,source_url,manifest) VALUES(?,?,?,?,?,?)',
                           (sha, source.name, creator, product or source.stem, source_url, json.dumps(manifest)))
            return {'hash': sha, 'duplicate': False, 'files': len(manifest)}
        finally:
            if stage.exists():
                shutil.rmtree(stage)

    def list(self):
        with self.connect() as db:
            rows = db.execute('SELECT * FROM products ORDER BY added DESC, hash').fetchall()
            result = []
            for row in rows:
                value = dict(row)
                value['files'] = json.loads(value.pop('manifest'))
                value['usage'] = [dict(x) for x in db.execute('SELECT project,avatar,instance,paths FROM usage WHERE hash=?', (row['hash'],))]
                value['usage'].extend(dict(x) for x in db.execute('SELECT * FROM instance_usage WHERE hash=?', (row['hash'],)))
                result.append(value)
            return result

    def record(self, sha):
        with self.connect() as db:
            row = db.execute('SELECT * FROM products WHERE hash=?', (sha,)).fetchone()
            if row is None:
                raise ValueError('Library version not found. Refresh your library.')
            value = dict(row)
            value['files'] = json.loads(value.pop('manifest'))
            return value

    def import_plan(self, sha, project, cancel_check=None):
        with self.write_lock:
            return self._import_plan(sha, project, cancel_check or (lambda: None))

    def _import_plan(self, sha, project, check):
        """An explicit copy plan, preserving original Unity paths and GUIDs."""
        record = self.record(sha)
        project = Path(project).resolve()
        if not (project / 'ProjectSettings/ProjectVersion.txt').is_file():
            raise ValueError('Choose an existing Unity project.')
        result, used, identities = [], {}, {}
        for entry in record['files']:
            check()
            if is_sidecar(entry['path']):
                continue
            parts = PurePosixPath(entry['path']).parts
            if 'Assets' not in parts:
                continue
            relative = PurePosixPath(*parts[parts.index('Assets'):])
            if len(relative.parts) < 2:
                raise ValueError('The product cannot replace the project Assets folder with a file.')
            # Editor scripts execute during import and need an explicit review.
            kind = 'code' if relative.suffix.lower() in CODE_EXTENSIONS or any(PurePosixPath(part).suffix.lower() in CODE_DIRECTORIES for part in relative.parts) else 'asset'
            key = str(relative).casefold()
            if key in used:
                if used[key]['sha256'] == entry['sha256'] and used[key]['guid'] == entry['guid']:
                    continue  # Identical copies bundled twice are one project asset.
                raise ValueError('Several nested packages write the same project path. Add the desired nested package separately.')
            used[key] = entry
            if entry['guid']:
                asset_path = str(relative).removesuffix('.meta')
                previous = identities.setdefault(entry['guid'], asset_path)
                if previous != asset_path:
                    raise ValueError('Duplicate Unity asset identity at ' + asset_path + ' and ' + previous)
            target = project / str(relative)
            self.validate_destination(project, target)
            state = 'new' if not target.exists() else ('unchanged' if target.is_file() and digest(target) == entry['sha256'] else 'conflict')
            folder = False
            if relative.suffix == '.meta':
                source = self.root / 'versions' / sha / 'files' / entry['path']
                with source.open('rb') as stream:
                    folder = bool(re.search(rb'^folderAsset:\s*yes\s*$', stream.read(16384), re.MULTILINE))
                if folder:
                    folder_target = target.with_suffix('')
                    self.validate_destination(project, folder_target)
                    if folder_target.exists() and not folder_target.is_dir():
                        state = 'conflict'
            result.append(dict(entry, destination=str(relative), state=state, kind=kind, folderAsset=folder))
        if not result:
            raise ValueError('No Assets folder was found. Add the product unitypackage or a ZIP containing its Assets folder.')
        mapped = {entry['destination'].casefold() for entry in result}
        for entry in result:
            if any(str(parent).casefold() in mapped for parent in PurePosixPath(entry['destination']).parents):
                raise ValueError('A product asset is also used as a destination folder: ' + entry['destination'])
        existing = project_identities(project, check)
        collisions = [{'guid': guid, 'destination': destination,
                       'existingPaths': sorted(path for path in existing.get(guid, []) if path != destination)}
                      for guid, destination in sorted(identities.items())
                      if any(path != destination for path in existing.get(guid, []))]
        available = set(existing) | set(identities)
        missing, incomplete, allowance = {}, [], MAX_REFERENCE_BYTES
        for entry in result:
            check()
            source = self.root / 'versions' / sha / 'files' / entry['path']
            found, consumed, partial = references(source, allowance, check)
            allowance -= consumed
            if partial:
                incomplete.append(entry['destination'])
            for guid in found - available:
                missing.setdefault(guid, []).append(entry['destination'])
        return {'hash': sha, 'project': str(project), 'files': result,
                'conflicts': sum(x['state'] == 'conflict' for x in result),
                'codeFiles': sum(x['kind'] == 'code' for x in result),
                'guidConflicts': collisions,
                'missingDependencies': [{'guid': guid, 'referencedBy': sorted(paths)} for guid, paths in sorted(missing.items())],
                'dependencyScanIncomplete': incomplete}

    @staticmethod
    def validate_destination(project, target):
        if not target.is_relative_to(project):
            raise ValueError('The destination is outside the project.')
        for path in (target, *target.parents):
            if path == project:
                break
            if path.is_symlink():
                raise ValueError('The destination uses a link: ' + target.relative_to(project).as_posix())
            if path != target and path.exists() and not path.is_dir():
                raise ValueError('A destination folder is an existing file: ' + path.relative_to(project).as_posix())

    def apply_import(self, sha, project, expected_plan, cancel_check=None):
        with self.write_lock:
            return self._apply_import(sha, project, expected_plan, cancel_check)

    def _apply_import(self, sha, project, expected_plan, cancel_check=None):
        check = cancel_check or (lambda: None)
        check()
        project = Path(project).resolve()
        current = self.import_plan(sha, project, cancel_check)
        check()
        if current != expected_plan:
            raise ValueError('The project changed since review. Review the import again.')
        if current['conflicts']:
            raise ValueError('Existing files differ. Keep the current project version or resolve the listed conflicts in a project copy.')
        if current['guidConflicts']:
            raise ValueError('GUID already exists at another path: ' + ', '.join(current['guidConflicts'][0]['existingPaths']))
        written, created_dirs = [], []
        staging = tempfile.TemporaryDirectory(prefix='wardrobe-import-')
        try:
            pending = [entry for entry in current['files'] if entry['state'] == 'new']
            for index, entry in enumerate(pending):
                check()
                source = self.root / 'versions' / sha / 'files' / entry['path']
                staged = Path(staging.name) / str(index)
                with source.open('rb') as stream, staged.open('xb') as target:
                    self.copy_checked(stream, target, check)
                if digest(staged) != entry['sha256']:
                    raise ValueError('Library content changed. Restore the original archive before importing.')
                entry['staged'] = staged
            # Folder metadata may already be present while its empty folder is absent.
            for entry in current['files']:
                check()
                if entry['folderAsset']:
                    folder = project / entry['destination'].removesuffix('.meta')
                    self.validate_destination(project, folder)
                    missing = []
                    while folder != project and not folder.exists():
                        missing.append(folder)
                        folder = folder.parent
                    for folder in reversed(missing):
                        folder.mkdir()
                        created_dirs.append(folder)
            for entry in sorted(pending, key=lambda entry: not entry['destination'].endswith('.meta')):
                check()
                if entry['state'] != 'new':
                    continue
                source = entry['staged']
                target = Path(project) / entry['destination']
                self.validate_destination(project, target)
                folders = []
                folder = target.with_suffix('') if entry['folderAsset'] else target.parent
                while folder != project and not folder.exists():
                    folders.append(folder)
                    folder = folder.parent
                for folder in reversed(folders):
                    folder.mkdir()
                    created_dirs.append(folder)
                with source.open('rb') as stream, target.open('xb') as out:
                    written.append(target)
                    if cancel_check:
                        self.copy_checked(stream, out, check)
                    else:
                        shutil.copyfileobj(stream, out, 1024 * 1024)
            check()
            with self.connect() as db:
                db.execute('INSERT OR REPLACE INTO usage VALUES(?,?,?,?,?)', (sha, str(Path(project).resolve()), '', '', json.dumps([x['destination'] for x in current['files']])))
        except Exception:
            # We only remove files this call successfully created; originals/shared files survive.
            for target in reversed(written):
                target.unlink(missing_ok=True)
            for folder in reversed(created_dirs):
                try:
                    folder.rmdir()
                except OSError:
                    pass  # Preserve any unrelated file created concurrently.
            raise
        finally:
            staging.cleanup()
        return {'ok': 1, 'copied': len(written), 'missingDependencies': current['missingDependencies'],
                'message': 'Files copied. Unity must import them and resolve dependencies before Wear.'}

    @staticmethod
    def copy_checked(source, destination, check):
        while True:
            check()
            block = source.read(1024 * 1024)
            if not block:
                break
            destination.write(block)

    def reconcile_usage(self, project, snapshot):
        """Record only a complete live bridge observation, never a cached browser claim.

        Matching prefab bytes establish prefab provenance, not the entire product's
        dependency version. GUID-only matches remain explicitly unconfirmed.
        """
        project = str(Path(project).resolve())
        if not isinstance(snapshot, dict) or snapshot.get('projectId') != project:
            return False
        if snapshot.get('usageComplete') is not True:
            return False
        def text(value, limit=2048):
            return isinstance(value, str) and len(value) <= limit and not any(ord(c) < 32 for c in value)
        avatar_id = snapshot.get('avatarId')
        if not text(avatar_id) or not avatar_id or not text(snapshot.get('avatarName')) or not text(snapshot.get('scene')) or not text(snapshot.get('session'), 128):
            return False
        items = snapshot.get('usage')
        if not isinstance(items, list) or len(items) > 8192:
            return False
        seen = set()
        for item in items:
            if (not isinstance(item, dict) or not text(item.get('instanceId')) or not item['instanceId'] or
                    item['instanceId'] in seen or not text(item.get('path')) or
                    not isinstance(item.get('guid'), str) or not re.fullmatch('[a-f0-9]{32}', item['guid']) or
                    not isinstance(item.get('sourceSha256'), str) or not re.fullmatch('(?:[a-f0-9]{64})?', item['sourceSha256']) or
                    not isinstance(item.get('dependencyHash'), str) or not re.fullmatch('[a-f0-9]{32}', item['dependencyHash'])):
                return False
            seen.add(item['instanceId'])
        with self.write_lock, self.connect() as db:
            candidates = {}
            for row in db.execute('SELECT hash,manifest FROM products'):
                for asset in json.loads(row['manifest']):
                    if asset['guid'] and asset['path'].lower().endswith('.prefab'):
                        candidates.setdefault(asset['guid'], set()).add((row['hash'], asset['sha256']))
            rows = []
            observed = time.time()
            for item in items:
                for archive, sha in candidates.get(item['guid'], ()):
                    match = 'prefab-bytes' if sha == item['sourceSha256'] else 'guid-only'
                    rows.append((archive, project, avatar_id, item['instanceId'], snapshot['avatarName'], item['path'],
                                 snapshot['scene'], snapshot['session'], item['guid'], item['sourceSha256'], item['dependencyHash'], match, observed))
            db.execute('DELETE FROM instance_usage WHERE project=? AND avatar_id=?', (project, avatar_id))
            db.executemany('INSERT OR REPLACE INTO instance_usage VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?)', rows)
        return True

    def update_impact(self, old_hash, new_hash):
        old, new = self.record(old_hash), self.record(new_hash)
        before = {x['path']: x for x in old['files']}
        after = {x['path']: x for x in new['files']}
        with self.connect() as db:
            usage = [dict(x) for x in db.execute('SELECT * FROM usage WHERE hash=?', (old_hash,))]
            usage.extend(dict(x) for x in db.execute('SELECT * FROM instance_usage WHERE hash=?', (old_hash,)))
        return {'changed': sorted(p for p in before.keys() & after.keys() if before[p]['sha256'] != after[p]['sha256']),
                'removed': sorted(before.keys() - after.keys()), 'added': sorted(after.keys() - before.keys()),
                'guidChanges': sorted(p for p in before.keys() & after.keys() if before[p]['guid'] != after[p]['guid']),
                'usage': usage, 'automaticUpdate': False}
