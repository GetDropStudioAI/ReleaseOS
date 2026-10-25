# ReleaseOS: Handoff Document

**Prepared:** 25 October 2026 (refreshed after REOS-117 to REOS-122 landed on `main`)
**For:** John Selph (owner)
**Repository:** https://github.com/GetDropStudioAI/ReleaseOS (branches `main` and `Team`, kept identical)
**Jira:** https://amortis.atlassian.net, project REOS (board 34)
**Design:** Figma file `uxMlQ7LTNoW8sIKWSIeY98`

This document says exactly what has been built, how it is built, how to run it, how well it has been tested, what has not been done, and what to do next. It was written from the repository and Jira as they stand, not from memory. Where something is not verified it says so.

---

## 1. Summary

ReleaseOS is an internal release-management tool for **Release Train Engineers, Release Managers and Governance Officers**. It bundles several product versions into one **release train**, enforces **stage gates** that mechanically block the train, runs **live runbooks** during deployment windows, keeps **Jira and ServiceNow** links honest with a visible sync-health view, and generates **stakeholder communications**, **analytics** and an **audit-ready evidence pack**. Later work added **workspaces** (multi-tenant), **deployments with telemetry and automatic rollback**, **approvals**, a **hash-chained audit log**, and a full **UI redesign** from Figma.

**State at handoff**

| Measure | Value |
|---|---|
| Original milestones M0 to M8 (foundation to hardening) | All built and passing their acceptance tests, except the human-led pilot (section 9) |
| Release-type work (RT1 to RT7, REOS-116 to 122) | All seven built and on `main`; REOS-117, 119, 120, 121 and 122 are In Review awaiting your sign-off |
| Redesign (Figma) | All screens restyled except two placeholders (Service catalog, Environments) |
| Jira tickets | 154 in project REOS: 131 Done, 6 In Review, 1 Ready, 16 To Do (10 epics and 6 planned stories, see section 10) |
| Commits | About 270 on `main` |
| Database | 72 tables, 152 triggers, 41 indexes on `main` |
| Code | About 27,700 lines of C# (Domain 3.6k, Infrastructure 17.9k, Api 6.2k), about 16,000 lines of TypeScript and CSS, about 30,400 lines of C# tests, 45 Playwright spec files |
| Last full regression on `main` | Domain 358, DrDrill 2, Infrastructure 374, Api 980 passed; schema oracle 493; web unit 171; Playwright 233; build 0 warnings (on `main` b4c35eb) |
| CI | GitHub Actions on macOS and Windows; the last confirmed green runs are on `9b921a2` and `fb30a05`; the runs on `d298e6e` and `b4c35eb` had not been checked when this was written |
| Browsers | Chrome only for now (decision Q-102a); Edge, Firefox and Safari are planned (section 9; tickets REOS-149 to 153) |

**Three things to know before anything else**

1. **No real release has run through it yet.** The pilot (one real release, no spreadsheet, a Governance Officer accepting the evidence pack) is human-led and has not been done. An automated dress rehearsal of the whole path passes.
2. **Several things have never been tested against the real world:** your identity provider, real Jira and ServiceNow tenants, a Windows Server / Linux / macOS service install, and a real reverse proxy. CI does not use live tenants.
3. **PDFs need a QuestPDF licence choice** (`Pdf:QuestPdfLicense`). The code does not pick one for you.

---

## 2. Where everything lives

| What | Where |
|---|---|
| Source code and history | GitHub `GetDropStudioAI/ReleaseOS`, branch `main` (also pushed to `Team`) |
| Rules every build session follows | `CLAUDE.md` at the repo root |
| Scope (what the app does and every mechanism) | `docs/PROJECT_SCOPE.md` |
| Decisions D1 to D53 and open items | `docs/DECISIONS.md` |
| Judgement calls ("Built to X, say if wrong") and parked questions | `docs/QUESTIONS.md` (about 236 entries) |
| Milestones M0 to M8 with acceptance tests | `docs/MILESTONES.md` |
| UI rules and design system | `docs/UI.md`, `docs/ui/tokens.css`, `docs/ui/redesign-tokens.json`, `docs/ui/mockups/` |
| Operations: install, configure, back up, restore, upgrade | `docs/RUNBOOK_OPERATIONS.md` |
| Pilot instructions and the Governance Officer's acceptance checklist | `docs/PILOT_GUIDE.md` |
| Regression gate and sprint log | `docs/REGRESSION.md` |
| Load test, disaster-recovery drill, PDF/A spike | `docs/LOAD_TEST.md`, `docs/DR_DRILL.md`, `docs/PDFA_SPIKE.md` |
| Security review and scan | `docs/SECURITY_REVIEW.md`, `docs/SECURITY_SCAN.md`, `docs/security/` |
| Proposals behind the redesign and release-type work | `docs/proposals/` |
| Database contract | `db/schema.sql`, `db/analytics.sql`, manual upgrade scripts in `db/upgrades/` (001 to 012) |
| Tickets | Jira project REOS |
| Design | Figma file `uxMlQ7LTNoW8sIKWSIeY98` |
| Continuous integration | `.github/workflows/ci.yml` |

**Open pull requests on GitHub** (all are draft PRs opened by the build sessions): #4, #16, #17, #20 and #24 to #31 are finished: their commits were merged into `main` by hand, so GitHub still shows them open. They can be closed. About 30 `reos-*` branches also remain and can be deleted once you are sure nothing is wanted from them.

---

## 3. What has been built

### 3.1 Core release management (milestones M0 to M8)

**Trains (releases).** Create, plan, clone from a template or a prior train, archive. Bundle several products with versions. Risk tier and change-record fields. State machine: Planning, Gated, Executing, Complete, Aborted. Complete needs a close code; Abort needs a reason (both through confirmation dialogs). Status changes happen only through domain services; the database triggers are the backstop.

