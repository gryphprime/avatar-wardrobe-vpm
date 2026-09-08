# UX plan milestone and remaining work

**Status: implementation paused at the user’s request.**

The integrated development milestone is commit [`26df488`](https://github.com/gryphprime/avatar-wardrobe-vpm/commit/26df488c57afb8c4c8767120bd4970cd900a95f1) on `dev/ux-plan`. It includes `dev/code-review-fixes`. This is a substantial working development checkpoint; it is not a claim that both plans and their release acceptance gates are fully complete.

Scope remains the September 7 UX and Feature Recommendations, the Drag and Drop / Snapshots / Async Plan, Aelchor reporting integration, and the explicitly requested advanced scene/object editor. Pausing does not remove unfinished requirements from that scope.

## What the milestone delivers

- Exact avatar/copy targeting, revision checks, reversible replacement/removal, configuration-aware Undo, and preserved creator setup.
- External local library with immutable archive originals, reviewed imports, offline browsing, provenance and update-impact records. Folder selection currently discovers ZIP/unitypackage archives inside the folder; it does not ingest a loose Unity asset folder.
- Durable asynchronous commands, pending Wearing rows, failure notices, guarded Retry/Dismiss and recovery after lost responses or reloads.
- Four core drag gestures: files to Library, outfit to Try on, outfit to Wearing, and variant to one exact worn copy. Buttons share the same command path.
- Processed snapshots for supported inputs, a private persistent Unity worker, Front/Three-quarter/Back, Before/After, zoom, retained photos, explicit PNG export and guarded photo refresh/recovery.
- Logical organization of Wardrobe-owned menu controls, processed menu/parameter diagnostics, supported material/static-shape editing, saved appearance recipes and reviewed restore.
- AAO preview/apply, Gesture Manager targeting and an installed MochiFitter native-window handoff.
- Opt-in advanced Scene hierarchy/object editing with Unity Undo and native Inspector access for unsupported fields.
- Bug and selected-item classification reports using the Aelchor contract, explicit consent, bounded metadata and idempotent retry.

See [implementation details](implementation-progress.md), [preview evidence](preview-validation.md), [appearance](appearance-editor.md), and [menu organization](menu-organization.md).

## Evidence at the pause

| Check | Evidence |
| --- | --- |
| Python | 110 tests passed. |
| Browser logic | 11 JavaScript suites passed. |
| C# policy | 13 executable assertions passed. |
| Combined Unity integration | 94 tests passed, zero skipped. |
| Later focused Unity runs | 12 Appearance/tool tests, 3 usage-hashing tests and 14 TryOn tests passed, zero skipped. These overlap the combined suite and must not be added into a unique test total. |
| Large-mesh regression | The million-vertex revision test completed in approximately 9 ms in the isolated fixture. This is not a measurement of the full production avatar. |
| Compilation/package | Runtime, Editor and test assemblies compiled; distribution archive integrity and required assets verified. |
| GitHub CI | [Development checks passed](https://github.com/gryphprime/avatar-wardrobe-vpm/actions/runs/34177178641) for `26df488`. The [separate Unity workflow](https://github.com/gryphprime/avatar-wardrobe-vpm/actions/runs/34177178630) was queued at the last check. |

The package is synced into the working Unity project. The last live check, at 2026-09-08 01:36:50 UTC, found the desktop host responding for the correct project at `http://localhost:49289/`, with the Unity bridge offline. Unity had been restarted by the user; the bridge had not yet been opened. That observation is a timestamped handoff fact, not a current availability guarantee.

No real avatar upload, production test report, or stable release was performed. Render evidence is from owned/synthetic fixtures; there is no verified complete-avatar photograph of the current production scene.

## What remains

### 1. Finish integration validation on representative avatars

Reconnect the bridge after Unity finishes loading, then exercise the complete files → variant → Try on → Wear → Replace → Remove one copy → Undo workflow in a saved test copy of a representative project. Check menu switching in Gesture Manager, saved-appearance restore, Appearance changes, and advanced Scene edits through the browser.

The current saved Upload scene contains enabled VRCFury components that the processed preview pipeline rejects. A native handoff or preserving those components during installation does not prove composed preview compatibility. Establish and validate a permitted supported processing path, or explicitly retain this unsupported-input boundary; never remove creator components merely to obtain a passing photograph. Broader creator/plugin compatibility and the tested version matrix remain important follow-up work.

Recheck the revision-polling fix in the asset-heavy project. The isolated regression passes, but full production-avatar responsiveness has not been measured. Complete visual review of the newest photo-history surface at desktop and narrow widths; its DOM/logic checks passed, while the final visual pass was blocked by unavailable UI automation.

### 2. Finish the wider interaction and appearance scope

| Planned behavior still missing or partial | Current boundary / next work |
| --- | --- |
| Library → matched item → Try On | Folder selection currently filters for ZIP/unitypackage files; loose asset folders are not ingested as products. Import planning includes all archive `Assets` entries rather than a chosen prefab’s dependency closure. Complete authorized folder staging, selected-variant/dependency review and direct post-import navigation to its matched Try On action. |
| Narrow-window dressing layout | At widths up to 780 px, the Wearing sidebar is hidden rather than available as the planned switchable drawer. Complete accessible Library/Wearing drawers and usable drop targets while preserving the photograph and target header. |
| Remaining drop-matrix actions | Worn-row category/order movement, an explicit Remove drop zone and switching-group membership drops are not completed as drag workflows. Existing removal/group buttons and menu organization do not prove those gestures. Add the gestures and matching click/keyboard actions without transform reparenting or changed defaults. |
| Switching-group wording | Some detail/upload controls still say “Menu Group(s)” while the Menu view says “Only one active.” Apply consistent behavioral wording so switching groups cannot be mistaken for layout-only folders. |
| Focused appearance regions and comparison | Current editing uses exact renderer/material slots and named static shapes. Known-compatible Eyes/Hair/Makeup/Nails regions, grouped/pinned shape controls and thumbnail presets are not a completed guided region workflow. The current shape review does not render the candidate. Add isolated visual comparison before Apply, preserving exact source/slot identity. |
| Texture composition | TexTransTool layering/decals/PSD-style workflows are not implemented adapters. Choose and validate a supported package/version and its preview/apply/Undo contract before claiming this part complete. |
| Performance explanations and a metric defect | The UI currently labels a distinct-material count as “Material slots.” Correct the count/label and verify repeated use of the same material across multiple slots. SDK-reported performance findings and clearer per-item attribution/shared-cost explanations also remain to be completed. |
| Confirmed-photo refresh coverage | Automatic refresh is connected to durable Wear/Replace/Remove/Undo completion. Direct Appearance, saved-preset, Scene, native Undo/Redo and dependency changes can mark a photo stale without automatically requesting a replacement. A newly selected avatar without history also requires a manual photograph. Extend the guarded refresh path across those supported changes while preserving manual drafts/history. |
| Worker capacity and operation visibility | The shadow executor has one worker but no enforced queue capacity, and in-memory request records are retained. Add server-side capacity/coalescing and bounded terminal retention; client-side cancellation alone does not prove the stated render bound. Operation receipts also lack distinct phase/structured-warning fields, and fixed 1.5-second polling needs hidden/disconnected backoff. |
| Final composed-menu experience | Processed menu controls and diagnostics are available, and Wardrobe-owned organization is implemented. Finish representative interactive switching validation and assess the richer composed tree/radial view, supported hide actions and handling newly introduced creator entries against the original plan. |

The advanced Scene surface implements pinned-avatar hierarchy and supported Inspector edits. It does not recreate Unity’s full Scene View/gizmo or general authoring environment; confirm the desired additional parity when resuming the advanced-experience request.

The four-core-gesture phase is a useful milestone, not a reason to silently discard the wider matrix or the original appearance requirements.

### 3. Complete acceptance and release evidence

- Measure pending-state latency (proposed p95 <100 ms), durable acceptance (investigate sustained p95 >250 ms), first useful photograph phases, warm cache behavior and worker resource cleanup on clean and asset-heavy projects. Include unfocused/minimized Unity, cold shaders and missing dependencies. Do not treat fixture timings as product guarantees.
- Complete the end-to-end interruption/recovery matrix at real process boundaries, in addition to the existing queue/unit/Unity tests. Record which boundaries are proven rather than labeling every crash case covered by one passing test.
- Observe representative users dressing, replacing a color, removing one of two copies and undoing, resolving a setup issue, and reaching a successful build. Record incorrect-target actions, recovery and Unity round-trips.
- Run a fresh-project installation and representative SDK build for the release commit; resolve/provision the queued Unity CI run. Stable publication remains explicitly gated.
- For reporting, perform the specification’s deliberately small live acceptance check: dashboard filters, filtered CSV, report/status event delivery and consumer-token revocation. This needs an explicitly authorized synthetic report and appropriate operator access. Client tests use mocked transport; the service operator owns persistence, dashboard and feed operations.

### 4. Later integrations explicitly distinguished by the plans

Guided fitter conversion and VRCQuestTools Android preparation remain later/optional work, not prerequisites of the four core drag gestures. MochiFitter currently opens its validated native window only. Conversion profiles, resulting appearance differences, platform preparation and representative build validation are not implemented by that handoff.

Other deliberately deferred areas include motion/physics fit checks, live 3D/video streaming, automatic texture hit testing, arbitrary cross-avatar multi-drag, universal plugin support and complete cross-scene history. Saved recipes currently restore existing exact scene copies and unchanged asset versions; they do not reconstruct deleted outfits or supply a full scene backup.

## Suggested next milestone when work resumes

First prove the supported dressing loop in the actual project environment and decide the current VRCFury preview compatibility path. Then complete the missing interaction/appearance requirements, followed by fresh-install, performance, usability and release evidence. Resume from `dev/ux-plan`; preserve the existing single preset model and all exact-target/Undo/recovery invariants.

No further implementation, Unity mutations, reports, uploads or release actions are authorized by this handoff itself. Resume work when the user asks.
