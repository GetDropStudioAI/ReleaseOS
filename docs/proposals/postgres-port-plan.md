# Postgres port plan (REOS-194, decision D59)

**Status:** draft for owner approval. **Nothing is migrated, and no production code changes, until this plan is approved.** This file and `tools/schema_inventory.py` are the only deliverables.
**Written:** 2026-11-25. **Inputs read:** `CLAUDE.md`, `db/schema.sql`, `db/analytics.sql`, `tests/reference/*`, `db/upgrades/*`, `docs/DECISIONS.md`, `docs/proposals/saas-market-and-gap-analysis.md` section 7, `docs/proposals/roadmap-stories.md`; plus `src/` greps for SQLite-specific code (section 4.12).

## 0. How to read the evidence marks

| Mark | Meaning |
|---|---|
| **[V]** | Verified in this session by running it: PostgreSQL 16.15 (Ubuntu package, local cluster) and SQLite 3.53.1 (the Python `sqlite3` module). The commands are in Appendix B. |
| **[S]** | Measured from the repository by `tools/schema_inventory.py` or a grep. Counts come from a text scan, so treat them as accurate to a few percent unless the script asserts them (72 tables, 152 triggers, 41 indexes are asserted). |
| **[R]** | Reasoned from Postgres documentation or general knowledge and **not run here**. Needs a check in the spike (REOS-196) or the oracle (REOS-195). |
| **[U]** | Unknown or an assumption. Needs an owner answer or data we do not have. |

Where the repository contradicts itself I say so. Example: `CLAUDE.md` says `test_schema.py` has 449 cases; today it runs **493 passed, 0 failed** [V].

## 1. Inventory (summary; the full generated tables are Appendix A)

`python3 tools/schema_inventory.py` parses `db/schema.sql` and prints every table, index and trigger. `--check` asserts 72 / 152 / 41. `--embed` rewrites Appendix A; `--check-embed` fails if Appendix A is stale. `--json` feeds the oracle.

| Fact [S] | Count |
|---|---|
| Tables / indexes / triggers | 72 / 41 / 152 (asserted) |
| Triggers by timing and event | BEFORE UPDATE 90, BEFORE INSERT 47, BEFORE DELETE 8, AFTER UPDATE 5, AFTER INSERT 2 |
| Tables that carry a trigger | 57 |
| `RAISE(ABORT, …)` statements / distinct messages | 211 / 89 (39 messages are shared by more than one trigger; "A row cannot move to another workspace" appears in 27) |
| Same-event groups with more than one trigger | 19 groups holding 61 triggers (largest: 10 on `StageGates` BEFORE UPDATE) |
| Pairs of triggers inside those groups | 117, of which **75 fire in the opposite order on Postgres** (section 3) |
| CHECK expressions / FK references / `ON DELETE` clauses | about 198 / 180 / 67 |
| Partial indexes / unique indexes | 13 / 9 |
| `COLLATE NOCASE` columns / `AUTOINCREMENT` tables / `REAL` columns | 12 / 5 / 20 |
| Null-safe `IS [NOT] NEW/OLD` comparisons | 105 lines |
| `GLOB` uses (CHECKs and one trigger) | 11 lines |

Trigger classes (heuristic: the script reads the text and applies 10 documented overrides; read it as a first pass, not proof):

| Class | Count | What it is | Examples |
|---|---|---|---|
| pure-check | 120 | CHECK-like validation. Sub-kinds: tenancy (84 `trg_Ws_*`), append-only/immutable (15), role / separation of duties and type/client/template rules (21) | `trg_Ws_ReleaseTrains_Insert`, `trg_Audit_NoUpdate`, `trg_Waiver_ApproverRole`, `trg_Template_ApproverNotAuthor_Update` |
| status-machine | 23 | Guards a status, state or decision path, or reads another row's status | `trg_Gate_Transition`, `trg_Train_GateLockout`, `trg_Train_ExecutingRequiresGo`, `trg_Step_DependenciesDone`, `trg_Deployment_Transition` |
| cascade-writes | 7 | AFTER trigger that writes rows | `trg_Task_ReopenDecertifies`, `trg_Task_InsertDecertifies` (decertify), `trg_Gate_CertifyLocksEvidence` (evidence lock), `trg_Gate_CertifyCapturesBaseline` (baseline capture), `trg_Train_CompleteRequiresPir` (PIR), `trg_Gate_LogTransition` (GateTransitions row), `trg_Train_AssignKey` |
| time-dependent | 1 | Compares with a clock | `trg_Train_ExecutingNoExpiredConditions` (D29: `COALESCE(NEW.LastChangedAt, strftime(…'now'))`) |
| sqlite-specific | 1 | Needs a SQLite function with no Postgres twin | `trg_Step_FreezeLockout` (`GLOB` on `ProductPattern`) |

Secondary SQLite-isms inside other classes [S]: seven triggers use `strftime(…,'now')` (`trg_Train_ExecutingNoExpiredConditions` and six of the seven cascades, all but `trg_Train_AssignKey`); `trg_Gate_CertifyCapturesBaseline` and `trg_Train_CompleteRequiresPir` use `randomblob` for ids and `json_object`, and the baseline also `json_group_array`. Each is handled in section 2. **25 triggers have a subquery in `WHEN`** (15 of the 23 status-machine, 6 pure-check, 2 cascades, the time one and the `GLOB` one); Postgres rejects that form in any trigger, BEFORE or AFTER.

## 2. Translation strategy per class

**Principle.** `CLAUDE.md` rules 1 and 2 and the rejected-alternatives table ("Lockout only in UI or service: any other client bypasses it") make the database the backstop. So the port keeps every rule in the database. The real choice is *how* (PL/pgSQL row triggers, constraint triggers, or services), not *whether*. Services keep their role as the primary check that produces the readable 422.

| Class | Recommendation | Why | Notes and traps |
|---|---|---|---|
| pure-check: append-only / immutable (15) | PL/pgSQL `BEFORE UPDATE/DELETE` row triggers, generated from the rule table | Same message, same behaviour | Postgres adds `TRUNCATE`, which row triggers do not see and SQLite has no equivalent. Add `BEFORE TRUNCATE` statement triggers on every append-only table, and `REVOKE TRUNCATE` from the app role [R]. `trg_Audit_NoUpdate` has a `WHEN NOT (OLD.Hash IS NULL AND …)` that lets the sealer write the hash pair once; port it as a body `IF` using `IS DISTINCT FROM` for each content column. |
| pure-check: tenancy `trg_Ws_*` (84) | Port as PL/pgSQL BEFORE row triggers, generated one-to-one, in batch 2 | They are mechanical (`EXISTS (SELECT … WHERE x.WorkspaceId IS NOT NEW.WorkspaceId)`) and 84 of the 152 rules | Do **not** replace them with composite foreign keys `(WorkspaceId, Id)` during the port: it changes the reported error (an FK violation, not the RAISE text) and the schema contract. Evaluate composite FKs after REOS-199 as a separate change (open question PG-7). Under row-level security these lookups run as the invoking role and see only that tenant's rows unless the function is `SECURITY DEFINER` [R]; REOS-199 must prove this. |
| pure-check: role / SoD / type / client / template rules (21) | Same: PL/pgSQL BEFORE row triggers | Same | Rules that read `WorkspaceMembers.Role` (REOS-105) keep their lookups. |
| status-machine (23) | PL/pgSQL BEFORE row triggers, one per rule, keeping each rule's own `UPDATE OF` list | Preserves the exact "fires when this column is in the SET list" semantics (section 3, point 4 [V]) | **A `WHEN` clause with a subquery is rejected by Postgres** [V] (`cannot use subquery in trigger WHEN condition`). 15 of the 23 have `EXISTS`/`SELECT` in `WHEN` (for example `trg_Train_GateLockout`, `trg_Train_ExecutingRequiresGo`; 25 triggers in all, section 1); move the whole condition into the function body as `IF NOT (…) THEN RETURN NEW; END IF`. Simple `WHEN NEW.Status <> OLD.Status` conditions can stay as `WHEN (OLD.* IS DISTINCT FROM NEW.*)`. |
| cascade-writes (7) | PL/pgSQL **AFTER** row triggers, kept as triggers, **not** moved into services | Rule 3 and D27: cascaded effects write their own audit rows and must happen even for a writer that bypasses the service | A cascade's nested `UPDATE` fires the target table's BEFORE and AFTER triggers again, the same as SQLite [V, experiment E14]. `trg_Task_ReopenDecertifies` updates `StageGates.Status`, which runs `trg_Gate_Transition`, the other gate rules and `trg_Gate_LogTransition`. Keep that nesting; the oracle covers it. Ids made by triggers (`lower(hex(randomblob(16)))`) become `replace(gen_random_uuid()::text,'-','')` [R]. JSON built by triggers is stored as TEXT: `json_build_object(...)::text` gives `{"b" : 1}` and `jsonb_build_object(...)::text` gives `{"a": "x", "b": 1}`, neither equal to SQLite's compact `{"b":1}` [V]. The chain hash covers stored bytes, so this is harmless for integrity but the oracle must compare JSON by value, not text. `json_group_array(...)` has no defined order in SQLite and none in Postgres: add `ORDER BY Id` inside `jsonb_agg` for `Baselines.SnapshotJson` so a baseline is deterministic [R]. |
| time-dependent (1) | PL/pgSQL BEFORE trigger; the clock is a function `app_now()` that returns `COALESCE(NULLIF(current_setting('releaseos.now', true), ''), to_char(now() AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS"Z"'))` | `now()` cannot be frozen in a test; a session setting can. Services still write `LastChangedAt` from `TimeProvider` (rule 4), so the fallback is only for raw SQL | The `to_char` format matched the ISO pattern [V]. Every `strftime('%Y-%m-%dT%H:%M:%SZ','now')` becomes `app_now()`. |
| sqlite-specific (1 trigger, 11 CHECK lines) | Define a **documented pattern subset** for `FreezeWindows.ProductPattern` and implement `glob_match(text, pattern)` once; rewrite `NOT GLOB '*[^0-9a-f]*'` CHECKs as `!~ '[^0-9a-f]'` | Postgres has no `GLOB` [V: syntax error]. `GLOB` is case-sensitive with `* ? [..]`; `LIKE` and `SIMILAR TO` are not the same | **Unknown [U]:** which glob features customers actually put in `ProductPattern`. Until answered, translate `*`, `?` and `[...]` to an anchored regex and test the three forms. |

**Not recommended: moving rules into services with constraint triggers as a backstop.** Constraint triggers are AFTER-only [V: `CREATE CONSTRAINT TRIGGER … BEFORE` is a syntax error], so they would fire after the row is written, after any other BEFORE rule, and would change which error wins. They are useful only for deferred, end-of-transaction invariants, and none of the 152 rules needs that today. This also means the "pure service" option would reintroduce the rejected "lockout only in the service" design.

## 3. The hard problem: which error is reported when several rules are violated

### 3.1 What actually differs

1. **SQLite** fires same-event triggers **newest-first** (reverse of creation order). `db/upgrades/*` re-create every trigger in file order on every upgrade, so the order is always the reverse of `db/schema.sql` [S, 012 header; V on a toy table].
2. **Postgres** fires same-event triggers in **name order**, byte-wise: with `trg_B_Upper` and `trg_a_lower` on one table, `trg_B_Upper` fired first [V].
3. **It is not a corner case on the real schema.** Statement: `UPDATE ReleaseTrains SET CurrentStatus='Complete', CloseCode='Successful', ReleaseTypeId=NULL` on a Planning train. That statement breaks both `trg_Train_Transition` (Planning → Complete is illegal) and `trg_Train_TypeRules_Update` (a release cannot lose its type). On the real `db/schema.sql` SQLite reports **"A release cannot lose its release type"** [V]. A straight port with the two triggers named as today reports **"Illegal train status transition"** on Postgres [V, with stub trigger bodies carrying the real names and messages].
4. **`UPDATE OF col` semantics match**: the trigger fires when `col` is named in the SET list even if the value is unchanged, on both engines [V, experiment E4; SQLite behaviour from its documentation, and consistent with `UPDATE T SET A=A` firing `t1` in my toy run]. This matters for the choice below.
5. **Order between a BEFORE trigger and a CHECK constraint matches**: with `CurrentStatus='Complete'` set on a Planning train without a `CloseCode`, both engines report the trigger message, not the CHECK [V]. So CHECK-versus-trigger precedence needs no work; only trigger-versus-trigger precedence does.
6. **Inside one trigger** the `SELECT RAISE(ABORT, …) WHERE …` statements run in text order (for example the eight checks of `trg_Ws_ReleaseTrains_Insert`). A PL/pgSQL function with `IF … THEN RAISE EXCEPTION` blocks in the same order reproduces that exactly [S, R].

How much is at stake [S, from `schema_inventory.py`]: 19 same-event groups, 117 pairs, **75 inverted on Postgres.** The worst groups are `ReleaseTrains` BEFORE UPDATE (9 triggers, 24 of 36 pairs inverted) and `StageGates` BEFORE UPDATE (10 triggers, 24 of 45). Groups where the order happens to be the same by accident: `StageGates` BEFORE INSERT, `TrainTemplates` INSERT and UPDATE (0 inverted). The AFTER group on `StageGates` also inverts (2 of 3): that decides the order in which the `GateTransitions`, `Baselines` and `AuditEvents` rows are written, so it changes audit `Id` order and therefore the hash chain order, not just an error message.

