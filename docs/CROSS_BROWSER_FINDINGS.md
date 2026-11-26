# Cross-browser findings (REOS-149)

Input for REOS-150 (Firefox), REOS-151 (Edge) and REOS-152 (WebKit/Safari). Nothing in the product was fixed in REOS-149.

**How this was produced.** `tests/e2e` (275 specs) run once per engine against a fresh host started through `start.py` (demo seed, one worker), on 2026-11-26 in the Linux dev container, Playwright 1.63:
`E2E_CROSS_BROWSER=1 npx playwright test --project=<name>`.

| Engine | Result (full run) | Notes |
|---|---|---|
| chromium (baseline, the per-PR gate) | 274 passed, 1 skipped, 0 failed (26.5 min) | the skip is an existing data-condition skip in `analytics.spec.ts`/`redesign-qa.spec.ts`, not a browser skip |
| firefox 155 | 244 passed, 1 skipped, **30 failed** (49 min) | |
| webkit (Playwright build) | 252 passed, 1 skipped, **22 failed** (31 min) | Linux WebKit is not Safari: macOS runs in CI will say more |
| msedge | **not run** | Edge cannot be installed or tested in a Linux container. It is Chromium-based, so a large difference is not expected, but nothing here says so. The first run of the `cross-browser` workflow on `windows-latest` is the evidence for REOS-151 |

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

## Edge (REOS-151) — not tested here

No result. The `msedge` project (`channel: 'msedge'`) is defined and runs in the `cross-browser` workflow on `windows-latest`, where Edge is installed. Treat its first run as the findings for REOS-151; add them here and skip with `REOS-151` as above.

## What is common to REOS-150 and REOS-152

The 21 clipboard failures per engine are one cause with one fix, the same for both stories: grant the clipboard permissions only when `browserName` is Chromium or Edge, and verify the copy another way on the other engines (for example from what the page puts in the DOM; this has not been tried here). Doing it once removes 21 skips in each engine and un-masks the rest of those tests.

## Not covered

Safari itself (macOS WebKit differs from the Linux build; the macOS `cross-browser` job covers it), Firefox on macOS/Windows, mobile engines, and anything the specs do not exercise.
