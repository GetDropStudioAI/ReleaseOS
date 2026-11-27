# EF Core on Postgres spike: findings (REOS-196, decision D59)

**Status:** spike complete, for owner review. **No change** to `db/schema.sql`, to any shipped project, or to the SQLite runtime path. SQLite stays the shipped database; the spike is `tests/ReleaseMgmt.PostgresSpike.Tests` (one new test project, added to `ReleaseMgmt.sln`) plus a non-gating workflow, `.github/workflows/ef-postgres-spike.yml`.
**Written:** 2026-11-27. **Plan this updates:** `docs/proposals/postgres-port-plan.md` (REOS-194); the schema generator and oracle are REOS-195's (`tools/gen_pg_schema.py`, `tests/reference/test_schema.py --dialect postgres`).

## 0. Recommendation

**GO for REOS-197, on four conditions.** Everything the plan marked as an EF-provider risk (converters, ids, `RETURNING`, the model, the shipped services) turned out small. What the spike found instead is a different class of problem that SQLite hides: **three correctness hazards that only exist once writers are concurrent**, each reproduced deterministically and each with a fix proven in the spike.

| # | Condition | Why |
|---|---|---|
| 1 | The audit sealer is redesigned in REOS-197 (not left in an unplanned "B3") using the `LOCK TABLE … SHARE ROW EXCLUSIVE` sealer proven in section 3.3 | Without it, an audit row that commits after a higher id is **never sealed** and the chain verifier reports it as tampering. Highest-risk item of the port. |
| 2 | Cross-row rules take the **parent row lock from the child side** (section 3.2), not only inside the checking trigger | The plan's PG-5 default ("row locks in the triggers") is not sufficient: a gate was certified with an open task while both sessions committed. |
| 3 | `Version` becomes an EF concurrency token (or the read takes `FOR UPDATE`) on Postgres (section 3.1) | Every service's `If-Match` check is read-then-write. Through the real services, **76 of 80 calls with the same stale `If-Match` returned 200** on Postgres; SQLite returned 5 of 80 (one per round, correct). |
| 4 | A Postgres sibling of `DbRules` behind a seam, before any API test runs on Postgres (section 2.5) | Today a trigger `RAISE` escapes `ServiceBase.RunAsync` as an exception: a 500 where the contract says 422 `{guard:"DbRule"}`. |

If the owner rejects condition 1 or 2, the answer becomes **NO-GO for Postgres as a system of record for evidence**, because the product's headline property (a gate cannot be certified against the rules; the audit trail is tamper-evident) would be weaker on Postgres than on SQLite.

## 1. What was built, and how to run it

