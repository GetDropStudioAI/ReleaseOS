# Cross-browser findings (REOS-149)

Input for REOS-150 (Firefox), REOS-151 (Edge) and REOS-152 (WebKit/Safari). Nothing in the product was fixed in REOS-149.

**How this was produced.** `tests/e2e` (275 specs) run once per engine against a fresh host started through `start.py` (demo seed, one worker), on 2026-11-26 in the Linux dev container, Playwright 1.63:
`E2E_CROSS_BROWSER=1 npx playwright test --project=<name>`.

| Engine | Result (full run) | Notes |
|---|---|---|
| chromium (baseline, the per-PR gate) | 274 passed, 1 skipped, 0 failed (26.5 min) | the skip is an existing data-condition skip in `analytics.spec.ts`/`redesign-qa.spec.ts`, not a browser skip |
| firefox 155 | 244 passed, 1 skipped, **30 failed** (49 min) | |
| webkit (Playwright build) | 252 passed, 1 skipped, **22 failed** (31 min) | Linux WebKit is not Safari: macOS runs in CI will say more |
| msedge | **275 passed, 0 failed, 0 skipped** (windows-latest, see the Edge section) | not run in the Linux container; the `cross-browser` workflow run on 2026-11-27 is the evidence |

Each failure below is a spec failure (light and dark theme variants counted separately). Every one of them is currently skipped with its ticket key in the spec, so the `cross-browser` job is green and a *new* regression shows up. Remove the skip when the story fixes the cause.

## Firefox (REOS-150) — 30 failures, 7 causes

| Spec (line) | Failures | Error | Likely cause |
|---|---|---|---|
| `calendar.spec.ts` (all 9 tests, light and dark) | 18 | `browser.newContext: Unknown permission: clipboard-read` | The spec grants `clipboard-read`/`clipboard-write` through `test.use({permissions})`; Firefox has no such permissions in Playwright, so the context is never created. **Everything else in these 18 tests is untested on Firefox** |
| `audit.spec.ts:30` (light, dark) | 2 | `browserContext.grantPermissions: Unknown permission: clipboard-read` | Same. The test also reads `navigator.clipboard.readText()`, which needs another way to check the copy |
| `comms.spec.ts:76` | 1 | `browserContext.grantPermissions: Unknown permission: clipboard-read` | Same |
| `a11y-screens.spec.ts:213` ("Dark" and "Light" choice from the keyboard) | 2 | `Tab never reached /^Dark$/ in 80 presses` (and `/^Light$/`) | Reproduces alone. The keyboard walk reaches `div.os-sidebar-top` as a tab stop (Firefox makes scrollable containers focusable), which the Chromium run does not; the account panel's button is not reached in 80 presses. Not diagnosed further |
| `a11y-screens.spec.ts:111` (overview; light, dark) and `:153` (train plan; light, dark) | 4 | `overview (light) tab order: focus is cycling among 1 elements (keyboard trap?)` — expected > 3, received 1 | Reproduces alone. After about 60 stops focus sits on one element and Tab does not leave it. Probably the same Firefox focus behaviour as above (scroll container or an element that keeps focus); not isolated |
| `qa-142.spec.ts:210` (light) | 1 | `Test timeout of 600000ms exceeded` | **Slow, not wrong**: alone it passes in 1.7 minutes; in the full run it exceeded its 10-minute budget. Firefox is slow with the 16 screens × error and slow-API states; no product defect seen |
| `redesign-qa.spec.ts:294` | 1 | `clock times with no UTC or zone label nearby` — received `Orchestration: "0:27:16"` | **Did not reproduce alone (passes).** Likely a timing-dependent countdown shown without its UTC label in the full run's state; may also fail on Chromium given the same state |
| `releases.spec.ts:119` | 1 | `Test timeout of 60000ms exceeded` | **Did not reproduce alone (passes in 4.9 s).** Likely order- or load-dependent |

Counts: 18 + 2 + 1 + 2 + 4 + 1 + 1 + 1 = 30.

## WebKit (REOS-152) — 22 failures, 4 causes

