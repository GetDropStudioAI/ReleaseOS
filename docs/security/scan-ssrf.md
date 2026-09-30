# Security scan: SSRF and unsafe consumption of APIs

**Scope:** OWASP API Security Top 10 2023, **API7:2023 Server Side Request Forgery** and **API10:2023 Unsafe Consumption of APIs**. In the OWASP Top 10 2025 these fall under **A01:2025 Broken Access Control** (SSRF moved there) and **A08:2025 Software or Data Integrity Failures** (data from third parties). The review covers every outbound HTTP call the server makes and what the server does with the answers.

**Base:** `main` at `c9a1180`. Findings are numbered SEC-C1 to SEC-C5. Decisions they raised are Q-SEC-C1 to Q-SEC-C3 in `docs/QUESTIONS.md`.

**Method:** I read the code and wrote a test for each suspected defect. Each test was run against the unfixed code and failed there. I then fixed the code and reran the test. No test uses the internet. The connect-time checks were tested against the real `SocketsHttpHandler` named clients, with a raw TCP listener on `127.0.0.1` standing in for internal services. Before this review, every connector and webhook test replaced that handler with a fake, so the connect-time check itself had never been run in a test. All runs were on Linux in a container with no IPv6.

## 1. Every outbound call

| Caller | Named client (handler) | Target comes from | Guard at save time | Guard at connect time |
|---|---|---|---|---|
| Jira connector: poll, "Test connection", "sync now" (`JiraCloudConnector` via `ConnectorHttp`) | `sync-connector` (`SyncRegistration.cs:30`) | `ConnectorState.BaseUrl`, set by an RTE or Release Manager (`PUT /connectors/Jira`) or `Connectors:Jira:BaseUrl` | `ConnectorUrlPolicy.Validate` (save and every `ConnectorFactory.CreateAsync`) | `ConnectCallback` resolves the host and connects only to addresses that `WebhookAddressPolicy` allows; no redirects; `UseProxy=false` |
| ServiceNow connector, including the OAuth token POST | same | same, and `Connectors:ServiceNow:TokenPath` (config) | same | same |
| Team notification webhooks (`TeamWebhookSender`) | `team-webhook` (`NotificationRegistration.cs:14`) | `WebhookDestinations.Url` through `Teams.WebhookDestinationId` | `WebhookUrlPolicy.Check` (`POST /sync/webhook-allowlist`), then `EnsurePublicAsync` before each send | same pattern, `Notifications:Webhooks:AllowPrivateTargets` |
| Comm dispatch webhooks (`CommWebhookSender`) | `comm-webhook` (`CommDispatchEndpoints.cs:62`) | `WebhookDestinations.Url`, by id or exact URL | same, then `BlockedAsync` before each send | same pattern, `Comms:Webhooks:AllowPrivateTargets` |
| OpenID Connect discovery, token and userinfo (`AddOpenIdConnect`) | framework backchannel | `Auth:Oidc:Authority` (configuration only) | none needed: operator configuration, `RequireHttpsMetadata` left at its default of true | framework default |

Nothing else goes out. I searched `src` for `HttpClient`, `IHttpClientFactory`, `HttpRequestMessage`, `WebRequest`, `WebClient`, `TcpClient`, `SmtpClient`, `Dns.` and `Process.Start`: nothing outside the rows above. The PDF, XLSX, CSV and ICS exporters fetch nothing: there is no server-side SVG or remote image, and QuestPDF only receives text and native drawing. Backups are local files.

## 2. Findings

