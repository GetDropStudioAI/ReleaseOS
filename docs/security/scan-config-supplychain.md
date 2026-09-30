# Security scan E: configuration, inventory, supply chain, logging and error handling

OWASP API8 Security Misconfiguration, API9 Improper Inventory Management; Top 10:2025 A02, A03, A06, A08, A09, A10; ASVS 5.0 V3, V13, V15, V16.
Reviewed by the lead (the reviewer planned for this area was not started). Base `main` at `c9a1180`; fixes on `main`.

## 1. Findings

| ID | Finding | OWASP | Severity | Status |
|---|---|---|---|---|
| SEC-E1 | DNS rebinding reaches the Development app. `AllowedHosts` was `*` and the cross-site guard compares `Origin` with the request's own `Host`, so a page on attacker.example that re-points its name to 127.0.0.1 is "same origin". In Development (how `start.py` runs the app) the dev sign-in is open, so the page could sign itself in as any role and read or change everything | API8, A06; ASVS V3.4, V13 | Medium | Fixed: loopback-only `AllowedHosts` in Development (`HostFilteringTests`); also defended by SEC-B2 |
| SEC-E2 | The `start.py` control channel (127.0.0.1:5099: status, reset, exit) accepted any `Host`. Its token is handed to the Vite-served script, so a rebinding page could read it and stop or restart the app | API8, A06 | Low | Fixed: non-loopback `Host` answered 421 (`test_start.py` ControlChannelHost). Vite 8 already refuses foreign hosts (checked live: 403) |
| SEC-E3 | CI workflow token had the repository default permissions; actions were referenced by mutable tags (`@v4`), not commit SHAs (Semgrep `github-actions-mutable-action-tag`) | A03 | Low | Fixed: `permissions: contents: read`, and every action pinned to the commit SHA GitHub itself resolved its tag to (the runner log of CI run 71), tag kept as a comment. Keeping pins current (Dependabot) is Q-SEC-E2 |
| SEC-E4 | Security events were not logged. The Serilog override for `Microsoft.AspNetCore` (Warning) hid "Authorization failed", and nothing logged a sign-in, a sign-out or a session ended for a deactivated user | A09; ASVS V16.3 | Medium | Fixed: `ReleaseMgmt.Security` category (`SecurityEventLogTests`) |
| SEC-E5 | No forwarded-header handling. Behind the runbook's TLS-terminating proxy every request came from the proxy over http: the dev sign-in's "this machine only" check passed for remote clients of a same-host proxy, the ICS brake counted every client as one, OIDC built `http://` return addresses, and HSTS was never sent | API8, A02; ASVS V12, V13 | Medium | Fixed: `X-Forwarded-For/Proto` honoured from loopback or `Proxy:KnownProxies`/`KnownNetworks` only; HSTS outside Development (`ProxyAndBrowserHardeningTests`, 6 tests) |
| SEC-E6 | The web page had no Content-Security-Policy (only downloads had one), no `Permissions-Policy` and no `Cross-Origin-Opener-Policy`. No XSS was found, so this is defence in depth | API8, A02; ASVS V3.4 | Low | Fixed: strict page CSP (no inline or evaluated script, no plugins, no framing). `csp.spec.ts` walks every screen of the built app with no violation, sees SignalR open its socket, and proves injected inline script is blocked |
| SEC-E7 | Backups failed under write load. The online backup opened its own connections without the `busy_timeout` CLAUDE.md requires on every connection and did not retry, so a lock held for a moment by a writer failed the 15-minute backup at once (CI run 71, Windows) | A10; ASVS V16.5 | Low | Fixed: busy timeout on both connections and the copy retried for up to 30 s before the backup alert fires (`BackupBusyTests` reproduces it on Linux with a held lock) |

Also fixed during the scan: a test race in `commlibrary.spec.ts` (CI run 74, Windows: the spec clicked away while a save was in flight). Not a security finding, fixed at the user's request: `start.py` never refreshed `node_modules` after a manual `git pull`
(Vite failed to resolve `@microsoft/signalr`). It now reinstalls whenever `package-lock.json` changed since the last `npm ci`.

## 2. Automated scans

| Tool | Scope | Result |
|---|---|---|
| `dotnet list package --vulnerable --include-transitive` | all 8 projects | no known-vulnerable package |
| `dotnet list package --deprecated` / `--outdated` | all projects | only test tooling: xunit 2.9.3 marked legacy (successor xunit.v3), older test SDK and coverlet |
| `npm audit` | `src/ReleaseMgmt.Web`, `tests/e2e` | 0 vulnerabilities |
| OSV-Scanner 2.2.3 | every lock file in the tree | no issues |
| gitleaks 8.28 | full history, all branches (93 commits) | 1 hit: the deliberate sentinel in `SecretsTests.cs` (false positive) |
| Semgrep 1.178 (C#, TypeScript, React, Python, GitHub Actions, secrets, OWASP Top 10, security-audit packs; 391 rules, 264 files) | tracked files | 8 results: 5 mutable action tags (SEC-E3); `csharp-sqli` in `AnalyticsService.cs:96` (false positive: fixed embedded SQL, values bound as parameters); 2 `dynamic-urllib` in `start.py` (false positive: loopback URLs only). 60 C# files only partly parsed (newer syntax), covered by the analyzers below |
| .NET analyzers, `AnalysisModeSecurity=All` | solution | CA2100 x19 (all constant SQL, the pragma interceptor and the analytics runner: false positives), CA5394 x36 (`System.Random`, all in tests and seeders, no security use) |

## 3. Checked, no issue

- **Inventory (API9).** `EndpointRoleMatrixTests` enumerates every route from the running app (180 in Development, 175 in Production) and fails on any
  route that is not listed with its allowed roles, or on a stale row, so the inventory cannot drift. Development-only routes are asserted absent in
  Production, and since SEC-B1 they also need `Auth:DevLogin:Enabled`. No OpenAPI/Swagger endpoint, no versioned duplicates (everything is `/api/v1`).
- **Exceptions (A10).** Unhandled exceptions become a generic 500 ProblemDetails with no stack trace outside Development; trigger aborts become 422
  `DbRule`; bad requests 4xx (REOS-53). The four bare `catch` blocks all rethrow; no swallowed exceptions.
- **Deserialization (A08).** No `BinaryFormatter`, `TypeNameHandling` or polymorphic JSON anywhere.
- **Static files.** `wwwroot` holds only the built SPA; no source maps are emitted.
- **Local listeners.** The API, Vite and the control channel bind 127.0.0.1 only; control-channel writes are POST with a per-run token compared in
  constant time.
- **Credential files.** Written 0600 (and the key ring 0700/0600 since SEC-B12).
- **CI triggers.** `pull_request`, not `pull_request_target`; no secrets in the workflow; uploaded artifacts are test reports and app logs from a
  throwaway database.

## 4. Not verified

- Behaviour behind a real reverse proxy (IIS, nginx, a load balancer): tested with simulated peers and headers only.
- CSP in Safari and Firefox (tested in Chromium). If a browser refuses the WebSocket under `connect-src 'self'`, SignalR falls back to another
  transport under the same directive.
- No dynamic scan (OWASP ZAP or similar) against a running instance, and no manual penetration test.
