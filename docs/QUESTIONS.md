# Open questions from build sessions

Claude Code: add a question here instead of deciding. Format:

```
## Q-001 · M<n> · <short title>
Context: <what you were building>
Question: <the decision needed>
Options considered: <a / b, with tradeoffs>
Blocked: <what is blocked, what you did instead>
```

## Q-001 · M1 · How the EF migrations create the schema
Context: REOS-16/17. M1 asks for EF entities + fluent config (CHECKs, partial indexes, FKs) and a triggers migration.
Question: may migration `Schema` execute db/schema.sql's DDL verbatim instead of EF-generated DDL?
Options considered: (a) fluent-configure every CHECK/FK/index in EF (47 tables, ~100 CHECKs; drift risk between two definitions of the contract) / (b) run schema.sql's CREATE TABLE/INDEX text verbatim, map entities for columns and keys only, and assert with tests that migrated DDL text equals schema.sql and the EF model matches every column.
**Decided 2026-09-29 (best practice, on the user's instruction to use judgement): keep (b).** One definition of the contract, drift caught by tests.
Blocked: nothing. Built (b): `SchemaContractTests` proves table/index/trigger DDL is byte-identical to schema.sql and the model maps every column. Relationships are not navigations; FKs live in the database. Say if you want (a).

## Q-002 · M1 · Where domain services live
Context: CLAUDE.md puts "domain services (no EF, no ASP.NET)" in ReleaseMgmt.Domain, but the services must read and write the database in one transaction with an audit row (rule 3).
Question: are services allowed to depend on the DbContext?
Options considered: (a) Domain holds pure guard logic over plain snapshot records; orchestration classes (TrainLifecycleService, GateService, …) live in Infrastructure/Services / (b) repository interfaces in Domain with EF implementations.
**Decided 2026-09-29: keep (a).** Keeps guards unit-testable without a database and follows the existing dependency direction.
Blocked: nothing. Building (a) (fewer abstractions, guards stay unit-testable without a database).

## Q-003 · M1 · Two sources of truth for a user's role
Context: authorization uses the role claim (IdP group map, `Auth:RoleMap`), but the separation-of-duties triggers read `Users.Role`.
Question: which wins?
Options considered: (a) just-in-time provisioning: on every sign-in upsert the `Users` row (email, display name, role from the claim), so both always agree / (b) admins maintain `Users.Role` and the claim is ignored / (c) both, with a mismatch refused.
Blocked: nothing. Built (a): `UserProvisioner` runs at sign-in (dev login and OIDC) and puts the `Users.Id` in a `uid` claim. Admin-edited roles are overwritten at the next sign-in; say if you want (b).
**Decided 2026-09-29: keep (a).** The identity provider is the single source of truth for who someone is and what role they hold; a role edited in the app would silently diverge from IdP group membership. Consequence: the Admin screen shows the role but role changes are made in the IdP group map (`Auth:RoleMap`).

## Q-004 · M1 · Is If-Match required?
Context: PROJECT_SCOPE §6 says "Mutations take If-Match: <Version>"; D8 says stale writes get 409.
Question: reject a mutation that has no If-Match header (428), or accept it and skip the check?
**Decided 2026-09-29: required.** Optimistic concurrency only protects anyone if clients cannot skip it. A versioned mutation without `If-Match` now gets 428 `{guard:"PreconditionRequired"}`. Config key `Api:RequireIfMatch` (default true) can relax it; the API test factory sets it false for older contract tests and a new test covers the 428.
Blocked: nothing. Was built optional; when present and stale -> 409 with the current row. Making it mandatory is a one-line change once the UI always sends it (M2).

## Q-005 · M1 · Waiver rejection has no column
Context: PROJECT_SCOPE §6 lists `POST /waivers/{id}:reject`, but GateWaivers has no rejected/decided-by fields.
Question: add columns (schema change, D-level decision) or drop the endpoint?
**Decided 2026-09-29: drop `:reject`; no schema change.** A waiver is approved or it is not; an unapproved request simply never unlocks the gate, and the reason it was declined belongs in the request thread/audit note, not a new status. Adding a decision column would change the 47-table contract for no rule that needs it. PROJECT_SCOPE §6 updated. Revisit if Governance needs a recorded rejection reason (then: schema.sql first, per CLAUDE.md rule 1).
Blocked: rejection is not built; only request and approve are.

## Q-006 · Password reset with MFA (REOS-59)
Context: requested 2026-09-29: "a way for a user to reset their password via MFA code or app". Sign-in today is OIDC (D12, OI-5), so the identity provider owns passwords and MFA. `Auth:Oidc:*` plus a dev-only fake login are all that exist.
Question: where should password reset live?
Options considered:
- (a) **Use the IdP's self-service reset** (Entra ID SSPR or equivalent): a "Forgot password" link to the IdP; MFA comes with it; near-zero code, no new secrets to protect, consistent with D12. Depends on your IdP offering it.
- (b) **Local accounts in this app**: password hashing (Argon2/PBKDF2), authenticator-app TOTP (RFC 6238) with recovery codes, rate limiting, audit rows, reset flow. Real scope: new tables (schema change), a security review, and it duplicates the IdP.
- (c) (b) plus emailed or texted codes: needs SMTP, which is off in v1 (OI-7), and an SMS provider.
Blocked: REOS-59 only. Nothing else waits on it.
**Answered 2026-09-29: option (a).** Built as config `Auth:PasswordResetUrl` (absolute https only, else ignored with a logged warning), exposed via anonymous `GET /auth/config`, shown as a link on the sign-in page. Reset and MFA are the identity provider's (e.g. Entra SSPR); the app stores no passwords or MFA secrets.

## Q-007 · M2 · What "Health" means in the Bundled products table (REOS-24)
Context: the Main mockup shows Health per product (On track, At risk, Ready) but no document defines it.
Question: how is a product's health derived?
Options considered: (a) purely from data we hold: Ready = it has tasks and all are done and no open blocker; At risk = any unresolved blocker on the product; On track = everything else / (b) add a manual health field (schema change) / (c) weight by blocker severity or task lateness.
Blocked: nothing. Built (a) as a provisional default in `TrainQueryEndpoints` (`/trains/{id}/products`): no schema change, no manual input to drift, and it can be swapped without touching the API shape. A product with no tasks is On track, never Ready. Say if Governance wants severity weighting (c).

## Q-008 · M2 · Time zone of the deployment window (REOS-24)
**Resolved 2026-09-29: this was already decided by D24, and the provisional answer I first built (the viewer's browser zone) was wrong.** D24: the server stores and returns UTC; the client formats in `Display:TimeZone` (default `America/Chicago`). Corrected in REOS-32: `GET /api/v1/config` exposes the zone, every screen formats and the window/step editors convert in it (DST-safe), and the zone abbreviation is shown next to times. No open question remains.

## Q-009 · M2 · Do UI-state autosaves get an audit row and a Version? (REOS-26)
Context: CLAUDE.md rule 3 says every service write stamps `Version` and writes one `AuditEvents` row. `UserSessionState` (per-tab UI state, saved every second while typing) has no `Version` column in schema.sql.
Question: does rule 3 apply to it?
Options considered: (a) exempt it: it is a private UI scratchpad, not a domain record; auditing would bury the real audit log in autosaves / (b) audit each save / (c) audit only draft commits (those already are audited by the domain write they produce).
Blocked: nothing. Built (a): `SessionService` writes no audit rows and no Version; a save only ever touches the caller's own (user, clientId) row. Domain writes made from a draft (add task, set window) are audited as before.

## Q-010 · M3 · When does the runbook stop being editable? (REOS-29)
Context: `RunbookSteps` is the plan; actuals live in `StepExecutions` and never overwrite it (rule 7), but a run has no copy of the plan: lateness is measured against the plan as it stands.
Question: may steps be added, edited or re-wired while a train is Executing, or while a Live run is open?
Options considered: (a) editable until the train is Complete or Aborted / (b) locked once the train is Executing / (c) locked while a Live run is open (and once Complete or Aborted); rehearsals never lock.
Blocked: nothing. **Decided 2026-09-29 (best practice, on the instruction to use judgement): (c).** Editing the plan under a live run would move the goalposts the run is being measured against and make late/on-time answers depend on when someone edited; rehearsals stay editable because that is how a plan gets fixed. Guards `RunInProgress` and `TrainClosed`; every plan change is audited with before/after. (b) would also block legitimate pre-window fixes while Executing.

## Q-011 · M3 · Forecast rules the scope leaves open (REOS-31)
Context: PROJECT_SCOPE section 3 gives the forecast formula and the escalation thresholds; a few cases it does not settle. The handoff scenario (`r26-24-scenario.json`) pins the main path and passes exactly; these are the edges.
Question and what was built (all provisional, one place each in `RunForecasting` / `RunService`; say if any should change):
- **A step that has not started cannot start in the past:** its forecast start is the latest of its planned start, its dependencies' forecast ends and *now* (the text only names "now" for the running step's successors; a general floor is the same rule without the special case).
- **Skipped** is transparent (it ends when its dependencies end). **Failed** stops the forecast: everything after it, and the finish, are unknown and reported in `blockedByFailed`; no alert is raised on an unknown finish (the failure itself is the alert).
- **Rehearsal:** the plan and the window are shifted by the same D28 offset, so a rehearsal has its own deadline. Rehearsals never page anyone.
- **Late escalation** fires when a Live step *starts* 5+ minutes behind its plan (owner or owning team's members + all RTEs, level 1) and, at 30+ minutes, also the Release Managers (level 2). A step that is late because it has not started yet is REOS-37's timer (NotificationScheduler).
- **Rollback deadline crossing** notifies all RTEs and Release Managers once per run, the first time the forecast finish passes the deadline.
- Notification or push failures after a committed step are raised through `IAlertSink`, never swallowed and never undo the step.

## Q-012 · M4 · Go/No-Go rules the scope leaves open (REOS-33)
Context: PROJECT_SCOPE names the endpoints and D29 the expiry behaviour; a few edges are not settled.
**Decided 2026-09-30 (best practice; say if any should change):**
- **The latest decision is picked by `DecidedAt`** (also inside the Executing trigger), and timestamps have whole-second resolution, so a decision that is not strictly later than the latest is refused (`DecisionTooSoon`) instead of guessing an order.
- **Conditions can be added after the fact only to the latest GoWithConditions decision** (`NotLatestDecision` otherwise); decisions themselves never change. A superseding decision starts a fresh condition list.
- **Who closes a condition:** its owner or a Release Manager. Recording and adding conditions is Release Manager only (RTE has no Go/No-Go rights, PROJECT_SCOPE section 1).
- **A NoGo may carry `newTargetReleaseDate`; recording it does not move the train's target.** Moving the target stays the explicit `PATCH /trains/{id}` (audited, rebases gates), so one action never changes two things.
- Recording or closing bumps the train `Version` so open views go stale (409) and live clients refetch.
- Expiry notifications (owner + Release Manager, 1 h before) belong to REOS-37's scheduler.

## Q-034a · M4 · Who creates freeze windows, and what does a Chill do? (REOS-34)
Context: the role table in PROJECT_SCOPE section 1 lists request and approve for overrides but not creating a freeze window.
**Decided 2026-09-30 (best practice; say if it should change):** windows are created by a Release Manager or Governance Officer (policy `FreezeApprove`, checked again in `FreezeService`, guard `FreezeCreateRole`): a freeze is a governance control, and an RTE who can request overrides should not also define the rules. There is no edit or delete of a window in v1 (nothing in section 6 asks for it). `Kind = Chill` is advisory: the trigger only enforces `Freeze`, so the service guard does too. No maximum override TTL is enforced (the schema only needs ExpiresAt > ApprovedAt); the expiry must be in the future.

## Q-034b · M4 · How is an override "requested" when FreezeOverrides only has approved rows? (REOS-34)
Context: `FreezeOverrides` has `RequestedByUserId` and `ApprovedByUserId` both NOT NULL and rows are immutable, so there is no pending state to store; adding a requests table would change `db/schema.sql`.
Options considered: (a) no schema change: the request is an audited service call that notifies the other Release Managers and Governance Officers (`FreezeOverrideRequested`), and the approver inserts the immutable row naming the requester / (b) a `FreezeOverrideRequests` table.
**Built (a):** `POST /freezes/{id}/override-requests` (RTE or RM) writes one audit row and notifies approvers; `POST /freezes/{id}/overrides` (RM or GO) inserts the row with `requestedByUserId` in the body. The approver must not be the requester (`OverrideSelfApproval`), and the requester must be an active RTE or Release Manager (`OverrideRequesterRole`, D32). Revisit as (b) if the team wants a queue with a proper pending state.

## Q-034c · M4 · Where the freeze guard applies, and what readiness says (REOS-34)
Context: the story says "gate/step start"; `trg_Step_FreezeLockout` only covers a Live Deploy-section step (StepExecutions to Running). Gate starts have no freeze trigger.
**Decided:** the service guard `FreezeLockout` mirrors the trigger exactly (Live run, Deploy section, start time inside a `Freeze` window, product pattern or no pattern, no unexpired override for that train) and runs in `RunService.StartStepAsync` after the dependency check. Gates are not blocked (no rule in the scope or schema). `IReadinessService` is unchanged: the trigger does not fire on train advance, so a freeze is not an Executing blocker; the train screen shows freezes and override state instead (`GET /freezes?trainId=`).
