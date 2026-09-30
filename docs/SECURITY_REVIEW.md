# Security and accessibility review (REOS-53, M8)

> **Superseded in part (2026-09-30):** the security scan in [SECURITY_SCAN.md](SECURITY_SCAN.md) changed cookie handling (always `Secure` and `__Host-` outside Development, SEC-B3), session lifetimes and sign-out (SEC-B4/B5) and added forwarded-header support (SEC-E5). Where this document says otherwise, the scan is current.

Scope of the story: the endpoint-to-role matrix as an automated test, antiforgery, attachment serving headers, secrets, and an accessibility pass (axe plus keyboard-only) over every screen. Decisions and judgement calls are Q-053a to Q-053h in `QUESTIONS.md`. Reviewed at commit 733b3b2 plus this change. Everything below was run on Linux with the in-process test host and Chromium only.

## 1. Summary

| # | Finding | Severity | Status |
|---|---|---|---|
| F1 | `POST /tasks/{id}:complete` and `:reopen` allowed any signed-in role, a Viewer included; a reopen decertifies a Certified gate | High (integrity of the gate evidence) | **Fixed** (Q-053a) |
| F2 | No CSRF defence beyond the framework's default cookie attributes; the multipart upload and every body-less action (`:capture-baseline`, `:start`, logout) had nothing between them and a cross-site form post | Medium (SameSite=Lax already stops the classic case) | **Fixed**: explicit `HttpOnly; SameSite=Lax` plus a cross-site guard (Q-053b) |
| F3 | A deactivated or deleted user's session cookie kept working for up to 14 days | Medium | **Fixed** (Q-053c) |
| F4 | Bad input answered 500 on four endpoints; every unreadable body or missing required query value became a 500 through the exception handler | Low (availability noise, hides real 500s) | **Fixed** (Q-053h) |
| F5 | No security headers: nothing stopped framing (clickjacking) or MIME sniffing outside the attachment and export downloads | Low | **Fixed** (`X-Frame-Options`, `X-Content-Type-Options`, `Referrer-Policy` on every response; Q-053f) |
| F6 | Attachment downloads relied on `attachment` and `nosniff` only | Low (defence in depth) | **Hardened**: `Content-Security-Policy: default-src 'none'; sandbox` added |
| F7 | Keyboard users saw no focus on bottom-rule inputs in inline forms and on the Analytics date inputs | Serious (WCAG 2.4.7), not found by axe | **Fixed** (Q-053g) |
| F8 | Webhook URLs (whose path is the token) are stored in clear in `WebhookDestinations.Url`, and so in every backup | Medium | **Not fixed, decision needed** (Q-053e); pinned by a test so it cannot spread |
| F9 | The key ring is plain XML on disk (existing Q-052e) | Medium | Not changed; restated for completeness |
| F10 | The role in the session cookie is fixed at sign-in while services read the role from the database | Low | **Not fixed** (Q-053c) |

Nothing else was found. Specifically: no credential in a committed configuration file, in any API response, in SQLite (including the WAL), in the audit trail, in alerts, in backups or in the log files, with the one exception in F8.

## 2. Endpoint-to-role matrix (`EndpointRoleMatrixTests`)

- Enumerates every `RouteEndpoint` in the running host's `EndpointDataSource` (180 in Development, 175 in Production). Each must have a row in the matrix table in the test file; an unlisted route fails with its name, and a row with no route fails too. The five dev-only routes (`/auth/dev-login`, `/api/v1/dev/*`) are asserted to be absent in Production.
- 900 role checks: each route as anonymous, Viewer, RTE, ReleaseManager, GovernanceOfficer. Denied must be exactly 401 (anonymous) or 403 (signed in). Allowed must be neither, and never a 5xx.
- The allowed sets are typed into the test from PROJECT_SCOPE section 1 (D14, D32: ReleaseManager is a superset of RTE and additionally records Go/No-Go; RTE and ReleaseManager together are the v1 "Admin"; freeze-override and template approval are ReleaseManager or GovernanceOfficer; waivers are GovernanceOfficer). They are not read from `Policies.cs`, so a policy change that contradicts the scope fails.
- There is no Contributor role and no separate Admin role; see Q-053d.
- `Owned_actions_refuse_people_who_are_neither_owner_nor_planner` gives the "owned" routes real rows: task complete/reopen, gate start/certify/fail/reopen (including RTE and RM refused on a Compliance gate), run steps. `Audit_and_evidence_reads...` covers the two Read routes that check a role inline.
- Result: passes for every endpoint after F1 and F4 were fixed. The first run failed on the four 500s (F4); F1 was found by reading each route's handler while classifying it, and the Owned test was written against the fixed code to pin it (it was not run against the unfixed code).

