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

Installing the runtime (the server needs only the ASP.NET Core runtime, x64 or arm64; the build machine needs the SDK and Node). These are the vendors' standard commands; they have not been run on your servers (section 12):

| Server | Install | Check |
|---|---|---|
| Windows Server 2019 or later | `winget install Microsoft.DotNet.AspNetCore.10`, or the "ASP.NET Core Runtime 10.0" installer from https://dotnet.microsoft.com/download/dotnet/10.0 (the Hosting Bundle is only needed behind IIS) | `dotnet --list-runtimes` lists `Microsoft.AspNetCore.App 10.0.x` |
| Ubuntu / Debian | `sudo apt-get install -y aspnetcore-runtime-10.0 fonts-dejavu-core` (Ubuntu's own feed, or Microsoft's package feed on releases that lack it) | same |
| RHEL / Rocky / Alma | `sudo dnf install -y aspnetcore-runtime-10.0 dejavu-sans-mono-fonts` | same |
| macOS 13 or later | the "ASP.NET Core Runtime 10.0" `.pkg` installer (Arm64 for Apple silicon) from the same page; it installs `/usr/local/share/dotnet/dotnet` | same |

Fonts: PDFs use the operating system's fonts (Q-050b). Windows and macOS have the monospace fonts the exports look for; a minimal Linux server may have none, which is why the commands above add DejaVu Sans Mono. Without it, hashes and times in the PDFs print in the body font (still correct, less legible).

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

