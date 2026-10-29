# REOS-142 · Redesign QA, every screen, both themes, and the exported files

Follows `docs/QA_REPORT_REOS-102.md` and closes the gaps it listed. Chrome (Chromium 1243 via Playwright) only, per Q-102a. Run on 2026-10-29 at 1600×1000, light and dark, against the demo database. GitHub Actions is blocked by a billing lock, so every result here is from a local run (final run numbers at the end). Service catalog and Environments are out of scope (REOS-155/156, other sessions). Q-102b (tertiary text) and Q-102c (shell sizes) are still owner decisions: no designed colour or shell size was changed.

Evidence: `tests/e2e/specs/qa-142.spec.ts` (new, 16 tests), `tests/ReleaseMgmt.Api.Tests/ExportQualityTests.cs` (new, 9 test cases), on top of the REOS-102 spec, which still passes.

## Gap 1 · Screens other than Overview against their Figma frames
Figma MCP was available. The seven light frames of file `uxMlQ7LTNoW8sIKWSIeY98` were read with `get_metadata` (frame, then children) and compared with the running app.

| Screen (frame) | Result | Measured |
|---|---|---|
| Portfolio & calendar (6:4990) | **Pass** | rail 216, bar 64, heading x 248 y 92, content 1320, 22 px between blocks (frame 20 to 22) |
| Release detail (6:5279) | **Pass** | same shell and heading position, 22 px rhythm; content column narrower because the inspector sits beside it |
| Approvals (6:5838) | **Pass, one difference** | shell, heading, width and rhythm match. The app has four metric cards above the queue; the frame has none (Q-142a) |
| Deployments (6:6058) | **Pass** | shell, heading, width and rhythm match (shown with its empty state: the demo has no deployment) |
| Audit history (6:6333) | **Pass** | shell, heading, width and rhythm match |
| Overview (6:4718), Orchestration (6:5552) | **Owner decision** | the frames are drawn at 248 / 72; the app keeps 216 / 64 (Q-102c). Asserted so a change is noticed |

**Correction to Q-102c:** it said the other six frames share the Overview frame's 248 px rail. Only Orchestration does; five of the seven frames already equal the token shell. Recorded as **Q-142a** with a recommendation (keep the tokens, redraw the two frames).
Block heights differ from the frames (the frames hold sample data); they are printed by the spec, not asserted. Inside a block (cards, table columns, chip sizes) was not measured frame by frame: the one comparison made is the layout skeleton. The other three frames (Dark variants) share the light frames' structure and were not measured separately.

## Gap 2 · Relative times observed moving
| Text | Seeded how | Observed |
|---|---|---|
| "N min ago", Sync health | `POST /api/v1/dev/sync/connector` (existing dev endpoint) with `lastSuccessMinutesAgo` 5 and 12, then a fake clock | **Pass.** Jira reads "5 min ago", ServiceNow "12 min ago"; after 3 minutes pass without a reload they read "8" and "15" |
| "Due in" / "Overdue by", Approvals | The demo's Gated "Card portal" gives the Release Manager a timed Go/No-Go (window start); the clock is set 31 minutes before it | **Pass.** "Due in 30m" → "Due in 20m" → "Overdue by 4m", the word "Overdue" present, and the "Due within an hour" card changes with it. The test fails, not skips, if that seed is gone |

**Defect found and fixed:** Sync health computed "N min ago" while rendering and had no timer, so it only moved when something else happened to re-render the page. In the full run the Jira row moved and the ServiceNow row stayed at "12 min ago". It now re-renders every 30 seconds (`SyncHealth.tsx`), as Approvals already did.
The REOS-102 note "Orchestration countdowns tick only while the demo's live drill runs" is unchanged.

## Gap 3 · Role-limited views
Four roles (Viewer, Governance Officer, RTE, Release Manager) × every built screen (14 + Sync health + Inbox) + a release page, in light and dark: each opens with no error alert and has **no axe serious or critical violation** (8 tests, all pass). A Viewer is offered no enabled write control on any of the 17 pages; the RTE is offered "New train" (control).

