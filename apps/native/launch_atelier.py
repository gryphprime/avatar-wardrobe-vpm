"""Launch Atelier from a source distribution with the installed Python.

This launcher intentionally does not bundle CPython. Set ``ATELIER_PYTHON``
when a desktop should use a particular installed interpreter, or pass
``--python /path/to/python`` before the application arguments.
"""
from __future__ import annotations

import os
from pathlib import Path
import shutil
import subprocess
import sys


def distribution_root() -> Path:
    # In a source checkout this file is apps/native/launch_atelier.py. In a
    # built archive it is atelier/apps/native/launch_atelier.py.
    return Path(__file__).resolve().parents[2]


def python_executable() -> str:
    configured = os.environ.get("ATELIER_PYTHON")
    if configured:
        return configured
    return sys.executable or shutil.which("python3") or shutil.which("python") or "python3"


def main(argv=None) -> int:
    args = list(sys.argv[1:] if argv is None else argv)
    executable = python_executable()
    if args[:1] == ["--python"]:
        if len(args) < 2:
            raise SystemExit("--python requires an interpreter path")
        executable, args = args[1], args[2:]
    environment = os.environ.copy()
    existing = environment.get("PYTHONPATH")
    environment["PYTHONPATH"] = str(distribution_root()) + (os.pathsep + existing if existing else "")
    completed = subprocess.run([executable, "-m", "atelier"] + args,
                               cwd=str(distribution_root()), env=environment)
    return int(completed.returncode)


if __name__ == "__main__":
    raise SystemExit(main())
