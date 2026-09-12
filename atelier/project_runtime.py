"""Process and ownership management for Atelier's private Unity worker.

The desktop host is intentionally disposable. Unity can take a long time to
start and it must continue to own a project when the host is restarted, so the
worker contract is persisted below ``<data>/workers/<workspace>`` as well as in
the project ownership lock. A new host never trusts a PID on its own: it
adopts an existing process only after an authenticated bridge context identifies
the exact requested project. PID start times are used before sending a signal
to a process that was adopted from another host.
"""
from __future__ import annotations

import contextlib
import ctypes
import hashlib
import json
import os
import secrets
import shutil
import signal
import socket
import subprocess
import sys
import tempfile
import threading
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Dict, Iterator, Optional, Tuple


class ProjectLockedError(RuntimeError):
    """Unity, or another process, currently owns the project."""


class ProjectOwnedError(RuntimeError):
    """A second Atelier worker was requested for the same project."""


@dataclass(frozen=True)
class ProjectInfo:
    path: str
    unity_version: Optional[str]
    packages: dict
    package_notes: tuple = ()

    def to_dict(self):
        value = {"path": self.path, "unityVersion": self.unity_version, "packages": self.packages}
        if self.package_notes:
            value["packageNotes"] = list(self.package_notes)
        return value


def inspect_project(path) -> ProjectInfo:
    root = Path(path).expanduser().resolve()
    if not (root / "ProjectSettings").is_dir():
        raise ValueError(f"Not a Unity project: {root}")
    version = None
    version_file = root / "ProjectSettings" / "ProjectVersion.txt"
    if version_file.exists():
        for line in version_file.read_text(errors="replace").splitlines():
            if line.startswith("m_EditorVersion:"):
                version = line.partition(":")[2].strip()
                break
    manifest = root / "Packages" / "manifest.json"
    notes = []
    try:
        manifest_data = json.loads(manifest.read_text()) if manifest.exists() else {}
        manifest_dependencies = manifest_data.get("dependencies", {}) if isinstance(manifest_data, dict) else {}
        if not isinstance(manifest_dependencies, dict):
            raise TypeError("dependencies is not an object")
        packages = {}
        for name, requested in manifest_dependencies.items():
            if isinstance(name, str) and isinstance(requested, str) and len(name) <= 128 and len(requested) <= 128:
                packages[name] = requested
            else:
                notes.append("Packages/manifest.json: ignored an invalid dependency entry.")
    except (OSError, UnicodeDecodeError, json.JSONDecodeError, TypeError):
        packages = {}
        notes.append("Packages/manifest.json is invalid; installed package versions may be incomplete.")
    # VPM keeps its desired/locked dependency graph beside the Unity manifest
    # in different VCC/vrc-get releases. Keep exact string versions where they
    # are available, while retaining a bounded note for malformed files.
    for candidate in (root / "vpm-manifest.json", root / "Packages" / "vpm-manifest.json"):
        if not candidate.exists():
            continue
        try:
            if candidate.stat().st_size > 512 * 1024:
                raise ValueError("too large")
            value = json.loads(candidate.read_text())
            if not isinstance(value, dict):
                raise ValueError("manifest is not an object")
            graphs = [value.get(key) for key in ("dependencies", "packages", "locked", "lock", "resolvedDependencies")]
            graphs = [graph for graph in graphs if graph is not None]
            if not graphs:
                raise ValueError("manifest has no dependency graph")
            for dependencies in graphs:
                if not isinstance(dependencies, dict):
                    notes.append(f"{candidate.name}: ignored a malformed dependency graph.")
                    continue
                for name, version in dependencies.items():
                    if isinstance(version, dict):
                        version = version.get("version") or version.get("resolved")
                    if isinstance(name, str) and isinstance(version, str) and len(name) <= 128 and len(version) <= 128:
                        packages[name] = version
                    else:
                        notes.append(f"{candidate.name}: ignored an invalid dependency entry.")
        except (OSError, UnicodeDecodeError, json.JSONDecodeError, TypeError, ValueError):
            notes.append(f"{candidate.name}: invalid or oversized VPM manifest.")
    # A package directory's package.json is the strongest evidence of what is
    # physically installed. Read only direct package directories and bound both
    # count and file size so project inspection stays cheap and local.
    packages_root = root / "Packages"
    embedded_names = set()
    try:
        package_dirs = [entry for entry in packages_root.iterdir() if entry.is_dir()][:512]
    except OSError:
        package_dirs = []
    for package_dir in package_dirs:
        package_json = package_dir / "package.json"
        if not package_json.is_file():
            continue
        try:
            if package_json.stat().st_size > 256 * 1024:
                raise ValueError("too large")
            value = json.loads(package_json.read_text())
            name, installed = value.get("name"), value.get("version")
            if isinstance(name, str) and isinstance(installed, str) and len(name) <= 128 and len(installed) <= 128:
                packages[name] = installed
                embedded_names.add(name)
            else:
                notes.append(f"Packages/{package_dir.name}/package.json: missing bounded name/version.")
        except (OSError, UnicodeDecodeError, json.JSONDecodeError, TypeError, ValueError):
            notes.append(f"Packages/{package_dir.name}/package.json: invalid or oversized package metadata.")
    # Unity records resolved package versions separately. Keep manifest values
    # when present, but expose a resolved version for package UI when available.
    lock = root / "Packages" / "packages-lock.json"
    if lock.exists():
        try:
            resolved = json.loads(lock.read_text()).get("dependencies", {})
            if not isinstance(resolved, dict):
                raise TypeError("dependencies is not an object")
            for name, value in resolved.items():
                if isinstance(name, str) and len(name) <= 128 and isinstance(value, dict) and isinstance(value.get("version"), str) and len(value["version"]) <= 128:
                    if value.get("source") == "embedded" and name not in embedded_names:
                        # A stale packages-lock entry can survive package
                        # removal until Unity refreshes its cache. Embedded
                        # packages are installed only when their directory is
                        # physically present.
                        notes.append(f"{name}: packages-lock.json lists an embedded package whose directory is missing.")
                        continue
                    packages.setdefault(name, value["version"])
        except (OSError, UnicodeDecodeError, json.JSONDecodeError, TypeError):
            notes.append("Packages/packages-lock.json: invalid or incomplete resolved metadata.")
    return ProjectInfo(str(root), version, packages, tuple(notes[:32]))


