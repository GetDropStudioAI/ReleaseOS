# Disaster-recovery drill (REOS-52)

Acceptance: **restore from backup in under 30 minutes (RPO 15 minutes), including the Data Protection key ring.** This replaces the waiver of the second-machine restore in REOS-14 (`docs/REGRESSION.md`, Sprint 1) with an automated, timed drill.

Result on the machine below: **restore from total loss to a healthy API in 3.6 s for a 43 MB database with 60 MB of attachments, and 13.5 s for a 360 MB database with 1 GB of attachments**, every check passing. Read section 5 (what was not tested) before treating that as a promise about production: no second machine, no multi-GB database, warm disk cache.

The drill also **found a defect** that the earlier restore tests could not: the API could not start on a restored database (section 4).

## 1. How to run it

```
python3 tools/dr_drill.py                                            # production scale: 200 trains, 100 000 audit rows, 60 x 1 MB attachments (about 40 s with the live-backup test)
python3 tools/dr_drill.py --scale small                              # the CI-sized instance (seconds)
python3 tools/dr_drill.py --audit-rows 1000000 --attach-count 200 --attach-kb 5120    # a bigger instance, to see how timings grow
dotnet test tests/ReleaseMgmt.DrDrill.Tests                          # what CI runs: the small drill plus the live-backup consistency test
```

Why .NET tests behind a Python front end (Q-052d): the drill needs the real credential store, the real `BackupService` and the built API; a Python script would have to re-implement all three. `tools/dr_drill.py` builds, sets the scale, runs the tests, prints the timing tables and returns non-zero on any failure. The small drill is part of the normal `dotnet test` run (about 10 s, run by CI on the Windows and macOS runners); it adds no CI step.

Everything happens in a scratch directory under the system temp folder; no network is used. The API under test is the shipped `ReleaseMgmt.Api.dll` started as a child process with the instance root as its **working directory**, so every path (`data/releasemgmt.db`, `data/keys`, `data/secrets`, `data/attachments`) is the production default.

## 2. The procedure the drill executes (this is the runbook's restore section)

Backup side (before the loss):

1. `BackupService` (hosted in the API) writes `data/backups/releasemgmt-<UTC>.db` every 15 minutes through the SQLite online backup API, runs `integrity_check` on the copy, and keeps one `releasemgmt-nightly-<UTC>.db` per day for 30 days (frequent ones 2 days). The copy is a single self-contained file, not in WAL mode. **The service backs up the database only.**
2. Copy off the machine, in this order: the newest backup file, **then** the attachments directory (files are only ever added, so a directory copied after the database backup contains every file the backup references), the credentials directory and the **key ring**. The drill copies `data/keys`, `data/secrets` and `data/attachments`.

Restore side (on a fresh host, working directory = instance root, `R`):

1. `dotnet ReleaseMgmt.Api.dll restore <off-box>/releasemgmt-<UTC>.db data/releasemgmt.db`. Refuses a backup that fails `integrity_check`, removes stale `-wal`/`-shm`, copies, switches the file to WAL, prints `integrity_check: ok`.
2. Copy `keys` back to `R/data/keys`, `secrets` to `R/data/secrets`, `attachments` to `R/data/attachments` (or wherever `DataProtection:KeysDirectory`, `Sync:Credentials:Directory` and `Attachments:Directory` point).
3. Start the API and wait for `GET /healthz` to answer `Healthy`.

## 3. Results

Machine: 4 logical cores (Intel Xeon @ 2.80 GHz, virtualised), 16 GB RAM, Ubuntu 24.04, ext4 on a virtual disk, .NET 10.0.12. Timings are wall-clock from `Stopwatch` (monotonic). All from one run each on 2026-09-30; the copies come from the operating system's page cache (nothing evicted it), so they are faster than a cold disk or a network share would be.

### 3.1 Production scale: 200 trains, 100 000 audit rows, 60 attachments of 1 MB

Database 42.6 MB (49 tables, 42 triggers, 116 235 rows), attachments 60 MB in 60 files, key ring 1 file, credentials 2 files.

| Restore step (after total loss) | Seconds | Detail |
|---|---:|---|
| 1. `ReleaseMgmt.Api restore` | 0.64 | copy, `integrity_check`, WAL switch |
| 2. Copy key ring back | 0.00 | 1 file |
| 3. Copy credentials back | 0.00 | 2 files |
| 4. Copy attachments back | 0.04 | 60 files, 60 MB |
| 5. Start the API, wait for `/healthz` | 2.91 | process start, migrations check, hosted services |
| **Total, loss to healthy** | **3.59** | budget 1 800 s |

