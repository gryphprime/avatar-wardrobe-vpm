# Avatar Wardrobe external indexer

Standalone catalog builder for the Avatar Wardrobe Unity window. It scans
`.prefab` files **as plain text** — no Unity, no asset loading, no editor
freezes — and writes the catalog the window reads:

    Library/AvatarWardrobe/catalog.json

A full scan of ~13k prefabs takes about a minute on an external SSD.
Incremental re-runs (unchanged files skipped by mtime+size) take seconds.

## Run it

From the project root:

    python3 Tools/WardrobeIndexer/wardrobe_index.py --project .
    python3 Tools/WardrobeIndexer/wardrobe_index.py --project . --full   # ignore cache

Or from the Unity window: sidebar → **Index changed assets** (incremental)
or **Rebuild index** (full). The window polls `catalog.json` and reloads it
when the run finishes. Progress is visible in the sidebar via
`Library/AvatarWardrobe/progress.json`; per-file progress goes to `scan.log`.

Windows releases bundle an isolated Python runtime, so buyers do not need
Python on PATH. macOS currently uses `python3` on PATH (macOS:
`/usr/bin/python3` works). The indexer uses only the Python standard library.

## Package the Windows runtime (maintainer)

From the project root, run:

    zsh Tools/WardrobeIndexer/package-windows-python.sh

The script downloads the pinned, official CPython x64 embedded distribution,
verifies its SHA-256 checksum, and extracts it into
`Tools/WardrobeIndexer/Runtime/Windows-x64/`. It refuses to overwrite an
existing runtime. Remove that directory before intentionally upgrading Python.
Keep the extracted `LICENSE.txt` in the exported package.

## How it works

- `.meta` files give exact GUIDs; `m_SourcePrefab` links resolve prefab
  variants and nested prefabs (memoized, cycle-guarded).
- Binary model files (FBX) can't be read as text: model instances recover
  counts/identity from variant overrides (`m_Materials`/`m_Bones` in
  `m_Modification` prove base renderers exist) and share bones/meshes
  within same-folder model groups. Renderer counts and bone lists for
  pure model instances stay empty — the window shows those as Untested,
  which is honest.
- Unity script components are identified by script GUID: plain `.cs`
  scripts via their `.meta` files (Modular Avatar types auto-discovered),
  DLL-embedded scripts (VRChat SDK) via the `(guid, fileID)` pair in
  `wardrobe_config.json`.
- Classification, family grouping, and variant naming port
  `AvatarWardrobeCatalog` 1:1; validated at 99.3% kind agreement against an
  in-Unity scan (`--check`, below).

## Family grouping

1. Identical mesh sets group together regardless of folder.
2. Otherwise, same folder + same base avatar (from `AvatarBaseNames.json`) + same cleaned name group together — per-color model duplicates (`180 Shinano` … `180 Shinano 11`) unite, while different garments (`1 Main` vs `10 Purple Shinano`) and other avatars' versions stay split.
3. Mesh-less records without a base match fall back to folder + cleaned name.

## Validate against an in-Unity catalog

    python3 Tools/WardrobeIndexer/wardrobe_index.py \
        --check Library/AvatarWardrobe/catalog.unity-backup.json \
                Library/AvatarWardrobe/catalog.json

## Config (`wardrobe_config.json`)

- `scripts`: extra script-GUID → component-name mappings.
- `vrcDescriptor`: `(guid, fileID)` of `VRCAvatarDescriptor` inside
  `VRCSDK3A.dll`. Stable per SDK version — update after an SDK upgrade
  (find it via `m_Script` in any avatar prefab; the doc carries
  `lipSync`/`VisemeBlendShapes`).
- `scanDirs` / `guidDirs`: where to look for prefabs / `.meta` files.
- `threads`: parse workers (I/O-bound; 16 is plenty).
