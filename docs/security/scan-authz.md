# Authorization scan: OWASP API Security Top 10 2023 (API1, API3, API5, API6)

One of five parallel security reviews of `main` at `c9a1180`. It goes deeper than REOS-53's `EndpointRoleMatrixTests`, which proves each route's **role** gate. This scan checks whether a caller who holds the right role may act on **that object**, see **those fields** and repeat or race **that flow**. Method: every handler in `src/ReleaseMgmt.Api/Endpoints/*`, `Auth/*` and `Realtime/*` was read with the service it calls in `src/ReleaseMgmt.Infrastructure/Services/*`, `Comms/*`, `Exports/*` and `Sync/*`. A candidate became a finding only once a test in `tests/ReleaseMgmt.Api.Tests/AuthzScanTests.cs` reproduced it against the unfixed code. Each fix was then made in the service (services stay the only status writers; `If-Match` and audit rules are unchanged), and the test was run again.

Visibility model assumed (PROJECT_SCOPE section 1): there are no per-train permissions. Every signed-in role reads every train, gate, runbook, attachment and analytic. Only the audit log and evidence packs are restricted (RTE, Release Manager, Governance Officer). Per-object authorization therefore matters for **per-user objects** (inbox, UI state, calendar token, previews), for **owned objects** (tasks, gates, steps, conditions, PIR actions) and for **governance records** (who requested, who completed, who approved).

## 1. Findings

| ID | Title | OWASP 2023 | Severity | Status |
|---|---|---|---|---|
| SEC-A1 | Freeze-override two-person rule satisfied by a requester the approver names without that person having asked | API3 (attribution field set by the caller), API6 | **High** | Fixed |
| SEC-A2 | Re-completing a completed task rewrites who completed it, defeating the Compliance SoD check and rewriting evidence on certified gates | API6, API3 | **Medium** | Fixed |
| SEC-A3 | A deactivated user's calendar feed (anonymous bearer URL) keeps working | API1, API5 | **Medium** | Fixed |
| SEC-A4 | Two concurrent dispatches of one T-minus schedule item both post to the channel | API6 (race) | **Low** | Fixed |

Everything else examined is either sound (section 6) or recorded as an observation with no code change (section 7, Q-SEC-A5).