| ID | Title | OWASP | Severity | Status |
|---|---|---|---|---|
| SEC-C1 | IPv6 addresses that carry an IPv4 address (NAT64, 6to4, IPv4-compatible, SIIT) passed the private-address check | API7:2023 / A01:2025 | Medium | **Fixed** |
| SEC-C2 | The save-time URL checks judged the Unicode host, so fullwidth or enclosed digits hid an IP literal. The connector base URL had no length bound | API7:2023 / A01:2025 | Low | **Fixed** |
| SEC-C3 | The webhook senders buffered the whole response body with no cap. A 200 with a body that never ends counted as a failure and the team sender posted it again | API10:2023 / A08:2025 (availability, integrity of delivery) | Medium | **Fixed** |
| SEC-C4 | Status names from Jira and ServiceNow were stored, audited forever and quoted in alerts, with no length or character check | API10:2023 / A08:2025 | Medium | **Fixed** (Q-SEC-C2) |
| SEC-C5 | The ServiceNow OAuth `access_token` was written into the `Authorization` header unchecked, and CR/LF in it reached the wire as an extra header | API10:2023 / A08:2025 | Low | **Fixed** |

### SEC-C1: IPv6 addresses that carry an IPv4 address passed the private-address check (Medium)

- **Evidence (at `c9a1180`):** `src/ReleaseMgmt.Api/Reminders/TeamWebhookSender.cs:16-21`. `WebhookAddressPolicy.IsBlocked` converted only IPv4-mapped `::ffff:a.b.c.d` back to IPv4. For every other IPv6 address it checked only link-local, site-local, unique-local, multicast and Teredo. The following all returned "not blocked":
  - `64:ff9b::a9fe:a9fe` (NAT64 for 169.254.169.254)
  - `64:ff9b::a00:5` (10.0.0.5)
  - `64:ff9b:1::/48` (NAT64 local use)
  - `::7f00:1` (IPv4-compatible 127.0.0.1)
  - `::ffff:0:a00:5` (SIIT)
  - `2002:a9fe:a9fe::1` (6to4)

  This one function is the whole address policy. It is used in these places:
  - the connect-time callback of all three named clients: `SyncRegistration.cs:41`, `NotificationRegistration.cs:24`, `CommDispatchEndpoints.cs:73`
  - the pre-send checks of both senders
  - the save-time checks: `SyncHealthService.cs:61` and `ConnectorUrlPolicy.cs:20` through `SyncRegistration.cs:52`
- **Impact:** on a host with IPv6 and a NAT64 translator, the translator turns these addresses into the private IPv4 address inside them. Examples are an IPv6-only cloud subnet with DNS64/NAT64, where the managed NAT gateway reaches IPv4 hosts in the same VPC, peered VPCs and on-premises. An RTE or Release Manager can enter `https://[64:ff9b::a00:5]` as a connector base URL or webhook address. They can also enter a name whose AAAA record is that address, and the check at connect time judges the resolved address but misread it. Either way they reach internal services. A connector sends the stored Jira or ServiceNow credential in the `Authorization` header to that address.
- **Severity rationale:** Medium. It bypasses the main SSRF control the design relies on (D13, Q-039b, Q-042d). It needs an admin role and a NAT64 network: a plain dual-stack or IPv4 host does not route these addresses. This was confirmed here: a connect to `::7f00:1`, `64:ff9b::7f00:1` or `2002:7f00:1::1` fails on a host without IPv6.
- **Reproduction:** `SsrfTests.IPv6_forms_that_carry_a_blocked_IPv4_address_are_blocked` covers 13 addresses and all failed before the fix. `A_NAT64_spelling_of_the_metadata_address_is_refused_as_a_connector_base_url_and_as_a_webhook_address` also failed: both `PUT /connectors/Jira` and `POST /sync/webhook-allowlist` accepted `[64:ff9b::a9fe:a9fe]` with 200.
- **Fix:** `IsBlocked` now takes the IPv4 address out of these forms and judges it like any IPv4 address: `::/96`, `::ffff:0:0/96`, `64:ff9b::/96` and `2002::/16` (`EmbeddedIPv4`). The following are always blocked:
  - `64:ff9b:1::/48` (RFC 8215 local use: the translation is whatever the operator chose)
  - `100::/64` (discard-only)
  - `2001:db8::/32` (documentation)

  A public IPv4 address behind NAT64 or 6to4 stays reachable. `IPv6_forms_that_carry_a_public_IPv4_address_stay_open` pins that, so an IPv6-only deployment can still reach Atlassian and ServiceNow. Because the connect-time callbacks, the pre-send checks and the save-time checks all call this function, all three clients get the fix.