**Not verified by this test:** that an allowed role can actually complete the business action (only that authorization lets it through); the database-side backstops (SoD triggers) are covered by the Infrastructure trigger suite, not here. The SignalR hub is checked for 401 and for negotiate, not for what it pushes to whom (`Clients.All` broadcasts carry ids and versions only, by design D7).

## 3. Antiforgery (`CsrfTests`)

Design and reasoning: Q-053b. Summary of what was reviewed and tested:

- Authentication is a cookie (`releasemgmt.auth`). It is `HttpOnly` and `SameSite=Lax`; the tests read the `Set-Cookie` header. `Secure` follows the request scheme (see 6).
- `CrossSiteRequestGuard` refuses cross-site state changes before authentication for `/api`, `/auth` and `/hub`. Tested: `Origin` of another site, `null`, `file://`, a look-alike host (`localhost.evil.example`), `Sec-Fetch-Site` cross-site and same-site; a refused request leaves no row and no audit event. Every one of the 97 POST/PUT/PATCH/DELETE routes (Development), the SignalR negotiate call, the multipart upload, login and logout are enumerated from the route table and each is refused when cross-site. Same-origin browsers, non-browser clients and configured `Security:AllowedOrigins` pass. Reads are unaffected (and there is no CORS policy, so another site cannot read them).
- No GET changes state (reviewed through the route table; all 81 GET routes are reads, downloads, feeds, health, sign-in config or the SPA fallback).
- Content types: a cross-site HTML form can send `multipart/form-data`, `application/x-www-form-urlencoded` or `text/plain`. Body-less action routes and the upload accept those, which is why the guard exists; JSON-bodied routes reject them anyway.
- `DisableAntiforgery()` on `POST /attachments` was a no-op (the antiforgery middleware is not in the pipeline); left in place, harmless.
- The OIDC callback (`/signin-oidc`, a cross-site form post from the IdP) is deliberately outside the guarded prefixes.

**Not verified:** the OpenID Connect flow end to end (no IdP in the test environment): state and nonce validation rely on the framework handler's defaults; SignalR WebSocket upgrade (a GET) is not Origin-checked by the server, it is protected by the connection token that only the guarded negotiate POST returns; behaviour behind a reverse proxy that rewrites `Host` was reasoned about (`Sec-Fetch-Site` decides) and not run.

## 4. Attachment and download headers (`AttachmentHeaderTests`, plus the existing tests)

Every attachment download carries `Content-Disposition: attachment` (RFC 6266 name, path and control characters, quotes, `;`, bidi overrides and leading dots removed; hostile names tested through the raw header), `X-Content-Type-Options: nosniff`, `Content-Security-Policy: default-src 'none'; sandbox`, `Cache-Control: private, no-store`, `X-Content-SHA256`, no `Accept-Ranges`, `X-Frame-Options: DENY`. The content type is the stored type reduced to `type/subtype` (parameters dropped, lower-cased) or `application/octet-stream` when it is not a plain media type (ten inputs tested, including control characters, `*/*` and none). Active types (`text/html`, `image/svg+xml`) are still served as sent; the headers make them inert (Q-053f). Storage is outside `wwwroot`, the file is not reachable as a static file or by a guessed path, and anonymous requests get 401. Export-job and grid-export downloads have their own header tests (`PdfExportTests`, `ImportExportTests`, `AuditTests`).

**Not verified:** behaviour of a real browser opening a hostile file (the headers were asserted, not exercised in a browser); antivirus or content scanning (none exists, out of scope).

## 5. Secrets (`SecretsTests`)

Plants a connector user name and secret, a webhook address whose path is a token, and an ICS feed token; runs a connector test and a sync against an upstream that echoes the credentials and the `Authorization` header back in its error body; then looks for every planted value (and the Base64 of `user:secret`) in:

- every response of every GET route without required parameters, as Release Manager and Governance Officer (about 130 responses), plus the audit list and CSV, the audit grid export and the alerts;
- every text value of every SQLite table, and the raw bytes of `app.db`, `app.db-wal`, `app.db-shm`, the backup files and the encrypted credential file;
- the audit rows (the credential change is audited by kind only), alerts, notifications, dispatches;
- the Serilog files.