### SEC-A1 · Freeze-override two-person rule satisfied by a requester who never asked · High
- **OWASP:** API3 (Broken Object Property Level Authorization: the caller sets `requestedByUserId`, an attribution field the caller should not control) and API6 (the approval flow can be run by one person).
- **Severity: High.** One Release Manager (or Governance Officer) can defeat a database-backed two-person control on freeze windows, a change-management control auditors sample under SOX ITGC. The immutable override row and the evidence pack then name a requester who never asked. Not Critical: it needs an approver account, and the approver is still recorded.
- **Evidence (c9a1180):** `src/ReleaseMgmt.Api/Endpoints/FreezeEndpoints.cs:12,23` binds `OverrideBody.RequestedByUserId` from the approver's body. `src/ReleaseMgmt.Infrastructure/Services/FreezeService.cs:99-107` checks only that the named person is not the approver and is an active RTE or RM, then writes that person as the requester. Under Q-034b no pending request is stored. `src/ReleaseMgmt.Web/src/Freezes.tsx:48` preselects the first other owner as "Requested by". An RM can also pre-provision the requester (`POST /imports/Users:preview` with role RTE: the Admin policy, which an RM holds, `src/ReleaseMgmt.Infrastructure/Exchange/AdminHandlers.cs:55`) and name that account, which never signs in and never asks.
- **Reproduction:** `AuthzScanTests.SEC_A1_A_freeze_override_needs_a_request_the_named_requester_actually_filed`. Before the fix, `POST /freezes/{id}/overrides` naming an RTE who had filed no request returned 200 and wrote the row.
- **Fix:** the grant needs an open request: an audited `RequestOverride` by the named requester for that window and train, made after the last Grant/Renew that named them for the same pair. One request covers one grant; a renewal needs a new request. Otherwise 422 `OverrideNotRequested`, which names who has an open request. The config key `Freeze:OverrideRequiresRequest` (default true) can switch the rule off; `SEC_A1_The_request_rule_can_be_switched_off_by_configuration` covers that path. Files: `src/ReleaseMgmt.Infrastructure/Services/FreezeService.cs`. The existing `tests/ReleaseMgmt.Api.Tests/FreezeTests.cs` now file the request before granting, as the real flow does; every assertion is kept. Decision record: **Q-SEC-A1** (open points: request expiry, and the screen's "Requested by" list).

### SEC-A2 · Re-completing a completed task rewrites who completed it · Medium
- **OWASP:** API6 (a replayed action changes a governance record) and API3 (it overwrites `CompletedByUserId` and `CompletedAt`).
- **Severity: Medium.** The Compliance segregation-of-duties rule (a certifier completed none of the gate's tasks) reads `CompletedByUserId`. A Governance Officer who did the work can have anyone allowed to complete tasks re-complete it (an RTE or RM, or the task or gate owner), then certify their own work; the trigger and service both pass. On a Certified gate, re-completion also rewrites who did the work and when, after the fact and without decertifying. It needs a second person or produces evidence drift, hence Medium, not High.
- **Evidence (c9a1180):** `src/ReleaseMgmt.Infrastructure/Services/TaskService.cs:44-52` sets `CompletedByUserId = actor` and `CompletedAt = now` whatever the current state. `db/schema.sql:766-769` (`trg_Gate_CertifierNotTaskAuthor`) reads that column. `db/schema.sql:811-813` decertifies only on `IsCompleted` 1→0, so re-completion is invisible to the gate.
- **Reproduction:** `AuthzScanTests.SEC_A2_Completing_an_already_completed_task_does_not_rewrite_who_completed_it`. Before the fix, `CompletedByUserId` changed to the RTE. The test stops at that assertion; that the officer's certify would then pass follows from the trigger and `GateService.EvaluateCertifyAsync`, which both read only that column.
- **Fix:** `:complete` on a completed task and `:reopen` on an open one are no-ops: 200 with the row as it is, no Version bump, no audit row. Files: `src/ReleaseMgmt.Infrastructure/Services/TaskService.cs`. Decision record: **Q-SEC-A2** (a no-op rather than 422).

### SEC-A3 · A deactivated user's calendar feed keeps working · Medium
- **OWASP:** API1 (the object behind the anonymous route, a user's feed, is served without checking that its owner may still read) and API5 (a deactivated user keeps a read function).
- **Severity: Medium.** It gives persistent, unauthenticated read access after off-boarding to every train's titles, target dates, gate due dates, deployment windows and freezes, and the user's own work. Q-053c decided that deactivation ends access, and this is the one path it missed. Content is titles and times only, and the user must have created the link before leaving, hence not High.
- **Evidence (c9a1180):** `src/ReleaseMgmt.Api/Endpoints/CalendarEndpoints.cs:40-44` maps the feeds `AllowAnonymous`. `src/ReleaseMgmt.Infrastructure/Services/IcsTokenService.cs:121-129` resolves the token to a user id and checks only that the token is not revoked.
- **Reproduction:** `AuthzScanTests.SEC_A3_A_deactivated_users_calendar_feed_stops_working`. Before the fix, the feed answered 200 after `PATCH /users/{id} {isActive:false}`.
- **Fix:** the token resolves only while its user is active, checked on every fetch. A deactivated user gets the same bare 404 as an unknown token. Files: `src/ReleaseMgmt.Infrastructure/Services/IcsTokenService.cs`. Decision record: **Q-SEC-A3** (whether to revoke tokens on deactivation instead).

### SEC-A4 · Two concurrent dispatches of one schedule item both post · Low
- **OWASP:** API6 (race on a flow that must happen once).
- **Severity: Low.** It needs two concurrent requests from people allowed to dispatch (a double click, two tabs, a retry). The impact is a duplicate message in a Teams or Slack stakeholder channel and a second dispatch row; the schedule item is stamped only once, so M12 is unaffected.
- **Evidence (c9a1180):** `src/ReleaseMgmt.Infrastructure/Comms/CommDispatchService.cs:85` checks `SentAt` outside any transaction, `:133` posts the webhook, and `:166` only re-reads `SentAt` inside the write transaction: it skips the stamp but still records the dispatch and answers 200.
- **Reproduction:** `AuthzScanTests.SEC_A4_Two_concurrent_dispatches_of_one_schedule_item_post_to_the_channel_once`. Before the fix, the fake sender was called twice, two rows were written and both requests answered 200.
- **Fix:** dispatches that name a schedule item are serialised per item (striped in-process locks in the singleton service). The second one sees the item sent and gets 422 `ScheduleAlreadySent` before anything is posted. Files: `src/ReleaseMgmt.Infrastructure/Comms/CommDispatchService.cs`. Decision record: **Q-SEC-A4** (single-process assumption; mark-sent is not under the lock).

## 2. API1 Broken Object Level Authorization: what was checked

Every route that takes an object id was classified by who may act on that object. The Owned actions were re-read even though REOS-53 pins them.

- **Per-user objects: the caller's own rows only, keyed by the `uid` claim and never by the URL.** `GET/PUT /me/session/{clientId}` (the key is (uid, clientId), and another user's clientId addresses your own row); `GET /me/notifications`, `/count`, `POST /me/notifications:read-all`, `POST /notifications/{id}:read` (another user's gets 403 `NotYourNotification`); `GET /exports/notifications.csv|xlsx` (filtered by uid); `GET /me/work`; `GET/POST /me/ics-tokens`, `:rotate`, `:revoke` (lookup by id and uid, so another user's gets 404); `POST /trains/{id}/tasks:commit` (preview lookup by id, train and uid); `POST /imports/{jobId}:commit` (lookup by id and uploader). Pinned for two users of the same role by `Pin_per_user_objects_are_not_reachable_by_another_planner`, alongside the existing single-role tests (`SessionTests`, `NotificationTests`, `CalendarTests.One_active_token_per_user...`, `ImportExportTests.A_preview_expires...`).
- **Owned objects: the owner, or a planner.** `POST /tasks/{id}:complete|:reopen` (`TaskDenial`); `POST /gates/{id}:start|:certify|:fail|:reopen` (`GateAccessAsync`; Compliance certify by Governance Officers only); `POST /runs/{id}/steps/{stepId}:*` (`CanActAsync` on the step, and the service requires the step to belong to the run's train); `POST /conditions/{id}:close` (owner or RM, in the service); `POST /pir-actions/{id}:complete|:reopen` (owner or close-out role, in the service); `DELETE /attachments/{id}` (uploader, RTE or RM, and never once locked).
- **Parent/child consistency (an id from another train cannot be smuggled in):** `DELETE /trains/{id}/cis/{ciId}`, `/trains/{id}/known-issues/{issueId}...` (lookup by both ids); `POST /trains/{id}/comms:dispatch` (template and schedule item must belong to the train); `POST /trains/{id}/links` (the entity must be part of the train); `PUT /steps/{id}/dependencies` (dependencies from the same train only); `POST /trains/{id}:rehearsed-rollback` (the run must be a finished Rehearsal of the train); `POST /trains/{id}/tasks:commit`.
- **Evidence packs:** `POST /trains/{id}/export-jobs`, `GET /export-jobs`, `GET /export-jobs/{id}`, `GET /export-jobs/{id}/file` refuse EvidencePack (PDF and ZIP) without AuditRead. The endpoint checks the role claim and the service checks the database role. `GET /exports/audit.csv|xlsx` needs AuditRead.
- **Guessable ids:** application rows use UUIDv7 (74 random bits) and trigger rows use random hex. The only sequential id is `AuditEvents.Id`, whose readers all need AuditRead. ICS tokens are 256-bit, stored as SHA-256, and compared in constant time; failed lookups are throttled.
- **Not a BOLA surface by design:** trains, gates, runs, attachments, comms, dispatch bodies, sync alerts, templates and analytics are readable by every role (scope section 1, Q-035b, Q-043g, Q-045f).

## 3. API3 Broken Object Property Level Authorization: what was checked

- **Request bodies:** every mutation binds a purpose-built record, never an entity. No body accepts `Status`/`CurrentStatus`, `Version`, `Role`, `Id`, `CreatedBy*`, `LastChangedBy*`, `CertifiedBy*`, `CompletedBy*` or a hash. The server computes attachment and import SHA-256 and ignores a client `sha256` part. Import files refuse status columns as header errors (Q-048c). `PATCH /users/{id}` takes only `handle` and `isActive`, and CSV import cannot change an existing user's role. No endpoint PATCHes a status column (CLAUDE.md rule 2): train, gate, PIR, known-issue, template, export, import and schedule states move only through action endpoints and services. **Exception found and fixed:** `requestedByUserId` on overrides (SEC-A1). The other "on behalf of" fields are assignments a planner is meant to make: task, step and condition owners, and the PIR-action owner, which must be an active user where the service checks it.
- **Responses:** no response carries a credential, a webhook URL (allowlist rows give host and an elided URL only, Q-042c), a token or its hash, a storage path (attachments, exports and imports return names and SHA-256 only), a stack trace (REOS-53 `SecretsTests`) or audit JSON outside the AuditRead routes. The PDF release report, run sheet and scorecard have no audit section; only the evidence pack prints the audit extract. The 409 bodies return the row the caller could already read or is allowed to change. **Observations:** `GET /users` gives every role e-mails, roles and active flags; connector base URLs are visible to all roles (section 7).

## 4. API5 Broken Function Level Authorization: what was checked

- **SignalR hub `/hub/trains`:** requires the Read policy. It declares **no client-callable method**: no "join group", no send, so a client cannot subscribe to a train or another user's channel. Server pushes are `TrainChanged(id, version)`, `ForecastChanged(trainId, runId)`, `SyncAlertRaised(alertId)` and `ServerTime` to all (ids only, D7: clients refetch through the authorised API), plus `NotificationCreated(id)` to one user by the `uid` claim (`UidUserIdProvider`). Pinned by `Pin_the_hub_exposes_no_client_callable_methods`: reflection finds no declared public method, and a Viewer's invocations of `JoinTrain`, `AddToGroupAsync`, `OnConnectedAsync` and `SendAsync` are refused.
- **Alternate routes and method switching:** the duplicate spellings share one handler and one policy: `/exports/*` and `/export/*`; `/sync/state` and `/sync/health`; `/ics/{t}.ics` and `/all.ics`; `/attachments?trainId` and `/trains/{id}/attachments`. `DELETE /export-jobs/{id}` answers 405 for everyone. Overrides have no PUT, PATCH or DELETE route (`FreezeTests`), and neither do dispatches or GoNoGo rows (immutable by trigger too). The SPA fallback and the anonymous routes (`/healthz`, `/auth/config`, `/auth/login`, `/auth/logout`, the ICS feeds) were reviewed; none performs a privileged action.
- **Dev routes:** `/auth/dev-login`, `/api/v1/dev/*` and `/api/v1/dev/sync/*` are mapped only in Development; the role matrix asserts they are absent in Production.
- **Import kinds writing privileged tables:** Users, Teams and Holidays need the Admin policy (RTE and RM in v1). A new user may be created with any role, including Governance Officer (Q-048f); the identity provider's role replaces it at first sign-in (`UserProvisioner`). The only lever this gave, faking a freeze-override requester, is closed by SEC-A1. Recorded as an observation.
- **Role decided or re-checked in the handler or service:** evidence-pack export jobs, attachment upload, dispatch (`DispatchRole`), freeze create, grant and request, waiver approval, Go/No-Go, template approve and retire, close-out, and known-issue accept all re-read the role from the database, so a cookie role that is out of date (F10) cannot widen them. The audit routes (`/audit`, `/audit.csv`, the audit grid export) decide on the cookie role alone, which is the known F10 limit (Q-053c).

## 5. API6 Unrestricted Access to Sensitive Business Flows: what was checked

- **Go/No-Go:** RM only, with the role re-read from the database; `If-Match` on the train (replay is 409) and a decision bumps the train Version; `DecisionTooSoon` stops two decisions in one second; rows are immutable.
- **Certify, waive, waivers:** each action runs in one write transaction. EF Core's SQLite `BeginTransaction` is `BEGIN IMMEDIATE`, so writers serialise and a concurrent second certify or approve re-reads the new state (`WaiverAlreadyDecided`, `IllegalGateTransition`). Waiver approver ≠ requester is checked by the service and by the trigger. Compliance SoD holds after SEC-A2.
- **Freeze overrides:** SEC-A1 (fixed). Overrides are immutable, and a renewal is a new row that now needs a new request.
- **Comm dispatch:** role re-checked; `If-Match` is the previewed train Version; an unknown token blocks; webhooks go only to allowlisted HTTPS destinations. SEC-A4 (race) fixed. Re-dispatching a template without a schedule item is allowed by design (Q-045f); see observation O-1.
- **ICS token rotation:** own token only; rotate is revoke plus insert in one transaction, with a unique index on the active token; a concurrent rotate gets `IcsTokenRevoked`.
- **Export generation:** EvidencePack gated; an identical open job is deduplicated inside the write transaction; jobs cannot be deleted (Q-050c). There is no per-user quota (O-1).
- **Import commit:** uploader only; `If-Match` on the job; one write transaction; a second commit gets `ImportCommitted`; the plan is re-computed and must match its signature; the trigger refuses a job with errors.
- **Notifications:** created only by services and the scheduler; marking read is idempotent and audited. Override requests notify every approver with no rate limit (O-1).
- **Parser commit, attachment upload and delete, baseline capture (one per train), PIR, and close-out** were read for replay and races; each is guarded inside its write transaction.

## 6. Checked, no issue

API1: the per-user, owned and parent/child routes listed in section 2; evidence-pack jobs; the audit grid; id guessability. API3: every request DTO in `Endpoints/*.cs`, every response record listed in section 3, and no entity bound from a body. API5: the hub (no client methods; the user channel is keyed by `uid`), alternate spellings, method switching, dev routes, anonymous routes, and inline role re-checks against the database. API6: Go/No-Go, certify, waive, waiver approval, ICS rotation, import commit, parser commit, baseline capture, and export dedup. Concurrency relies on `BEGIN IMMEDIATE` per service call; the one check-then-act outside a transaction was SEC-A4.

## 7. Observations: reported, not changed (Q-SEC-A5)

These were seen and judged either by design or in need of a product decision; none was counted as a finding.

- **O-1 · No per-user limits on repeatable flows (API6/API4):** any role may queue unlimited PDF export jobs (`ExportService.cs:68` deduplicates only an identical open job, and files are kept forever per Q-050c); each `override-requests` call notifies every RM and GO (`FreezeService.cs`, `RequestOverrideAsync`); dispatch without a schedule item may be repeated (Q-045f). This belongs with the API4 review (rate limiting and quotas).
- **O-2 · `GET /users` returns e-mail, role and active flag to Viewers** (`AdminEndpoints.cs:20`, `AdminService.cs:15`). The pickers use `/owners` (names only), but the scope lets a Viewer read everything except the audit log.
- **O-3 · Import previews are readable by any planner** (`ImportEndpoints.cs:70-74`); only the uploader commits. Planners can export the same data anyway.
- **O-4 · The webhook allowlist and dispatch sit with the same roles** (RTE and RM, D14/D32; `SyncEndpoints.cs:56`), so the allowlist guards against mistakes, not against a hostile RTE. The SSRF checks remain.
- **O-5 · Existence oracle on `POST /notifications/{id}:read`:** someone else's id gives 403 and an unknown id gives 404 (`NotificationEndpoints.cs:27`). Decided and pinned by `NotificationTests`; ids are UUIDv7, so there is nothing to enumerate.
- **O-6 · Template approval has no author ≠ approver rule** (decided, Q-038t1). **An RTE may deactivate any user**, including RMs and GOs (v1 admin, D14/D32); signing in again reactivates (Q-053c).
- **O-7 · Connector base URLs are visible to every role** (`ConnectorView.BaseUrl`): internal host names, no credential.

## 8. Not verified

- **SignalR over WebSockets after deactivation:** an open WebSocket is authorised once, so a deactivated user's socket would keep receiving id-only pushes until it reconnects. Long polling is re-authorised on every poll. This was reasoned from the framework's behaviour, not run: the in-memory test server was driven with long polling only.
- **More than one app process:** the SEC-A4 lock is in-process. The deployment is a single app instance on one SQLite file (D3), and a multi-instance setup was not tried.
- **The Freezes screen after SEC-A1:** the UI is unchanged. A grant naming someone without an open request now shows the 422 message inline, which lists who has an open request. This was not exercised in Playwright.
- **Exception text in Viewer-visible export errors and sync alerts** (`ExportWorker.cs:117-133` passes `ex.Message`, which for an I/O error can include a server path). Reasoned, not reproduced.
- **OIDC-specific paths** (group-to-role mapping, a role change at the IdP, F10): these belong to the authentication review. Here, only the database role re-checks in section 4 were confirmed.

## 9. Changes

Code and tests: commit `95cb645` (on top of `c9a1180`); this document and the Q entries follow in the next commit.

| File | Change |
|---|---|
| `src/ReleaseMgmt.Infrastructure/Services/FreezeService.cs` | SEC-A1: an open, audited request is needed per grant; guard `OverrideNotRequested`; config `Freeze:OverrideRequiresRequest` (default true) |
| `src/ReleaseMgmt.Infrastructure/Services/TaskService.cs` | SEC-A2: completing a completed task, or reopening an open one, is a no-op |
| `src/ReleaseMgmt.Infrastructure/Services/IcsTokenService.cs` | SEC-A3: a feed token resolves only while its user is active |
| `src/ReleaseMgmt.Infrastructure/Comms/CommDispatchService.cs` | SEC-A4: dispatches that name a schedule item are serialised per item |
| `tests/ReleaseMgmt.Api.Tests/AuthzScanTests.cs` (new) | the four reproductions, the config switch, and two pins (hub, per-user objects) |
| `tests/ReleaseMgmt.Api.Tests/FreezeTests.cs` | grants go through the requester's request first (assertions unchanged) |
| `docs/QUESTIONS.md` | Q-SEC-A1 to Q-SEC-A5 |

There is no schema change, no new package, no web change and no `Program.cs` change. The new configuration key is `Freeze:OverrideRequiresRequest` (optional, default true).
