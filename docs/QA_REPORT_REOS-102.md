# REOS-102 · Redesign QA report

Chrome (Chromium 1243 via Playwright) only, per Q-102a. Run on 2026-10-28 at 1600×1000, light and dark, signed in as an RTE against the demo database. GitHub Actions was blocked by a billing lock, so every result here is from a local run. Edge, Firefox and Safari are still deferred (REOS-149 to 153).

Evidence lives in `tests/e2e/specs/redesign-qa.spec.ts` (13 tests, new; 12 run and 1 skips itself when the demo live run is over) and `tests/tools/contrast_matrix.py`, on top of the older `a11y-screens`, `a11y`, `gallery`, `analytics`, `calendar`, `approvals`, `audit` and `releases` specs.

| # | Check | Result | Evidence |
|---|---|---|---|
| 1 | Visual match at 1600 px; no shell shift between screens | **Partial** | Shell is the same box on all 16 screens in both themes (spec 1) and has no horizontal page scroll. Sizes follow the tokens (216 px rail, 64 px bar) but the Figma Overview frame measures 248 / 72 / gutter 28 / panel padding 20 (**Q-102c**, owner decision). Layout, panels, metric cards, chips and badges match the Overview frame by eye; only that frame was measured. `docs/ui/mockups/*.png` predate the redesign and were not used as the reference. |
| 2 | WCAG 2.2 AA contrast, both themes | **Pass** (one owner question) | 1,857 rendered texts in light and 1,861 in dark, measured with alpha composited over what is really behind them: lowest **4.54:1** light, **5.15:1** dark, including 9–12 px mono captions and chip text on tints. axe `color-contrast` clean on every screen. A canary element proves the measurement sees the failing tertiary grey (2.75:1). Token matrix (`contrast_matrix.py`): light `text/tertiary` fails everywhere (2.64 to 2.95:1), `status/warning` is 4.41–4.46:1 on four tinted grounds no screen uses. No text uses tertiary (spec fails if one does). Decision still open: **Q-102b**. |
| 3 | Keyboard: nav, tabs, calendar, queue, tables, detail panels, dialogs | **Pass** | Existing specs: keyboard tour of every view with Tab and Enter, whole page reachable, visible focus, no trap (`a11y-screens`); calendar arrows, Page Down, Enter; Approvals queue tabs; audit table and details panel; releases rows; Inspector and drawers open and close with Escape. New: Tab and Shift+Tab never reach the page behind a confirmation dialog, Escape returns focus (spec 3b); chart "View data" from the keyboard (spec 4). |
| 4 | Status never colour alone | **Pass** | Every `os-chip` (spec 3) carries a word, dots are `aria-hidden`, every count badge has an accessible name, colour-only marks fail the spec. Pipeline stages say their state in words and in each stage button's name. |
| 5 | Charts and the pipeline graph have text alternatives | **Pass** | Overview throughput: `svg` named with a one-sentence summary plus "View data" table. Analytics: every chart is `role="img"` named by its finding, "View data" opens a table with headers, SVG only. Orchestration: an ordered list "Stages" of buttons with full names; connectors are decorative. No unnamed large SVG on any screen. |
| 6 | Live regions announce important changes without noise | **Pass** | One global polite announcer; no `aria-live="assertive"`; no `role="alert"` on a healthy page; at most ten status regions per screen (Analytics has seven, one per chart card); the connection word is empty while live (spec 4, 6). |
| 7 | Loading, empty, error state on every screen | **Pass after two fixes** | Spec 5 forces a 500 and a slow API on each screen: loading status shown and error with Retry on 14 screens. Defects found and fixed: Orchestration showed "Loading the releases…" forever when the release list failed; Comm library showed a bare "▲ Request failed" with no Retry. Releases loads from the shell so its loading state is not observable on navigation. Service catalog and Environments are placeholders ("This screen is not built yet", HANDOFF section 9): N/A. Empty states were already covered per screen by the existing specs. |
| 8 | Live data degrades on SignalR disconnect and recovers | **Pass** | Spec 6: socket cut → "Reconnecting…" in the utility bar status, table data kept, restored → status clears and the page refetches. Spec 6b: a 20 s+ outage → "Offline: reload to reconnect" (automatic retries end after about 19 s by design; a reload recovers). |
| 9 | Times UTC-labelled; relative times update | **Pass, one gap in evidence** | Spec 7: every clock time in `main` on all 16 screens has UTC or a zone abbreviation beside it; the shell clock reads `DD MON YYYY · HH:MM UTC` and is within two minutes of real UTC (it is the server's time, D24; its minute rollover is in `tests-unit/shell.test.ts`). Orchestration countdowns and elapsed time tick (spec 7b), but only while the demo's live drill is running: its window is two hours from app start, so the spec skips itself afterwards (it ran and passed on the first run). "N min ago" (Sync health) and "Due in" (Approvals) could not be shown moving: the demo database has no connector cycles or pending approvals, so those screens have no such text. Not verified in the browser. |

## Fixed in `src/ReleaseMgmt.Web`
- `orchestration/Orchestration.tsx`, `App.tsx`: a failed release list shows an error with Retry instead of an endless loading state.
- `CommLibrary.tsx`: a failed library load shows the shared `ErrorState` with Retry.

## Owner decisions raised (docs/QUESTIONS.md)
- **Q-102b** light `text/tertiary` is 2.75:1; built as decoration only; warning text on four tints is 4.41:1.
- **Q-102c** shell sizes in the tokens differ from the Figma frame.

## Not done / limits
- Other browsers (deferred). Only the Overview frame was compared with Figma numerically; other screens were checked against the tokens and components, not frame by frame.
- Relative "ago" and "due in" text (check 9), above.
- Checks use one RTE; role-limited views are covered by the older specs.
- The `src/ReleaseMgmt.Api/logs` cleanup between runs was blocked by a safety check; disk was not an issue (about 30 GB free).

## How to rerun
`python3 tests/tools/contrast_matrix.py`; then, with the app up, `cd tests/e2e && npx playwright test redesign-qa`. The spec prints the per-screen contrast and state tables.