### 3.2 Options

| Option | How | For | Against |
|---|---|---|---|
| **A. Numbered names (recommended)** | Keep one trigger per rule; name each `t{1000 - file_order:03d}_{original}`, for example `t847_trg_Train_TypeRules_Update`. Newest rule gets the smallest number, so alphabetical order equals SQLite newest-first. The generator computes the number from the line order in `db/schema.sql`. | Keeps each rule's own `UPDATE OF` list and `WHEN` (point 4). Keeps the 1:1 rule/trigger inventory and the existing CI check ("exactly the trigger names in `db/schema.sql`": compare after stripping `^t\d{3}_`). Adding a rule at the end of the file needs no renames. The longest name stays 43 characters, under Postgres's 63-byte limit [S, 38 + 5]. | Relies on a documented but implicit Postgres behaviour; a hand-added trigger with an ordinary name silently joins the order. Mitigation: the CI manifest test below. Inserting a rule mid-file renumbers later ones (the upgrade scripts already drop and re-create all triggers, so that costs nothing new). |
| B. One dispatcher per (table, timing, event) | A single `BEFORE UPDATE` trigger calls per-rule check functions in SQLite order. | Order is explicit code and obvious in review; no reliance on names. | **Changes semantics:** a dispatcher cannot know the SET list, so each rule's "fires when this column is named" becomes "fires on any update" (or "when the value changed"). A rule that checks NEW-state invariants (`trg_Train_TypeRules_Update`, which has no `WHEN`) would start rejecting an unrelated update to a row that is already in a violating state, which SQLite allows. Merges 152 inventory rows into about 57, so the parity table and the CI name check change. Possible with `BEFORE UPDATE OF <union of all columns>`, but still cannot honour the per-rule lists. |
| C. Ordered checks inside one function, triggered `UPDATE OF <union>` | Same as B with a column union. | Fewer triggers | Same semantic gap as B, plus one large function per table that is harder to diff against `schema.sql`. |

**Recommendation: A for every BEFORE and AFTER group, applied uniformly** (single-trigger groups get the prefix too, so adding a second trigger later cannot change behaviour silently). **Fallback if the DBA or owner rejects reliance on name order: B for the 19 groups only, with a written list of rules whose `UPDATE OF` list differs from the union** (the oracle will show which). Recorded as open question PG-4.

### 3.3 Proof method: SQLite is the specification

Until REOS-193 retires SQLite, the expected value for every case is **what SQLite does**, obtained by running the case, never by hand-writing the message.

1. **Layer 1: the existing oracle on both dialects.** Make `tests/reference/test_schema.py` (493 cases; about 470 `check()` calls, about 307 of them with an `expect=` message [S]) take a dialect adapter (`--dialect sqlite|postgres`, DSN for Postgres). The same case list, the same expectations. Tighten loose expectations: the file has 84 distinct `expect=` substrings, several short (`final`, `baseline`, `CHECK`, `immutable`), and 39 messages are shared by several triggers, so a message alone does not identify the rule that fired. On Postgres, raise with `USING DETAIL = TG_NAME` and map the rule name; on SQLite the case recipe must make exactly one rule applicable (that is already the design of most cases) [R].
2. **Layer 2: one case per rule, plus one per ordered pair.** A rule table in the oracle, `rule → (setup, minimal violating statement)`, generated from `--json` and reviewed by hand. For every same-group pair (117) the harness combines the two violation recipes into one statement when they are compatible, runs it on SQLite, records the message, and requires Postgres to report the same message. Pairs whose recipes cannot be combined (they set one column to two different values) go in a reviewed list with the reason ("unreachable together"). The 75 inverted pairs are the must-pass set; the other 42 guard against regressions. Output is the **parity table**: one row per trigger and per index (and per CHECK where the oracle covers it), with: SQLite case ids, Postgres case ids, pairs covered, status (ported / proved / waived with reason), owner. CI fails if a trigger has no row or a row has no case on both dialects.
3. **Layer 3: ordering manifest test.** On Postgres, read `pg_trigger` for each group, sort by `tgname`, strip the prefix, and require the sequence to equal SQLite's newest-first sequence from the generator. Also run a trace trigger harness in a test database to observe the actual fire order for a representative `UPDATE` of each group [R].
4. **Layer 4: differential fuzz** (nightly, not per push). Start both engines from the same seed state (`tests/reference/fixtures/seed.db`, loaded into Postgres by the migration tool), apply the same random sequence of INSERT/UPDATE/DELETE statements drawn from per-table mutation templates, and compare for each: accepted or rejected, message, and a digest of the affected rows. A divergence is a bug in the port or a documented, waived difference. Target volume, 50k statements per run [U: pick after seeing run time].
5. **Layer 5: concurrency cases (new, no SQLite equivalent).** See section 4.8.

**Definition of done for the hard problem:** every one of the 152 triggers has a parity row with a case on both dialects; all 117 pairs are either proved or on the reviewed unreachable list; the manifest test passes; the fuzz run has no unexplained divergence over N consecutive nightly runs [U: N].

## 4. Other dialect issues

| # | Area | SQLite today | Postgres | Action | Evidence |
|---|---|---|---|---|---|
| 4.1 | Identifier case | Case-insensitive; schema uses PascalCase unquoted | Unquoted names fold to lower case: `SELECT Id FROM StageGates` fails (`relation "stagegates" does not exist`) | Keep PascalCase and **quote everywhere** (EF Npgsql already quotes). Generate `schema.pg.sql` from the parser rather than hand-write 2,000 lines. Raw SQL (`analytics.sql`, oracle fixtures, upgrade scripts) must quote too. Alternative `snake_case` renames every column and breaks "`db/schema.sql` is the contract": not recommended (PG-1). | [V] |
| 4.2 | Partial and expression indexes | 13 partial, one with an expression (`UX_Trains_VersionSlot`: `COALESCE(BetaNumber,0)` `WHERE … CurrentStatus <> 'Aborted'`) | Supported; the predicate must be immutable | Direct port. UNIQUE allows several NULLs in both engines by default [R]. | `UX_Trains_VersionSlot` shape created and duplicate rejected [V] |
| 4.3 | Text timestamps and converters | UTC ISO-8601 TEXT via `UtcTextConverter` and `DateTextConverter` (D5); lexicographic comparison is chronological | TEXT works unchanged | **Keep TEXT in the first port** (PG-2): zero semantic change, the audit hash chain stays valid byte for byte, `seed.db` and exports stay comparable. Add `COLLATE "C"` on timestamp and date columns, or create the database with `C` collation: with another locale, punctuation-insensitive sorting can reorder strings [R, not tested; my cluster uses `C`]. `timestamptz` is a separate later migration. Replace `strftime(...,'now')` with `app_now()` (section 2). | [V] for the format, [R] for collation |
| 4.4 | Ids | UUIDv7 as TEXT (`Guid.CreateVersion7()`); trigger rows use 32 hex characters (rule 6) | TEXT works; `uuid` is 16 bytes against 36 and gives smaller indexes | **Keep TEXT** (PG-3): ids appear in URLs, exports, hashes and audit canonical text. Native `uuid` later, with a documented change of canonical text. UUIDv7 keeps index locality. `AuditEvents.Id` and four other tables use `INTEGER PRIMARY KEY AUTOINCREMENT`, so they become `GENERATED BY DEFAULT AS IDENTITY` (BY DEFAULT so the migration can load existing ids, then `setval`). | [R] |
| 4.5 | Pragmas | `journal_mode=WAL` (persists), `foreign_keys=ON` and `busy_timeout=5000` per connection (`SqliteConnectionInterceptor`) | WAL has no meaning; foreign keys are always enforced; `busy_timeout` becomes `lock_timeout` and `statement_timeout`, set per role or connection | Remove the interceptor from the Postgres path. Add `lock_timeout`, `statement_timeout` and `idle_in_transaction_session_timeout` as role settings. `PRAGMA query_only=ON` (`AnalyticsConnectionFactory`) becomes a read-only role or `default_transaction_read_only`. `PRAGMA integrity_check` (`BackupRunner`) has no twin: use checksums plus `amcheck` where the host allows [U]. Online backup (`BackupDatabase`, WAL checkpoint) is replaced by `pg_dump` or point-in-time recovery from the host; `docs/DR_DRILL.md` and `tools/dr_drill.py` change. | [S] for the SQLite side |
| 4.6 | RETURNING | `CLAUDE.md` rule 1: every entity uses `ToTable(t => t.UseSqlReturningClause(false))` because SQLite `RETURNING` is unsupported on tables with AFTER triggers | Postgres supports `RETURNING` with triggers | The call is part of EF Relational, not SQLite-only: on Postgres it would make EF read generated values with an extra statement, and for identity keys (`AuditEvents.Id`) it may matter. Make it conditional on the provider and check the behaviour in REOS-196. | [U] |
| 4.7 | CHECK semantics | NULL passes a CHECK; the CHECK message names the expression | Same NULL rule; message is `violates check constraint "<name>"` | Normalise in `DbRules`: SQLite code 19 covers CHECK, FK, UNIQUE, NOT NULL and RAISE, Postgres separates them (23514, 23503, 23505, 23502, and a trigger `RAISE`). Give every trigger `RAISE` one custom SQLSTATE (for example `RE001`) so mapping to 422 `{guard:"DbRule"}` is unambiguous [R]. SQLite also accepts a text value in an INTEGER column (type affinity) where Postgres errors: see the pre-flight type audit (section 6). `IS NOT`/`IS` on non-NULL operands (105 lines) become `IS DISTINCT FROM` / `IS NOT DISTINCT FROM`. `length()` counts characters in both. | [R]; precedence of CHECK versus trigger [V] |
| 4.8 | Concurrency (new) | One writer at a time, so a trigger's `NOT EXISTS`/`COUNT` check and the write are atomic | `READ COMMITTED` by default: a concurrent commit can land between a trigger's check and the write, and deadlocks (40P01) exist | Cross-row rules (`trg_Train_GateLockout`, `trg_Gate_CertifyInOrder`, `trg_Gate_CertifyRequiresTasks`, `trg_Train_ExecutingRequiresGo`, `trg_Approval_ApprovedNeedsAll`) need `SELECT … FOR UPDATE` on the parent row inside the trigger, or `SERIALIZABLE` with a retry policy in the service. Provisional default: row locks (PG-5). Add two-session oracle cases (layer 5) and a service retry for 40001/40P01. This removes a property D2 described as a limit and also gives one we did not have; it is the main hidden semantic change. | [R] |
| 4.9 | Audit chain and sequences | `AuditEvents.Id` is assigned and visible in commit order because writers are serial; the sealer (`AuditChainService`) seals new rows in Id order, and `tools/verify_audit_chain.py` treats a row inserted behind the sealed head as a failure | A sequence hands out ids at insert time, not commit time: row 101 can commit before row 100, so the sealer can seal 101 and then 100 appears behind the head | Redesign the sealer before batch 1: take `pg_advisory_xact_lock(hashtext(chain))` around the audit insert (the trigger-written rows too), or seal only ids below the oldest in-flight transaction (`pg_snapshot_xmin(pg_current_snapshot())`) [R]. Highest-risk item of the app layer. | [R] from the script's docstring |
| 4.10 | Collation and ordering | `BINARY` (byte) text order; "every multi-row query has a deterministic ORDER BY" | Locale-dependent unless the database or column uses `C` | Create the database with `C` collation or add `COLLATE "C"` on sort keys, so exports, PDFs and `expected_metrics.json` order rows identically. Managed hosts may restrict locale at create time [U]. | [R] |
| 4.11 | `COLLATE NOCASE` (12 columns: `Users.Email`, `Handle`, `Workspaces.Slug`, `ReleaseTypes.Name`, …) | ASCII-only case folding in indexes and UNIQUE | Use `citext` (extension available in this install; `'A@x.com'` and `'a@X.com'` collide under a UNIQUE) | `citext` folds with the locale, so non-ASCII letters can differ from SQLite's ASCII-only fold; the pre-flight audit lists rows that collide under one rule but not the other. Nondeterministic ICU collations are the alternative but cannot be used with `LIKE` on Postgres 16 [R]. Check that the chosen host allows `citext` [U]. | [V] for citext; the rest [R] |
| 4.12 | JSON and analytics SQL | `json_each` (21 lines in `analytics.sql`, mostly the `:ws` workspace-id array), `json_extract` (4), `julianday` (19), `date(` (30), `instr` (4), window functions (7 `row_number … OVER`), named parameters (`:from`, `:ws`, …) | `jsonb_array_elements_text($1::jsonb)` or `= ANY($1)`; `(a::date - b::date)` for day differences; `strpos`; windows work unchanged; positional parameters | A twin file `db/analytics.pg.sql`, with `seed_and_query.py` run on both and every one of the 24 metrics compared with `fixtures/expected_metrics.json`. `round(double precision, int)` does not exist on Postgres; cast to `numeric` first [R]. JSON columns stay TEXT (point 2, section 2). | [S], [R] |
| 4.13 | `REAL` | 8-byte IEEE | `real` is **4-byte**: `0.1::real` read back as 0.10000000149 [V] | Map every `REAL` to `double precision` (20 columns: telemetry thresholds, `MetricSamples.Value`, `TrafficPercent`). | [V] |
| 4.14 | Booleans | `INTEGER` 0/1 with `CHECK (x IN (0,1))` | `boolean` exists | Keep `smallint` 0/1 with the same CHECK (PG-8): CSV, JSON and the converters stay the same. | [R] |
| 4.15 | App code that names SQLite | 14 files under `src/` mention `Sqlite` (11 import `Microsoft.Data.Sqlite`): `DbRules`, `AuditChainService`, `BackupRunner`, `SqliteConnectionInterceptor`, `AnalyticsService`, `AnalyticsConnectionFactory`, `PirDueMap`, `CommHydrationService`, `ExportModelLoader`, `SyncHealthService`, `DbRegistration`, `DesignTimeFactory`, `Program.cs`, `WebhookUrlProtectionUpgrade`; four more (`ReleaseModelService`, `AuditQueryService`, `Health.cs`, and `AuditChainService`) run raw SQL through `SqlQuery`/`FromSqlRaw`/`ExecuteSqlRaw` | — | Each gets a provider seam or a Postgres sibling. Counted by grep, so the true edit list may be longer. | [S] |

