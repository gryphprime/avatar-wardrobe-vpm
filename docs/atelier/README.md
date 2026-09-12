# Atelier development alpha

Atelier is being developed alongside Avatar Wardrobe as an independent product.
This branch implements an expanding runnable alpha of the supplied
[product definition](Atelier_PRD.md). It is a development alpha, not completion
of the entire MVP or a production release.

## Run

From the repository root, using Python 3.9 or later:

```sh
python3 -m atelier
```

Atelier starts its own authenticated loopback host and opens its desktop web UI.
Unity is not required for startup, library browsing, project registration, or
draft editing. State defaults to `~/.atelier`; use `--data /path/to/data` to
choose another location. `--no-browser` prints the launch link instead.

The link includes a per-launch authentication token in its URL fragment. The UI
keeps it in session storage and removes the fragment. Only requests from the
same local origin with the token can read or mutate application data.

For Unity execution, install Unity 2022.3.22f1 or provide its executable with
`--unity /path/to/Unity`. Register an existing project, install the Atelier
bridge through the workspace controls, and start the worker. Bridge provisioning
adds a local UPM package reference and saves the previous package manifest.
Use a disposable project while evaluating this alpha.

## Current implementation

- Independent Python desktop host, UI, local database and Unity package. Neither
  an installed Avatar Wardrobe package nor its server is a runtime dependency.
- Existing-project inspection and idempotent workspace registration.
- AW's content-addressed immutable archive library, extracted into a Unity-source
  module with provenance retained. Archive import, prefab indexing, review plans,
  GUID/conflict checks and explicit project staging remain separate operations.
- Versioned desired recipes, undo history, confirmed Unity revisions and rendered
  artifact revisions. Accepting a draft does not advance confirmed state.
- Durable, ordered reconciliation commands with stable IDs, revision checks,
  receipt recovery and explicit failure/review states. Only undispatched snapshot
  requests may be superseded; explicit mutation commands are retained.
- Snapshot creator UI with worn-copy identities, replace/remove draft actions,
  camera presets, retained photos and export.
- Independent Unity bridge for the supported saved-scene/prefab workflow. See
  [the bridge support boundary](../../unity/bridge/README.md).
- Declared material colors and static blendshapes on Atelier-owned prefab copies,
  with immutable source materials, reversible generated assignments, whole-recipe
  undo, unsupported-control warnings, and debounced desired-state UI.
- Authoritative inspection and explicit recovery that compares Unity with the
  draft, checks the review is still current, and rebases without replaying an
  ambiguous mutation. Current and desired recipes are shown side by side.
- Authenticated worker adoption after desktop restart, stable process identity,
  shared project ownership across file operations, exact Unity-version discovery,
  and cached health status. Native Unity receives the same bridge on handoff.
- A host-selected adapter registry with durable inspection and snapshot handlers,
  public declaration/execution APIs, and a Modular Avatar integration for existing
  VRChat Avatar SDK projects. Package requests have an explicit plan/apply UI.
- Durable package/import journals. Interrupted project operations block new work
  until a fingerprinted review is acknowledged; missing staged files stay visible.

The standalone distribution is a Python-hosted web UI. Portable launchers and an
unsigned macOS development `.app` are available; they require installed Python.
See [distribution instructions](distribution.md). Self-contained installers,
code signing and notarization remain release work.

## Code boundaries

| Area | Location | Responsibility |
| --- | --- | --- |
| Desktop | `apps/desktop/` | Product UI, local draft interaction and status |
| Core | `atelier/core.py` | Workspaces, state revisions, durable operations, artifacts |
| Host | `atelier/host.py` | Authenticated transport and background dispatch |
| Library | `atelier/library.py`, `atelier/sources/` | Product facade and extracted Unity archive handling |
| Runtime | `atelier/project_runtime.py`, `atelier/bridge.py` | Project lifecycle and Unity process protocol |
| Packages | `atelier/packages.py` | Fingerprinted, exclusive vrc-get plans |
| SDK | `adapters_sdk/`, `adapters/` | Declarative integrations; no arbitrary frontend execution |
| Unity | `unity/bridge/` | Exact target resolution, supported mutations and rendering |

