# Decisions — Release Management App

Status as of 2026-09-28. **Confirmed** decisions are not to be revisited in build sessions. Open items carry a provisional default so work is never blocked; build to the default behind the named config key.

## Confirmed decisions

| ID | Decision | Tradeoff accepted |
|---|---|---|
| D1 | **.NET 10 LTS** (supported to Nov 2028), ASP.NET Core minimal APIs, EF Core 10. The source spec said .NET 8, which reaches end of support on 10 Nov 2026. | None material |
| D2 | SQLite, WAL, `busy_timeout=5000`, `foreign_keys=ON` on every connection | One writer at a time: fine for tens of concurrent editors, wrong for hundreds or horizontal scale |
| D3 | Single app instance on local disk (never NFS/SMB). SQLite online backup every 15 min plus nightly, 30-day retention; restore drill in M0 | No high availability; host loss = restore from last backup (≤15 min data loss) |
| D4 | Provider-neutral EF model. SQLite-specific SQL only in the trigger migration and the analytics queries | Triggers and window-function queries are ported by hand if you ever move to MySQL |
| D5 | Timestamps UTC ISO-8601 TEXT via value converter; no `DateTimeOffset`/`decimal` in entities; metrics `double`/`long` | Time-zone display is entirely a UI concern |
| D6 | React + TypeScript + Vite served by the .NET host | Two languages; Blazor's dense-grid ecosystem is thinner |
| D7 | SignalR live updates; clients refetch on `TrainChanged{id,version}` rather than trusting pushed payloads | One persistent connection per tab |
| D8 | Optimistic concurrency: integer `Version` on every mutable table; `If-Match` header; stale write → 409 with current row | Occasional conflicts the user re-applies |
| D9 | Gate offsets are **business days** before `TargetReleaseDate`, using the `Holidays` table | One more table to maintain |
| D10 | Gate waiver needs a written reason (≥20 chars) and a **second** Governance Officer; Waived ≠ Certified in every report | Slower emergency path |
| D11 | ITSM is **read-only** in v1, polled every 5 min (1 min while a deployment window is open) | Up to 5 min staleness; no inbound firewall rules |
| D12 | Integration credentials in ASP.NET Data Protection / OS secret store | Key backup is part of the DR runbook |
| D13 | Webhook dispatch only to admin-allowlisted HTTPS URLs | Admin step before a new channel works |
| D14 | Roles: Viewer, ReleaseManager, RTE, GovernanceOfficer. Only Governance Officers certify or waive Compliance-class gates; in v1 RTE is also admin | Small orgs need ≥2 Governance Officers |
| D15 | Baseline captured automatically when the first gate named "*Freeze*" certifies; immutable rows. Manual "capture baseline" action for trains without a freeze gate | Second copy of planning data |
| D16 | Analytics computed live in SQLite (window functions via `SqlQuery<T>`); `MetricSnapshots` written only when an evidence pack is generated | No pre-aggregation |
| D17 | PDF via **QuestPDF**; charts embedded as SVG from the same ECharts instance | Community licence only under USD 1M revenue and not public/government. See open item 2 |
| D18 | CSV via **Sep** with explicit row→DTO mappers collecting `(row, column, message)`; imports are preview-then-commit, all-or-nothing | Hand-written mapper per import kind |
| D19 | XLSX export via **ClosedXML**; no Excel import | — |
| D20 | Charts via **Apache ECharts 6**, `echarts/core` selective imports, SVG renderer | Heavier than Recharts for a few charts; required for screen/PDF parity |
| D21 | ICS via **Ical.Net 5**, stable UIDs per gate/window, per-user revocable URL token | NodaTime transitive dependency |
| D22 | Audit rows retained 7 years; trains archived, never hard-deleted | Unbounded growth (MBs/year) |
| D23 | Theme: macOS look (system fonts, soft greys, translucent sidebar/toolbar, hairlines, system-blue accent, dark mode) **keeping** the no checkboxes/toggles/pills/boxes rule. Must work identically on Windows | Built against macOS 26 conventions; macOS 27 specifics unverified |
| D24 | Display time zone from config `Display:TimeZone` (default `America/Chicago`); storage and API are UTC; the API returns UTC and the client formats | Per-user time zones deferred to v2 |
| D25 | Exactly **one deployment window and one baseline per train** in v1 (UNIQUE in schema). Re-baselining is not in v1 | A moved release keeps its original baseline, which is the point of on-time % |
| D26 | Gate transitions: Pending→InProgress; InProgress→Certified/Failed/Waived; Failed→InProgress; Certified→InProgress (decertify). Certified→Failed is illegal (reopen first); Waived is terminal | A wrongly waived gate needs a new gate, not an edit |
| D27 | Audit: one `AuditEvents` row per service write; triggers write their own rows for cascaded effects (decertify, evidence lock, baseline capture, PIR creation) with the actor from `LastChangedByUserId` and time from `LastChangedAt` | Services must always set those two columns |
| D28 | Runs: Live uses absolute planned times; Rehearsal shifts every planned time by (run `StartedAt` − earliest planned start of non-Rollback steps) | Rehearsal lateness is relative to when the rehearsal began |
| D29 | Go/No-Go conditions: an open condition past `ExpiresAt` blocks Gated→Executing (trigger) and notifies its owner and the Release Manager; once Executing it only alerts. Freeze overrides are immutable and renew by inserting a new row | The expiry trigger compares against the train's `LastChangedAt` (service clock), falling back to SQLite's clock only if unset |
| D30 | `SyncAlerts` is the alert table for **every** background failure: sources Jira, ServiceNow, SyncEngine (stall), Backup, Export, Webhook, Notifications | Name kept for continuity |
| D31 | CI runs on `windows-latest` and `macos-latest` (build, test, `test_schema.py`; use `python` on Windows). Light and dark mode are required on every screen from M0, with a user override (`data-theme`) | Linux not in CI; server still documented for Windows Server and Linux |
| D32 | Release Manager can do everything an RTE can (including v1 admin, audit and evidence reads); RTE or Release Manager may request a freeze override; approval stays with a different Release Manager or Governance Officer | Role separation between RTE and RM is weaker; SoD is unaffected |
| D33 | The `Team` branch is retired (2026-09-29, John: its branch protection blocked pushes). Work happens on `main`; `start.py reset` pulls `origin/main`; CI runs on pushes to `main` and on pull requests | `main` has no PR gate unless a rule is added later |
| D34 | Viewers keep seeing every user's email in `GET /users` (2026-09-30, John; REOS-71, Q-SEC-A5 b) | Email addresses are visible to every signed-in role |
| D35 | Generated export files of every kind (evidence packs and ZIPs included) are kept 365 days (`Exports:RetentionDays`), then deleted by the export worker; the job row, its SHA-256 and audit trail stay and the download answers 410 (2026-09-30, John; REOS-72, Q-050c, SEC-D8) | A pack older than a year must be regenerated, and shows today's data, not the original |
| D36 | Webhook URLs are stored encrypted with Data Protection (`WebhookDestinations.ProtectedUrl`, purpose per table), unique by an HMAC column (`UrlHmac`), decrypted only by the senders; existing rows are converted at start-up (2026-09-30, John; REOS-73, Q-053e). Schema change | Losing the key ring (or `secrets/webhook-url.hmackey` and the ring) means re-entering every webhook |
| D37 | The Data Protection key ring is encrypted at rest: a certificate when configured, else DPAPI user scope on Windows; outside Development the app refuses to start without one unless `DataProtection:AllowUnprotectedKeys=true` (warned at every start) (2026-09-30, John; REOS-74, Q-052e) | DPAPI ties the ring to one account on one machine: a DR restore elsewhere needs the certificate |
| D38 | Outside Development connectors may reach only `*.atlassian.net` and `*.service-now.com` on 443 unless `Sync:AllowedHosts` says otherwise (explicit value replaces the default; `*` = any public host) (2026-09-30, John; REOS-75, Q-SEC-C3) | On-prem Jira or a custom ServiceNow domain must be listed before it works |
| D39 | No Dependabot; GitHub Action SHA pins are updated by hand from a CI run log (runbook section 13) (2026-09-30, John; REOS-76, Q-SEC-E2) | Pins go stale unless someone checks |
| D40 | Operator NAT64 /96 prefixes (`Sync:Nat64Prefixes`) are judged by their embedded IPv4 like `64:ff9b::/96`, for connectors and webhooks; a bad value stops the start (2026-09-30, John; REOS-77, Q-SEC-C1) | One more key for IPv6-only deployments to set |