**Stage gates.** Ordered gates with business-day offsets before the target date (using a holiday calendar). Owner is a user or a team. Pending, InProgress, Certified, Failed, Waived. Compliance-class gates can only be certified by a Governance Officer who did none of the gate's tasks. Waivers need a written reason of at least 20 characters and a second Governance Officer. Evidence attachments with SHA-256 computed on the server. Certified gates can be decertified (reopened). A database-enforced lockout stops a train reaching Executing until every guard is met.

**Checklists.** Tasks on gates, with a bulk-paste parser (line grammar, preview, all-or-nothing commit) and golden-file tests.

**Runbooks.** Steps with sections, planned start and duration, dependencies with cycle detection. **Rehearsal** and **Live** runs. Actuals live in `StepExecutions` and never overwrite the plan. Countdowns, forecast finish, late-step escalation, rollback section and a rollback deadline alert. Contingency line (planned runbook time against the window and rollback).

**Governance.** Go/No-Go decision with expiring conditions (immutable decisions). Freeze windows and reasoned, expiring overrides (renewing inserts a new row). Waiver request and approval. Post-implementation review (PIR) is created automatically after a rollback or abort, with a due date. Hypercare and known issues. A baseline is captured automatically when the first "Freeze" gate certifies and is immutable.

**ITSM sync (read-only).** Jira Cloud and ServiceNow connectors, polled every 5 minutes (every minute while a deployment window is open). Per-link state and mismatch rules. A **Sync health** screen and banner. Every background failure becomes a `SyncAlerts` row plus a live push; a stalled poller raises its own alert. Credentials are held through ASP.NET Data Protection, never in SQLite.

**Communications.** Template library, per-train templates, a T-minus schedule, token hydration with a full allow-list (27 tokens), preview, copy and `mailto:` dispatch, and webhook dispatch to admin-allow-listed HTTPS URLs only. Dispatched messages are stored byte for byte and cannot be edited or deleted. Webhook addresses are stored encrypted.

**Calendar.** Month, week, timeline and list across trains, freeze windows and milestones. ICS feeds with stable UIDs and a per-user revocable token.

**Analytics.** 24 metrics: M1 to M15 per workspace and M16 to M24 across a set of workspaces, computed live in SQLite. Charts use Apache ECharts (SVG). Per-chart CSV, XLSX and SVG export, and one analytics workbook (XLSX) with a Parameters sheet and one sheet per metric.

**Import and export.** CSV import for 10 kinds (preview then all-or-nothing commit, with a diff preview), CSV and XLSX export of every grid, four PDFs and an evidence pack (a ZIP with a manifest and SHA-256 hashes). PDFs show times in UTC with the display zone beside them.

**Session engine.** Per-tab UI state, autosaved drafts, deep links that win over a saved drawer, and optimistic concurrency everywhere (`If-Match`, a 409 with the current row, never a silent overwrite).

**Notifications.** In-app inbox plus optional team webhooks (no email in v1). Reminders at 50% of a gate's time, escalation level 1 to the Release Manager when overdue and level 2 to the RTE a business day later.

**Audit.** One audit row for every service write, in the same transaction; triggers write their own rows for cascaded effects. Retained 7 years. Hash-chained with verification (REOS-114).

### 3.2 Added after the original scope

| Feature | Tickets |
|---|---|
| **Workspaces (tenants):** every train, template, team, freeze, connector, webhook, comm template, environment, service and deployment belongs to a workspace; people have a role per workspace; a switcher, create-workspace and member management; 49 triggers keep the boundary | REOS-104 to 106 |
| **Release model:** key, owner, team, target environments on every release | REOS-107 |
| **Overview and approval queue endpoints** | REOS-108 |
| **Environments and service catalog** (API only, see section 9) | REOS-109 |
| **Deployments:** rollout state and events, a push **ingest API** with keys and rate limits | REOS-110 |
| **Telemetry:** metric samples, thresholds, SLOs and canary analysis | REOS-111 |
| **Deployment control:** webhooks, automatic rollback, run pause | REOS-112 |
| **Approvals:** required approvers, Request changes, policies | REOS-113 |
| **Hash-chained audit** with verification | REOS-114 |
| **Release types** (Beta with a number 1 to 3, CERT, PenTest Cert, Production, per workspace) with default templates, and filters by type | REOS-116 (RT1) |
| **Clients and segments** (Standard, On-Demand with a CERT environment), CSV import | REOS-118 (RT3) |
| **Versions:** link the releases of one product version in order (Beta 1 to 3, CERT, PenTest Cert, Production); the chain shows on the release, calendar and overview; a slipped release flags later ones as an advisory; create the next release from the previous one | REOS-117 (RT2) |
| **Client schedules:** an offered date per On-Demand client (Standard date plus `Release:OnDemandOffsetDays`, default 7), chosen dates audited, a combined Standard + On-Demand view, freeze warnings | REOS-119 (RT4) |
| **Calendar, overview and ICS** show release types, version chains and client dates, filterable by type and segment | REOS-120 (RT5) |
| **Executive analytics:** metrics M16 to M24 across the workspaces a person belongs to, VP and Director views, grouping by workspace, team, type, segment and quarter, on-time % separately for CERT and production, a portfolio PDF and the executive grouping in the workbook | REOS-121 (RT6) |
| **Template governance:** tasks and milestones in templates, template versions, an approver who is not the author, Compliance gates locked on the release | REOS-122 (RT7) |
| **Documents and messages carry the workspace and release identity**, with deep links through `App:PublicBaseUrl` | REOS-137, 139 |
| **Calendar feeds name their workspace and key** | REOS-138 |
| **UTC with local time** on every screen (D44) | REOS-94, 136 |

### 3.3 Screens (sidebar)

