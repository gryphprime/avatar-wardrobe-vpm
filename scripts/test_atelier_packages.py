"""Real vrc-get integration smoke test using only a disposable project/config.

This is an opt-in network test. It does not use the user's VCC/ALCOM settings.
On macOS/Linux vrc-get honors XDG_DATA_HOME. Windows isolation needs a dedicated
test account, so this script refuses that platform rather than changing settings.
"""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import shutil
from contextlib import contextmanager

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from atelier.host import Application
from atelier.packages import PackageRuntime


@contextmanager
def fixture_directory(keep):
    root = Path(tempfile.mkdtemp(prefix='atelier-integration-')).resolve()
    try:
        yield root
    finally:
        if keep:
            print('Retained generated project: ' + str(root), flush=True)
        else:
            shutil.rmtree(root)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--vrc-get', required=True)
    parser.add_argument('--output-directory', required=True)
    parser.add_argument('--unity', help='Also start Unity to validate package compilation and bridge reconnect')
    parser.add_argument('--keep', action='store_true', help='Keep the generated fixture for diagnosing failures')
    args = parser.parse_args()
    if os.name == 'nt':
        parser.error('This isolated network fixture currently supports macOS and Linux only.')
    output = Path(args.output_directory).resolve()
    output.mkdir(parents=True, exist_ok=True)
    with fixture_directory(args.keep) as folder:
        root = Path(folder)
        environment = dict(os.environ, XDG_DATA_HOME=str(root / 'vrc-get-data'))
        executable = str(Path(args.vrc_get).resolve())
        subprocess.run([executable, 'repo', 'add', 'https://vpm.nadena.dev/vpm.json'], env=environment,
                       stdin=subprocess.DEVNULL, check=True, timeout=60)
        project = root / 'Project'
        (project / 'ProjectSettings').mkdir(parents=True)
        (project / 'ProjectSettings/ProjectVersion.txt').write_text('m_EditorVersion: 2022.3.22f1\n')
        (project / 'Packages').mkdir()
        (project / 'Assets').mkdir()
        # Match the built-in module set of a normal 3D project. The VRChat SDK
        # assumes these Unity modules exist rather than declaring every one.
        modules = ('ai androidjni animation assetbundle audio cloth director imageconversion imgui jsonserialize '
                   'particlesystem physics physics2d screencapture terrain terrainphysics ui uielements '
                   'umbra unityanalytics unitywebrequest unitywebrequestassetbundle unitywebrequestaudio '
                   'unitywebrequesttexture unitywebrequestwww vehicles video vr wind xr').split()
        dependencies = {'com.unity.modules.' + module: '1.0.0' for module in modules}
        dependencies['com.unity.nuget.newtonsoft-json'] = '3.2.1'
        dependencies['com.unity.test-framework'] = '1.1.33'
        (project / 'Packages/manifest.json').write_text(json.dumps({'dependencies': dependencies}))
        # NDMF's editor references libraries bundled by VRChat's base package.
        # Exercise the supported existing-Avatar-SDK project, not bare Unity.
        print('Preparing the generated VRChat Avatar SDK project', flush=True)
        subprocess.run([executable, 'install', '--yes', 'com.vrchat.avatars', '3.10.5'], cwd=project, env=environment,
                       stdin=subprocess.DEVNULL, check=True, timeout=180)
        app = Application(root / 'data', unity=args.unity, package_runtime=PackageRuntime(executable, environment))
        report = {'integration': 'atelier.modular-avatar', 'fixture': 'generated VRChat Avatar SDK 3.10.5 project', 'phases': []}
        try:
            wid = app.register({'projectPath': str(project)})['id']
            def await_job(job):
                return app.jobs[job['id']].result(timeout=360)
            def packages(action):
                print('Reviewing and applying Modular Avatar ' + action, flush=True)
                planned = app.integration_plan(wid, 'atelier.modular-avatar', action)
                plan = await_job(planned)
                result = await_job(app.integration_apply(wid, plan['id']))
                report['phases'].append({'action': action, 'plan': plan, 'result': result,
                                         'packages': app.store.workspace(wid)['packages']})
                (output / 'package-validation.json').write_text(json.dumps(report, indent=2))
            def check_unity(phase):
                print('Checking Unity compilation and bridge: ' + phase, flush=True)
                app.worker_action(wid, 'start')
                app.jobs[wid].result(timeout=30)
                worker = app.worker(wid)
                deadline = time.monotonic() + 240
                context = None
                while time.monotonic() < deadline:
                    try:
                        context = worker.client().context()
                        if context['projectPath'] == str(project): break
                    except Exception:
                        if worker.status().get('state') in ('failed', 'offline'):
                            raise RuntimeError('Unity exited during package compilation: ' + str(worker.status()))
                        time.sleep(.5)
                if context is None:
                    raise RuntimeError('Unity bridge did not become ready; inspect the saved worker log.')
                report['phases'].append({'action': phase, 'unityContext': context})
                worker.stop()
                (output / 'package-validation.json').write_text(json.dumps(report, indent=2))
            packages('install')
            if args.unity:
                app.worker_action(wid, 'provision')
                app.jobs[wid].result(timeout=30)
                check_unity('compile-with-integration')
            packages('remove')
            if args.unity:
                check_unity('compile-after-removal')
            report['ok'] = True
        finally:
            for worker in app.workers.values():
                log = worker.state_dir / 'unity-worker.log'
                if log.exists():
                    shutil.copy2(log, output / 'unity.log')
            app.close()
            (output / 'package-validation.json').write_text(json.dumps(report, indent=2))
        print('Integration install/remove validation passed: ' + str(output), flush=True)


if __name__ == '__main__':
    main()
