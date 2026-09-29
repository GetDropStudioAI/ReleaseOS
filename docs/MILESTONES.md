# Milestones — Release Management App

Nine milestones ordered by dependency. Each is one sprint objective; **Done when** is its acceptance test and must pass in CI before any milestone that depends on it starts.

```
M0 Foundation → M1 Domain + enforcement → M2 Workspace + session
                                              ├─ M3 Checklists + runbook ─┐
                                              ├─ M4 Governance ───────────┼─ M6 Communications ────┐
                                              └─ M5 ITSM sync ────────────┴─ M7 Analytics + exchange ┴─ M8 Hardening + pilot
```
All tables and triggers exist from M1. After M2 merges, M3, M4 and M5 build disjoint services, endpoints and screens and may run as parallel subagent streams on separate branches. Cross-stream calls go only through the interfaces M2 defines (`INotifier`, `IReadinessService`, `IClock` via `TimeProvider`); a stream that needs another stream's data in tests seeds rows directly.

Every prompt assumes Claude Code has read `CLAUDE.md`, `docs/PROJECT_SCOPE.md` and `docs/DECISIONS.md`.

---

## M0 · Foundation
**Prompt:**
> Create the .NET 10 solution per the layout in CLAUDE.md (Domain, Infrastructure, Api, Web + three test projects). Api: minimal APIs, Serilog to file, `/healthz`, OIDC auth with a dev-only fake login (config `Auth:Oidc:*`, `Auth:RoleMap`), authorization policies for the four roles, `TimeProvider` registered (system in prod, `FakeTimeProvider` in tests). Infrastructure: `AppDbContext` on SQLite with a connection interceptor that sets WAL, foreign_keys and busy_timeout on every open; UTC string value converter applied globally; UUIDv7 ids; `UseSqlReturningClause(false)` applied to every entity by convention. `BackupService` using the SQLite online backup API every 15 min plus nightly, 30-day retention, and a `restore` CLI verb; failures raise a `Backup`/`BackupFailed` alert. Web: React + TS + Vite shell with the three-pane layout and `docs/ui/tokens.css` (light + dark via `prefers-color-scheme`, `data-theme` override), building into Api/wwwroot. CI (GitHub Actions) on windows-latest and macos-latest: build, test, `python3 tests/reference/test_schema.py`.

**Done when:** a signed-in dev user sees the empty Stream in light and dark; `/healthz` is green; a backup restores on a second machine and `PRAGMA integrity_check` returns ok; CI is green on Windows and macOS.

## M1 · Domain + enforcement
**Prompt:**
> Implement every table in `db/schema.sql` as EF entities + fluent config (CHECKs, partial unique indexes, FKs with the same delete behaviour). Add migration `0002_Triggers` applying every trigger from `db/schema.sql` verbatim and in file order. Map trigger aborts (`SqliteException`) to 422 `DbRule`. Add a test asserting the migrated database's trigger names equal the set in `schema.sql`. Port `tests/reference/test_schema.py` to xUnit against the migrated DB: same 82 cases, same expected messages. Build domain services `TrainLifecycleService`, `GateService`, `WaiverService`, `TaskService`, `BaselineService` that check each guard first and return typed results mapped to 422 `{guard, details}`. Every service write bumps `Version`, checks `If-Match` (409 with current row), sets `LastChangedByUserId`/`LastChangedAt` from the caller and `TimeProvider`, and writes one `AuditEvents` row in the same transaction. Business-day `DueOn` calculator using `Holidays`, recalculated and audited when `TargetReleaseDate` changes. Seed: default template (OI-10), US federal holidays 2026–2027, 3 demo trains with 4 gates each in mixed states. Admin endpoints and minimal screens for users, teams (with `Handle`) and holidays.

**Done when:** the ported 82-case suite and the trigger-name test pass; API contract tests show each illegal train and gate transition returns 422 naming its guard; each service write produces exactly one service-written audit row, and each cascaded effect (decertify, evidence lock, baseline, PIR) produces one trigger-written row with the correct actor; changing a target date moves every `DueOn` correctly across a holiday.