- **Still open:** a NAT64 prefix chosen by the network operator (RFC 6052) cannot be recognised without configuration. See Q-SEC-C1.

### SEC-C2: The save-time checks judged the Unicode host; the connector base URL had no length bound (Low)

- **Evidence (at `c9a1180`):**
  - `src/ReleaseMgmt.Infrastructure/Sync/ConnectorUrlPolicy.cs:20` parsed `uri.Host`.
  - `src/ReleaseMgmt.Api/Sync/SyncHealthService.cs:61` parsed only when `uri.HostNameType` was IPv4 or IPv6.

  For `https://１２７.０.０.１/` (fullwidth) or `https://①②⑦.0.0.1/` (enclosed digits), .NET reports `HostNameType = Dns`, with `Host` in Unicode and `IdnHost = "127.0.0.1"`. I probed this with .NET 10.0.401. The handler connects to `IdnHost`. So the connector check never saw an IP. The webhook check sent the host down the name rules, where `127.0.0.1` has a dot and no internal suffix, and accepted it: the allowlist stored `https://127.0.0.1/hook`. Separately, `ConnectorUrlPolicy` had no length limit and no whitespace or control-character check, while the webhook policy caps at 2048.
- **Impact:** the save-time guard could be bypassed, but the address was never reached. The pre-send and connect-time checks judge the address actually used and refused it. The `SsrfTests` real-socket tests confirm that path. What remained was defence in depth plus a stored allowlist row that an admin would read as public. The unbounded base URL was stored, audited and returned by `GET /connectors` to every signed-in role.
- **Reproduction:** `SsrfTests.An_address_spelled_with_unicode_digits_is_judged_by_the_address_it_becomes` has 4 cases and all failed before the fix. `SsrfTests.A_connector_base_url_is_bounded_like_a_webhook_address` also failed.
- **Fix:**
  - New `ConnectorUrlPolicy.LiteralAddress(uri)` parses `IdnHost` first, then `Host`, then `DnsSafeHost`, ignoring any IPv6 zone. Both policies now use it whatever `HostNameType` says.
  - The connector base URL is capped at 2048 characters, rejects whitespace and control characters, and matches `Sync:AllowedHosts` against `IdnHost`, the host that will actually be connected to.

  Existing expectations are unchanged: `http://localhost:6081` is still accepted in Development because the connector policy has no name rules. Checking names at save time for connectors, as webhooks do, would change that accepted value, so it was not added. The connect-time check covers names.

### SEC-C3: The webhook senders buffered the whole response body with no cap; a slow 200 was sent again (Medium)

- **Evidence (at `c9a1180`):** `src/ReleaseMgmt.Api/Reminders/TeamWebhookSender.cs:124` and `src/ReleaseMgmt.Api/Comms/CommWebhookSender.cs:72` call `HttpClient.SendAsync(req, token)`. The default `HttpCompletionOption.ResponseContentRead` reads the whole body into memory before returning, up to `MaxResponseContentBufferSize`, which is 2 GB by default. It does this inside the per-attempt timeout, and neither sender ever looks at the body.
- **Impact:**
  - **Memory:** an allowlisted endpoint that is hostile, compromised or broken can stream as much as the network carries within the 10 s timeout. With the team sender that is up to 3 attempts per notice, on the single app instance (D3).
  - **Duplicate sends:** a 200 whose body trickles or never ends is recorded as "no answer within 10s". The team sender retries timeouts, so the channel receives the same notice up to 3 times, and an alert says delivery failed when it succeeded. A "Generic" destination can be any public host an admin allowlisted.
