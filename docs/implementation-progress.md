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