## M2 · Workspace + session
**Prompt:**
> Build screen 1 (planning workspace) and the Stream per `docs/ui/mockups/Main.html` and `UI.md`: Stream grouped by status with blocker counts and gate glyphs; train header with actions; `IReadinessService` + `GET /trains/{id}/readiness` driving the "To reach Executing" line from the same checks `:advance` uses; products table with rollups computed in SQL; gate timeline SVG on a business-day axis; inline checklist; Inspector for gate/task/product; deployment window edit (`GET/PUT /trains/{id}/window`). Define `INotifier` (writes `Notifications` rows and pushes `NotificationCreated`; M4 adds reminders/escalation and team webhooks behind it). Session engine per PROJECT_SCOPE §5.4. SignalR hub with `TrainChanged` and `ServerTime`; clients refetch on push. Optimistic-concurrency UI: on 409 keep the draft and show the other user's change inline. No modals anywhere.

**Done when:** Playwright closes a tab mid-draft and reopens it with the draft intact; two browser contexts editing one gate produce a visible 409 conflict, never a silent overwrite; a deep link to a gate reopens with the Inspector on that gate; the readiness line and `:advance` agree on every guard of a seeded train; an axe scan has no serious violations.

## M3 · Checklists + runbook
**Prompt:**
> Bulk parser per §5.1 with `ParsePreviews`, golden-file tests (valid, every error type, fuzzed input) and the drawer from `mockups/BulkParser.html`. Runbook editor (steps, sections, dependencies with cycle detection), Rehearsal and Live runs (D28 rebasing), step actions, `ForecastService` per §3 (forecast, rollback deadline, late escalation via `INotifier`) pushing `ForecastChanged`, and screen 2 from `mockups/Runbook.html`. Countdowns use the server-time offset from SignalR and format in `Display:TimeZone`. Freeze lockout is enforced by the trigger; in M3 tests, seed `FreezeWindows`/`FreezeOverrides` rows directly.

**Done when:** parser golden files pass; the scenario in `tests/reference/fixtures/r26-24-scenario.json` (setup and plan inserted by a test helper, actuals applied at the given times on a `FakeTimeProvider`) yields exactly its `expected` block: step variances +1, +3, +5, +16, +19 then forecast +19 on every remaining step, forecast finish 09:29Z (04:29 CT), rollback deadline 09:15Z (04:15 CT), crossing by 14 min with the alert raised, window closing in 8,566 s; countdowns stay correct with the browser clock skewed by 7 minutes; starting a step before its dependency returns 422.

## M4 · Governance
**Prompt:**
> Change record + CIs; Go/No-Go with conditions (immutable decisions, gate snapshot JSON, expiry behaviour per D29); freeze windows and overrides (renew = new row); waiver request/approve; evidence attachments (multipart, SHA-256 server-side, 50 MB, stored outside wwwroot, locked on certify); rollback-rehearsal attestation; PIR and actions; known issues; notification reminders and escalation per §5.5 behind `INotifier` (in-app + team webhooks; SMTP off per OI-7); My work queue; notifications inbox; audit viewer; train templates screen (draft → approved → retired with review date). Screens without mockups follow `UI.md`.

**Done when:** a seeded High-risk train cannot reach Executing until every guard in the train state machine is met, shown through both UI and API; a self-approved waiver or override, a waiver approved by a non-Governance Officer, and an override approved by an RTE are each refused; certifying a gate locks its evidence and later unlock, rename or delete attempts fail; with a `FakeTimeProvider`, a gate past `DueOn` produces level-1 then level-2 escalations on schedule and gate transitions record the fake time.

