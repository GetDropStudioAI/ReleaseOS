# Security scan: OWASP API Top 10 and beyond

**Status: in progress.** Sections 1 and 2 are final; results, fixes and the not-verified list are added as the reviews land.

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
