Speed up thumbnail loading and improve wardrobe recovery.

Cached high-resolution images now load through a separate bounded queue, so they
can appear while Unity generates other previews. Current fingerprinted thumbnails
use browser caching, and selected previews retain a render slot. Visible thumbnail
generation has a shorter cooldown without increasing speculative background work.

Preview retry and selected-item error states recover reliably. Worn-copy controls
handle incomplete scene identity data, and saved appearance waits for a valid
inspection revision before enabling Save.

Also includes asynchronous shared avatar fingerprinting, complete library
pagination, bounded background operation queues, persistent write receipts,
transactional settings import, and scene-scoped preset settings.

Validation: 105 Unity EditMode tests passed with graphics in a disposable project;
3 optional integrations skipped. Repository Python and JavaScript regressions
passed, including controlled preview latency, retry, identity, and appearance tests.
The preview queue benchmark improved from about 302 ms to 1–2 ms for cached images
behind two simulated 300 ms renders; this is not a live avatar render benchmark.

View-only license; see LICENSE and THIRD_PARTY_NOTICES.md.
