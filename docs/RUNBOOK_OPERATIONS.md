# Operations runbook

For the person who installs, runs and restores the Release Management app. Everything here is taken from the code and configuration as built; where a step has **not** been exercised on a real server yet it says so (see "Verified and not verified" at the end).

## 1. What runs, and what must be kept

One process: `ReleaseMgmt.Api`. It serves the API, the SignalR hub (`/hub/trains`) and the built React app (from `wwwroot`) on one port. There are background services inside it: backups, the notification scheduler, the sync poller and watchdog, the export worker (if present in your build), the session-state janitor and the server-clock broadcaster. There is nothing else to deploy: no database server, no message queue.

**State lives in files. All of these must be backed up, and the last four are NOT covered by the automatic database backup:**

| What | Default location | Config key | Covered by automatic backup? |
|---|---|---|---|
| SQLite database (WAL mode) | `data/releasemgmt.db` | `Db:Path` | Yes (every 15 minutes) |
| Backups | `data/backups/` | `Backup:Directory` | (they are the backups: copy them off the machine) |
| **Data Protection key ring** | `keys/` beside the database | `DataProtection:KeysDirectory` | **No** |
| **Connector credentials** (encrypted with the key ring) | `secrets/` beside the database | `Sync:Credentials:Directory` | **No** |
| **Evidence attachments** | `data/attachments/` | `Attachments:Directory` | **No** |
| **Generated exports** (evidence packs, reports) | `data/exports/` | `Exports:Directory` | **No** |
| Logs | `logs/releasemgmt-YYYYMMDD.log` | `Logging:File` | No (diagnostic only) |

Why the key ring matters: connector credentials are stored encrypted with keys from the key ring. A database restored **without** the key ring works, but every stored connector credential becomes unreadable and must be re-entered (the sync poller raises an authentication alert). The key ring and the `secrets/` directory are useless without each other, and must be kept away from the database backups' storage account if you want the encryption to mean anything.

## 2. Prerequisites

| | Needed to run | Needed to build |
|---|---|---|
| .NET 10 **ASP.NET Core runtime** | yes | (SDK: yes) |
| Node 22 | no | yes (builds the web app) |
| Python 3.9+ | no | only for `start.py` and the reference tests |

Time: keep the server clock correct (NTP). Every timestamp is UTC; the screens show times in `Display:TimeZone` (default `America/Chicago`, an IANA id; on Windows .NET maps it).

## 3. Build and publish

From a clean checkout of `main`:

```
cd src/ReleaseMgmt.Web && npm ci && npm run build      # writes the app into src/ReleaseMgmt.Api/wwwroot
cd ../..
dotnet publish src/ReleaseMgmt.Api -c Release -o publish
```

`publish/` is the deployable: copy it to the server (for example `C:\ReleaseMgmt\app`, `/opt/releasemgmt/app`, `/usr/local/releasemgmt/app`). Keep **data outside the app directory** so an upgrade never touches it (next section).

## 4. Configuration

Configuration comes from, in order of precedence: environment variables, `appsettings.Production.json` next to the app, `appsettings.json`. Environment variables use `__` for `:` (`Db__Path`, `Auth__Oidc__ClientSecret`).

**Secrets never go in `appsettings*.json` in git.** Supply `Auth:Oidc:ClientSecret` from an environment variable or the OS secret store. Connector credentials are entered in the app (Connectors screen) and stored encrypted, never in configuration.

Minimum for production:

| Key | Purpose |
|---|---|
| `ASPNETCORE_ENVIRONMENT=Production` | disables the development-only login and dev endpoints |
| `ASPNETCORE_URLS` | listen address, e.g. `http://127.0.0.1:6080` behind a reverse proxy |
| `Db:Path` | absolute path to the database file |
| `DataProtection:KeysDirectory`, `Sync:Credentials:Directory` | absolute paths, on the same volume you back up |
| `Backup:Directory`, `Attachments:Directory`, `Exports:Directory` | absolute paths |
| `Auth:Oidc:Authority`, `Auth:Oidc:ClientId`, `Auth:Oidc:ClientSecret` | organisational sign-in (OpenID Connect, authorization-code flow; the identity provider must return an `email` claim) |
| `Auth:RoleMap` | maps identity-provider groups or roles to the app roles `Viewer`, `RTE`, `ReleaseManager`, `GovernanceOfficer` |
| `Auth:PasswordResetUrl` | optional; absolute `https` URL of the identity provider's self-service reset page |
| `Display:TimeZone` | IANA zone for the screens |

