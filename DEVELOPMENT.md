# Development validation and September 2026 review

Changes for review findings F01–F18 live on `dev/code-review-fixes`. This branch
must not be merged to `main` until reviewed: `main` publishes the VPM package.
No version bump, release tag, VPM listing update, or real avatar upload is part
of this change.

## Review disposition

| Findings | Change |
| --- | --- |
| F01 | Batch outcomes belong to a run ID; failure, refusal, cancellation and unfinished work cannot be inferred as success from an existing Blueprint ID. Express creation records explicit completion and retains the remote ID if local persistence fails. |
| F02 | The preset bridge receives the actual staging object and validates its scene/descriptor. The legacy global-name lookup was removed. |
| F03 | Both part-toggle replacement and removal reject mixed ownership before changing defaults. Incomplete previous parameters are validated before deletion. |
| F04 | Unrecoverable existing upload settings block writes/import. Backup recovery preserves the corrupt primary separately. Save failures propagate and discard failed in-memory edits; import installs its cache only after persistence succeeds. |
| F05 | Preset save/rename and menu edits use the existing edit boundary. Failed results and exceptions restore scene and both settings stores independently. |
| F06 | A serialized Unity Undo adapter restores preset metadata with scene Undo/Redo. Upload records are retained so Undo cannot erase a later successful remote upload's identity. |
| F07 | Upload reads no longer migrate menus, discover/register legacy presets, or persist defaults. Read scopes return detached missing defaults and reject saves. “Migrate legacy presets” is an explicit, repeatable mutation using the edit boundary. Owner migration occurs at an edit boundary rather than in CurrentBase. |
| F08 | The UI workflow preference is canonical. The old flag only supplies the initial value when the preference file is absent; its setter and the unused generator argument were removed. |
| F09 | Installed-row actions retain GUID, owner and hierarchy path, including in One Avatar mode. Asset-level removal explicitly says all copies. |
| F10 | Holder creation and rename share a sibling allocator after sanitizing names. Stable preset/upload keys remain unchanged. |
| F11, F12 | Removed obsolete GUID-only variant staging and its upload map. The legacy upload endpoint rejects GUID-only requests with guidance to create a preset; preset and whole-avatar uploads remain. Existing map files are left untouched for recovery. No documented external API commitment was found in this repository. |
| F13 | Scene results remain readable for 24 hours, subject to a 64-terminal-result count bound. Batch results are also bounded and repeatable. Jobs lost to domain reload remain unknown, never automatically retried. |
| F14 | The browser creates a request identity and recovers that exact job, handling terminal success/failure/cancellation/unknown results through the ordinary result handler. It never falls back to an unrelated latest job or repeats an upload. |
| F15, F16 | Signed references are parsed consistently. Nested instance counts retain multiplicity while dependencies stay unique. Parser cache signature advanced to 5. Counts remain advisory and do not model every Unity removed-component override. The VPM repository has one packaged Python implementation; it has no Tools mirror. |
| F17 | Compatibility cache capped at 4096 entries; localized/context keys are retained. |
| F18 | The former sentinel-driven staging check is replaced by an EditMode test assembly. Releases depend on fast tests and a supported Unity/SDK compile/test job. |

## Local checks

```sh
python3 -m unittest discover -s tests -p 'test_*.py' -v
for test in tests/*.test.js; do node "$test" || exit; done
python3 scripts/prepare_unity_tests.py --dependencies /path/to/resolved-project --output /path/to/new-fixture
"$UNITY_EDITOR_PATH" -batchmode -projectPath /path/to/new-fixture -runTests -testPlatform EditMode -testFilter OutfitToggleGenerator.ReviewRegressionTests -testResults results.xml -logFile unity-tests.log
python3 scripts/check_unity_results.py results.xml
```

The fixture builder requires Unity **2022.3.22f1**, VRChat Base/Avatars **3.10.5**,
Modular Avatar **1.18.7**, and NDMF **1.14.8**. It reads dependency package locations
from an already resolved project and creates a separate project. It copies no
avatar assets, scenes, project settings or credentials. Run the test suite in
that disposable fixture: the tests create their own saved scenes and settings.
SDK networking is not exercised. Staging uses the existing build delegate seam.

Local validation on September 7, 2026 passed: 12 Unity EditMode regression tests,
7 Python tests, all 5 browser test scripts, JavaScript syntax checks, and
`git diff --check`.

## Required CI runner provisioning

The repository had no registered self-hosted runners when checked on September
7, 2026. Before merging, provision a dedicated licensed runner with label
`avatar-wardrobe-unity`, Python 3, Bash, and these environment variables:

- `UNITY_EDITOR_PATH`: Unity 2022.3.22f1 executable.
- `WARDROBE_UNITY_DEPENDENCIES`: a resolved dependency project with the versions above.

No VRChat account or publishing credentials are needed. Do not expose this runner
to untrusted pull-request code. The validation workflow runs on development
branch pushes and is called by the release workflow. Without the runner, Unity
validation queues and publication remains blocked; the workflow does not skip
Unity checks or accept the existence of an empty test report.

The local test run is evidence for this branch, not a substitute for the CI gate.
Real VRChat uploads, platform/domain-reload resume and a full user-avatar catalog
scan remain integration checks for a later release candidate.

## Integration with the UX plan

`dev/ux-plan` incorporates these fixes. Installed-item removal additionally requires the current Editor instance identity, so same-named siblings remain distinct. The combined Undo journal restores presets and compatibility trust; failed edits also roll back upload settings, while ordinary Undo preserves later remote upload identities. Stable publication retains the manual fresh-project/build attestation and now also requires the automated Unity gate.
