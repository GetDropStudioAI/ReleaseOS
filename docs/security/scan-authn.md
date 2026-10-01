# Security scan B: authentication and session management

Scope: OWASP API2:2023 Broken Authentication, OWASP Top 10:2025 A07 Authentication Failures and A04 Cryptographic Failures (as they apply to sign-in, sessions and tokens), ASVS 5.0 V6 (authentication), V7 (session management), V9 (self-contained tokens), V10 (OAuth and OIDC). Base: `main` at c9a1180. It builds on REOS-53 (`docs/SECURITY_REVIEW.md`: cross-site request guard, `HttpOnly; SameSite=Lax`, `SessionValidator`), which was not redone.

Method: read `Program.cs`, `src/ReleaseMgmt.Api/Auth/*`, the calendar (ICS) endpoints and token service, the SignalR hub, `start.py`, `launchSettings.json`, `appsettings*.json`, the CI workflow, the e2e launcher, the DR drill and load-test launchers. Every finding below was reproduced by a test that **failed against c9a1180** before the fix was written (`tests/ReleaseMgmt.Api.Tests/AuthnSecurityTests.cs`, and `tests/tools/test_start.py` class `BackendEnvironment` for `start.py`). The OpenID Connect sign-in was exercised by running the host's own configured `OnTokenValidated` delegate with hand-made claims: there is no identity provider in the test environment.

## 1. Findings

| # | Title | OWASP / ASVS | Severity | Status |
|---|---|---|---|---|
| SEC-B1 | Development alone decided whether "sign in as any role" existed; `start.py` forced Development over an explicit choice, and dev-login stayed beside a configured identity provider | A07, API2, A02; ASVS V6.3 | High | Fixed (Q-SEC-B1) |
| SEC-B2 | dev-login answered any caller and any Host name: another machine, or a web page through DNS rebinding | A07, API2; ASVS V6.3 | Medium | Fixed (Q-SEC-B1) |
| SEC-B3 | Session cookie not `Secure` behind the runbook's TLS-terminating proxy; no `__Host-` prefix | A04, A07; ASVS V7.1, V3.3.1 | Medium | Fixed |
| SEC-B4 | No absolute session lifetime; 14-day sliding idle timeout, so an active user was never sent back to the identity provider | A07; ASVS V7.3.1, V7.3.2 | Medium | Fixed (Q-SEC-B4) |
| SEC-B5 | Sign-out did not end the session on the server; a copy of the cookie kept working | A07; ASVS V7.4.1 | Medium | Fixed; durable since REOS-62 (Q-SEC-B5) |
| SEC-B6 | A deactivated user's calendar feed kept working | A07, API2; ASVS V7.4.2 | Medium | Fixed (Q-SEC-B6) |
| SEC-B7 | Role claims issued by the identity provider bypassed `Auth:RoleMap` | A07, A01; ASVS V10.3 | Medium | Fixed (Q-SEC-B7) |
| SEC-B8 | An email the identity provider marks unverified signed in as the account with that email | A07; ASVS V10.5 | Medium | Fixed: bound to issuer + subject since REOS-61 (Q-SEC-B8, Q-SEC-B8m) |
| SEC-B9 | Every identity the IdP authenticated became a Viewer and was provisioned | A07, A01; ASVS V6, V8.1 | Medium | Fixed, safest default behind `Auth:DefaultRole` (Q-SEC-B9) |
| SEC-B10 | Calendar feed tokens written to the log when request logging is raised (or by any property-printing format) | A09, A04; ASVS V16.2, V14.2 | Low | Fixed |
| SEC-B11 | A session cookie minted by a Development copy of a restored backup was accepted by the Production instance | A07, A04; ASVS V9.1 | Medium | Fixed |
| SEC-B12 | Key ring that signs every session readable by every local account (0755 directory, 0644 keys) | A04; ASVS V11.1, V13.3 | Medium | Fixed on Linux/macOS |
| SEC-B13 | Under organisation sign-in, a background API or hub call without a session was answered with an IdP redirect (500 when the IdP was unreachable), not 401 | A07; ASVS V10.4 | Medium | Fixed |

Severity is rated for the pilot deployment the runbook describes (one internal host, organisation OIDC, TLS at a reverse proxy), not for the internet.

### SEC-B1 · dev-login gated only by the environment name; `start.py` forced Development (High)