Other keys, with their defaults, are listed by the code that reads them: `Api:RequireIfMatch` (true), `Attachments:MaxBytes` (50 MB), `Audit:CsvMaxRows` (50,000), `Realtime:ServerTimeSeconds` (10), `Notifications:ScanSeconds` (30), `Notifications:Webhooks:*`, `Comms:Webhooks:*` (timeout 10 s), `Sync:PollSeconds` (300), `Sync:WindowPollSeconds` (60), `Sync:TimeoutSeconds`, `Sync:WatchdogSeconds`, `Sync:StaleAfterMinutes`, `Sync:AllowedHosts`, `Sync:AllowPrivateTargets` (false), `Connectors:{Jira|ServiceNow}:BaseUrl`, `Ics:MaxFailuresPerMinute` (60), `Seed:Demo` (false outside Development). **Do not set `Seed:Demo=true` in production**: it inserts demonstration trains.

The first start creates the database, applies the migrations and seeds reference data. Outbound connections to Jira Cloud and ServiceNow, and to webhook destinations, are HTTPS only and refuse private addresses unless you explicitly allow them; the poller and webhook senders do not use an HTTP proxy.

## 5. Running it as a service

The app does not host itself as a Windows service or a systemd unit; run the published `ReleaseMgmt.Api` under a service manager. The commands below are standard for each platform; they have not been exercised on real servers yet (section 12).

**Linux (systemd)** - `/etc/systemd/system/releasemgmt.service`:

```
[Unit]
Description=Release Management
After=network-online.target

[Service]
WorkingDirectory=/opt/releasemgmt/app
ExecStart=/usr/bin/dotnet /opt/releasemgmt/app/ReleaseMgmt.Api.dll
Restart=on-failure
User=releasemgmt
EnvironmentFile=/etc/releasemgmt/env
# env file holds ASPNETCORE_ENVIRONMENT=Production, ASPNETCORE_URLS, Db__Path=/var/lib/releasemgmt/releasemgmt.db, ...

[Install]
WantedBy=multi-user.target
```

`sudo systemctl enable --now releasemgmt`. The `releasemgmt` user must own the data directories and nobody else should read `keys/` and `secrets/` (mode 700).

**Windows Server** - use a service wrapper (WinSW or NSSM) around `dotnet.exe ReleaseMgmt.Api.dll` (or `ReleaseMgmt.Api.exe`), set the environment variables in the wrapper's configuration, run it as a dedicated service account, and restrict the data directories' ACLs to that account and administrators.

**macOS** - a `launchd` plist in `/Library/LaunchDaemons` running `dotnet ReleaseMgmt.Api.dll` with the environment variables in `EnvironmentVariables`. Developers on a laptop can use `python start.py` instead (see the README); that is not a production mode.

**Reverse proxy and TLS.** Terminate HTTPS at a reverse proxy (or configure a Kestrel certificate). The proxy must pass WebSocket upgrades for `/hub/trains`. **The proxy must not log request paths under `/api/v1/ics/`**: calendar feed tokens are carried in that path. The app itself keeps them out of its own logs.

**Health.** `GET /healthz` is anonymous and returns 200 `{status:"Healthy", db:"ok", backup:<last success time>}` or 503. Point your monitor at it, and alert if `backup` is older than about 30 minutes.

## 6. Backups

Automatic: the app takes a consistent online backup of the database every **15 minutes** into `Backup:Directory`; the first backup of each UTC day is kept as a **nightly** for 30 days, the others for 2 days. A failed backup raises an alert (source `Backup`, kind `BackupFailed`) visible on the Sync health screen and in the inbox.

What you must add:

1. Copy `Backup:Directory` off the machine at least daily (a scheduled job to object storage or another host).
2. Back up `keys/`, `secrets/`, `attachments/` and `exports/` **as well**, at least daily, and whenever the key ring changes. Keep the key-ring backup and the database backup in separately access-controlled locations; the pair recovers everything, either alone does not.
3. Test a restore (section 7) before relying on any of this and after any change to the paths.

## 7. Restore

Assume the machine is lost. On a replacement host with the app installed and configured (same `Db:Path` etc.):

1. **Stop** the service.
2. Restore the **key ring** (`keys/`) and the **connector credentials** (`secrets/`) from their backup, then `attachments/` and `exports/`.
3. Restore the database from the newest healthy backup file:

   ```
   dotnet ReleaseMgmt.Api.dll restore <path-to-backup-file> <Db:Path value>
   ```

   The command refuses a backup that fails SQLite's `integrity_check`, copies it over the target, and prints the integrity result. Backups are single self-contained files (not WAL mode).