## M5 · ITSM sync
**Prompt:**
> Jira Cloud and ServiceNow read connectors (OI-3/OI-4), `ConnectorState`, connectors admin screen (credentials via Data Protection), `ExternalLinks` management, `SyncPollerService` (5 min; 1 min while any `DeploymentWindows` row is open), mismatch rules in §5.3.7, `SyncAlerts` fingerprint upsert, 429 backoff, `SyncWatchdogService`, banner + SignalR surfacing, webhook allowlist admin, and screen 5 from `mockups/SyncHealth.html`. Store keys, states and dates only (OI-12). Integration tests use recorded HTTP fakes and `FakeTimeProvider`; no live tenants in CI.

**Done when:** 401, 404 and timeout each produce a visible alert within one interval; 429 produces one within three intervals; a stopped poller produces a `SyncEngine`/`Stalled` alert within three intervals; 288 repeated identical failures leave one alert row with count 288; one clean cycle resolves an alert and keeps its history.

## M6 · Communications
**Prompt:**
> Comm template library screen + per-train templates, T-minus schedule from the template, token hydration per §5.2 with the full allowlist, preview endpoint with `tokenErrors`, `asOf` and `trainVersion`, dispatch via copy (rich text + Markdown), `mailto:` and allowlisted webhooks (failures raise `Webhook`/`DeliveryFailed` alerts), `CommDispatches` log, and the drawer from `mockups/Comms.html`.

**Done when:** every token has a unit test, including empty-list "None"; an unknown token blocks dispatch with a clear message; a dispatched message is retrievable byte-for-byte and can't be edited or deleted; a non-allowlisted or non-HTTPS webhook is refused.

## M7 · Analytics + exchange
**Prompt:**
> Analytics endpoints for M1–M15 from `db/analytics.sql` via `SqlQuery<T>`, binding `from`, `to` and `now` (the request's as-of, defaulting to `TimeProvider` now). Analytics screen from `mockups/Analytics.html` with ECharts (SVG renderer) and per-chart CSV/XLSX/SVG export. CSV import for all 9 kinds per §9 using Sep, with preview/commit, Append/Upsert, audited updates, and the screen from `mockups/ImportExport.html`. CSV+XLSX export for every grid (ClosedXML, injection escaping). PDFs via QuestPDF as `ExportJobs` (failures raise `Export`/`ExportFailed` alerts): release report, run sheet, evidence pack (+ZIP, manifest.csv, MetricSnapshots), scorecard; page 1 of the evidence pack must match `mockups/EvidencePack.html`. Calendar screen (month/week, trains + freeze windows) and ICS feeds with Ical.Net and `IcsTokens` (hash stored; rotate revokes). Spike PDF/A support and report (OI-8).

**Done when:** an integration test opens a copy of `tests/reference/fixtures/seed.db` directly (it was built from `schema.sql`, has no `__EFMigrationsHistory`, and must NOT be migrated; the analytics service takes a connection factory for this), calls every analytics endpoint with the `params` in `expected_metrics.json`, and gets exactly that file's rows (numbers compared to 1 decimal place); every grid export re-imports unchanged; an import with one bad row commits nothing and reports row + column; an evidence pack's stored SHA-256 verifies against the downloaded file; ICS UIDs stay stable when a gate date moves and a rotated token stops working.

## M8 · Hardening + pilot
**Prompt:**
> Load test (k6 or NBomber): 50 concurrent users, 200 trains, 100k audit rows. Timed DR drill from backup, including the Data Protection key ring. Accessibility pass (axe + keyboard-only walkthrough of every screen). Security review: the endpoint→role matrix in PROJECT_SCOPE §1 as an automated test, antiforgery, attachment serving headers, secrets. Write `docs/RUNBOOK_OPERATIONS.md` (install on Windows Server and Linux, backup/restore, key backup, connector credential rotation). Support one real train end to end with the pilot RTE.

**Done when:** p95 < 300 ms at load; restore < 30 min; no serious axe violations; the role-matrix test passes for every endpoint; the pilot RTE runs a real release without a spreadsheet and a Governance Officer accepts its evidence pack.
