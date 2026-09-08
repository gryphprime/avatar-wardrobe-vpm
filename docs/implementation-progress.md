# Dressing workflow implementation

Source plan: Avatar Wardrobe UX and feature recommendations, September 7, 2026.
This is the implementation ledger, not a claim that the whole plan is complete.

## Trust and reversible edits

Implemented: scene target selection and pinning; exact-copy removal in Wardrobe and
Presets; explicit replacement versus additional copies; setup warnings serialized
on scene instances and stripped from builds; fit trust scoped to source dependency
hash, base dependency hash, scene avatar identity, and shape values. Legacy global
compatibility booleans no longer grant a fit exception. Creator MA, VRCFury and
lilycalInventory setup is preserved without invoking those engines.

Preset and compatibility settings join the scene's Undo operation for supported
Wardrobe edits. Failed results and exceptions restore settings as well as objects.
External changes to a file cause an explicit partial-Undo warning rather than
silently overwriting unrelated edits. History follows Unity's session lifetime;
restart-persistent Undo is not promised. Generated assets and engine-specific
settings are the next transaction scope to integrate.

Validation on September 7: package assemblies compiled against the open project's
Unity 2022.3.22f1 assemblies; 13 executable identity/context/fit assertions passed;
three existing JS suites and four Python descriptor tests passed. The Unity menu
`Tools > Avatar Outfit Toggles > Diagnostics > Validate Wardrobe Editing` passed
10 actual scene/Undo/Redo/failure assertions using disposable fixture objects and
restored the original settings. The installed SDK's VRCExpressionsMenuEditor
throws a NullReferenceException in its Undo callback with the current inspector;
Wardrobe's assertions still passed. Fresh-project build validation remains required.

## Remaining plan scope

- Composed, processed Try On worker, rotation/compare/poses and cancel/apply validation.
- External library host, immutable archive ingestion, project links and offline browsing.
- Visual composed menu, validated testing adapter, focused appearance controls and AAO metrics.
- Appearance recipes using existing preset storage, update impact, optional fitting and Android handoffs.
- Broader engine/generated-asset undo, restart boundaries and dependency matrix tests.
- Fresh-project installation, representative build workflow and observed-user task validation.

Stable publishing now requires a manual workflow and fresh-project/build attestation;
development pushes do not publish stable VPM releases.

## Review branch integration

Merged `dev/code-review-fixes` into `dev/ux-plan`, preserving upload outcomes, job recovery, read-only state reads, corrupt-file handling, parser fixes and Unity CI gating. Consolidated overlapping Undo and instance-removal implementations. The merged package passed 13 Unity EditMode tests in a new isolated Unity 2022.3.22f1 fixture, including the ten scene-editing assertions, seven Python tests, all five browser suites and 13 pure C# policy assertions. The local legacy Tools indexer wrapper still points at the removed Assets package; the VPM package regression tests exercise its actual packaged indexer.

## Library and reporting

The external Python host keeps the library and bounded preview cache available without Unity. It ingests ZIP/unitypackage bytes or selected folders, preserves immutable originals, identifies duplicate versions, reviews existing paths/GUIDs/executable content/dependencies, and creates project copies. Unity import is protected by a bounded renewable lease. Library records currently track project paths; automatic avatar/instance usage reconciliation remains part of the operation integration.

Browser validation used a disposable archive/project: archive add succeeded; cancelling import created no project asset; explicit import created the reviewed copy and a project usage record. English and Japanese library copy is available. The existing reporting client from main is integrated with source metadata minimization, UUID-v4 validation and UTF-8 payload bounds. Automated transport tests use mocked requests.

Processed Try On backend passed five Unity EditMode tests in addition to fourteen regression tests (nineteen total). These cover actual NDMF processing, isolated material changes, source fingerprint/selection/Undo preservation, duplicate-name replacement and rejection of unsupported inputs. Snapshot transport/UI and shadow worker are being integrated. These tests are not evidence of full real-avatar fit or runtime gesture fidelity.

See [expanded scope](expanded-scope.md) for the user's additional plan and parallel work streams.

## Expanded browser and async work

The advanced Scene editor is wired behind its Settings toggle: hierarchy search,
exact object selection, rename/active/transform edits, create/duplicate/reparent,
explicit deletion, and typed primitive component fields with native Inspector
handoff for unsupported fields. Logical Menu organization has a separate view and
keeps control/default/parameter identities and clothing hierarchy intact.

The desktop owns a SQLite intent queue with a project writer lease, immutable
UUID commands, predecessor revisions, cancellation requests, receipt recovery,
and retained identity tombstones. The Unity bridge commits a receipt before
nonblocking dispatch; reads of receipt/context/image state do not wait on Unity's
main thread. Pending scene changes block publishing review/build entrypoints;
photos alone do not. Prior-session unsaved successes become Needs review.

Browser Wear/Remove and drag gestures share the same normalized command client.
Wearing rows distinguish submitting/queued/running from confirmed instances.
Complete-avatar Try On uses a captured source and a separate graphics-enabled
Unity worker; an explicit active-Unity fallback uses the same rendering code.
Front/three-quarter/back and before/after images retain source context and stale
captions. Material/controller export and full executor integration tests are
still being tightened; this is a development milestone, not a stable release.

Validated so far: 23 desktop queue tests, 9 operation HTTP tests, 15 existing
host tests, 4 shadow HTTP tests, and all 8 Node suites. Synthetic browser checks
confirmed queued Wear stays distinct from installed state, report consent removes
browser diagnostics, and classification reports use the chosen variant's bounded
metadata. No production report was sent. Unity tests caught and corrected a
PreviewRenderUtility lifecycle defect; an actual processed PNG now renders.
The combined Unity suite is being rerun with fully copied package dependencies.
Checksums of 3,008 real dependency files matched the earliest capture; SDK attempts
to write protected packages during earlier fixture runs produced no observed
source changes. The fixture preparation script now copies dependencies by default.