| Group | Screen | State |
|---|---|---|
| Workspace | Overview | Built, redesigned |
| | Portfolio and calendar | Built, redesigned |
| | Releases (full-width table with filters; the old Stream is gone, D50) | Built, redesigned |
| | Orchestration (pipeline, Rehearsal and Live run views, run panels) | Built, redesigned |
| | Approvals | Built, redesigned |
| | Deployments (list and monitoring page with charts, rollback and pause) | Built, redesigned |
| | Audit history | Built, redesigned |
| Management | **Service catalog** | **Placeholder only** (shows "B8" page) |
| | **Environments** | **Placeholder only** (shows "B6" page) |
| | Teams and settings (users, teams, holidays, release types, versions, clients, workspace) | Built |
| | Templates | Built |
| | Comm library | Built |
| | Connectors | Built |
| More | My work | Built, redesigned |
| | Analytics | Built, redesigned |
| | Imports and exports | Built, redesigned |
| Elsewhere | Release detail (planning, governance, closeout, dependencies tabs) | Built |
| | Sync health, Inbox | Built, redesigned |
| | Sign-in, refused sign-in, loading, server unreachable, stopped, no workspace, unknown path | Built, redesigned |
| | Component gallery (`/gallery`) | Built (for design review) |

The design system lives in `src/ReleaseMgmt.Web/src/ui/` (buttons, status chips, count badges, metric cards, panels, tables, tabs, dialogs, side panel, shared loading, empty and error states, a `Time` component). It uses the tokens in `docs/ui/tokens.css`, which are generated from the Figma variables for light and dark mode.

### 3.4 API

About 280 route registrations across 40 endpoint files in `src/ReleaseMgmt.Api/Endpoints/`: trains and lifecycle, gates and governance, runbooks and runs, plan structure, templates, communications, connectors and sync, calendar and ICS, analytics, imports and exports, notifications, audit, approvals, deployments and ingest, deployment control, environments and services, release model, release types, clients, workspaces and admin, session state, and the SignalR hub. Every route has a role policy, and a test enumerates every route and checks 900 role combinations.

---

## 4. How it is built

### 4.1 Stack (locked)

.NET 10 LTS (`net10.0`), ASP.NET Core minimal APIs, EF Core 10 with SQLite in WAL mode, SignalR, React + TypeScript + Vite (built into `wwwroot` and served by the .NET host: one deployable), Apache ECharts 6, QuestPDF, Sep (CSV), ClosedXML (XLSX), Ical.Net 5 (ICS). Tests: xUnit and Playwright. Fonts come from the operating system and SF Pro is never bundled.

### 4.2 Solution layout

```
src/
  ReleaseMgmt.Domain/          entities, enums, domain services (no EF, no ASP.NET)
  ReleaseMgmt.Infrastructure/  EF DbContext, migrations, trigger migration, connectors, exporters
  ReleaseMgmt.Api/             minimal API endpoints, SignalR hub, auth, background services
  ReleaseMgmt.Web/             React + Vite app (outputs to Api/wwwroot)
tests/
  ReleaseMgmt.Domain.Tests / Infrastructure.Tests / Api.Tests / DrDrill.Tests / LoadTests
  e2e/                         Playwright (42 spec files)
  reference/                   Python oracle tests against db/schema.sql
  tools/                       tooling tests
db/                            schema.sql, analytics.sql, upgrades/
tools/                         generators and drills (gen_entities.py, gen_tokens.py, dr_drill.py, run_load_test.py ...)
```

### 4.3 The non-negotiable rules (from `CLAUDE.md`)

1. `db/schema.sql` is the contract. Migrations must produce the same tables, CHECKs, indexes and triggers; triggers are applied verbatim and in file order. A CI test asserts the migrated database has exactly the trigger names in the file.
2. Status changes only through domain services; no endpoint PATCHes a status.
3. Every service write stamps `Version` and writes one `AuditEvents` row in the same transaction.
4. Time comes from an injected `TimeProvider`, never `DateTime.UtcNow`.
5. Timestamps are UTC ISO-8601 text; `DateTimeOffset` and `decimal` are banned in entities.
6. Ids are UUIDv7 text (trigger-created rows use random hex: a documented exception).
7. Actuals never overwrite the plan; baselines are immutable.
8. Fail fast: any background failure becomes a `SyncAlerts` row and a live push.
9. UI follows the Figma redesign; status is never colour alone; no checkboxes or toggles; tokens only, no hard-coded colours; times are UTC and labelled with local time beside them.
10. Credentials only through Data Protection or the OS secret store, never in SQLite or committed settings.

### 4.4 Roles

Viewer, RTE, ReleaseManager, GovernanceOfficer. The Release Manager can do everything an RTE can plus Go/No-Go and freeze-override approval. In v1, RTE and Release Manager are also the administrators. Separation of duties is enforced in the database: a Compliance-gate certifier must be a Governance Officer who did none of the gate's tasks; a waiver approver is a different Governance Officer; a freeze-override approver is a Release Manager or Governance Officer who is not the requester.

### 4.5 Database

SQLite, WAL mode, `foreign_keys=ON`, `busy_timeout=5000` on every connection. 72 tables, 152 triggers, 41 indexes on `main`. Twelve manual upgrade scripts in `db/upgrades/` take an older database to the current schema (001 identity, 002 encrypted webhooks, 003 milestones, 004 workspaces, 005 workspace roles, 006 release model and deployments, 007 telemetry/control/approvals/audit chain, 008 release types per release, 009 clients and segments, 010 release versions, 011 client dates, 012 template governance). `start.py` saves a copy of your database and runs the scripts automatically when a pull changes the schema.

### 4.6 Background services

Backup (every 15 minutes plus nightly, 30-day retention), sync poller, sync watchdog, notification scheduler, export worker, audit-chain worker, telemetry maintenance, session-state janitor, hub session monitor, server-time broadcaster.

---

## 5. Running it

**Locally** (needs Python 3.9+, .NET 10 SDK, Node 22):

```
python start.py            # starts the API (:6080) and the frontend (:6273), opens the browser
python start.py status     # what is the running instance doing
python start.py reset      # pull the latest from origin/main, restart both
python start.py stop       # stop both
```