- **Severity rationale:** Medium. It affects availability of the only instance and the correctness of the delivery record (a failure alert for a message that was delivered). It needs a misbehaving allowlisted endpoint.
- **Reproduction:** both tests failed before the fix:
  - `SsrfTests.A_team_webhook_that_answers_200_and_then_streams_forever_is_delivered_once_and_its_body_is_never_buffered` (not delivered, 3 posts, 8 MB read)
  - `SsrfTests.A_comm_webhook_that_answers_200_and_then_streams_forever_is_delivered_and_its_body_is_never_buffered` ("no answer within 1s")
- **Fix:** both senders use `HttpCompletionOption.ResponseHeadersRead`. The status decides and the body is never read. Disposing the response lets `SocketsHttpHandler` drain at most 1 MB, then drop the connection. The connectors already streamed with a cap (`ConnectorHttp`, `Sync:MaxResponseBytes`).

### SEC-C4: Status names from other systems were stored, audited and quoted with no check (Medium)

- **Evidence (at `c9a1180`):**
  - Where the text comes from:
    - `src/ReleaseMgmt.Infrastructure/Sync/JiraCloudConnector.cs:25` returns the Jira status name as received.
    - `src/ReleaseMgmt.Infrastructure/Sync/ServiceNowConnector.cs:32` maps state codes, but unknown codes and display values pass through (`src/ReleaseMgmt.Domain/Sync/ExternalKeys.cs:41,48`).
  - Where it goes:
    - `SyncCycleWriter.cs:35` stores it in `ExternalLinks.LastSyncedStatus`.
    - `SyncCycleWriter.cs:41` writes it into an `AuditEvents` row, in both before and after, each time it changes. Audit rows are immutable and kept 7 years (D22).
    - `MismatchRules.cs:43,45` quotes it in `SyncAlerts.ErrorMessage` and the in-app notifications sent to every RTE and Release Manager.
  - The only bound was the 1 MB response cap.
- **Impact:** a hostile, compromised or misconfigured Jira or ServiceNow can do three things:
  1. Put free text into the "state". OI-12's assumption is that ticket text may contain card or customer data, so OI-12 stores keys, states and dates only. This text lands in the 7-year immutable audit trail and in notifications, where it cannot be deleted.
  2. Fill the disk of the single SQLite host. A value just under 1 MB that changes every cycle writes about 2 MB of audit per link per cycle, and cycles run every minute while a deployment window is open.
  3. Put bidi overrides or zero-width characters into alert text, so an operator reads a spoofed state such as "Implement".

  React escapes all of this text, so there is no script injection. The UI never renders an external value as a link.
- **Severity rationale:** Medium. It breaks data minimisation into records that cannot be deleted, and it can exhaust storage. It needs a malicious or compromised upstream, or an unusual status configuration.
- **Reproduction:** `tests/ReleaseMgmt.Infrastructure.Tests/SyncUnsafeConsumptionTests.cs`. 14 connector cases (Jira and ServiceNow × 7 hostile values) and `A_hostile_state_never_reaches_the_link_the_audit_trail_the_alerts_or_the_inbox` all failed before the fix. The last test runs two poll cycles on an Executing train, then searches links, audit, alerts and notifications for a planted card number. `Ordinary_status_names_still_pass` covers accented, CJK and emoji names.
- **Fix:** `ExternalKeys.IsPlausibleState` applies these rules:
  - not blank
  - at most 100 characters (`MaxStateLength`)
  - no control, format, line or paragraph separator, private-use or unpaired-surrogate characters

  Both connectors call it. A failing value is a link-level `ParseError`: the alert is visible and never silent (rule 8), its message does not repeat the value, and nothing is stored or audited. The value is refused, not trimmed, which matches Q-045c's "refused, never altered". The limit is recorded in Q-SEC-C2.

### SEC-C5: The OAuth access token went into a request header unchecked (Low)

