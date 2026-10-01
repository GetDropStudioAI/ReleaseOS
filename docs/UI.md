# UI specification

Tokens: `docs/ui/tokens.css` (import it as the base stylesheet). Mockups: `docs/ui/mockups/*.html` (open in a browser; fonts resolve to your OS) and `*.png` renders. Mockup data is illustrative; match layout, density, copy and behaviour.

## The rule
No checkboxes, toggle switches, pills, badges or bordered boxes anywhere. That means:
- **Status** is a word plus glyph plus colour: `✓ Certified`, `◐ In progress`, `○ Pending`, `✗ Failed`, `▲ 2 blockers`, `● in sync`, `◆ mismatch`.
- **Actions** are text buttons (`button.text`). Primary action = same style, placed last in the row. Disabled actions always show *why* on the line below.
- **Choices** (tabs, Append/Upsert, Plan/Rehearsal/Live, dispatch channel) are underlined text, not segmented capsules.
- **Selection** is a full-width row tint (`--selection`), never a rounded highlight.
- **Grouping** is whitespace + hairline rules, never a bordered card.
- **Inputs** show a bottom rule only. The bulk parser editor is rules above and below, a line-number gutter, and a tinted background on error/warning lines.
- **No modals.** Detail opens inline or in the right drawer. Destructive confirms are inline ("Mark failed" → inline reason field + "Confirm").

## Look (D23)
macOS feel: system font, soft greys, translucent toolbar and Stream (backdrop blur; flat fallback), hairline separators, system-blue accent, full dark mode following `prefers-color-scheme` with a manual override (`data-theme` on `<html>`) in the user menu. On Windows the same CSS yields Segoe UI Variable, Cascadia Mono and a Mica-like translucent sidebar. Built against macOS 26 conventions; macOS 27 specifics are unverified, and a change is a token edit.

Type: 13 px body, 12 px captions and group headers (sentence case), 22 px drawer titles, 28 px page titles, semibold, -0.02em tracking. Every time, count, version and id is monospaced with tabular figures.

## Layout
| Region | Width | Content |
|---|---|---|
| Toolbar | full, 44 px | app name, primary nav (Trains, Calendar, Analytics, Sync health, Imports & exports), clock + connector glyphs, My work, user menu |
| Stream | 272 px | trains grouped Executing / Gated / Planning / Complete (30 days); each row: id, title, date · T-n, blocker count, gate glyph string (●●◐○); freeze footer |
| Workspace | flexible | selected train or page |
| Inspector / drawer | 380 px inspector; 560–620 px drawers | context detail; parser; comms |

Global failure banner (connector down, sync stalled) sits under the toolbar on **every** screen.

## Screens (mockup file → what must be implemented)
| Mockup | Must behave |
|---|---|
| `Main` / `MainDark` | "To reach Executing" line from `GET /trains/{id}/readiness` (same checks as the API); products table rollups; gate timeline SVG with business-day axis and a dashed "now" line; clicking a gate expands its checklist inline and opens it in the Inspector; certify disabled with reason and the eligible certifier named; train milestones as ◆ in a lane under the gates plus a Milestones section (add, edit, done, remove inline; Q-0843) |
| `Runbook` | clock, window countdown, forecast finish, time to rollback deadline, steps done; section headers; per-row bars on a window-scaled axis (planned grey, actual ink, running accent, forecast dashed, deadline red line); red alert when forecast crosses the rollback deadline; running-step drawer with count-up timer, instructions, notes, Done / Fail / Skip (Skip requires a note) |
| `BulkParser` | line-numbered editor, error/warning glyphs in gutter, summary line, closest-match suggestions as buttons, preview table, disabled commit with reason; centre shows what will be added |
| `Comms` | Message / Schedule / Sent log tabs; template source with token highlighting beside hydrated preview; "data as of hh:mm · train vN"; T-minus schedule with sent/late/ready states; Copy as rich text / Open in mail / Post to webhook |
| `SyncHealth` | banner; connector table; mismatches (rule + since); alerts (fingerprint, count); watchdog line; alert Inspector with cause, impact, steps to clear, who was told |
| `Analytics` | filter line; six headline figures separated by rules; charts titled with their finding; CSV/XLSX/SVG per chart; ECharts with SVG renderer and the token palette |
| `ImportExport` | file line with SHA-256; Append/Upsert; counts; error table; preview with `+ ~ = ✗` markers and struck-through old values; export catalogue; ICS link with Copy / Rotate; recent jobs |
| `EvidencePack` | page 1 layout of the QuestPDF evidence pack (Letter) |

## Screens without mockups
Build these from the same patterns (table + Inspector, text actions, underlined tabs). No new visual vocabulary.
| Screen | Milestone | Layout |
|---|---|---|
| Users, teams, holidays | M1 | one table per entity; row opens in Inspector; CSV import/export in the header |
| Connectors, webhook allowlist | M5 | table of connectors with state from `ConnectorState`; credentials entered in the Inspector, never displayed back |
| Train templates | M4 | template table (status, review due); selected template shows gates, runbook skeleton and T-minus plan as tables |
| Comm template library | M6 | as Comms drawer, full width |
| Audit viewer | M4 | filter line (train, entity, actor, date range), dense event table, before/after JSON in Inspector, CSV export |
| Notifications inbox, My work | M4 | two tables: items assigned to me by due date; notifications newest first, unread in semibold |
| Calendar | M7 | month and week grids of trains (target date, window, gate due dates, ◆ milestones) with freeze windows as tinted date ranges; click opens the train |

## Accessibility
Real `<button>`/`<a>`/`<input>`+`<label>`; icon-only controls get `aria-label`; glyph status always has a word beside it; tokens meet WCAG AA (light status colours are darkened from Apple's system colours for 4.5:1 on white); keyboard: `j/k` moves rows in grids, `Enter` opens in Inspector, `Esc` closes the drawer (no global handlers that swallow typing in inputs).