## Rejected alternatives (do not reintroduce)

| Alternative | Why rejected |
|---|---|
| Buy Cutover / Digital.ai / Plutora | $1,850/mo starter to six figures/yr; none has sync health, DB-enforced lockout, or hydrated RTE comms |
| .NET 8 | EOL 10 Nov 2026 |
| Boolean `IsCertified` | Can't represent Failed, Waived, decertified |
| Lockout only in UI or service | Any other client bypasses it; triggers are the backstop |
| One `UserSessionState` row per user | Tabs overwrite each other's drafts |
| Stored rollup columns | Drift; compute in SQL |
| Calendar-day gate offsets | Land on weekends/holidays |
| Actuals written onto plan rows | Destroys the baseline |
| Inbound Jira/ServiceNow webhooks in v1 | Firewall + signature work; polling suffices |
| Write-back to ServiceNow in v1 | Makes this app a system of record for change |
| CSV "replace all" mode | Loses content |
| Modals; pills, badges, toggles, checkboxes | Spec and UI rule |
| Pre-aggregated metric tables / nightly jobs | Unneeded at this volume |
| DuckDB | ~100 MB native package, no gain at ~100k rows |
| Headless Chromium PDFs | ~281 MB browser + OS deps |
| EPPlus | Commercial licence for business use |
| CsvHelper | No net10 target, no release since June 2025 |
| Recharts / Nivo / Observable Plot | No SVG export API, or 16–19 months without a release |
| Blazor | Thinner dense-grid ecosystem |
| Bundling SF Pro | Apple licence restricts it to Apple platforms |