## 5. Test strategy

1. **Dual-dialect oracle (REOS-195).** `tests/reference` gets a dialect adapter; `test_schema.py`, `seed_and_query.py` and the new rule/pair/fuzz layers (3.3) run against SQLite and Postgres. `test_upgrades.py` stays SQLite-only: it tests the SQLite upgrade path that existing customers still need until they migrate.
2. **Parity table.** Generated, committed, reviewed (3.3 layer 2). It is the release gate for each batch. Columns: rule or index, class, SQLite cases, Postgres cases, ordered pairs covered, status, owner.
3. **Schema contract on Postgres.** Port `SchemaContractTests` (the test that the migrated database has exactly the trigger names in `db/schema.sql`) to read `pg_trigger` and strip the `t\d{3}_` prefix; add the same for indexes, CHECK names, column types and nullability from `information_schema`.
4. **Unit and integration tests (xUnit).** `CLAUDE.md` says "no Testcontainers: SQLite runs in-process". Postgres needs a real server, so this stack rule must be amended when the plan is approved: a Postgres 16 container (Testcontainers or a CI service), one database per test class created from the template database [R]. Ask the owner to confirm the amendment (PG-6).
5. **CI.** A Postgres container job runs the oracle on both dialects, the xUnit suite with `Database:Provider=Postgres`, and the manifest and parity checks. Two constraints to settle:
   - **GitHub Actions is blocked by billing right now.** The plan's CI cannot be exercised, and I did not wait for it. Everything in this plan that says "CI" is design only. Until billing is fixed, run the dual-dialect oracle locally and record the result in the PR.
   - D31 puts CI on `windows-latest` and `macos-latest` only ("Linux not in CI"). GitHub service containers are Linux-only [R], so the Postgres job needs a Linux runner (amend D31) or a Postgres install step on the existing runners.
6. **Concurrency tests** (two sessions) for the rules in 4.8, and a rerun of `tools/run_load_test.py` and `docs/LOAD_TEST.md` against Postgres for a smoke comparison.
7. **Metric parity.** The 24 metrics and the executive cases against `expected_metrics.json`, with a documented tolerance for floating-point rounding.

## 6. Data migration from SQLite for existing customers

The number and size of existing customer databases is **unknown [U]**. D22 says audit growth is "MBs/year", so databases are likely small and downtime should be minutes [R]; confirm per customer.

**Steps** (one tool, `tools/` or a .NET command, resumable per table, with `--dry-run` against a copy):

