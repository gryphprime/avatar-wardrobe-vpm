# Package operations

`PackageRuntime.plan(project, action, package, version)` reads and fingerprints
`Packages/manifest.json` without contacting a repository. It returns an exact,
reviewable command and marks the operation as requiring review. Supported actions are
`install` and `remove`; package names and versions are bounded and shell metacharacters
are rejected.

`apply(plan)` is an explicit mutation boundary. It rechecks the combined fingerprint of
`manifest.json`, `vpm-manifest.json`, and `packages-lock.json` when present,
refuses Unity's `Library/UnityLockfile` or `Temp/UnityLockfile`, takes an Atelier-owned
exclusive lock, supplies no interactive stdin, and invokes `vrc-get` with `shell=False`
and bounded in-memory output. It passes vrc-get's explicit `--yes` flag only at this
reviewed application boundary; the flag is defined by the upstream install/remove
commands ([vrc-get commands.rs](https://raw.githubusercontent.com/vrc-get/vrc-get/master/vrc-get/src/commands.rs#L408-L433)). It does not
add repositories, trust sources, run imported scripts, or install anything automatically.
The command shape follows vrc-get's official CLI (`vrc-get install [package] [version]`
and `vrc-get remove [package]`); network and repository policy remain the user's
responsibility.

## Desktop integration flow

The Integrations page includes Modular Avatar, pinned to 1.18.7. Its upstream
[tagged package metadata](https://github.com/bdunderscore/modular-avatar/blob/1.18.7/package.json)
requires NDMF `>=1.14.7 <2.0.0-a`. Its code is MIT-licensed, with distinct artwork
terms in upstream [COPYING.md](https://github.com/bdunderscore/modular-avatar/blob/1.18.7/COPYING.md).
Atelier installs the official package unmodified and does not bundle its artwork.
Inspection and saved-scene photographs are the initial adapter actions;
automatic fitting and processed NDMF previews are not implemented.

Atelier's initial supported integration path requires an existing VRChat Avatar
SDK project, version 3.10.5 or later within 3.x. The validated configuration is
SDK 3.10.5, MA 1.18.7 and NDMF 1.14.8. A normal 3D project's Unity modules and
test framework must be present; the SDK itself references them. The fixture
creates this environment explicitly. A bare Unity project is rejected for this
integration because NDMF relies on libraries supplied by the Avatar SDK.

Install vrc-get separately and enable the official repository
`https://vpm.nadena.dev/vpm.json` in vrc-get or ALCOM. Atelier discovers an installed
CLI from PATH or common Homebrew locations. `--vrc-get /path/to/vrc-get` and
`ATELIER_VRC_GET` select an explicit executable. No CLI is downloaded by the app.

The UI reviews the requested package, pinned version, declared dependencies,
current manifest fingerprint and exact command. This is a request plan, not a
dependency-solver dry run: vrc-get resolves its complete dependency set at apply.
The result retains bounded stdout/stderr, and the host checks that the requested
version is actually installed or removed before reporting success.

Package operations stop the private worker and hold the same project lock used
by all Atelier worker controllers. A durable project-operation journal is written
before changing files. On interruption, Atelier requires current-file review and
explicit acknowledgement; it never retries a package/import mutation silently.
Imported file review distinguishes unchanged, changed and missing staged files.

The opt-in `scripts/test_atelier_packages.py` uses an isolated vrc-get
configuration and a disposable Unity project. It requires network access and an
installed CLI; `--unity /path/to/Unity` also checks compilation/reconnect before
and after removal. It currently supports macOS/Linux configuration isolation.
The final local fixture passed install, compilation/reconnect, removal and a
second compilation/reconnect; its generated evidence is at
`/private/tmp/atelier-package-evidence5` on the validation machine.
