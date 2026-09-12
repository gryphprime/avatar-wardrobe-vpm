"""Explicit, reviewable vrc-get package operations.

Planning is pure and never contacts a repository. Applying a reviewed plan is the
only path that invokes vrc-get; it rechecks the manifest fingerprint and Unity lock.
"""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import threading

MAX_OUTPUT = 64 * 1024
PACKAGE = r"^[a-z0-9][a-z0-9._-]{0,127}$"
VERSION = r"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$"


def _valid(value, pattern, label):
    import re
    if not isinstance(value, str) or len(value) > 128 or not re.fullmatch(pattern, value):
        raise ValueError(f"Invalid {label}.")
    return value


def _manifest(project):
    path = Path(project).resolve() / "Packages/manifest.json"
    if not path.is_file():
        raise ValueError("Choose an existing Unity project with Packages/manifest.json.")
    related = [path] + [path.parent / n for n in ("vpm-manifest.json", "packages-lock.json") if (path.parent / n).is_file()]
    raw = path.read_bytes()
    if sum(item.stat().st_size for item in related) > 2 * 1024 * 1024:
        raise ValueError("Unity package manifest is too large.")
    try:
        data = json.loads(raw)
    except (ValueError, UnicodeDecodeError) as error:
        raise ValueError("Unity package manifest is invalid JSON.") from error
    if not isinstance(data, dict) or not isinstance(data.get("dependencies", {}), dict):
        raise ValueError("Unity package manifest has no dependencies object.")
    digest = hashlib.sha256()
    for item in related:
        digest.update(item.name.encode()); digest.update(item.read_bytes())
    return path, data, digest.hexdigest()


class PackageRuntime:
    def __init__(self, executable=None, environment=None):
        candidate = executable or os.environ.get('ATELIER_VRC_GET') or shutil.which("vrc-get")
        if not candidate and os.name != 'nt':
            candidate = next((str(path) for path in (Path('/opt/homebrew/bin/vrc-get'), Path('/usr/local/bin/vrc-get')) if path.is_file()), None)
        self.executable = str(Path(candidate).expanduser().resolve()) if candidate else None
        self.environment = dict(environment) if environment is not None else None

    def available(self):
        return bool(self.executable and Path(self.executable).is_file() and os.access(self.executable, os.X_OK))

    def plan(self, project, action, package, version=None):
        if action not in ("install", "remove"):
            raise ValueError("Package action must be install or remove.")
        package = _valid(package, PACKAGE, "package name")
        if version is not None:
            version = _valid(version, VERSION, "package version")
        root = Path(project).resolve()
        path, data, fingerprint = _manifest(root)
        if action == "remove" and version is not None:
            raise ValueError("Package removal does not accept a version.")
        command = ["vrc-get", action, "--yes", package] + ([version] if version else [])
        return {"action": action, "package": package, "version": version,
                "command": command, "cwd": str(root), "manifest": str(path),
                "manifestSha256": fingerprint, "current": data.get("dependencies", {}).get(package),
                "requiresReview": True, "network": True}

    def apply(self, plan):
        if not isinstance(plan, dict) or not isinstance(plan.get('cwd'), str):
            raise ValueError('Invalid package plan.')
        from .project_runtime import project_mutation_lock
        root = Path(plan['cwd']).resolve()
        _manifest(root)
        with project_mutation_lock(root):
            return self._apply_locked(plan)

    def _apply_locked(self, plan):
        if not isinstance(plan, dict) or plan.get("action") not in ("install", "remove"):
            raise ValueError("Invalid package plan.")
        if plan["action"] == "remove" and plan.get("version") is not None:
            raise ValueError("Package removal does not accept a version.")
        root = Path(plan.get("cwd", "")).resolve()
        path, _, fingerprint = _manifest(root)
        if fingerprint != plan.get("manifestSha256"):
            raise ValueError("Package manifest changed since review.")
        if not self.available():
            raise RuntimeError("vrc-get is not available.")
        # Rebuild arguments from validated fields; never trust a serialized command.
        command = [self.executable, plan["action"], "--yes", _valid(plan["package"], PACKAGE, "package name")]
        if plan.get("version") is not None:
            command.append(_valid(plan["version"], VERSION, "package version"))
        lock = root / ".atelier-package.lock"
        with lock.open("a+b") as handle:
            if os.name != "nt":
                import fcntl
                try: fcntl.flock(handle.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
                except OSError as error: raise RuntimeError("Another package operation is running.") from error
            else:
                import msvcrt
                handle.write(b'0'); handle.flush(); handle.seek(0)
                try: msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
                except OSError as error: raise RuntimeError("Another package operation is running.") from error
            try:
                if _manifest(root)[2] != plan.get("manifestSha256"):
                    raise ValueError("Package manifest changed since review.")
                if (root / "Library/UnityLockfile").exists() or (root / "Temp/UnityLockfile").exists():
                    raise RuntimeError("Unity is running; close it before changing packages.")
                process = subprocess.Popen(command, cwd=str(root), shell=False, stdin=subprocess.DEVNULL,
                                           stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=self.environment)
                output = {"stdout": [], "stderr": []}
                def drain(name, stream):
                    total = 0
                    for line in iter(lambda: stream.read(8192), b''):
                        if total < MAX_OUTPUT:
                            part = line[:MAX_OUTPUT - total]; output[name].append(part); total += len(part)
                    stream.close()
                threads = [threading.Thread(target=drain, args=(name, getattr(process, name)), daemon=True) for name in ("stdout", "stderr")]
                for thread in threads: thread.start()
                try:
                    returncode = process.wait(timeout=300)
                except subprocess.TimeoutExpired:
                    process.kill(); process.wait(timeout=5)
                    raise RuntimeError("vrc-get timed out.")
                for thread in threads: thread.join(timeout=5)
            finally:
                if os.name != "nt":
                    fcntl.flock(handle.fileno(), fcntl.LOCK_UN)
                else:
                    handle.seek(0)
                    msvcrt.locking(handle.fileno(), msvcrt.LK_UNLCK, 1)
        return {"ok": returncode == 0, "returncode": returncode,
                "stdout": b"".join(output["stdout"]).decode('utf-8', 'replace'),
                "stderr": b"".join(output["stderr"]).decode('utf-8', 'replace'),
                "command": command[1:], "cwd": str(root)}
