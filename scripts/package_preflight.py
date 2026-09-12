"""Read-only preflight checks for an Avatar Wardrobe VPM package/archive."""
import argparse
import json
import re
from pathlib import Path
from zipfile import ZipFile

IGNORED_PARTS = {'.git', '__pycache__'}

def ignored(path: Path) -> bool:
    return any(part in IGNORED_PARTS or part == '.DS_Store' or part.startswith('._') or part.endswith('.pyc') for part in path.parts)


def check_package(root: Path):
    manifest = json.loads((root / 'package.json').read_text(encoding='utf-8'))
    name, version = manifest.get('name'), manifest.get('version')
    assert name == 'dev.gryphprime.avatar-wardrobe', 'unexpected package name'
    assert isinstance(version, str) and re.fullmatch(r'\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?', version), 'invalid package version'
    assert json.loads((root / 'Editor/WardrobeVersion.json').read_text())['version'] == version, 'UI/package version mismatch'
    assert f'/{name}-{version}.zip' in manifest.get('url', ''), 'manifest URL/version mismatch'
    missing = []
    guids = {}
    for path in root.rglob('*'):
        if ignored(path.relative_to(root)):
            continue
        if path.is_dir() and path != root and not path.name.startswith('._'):
            if not Path(str(path) + '.meta').is_file():
                missing.append(str(path.relative_to(root)) + ' (folder meta)')
        if path.is_file() and not path.name.startswith('._') and path.name != '.DS_Store' and not path.name.endswith('.meta'):
            if not Path(str(path) + '.meta').is_file():
                missing.append(str(path.relative_to(root)))
        if path.is_file() and path.name.endswith('.meta') and not path.name.startswith('._'):
            match = re.search(r'^guid:\s*([0-9a-f]{32})\s*$', path.read_text(errors='ignore'), re.MULTILINE)
            assert match, f'invalid Unity metadata: {path.relative_to(root)}'
            if match:
                guid = match.group(1)
                if guid in guids:
                    raise AssertionError(f'duplicate Unity GUID {guid}: {guids[guid]} and {path.relative_to(root)}')
                guids[guid] = path.relative_to(root)
    assert not missing, 'missing Unity metadata: ' + ', '.join(missing[:5])
    return version


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('package', type=Path)
    parser.add_argument('--archive', type=Path, help='archive to inspect for AppleDouble sidecars')
    args = parser.parse_args()
    version = check_package(args.package)
    if args.archive:
        with ZipFile(args.archive) as archive:
            sidecars = [n for n in archive.namelist() if any(part.startswith('._') for part in Path(n).parts)]
            assert not sidecars, 'archive contains AppleDouble sidecars: ' + ', '.join(sidecars[:5])
            assert archive.testzip() is None, 'archive CRC check failed'
            archived_manifest = json.loads(archive.read('package.json'))
            assert archived_manifest == json.loads((args.package / 'package.json').read_text()), 'archive manifest differs from source'
            files = [p for p in args.package.rglob('*') if p.is_file() and not ignored(p.relative_to(args.package))]
            expected = {p.relative_to(args.package).as_posix() for p in files}
            actual = {name for name in archive.namelist() if not name.endswith('/')}
            assert actual == expected, 'archive inputs differ from package source'
            for file in files:
                assert archive.read(file.relative_to(args.package).as_posix()) == file.read_bytes(), f'archive content differs: {file}'
    print(f'Package preflight passed: {version}')


if __name__ == '__main__':
    main()
