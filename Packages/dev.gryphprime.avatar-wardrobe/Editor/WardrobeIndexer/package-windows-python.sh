#!/usr/bin/env sh
# Downloads the pinned CPython embedded runtime used by Avatar Wardrobe's
# Windows indexer. Run from any directory: zsh Tools/WardrobeIndexer/package-windows-python.sh
set -eu

python_version="3.13.15"
archive_name="python-${python_version}-embed-amd64.zip"
archive_url="https://www.python.org/ftp/python/${python_version}/${archive_name}"
archive_sha256="d1f04d990aee1253d8569e8e5104e30fa9f5fa830899f14843448872d936a2cf"

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
runtime_dir="$script_dir/Runtime/Windows-x64"

if [ -e "$runtime_dir" ]; then
    echo "Refusing to overwrite existing runtime: $runtime_dir" >&2
    echo "Remove it first if you intentionally want to upgrade CPython." >&2
    exit 1
fi

for command in curl shasum unzip; do
    if ! command -v "$command" >/dev/null 2>&1; then
        echo "Missing required command: $command" >&2
        exit 1
    fi
done

temp_dir=$(mktemp -d "${TMPDIR:-/tmp}/avatar-wardrobe-python.XXXXXX")
cleanup() { rm -rf "$temp_dir"; }
trap cleanup EXIT HUP INT TERM

archive_path="$temp_dir/$archive_name"
echo "Downloading CPython $python_version Windows x64 embedded runtime..."
curl --fail --location --proto '=https' --tlsv1.2 --output "$archive_path" "$archive_url"

actual_sha256=$(shasum -a 256 "$archive_path" | awk '{print $1}')
if [ "$actual_sha256" != "$archive_sha256" ]; then
    echo "SHA-256 verification failed." >&2
    echo "Expected: $archive_sha256" >&2
    echo "Actual:   $actual_sha256" >&2
    exit 1
fi

mkdir -p "$runtime_dir"
unzip -q "$archive_path" -d "$runtime_dir"
# macOS can create AppleDouble sidecars when extracting onto non-APFS volumes.
# They are metadata only and must not ship in the Unity package.
find "$runtime_dir" -type f -name '._*' -delete

if [ ! -f "$runtime_dir/python.exe" ] || [ ! -f "$runtime_dir/python313.zip" ] || [ ! -f "$runtime_dir/LICENSE.txt" ]; then
    echo "The embedded Python archive did not contain the expected runtime files." >&2
    exit 1
fi

echo "Bundled CPython $python_version at: $runtime_dir"
echo "The Unity editor will use this runtime automatically on Windows."