4. Start the service. Check `GET /healthz`, sign in, open Sync health and confirm no authentication alert appears for the connectors (proof the key ring and credentials restored together).
5. Spot-check: the newest audit entry is close to the time of the backup; an evidence attachment downloads and its SHA-256 (shown on the gate) matches.

Recovery point: at most 15 minutes of changes are lost (the backup interval). Recovery time: measured by the timed drill in [DR_DRILL.md](DR_DRILL.md) (target: under 30 minutes for the whole procedure). Measured on a 4-core shared container: 3.6 s at 200 trains / 100k audit rows, 13.5 s for a 360 MB database plus 1 GB of attachments. Not measured on the pilot host: repeat it there. The drill also showed that nothing in the app backs up `keys/`, `secrets/` or `attachments/`; copy the database first, then attachments, and re-copy keys and secrets after any rotation or credential change.

## 8. Upgrading

1. Announce a short window; take a fresh backup (copy the newest file from `Backup:Directory`, or trigger by restarting is not needed) and copy `keys/` and `secrets/`.
2. Stop the service; keep the previous `publish` directory.
3. Replace the app directory with the new `publish` output (the data directories are elsewhere, so they are untouched).
4. Start the service. Schema migrations run automatically at startup; a failed migration stops the app with the error in the log, and nothing half-applied is served.
5. Verify `/healthz`, sign in, open a train.

Rollback: stop, put the previous `publish` directory back, restore the pre-upgrade database backup (section 7 step 3, because a newer schema may not run on an older build), start.

## 9. Connector credential rotation

Jira Cloud and ServiceNow credentials are entered on the **Connectors** screen (RTE or Release Manager); they are write-only and never shown again.

1. Create the new credential in the source system (a read-only service account/token; the app only reads keys, states and dates).
2. Connectors screen -> the connector -> enter the new credential -> **Test connection** (an inline result says whether it worked).
3. When the test passes, **Sync now** (or wait one poll interval), then revoke the old credential in the source system.
4. Sync health should show no open authentication alert; an alert clears itself after one clean cycle.

If a credential leaks: revoke it at the source first, then replace it as above. The Data Protection key ring itself rotates automatically (framework default: a new key roughly every 90 days; old keys stay so old data still decrypts): keep every key file in backups.

## 10. Monitoring and alerts

- `/healthz` (process, database, last backup).
- **Sync health** screen and the banner shown on every screen when a connector is failing or the sync engine has stalled; alerts also arrive in the inbox of RTEs and Release Managers.
- Notification failures, webhook failures (`Webhook/DeliveryFailed`), export failures (`Export/ExportFailed`) and backup failures are all alerts, never silent.
- Logs: `logs/`, daily files. Request lines for the ICS feeds are kept out; connector URLs and webhook URLs are never logged.

## 11. Pilot checklist (human-led, with the pilot RTE)

Not automatable; tick these with the pilot RTE and the Governance Officer for one real train:

- [ ] Users sign in with the organisation identity; roles are correct (RTE, Release Manager, Governance Officer, Viewer).
- [ ] Train created with its products, gates and owners; gate dates match the calendar.
- [ ] Checklists and the runbook are entered (bulk paste works for the team's real lists).
- [ ] Connectors point at the real Jira and ServiceNow; links show state; a deliberate wrong key shows a stale/broken link, not a silent gap.
- [ ] Go/No-Go recorded by a Release Manager; conditions have owners and expiries.
- [ ] A rehearsal run has been done and its rollback attested (High-risk trains).
- [ ] The live run: timings, forecast and the rollback-deadline alert behave as expected.
- [ ] Communications sent through the drawer; the log shows what was sent.
- [ ] Evidence gathered on gates; the evidence pack generated; **a Governance Officer accepts the pack** (this is the acceptance criterion of REOS-54).
- [ ] The release ran **without a spreadsheet**.
- [ ] A restore drill on the pilot host has been done (section 7) and its time recorded.

## 12. Verified and not verified

Verified by automated tests in this repository: the backup and restore code paths (`BackupTests`, green on Windows and macOS in CI), credentials never stored in the database, the health endpoint, configuration keys listed above (as read by the code), and the timed restore drill (see DR_DRILL.md).

**Not verified on a real server yet:** the service definitions in section 5 (standard patterns, never run here on Windows Server, Linux or macOS servers), reverse-proxy WebSocket and logging configuration, OIDC against your identity provider, connectors against real Jira/ServiceNow tenants (CI never uses live tenants), and the pilot itself. Do these on a staging host first.