- **Evidence:** `src/ReleaseMgmt.Api/Program.cs:201-213` (c9a1180) mapped `POST /auth/dev-login` (anonymous; any email, any role, provisions the user) whenever `IsDevelopment()`; `:179`, `:193` mapped the `/api/v1/dev/*` tools the same way (they write without audit rows). `start.py:186` set `ASPNETCORE_ENVIRONMENT=Development` after merging `os.environ`, so an operator's explicit `ASPNETCORE_ENVIRONMENT=Production` (or `DOTNET_ENVIRONMENT`) was silently replaced. Nothing tied dev-login to the absence of organisation sign-in: with `Auth:Oidc:Authority` configured, both existed.
- **Why High:** the environment is one mutable string (set by `start.py`, `launchSettings.json`, a `--environment` switch, or an operator "switching to Development to see the error"), and when it is Development anyone who can reach the port becomes a Release Manager or Governance Officer, which defeats every separation-of-duties rule. The README makes `start.py` the easy way to run the app.
- **Launch paths reviewed:** published app (`dotnet ReleaseMgmt.Api.dll`, runbook): unset means Production (framework default), and `launchSettings.json` is not published. `dotnet run` uses `launchSettings.json`: Development on `localhost:5282`/`7031`. `start.py`: Development on `127.0.0.1:6080`, Vite on `127.0.0.1:6273` (Vite 8 refuses unknown Host names). CI (`ci.yml`): sets no environment; the e2e run goes through `start.py`. DR drill and load test: Development on `127.0.0.1`.
- **Reproduction:** `SEC_B1_dev_login_and_dev_tools_are_not_mapped_when_organisation_sign_in_is_configured` (was 200 with a cookie), `test_start.py` `test_an_explicit_aspnetcore_environment_is_not_overridden` and `test_an_explicit_dotnet_environment_is_not_overridden` (were `Development`).
- **Fix:** `start.py` passes through `ASPNETCORE_ENVIRONMENT`/`DOTNET_ENVIRONMENT` and only defaults to Development (and prints it). `DevSignIn.Enabled`: dev-login and the dev tools exist only in Development **and** when organisation sign-in is not configured, unless `Auth:DevLogin:Enabled=true`; outside Development the key is ignored with a warning. Also pinned: `SEC_B1_a_developer_can_still_opt_in...`, `SEC_B1_outside_Development_the_opt_in_does_not_bring_dev_login_back`, `test_the_backend_is_always_bound_to_loopback`.
- **Residual:** on a shared host running Development without OIDC, any local account can still use dev-login. Q-SEC-B1 asks whether a pilot may ever use `start.py`.

### SEC-B2 · dev-login reachable from other machines and through DNS rebinding (Medium)

