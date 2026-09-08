"""Create an isolated test project using a provisioned Unity/SDK dependency project.
Package dependencies are copied so SDK initialization cannot change the provisioned project.
No avatar assets, user settings, credentials or scenes are copied.
"""
import argparse
import json
from pathlib import Path
import shutil

parser=argparse.ArgumentParser()
parser.add_argument('--dependencies',type=Path,required=True)
parser.add_argument('--output',type=Path,required=True)
args=parser.parse_args()
repo=Path(__file__).resolve().parents[1]
source=args.dependencies.resolve()
out=args.output.resolve()
if out.exists(): raise SystemExit('Output must be a new directory')
version=(source/'ProjectSettings/ProjectVersion.txt').read_text()
if 'm_EditorVersion: 2022.3.22f1\n' not in version: raise SystemExit('Unity 2022.3.22f1 is required')
available={}
for directory in [source/'Library/PackageCache',source/'Packages']:
    for f in directory.glob('*/package.json'):
        data=json.loads(f.read_text())
        available[data['name']]=(f.parent,data)
pins={'com.vrchat.avatars':'3.10.5','com.vrchat.base':'3.10.5','nadena.dev.modular-avatar':'1.18.7','nadena.dev.ndmf':'1.14.8'}
for name, version in pins.items():
    if name not in available or available[name][1]['version']!=version: raise SystemExit(f'Missing supported dependency: {name} {version}')
deps={}
package_sources={}
def add(name):
    if name in deps:return
    if name.startswith('com.unity.modules.'):
        deps[name]='1.0.0';return
    if name not in available: raise SystemExit(f'Missing dependency {name}; resolve the provisioned project first')
    path,data=available[name]
    deps[name]='file:../IsolatedPackages/'+name
    package_sources[name]=path
    for dependency in set(data.get('dependencies',{}))|set(data.get('vpmDependencies',{})):add(dependency)
for name in [*pins,'com.unity.test-framework','com.unity.ugui','com.unity.modules.imgui','com.unity.modules.imageconversion','com.unity.modules.physics','com.unity.modules.cloth','com.unity.modules.animation','com.unity.modules.audio','com.unity.modules.unitywebrequest','com.unity.modules.unitywebrequesttexture','com.unity.modules.unitywebrequestaudio','com.unity.modules.unitywebrequestassetbundle']:add(name)
for name in json.loads((source/'Packages/manifest.json').read_text())['dependencies']:
    if name.startswith('com.unity.modules.'): add(name)
deps['dev.gryphprime.avatar-wardrobe']='file:'+str(repo/'Packages/dev.gryphprime.avatar-wardrobe')
# File dependencies may update their own importer metadata during SDK initialization.
# Copy every provisioned package; a symlink would still share mutable sources.
for name,path in package_sources.items():
    if any(item.is_symlink() for item in path.rglob('*')):
        raise SystemExit(f'Dependency contains a link; provide an unpacked package: {name}')
(out/'Packages').mkdir(parents=True)
try:
    for name,path in package_sources.items():
        shutil.copytree(path, out/'IsolatedPackages'/name,
                        ignore=shutil.ignore_patterns('._*','__pycache__','*.pyc','.git'))
except BaseException:
    shutil.rmtree(out)
    raise
(out/'Assets').mkdir()
(out/'ProjectSettings').mkdir()
(out/'ProjectSettings/ProjectVersion.txt').write_text('m_EditorVersion: 2022.3.22f1\n')
(out/'Packages/manifest.json').write_text(json.dumps({'dependencies':deps,'testables':['dev.gryphprime.avatar-wardrobe']},indent=2)+'\n')
print(out)
