Make wardrobe editing clearer and more resilient.

Preset loading failures now keep the selected destination and offer Retry. Late
responses cannot replace a newly created preset. Worn-item menu and part-toggle
changes use explicit Apply settings and Cancel edits; applying both is one Unity
transaction with rollback on failure.

Defaults and Avatar ID drafts are retained per project and avatar in the browser
tab. Upload review lists the avatar, presets, platforms, and whether each upload
creates or updates an avatar. It blocks pending writes and unapplied edits, with
a direct return to the item that needs attention.

Keyboard navigation now passes correctly through Technical details. Preset rows
are compact, Defaults and Help explain the workflow, scrollbars and action labels
are easier to see, and the Unity launcher opens Desktop Library directly. Preview
activity shows actual loading and waiting requests instead of a misleading global
cache percentage. Background high-resolution loading remains enabled.

Validation: 30 focused Unity EditMode tests, 45 JavaScript tests reported by the
Node runner, and 10 Python tests passed. English/Japanese browser fixture QA and
package/archive validation passed. Real VRChat uploads were not performed.

View-only license; see LICENSE and THIRD_PARTY_NOTICES.md.