The desktop owns SQLite. Unity owns its execution receipts and communicates
through protocol version 1. Unity never opens the desktop database.
The wire boundary is documented in [the protocol reference](../../protocol/README.md).

## Recovery and scope

Desired edits remain available while Unity is offline. Synchronize explicitly
to commit the current draft to the durable queue. After a lost response, Atelier
queries the original operation ID; it does not assume failure and repeat the
mutation. A missing or ambiguous receipt needs review. Failed changes retain
the draft. Dismissing a definite failure permits a new synchronization request.

Undo restores the previous desired recipe. Synchronizing that recipe applies
the reversal to Unity. This is distinct from claiming an offline Undo has
already changed the project.

The bridge supports its own generated prefab copies and their declared static
appearance fields. It does not reproduce AW's complete Modular Avatar/NDMF
processing, fitting, arbitrary base-avatar appearance, upload flow, advanced scene
tools or plugin compatibility. Animated, HDR, ambiguous and unsupported controls
are omitted or rejected explicitly. Final validation/build remains an interactive Unity handoff. A
photograph does not establish production avatar compatibility or build validity.

## Validation

```sh
python3 -m unittest discover -s tests -p 'test_*.py' -v
for test in tests/*.test.js; do node "$test" || exit; done
python3 scripts/build_atelier.py
python3 scripts/test_atelier_unity.py --output-directory /tmp/atelier-bridge-evidence
```

Tests include the original AW parser/browser regressions and the new Atelier
state, transport, library, package and SDK checks. The Unity fixture validation
uses only generated test assets, without a user avatar or VRChat credentials.
The Unity fixture additionally needs Pillow to inspect actual rendered pixels.
It exercises add/replace/remove, supported color/shape controls, whole-recipe undo,
immutable source bytes, HDR/ambiguous-control handling, inspection, repeat receipt
reads, stale and wrong targets, worker restart, unreadable receipt recovery and
graphics-enabled PNG output. A separate opt-in
[package fixture](../../scripts/test_atelier_packages.py) validates the real
vrc-get install/remove path and Unity package compilation with isolated settings.

The automated suite includes actual process interruption for accepted drafts,
partial imports, worker adoption, second-writer exclusion and interactive handoff.
The development archive is executed after extraction without the AW package.
The previous alpha's browser checks covered onboarding and library/import flows;
the new UI's live browser access currently awaits specific localhost permission.
Generated fixture results are not representative production-avatar acceptance
evidence, and the PRD's performance thresholds remain unmeasured acceptance goals.

On September 12, 2026, the final local run passed 56 Python tests, all seven
JavaScript test scripts, archive and macOS development-bundle builds, and the
Unity 2022.3.22f1 graphics fixture. The real integration fixture installed Modular
Avatar 1.18.7 and NDMF 1.14.8 into a generated VRChat Avatar SDK 3.10.5 project,
compiled and reconnected the bridge, removed Modular Avatar, then compiled and
reconnected again. No avatar upload or production-project edit was performed.

`build_atelier.py` creates `dist/atelier-alpha.zip` containing Atelier's modules,
web UI, bridge and documentation. The Avatar Wardrobe distributable package is
excluded. Extract it, enter the extracted `atelier` folder and run
`python3 -m atelier`.

## Remaining PRD acceptance work

1. Production avatar dressing through extracted AW setup/adapter behavior,
   including Modular Avatar/NDMF processing and complete reversible configuration.
2. Broader base-avatar appearance mappings and plugin/animation compatibility;
   current controls are deliberately limited to supported owned-copy fields.
3. Fresh-machine distribution testing, self-contained runtime packaging, signing,
   and release-level licensing decisions for Atelier and the public SDK/adapters.
4. Representative avatar, cold/warm-worker, rendering, build and usability
   measurements, plus further real interruption cases during render/build.
5. Full dependency-solver previews and a broader reviewed integration catalog.

Avatar Wardrobe's package, version and release pipeline retain their separate
identity. This branch does not publish Atelier or alter the live VPM listing.
