# UI specification

## Redesign system (D41, 2026-10-23): the reference for every new or restyled screen

The visual reference is the Figma redesign [uxMlQ7LTNoW8sIKWSIeY98](https://www.figma.com/design/uxMlQ7LTNoW8sIKWSIeY98): seven 1600 px screens, each in a light frame and a dark frame (REOS-103). It replaces the no-pills/badges/boxes/modals rules of the sections below; those sections still describe the screens that have not been restyled yet. Mapping of the design's terms to this app: `docs/proposals/redesign-mapping.md`.

**Tokens.** `docs/ui/redesign-tokens.json` is the export of the Figma variables (collection Color, modes Light and Dark, 35 semantic colours) plus the dimensions sampled from the frames (type sizes, radii, spacing, control and layout sizes). `python3 tools/gen_tokens.py` writes them into the generated block of `docs/ui/tokens.css` (`--color-*` and the dimension names); `--check` fails when the CSS has drifted, and a web unit test checks the same. To change a colour: change the Figma variable, copy the value into the JSON, run the generator. Light is the default; dark follows the OS unless the person chose a theme, and `data-theme="light"|"dark"` on any element shows that theme inside it (the gallery uses this). Fonts stay the OS faces (`--font-sans`, `--font-mono`; CLAUDE.md rule 10), not the frames' Inter and IBM Plex Mono.

**Components** (`src/ReleaseMgmt.Web/src/ui/`, styles in `ui/ui.css`, all `os-` classes, tokens only, no colour literal: a unit test fails on one):

| Component | Use |
|---|---|
| `Button` | `primary` (filled accent, one per area), `secondary` (outlined), `destructive` (danger text, e.g. Rollback); optional leading Lucide icon |
| `StatusChip` | dot + status word; the tone comes from the word (`chipTone`: Successful/Passed/Approved/Healthy/Recorded = success, Running/Live/Canary active = info, Pending/Blocked/Medium risk/Conditional go = warning, Failed = danger, Planned/Queued = neutral). Never colour alone |
| `CountBadge` | a count beside a navigation item; nothing at zero; the accessible name says what is counted |
| `MetricCard` | label, large value, optional unit, detail line in a tone |
| `Panel` | heading row (title, subtitle, right-aligned meta), body, footer; `inverse` is the dark panel (Go/no-go, Deployment events) |
| `DataTable` + `TwoLine` | column headings, rows, two-line first cell, footer with count and sync time; with `onSelect`, rows are focusable, Up/Down move, Enter selects |
| `Tabs` + `TabPanel` | ARIA tabs with optional counts; one tab stop, arrows, Home, End |
| `Notice` | operational notice: icon, title, description, action link; `live` for one that appears while the person is on the page |
| `PropertyList` | label left, value right, `mono` for ids, versions and times |
| `ProgressBar` | readiness, canary traffic; announces the percentage |
| `FilterSelect` | the design's filter dropdown, a native select underneath |
| `SearchField` | leading icon and the platform's shortcut hint (⌘ K or Ctrl K); the shell owns the binding |
| `LoadingState`, `EmptyState`, `ErrorState`, `NoPermissionState`, `ReadOnlyNotice`, `ConflictNotice`, `DisabledAction`, `StaleBanner`, `SaveFailureNotice` | the states every screen shares (REOS-123): always words, never colour alone; loading, errors, conflicts and stale data are live regions; a disabled action shows its reason as text the button is described by. Screens use these instead of their own "Loading…" and "✗ message" lines |
| `SidePanel` | the right-hand column's one panel (REOS-124, from Audit's event details): a `Panel` with an icon-only Close in its heading row. The Inspector (gate, task, product, step) and the New train, Paste tasks, Communicate and Run step drawers use it; the title is the h2 that takes focus on open |
| `ConfirmDialog` | the only dialog: destructive or irreversible actions (Rollback, Pause run); starts on Cancel, Escape cancels, focus returns |
| `PageHeading` | breadcrumb trail, the page's h1, a one-line description, up to two actions (secondary + primary) on the right |

