# Security scan: OWASP API Top 10 and beyond


Date: 2026-09-30. Scope: every file in the repository at the head of `main`, which the `Team` branch was synced to during the scan (see section 1).
This builds on the REOS-53 review ([SECURITY_REVIEW.md](SECURITY_REVIEW.md)); per-area detail is in [docs/security/](security/).

## 1. Scope and method

- **Branch.** The request named `Team`. At the start `Team` was 47 commits behind `main` (a strict ancestor: it lacked all of Sprint 4, including the
  connectors, webhooks, CSV import and PDF/ICS exports, which is where SSRF and resource-consumption risks live). The scan therefore ran on `main`.
  During the scan the user asked for the branches to be synced: `Team` carried one docs-only commit (accessibility and UX reviews) that was merged into
  `main`, and `Team` was fast-forwarded, so both branches now hold the scanned code plus the fixes below.
- **Automated.** Dependency audit (NuGet direct and transitive, npm, OSV), secret scan of the full git history (gitleaks), SAST (Semgrep with the
  C#, TypeScript, React, Python, GitHub Actions, secrets, OWASP Top 10 and security-audit rule packs), and the .NET security analyzers
  (`AnalysisModeSecurity=All`).
- **Manual.** Four reviewers in parallel (authorization; authentication and sessions; SSRF and third-party API consumption; resource consumption,
  injection and file handling), plus configuration, inventory, supply chain and logging reviewed by the lead. A finding counts only when a test
  reproduces it; each fix ships with that test, which failed before the fix and passes after it.

## 2. Catalogue

### 2.1 The ten requested checks, mapped to the OWASP API Security Top 10 (2023)

Some of the names asked for come from the 2019 edition; the 2023 edition merged or renamed them. All ten are covered.

| Requested | OWASP API 2023 | Notes |
|---|---|---|
| BOLA, Broken object level authorization | API1 | Per-object checks, not just per-role |
| Broken authentication | API2 | Also Top 10:2025 A07 |
| Excessive data exposure | API3 Broken Object Property Level Authorization | 2019 API3 merged with Mass Assignment (2019 API6) into 2023 API3; both halves checked |
| Unrestricted resource consumption | API4 | |
| Broken function level authorization | API5 | |
| Unrestricted access to sensitive business flows | API6 | |
| SSRF, Server side request forgery | API7 | In the web Top 10:2025 SSRF is now part of A01 |
| Security misconfiguration | API8 | Also Top 10:2025 A02 |
| Improper assets management | API9 Improper Inventory Management | Renamed in 2023 |
| Unsafe consumption of APIs | API10 | |

### 2.2 Additional classes evaluated

From the [OWASP Top 10:2025](https://top10.owasp.org/2025/) (web application list), beyond what the API list already covers:

| Top 10:2025 | What it adds here |
|---|---|
| A03 Software Supply Chain Failures | Vulnerable or deprecated packages, lock files, CI workflow permissions and action pinning, build provenance |
| A04 Cryptographic Failures | Token entropy and hashing, Data Protection key ring at rest, cookie flags, TLS assumptions |
| A05 Injection | SQL, CSV/formula, HTTP header (CRLF), log, path traversal, zip slip, XSS, XXE, template/token injection |
| A06 Insecure Design | Local development launcher reachable by other sites (DNS rebinding), trust boundaries between roles |
| A08 Software or Data Integrity Failures | Evidence pack hashing, append-only audit, unsafe deserialization, backup integrity |
| A09 Security Logging and Alerting Failures | Sign-in, sign-out and access-denied events; secrets kept out of logs |
| A10 Mishandling of Exceptional Conditions | Fail-closed error handling, no stack traces to clients, no swallowed exceptions |

From [OWASP ASVS 5.0](https://github.com/OWASP/ASVS) (May 2025), the chapters that apply to this stack: V1 Encoding and Sanitization, V2 Validation
and Business Logic, V3 Web Frontend Security (CSP, framing, CORS), V4 API and Web Service, V5 File Handling, V6 Authentication, V7 Session
Management, V8 Authorization, V10 OAuth and OIDC, V11 Cryptography, V12 Secure Communication, V13 Configuration, V14 Data Protection, V15 Secure
Coding and Architecture, V16 Security Logging and Error Handling. Not applicable: V9 Self-contained Tokens (no JWTs are issued) and V17 WebRTC.

Stack-specific checks drawn from the [OWASP .NET Security Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/DotNet_Security_Cheat_Sheet.html)
and [CSV Injection](https://owasp.org/www-community/attacks/CSV_Injection): SignalR hub authorization and group membership, antiforgery for cookie
auth, `Content-Disposition` and download headers, XLSX (ClosedXML) parsing of untrusted files, QuestPDF hyperlinks, raw SQL through
`SqliteCommand`, host header validation, DNS rebinding against loopback services, Vite dev server exposure, and the `start.py` control channel.

## 3. Result

**39 findings, 38 distinct defects (SEC-A3 and SEC-B6 are the same one, found by two reviewers): 3 High, 23 Medium, 12 Low.**
Of the 38, 35 are fixed, 2 are partly fixed, and 1 is not fixed and waits on a decision. Every fix came with a test that failed before it and passes after it.
Nothing Critical was found. No SQL injection, XSS, XXE, path traversal, zip slip, unsafe deserialization, SSRF to an internal address through the
real HTTP clients, or known-vulnerable dependency was found.

### 3.1 By requested category

| OWASP API 2023 | Findings | Verdict now |
|---|---|---|
| API1 BOLA | SEC-A3/B6 (a deactivated user's calendar feed kept working) | Fixed. Every id-taking route checked; per-user objects (feed tokens, previews, UI state, notifications) pinned against another user of the same role |
| API2 Broken authentication | SEC-B1 (High), B2 to B13, E1 | Fixed except B8 partly (binding accounts to the IdP subject needs a schema change) |
| API3 Excessive data exposure / mass assignment | SEC-A1 (High: approver could name the requester of a freeze override), A2 | Fixed. Observation: Viewers see every user's email (Q-SEC-A5) |
| API4 Unrestricted resource consumption | SEC-D1 (High: one request could exhaust memory), D2 to D4, D7 to D10, C3 | Fixed except D7 partly (per-field lengths) and D10 (feed brake behind an address-less proxy) |
| API5 Broken function level authorization | none new | Role matrix (REOS-53) covers all 180 routes; the SignalR hub exposes no client-callable methods (pinned) |
| API6 Sensitive business flows | SEC-A1, A2, A4 (race: a schedule item posted twice), D8 | Fixed |
| API7 SSRF | SEC-C1 (NAT64 / IPv4-in-IPv6 forms), C2 | Fixed. The connect-time guard, redirects and DNS rebinding were proven through the three real HTTP clients |
| API8 Security misconfiguration | SEC-E1, E2, E5, E6, B3, B12 | Fixed |
| API9 Improper inventory management | none | The role-matrix test is a living inventory: an unlisted route fails the build |
| API10 Unsafe consumption of APIs | SEC-C3, C4, C5 | Fixed |

### 3.2 Additional classes

| Top 10:2025 | Findings | Verdict now |
|---|---|---|
| A03 Supply chain | SEC-E3 | Dependencies clean; CI token least privilege; actions pinned to commit SHAs (keeping them current: Q-SEC-E2) |
| A04 Cryptographic failures | SEC-B3, B11, B12 | Fixed |
| A05 Injection | SEC-D5 (log), D6 (Slack/Teams markup) | Fixed; SQL, XSS, header, path, zip, XXE, ICS and template injection checked clean |
| A06 Insecure design | SEC-E1, E2 (DNS rebinding), A1 | Fixed |
| A08 Integrity | SEC-A2, C4 | Fixed; no unsafe deserialization |
| A09 Logging and alerting | SEC-E4, B10, D5 | Fixed |
| A10 Exceptional conditions | SEC-B13, E7 (backups failed under write load) | Fixed; no swallowed exceptions |

## 4. All findings

Detail, evidence and reproduction tests for each are in the per-area reports: [authorization](security/scan-authz.md),
[authentication](security/scan-authn.md), [SSRF and third-party APIs](security/scan-ssrf.md),
[resources and injection](security/scan-resources-injection.md), [configuration and supply chain](security/scan-config-supplychain.md).

| ID | Finding | Sev | Status |
|---|---|---|---|
| SEC-B1 | Anonymous "sign in as any role" existed wherever the environment was Development; `start.py` forced Development even over an explicit choice | High | Fixed |
| SEC-A1 | Freeze-override two-person rule: the approver could name a requester who never asked | High | Fixed |
| SEC-D1 | Comm preview (any role): quadratic parser and unbounded errors, one request could exhaust memory | High | Fixed |
| SEC-A2 | Re-completing a task rewrote who completed it, defeating segregation of duties | Medium | Fixed |
| SEC-A3 / B6 | A deactivated user's calendar feed kept working | Medium | Fixed |
| SEC-B2 | Dev sign-in answered other machines and DNS-rebinding pages | Medium | Fixed |
| SEC-B3 | Session cookie not Secure behind the TLS proxy; no `__Host-` prefix | Medium | Fixed |
| SEC-B4 | No absolute session lifetime; 14-day sliding cookie | Medium | Fixed (60 min idle, 12 h absolute) |
| SEC-B5 | Sign-out did not end the session on the server | Medium | Fixed (in memory; a restart forgets, Q-SEC-B5) |
| SEC-B7 | IdP role claims bypassed `Auth:RoleMap` | Medium | Fixed |
| SEC-B8 | An unverified email signed in as the existing account | Medium | Partly fixed (subject binding needs a schema change) |
| SEC-B9 | Every IdP identity became a Viewer | Medium | Fixed (refused unless `Auth:DefaultRole=Viewer`) |
| SEC-B11 | A Development copy of a backup could mint Production sessions | Medium | Fixed |
| SEC-B12 | Key ring readable by every local account | Medium | Fixed (Linux/macOS) |
| SEC-B13 | API calls without a session got an IdP redirect (or 500), not 401 | Medium | Fixed |
| SEC-C1 | IPv6 forms carrying a private IPv4 address passed the SSRF check | Medium | Fixed |
| SEC-C3 | Webhook responses read without a cap; a slow 200 was re-sent | Medium | Fixed |
| SEC-C4 | Jira/ServiceNow status text stored and audited forever with no bound | Medium | Fixed |
| SEC-D2 | Import suggestions: unbounded edit distance (one cell cost 83 s of CPU) | Medium | Fixed |
| SEC-D3 | Import width and error list unbounded | Medium | Fixed |
| SEC-D4 | Checklist paste: unbounded line length | Medium | Fixed |
| SEC-D6 | Slack/Teams markup injection (`<!channel>`, links) | Medium | Fixed |
| SEC-D7 | No request-body limit below ~30 MB | Medium | Fixed (1 MiB global; per-field limits on every JSON body since REOS-66, Q-SEC-D1) |
| SEC-E1 | DNS rebinding reached the Development app | Medium | Fixed (outside Development the app refuses to start while `AllowedHosts` is `*` or empty, REOS-68) |
| SEC-E4 | Sign-in, sign-out and access-denied events not logged | Medium | Fixed |
| SEC-E5 | Forwarded headers ignored behind the proxy; no HSTS | Medium | Fixed |
| SEC-A4 | Race: one schedule item could post to the channel twice | Low | Fixed |
| SEC-B10 | Calendar tokens could reach the log | Low | Fixed |
| SEC-C2 | Unicode-digit host names hid IP literals at save time | Low | Fixed |
| SEC-C5 | CR/LF in an OAuth token reached the wire as a header | Low | Fixed |
| SEC-D5 | Log injection through a webhook channel name | Low | Fixed |
| SEC-D8 | Export jobs could be queued without bound | Low | Fixed (retention still open, Q-050c) |
| SEC-D9 | Stored previews unbounded per user and never purged | Low | Fixed |
| SEC-D10 | Calendar-feed brake behind a proxy that sends no client address | Low | Fixed (REOS-67: a valid token is always served; only failed lookups are braked, Q-SEC-D5) |
| SEC-E2 | `start.py` control channel accepted any Host | Low | Fixed |
| SEC-E3 | CI token default permissions; actions on mutable tags | Low | Fixed (keeping pins current: Q-SEC-E2) |
| SEC-E7 | Backups failed with "database is locked" under write load (no busy timeout, no retry) | Low | Fixed |
| SEC-E6 | No Content-Security-Policy on the web page | Low | Fixed |

## 5. Decisions for the product owner

Recorded in [QUESTIONS.md](QUESTIONS.md), each with the safest default already built: Q-SEC-A1 to A5 (override-request expiry, Viewers seeing
emails, import-preview visibility), Q-SEC-B1 and B4 to B9 (whether a pilot may ever run through `start.py`, session lengths, a durable sign-out list,
feed-link expiry, IdP subject binding, unmapped users), Q-SEC-C1 to C3 (NAT64 prefixes, status-name bound, connector host allowlist), Q-SEC-D1 to D6
(per-field limits, export retention, the feed brake), Q-SEC-E1 to E3 (production `AllowedHosts`, keeping action pins current, inline style in the CSP).
The most important: **Q-SEC-E1** (production must set `AllowedHosts`), **Q-SEC-B1** (never run a pilot in Development) and **Q-SEC-C3** (restrict
connectors to the real Atlassian and ServiceNow hosts).

## 6. Verification

On `main` after all fixes: `dotnet build` 0 warnings; Domain 307, Infrastructure 228, Api 619 (1 skipped: the SEC-D10 reproduction), DR drill 2;
schema oracle 82/82; tooling tests OK; web build and unit tests; Playwright 78/78, including the new CSP walk of the built app.

CI: run 86 on head `891ccbc` green on `windows-latest` and `macos-latest` (build, all tests, oracle, tooling, Playwright on both), as were runs 83 to 85.

## 7. Not verified

- A real identity provider end to end (OIDC flows are tested with the handler events, not a live IdP).
- A real reverse proxy, a NAT64 network, a real Slack workspace or Teams channel, Safari and Firefox for the CSP.
- More than one app instance (the design is single-instance, D3).
- No dynamic scan (OWASP ZAP or similar) and no manual penetration test. Recommended before exposing the app beyond the corporate network.
