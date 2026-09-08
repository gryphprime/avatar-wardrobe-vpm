# Expanded implementation scope

The active goal includes both user-supplied plans from September 7, 2026:

- Avatar Wardrobe UX and Feature Recommendations.
- Avatar Wardrobe Drag and Drop, Snapshots and Async Plan.

The user also explicitly requested Aelchor bug/misclassification reporting and delegated implementation of an advanced Unity-like scene/object editor. The advanced editor is an opt-in surface; it is requested scope even though the follow-up design document recommends limiting the normal dressing workflow.

Current work streams:

- Immutable external library/import review and local offline browsing.
- Processed avatar snapshots, shadow capture/worker, and rendering validation.
- Durable typed commands, bridge receipts, optimistic Wearing, drag/button parity.
- Advanced hierarchy and Inspector with exact targets, revisions and Unity Undo.
- Existing Aelchor reporting client integration using upstream commit 872e73f and the supplied service contract.
- Original menu/appearance/optimization/recipe/fitting/Android work remains in scope.

Reports are sent only by an explicit Send report action. Validation uses mocked transport; no synthetic reports, photos, logs or purchased content are submitted to production automatically.