def _version_key(value: str):
    parts = []
    for piece in str(value).replace("-", ".").split("."):
        number = "".join(character for character in piece if character.isdigit())
        parts.append((0, int(number)) if number else (1, piece.lower()))
    return tuple(parts)


def _project_version(project) -> Optional[str]:
    try:
        return inspect_project(project).unity_version
    except (OSError, ValueError, json.JSONDecodeError):
        return None


def _path_candidates_from_root(root: Path):
    """Yield Unity executables below a Hub/editor installation root."""
    if not root.exists() or not root.is_dir():
        return
    for version_dir in sorted((entry for entry in root.iterdir() if entry.is_dir()), key=lambda p: _version_key(p.name), reverse=True):
        names = (
            version_dir / "Unity.app" / "Contents" / "MacOS" / "Unity",
            version_dir / "Editor" / "Unity.exe",
            version_dir / "Editor" / "Unity",
            version_dir / "Unity",
        )
        for candidate in names:
            if candidate.is_file():
                yield candidate


def discover_unity(project=None, version=None) -> Optional[str]:
    """Find a Unity editor matching a project on macOS, Windows, or Linux."""
    explicit = os.environ.get("UNITY_PATH")
    if explicit and Path(explicit).expanduser().is_file():
        return str(Path(explicit).expanduser())
    desired = version or (_project_version(project) if project else None)
    roots = []
    for variable in ("UNITY_EDITOR_ROOT", "UNITY_HUB_EDITOR_ROOT", "UNITY_INSTALL_ROOT"):
        value = os.environ.get(variable)
        if value:
            roots.append(Path(value).expanduser())
    home = Path.home()
    if sys.platform == "darwin":
        roots.extend((Path("/Applications/Unity/Hub/Editor"), home / "Applications" / "Unity" / "Hub" / "Editor"))
    elif os.name == "nt":
        roots.extend((
            Path(os.environ.get("PROGRAMFILES", r"C:\\Program Files")) / "Unity Hub" / "Editor",
            Path(os.environ.get("PROGRAMFILES", r"C:\\Program Files")) / "Unity" / "Hub" / "Editor",
            Path(os.environ.get("LOCALAPPDATA", str(home / "AppData" / "Local"))) / "Programs" / "Unity Hub" / "Editor",
        ))
    else:
        roots.extend((home / "Unity" / "Hub" / "Editor", Path("/opt/Unity/Hub/Editor"), Path("/opt/Unity/Editor")))
    candidates, seen = [], set()
    for root in roots:
        try:
            values = _path_candidates_from_root(root)
            if values:
                for candidate in values:
                    resolved = str(candidate.resolve())
                    if resolved not in seen:
                        seen.add(resolved)
                        candidates.append(candidate)
        except OSError:
            continue
    if desired:
        for candidate in candidates:
            if any(parent.name == desired for parent in (candidate,) + tuple(candidate.parents)):
                return str(candidate)
    if candidates:
        return str(candidates[0])
    return shutil.which("unity") or shutil.which("Unity")


def _unity_is_locked(root: Path) -> bool:
    """Return whether Unity's live lock exists; never remove this file."""
    root = Path(root)
    return any((root / relative / "UnityLockfile").exists() for relative in ("Temp", "Library"))


_locked = _unity_is_locked


def _fsync_file(handle):
    handle.flush()
    try:
        os.fsync(handle.fileno())
    except OSError:
        pass


def _atomic_json(path: Path, value: dict, mode=0o600):
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, temporary_name = tempfile.mkstemp(prefix="." + path.name + ".", suffix=".tmp", dir=str(path.parent))
    temporary = Path(temporary_name)
    try:
        os.fchmod(fd, mode)
        with os.fdopen(fd, "w", encoding="utf-8") as handle:
            fd = None
            json.dump(value, handle, sort_keys=True, separators=(",", ":"))
            handle.write("\n")
            _fsync_file(handle)
        os.replace(str(temporary), str(path))
    finally:
        if fd is not None:
            os.close(fd)
        try:
            temporary.unlink()
        except FileNotFoundError:
            pass


def _atomic_bytes(path: Path, contents: bytes, mode=0o600):
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, temporary_name = tempfile.mkstemp(prefix="." + path.name + ".", suffix=".tmp", dir=str(path.parent))
    temporary = Path(temporary_name)
    try:
        os.fchmod(fd, mode)
        with os.fdopen(fd, "wb") as handle:
            fd = None
            handle.write(contents)
            _fsync_file(handle)
        os.replace(str(temporary), str(path))
    finally:
        if fd is not None:
            os.close(fd)
        try:
            temporary.unlink()
        except FileNotFoundError:
            pass


