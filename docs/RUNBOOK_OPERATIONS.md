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
| **Connector credentials** and the webhook address key `webhook-url.hmackey` (encrypted with the key ring) | `secrets/` beside the database | `Sync:Credentials:Directory` | **No** |
| **Key-ring certificate** (PKCS#12), if you use one (section 4) | your choice, outside the app and data directories | `DataProtection:Certificate:Path` | **No** |
| **Evidence attachments** | `data/attachments/` | `Attachments:Directory` | **No** |
| **Generated exports** (evidence packs, reports; each file deleted 365 days after it was made, D35) | `data/exports/` | `Exports:Directory` | **No** |
| Logs | `logs/releasemgmt-YYYYMMDD.log` | `Logging:File` | No (diagnostic only) |

Why the key ring matters: connector credentials and webhook addresses are stored encrypted with keys from the key ring (webhook addresses in the database itself, D36). A database restored **without** the key ring works, but every stored connector credential and webhook address becomes unreadable and must be re-entered (the sync poller raises an authentication alert; a webhook delivery fails with "remove the destination and add it again"). The key ring and the `secrets/` directory are useless without each other, and must be kept away from the database backups' storage account if you want the encryption to mean anything. Since 2026-09-30 the key ring is itself encrypted at rest (D37, section 4): with a certificate, the ring is useless without the certificate's private key, which is a third thing to keep, separately.

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
| `Auth:RoleMap` | maps identity-provider groups or roles to the app roles `Viewer`, `RTE`, `ReleaseManager`, `GovernanceOfficer`. Only mapped values count: an IdP role named like an app role needs its own entry (`Auth:RoleMap:ReleaseManager=ReleaseManager`) |
| `Auth:DefaultRole` | empty (default): an identity in no mapped group or role is refused at sign-in. `Viewer`: such identities may sign in read-only (with Entra ID's default "assignment required: no", that is every member and guest of the tenant) |
| `Auth:PasswordResetUrl` | optional; absolute `https` URL of the identity provider's self-service reset page |
| `Display:TimeZone` | IANA zone for the screens |
| `AllowedHosts` | **required outside Development.** The host name(s) users type, e.g. `releases.example.com` (`;`-separated; `*.example.com` is allowed). Any other `Host` header is answered 400, so a DNS-rebinding page cannot reach the app under its own name (SEC-E1). Nothing is shipped for it: while it is unset, empty or `*` the app **refuses to start** and logs `AllowedHosts is empty` / `AllowedHosts is "*"` (REOS-68, Q-SEC-E1). Development allows loopback names only (`appsettings.Development.json`) and is not checked |
| **Key ring protection** (D37, REOS-74) | required outside Development, or the app refuses to start and names these keys. **Linux / macOS:** `DataProtection:Certificate:Path` (a PKCS#12 file with an RSA key, readable only by the service account) and `DataProtection:Certificate:Password` **from the environment** (`DataProtection__Certificate__Password` in the service's environment file, or the OS secret store; a password found in an `appsettings*.json` file stops the start), or `DataProtection:Certificate:Thumbprint` (a certificate with its private key in the service account's CurrentUser `My` store, or LocalMachine `My`). **Windows:** nothing to set: DPAPI with the service account's **user** scope is used (only that account on that machine can read the keys); set a certificate as above instead if you need to restore onto another machine (recommended, see section 7), or `DataProtection:Dpapi:Scope=Machine` if several accounts must run the app. A pilot that accepts plain keys may set `DataProtection:AllowUnprotectedKeys=true`: the app then logs a Warning at every start |
| `Sync:AllowedHosts` | connector hosts (D38, REOS-75). Unset outside Development = `*.atlassian.net, *.service-now.com` on port 443 only. Setting it **replaces** that list: on-prem Jira Data Center or a ServiceNow custom domain must be listed (`jira.example.com:8443` for a port other than 443; `*.example.com` for subdomains). `*` alone allows any public host and port. Saving a connector on another host answers "not on the allowed list (Sync:AllowedHosts ...)" |
| `Sync:Nat64Prefixes` | only on an IPv6-only network whose NAT64 uses its own prefix (D40, REOS-77): the /96 prefix(es), e.g. `2001:db8:64::/96`, so addresses behind it are judged by the IPv4 they reach (connectors and webhooks). A malformed value stops the start, naming the key |
| `Proxy:KnownProxies`, `Proxy:KnownNetworks` | only when the TLS-terminating reverse proxy is **not** on the same host: its address(es), or CIDR network(s). A proxy on the same host (loopback) is trusted by default. Without this the app judges the proxy's address and scheme instead of the client's (SEC-E5) |

Other keys, with their defaults, are listed by the code that reads them. Security limits added by the 2026-09-30 scan ([SECURITY_SCAN.md](SECURITY_SCAN.md)): `Limits:MaxRequestBodyBytes` (1 MiB; uploads keep their own caps), `Imports:MaxColumns` (200), `Imports:MaxOpenPreviewsPerUser` (5), `Exports:MaxOpenJobsPerUser` (10), `Exports:RetentionDays` (365, D35: a generated file is deleted that many days after it was made; its record and hash stay and its download answers 410), `Freeze:OverrideRequiresRequest` (true). Also: `Api:RequireIfMatch` (true), `Attachments:MaxBytes` (50 MB), `Audit:CsvMaxRows` (50,000), `Realtime:ServerTimeSeconds` (10), `Notifications:ScanSeconds` (30), `Notifications:Webhooks:*`, `Comms:Webhooks:*` (timeout 10 s), `Sync:PollSeconds` (300), `Sync:WindowPollSeconds` (60), `Sync:TimeoutSeconds`, `Sync:WatchdogSeconds`, `Sync:StaleAfterMinutes`, `Sync:AllowedHosts`, `Sync:AllowPrivateTargets` (false), `Connectors:{Jira|ServiceNow}:BaseUrl`, `Ics:MaxFailuresPerMinute` (60), `Seed:Demo` (false outside Development), `Auth:Session:IdleMinutes` (60), `Auth:Session:AbsoluteHours` (12, from sign-in), `Auth:SessionRecheckSeconds` (10), `Auth:Cookie:RequireHttps` (true outside Development: the session cookie is `__Host-releasemgmt.auth`, `Secure`, so browsers must reach the app over HTTPS), `Auth:DevLogin:Enabled` (Development only; default on unless `Auth:Oidc:Authority` is set). **Do not set `Seed:Demo=true` in production**: it inserts demonstration trains. **Never run a pilot or production host in Development** (`python start.py` defaults to it): Development maps an anonymous "sign in as any role" endpoint for callers on the same machine.

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

Key-ring certificate on Linux or macOS (once, before the first start; D37). A self-signed certificate is enough, it is only used to encrypt the keys:

```
sudo -u releasemgmt openssl req -x509 -newkey rsa:3072 -sha256 -days 3650 -nodes -subj "/CN=ReleaseMgmt key ring" \
     -keyout /etc/releasemgmt/keyring.key -out /etc/releasemgmt/keyring.crt
sudo -u releasemgmt openssl pkcs12 -export -inkey /etc/releasemgmt/keyring.key -in /etc/releasemgmt/keyring.crt \
     -out /etc/releasemgmt/keyring.pfx            # prompts for the export password
sudo shred -u /etc/releasemgmt/keyring.key; sudo chmod 600 /etc/releasemgmt/keyring.pfx
```

then in the environment file (mode 600, root and `releasemgmt` only): `DataProtection__Certificate__Path=/etc/releasemgmt/keyring.pfx` and `DataProtection__Certificate__Password=<the password>`. Copy `keyring.pfx` and its password to the place you keep recovery secrets (not next to the `keys/` backup). The first start logs `Data Protection key ring protection: certificate CN=ReleaseMgmt key ring (...)`; keys written before this step stay readable, every new key is encrypted.

**Windows Server** - use a service wrapper (WinSW or NSSM) around `dotnet.exe ReleaseMgmt.Api.dll` (or `ReleaseMgmt.Api.exe`), set the environment variables in the wrapper's configuration, run it as a dedicated service account, and restrict the data directories' ACLs to that account and administrators. The key ring is encrypted with DPAPI for that service account (D37): the account needs a loaded user profile (WinSW and NSSM services running as a real account have one; a virtual or managed service account also works). If you want to be able to restore onto another machine, import a key-ring certificate (with private key) into the service account's `Cert:\CurrentUser\My` store and set `DataProtection:Certificate:Thumbprint` instead; export it (with its private key, password protected) to your recovery-secrets store.

**macOS** - a `launchd` plist in `/Library/LaunchDaemons` running `dotnet ReleaseMgmt.Api.dll` with the environment variables in `EnvironmentVariables`. Developers on a laptop can use `python start.py` instead (see the README); that is not a production mode.

**Reverse proxy and TLS.** Terminate HTTPS at a reverse proxy (or configure a Kestrel certificate). The proxy must pass WebSocket upgrades for `/hub/trains`. **The proxy must not log request paths under `/api/v1/ics/`**: calendar feed tokens are carried in that path. The app itself keeps them out of its own logs.

**Health.** `GET /healthz` is anonymous and returns 200 `{status:"Healthy", db:"ok", backup:<last success time>, poller:{...}, watchdog:{...}}` or 503. `poller` and `watchdog` each give `state` (`disabled` when `Sync:Enabled=false`, `running`, or `stalled`), `lastCycleUtc` (the last pass of its timer loop) and `stalledAfterSeconds` (3 of its intervals: 900 s for the poller, 180 s for the watchdog). It is **503 when the database check fails or an enabled loop has stalled** (REOS-69, Q-069); restart the service and look for "loop ended" errors in the log. Point your monitor at it, and alert if `backup` is older than about 30 minutes.

## 6. Backups

Automatic: the app takes a consistent online backup of the database every **15 minutes** into `Backup:Directory`; the first backup of each UTC day is kept as a **nightly** for 30 days, the others for 2 days. A failed backup raises an alert (source `Backup`, kind `BackupFailed`) visible on the Sync health screen and in the inbox.

What you must add:

1. Copy `Backup:Directory` off the machine at least daily (a scheduled job to object storage or another host).
2. Back up `keys/`, `secrets/`, `attachments/` and `exports/` **as well**, at least daily, and whenever the key ring changes. Keep the key-ring backup and the database backup in separately access-controlled locations; the pair recovers everything, either alone does not. With a key-ring certificate (D37) the `keys/` copy is encrypted; keep the certificate (PFX and password) in a third place, your recovery-secrets store. With Windows DPAPI the `keys/` copy can only be read by the same account on the same machine: it is no use for a restore onto new hardware (section 7).
3. Test a restore (section 7) before relying on any of this and after any change to the paths.

Exports older than `Exports:RetentionDays` (365) are deleted by the app (D35); a file backup of `exports/` keeps them only as long as your backup retention does.

## 7. Restore

Assume the machine is lost. On a replacement host with the app installed and configured (same `Db:Path` etc.):

1. **Stop** the service.
2. Restore the **key ring** (`keys/`) and the **connector credentials** (`secrets/`, which also holds the webhook address key) from their backup, then `attachments/` and `exports/`. The key ring is encrypted (D37): configure the **same certificate** (`DataProtection:Certificate:Path` + password, or import it and set `DataProtection:Certificate:Thumbprint`) before starting. A ring protected with Windows DPAPI can only be read by the same service account on the same machine: on a new machine it cannot be read, so every connector credential and webhook address must be entered again; use a certificate if that is not acceptable. Do not delete key files, old ones included: data protected with an old key needs it.
3. Restore the database from the newest healthy backup file:

   ```
   dotnet ReleaseMgmt.Api.dll restore <path-to-backup-file> <Db:Path value>
   ```

   The command refuses a backup that fails SQLite's `integrity_check`, copies it over the target, and prints the integrity result. Backups are single self-contained files (not WAL mode).
4. Start the service. Check `GET /healthz`, sign in, open Sync health and confirm no authentication alert appears for the connectors (proof the key ring and credentials restored together). A database backup from before 2026-09-30 holds the webhook addresses in clear; the first start encrypts them (log line "Webhook addresses are now stored encrypted", one `ProtectUrl` audit row per destination). If `secrets/webhook-url.hmackey` was not restored, the start re-keys the addresses it can decrypt and logs a Warning naming any it cannot (re-add those).
5. Spot-check: the newest audit entry is close to the time of the backup; an evidence attachment downloads and its SHA-256 (shown on the gate) matches.

Recovery point: at most 15 minutes of changes are lost (the backup interval). Recovery time: measured by the timed drill in [DR_DRILL.md](DR_DRILL.md) (target: under 30 minutes for the whole procedure). Measured on a 4-core shared container: 3.6 s at 200 trains / 100k audit rows, 13.5 s for a 360 MB database plus 1 GB of attachments. Not measured on the pilot host: repeat it there. The drill also showed that nothing in the app backs up `keys/`, `secrets/` or `attachments/`; copy the database first, then attachments, and re-copy keys and secrets after any rotation or credential change.

## 8. Upgrading

1. Announce a short window; take a fresh backup (copy the newest file from `Backup:Directory`, or trigger by restarting is not needed) and copy `keys/` and `secrets/`.
2. Stop the service; keep the previous `publish` directory.
3. Replace the app directory with the new `publish` output (the data directories are elsewhere, so they are untouched).
4. If the release notes list a script in `db/upgrades/` that is newer than the database (Q-SEC-B8m), run each such script, in number order, with the
   service still stopped: `sqlite3 <Database path> < db/upgrades/NNN_name.sql`. Each script is one transaction, refuses to run against the wrong
   version, and ends by printing `PRAGMA foreign_key_check` (expect no rows) and `PRAGMA integrity_check` (expect `ok`). Anything else: restore the
   backup from step 1 and stop. If a script is missed, the app refuses to start and the log names the migrations it found ("created by an older build").
5. Start the service. Schema migrations run automatically at startup; a failed migration stops the app with the error in the log, and nothing half-applied is served.
6. Verify `/healthz`, sign in, open a train.

| Script | Brings a database from | Change |
|---|---|---|
| `001_idp_identity_and_session_revocations.sql` | migrations `20260929110130_Schema` + `20260929110136_Triggers` | REOS-61/62: `Users.IdpIssuer`/`IdpSubject` (rebuilds `Users`), `SessionRevocations` |
| `002_webhook_urls_encrypted.sql` | migrations `20260930234604_Schema` + `20260930234616_Triggers` | REOS-73: records the new migrations only; the app encrypts `WebhookDestinations` at its next start (needs the key ring and `secrets/`, so restore both first) |
| `003_train_milestones.sql` | migrations `20261001030509_Schema` + `20261001030524_Triggers` | REOS-84: adds `TrainMilestones` and `IX_Milestones_Train` |

Rollback: stop, put the previous `publish` directory back, restore the pre-upgrade database backup (section 7 step 3, because a newer schema may not run on an older build), start.

## 9. Connector credential rotation

Jira Cloud and ServiceNow credentials are entered on the **Connectors** screen (RTE or Release Manager); they are write-only and never shown again.

1. Create the new credential in the source system (a read-only service account/token; the app only reads keys, states and dates).
2. Connectors screen -> the connector -> enter the new credential -> **Test connection** (an inline result says whether it worked).
3. When the test passes, **Sync now** (or wait one poll interval), then revoke the old credential in the source system.
4. Sync health should show no open authentication alert; an alert clears itself after one clean cycle.

If a credential leaks: revoke it at the source first, then replace it as above. The Data Protection key ring itself rotates automatically (framework default: a new key roughly every 90 days; old keys stay so old data still decrypts): keep every key file in backups.

**Webhook addresses** (Teams, Slack, Generic) are secrets too (their path is the token) and are stored encrypted (D36). To rotate one: create the new webhook in Teams or Slack, add it on Sync health -> Webhook allowlist, point the team or template at it, then remove the old entry (a used entry stays as the record of past dispatches) and delete the old webhook in Teams or Slack. Rotate every webhook if a database backup taken **before 2026-09-30** (when addresses were still in clear) may have been exposed.

**Key-ring certificate** (D37). The certificate only encrypts the key files; its expiry date does not stop the app. To replace it (compromise, policy): create the new certificate as in section 5, set it as `DataProtection:Certificate:Path`/`Password` (or `Thumbprint`) and keep the **old** one readable as `DataProtection:Certificate:PreviousPath` + `DataProtection:Certificate:PreviousPassword` (or `DataProtection:Certificate:PreviousThumbprints`, comma-separated, with the old certificate left in the store), restart, and back up `keys/`. Keys created from then on are encrypted with the new certificate; keys written under the old one still need it, so keep the old certificate configured and in your recovery-secrets store for as long as those key files exist (they never expire from the ring: plan on keeping it). If the old certificate's private key leaked, treat the key ring as exposed: enter every connector credential and webhook address again after the change, so they are protected by a key the attacker cannot read. On Windows with DPAPI there is nothing to rotate; the service account's password change does not affect it.

## 10. Monitoring and alerts

- `/healthz` (process, database, last backup, sync poller and watchdog loops; 503 when one has stalled).
- **Sync health** screen and the banner shown on every screen when a connector is failing or the sync engine has stalled; alerts also arrive in the inbox of RTEs and Release Managers.
- Notification failures, webhook failures (`Webhook/DeliveryFailed`), export failures (`Export/ExportFailed`, also when a file past its retention cannot be deleted) and backup failures are all alerts, never silent.
- At every start the log names the key-ring protection (`Data Protection key ring protection: ...`), or logs a **Warning** while `DataProtection:AllowUnprotectedKeys=true` is in use: fix that before the pilot ends.
- Security events are logged under the category `ReleaseMgmt.Security`: sign-in (who, role, method), sign-out, access denied (Warning: who, role, method, path) and sessions ended because the user was deactivated. A burst of `Access denied` lines from one user is worth a look.
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

Since 2026-09-30 also: export retention on a fake clock, the encrypted webhook addresses and the conversion of an older database, the key-ring certificate (a generated test certificate; on Windows the same test checks DPAPI), the connector host default and NAT64 prefixes. Run here on Linux; the same tests run on the Windows and macOS CI runners.

**Not verified on a real server yet:** the certificate commands in section 5, DPAPI under a real service wrapper account, the service definitions in section 5 (standard patterns, never run here on Windows Server, Linux or macOS servers), reverse-proxy WebSocket and logging configuration, OIDC against your identity provider, connectors against real Jira/ServiceNow tenants (CI never uses live tenants), and the pilot itself. Do these on a staging host first.

## 13. Keeping the CI action pins current (D39)

There is no Dependabot (decided 2026-09-30, REOS-76). The GitHub Actions in `.github/workflows/ci.yml` are pinned to commit SHAs with their tag as a comment (`uses: actions/checkout@<sha>   # v4`); they do not move by themselves. At least once a release train (and when GitHub announces a security fix for an action):

1. Check each action's releases page for a newer major or patch tag.
2. On a branch, change the line to the tag (`uses: actions/checkout@v5`), push, and let CI run.
3. Open the run, any job, the **Set up job** step: the runner logs `Download action repository 'actions/checkout@v5' (SHA:<40 hex>)`. That SHA is what the tag pointed to for this run.
4. Replace the tag with that SHA and keep the tag as the comment (`uses: actions/checkout@<sha>   # v5`), push, and check CI is green on both runners.
5. Run `npm audit` (in `src/ReleaseMgmt.Web` and `tests/e2e`) and `dotnet list package --vulnerable --include-transitive` at the same time; nothing reports them automatically either.
