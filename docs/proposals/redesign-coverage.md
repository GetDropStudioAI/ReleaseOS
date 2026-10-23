# UI redesign (REOS-90): coverage of every user-facing surface

Status: analysis, 2026-10-23. It answers the owner's request "make sure we are covering all aspects of the application to update". Nothing was built or changed. Baseline: `main` plus the uncommitted REOS-94 shell work in `src/ReleaseMgmt.Web/src/shell/` (in progress in another session). Jira was read on 2026-10-23: REOS-90 to REOS-114; no later REOS story exists.

How to read the tables:
- **Ticket**: the REOS story whose scope names the surface. "—" means none.
- **Design**: what the Figma file [uxMlQ7LTNoW8sIKWSIeY98](https://www.figma.com/design/uxMlQ7LTNoW8sIKWSIeY98) shows (seven 1600 px frames, light and dark per REOS-103). "None" means no frame or element.
- **Coverage**:
  - `Covered`: a story names it and the design draws it.
  - `Story only`: a story names it, but there is no design.
  - `Partly`: a story or frame covers part of it; the rest is listed.
  - `Not covered`: no story names it.

D41 says "every existing screen is restyled to match". No story under REOS-90 does that for the screens outside the seven frames. That is the main finding.

## 1. Summary

| Area | Surfaces | Covered | Story only | Partly | Not covered |
|---|---|---|---|---|---|
| Shell and global elements (section 2) | 13 | 4 | 3 | 4 | 2 |
| Sign-in, start-up and stop pages (3) | 7 | 0 | 0 | 0 | 7 |
| Destinations (4) | 25 | 4 | 6 | 4 | 11 |
| Release workspace, Plan mode (5.1) | 20 | 0 | 1 | 7 | 12 |
| Release workspace, Rehearsal and Live (5.2) | 8 | 1 | 1 | 4 | 2 |
| Inspector panels (5.3) | 6 | 0 | 0 | 1 | 5 |
| Drawers (5.4) | 4 | 0 | 0 | 1 | 3 |
| Files, feeds and messages (6) | 22 | 2 | 0 | 5 | 15 |
| States and cross-cutting rules (7) | 17 | 0 | 0 | 8 | 9 |
| **Total** | **122** | **11** | **11** | **34** | **66** |

What the numbers mean:
1. **The seven frames touch 7 of the 19 routes in `route.ts`.** Nine routes have no screen story at all:
   - Teams & settings, Templates, Comm library and Connectors.
   - Sync health (with the webhook allowlist), My work and Inbox.
   - Analytics and Imports & exports.

   Inside the covered routes, three large parts have no story either: the Calendar's month view, the release list (the Stream), and the Rehearsal/Live runbook modes.
2. **The release workspace is mostly undesigned below its header.** REOS-97 covers the header, the readiness score, a gate table, the service manifest, the Go/No-Go card and the release properties. It does not cover:
   - the gate timeline, the checklist and the milestones;
   - the forms behind Record Go/No-Go, the change record, freezes and overrides, waivers, evidence upload and links;
   - the runbook editor, the closeout (rollback rehearsal, PIR, known issues) and the exports panel.
3. **The runbook during a run loses its core numbers.** REOS-98's pipeline has stage cards and a steps table. It has none of these: forecast finish, rollback deadline, window countdown, the plan/actual/forecast bars, and the Done/Fail/Skip drawer. Rehearsal runs are not mentioned.
4. **The Inspector and the four drawers are not designed.** The four drawers are New train, Bulk paste, Communicate and Run step. The new shell has no right-hand panel pattern. The nearest ones are Audit's closable details panel (6:6333) and the Approvals decision pane (6:5838).
5. **The pages around sign-in are not covered.** That is sign-in, start-up loading, server unreachable, stopped and refused sign-in. They, and the browser tab, are still titled "Release Management", not "ReleaseOS".
6. **Nothing that leaves the browser is covered.** That is the 4 PDFs, the evidence ZIP, 18 grids, the analytics XLSX, the ICS feeds, the in-app notification texts, the team webhook texts and the comm templates. REOS-107 (key, owner, team) and D45 (workspaces) both change what these outputs must carry.
7. **The states have no build story.** REOS-92 lists them and REOS-102 checks them, but no story designs or builds them. The app's states are muted text and "✗ … Retry"; the design's are panels and chips.
8. **D44 (UTC first, local time beside it) reaches only the shell and the seven new screens.** The other screens still format in `Display:TimeZone`. For example, the run clock reads "09:29Z (04:29 CT)" (M3 Done-when), and the PDFs print CT.

## 2. Shell and global elements

| Surface | Today | Ticket | Design | Coverage |
|---|---|---|---|---|
| Sidebar: brand, destinations in the design's order, active state, Approvals count | `shell/Sidebar.tsx`, `shell/nav.ts` | REOS-94, REOS-108 (count) | 6:4719 | `Covered` |
| Pages the design does not place: the "More" group (My work, Analytics, Imports & exports) and Templates, Comm library, Connectors under Management | `shell/nav.ts` (QUESTIONS Q-094a) | REOS-94 | None | `Partly`: the placement is an agent's choice, not a design |
| Workspace switcher | name and region only (`Sidebar.tsx`) | REOS-106 | "Acme Technologies / Enterprise workspace" with a chevron; the open menu is not drawn | `Partly` |
| Control-plane card, linking to Sync health | `Sidebar.tsx`, `shell/health.ts` | REOS-94 | healthy state only ("All systems operational") | `Partly`: degraded and down are not drawn |
| User card and account panel: Appearance (Auto, Light, Dark), Sign out, Reset and Exit under `start.py` | `shell/UserMenu.tsx` | REOS-94 (card) | card only | `Partly`: the panel is not drawn |
| Utility bar: breadcrumb, Jump to (⌘K / Ctrl K), UTC and local clock, live-connection words, Inbox bell | `shell/UtilityBar.tsx`, `JumpTo.tsx` | REOS-94 | 6:4764 | `Covered` |
| Jump to results list | `JumpTo.tsx` | REOS-94 | search field only (REOS-92 "Global search results") | `Story only` |
| Page heading: trail, title, description, up to two actions | `ui/layout.tsx` | REOS-93, REOS-94 | every frame | `Covered` |
| Placeholder for an unbuilt destination | `shell/Placeholder.tsx` | REOS-94 | None | `Story only` |
| Skip link, focus moved to the page heading after navigation, document title "… · ReleaseOS" | `App.tsx` | REOS-94 | — | `Story only` |
| Component gallery `/gallery` | `Gallery.tsx` | REOS-93 | — (review page) | `Covered` |
| Connector-wide sync banner under the utility bar, on every page | `SyncBanner.tsx` | — | None | `Not covered` |
| Session-save failure line ("▲ … could not be saved") | `App.tsx` | — | None | `Not covered` |

## 3. Sign-in, start-up and stop pages

All seven are drawn today as a plain centred column (`main.signin`) headed "Release Management". The browser tab title in `index.html` is also "Release Management".

| Surface | Today | Ticket | Design | Coverage |
|---|---|---|---|---|
| Sign-in: dev sign-in (email, role), Organisation sign-in link, password-reset link, Appearance | `App.tsx` `SignIn` | — | None | `Not covered` |
| Refused organisation sign-in, in words (REOS-65 reason codes; one says "Ask an administrator to add you to a Release Management group") | `signinReason.ts` | — | None | `Not covered` |
| Start-up "Loading…" (before `/me`, before the session restores, before the display zone loads) | `App.tsx` | — | None | `Not covered` |
| "Cannot reach the server" | `App.tsx` | — | None | `Not covered` |
| Stopped: "The app has stopped. You can close this tab." (after Exit under `start.py`) | `App.tsx`, `shell/UserMenu.tsx` | — | None | `Not covered` |
| Signed in, but a member of no workspace, or the only workspace is archived (D45) | does not exist | — (REOS-105 and REOS-106 do not name it) | None | `Not covered` |
| Unknown path, or a deep link to a train that is gone | falls back to Releases (`route.ts` `ROOT`); a missing train shows "✗ … Retry" | — | None | `Not covered` |

## 4. Destinations

The routes come from `route.ts` (`View`). The sidebar labels come from `shell/nav.ts`.

| Destination | Path | Today | Ticket | Design | Coverage |
|---|---|---|---|---|---|
| Overview | `/overview` | placeholder | REOS-95, REOS-108 (B2) | 6:4718 | `Covered` |
| Portfolio & calendar | `/calendar` | `Calendar.tsx`: month and week grids; target dates, windows, gate due dates, ◆ milestones, freeze and chill ranges; the Subscribe panel (create, copy, rotate, revoke an ICS link) | REOS-96 | 6:4990, the week view by team only. The month view, the Timeline and Release list tabs, and the Subscribe panel are not drawn | `Partly` |
| Releases (list) | `/` | **Built (REOS-125, D50, Q-1119):** a full-width table with filters for status, team, environment and type; the Stream and its freeze footer are gone. Was: the Stream, 272 px, grouped by status, gate glyph string, blocker count, freeze footer | REOS-125 | None (built from the Deployments list pattern) | `Covered` |
| Release detail | `/trains/{id}` | see section 5 | REOS-97 | 6:5279 | `Partly` (section 5) |
| Orchestration | `/orchestration` | placeholder; runs live inside each release (Rehearsal and Live modes) | REOS-98 | 6:5552 | `Covered` for the pipeline; see 5.2 for what it drops |
| Approvals | `/approvals` | interim queue table (`shell/Approvals.tsx`) | REOS-99, REOS-108 (B1), REOS-113 (B5) | 6:5838, Pending tab only | `Partly`: Decided, Exceptions and Policy library are not drawn |
| Deployments | `/deployments` | placeholder | REOS-100, REOS-110 to REOS-112 | 6:6058, one deployment (DEP-8841) | `Partly`: the destination needs a list of deployments, which is not drawn |
| Audit history | `/audit` | `AuditViewer.tsx` | REOS-101, REOS-114 | 6:6333 | `Covered` |
| Service catalog | `/services` | placeholder | REOS-109 | None | `Story only` |
| Environments | `/environments` | placeholder | REOS-109 | None | `Story only` |
| Teams & settings | `/admin` | `Admin.tsx`: Users (add user, role), Teams (handle, members, webhook), Holidays (CSV) | REOS-106 adds members | None | `Not covered` |
| Templates | `/templates` | `Templates.tsx`: list, Draft editor (gates, runbook skeleton, T-minus plan), approve, retire, review due | — | None | `Not covered` |
| Comm library | `/library` | `CommLibrary.tsx`: library list, editor with token check, preview against a train, the train's messages, T-minus schedule (`CommSchedule.tsx`) | — | None | `Not covered` |
| Connectors | `/connectors` | `Connectors.tsx`: Jira and ServiceNow, base URL, credentials (never shown back), enable | — (REOS-105 scopes them) | None | `Not covered` |
| Sync health | `/sync` | `SyncHealth.tsx`: banner, connectors, mismatches, open alerts, resolved history, alert detail, watchdog line, and the **webhook allowlist** | REOS-94 links to it | None | `Not covered` |
| My work | `/work` | `MyWork.tsx`: what is assigned to me, by due date | — | None | `Not covered` |
| Inbox | `/inbox` | `Inbox.tsx`: notifications, unread in semibold, mark read, open the target | REOS-94 (the bell only) | bell icon | `Not covered` |
| Analytics | `/analytics` | `AnalyticsScreen.tsx`: period, six headline figures, a chart per metric, CSV / XLSX / SVG per chart | — (REOS-95 has one throughput chart) | None | `Not covered` |
| Imports & exports | `/importexport` | `ImportExport.tsx`, `GridExports.tsx`: preview with `+ ~ = ✗`, errors, warnings, Append/Upsert, recent imports, 18 grids as CSV and XLSX | — | None | `Not covered` |
| Gallery | `/gallery` | review page | REOS-93 | — | `Covered` |
| Create workspace, members and roles | — | none | REOS-106 | None | `Story only` |
| Ingest API keys (create, revoke) | — | none | REOS-110 ("created and revoked on Admin") | None | `Story only` |
| Environment control webhooks, thresholds and SLOs | — | none | REOS-109, REOS-111, REOS-112 | None | `Story only` |
| Approval policies ("Manage policies") | — | none | REOS-113 | a button in 6:5838 | `Story only` |
| Retention policy | — | none | REOS-101 (secondary action) | a button in 6:6333 | `Not covered`: mapping section 5 proposes the text "kept indefinitely; exports 365 days"; no story says so |

## 5. The release workspace (`/trains/{id}`)

### 5.1 Plan mode, top to bottom (`Trains.tsx` `TrainHeader`)

| Surface | Today | Ticket | Design | Coverage |
|---|---|---|---|---|
| Header line: kind, CHG, risk, version. Eight actions: Rehearsal, Live runbook, Record Go/No-Go, Communicate, Export, Abort…, Complete…, Advance to … | `Trains.tsx` | REOS-97 | "Edit release" and "View pipeline" only; a context row with status, risk, team, owner, window | `Partly`: none of the eight existing actions is placed |
| Complete (close code, notes) and Abort (reason): inline forms (REOS-86) | `TrainClose.tsx` | — | None | `Not covered`. Both are irreversible, so D41 would allow a confirmation dialog |
| Title; status; target date with T-n business days; window line (inline edit, collision warning) | `Trains.tsx`, `Planning.tsx` `WindowLine`, `Advisories.tsx` | REOS-97 | window shown read-only | `Partly`: edit, T-n and collisions not drawn |
| "To reach {status}" readiness line, with the contingency and collision advisories (REOS-85, REOS-88) | `Trains.tsx`, `Advisories.tsx` | REOS-97 | gate score "18 / 20", % ready, gate group table | `Partly`: non-gate guards (Go decision, baseline, rehearsed rollback, expired condition) and the advisories have no place |
| Bundled products: table, rollups, add, edit, remove | `Planning.tsx` `Products` | REOS-97 (service manifest), REOS-109 | manifest with per-environment state and LOCKED | `Partly`: editing not drawn |
| Gate timeline on a business-day axis, "now" line, ◆ milestone lane, Add gate | `Planning.tsx` `Timeline`, `AddGate` | — | None (the design has a gate table, no timeline) | `Not covered` |
| Checklist of the selected gate: tasks, owner, product, done, reopen, add task, Paste tasks | `Planning.tsx` `Checklist` | — | "Checks n / m" count only | `Not covered` |
| Milestones: add, edit, mark done, remove | `Milestones.tsx` | — | None | `Not covered` |
| Go/No-Go: record (Go, Go with conditions, No-Go with a new target), conditions with owner and expiry, close a condition, history | `Governance.tsx` `GoNoGo` | REOS-97 (card), REOS-99 | dark card: decision, rationale, owner, time | `Partly`: the form and history are not drawn |
| Change record: seven text fields, CAB date, affected CIs | `Governance.tsx` `ChangeRecordPanel` | REOS-97 (properties) | one property row "Change record" | `Partly` |
| Freeze windows: create freeze or chill, request override, grant override | `Freezes.tsx` `FreezesPanel` | — (REOS-96 shows the next freeze) | None | `Not covered` |
| Jira and ServiceNow links with their sync state | `Links.tsx` | — | None | `Not covered` |
| Runbook editor: steps by section, owners, planned times, dependencies, contingency line | `Runbook.tsx` | REOS-98 "Configuration" tab | tab label only | `Story only` |
| Rollback rehearsal attestation | `Closeout.tsx` `RollbackSection` | REOS-97 ("rollback plan, validated, RTO") | one property | `Partly` |
| Post-implementation review and its actions | `Closeout.tsx` `PirSection` | — | None | `Not covered` |
| Known issues and hypercare exit | `Closeout.tsx` `IssuesSection` | — | None | `Not covered` |
| Exports panel: queue a PDF, job state, SHA-256, download | `ExportsPanel.tsx` | — | None | `Not covered` |
| Release detail tabs Services, Dependencies, Approvals, Activity | none | REOS-97 | labels only (REOS-92) | `Not covered` as content: mapping section 5 assigns them, no story builds them |
| Train-level evidence (attachments on the train) | `Evidence.tsx` | — | three evidence cards in 6:5838 only | `Not covered` |
| No train selected: "Select a release in the Stream" / "No trains yet…" | `App.tsx` | REOS-125: the Releases table's empty state | None | `Covered` |

### 5.2 Rehearsal and Live modes (`LiveRun.tsx`)

| Surface | Today | Ticket | Design | Coverage |
|---|---|---|---|---|
| Mode switch Plan / Rehearsal / Live (`/trains/{id}/rehearsal`, `/live`) | `LiveRun.tsx` | — | None: Orchestration is a separate destination with a release selector | `Not covered` |
| No run yet; Start rehearsal; Start live run (needs Executing) | `LiveRun.tsx` | — | None | `Not covered` |
| Run clock: now, window countdown, forecast finish, time to rollback deadline, steps done | `LiveRun.tsx` | REOS-98 | status chip, run ID, started by, elapsed | `Partly`: forecast, deadline and countdown missing |
| Step rows with plan, actual and forecast bars on a window-scaled axis, deadline line, alert when the forecast crosses the deadline | `LiveRun.tsx` `Bars` | REOS-98 (steps table) | stage cards and a table; no bars, no forecast | `Partly` |
| Sections (PreCheck, Deploy, Verify, Rollback, Hypercare) as stages | `LiveRun.tsx` | REOS-98 | five per-environment stage cards | `Partly`: the mapping draws one card per section; per-environment stages need D42 data |
| End run: completed, rolled back, aborted (inline confirm) | `LiveRun.tsx` | REOS-98, REOS-112 (pause) | "Pause run" only | `Partly` |
| Run history | `GET /trains/{id}/runs` | REOS-98 "Run history" tab | tab label | `Story only` |
| Canary panel, step strategy | none | REOS-98, REOS-111 | drawn | `Covered` (new function) |

### 5.3 Inspector panels (right column, 380 px; `Planning.tsx` `Inspector`)

| Surface | Today | Ticket | Design | Coverage |
|---|---|---|---|---|
| Nothing selected | "Select a train, gate or task…" | — | None | `Not covered` |
| Gate: facts; Start, Certify, Fail, Reopen, Decertify; why Certify is unavailable; edit or remove the definition; evidence; waivers (request, approve, waive) | `Planning.tsx`, `Evidence.tsx`, `Freezes.tsx` `WaiversPanel` | REOS-99 | the Approvals decision pane (evidence cards, required approvers, note) | `Partly`: covers a decision reached from Approvals, not the gate panel inside a release |
| Task: state, owner, product, completed by; Mark done / Reopen; evidence | `Planning.tsx` | — | None | `Not covered` |
| Product: version, project code, tasks, health, open blockers | `Planning.tsx` | — | None | `Not covered` |
| Runbook step (plan mode): fields, instructions, dependencies, owner | `Runbook.tsx` `StepInspector` | — | None | `Not covered` |
| 409 conflict notice that keeps the draft and shows the other change | `Conflict.tsx` | — | None | `Not covered` |

### 5.4 Drawers (right column, 560 to 620 px)

| Surface | Today | Ticket | Design | Coverage |
|---|---|---|---|---|
| New train: blank, from an Approved template, or clone; title, target, risk, window | `NewTrain.tsx` | REOS-95 "Create release", REOS-96 "Plan release", REOS-107 (fields) | buttons only; REOS-92 lists the Create, Plan and Edit release flows as undesigned | `Partly` |
| Bulk paste: line-numbered editor, gutter glyphs, closest-match buttons, preview, commit | `Bulk.tsx` | — | None | `Not covered` |
| Communicate: Message, Schedule and Sent log tabs; token highlighting; "data as of"; Copy as rich text, Copy Markdown, Open in mail, Post to webhook | `CommsDrawer.tsx` | — | None | `Not covered` |
| Run step: count-up timer, instructions, notes, Done, Fail, Skip (needs a note) | `LiveRun.tsx` `RunStepDrawer` | — | None (REOS-98's table shows state only) | `Not covered` |

## 6. Files, feeds and messages

None of these is in Figma, and none has a story except where noted. Two decisions reach all of them:
- **REOS-107** (key such as RLS-284, owner, team, environments, branch, on-call). It says these are "carried by import and export, ICS and the evidence pack", but names no other output.
- **D45**: a document or message that leaves the app must say which workspace it came from.

### 6.1 PDFs (QuestPDF, `src/ReleaseMgmt.Infrastructure/Exports/Documents/`)

| Surface | Today | Ticket | Design | Coverage |
|---|---|---|---|---|
| Release report: summary, gate timeline, blockers, comms sent, scorecard | `ReleaseReportDocument.cs` | — | None | `Not covered` |
| Run sheet: plan by section, rollback "only if called", actuals after a run | `RunSheetDocument.cs` | — | None | `Not covered` |
| Audit evidence pack: nine sections; page 1 must match `mockups/EvidencePack.html` (M7 Done-when) | `EvidencePackDocument.cs` | REOS-107 (carry the new fields) | None | `Partly` |
| Evidence ZIP: files and `manifest.csv` | `EvidenceZip.cs` | — | None | `Not covered` |
| Post-release scorecard | `ScorecardDocument.cs` | — | None | `Not covered` |
| Shared frame: Letter page, print colours (`PdfStyle.cs`), Lato with OS monospace, header and footer (job ref, generated by, train title and version) | `TrainDocument.cs` | — | None | `Not covered`. Points to decide: the title is the train title, with no key, workspace or owner. Times are in `Display:TimeZone` with CT/ET labels; D44 left PDFs there "until decided otherwise" |

### 6.2 CSV, XLSX and imports

| Surface | Today | Ticket | Design | Coverage |
|---|---|---|---|---|
| 18 grids as CSV and XLSX: the 9 import kinds, plus blockers, known issues, freeze windows, attachments manifest, my notifications, sync alerts, templates, audit log, milestones | `GridExportService.cs`, `GridFiles.cs` | REOS-107 | None | `Partly`: data columns only; there is no workspace column, and round-trip re-import must keep working |
| CSV import, 9 kinds, preview then commit | `CsvImportService.cs` | REOS-107, REOS-109 (service key) | None | `Partly` |
| Analytics per chart: CSV (client), XLSX (server), SVG (the drawn chart, ECharts palette) | `analyticsExport.ts`, `AnalyticsExportEndpoints.cs` | — | None | `Not covered`: the SVG colours must follow the new tokens |
| Analytics workbook with one sheet per metric and a parameters sheet (PROJECT_SCOPE section 9) | not built: one metric per XLSX | — | None | `Not covered` |
| Audit log CSV | `/audit.csv` | REOS-101 | "Export audit log" | `Covered` |

### 6.3 Calendar feeds (ICS, `IcsFeedService.cs`)

| Surface | Today | Ticket | Design | Coverage |
|---|---|---|---|---|
| Feeds: all trains, mine, freezes, one train (and the older `/ics/{token}.ics`) | `CalendarEndpoints.cs` | REOS-96 ("Export calendar"), REOS-107 | a button | `Partly` |
| Event and feed text | titles "R26.12 Card portal: Code Freeze due", "…: target release date", "…: deployment window", "Freeze: Q4 close". Feed names "Release trains", "My release work", "Freeze and chill windows". PRODID `-//ReleaseMgmt//Release calendar//EN` | — | None | `Not covered`: tokens are per workspace (`IcsTokens.WorkspaceId`), but feeds from two workspaces get the same name in a subscriber's calendar; no key in titles. The UIDs (`…@releasemgmt`) must **not** change: they are the subscriber's event identity |

### 6.4 Notifications, webhooks and communications

| Surface | Today | Ticket | Design | Coverage |
|---|---|---|---|---|
| In-app notification texts, 10 kinds with escalation level: GateEntered, GateReminder, GateOverdue (L1, L2), StepLate (L1, L2), RollbackDeadline, ConditionExpiring, ConditionExpired, PirOverdue, FreezeOverrideRequested, SyncAlert | `NotificationScheduler.cs`, `RunService.cs`, `FreezeService.cs`, `SyncAlertWriter.cs` | — | None | `Not covered`. The text is stored when the notification is created (`Notifications.Message`). Times are mixed: "due 2026-10-30" (no zone), "expires at 2026-10-30 14:00Z", and the forecast finish unformatted. There is no release key and no workspace |
| Team webhook messages: Teams and Slack `{"text": …}`; Generic JSON (kind, entity, level, message, trainId, at) | `TeamWebhookSender.cs` | — | None | `Not covered`: plain notification text, no link back to the app, no workspace name |
| Default comm library: T-7 Readiness notice, T-3 Checkpoint, T-1 Go/No-Go outcome, T0 Cutover start, T0 Release complete, Hypercare exit; subjects "[{ReleaseTitle}] …" | `CommDefaults.cs` | — | None | `Not covered` |
| Comm dispatch formats: rich-text (HTML) copy, Markdown copy, `mailto:` plain text, webhook JSON | `CommDispatchService.cs` | — | None | `Not covered` |
| Comm tokens (22, allowlisted, PROJECT_SCOPE 5.2) | `CommTokens` | — | None | `Not covered`: no token for the key, owner, team, environments or workspace that REOS-107 and D45 add |
| Email | none: no SMTP (OI-7, `Notifications:Smtp:Enabled=false`); "email" means `mailto:` and copy | — | — | `Not covered`: nothing to restyle unless OI-7 changes; then an HTML email layout is needed |
| Sync alert texts (banner, Sync health, notification) | `SyncAlertWriter.cs`, connectors | — | None | `Not covered` |
| Guard (422) and validation messages shown inline | every service | REOS-99 (approval validation) | the decision-note hint only | `Partly` |
| Outbound control webhook payload (Pause, Promote, Rollback) | none | REOS-112 | — | `Covered` (new, by the story) |

## 7. States and cross-cutting rules

| Surface | Today | Ticket | Design | Coverage |
|---|---|---|---|---|
| Loading | muted "Loading…" per section | REOS-92 lists it; REOS-102 checks it | None | `Not covered`: no story builds it |
| Empty | a sentence and where to go ("No trains yet. Import trains from a CSV…") | REOS-95 and REOS-99 acceptance | None | `Partly`: two screens |
| Load error with Retry | "✗ message · Retry" inline | REOS-92, REOS-102 | None | `Not covered` |
| No permission | "The audit log is available to…"; actions hidden; Admin read-only | REOS-92 | None | `Not covered` |
| Disabled action with its reason | "Advance to Gated is off: 2 guards not met"; the "Certify unavailable" list | REOS-99 (Approve disabled with reason) | None | `Partly` |
| Validation and guard messages | inline, under the control | REOS-99 | None | `Partly` |
| 409: someone else changed it | `Conflict.tsx` keeps the draft | — | None | `Not covered` |
| Stale or disconnected live data | clock words "Reconnecting…", "Offline: reload to reconnect"; refetch on reconnect | REOS-94, REOS-100, REOS-102 | "LIVE · REFRESH 5s", "STREAM CONNECTED" only | `Partly` |
| Read-only release (plan locked while Executing; Complete, Aborted, archived) | 422 `PlanLocked`, `TrainClosed`; controls hidden | — | None | `Not covered` |
| Long names, many rows, more than five stages | truncation with full title; tables scroll | REOS-92, REOS-97 (20+ services), REOS-98 | None | `Partly` |
| Workspace states: switching, no membership, archived workspace | none | — | None | `Not covered` |
| UTC first with local time beside it (D44) | shell and the new screens only; elsewhere `time.ts` formats in `Display:TimeZone` | REOS-94 and each screen story, for their own screens | 7 frames | `Partly`: the nine routes without a screen story, the drawers, the PDFs and the notification texts are not covered |
| Dark mode on every screen (D43) | tokens, `data-theme` | REOS-103 (seven frames) | dark frames for the seven | `Partly`: the other screens are not drawn dark |
| Visual and accessibility QA | | REOS-102 | 7 frames at 1600 px | `Partly`: REOS-102 compares only the seven screens |
| Narrow viewports (tablet, phone) | desktop widths only | REOS-92 ("decide") | None | `Not covered`: owner decision |
| Product name: "ReleaseOS" in the shell; "Release Management" on sign-in, `index.html`, a sign-in reason; "ReleaseMgmt" in the ICS PRODID | various | — | ReleaseOS | `Not covered` |
| Keyboard model: `j`/`k` and Enter in grids, Esc closes the Inspector (UI.md) vs the new `DataTable` (Up/Down, Enter) | both exist | REOS-93 (`DataTable`) | — | `Not covered`: two models on screen until every grid is moved |

## 8. Gaps and proposed stories

All are stories under REOS-90 (created 2026-10-23 as REOS-123 to REOS-142). Each fits the build order in `redesign-mapping.md` section 6 at the step named. "Needs design" means a Figma frame or an explicit "use the component set as is" from the designer first (REOS-92's rule).

| # | Jira | Story | Scope (one line) | Depends on | Build-order step |
|---|---|---|---|---|---|
| G1 | REOS-123 | **State patterns in the new component set** | Loading, empty, error with Retry, no permission, read-only, 409 conflict, disabled-with-reason, stale/offline, session-save failure and the sync banner as `ui/` components in the gallery, light and dark, used by every screen story | REOS-93 | 2, before any screen story |
| G2 | REOS-124 | **Right-hand panel and drawers in the new shell** | One panel pattern (from Audit's details panel) for the gate, task, product and step Inspector, and for the New train, Bulk paste, Communicate and Run step drawers; needs design | REOS-93, REOS-94 | 4 |
| G3 | REOS-125 | **Releases list and the Stream** | Decide and build the Releases destination: keep the 272 px Stream inside the new shell, or a release table page with filters (status, team, environment, type); the freeze footer goes with it; needs design | REOS-94, REOS-107 | 5 |
| G4 | REOS-126 | **Release detail: planning sections** | Gate timeline with the milestone lane, checklist, milestones, products editing, runbook editor and step Inspector, in REOS-97's tab structure; needs design | REOS-97, G2 | 5 |
| G5 | REOS-127 | **Release detail: governance and closeout sections** | Record Go/No-Go and conditions, change record and CIs, freezes and overrides, waivers, evidence, links, rollback rehearsal, PIR, known issues, exports panel, Complete and Abort as confirmation dialogs; needs design | REOS-97, G2 | 5 |
| G6 | REOS-128 | **Run view in the redesign** | Rehearsal and Live modes inside Orchestration: run clock (window countdown, forecast finish, rollback deadline), plan/actual/forecast bars, deadline alert, start and end run, Run step drawer, mode switch from a release | REOS-98, G2 | 6 |
| G7 | REOS-129 | **Management screens** | Teams & settings (users, teams, holidays, workspace members), Templates, Comm library and the T-minus schedule, Connectors, restyled as table plus panel | REOS-94, REOS-105, G1, G2 | 5 |
| G8 | REOS-130 | **Sync health and the webhook allowlist** | Restyle the screen, the alert detail and the allowlist; the control-plane card's degraded and down states point here | REOS-94, G1 | 5 |
| G9 | REOS-131 | **My work and Inbox** | Restyle both; Inbox rows get chips by kind and escalation level; My work links decisions to Approvals | REOS-94, REOS-108, G1 | 5 |
| G10 | REOS-132 | **Analytics screen** | Six headline figures as metric cards, charts on the token palette (SVG export follows), period control; see `release-types-and-executive-analytics.md` for the executive views | REOS-93, REOS-105 | 5 |
| G11 | REOS-133 | **Imports & exports screen** | Import preview with diff markers, error table, recent imports, grid catalogue, recent export jobs | REOS-93, G1 | 5 |
| G12 | REOS-134 | **Calendar beyond the week frame** | Month view, Timeline and Release list tabs, freeze and chill ranges, milestones, the Subscribe panel (create, copy, rotate, revoke); needs design | REOS-96 | 5 |
| G13 | REOS-135 | **Sign-in and start-up pages** | Sign-in, refused sign-in, loading, server unreachable, stopped, no workspace, unknown path; product name "ReleaseOS" on all of them and in `index.html` | REOS-93, REOS-106 | 4 |
| G14 | REOS-136 | **UTC on every remaining screen (D44)** | Every screen, drawer and Inspector not covered by a screen story shows UTC with the local time beside it; update the tests that assert CT (M3 Done-when) | REOS-94 | 4 |
| G15 | REOS-137 | **Documents carry the release model and the workspace** | All 4 PDFs, the evidence ZIP manifest, grids and the analytics XLSX gain key, owner, team, environments and workspace name; PDF times follow the owner's answer (UTC or `Display:TimeZone`); M7's page-1 test updated | REOS-107, REOS-105 | 5 |
| G16 | REOS-138 | **Calendar feeds name their workspace and key** | Feed names and event titles carry the workspace and key (e.g. "Payments · RLS-284 Card portal: Code Freeze due"); UIDs unchanged; PRODID to ReleaseOS | REOS-107, REOS-105 | 5 |
| G17 | REOS-139 | **Message texts: notifications, team webhooks, comm tokens** | One message style: key and title, UTC times labelled "UTC", workspace name; a deep link back to the app (needs a configured public base URL); new tokens `{ReleaseKey} {Owner} {Team} {Environments} {Workspace}` (PROJECT_SCOPE 5.2 updated) | REOS-107, REOS-105 | 5 |
| G18 | REOS-140 | **Deployments list** | The Deployments destination's list (environment, service, version, state, release), from which DEP-nnnn opens; needs design | REOS-110 | 6 |
| G19 | REOS-141 | **Analytics workbook** | One XLSX with a parameters sheet and one sheet per metric, as PROJECT_SCOPE section 9 promises | REOS-105 | 5 |
| G20 | REOS-142 | **Extend REOS-102** | QA covers every restyled screen (G-stories), both themes, the files of section 6, and the single keyboard model for grids | all of the above | 9 |

### Questions the gaps raise for the owner

1. **The Stream (G3).** (a) Keep it as a second column inside the new shell. (b) Replace it with a Releases table page; the release detail then uses the full width. **Recommended: (b).** The design has no second column, and REOS-96's "Release list" tab would be the same table.
2. **PDF and ICS times (G15, G16).** D44 left them on `Display:TimeZone`. (a) Keep that. (b) Use UTC, labelled, as on screen. (c) UTC with the display zone beside it. **Recommended: (c)** for PDFs, which auditors read away from the app. ICS stays UTC in the file, since calendars convert it.
3. **Deep links in notifications and webhooks (G17).** This needs a public base URL in configuration. (a) Add `App:PublicBaseUrl`. (b) No links. **Recommended: (a).**
4. **Complete and Abort (G5).** (a) Inline forms, as now. (b) Confirmation dialogs (D41 allows them for irreversible actions). **Recommended: (b).**
5. **Narrow viewports.** (a) Desktop only, recorded as a decision. (b) Tablet. **Recommended: (a)** until a user asks.