In Development there is an anonymous dev sign-in. **Never run Development in production.**

**In production** follow `docs/RUNBOOK_OPERATIONS.md` (install on Windows Server, Linux or macOS; build and publish; configuration; running as a service; backups; restore; upgrading; credential rotation; monitoring; pilot checklist; verified and not verified).

**Key configuration** (full list in the runbook, section 4):

| Key | Purpose |
|---|---|
| `Db:Path`, `Backup:Directory`, `Attachments:Directory`, `Exports:Directory`, `DataProtection:KeysDirectory`, `Sync:Credentials:Directory` | Absolute paths; keep the key and credential folders on the same volume you back up |
| `Auth:Oidc:Authority`, `ClientId`, `ClientSecret` | Organisation sign-in (OpenID Connect; the provider must return an `email` claim) |
| `Auth:RoleMap`, `Auth:DefaultRole` | Map identity-provider groups to the four roles; empty default role refuses unmapped people |
| `Display:TimeZone` | Zone shown beside UTC and used for business days (default America/Chicago) |
| `App:PublicBaseUrl` | The address people open the app at; used for deep links in messages (none when unset) |
| `Pdf:QuestPdfLicense` | **Required for every PDF**: Community, Professional or Enterprise |
| `Workspace:DefaultName`, `Workspace:Region` | Name of the first workspace |
| `Sync:AllowedHosts`, `Sync:Nat64Prefixes`, `Proxy:KnownProxies` | Outbound connector hosts, IPv6-only networks, a reverse proxy not on the same host |
| `Ingest:*` | Rate limits for the deployment ingest API |
| `ReleaseTypes:SeedAtStart` (true) | Seeds Beta, CERT, PenTest Cert and Production into each workspace |
| `Release:OnDemandOffsetDays` (7), `Versions:MinGapDays` (1), `Versions:NextOffsetDays` | Provisional defaults for client schedules and version chains |
| `Analytics:FiscalYearStartMonth` (1) | 1 means calendar quarters; set 1 to 12 for a fiscal year |
| `Exports:RetentionDays` (365), `Retention:AuditYears` (7) | Retention |

Outside Development the app refuses to start without an encrypted key ring (a certificate, or DPAPI on Windows) unless `DataProtection:AllowUnprotectedKeys=true`, and refuses unlisted hosts.

---

## 6. Quality and verification

**Automated regression** (`docs/REGRESSION.md`): build with warnings as findings; all .NET tests; schema oracle; upgrade scripts reach `schema.sql`; metrics oracle; tooling tests; workflow check; web unit tests; web build; Playwright (axe accessibility scans in light and dark and keyboard-only tours).

| Check | Last full local regression on `main` (b4c35eb) |
|---|---|
| Build | 0 errors, 0 warnings |
| Domain / DrDrill / Infrastructure / Api tests | 358 / 2 / 374 / 980 passed |
| Schema oracle | 493 passed |
| Web unit tests | 171 passed |
| Playwright | 233 passed |
| CI (macOS and Windows) | Green on 33bff9f, b9c6f46, 9b921a2 and fb30a05; not yet checked on d298e6e and b4c35eb |

**Performance** (`docs/LOAD_TEST.md`): 50 concurrent users, 200 trains, 100,000 audit rows: p95 about 104 ms against a 300 ms limit, no errors. Measured on a shared 4-core container, not your host.

**Disaster recovery** (`docs/DR_DRILL.md`): restore from total loss to a healthy API in seconds on test data (3.6 s for a 43 MB database, 13.5 s for 360 MB), including the key ring. The drill found and fixed a defect (the API could not start on a restored database).

**Security** (`docs/SECURITY_REVIEW.md`, `docs/SECURITY_SCAN.md`): endpoint-to-role matrix test; cross-site request guard; attachment hardening; no secret found in the database, logs, audit rows, alerts or backups; encrypted webhook URLs and key ring; connector host allow-list; cookie, session and forwarded-header hardening. A later OWASP-based scan produced further fixes.

**Accessibility:** axe scans and keyboard-only tours in both themes on every screen.

**What is NOT verified** (also in the runbook):

- Sign-in against your real identity provider.
- Connectors against real Jira and ServiceNow tenants.
- The service definitions and certificate commands in the runbook on a real Windows Server, Linux or macOS host.
- Reverse-proxy WebSocket and logging configuration.
- DPAPI under a real service account.
- The pilot itself.
- Browsers other than Chrome (Edge, Firefox, Safari are planned).
- Load and recovery figures on your production hardware.

**Known fragile tests.** CI on macOS and Windows has produced timing-related failures that were fixed at the root, not by loosening them (sync poller races, PDF text extraction differences, environment-variable races, a focus race in the audit keyboard test). The audit test (REOS-154) is fixed and has passed CI in several runs since; keep an eye on it. A full Playwright run writes about 700 MB of Development logs to `src/ReleaseMgmt.Api/logs`; on a machine with a small disk allowance, clear old logs first or unrelated specs fail with `ENOSPC`.

---

## 7. How the work was done (so you can continue it)

- **Rules first:** `CLAUDE.md` tells every build session to build, not decide. Anything not covered goes into `docs/QUESTIONS.md` as "Built to X, say if wrong". Open items in `docs/DECISIONS.md` are built to a provisional default behind a named configuration key.
- **Parallel sessions:** feature stories were built by separate remote Claude Code sessions, each on its own branch (`reos-NNN`), each opening a draft PR and running its own full regression. One coordinating session reviewed each diff, merged it locally (keeping both sides of documentation conflicts), ran the **full regression** on the merged tree, and only then pushed `main` and `Team`.
- **Never push failing tests.** A test-only fix to a single spec may go out once that spec passes.
- **Jira workflow:** To Do, Ready, In Progress, In Review, Done. Each ticket was walked through every lane. **Done only after your sign-off.** Comments on each ticket record what was merged and what the regression showed.
- **Documentation stays current:** counts in `CLAUDE.md`, README and the runbook are updated when the schema changes.
- **Schema changes** follow one order: edit `db/schema.sql`, run `python3 tests/reference/test_schema.py`, add a manual upgrade script with test coverage, regenerate entities (`python3 tools/gen_entities.py`), re-scaffold and patch the two migrations (`dotnet ef ...` then `python3 tools/patch_migrations.py`).

