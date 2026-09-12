# Avatar Wardrobe — VPM

Browser-based wardrobe indexing, outfit installation, generated toggles and
preset uploads for Unity 2022.3 VRChat avatar projects.

## License

View-only. Installation and use require separate permission from gryphprime.
See LICENSE and THIRD_PARTY_NOTICES.md. Availability in a VPM listing does not
grant permission to use the software.

## Install (authorized users)

Add https://gryphprime.github.io/avatar-wardrobe-vpm/index.json to ALCOM or VCC.
Install Avatar Wardrobe from the regular package list. VPM resolves VRChat SDK
Avatars and Modular Avatar; add the Modular Avatar repository if necessary.
Legacy Assets/OutfitToggleGenerator installations are migrated automatically.
Open Tools > Avatar Wardrobe after Unity compiles.

Windows includes a private Python runtime. macOS requires python3 on PATH;
optional Apple Intelligence features require macOS 26 or later.

The local release candidate passes Unity 2022.3.22f1 compilation and the
Avatar Wardrobe EditMode suite (105 passed, three optional integrations skipped).
A fresh VCC/ALCOM installation and a real avatar upload remain release gates.

## Local experimental views

Appearance and Menu are feature flagged off by default. To enable either view
only in your browser, run the corresponding override in the wardrobe page's
browser developer console, then reload:

```js
localStorage.setItem("wardrobeFeatureAppearance", "1");
localStorage.setItem("wardrobeFeatureMenu", "1");
location.reload();
```

Enable each independently by setting only its key. To restore the defaults:

```js
localStorage.removeItem("wardrobeFeatureAppearance");
localStorage.removeItem("wardrobeFeatureMenu");
location.reload();
```

Overrides are scoped to this browser profile and origin (including port), and
are never written to project settings or exported presets. Missing, invalid, or
unavailable storage leaves both views disabled. These flags gate the browser
views; they do not disable the underlying Unity APIs.

### Queued browser writes

The shared browser client requests asynchronous writes with `X-Wardrobe-Queue: 1`.
Outfit installs/removals, menu organization, part toggles, scene/appearance edits,
and preset/configuration edits return HTTP 202 with a `writeJob` receipt.
`GET /api/write_result?id=...` reads serialized progress without waiting for the
Unity main thread. The browser keeps navigation available, displays the number
of pending changes, and refreshes the affected view after the original result
arrives. Existing clients without the header retain synchronous responses.

Writes share the operation dispatcher and execute serially, at most one per
Editor update. Reads and writes alternate under load. Compilation, imports,
Play Mode, and active builds/uploads pause dispatch; queued actions retain their
original session, avatar, and language. Build reviews reject pending writes.
Unity APIs still run on the main thread: an individual prefab or asset operation
cannot be preempted and may briefly stall the Editor. This queue does not move
Unity object access to worker threads.

Receipts are session-local and retained for 30 minutes. A disconnect/reload can
leave completion uncertain; refresh and inspect Unity before repeating a write.
The client never automatically resubmits accepted mutations. The existing durable
outfit-operation flow in the desktop host continues to use its own journal.

Focused checks: `python3 -m unittest discover -s Tools/Tests -p test_write_queue.py -v`
and `node Tools/Tests/test_write_queue.cjs` from the project root.

### Main-thread offloading

Bulk work now runs on workers while Unity-only access remains on the Editor thread:

