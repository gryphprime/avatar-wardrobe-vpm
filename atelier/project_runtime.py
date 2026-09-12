"""Process and ownership management for Atelier's private Unity worker."""
from __future__ import annotations

import json
import os
import secrets
import shutil
import socket
import subprocess
import threading
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Optional


class ProjectLockedError(RuntimeError):
    """Unity, or another Atelier process, owns the project."""


class ProjectOwnedError(RuntimeError):
    """A second worker was requested for the same project."""


@dataclass(frozen=True)
class ProjectInfo:
    path: str
    unity_version: Optional[str]
    packages: dict

    def to_dict(self):
        return {"path": self.path, "unityVersion": self.unity_version, "packages": self.packages}


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
    packages = dict(json.loads(manifest.read_text()).get("dependencies", {})) if manifest.exists() else {}
    return ProjectInfo(str(root), version, packages)


def discover_unity() -> Optional[str]:
    candidates = (
        os.environ.get("UNITY_PATH"),
        "/Applications/Unity/Hub/Editor/2022.3.22f1/Unity.app/Contents/MacOS/Unity",
        shutil.which("unity"),
        shutil.which("Unity"),
    )
    return next((candidate for candidate in candidates if candidate and Path(candidate).is_file()), None)


def _unity_is_locked(root: Path) -> bool:
    # EditorInstance.json can survive a crash; UnityLockfile is the live lock.
    return (root / "Temp" / "UnityLockfile").exists()


# Kept as a private compatibility alias while the host and package code migrate.
_locked = _unity_is_locked


def provision_bridge(project, bridge_path=None):
    """Add the standalone bridge package using an atomic manifest replacement."""
    root = Path(project).expanduser().resolve()
    if _unity_is_locked(root):
        raise ProjectLockedError(f"Unity project is locked: {root}")
    manifest = root / "Packages" / "manifest.json"
    if not manifest.exists():
        raise ValueError("Packages/manifest.json is missing")
    contents = manifest.read_bytes()
    data = json.loads(contents.decode("utf-8"))
    bridge = Path(bridge_path or (Path(__file__).resolve().parents[1] / "unity" / "bridge")).resolve()
    data.setdefault("dependencies", {})["dev.gryphprime.atelier-bridge"] = "file:" + bridge.as_posix()
    backup = manifest.with_suffix(manifest.suffix + ".bak")
    backup.write_bytes(contents)
    temporary = manifest.with_name(manifest.name + ".atelier-tmp")
    try:
        temporary.write_text(json.dumps(data, indent=2, sort_keys=True) + "\n", encoding="utf-8")
        os.replace(temporary, manifest)
    finally:
        if temporary.exists():
            temporary.unlink()
    return manifest


_owners: dict[str, "UnityWorker"] = {}
_owners_lock = threading.Lock()