| Spec (line) | Failures | Error | Likely cause |
|---|---|---|---|
| `calendar.spec.ts` (all 9 tests, light and dark) | 18 | `browserContext.newPage: Unknown permission: clipboard-write` | WebKit rejects the clipboard permissions the spec grants. **Everything else in these tests is untested on WebKit** |
| `audit.spec.ts:30` (light, dark) | 2 | `browserContext.grantPermissions: Unknown permission: clipboard-write` | Same |
| `comms.spec.ts:76` | 1 | `browserContext.grantPermissions: Unknown permission: clipboard-write` | Same |
| `redesign-qa.spec.ts:212` ("API failure shows an error state with Retry on every screen") | 1 | `FAIL  Analytics: loading state shown; error shown, Retry no: Your workspaces could not be loaded Request failed (500)` (the other 15 screens pass) | The Analytics error state has no Retry button in this run. Not seen on Chromium or Firefox. Possibly WebKit-specific rendering or request timing of the workspaces call; needs a look at the Analytics error path in WebKit |

Counts: 18 + 2 + 1 + 1 = 22.

## Edge (REOS-151) — 275 passed, 0 failed

`cross-browser (msedge on windows-latest)`, run 45911834177 (branch `reos-xb-webkit-edge`, 2026-11-26/27, Edge on the Windows runner, Playwright `channel: 'msedge'`): **275 passed, 0 failed, 0 skipped (30.7 min)**. No Edge-only skip exists in any spec, so every spec ran, clipboard ones included (Edge is Chromium and takes the clipboard permissions). Nothing to fix and nothing skipped with `REOS-151`. The Windows log shows a `ConnectionAbortedError [WinError 10053]` from `start.py`'s health endpoint at the end of the run (the harness closing a polling connection at shutdown); no test was affected.

## Safari evidence: macOS WebKit (REOS-152) — 238 passed, 16 failed, 21 skipped

Same run, `cross-browser (webkit on macos-latest)` (Playwright's WebKit build on Apple silicon; closer to Safari than the Linux build, but still not Safari itself). The 21 skips are the clipboard skips (owned by `reos-xb-firefox`). The 16 failures, three causes:

| Spec (line) | Failures | Cause | Status |
|---|---|---|---|
| `train-close:29`, `deployments:185`, `gallery:69`, `orchestration:139`, `redesign-qa:341`, `synchealth:30` (light, dark) | 7 | After Escape the button that opened the dialog or drawer is not focused (`toBeFocused`, received `inactive`). **Product defect**: Safari does not focus a button when it is clicked, so `document.activeElement` was `<body>` when the dialog opened and focus could not be given back | **Fixed** with `focusOpener()` in `focus.ts` (falls back to the control last pressed with a pointer), used by `ConfirmDialog`, `useRestoreFocus`, `InspectorFrame` and `StepDrawer`. `safari-focus.spec.ts` emulates Safari's click on every engine; it fails without the fix and passes with it. **Not yet re-run on macOS** (the temporary trigger is reverted), so the nightly run is the proof |
| `analytics:179`, `gallery:33`, `importexport:129` (light, dark) | 4 | The spec walks by Tab to buttons and links. Safari on macOS does not Tab to them unless "Press Tab to highlight each item" is on (Option+Tab otherwise). Not a product defect | Skipped on macOS WebKit with `REOS-152`; keyboard access in Safari is **untested** by these specs |
| `a11y-screens:111` (light, dark) and `:215` (Dark, Light choice) | 4 | Same Tab behaviour (`Tab never reached /^Overview$/ in 80 presses`, `/account and appearance/`) | **Not touched**: `a11y-screens.spec.ts` belongs to `reos-xb-firefox`. Until it skips or adapts them the macOS WebKit job stays red on these 4 |
| `deployments:61` (light only) | 1 | `locator.fill` on the filter search box timed out at 60 s; the dark run and Edge pass; Linux WebKit passed it | Skipped on macOS WebKit, light only, with `REOS-152`. **Not diagnosed** (trace is in artifact `playwright-webkit`, run 45911834177); may be a one-off |

The Linux-WebKit finding `redesign-qa.spec.ts:212` (Analytics error state without Retry) was fixed earlier on this branch: the scope notice "Your workspaces could not be loaded" had no Retry, and the check passed or failed on whether the main error state (which has one) had rendered yet. It now has Retry, the skip is gone, and the spec passes on Linux WebKit (12 passed, 1 existing data skip) and on macOS (no longer in the failure list).

## What is common to REOS-150 and REOS-152

The 21 clipboard failures per engine are one cause with one fix, the same for both stories: grant the clipboard permissions only when `browserName` is Chromium or Edge, and verify the copy another way on the other engines (for example from what the page puts in the DOM; this has not been tried here). Doing it once removes 21 skips in each engine and un-masks the rest of those tests.

## Not covered

Safari itself (macOS WebKit differs from the Linux build; the macOS `cross-browser` job covers it), Firefox on macOS/Windows, mobile engines, and anything the specs do not exercise.