- **Evidence:** `Program.cs:203` (c9a1180) checked nothing about the caller; `appsettings.json:11` `AllowedHosts: "*"`. `CrossSiteRequestGuard` lets `Sec-Fetch-Site: same-origin` through by design, and a page on `rebind.attacker.example` whose name has been re-pointed at 127.0.0.1 is same-origin to the browser. Such a page, opened by a developer running `start.py`, could sign itself in on `:6080` as any role and then read or change everything through the API (the victim's own cookie is not even needed).
- **Reproduction:** `SEC_B2_dev_login_refuses_a_Host_that_is_not_this_machine_so_DNS_rebinding_cannot_sign_in` (was 200); `SEC_B2_dev_login_answers_only_callers_on_this_machine` for `192.0.2.10` and `10.1.2.3` (were 200; loopback v4, v6 and v4-mapped stay 200).
- **Fix:** `DevSignIn.IsLocal`: the peer address must be loopback (none = in-process or Unix socket) **and** the Host name must be `localhost`, `*.localhost` or a loopback literal; otherwise 403 `{guard:"DevLoginLocalOnly"}` and a warning in the log. Not changed: `AllowedHosts` (a production decision for the misconfiguration scan).

### SEC-B3 · Session cookie not Secure behind a TLS-terminating proxy; no `__Host-` prefix (Medium)

- **Evidence:** `Program.cs:98-100` (c9a1180) left `Cookie.SecurePolicy` at `SameAsRequest`. The runbook (section 4) runs the app on `http://127.0.0.1:6080` behind a proxy and there is no forwarded-headers middleware, so Kestrel sees `http` and the cookie went out without `Secure`: a single plain-http request to the host name (a typed URL, a downgraded link) would carry the session in clear. Without the `__Host-` prefix a sibling sub-domain can also plant a cookie of the same name (login CSRF, cookie tossing).
- **Reproduction:** `SEC_B3_outside_Development_the_session_cookie_is_Secure_and_host_only...` (Production host, test-only sign-in through the real cookie options; the cookie was `releasemgmt.auth` without `secure`).
- **Fix:** `Auth:Cookie:RequireHttps` (default true outside Development): `__Host-releasemgmt.auth`, `Secure` always, `Path=/`, no `Domain`. Development keeps `releasemgmt.auth` with `SameAsRequest` so `start.py` over http still works (`SEC_B3_Development_keeps_the_plain_cookie...`, and REOS-53's cookie test is unchanged). A plain-http production host can no longer sign in (the browser drops a `Secure` cookie over http): that is intended; the key is the escape hatch.

### SEC-B4 · No absolute session lifetime; 14-day sliding idle timeout (Medium)

- **Evidence:** `Program.cs:96-104` (c9a1180) kept the cookie defaults (`ExpireTimeSpan` 14 days, sliding, no absolute limit) and OIDC `UseTokenLifetime=false`. Users are provisioned only at sign-in (Q-003), so a person disabled at the identity provider, or moved out of a mapped group (F10), kept their session and role for as long as any tab stayed open: the SPA polls every minute, so the cookie slid forever. REOS-53 recommended a shorter idle timeout but left it.
- **Reproduction:** `SEC_B4_a_session_nobody_uses_ends_after_the_idle_timeout`, `SEC_B4_an_active_session_still_ends_at_the_absolute_lifetime...`, `SEC_B4_both_limits_are_configurable` (all were 200 where 401 is expected; the fake clock drives both the cookie handler and the check).
- **Fix:** `SessionLifetime`: `Auth:Session:IdleMinutes` (default 60, sliding) and `Auth:Session:AbsoluteHours` (default 12, from the sign-in time stamped in the ticket at `OnSigningIn` and kept across renewals). Cookies issued before the change have no sign-in time and are refused once. Defaults for confirmation: Q-SEC-B4. `CommDispatchTests` advances its fake clock by three days while signed in; its host now sets longer limits (test configuration only, no assertion changed).

### SEC-B5 · Sign-out did not end the session on the server (Medium)

- **Evidence:** `Program.cs:223-227` (c9a1180): logout only sent a cookie deletion; the ticket is self-contained, so any copy (a shared browser profile, a proxy or HAR file, malware) stayed valid for up to 14 days, sliding.
- **Reproduction:** `SEC_B5_signing_out_ends_the_session_for_every_copy_of_the_cookie` (the copy answered 200 after logout).
- **Fix:** each sign-in gets a random session id in the ticket; logout records it and `SessionLifetime.ValidateAsync` refuses it until the session's absolute lifetime would have ended. **REOS-62:** the list is stored in `SessionRevocations` (pruned once a row's session could no longer be alive) and cached in memory, so a restart no longer revives a copied cookie (`SEC_B5_a_signed_out_cookie_stays_refused_after_the_host_restarts_on_the_same_database`). **Limit (Q-SEC-B5):** signing out does not end the IdP session (no RP-initiated logout).

### SEC-B6 · A deactivated user's calendar feed kept working (Medium)

- **Evidence:** `src/ReleaseMgmt.Infrastructure/Services/IcsTokenService.cs:121-132` (c9a1180) resolved a token to its user without looking at `Users.IsActive`. Q-053c stopped the deactivated user's cookie, not their other credential: release dates, deployment windows, freezes and gate dues kept flowing to the leaver's calendar.
- **Reproduction:** `SEC_B6_a_deactivated_users_calendar_feed_stops_working...` (was 200 after `PATCH /users/{id} {isActive:false}`).
- **Fix:** a feed resolves only for an active user; the answer is the same bare 404 as an unknown token. The link is not revoked, so a user the IdP signs back in (which reactivates them, Q-053c) gets it back. Links still never expire: Q-SEC-B6.

### SEC-B7 · Identity-provider role claims bypassed `Auth:RoleMap` (Medium)

- **Evidence:** `Program.cs:119-122` (c9a1180) added mapped roles next to whatever role claims the token already had. The OIDC handler maps a token's `roles`/`role` claim to `ClaimTypes.Role` (`MapInboundClaims` default), so an Entra app role, a Keycloak client role or any IdP role spelled `GovernanceOfficer` granted that role with no map entry (and the DB role was chosen from the same list). An IdP role not spelled like an app role broke sign-in with a `CHECK` failure on `Users.Role`.
- **Reproduction:** `SEC_B7_a_role_claim_from_the_identity_provider_does_not_bypass_the_role_map` (identity got `GovernanceOfficer, Viewer`), `SEC_B7_identity_provider_roles_are_mapped_through_the_role_map_like_groups` (threw `DbUpdateException`).
- **Fix:** `RoleMapper.AddRoles` removes every IdP role claim (`ClaimTypes.Role`, `roles`, `role`, the identity's role type) and uses the values as map keys like groups, as the runbook already described ("groups or roles"). Q-SEC-B7.

### SEC-B8 · Account linked through an unverified email (Medium)

- **Evidence:** `Program.cs:120` (c9a1180) and `UserProvisioner.UpsertAsync` key the account by email (`email`, else `preferred_username`) and never looked at `email_verified`, so a token for a different person carrying the same address signed in as the existing user: their gate and task ownership, audit identity and SoD history (the role comes from the new token).
- **Reproduction:** `SEC_B8_an_email_the_identity_provider_marks_unverified...` (sign-in succeeded as the Governance Officer's row).
- **Fix:** a token with `email_verified=false` is refused and logged. **REOS-61:** users are bound to the IdP's issuer and subject (`Users.IdpIssuer`, `Users.IdpSubject`); sign-in matches on them and email follows the IdP. A user who existed before is bound on first sign-in only with `email_verified=true`; a second identity with a bound user's email is refused and logged (`SEC_B8_a_second_identity_carrying_the_email_of_a_bound_user_is_refused`, `..._the_same_subject_signs_in_after_its_email_changes...`, `..._first_sign_in_binds_a_pre_existing_unbound_user...`). Open points (Entra and `email_verified`, upgrading an existing database): Q-SEC-B8, Q-SEC-B8m.

### SEC-B9 · Every IdP identity became a Viewer (Medium)

- **Evidence:** `src/ReleaseMgmt.Api/Auth/RoleMapper.cs:6,12` (c9a1180): no mapped group means Viewer, and the user is provisioned. Viewer reads every train, runbook, known issue and PIR. With Entra ID's default (assignment not required) that is every member and guest of the tenant; there was no allow-list in the app.
- **Reproduction:** `SEC_B9_an_identity_in_no_mapped_group_is_refused_unless_a_default_role_is_configured` (sign-in succeeded and a `Users` row was created).
- **Fix (safest default, product decision Q-SEC-B9):** unmapped identities are refused unless `Auth:DefaultRole=Viewer`; any other value stops start-up. `RoleMapper.Map(groups, map)` keeps its Viewer default, so `HostTests.Role_map_translates_groups_and_defaults_to_Viewer` is unchanged. Runbook section 4 lists the key.

### SEC-B10 · Feed tokens in the log when request logging is raised (Low)

- **Evidence:** the token is a path segment. `appsettings.json:9` keeps `Microsoft.AspNetCore` at Warning, but REOS-53 already noted that raising it (the usual first troubleshooting step) prints every request line, and the hosting log scope puts `RequestPath` on every event of the request, which any JSON or `{Properties}` format writes. The runbook already warns about proxy logs.
- **Reproduction:** `SEC_B10_feed_tokens_stay_out_of_the_log_even_when_request_logging_is_turned_up` (the plaintext token was in the log file).
- **Fix:** `IcsTokenRedactor`, a Serilog enricher registered in `Program.cs`, masks the segment after `/ics/` in every string property (scalars, sequences, structures) before any sink. Not covered: text interpolated into a message template and exception messages; nothing in the app writes the path there.

### SEC-B11 · A Development copy of a restored backup could mint Production sessions (Medium)

- **Evidence:** the cookie ticket was protected with the default purpose under the fixed application name `ReleaseMgmt` (`src/ReleaseMgmt.Api/Reminders/SyncRegistration.cs:27-28`). The DR runbook restores the database **with** `keys/` onto another host; run there through `start.py` (Development), dev-login upserts by email and so returns a real production user id, and the cookie it mints decrypts on production, where `SessionValidator` finds that user active.
- **Reproduction:** `SEC_B11_a_session_minted_by_a_Development_copy_is_refused_by_the_Production_instance...` (was 200 on the Production host).
- **Fix:** `CookieProtectionPurpose` protects the ticket under `ReleaseMgmt.Auth.SessionCookie` + the environment name + `v1`. Someone holding the key ring can still forge a cookie with their own code: the key ring remains the secret (Q-052e, SEC-B12). Existing cookies are invalidated once by the change.

### SEC-B12 · Key ring readable by every local account (Medium)

- **Evidence:** `SyncRegistration.cs:27-28` persists the ring with the framework's file permissions: `keys/` 0755 and each key 0644 on Linux and macOS (measured). The ring decrypts the connector credentials (whose own files are 0600) **and** signs every session cookie, so any local account could mint a Release Manager session. The runbook asks for mode 700 by hand.
- **Reproduction:** `SEC_B12_the_key_ring_that_signs_sessions_is_readable_by_the_service_account_only` (directory had group and other read and execute).
- **Fix:** `KeyRingPermissions` (post-configures `KeyManagementOptions`): directory 0700, keys 0600 at start-up and after every key the framework writes (rotation). Windows is left to ACLs (runbook section 5); the ring is still plain XML (Q-052e).

### SEC-B13 · Anonymous API and hub calls were challenged with an IdP redirect (Medium)

- **Evidence:** `Program.cs:95` (c9a1180) made OIDC the default challenge scheme whenever `Auth:Oidc:Authority` was set. Every unauthenticated API call and SignalR negotiate was then answered with a 302 to the identity provider plus fresh nonce and correlation cookies (500 with an exception when the IdP metadata could not be fetched, as in the test) instead of the 401 the SPA turns into its sign-in page. `fetch` follows the cross-origin redirect and fails, so the SPA showed "Cannot reach the server"; each background poll (every minute) added cookies until the request headers grew too large (a self-inflicted lock-out), and an IdP outage turned every API call into a 500. The endpoint matrix's "anonymous = exactly 401" contract only held without OIDC.
- **Reproduction:** `SEC_B13_with_organisation_sign_in_an_api_call_without_a_session_is_401...` (`GET /api/v1/me` gave 500).
- **Fix:** the cookie scheme answers challenges (401); organisation sign-in starts only at `GET /auth/login`, which already challenges OIDC explicitly.

## 2. Checked, no issue

- **Environment when unset:** Production (framework default); `launchSettings.json` is used by `dotnet run` only and is not published. CI sets no environment.
- **OIDC protocol settings** (by reading; no IdP here): authorization-code flow with PKCE (framework default), nonce and state/correlation validation (defaults), issuer, audience (= client id), lifetime and signature validation from the discovery document (defaults), `RequireHttpsMetadata` default true, `SaveTokens=false` (no IdP tokens in the cookie), client secret only from configuration or environment, `form_post` callback outside the cross-site guard on purpose.
- **Redirects:** `GET /auth/login` challenges with the fixed `RedirectUri = "/"`; there is no `returnUrl` parameter anywhere; the cookie handler's login and access-denied redirects are replaced by 401/403. No open redirect.
- **Session fixation:** there is no pre-authentication session; every sign-in issues a new encrypted ticket and now a new session id. Per-tab UI state is keyed by user and client id, not by the cookie.
- **Session re-validation:** `SessionValidator` re-reads `Users.IsActive` at most every `Auth:SessionRecheckSeconds` (10 s); a cookie without `uid` cannot be produced without the key ring. Role drift (F10) remains, now bounded by the 12-hour absolute lifetime.
- **ICS tokens:** 256 bits from `RandomNumberGenerator`, base64url; only SHA-256 stored (a salt adds nothing for 256-bit random secrets); lookup by hash, then `FixedTimeEquals`; length bounds; one bare 404 for unknown, revoked, malformed (and now deactivated); rotate and revoke are atomic and owner-only (existing `CalendarTests`); failed lookups throttled per client address. Default logging keeps the path out, including the unhandled-exception path (its log message has no path).
- **SignalR:** cookie-authenticated; negotiate is a POST behind the cross-site guard; the WebSocket and long-polling requests go through the `Read` policy themselves; no bearer or `access_token` query-string authentication is configured, so a token in the query string is ignored; the connection token is not a credential on its own.
- **Anonymous routes** (every `[AllowAnonymous]` / `AllowAnonymous()` in `src`): `GET /healthz` (monitoring; status, database state and last backup time only), `GET /auth/config` (the sign-in page needs it before sign-in; two public values), `POST /auth/logout` (ends one's own session; cross-site guarded), `GET /auth/login` (starts OIDC; only with an authority), `POST /auth/dev-login` (Development only, this machine only, SEC-B1/B2), the five `GET /api/v1/ics/{token}...` feeds (calendar apps cannot sign in; the token is the credential), `GET {*path:nonfile}` (the SPA shell, no data). Plus the handler paths `/signin-oidc` and `/signout-callback-oidc`, and static files. All other routes need a session (the fallback policy requires an authenticated user).
- **Brute force and stuffing:** no passwords in the app (IdP, Q-006); feed tokens are unguessable and throttled; dev-login is local-only; `/auth/login` is a redirect.
- **Data Protection:** one application name (`ReleaseMgmt`), keys beside the database, default algorithms (AES-256-CBC with HMAC-SHA256), 90-day rotation; now 0700/0600 (SEC-B12) and the cookie purpose names the environment (SEC-B11).

## 3. Observations (not counted: no failing test, or another scan's area)

- **SignalR connections outlive the session.** A WebSocket opened before sign-out, deactivation or expiry keeps receiving pushes (ids, versions and the server time only; every API call it prompts is refused). `CloseOnAuthenticationExpiration` was not turned on because the effect on the SPA's automatic reconnect during a Live run was not tested.
- **No forwarded-headers handling** (misconfiguration scan): behind the runbook's proxy Kestrel sees `http` and the proxy's address, so the OIDC `redirect_uri` is built as `http://…/signin-oidc` (identity providers refuse it, or the code travels over http if one allows it), and the feed throttle puts every client into one bucket (61 bad requests a minute from anyone 429 every calendar feed). Trusting `X-Forwarded-*` needs the proxy's address as configuration.
- **Sign-in writes no audit row** (logging scan): `UserProvisioner` changes `Users.Role`, the display name and `IsActive` (reactivation) at every sign-in without an `AuditEvents` row.
- **A refused organisation sign-in** (SEC-B8, SEC-B9, missing email) ends on the framework's error response at `/signin-oidc` (a 500), with the reason in the log only.
- **Log scans in tests can read another host's log** (test reliability, logging scan): `Program.cs` calls `UseSerilog` without `preserveStaticLogger`, so every in-process test host logs through the static `Log.Logger` of whichever host was built last. The log-file scans in `CalendarTests.Only_the_hash_is_stored...` and `SecretsTests` may therefore scan a file their own requests never wrote to when classes run in parallel. The SEC-B10 host test runs in a non-parallel collection for this reason. In production there is one host per process, so this does not affect the app.

## 4. Not verified

- The OIDC handshake end to end against a real identity provider (discovery, PKCE, nonce, form post, userinfo): only the app's own `OnTokenValidated` logic was run.
- Browser behaviour for `__Host-`/`Secure` cookies and the DNS-rebinding scenario in a real browser (the headers were asserted over the in-process server).
- Windows: key ring ACLs, and `start.py` environment pass-through on Windows (the test ran on Linux).
- Reverse-proxy logging of feed paths (runbook instruction only).
- The Playwright e2e suite (no browser in the review environment). It signs in through dev-login at `http://127.0.0.1:6273` (the Vite proxy keeps that Host and connects from 127.0.0.1), which the SEC-B2 rule allows; a real Kestrel process was checked by hand: loopback 200, a rebinding Host 403, `keys/` 700 and key files 600, no dev-login in Production.

## 5. Test results (this change)

`dotnet build ReleaseMgmt.sln --no-incremental`: 0 warnings, 0 errors. `dotnet test`: Domain 298, Infrastructure 200, DR drill 2, Api 548 (523 before, plus 25 in `AuthnSecurityTests.cs`), all passing. `python3 tests/reference/test_schema.py`: 82/82. `python3 tests/tools/test_start.py`: 10 tests OK (4 new). Against c9a1180, 18 of the new API tests and 2 of the new `start.py` tests failed, one or more per finding; the others pin behaviour the fixes must keep. Only test configuration changed in existing tests: `CommDispatchTests` sets longer session limits because its fake clock jumps days while clients stay signed in.

## 6. Configuration keys added

`Auth:DevLogin:Enabled` (Development only; default on unless an authority is set), `Auth:Cookie:RequireHttps` (default true outside Development), `Auth:Session:IdleMinutes` (60), `Auth:Session:AbsoluteHours` (12), `Auth:DefaultRole` (empty or `Viewer`; default empty). No schema change, no new package.