def _lock_handle(handle, blocking=False):
    """Acquire an advisory lock without replacing the lock-file inode."""
    if os.name == "nt":
        import msvcrt
        handle.seek(0)
        try:
            if handle.tell() == 0 and os.fstat(handle.fileno()).st_size == 0:
                handle.write(b"0")
                handle.flush()
                handle.seek(0)
            msvcrt.locking(handle.fileno(), msvcrt.LK_LOCK if blocking else msvcrt.LK_NBLCK, 1)
        except OSError:
            return False
        return True
    import fcntl
    flags = fcntl.LOCK_EX | (0 if blocking else fcntl.LOCK_NB)
    try:
        fcntl.flock(handle.fileno(), flags)
    except OSError:
        return False
    return True


def _unlock_handle(handle):
    try:
        if os.name == "nt":
            import msvcrt
            handle.seek(0)
            msvcrt.locking(handle.fileno(), msvcrt.LK_UNLCK, 1)
        else:
            import fcntl
            fcntl.flock(handle.fileno(), fcntl.LOCK_UN)
    except (OSError, ValueError):
        pass


def _read_lock(handle) -> dict:
    try:
        handle.seek(0)
        raw = handle.read().decode("utf-8")
        return json.loads(raw) if raw.strip() else {}
    except (OSError, UnicodeDecodeError, json.JSONDecodeError):
        return {}
    finally:
        try:
            handle.seek(0)
        except OSError:
            pass


def _write_lock(handle, record: dict):
    """Write metadata in-place while holding the advisory lock.

    Replacing this inode would silently defeat flock on POSIX: another host
    could open the replacement while the first host still held the old inode.
    """
    encoded = (json.dumps(record, sort_keys=True, separators=(",", ":")) + "\n").encode("utf-8")
    handle.seek(0)
    handle.truncate(0)
    handle.write(encoded)
    handle.flush()
    try:
        os.fsync(handle.fileno())
    except OSError:
        pass


@contextlib.contextmanager
def _project_lock(root: Path) -> Iterator[Tuple[Any, dict]]:
    """Hold the durable project lock for a short project-file operation."""
    path = root / ".atelier" / "unity-worker.lock"
    path.parent.mkdir(parents=True, exist_ok=True)
    handle = path.open("a+b")
    if not _lock_handle(handle):
        handle.close()
        raise ProjectOwnedError(f"Project already owned: {root}")
    try:
        yield handle, _read_lock(handle)
    finally:
        _unlock_handle(handle)
        handle.close()


def _bridge_context_for_record(record: dict, project: Path, timeout=0.25):
    config = record.get("config") if isinstance(record, dict) else None
    if not isinstance(config, dict):
        return None, None
    try:
        if str(Path(config.get("projectPath", "")).expanduser().resolve()) != str(project):
            return None, None
        from .bridge import BridgeClient
        endpoint = "http://127.0.0.1:" + str(int(config["port"]))
        client = BridgeClient(endpoint, str(config["token"]), timeout=timeout)
        context = client.context()
        actual = str(Path(context.get("projectPath", "")).expanduser().resolve())
        if actual != str(project):
            return None, None
        return client, context
    except (KeyError, TypeError, ValueError, OSError, RuntimeError):
        return None, None


def provision_bridge(project, bridge_path=None):
    """Install the bridge package with a durable project lock and atomic files."""
    root = Path(project).expanduser().resolve()
    if not root.is_dir():
        raise ValueError(f"Unity project does not exist: {root}")
    manifest = root / "Packages" / "manifest.json"
    if not manifest.exists():
        raise ValueError("Packages/manifest.json is missing")
    with _project_lock(root) as (lock_handle, lock_record):
        client, _ = _bridge_context_for_record(lock_record, root, timeout=.2)
        if client is not None:
            raise ProjectOwnedError(f"Project already has an Atelier worker: {root}")
        if lock_record.get("processIdentity") and _same_identity(lock_record.get("processIdentity")):
            raise ProjectOwnedError(f"Project worker is still starting: {root}")
        if _unity_is_locked(root):
            raise ProjectLockedError(f"Unity project is locked: {root}")
        contents = manifest.read_bytes()
        data = json.loads(contents.decode("utf-8"))
        bridge = Path(bridge_path or (Path(__file__).resolve().parents[1] / "unity" / "bridge")).expanduser().resolve()
        desired = "file:" + bridge.as_posix()
        dependencies = data.setdefault("dependencies", {})
        changed = dependencies.get("dev.gryphprime.atelier-bridge") != desired
        backup = manifest.with_suffix(manifest.suffix + ".bak")
        if not backup.exists():
            _atomic_bytes(backup, contents, mode=0o600)
        if changed:
            dependencies["dev.gryphprime.atelier-bridge"] = desired
            _atomic_json(manifest, data, mode=0o600)
        return manifest


@contextlib.contextmanager
def project_mutation_lock(project) -> Iterator[Path]:
    """Serialize reviewed package/import writes with worker ownership.

    The yielded path is the resolved Unity project. The context acquires the
    same durable advisory lock used by ``UnityWorker`` and rechecks a persisted
    worker's authenticated bridge, PID start marker, and Unity's live lock
    before allowing project files to change. It never removes stale metadata or
    ``Temp/UnityLockfile``.
    """
    root = Path(project).expanduser().resolve()
    if not root.is_dir():
        raise ValueError(f"Unity project does not exist: {root}")
    with _project_lock(root) as (_handle, record):
        client, _ = _bridge_context_for_record(record, root, timeout=.2)
        if client is not None:
            raise ProjectOwnedError(f"Project already has an Atelier worker: {root}")
        if record.get("processIdentity") and _same_identity(record.get("processIdentity")):
            raise ProjectOwnedError(f"Project worker is still starting: {root}")
        if _unity_is_locked(root):
            raise ProjectLockedError(f"Unity project is locked: {root}")
        yield root