## Open items — build to the provisional default

| # | Question | Provisional default (build this) | Config key | Needed by |
|---|---|---|---|---|
| 1 | Is this the Internal Release Management Platform (existing ~45-decision record) or a separate product? | Separate product. **Confirmed by John 2026-09-29: build as a new project.** | — | Before M0 |
| 2 | Deployer's revenue / public status (QuestPDF licence) | Code against QuestPDF; set `QuestPDF.Settings.License` from config; do not commit a licence choice | `Pdf:QuestPdfLicense` | M7 |
| 3 | ServiceNow instance, tables, service account, auth | Build connector against `change_request` + `change_task` Table API, OAuth client-credentials with basic-auth fallback; integration tests use a recorded fake | `Connectors:ServiceNow:*` | M5 |
| 4 | Jira Cloud or DC; products ↔ Fix Versions? | Jira Cloud REST v3, API-token auth; products map to Fix Versions by `ProjectCode` + `VersionTag` | `Connectors:Jira:*` | M5 |
| 5 | Identity provider and group→role mapping | Generic OIDC (Entra ID shaped); roles from a configurable group-claim map; dev mode uses a fake login | `Auth:Oidc:*`, `Auth:RoleMap` | M0 |
| 6 | Windows Server or Linux container? | Support both servers; CI builds on Windows and macOS (see D31); file permissions documented for each | — | M0 |
| 7 | Internal SMTP relay? | No email in v1: in-app inbox + team Teams/Slack webhooks | `Notifications:Smtp:Enabled=false` | M4 |
| 8 | 7-year retention right? PDF/A needed? | 7 years; PDF/A off, spike QuestPDF PDF/A support in M7 and report | `Retention:AuditYears=7` | M7 |
| 9 | Should an open ServiceNow mismatch block Gated→Executing? | No. Mismatch is shown, never blocks | `Governance:BlockOnChgMismatch=false` | M5 |
| 10 | Default gates, classes and offsets | Code Freeze T-5 Standard (before Gated); QA Sign-off T-3 Standard (before Gated); Compliance Sign-off T-2 Compliance (before Executing); CAB Approval T-1 Compliance (before Executing) | seed template | M1 |
| 11 | Client communications application | Not integrated; `mailto:` and copy only | — | M6 |
| 12 | May ticket summaries contain customer/card data? | Assume yes: store keys, states and dates only, never summaries/descriptions | — | M5 |
| 13 | Holiday calendar source | Maintained in-app with CSV import (US federal holidays seeded for 2026–2027) | — | M1 |
| 14 | Can a gate with zero tasks certify? | Compliance-class gates need ≥1 task; Standard gates may be empty. Already in `schema.sql` as `trg_Gate_ComplianceNeedsTask` with tests; **confirmed by John 2026-09-29** | — (schema rule) | M1 |