- **Evidence (at `c9a1180`):** `src/ReleaseMgmt.Infrastructure/Sync/ServiceNowConnector.cs:58-60` takes `access_token` from the token response. `ConnectorHttp.cs:29` adds it with `Headers.TryAddWithoutValidation("Authorization", "Bearer " + token)`. I verified against a raw socket on .NET 10.0.401 that a value containing `\r\n` is written to the wire as is: `Authorization: Bearer abc` followed by `X-Injected: 1` as a separate header.
- **Impact:** a hostile or compromised OAuth endpoint can add headers to every following request in the cycle. Those requests go to the same host, which already controls the answers. The real risk is desync or request smuggling through a shared reverse proxy or WAF in front of that host, on connections that carry credentials. The token was also unbounded (up to 1 MB).
- **Severity rationale:** Low. The attacker must already control the ServiceNow OAuth answer, and the target is the same origin.
- **Reproduction:** both failed before the fix, because the GET was sent with the bad token:
  - `SyncUnsafeConsumptionTests.An_oauth_answer_whose_access_token_is_not_a_bearer_token_is_an_auth_failure_and_nothing_is_sent_with_it` (CR/LF, space, empty, non-ASCII)
  - `An_oversized_oauth_token_is_refused_and_a_jwt_shaped_one_is_accepted`
- **Fix:** `ServiceNowConnector.IsBearerToken`: RFC 6750 `b64token`, at most 8192 characters. Anything else is an `Auth` failure ("no usable access token") and is never sent. A JWT-shaped token still passes. The other value sent in a header, Basic auth, is Base64 of admin-entered values and cannot carry CR/LF.

## 3. Checked, no issue

Each item below was read in code and, where marked [test], pinned by a test. "Pinned in `SsrfTests`" means the test passed before and after the fixes.

- **Loopback in every spelling:** `127.0.0.1`, `localhost`, decimal `2130706433`, hex `0x7f000001`, octal `017700000001` and `0177.0.0.1`, short `127.1`, mixed `0x7f.1`, `0.0.0.0`, `0`, `[::ffff:127.0.0.1]` and `[::1]`. .NET's `Uri` turns the numeric forms into `127.0.0.1`. Each connect-time callback then refuses the resolved address before a socket opens. [test] `No_outbound_client_ever_connects_to_a_loopback_target_however_it_is_spelled`, 12 spellings × 3 real clients, and the listener counted zero connections. `0.0.0.0` does reach the loopback listener on Linux: a raw probe connected. It is blocked (0/8).
- **Private and reserved IPv4:** 10/8, 172.16/12, 192.168/16, 100.64/10 (CGNAT), 169.254/16 (metadata), 0/8, 192.0.0/24, 198.18/15 and 224/4 through 255.255.255.255 are all blocked. In IPv6, fc00::/7, fe80::/10, fec0::/10, ff00::/8, `::`, `::1` and Teredo are blocked. [test] The existing `NotificationTests.The_address_policy_blocks_every_private_range...` plus the new IPv6 cases.
- **DNS rebinding:** each check at connect time resolves the name itself (`ConnectCallback`) and connects only to the addresses it vetted, so no second resolution can differ. A name that resolves to both public and private addresses connects only to the public ones. [test] `Test_connection_to_a_host_name_that_resolves_to_loopback_is_refused_when_the_socket_connects` goes through the whole `PUT /connectors` and `:test` path with `http://localhost:<port>`: 422 `ConnectorTestFailed`, the target is not named in the message, and the listener counted zero connections.
- **Redirects:** `AllowAutoRedirect=false` on all three handlers. A 3xx comes back to the caller, and the connector and comm sender treat it as an error. [test] `A_redirect_is_handed_back_and_never_followed_by_any_outbound_client`: a real 302 to a second listener, which counted zero connections. Because redirects are not followed, credentials never go to a redirect target. `ConnectorHttp` also refuses any request whose scheme, host or port differs from the base URL (existing `Requests_stay_on_the_configured_host`).
- **Schemes, user info, fragments:** only `https` is allowed, plus `http` in Development for connectors and team webhooks; comm webhooks and the allowlist are always `https`, and the schema has `CHECK (Url LIKE 'https://%')`. User info is refused, and so are a query or fragment in a base URL and a fragment in a webhook. Existing tests cover these.
- **The connector base URL and webhooks use the same guard:** yes at connect time, since the same `WebhookAddressPolicy` runs in each `ConnectCallback`. At save time the connector check is narrower: IP literals only, no name rules. It now judges the connected form (SEC-C2).
- **IDN tricks:** `ⓛⓞⓒⓐⓛⓗⓞⓢⓣ` becomes `localhost` in `IdnHost`. Webhooks refuse it at save time, and connectors refuse it at connect time. For `Sync:AllowedHosts`, a Unicode dot (`evil.com．atlassian.net`) or a suffix trick (`acme.atlassian.net。evil.com`) cannot turn a host outside the list into one that matches: the ASCII suffix is kept, and matching now uses `IdnHost`. A trailing dot (`127.0.0.1.`) is not an IP literal to `Uri`; it fails to resolve and is refused.
- **Proxy:** `UseProxy=false` on all three handlers, so `HTTP(S)_PROXY` cannot route around the connect-time check. The open question about corporate proxies is Q-039b and Q-SEC-C3.
- **HTTP/3 or Alt-Svc bypassing `ConnectCallback`:** requests use the default HTTP/1.1 with `RequestVersionOrLower`, so HTTP/3 is never tried.
- **TLS:** no `ServerCertificateCustomValidationCallback`, `DangerousAcceptAnyServerCertificateValidator`, `RemoteCertificateValidationCallback` or `SslOptions` appears anywhere in `src`, so default certificate validation applies everywhere. OIDC keeps `RequireHttpsMetadata` at its default of true.
- **Timeouts:**
  - Connectors: `Sync:TimeoutSeconds` (10) covers the headers and the body read, and `ConnectTimeout` equals it.
  - Team webhooks: 10 s per attempt, at most 5 attempts (config).
  - Comm webhooks: `Comms:Webhooks:TimeoutSeconds` (10, capped at 60), 1 attempt by default.
  - OIDC: framework default.