class UnityWorker:
    """One authenticated, loopback-only Unity Editor process per project."""

    # Large avatar projects can spend several minutes resolving packages/importing.
    max_startup_seconds = 300

    def __init__(self, project, unity_path=None, state_dir=None):
        self.project = Path(project).expanduser().resolve()
        self.unity_path = unity_path or discover_unity()
        self.state_dir = Path(state_dir or (self.project / ".atelier")).expanduser().resolve()
        self.process: Optional[subprocess.Popen] = None
        self.state = "offline"
        self.endpoint: Optional[str] = None
        self.token: Optional[str] = None
        self.error: Optional[str] = None
        self._lock_path = self.project / ".atelier" / "unity-worker.lock"
        self._owns_lock = False
        self._started_at = 0.0

    @staticmethod
    def _free_loopback_port() -> int:
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as probe:
            probe.bind(("127.0.0.1", 0))
            return int(probe.getsockname()[1])

    def _claim_project(self):
        if _unity_is_locked(self.project):
            raise ProjectLockedError(f"Unity project is locked: {self.project}")
        self._lock_path.parent.mkdir(parents=True, exist_ok=True)
        try:
            descriptor = os.open(str(self._lock_path), os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
        except FileExistsError:
            try:
                recorded = json.loads(self._lock_path.read_text(encoding="utf-8"))
                os.kill(int(recorded.get("pid", 0)), 0)
            except (ValueError, ProcessLookupError, PermissionError, OSError, json.JSONDecodeError):
                try:
                    self._lock_path.unlink()
                    descriptor = os.open(str(self._lock_path), os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
                except FileExistsError as exc:
                    raise ProjectOwnedError(f"Project already owned: {self.project}") from exc
            else:
                raise ProjectOwnedError(f"Project already owned: {self.project}")
        with os.fdopen(descriptor, "w", encoding="utf-8") as handle:
            json.dump({"pid": os.getpid(), "project": str(self.project), "createdAt": time.time()}, handle)
        self._owns_lock = True

    def _release_project(self):
        if self._owns_lock:
            try:
                self._lock_path.unlink()
            except FileNotFoundError:
                pass
        self._owns_lock = False

    def _write_config(self):
        self.state_dir.mkdir(parents=True, exist_ok=True)
        port = self._free_loopback_port()
        self.token = secrets.token_urlsafe(32)
        self.endpoint = f"http://127.0.0.1:{port}"
        config = {"token": self.token, "port": port, "projectPath": str(self.project), "outputRoot": str(self.state_dir)}
        config_file = self.state_dir / "bridge.json"
        temporary = config_file.with_name(config_file.name + ".tmp")
        temporary.write_text(json.dumps(config, sort_keys=True), encoding="utf-8")
        os.replace(temporary, config_file)
        return config

    def start(self, graphics=True):
        """Launch Unity and persist the bridge contract before process startup."""
        with _owners_lock:
            existing = _owners.get(str(self.project))
            if existing is not None and existing is not self:
                raise ProjectOwnedError(f"Project already owned: {self.project}")
            if self.process and self.process.poll() is None:
                return self.process
            if not self.unity_path:
                raise FileNotFoundError("Unity executable not found")
            if not self.project.is_dir():
                raise ValueError(f"Unity project does not exist: {self.project}")
            self._claim_project()
            config = self._write_config()
            self.state, self.error, self._started_at = "starting", None, time.monotonic()
            logs = self.state_dir / "unity-worker.log"
            environment = os.environ.copy()
            environment["ATELIER_BRIDGE_CONFIG"] = json.dumps(config, separators=(",", ":"))
            args = [str(self.unity_path), "-projectPath", str(self.project), "-batchmode", "-logFile", str(logs)]
            if not graphics:
                args.append("-nographics")
            try:
                self.process = subprocess.Popen(args, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, env=environment)
            except Exception:
                self._release_project()
                self.state = "failed"
                raise
            _owners[str(self.project)] = self
            return self.process

    def status(self):
        running = bool(self.process and self.process.poll() is None)
        if self.state == "starting" and not running:
            self.state, self.error = "failed", "Unity worker exited during startup"
            self._release_project()
        elif self.state == "starting" and self.endpoint and self.token:
            try:
                from .bridge import BridgeClient, BridgeError
                BridgeClient(self.endpoint, self.token, timeout=.05).context()
                self.state = "online"
            except (BridgeError, OSError, ValueError):
                pass
        if self.state == "starting" and time.monotonic() - self._started_at > self.max_startup_seconds:
            self.state, self.error = "failed", "Unity bridge did not become ready before timeout"
        elif self.state == "interactive" and not running:
            self.state = "offline"
            with _owners_lock:
                if _owners.get(str(self.project)) is self:
                    _owners.pop(str(self.project), None)
            self._release_project()
        result = {"state": self.state if running or self.state == "failed" else "offline"}
        if self.error:
            result["error"] = self.error
        return result

    def client(self, timeout=15):
        from .bridge import BridgeClient
        config_file = self.state_dir / "bridge.json"
        if not config_file.exists():
            return None
        config = json.loads(config_file.read_text(encoding="utf-8"))
        self.endpoint, self.token = f"http://127.0.0.1:{int(config['port'])}", config["token"]
        return BridgeClient(self.endpoint, self.token, timeout=timeout)

    def stop(self):
        process = self.process
        if process and process.poll() is None:
            try:
                self.client(timeout=.5).shutdown()
            except Exception:
                # A crashed or not-yet-ready bridge still needs the bounded process
                # fallback below; do not treat transport failure as authority to edit files.
                pass
            graceful_deadline = time.monotonic() + 20
            while process.poll() is None and time.monotonic() < graceful_deadline:
                time.sleep(.1)
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=15)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=5)
        # Unity removes its own lock just after process exit. Never remove a lock
        # file: another editor could have claimed it between process shutdown and
        # this check.
        deadline = time.monotonic() + 15
        while _unity_is_locked(self.project) and time.monotonic() < deadline:
            time.sleep(.1)
        with _owners_lock:
            if _owners.get(str(self.project)) is self:
                _owners.pop(str(self.project), None)
        self._release_project()
        self.process = None
        if _unity_is_locked(self.project):
            self.state = "failed"
            self.error = "Unity lock remains after worker exit; review the project before starting another writer."
        else:
            self.state, self.error = "offline", None

    def open_interactive(self):
        """Hand the project back to a foreground Unity process."""
        with _owners_lock:
            owner = _owners.get(str(self.project))
            if owner is not None and owner is not self:
                raise ProjectOwnedError(f"Project already owned: {self.project}")
        self.stop()
        if not self.unity_path:
            raise FileNotFoundError("Unity executable not found")
        if _unity_is_locked(self.project):
            raise ProjectLockedError(f"Unity project is locked: {self.project}")
        self._claim_project()
        try:
            self.process = subprocess.Popen([str(self.unity_path), "-projectPath", str(self.project)])
        except Exception:
            self._release_project()
            raise
        with _owners_lock:
            _owners[str(self.project)] = self
        self.state, self.error = "interactive", None
        return self.process