_owners: Dict[str, "UnityWorker"] = {}
_owners_lock = threading.RLock()


def _proc_identity(pid: int) -> Optional[dict]:
    """Read a PID start marker and command line without third-party modules."""
    try:
        pid = int(pid)
    except (TypeError, ValueError):
        return None
    if pid <= 0:
        return None
    proc = Path("/proc") / str(pid)
    if proc.is_dir():
        try:
            raw = (proc / "stat").read_text(errors="replace")
            close = raw.rfind(")")
            fields = raw[close + 2 :].split()
            process_state = fields[0] if fields else ""
            start = fields[19] if len(fields) > 19 else None
            command = (proc / "cmdline").read_bytes().replace(b"\0", b" ").strip()
            return {"pid": pid, "start": start, "state": process_state,
                    "command": hashlib.sha256(command).hexdigest() if command else ""}
        except (OSError, IndexError):
            return None
    if sys.platform == "darwin":
        # macOS has no /proc. ``ps`` exposes a stable process start string
        # (including the date and time) and the command line without requiring
        # a third-party process library. No shell is involved.
        # Test doubles commonly replace ``subprocess.Popen``; avoid invoking
        # the patched constructor while recording the child identity.
        if not isinstance(subprocess.Popen, type):
            return None
        try:
            result = subprocess.run(("ps", "-p", str(pid), "-o", "lstart=", "-o", "stat=", "-o", "command="),
                                    stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                                    text=True, timeout=.5, check=False)
            line = result.stdout.strip()
            if not line:
                return None
            fields = line.split(None, 6)
            if len(fields) < 5:
                return None
            start = " ".join(fields[:5])
            process_state = fields[5] if len(fields) > 5 else ""
            command = fields[6] if len(fields) > 6 else ""
            return {"pid": pid, "start": start, "state": process_state,
                    "command": hashlib.sha256(command.encode()).hexdigest()}
        except (OSError, subprocess.TimeoutExpired, AttributeError, TypeError):
            return None
    if os.name == "nt":
        # Windows exposes a kernel creation FILETIME, which is a stable
        # identity marker even when a PID is recycled. No process termination
        # API is used here; the marker only authorizes a later os.kill call.
        try:
            kernel = ctypes.WinDLL("kernel32", use_last_error=True)
            access = 0x1000  # PROCESS_QUERY_LIMITED_INFORMATION
            handle = kernel.OpenProcess(access, False, pid)
            if not handle:
                return None
            class _FileTime(ctypes.Structure):
                _fields_ = [("low", ctypes.c_uint32), ("high", ctypes.c_uint32)]
            creation, exit_time, kernel_time, user_time = (_FileTime(), _FileTime(), _FileTime(), _FileTime())
            try:
                ok = kernel.GetProcessTimes(handle, ctypes.byref(creation), ctypes.byref(exit_time),
                                           ctypes.byref(kernel_time), ctypes.byref(user_time))
                if not ok:
                    return None
                marker = (int(creation.high) << 32) | int(creation.low)
                return {"pid": pid, "start": str(marker), "state": "", "command": ""}
            finally:
                kernel.CloseHandle(handle)
        except (OSError, AttributeError, TypeError):
            return None
    return None


def _same_identity(record: Optional[dict]) -> bool:
    if not isinstance(record, dict) or not record.get("start"):
        return False
    current = _proc_identity(record.get("pid"))
    return bool(current and not str(current.get("state", "")).startswith("Z") and
                current.get("pid") == record.get("pid") and current.get("start") == record.get("start"))


