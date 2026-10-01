# Project Scope — Release Management App (v1)

Built on .NET 10. Decisions referenced as D1–D30 and open items as OI-n are in `DECISIONS.md`.

## 1. Purpose and users

An internal tool that bundles several product versions into one **release train**, enforces **stage gates** that mechanically block the train, runs **live runbooks** during deployment windows, keeps **Jira/ServiceNow** links honest with a visible **sync health** view, and generates **stakeholder communications** from live data. It produces the evidence auditors sample (SOX ITGC, SOC 2 CC8.1, PCI DSS 6, FFIEC).

| Role | Can |
|---|---|
| Viewer | Read everything except the audit log |
| ReleaseManager | Everything an RTE can do (including admin in v1); also records Go/No-Go and approves freeze overrides |
| RTE | Create and plan trains, run runbooks, import, dispatch comms, admin (v1) |
| GovernanceOfficer | Certify/waive Compliance-class gates; approve waivers; read the audit log |

Separation of duties is enforced in the database (see §4): a Compliance-gate certifier must be a Governance Officer who completed none of that gate's tasks; a waiver approver must be a Governance Officer other than the requester; a freeze-override approver must be a Release Manager or Governance Officer other than the requester.

### Endpoint permissions
| Action | Viewer | RTE | ReleaseManager | GovernanceOfficer |
|---|---|---|---|---|
| Read trains, gates, runbooks, analytics | ✓ | ✓ | ✓ | ✓ |
| Create/edit trains, products, gates, tasks, steps; import; paste tasks | | ✓ | ✓ | |
| Start/certify/fail Standard gates | | gate owner or RTE | gate owner or RM | gate owner |
| Certify Compliance gates; request/approve waivers | | | | ✓ (not own tasks; not own request) |
| Advance/abort/complete train | | ✓ | ✓ | |
| Record Go/No-Go | | | ✓ | |
| Request freeze override | | ✓ | ✓ | |
| Approve freeze override | | | ✓ (not own request) | ✓ (not own request) |
| Run steps (start/done/fail/skip) | | ✓ + step owner | ✓ + step owner | |
| Dispatch comms | | ✓ | ✓ | |
| Read audit log, evidence packs | | ✓ | ✓ | ✓ |
| Admin (users, teams, holidays, webhooks, connectors) | | ✓ (v1) | ✓ (v1) | |

Enforced by ASP.NET authorization policies on every endpoint; the database backstops the governance rows (certify, waiver, override). A contract test enumerates every endpoint and asserts its policy.

## 2. v1 scope

| Area | In v1 | Out of v1 |
|---|---|---|
| Trains | Create, clone from template or prior train, archive; bundle N products with versions; risk tier; change-record fields | Cross-train dependencies |
| Gates | Ordered gates, business-day offsets, owner (user xor team), certify / fail / reopen, dual-approval waiver, DB-enforced lockout, evidence attachments, transition log, notifications + escalation | Custom gate types |
| Runbooks | Steps with section, planned start/duration, dependencies; Rehearsal and Live runs; countdowns, forecast finish, late escalation; rollback section | Executing deployments (this app records, it does not deploy) |
| Bulk parser | Line grammar, preview, atomic commit | Excel import |
| Governance | Go/No-Go record with expiring conditions; freeze windows + reasoned, expiring overrides; SoD; PIR auto-required; hypercare / known issues | Hash-chained audit |
| ITSM | Jira + ServiceNow read-only polling, per-link state, mismatch rules, Sync Health | Write-back, inbound webhooks |
| Comms | Template library, per-train copies, T-minus schedule, token hydration, copy / `mailto:` / allowlisted Teams-Slack webhooks, dispatch log | SMTP, scheduled auto-send |
| Calendar | Month/week across trains + freeze windows; ICS feeds | Environment booking |
| Analytics | 15 metrics (§8), analytics screen, per-train scorecard | Ad hoc report builder |
| Import/export | CSV import (9 kinds), CSV+XLSX export of every grid, 4 PDFs, ICS | Excel import |
| Session | Per-tab UI state, autosaved drafts, deep links | Co-editing |
| Admin and support screens | Users, teams, holidays, webhooks, connectors, train templates, comm library, audit viewer, notifications inbox, My work, calendar (see `UI.md`, no mockups) | — |
| Identity | OIDC, 4 roles, teams | SCIM |

## 3. Domain model