Backup side (not counted): online backup of the live database 0.74 s; copying the three directories 0.04 s. Verification (not counted): database checks 0.71 s, API checks 0.67 s, credential and key-ring checks 0.40 s, attachment hashes 0.19 s.

### 3.2 Larger instance: 1 000 000 audit rows, 200 attachments of 5 MB (to see how it scales)

Database 360.2 MB (1 016 515 rows), attachments 1 000 MB in 200 files.

| Restore step | Seconds |
|---|---:|
| 1. `ReleaseMgmt.Api restore` | 7.60 |
| 2. + 3. key ring and credentials | 0.00 |
| 4. Copy attachments back | 2.90 |
| 5. Start the API, wait for `/healthz` | 3.03 |
| **Total** | **13.54** |

Backup side: online backup of the live database 11.75 s (this one competes with nothing here; it is the number to compare with the 15-minute interval), attachments copy 5.97 s.

### 3.3 CI-sized instance (what every `dotnet test` runs): 20 trains, 5 000 audit rows, 6 attachments of 64 KB

Total restore 2.9 s; the whole test takes about 8 to 10 s because it starts the API twice (populate, restored boot) and the restore CLI once.

### 3.4 What the drill verifies (all pass at every scale)

| Check | How |
|---|---|
| Backup equals the live database it was taken from | row counts of every table, trigger set and a SHA-256 over every audit row, live file vs backup file |
| Total loss is real | the whole instance directory (database, WAL, key ring, credentials, attachments, logs, local backups) is deleted; only the off-box copy remains |
| The restore CLI is healthy | prints `integrity_check: ok` |
| SQLite `integrity_check` on the restored file | `ok` |
| **Trigger set equals `db/schema.sql`** | 42 names parsed from `schema.sql` equal the 42 in the restored database, none missing or extra |
| Row counts | all 49 tables equal the pre-loss counts (before the API starts and writes anything) |
| **Audit trail intact** | count, highest Id and a SHA-256 over every audit row identical; an `UPDATE` on the first audit row still aborts with `AuditEvents is append-only` |
| `/healthz` | `Healthy`, `db: ok` |
| The API serves the data | `GET /trains` returns the same number of trains as before the loss |
| **Credentials decrypt with the restored key ring** | `DataProtectionCredentialStore` (the API's own class) reads back the exact Jira token and ServiceNow secret; and the running restored API lists both connectors with their credential kind (a kind is listed only when the stored file decrypts) |
| **Without the key ring they do not** | same secrets directory, brand-new empty key ring: `GetAsync` throws `ConnectorException` "The stored Jira credentials cannot be read (the Data Protection key ring changed); enter them again"; the admin listing shows no credentials (status `NoCredentials`), and the poller would report `AuthFailed` until they are entered again |
| Secrets are encrypted at rest and never in SQLite | the plaintext secrets appear neither in `Jira.cred` nor anywhere in the database file |
| Attachments | SHA-256 and size of every restored file equals the value stored in `Attachments` |
| ICS feed token | a token issued before the loss still serves `/ics/{token}.ics` (only its hash is stored, in the database) |
| Time | total restore under 1 800 s (asserted) |

### 3.5 Backup of a live WAL database while writes happen (`LiveBackupConsistencyTests`)

Eight tasks are ticked and unticked in a loop through the real `TaskService` (each write changes the task row and adds one audit row in one transaction) while `BackupRunner.Backup` runs back to back on the same WAL database. A 15 s run took **132 backups during 3 097 concurrent writes**. Every backup passed `integrity_check`, was a single file with no `-wal` beside it and journal mode `delete`, and satisfied an invariant that a torn snapshot would break: for every task, `Version = 1 + its audit rows` and `Complete rows - Reopen rows = IsCompleted`. The audit row count grew from 0 to 3 081 across the backups, so the writers really ran between them. A failing writer (for example an `SQLITE_BUSY` surfacing as an exception) fails the test. CI runs the same test for 3 s.

## 4. Defect found and fixed: the API could not start on a restored database

The first drill run failed at "start the API": `SQLite Error 8: 'attempt to write a readonly database'` in `SqliteConnectionInterceptor` during `Database.MigrateAsync()`. Cause: `BackupRunner.Backup` deliberately writes the copy as one self-contained file (default journal mode, not WAL). EF Core's SQLite `Migrate()` first calls `Exists()`, which opens a **read-only** connection; the interceptor then ran `PRAGMA journal_mode=WAL` on it, which needs a write, and the process died. `BackupTests` (REOS-14) checked the restored file with plain SQLite and never started the application against it, which is why the waived second-machine restore would have found this on the day of a real disaster.

Fixed in two places, with regression tests (`RestoredDatabaseStartupTests`): `BackupRunner.Restore` switches the restored file to WAL and fails loudly if it cannot; and `SqliteConnectionInterceptor` skips `journal_mode` on read-only connections, so a backup copied into place by hand (not through the CLI) also starts, the first read-write connection making the switch. No schema change.

## 5. What was not tested, and how far to trust the numbers

- **A second physical machine.** Everything ran on one machine, in a scratch directory, with a deleted directory standing in for a lost host. Not exercised: a different OS user or service account and its file ACLs, a different machine name, a different path layout, Windows Server or macOS (the small drill runs there in CI, but its timings are not recorded here).
- **A multi-GB database.** The largest instance was a 360 MB database with 1 GB of attachments. Extrapolation, **linear from two measured points on this machine and unvalidated beyond them**: the restore CLI took 15 ms per MB at 43 MB and 21 ms per MB at 360 MB (copy plus a full `integrity_check` plus the WAL switch, warm cache); the attachment copy took 2.9 ms per MB warm; the API start was 3 s at both sizes (it does not scale with the data; the first backup it triggers runs in the background and is not on the path to `/healthz`). That gives about 21 s per GB of database and 3 s per GB of attachments with a warm cache. **Add the time to read the bytes from wherever they really are:** about 10 s per GB at 100 MB/s, 20 s per GB at 50 MB/s. Example: a 5 GB database plus 50 GB of attachments restored from a 100 MB/s disk is roughly 105 s + 150 s + 563 s, about 13 minutes; from a 50 MB/s share about 23 minutes (105 s + 150 s + 1 126 s). Under 30 minutes, but not by a wide margin, and ten times the attachments would break it. If the attachment store grows past tens of GB, keep it on the same host as a mirrored copy or restore attachments after the API is healthy (the app raises a `FileMissing` alert for a missing file rather than failing).
- **Cold caches.** Every copy read from the page cache. A cold disk, a network share or object storage will be slower (the model above adds that term).
- **The time to get there.** The 30-minute clock in the story is measured here from an empty host with the files at hand. Provisioning the machine, installing the .NET 10 runtime, fetching the off-box copy and the decision to start are not in it; the operations runbook must budget them separately.
- **RPO.** The 15-minute RPO is the backup interval, taken from the design (`BackupService`), not measured. Attachments uploaded after the last off-box copy are lost while their database rows survive; the app reports each as a missing file.
- **Restoring an older backup into a newer build.** The drill restores a backup taken by the same build. `Migrate()` runs on start, so an older schema is upgraded, but that path is not exercised here.
- **Windows DPAPI or certificate-protected key rings** (Q-052e). The ring is restored as plain files, which is what the code does today.
- **The nightly and pruning behaviour** is covered by `BackupTests`, not by the drill.

## 6. Findings for the operations runbook (M8 `RUNBOOK_OPERATIONS.md`)

1. **Nothing in the application backs up the key ring, the credentials directory or the attachments.** The 15-minute job copies the database only. Those three directories must be copied by the operator's own job. The key ring changes rarely (Data Protection adds a key about every 90 days) and the credentials change when someone enters a token; the attachment store only grows. Copy the database first, then attachments, and copy keys and credentials after any rotation or credential change (or nightly).
2. **The key ring is the credentials.** Without it the connector credentials are unreadable and must be re-entered (the drill proves the error message). With it and the `secrets` directory together, anyone can decrypt them: the ring is plain XML files unless a protector is configured (Q-052e), so keep the off-box copy of `keys` apart from, or encrypted separately from, `secrets`.
3. **Use the CLI to restore, then start the service.** `ReleaseMgmt.Api restore` is the supported path; a hand copy now also works but skips the `integrity_check` refusal.
4. After a restore, expect the pollers to report `AuthFailed` (if the ring was missing) and a `FileMissing` alert for each attachment newer than the last off-box copy.
