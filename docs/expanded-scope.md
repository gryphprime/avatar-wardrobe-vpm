# Expanded implementation scope

The active goal includes both user-supplied plans from September 7, 2026:

- Avatar Wardrobe UX and Feature Recommendations.
- Avatar Wardrobe Drag and Drop, Snapshots and Async Plan.

The user also explicitly requested Aelchor bug/misclassification reporting and delegated implementation of an advanced Unity-like scene/object editor. The advanced editor is an opt-in surface; it is requested scope even though the follow-up design document recommends limiting the normal dressing workflow.

The implementation connects these requested workflows:

- Immutable external library/import review and local offline browsing.
- Processed avatar snapshots, shadow capture/worker, and rendering validation; recent/kept photo history, explicit PNG export, reload recovery and revision-bound confirmed-photo refresh.
- Durable typed commands, bridge receipts, optimistic Wearing, drag/button parity, persistent failure notices and safe explicit Retry/Dismiss without replaying uncertain work.
- Contextual empty-state actions for missing avatars, missing project outfits, active filters and an offline Unity connection.
- Advanced hierarchy and Inspector with exact targets, revisions and Unity Undo.
- Existing Aelchor reporting client integration using upstream commit 872e73f and the supplied service contract.
- Logical menu organization, supported parameter diagnostics, exact-slot material/static-shape editing, AAO preview/apply and the existing preset's saved appearance recipe.
- Validated native tool handoffs: Gesture Manager's pinned target and MochiFitter's public menu. Unsupported texture-layering, fitting and Android conversion integrations retain explicit prerequisites and native/official guidance.

The normal dressing workflow stays independent of the opt-in Scene editor. The
Scene surface edits the pinned avatar hierarchy and supported primitive component
fields; complex or unsupported fields open Unity's Inspector. It is not a second
preset system or an unrestricted replacement for Unity.

Photo history preserves provenance and verified image bytes; opening a photograph
does not restore an appearance or authorize Wear. Saved appearance restoration is
a separate reviewed operation requiring existing exact copies and unchanged asset
versions. Dismissing a failed change hides its notice without deleting its durable
outcome. A known failed edit may be retried with a new ID only after a fresh exact
target check; unknown acceptance and Needs review never silently become a retry.

Compatibility support is bounded to validated public contracts. MochiFitter
handoff does not perform conversion or redistribute paid files. TexTransTool
layering, other paid fitting systems and VRCQuestTools conversion are not claimed
as implemented automated adapters. Static snapshots do not establish animation,
physics, networking, runtime performance rank or upload success. See
[implementation and validation](implementation-progress.md) for delivered behavior
and the evidence boundary for each integration.

Reports are sent only by an explicit Send report action. Validation uses mocked transport; no synthetic reports, photos, logs or purchased content are submitted to production automatically.
