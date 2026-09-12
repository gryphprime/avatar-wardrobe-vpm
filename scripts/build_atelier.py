"""Build a standalone source distribution without the Avatar Wardrobe package."""
import argparse
from pathlib import Path
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def build(destination):
    destination = Path(destination)
    destination.parent.mkdir(parents=True, exist_ok=True)
    roots = ['atelier', 'apps/desktop', 'unity/bridge', 'adapters', 'adapters_sdk', 'adapters-sdk', 'docs/atelier', 'protocol']
    with zipfile.ZipFile(destination, 'w', zipfile.ZIP_DEFLATED) as archive:
        for relative in roots:
            for path in sorted((ROOT / relative).rglob('*')):
                if path.is_file() and '__pycache__' not in path.parts and path.suffix != '.pyc':
                    archive.write(path, str(Path('atelier') / path.relative_to(ROOT)))
        for name in ('LICENSE', 'THIRD_PARTY_NOTICES.md'):
            archive.write(ROOT / name, 'atelier/' + name)
    return destination


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', default=str(ROOT / 'dist' / 'atelier-alpha.zip'))
    print(build(parser.parse_args().output))
