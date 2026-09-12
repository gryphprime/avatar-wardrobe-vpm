# Atelier development alpha

Atelier is being developed alongside Avatar Wardrobe as an independent product.
This branch implements the first runnable vertical slice of the supplied
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
- Declarative public adapter manifest API and a built-in example declaration.
- A tested `vrc-get` subprocess boundary for reviewed package plans; this is not
  yet a reviewed catalog of production integrations.

The standalone distribution is deliberately a Python-hosted web UI at this
stage. Native application bundles, code signing and installers are later work.

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

The first bridge supports its own generated prefab copies. It does not reproduce
AW's complete Modular Avatar/NDMF setup, fitting, appearance editor, upload flow,
advanced scene tools or plugin compatibility. Unsupported appearance is rejected
explicitly. Final validation/build remains an interactive Unity handoff. A
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
It exercises add/replace/remove, repeat receipt reads, stale and wrong targets,
worker restart, unreadable receipt recovery and graphics-enabled PNG output.

Local validation on September 12, 2026 passed 34 Python tests, seven JavaScript
test scripts, standalone archive execution without the AW package, and the
Unity 2022.3.22f1 fixture with real graphics. Browser checks covered onboarding,
offline archive import, prefab selection, separate draft/confirmed display and
import review. Reconnecting the browser after a development-host restart was
blocked by automatic approval review; reviewed project import was subsequently
verified through the authenticated HTTP/application tests. These are generated
fixture results, not representative production-avatar acceptance evidence.

`build_atelier.py` creates `dist/atelier-alpha.zip` containing Atelier's modules,
web UI, bridge and documentation. The Avatar Wardrobe distributable package is
excluded. Extract it, enter the extracted `atelier` folder and run
`python3 -m atelier`.

## Remaining PRD acceptance work

1. Production avatar dressing through extracted AW setup/adapter behavior,
   including Modular Avatar/NDMF processing and complete reversible configuration.
2. Supported appearance mappings and generated material/shape workflows.
3. Complete worker reconnect/manual-editor reconciliation and package-reload
   recovery across real interruptions, with explicit ambiguous-state resolution.
4. At least one reviewed optional integration, usable installation UI and public
   adapter execution API. The current declarative example is not that acceptance.
5. Native desktop packaging, fresh-install testing and release-level licensing
   decisions for Atelier and the public SDK/first-party adapters.
6. Representative avatar, cold/warm-worker, package reload, rendering, build and
   usability measurements. Performance targets in the PRD remain targets.

Avatar Wardrobe's package, version and release pipeline retain their separate
identity. This branch does not publish Atelier or alter the live VPM listing.