A **ReleaseTrain** owns: `BundledProducts`, `StageGates` (→ `ChecklistTasks`, `GateWaivers`, `GateTransitions`), `RunbookSteps` (→ `StepDependencies`), `RunbookRuns` (→ `StepExecutions`), `Blockers`, `KnownIssues`, `ChangeRecords` (1:1), `AffectedCIs`, `DeploymentWindows`, `GoNoGoDecisions` (→ `GoNoGoConditions`), `PostImplementationReviews` (→ `PirActions`), `Baselines`, `ExternalLinks`, `CommTemplates` / `CommSchedule` / `CommDispatches`, `Attachments`, `TrainMilestones` (named key dates; informational, never a guard).

`TargetReleaseDate` is the scheduling anchor (the CERT date). Gate `DueOn` = target minus `OffsetDays` business days, skipping weekends and `Holidays`; recomputed whenever the target changes (and audited).

### Train state machine
Legal moves only: Planning→Gated, Planning→Aborted, Gated→Planning, Gated→Executing, Gated→Aborted, Executing→Complete, Executing→Aborted. Anything else: 422 `IllegalTransition`.

| Transition | Guards (each a trigger + service check, each with a test) |
|---|---|
| Planning → Gated | Every gate with `RequiredBeforeStatus='Gated'` is Certified or Waived |
| Gated → Executing | Every gate required before Executing is Certified/Waived; latest Go/No-Go is Go or GoWithConditions; a Baseline exists; if RiskTier High/VeryHigh, `RollbackRehearsedAt` is set |
| Gated → Executing (cont.) | no open Go/No-Go condition past its `ExpiresAt` (D29) |
| Executing → Complete | every gate with `RequiredBeforeStatus='Complete'` is Certified/Waived; `CloseCode` set; if not Successful a PIR row is auto-created |

### Gate state machine (D26, trigger `trg_Gate_Transition`)
Pending → InProgress → Certified | Failed | Waived. Failed → InProgress on reopen. Certified → InProgress on reopen, and automatically when a task is reopened or added. Certified → Failed is illegal (reopen first); Waived is terminal. Anything else: 422 `IllegalGateTransition`.

Certify requires: all tasks done; all earlier gates Certified/Waived; for Compliance gates, certifier is a Governance Officer who completed none of the gate's tasks, and the gate has ≥1 task (OI-14). Waive requires an approved `GateWaivers` row with a different approver. Certifying locks the gate's and its tasks' attachments.

### Runbook execution
Plan (`RunbookSteps`) and actuals (`StepExecutions` per `RunbookRuns`) are separate. Live runs use absolute planned times; Rehearsal runs shift all planned times by (run start − earliest planned start of non-Rollback steps) (D28). Each train has exactly one `DeploymentWindows` row (D25). At most one open Live run per train; a Live run needs the train Executing; a step cannot start until dependencies are Done/Skipped in the same run; Skipped needs a note; a Deploy-section step cannot start inside a matching freeze window without an unexpired, second-person override.

