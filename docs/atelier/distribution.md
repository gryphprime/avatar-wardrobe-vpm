# Atelier development distributions

Atelier is a Python-hosted local web application. The distribution contains the
Python modules and browser assets; it does not bundle Python, Unity, or a code
signing identity. Use Python 3.9 or later and install Unity separately. The
runtime uses only Python's standard library.

## Source archive

Build the source archive from the repository root:

```sh
python3 scripts/build_atelier.py --output dist/atelier-alpha.zip --format zip
```

Extract it and run the host from the extracted `atelier` directory:

```sh
cd extracted/atelier
python3 -m atelier --no-browser
```

`apps/native/launch_atelier.py`, `launch_atelier.sh`, and
`launch_atelier.cmd` are convenience launchers. They find the current
distribution and invoke an installed Python. Set `ATELIER_PYTHON` when more
than one Python installation is available.

## macOS development app

Generate an unsigned `.app` bundle with a launcher pointing at the installed
interpreter used for the build:

```sh
python3 scripts/build_atelier.py --format app --output dist/Atelier.app
```

The launcher can be redirected at runtime with `ATELIER_PYTHON=/path/to/python`.
The app opens the same loopback browser host as `python -m atelier`; it is not a
self-contained installer and is not code signed or notarized by this script.

## Portable directory

For a directory that can be copied between machines with Python already
installed:

```sh
python3 scripts/build_atelier.py --format portable --output dist/atelier
```

Run `apps/native/launch_atelier.sh` on macOS/Linux or
`apps/native/launch_atelier.cmd` on Windows. Unity discovery still follows the
project's exact `ProjectVersion.txt` editor version and the platform's normal
Unity Hub installation locations; `UNITY_PATH` remains an explicit override.