Icons are Lucide (`lucide-react`, pinned), decorative (`aria-hidden`) unless they are the only content of a control, 16 px at stroke 1.6. **Rules that stay:** status is never colour alone; every interactive element has the visible focus ring; no checkboxes or toggle switches; times are UTC and labelled, with the person's local time alongside (D44). **Contrast:** components use `text/secondary` for any caption that carries information and `text/tertiary` only for decoration, until QUESTIONS Q-090f is decided.

**Shell (REOS-94, `src/ReleaseMgmt.Web/src/shell/`).** Every signed-in page sits in it:
- **Sidebar** (216 px, `aside` "Workspace"):
  - brand, then the workspace's name and region (REOS-106: with more than one workspace it is the switcher, a menu of the person's workspaces; with one it is plain text; Q-106e);
  - the Primary navigation (`shell/nav.ts`: the design's Workspace group in order with its Lucide icons, the Management list, and a More group for screens the design does not place, Q-094a). The Approvals item carries the count of decisions waiting for the person (`GET /approvals`), hidden at zero;
  - in the footer, the control-plane card (`shell/health.ts`: operational, degraded or service disruption, from `/healthz`, the sync state and the live connection; a link to Sync health) and the user card, which opens the account panel (Appearance, Sign out, Reset/Exit under `start.py`).
- **Utility bar** (64 px, the `banner`):
  - where you are (workspace / page);
  - search (Jump to; ⌘K / Ctrl+K focuses it);
  - the clock in UTC with the person's own time and zone beside it (D44, `shell/clock.ts`), and the connection state in words while it is not live;
  - the Inbox bell with its unread count.
- **The page area.** Every page uses the full width. Releases (`/`) is the table of releases (D50, REOS-125); an open release (`/trains/{id}`) has the Inspector beside it, and so does the list while the New train drawer is open. The 272 px Stream is gone.
- **New destinations.** A destination with no screen yet opens a placeholder page (heading, "not built yet", where the work is done today), never a 404. Screens not yet restyled keep their own look inside the shell until their story, including the `--bg` surface they were contrast-checked on (`.shell`); only pages marked `redesigned` sit on `--color-bg-canvas`, where the old `--brand-title` titles would fall to 4.46:1.

**Orchestration (REOS-98, REOS-128, `src/ReleaseMgmt.Web/src/orchestration/`).** The destination for a release's run, with Rehearsal and Live inside it (`/orchestration/{trainId}/{live|rehearsal}[/steps/{stepId}]`): the page heading with Pause run and View deployment; a context row (status chip, run key, who started it and when in UTC with the local time, elapsed, release and mode selectors); tabs Pipeline, Run history, Configuration, Variables & secrets; on Pipeline the run clock (metric cards), the deadline alert, the Progressive delivery graph (one card per runbook section, a button that announces its name and status, wrapping), the steps table and canary panel of the selected stage, the plan/actual/forecast bars with their text equivalents, the run controls (each a confirmation dialog) and the Run step drawer in the right-hand column. The data-model mapping and the places the design had no data are Q-98a to Q-98d and Q-128a in `docs/QUESTIONS.md`.

**Release types (REOS-116, `src/ReleaseMgmt.Web/src/mgmt/ReleaseTypes.tsx`).** A tab of Teams & settings (all roles read, RTE and Release Manager edit): the types as a `DataTable` (type, default template, releases, state chip) with the detail `Panel` and an "Add release type" `Panel` beside it; Archive asks in a `ConfirmDialog`; numbering is a select, never a toggle. New train asks for the type (and the number of a Beta) before the target date, and says which template the type starts from; the Releases table shows "Beta 2"; Portfolio & calendar and Analytics each have a Release type filter. `tests/e2e/specs/release-types.spec.ts` checks the flow with axe in both themes. Questions: Q-116a.

**Clients (REOS-118, `src/ReleaseMgmt.Web/src/mgmt/Clients.tsx`).** The next tab of Teams & settings, built like Release types: a `DataTable` (client, segment `StatusChip` with the word "Standard" or "On-Demand", CERT environment, state chip) with a detail `Panel` and an "Add client" `Panel` beside it; segment is a select, never a toggle; the CERT environment select appears only for On-Demand, and a Standard client says in words that it has none. Add and Save stay off with the reason in words (name, or "Choose the CERT environment of this On-Demand client."). Archive asks in a `ConfirmDialog` that starts on Cancel; Restore needs none. Loading, empty ("No clients yet. Add one, or import a CSV.") and error states are the shared ones. A link under the table goes to Imports & exports for the CSV kind. Axe in both themes (`tests/e2e/specs/clients.spec.ts`).

**Versions (REOS-117, `src/ReleaseMgmt.Web/src/versions/`, `mgmt/Versions.tsx`).** `ChainStrip` draws a version's chain as an ordered list: each step is its label, a `StatusChip` (dot and word, never colour alone) and its target date; the release being looked at says "(this release)" and carries `aria-current="step"`; a step planned too soon after the one before it adds "▲ Advisory: too soon". It is full on the release page's Dependencies tab (`VersionPanel`: advisories as warning `Notice`s that say nothing is blocked, "Create next release…", "Take out of version…" behind a `ConfirmDialog`) and compact (`ChainBriefView`: "Orion 5.2 · Beta 2, 2 of 4" and the labels with statuses) under the release's context row, in the calendar's upcoming list and in the overview's activity table. Versions are also a tab of Teams & settings (table, detail panel with the chain, "Add version" panel; Archive asks in a `ConfirmDialog`). The New train drawer has an optional Version select. A release in no version says so and offers "Add to an existing version" and "Start a new version with this release". Loading, empty and error states are the shared ones. `tests/e2e/specs/versions.spec.ts` checks the flows with axe in both themes. Questions: Q-117a.
**Client dates (REOS-119, `src/ReleaseMgmt.Web/src/release/ClientDates.tsx`).** A `Panel` on the release's Overview, between Release properties and Readiness, for CERT and Production releases only (other types show nothing). One `DataTable` ("Client dates") lists every client: client, segment `StatusChip` ("Standard" or "On-Demand"), the date with where it comes from in words ("Standard date", "Offered", "Offered (default)", "Chosen by the client"), how far it sits from the Standard date ("+7 days") and its wave, the offered date, the chosen date with "Recorded by … · time UTC (local)" (D44; the dates themselves are calendar dates and carry no zone), and the advisories. Advisories use the Advisories style: a ▲ glyph, a screen-reader "Advisory:" prefix, the words ("Freeze window \"…\" covers this date", "Earlier than the Standard date") and "(advisory)"; they never disable an action. Selecting an On-Demand row opens the date form under the table: two native date inputs (chosen, offered), Save dates (off with the reason in words, e.g. "A chosen date cannot be earlier than the Standard date (Fri 7 Mar 2031)."), and "Clear chosen date", which asks in a `ConfirmDialog` that starts on Cancel. "Schedule N On-Demand clients" records the default offered dates so they move with the release. Viewers and non-planners read the table and see "Only an RTE or Release Manager can record client dates.". Loading, error and empty states are the shared ones. Axe in both themes (`tests/e2e/specs/client-dates.spec.ts`).

**Train templates (REOS-38, REOS-129, REOS-122, `src/ReleaseMgmt.Web/src/Templates.tsx`, `templateLogic.ts`).** A table of templates (name with its counts of gates, tasks, milestones and steps, version number, status `StatusChip`, review due, approver) beside the selected template's `Panel` (template version, default risk, review, author, last changed by, record version). Below: Gates (a Compliance gate says "Compliance · locked on releases" as a chip with those words), Checklist tasks (gate, task, owner team), Milestones (`T−14`, the name, "14 days before the target", owner team), Runbook skeleton, T-minus plan, and for a version "Changes from version N" (Added, Removed, Changed chips; a Compliance gate says so in words) and "Versions" (rows open that version). The editor is the same panels as tables of fields; a task has a gate select (it moves between gates), a milestone's offset reads back as `T−n` beside the field. Buttons: Edit (draft), Approve (primary), New version (an Approved template with no newer version), Retire (destructive). Approve stays off with its reason in words beside it (`DisabledAction`) when the person wrote the template ("You created this template, so someone else approves it (separation of duties).") or when a Governance Officer must ("This version changes a Compliance gate, so a Governance Officer approves it."); a draft that needs one also says so in an info `Notice`. Approving a version that replaces an Approved one asks in a `ConfirmDialog` ("Approve and retire the old version"; starts on Cancel). On a release, the gate Inspector shows "Locked by template" as a chip, has no Remove gate, and disables Class with the reason beside it; a milestone that came from a template says "moves with the target (T−14)" and, when edited, that a date set by hand fixes it. `tests/e2e/specs/templates.spec.ts` and `template-governance.spec.ts` check these with axe in both themes.

**Start-up and sign-in pages (REOS-135, `src/ReleaseMgmt.Web/src/startup/`).** Seven pages outside the shell share one frame (product name "ReleaseOS", the page's h1, a line, a Panel): Sign in, the refused sign-in (a danger Notice in the same page), Starting (loading), Cannot reach the server (Try again), ReleaseOS has stopped, No workspace, and Page not found (unknown path, `isKnownPath`). Focus lands on the h1, or on the Email field on the sign-in page; each sets its own document title `<page> · ReleaseOS`. `tests/e2e/specs/startup.spec.ts` checks each with axe in both themes. Questions: Q-135a.

**Gallery.** `/gallery` (signed in) shows every component and variant in light and dark side by side, for review against the Figma frames; `tests/e2e/specs/gallery.spec.ts` checks it with axe in both themes, focus rings, keyboard tabs and rows, and the dialog.

## Before the redesign (screens not yet restyled)

Tokens: `docs/ui/tokens.css` (import it as the base stylesheet). Mockups: `docs/ui/mockups/*.html` (open in a browser; fonts resolve to your OS) and `*.png` renders. Mockup data is illustrative; match layout, density, copy and behaviour.

Figma: [ReleaseOS — UI](https://www.figma.com/design/nlfpTZHvRakSyUGpZ3N0Xr) holds the same tokens as variables (Color: Light/Dark, Layout), the text styles, the core components (Text button, Status, Tab, Line input, Stream row, Gate dot, Section head, Inspector field) and the Main train workspace in light and dark. `tokens.css` stays the source of truth; a token change is made there first and mirrored in Figma. Figma uses Inter and Roboto Mono as stand-ins because SF Pro / SF Mono are not served to its renderer; the app still uses the OS fonts (rule 10). See `docs/QUESTIONS.md` Q-085.

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
macOS feel: system font, soft greys, translucent toolbar and sidebar (backdrop blur; flat fallback), hairline separators, system-blue accent, full dark mode following `prefers-color-scheme` with a manual override (`data-theme` on `<html>`) in the user menu. On Windows the same CSS yields Segoe UI Variable, Cascadia Mono and a Mica-like translucent sidebar. Built against macOS 26 conventions; macOS 27 specifics are unverified, and a change is a token edit.

**Brand (direction D, chosen 2026-09-30):** Fiserv Orange carries the main wording in both themes: page titles, drawer and inspector titles, section heads, the app name and the current-tab underline (`--brand-title` for titles and indicators, `--brand-text` for smaller wording; light mode uses the AA-safe shades `#C24E00` / `#B84A00` because exact `#FF6600` is 2.94:1 on white, and both meet 4.5:1 at any size; dark mode uses exact `#FF6600`). Blue (`--accent`) stays the colour of everything clickable and of the focus ring. Mockups: the "D · Command" page of the design canvas. A toolbar "Jump to" field (Ctrl/Cmd+K) opens any train or page by typing.

Type: 13 px body, 12 px captions and group headers (sentence case), 22 px drawer titles, 28 px page titles, semibold, -0.02em tracking. Every time, count, version and id is monospaced with tabular figures.

## Layout
| Region | Width | Content |
|---|---|---|
| Toolbar | full, 44 px | app name, primary nav (Trains, Calendar, Analytics, Sync health, Imports & exports), clock + connector glyphs, My work, user menu |
| Stream | removed (D50, REOS-125) | replaced by the Releases table: key, title, status chip with blockers, type, team and owner, environments, target date · T-n, gates passed; filters for status, team, environment and type |
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