| Piece | What it is |
|---|---|
| `tests/ReleaseMgmt.PostgresSpike.Tests` | xUnit, `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.4 next to EF Core 10.0.12. 37 tests, each `[PgFact]`: it skips itself when `REOS_PG_DSN` is unset (the variable the REOS-195 workflow already sets), so ordinary `dotnet build` and `dotnet test` on Windows and macOS are unaffected. |
| Schema | Generated from `db/schema.sql` at test time by `tools/gen_pg_schema.py` (never committed), loaded once into a template database; every test gets `CREATE DATABASE … TEMPLATE` (milliseconds). |
| Context | `PgReleaseDbContext : ReleaseDbContext`, the shipped model and query filters unchanged, plus **one** convention (booleans as 0/1). |
| Slice | The real `TrainLifecycleService`, `GateService`, `WaiverService` and `TaskService`, with the scenarios of `Infrastructure.Tests/ServiceTests.cs` (same seed, same assertions). |

```bash
# any Postgres 16 will do; the test creates and drops its own databases
export REOS_PG_DSN="postgresql://postgres:postgres@localhost:5432/postgres"
dotnet test tests/ReleaseMgmt.PostgresSpike.Tests --logger "console;verbosity=detailed"
REOS_SPIKE_REPORT=measurements.txt dotnet test tests/ReleaseMgmt.PostgresSpike.Tests --filter Measure
```

**Evidence marks:** **[V]** = ran in this session and the test is in the project (the test class is named). **[R]** = reasoned, not run. **[J]** = judgement.

**Where it ran.** PostgreSQL 16.15 (Ubuntu package, local cluster, `fsync=on`), .NET SDK 10.0.404, Linux. **It did not run in GitHub Actions**: the plan records Actions as blocked by billing, so `ef-postgres-spike.yml` has been validated for syntax only (`python3 tests/tools/test_workflows.py`), not executed. Its shape copies `schema-oracle-postgres.yml` (Linux, `postgres:16` service container, push to `main` and nightly only, `continue-on-error`).

## 2. Results by question

### 2.1 Provider wiring and the model [V: `SliceServiceTests`, `ProviderTests`]

`UseNpgsql` builds the shipped model: all 85 entity types, every query filter, no model change. The shipped `ReleaseDbContext` was subclassed only to add a convention. **Swapping `UseSqlite` for `UseNpgsql` and changing nothing else fails on every table with a `bool`** (15 properties): reads throw `InvalidCastException` ("Reading as 'System.Boolean' is not supported for fields having DataTypeName 'bigint'") and writes fail with 42804 (`column "IsCompleted" is of type bigint but expression is of type boolean`). The generated schema keeps flags as 0/1 integers (plan PG-8), so the fix is `Properties<bool>().HaveConversion<BoolToZeroOneConverter<long>>()`. Nothing else was needed: `int` properties read from and write to `bigint` columns without a converter (Npgsql accepts int4 parameters and reads int8 into `int`).

### 2.2 Timestamps and ids [V: `ConverterAndIdTests`]

- `UtcTextConverter` and `DateTextConverter` work on Postgres **unchanged**: a `Local`-kind value with milliseconds is stored as `2026-10-20T16:30:05Z`, the column is `text`, `DateOnly` is `2026-12-31`, and a range filter on `OccurredAt` translates to a plain text comparison in SQL (not client-side). Rule 5 (no `DateTimeOffset`, no `decimal`) stays as written; Npgsql would accept them, but the audit canonical text and the trigger comparisons need the fixed text form.
- UUIDv7 text ids: 3,000 ids shuffled and sorted in SQL come back in creation order under the database default, `COLLATE "C"` **and** ICU (`und-x-icu`). The plan's collation worry (4.3, 4.10) does not apply to ids: they are fixed-width lower-case hex and hyphens sit at the same positions. It still applies to free-text sort keys; the spike's cluster is `C.UTF-8`, so the glibc `en_US` case is **[R]**, untested.
- Trigger-made ids (32 hex chars from `gen_random_uuid()`) are the generator's translation of `randomblob`; the PIR and baseline cascades that use them pass in the slice.

### 2.3 EF migration generation [V: `ProviderTests`, `MigrationTests`]

EF's own DDL for the model (`Database.GenerateCreateScript()` under Npgsql), against the contract schema from the generator:

| | EF-generated | Contract (generated from `schema.sql`) |
|---|---:|---:|
| Tables | 85 | 85 (identical names) |
| Foreign keys | 222 | 222 |
| **CHECK constraints** | **0** | **296** |
| **Triggers** | **0** | **188** |
| Indexes besides keys | 210 | 55 (EF adds one per foreign key by convention) |
| Column type differences | 114 `integer` where the contract has `bigint` (the 106 `int` properties and 8 more); 11 `citext` columns come out as `text` | |

**Conclusion: do not generate the Postgres schema with `dotnet ef migrations add`.** The contract cannot be expressed in the model (CLAUDE.md rule 1 already says so for SQLite: the migrations execute the contract verbatim, `docs/QUESTIONS.md` Q-001). The same pattern works on Postgres: one hand-written EF migration whose `Up` is `migrationBuilder.Sql(<generated contract>)`.

- `MigrationTests` applies exactly that through `Database.MigrateAsync()` on an empty database: 85 tables, **188 triggers, all carrying the `t{n}_` ordering prefix**, one `__EFMigrationsHistory` row.
- **Npgsql runs the whole generated script as one command**, dollar-quoted `$f$`/`$fn$` function bodies included. No statement splitter is needed (the spike first wrote one; it was unnecessary and was removed).
- `Migrate()` on a context with **no model snapshot** applies without `PendingModelChangesWarning` failing it. A snapshot is still wanted if anyone scaffolds migrations; keep a separate migrations folder and history per provider (the SQLite snapshot must not be shared).
- The shipped `SchemaContractTests` (migrated database has exactly `db/schema.sql`'s trigger names) needs a Postgres twin that strips the prefix (plan section 5, point 3); the spike checks the count and the prefix only.

### 2.4 `RETURNING` versus AFTER triggers (rule 1) [V: `ReturningAndRuleTests`]

- **Postgres has no `RETURNING` restriction on tables with AFTER triggers.** A `StageGates` update that fires the three AFTER cascades (evidence lock, baseline capture, transition log) saves correctly with EF's default `RETURNING` behaviour switched back on for every table.
- The shipped setting `ToTable(t => t.UseSqlReturningClause(false))` is **accepted and harmless** on Npgsql, but it does not remove `RETURNING` where a value is generated: an insert into `GateTransitions` (identity key) still reads `Id` back with `RETURNING "Id"`. An update with no store-generated column has none either way.
- So rule 1 is SQLite-specific. Keep the call for the SQLite path; on Postgres it can stay (no behaviour change) or be made conditional. This resolves plan 4.6, which was marked [U].

### 2.5 Trigger `RAISE` becomes a guard failure, not a 500 [V: `ReturningAndRuleTests`]

- A rule trigger surfaces as `PostgresException` with SQLSTATE **`RE001`** (the generator's one custom state) and `MessageText` equal to the rule text, e.g. "Illegal gate status transition".
- **The shipped `DbRules.TryGetMessage` does not recognise it** (it looks for `SqliteException` code 19), so `ServiceBase.RunAsync` does not catch it: the `DbUpdateException` escapes. On the API that is a 500 where CLAUDE.md rule 1 requires 422 `{guard:"DbRule", message}`.
- A Postgres-aware mapper (in the spike: `PgDbRules`) returns the identical message for triggers and maps class 23 to SQLite's wording (`UNIQUE constraint failed: Table.Constraint`, and for FK, NOT NULL and CHECK), so the one place that special-cases an index by name (`UX_Trains_VersionSlot`) keeps working: Postgres reports the constraint name too. `DbRules` needs a provider seam, and `ServiceBase` should call the seam, not the static class.

### 2.6 The slice on Postgres [V: `SliceServiceTests`]

**12 of 12 scenarios pass on Postgres through the real services, with no change to any service**: illegal train and gate transitions name their guard; the gate lockout lists the uncertified gate; Certify reports every reason at once; the Compliance separation-of-duties rule refuses a certifier who completed a task and lets a second Governance Officer through; one service audit row per write and one trigger-written row per cascade (evidence lock, baseline capture), with the right actor **and the service clock's time**; reopening a task and adding a task decertify the gate with the actor who did it; `Version` bumps and `LastChanged` stamps; waiver reason, approver role and self-approval; and the whole Planning → Gated → Executing → Complete walk including the auto-required PIR. The same scenarios still run against SQLite in `ServiceTests.cs` (505 Infrastructure tests green), so the slice is green on both databases.

**Not done: API (WebApplicationFactory) tests on Postgres.** They need the provider chosen at start-up (`Program.cs`, `DbRegistration`), which is the shipped path this story was told not to change. They are the first thing batch 1 should do (condition 4).

## 3. What breaks under concurrency (the real finding)

SQLite has one writer at a time, so a trigger's `EXISTS` check and the write that follows are atomic, ids are issued in commit order, and read-then-write in a transaction cannot lose an update. Postgres defaults to `READ COMMITTED` and has none of those properties. All three hazards below are **deterministic tests** (two open transactions), each followed by the fix proven the same way.

### 3.1 Lost update behind `If-Match` [V: `ConcurrencyTests`]

Every service does: read the row, compare the caller's `If-Match` with the version it read, change, `Version++`, save. Two callers both send `If-Match: 1`; B reads before A commits. **B's check passes on its stale read, B's `UPDATE … WHERE Id=@id` succeeds, and A's change is gone** (final `Version=2` after two writes, title "B"). Through the real services, with 16 parallel callers sending the same stale `If-Match` over 5 rounds:

| Engine | 200 | 409 | exceptions | Ideal |
|---|---:|---:|---:|---|
| SQLite | 5 | 75 | 0 | 5 / 75 / 0 |
| **Postgres** | **76** | **4** | 0 | 5 / 75 / 0 |

Fixes, all proven: (1) `Version` as an EF concurrency token (`UPDATE … WHERE Version=@original` → `DbUpdateConcurrencyException`, which `RunAsync` would map to the 409 path); (2) `SELECT … FOR UPDATE` on the read (the second writer blocks, then sees the new `Version`); (3) `REPEATABLE READ`/`SERIALIZABLE` (second writer gets **40001**). Recommendation **(1)**: it is declarative, costs no lock wait, and needs one place (a model convention over every table with a `Version`). Fix (3) additionally needs a retry loop, and **a retrying execution strategy cannot be combined with `RunAsync`'s own `BeginTransactionAsync`** (EF refuses at the first command: "does not support user-initiated transactions"), so retries mean moving `RunAsync` under `strategy.ExecuteAsync`. Npgsql also wraps a 40001 as `InvalidOperationException > DbUpdateException > PostgresException`, so any mapper must walk the chain.

### 3.2 Cross-row rules race (plan 4.8) [V: `ConcurrencyTests`]

Session A inserts an open checklist task on gate g1 (uncommitted). Session B certifies g1: `trg_Gate_CertifyRequiresTasks` looks for open tasks, cannot see A's, and allows it. Both commit: **g1 is `Certified` with one open task**, the exact state the rule exists to prevent.

The fix is **not** a lock inside the checking trigger: a `FOR UPDATE` on `ChecklistTasks` cannot lock a row that is not yet visible. It is a lock taken by the **writer of the child row** on the parent row (`PERFORM 1 FROM "StageGates" WHERE "Id"=NEW."StageGateId" FOR UPDATE` in a BEFORE trigger on `ChecklistTasks`). With it in place the certifier **waits for A**, then its trigger sees the open task and refuses with the rule's own message ("Gate has open checklist tasks", `RE001`). The spike proved this with one trigger; batch 1 must find every child table that a parent-side rule reads (`trg_Train_GateLockout`, `trg_Gate_CertifyInOrder`, `trg_Gate_CertifyRequiresTasks`, `trg_Train_ExecutingRequiresGo`, `trg_Approval_ApprovedNeedsAll` and the 25 subquery-`WHEN` rules) and add the lock, plus a two-session test for each. This is more work than PG-5's wording implies and it is the item most likely to be forgotten, so it needs a generated checklist, not memory.

### 3.3 The audit chain: skipped rows and isolation [V: `AuditChainTests`]

The shipped sealer (`AuditChainService`) is raw `Microsoft.Data.Sqlite` and rests on `BEGIN IMMEDIATE` and on Id order equalling commit order. Its critical section was ported to Npgsql unchanged (read head; next unsealed rows with `Id > LastId` in Id order; hash; update; advance head) and the chain verified the way `tools/verify_audit_chain.py` does.

- **Hazard [V]:** an `IDENTITY` value is taken at `INSERT`, not at `COMMIT`. Transaction A inserts row n (uncommitted); B inserts row n+1 and commits; the sealer seals n+1 and moves the head past n; A commits. **Row n is filtered out by `Id > LastId` and stays unsealed forever**, a row "behind the head" which the verifier reports as a failure. (Triggers write audit rows too, inside the same transactions, so this is not avoidable by discipline in the services.)
- **Fix [V]:** the sealer takes `LOCK TABLE "AuditEvents" IN SHARE ROW EXCLUSIVE MODE` first. That conflicts with the `ROW EXCLUSIVE` lock every writer holds until commit, so it **waits for in-flight writers**, after which every id below the highest visible one is committed or aborted; it also conflicts with itself, so two sealers cannot run together (this replaces `BEGIN IMMEDIATE` and the head `FOR UPDATE`). Proven with 4 writers (some holding transactions open so ids commit out of order) and 3 sealers looping: **400 rows written, 400 sealed, 0 skipped, 0 broken links.** Cost: writers block for the duration of one seal batch (default 1000 rows, `Audit:Seal:BatchSize`; the batch's duration was not measured on its own, so size it against the write-latency budget); sealing is off the write path today and stays off it.
- Alternatives not tried **[R]**: seal only ids below `pg_snapshot_xmin(pg_current_snapshot())`; an advisory lock in every audit writer (serialises all audit writes: rejected).
- Not ported: chain verification, the sealer's host-chain handling and `tools/verify_audit_chain.py`'s Postgres reader.

## 4. Measurements [V: `MeasurementTests.Measure`]

Both engines through the **same real services** (`TaskService.AddAsync`: transaction, guard reads, insert, audit row, trigger rules, commit), SQLite migrated by the shipped migrations. **One machine, server on localhost.** These compare the engines under identical conditions; they are not capacity figures for a hosted Postgres (no network round trip, no managed-storage commit latency, no pooler). A second run with `fsync=off` gave the same picture within noise.

| Measure | SQLite (WAL) | Postgres 16 |
|---|---|---|
| Sequential service write, p50 / p95 (300 calls) | 8.9 / 12.2 ms | 12.7 / 17.1 ms |
| 1 writer | 102 writes/s | 93 writes/s |
| 4 concurrent writers: throughput, p95 | 93 /s, 70 ms | **209 /s**, 30 ms |
| 8 concurrent writers | 86 /s, p95 179 ms | **327 /s**, 36 ms |
| 16 concurrent writers | 81 /s, p95 518 ms | **358 /s**, 63 ms |
| Failed writes at 16 writers (busy timeout 5 s) | 0 | 0 |
| Connection open + `SELECT 1`, `Pooling=false` / `true` | n/a | 2.3 ms / 0.06 ms |
| 24 writers, `Maximum Pool Size` 2 / 4 / 100 | n/a | 519 / 774 / 945 writes/s, **0 failures** at every size |

Reading it **[J]**: a single Postgres write costs about 4 ms more than SQLite (several round trips and PL/pgSQL triggers against an in-process library). Throughput scales with writers on Postgres and **collapses on SQLite** (write queue: p95 518 ms at 16). At today's design load (a handful of Release Train Engineers) neither engine is the bottleneck; the case for Postgres is multi-tenant concurrency and operations, not speed.

**Connections and pooling.**
- Keep pooling on (the Npgsql default). Opening without it costs 2.3 ms per call, 40 times the pooled cost.
- A service call uses one connection at a time, so a small pool does not deadlock: it only queues (24 writers on a pool of 2 all succeeded). Size the pool for the number of requests in flight, then to the server's `max_connections` divided by instances. **[R]** Behind PgBouncer in transaction mode, session state (`SET`, prepared statements, advisory session locks) does not survive: batch 1 uses only transaction-scoped features (`LOCK TABLE`, row locks, `pg_advisory_xact_lock`), but REOS-199's tenant context must be set with `set_config(…, true)` per transaction, never per session.
- Set `lock_timeout`, `statement_timeout` and `idle_in_transaction_session_timeout` as role settings (plan 4.5): with the sealer's table lock, a stuck transaction would otherwise stall every audit writer.

**Isolation needed for the audit chain:** `READ COMMITTED` plus the sealer's table lock (3.3). `SERIALIZABLE` is not required and would add 40001 retries to every writer.

## 5. Everything else that breaks or must change

| # | Item | Severity | Where it lands |
|---|---|---|---|
| 1 | Booleans need the 0/1 convention (2.1) | Low, one line | model convention |
| 2 | `DbRules` is SQLite-only and `ServiceBase` calls it statically (2.5) | High (500 instead of 422) | provider seam |
| 3 | `Version` race (3.1) | High | concurrency token |
| 4 | Cross-row rule race (3.2) | High | batch 1 generator + tests |
| 5 | Audit sealer (3.3) | High | REOS-197 |
| 6 | Retry strategy and `RunAsync`'s own transaction (3.1) | Medium | `ServiceBase.RunAsync` |
| 7 | EF-generated DDL is not usable as the schema (2.3): no CHECKs or triggers, 114 `integer` columns, 11 `citext`, 155 extra FK indexes | Info | keep the verbatim-SQL migration; one migrations folder per provider |
| 8 | Rule 1 (`UseSqlReturningClause(false)`) is SQLite-only (2.4) | Info | keep, or make conditional |
| 9 | ~18 files still name SQLite (plan 4.15): the spike touched none; 4 run raw SQL | Medium | app-layer seams story |

## 6. Revised estimates for REOS-197, 198 and 199

Person-days for one engineer who knows the repository; **[J] judgement, calibrated by this spike, not measured**. The plan's own caveat stands: the roadmap's points are not calibrated.

| Batch / story | Plan (7.1) | Revised | What changed |
|---|---|---|---|
| B0 REOS-195 + 196 | 13 – 24 | **done** | The generator and oracle (re-run in this session: `test_schema.py` 706 passed, 0 failed on SQLite **and** on Postgres 16.15, 85 tables and 188 triggers) plus this spike. The plan's B0 assumption "EF can map the entities without model changes" held. |
| **B1 REOS-197** core governance rules | 15 – 25 | **17 – 28** | Translation shrinks (about −4: the generator already produces all 188 triggers and the oracle is green on Postgres at the SQL level). Grows: parent-row locking from the child side for every cross-row rule, with a two-session test each (+5–7); the audit sealer, **moved in from the unplanned B3** (+6–9); `Version` token and 409 mapping (+2). Points: **keep 13 or split** (rules and locking 13; sealer and concurrency 8). |
| B2 REOS-198 remaining rules, parity, migration tool | 8 – 25 (+ B5 10 – 18) | **8 – 22** (+ B5 10 – 18) | The 84 tenancy rules read an immutable column and are generated; no concurrency work expected there **[R]**. B5 (migration tool) is untouched by the spike. Points: keep 13. |
| B3 app layer, **no story today** | 12 – 22 | **8 – 15**, plus the sealer in B1 | Spike shows the provider seam, `DbRules` sibling, pragmas, `BackupRunner` → `pg_dump`/PITR and the `RunAsync` retry restructure are well-bounded. **Create the story** (about 8 points) and put API tests on Postgres first in it. |
| B4 analytics twin, no story today | 6 – 12 | **6 – 12** | Not touched by the spike. Create the story (about 5 points). |
| B6 hardening | 6 – 12 | **8 – 14** | The three hazards need permanent two-session regression tests in CI. |
| **REOS-199** tenant model and RLS | not in 7.1 | **15 – 25** (13 points stands) | **Not evidenced by this spike.** Two new constraints from it: tenant context must be transaction-scoped (pooling, PgBouncer), and every lock-taking trigger from 3.2 must keep working under `FORCE ROW LEVEL SECURITY` (`SECURITY DEFINER` where it reads across tenants). Add a 3-point RLS probe before committing to the estimate. |
| **Total, B0–B6 (excl. REOS-199)** | 70 – 138 | **70 – 133** | The total barely moves; the composition does: **less translation, more concurrency**. Remaining after B0: 57 – 109. |

## 7. What is not proven

1. **GitHub Actions has not run the workflow.** Billing blocks Actions (plan section 5); the spike ran on a local Postgres 16.15 only. The workflow is syntax-checked, not executed.
2. **No API tests on Postgres** (needs the `Program.cs` provider switch, out of scope for a spike that must not touch the shipped path).
3. **Only localhost latency.** No hosted Postgres, network round trip, PgBouncer, synchronous replication or managed storage. The throughput comparison says how the engines scale, not what production gets.
4. **Only 16 concurrent writers** and a short run; no soak, no deadlock hunt beyond the three hazards (40P01 not provoked).
5. **The 117 same-event trigger pairs and the manifest test** are REOS-195's/batch 1's. The slice's cases pin the order only where one statement breaks two rules.
6. **glibc `en_US` collation** untested (the cluster is `C.UTF-8`; ICU was tested); `citext` folding against SQLite's ASCII fold is plan 4.11.
7. **Analytics SQL, the SQLite → Postgres migration tool, backups (`pg_dump`, PITR), RLS and the 12 `analytics.pg.sql` constructs**: not touched.
8. **The audit chain's verification path** and `verify_audit_chain.py` were not ported; the spike checked chains with an in-test verifier that recomputes `AuditChain.Hash`.
9. **Windows and macOS developer experience** of running Postgres for the tests (the plan's PG-6: CI platforms and "no Testcontainers" need an owner decision). The tests expect a server at `REOS_PG_DSN`.

## 8. Decisions this asks of the owner

| Item | Provisional default (nothing is changed in `DECISIONS.md`) |
|---|---|
| **PG-5**, concurrency for cross-row rules | Refine to: `READ COMMITTED`, **parent row lock taken by the child-side trigger**, two-session test per rule. The wording "row locks in the triggers" is not enough (3.2). |
| New: audit sealer isolation | `LOCK TABLE … SHARE ROW EXCLUSIVE` in the sealer; no advisory lock in writers; no `SERIALIZABLE`. |
| New: optimistic concurrency on Postgres | `Version` as an EF concurrency token via one model convention. |
| New: Postgres migrations | One hand-written EF migration executing the generated contract verbatim; separate migrations folder and history per provider. |
| **PG-6** | Needs an answer before batch 1: a Linux Postgres job in CI, and whether the xUnit suite may require a server (`REOS_PG_DSN`) for the Postgres project only. |
| Create stories | App-layer seams (B3, about 8 points) and analytics twin (B4, about 5 points); both are unplanned in the roadmap. |
