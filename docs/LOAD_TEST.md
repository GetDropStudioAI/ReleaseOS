# Load test (REOS-52)

Acceptance (`PROJECT_SCOPE` non-functional table, M8): **50 concurrent users, 200 trains, 100 000 audit rows, p95 API latency under 300 ms.**
Result on the machine below: **met, after fixing two defects the test found** (details and before/after numbers in section 4). These are measurements from a shared development container, **not** the production host; re-run on the target hardware before relying on them (section 1).

## 1. How to run it

```
python3 tools/run_load_test.py                      # both profiles, 60 s measured each, about 4 minutes in all
python3 tools/run_load_test.py --profile stress     # one profile
python3 tools/run_load_test.py --duration 30 --users 50 --trains 200 --audit-rows 100000
dotnet run -c Release --project tests/ReleaseMgmt.LoadTests     # one run, configured by LOAD_* environment variables (see LoadRunner.cs)
```

It needs the .NET 10 SDK and Python 3.9+, uses only loopback and needs no network at run time. It is **not** part of `dotnet test` or CI: `tests/ReleaseMgmt.LoadTests` is an executable project (in the solution, so it always compiles), not an xUnit project. The exit code is 0 only when every profile passes (section 3). Reports are written to `tests/ReleaseMgmt.LoadTests/test-results/` (git-ignored) as markdown, one per profile, plus NBomber's own report folder.

## 2. What it does

- **Server:** the built `ReleaseMgmt.Api.dll` as its **own process** (Development environment, real Kestrel on a loopback port, temporary SQLite database in WAL mode on local disk, production defaults for every path). A separate process over real sockets was chosen over `WebApplicationFactory`'s in-memory `TestServer` because the in-memory handler skips Kestrel, the socket and HTTP parsing, and shares the load generator's garbage collector; the numbers would flatter the server.
- **Load generator:** NBomber 6.6 (scheduler and console report) in a second process on the same machine, so generator and server compete for the same cores. NBomber only schedules 50 constant copies; every request is also timed and classified by the test itself so percentiles are exact (nearest rank over every request). See Q-052a for the NBomber licence question.
- **Users:** 50, each signed in through the Development `POST /auth/dev-login` with its own HTTP client and cookie: 10 RTE, 5 Release Manager, 5 Governance Officer, 30 Viewer.
- **Data**, inserted by SQL in one transaction (about 3 s): 200 trains (40% Planning, 20% Gated, 20% Executing, 12% Complete, 8% Aborted), 1 000 gates, 8 000 checklist tasks, 799 products, 40 open blockers, 2 100 runbook steps (30 per runbook, on the Executing trains and a subset of the others), 40 Live runs and Rehearsal history, 2 000 notifications, and **100 000 audit rows** spread over 120 days with 12 entity types. No `ANALYZE` is run (the application never runs one).
- **Mix** (one action per iteration, chosen by weight; every HTTP call is a separate measured request): 14% Stream (`GET /trains` and `/freeze-windows/ahead`), 22% open a train (detail, readiness, products, window, gate), 12% tick and untick a task (gate read, `:complete`, `:reopen`, each with the `If-Match` Version just read), 3% five deliberately shared tasks (stale Versions give 409), 8% live-run reads (run, forecast, steps), 6% live step start and done (one-shot pool of 1 080 steps on Executing trains), 8% audit viewer (newest page plus keyset paging, filters by train, entity type, actor, action, date range and entity id, entity-type list), 8% analytics M1 to M15, 5% calendar month, 8% My work, 6% inbox. Only the 20 users who may can open the audit viewer or start steps; the rest do something else.
- **While the load runs:** three to four online backups of the live database (`BackupRunner.Backup`, which runs `integrity_check` on the copy and throws unless it says `ok`).
- **Profiles:** `stress` has no think time, so each user fires the next action as soon as the last returned (closed loop: latency is users divided by throughput, and the run measures saturation). `typical` has 1 to 3 s between actions, the pace of a person at the screen.
- **Before the clock starts:** a pre-flight pass over every read endpoint (fails the run on any non-2xx) that also records **unloaded** latency (one user, ten requests each), and a 10 s warm-up that is not measured.

## 3. Pass criteria

The run fails if overall p95 is 300 ms or more, **or any single read request's p95** is, **or** there is any unexpected error (non-2xx; a 409 on the five shared tasks is expected and counted apart), **or** any SQLite busy/locked message appears in a response, the server log or the server output. Write requests are measured and reported separately and marked against the same budget, but do not gate; in the recorded runs every write is under budget anyway.

## 4. Results

