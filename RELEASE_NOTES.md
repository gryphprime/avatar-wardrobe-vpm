Add direct Upload Avatar in single-avatar mode (PC).

Upload the selected scene avatar without creating or depending on an Outfits
folder or named presets. Review new/existing identity, run a local Build Check,
follow progress, or request cancellation. New uploads generate a thumbnail when
none exists; existing remote thumbnails are preserved. Temporary build scenes
are saved automatically and removed afterward. Successful uploads copy the
Blueprint ID back to the source; save your scene to retain it.

The review includes explicit ownership confirmation, scoped batch-style SDK
confirmation handling, avatar preview, status colors, reduced-motion support,
and a top-right close button. Upload stays disabled until ownership is checked.

Validation: Unity compilation; synthetic-avatar staging and cleanup tests on
success, failure and cancellation; thumbnail generation/cache tests; browser
flow and consent tests. A complete real SDK upload was not performed by these tests.

View-only license; see LICENSE and THIRD_PARTY_NOTICES.md.
