# CLAUDE.md — Release Management App

You are building an internal release-management tool for Release Train Engineers, Release Managers and Governance Officers. The scoping is finished. **Your job is to build, not to decide.** Every architectural choice is recorded in `docs/DECISIONS.md`. If something is not covered, write the question into `docs/QUESTIONS.md` and continue with work it does not block; if nothing is unblocked, stop and ask. Never pick a default silently.

## Read before any work
1. `docs/PROJECT_SCOPE.md`: what the app does and every mechanism behind it
2. `docs/DECISIONS.md`: D1–D30 (confirmed), rejected alternatives, open items with provisional defaults
3. `docs/MILESTONES.md`: the milestone you are on, its prompt and its "Done when" test
4. `docs/UI.md`: before touching any front-end file

## Stack (locked)
- **.NET 10 LTS** (`net10.0`), ASP.NET Core minimal APIs, EF Core 10 with `Microsoft.EntityFrameworkCore.Sqlite`
- SQLite in WAL mode, set on **every** connection: `PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;`
- SignalR for live updates
- React + TypeScript + Vite, built into `wwwroot` and served by the .NET host (one deployable)
- Charts: Apache ECharts 6 via `echarts/core` selective imports, SVG renderer
- PDF: QuestPDF · CSV: Sep · XLSX: ClosedXML · ICS: Ical.Net 5
- Tests: xUnit for unit, integration and contract tests (no Testcontainers: SQLite runs in-process); Playwright for end-to-end

## Solution layout
```
src/
  ReleaseMgmt.Domain/          entities, enums, domain services (no EF, no ASP.NET)
  ReleaseMgmt.Infrastructure/  EF DbContext, migrations, SQLite triggers migration, connectors, exporters
  ReleaseMgmt.Api/             minimal API endpoints, SignalR hub, auth, background services
  ReleaseMgmt.Web/             React + Vite app (outputs to Api/wwwroot)
tests/
  ReleaseMgmt.Domain.Tests/
  ReleaseMgmt.Infrastructure.Tests/   includes the trigger suite ported from tests/reference/
  ReleaseMgmt.Api.Tests/              WebApplicationFactory contract tests
  e2e/                                Playwright
db/          schema.sql and analytics.sql: the reference, not the runtime source
tests/reference/   Python oracle tests; must keep passing against db/schema.sql
```

## Non-negotiable rules
1. **`db/schema.sql` is the contract** (47 tables, 42 triggers, 20 indexes). EF migrations must produce the same tables, CHECKs, indexes and triggers. Triggers go in one hand-written migration (`migrationBuilder.Sql`), verbatim and **in file order** (SQLite fires same-event triggers newest-first, and the error each rule reports depends on that order). If you need to change the schema, change `db/schema.sql` first, rerun `python3 tests/reference/test_schema.py`, then the migration.
   - **Every entity** is configured with `ToTable(t => t.UseSqlReturningClause(false))`. EF Core's default save path (SQLite `RETURNING`) is not supported on tables with AFTER triggers.
   - A trigger `RAISE(ABORT, …)` surfaces as a `SqliteException`. Map it to 422 `{guard:"DbRule", message}`, never a 500. The service normally catches the rule first; the mapping covers races and clock skew.
   - SQLite migrations that rebuild a table silently drop its triggers. A CI test asserts the migrated database has exactly the trigger names in `db/schema.sql`; if a migration rebuilds a table, it must re-apply that table's triggers.
2. **Status changes only through domain services** (`TrainLifecycleService`, `GateService`, `RunService`). No endpoint PATCHes a `Status` column. The service checks the rule first to return a readable 422; the trigger is the backstop.
3. **Every service write** stamps `Version` (optimistic concurrency, `If-Match` → 409 with the current row) and writes one `AuditEvents` row **in the same transaction**. Effects cascaded by triggers (decertify, evidence lock, baseline capture, PIR creation) write their own audit rows. The service must set `LastChangedByUserId` and `LastChangedAt` on rows that have them so those audit rows carry the right actor and time.
4. **Time comes from an injected `TimeProvider`**, never `DateTime.UtcNow` directly. Services write `LastChangedAt` (and every timestamp) from it, which is how the test clock reaches triggers. SQLite's own `'now'` is used only where noted in `schema.sql`.
5. **Timestamps:** UTC ISO-8601 TEXT via a value converter. `DateTimeOffset` and `decimal` are banned in entities (EF's SQLite provider evaluates them client-side). Metrics are `double`/`long`.
6. **Ids:** UUIDv7 as TEXT (`Guid.CreateVersion7()`). Rows created by triggers use random hex ids: a documented exception.
7. **Actuals never overwrite the plan.** Runbook actuals live in `StepExecutions`; baselines in `Baselines` are immutable.
8. **Fail fast, never silent.** Any background failure becomes a `SyncAlerts` row and a SignalR push. No `catch {}` without logging and surfacing.
9. **UI rule:** no checkboxes, toggle switches, pills, badges or bordered boxes. Status is words, glyphs (✓ ✗ ● ◐ ○ ▲) and coloured text; actions are text buttons; selection is a full-width row tint. Use tokens from `docs/ui/tokens.css`; no hard-coded colours. No modals: inline expansion or the right drawer.
10. **Fonts come from the OS** (SF on macOS, Segoe UI Variable on Windows). Never bundle SF Pro.
11. Credentials only via ASP.NET Data Protection / OS secret store, never in SQLite or appsettings committed to git.

## Commands
```bash
dotnet build
dotnet test
python3 tests/reference/test_schema.py          # 82 cases, must stay green
python3 tests/reference/seed_and_query.py       # seeds history through the triggers, runs all 15 metrics
python3 tests/reference/seed_and_query.py --write   # regenerates fixtures/seed.db + expected_metrics.json (only if schema/analytics change)
python3 tools/gen_entities.py                   # regenerate Domain entities + EF model map after any db/schema.sql change
# then re-scaffold + patch the two migrations (they execute schema.sql verbatim, see docs/QUESTIONS.md Q-001):
#   cd src/ReleaseMgmt.Infrastructure && dotnet ef migrations add Schema -o Migrations && dotnet ef migrations add Triggers -o Migrations && python3 ../../tools/patch_migrations.py
cd src/ReleaseMgmt.Web && npm run dev            # Vite dev server, proxies /api to :5080
npx playwright test                              # from tests/e2e
```

## Working method
- One milestone at a time from `docs/MILESTONES.md`. A milestone is done only when its **Done when** test passes in CI.
- After M2 merges, M3, M4 and M5 may run as parallel subagent streams: all tables exist from M1, and the streams build disjoint services, endpoints and screens. Cross-stream calls go through interfaces defined in M2 (`INotifier`, `IReadinessService`). M6 and M7 wait for all three.
- Open items in `docs/DECISIONS.md` list a **provisional default**. Build to the default behind the named config key, and do not treat it as settled.
- Mockups in `docs/ui/mockups/` are the visual reference. Match layout, density and copy; the data in them is illustrative.
