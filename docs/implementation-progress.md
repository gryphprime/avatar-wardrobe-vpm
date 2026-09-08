# Wardrobe implementation and validation

This branch implements the September 7 UX recommendations and the added drag/drop,
snapshots and asynchronous-operation plan. It also integrates the existing Aelchor
reporting contract and an optional advanced scene/object editor.

The development branch is `dev/ux-plan`. It includes `dev/code-review-fixes`;
development pushes do not publish stable VPM releases.

## Delivered workflows

| Workflow | Behavior |
| --- | --- |
| Choose a target | Pin an exact scene avatar. Project/session/avatar/revision guards protect later operations. One-avatar presentation preserves the existing preset configuration. |
| Add purchased files | External local library accepts ZIP/unitypackage files and chosen folders, retains immutable originals, detects duplicates, and reviews paths, GUID conflicts, executable content and dependencies before copying. Each selected archive has its own result. |
| Browse offline | Library records and a bounded catalog/image cache remain available while Unity is closed or its requests time out, including Python 3.9 socket timeouts. Contextual empty states offer Choose avatar, Add purchased files, Clear filters, Retry or the local library, with English/Japanese copy. No project files are automatically unlinked. |
| Review update impact | Original version hashes and optional creator/product/source metadata stay in the library. Live observations record exact avatar/copy references. Matching prefab bytes and unconfirmed GUID-only version matches are distinguished. |
| Try on | Capture the complete source into owned storage, process supported build transformations in a separate persistent graphics-enabled Unity project, and show Front/Three-quarter/Back, Before/After and zoom. Active-Unity preview is an explicit fallback. |
| Keep photographs | Recent and kept photo history can open, keep/unkeep and explicitly export verified local PNG bytes. Reload restores a cached photo with its original source/target provenance. A confirmed mutation refreshes the current-avatar photograph only after its exact revision is published and earlier changes settle. A manual draft or history selection is preserved. |
| Wear and replace | Buttons and drag gestures share normalized immutable commands. Add another copy, replace one exact copy, and remove one exact copy use the same durable queue and executor. Pending intent remains separate from confirmed Wearing rows. |
| Recover and undo | A SQLite journal and Unity receipts retain accepted intent and outcomes. Failed and Needs review notices remain beside Wearing until dismissed; Activity retains their outcomes and dismissal survives reload. Explicit Retry rechecks a known failed receipt and exact current target before allocating a new command ID. Uncertain acceptance keeps its original ID; Needs review offers review/dismiss rather than replay. A completed edit has a guarded, session-only Undo for its captured native group and all three Wardrobe configuration stores. |
| Understand cost | Processed copies report visible renderers, material slots, triangles, PhysBone/contact counts and distinct texture allocation estimates, with largest texture contributors. These are not exact platform VRAM or FPS predictions. |
| Organize menus | A logical tree organizes supported Wardrobe-owned controls while preserving original hierarchy, parameters, defaults and switching behavior. Unsupported creator menu trees remain read-only. The photo details show controls, parameters and supported parameter diagnostics from the processed copy. |
| Edit appearance | Exact renderer/slot controls cover supported Standard/lilToon tint, texture and bounded numeric fields plus named static shapes. Review precedes Apply. A project-local material copy preserves shared sources, and Unity Undo restores assignment. |
| Save an appearance | A versioned optional recipe lives on the existing preset record. Save/Review restore/Apply operate on existing exact scene copies and validated asset versions, including placement, materials, shape weights, visibility and defaults. Export contains references and values, never asset files. |
| Test/optimize | AAO 1.9.18 public defaults have an actual processed before/after preview and reviewed, undoable Apply. Gesture Manager 3.9.9 receives the pinned avatar through its public Favourite field; entering Play Mode remains explicit. |
| Open a supported fitter | An installed MochiFitter can open through its exact validated public native-menu contract. Wardrobe does not configure or run conversion; source/target selection, profiles and fitting remain in that native tool. Its release version is reported unavailable when the assembly does not identify it. |
| Advanced scene editing | Settings enables exact hierarchy/object selection, rename, active state, transforms, creation, duplication, reparenting, deletion and supported primitive component fields. Unsupported fields open the native Inspector. |
| Report a problem | Help opens a bug report; item details open classification feedback for the selected variant. Only explicit Send contacts Aelchor. Preview, bounded allowlisted metadata, diagnostic opt-out, same-ID retry and receipts follow the supplied contract. |