Machine: 4 logical cores (Intel Xeon @ 2.80 GHz, virtualised), about 10 GB RAM available to the process (16 GB in the container), Ubuntu 24.04, .NET 10.0.12, ext4 on a virtual disk. Server and generator share those 4 cores. 2026-09-30.

### 4.1 Final code, both profiles (`python3 tools/run_load_test.py`, 60 s measured, 50 users)

| Profile | Requests | Throughput | p50 ms | p95 ms | p99 ms | max ms | Errors | Expected 409 | SQLite busy/locked |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `stress` overall | 52 158 | 869 req/s | 51.3 | **104.3** | 137.3 | 620.1 | 0 | 419 | 0 |
| `stress` reads | 46 049 | | 50.8 | 101.5 | 134.0 | 296.9 | 0 | 0 | |
| `stress` writes | 6 109 | | 55.1 | 117.6 | 172.6 | 620.1 | 0 | 419 | |
| `typical` overall | 4 039 | 67 req/s | 3.0 | **13.5** | 21.5 | 136.3 | 0 | 3 | 0 |
| `typical` reads | 3 558 | | 2.8 | 13.7 | 21.5 | 39.3 | 0 | 0 | |
| `typical` writes | 481 | | 4.0 | 10.7 | 29.0 | 136.3 | 0 | 3 | |

Both profiles **passed**: p95 104 ms (stress) and 14 ms (typical), against 300 ms. Server CPU during `stress`: 252% of one core (63% of the machine), generator 17%, server working set 293 MB. Three online backups during the run took 0.5 to 0.6 s each for 43.5 MB and all passed `integrity_check`. 334 live step start+done pairs completed.

**Run-to-run variation is real.** An earlier `stress` run of the same code (same seed) gave 1 069 req/s, overall p95 76.5 ms, reads p95 74.8, writes p95 88.6; the final one above gave 869 req/s and 104.3. The container is shared, so treat about 75 to 105 ms as the range for this profile on this machine. All recorded runs of the final code are under 300 ms with wide margin.

Slowest requests under `stress` (p95 ms; the full table for every request is in the generated report):

| Request | Kind | Count | p50 | p95 | p99 | max | Errors | Expected 409 |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| Live step `:start` | write | 334 | 58.3 | 137.9 | 225.4 | 620.1 | 0 | 0 |
| Calendar month range | read | 955 | 70.4 | 134.9 | 164.9 | 238.3 | 0 | 0 |
| Stream `GET /trains` | read | 2 717 | 67.9 | 127.9 | 171.2 | 296.9 | 0 | 0 |
| Shared task `:complete` (contended) | write | 316 | 51.5 | 126.0 | 197.5 | 386.9 | 0 | 213 |
| Live step `:done` | write | 334 | 54.3 | 125.2 | 165.3 | 186.2 | 0 | 0 |
| Task `:reopen` | write | 2 409 | 55.5 | 116.1 | 171.8 | 407.1 | 0 | 0 |
| Audit filter by train | read | 82 | 64.7 | 115.0 | 170.5 | 170.5 | 0 | 0 |
| Task `:complete` | write | 2 409 | 54.8 | 114.8 | 162.5 | 444.6 | 0 | 0 |
| Analytics M11 | read | 153 | 53.9 | 114.5 | 136.0 | 158.5 | 0 | 0 |
| My work `GET /me/work` | read | 1 504 | 58.5 | 112.8 | 152.5 | 181.4 | 0 | 0 |

Under `stress` a trivial request such as `GET /me/notifications/count` has p50 48 ms only because 50 users are waiting on about 870 requests per second (50 / 870 = 57 ms mean): that is queueing, not work. **Unloaded** (one user) every read is 1 to 15 ms except `GET /trains` (35 ms, 200 trains in one payload) and the calendar month (14 ms); see the "Unloaded latency" table in each report. Requests with fewer than 100 samples (the individual audit filters and analytics metrics, and almost everything in `typical`) have a coarse p95: indicative, but the gates over all requests and all reads rest on tens of thousands of samples.

**SQLite behaviour under concurrency:** zero `database is locked` / `SQLITE_BUSY` occurrences in every run at 50 users (6 109 writes in the `stress` run alongside about 46 000 reads and three online backups) with `busy_timeout=5000`, zero server errors. 419 conflicts (`If-Match` mismatch) on the five shared tasks came back as clean 409s with the current row.

### 4.2 Before and after the fixes (`stress`, 50 users, same seed)