**Forecast** (computed server-side on every step event and pushed): for each not-yet-done step, forecast start = max(planned start, forecast end of all dependencies, now for the running step's successors); forecast end = start + planned duration (running step: actual start + planned duration, or now if already over). **Rollback deadline** = window end − total planned duration of Rollback-section steps. Alert when forecast finish > rollback deadline. **Late escalation:** warning when a step starts ≥5 min after plan (notify step owner + RTE), critical at ≥30 min (notify Release Manager).

## 4. Schema

`db/schema.sql` is the contract: 49 tables, 42 triggers, 23 indexes; `tests/reference/test_schema.py` (94 cases) must pass against it. Conventions: UUIDv7 TEXT ids; UTC ISO-8601 TEXT timestamps; 0/1 booleans with CHECK; `Version` on every mutable table.

Immutable by trigger: `AuditEvents`, `GateTransitions`, `Baselines`, `GoNoGoDecisions`, `CommDispatches` and `FreezeOverrides` (no update, no delete), and `Attachments` once locked (no update of any column, no delete).

Support tables: `IcsTokens` (hash of each user's calendar token, one active per user), `ParsePreviews` (parser previews, 30-minute expiry, refused if the train's `Version` moved), `ConnectorState` (base URL, enabled flag and cycle bookkeeping per connector; secrets stay in Data Protection), `Teams.WebhookDestinationId` (team notification channel), `Users.Handle` (optional `@handle`).

Audit (D27): one row per service write; cascaded trigger effects write their own rows. Services must set `LastChangedByUserId` and `LastChangedAt` wherever those columns exist. Honest limit: anyone with write access to the DB file can drop a trigger; mitigated by OS file permissions (service account only) and 15-minute backups (D3).

Port to EF: tables, CHECKs, partial unique indexes (`UX_RunbookRuns_OneOpenLive`, `UX_SyncAlerts_OpenFingerprint`, `UX_IcsTokens_ActivePerUser`) via fluent config; all triggers in one `migrationBuilder.Sql` migration, verbatim from `schema.sql`; `ToTable(t => t.UseSqlReturningClause(false))` on every entity (EF Core cannot use `RETURNING` on tables with AFTER triggers). Port `test_schema.py` to xUnit against the migrated database, keeping every case and expected message, plus a test that the migrated trigger names equal those in `schema.sql`.

## 5. Engines

### 5.1 Bulk checklist parser
```
# Gate name              switch target gate (must exist, case-insensitive)
- task text @owner [Product]
// comment
```
- Lines before any `#` go to the gate selected in the UI. `@handle` (team) or `@email`/user handle sets owner; `[Product]` links a bundled product.
- Missing owner → gate owner, flagged as a warning. Unknown owner/gate/product → error with line number and closest matches.
- Blank lines and `//` skipped; any other line is an error naming the expected forms. Max 500 lines. Duplicate description in a gate → warning.
- `POST /trains/{id}/tasks:parse` returns a preview stored in `ParsePreviews` (30 min); `:commit {previewId}` inserts in one transaction; any error blocks commit; a preview taken at an older train `Version` returns 409.
- Owner resolution order: `@handle` matches `Teams.Handle`, then `Users.Handle`; anything containing a second `@` or a dot is matched against `Users.Email`. Committing into a Certified gate requires the client to echo `acknowledgeDecertify=true`.
- Pasted text is saved in session state until commit or discard.

### 5.2 Communication token hydration
Allowlisted tokens only: `{ReleaseTitle} {TargetDate} {DaysToTarget} {Status} {Window} {ChangeTicket} {ProductList} {ProductCount} {GateTable} {NextGate} {NextGateDue} {TasksDone} {TasksTotal} {PercentComplete} {BlockerCount} {CriticalBlockers} {BlockerList} {Owners} {GoNoGoDecision} {Conditions} {KnownIssues} {CloseCode}`.
- Unknown token = error; preview returns `tokenErrors[]`; dispatch refused.
- Empty lists render "None". Values escaped per target (Markdown; HTML for rich-text copy; JSON for webhooks).
- Hydration reads one snapshot in a single read transaction; preview returns `asOf` and `trainVersion`; the client warns if the train's version moved.
- Every copy / `mailto:` / webhook writes a `CommDispatches` row with the exact text. Webhook: allowlisted HTTPS only, 10 s timeout, failure recorded and surfaced.
- T-minus schedule seeded from the template's `TemplateCommSchedule` (default T-7, T-3, T-1, T0 start, T0 complete, hypercare exit); `SentAt` vs `DueAt` feeds M12.

### 5.3 Sync pipeline (fail-fast)
1. `SyncPollerService` (hosted) per connector: every 5 min; every 1 min while any deployment window is open.
2. For each `ExternalLink`, fetch state; compute `ExpectedStatus` from the app; set `SyncState` InSync / Mismatch / NotFound / AuthFailed; stamp `LastSyncedAt` only on success.
3. Errors classified (AuthFailed, Unreachable, NotFound, RateLimited, ParseError) and upserted into `SyncAlerts` by fingerprint `hash(system, kind, key)`: increment `OccurrenceCount` and `LastOccurredAt`; never a new row per cycle. 429 → exponential backoff; alert after 3 consecutive (so within three intervals, not one). Cycle bookkeeping lives in `ConnectorState`.
4. Surfacing: SignalR `SyncAlertRaised` to all clients viewing affected trains; connector-wide failures show a banner on every screen; in-app notification to RTEs and Release Managers; team webhook after 3 consecutive failures.
5. **Watchdog** (separate timer): if `ConnectorState.LastCycleCompletedAt` is older than 3 intervals, raise a `SyncEngine`/`Stalled` alert (D30). Backup, export and webhook failures raise alerts the same way.
6. Links older than 3 intervals render as stale even without an error.
7. Mismatch rules v1: train Executing but CHG not in Implement; train Complete but CHG not Closed after 24 h; train Complete but a Jira fix version not Released after 24 h; CHG planned window ≠ train deployment window; Jira fix-version release date ≠ train target.
8. Alerts auto-resolve after one clean cycle; history stays.
9. Store keys, states and dates only, never ticket summaries (OI-12).

### 5.4 Session engine (state-insulated UI)
- UI state (selected train/node, open drawer, filters, scroll anchors, unsent drafts) saved to `UserSessionState` keyed by (user, clientId). `clientId` lives in `sessionStorage`. Debounce 1 s, plus save on `visibilitychange`.
- A reopened tab restores its own state; a new tab starts from the user's most recent state.
- URL carries train + selected node (deep links survive refresh).
- `SchemaVersion` on state; unknown version resets layout but keeps drafts as raw text. 256 KB cap (CHECK).
- No modals; centre workspace is never unmounted by navigation. A 409 keeps the draft and shows the conflicting change.

### 5.5 Notifications and escalation
In-app inbox (`Notifications`) plus optional team webhooks (no SMTP in v1, OI-7). Gate entered InProgress → owner. At 50% of the time from entry to `DueOn` → reminder. `DueOn` passed → escalation level 1 to Release Manager; +1 business day → level 2 to RTE. Step late ≥5 min → owner + RTE; ≥30 min → Release Manager. Go/No-Go condition 1 h before expiry → condition owner.

### 5.6 Background services
`SyncPollerService`, `SyncWatchdogService`, `NotificationScheduler` (gate reminders, condition expiry), `ForecastService` (on step events), `BackupService` (SQLite online backup API every 15 min + nightly, 30-day retention, D3), `ExportWorker` (PDF/XLSX jobs from `ExportJobs`), `SessionStateJanitor` (drop client states idle > 30 days).

## 6. API surface

REST under `/api/v1`. Mutations take `If-Match: <Version>`; 409 returns the current row. State changes are action endpoints; 422 bodies name the failing guard: `{"guard":"GateLockout","gates":[…]}`.

| Area | Endpoints |
|---|---|
| Trains | `GET/POST /trains`, `GET/PATCH /trains/{id}`, `POST /trains/{id}:clone`, `:archive`, `GET/PUT /trains/{id}/window`, `GET /trains/{id}/readiness` (guard list used by the "To reach Executing" line; same checks as `:advance`) |
| Lifecycle | `POST /trains/{id}:advance {to}`, `:abort`, `:complete {closeCode, notes}`, `:rehearsed-rollback`, `:capture-baseline` |
| Change record | `GET/PUT /trains/{id}/change-record`, `GET/POST/DELETE /trains/{id}/cis` |
| Products | `GET/POST /trains/{id}/products`, `PATCH/DELETE /products/{id}` |
| Gates | `GET/POST /trains/{id}/gates`, `PATCH /gates/{id}`, `POST /gates/{id}:start` `:certify` `:fail` `:reopen`, `GET /gates/{id}/transitions` |
| Waivers | `POST /gates/{id}/waivers`, `POST /waivers/{id}:approve` (no `:reject`; see Q-005) |
| Tasks | `GET/POST /gates/{id}/tasks`, `PATCH /tasks/{id}`, `POST /tasks/{id}:complete` `:reopen` |
| Parser | `POST /trains/{id}/tasks:parse`, `POST /trains/{id}/tasks:commit` |
| Runbook | `GET/POST /trains/{id}/steps`, `PATCH /steps/{id}`, `PUT /steps/{id}/dependencies` |
| Runs | `POST /trains/{id}/runs {mode}`, `POST /runs/{id}/steps/{stepId}:start` `:done` `:fail` `:skip`, `POST /runs/{id}:end`, `GET /runs/{id}/forecast` |
| Governance | `POST /trains/{id}/gonogo`, `POST /gonogo/{id}/conditions`, `POST /conditions/{id}:close`, `GET/POST /freezes`, `POST /freezes/{id}/overrides`, `GET/PATCH /trains/{id}/pir`, `/trains/{id}/known-issues` |
| Blockers | `GET/POST /trains/{id}/blockers`, `POST /blockers/{id}:resolve` |
| Milestones | `GET/POST /trains/{id}/milestones`, `PATCH/DELETE /milestones/{id}`, `POST /milestones/{id}:done` `:undone` (informational key dates; never a guard, Q-0840..Q-0846) |
| Evidence | `POST /attachments` (multipart, 50 MB max, SHA-256 server-side), `GET /attachments/{id}` |
| Integrations | `GET/POST /trains/{id}/links`, `GET /sync/health`, `GET /sync/alerts`, `POST /sync/alerts/{id}:resolve`, `POST /sync:run-now` |
| Comms | `GET/POST /trains/{id}/comms`, `POST /comms/{id}:preview`, `POST /comms/{id}:dispatch {channel}`, `GET /trains/{id}/comm-schedule` |
| Templates | `GET/POST /templates`, `POST /templates/{id}:approve` `:retire`, `/comm-library` |
| Analytics | `GET /analytics/{metric}?from&to&train&product&riskTier&gateClass` (M1–M15) |
| Import | `POST /imports/{kind}:preview` (multipart), `POST /imports/{id}:commit` |
| Export | `GET /export/{grid}.csv` or `.xlsx`, `POST /exports {kind, trainId}` → job, `GET /exports/{id}` |
| Calendar | `GET /calendar?from&to`, `GET /ics/{token}/trains/{id}.ics`, `GET /ics/{token}/all.ics`, `POST /me/ics-token:rotate` |
| Session / me | `GET/PUT /me/session/{clientId}`, `GET /me/work`, `GET /me/notifications`, `POST /notifications/{id}:read` |
| Audit | `GET /audit?train&entity&from&to`, `GET /audit.csv` (GovernanceOfficer, RTE) |
| Admin | `/users`, `/teams`, `/holidays`, `/webhooks`, `/connectors` |

SignalR hub `/hub/trains`: join a group per open train; server pushes `TrainChanged{id,version}`, `StepChanged`, `ForecastChanged`, `SyncAlertRaised`, `NotificationCreated`, and `ServerTime` every 10 s (client computes clock offset for countdowns). Clients refetch rather than trust payloads.

## 7. Screens

Reference: `docs/ui/mockups/*.html` (open in a browser) and `*.png`. Layout: three panes: left **Stream** (trains by state), centre **workspace**, right **Inspector / drawer**. Details in `UI.md`.

| # | Screen | Must show |
|---|---|---|
| 1 | Planning workspace | "To reach Executing" guard line from the same checks the API uses; products rollup; gate timeline to scale in business days; inline checklist of the selected gate; Inspector with certify eligibility and the reason when disabled |
| 2 | Live runbook | Now, window countdown, forecast finish, time to rollback deadline, steps done; per-step plan / actual / forecast bars on a window-scaled axis; forecast-crosses-deadline alert; running-step drawer (timer, instructions, notes, Done/Fail/Skip) |
| 3 | Bulk parser drawer | Line-numbered text, inline error/warning markers, parsed preview, disabled commit with reason |
| 4 | Communication drawer | Template with token highlighting, hydrated preview with "data as of", T-minus schedule, dispatch actions; centre shows the Go/No-Go record |
| 5 | Sync health | Banner for connector-wide failure; connectors table; mismatches with rule; alerts with counts; watchdog status |
| 6 | Analytics | Six headline figures; charts titled with their finding; CSV/XLSX/SVG per chart |
| 7 | Imports & exports | Import preview (new/updated with diffs/unchanged/error), error table, export catalogue, ICS link, recent jobs with SHA-256 |
| 8 | Evidence pack PDF | See §9 |

## 8. Analytics (queries in `db/analytics.sql`, tested by `tests/reference/seed_and_query.py`)

| Key | Metric | Definition |
|---|---|---|
| M1 | On-time rate | completed trains with actual end ≤ **baseline** planned date ÷ completed |
| M2 | Slip days | actual end − baseline planned date |
| M3 | Gate cycle time | first InProgress → first Certified/Waived, median and p90, per gate name |
| M4 | Gate first-pass | gates never Failed or Waived ÷ gates |
| M5 | Late certification | certified after `DueOn`; average days late |
| M6 | Scope churn | products added + removed since baseline |
| M7 | Step variance | start lateness and duration overrun per step (Live runs) |
| M8 | Runbook summary | total overrun and worst step per train |
| M9 | Blocker aging | open blockers by severity × (<1 d, 1–3, 3–7, ≥7) |
| M10 | Outcomes | rollback rate; close code ≠ Successful ÷ completed |
| M11 | Freeze exceptions | overrides per window; average TTL |
| M12 | Comms timeliness | sent ≤ due ÷ scheduled; missed |
| M13 | Waiver rate | waived ÷ certified Compliance gates, by month |
| M14 | Sync health | links by state; stalest sync; open alerts |
| M15 | Throughput | completed per month; median created→complete days |

M10 and M15 are labelled release-level, not DORA (no CI/CD commit data in v1). Queries run via `SqlQuery<T>` with bound parameters. Evidence-pack generation writes the values it prints to `MetricSnapshots`.

## 9. Import and export

### CSV import
| Kind | Upsert key | Required | Optional |
|---|---|---|---|
| Trains | Title | Title, TargetReleaseDate, RiskTier | Template, ChangeTicketNumber, WindowStart, WindowEnd |
| Products | Train + ProductName | Train, ProductName, VersionTag, ProjectCode | — |
| Gates | Train + SequenceOrder | Train, GateName, SequenceOrder, OffsetDays, RequiredBefore, Owner | GateClass |
| Tasks | Train + Gate + Description | Train, Gate, Description, Owner | Product, Order |
| RunbookSteps | Train + StepCode | Train, StepCode, Title, Section, PlannedStart, DurationMin, Owner | Product, DependsOn, Instructions |
| ExternalLinks | System + Key + Entity | Train, EntityType, EntityRef, System, Key | — |
| Holidays | Day | Day, Name | — |
| Users | Email | Email, DisplayName, Role | Handle |
| Teams | Handle | Handle, Name, Members (`;`-separated emails) | — |

Rules (all enforced in code): UTF-8 (BOM ok), RFC 4180, header required, case-insensitive headers, ≤10,000 rows / 5 MB; **unknown columns are errors**; columns starting `#` are skipped (exports mark read-only fields with `#` so files round-trip); ISO-8601 only, timestamps need `Z` or an offset; owners must exist (email or `@handle`); `DependsOn` = `;`-separated step codes resolved within file + train, cycles rejected; modes **Append** (existing key = error) and **Upsert** (changes audited with before/after), no replace-all; plan imports refused once Executing/Complete; task import into a Certified gate warns it will decertify; preview returns counts + `[{row, column, message}]`; commit is one transaction; `ImportJobs` stores file SHA-256 and errors, and a job with errors cannot commit (trigger).

### Exports
| Export | Format | Contents |
|---|---|---|
| Any grid | CSV, XLSX | current filters + visible columns + ids; XLSX frozen header, typed cells |
| Release report | PDF | summary, products, gate timeline + cycle times, blockers, comms sent, close code, scorecard |
| Runbook run sheet | PDF, CSV | before: plan, owners, deps, rollback; after: actuals, variance, skips with notes |
| Audit evidence pack | PDF + ZIP | change record; certifications with timestamps + SoD check; transition log; waivers + freeze overrides with reasons; Go/No-Go + conditions; attachment manifest with SHA-256; audit-log extract; metric snapshot. ZIP adds files + `manifest.csv` |
| Post-release scorecard | PDF | planned vs actual, on-time, churn, variance, incidents/rollback, PIR |
| Analytics workbook | XLSX | one sheet per metric, parameters sheet first |
| Audit log | CSV | filtered events with before/after JSON |
| Calendar | ICS | gate due dates, milestones, windows, freezes; stable UIDs; per-user revocable token; titles and times only |

Mechanics: PDFs are `ExportJobs` background jobs; footer = generated-at, generated-by, train `Version`, page n of m; PDF SHA-256 stored. Charts in PDFs are the client's ECharts SVG sent with the request; unattended exports use ECharts `renderToSVGString` in a small Node step. CSV/XLSX cells starting `= + - @`, tab or CR are prefixed with `'` (OWASP CSV injection). Evidence packs are regenerated on demand, never cached.

## 10. Non-functional

| Area | Target |
|---|---|
| Scale | 50 concurrent users, 200 trains, 100k audit rows |
| Latency | p95 API < 300 ms at that load |
| Recovery | restore from backup < 30 min; RPO ≤ 15 min |
| Security | OIDC; role checks on every endpoint; antiforgery for cookie auth; secrets via Data Protection; webhook allowlist; CSV injection escaping; attachments stored outside `wwwroot`, served with `Content-Disposition: attachment` |
| Accessibility | keyboard reachable, real `<button>`/`<a>`, WCAG AA contrast (tokens already comply), `prefers-reduced-motion` |
| Platforms | server on Windows Server or Linux; client in current Edge, Chrome, Safari on macOS and Windows |
| Observability | structured logs (Serilog to file), health endpoint `/healthz` reporting DB, poller, watchdog |
| Dev ports | API `http://127.0.0.1:6080`, Vite `http://127.0.0.1:6273` (IPv4 loopback: `localhost` binds IPv6 only on Windows) proxying `/api` and `/hub` |
| Time | server stores and returns UTC; UI formats in `Display:TimeZone` (default America/Chicago, D24); all server time from an injected `TimeProvider` |