## Storage and safety boundaries

Unity assets and `.meta` GUIDs are preserved. Failed supported edits restore scene
objects and Wardrobe settings together. Configuration-aware native Undo refuses to
overwrite an externally changed settings file. Asynchronous Undo checks the exact
native group ID/name, target, post revision and scene state before reverting its
captured group; later unrelated work makes it unavailable.

The desktop serializes mutations under a project writer lease. Acceptance is
persisted before execution; a lost response is reconciled by ID. Publishing and
build review are blocked while scene mutations are pending. Cancellation stops
queued work or discards a running photograph at its supported boundary.

Shadow capture uses immutable content-addressed package snapshots, private project
and Library folders, source leases, pins and bounded retention. Only recorded,
owned incomplete capture outputs are cleaned after interruption. A visual revision
ignores supported logical menu folder-label edits; full revisions continue to guard
Wear and other changes. See [preview validation](preview-validation.md).

The macOS worker accepts either the Unity executable or a valid `Unity.app`
bundle, resolving the latter to `Contents/MacOS/Unity`. Live usage hashing is
bounded to supported prefab inputs and reports unconfirmed versions when its
file/read budget is exceeded. The revision traversal skips numeric mesh/avatar
payloads that cannot contain referenced Unity assets, retaining scene, material
and asset dependency checks without walking every mesh array element.

## Validation

The package is exercised in a marked, disposable Unity 2022.3.22f1 project with
copied dependencies. The bootstrap can save an untitled fixture scene only when
that explicit marker is present. Tests never rely on saving a user's scene.

The most recent completed combined Unity suite passed **94 tests with zero skipped**. It includes real scene executor FIFO/identity tests, duplicate-copy
replace/remove, scoped Undo and configuration rollback, actual NDMF processing and
PNG output, unsaved material/controller roundtrip, cache reuse, optional AAO and
Gesture Manager adapters, exact-slot appearance edits and saved-appearance restore.

Later focused runs passed **12 appearance/tool tests**, including MochiFitter's
public native-menu contract, **3 library usage-hashing tests**, and **14 try-on
tests** with zero skipped. The latter include parameter diagnostics, animation
object-curve references and bounded traversal of a million-vertex mesh. Runtime,
Editor and test assemblies also passed the standalone compilation check. These
focused runs validate later changes separately from the earlier combined run;
they do not establish real-avatar capture latency or runtime fidelity.

The external checkpoint suite passed **110 Python tests**, **11 JavaScript suites**, and
**13 executable C# policy assertions**. Release packaging verifies archive integrity
and inclusion of reporting, snapshots, appearance and recipe assets. CI Development
checks passed for checkpoint `4c2af57`; later checkpoints repeat the same checks.
The separate GitHub Unity workflow remains queued; local Unity integration results
are recorded separately and do not imply that queued CI has completed.

Synthetic browser checks cover delayed queue acceptance, offline browsing, report
consent, exact selected-variant reporting, advanced Scene/Menu operations, and
Appearance review/cancel/apply at desktop and narrow widths. No fixture report or
avatar was uploaded to production.

## Supported limits

- The current real saved Upload scene contains enabled VRCFury components. The
  processed preview worker rejects that unsupported setup; it does not strip the
  components or present an unprocessed image as an exact fit result. The real
  project's saved scene was audited read-only, not used as evidence of a successful
  complete-avatar render.
- Static editor photographs do not simulate gestures, animation state machines,
  networking, physics or SDK upload callbacks. Fit remains something to inspect.
- TexTransTool layering, paid fitter conversions and VRCQuestTools Android
  conversion require separately installed, supported native workflows. The UI
  provides explanations and official guidance; it does not download these tools,
  guess UV compatibility or promise conversion success.
- Saved appearance restore requires the original scene copies and asset versions.
  Missing/deleted copies or changed assets require review and a new saved recipe.
  It does not reconstruct arbitrary deleted objects or provide cross-project scene
  history. Ordinary scene saving remains explicit.
- Generated appearance materials remain available after Undo for redo and saved
  recipes. Source textures and globally shared importer settings are unchanged.
- Human usability targets and real VRChat runtime fidelity have not been measured.
  Stable publication still requires the manual fresh-project/build attestation.

Further implementation details: [appearance](appearance-editor.md),
[menu organization](menu-organization.md), and [expanded scope](expanded-scope.md).
