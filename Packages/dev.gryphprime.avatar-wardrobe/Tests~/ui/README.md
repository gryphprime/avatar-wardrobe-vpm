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

**Steps:** load, open the item with the most variants, open the look picker and switch looks (then switch back), worn item → Fit, the Looks page, check two looks, upload review (with both thumbnail dialogs), Settings, Activity. On the mock, the first window size also runs **queue**: wear three items quickly, wear one after a Unity reload, wear one while Unity is busy, opening another item meanwhile, and create a look from the item panel and wear into it before Unity has made it.

Against Unity the steps only read data. The one exception is switching looks, which changes the selected look and then restores it. Nothing is worn, removed, saved or uploaded; the queue step only runs on the mock.

**Layout rules:**
- the side panel keeps its CSS width
- no sideways page scroll
- nothing sticks out of its column
- the item preview stays inside the panel
- no `undefined`, `NaN`, `null`, `{0}` or doubled ✓ on screen
- no red toasts (fail on the mock, warning against Unity) and no console errors
- labels are not cut off without an ellipsis
- controls don't overlap
- controls are at least 24px on a phone
- headings and labels are not covered by other elements
- look cards on the Looks page are square

**Timing budgets:**

| What | Budget |
| --- | --- |
| Item name visible after a click | ≤150 ms |
| Look switch visible | ≤150 ms |
| Catalog refetches when switching looks | none |
| Catalog emptied when switching looks | never |
| Scroll position after switching looks | unchanged |
| Upload button with two looks checked | names 2 looks |
| `/api/installed` while idle for 15 s | at most once |
| Clicking Wear | shown in the modal and the Wearing list at once, page blocked ≤50 ms |
| Queued changes | status reads "Saving…", all applied within 15 s |
| Change after a Unity reload | applies by itself |
| Change while Unity is busy | shown as waiting; other items still open |

**Mock server** (`mock_server.py`): it runs Unity's main-thread requests one at a time, with timings measured on a real project. Redundant requests therefore queue up the same way they do in the editor. `--latency 2` makes the server twice as slow. `--img DIR` serves real `.webp` thumbnails instead of placeholders.

Writes behave like Unity's queue: a `202` with a receipt ID, applied one at a time (`--write-ms`, default 400) and polled through `/api/write_result`. `POST /mock/reload` starts a new session, so writes signed with the old one get Unity's `409 session-changed`. `POST /mock/busy?ms=N` stalls the write worker, like Unity importing or compiling.

To add a check, add a rule to `audit()` or a step to `runViewport()` in `run.mjs`.
