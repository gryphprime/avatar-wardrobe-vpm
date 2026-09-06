"""Incremental source fingerprints. Advisory YAML references, not Unity build reachability."""
import contextlib
import hashlib
import json
import os
import re
import tempfile

GUID_RE = re.compile(rb"\bguid:\s*([0-9a-fA-F]{32})\b")
TEXT_TYPES = {".prefab", ".mat", ".asset", ".controller", ".overridecontroller",
              ".anim", ".unity", ".meta", ".shader", ".shadergraph", ".shadersubgraph"}

def atomic_json(path, value, unchanged_ok=False):
    data = json.dumps(value, ensure_ascii=False, separators=(",", ":"), sort_keys=True).encode("utf-8")
    if unchanged_ok:
        try:
            with open(path, "rb") as stream:
                if stream.read() == data:
                    return
        except FileNotFoundError:
            pass
    os.makedirs(os.path.dirname(path), exist_ok=True)
    fd, tmp = tempfile.mkstemp(prefix=".wardrobe-", dir=os.path.dirname(path))
    try:
        with os.fdopen(fd, "wb") as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(tmp, path)
    finally:
        if os.path.exists(tmp):
            os.unlink(tmp)

def read_json(path, default):
    try:
        with open(path, encoding="utf-8") as stream:
            return json.load(stream)
    except (OSError, ValueError):
        return default

def file_digest(path):
    try:
        with open(path, "rb") as stream:
            return hashlib.sha256(stream.read()).hexdigest()
    except FileNotFoundError:
        return ""

def walk_assets(top):
    # Follow linked creator folders, but never recurse forever through a link cycle.
    # A physical folder is visited once per scan root.
    visited = set()
    for directory, dirs, files in os.walk(top, followlinks=True):
        physical = os.path.normcase(os.path.realpath(directory))
        if physical in visited:
            dirs[:] = []
            continue
        visited.add(physical)
        dirs[:] = sorted(d for d in dirs if not d.startswith(".") and d not in {"__pycache__", "node_modules"})
        yield directory, dirs, sorted(files)

@contextlib.contextmanager
def index_lock(path):
    """OS-owned lock: crashes release it and stale lock files are harmless."""
    with open(path, "a+b") as stream:
        stream.seek(0)
        if os.name == "nt":
            import msvcrt
            if not stream.read(1):
                stream.write(b"0"); stream.flush()
            stream.seek(0)
            try:
                msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
            except OSError as exc:
                raise RuntimeError("An indexer is already running for this project.") from exc
        else:
            import fcntl
            try:
                fcntl.flock(stream, fcntl.LOCK_EX | fcntl.LOCK_NB)
            except OSError as exc:
                raise RuntimeError("An indexer is already running for this project.") from exc
        try:
            yield
        finally:
            if os.name == "nt":
                stream.seek(0); msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                fcntl.flock(stream, fcntl.LOCK_UN)

class InputSnapshot:
    def __init__(self, root, guid_map, path, full=False):
        self.root, self.guid_map, self.path = root, guid_map, path
        previous = {} if full else read_json(path, {})
        self.old = previous if isinstance(previous, dict) else {}
        self.files = {}

    def file(self, relative):
        if relative in self.files:
            return self.files[relative]
        path = os.path.join(self.root, relative)
        try:
            stat = os.stat(path)
            stamp = [str(stat.st_mtime_ns), stat.st_size]
            old = self.old.get(relative)
            if isinstance(old, dict) and old.get("stamp") == stamp and "hash" in old and isinstance(old.get("refs"), list):
                entry = old
            else:
                digest = hashlib.sha256()
                refs = set()
                # Text Unity objects need references; binary models/textures are hashed in chunks.
                with open(path, "rb") as stream:
                    if os.path.splitext(path)[1].lower() in TEXT_TYPES:
                        data = stream.read()
                        digest.update(data)
                        refs = {g.decode("ascii").lower() for g in GUID_RE.findall(data)}
                    else:
                        for block in iter(lambda: stream.read(1024 * 1024), b""):
                            digest.update(block)
                entry = {"stamp": stamp, "hash": digest.hexdigest(), "refs": sorted(refs)}
        except FileNotFoundError:
            entry = {"stamp": None, "hash": "missing", "refs": []}
        self.files[relative] = entry
        return entry

    def fingerprint(self, relative):
        seen, pending, unresolved = set(), [relative, relative + ".meta"], set()
        while pending:
            path = pending.pop()
            if path in seen:
                continue
            seen.add(path)
            entry = self.file(path)
            for guid in entry["refs"]:
                resolved = self.guid_map.get(guid)
                if resolved:
                    pending.extend([resolved, resolved + ".meta"])
                else:
                    unresolved.add(guid)
        digest = hashlib.sha256()
        for path in sorted(seen):
            digest.update((path + "\0" + self.file(path)["hash"] + "\n").encode("utf-8"))
        digest.update("|".join(sorted(unresolved)).encode("ascii"))
        return digest.hexdigest(), sorted(seen - {relative, relative + ".meta"})

    def commit(self):
        # A scan racing with a save must not publish inconsistent records as current.
        for relative, entry in self.files.items():
            try:
                stat = os.stat(os.path.join(self.root, relative))
                stamp = [str(stat.st_mtime_ns), stat.st_size]
            except FileNotFoundError:
                stamp = None
            if stamp != entry["stamp"]:
                raise RuntimeError("Asset changed during indexing; retry: " + relative)
        atomic_json(self.path, self.files, unchanged_ok=True)
