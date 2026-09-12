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