Where the non-secret settings live: the publish output contains `appsettings.json` and `appsettings.Development.json` only, and an upgrade replaces the whole app directory (section 8). Do not edit the shipped `appsettings.json`. Put your settings either in the service's environment (section 5) or in an `appsettings.Production.json` whose master copy you keep **outside** the app directory (`/etc/releasemgmt/`, `C:\ReleaseMgmt\config\`) and copy into the app directory at every upgrade. Use the JSON file for `Auth:RoleMap` when a group name or id contains a character other than letters, digits and `_` (an Entra ID group object id has `-`): systemd ignores environment variable names like that, so the mapping would silently be missing and those people would be refused at sign-in.

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
| `Display:TimeZone` | IANA zone shown beside UTC on screens, PDFs and messages, and used for business days |
| `Release:OnDemandOffsetDays` | calendar days (0 to 90, default 7) from a release's Standard date to the date an On-Demand client is offered on a CERT or Production release (D47, REOS-119, provisional: Q-119a). It applies to offered dates made after a change; dates already offered keep theirs and move with the release. Not a whole number from 0 to 90: the app refuses to start and names the key |
| `App:PublicBaseUrl` | optional (D52, REOS-139): the address people open the app at, e.g. `https://releaseos.example.com` (http or https, no user name or password, no query, no fragment; a path prefix is kept). Notifications, team webhooks and communications then end with a link back into the app. **Unset: no link is added** (the host name is never guessed). A malformed value stops the start, naming the key |
| `Workspace:DefaultName`, `Workspace:Region` | optional (REOS-104, Q-090e): the name (up to 100 characters) and region label (up to 60) of the default workspace, which holds every row from before workspaces. Set: applied at start and audited; unset: the stored value stays ("Default", no region, on a new database) |
| `Pdf:QuestPdfLicense` | **required for every PDF and the evidence pack** (OI-2, Q-050b): `Community`, `Professional` or `Enterprise`, whichever QuestPDF licence your organisation qualifies for (Community: under USD 1M annual revenue and not a public or government body; D17). There is no default. While it is unset, "Export" answers 422 `ExportLicence` naming this key and no pack can be generated, so set it before the pilot |
| `AllowedHosts` | **required outside Development.** The host name(s) users type, e.g. `releases.example.com` (`;`-separated; `*.example.com` is allowed). Any other `Host` header is answered 400, so a DNS-rebinding page cannot reach the app under its own name (SEC-E1). Nothing is shipped for it: while it is unset, empty or `*` the app **refuses to start** and logs `AllowedHosts is empty` / `AllowedHosts is "*"` (REOS-68, Q-SEC-E1). Development allows loopback names only (`appsettings.Development.json`) and is not checked |
| **Key ring protection** (D37, REOS-74) | required outside Development, or the app refuses to start and names these keys. **Linux / macOS:** `DataProtection:Certificate:Path` (a PKCS#12 file with an RSA key, readable only by the service account) and `DataProtection:Certificate:Password` **from the environment** (`DataProtection__Certificate__Password` in the service's environment file, or the OS secret store; a password found in an `appsettings*.json` file stops the start), or `DataProtection:Certificate:Thumbprint` (a certificate with its private key in the service account's CurrentUser `My` store, or LocalMachine `My`). **Windows:** nothing to set: DPAPI with the service account's **user** scope is used (only that account on that machine can read the keys); set a certificate as above instead if you need to restore onto another machine (recommended, see section 7), or `DataProtection:Dpapi:Scope=Machine` if several accounts must run the app. A pilot that accepts plain keys may set `DataProtection:AllowUnprotectedKeys=true`: the app then logs a Warning at every start |
| `Sync:AllowedHosts` | connector hosts (D38, REOS-75). Unset outside Development = `*.atlassian.net, *.service-now.com` on port 443 only. Setting it **replaces** that list: on-prem Jira Data Center or a ServiceNow custom domain must be listed (`jira.example.com:8443` for a port other than 443; `*.example.com` for subdomains). `*` alone allows any public host and port. Saving a connector on another host answers "not on the allowed list (Sync:AllowedHosts ...)" |
| `Sync:Nat64Prefixes` | only on an IPv6-only network whose NAT64 uses its own prefix (D40, REOS-77): the /96 prefix(es), e.g. `2001:db8:64::/96`, so addresses behind it are judged by the IPv4 they reach (connectors and webhooks). A malformed value stops the start, naming the key |
| `Proxy:KnownProxies`, `Proxy:KnownNetworks` | only when the TLS-terminating reverse proxy is **not** on the same host: its address(es), or CIDR network(s). A proxy on the same host (loopback) is trusted by default. Without this the app judges the proxy's address and scheme instead of the client's (SEC-E5) |
| `Ingest:RequestsPerSecond`, `Ingest:Burst`, `Ingest:FailedKeysPerMinute`, `Ingest:RejectionAuditsPerMinute` | the deployment ingest API's limits (REOS-110, section 14): calls per second and burst per key (defaults 100 and 200), refused keys per address per minute (30) and how many of them are audited (10) |

Other keys, with their defaults, are listed by the code that reads them. Security limits added by the 2026-09-30 scan ([SECURITY_SCAN.md](SECURITY_SCAN.md)): `Limits:MaxRequestBodyBytes` (1 MiB; uploads keep their own caps), `Imports:MaxColumns` (200), `Imports:MaxOpenPreviewsPerUser` (5), `Exports:MaxOpenJobsPerUser` (10), `Exports:RetentionDays` (365, D35: a generated file is deleted that many days after it was made; its record and hash stay and its download answers 410), `Freeze:OverrideRequiresRequest` (true). Also: `Api:RequireIfMatch` (true), `Attachments:MaxBytes` (50 MB), `Audit:CsvMaxRows` (50,000), `Realtime:ServerTimeSeconds` (10), `Notifications:ScanSeconds` (30), `Notifications:Webhooks:*`, `Comms:Webhooks:*` (timeout 10 s), `Sync:PollSeconds` (300), `Sync:WindowPollSeconds` (60), `Sync:TimeoutSeconds`, `Sync:WatchdogSeconds`, `Sync:StaleAfterMinutes`, `Sync:AllowedHosts`, `Sync:AllowPrivateTargets` (false), `Connectors:{Jira|ServiceNow}:BaseUrl`, `Ics:MaxFailuresPerMinute` (60), `Seed:Demo` (false outside Development), `Auth:Session:IdleMinutes` (60), `Auth:Session:AbsoluteHours` (12, from sign-in), `Auth:SessionRecheckSeconds` (10), `Auth:Cookie:RequireHttps` (true outside Development: the session cookie is `__Host-releasemgmt.auth`, `Secure`, so browsers must reach the app over HTTPS), `Auth:DevLogin:Enabled` (Development only; default on unless `Auth:Oidc:Authority` is set). **Do not set `Seed:Demo=true` in production**: it inserts demonstration trains. **Never run a pilot or production host in Development** (`python start.py` defaults to it): Development maps an anonymous "sign in as any role" endpoint for callers on the same machine.

The first start creates the database, applies the migrations and seeds reference data. Outbound connections to Jira Cloud and ServiceNow, and to webhook destinations, are HTTPS only and refuse private addresses unless you explicitly allow them; the poller and webhook senders do not use an HTTP proxy.

## 5. Running it as a service

The app does not host itself as a Windows service or a systemd unit; run the published `ReleaseMgmt.Api` under a service manager. The commands below are standard for each platform; they have not been exercised on real servers yet (section 12).

**Linux (systemd).** Once, as root: a system account, the directories (app and data apart, so an upgrade never touches the data), then the environment file and the unit.

```
sudo useradd --system --home-dir /var/lib/releasemgmt --shell /usr/sbin/nologin releasemgmt
sudo install -d -o root -g root -m 755 /opt/releasemgmt /opt/releasemgmt/app          # copy publish/ here (section 3)
sudo install -d -o releasemgmt -g releasemgmt -m 750 /var/lib/releasemgmt /var/lib/releasemgmt/backups \
     /var/lib/releasemgmt/attachments /var/lib/releasemgmt/exports /var/log/releasemgmt
sudo install -d -o releasemgmt -g releasemgmt -m 700 /var/lib/releasemgmt/keys /var/lib/releasemgmt/secrets
sudo install -d -o root -g releasemgmt -m 750 /etc/releasemgmt                   # configuration: the service reads it, never writes it
```

`/etc/releasemgmt/env` (owned by root, mode 600; systemd reads it before dropping to the service account):

```
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://127.0.0.1:6080
AllowedHosts=releases.example.com
Db__Path=/var/lib/releasemgmt/releasemgmt.db
Backup__Directory=/var/lib/releasemgmt/backups
DataProtection__KeysDirectory=/var/lib/releasemgmt/keys
Sync__Credentials__Directory=/var/lib/releasemgmt/secrets
Attachments__Directory=/var/lib/releasemgmt/attachments
Exports__Directory=/var/lib/releasemgmt/exports
Logging__File=/var/log/releasemgmt/releasemgmt-.log
Display__TimeZone=America/Chicago
Pdf__QuestPdfLicense=Community
Auth__Oidc__Authority=https://login.microsoftonline.com/<tenant-id>/v2.0
Auth__Oidc__ClientId=<client-id>
Auth__Oidc__ClientSecret=<client-secret>
DataProtection__Certificate__Path=/etc/releasemgmt/keyring.pfx
DataProtection__Certificate__Password=<the PFX password>
```

`Auth:RoleMap` goes in `/etc/releasemgmt/appsettings.Production.json`, copied into the app directory (section 4), for example `{"Auth":{"RoleMap":{"<RTE group id>":"RTE","<Release Managers group id>":"ReleaseManager","<Governance group id>":"GovernanceOfficer","<Viewers group id>":"Viewer"}}}`.

`/etc/systemd/system/releasemgmt.service`:

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

[Install]
WantedBy=multi-user.target
```

Create the key-ring certificate (below) before the first start, then `sudo systemctl daemon-reload && sudo systemctl enable --now releasemgmt`, and follow the log with `journalctl -u releasemgmt -f` (startup refusals, such as a missing `AllowedHosts`, are printed there). The `releasemgmt` user must own the data directories and nobody else should read `keys/` and `secrets/` (mode 700).

Key-ring certificate on Linux or macOS (once, before the first start; D37). A self-signed certificate is enough, it is only used to encrypt the keys:

```
sudo openssl req -x509 -newkey rsa:3072 -sha256 -days 3650 -nodes -subj "/CN=ReleaseMgmt key ring" \
     -keyout /etc/releasemgmt/keyring.key -out /etc/releasemgmt/keyring.crt
sudo openssl pkcs12 -export -inkey /etc/releasemgmt/keyring.key -in /etc/releasemgmt/keyring.crt \
     -out /etc/releasemgmt/keyring.pfx            # prompts for the export password
sudo shred -u /etc/releasemgmt/keyring.key
sudo chown root:releasemgmt /etc/releasemgmt/keyring.pfx; sudo chmod 640 /etc/releasemgmt/keyring.pfx   # the service reads it
```

then in the environment file (owned by root, mode 600): `DataProtection__Certificate__Path=/etc/releasemgmt/keyring.pfx` and `DataProtection__Certificate__Password=<the password>`. Copy `keyring.pfx` and its password to the place you keep recovery secrets (not next to the `keys/` backup). The first start logs `Data Protection key ring protection: certificate CN=ReleaseMgmt key ring (...)`; keys written before this step stay readable, every new key is encrypted.

**Windows Server** - use a service wrapper (WinSW or NSSM) around `dotnet.exe ReleaseMgmt.Api.dll` (or `ReleaseMgmt.Api.exe`), set the environment variables in the wrapper's configuration, run it as a dedicated service account, and restrict the data directories' ACLs to that account and administrators. The key ring is encrypted with DPAPI for that service account (D37): the account needs a loaded user profile (WinSW and NSSM services running as a real account have one; a virtual or managed service account also works). If you want to be able to restore onto another machine, import a key-ring certificate (with private key) into the service account's `Cert:\CurrentUser\My` store and set `DataProtection:Certificate:Thumbprint` instead; export it (with its private key, password protected) to your recovery-secrets store.

In an elevated PowerShell, once (the account `CORP\svc-releasemgmt` and the drive letters are examples):

```
New-Item -ItemType Directory C:\ReleaseMgmt\app, C:\ReleaseMgmt\config, D:\ReleaseMgmt\data\backups, D:\ReleaseMgmt\data\keys, `
  D:\ReleaseMgmt\data\secrets, D:\ReleaseMgmt\data\attachments, D:\ReleaseMgmt\data\exports, D:\ReleaseMgmt\logs
icacls D:\ReleaseMgmt /inheritance:r /grant:r "CORP\svc-releasemgmt:(OI)(CI)M" "Administrators:(OI)(CI)F" "SYSTEM:(OI)(CI)F"
icacls C:\ReleaseMgmt\config /inheritance:r /grant:r "CORP\svc-releasemgmt:(OI)(CI)RX" "Administrators:(OI)(CI)F" "SYSTEM:(OI)(CI)F"
```

Copy `publish\` into `C:\ReleaseMgmt\app`, and WinSW (`WinSW-x64.exe`, renamed `releasemgmt.exe`) with this `releasemgmt.xml` beside it in `C:\ReleaseMgmt\config` (the file holds the OIDC client secret, so the ACL above, Administrators and the service account only, is what protects it):

```
<service>
  <id>releasemgmt</id>
  <name>Release Management</name>
  <description>Release Management app (ReleaseMgmt.Api)</description>
  <executable>C:\Program Files\dotnet\dotnet.exe</executable>
  <arguments>C:\ReleaseMgmt\app\ReleaseMgmt.Api.dll</arguments>
  <workingdirectory>C:\ReleaseMgmt\app</workingdirectory>
  <startmode>Automatic</startmode>
  <onfailure action="restart" delay="10 sec"/>
  <env name="ASPNETCORE_ENVIRONMENT" value="Production"/>
  <env name="ASPNETCORE_URLS" value="http://127.0.0.1:6080"/>
  <env name="AllowedHosts" value="releases.example.com"/>
  <env name="Db__Path" value="D:\ReleaseMgmt\data\releasemgmt.db"/>
  <env name="Backup__Directory" value="D:\ReleaseMgmt\data\backups"/>
  <env name="DataProtection__KeysDirectory" value="D:\ReleaseMgmt\data\keys"/>
  <env name="Sync__Credentials__Directory" value="D:\ReleaseMgmt\data\secrets"/>
  <env name="Attachments__Directory" value="D:\ReleaseMgmt\data\attachments"/>
  <env name="Exports__Directory" value="D:\ReleaseMgmt\data\exports"/>
  <env name="Logging__File" value="D:\ReleaseMgmt\logs\releasemgmt-.log"/>
  <env name="Display__TimeZone" value="America/Chicago"/>
  <env name="Pdf__QuestPdfLicense" value="Community"/>
  <env name="Auth__Oidc__Authority" value="https://login.microsoftonline.com/&lt;tenant-id&gt;/v2.0"/>
  <env name="Auth__Oidc__ClientId" value="&lt;client-id&gt;"/>
  <env name="Auth__Oidc__ClientSecret" value="&lt;client-secret&gt;"/>
  <logpath>D:\ReleaseMgmt\logs</logpath>
  <log mode="roll-by-size"/>
</service>
```

Then `C:\ReleaseMgmt\config\releasemgmt.exe install`, set the log-on account (Services console, Log On tab, `CORP\svc-releasemgmt`; Windows grants it "Log on as a service"), and `Start-Service releasemgmt`. Put `Auth:RoleMap` in `C:\ReleaseMgmt\config\appsettings.Production.json` and copy it into `C:\ReleaseMgmt\app` (section 4). The wrapper writes its own log and the app's console output to `D:\ReleaseMgmt\logs` too (`<logpath>`; the service account cannot write to the config folder): look there first when the service will not start.

**macOS** - a `launchd` daemon. Developers on a laptop can use `python start.py` instead (see the README); that is not a production mode. Create a dedicated standard account with no login shell for the service (`_releasemgmt` below; use your Mac administrator's usual method for service accounts), the directories as on Linux under `/usr/local/releasemgmt/` (app) and `/Library/Application Support/ReleaseMgmt/` (data, owned by `_releasemgmt`, `keys` and `secrets` mode 700), and the key-ring certificate with the same `openssl` commands as on Linux, written to `/Library/Application Support/ReleaseMgmt/keyring.pfx` (owner root, group of `_releasemgmt`, mode 640). Then `/Library/LaunchDaemons/com.example.releasemgmt.plist`:

```
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key><string>com.example.releasemgmt</string>
  <key>ProgramArguments</key>
  <array><string>/usr/local/share/dotnet/dotnet</string><string>/usr/local/releasemgmt/app/ReleaseMgmt.Api.dll</string></array>
  <key>WorkingDirectory</key><string>/usr/local/releasemgmt/app</string>
  <key>UserName</key><string>_releasemgmt</string>
  <key>RunAtLoad</key><true/>
  <key>KeepAlive</key><dict><key>SuccessfulExit</key><false/></dict>
  <key>StandardErrorPath</key><string>/Library/Application Support/ReleaseMgmt/logs/stderr.log</string>
  <key>EnvironmentVariables</key>
  <dict>
    <key>ASPNETCORE_ENVIRONMENT</key><string>Production</string>
    <key>ASPNETCORE_URLS</key><string>http://127.0.0.1:6080</string>
    <key>AllowedHosts</key><string>releases.example.com</string>
    <key>Db__Path</key><string>/Library/Application Support/ReleaseMgmt/releasemgmt.db</string>
    <key>Backup__Directory</key><string>/Library/Application Support/ReleaseMgmt/backups</string>
    <key>DataProtection__KeysDirectory</key><string>/Library/Application Support/ReleaseMgmt/keys</string>
    <key>Sync__Credentials__Directory</key><string>/Library/Application Support/ReleaseMgmt/secrets</string>
    <key>Attachments__Directory</key><string>/Library/Application Support/ReleaseMgmt/attachments</string>
    <key>Exports__Directory</key><string>/Library/Application Support/ReleaseMgmt/exports</string>
    <key>Logging__File</key><string>/Library/Application Support/ReleaseMgmt/logs/releasemgmt-.log</string>
    <key>Display__TimeZone</key><string>America/Chicago</string>
    <key>Pdf__QuestPdfLicense</key><string>Community</string>
    <key>Auth__Oidc__Authority</key><string>https://login.microsoftonline.com/&lt;tenant-id&gt;/v2.0</string>
    <key>Auth__Oidc__ClientId</key><string>&lt;client-id&gt;</string>
    <key>Auth__Oidc__ClientSecret</key><string>&lt;client-secret&gt;</string>
    <key>DataProtection__Certificate__Path</key><string>/Library/Application Support/ReleaseMgmt/keyring.pfx</string>
    <key>DataProtection__Certificate__Password</key><string>&lt;the PFX password&gt;</string>
  </dict>
</dict>
</plist>
```

The plist holds secrets: `sudo chown root:wheel` it and `sudo chmod 600` it. Start with `sudo launchctl bootstrap system /Library/LaunchDaemons/com.example.releasemgmt.plist`, restart with `sudo launchctl kickstart -k system/com.example.releasemgmt`, stop with `sudo launchctl bootout system/com.example.releasemgmt`. `KeepAlive` restarts the app when it exits with an error, as `Restart=on-failure` does on Linux.

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

The drill times only the part a script can do: from an empty host with the files already on it to the first healthy `/healthz` (Q-052d). The 30-minute target is for the whole procedure, so budget the parts around it too, and write your own measured figures into this table after the drill on the pilot host:

| Part of the restore | Budget | What makes it fit |
|---|---|---|
| Someone decides to restore and the operator starts | 5 min | an on-call rota that names who decides |
| A replacement host with the .NET runtime and the app installed (section 2, 3, 5) | 10 min | a standby host or VM image prepared **in advance**; building a server from nothing does not fit in 30 minutes |
| Fetch the newest backup and the `keys/`, `secrets/`, `attachments/` copies from off-box storage | 5 min | storage reachable from the replacement host; the time grows with the attachments' size, so measure it |
| Restore command, copies, start (the drill) | 1 min | measured: 3.6 s to 13.5 s |
| Checks in step 4 and 5 | 5 min | the sign-in account and the attachment to spot-check chosen beforehand |
| **Total** | **26 min** | |

## 8. Upgrading

1. Announce a short window; take a fresh backup (copy the newest file from `Backup:Directory`, or trigger by restarting is not needed) and copy `keys/` and `secrets/`.
2. Stop the service; keep the previous `publish` directory.
3. Replace the app directory with the new `publish` output (the data directories are elsewhere, so they are untouched), and copy your `appsettings.Production.json` back into it from its master copy if you use one (section 4).
4. If the release notes list a script in `db/upgrades/` that is newer than the database (Q-SEC-B8m), run each such script, in number order, with the
   service still stopped: `sqlite3 <Database path> < db/upgrades/NNN_name.sql`. Each script is one transaction, refuses to run against the wrong
   version, and ends by printing `PRAGMA foreign_key_check` (expect no rows) and `PRAGMA integrity_check` (expect `ok`). Anything else: restore the
   backup from step 1 and stop. If a script is missed, the app refuses to start and the log names the migrations it found ("created by an older build"). On a developer machine `start.py` does this step itself (backup first, then the scripts the database needs); on a server, run the scripts by hand as above.
5. Start the service. Schema migrations run automatically at startup; a failed migration stops the app with the error in the log, and nothing half-applied is served.
6. Verify `/healthz`, sign in, open a train.

| Script | Brings a database from | Change |
|---|---|---|
| `001_idp_identity_and_session_revocations.sql` | migrations `20260929110130_Schema` + `20260929110136_Triggers` | REOS-61/62: `Users.IdpIssuer`/`IdpSubject` (rebuilds `Users`), `SessionRevocations` |
| `002_webhook_urls_encrypted.sql` | migrations `20260930234604_Schema` + `20260930234616_Triggers` | REOS-73: records the new migrations only; the app encrypts `WebhookDestinations` at its next start (needs the key ring and `secrets/`, so restore both first) |
| `003_train_milestones.sql` | migrations `20261001030509_Schema` + `20261001030524_Triggers` | REOS-84: adds `TrainMilestones` and `IX_Milestones_Train` |
| `004_workspaces.sql` | migrations `20261001041930_Schema` + `20261001041948_Triggers` | REOS-104: workspaces. Adds `Workspaces` (with the default one) and `WorkspaceMembers`; every existing row moves into the default workspace and every user becomes a member of it with their role; rebuilds `Teams`, `Holidays`, `TrainTemplates`, `CommTemplateLibrary` and `ConnectorState` (names, days and connectors become unique per workspace); re-creates every trigger. Set `Workspace:DefaultName` before the first start if the default workspace should not be called "Default" |
| `005_workspace_roles.sql` | migrations `20261023032932_Schema` + `20261023032948_Triggers` | REOS-105: the Compliance-certifier, waiver-approver and freeze-override-approver rules read the role a person holds in the train's workspace. No table changes; a user without a membership joins the default workspace with their role first; re-creates every trigger |
| `006_release_model_deployments.sql` | migrations `20261023045651_Schema` + `20261023045703_Triggers` | REOS-107/109/110: release keys (`Workspaces.KeyPrefix`/`NextKeyNumber`; every existing release gets `RLS-n` in creation order), `ReleaseTrains` owner, team, branch, on-call and type, `BundledProducts.CatalogServiceId`; adds `ReleaseTypes`, `Environments`, `TrainEnvironments`, `CatalogServices`, `IngestKeys`, `Deployments`, `DeploymentEvents`; re-creates every trigger. Release types are seeded by the app at its next start |
| `007_telemetry_control_approvals_audit_chain.sql` | migrations `20261023111406_Schema` + `20261023111416_Triggers` | REOS-111 to 114: adds `MetricSamples`, `MetricRollups`, `ThresholdBreaches`, `DeploymentControlActions`, `ApprovalPolicies`, `ApprovalPolicyRequirements`, `Approvals`, `ApprovalDecisions`, `AuditChainHeads`; `Environments` threshold duration and canary window, `Deployments.StepStartedAt`/`AutoRollbackArmedAt`, `RunbookRuns` pause columns, `GateWaivers` decline columns, `AuditEvents.PrevHash`/`Hash`; rebuilds `SyncAlerts` (wider CHECKs, rows kept); re-creates every trigger. Existing audit rows are sealed by the app at its next start (hash chain, section 15), not by the script |
| `008_release_types_per_release.sql` | migrations `20261023142626_Schema` + `20261023142643_Triggers` | REOS-116: `ReleaseTypes.IsBeta` and `ReleaseTrains.BetaNumber`; the type called Beta becomes a Beta type; three triggers (a release has a type once its workspace has types, a Beta release has its number 1 to 3, a type that releases use cannot change its Beta setting). The app then gives every release without a type its workspace's Production type at its next start (audited, one row per release); to leave that out set `ReleaseTypes:SeedAtStart` to `false` |
| `009_clients_and_segments.sql` | migrations `20261024201636_Schema` + `20261024201644_Triggers` | REOS-118: adds `Clients` (empty to start); `ImportJobs` is rebuilt so its Kind CHECK accepts `Clients` (rows copied unchanged); four triggers (an On-Demand client has a CERT environment and a Standard client has none, and the environment belongs to the client's workspace). Nothing in the app needs a start-up step for it |
| `010_release_versions.sql` | migrations `20261024211850_Schema` + `20261024211901_Triggers` | REOS-117: adds `ReleaseVersions` and `UX_Trains_VersionSlot`, `ReleaseTrains.ReleaseVersionId`; `trg_Train_TypeRules_*` and `trg_Ws_ReleaseTrains_*` also read the version, `trg_Ws_ReleaseVersions_Update` is new (138 triggers with REOS-118's). No release is put in a version by the script. |
| `011_client_dates.sql` | migrations `20261025001928_Schema` + `20261025001939_Triggers` (after 010) | REOS-119: adds `ReleaseClientDates` (empty to start: an On-Demand client has no row until a planner schedules it or records a date, and a release shows the default offered date until then); four triggers (only an On-Demand client has its own date, a date keeps its release and client, rows stay in their workspace). Nothing in the app needs a start-up step for it |
| `012_template_governance.sql` | migrations `20261025024857_Schema` + `20261025024913_Triggers` (after 011) | REOS-122: adds `TemplateTasks` and `TemplateMilestones` (empty to start); `TrainTemplates` is rebuilt (rows copied unchanged; `UNIQUE (WorkspaceId, Name, VersionNo)` so a version keeps its template's name; new columns `CreatedByUserId`, `LastEditedByUserId` (empty for an existing template: nobody is recorded as its author), `VersionNo` (1), `SupersedesTemplateId`); `StageGates.TemplateLocked` (0) and `TrainMilestones.OffsetDays` (empty) are added in place; ten triggers are new (the approver is not the author, a locked Compliance gate, workspace boundaries) and one changes. No existing gate is locked and no existing milestone gets an offset. Nothing in the app needs a start-up step for it |

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
- Logs: the `Logging:File` path, one file a day (default `logs/` under the working directory; section 5 sets `/var/log/releasemgmt/`, `D:\ReleaseMgmt\logs\` or `/Library/Application Support/ReleaseMgmt/logs/`). Request lines for the ICS feeds are kept out; connector URLs and webhook URLs are never logged.

## 11. Pilot checklist (human-led, with the pilot RTE)

The pilot itself is run with people, step by step, from [PILOT_GUIDE.md](PILOT_GUIDE.md): how a team's spreadsheet maps onto the screens, who does what, the Governance Officer's acceptance checklist for the evidence pack, and the sign-off record. The same path is rehearsed by an automated test on every CI run (`PilotDressRehearsalTests`, section 12), so a failure in the software shows up before the pilot does. Tick these with the pilot RTE and the Governance Officer for one real train:

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

Verified by automated tests in this repository: the pilot's whole path for one release through the HTTP API with If-Match required (`PilotDressRehearsalTests`, REOS-54: template approval, train from template, change record, runbook, pasted checklists, four gates with segregation of duties, rehearsal and rollback attestation, Go/No-Go with a condition, live run, three messages, completion, hypercare, PIR, and the evidence pack and ZIP verified by SHA-256), the backup and restore code paths (`BackupTests`, green on Windows and macOS in CI), credentials never stored in the database, the health endpoint, configuration keys listed above (as read by the code), and the timed restore drill (see DR_DRILL.md).

Since 2026-09-30 also: export retention on a fake clock, the encrypted webhook addresses and the conversion of an older database, the key-ring certificate (a generated test certificate; on Windows the same test checks DPAPI), the connector host default and NAT64 prefixes. Run here on Linux; the same tests run on the Windows and macOS CI runners.

**Not verified on a real server yet:** the certificate commands in section 5, DPAPI under a real service wrapper account, the service definitions in section 5 (standard patterns, never run here on Windows Server, Linux or macOS servers), reverse-proxy WebSocket and logging configuration, OIDC against your identity provider, connectors against real Jira/ServiceNow tenants (CI never uses live tenants), and the pilot itself. Do these on a staging host first.

## 13. Keeping the CI action pins current (D39)

There is no Dependabot (decided 2026-09-30, REOS-76). The GitHub Actions in `.github/workflows/ci.yml` are pinned to commit SHAs with their tag as a comment (`uses: actions/checkout@<sha>   # v4`); they do not move by themselves. At least once a release train (and when GitHub announces a security fix for an action):

1. Check each action's releases page for a newer major or patch tag.
2. On a branch, change the line to the tag (`uses: actions/checkout@v5`), push, and let CI run.
3. Open the run, any job, the **Set up job** step: the runner logs `Download action repository 'actions/checkout@v5' (SHA:<40 hex>)`. That SHA is what the tag pointed to for this run.
4. Replace the tag with that SHA and keep the tag as the comment (`uses: actions/checkout@<sha>   # v5`), push, and check CI is green on both runners.
5. Run `npm audit` (in `src/ReleaseMgmt.Web` and `tests/e2e`) and `dotnet list package --vulnerable --include-transitive` at the same time; nothing reports them automatically either.

## 14. Deployment ingest keys and limits (REOS-110)

The deployment tool pushes deployments and events to `POST /api/v1/ingest/deployments` and `/api/v1/ingest/events` with an API key of its workspace (`Authorization: Bearer rk_…`).

- **Create** a key on the Admin side (`POST /api/v1/ingest-keys {name}`, RTE or Release Manager). The secret is in that one answer and nowhere else: only its SHA-256 is stored (`IngestKeys.KeySha256`), so a copy of the database or a backup holds nothing that can be used. Put it in the deployment tool's secret store. A lost key is replaced, not recovered.
- **Revoke** with `POST /api/v1/ingest-keys/{id}:revoke`; the key stops at the next request. Rotate by creating the new key, switching the tool, then revoking the old one. A key of an archived workspace stops working.
- **A bad key** (malformed, unknown, revoked) is 401 `InvalidIngestKey`, logged as a warning (`Ingest key refused … from <address>`, never the key) and audited as a host-wide `IngestKey/Rejected` row (the caller's address and the reason). Per address, `Ingest:FailedKeysPerMinute` (default 30) refusals are allowed a minute, after which that address gets 429 for the rest of the minute, and only the first `Ingest:RejectionAuditsPerMinute` (default 10) are audited, so a guessing client cannot fill the audit log. Behind a proxy that is not on the same host, `Proxy:KnownProxies` / `Proxy:KnownNetworks` (section 4) must be set, or every caller shares the proxy's address and one client's refusals slow all of them.
- **Rate limit** per key: `Ingest:RequestsPerSecond` (default 100) with a burst of `Ingest:Burst` (default 200); beyond it, 429 `RateLimited` with `Retry-After`. The buckets are in memory (a restart refills them). An event call carries up to 100 events, so the limit is on calls, not events.
- **Idempotency**: a deployment is identified by the tool's `externalId` (unique per workspace) and an event by its own `externalId` within the deployment; a replay answers 200 with `changed: false` / `duplicates: n` and writes nothing, not even an audit row.
- **Load**: 50 events/s for 3 s runs in `IngestApiTests.Fifty_events_a_second_are_accepted_without_slowing_the_app`; set `REOS_INGEST_LOAD_SECONDS` for a longer run (docs/LOAD_TEST.md section 6).


## 15. Telemetry, deployment control and automatic rollback (REOS-111, REOS-112)

- **Samples** arrive at `POST /api/v1/ingest/metrics` with the same ingest key as section 14 (up to `Telemetry:MaxSamplesPerCall`, default 200, per call; a replay is a no-op). A breach is judged inside the same transaction: a metric past its environment threshold for `ThresholdForSeconds` (default 120, set per environment) of *sample time*, not wall time, so a late batch does not invent one. Late samples older than the newest of their series are stored but not evaluated.
- **Retention**: samples are summarised into hourly rollups and deleted after `Telemetry:RetentionDays` (default 30) by the housekeeping worker (`Telemetry:MaintenanceMinutes`, default 60; `Telemetry:Maintenance:Enabled=false` stops it). A failed pass raises a `Telemetry/RetentionFailed` alert and is retried at the next pass.
- **Control webhook**: an environment names an outbound webhook destination (section 10, with its allowlist and address checks) and has its own signing key. Create or rotate it with `POST /api/v1/environments/{id}/control-webhook:signing-key` (Admin); the secret is in that one answer. It is kept in Data Protection files under `Sync:Credentials:Directory/control-webhooks/<workspace>/`, never in SQLite or appsettings. Back that directory up with the key ring (section 7).
- **Receiving side**: each call is a single POST (10 s timeout, `Control:Webhooks:TimeoutSeconds`; no retry, no redirects) with `X-ReleaseOS-Delivery` (the action id), `X-ReleaseOS-Timestamp` (Unix seconds) and `X-ReleaseOS-Signature: v1=<hex HMAC-SHA256(secret, "{timestamp}.{delivery}.{body}")>`. Verify the signature over the raw body, reject timestamps more than five minutes old, and treat a repeated delivery id as already done. `Control:Webhooks:AllowPrivateTargets=true` is for lab use only.
- **A control call that fails** (the tool refuses, times out, or the destination is unreachable) is a `Failed` row in the deployment's control history, a `Deployment/ControlFailed` alert and a live push, and the API answers 502 `ControlFailed`. Automatic rollback fires at most once per breach and stays armed when the call fails, so a person can retry; it disarms when a rollback is delivered. Calls left `Pending` by a host that stopped mid-call are closed as `Failed` within `Control:SweepSeconds` (default 60).
- A control call does not change the deployment's state: the deployment tool reports the new state back through the ingest API.

## 16. The audit hash chain and how to verify it (REOS-114)

- Every audit row is chained to the one before it in its workspace (host-wide rows form a chain of their own): `Hash = SHA-256(PrevHash + "\n" + canonical row)`, defined in `AuditChain.cs` and independently in `tools/verify_audit_chain.py`. A background sealer (`Audit:Seal:Enabled`, default true; `Audit:Seal:IntervalSeconds`, default 5) seals new rows in batches shortly after they commit, so a write never waits for it. Rows written before upgrade 007 are sealed at the first start.
- A full verification runs hourly (`Audit:Verify:IntervalMinutes`) and on demand: `GET /api/v1/audit/integrity?recheck=true`. The answer says `Verified` or `Failed` and, for a failure, the first row that does not verify and why. A failure raises an `Audit/IntegrityFailed` alert and a live push.
- **Offline check** of a database file or a backup copy, with no app running: `python3 tools/verify_audit_chain.py <file>` or `ReleaseMgmt.Api verify-audit <file>`. Exit 0: every chain verifies; 1: a chain is broken (the first bad row is printed); 2: the file could not be read.
- A tail deletion is detected because the chain head (last id and hash) is remembered in `AuditChainHeads`. The chain is not signed: someone with write access to the file who recomputes every later hash and the head is not detected by this check. Keep an off-box copy of the head hash from `GET /audit/integrity` (or a backup) if that matters to your audit.
- If sealing is failing, the alert says so; the log keeps being written and sealed later (`pendingRows` in the integrity answer says how far behind it is).
