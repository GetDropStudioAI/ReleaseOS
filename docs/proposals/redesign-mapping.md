# UI redesign (REOS-90): data-model mapping and design gaps

Status: analysis for **REOS-91** (map the design's vocabulary to the data model) and **REOS-92** (design gaps), 2026-10-23. Nothing is built. Section 1 lists the owner decisions that block the rest of Sprint 6; sections 2 and 3 are REOS-91's mapping; section 4 lists the new backend stories the design would need; section 5 answers REOS-92 item by item.

Design: [Figma uxMlQ7LTNoW8sIKWSIeY98](https://www.figma.com/design/uxMlQ7LTNoW8sIKWSIeY98/Untitled?node-id=0-1), seven 1600 px light frames: Organization overview 6:4718, Release portfolio 6:4990, Release detail 6:5279, Orchestration pipeline 6:5552, Approvals 6:5838, Deployment monitoring 6:6058, Audit history 6:6333. This is a different file from the one made under Q-085 (`nlfpTZHvRakSyUGpZ3N0Xr`), which stayed inside the UI rules.

## 1. Decisions needed before any screen is built

| # | Question | Why it blocks | Options |
|---|---|---|---|
| A | **Does the redesign replace the UI rules?** The design is built from status chips (tinted pills), a count badge, bordered panels and metric cards, filled primary buttons, dark panels and (for Rollback and Pause) confirmation dialogs. CLAUDE.md rule 9, `docs/UI.md` and DECISIONS.md "Rejected alternatives" ("Modals; pills, badges, toggles, checkboxes … do not reintroduce") forbid every one of these | REOS-93 (components) and REOS-94 (shell) are mostly these elements | **1.** The redesign supersedes them: record a new decision, rewrite rule 9, UI.md and `tokens.css`, and the existing screens are restyled too. **2.** Keep the rules and take the redesign's structure only (sidebar, page heading, metric row, tables, detail panes) drawn in the current idiom: words, glyphs, coloured text, hairline rules, inline expansion. **3.** A named mix |
| B | **Should ReleaseOS deploy and monitor?** Deployment monitoring (6:6058), the canary panel and stage graph (6:5552), "Environment health" and "Deployment success" assume the app drives rollouts (traffic %, pods, Pause rollout, Rollback) and reads telemetry (error rate, p95 latency, availability, SLOs). ReleaseOS coordinates people running runbook steps; it has no deployment controller and no metrics source (PROJECT_SCOPE section 1). Acting on external systems is also the kind of write-back DECISIONS.md rejected for v1 | REOS-100 entirely; parts of REOS-95 and REOS-98 | **1.** Out of scope: drop the Deployment monitoring screen and the telemetry panels; the pipeline view shows the runbook. **2.** A new epic: read-only telemetry from a named source (Prometheus, Datadog …), no control actions. **3.** Full integration with a deployment controller (Argo Rollouts, Spinnaker …): a different product, months of work |
| C | **Dark mode.** The design is light only; the app has light and dark today (REOS-27, UI.md) | REOS-93 tokens | Keep dark (derive dark values for every new token), or drop it |
| D | **Single organisation.** The workspace switcher ("Acme Technologies / Enterprise workspace") and "Control plane / EU-WEST-1" imply several tenants and a hosted control plane; the app is one organisation on one host | REOS-94 | Show the organisation name from configuration and the host's health, no switcher; or build multi-tenancy (large) |
| E | **Times in UTC.** The design labels every time UTC; the app shows `Display:TimeZone` (America/Chicago by default) and stores UTC | Every screen | UTC everywhere, the display zone everywhere, or a per-user choice |
| F | **Tokens.** The Figma file has no variables or styles (`get_variable_defs` returns nothing), so REOS-93's "read tokens from Figma variables" cannot be done | REOS-93 | Sample the values from the frames into `tokens.css`, or add variables to the file first. Fonts: the frames use Inter and a monospace face; rule 10 (OS fonts, never bundled) is met by the system UI stack and the system monospace (SF Mono, Cascadia Mono), which look close |

## 2. Mapping table (REOS-91)

"Fits" means a current endpoint carries it. "Derived" means no new table, but a new read endpoint or a computed field. "New" means a schema change and a backend story (section 4). "None" means there is no source and no story is proposed unless decision B says so.

| Design term | In the design | ReleaseOS today | Binding |
|---|---|---|---|
| **Release** RLS-284 | name, ID, team, service count, owner, environment, window, risk, status | **Train** (`ReleaseTrains`): title (teams write "R26.12 Card portal"), risk tier, status, target date, window. `GET /trains`, `GET /trains/{id}` | Fits, except: no short display ID (UUIDv7 ids; the title usually carries the code), **no team** (gates, tasks and steps have owning teams; a train does not), **no owner** column, **no environment** |
| Release status | Planned, Running, Blocked, Successful, Failed | `CurrentStatus` Planning / Gated / Executing / Complete (+ close code) / Aborted; blockers from readiness | Derived: Running = Executing; Blocked = open readiness blockers; Successful / Failed = close code or Aborted |
| **Quality gates** | gate group, checks n/m, evidence, owner, state, score 18/20, % ready | **Stage gates** with checklist tasks, owner, attachments (evidence), status; readiness | Fits well: `GET /trains/{id}` (gates with tasks done/total), `GET /gates/{id}`, `/readiness`, `/attachments` |
| **Go / no-go** card | Conditional go, rationale, owner, time | `GoNoGoDecisions` + conditions: Go / GoWithConditions / NoGo, notes, decider, time | Fits: `GET /trains/{id}/gonogo` |
| **Service manifest**, "v7 · LOCKED" | service, version, team, per-environment state | **Bundled products** (name, version, project code) and the **baseline** (immutable, captured when Code Freeze certifies) | Partly: products and versions fit; "locked" = baseline captured; **per-environment state and a team per product: none** |
| **Change record** CHG-1042 | on the release; maintenance window CHG-092 | `ChangeRecords` + `ChangeTicketNumber`; ServiceNow link state via `ExternalLinks` (read-only; write-back rejected) | Fits for the record and its link state. Maintenance windows: none (freeze windows are Freeze or Chill only) |
| **Orchestration run** RUN-1287 | run ID, started by, elapsed, Pause run | **Runbook run** (`RunbookRuns`: Rehearsal or Live, started, ended, outcome); `GET /trains/{id}/runs`, `GET /runs/{id}`, forecast | Fits, except no short run ID and **no pause** (steps are done by people; a run has no paused state) |
| **Stages** Preflight → Staging → Production EU → Production US → Verification | per-environment stages with task bullets, check counts, time spans | Runbook **sections**: PreCheck, Deploy, Verify, Rollback, Hypercare (fixed set) with steps, dependencies and actuals | Derived: a stage card per section. Per-environment stages (EU, US): **none** |
| **Execution steps** | step / service, strategy, started, duration, state | `StepExecutions` with planned vs actual, owner, note | Fits; "Strategy" (rolling, canary, atomic): **none** |
| **Canary analysis**, health gates, SLO thresholds | traffic %, error rate, p95, auto-promote | none | **None** (decision B) |
| **Deployment** DEP-8841 | live rollout of one service to one environment, pods, traffic, charts, event stream | none (closest: the live run and its step events over SignalR) | **None** (decision B) |
| **Approval** APR-621 | one queue item with due time, evidence cards, several required approvers, decision note, Request changes / Approve & record | No single entity. Things that wait for a person's decision today: Compliance gate certification (Governance Officer), waiver approval, freeze-override approval, the Go/No-Go decision (Release Manager), closing a Go condition, template approval, accepting a known issue | **Derived** queue over those (section 4, B1). **New** if wanted: several approvers per item, "Request changes" (today a gate can fail or reopen, a waiver cannot be declined) |
| Separation of duties | "Release owners cannot approve their own production changes" | Enforced in the database: certifier not a task author, waiver approver not the requester, override approver not the requester | Fits (different rules from the design's; the existing ones stay) |
| **Exceptions** | "Open exceptions 1" | Waivers, freeze overrides, escalations (the evidence pack's section 4 "Exceptions") | Derived |
| **Policy library**, policy coverage % | managed policies | none: the rules are code, triggers and templates | **None**; templates are the nearest thing to show |
| **Service catalog** | global list of services | Products exist per train only; affected CIs per train | **New** if a global catalog is wanted |
| **Environments** | Production EU / US, Staging, regions, health | none | **None** |
| **Teams & settings** | management link | **Admin** (Users, Teams, Holidays) and **Connectors** | Fits (existing screens) |
| **Audit event** | time, event, resource, actor, outcome, before/after diff | `AuditEvents` (append-only, trigger-backed): `GET /audit` with filters, `/audit/entity-types`, `/audit.csv` | Fits; diff = BeforeJson / AfterJson. "Outcome" is derived from the action |
| Audit **integrity**: "Signed · Hash chain verified", SHA-256, archive sync | | Append-only by triggers; **no hash chain, no signature, no archive** | **New** (B4) |
| Audit retention "365 days immutable" | | The audit log is kept forever; generated exports 365 days (D35) | Fits if shown as "kept indefinitely" |
| Notifications bell | | **Inbox** (`/me/notifications`, unread count) | Fits |
| Global search ⌘K | | **Jump to** (⌘K) | Fits |
| Clock | UTC, ticking | Server-time broadcast over SignalR, shown in the display zone | Fits (decision E for the zone) |
| Control-plane status | "All systems operational" | `/healthz` (database, backup, poller, watchdog) and Sync health | Fits as "host and sync health" |
| Approvals badge count | 4 | none | Derived from B1 |
| Overview metrics | Active releases, Running now, Need attention, Deployment success % | open trains, Executing trains, readiness blockers + pending approvals, close-code mix (analytics M10) | Derived (B2) |
| Release throughput chart | 187 deployments in 14 days, per day | Analytics M15: trains completed **per month** | Derived, but the volume is a few trains a month, not 13 a day: a daily bar chart will be mostly empty |
| Calendar rows per team; production slots "3 of 4" | | Calendar (month/week) of trains and freezes; no team on a train; no slot concept | Rows: none (no train team) unless B3. Slots: **none** |
| Scheduling conflict | "Autumn storefront overlaps the catalog maintenance" | Window collisions (REOS-85) and freeze overlap | Fits |
| Next change freeze | | `/freeze-windows/ahead` | Fits |
| **External dependencies** | payments-api ≥3.4, Redis 7.2 | Cross-train dependencies are **out of v1** (PROJECT_SCOPE section 2); REOS-85 warns of collisions only | **None** in v1 |
| Release properties | target environments, change record, release branch, rollback plan (validated, RTO), on-call | change record; rollback plan = change record backout plan + rollback attestation | Partly; environments, release branch, on-call: **none** |

## 3. How much of each screen can bind to live data today

| Screen | Binds today | Needs a derived endpoint | No source (decision B or new story) |
|---|---|---|---|
| REOS-95 Organization overview | release activity table, attention items from readiness | metric cards, attention list, throughput (B2) | Environment health, "deployments" wording, per-day throughput at this volume |
| REOS-96 Portfolio & calendar | week calendar of trains and freezes, upcoming windows, conflicts, next freeze, ICS export | filters by status | rows per team (no train team), production slots, "Dependency" column |
| REOS-97 Release detail | header, risk, quality gates and score, Go/No-Go card, products and versions, baseline lock, change record, rollback plan | tab counts | per-environment product state, release branch, on-call, external dependencies |
| REOS-98 Orchestration pipeline | runbook run, sections as stage cards, steps with actuals, live updates, elapsed | stage summaries | Pause run, strategy, canary panel, per-environment stages |
| REOS-99 Approvals | the decisions that exist (certify, waivers, overrides, Go/No-Go, conditions) | the queue and its counts (B1) | several approvers per item, Request changes, policies, policy coverage |
| REOS-100 Deployment monitoring | nothing | | **all of it** (decision B) |
| REOS-101 Audit history | table, filters, actor, before/after diff, CSV export, keyboard | outcome column | integrity banner, signature, archive sync (B4) |

## 4. New backend stories the design would need

| # | Story | Schema change | Size | Needed by |
|---|---|---|---|---|
| B1 | **Approval queue**: `GET /approvals` lists what waits for the signed-in person (Compliance gates to certify, waivers and freeze overrides to approve, Go/No-Go to record, conditions to close) with due times and counts; the badge reads its count | none | S | REOS-94, REOS-99 |
| B2 | **Overview summary**: `GET /overview?from&to` with the metric-card numbers, attention items and daily completions | none | S | REOS-95 |
| B3 | **Train owner, team and display key**: columns on `ReleaseTrains` (owner user, owning team, short key such as RLS-284), set at creation, shown everywhere | yes (+ upgrade script) | M | REOS-95, 96, 97 |
| B4 | **Hash-chained audit**: each audit row carries the hash of the previous row and its own; a verification endpoint; the banner shows the result. Trigger-written audit rows cannot compute SHA-256 in SQLite, so the chain is sealed by the app after each commit (with a verification job), which needs its own design | yes | L | REOS-101 |
| B5 | **Several approvers and "Request changes"**: an approval with required roles and per-role decisions, and a declined state for waivers and overrides | yes; changes governance rules in the triggers | L | REOS-99 |
| B6 | **Environments and per-environment product state** | yes | M–L | REOS-97, 98 |
| B7 | **Deployment telemetry and control** (decision B) | yes, plus external integrations | XL | REOS-100, canary panels |
| B8 | **Global product (service) catalog** | yes | M | Service catalog |

## 5. Design gaps (REOS-92): recommendation per item

"Existing" points at a screen that already does the job and can be restyled; "idiom" means build it the way the app already handles it, no design needed; "design" means it needs a designer.

| Gap | Recommendation |
|---|---|
| Releases (list) | Existing: the Stream (train list) |
| Service catalog | Design + B8, or hide until decided |
| Environments | Hide (no model) unless B6 |
| Teams & settings | Existing: Admin (Users, Teams, Holidays) and Connectors |
| Portfolio: Timeline, Release list | Release list = the Stream; Timeline = the Calendar's month view |
| Release detail tabs: Services, Dependencies, Approvals, Activity | Services = products; Approvals = the train's gates, waivers and Go/No-Go; Activity = the audit viewer filtered to the train; Dependencies: hide (out of v1) |
| Orchestration tabs: Run history, Configuration, Variables & secrets | Run history = the train's runs (exists); Configuration = the runbook plan; Variables & secrets: hide (no model; credentials never shown, rule 11) |
| Approvals tabs: Decided, Exceptions, Policy library | Decided and Exceptions from audit and waivers/overrides (B1); Policy library: hide |
| Create release / Plan release / Edit release | Existing: New train (template, clone or blank) and the planning workspace |
| Manage policies, Review queue, Retention policy | Hide, hide (the queue is the page), text: "kept indefinitely; exports 365 days" |
| Notifications bell | Existing: Inbox |
| Global search ⌘K | Existing: Jump to |
| Rollback / Pause confirmations | Idiom: inline confirm (`ConfirmInline`), as Abort does today; only if decision B keeps those actions (Pause run has no model) |
| Loading, empty, error, no-permission states | Idiom: the app's existing inline states (muted text, inline error with Retry, "Only … can do this") |
| Stale or disconnected live data | Idiom: the existing sync banner and SignalR reconnect handling |
| Approval submit, validation, success | Idiom: inline validation and the 422 guard message, as on every action today |
| Long names, many teams or services, more than five stages | Idiom: truncation with full title, scrolling tables; stages wrap |
| Tablet and mobile | Decide; today the app is designed for desktop widths and UI.md specifies nothing narrower |
| Dark mode | Decision C |
| "AUTOSAVED" vs "READ-ONLY RUN VIEW" | A run view is read-only (actuals come from step actions; the plan is edited in the runbook); drop "AUTOSAVED" |
