Keep high-resolution previews loading while Avatar Wardrobe is unfocused.

Switching apps, switching browser tabs, or minimizing the browser no longer pauses
thumbnail requests and cache upgrades. Unity continues warming the last visible
catalog grid and its existing look-ahead queue. Hidden pages send a light heartbeat
without refreshing the full catalog; the server allows two minutes between beats
to tolerate browser timer throttling. Closing the page releases the preview lease.

Rendering still yields to Unity's foreground work and pauses during compilation,
imports, play mode, and uploads. Image request limits, cache bounds, and selected
preview priority are preserved. Activity and Settings explain the new behavior in
English and Japanese.

Validation: all 11 repository JavaScript test scripts and 10 Python tests passed,
including 16 preview scheduler cases and 2 page-lifecycle cases. Supported Unity
Runtime, Editor, and Test assemblies compile. Source and package preflight checks
passed; local browser fixture loaded thumbnails without console errors. Hidden-tab
and focus-loss behavior was verified with controlled browser API fixtures in tests.

View-only license; see LICENSE and THIRD_PARTY_NOTICES.md.