- **Response size:** connectors stream with a cap (`Sync:MaxResponseBytes`, 1 MB, overshoot at most 16 KB), and a `Content-Length` over the cap is refused before reading. The webhook senders no longer read the body (SEC-C3). `AutomaticDecompression` is off, so there are no decompression bombs. Headers are bounded by the default `MaxResponseHeadersLength` (64 KB).
- **JSON:** `JsonDocument` default `MaxDepth` 64, input already capped at 1 MB. A parse failure becomes `Parse` with a fixed message.
- **Third-party fields kept:**
  - Jira issue: `fields.status.name`, validated.
  - Jira fix version: `name` (compared, not stored), `released`, `archived`, `releaseDate` (strict `yyyy-MM-dd`).
  - ServiceNow: `state`, validated, and `start_date`/`end_date` (two strict formats).
  - Requests ask only for those fields (`fields=status`, `sysparm_fields`).
  - Not stored: summaries, descriptions and people. This was already pinned by a test that plants a card number in `short_description`.
  - `expires_in` falls back to 60 if it is not an int32. `Retry-After` is ignored if negative and capped at one poll interval (Q-039d).
- **Links in the UI:** no external key, base URL, status or alert text is rendered as `href`; I searched `src/ReleaseMgmt.Web/src` for `href=`, `dangerouslySetInnerHTML`, `innerHTML` and `window.open`. `Auth:PasswordResetUrl` is operator configuration and must be absolute `https` (`Program.cs:215`). The mailto link has a fixed `mailto:` prefix and `encodeURIComponent`. External text is shown through React text nodes.
- **Pagination:** there is no pagination loop. The Jira version lookup is one GET with `maxResults=50&query=`, and ServiceNow uses `sysparm_limit=1`, so a hostile `nextPage` or `isLast:false` cannot cause a loop or a request elsewhere.
- **Retry storms and 429:** connectors never retry within a cycle. A connector-wide failure stops the cycle. 429 backs off exponentially, capped at one interval, and honours `Retry-After` (Q-039d). The comm sender does not retry by default. The team sender retries 408, 429 and 5xx up to the configured count, with linear delay.
- **Reflected bodies (read-SSRF):** `ConnectorException` messages carry the status class and code only. Transport exceptions keep their type name only. "Test connection" returns only that message. No response body, URL or header is ever echoed. The existing `SecretsTests` echo-upstream test plus the `localhost` test above cover this.
- **Credential destination:** credentials go only to the configured host (same-host check, no redirects). The OAuth `TokenPath` comes from configuration and is resolved against the base URL; an absolute value is refused by the same-host check.
- **Path injection through external keys:** keys are validated to narrow patterns before use (`ExternalKeys`), and every path or query part is `Uri.EscapeDataString`-ed.