**Commands you need**

```
dotnet build                                   # warnings count as findings
dotnet test
python3 tests/reference/test_schema.py         # schema oracle
python3 tests/reference/test_upgrades.py       # manual upgrade scripts reach schema.sql
python3 tests/reference/seed_and_query.py      # seeds history through the triggers, runs all 15 metrics
python3 tests/tools/test_start.py              # tooling
python3 tests/tools/test_workflows.py          # after editing .github/workflows
cd src/ReleaseMgmt.Web && npm test && npm run build
cd tests/e2e && npx playwright test            # Playwright
python3 tools/run_load_test.py                 # load test (not part of CI)
python3 tools/dr_drill.py                      # disaster-recovery drill
```

---

## 8. Decisions

53 confirmed decisions are in `docs/DECISIONS.md`. The ones that shape the product most:

| ID | Decision |
|---|---|
| D1, D2, D3 | .NET 10 LTS; SQLite in WAL mode on local disk; one app instance; backups every 15 minutes |
| D8 | Optimistic concurrency with `If-Match` and 409 |
| D9, D26 | Gate offsets in business days; the gate state machine |
| D10, D14, D32 | Waivers need a reason and a second Governance Officer; the four roles |
| D11, D12, D13 | ITSM read-only; credentials via Data Protection; webhooks only to allow-listed HTTPS URLs |
| D22, D27, D30 | 7-year audit retention; one audit row per service write; `SyncAlerts` for every background failure |
| D41, D43, D44 | The Figma redesign replaces the old UI rules; dark mode stays; UTC is the standard time with local time beside it |
| D42 | The app tracks deployments and their telemetry |
| D45 | Multiple workspaces on one host |
| D46 to D49 | Release types, Standard and On-Demand clients, linked Beta/CERT/PenTest/Production releases, executive analytics across the person's workspaces |
| D50 to D53 | Releases as a table (no Stream); PDFs show UTC with the display zone beside it; `App:PublicBaseUrl` for links; confirmation dialogs for Complete and Abort |

**Decisions still open** (built to provisional defaults; none blocks work):

| Open item | Default built |
|---|---|
| QuestPDF licence tier (revenue / public status) | You set `Pdf:QuestPdfLicense`; nothing is committed |
| ServiceNow tables, account and auth | `change_request` and `change_task` Table API; OAuth client credentials with basic fallback |
| Jira Cloud or Data Center; product-to-version mapping | Jira Cloud REST v3, API token; products map to Fix Versions |
| Identity provider and group mapping | Generic OIDC, configurable group-claim map |
| Windows Server or Linux | Both supported and documented; CI runs macOS and Windows |
| Email (SMTP) | None in v1: in-app inbox plus Teams/Slack webhooks |
| Retention and PDF/A | 7 years; PDF/A off |
| Should an open ServiceNow mismatch block a gate | No, it shows only |
| Customer or card data in ticket summaries | Assume yes: only keys, states and dates are stored |
| Release-type rules still provisional | A chosen client date may not be earlier than the Standard date; clients may choose CERT dates too; 7-day On-Demand offset |
| Template governance (proposal Q8 to Q10) | Built to the recommended defaults: planners cannot remove or downgrade Compliance gates; template milestones move with the target; a template needs an approver who is not the author, plus a Governance Officer when a Compliance gate changes (Q-122a) |
| Is a quarter calendar or fiscal | Calendar, behind `Analytics:FiscalYearStartMonth` (Q-121a) |

`docs/QUESTIONS.md` lists every judgement call made during the build with its reasoning. Read the entries for the areas you plan to change.

---

## 9. What is NOT built, and known gaps

| Gap | Detail |
|---|---|
| **Service catalog and Environments screens** | Sidebar entries open placeholder pages. The API for environments and services exists (REOS-109: list, get, create, update, archive, unarchive, and linking a product to a service). A UI story is needed. |
| **The pilot** | A real release run by the pilot RTE without a spreadsheet, with a Governance Officer accepting the evidence pack (REOS-54 acceptance). Guide and checklist are written (`docs/PILOT_GUIDE.md`). Not yet done. |
| **Real-world integrations** | OIDC, Jira, ServiceNow, reverse proxy, service install: see section 6. |
| **Release-type work** | RT1 to RT7 are built and on `main`. The owner questions behind them are built to provisional defaults (section 8). Waiting only for your sign-off. |
| **Not shown in lists or exports yet** | Version chain and client dates in the Releases list, CSV and PDFs (the calendar, overview and ICS feeds do show them). |
| **Cross-browser** | Edge, Firefox, Safari. Plan in REOS-149 to 153. |
| **Redesign QA** | REOS-102 (visual match to Figma at 1600px, contrast, keyboard, live regions) and REOS-142 (extend QA to every restyled screen). Not started; automated axe scans cover much of the accessibility part. |
| **Out of v1 by design** | Write-back to Jira or ServiceNow, inbound webhooks, SMTP email, cross-train dependencies (except version chains), Excel import, high availability or horizontal scale, per-user time zone storage, ad hoc report builder, executing deployments (the app records, it does not deploy). |
| **Jira housekeeping** | Milestone epics REOS-3 to 9 still say To Do although M2 to M8 are built; epic REOS-55 and the redesign epic REOS-90 are To Do. |

---

## 10. Status of work at handoff

**Nothing is in flight.** Everything built is on `main` and `Team`.