| | Requests/s | Overall p50 | Overall p95 | Reads p95 | `GET /trains` p50 / p95 | Audit "entity type" filter p95 | Verdict |
|---|---:|---:|---:|---:|---|---:|---|
| Original code | 182 | 111 ms | **1 504 ms** | 1 563 ms | 2 054 / 3 214 ms | 1 370 ms | **fail** (32 read requests over budget) |
| After fix 1 (Stream) | 636 | 71.5 ms | 137.8 ms | 133.1 ms | 99.7 / 174 ms | 440 ms | fail on one request |
| After fix 2 (audit filter), first run | 1 069 | 43.0 ms | 76.5 ms | 74.8 ms | 56.5 / 96.7 ms | not recorded | pass |
| Final code, `tools/run_load_test.py` | 869 | 51.3 ms | 104.3 ms | 101.5 ms | 67.9 / 127.9 ms | 108.0 ms | pass |

1. **`GET /trains` asked the readiness service once per train** (about six queries each). With 200 trains a single, unloaded request took 227 ms, and with 50 users it consumed most of the server. Fixed in code, no index: `TrainLifecycleService.NextHopBlockerCountsAsync` counts every train's next-hop blockers with five set-based queries (unloaded: 227 ms to 35 ms). `TrainBlockerCountsTests` asserts the counts equal what `EvaluateAsync` (the guards `:advance` runs) reports for 340 combinations of status, risk, gate, Go/No-Go, condition, baseline and rollback state.
2. **The audit viewer's entity-type filter** (`?entity=ChecklistTask`, a value a third of the rows share) was planned by SQLite as "use `IX_Audit_Entity`, then sort every match by Id": 84 to 98 ms for one request at 100 000 rows, growing with the log. `EXPLAIN QUERY PLAN` showed it. `ANALYZE` does not change the plan in the SQLite build we ship (tried, and abandoned). Fixed in code: without an entity id the filter is written as `EntityType || ''`, so the planner walks the primary key downwards and stops after one page (unloaded: 98 ms to 4 ms). `AuditQueryPlanTests` asserts the plan for the SQL EF really sends, and that with an entity id the index is still used.
3. **No schema change and no new index.** `db/schema.sql` is untouched (20 indexes). An index `AuditEvents(EntityType, Id)` would serve the entity filter at any selectivity; it is recorded as an open suggestion in Q-052b, not applied.

### 4.3 Headroom (informational, not part of the acceptance; one run each, zero think time)

| Users | Requests/s | Overall p95 | Reads p95 | Writes p95 | Server CPU (of 4 cores) | Errors | Verdict |
|---:|---:|---:|---:|---:|---|---:|---|
| 50 (final) | 869 | 104 ms | 102 ms | 118 ms | 63% | 0 | pass |
| 100 | 590 | 262 ms | 260 ms | 293 ms | 43% | 0 | fails on single reads (Calendar 353 ms, Stream 310 ms) |
| 200 | 554 | 556 ms | 551 ms | 586 ms | 42% | 0 | fail |

With no think time, 100 to 200 simultaneous users is a load no team of that size produces (100 real users click about once every few seconds, which is what `typical` models: 67 requests per second for 50 users). Throughput **falls** as concurrency rises past 50 while the server uses well under half of the machine, which means requests are waiting rather than computing. Likely causes, **not investigated further** because the acceptance is met: EF Core's SQLite provider runs each command synchronously on a thread-pool thread, so 100+ concurrent requests exhaust the pool's ready threads and it injects new ones slowly; and every request opens a connection and re-runs three PRAGMAs. If the pilot grows well beyond 50 active users, start there.

## 5. What this does not show

- A production host: no separate client machine, no reverse proxy or TLS, no antivirus scanning the database file (a real risk on Windows Server), no network storage (D3 forbids it), a different core count and disk.
- Login through the identity provider (dev-login is used), SignalR fan-out (not exercised; the live push is a notification and a refetch), PDF/XLSX export jobs, Jira/ServiceNow polling and comm dispatch running in the background (they were idle: no connector is configured).
- Long runs. Each profile is 60 s measured after a 10 s warm-up. A soak test over hours (memory growth, WAL checkpoint starvation while a reader holds a snapshot) has not been run.
- Data growth: 100 000 audit rows and 200 trains are the stated scale. The audit table grows without bound; at ten times that, re-run with `--audit-rows 1000000` and look at the audit filters and the CSV export first.
- The analytics queries here run on synthetic history (mostly certified gates and few transitions). Their unloaded cost is 2 to 6 ms, which says the queries are index-friendly, not that real history will look the same.
- Write requests are measured against the same 300 ms budget for reporting only.