1. **Pre-flight on a copy** (online backup, never the live file): `integrity_check`, `foreign_key_check`, `python3 tools/verify_audit_chain.py` (the chain must verify before we start), schema equals `db/schema.sql` (`test_upgrades.py` shape check), and a **type audit**: `typeof()` per column against the declared type (SQLite stores text in an INTEGER column without complaint; Postgres will not load it); timestamp and date text matches `^\d{4}-\d{2}-\d{2}(T\d{2}:\d{2}:\d{2}Z)?$`; `json_valid()` on JSON columns; `NOCASE` collisions under `citext`; `ProductPattern` values against the supported glob subset; `REAL` range. A failing row blocks the migration and is reported with table, key and column.
2. **Quiesce**: stop the app (or maintenance mode), take the final online backup, record the SHA-256 of that file.
3. **Create the schema on Postgres from the generated DDL with the user triggers disabled** (`ALTER TABLE … DISABLE TRIGGER USER`, which needs table ownership; `session_replication_role = replica` needs a superuser that managed hosts often withhold [R]). Reason: loading history through the triggers would fire cascades and write new audit rows and `GateTransitions`.
4. **Copy** tables in foreign-key order with `COPY … FROM STDIN (FORMAT BINARY)` through Npgsql, reading SQLite with the same tool. TEXT stays TEXT, so audit hashes still match. Then `setval` on the five identity sequences to `max(Id)+1`.
5. **Re-enable triggers**, run `SET CONSTRAINTS ALL IMMEDIATE`, and validate foreign keys.
6. **Checksums.** For every table, the tool computes a per-row SHA-256 over a canonical serialisation (the audit chain's form: each column as `<UTF-8 byte length>:<value>`, `-` for NULL, schema column order, `REAL` as the shortest round-trip text) and a table digest: SHA-256 over the per-row hashes ordered by primary key (byte order). It computes this **on the SQLite source while reading** and again **on Postgres by reading back**, and compares: row counts and digests per table. The manifest (table, rows, digest, SQLite file SHA-256, tool version) is saved to the migration report.
7. **Verify behaviour**: the audit chain on Postgres (port `verify_audit_chain.py` to read Postgres) with the same heads as `AuditChainHeads`; the trigger-name, index and column parity tests; the 24 metrics on both databases for the same `:now`; invariant queries (gate status against the last `GateTransitions` row, one baseline per train, one window per train).
8. **Record the migration as an audit event through the service after the load**, so it is appended at the chain head and carries the manifest hash. Do not write it before the load.
9. **Cutover and rollback window:** switch `Database:Provider` and the connection string; keep the SQLite file read-only for the retention period (30 days unless the owner says otherwise [U]).

Each customer gets at least one full rehearsal on a copy first, with the report reviewed.

## 7. Effort, risk, rollback, scope

### 7.1 Estimate by batch

Person-days for **one engineer who knows this repository**, including tests and review fixes, excluding calendar waits (CI billing, owner answers). **The ranges are judgment, not measurement**; the roadmap itself says its points are not calibrated, and REOS-196 (the EF spike) should narrow B0 and B3 before anyone commits to dates.

| Batch | Scope | Roadmap story | Person-days | Main assumption |
|---|---|---|---|---|
| B0 | DDL generator (`schema.pg.sql`), dialect adapter for the oracle, rule table and parity table skeleton, EF Core Npgsql spike | REOS-195 (13 pts), REOS-196 (8 pts) | 13 – 24 | The generator reuses `tools/schema_inventory.py`'s parser; EF can map the 72 entities without model changes |
| B1 | Port 47 triggers: 15 append-only, 23 status-machine, 7 cascades, 1 time, 1 `GLOB`; `app_now()`, `glob_match()`; pair cases for the groups they sit in | REOS-197 (13 pts) | 15 – 25 | Cascades and the 25 subquery `WHEN` conversions take most of it |
| B2 | Port 105 triggers: 84 tenancy, 21 role/SoD/type/client/template; complete all 117 pairs | REOS-198 (13 pts, shared with B5) | 8 – 25 | Low end if generated from a rule table, high end if hand-written; tenancy rules are repetitive |
| B3 | App layer: `DbRules` mapping, provider seam, pragmas replaced, `BackupRunner` replaced by `pg_dump`/PITR, **audit sealer redesign (4.9)**, retry and row-lock policy (4.8), about 18 SQLite-aware files | **no story found** | 12 – 22 | Sealer redesign is the largest item; no plan for it exists today |
| B4 | Analytics twin: 24 metrics in `analytics.pg.sql`, compared with `expected_metrics.json` | **no story found** | 6 – 12 | 381 lines, 21 `json_each`, 19 `julianday`, 30 `date(` |
| B5 | Migration tool, pre-flight audit, checksum manifest, rehearsal on pilot data | REOS-198 | 10 – 18 | Customer databases are small [U] |
| B6 | Hardening: concurrency suite, differential fuzz, load test, CI job, runbooks, DR drill | none | 6 – 12 | CI available (currently not) |
| | **Total** | roadmap: 47 pts across REOS-195 to 198 | **70 – 138** | |

At one engineer that is roughly 14 to 28 working weeks; with two engineers (B1–B2 beside B3–B4) calendar time is shorter, but B3's sealer and B4 touch the same services as batch 1 and need coordination [R]. The roadmap's four stories sum to 47 points and I found **no story for B3 or B4**, which together are 18 – 34 person-days. If those stay unplanned, the Postgres track will overrun its sprints.

### 7.2 Risks

| Risk | Likelihood / impact | Mitigation |
|---|---|---|
| A reported error differs for some rule combination | High without the plan, low with it / high (the product's headline property) | Section 3: numbered names, pair cases, manifest test, fuzz |
| Audit chain breaks under concurrent writers (4.9) | Medium / high (evidence integrity) | Redesign and concurrency tests before batch 1 merges |
| Race windows in cross-row rules (4.8) | Medium / high | Row locks or `SERIALIZABLE`, two-session tests |
| Loose or shared-message oracle cases hide a wrong rule | Medium / medium | Tighten expectations; Postgres side reports the trigger name |
| EF Npgsql behaves differently with `UseSqlReturningClause`, converters or identity (4.6) | Medium / medium | REOS-196 spike before B1 |
| Customer data fails the type audit or collides under `citext` | Low to medium / medium | Pre-flight audit blocks cutover; fix data on the SQLite side first |
| `GLOB` patterns in customer data use features we do not translate | Unknown / low | Pre-flight audit lists patterns; define the subset |
| Estimates are too low (no calibration, two unplanned items) | Medium / medium | Re-estimate after REOS-196 |
| CI cannot run Postgres (billing, Linux-only service containers, D31) | Certain today / medium | Local oracle runs recorded in PRs; amend D31 |
| Row-level security (REOS-199) and trigger lookups interact | Unknown / high | Prove in REOS-199 with `FORCE ROW LEVEL SECURITY` cases; `SECURITY DEFINER` where needed |

### 7.3 Rollback

- **Before cutover:** the SQLite code path stays as-is behind `Database:Provider` (default `Sqlite`); the Postgres schema is additive. A batch is reverted by reverting its PR. `main` never changes SQLite behaviour for this track.
- **At cutover:** a customer migrates only after a rehearsal passes. If verification fails, we do not switch: the SQLite file is untouched (the load only reads it) and the Postgres database is dropped.
- **After cutover:** while no writes have reached Postgres, rollback is stopping the app, restoring the previous build and pointing it at the preserved SQLite file. Once Postgres has accepted writes, going back needs a **reverse migration tool** (Postgres to SQLite, same checksum method), which this plan does not include (estimate 5 – 10 person-days if wanted [U]). The default stance: define a rollback window per customer (for example 72 hours [U]) after which Postgres is the system of record and recovery is Postgres backup and restore.

### 7.4 Out of scope

- The tenant model and row-level security (REOS-199), SSO and SCIM, the durable job queue, per-tenant secrets, regions, billing; this plan only records where they touch the port.
- Changing any rule's behaviour or message; any change to `db/schema.sql` (the port reads it; schema changes keep following `CLAUDE.md` rule 1).
- Native `uuid`, `timestamptz` and `boolean` columns, composite tenant foreign keys (PG-7), partitioning of `AuditEvents`.
- Choosing the Postgres host and its HA, replication and backup topology.
- Removing SQLite (depends on REOS-193).
- Performance tuning beyond the smoke load test.
- Editing `CLAUDE.md`, `docs/DECISIONS.md` or `docs/QUESTIONS.md`: they change when this plan is approved, not before.

## 8. Open owner decision: does a self-hosted edition continue? (REOS-193)

This is the largest swing factor in the plan and **the plan is valid for all four answers; only its lifetime and cost change.** Today's roadmap treats REOS-193 as a 2-point decision in Sprint 8; the numbers below are my estimates [U].

| Answer | What happens to the SQLite path | How the plan changes | Effort delta from 7.1 |
|---|---|---|---|
| **A. SaaS only** | SQLite becomes the migration source only; remove it after the last customer migrates | B0 to B6 as written. The oracle's SQLite side is retired after the last migration. D2, D3 and D4 are superseded for the product; amend `CLAUDE.md`'s stack and the "no Testcontainers" rule | + 5 – 10 days to remove SQLite code and tests later; nothing permanent |
| **B. Self-hosted continues on SQLite** | Both dialects are maintained for as long as the edition lives | B0 to B6 plus a **permanent** dual-dialect discipline: every new rule (for example the database-enforced SoD of REOS-177) is written twice, the oracle and parity table run on both for every PR, EF migrations exist per provider, `analytics.pg.sql` and `analytics.sql` change together, CI cost doubles | + roughly 15 – 25% on every future schema or rule story [U guess]; the parity table becomes a standing release gate |
| **C. Self-hosted continues on Postgres** | SQLite retired as in A; customers run their own Postgres | As A, plus packaging, version support matrix, backup and restore docs and a customer-run migration. D3 ("single app instance, local disk, no database server") no longer holds for them | + 10 – 20 days for packaging and docs; ongoing support of customer-run Postgres |
| **D. Self-hosted frozen** (security fixes only on the last SQLite build) | Frozen branch, no new features | As A for new work; the frozen branch keeps `db/schema.sql` as of its last release | + branch maintenance; sets an end date for the edition; features such as CAB and AI do not reach those customers |

Effects that are the same for all answers: B0 to B4 are needed; the migration tool (B5) is needed for any customer who moves, which is every customer in A, C and D and only the SaaS-bound ones in B; the numbered-name scheme and the audit sealer redesign are needed regardless.

**Needs the owner:** A, B, C or D. Provisional default until answered: **A** (it is the cheapest, matches D59's wording "Postgres for the SaaS offering", and forces no permanent dual-dialect cost). That is a build default only, not a decision.

## 9. Open questions with provisional defaults

Build to the default; do not treat it as settled. Each should be written into `docs/QUESTIONS.md` when the plan is approved.

| # | Question | Provisional default |
|---|---|---|
| PG-1 | Identifier naming in Postgres | Keep PascalCase, quoted everywhere, generated DDL |
| PG-2 | Timestamp and date columns | Keep TEXT (D5), `COLLATE "C"` |
| PG-3 | Id columns | Keep TEXT UUIDv7 and the 32-hex trigger ids (rule 6) |
| PG-4 | How to preserve error order | Numbered per-rule triggers (option A); dispatcher per group as the fallback |
| PG-5 | Concurrency for cross-row rules | `READ COMMITTED` with explicit row locks in the triggers; `SERIALIZABLE` + retry as the alternative |
| PG-6 | Amend the test stack rule ("no Testcontainers") and D31 (CI platforms) | Add a Linux job with a Postgres 16 container |
| PG-7 | Tenancy rules `trg_Ws_*`: triggers or composite foreign keys | Triggers (this port); composite FKs evaluated after REOS-199 |
| PG-8 | Boolean columns | Keep `smallint` 0/1 with CHECK |
| PG-9 | Postgres version and host | 16 or later, extensions limited to `citext` (and `pgcrypto` only if the host lacks `gen_random_uuid()`); host not chosen |
| PG-10 | Meaning of `FreezeWindows.ProductPattern` | Supported subset `* ? [...]` translated to a regex; needs product confirmation |

## Appendix A. Generated inventory

<!-- inventory:begin (generated by tools/schema_inventory.py --embed; do not edit by hand) -->

Generated by `tools/schema_inventory.py` from `db/schema.sql`: 72 tables, 152 triggers, 41 indexes.

### Trigger classes (heuristic, see the script)

| class | triggers |
|---|---|
| pure-check | 120 |
| status-machine | 23 |
| cascade-writes | 7 |
| time-dependent | 1 |
| sqlite-specific | 1 |

### Tables

| # | table | columns | CHECKs | FKs | COLLATE NOCASE | triggers |
|---|---|---|---|---|---|---|
| 1 | Users | 9 | 3 | 0 | 2 |  |
| 2 | Workspaces | 9 | 5 | 0 | 1 |  |
| 3 | WorkspaceMembers | 5 | 1 | 2 |  |  |
| 4 | Teams | 6 | 0 | 2 | 1 | 2 |
| 5 | TeamMembers | 2 | 0 | 2 |  |  |
| 6 | Holidays | 4 | 0 | 1 |  | 1 |
| 7 | TrainTemplates | 13 | 5 | 5 |  | 4 |
| 8 | TemplateGates | 9 | 2 | 2 |  | 2 |
| 9 | TemplateSteps | 9 | 2 | 2 |  | 2 |
| 10 | TemplateTasks | 7 | 1 | 3 |  | 2 |
| 11 | TemplateMilestones | 7 | 2 | 2 |  | 2 |
| 12 | TemplateCommSchedule | 5 | 0 | 2 |  | 2 |
| 13 | CommTemplateLibrary | 8 | 0 | 1 |  | 1 |
| 14 | ReleaseTrains | 32 | 8 | 12 |  | 13 |
| 15 | ChangeRecords | 9 | 0 | 1 |  |  |
| 16 | AffectedCIs | 5 | 0 | 1 |  |  |
| 17 | DeploymentWindows | 5 | 1 | 1 |  |  |
| 18 | BundledProducts | 7 | 0 | 2 |  | 2 |
| 19 | StageGates | 17 | 6 | 5 |  | 16 |
| 20 | GateTransitions | 6 | 0 | 2 |  | 2 |
| 21 | GateWaivers | 11 | 5 | 4 |  | 5 |
| 22 | ChecklistTasks | 13 | 3 | 6 |  | 4 |
| 23 | Attachments | 12 | 4 | 2 |  | 2 |
| 24 | RunbookSteps | 12 | 3 | 4 |  | 2 |
| 25 | StepDependencies | 2 | 1 | 2 |  | 2 |
| 26 | RunbookRuns | 11 | 5 | 3 |  | 2 |
| 27 | StepExecutions | 9 | 4 | 3 |  | 5 |
| 28 | Blockers | 10 | 1 | 4 |  | 2 |
| 29 | KnownIssues | 10 | 2 | 1 |  |  |
| 30 | GoNoGoDecisions | 8 | 2 | 2 |  | 2 |
| 31 | GoNoGoConditions | 8 | 0 | 3 |  | 1 |
| 32 | PostImplementationReviews | 7 | 1 | 1 |  |  |
| 33 | PirActions | 7 | 0 | 2 |  |  |
| 34 | FreezeWindows | 9 | 2 | 2 |  | 1 |
| 35 | FreezeOverrides | 8 | 3 | 4 |  | 4 |
| 36 | Baselines | 7 | 1 | 3 |  | 2 |
| 37 | ExternalLinks | 11 | 3 | 1 |  |  |
| 38 | SyncAlerts | 14 | 3 | 3 |  | 2 |
| 39 | WebhookDestinations | 8 | 4 | 1 |  | 1 |
| 40 | CommTemplates | 9 | 0 | 3 |  | 2 |
| 41 | CommSchedule | 7 | 0 | 3 |  | 2 |
| 42 | CommDispatches | 10 | 4 | 3 |  | 3 |
| 43 | Notifications | 11 | 1 | 2 |  | 1 |
| 44 | UserSessionState | 6 | 1 | 2 |  |  |
| 45 | ImportJobs | 14 | 4 | 3 |  | 3 |
| 46 | ExportJobs | 13 | 3 | 3 |  | 2 |
| 47 | MetricSnapshots | 8 | 0 | 2 |  | 2 |
| 48 | IcsTokens | 6 | 1 | 2 |  | 1 |
| 49 | SessionRevocations | 4 | 0 | 1 |  |  |
| 50 | ParsePreviews | 9 | 2 | 2 |  |  |
| 51 | ConnectorState | 9 | 2 | 1 |  | 1 |
| 52 | AuditEvents | 12 | 2 | 0 |  | 3 |
| 53 | TrainMilestones | 14 | 7 | 5 |  | 2 |
| 54 | ReleaseTypes | 8 | 2 | 2 | 1 | 3 |
| 55 | ReleaseVersions | 6 | 1 | 1 | 1 | 1 |
| 56 | Environments | 20 | 11 | 3 | 1 | 2 |
| 57 | Clients | 10 | 2 | 3 | 1 | 4 |
| 58 | ReleaseClientDates | 12 | 3 | 5 |  | 4 |
| 59 | TrainEnvironments | 2 | 0 | 2 |  | 2 |
| 60 | CatalogServices | 12 | 4 | 3 | 1 | 2 |
| 61 | IngestKeys | 11 | 4 | 3 |  | 1 |
| 62 | Deployments | 23 | 11 | 6 |  | 3 |
| 63 | DeploymentEvents | 7 | 3 | 1 |  | 1 |
| 64 | MetricSamples | 10 | 6 | 2 |  | 2 |
| 65 | MetricRollups | 12 | 2 | 2 |  | 2 |
| 66 | ThresholdBreaches | 15 | 7 | 2 |  | 2 |
| 67 | DeploymentControlActions | 14 | 10 | 4 |  | 2 |
| 68 | ApprovalPolicies | 12 | 5 | 2 | 1 | 1 |
| 69 | ApprovalPolicyRequirements | 7 | 5 | 1 | 1 | 2 |
| 70 | Approvals | 13 | 3 | 5 |  | 4 |
| 71 | ApprovalDecisions | 11 | 5 | 2 | 1 | 4 |
| 72 | AuditChainHeads | 10 | 4 | 0 |  |  |

### Indexes

| # | index | table | unique | columns | partial predicate |
|---|---|---|---|---|---|
| 1 | UX_Users_IdpIdentity | Users | yes | IdpIssuer, IdpSubject | IdpSubject IS NOT NULL |
| 2 | IX_WorkspaceMembers_User | WorkspaceMembers |  | UserId |  |
| 3 | UX_RunbookRuns_OneOpenLive | RunbookRuns | yes | ReleaseTrainId | Mode = 'Live' AND EndedAt IS NULL |
| 4 | UX_SyncAlerts_OpenFingerprint | SyncAlerts | yes | Fingerprint | IsResolved = 0 |
| 5 | UX_IcsTokens_ActivePerUser | IcsTokens | yes | WorkspaceId, UserId | RevokedAt IS NULL |
| 6 | IX_SessionRevocations_Expiry | SessionRevocations |  | ExpiresAt |  |
| 7 | IX_Trains_Status | ReleaseTrains |  | CurrentStatus, TargetReleaseDate |  |
| 8 | IX_Trains_Workspace | ReleaseTrains |  | WorkspaceId, TargetReleaseDate |  |
| 9 | IX_Gates_Train | StageGates |  | ReleaseTrainId, SequenceOrder |  |
| 10 | IX_Gates_Owner | StageGates |  | OwnerUserId, Status |  |
| 11 | IX_GateTrans_Gate | GateTransitions |  | StageGateId, OccurredAt |  |
| 12 | IX_Tasks_Gate | ChecklistTasks |  | StageGateId, SequenceOrder |  |
| 13 | IX_Tasks_Owner | ChecklistTasks |  | OwnerUserId, IsCompleted |  |
| 14 | IX_Steps_Train | RunbookSteps |  | ReleaseTrainId, PlannedStartAt |  |
| 15 | IX_StepExec_Run | StepExecutions |  | RunId, Status |  |
| 16 | IX_Blockers_Open | Blockers |  | ReleaseTrainId, Severity | ResolvedAt IS NULL |
| 17 | IX_Links_Entity | ExternalLinks |  | EntityType, EntityId |  |
| 18 | IX_Links_State | ExternalLinks |  | SyncState | SyncState <> 'InSync' |
| 19 | IX_Attach_Entity | Attachments |  | EntityType, EntityId |  |
| 20 | IX_Comm_Due | CommSchedule |  | ReleaseTrainId, DueAt |  |
| 21 | IX_Notif_User | Notifications |  | UserId, ReadAt, CreatedAt |  |
| 22 | IX_Freeze_Range | FreezeWindows |  | WorkspaceId, StartsAt, EndsAt |  |
| 23 | IX_Audit_Train | AuditEvents |  | ReleaseTrainId, OccurredAt |  |
| 24 | IX_Audit_Entity | AuditEvents |  | EntityType, EntityId |  |
| 25 | IX_Audit_Workspace | AuditEvents |  | WorkspaceId, OccurredAt |  |
| 26 | IX_Milestones_Train | TrainMilestones |  | ReleaseTrainId, DueOn |  |
| 27 | UX_Trains_Key | ReleaseTrains | yes | WorkspaceId, ReleaseKey | ReleaseKey IS NOT NULL |
| 28 | IX_Deployments_Env | Deployments |  | WorkspaceId, EnvironmentId, State |  |
| 29 | IX_Deployments_Service | Deployments |  | CatalogServiceId, EnvironmentId, CreatedAt |  |
| 30 | IX_Deployments_Train | Deployments |  | ReleaseTrainId | ReleaseTrainId IS NOT NULL |
| 31 | IX_DeploymentEvents_Deployment | DeploymentEvents |  | DeploymentId, OccurredAt |  |
| 32 | IX_MetricSamples_Time | MetricSamples |  | SampledAt |  |
| 33 | UX_Breaches_Open | ThresholdBreaches | yes | DeploymentId, Series, Metric | ResolvedAt IS NULL |
| 34 | IX_Breaches_Deployment | ThresholdBreaches |  | DeploymentId, StartedAt |  |
| 35 | IX_ControlActions_Deployment | DeploymentControlActions |  | DeploymentId, RequestedAt |  |
| 36 | UX_ControlActions_OneRollbackPerBreach | DeploymentControlActions | yes | BreachId | BreachId IS NOT NULL |
| 37 | UX_Approvals_OneOpenPerTarget | Approvals | yes | ReleaseTrainId, EnvironmentId | Status IN ('Pending','ChangesRequested') |
| 38 | IX_Approvals_Workspace | Approvals |  | WorkspaceId, Status, DueAt |  |
| 39 | IX_Audit_Chain | AuditEvents |  | WorkspaceId, Id |  |
| 40 | IX_Audit_Unsealed | AuditEvents |  | Id | Hash IS NULL |
| 41 | UX_Trains_VersionSlot | ReleaseTrains | yes | ReleaseVersionId, ReleaseTypeId, COALESCE(BetaNumber, 0) | ReleaseVersionId IS NOT NULL AND CurrentStatus <> 'Aborted' |

### Triggers

`rank` is the SQLite fire position inside its (table, timing, event) group: 1 fires first (newest-first = reverse file order). An `UPDATE OF` trigger only joins the group when the statement's SET list names one of its columns.

| # | trigger | table | timing | event (OF) | rank/group | class | rule text (first RAISE message) | note |
|---|---|---|---|---|---|---|---|---|
| 1 | trg_Audit_NoUpdate | AuditEvents | BEFORE | UPDATE | 1/1 | pure-check | AuditEvents is append-only | immutability / append-only |
| 2 | trg_Audit_NoDelete | AuditEvents | BEFORE | DELETE | 1/1 | pure-check | AuditEvents is append-only | immutability / append-only |
| 3 | trg_GateTrans_NoUpdate | GateTransitions | BEFORE | UPDATE | 1/1 | pure-check | GateTransitions is append-only | immutability / append-only |
| 4 | trg_GateTrans_NoDelete | GateTransitions | BEFORE | DELETE | 1/1 | pure-check | GateTransitions is append-only | immutability / append-only |
| 5 | trg_Baseline_NoUpdate | Baselines | BEFORE | UPDATE | 1/1 | pure-check | Baselines are immutable | immutability / append-only |
| 6 | trg_Baseline_NoDelete | Baselines | BEFORE | DELETE | 1/1 | pure-check | Baselines are immutable | immutability / append-only |
| 7 | trg_GoNoGo_NoUpdate | GoNoGoDecisions | BEFORE | UPDATE | 1/1 | pure-check | Go/No-Go decisions are immutable | immutability / append-only |
| 8 | trg_GoNoGo_NoDelete | GoNoGoDecisions | BEFORE | DELETE | 1/1 | pure-check | Go/No-Go decisions are immutable | immutability / append-only |
| 9 | trg_Dispatch_NoUpdate | CommDispatches | BEFORE | UPDATE | 1/1 | pure-check | Dispatches are immutable | immutability / append-only |
| 10 | trg_Dispatch_NoDelete | CommDispatches | BEFORE | DELETE | 1/1 | pure-check | Dispatches are immutable | immutability / append-only |
| 11 | trg_Attachment_LockedNoDelete | Attachments | BEFORE | DELETE | 1/1 | pure-check | Attachment is locked evidence | immutability / append-only |
| 12 | trg_Attachment_LockedNoUpdate | Attachments | BEFORE | UPDATE | 1/1 | pure-check | Attachment is locked evidence | immutability / append-only |
| 13 | trg_Gate_Transition | StageGates | BEFORE | UPDATE OF Status | 10/10 | status-machine | Illegal gate status transition | guards a status/decision transition or reads another row's status |
| 14 | trg_Train_Transition | ReleaseTrains | BEFORE | UPDATE OF CurrentStatus | 9/9 | status-machine | Illegal train status transition | guards a status/decision transition or reads another row's status |
| 15 | trg_Train_GateLockout | ReleaseTrains | BEFORE | UPDATE OF CurrentStatus | 8/9 | status-machine | Gate lockout: an uncertified gate blocks this transition | guards a status/decision transition or reads another row's status |
| 16 | trg_Train_ExecutingRequiresGo | ReleaseTrains | BEFORE | UPDATE OF CurrentStatus | 7/9 | status-machine | Executing requires a recorded Go decision | guards a status/decision transition or reads another row's status |
| 17 | trg_Train_ExecutingNoExpiredConditions | ReleaseTrains | BEFORE | UPDATE OF CurrentStatus | 6/9 | time-dependent | A Go/No-Go condition expired without being closed | compares to the service clock column, falling back to SQLite 'now' |
| 18 | trg_Train_ExecutingRequiresBaseline | ReleaseTrains | BEFORE | UPDATE OF CurrentStatus | 5/9 | status-machine | Executing requires a captured baseline | guards a status/decision transition or reads another row's status |
| 19 | trg_Train_ExecutingRequiresRollback | ReleaseTrains | BEFORE | UPDATE OF CurrentStatus | 4/9 | status-machine | High-risk train requires a rehearsed rollback | guards a status/decision transition or reads another row's status |
| 20 | trg_Train_CompleteRequiresPir | ReleaseTrains | AFTER | UPDATE OF CurrentStatus | 1/1 | cascade-writes | (no RAISE: writes AuditEvents, PostImplementationReviews) | writes AuditEvents, PostImplementationReviews |
| 21 | trg_Gate_CertifyCapturesBaseline | StageGates | AFTER | UPDATE OF Status | 3/3 | cascade-writes | (no RAISE: writes AuditEvents, Baselines) | writes AuditEvents, Baselines |
| 22 | trg_Gate_RequireActor | StageGates | BEFORE | UPDATE OF Status | 9/10 | status-machine | Gate status change requires LastChangedByUserId | guards a status/decision transition or reads another row's status |
| 23 | trg_Gate_LogTransition | StageGates | AFTER | UPDATE OF Status | 2/3 | cascade-writes | (no RAISE: writes GateTransitions) | writes GateTransitions |
| 24 | trg_Gate_CertifyRequiresTasks | StageGates | BEFORE | UPDATE OF Status | 8/10 | status-machine | Gate has open checklist tasks | guards a status/decision transition or reads another row's status |
| 25 | trg_Gate_ComplianceNeedsTask | StageGates | BEFORE | UPDATE OF Status | 7/10 | status-machine | Compliance gate needs at least one task | guards a status/decision transition or reads another row's status |
| 26 | trg_Gate_CertifyInOrder | StageGates | BEFORE | UPDATE OF Status | 6/10 | status-machine | An earlier gate is not certified | guards a status/decision transition or reads another row's status |
| 27 | trg_Gate_ComplianceCertifierRole | StageGates | BEFORE | UPDATE OF Status | 5/10 | status-machine | Compliance gates are certified by Governance Officers only | guards a status/decision transition or reads another row's status |
| 28 | trg_Gate_CertifierNotTaskAuthor | StageGates | BEFORE | UPDATE OF Status | 4/10 | status-machine | Segregation of duties: certifier completed a task in this gate | guards a status/decision transition or reads another row's status |
| 29 | trg_Gate_WaiveRequiresApproval | StageGates | BEFORE | UPDATE OF Status | 3/10 | status-machine | Waiver requires a second approver | guards a status/decision transition or reads another row's status |
| 30 | trg_Gate_CertifyLocksEvidence | StageGates | AFTER | UPDATE OF Status | 1/3 | cascade-writes | (no RAISE: writes Attachments, AuditEvents) | writes Attachments, AuditEvents |
| 31 | trg_Waiver_ApproverRole | GateWaivers | BEFORE | UPDATE OF ApprovedByUserId | 4/4 | pure-check | Waivers are approved by a Governance Officer | role / separation-of-duties lookup |
| 32 | trg_Waiver_ApproverRoleInsert | GateWaivers | BEFORE | INSERT | 1/1 | pure-check | Waivers are approved by a Governance Officer | role / separation-of-duties lookup |
| 33 | trg_Waiver_DeclineRole | GateWaivers | BEFORE | UPDATE OF DeclinedByUserId | 3/4 | pure-check | Waivers are declined by a Governance Officer | role / separation-of-duties lookup |
| 34 | trg_Waiver_DeclineDistinct | GateWaivers | BEFORE | UPDATE OF DeclinedByUserId | 2/4 | pure-check | A waiver needs two distinct people: the requester cannot decline it | row or cross-row validation |
| 35 | trg_Waiver_ApprovedOrDeclined | GateWaivers | BEFORE | UPDATE OF ApprovedByUserId, DeclinedByUserId | 1/4 | pure-check | A waiver is approved or declined, not both | row or cross-row validation |
| 36 | trg_Override_NoUpdate | FreezeOverrides | BEFORE | UPDATE | 1/1 | pure-check | Freeze overrides are immutable; renew with a new row | immutability / append-only |
| 37 | trg_Override_NoDelete | FreezeOverrides | BEFORE | DELETE | 1/1 | pure-check | Freeze overrides are immutable; renew with a new row | immutability / append-only |
| 38 | trg_Override_ApproverRole | FreezeOverrides | BEFORE | INSERT | 2/2 | pure-check | Freeze overrides are approved by a Release Manager or Governance Officer | role / separation-of-duties lookup |
| 39 | trg_Task_ReopenDecertifies | ChecklistTasks | AFTER | UPDATE OF IsCompleted | 1/1 | cascade-writes | (no RAISE: writes AuditEvents, StageGates) | writes AuditEvents, StageGates |
| 40 | trg_Task_InsertDecertifies | ChecklistTasks | AFTER | INSERT | 1/1 | cascade-writes | (no RAISE: writes AuditEvents, StageGates) | writes AuditEvents, StageGates |
| 41 | trg_Run_LiveRequiresExecuting | RunbookRuns | BEFORE | INSERT | 1/1 | status-machine | Live run requires the train to be Executing | INSERT guard that reads the train's CurrentStatus |
| 42 | trg_Step_DependenciesDone | StepExecutions | BEFORE | UPDATE OF Status | 4/4 | status-machine | Dependency not complete | guards a status/decision transition or reads another row's status |
| 43 | trg_Step_FreezeLockout | StepExecutions | BEFORE | UPDATE OF Status | 3/4 | sqlite-specific | Freeze window: no valid override for this train | uses GLOB for ProductPattern; it is also a status guard (Status -> Running); GLOB is case-sensitive, Postgres LIKE/SIMILAR TO are not the same |
| 44 | trg_Condition_RequiresConditionalGo | GoNoGoConditions | BEFORE | INSERT | 1/1 | status-machine | Conditions belong to a GoWithConditions decision | INSERT guard that reads the decision's Decision value |
| 45 | trg_Import_NoCommitWithErrors | ImportJobs | BEFORE | UPDATE OF Status | 2/2 | status-machine | Import has row errors; fix the file and re-preview | guards ImportJobs.Status -> Committed |
| 46 | trg_Train_KeyImmutable | ReleaseTrains | BEFORE | UPDATE OF ReleaseKey | 3/9 | pure-check | A release key never changes | immutability / append-only |
| 47 | trg_Train_AssignKey | ReleaseTrains | AFTER | INSERT | 1/1 | cascade-writes | (no RAISE: writes ReleaseTrains, Workspaces) | AFTER INSERT writes Workspaces.NextKeyNumber and the train's own ReleaseKey; no audit row |
| 48 | trg_Train_TypeRules_Insert | ReleaseTrains | BEFORE | INSERT | 2/2 | pure-check | A release needs a release type (+3 more in body order) | row or cross-row validation |
| 49 | trg_Train_TypeRules_Update | ReleaseTrains | BEFORE | UPDATE OF ReleaseTypeId, BetaNumber, ReleaseVersionId | 2/9 | pure-check | A release cannot lose its release type (+3 more in body order) | row or cross-row validation |
| 50 | trg_ReleaseType_BetaFlagLocked | ReleaseTypes | BEFORE | UPDATE OF IsBeta | 2/2 | pure-check | A release type that releases use cannot become or stop being a Beta type | immutability / append-only |
| 51 | trg_Client_SegmentRules_Insert | Clients | BEFORE | INSERT | 2/2 | pure-check | Only an On-Demand client has a CERT environment (+1 more in body order) | row or cross-row validation |
| 52 | trg_Client_SegmentRules_Update | Clients | BEFORE | UPDATE OF Segment, CertEnvironmentId | 2/2 | pure-check | Only an On-Demand client has a CERT environment (+1 more in body order) | row or cross-row validation |
| 53 | trg_ClientDate_Rules_Insert | ReleaseClientDates | BEFORE | INSERT | 2/2 | pure-check | Only an On-Demand client has its own date on a release | row or cross-row validation |
| 54 | trg_ClientDate_Rules_Update | ReleaseClientDates | BEFORE | UPDATE OF ReleaseTrainId, ClientId | 2/2 | pure-check | A client date keeps its release and client | row or cross-row validation |
| 55 | trg_Deployment_Transition | Deployments | BEFORE | UPDATE OF State | 2/2 | status-machine | Illegal deployment state transition | guards a status/decision transition or reads another row's status |
| 56 | trg_DeploymentEvent_NoUpdate | DeploymentEvents | BEFORE | UPDATE | 1/1 | pure-check | DeploymentEvents is append-only | immutability / append-only |
| 57 | trg_Step_RunPaused | StepExecutions | BEFORE | UPDATE OF Status | 2/4 | status-machine | The run is paused: no step can start until it is resumed | guards a status/decision transition or reads another row's status |
| 58 | trg_Approval_ApprovedNeedsAll | Approvals | BEFORE | UPDATE OF Status | 3/3 | status-machine | Every required approver must approve first | guards a status/decision transition or reads another row's status |
| 59 | trg_Approval_Final | Approvals | BEFORE | UPDATE OF Status | 2/3 | status-machine | An approved or cancelled approval is final | guards a status/decision transition or reads another row's status |
| 60 | trg_ApprovalDecision_ApprovalOpen | ApprovalDecisions | BEFORE | UPDATE | 4/4 | status-machine | The approval is closed | reads Approvals.Status: a decision cannot change once the approval is closed |
| 61 | trg_ApprovalDecision_Final | ApprovalDecisions | BEFORE | UPDATE OF Decision | 3/4 | status-machine | A decision is final; the requester resubmits the approval for a new round | guards a status/decision transition or reads another row's status |
| 62 | trg_ApprovalDecision_Eligible | ApprovalDecisions | BEFORE | UPDATE OF Decision | 2/4 | pure-check | Not an eligible approver for this seat | role / separation-of-duties lookup on who may decide (fires on Decision change but checks the person, not a status path) |
| 63 | trg_ApprovalDecision_Separation | ApprovalDecisions | BEFORE | UPDATE OF Decision | 1/4 | pure-check | Separation of duties: you cannot approve a change you requested (+2 more in body order) | role / separation-of-duties lookup on who may decide |
| 64 | trg_Ws_Holidays_Update | Holidays | BEFORE | UPDATE OF WorkspaceId | 1/1 | pure-check | A row cannot move to another workspace | tenancy: parent and child must share a workspace/train |
| 65 | trg_Ws_TrainTemplates_Update | TrainTemplates | BEFORE | UPDATE OF WorkspaceId, SupersedesTemplateId | 2/2 | pure-check | A row cannot move to another workspace (+1 more in body order) | tenancy: parent and child must share a workspace/train |
| 66 | trg_Ws_CommTemplateLibrary_Update | CommTemplateLibrary | BEFORE | UPDATE OF WorkspaceId | 1/1 | pure-check | A row cannot move to another workspace | tenancy: parent and child must share a workspace/train |
| 67 | trg_Ws_FreezeWindows_Update | FreezeWindows | BEFORE | UPDATE OF WorkspaceId | 1/1 | pure-check | A row cannot move to another workspace | tenancy: parent and child must share a workspace/train |
| 68 | trg_Ws_WebhookDestinations_Update | WebhookDestinations | BEFORE | UPDATE OF WorkspaceId | 1/1 | pure-check | A row cannot move to another workspace | tenancy: parent and child must share a workspace/train |
| 69 | trg_Ws_Notifications_Update | Notifications | BEFORE | UPDATE OF WorkspaceId | 1/1 | pure-check | A row cannot move to another workspace | tenancy: parent and child must share a workspace/train |
| 70 | trg_Ws_IcsTokens_Update | IcsTokens | BEFORE | UPDATE OF WorkspaceId | 1/1 | pure-check | A row cannot move to another workspace | tenancy: parent and child must share a workspace/train |
| 71 | trg_Ws_ConnectorState_Update | ConnectorState | BEFORE | UPDATE OF WorkspaceId | 1/1 | pure-check | A row cannot move to another workspace | tenancy: parent and child must share a workspace/train |
| 72 | trg_Ws_ReleaseTrains_Insert | ReleaseTrains | BEFORE | INSERT | 1/2 | pure-check | The template belongs to another workspace (+7 more in body order) | tenancy: parent and child must share a workspace/train |
| 73 | trg_Ws_ReleaseTrains_Update | ReleaseTrains | BEFORE | UPDATE OF WorkspaceId, TemplateId, ClonedFromTrainId, OwnerUserId, OwnerTeamId, OnCallUserId, OnCallTeamId, ReleaseTypeId, ReleaseVersionId | 1/9 | pure-check | A row cannot move to another workspace (+8 more in body order) | tenancy: parent and child must share a workspace/train |
| 74 | trg_Ws_Teams_Insert | Teams | BEFORE | INSERT | 1/1 | pure-check | The webhook destination belongs to another workspace | tenancy: parent and child must share a workspace/train |
| 75 | trg_Ws_Teams_Update | Teams | BEFORE | UPDATE OF WorkspaceId, WebhookDestinationId | 1/1 | pure-check | A row cannot move to another workspace (+1 more in body order) | tenancy: parent and child must share a workspace/train |
| 76 | trg_Ws_SyncAlerts_Insert | SyncAlerts | BEFORE | INSERT | 1/1 | pure-check | The train belongs to another workspace | tenancy: parent and child must share a workspace/train |
| 77 | trg_Ws_SyncAlerts_Update | SyncAlerts | BEFORE | UPDATE OF WorkspaceId, ReleaseTrainId | 1/1 | pure-check | A row cannot move to another workspace (+1 more in body order) | tenancy: parent and child must share a workspace/train |
| 78 | trg_Ws_ImportJobs_Insert | ImportJobs | BEFORE | INSERT | 1/1 | pure-check | The train belongs to another workspace | tenancy: parent and child must share a workspace/train |
| 79 | trg_Ws_ImportJobs_Update | ImportJobs | BEFORE | UPDATE OF WorkspaceId, ReleaseTrainId | 1/2 | pure-check | A row cannot move to another workspace (+1 more in body order) | tenancy: parent and child must share a workspace/train |
| 80 | trg_Ws_ExportJobs_Insert | ExportJobs | BEFORE | INSERT | 1/1 | pure-check | The train belongs to another workspace | tenancy: parent and child must share a workspace/train |
| 81 | trg_Ws_ExportJobs_Update | ExportJobs | BEFORE | UPDATE OF WorkspaceId, ReleaseTrainId | 1/1 | pure-check | A row cannot move to another workspace (+1 more in body order) | tenancy: parent and child must share a workspace/train |
| 82 | trg_Ws_AuditEvents_Insert | AuditEvents | BEFORE | INSERT | 1/1 | pure-check | The train belongs to another workspace | tenancy: parent and child must share a workspace/train |
| 83 | trg_Ws_TemplateGates_Insert | TemplateGates | BEFORE | INSERT | 1/1 | pure-check | The team belongs to another workspace | tenancy: parent and child must share a workspace/train |
| 84 | trg_Ws_TemplateGates_Update | TemplateGates | BEFORE | UPDATE OF TemplateId, OwnerTeamId | 1/1 | pure-check | The team belongs to another workspace | tenancy: parent and child must share a workspace/train |
| 85 | trg_Ws_TemplateSteps_Insert | TemplateSteps | BEFORE | INSERT | 1/1 | pure-check | The team belongs to another workspace | tenancy: parent and child must share a workspace/train |
| 86 | trg_Ws_TemplateSteps_Update | TemplateSteps | BEFORE | UPDATE OF TemplateId, OwnerTeamId | 1/1 | pure-check | The team belongs to another workspace | tenancy: parent and child must share a workspace/train |
| 87 | trg_Ws_TemplateCommSchedule_Insert | TemplateCommSchedule | BEFORE | INSERT | 1/1 | pure-check | The library template belongs to another workspace | tenancy: parent and child must share a workspace/train |
| 88 | trg_Ws_TemplateCommSchedule_Update | TemplateCommSchedule | BEFORE | UPDATE OF TemplateId, LibraryTemplateId | 1/1 | pure-check | The library template belongs to another workspace | tenancy: parent and child must share a workspace/train |
| 89 | trg_Ws_TrainTemplates_Insert | TrainTemplates | BEFORE | INSERT | 2/2 | pure-check | The previous version belongs to another workspace | tenancy: parent and child must share a workspace/train |
| 90 | trg_Ws_TemplateTasks_Insert | TemplateTasks | BEFORE | INSERT | 1/1 | pure-check | The team belongs to another workspace (+1 more in body order) | tenancy: parent and child must share a workspace/train |
| 91 | trg_Ws_TemplateTasks_Update | TemplateTasks | BEFORE | UPDATE OF TemplateId, TemplateGateId, OwnerTeamId | 1/1 | pure-check | The team belongs to another workspace (+1 more in body order) | tenancy: parent and child must share a workspace/train |
| 92 | trg_Ws_TemplateMilestones_Insert | TemplateMilestones | BEFORE | INSERT | 1/1 | pure-check | The team belongs to another workspace | tenancy: parent and child must share a workspace/train |
| 93 | trg_Ws_TemplateMilestones_Update | TemplateMilestones | BEFORE | UPDATE OF TemplateId, OwnerTeamId | 1/1 | pure-check | The team belongs to another workspace | tenancy: parent and child must share a workspace/train |
| 94 | trg_Ws_StageGates_Insert | StageGates | BEFORE | INSERT | 2/2 | pure-check | The team belongs to another workspace | tenancy: parent and child must share a workspace/train |
| 95 | trg_Ws_StageGates_Update | StageGates | BEFORE | UPDATE OF ReleaseTrainId, OwnerTeamId | 2/10 | pure-check | A row cannot move to another train (+1 more in body order) | tenancy: parent and child must share a workspace/train |
| 96 | trg_Ws_RunbookSteps_Insert | RunbookSteps | BEFORE | INSERT | 1/1 | pure-check | The team belongs to another workspace (+1 more in body order) | tenancy: parent and child must share a workspace/train |
| 97 | trg_Ws_RunbookSteps_Update | RunbookSteps | BEFORE | UPDATE OF ReleaseTrainId, OwnerTeamId, BundledProductId | 1/1 | pure-check | A row cannot move to another train (+2 more in body order) | tenancy: parent and child must share a workspace/train |
| 98 | trg_Ws_TrainMilestones_Insert | TrainMilestones | BEFORE | INSERT | 1/1 | pure-check | The team belongs to another workspace | tenancy: parent and child must share a workspace/train |
| 99 | trg_Ws_TrainMilestones_Update | TrainMilestones | BEFORE | UPDATE OF ReleaseTrainId, OwnerTeamId | 1/1 | pure-check | A row cannot move to another train (+1 more in body order) | tenancy: parent and child must share a workspace/train |
| 100 | trg_Ws_Blockers_Insert | Blockers | BEFORE | INSERT | 1/1 | pure-check | The product belongs to another train (+1 more in body order) | tenancy: parent and child must share a workspace/train |
| 101 | trg_Ws_Blockers_Update | Blockers | BEFORE | UPDATE OF ReleaseTrainId, BundledProductId, StageGateId | 1/1 | pure-check | A row cannot move to another train (+2 more in body order) | tenancy: parent and child must share a workspace/train |
| 102 | trg_Ws_CommTemplates_Insert | CommTemplates | BEFORE | INSERT | 1/1 | pure-check | The library template belongs to another workspace (+1 more in body order) | tenancy: parent and child must share a workspace/train |
| 103 | trg_Ws_CommTemplates_Update | CommTemplates | BEFORE | UPDATE OF ReleaseTrainId, LibraryTemplateId, StageGateId | 1/1 | pure-check | A row cannot move to another train (+2 more in body order) | tenancy: parent and child must share a workspace/train |
| 104 | trg_Ws_CommSchedule_Insert | CommSchedule | BEFORE | INSERT | 1/1 | pure-check | The communication template belongs to another train (+1 more in body order) | tenancy: parent and child must share a workspace/train |
| 105 | trg_Ws_CommSchedule_Update | CommSchedule | BEFORE | UPDATE OF ReleaseTrainId, CommTemplateId, DispatchId | 1/1 | pure-check | A row cannot move to another train (+2 more in body order) | tenancy: parent and child must share a workspace/train |
| 106 | trg_Ws_BundledProducts_Update | BundledProducts | BEFORE | UPDATE OF ReleaseTrainId, CatalogServiceId | 1/1 | pure-check | A row cannot move to another train (+1 more in body order) | tenancy: parent and child must share a workspace/train |
| 107 | trg_Ws_RunbookRuns_Update | RunbookRuns | BEFORE | UPDATE OF ReleaseTrainId | 1/1 | pure-check | A row cannot move to another train | tenancy: parent and child must share a workspace/train |
| 108 | trg_Ws_ChecklistTasks_Insert | ChecklistTasks | BEFORE | INSERT | 1/1 | pure-check | The team belongs to another workspace (+1 more in body order) | tenancy: parent and child must share a workspace/train |
| 109 | trg_Ws_ChecklistTasks_Update | ChecklistTasks | BEFORE | UPDATE OF StageGateId, OwnerTeamId, BundledProductId | 1/1 | pure-check | A row cannot move to another train (+2 more in body order) | tenancy: parent and child must share a workspace/train |
| 110 | trg_Ws_StepExecutions_Insert | StepExecutions | BEFORE | INSERT | 1/1 | pure-check | The step belongs to another train | tenancy: parent and child must share a workspace/train |
| 111 | trg_Ws_StepExecutions_Update | StepExecutions | BEFORE | UPDATE OF RunId, StepId | 1/4 | pure-check | The step belongs to another train | tenancy: parent and child must share a workspace/train |
| 112 | trg_Ws_StepDependencies_Insert | StepDependencies | BEFORE | INSERT | 1/1 | pure-check | The step belongs to another train | tenancy: parent and child must share a workspace/train |
| 113 | trg_Ws_StepDependencies_Update | StepDependencies | BEFORE | UPDATE OF StepId, DependsOnStepId | 1/1 | pure-check | The step belongs to another train | tenancy: parent and child must share a workspace/train |
| 114 | trg_Ws_CommDispatches_Insert | CommDispatches | BEFORE | INSERT | 1/1 | pure-check | The webhook destination belongs to another workspace | tenancy: parent and child must share a workspace/train |
| 115 | trg_Ws_FreezeOverrides_Insert | FreezeOverrides | BEFORE | INSERT | 1/2 | pure-check | The freeze window belongs to another workspace | tenancy: parent and child must share a workspace/train |
| 116 | trg_Ws_MetricSnapshots_Insert | MetricSnapshots | BEFORE | INSERT | 1/1 | pure-check | The train belongs to another workspace | tenancy: parent and child must share a workspace/train |
| 117 | trg_Ws_MetricSnapshots_Update | MetricSnapshots | BEFORE | UPDATE OF ExportJobId, ReleaseTrainId | 1/1 | pure-check | The train belongs to another workspace | tenancy: parent and child must share a workspace/train |
| 118 | trg_Ws_ReleaseTypes_Insert | ReleaseTypes | BEFORE | INSERT | 1/1 | pure-check | The template belongs to another workspace | tenancy: parent and child must share a workspace/train |
| 119 | trg_Ws_ReleaseTypes_Update | ReleaseTypes | BEFORE | UPDATE OF WorkspaceId, DefaultTemplateId | 1/2 | pure-check | A row cannot move to another workspace (+1 more in body order) | tenancy: parent and child must share a workspace/train |
| 120 | trg_Ws_ReleaseVersions_Update | ReleaseVersions | BEFORE | UPDATE OF WorkspaceId | 1/1 | pure-check | A row cannot move to another workspace | tenancy: parent and child must share a workspace/train |
| 121 | trg_Ws_Environments_Insert | Environments | BEFORE | INSERT | 1/1 | pure-check | The webhook destination belongs to another workspace | tenancy: parent and child must share a workspace/train |
| 122 | trg_Ws_Environments_Update | Environments | BEFORE | UPDATE OF WorkspaceId, ControlWebhookDestinationId | 1/1 | pure-check | A row cannot move to another workspace (+1 more in body order) | tenancy: parent and child must share a workspace/train |
| 123 | trg_Ws_Clients_Insert | Clients | BEFORE | INSERT | 1/2 | pure-check | The environment belongs to another workspace | tenancy: parent and child must share a workspace/train |
| 124 | trg_Ws_Clients_Update | Clients | BEFORE | UPDATE OF WorkspaceId, CertEnvironmentId | 1/2 | pure-check | A row cannot move to another workspace (+1 more in body order) | tenancy: parent and child must share a workspace/train |
| 125 | trg_Ws_ReleaseClientDates_Insert | ReleaseClientDates | BEFORE | INSERT | 1/2 | pure-check | The train belongs to another workspace (+1 more in body order) | tenancy: parent and child must share a workspace/train |
| 126 | trg_Ws_ReleaseClientDates_Update | ReleaseClientDates | BEFORE | UPDATE OF WorkspaceId | 1/2 | pure-check | A row cannot move to another workspace | tenancy: parent and child must share a workspace/train |
| 127 | trg_Ws_CatalogServices_Insert | CatalogServices | BEFORE | INSERT | 1/1 | pure-check | The team belongs to another workspace | tenancy: parent and child must share a workspace/train |
| 128 | trg_Ws_CatalogServices_Update | CatalogServices | BEFORE | UPDATE OF WorkspaceId, OwnerTeamId | 1/1 | pure-check | A row cannot move to another workspace (+1 more in body order) | tenancy: parent and child must share a workspace/train |
| 129 | trg_Ws_IngestKeys_Update | IngestKeys | BEFORE | UPDATE OF WorkspaceId | 1/1 | pure-check | A row cannot move to another workspace | tenancy: parent and child must share a workspace/train |
| 130 | trg_Ws_TrainEnvironments_Insert | TrainEnvironments | BEFORE | INSERT | 1/1 | pure-check | The environment belongs to another workspace | tenancy: parent and child must share a workspace/train |
| 131 | trg_Ws_TrainEnvironments_Update | TrainEnvironments | BEFORE | UPDATE OF ReleaseTrainId, EnvironmentId | 1/1 | pure-check | A row cannot move to another train (+1 more in body order) | tenancy: parent and child must share a workspace/train |
| 132 | trg_Ws_BundledProducts_Insert | BundledProducts | BEFORE | INSERT | 1/1 | pure-check | The catalog service belongs to another workspace | tenancy: parent and child must share a workspace/train |
| 133 | trg_Ws_Deployments_Insert | Deployments | BEFORE | INSERT | 1/1 | pure-check | The service belongs to another workspace (+4 more in body order) | tenancy: parent and child must share a workspace/train |
| 134 | trg_Ws_Deployments_Update | Deployments | BEFORE | UPDATE OF WorkspaceId, ExternalId, CatalogServiceId, EnvironmentId, ReleaseTrainId, RunId | 1/2 | pure-check | A row cannot move to another workspace (+3 more in body order) | tenancy: parent and child must share a workspace/train |
| 135 | trg_Ws_MetricSamples_Insert | MetricSamples | BEFORE | INSERT | 1/1 | pure-check | The deployment belongs to another workspace | tenancy: parent and child must share a workspace/train |
| 136 | trg_Ws_MetricRollups_Insert | MetricRollups | BEFORE | INSERT | 1/1 | pure-check | The deployment belongs to another workspace | tenancy: parent and child must share a workspace/train |
| 137 | trg_Ws_ThresholdBreaches_Insert | ThresholdBreaches | BEFORE | INSERT | 1/1 | pure-check | The deployment belongs to another workspace | tenancy: parent and child must share a workspace/train |
| 138 | trg_Ws_ControlActions_Insert | DeploymentControlActions | BEFORE | INSERT | 1/1 | pure-check | The deployment belongs to another workspace (+1 more in body order) | tenancy: parent and child must share a workspace/train |
| 139 | trg_Ws_MetricSamples_Update | MetricSamples | BEFORE | UPDATE OF WorkspaceId | 1/1 | pure-check | A row cannot move to another workspace | tenancy: parent and child must share a workspace/train |
| 140 | trg_Ws_MetricRollups_Update | MetricRollups | BEFORE | UPDATE OF WorkspaceId | 1/1 | pure-check | A row cannot move to another workspace | tenancy: parent and child must share a workspace/train |
| 141 | trg_Ws_ThresholdBreaches_Update | ThresholdBreaches | BEFORE | UPDATE OF WorkspaceId | 1/1 | pure-check | A row cannot move to another workspace | tenancy: parent and child must share a workspace/train |
| 142 | trg_Ws_DeploymentControlActions_Update | DeploymentControlActions | BEFORE | UPDATE OF WorkspaceId | 1/1 | pure-check | A row cannot move to another workspace | tenancy: parent and child must share a workspace/train |
| 143 | trg_Ws_ApprovalPolicies_Update | ApprovalPolicies | BEFORE | UPDATE OF WorkspaceId | 1/1 | pure-check | A row cannot move to another workspace | tenancy: parent and child must share a workspace/train |
| 144 | trg_Ws_PolicyRequirements_Insert | ApprovalPolicyRequirements | BEFORE | INSERT | 1/1 | pure-check | The team belongs to another workspace | tenancy: parent and child must share a workspace/train |
| 145 | trg_Ws_PolicyRequirements_Update | ApprovalPolicyRequirements | BEFORE | UPDATE OF PolicyId, EligibleKind, EligibleValue | 1/1 | pure-check | The team belongs to another workspace | tenancy: parent and child must share a workspace/train |
| 146 | trg_Ws_Approvals_Insert | Approvals | BEFORE | INSERT | 1/1 | pure-check | The train belongs to another workspace (+1 more in body order) | tenancy: parent and child must share a workspace/train |
| 147 | trg_Ws_Approvals_Update | Approvals | BEFORE | UPDATE OF WorkspaceId, ReleaseTrainId, EnvironmentId | 1/3 | pure-check | A row cannot move to another workspace (+1 more in body order) | tenancy: parent and child must share a workspace/train |
| 148 | trg_Template_ApproverNotAuthor_Insert | TrainTemplates | BEFORE | INSERT | 1/2 | pure-check | Separation of duties: the author of a template cannot approve it | role / separation-of-duties lookup: approver is not the template author |
| 149 | trg_Template_ApproverNotAuthor_Update | TrainTemplates | BEFORE | UPDATE OF Status, ApprovedByUserId | 1/2 | pure-check | Separation of duties: the author of a template cannot approve it | role / separation-of-duties lookup: approver is not the template author |
| 150 | trg_Gate_TemplateLocked_Insert | StageGates | BEFORE | INSERT | 1/2 | pure-check | Only a gate of a release made from a template can come from a template | immutability / append-only |
| 151 | trg_Gate_TemplateLocked_Update | StageGates | BEFORE | UPDATE OF GateClass, TemplateLocked | 1/10 | pure-check | A Compliance gate from the approved template cannot be downgraded or unlocked; request a waiver to skip it | immutability / append-only |
| 152 | trg_Gate_TemplateLocked_Delete | StageGates | BEFORE | DELETE | 1/1 | pure-check | A Compliance gate from the approved template cannot be removed; request a waiver to skip it | immutability / append-only |

### Same-event groups with more than one trigger (SQLite fire order)

| table | timing | event | fire order (first to last) |
|---|---|---|---|
| ApprovalDecisions | BEFORE | UPDATE | trg_ApprovalDecision_Separation → trg_ApprovalDecision_Eligible → trg_ApprovalDecision_Final → trg_ApprovalDecision_ApprovalOpen |
| Approvals | BEFORE | UPDATE | trg_Ws_Approvals_Update → trg_Approval_Final → trg_Approval_ApprovedNeedsAll |
| Clients | BEFORE | INSERT | trg_Ws_Clients_Insert → trg_Client_SegmentRules_Insert |
| Clients | BEFORE | UPDATE | trg_Ws_Clients_Update → trg_Client_SegmentRules_Update |
| Deployments | BEFORE | UPDATE | trg_Ws_Deployments_Update → trg_Deployment_Transition |
| FreezeOverrides | BEFORE | INSERT | trg_Ws_FreezeOverrides_Insert → trg_Override_ApproverRole |
| GateWaivers | BEFORE | UPDATE | trg_Waiver_ApprovedOrDeclined → trg_Waiver_DeclineDistinct → trg_Waiver_DeclineRole → trg_Waiver_ApproverRole |
| ImportJobs | BEFORE | UPDATE | trg_Ws_ImportJobs_Update → trg_Import_NoCommitWithErrors |
| ReleaseClientDates | BEFORE | INSERT | trg_Ws_ReleaseClientDates_Insert → trg_ClientDate_Rules_Insert |
| ReleaseClientDates | BEFORE | UPDATE | trg_Ws_ReleaseClientDates_Update → trg_ClientDate_Rules_Update |
| ReleaseTrains | BEFORE | INSERT | trg_Ws_ReleaseTrains_Insert → trg_Train_TypeRules_Insert |
| ReleaseTrains | BEFORE | UPDATE | trg_Ws_ReleaseTrains_Update → trg_Train_TypeRules_Update → trg_Train_KeyImmutable → trg_Train_ExecutingRequiresRollback → trg_Train_ExecutingRequiresBaseline → trg_Train_ExecutingNoExpiredConditions → trg_Train_ExecutingRequiresGo → trg_Train_GateLockout → trg_Train_Transition |
| ReleaseTypes | BEFORE | UPDATE | trg_Ws_ReleaseTypes_Update → trg_ReleaseType_BetaFlagLocked |
| StageGates | AFTER | UPDATE | trg_Gate_CertifyLocksEvidence → trg_Gate_LogTransition → trg_Gate_CertifyCapturesBaseline |
| StageGates | BEFORE | INSERT | trg_Gate_TemplateLocked_Insert → trg_Ws_StageGates_Insert |
| StageGates | BEFORE | UPDATE | trg_Gate_TemplateLocked_Update → trg_Ws_StageGates_Update → trg_Gate_WaiveRequiresApproval → trg_Gate_CertifierNotTaskAuthor → trg_Gate_ComplianceCertifierRole → trg_Gate_CertifyInOrder → trg_Gate_ComplianceNeedsTask → trg_Gate_CertifyRequiresTasks → trg_Gate_RequireActor → trg_Gate_Transition |
| StepExecutions | BEFORE | UPDATE | trg_Ws_StepExecutions_Update → trg_Step_RunPaused → trg_Step_FreezeLockout → trg_Step_DependenciesDone |
| TrainTemplates | BEFORE | INSERT | trg_Template_ApproverNotAuthor_Insert → trg_Ws_TrainTemplates_Insert |
| TrainTemplates | BEFORE | UPDATE | trg_Template_ApproverNotAuthor_Update → trg_Ws_TrainTemplates_Update |

### Order divergence: SQLite newest-first versus Postgres alphabetical (byte order)

| table | timing | event | triggers | pairs | pairs in a different order on Postgres |
|---|---|---|---|---|---|
| ApprovalDecisions | BEFORE | UPDATE | 4 | 6 | 5 |
| Approvals | BEFORE | UPDATE | 3 | 3 | 3 |
| Clients | BEFORE | INSERT | 2 | 1 | 1 |
| Clients | BEFORE | UPDATE | 2 | 1 | 1 |
| Deployments | BEFORE | UPDATE | 2 | 1 | 1 |
| FreezeOverrides | BEFORE | INSERT | 2 | 1 | 1 |
| GateWaivers | BEFORE | UPDATE | 4 | 6 | 2 |
| ImportJobs | BEFORE | UPDATE | 2 | 1 | 1 |
| ReleaseClientDates | BEFORE | INSERT | 2 | 1 | 1 |
| ReleaseClientDates | BEFORE | UPDATE | 2 | 1 | 1 |
| ReleaseTrains | BEFORE | INSERT | 2 | 1 | 1 |
| ReleaseTrains | BEFORE | UPDATE | 9 | 36 | 24 |
| ReleaseTypes | BEFORE | UPDATE | 2 | 1 | 1 |
| StageGates | AFTER | UPDATE | 3 | 3 | 2 |
| StageGates | BEFORE | INSERT | 2 | 1 | 0 |
| StageGates | BEFORE | UPDATE | 10 | 45 | 24 |
| StepExecutions | BEFORE | UPDATE | 4 | 6 | 6 |
| TrainTemplates | BEFORE | INSERT | 2 | 1 | 0 |
| TrainTemplates | BEFORE | UPDATE | 2 | 1 | 0 |
| **total** | | | | **117** | **75** |

Inverted pairs (first fires before second on SQLite, after it on Postgres):

- ApprovalDecisions BEFORE UPDATE: `trg_ApprovalDecision_Final` before `trg_ApprovalDecision_ApprovalOpen`
- ApprovalDecisions BEFORE UPDATE: `trg_ApprovalDecision_Eligible` before `trg_ApprovalDecision_ApprovalOpen`
- ApprovalDecisions BEFORE UPDATE: `trg_ApprovalDecision_Separation` before `trg_ApprovalDecision_ApprovalOpen`
- ApprovalDecisions BEFORE UPDATE: `trg_ApprovalDecision_Separation` before `trg_ApprovalDecision_Final`
- ApprovalDecisions BEFORE UPDATE: `trg_ApprovalDecision_Separation` before `trg_ApprovalDecision_Eligible`
- Approvals BEFORE UPDATE: `trg_Approval_Final` before `trg_Approval_ApprovedNeedsAll`
- Approvals BEFORE UPDATE: `trg_Ws_Approvals_Update` before `trg_Approval_ApprovedNeedsAll`
- Approvals BEFORE UPDATE: `trg_Ws_Approvals_Update` before `trg_Approval_Final`
- Clients BEFORE INSERT: `trg_Ws_Clients_Insert` before `trg_Client_SegmentRules_Insert`
- Clients BEFORE UPDATE: `trg_Ws_Clients_Update` before `trg_Client_SegmentRules_Update`
- Deployments BEFORE UPDATE: `trg_Ws_Deployments_Update` before `trg_Deployment_Transition`
- FreezeOverrides BEFORE INSERT: `trg_Ws_FreezeOverrides_Insert` before `trg_Override_ApproverRole`
- GateWaivers BEFORE UPDATE: `trg_Waiver_DeclineRole` before `trg_Waiver_ApproverRole`
- GateWaivers BEFORE UPDATE: `trg_Waiver_DeclineDistinct` before `trg_Waiver_ApproverRole`
- ImportJobs BEFORE UPDATE: `trg_Ws_ImportJobs_Update` before `trg_Import_NoCommitWithErrors`
- ReleaseClientDates BEFORE INSERT: `trg_Ws_ReleaseClientDates_Insert` before `trg_ClientDate_Rules_Insert`
- ReleaseClientDates BEFORE UPDATE: `trg_Ws_ReleaseClientDates_Update` before `trg_ClientDate_Rules_Update`
- ReleaseTrains BEFORE INSERT: `trg_Ws_ReleaseTrains_Insert` before `trg_Train_TypeRules_Insert`
- ReleaseTrains BEFORE UPDATE: `trg_Train_TypeRules_Update` before `trg_Train_Transition`
- ReleaseTrains BEFORE UPDATE: `trg_Ws_ReleaseTrains_Update` before `trg_Train_Transition`
- ReleaseTrains BEFORE UPDATE: `trg_Train_KeyImmutable` before `trg_Train_GateLockout`
- ReleaseTrains BEFORE UPDATE: `trg_Train_TypeRules_Update` before `trg_Train_GateLockout`
- ReleaseTrains BEFORE UPDATE: `trg_Ws_ReleaseTrains_Update` before `trg_Train_GateLockout`
- ReleaseTrains BEFORE UPDATE: `trg_Train_ExecutingRequiresRollback` before `trg_Train_ExecutingRequiresGo`
- ReleaseTrains BEFORE UPDATE: `trg_Train_KeyImmutable` before `trg_Train_ExecutingRequiresGo`
- ReleaseTrains BEFORE UPDATE: `trg_Train_TypeRules_Update` before `trg_Train_ExecutingRequiresGo`
- ReleaseTrains BEFORE UPDATE: `trg_Ws_ReleaseTrains_Update` before `trg_Train_ExecutingRequiresGo`
- ReleaseTrains BEFORE UPDATE: `trg_Train_ExecutingRequiresBaseline` before `trg_Train_ExecutingNoExpiredConditions`
- ReleaseTrains BEFORE UPDATE: `trg_Train_ExecutingRequiresRollback` before `trg_Train_ExecutingNoExpiredConditions`
- ReleaseTrains BEFORE UPDATE: `trg_Train_KeyImmutable` before `trg_Train_ExecutingNoExpiredConditions`
- ReleaseTrains BEFORE UPDATE: `trg_Train_TypeRules_Update` before `trg_Train_ExecutingNoExpiredConditions`
- ReleaseTrains BEFORE UPDATE: `trg_Ws_ReleaseTrains_Update` before `trg_Train_ExecutingNoExpiredConditions`
- ReleaseTrains BEFORE UPDATE: `trg_Train_ExecutingRequiresRollback` before `trg_Train_ExecutingRequiresBaseline`
- ReleaseTrains BEFORE UPDATE: `trg_Train_KeyImmutable` before `trg_Train_ExecutingRequiresBaseline`
- ReleaseTrains BEFORE UPDATE: `trg_Train_TypeRules_Update` before `trg_Train_ExecutingRequiresBaseline`
- ReleaseTrains BEFORE UPDATE: `trg_Ws_ReleaseTrains_Update` before `trg_Train_ExecutingRequiresBaseline`
- ReleaseTrains BEFORE UPDATE: `trg_Train_KeyImmutable` before `trg_Train_ExecutingRequiresRollback`
- ReleaseTrains BEFORE UPDATE: `trg_Train_TypeRules_Update` before `trg_Train_ExecutingRequiresRollback`
- ReleaseTrains BEFORE UPDATE: `trg_Ws_ReleaseTrains_Update` before `trg_Train_ExecutingRequiresRollback`
- ReleaseTrains BEFORE UPDATE: `trg_Train_TypeRules_Update` before `trg_Train_KeyImmutable`
- ReleaseTrains BEFORE UPDATE: `trg_Ws_ReleaseTrains_Update` before `trg_Train_KeyImmutable`
- ReleaseTrains BEFORE UPDATE: `trg_Ws_ReleaseTrains_Update` before `trg_Train_TypeRules_Update`
- ReleaseTypes BEFORE UPDATE: `trg_Ws_ReleaseTypes_Update` before `trg_ReleaseType_BetaFlagLocked`
- StageGates AFTER UPDATE: `trg_Gate_LogTransition` before `trg_Gate_CertifyCapturesBaseline`
- StageGates AFTER UPDATE: `trg_Gate_CertifyLocksEvidence` before `trg_Gate_CertifyCapturesBaseline`
- StageGates BEFORE UPDATE: `trg_Gate_WaiveRequiresApproval` before `trg_Gate_Transition`
- StageGates BEFORE UPDATE: `trg_Ws_StageGates_Update` before `trg_Gate_Transition`
- StageGates BEFORE UPDATE: `trg_Gate_WaiveRequiresApproval` before `trg_Gate_RequireActor`
- StageGates BEFORE UPDATE: `trg_Ws_StageGates_Update` before `trg_Gate_RequireActor`
- StageGates BEFORE UPDATE: `trg_Gate_TemplateLocked_Update` before `trg_Gate_RequireActor`
- StageGates BEFORE UPDATE: `trg_Gate_ComplianceNeedsTask` before `trg_Gate_CertifyRequiresTasks`
- StageGates BEFORE UPDATE: `trg_Gate_ComplianceCertifierRole` before `trg_Gate_CertifyRequiresTasks`
- StageGates BEFORE UPDATE: `trg_Gate_WaiveRequiresApproval` before `trg_Gate_CertifyRequiresTasks`
- StageGates BEFORE UPDATE: `trg_Ws_StageGates_Update` before `trg_Gate_CertifyRequiresTasks`
- StageGates BEFORE UPDATE: `trg_Gate_TemplateLocked_Update` before `trg_Gate_CertifyRequiresTasks`
- StageGates BEFORE UPDATE: `trg_Gate_WaiveRequiresApproval` before `trg_Gate_ComplianceNeedsTask`
- StageGates BEFORE UPDATE: `trg_Ws_StageGates_Update` before `trg_Gate_ComplianceNeedsTask`
- StageGates BEFORE UPDATE: `trg_Gate_TemplateLocked_Update` before `trg_Gate_ComplianceNeedsTask`
- StageGates BEFORE UPDATE: `trg_Gate_ComplianceCertifierRole` before `trg_Gate_CertifyInOrder`
- StageGates BEFORE UPDATE: `trg_Gate_WaiveRequiresApproval` before `trg_Gate_CertifyInOrder`
- StageGates BEFORE UPDATE: `trg_Ws_StageGates_Update` before `trg_Gate_CertifyInOrder`
- StageGates BEFORE UPDATE: `trg_Gate_TemplateLocked_Update` before `trg_Gate_CertifyInOrder`
- StageGates BEFORE UPDATE: `trg_Gate_WaiveRequiresApproval` before `trg_Gate_ComplianceCertifierRole`
- StageGates BEFORE UPDATE: `trg_Ws_StageGates_Update` before `trg_Gate_ComplianceCertifierRole`
- StageGates BEFORE UPDATE: `trg_Gate_TemplateLocked_Update` before `trg_Gate_ComplianceCertifierRole`
- StageGates BEFORE UPDATE: `trg_Gate_WaiveRequiresApproval` before `trg_Gate_CertifierNotTaskAuthor`
- StageGates BEFORE UPDATE: `trg_Ws_StageGates_Update` before `trg_Gate_CertifierNotTaskAuthor`
- StageGates BEFORE UPDATE: `trg_Gate_TemplateLocked_Update` before `trg_Gate_CertifierNotTaskAuthor`
- StageGates BEFORE UPDATE: `trg_Ws_StageGates_Update` before `trg_Gate_WaiveRequiresApproval`
- StepExecutions BEFORE UPDATE: `trg_Step_FreezeLockout` before `trg_Step_DependenciesDone`
- StepExecutions BEFORE UPDATE: `trg_Step_RunPaused` before `trg_Step_DependenciesDone`
- StepExecutions BEFORE UPDATE: `trg_Ws_StepExecutions_Update` before `trg_Step_DependenciesDone`
- StepExecutions BEFORE UPDATE: `trg_Step_RunPaused` before `trg_Step_FreezeLockout`
- StepExecutions BEFORE UPDATE: `trg_Ws_StepExecutions_Update` before `trg_Step_FreezeLockout`
- StepExecutions BEFORE UPDATE: `trg_Ws_StepExecutions_Update` before `trg_Step_RunPaused`

<!-- inventory:end -->

## Appendix B. What I ran (reproducible)

Environment: PostgreSQL 16.15 (Ubuntu package), SQLite 3.53.1 via Python 3.13, throw-away cluster in the session scratch directory (`initdb`, `pg_ctl`, TCP on localhost; not part of the repository). `test_schema.py`: `python3 tests/reference/test_schema.py` ended **493 passed, 0 failed**. `python3 tools/schema_inventory.py --check` ended `OK {'tables': 72, 'triggers': 152, 'indexes': 41}`.

| Id | Experiment | Result |
|---|---|---|
| E2 | Two BEFORE UPDATE triggers named `trg_a_lower` and `trg_B_Upper` | `trg_B_Upper` fired first: byte order |
| E3 | `CREATE TRIGGER … WHEN (EXISTS (SELECT 1 FROM …))` | `ERROR: cannot use subquery in trigger WHEN condition` |
| E4 | `BEFORE UPDATE OF "Actor"`; update `Status` only, then `SET "Actor" = "Actor"` | Not fired, then fired (SET list decides, not a changed value) |
| E1b | Stub triggers named `trg_Train_Transition` and `trg_Train_TypeRules_Update` with the real messages; one UPDATE breaks both | Postgres: "Illegal train status transition". Real `db/schema.sql` on SQLite for the same statement: "A release cannot lose its release type" |
| CHK | One UPDATE breaks `CHECK ((CurrentStatus='Complete') = (CloseCode IS NOT NULL))` and a BEFORE trigger | Both engines: the trigger message wins |
| E5/E13 | Dispatcher raising in code order with `ERRCODE 'P0001'` | First failing check reported; SQLSTATE and message readable in a handler |
| E6/E7 | `CREATE CONSTRAINT TRIGGER … BEFORE` | Syntax error: constraint triggers are AFTER only |
| E14 | AFTER trigger on table `a` updates table `b` that has a BEFORE trigger | `b`'s BEFORE trigger fired (nesting works) |
| E8 | `SELECT Id FROM StageGates` against quoted PascalCase tables | `relation "stagegates" does not exist` |
| E9 | `citext` UNIQUE with `'A@x.com'` then `'a@X.com'` | Second insert rejected |
| E10 | `'abc' GLOB 'a*'` | Syntax error: no `GLOB` |
| E11 | `json_build_object(...)::text`, `jsonb_build_object(...)::text`, `to_char(... 'YYYY-MM-DD"T"HH24:MI:SS"Z"')` | `{"b" : 1, "a" : "x"}`; `{"a": "x", "b": 1}`; ISO shape matches |
| E12 | Partial unique expression index like `UX_Trains_VersionSlot`, two equal rows | Second rejected |
| E15 | `0.1::real` read back as double | `0.10000000149011612` |

Not run: concurrency (4.8), sealer behaviour (4.9), `UseSqlReturningClause` with Npgsql (4.6), `citext` against non-ASCII, collation other than `C`, RLS with trigger lookups, and anything in CI.