| Ticket | State |
|---|---|
| REOS-117, 119, 120, 121, 122 (release-type stories) | In Review, waiting for your sign-off |
| REOS-154 (macOS CI audit-test flake) | In Review |
| REOS-102 (redesign QA) | Ready, not started |
| REOS-142 (extend QA to every restyled screen) | To Do |
| REOS-149 to 153 (cross-browser plan for Edge, Firefox, Safari) | To Do. They sit in Sprint 7, but you wanted them in the next sprint: please move them (I cannot move tickets between sprints). |

**Suggested order for what is next**

1. Sign off (or return) the In Review tickets.
2. A story for the Service catalog and Environments screens (the API is ready).
3. REOS-102 and REOS-142 (QA) now that the screens have settled. Chrome only for the moment.
4. Cross-browser (REOS-149 to 153).
5. Run the pilot (section 11).
6. Optional clean-up worth doing: lower the Development log level for `Microsoft.EntityFrameworkCore.Database.Command` so E2E runs write far smaller logs.

---

## 11. Your handover checklist

**Before a pilot**

- [ ] Choose and set `Pdf:QuestPdfLicense`.
- [ ] Provision a host (runbook section 2 to 5) and run the service definitions on a staging host first.
- [ ] Configure OIDC and the role map; sign in with each pilot role.
- [ ] Create connector service accounts (read-only) for Jira and ServiceNow; use **Test connection** and watch **Sync health**.
- [ ] Set `App:PublicBaseUrl` and the reverse proxy (WebSocket for SignalR).
- [ ] Encrypted key ring: a certificate (or DPAPI on Windows); back up `keys`, `secrets` and `attachments` with the database.
- [ ] Do a restore drill on the host and record the time (runbook section 7).
- [ ] Review and approve the "Standard release" template, check holidays, create teams.
- [ ] Follow `docs/PILOT_GUIDE.md` and the Governance Officer's acceptance checklist.

**Access you should confirm you hold**

- [ ] GitHub organisation `GetDropStudioAI`: repository admin; Actions enabled for macOS and Windows runners.
- [ ] Jira site amortis.atlassian.net, project REOS (workflow To Do, Ready, In Progress, In Review, Done).
- [ ] Figma file `uxMlQ7LTNoW8sIKWSIeY98`.
- [ ] Any QuestPDF licence you take out.
- [ ] No credentials are stored in the repository. Connector credentials, the webhook secret path and the key ring live on the host, not in git.

**Housekeeping**

- [ ] Close finished draft PRs #4, #16, #17, #20 and #24 to #31 and delete merged `reos-*` branches.
- [ ] Update the status of the milestone epics REOS-3 to 9.
- [ ] Put the cross-browser tickets into a sprint.

---

## 12. Glossary

| Term | Meaning |
|---|---|
| Train / release | One bundled release of several product versions with a target date |
| Gate | A stage-gate checkpoint with tasks, an owner, a due date and a class (Standard or Compliance) |
| RTE | Release Train Engineer |
| Go/No-Go | The Release Manager's recorded decision before execution, with expiring conditions |
| Freeze / chill window | A period when deploying is blocked or discouraged; an override needs a reason and an approver |
| PIR | Post-implementation review, required after a rollback or abort |
| Evidence pack | A PDF plus a ZIP of attachments with SHA-256 hashes, for audit |
| Workspace | A tenant; owns its trains, teams, connectors, and so on |
| Release type | Beta (1 to 3), CERT, PenTest Cert, Production |
| Standard / On-Demand | Client segments; On-Demand clients have a CERT environment and may choose another date |
| Version (RT2) | A link between the releases of one product version, in order |
| D-number / Q-number | A decision in `DECISIONS.md` / a judgement call in `QUESTIONS.md` |
| REOS-nnn | A Jira ticket |

---

## 13. Appendix: Jira ticket index (all 154)

Generated from Jira on 25 October 2026. "Epic" entries are containers. Status is the Jira status at that time.

