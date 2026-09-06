"""Build only the distributable package; optionally merge live listing history."""
import argparse
import hashlib
import json
from pathlib import Path
import urllib.error
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[1]
PACKAGE = ROOT / 'Packages/dev.gryphprime.avatar-wardrobe'
LISTING_URL = 'https://gryphprime.github.io/avatar-wardrobe-vpm/index.json'

def build(live=False, tag=None):
    manifest = json.loads((PACKAGE / 'package.json').read_text())
    name, version = manifest['name'], manifest['version']
    if tag and tag != 'v' + version:
        raise ValueError('Git tag must match package.json version')
    for required in ['LICENSE', 'THIRD_PARTY_NOTICES.md', 'AvatarWardrobe.Runtime.asmdef',
                     'Editor/AvatarWardrobe.Editor.asmdef', 'Editor/WardrobePackagePaths.cs',
                     'Editor/WardrobeIndexer/wardrobe_index.py', 'Web/wardrobe.html']:
        assert (PACKAGE / required).is_file(), required
    listing = {'name': 'Avatar Wardrobe', 'id': 'dev.gryphprime.avatar-wardrobe.listing',
               'url': LISTING_URL, 'author': 'gryphprime', 'packages': {}}
    if live:
        try:
            with urllib.request.urlopen(LISTING_URL, timeout=30) as response:
                listing = json.load(response)
        except urllib.error.HTTPError as error:
            if error.code != 404:
                raise
        if listing.get('id') != 'dev.gryphprime.avatar-wardrobe.listing':
            raise ValueError('Unexpected listing identity')
    elif (ROOT / 'docs/index.json').exists():
        listing = json.loads((ROOT / 'docs/index.json').read_text())
    archive = ROOT / 'dist' / f'{name}-{version}.zip'
    assert manifest['url'] == f'https://github.com/gryphprime/avatar-wardrobe-vpm/releases/download/v{version}/{archive.name}'
    archive.parent.mkdir(exist_ok=True)
    with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as output:
        for path in sorted(PACKAGE.rglob('*')):
            relative = path.relative_to(PACKAGE)
            if path.is_symlink():
                raise ValueError(f'Symlink prohibited: {relative}')
            if any(part.startswith('._') or part in {'.DS_Store', '__pycache__', '.git'} for part in relative.parts) or path.suffix == '.pyc':
                continue
            if not path.is_file():
                continue
            info = zipfile.ZipInfo(relative.as_posix(), date_time=(2026, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = (0o100755 if path.stat().st_mode & 0o111 else 0o100644) << 16
            output.writestr(info, path.read_bytes())
    digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    versions = listing['packages'].setdefault(name, {'versions': {}})['versions']
    if version in versions and versions[version].get('zipSHA256') != digest:
        raise ValueError('Version already published with different content; bump version')
    versions[version] = dict(manifest, zipSHA256=digest)
    (ROOT / 'docs/index.json').write_text(json.dumps(listing, indent=2) + '\n')
    (archive.parent / 'SHA256SUMS').write_text(f'{digest}  {archive.name}\n')
    with zipfile.ZipFile(archive) as check:
        assert check.testzip() is None
        assert json.loads(check.read('package.json')) == manifest
    print(f'Built {archive.name}: {archive.stat().st_size:,} bytes; SHA-256 {digest}')

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--live', action='store_true')
    parser.add_argument('--tag')
    args = parser.parse_args()
    build(args.live, args.tag)