- Catalog file reads, JSON parsing, validation, and record normalization run in a single background load. Readers keep the last complete catalog; replacement files are checked again before publication, and dirty events received during loading are preserved.
- Display fingerprints collect scene and referenced-asset data in approximately 2 ms slices. String hashing and settings-file hashing run on workers. Unchanged fingerprints are reused, invalidated by object/project/Undo events, and reconciled fully every 30 seconds. Mutation preconditions still use strict current-state checks. Preset appearance summaries use the display fingerprint instead of synchronously traversing the avatar.
- Installed-prefab provenance hashing runs on the HTTP worker. Unity dependency versions are checked again before those hashes are returned.
- Shadow capture retains Unity serialization, prefab saving, and cleanup on the main thread. Dependency/package copying, checksums, cache-lock waits, manifests, and output cleanup run on workers. Changed source files, changed avatars, and ended sessions fail closed.
- Thumbnail and snapshot renders copy managed pixel arrays before destroying textures. Workers compress PNGs and atomically save them. Thumbnail encoding is bounded to eight pending jobs; retries delete files after earlier writes settle.
- Cache initialization/clearing, indexer shutdown waits, progress/log reads, thumbnail file responses, and diagnostic directory enumeration run off-thread. Required operation journal writes finish before the next operation is eligible to execute.
- Queued writes retain exclusive ownership across awaits; reads can still run. Results are serialized off-thread and late completions from stopped sessions are rejected.

The synchronous `WardrobeShadowCapture.Capture` and `WardrobeTryOnWorker.Render` entry points remain for existing isolated tests/compatibility; browser operations use their asynchronous equivalents. Unity object serialization, AssetDatabase/import operations, rendering/readback, Undo, strict edit preconditions, and settings commits that participate in immediate rollback remain synchronous. Do not move those calls into `Task.Run` or report a transaction complete before its files are committed.

Validation from the project root:

```sh
python3 -m unittest discover -s Tools/Tests -p test_write_queue.py -v
python3 -m unittest discover -s Tools/Tests -p test_background_value.py -v
node Tools/Tests/test_write_queue.cjs
```

Editor coverage is in `OffloadTests` (revision identity, worker PNG pixels/alpha, catalog replacement/deletion/schema handling) and the updated `OperationExecutorTests`. The standalone checks and compilation passed during implementation; the Editor-only tests were compiled but could not be executed through the available Test Runner automation. No upload or live-avatar performance benchmark was performed.


### Thumbnail performance and release checks (2026-09-12)

The browser schedules cached thumbnail reads separately from Unity renders: four
cache probes can proceed while up to two grid render requests wait on the Editor,
with a third render slot reserved for the selected item. Low-resolution previews
still appear first when a 512px image is unavailable. Fingerprinted, current-session
responses permit browser caching; explicit retries bypass cached responses and
replace pending requests without replaying old work.

Visible high-resolution renders now wait one render duration (minimum 100 ms)
between jobs, instead of three. Speculative renders retain the existing longer
idle interval. Low/high PNG encoding queues have independent limits of eight/four;
capacity is checked before rendering so a saturated queue cannot discard a preview
and leave it permanently marked as attempted.

The selected-item gallery delegates loading and failure states to the preview
pipeline. Worn-copy controls tolerate responses without exact instance IDs and
keep removal disabled until a valid copy can be selected. Saved appearance controls
wait for Unity's asynchronous inspection revision before enabling Save and refresh
that readiness for up to 30 attempts without blocking the page.

Focused regression commands from the project root:

```sh
node Tools/Tests/test_preview_pipeline.cjs
node Tools/Tests/test_worn_copy_identity.cjs
node Tools/Tests/test_preset_appearance_readiness.cjs
python3 Tools/ReviewTests/package_preflight.py Packages/dev.gryphprime.avatar-wardrobe
```

The controlled scheduler regression delivered 12 cached images behind two simulated
300 ms Unity requests in about 1–2 ms, versus 302 ms before the change. This measures
queue scheduling, not real prefab render time. The full EditMode suite was run with
graphics in a disposable Unity project. Live large-avatar latency and Windows runtime
validation still need target-machine testing. The package version is unchanged;
carry these installed-package edits into the source repository and assign the release
version before publishing.

High-resolution previews continue while the browser is unfocused or hidden. The
last visible grid and its look-ahead queue keep warming while the page is open;
closing the page stops demand. Unity compilation, importing, play mode and uploads
still pause rendering. If the browser fully suspends or discards the page, the
preview lease expires after two minutes without a heartbeat.
