# Avatar Wardrobe experience fixes — September 12, 2026

This follow-up addresses the friction review against v1.0.20.

## Findings and corrections

| Finding | Correction |
| --- | --- |
| Failed preset reads could silently target Common | Validate preset and membership responses, retain the destination, disable dependent actions, and show inline Retry. A removed destination stays explicitly unavailable. |
| Late preset reads could overwrite a new preset | Invalidate old reads during creation and preserve the created destination through follow-up failures. |
| Defaults and Avatar ID drafts disappeared across avatar/session changes | Keep separate project/avatar drafts in sessionStorage with restored/unsaved status and discard. Stable saved-avatar identity survives a Unity session change; unsaved identities remain session-bound. |
| Menu/toggle edits had unclear save semantics | Existing worn items use fixed-footer Apply settings and Cancel edits; choices persist while browsing. New-item choices apply with Wear. Menu and toggle edits commit as one Unity transaction, preserving other presets and rolling back a partial failure. |
| Unapplied item edits could bypass upload review | Block upload with an explicit draft message and Review item edits action that returns to the retained draft. Ignore drafts for other avatars and no-longer-worn copies. |
| Tab from Technical details looped to Close | Respect native summary focus and skip hidden, disabled, inert, and closed-details controls in the dialog focus trap. |
| Generic upload confirmation concealed important consequences | Review the current avatar, each preset, platforms, create/update behavior, release status, and batch cancellation limitations. Recheck target/config/draft state before committing. |
| Cached high-resolution percentage resembled stuck progress | Show actual active and waiting requests in the HUD; put cache coverage and background warming explanation in Activity. Include retry-backoff jobs in the waiting count. |
| Dense preset rows and unclear Defaults/Help | Collapse preset editors, expand one at a time, group Defaults, explain dependent options and save behavior, and provide task-oriented Help. |
| Hidden scrolling, small labels, low action contrast | Restore visible scrollbars, improve operational type sizes and button/hover contrast, and keep Apply/Cancel accessible in the footer. |
| Try on setup pointed users to the wrong surface | Explain the Desktop Library requirement and add its direct button to the regular Unity launcher. |

## Verification

- Node `--test tests/*.test.js`: **45 passed, 0 failed**. Includes seven destination/draft-scope cases, upload review and recovery checks, focus-trap cases, and queued item-settings receipt behavior. Existing preview/lifecycle checks pass.
- Python discovery: **10 passed**.
- Unity 2022.3.22f1 disposable fixture: **30 passed, 0 failed** using `PresetAppearanceTests;ReviewRegressionTests`. Exercises menu-only edits, toggle-only edits, multiple copies, other-preset isolation, invalid/ambiguous paths, rollback, and existing preset regressions. Runtime, Editor, and Test assemblies compile.
- English/Japanese upload copy: all **73** fallback keys present, no duplicate keys, placeholders match.
- Browser via computer use against source and synthetic API fixtures: failed preset load preserves Everyday and disables actions; Retry recovery; no settings mutation before Apply; failed Apply retains edits and allows retry; successful Apply; keyboard Tab across details; retained Defaults after switching avatars; concrete single/batch upload review; dirty-draft upload blocking; item-edit review returns to the correct draft. No real upload commit clicked.
- Layout: desktop and 780 × 850 window confirmation, including Japanese Defaults, with no horizontal document overflow. Temporary viewport override reset and QA tab/server closed.
- Disposable package built with test version 1.0.0; manifest/version parity, Unity metadata/GUID uniqueness, archive CRC, exact source bytes, and sidecar/cache exclusion pass. The test version is not a published version.

## Scope limits

Browser mutation and upload flows used synthetic API fixtures; real scene behavior
was verified separately in the disposable Unity project. The synthetic server
lacks durable write-receipt recovery, so an intentionally failed write can leave a
fixture-only pending indicator after reload; actual receipt behavior is covered by
the request-queue tests. Fresh VCC/ALCOM install, Windows runtime, real large-avatar
cold/cached timings, and real VRChat upload smoke tests are not claimed here.
GitHub's licensed Unity runner is still disabled; Unity results above are local.
