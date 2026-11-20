# Wardrobe V2 browser checks

Screenshots, layout rules and interaction timings for `Web/wardrobe-v2.*`. Unity ignores this folder because its name ends in `~`.

Requirements: Node 22+, Python 3, and Google Chrome. There are no npm packages.

```sh
node run.mjs                                   # mock server, all window sizes (about 2 minutes)
node run.mjs --url http://localhost:8909/v2    # the running Unity editor
node run.mjs --viewports wide --idle 0 --open  # one size, skip the idle check, open the report
```

Each run writes `results/<time>/index.html`, `report.json`, and one screenshot per step and window size. The exit code is 1 when any rule fails.

**Window sizes:** laptop 1280×800, desktop 1440×900, wide 2000×1035, ultra 2560×1440, phone 390×844.

**Steps:** load, open the item with the most variants, switch looks (then switch back), worn item → Fit, All looks, upload review, Settings, Activity.

Against Unity the steps only read data. The one exception is switching looks, which changes the selected look and then restores it. Nothing is worn, removed, saved or uploaded.

**Layout rules:**
- the side panel and rail keep their CSS width
- no sideways page scroll
- nothing sticks out of its column
- the item preview stays inside the panel
- no `undefined`, `NaN`, `null`, `{0}` or doubled ✓ on screen
- no error toasts or console errors
- labels are not cut off without an ellipsis
- controls don't overlap
- controls are at least 24px on a phone

**Timing budgets:**

| What | Budget |
| --- | --- |
| Item name visible after a click | ≤150 ms |
| Look switch visible | ≤150 ms |
| Catalog refetches when switching looks | none |
| Catalog emptied when switching looks | never |
| Scroll position after switching looks | unchanged |
| `/api/installed` while idle for 15 s | at most once |

**Mock server** (`mock_server.py`): it runs Unity's main-thread requests one at a time, with timings measured on a real project. Redundant requests therefore queue up the same way they do in the editor. `--latency 2` makes the server twice as slow. `--img DIR` serves real `.webp` thumbnails instead of placeholders.

To add a check, add a rule to `audit()` or a step to `runViewport()` in `run.mjs`.
