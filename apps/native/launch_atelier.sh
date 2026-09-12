#!/bin/sh
# Development/source launcher. Python is intentionally installed separately.
set -eu
SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
ROOT=$(CDPATH= cd -- "$SCRIPT_DIR/../.." && pwd)
PYTHON=${ATELIER_PYTHON:-${PYTHON:-python3}}
export PYTHONPATH="$ROOT${PYTHONPATH:+:$PYTHONPATH}"
cd "$ROOT"
exec "$PYTHON" -m atelier "$@"