None contains any of them, except that the ICS token appears once, in the response that minted it (by design), and `IcsTokens` holds only a 64-hex SHA-256. Also tested: the webhook address exists only in `WebhookDestinations.Url` (F8) and is removed with the row; committed `appsettings*.json` contain no value under a key that looks like a secret; an unexpected 500 carries no table name, exception type, stack trace or path; `/auth/config` and `/healthz` carry nothing secret. The OIDC client secret is read from configuration or the environment only (`Auth:Oidc:ClientSecret`), never committed.

A repository grep for `secret|password|token|bearer|apikey|credential` in `src`, `start.py` and `tools` found only the code paths covered above and documentation comments; no literal credential.

**Not verified:** the key ring and `secrets` directory permissions on a real Windows Server or Linux install (an ACL, not code; runbook); log content at levels above Information or with `Microsoft.AspNetCore` raised (it is Warning in `appsettings.json`; if someone raises it to Information, request logging would print ICS feed URLs, whose path is the token); the OS secret store (only Data Protection with files is implemented); production log shipping.

## 6. Accessibility (`a11y-screens.spec.ts`)

Method and scope: Q-053g. 6 tests, all green; the existing `a11y.spec.ts` and the axe checks in the live, calendar, audit, governance, import/export, My work and sync specs also pass unchanged.

- Every `Route.view` (trains, admin, work, inbox, audit, templates, sync, connectors, library, analytics, calendar, importexport) is opened with Tab and Enter only, then scanned with axe (`wcag2a/2aa/21a/21aa/best-practice`, no rules disabled) in light and in dark. No serious or critical violation on any of them.
- Also scanned: the train in Plan, Rehearsal and Live mode, the gate Inspector, the bulk (Paste tasks) drawer and the Communicate drawer, and Trains, Analytics and Sync health with the manual light/dark override chosen from the keyboard.
- On each of those pages the whole tab order is walked: every stop has an accessible name, a non-empty box and a visible focus indicator; no keyboard trap.
- Found and fixed: F7. Axe cannot see it (contrast of an absent indicator is not measured); the tab-order walk did.

**Not verified:** real assistive technology (VoiceOver, NVDA, JAWS); Firefox and Safari; Windows high-contrast or forced-colours; zoom to 400% and reflow (WCAG 1.4.10), text spacing, touch target size; screen states that need special data (an active live run, an open sync alert list with many rows, the import preview with errors, template editing form) beyond what the other specs scan; Viewer, Release Manager and Governance Officer navigation (the tour runs as an RTE, who sees every link); the `j`/`k`/`Enter` grid shortcuts (covered by their own specs, not by this walk); automated axe finds a minority of WCAG issues, so "no serious violations" is the acceptance test and not a conformance statement.

## 7. What was changed in product code

| File | Change |
|---|---|
| `src/ReleaseMgmt.Api/Endpoints/LifecycleEndpoints.cs` | F1: `TaskDenial` |
| `src/ReleaseMgmt.Api/Auth/RequestSecurity.cs` (new) | F2 `CrossSiteRequestGuard`, F5 `SecurityHeaders`, F3 `SessionValidator` |
| `src/ReleaseMgmt.Api/Program.cs` | registers the three, explicit cookie `HttpOnly` and `SameSite=Lax` |
| `src/ReleaseMgmt.Api/DbRuleExceptionHandler.cs` | F4: `BadHttpRequestException` keeps its 4xx |
| `src/ReleaseMgmt.Api/Endpoints/SessionEndpoints.cs`, `SyncDevEndpoints.cs`, `src/ReleaseMgmt.Infrastructure/Services/AdminService.cs` | F4: null payloads answer 400 or 422 |
| `src/ReleaseMgmt.Api/Endpoints/AttachmentEndpoints.cs` | F6: CSP on downloads |
| `src/ReleaseMgmt.Web/src/app.css` | F7: focus rule restated at the end of the file |

New configuration keys (both optional): `Security:AllowedOrigins` (origins allowed to make cross-site writes, default none) and `Auth:SessionRecheckSeconds` (default 10). No schema change, no change to `db/schema.sql`, no new package.

## 8. Recommended next steps (decisions for the product owner)

1. F8: keep webhook URLs in SQLite, or move them to the credential store (Q-053e).
2. Set `Secure` on the session cookie explicitly for production (today it follows the request scheme, so behind a TLS-terminating proxy that forwards plain HTTP the cookie is not `Secure`), add HSTS at the terminator, and choose an idle timeout shorter than the 14-day sliding default.
3. F10: decide whether a role change at the IdP should end the session (Q-053c).
4. A CSP for the SPA (needs a nonce or hashes for the inline styles), when the design allows.
5. Rate limiting on `POST /auth/login` and `dev-login` is not present (dev-login is Development only).
