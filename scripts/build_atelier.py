"""Build Atelier source, portable, and macOS development distributions.

The generated launchers use an installed Python interpreter. This command does
not claim to be a self-contained installer and does not code-sign macOS output.
"""
from __future__ import annotations

import argparse
import os
from pathlib import Path
import shutil
import shlex
import stat
import sys
import textwrap
import zipfile

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_ROOTS = ('atelier', 'apps/desktop', 'apps/native', 'unity/bridge', 'adapters', 'adapters_sdk',
                 'adapters-sdk', 'docs/atelier', 'protocol')


def _files():
    for relative in DEFAULT_ROOTS:
        root = ROOT / relative
        if not root.exists():
            continue
        for path in sorted(root.rglob('*')):
            if path.is_file() and '__pycache__' not in path.parts and path.suffix != '.pyc':
                yield path


def _archive(destination: Path):
    destination.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(destination, 'w', zipfile.ZIP_DEFLATED) as archive:
        for path in _files():
            archive.write(path, str(Path('atelier') / path.relative_to(ROOT)))
        for name in ('LICENSE', 'THIRD_PARTY_NOTICES.md'):
            source = ROOT / name
            if source.exists():
                archive.write(source, 'atelier/' + name)
    return destination


def _copy_tree(destination: Path):
    if destination.exists():
        if destination.is_file():
            raise ValueError(f'Portable destination is a file: {destination}')
    destination.mkdir(parents=True, exist_ok=True)
    for path in _files():
        target = destination / path.relative_to(ROOT)
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(path, target)
    for name in ('LICENSE', 'THIRD_PARTY_NOTICES.md'):
        source = ROOT / name
        if source.exists():
            shutil.copy2(source, destination / name)
    for launcher in ('launch_atelier.sh', 'launch_atelier.cmd', 'launch_atelier.py'):
        target = destination / 'apps' / 'native' / launcher
        if target.exists() and target.suffix == '.sh':
            target.chmod(target.stat().st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)
    return destination


def _mac_launcher(resources: Path, executable: str):
    path = resources.parent.parent / 'MacOS' / 'Atelier'
    path.parent.mkdir(parents=True, exist_ok=True)
    # ATELIER_PYTHON allows a user to select an installed interpreter at run
    # time; the generated default records the interpreter used for packaging.
    default_python = shlex.quote(executable)
    body = textwrap.dedent(f'''\
        #!/bin/sh
        set -eu
        HERE=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
        RESOURCES=$(CDPATH= cd -- "$HERE/../Resources/atelier" && pwd)
        DEFAULT_PYTHON={default_python}
        PYTHON=${{ATELIER_PYTHON:-$DEFAULT_PYTHON}}
        export PYTHONPATH="$RESOURCES${{PYTHONPATH:+:$PYTHONPATH}}"
        cd "$RESOURCES"
        exec "$PYTHON" -m atelier "$@"
    ''')
    path.write_text(body, encoding='utf-8')
    path.chmod(path.stat().st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)
    return path


def _mac_app(destination: Path, python_executable=None):
    if destination.suffix != '.app':
        destination = destination.with_suffix('.app')
    if destination.exists():
        if destination.is_file():
            raise ValueError(f'Application destination is a file: {destination}')
        if destination.is_symlink():
            raise ValueError(f'Refusing to replace a symlink application path: {destination}')
        marker = destination / 'Contents' / 'Info.plist'
        try:
            recognizable = marker.is_file() and 'dev.gryphprime.atelier' in marker.read_text(encoding='utf-8')
        except (OSError, UnicodeDecodeError):
            recognizable = False
        if not recognizable:
            raise ValueError(f'Refusing to replace an unrecognized application bundle: {destination}')
        shutil.rmtree(destination)
    contents = destination / 'Contents'
    resources = contents / 'Resources' / 'atelier'
    _copy_tree(resources)
    executable = str(Path(python_executable or sys.executable).expanduser())
    _mac_launcher(resources, executable)
    info = '''<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleDisplayName</key><string>Atelier</string>
<key>CFBundleExecutable</key><string>Atelier</string>
<key>CFBundleIdentifier</key><string>dev.gryphprime.atelier</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleVersion</key><string>0.1.0</string>
<key>CFBundleShortVersionString</key><string>0.1.0</string>
</dict></plist>
'''
    (contents / 'Info.plist').write_text(info, encoding='utf-8')
    return destination


def build(destination, format=None, python_executable=None):
    """Build a distribution.

    ``build(path)`` retains the original zip API. ``format`` may be ``zip``,
    ``portable`` (a runnable source directory), or ``app`` (a macOS ``.app``
    development bundle).
    """
    destination = Path(destination)
    selected = (format or ('app' if destination.suffix == '.app' else 'zip')).lower()
    if selected in ('zip', 'source'):
        return _archive(destination)
    if selected in ('portable', 'directory', 'dir'):
        return _copy_tree(destination)
    if selected in ('app', 'macos', 'mac'):
        return _mac_app(destination, python_executable)
    raise ValueError('Unsupported Atelier distribution format: ' + selected)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', default=str(ROOT / 'dist' / 'atelier-alpha.zip'))
    parser.add_argument('--format', choices=('zip', 'portable', 'app'), help='Output format; inferred from .app suffix when omitted.')
    parser.add_argument('--python', dest='python_executable', help='Installed Python path embedded as the macOS launcher default.')
    args = parser.parse_args()
    print(build(args.output, format=args.format, python_executable=args.python_executable))