| Finding | Outcome |
|---|---|
| Release page checklist offered "Paste tasks" and "Add task" to a Viewer (the server refuses: Plan policy) | **Fixed**: shown to planners only (`Planning.tsx`, `ReleaseDetail.tsx`) |
| "Mark done" / "Reopen" shown to a Viewer | **Kept, by decision REOS-53** (a task's owner may complete it): Q-142b |
| "Create link" (own calendar feed) and "Resolve →" (opens the release) shown to a Viewer | Allowed, listed in the spec: Q-142b |
| "Start rehearsal run" on Orchestration shown disabled to a Viewer | Accepted (disabled is not offered); Q-142b |

## Gap 4 · Exported files
Every file was exported from the running demo and read (`pdftotext`, `pdfplumber`, `openpyxl`, the raw ICS), then the checks were made permanent in `ExportQualityTests` (Api.Tests, next to the exporter tests it extends).

| Format | What is asserted | Result |
|---|---|---|
| PDF ×5 (release report, run sheet, evidence pack, scorecard, portfolio) | opens; title and author; Letter; "Page n of m" on every page; an instant labelled UTC on page 1; every text colour is one of the four `PdfStyle` colours (so none is hard-coded) and each is ≥ 4.5:1 on white | **Pass.** Defect fixed: the evidence pack read "planned.Not rehearsed." |
| XLSX, all 19 grids and the analytics workbook (25 sheets) | header row of words, none empty, bold; first row frozen; grids have a filter; instants as text ending in Z; Parameters sheet says UTC | **Pass.** Note: Parameters freezes 14 rows (Q-142c) |
| CSV, all 19 grids | UTF-8 BOM, CRLF, same header as the XLSX, whole-cell instants carry the Z | **Pass** |
| ICS (all, mine, freezes, one train) | parses; CRLF; lines ≤ 75 octets; UID, summary and UTC DTSTAMP on every event; timed events UTC, others whole days; no TZID | **Pass** |

Not covered: PDFs are not tagged for screen readers (PDFA_SPIKE decision); the PDF's visual layout was looked at on two pages, not every page.

## Gap 5 · Loading, error and empty on every screen, both themes
| State | Method | Result (16 screens × 2 themes) |
|---|---|---|
| Loading | API slowed 2.5 s; a polite `role=status` with words, axe on it | **Pass** on 14; Releases and Orchestration load from the shell at start, so their loading state is not observable on navigation (as in REOS-102) |
| Error | API forced to 500; an alert with words and Retry, axe on it, then the API restored and **Retry clicked**: the alert goes away | **Pass** on 16 after one fix: **Comm library** Retry only reloaded the library, so an error from the token list stayed on screen; Retry now reloads everything the page read at start (`CommLibrary.tsx`) |
| Empty | a freshly created workspace (no data): every screen opens, no alert, no `NaN`/`undefined`/`null`/`Invalid Date`, axe clean | **Pass**. Fourteen screens say so in words ("No releases yet", "Nothing open", "No deployments yet", …); on Teams & settings and Analytics the spec's phrase match found only the word "empty", so those two were checked for errors, broken values and axe but not read for an explicit empty message |

## Fixed in this change
- `Planning.tsx`, `release/ReleaseDetail.tsx`: "Paste tasks" and "Add task" for planners only.
- `SyncHealth.tsx`: "N min ago" re-renders every 30 s.
- `CommLibrary.tsx`: Retry reloads the whole page's data.
- `Exports/Documents/EvidencePackDocument.cs`: missing space in the backout line.

## Owner questions raised (docs/QUESTIONS.md)
Q-142a (frames disagree on the shell; Approvals metric cards), Q-142b (what a Viewer is offered), Q-142c (export read-through: Parameters freeze, CSV headers, ICS text, untagged PDFs). Q-102b and Q-102c remain open.

## Limits
- Chrome only. One viewport (1600 px); smaller widths were not part of this story.
- Frames were compared at skeleton level (shell, heading, widths, spacing), not pixel by pixel; the dark frames were not measured separately.
- Contrast of the screens was measured in REOS-102 (lowest 4.54:1 light, 5.15:1 dark); here axe `color-contrast` runs on every screen, role and state in both themes.
- The relative-time checks use a fake clock in the browser; the server clock is real.

## How to rerun
With the app up (or let Playwright start it): `cd tests/e2e && Pdf__QuestPdfLicense=Evaluation npx playwright test qa-142` (about 11 min; the spec prints GEOMETRY, ROLES, STATES and EMPTY tables). Exports: `dotnet test tests/ReleaseMgmt.Api.Tests --filter ExportQuality`.

## Final local run
All local, on the final tree (CI is blocked by billing and was not waited for):
- `dotnet build -c Release`: 0 warnings, 0 errors.
- `dotnet test`: Domain 358, DrDrill 2, Infrastructure 374, Api 989 (including the 9 new export cases): all pass. One earlier full Api run had a single failure, `EndpointRoleMatrixTests` (`GET /ingest-keys` as Governance Officer answered 500, not 403); it passed twice alone and in the next full run, and nothing in this change touches it. Not understood: flagged here rather than hidden.
- `python3 tests/reference/test_schema.py`: 493 passed. Workflow test: OK.
- Web unit tests 171 passed; `npm run build` OK.
- Full Playwright with `Pdf__QuestPdfLicense=Evaluation` (logs cleared first): **262 passed** in 42 min, including the 16 new tests and the REOS-102 spec.
- Two failures of the new Sync health test during development were the test, not the page: a fake browser clock fights the server's clock push every 10 s. The test now holds that push back (it says so in a comment).