## 4. Not verified

- **NAT64 end to end:** there is no IPv6 in the test container (`Socket.OSSupportsIPv6 = False`). The finding and the fix were verified at the policy level and by reasoning about translator behaviour, not through a real NAT64 gateway. The same applies to whether Windows or macOS route `::a.b.c.d` (IPv4-compatible) addresses anywhere; they are now blocked regardless.
- **The OIDC backchannel:** discovery, JWKS, token and userinfo URLs come from the identity provider's metadata. They were not tested; there is no IdP in the test environment. They are operator-trusted, and the framework enforces https metadata.
- **Behaviour behind a corporate egress proxy:** not built (`UseProxy=false`, Q-039b).
- **Windows and macOS:** the real-socket tests use `127.0.0.1` listeners and should behave the same, but were run on Linux only; CI covers Windows and macOS (D31).
- **Memory use under a real multi-gigabyte stream:** the tests use an in-memory body that counts bytes read. They prove the senders read nothing, not the process's peak memory.

## 5. Changes

| File | Change |
|---|---|
| `src/ReleaseMgmt.Api/Reminders/TeamWebhookSender.cs` | SEC-C1 `WebhookAddressPolicy.IsBlocked` plus `EmbeddedIPv4`; SEC-C3 `ResponseHeadersRead` |
| `src/ReleaseMgmt.Api/Comms/CommWebhookSender.cs` | SEC-C3 `ResponseHeadersRead` |
| `src/ReleaseMgmt.Api/Sync/SyncHealthService.cs` | SEC-C2: `WebhookUrlPolicy.IsInternal` uses `ConnectorUrlPolicy.LiteralAddress` |
| `src/ReleaseMgmt.Infrastructure/Sync/ConnectorUrlPolicy.cs` | SEC-C2: `LiteralAddress`, `MaxUrlLength` 2048, no whitespace or control characters, `IdnHost` for `Sync:AllowedHosts` |
| `src/ReleaseMgmt.Domain/Sync/ExternalKeys.cs` | SEC-C4: `MaxStateLength`, `IsPlausibleState` |
| `src/ReleaseMgmt.Infrastructure/Sync/JiraCloudConnector.cs`, `ServiceNowConnector.cs` | SEC-C4 state check; SEC-C5 `IsBearerToken` |
| `tests/ReleaseMgmt.Api.Tests/SsrfTests.cs` (new) | real-handler pins plus SEC-C1, C2, C3 reproductions (39 tests) |
| `tests/ReleaseMgmt.Infrastructure.Tests/SyncUnsafeConsumptionTests.cs` (new) | SEC-C4 and C5 reproductions (26 tests) |
| `docs/QUESTIONS.md` | Q-SEC-C1 (NAT64 network-specific prefix), Q-SEC-C2 (status name bound), Q-SEC-C3 (default host allowlist and ports) |

- No schema change and no new package.
- No new configuration key: the limits are constants, and existing keys keep their meaning.
- No existing test was changed or removed.