class UnityWorker:
    """One authenticated, loopback-only Unity Editor process per project."""

    max_startup_seconds = 300
    health_interval_seconds = .5
    default_shutdown_seconds = 20
    default_terminate_seconds = 15

    def __init__(self, project, unity_path=None, state_dir=None):
        self.project = Path(project).expanduser().resolve()
        self.unity_path = unity_path or discover_unity(self.project)
        self.state_dir = Path(state_dir or (self.project / ".atelier")).expanduser().resolve()
        self.state_dir.mkdir(parents=True, exist_ok=True)
        self.process: Optional[subprocess.Popen] = None
        self.state = "offline"
        self.endpoint: Optional[str] = None
        self.token: Optional[str] = None
        self.error: Optional[str] = None
        self._lock_path = self.project / ".atelier" / "unity-worker.lock"
        self._lock_handle = None
        self._owns_lock = False
        self._started_at = 0.0
        self._started_wall = 0.0
        self._pid: Optional[int] = None
        self._process_identity: Optional[dict] = None
        self._owner_token: Optional[str] = None
        self._mode = "worker"
        self._adopted = False
        self._health_ok = False
        self._health_error: Optional[str] = None
        self._health_thread = None
        self._health_stop = threading.Event()
        self._lifecycle_lock = threading.RLock()
        self._last_activity = time.monotonic()
        self._idle_generation = 0
        self._load_persisted()

    @staticmethod
    def _free_loopback_port() -> int:
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as probe:
            probe.bind(("127.0.0.1", 0))
            return int(probe.getsockname()[1])

    @property
    def _state_path(self):
        return self.state_dir / "worker.json"

    @property
    def _config_path(self):
        return self.state_dir / "bridge.json"

    def _load_persisted(self):
        try:
            record = json.loads(self._state_path.read_text(encoding="utf-8"))
        except (FileNotFoundError, OSError, UnicodeDecodeError, json.JSONDecodeError):
            return
        if not isinstance(record, dict) or str(record.get("projectPath", "")) != str(self.project):
            return
        config = record.get("config")
        if not isinstance(config, dict):
            try:
                config = json.loads(self._config_path.read_text(encoding="utf-8"))
            except (FileNotFoundError, OSError, UnicodeDecodeError, json.JSONDecodeError):
                config = None
        if isinstance(config, dict) and config.get("token") and config.get("port"):
            try:
                self.token = str(config["token"])
                self.endpoint = "http://127.0.0.1:" + str(int(config["port"]))
            except (TypeError, ValueError):
                self.token = self.endpoint = None
        try:
            self._pid = int(record["pid"]) if record.get("pid") is not None else None
        except (TypeError, ValueError):
            self._pid = None
        self._process_identity = record.get("processIdentity") if isinstance(record.get("processIdentity"), dict) else None
        self._owner_token = str(record.get("ownerToken")) if record.get("ownerToken") else None
        self._mode = "interactive" if record.get("mode") == "interactive" else "worker"
        try:
            self._started_wall = float(record.get("startedAt") or 0.0)
        except (TypeError, ValueError):
            self._started_wall = 0.0
        if self._pid and self.endpoint and self.token:
            self.state = "interactive" if self._mode == "interactive" else "starting"
            self._start_health_monitor()

    def _write_state(self, state=None, released=False):
        port = None
        if self.endpoint:
            try:
                port = int(self.endpoint.rsplit(":", 1)[1])
            except (ValueError, IndexError):
                pass
        value = {
            "version": 2, "projectPath": str(self.project), "stateDir": str(self.state_dir),
            "pid": self._pid, "processIdentity": self._process_identity, "ownerToken": self._owner_token,
            "mode": self._mode, "config": {"token": self.token, "port": port,
            "projectPath": str(self.project), "outputRoot": str(self.state_dir)},
            "state": state or self.state, "updatedAt": time.time(),
        }
        if self._started_wall:
            value["startedAt"] = self._started_wall
        if released:
            value["releasedAt"] = time.time()
        _atomic_json(self._state_path, value)

    def _new_config(self, preserve_token=False):
        self.state_dir.mkdir(parents=True, exist_ok=True)
        port = self._free_loopback_port()
        if not preserve_token or not self.token:
            self.token = secrets.token_urlsafe(32)
        self.endpoint = f"http://127.0.0.1:{port}"
        config = {"token": self.token, "port": port, "projectPath": str(self.project), "outputRoot": str(self.state_dir)}
        _atomic_json(self._config_path, config)
        return config

    def _write_config(self):
        return self._new_config()

    def _record_for_adoption(self, lock_record=None):
        record = dict(lock_record or {})
        if not record.get("config") and self.endpoint and self.token:
            try:
                config = json.loads(self._config_path.read_text(encoding="utf-8"))
            except (FileNotFoundError, OSError, UnicodeDecodeError, json.JSONDecodeError):
                config = None
            if isinstance(config, dict):
                record["config"] = config
        if record.get("projectPath") and str(Path(record["projectPath"]).expanduser().resolve()) != str(self.project):
            return {}
        return record

    def _adopt(self, lock_handle, lock_record):
        record = self._record_for_adoption(lock_record)
        client, _ = _bridge_context_for_record(record, self.project, timeout=.35)
        if client is None:
            return False
        persisted_state = record.get("stateDir")
        if persisted_state and str(Path(persisted_state).expanduser().resolve()) != str(self.state_dir):
            # The bridge's output directory belongs to the original host's
            # workspace. Adopting it from another data root would make receipts
            # and snapshots appear to succeed while writing outside this host.
            raise ProjectOwnedError("An Atelier worker for this project belongs to another data directory.")
        config = record.get("config", {})
        output_root = config.get("outputRoot") if isinstance(config, dict) else None
        if output_root and str(Path(output_root).expanduser().resolve()) != str(self.state_dir):
            raise ProjectOwnedError("An Atelier worker writes receipts in another data directory.")
        self.endpoint = "http://127.0.0.1:" + str(int(config["port"]))
        self.token = str(config["token"])
        try:
            self._pid = int(record["pid"]) if record.get("pid") is not None else self._pid
        except (TypeError, ValueError):
            pass
        self._process_identity = record.get("processIdentity") or self._process_identity
        self._owner_token = record.get("ownerToken") or self._owner_token or secrets.token_urlsafe(24)
        self._mode = "interactive" if record.get("mode") == "interactive" else "worker"
        try:
            self._started_wall = float(record.get("startedAt") or self._started_wall or 0.0)
        except (TypeError, ValueError):
            pass
        self._adopted = True
        self._owns_lock, self._lock_handle = True, lock_handle
        self.state, self.error = ("interactive" if self._mode == "interactive" else "online"), None
        self._health_ok, self._health_error = True, None
        _owners[str(self.project)] = self
        self._write_state(self.state)
        _write_lock(lock_handle, {
            "version": 2, "projectPath": str(self.project), "stateDir": str(self.state_dir),
            "ownerToken": self._owner_token, "pid": self._pid, "processIdentity": self._process_identity,
            "mode": self._mode, "config": config, "state": self.state, "adoptedAt": time.time(),
        })
        self._start_health_monitor()
        return True

    def _claim_project(self, adopt_only=False):
        """Acquire the advisory lock and adopt an authenticated child if present."""
        with _owners_lock:
            existing = _owners.get(str(self.project))
            if existing is not None and existing is not self:
                raise ProjectOwnedError(f"Project already owned: {self.project}")
        if self._lock_handle is not None:
            return False
        self._lock_path.parent.mkdir(parents=True, exist_ok=True)
        handle = self._lock_path.open("a+b")
        if not _lock_handle(handle):
            handle.close()
            raise ProjectOwnedError(f"Project already owned: {self.project}")
        lock_record = _read_lock(handle)
        try:
            if self._adopt(handle, lock_record):
                return True
        except ProjectOwnedError:
            _unlock_handle(handle)
            handle.close()
            raise
        # A process can be alive while Unity is still importing and before the
        # bridge listener binds its port. Its PID start marker is stronger than
        # an absent UnityLockfile, so do not launch a second writer into it.
        if lock_record.get("processIdentity") and _same_identity(lock_record.get("processIdentity")):
            _unlock_handle(handle)
            handle.close()
            raise ProjectOwnedError(f"Project worker is still starting: {self.project}")
        if _unity_is_locked(self.project):
            _unlock_handle(handle)
            handle.close()
            if adopt_only:
                return False
            raise ProjectLockedError(f"Unity project is locked: {self.project}")
        if adopt_only:
            _unlock_handle(handle)
            handle.close()
            return False
        self._lock_handle = handle
        self._owns_lock = True
        self._owner_token = secrets.token_urlsafe(24)
        _write_lock(handle, {
            "version": 2, "projectPath": str(self.project), "stateDir": str(self.state_dir),
            "ownerToken": self._owner_token, "pid": None, "processIdentity": None,
            "mode": self._mode, "config": None, "state": "claimed", "claimedAt": time.time(),
        })
        return False

    def _release_project(self, state="offline"):
        with self._lifecycle_lock:
            if not self._owns_lock:
                return
            if self._lock_handle is not None:
                try:
                    record = _read_lock(self._lock_handle)
                    if record.get("ownerToken") in (None, self._owner_token):
                        record.update({"projectPath": str(self.project), "stateDir": str(self.state_dir),
                                       "ownerToken": self._owner_token, "pid": self._pid,
                                       "processIdentity": self._process_identity, "mode": self._mode,
                                       "state": state, "releasedAt": time.time()})
                        _write_lock(self._lock_handle, record)
                except (OSError, ValueError):
                    pass
                _unlock_handle(self._lock_handle)
                try:
                    self._lock_handle.close()
                except OSError:
                    pass
            self._lock_handle = None
            self._owns_lock = False
            with _owners_lock:
                if _owners.get(str(self.project)) is self:
                    _owners.pop(str(self.project), None)

    def _start_health_monitor(self):
        if self._health_thread and self._health_thread.is_alive():
            return
        self._health_stop.clear()
        self._health_thread = threading.Thread(target=self._health_loop, name="atelier-worker-health", daemon=True)
        self._health_thread.start()

    def _health_loop(self):
        while not self._health_stop.wait(self.health_interval_seconds):
            with self._lifecycle_lock:
                if self.state not in ("starting", "online", "interactive"):
                    return
                if not self._process_is_alive() or (self.process is None and self._process_identity and not _same_identity(self._process_identity)):
                    self._mark_dead("Unity worker exited")
                    return
            self.probe(timeout=.25, _from_monitor=True)

    def _process_is_alive(self, verify_identity=False):
        if self.process is not None:
            return self.process.poll() is None
        if self._pid is None:
            return bool(self._health_ok)
        if verify_identity and self._process_identity and self._process_identity.get("start"):
            return _same_identity(self._process_identity)
        try:
            os.kill(int(self._pid), 0)
            return True
        except (OSError, ValueError):
            return False

    def _exit_hint(self):
        """Return a bounded Unity startup-log tail for actionable failures."""
        log = self.state_dir / "unity-worker.log"
        try:
            with log.open("rb") as handle:
                handle.seek(0, os.SEEK_END)
                handle.seek(max(0, handle.tell() - 64 * 1024))
                text = handle.read(64 * 1024).decode("utf-8", "replace")
            lines = [line.strip() for line in text.splitlines() if line.strip()]
            if not lines:
                return None
            hint = " ".join(lines[-8:]).replace("\x00", " ")
            return hint[-2048:]
        except (OSError, UnicodeDecodeError):
            return None

    def _mark_dead(self, error=None):
        with self._lifecycle_lock:
            previous = self.state
            if previous not in ("offline", "failed"):
                self.error = error or "Unity worker exited"
                hint = self._exit_hint()
                if hint:
                    self.error += ": " + hint
            elapsed = (time.monotonic() - self._started_at if self._started_at else
                       (time.time() - self._started_wall if self._started_wall else 0.0))
            self.state = "failed" if previous == "starting" and elapsed > self.max_startup_seconds else "offline"
            self._health_ok = False
            self._health_error = error
            self._health_stop.set()
            self._release_project(state=self.state)
            try:
                self._write_state(self.state, released=True)
            except OSError:
                pass

    def status(self):
        """Return cached state without making a network request."""
        with self._lifecycle_lock:
            if self.state in ("starting", "online", "interactive") and not self._process_is_alive():
                elapsed = (time.monotonic() - self._started_at if self._started_at else
                           (time.time() - self._started_wall if self._started_wall else 0.0))
                if self.state == "starting" and elapsed > self.max_startup_seconds:
                    self.error = "Unity worker exited during startup"
                    self.state = "failed"
                else:
                    self.error = self.error or "Unity worker exited"
                    hint = self._exit_hint()
                    if hint and hint not in self.error:
                        self.error += ": " + hint
                    self.state = "offline"
                self._health_stop.set()
                self._release_project(state=self.state)
                try:
                    self._write_state(self.state, released=True)
                except OSError:
                    pass
            result = {"state": self.state if self.state in ("starting", "online", "interactive", "failed") else "offline"}
            if self.error:
                result["error"] = self.error
            return result

    def probe(self, timeout=.5, _from_monitor=False) -> bool:
        """Authenticate the bridge and verify exact project identity."""
        try:
            client = self.client(timeout=timeout, _touch=not _from_monitor, _internal=True)
            if client is None:
                raise RuntimeError("worker bridge configuration is unavailable")
            context = client.context()
            actual = str(Path(context.get("projectPath", "")).expanduser().resolve())
            if actual != str(self.project):
                raise RuntimeError("Unity bridge project identity mismatch")
            with self._lifecycle_lock:
                previous = self.state
                self._health_ok, self._health_error = True, None
                if self._mode == "interactive":
                    self.state = "interactive"
                elif self.state in ("starting", "online"):
                    self.state = "online"
                self.error = None
                if previous != self.state:
                    try:
                        self._write_state(self.state)
                    except OSError:
                        pass
            return True
        except Exception as error:
            with self._lifecycle_lock:
                self._health_ok, self._health_error = False, str(error)
            return False

    def start(self, graphics=True):
        """Adopt an authenticated worker or launch Unity on demand."""
        with self._lifecycle_lock:
            current = self.status()
            # A restarted host has only persisted metadata; it must still
            # acquire the project lock and authenticate adoption before using
            # the bridge. A local Popen/adopted owner may return immediately.
            if current["state"] in ("online", "interactive", "starting") and self._process_is_alive() and (self.process is not None or self._owns_lock):
                return self.process
            if not self.unity_path:
                raise FileNotFoundError("Unity executable not found")
            if not self.project.is_dir():
                raise ValueError(f"Unity project does not exist: {self.project}")
            adopted = self._claim_project()
            if adopted:
                return self.process
            config = self._new_config()
            self._mode = "worker"
            self.state, self.error, self._started_at = "starting", None, time.monotonic()
            self._started_wall = time.time()
            self._pid, self._process_identity = None, None
            self._write_state("starting")
            logs = self.state_dir / "unity-worker.log"
            environment = os.environ.copy()
            environment["ATELIER_BRIDGE_CONFIG"] = json.dumps(config, separators=(",", ":"))
            args = [str(self.unity_path), "-projectPath", str(self.project), "-batchmode", "-logFile", str(logs)]
            if not graphics:
                args.append("-nographics")
            try:
                self.process = subprocess.Popen(args, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, env=environment)
            except Exception:
                self._release_project(state="failed")
                self.state, self.error = "failed", "Unity worker could not be started"
                try:
                    self._write_state("failed", released=True)
                except OSError:
                    pass
                raise
            self._pid = int(self.process.pid)
            self._process_identity = _proc_identity(self._pid)
            _owners[str(self.project)] = self
            _write_lock(self._lock_handle, {
                "version": 2, "projectPath": str(self.project), "stateDir": str(self.state_dir),
                "ownerToken": self._owner_token, "pid": self._pid, "processIdentity": self._process_identity,
                "mode": self._mode, "config": config, "state": "starting", "startedAt": time.time(),
            })
            self._write_state("starting")
            self._start_health_monitor()
            self._last_activity = time.monotonic()
            return self.process

    def resume_existing(self):
        """Claim/authenticate a persisted worker without launching Unity.

        Hosts call this during startup for registered projects. It is safe to
        invoke repeatedly and returns ``False`` when no authenticated bridge is
        still serving; it never creates a new Unity process.
        """
        with self._lifecycle_lock:
            if self._owns_lock and self._process_is_alive():
                return True
            if not (self._pid and self.endpoint and self.token):
                return False
            try:
                return bool(self._claim_project(adopt_only=True))
            except (ProjectOwnedError, ProjectLockedError):
                return False

    def ensure(self, graphics=True):
        """Ensure a worker is adopted/launched; callers can then wait for probe."""
        return self.start(graphics=graphics)

    def client(self, timeout=15, _touch=True, _internal=False):
        from .bridge import BridgeClient
        if self.process is None and not self._owns_lock and not _internal:
            # A restarted host must claim/adopt the durable project lock before
            # handing a bridge client to dispatch code. Health probes use the
            # private flag and never perform writes.
            return None
        try:
            config = json.loads(self._config_path.read_text(encoding="utf-8"))
            project_path = str(Path(config["projectPath"]).expanduser().resolve())
            if project_path != str(self.project):
                return None
            self.endpoint, self.token = f"http://127.0.0.1:{int(config['port'])}", str(config["token"])
            if _touch:
                self._last_activity = time.monotonic()
            return BridgeClient(self.endpoint, self.token, timeout=timeout)
        except (FileNotFoundError, OSError, UnicodeDecodeError, json.JSONDecodeError, KeyError, TypeError, ValueError):
            return None

    def stop(self, graceful_seconds=None, terminate_seconds=None):
        """Gracefully stop only an authenticated, exact-project worker."""
        graceful_seconds = self.default_shutdown_seconds if graceful_seconds is None else max(0.0, float(graceful_seconds))
        terminate_seconds = self.default_terminate_seconds if terminate_seconds is None else max(0.0, float(terminate_seconds))
        # A restarted host must first own/adopt the durable record. If it
        # cannot authenticate the endpoint, it is not allowed to signal the
        # persisted PID at all.
        if self.process is None and not self._owns_lock and not self.resume_existing():
            return
        with self._lifecycle_lock:
            process = self.process
            pid = self._pid
            identity = self._process_identity
            client = None
            candidate = self.client(timeout=.5, _touch=False)
            if candidate is not None:
                try:
                    # A live Popen handle is already an exact child-owned
                    # authority; its bridge token authenticates shutdown. An
                    # adopted worker must prove project identity with context
                    # before any request is sent to a persisted endpoint.
                    if process is not None:
                        client = candidate
                    else:
                        context = candidate.context()
                        actual = str(Path(context.get("projectPath", "")).expanduser().resolve())
                        if actual == str(self.project):
                            client = candidate
                except Exception:
                    client = None
            if client is not None:
                try:
                    client.shutdown()
                except Exception:
                    pass
        deadline = time.monotonic() + graceful_seconds
        while time.monotonic() < deadline and self._process_is_alive(verify_identity=True):
            time.sleep(.05)
        if self._process_is_alive(verify_identity=True):
            if process is not None and process.poll() is None:
                try:
                    process.terminate()
                except OSError:
                    pass
            elif identity and _same_identity(identity):
                try:
                    os.kill(int(pid), signal.SIGTERM)
                except (OSError, TypeError, ValueError):
                    pass
            deadline = time.monotonic() + terminate_seconds
            while time.monotonic() < deadline and self._process_is_alive(verify_identity=True):
                time.sleep(.05)
            if self._process_is_alive(verify_identity=True) and process is not None and process.poll() is None:
                try:
                    process.kill()
                except OSError:
                    pass
                try:
                    process.wait(timeout=max(0.1, terminate_seconds))
                except (OSError, subprocess.TimeoutExpired):
                    pass
        with self._lifecycle_lock:
            self._health_stop.set()
            running = self._process_is_alive(verify_identity=True)
            if process is not None and process.poll() is not None:
                self.process = None
            lingering_lock = _unity_is_locked(self.project)
            self._release_project(state="failed" if running or lingering_lock else "offline")
            self.state = "failed" if running or lingering_lock else "offline"
            self.error = ("Unity worker did not exit; review the project before another writer starts." if running else
                           "Unity lock remains after worker exit; review the project before another writer starts." if lingering_lock else None)
            try:
                self._write_state(self.state, released=True)
            except OSError:
                pass

    def idle(self, seconds=30):
        """Stop a background worker after an inactivity window."""
        seconds = max(0.0, float(seconds))
        with self._lifecycle_lock:
            self._idle_generation += 1
            generation = self._idle_generation
            deadline = time.monotonic() + seconds

        def wait_and_stop():
            remaining = deadline - time.monotonic()
            if remaining > 0:
                time.sleep(remaining)
            with self._lifecycle_lock:
                if generation != self._idle_generation or self._mode == "interactive":
                    return
                if time.monotonic() - self._last_activity < seconds or self.state not in ("starting", "online"):
                    return
            self.stop()

        threading.Thread(target=wait_and_stop, name="atelier-worker-idle", daemon=True).start()

    def open_interactive(self):
        """Hand the project to foreground Unity while keeping bridge identity."""
        with _owners_lock:
            owner = _owners.get(str(self.project))
            if owner is not None and owner is not self:
                raise ProjectOwnedError(f"Project already owned: {self.project}")
        self.stop()
        if not self.unity_path:
            raise FileNotFoundError("Unity executable not found")
        if _unity_is_locked(self.project):
            raise ProjectLockedError(f"Project is locked by Unity: {self.project}")
        with self._lifecycle_lock:
            if self._claim_project():
                # stop() could not complete a still-serving worker. Keep the
                # adopted lock and fail closed instead of launching a second
                # Unity writer with a fresh bridge token.
                raise ProjectOwnedError(f"Project worker is still running: {self.project}")
            self._mode = "interactive"
            config = self._new_config()
            self.state, self.error, self._started_at = "interactive", None, time.monotonic()
            self._started_wall = time.time()
            self._pid, self._process_identity = None, None
            self._write_state("interactive")
            environment = os.environ.copy()
            environment["ATELIER_BRIDGE_CONFIG"] = json.dumps(config, separators=(",", ":"))
            try:
                self.process = subprocess.Popen([str(self.unity_path), "-projectPath", str(self.project)],
                                                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, env=environment)
            except Exception:
                self._release_project(state="failed")
                self.state = "failed"
                raise
            self._pid = int(self.process.pid)
            self._process_identity = _proc_identity(self._pid)
            _owners[str(self.project)] = self
            _write_lock(self._lock_handle, {
                "version": 2, "projectPath": str(self.project), "stateDir": str(self.state_dir),
                "ownerToken": self._owner_token, "pid": self._pid, "processIdentity": self._process_identity,
                "mode": "interactive", "config": config, "state": "interactive", "startedAt": time.time(),
            })
            self._write_state("interactive")
            self._start_health_monitor()
            return self.process