| Ticket | Type | Status | Summary |
|---|---|---|---|
| REOS-1 | Epic | Done | M0 Foundation |
| REOS-2 | Epic | Done | M1 Domain + enforcement |
| REOS-3 | Epic | To Do | M2 Workspace + session |
| REOS-4 | Epic | To Do | M3 Checklists + runbook |
| REOS-5 | Epic | To Do | M4 Governance |
| REOS-6 | Epic | To Do | M5 ITSM sync |
| REOS-7 | Epic | To Do | M6 Communications |
| REOS-8 | Epic | To Do | M7 Analytics + exchange |
| REOS-9 | Epic | To Do | M8 Hardening + pilot |
| REOS-10 | Story | Done | Solution skeleton and CI on Windows + macOS |
| REOS-11 | Story | Done | SQLite/EF Core infrastructure conventions |
| REOS-12 | Story | Done | API host: Serilog, /healthz, TimeProvider |
| REOS-13 | Story | Done | Authentication and role policies (OIDC + dev login) |
| REOS-14 | Story | Done | BackupService and restore CLI |
| REOS-15 | Story | Done | Web shell with three-pane layout, light and dark mode |
| REOS-16 | Story | Done | EF entities and fluent config for all 47 tables |
| REOS-17 | Story | Done | Triggers migration, trigger-name test, DbRule 422 mapping |
| REOS-18 | Story | Done | Port the 82-case trigger suite to xUnit |
| REOS-19 | Story | Done | Domain services with audit and optimistic concurrency |
| REOS-20 | Story | Done | Business-day DueOn calculator |
| REOS-21 | Story | Done | Seed data and admin screens (users, teams, holidays) |
| REOS-22 | Story | Done | Stream and train header (screen 1) |
| REOS-23 | Story | Done | IReadinessService and readiness endpoint |
| REOS-24 | Story | Done | Products, gate timeline, checklist, Inspector, window edit |
| REOS-25 | Story | Done | INotifier and SignalR hub |
| REOS-26 | Story | Done | Session engine and deep links |
| REOS-27 | Story | Done | Concurrency conflict UI and accessibility (light + dark) |
| REOS-28 | Story | Done | Bulk checklist parser and drawer |
| REOS-29 | Story | Done | Runbook editor with dependency cycle detection |
| REOS-30 | Story | Done | Rehearsal and Live runs, step actions |
| REOS-31 | Story | Done | ForecastService and late escalation |
| REOS-32 | Story | Done | Live runbook screen (screen 2) |
| REOS-33 | Story | Done | Change record, CIs and Go/No-Go with conditions |
| REOS-34 | Story | Done | Freeze windows, overrides and waivers with separation of duties |
| REOS-35 | Story | Done | Evidence attachments |
| REOS-36 | Story | Done | Rollback attestation, PIR, actions, known issues |
| REOS-37 | Story | Done | Notifications, reminders and escalation |
| REOS-38 | Story | Done | My work, inbox, audit viewer and templates screens |
| REOS-39 | Story | Done | Jira Cloud and ServiceNow read connectors |
| REOS-40 | Story | Done | SyncPollerService, alert fingerprinting and 429 backoff |
| REOS-41 | Story | Done | SyncWatchdogService and mismatch rules |
| REOS-42 | Story | Done | Sync Health screen, banner and webhook allowlist admin |
| REOS-43 | Story | Done | Comm template library and T-minus schedule |
| REOS-44 | Story | Done | Token hydration and preview endpoint |
| REOS-45 | Story | Done | Dispatch (copy, mailto, webhook), log and drawer |
| REOS-46 | Story | Done | Analytics endpoints M1-M15 |
| REOS-47 | Story | Done | Analytics screen with ECharts |
| REOS-48 | Story | Done | CSV import (9 kinds) and ImportExport screen |
| REOS-49 | Story | Done | CSV/XLSX grid exports |
| REOS-50 | Story | Done | PDF exports and evidence pack |
| REOS-51 | Story | Done | Calendar screen and ICS feeds |
| REOS-52 | Story | Done | Load test and DR drill |
| REOS-53 | Story | Done | Accessibility and security review |
| REOS-54 | Story | Done | Operations runbook and pilot |
| REOS-55 | Epic | To Do | Local run tooling and account security |
| REOS-56 | Story | Done | start.py: run backend and frontend with one command |
| REOS-57 | Story | Done | Sign out and exit: in the app and from the command line |
| REOS-58 | Story | Done | Reset: pull latest from Team, restart backend and frontend, refresh the browser |
| REOS-59 | Story | Done | Password reset with an MFA code or authenticator app (design decision pending) |
| REOS-60 | Epic | Done | Security scan 2026-09-30: OWASP API Top 10 remediation |
| REOS-61 | Defect | Done | SEC-B8: users are matched to identity-provider sign-ins by email, not by issuer + subject |
| REOS-62 | Defect | Done | SEC-B5: server-side sign-out is forgotten on restart, so a copied cookie works again |
| REOS-63 | Defect | Done | SignalR connection outlives sign-out, session expiry and user deactivation |
| REOS-64 | Defect | Done | Sign-in changes a user's role or reactivates them without an audit row |
| REOS-65 | Defect | Done | A refused organisation sign-in ends on a 500 error page |
| REOS-66 | Defect | Done | SEC-D7: JSON endpoints have no per-field length limits |
| REOS-67 | Defect | Done | SEC-D10: failed calendar-feed guesses lock out valid calendars behind a shared address |
| REOS-68 | Defect | Done | Production starts with AllowedHosts = * (any Host header accepted) |
| REOS-69 | Defect | Done | /healthz always reports the sync poller and watchdog as notConfigured |
| REOS-70 | Defect | Done | Test hosts share one static Serilog logger, so log assertions can read another host's events |
| REOS-71 | Task | Done | Decision: should Viewers see every user's email address? (Q-SEC-A5) |
| REOS-72 | Task | Done | Decision: retention period for generated export files (Q-050c, SEC-D8) |
| REOS-73 | Task | Done | Decision: encrypt webhook URLs at rest (Q-053e) |
| REOS-74 | Task | Done | Decision: protect the Data Protection key ring at rest (Q-052e) |
| REOS-75 | Task | Done | Decision: restrict connectors to the real Atlassian and ServiceNow hosts by default (Q-SEC-C3) |
| REOS-76 | Task | Done | Decision: enable Dependabot for GitHub Actions, NuGet and npm (Q-SEC-E2) |
| REOS-77 | Task | Done | Decision: operator NAT64 prefixes for the SSRF guard (Q-SEC-C1) |
| REOS-78 | Task | Done | Security scan remediation: 35 defects fixed (SEC-A, B, C, D, E) |
| REOS-79 | Epic | Done | Create records from the app (trains, products, gates, links, users, milestones) |
| REOS-80 | Story | Done | New train: blank, from an approved template, or cloned from a prior train |
| REOS-81 | Story | Done | Add, edit and remove products and gates on a train |
| REOS-82 | Story | Done | External links screen on the train (add, see state, remove) |
| REOS-83 | Story | Done | Add users ahead of their first sign-in (Admin) |
| REOS-84 | Story | Done | Milestones: named key dates on a train |
| REOS-85 | Task | Done | Warn when two trains' deployment windows overlap and share a CI or product |
| REOS-86 | Bug | Done | Train header cannot complete (close code) or abort a train |
| REOS-87 | Task | Done | Post-implementation review required after a rollback or abort, with a due date |
| REOS-88 | Task | Done | Contingency line: planned runbook time vs the window and rollback deadline |
| REOS-89 | Task | Done | Contingency line: planned runbook time vs the window and rollback deadline |
| REOS-90 | Epic | To Do | UI redesign: ReleaseOS workspace (7 screens from Figma) |
| REOS-91 | Task | Done | Decision: map the redesign's vocabulary and entities to the current data model |
| REOS-92 | Task | Done | Design gaps: screens, states and flows the Figma file does not cover |
| REOS-93 | Story | Done | Redesign foundation: design tokens and shared component library |
| REOS-94 | Story | Done | App shell: sidebar navigation, utility bar and page heading |
| REOS-95 | Story | Done | Screen: Organization overview |
| REOS-96 | Story | Done | Screen: Release portfolio and calendar |
| REOS-97 | Story | Done | Screen: Release detail and readiness |
| REOS-98 | Story | Done | Screen: Orchestration pipeline |
| REOS-99 | Story | Done | Screen: Approvals and governance |
| REOS-100 | Story | Done | Screen: Deployment monitoring |
| REOS-101 | Story | Done | Screen: Audit history |
| REOS-102 | Task | Ready | Redesign QA: accessibility, visual match to Figma and live-data checks |
| REOS-103 | Task | Done | Figma: colour variables (light and dark) and dark versions of the seven frames |
| REOS-104 | Story | Done | Workspaces: model, membership and migration of existing data |
| REOS-105 | Story | Done | Workspaces: scope every query, permission and live update to the current workspace |
| REOS-106 | Story | Done | Workspaces: switcher, create workspace and manage members |
| REOS-107 | Story | Done | Release model: key, owner, team, target environments, release branch and on-call |
| REOS-108 | Story | Done | Overview summary and approval queue endpoints |
| REOS-109 | Story | Done | Environments and service catalog |
| REOS-110 | Story | Done | Deployments: rollout state, events and the push ingest API |
| REOS-111 | Story | Done | Telemetry: metric samples, thresholds, SLOs and canary analysis |
| REOS-112 | Story | Done | Deployment control webhooks, automatic rollback, and run pause |
| REOS-113 | Story | Done | Approvals: required approvers, Request changes, policies and exceptions |
| REOS-114 | Story | Done | Hash-chained audit with verification |
| REOS-115 | Epic | To Do | Release types, versions and client schedules |
| REOS-116 | Story | Done | RT1: Release types and per-type templates |
| REOS-117 | Story | In Review | RT2: Versions: link a product version's releases (Beta 1-3, CERT, PenTest Cert, Production) |
| REOS-118 | Story | Done | RT3: Clients and segments (Standard, On-Demand with a CERT environment) |
| REOS-119 | Story | In Review | RT4: Client schedules: Standard and On-Demand dates on CERT and Production releases |
| REOS-120 | Story | In Review | RT5: Calendar, portfolio and overview show types, versions and client dates |
| REOS-121 | Story | In Review | RT6: VP and Director analytics across the person's workspaces |
| REOS-122 | Story | In Review | RT7: Template governance: tasks and milestones in templates, versions, approver is not the author |
| REOS-123 | Story | Done | G1: State patterns in the new component set |
| REOS-124 | Story | Done | G2: Right-hand panel and drawers in the new shell |
| REOS-125 | Story | Done | G3: Releases list and the Stream |
| REOS-126 | Story | Done | G4: Release detail: planning sections |
| REOS-127 | Story | Done | G5: Release detail: governance and closeout sections |
| REOS-128 | Story | Done | G6: Run view in the redesign (Rehearsal and Live inside Orchestration) |
| REOS-129 | Story | Done | G7: Management screens (Teams & settings, Templates, Comm library, Connectors) |
| REOS-130 | Story | Done | G8: Sync health and the webhook allowlist |
| REOS-131 | Story | Done | G9: My work and Inbox |
| REOS-132 | Story | Done | G10: Analytics screen in the redesign |
| REOS-133 | Story | Done | G11: Imports & exports screen |
| REOS-134 | Story | Done | G12: Calendar beyond the week frame (Month, Timeline, list, Subscribe) |
| REOS-135 | Story | Done | G13: Sign-in and start-up pages |
| REOS-136 | Story | Done | G14: UTC with local time on every remaining screen (D44) |
| REOS-137 | Story | Done | G15: Documents carry the release model and the workspace |
| REOS-138 | Story | Done | G16: Calendar feeds name their workspace and key |
| REOS-139 | Story | Done | G17: Message texts: notifications, team webhooks, comm tokens |
| REOS-140 | Story | Done | G18: Deployments list |
| REOS-141 | Story | Done | G19: Analytics workbook (XLSX) |
| REOS-142 | Story | To Do | G20: Extend REOS-102 QA to every restyled screen, both themes and the files |
| REOS-143 | Bug | Done | A Communicate drawer restored from the saved session hides the gate Inspector on a deep link |
| REOS-144 | Bug | Done | SyncPollerTests timer test times out once in a full run under load |
| REOS-145 | Bug | Done | IngestApiTests load test fails on the Windows CI runner: "ingest p95 over a second" |
| REOS-146 | Bug | Done | Playwright mywork-inbox "Unread only / Mark read" fails in a full run: the first Mark read button may not belong to the counted rows |
| REOS-147 | Bug | Done | macOS CI: axe finds "Grant & record" button text at 1.11:1 contrast on the approvals page after an empty-note click |
| REOS-148 | Bug | Done | Windows CI: two Api tests fail only there (sync poller races a seeded test; PdfPig drops a space in the zone date) |
| REOS-149 | Story | To Do | XB1: Run the Playwright suite on Edge, Firefox and WebKit in CI |
| REOS-150 | Story | To Do | XB2: Firefox: fix what the cross-browser run finds |
| REOS-151 | Story | To Do | XB3: Edge: fix what the cross-browser run finds |
| REOS-152 | Story | To Do | XB4: Safari: fix WebKit findings and run a real-Safari checklist |
| REOS-153 | Story | To Do | XB5: Make the cross-browser run part of the gate and close the REOS-102 browser line |
| REOS-154 | Bug | In Review | macOS CI: audit.spec.ts keyboard test fails intermittently (Close event details not focused after Enter) |
