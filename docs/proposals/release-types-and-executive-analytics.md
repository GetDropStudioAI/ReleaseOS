# Release types, configurable gates and milestones, and executive analytics

Status: proposal, 2026-10-23. Nothing was built or changed. The owner asked: "What enhancements can we make to include:
- VP-level and Director-level analytics;
- configurations for gates and milestones;
- CERT, Production, Standard Production and On-Demand Production, with an option to combine Standard and On-Demand Production;
- Beta 1, 2, 3 releases, and PenTest releases."

Each topic below has five parts: what the app does today, the gap, options, a recommendation, and the questions only the owner can answer. Several of the owner's terms can mean different things. Where a term is ambiguous, this document lists the readings and asks; it does not pick one. Section 5 fits the work into the redesign's build order, and section 6 lists the decisions.

## 0. Facts every topic depends on

| Fact | Where |
|---|---|
| A release is a **train** with **one** target date. `TargetReleaseDate` is commented "the CERT date; gate offsets hang off it" | `db/schema.sql` (`ReleaseTrains`), PROJECT_SCOPE section 3 |
| A train has **no type, track, owner or team**; only `RiskTier` (Low, Moderate, High, VeryHigh). REOS-107 adds key, owner, team, target environments, branch and on-call | `ReleaseTrains`; REOS-107 |
| **One deployment window per train**, enforced by UNIQUE | D25, `DeploymentWindows` |
| **One baseline per train**. It is captured when the first gate whose name contains "Freeze" certifies, or by hand. Executing requires it | D15, `trg_Gate_CertifyCapturesBaseline`, `trg_Train_ExecutingRequiresBaseline` |
| **Cross-train dependencies are out of v1**. REOS-85 only warns when two windows collide | PROJECT_SCOPE section 2 |
| **No environments** until REOS-109 (name, region, kind staging or production, order) | REOS-109, D42 |
| **Workspaces**: roles are held per workspace; "nothing is shared across workspaces" | D45, QUESTIONS Q-090e, `WorkspaceMembers` |
| The ServiceNow change type (Standard, Normal, Emergency) is **not stored**. Proposal P10 would add it as `ChangeType` | `docs/proposals/release-guide-gap-analysis.md` item 5 and P10 |

**Cost of any schema change** (from the gap analysis, section 2):
- `db/schema.sql` first, with `tests/reference/test_schema.py` cases.
- `tools/gen_entities.py`, then re-scaffold and patch the two migrations.
- A numbered `db/upgrades/` script, with `test_upgrades.py` and `UpgradePathTests`.

Two kinds of change cost more:
- **Tables with triggers** (`ReleaseTrains`, `StageGates`) can take new columns with `ALTER TABLE ADD COLUMN`, but a rebuild drops their triggers (CLAUDE.md rule 1).
- **A change to `db/analytics.sql`** regenerates the fixtures (`seed_and_query.py --write`).

**Two naming collisions to settle first:**
- **"Standard"** is also ITIL's "standard change" (pre-approved, low risk). ServiceNow's `change_request.type` uses it, and P10 proposes it as a change type.
- **"Production"** is also REOS-109's environment kind.

A release type called "Standard Production" would sit beside both, so the names need care.

---

## 1. VP-level and Director-level analytics

### 1.1 Today

- **Analytics screen.** `/analytics` (`AnalyticsScreen.tsx`) has a period, six headline figures, one chart per metric, and CSV, XLSX and SVG per chart. Every role may read it (`Policies.Read`).
- **Fifteen metrics.** M1 to M15 live in `db/analytics.sql`. They are computed live (D16) through `GET /api/v1/analytics/{metric}?from&to&now` (`AnalyticsEndpoints.cs`).
- **Filters.** PROJECT_SCOPE section 6 lists `train`, `product`, `riskTier` and `gateClass` filters. **None is implemented**: the endpoint takes the window only.
- **Grouping.** The only groupings are:
  - gate name (M3 to M5);
  - month (M13, M15);
  - severity (M9);
  - freeze window (M11);
  - source system (M14).

  There is no team, workspace, type, environment or quarter grouping.
- **Workspace scoping.** `db/analytics.sql` has **no `WorkspaceId` predicate**. Once a second workspace exists, every metric adds up every workspace's rows. REOS-105's EF query filters do not reach raw SQL. This belongs in REOS-105's scope as a fix, whatever the owner decides below.
- **Outputs.**
  - The only report is the per-train post-release scorecard PDF (`ScorecardDocument.cs`).
  - `MetricSnapshots` are written only for evidence packs (D16).
  - PROJECT_SCOPE section 9 promises an "analytics workbook, one sheet per metric", but only one metric per XLSX exists.
- **Overview.** REOS-95 (planned) is an operational overview of the current workspace: four metric cards and a 14-day throughput chart, from `GET /overview` (REOS-108).
- **Known metric weaknesses.** There is no time to restore, and M10 ignores aborted trains (gap analysis P9).

### 1.2 Gap

A VP needs figures across several workspaces, by quarter, with trends. A Director needs figures by team, with causes (slip reasons, approval waits). The app has neither the dimensions (team, type, quarter, workspace) nor the access model (anything above one workspace) nor a portfolio report.

### 1.3 What each audience needs, mapped to metrics

| Need | Audience | Existing metric | What changes |
|---|---|---|---|
| Predictability: on-time % by quarter, workspace, team, type | VP | M1 (period only) | Group by quarter, workspace, team, type |
| Slip size and spread | VP, Director | M2 | Same grouping; median and p90 |
| Change failure rate, rollback rate | VP | M10 | Count an aborted train that rolled back (P9); keep "release-level, not DORA" |
| Time to restore | VP | none | **M16** (P9) |
| Throughput and lead time | VP, Director | M15 (monthly) | Quarter; by team and type (**M22**) |
| Compliance and risk exceptions | VP | M13 waivers, M11 freeze overrides | **M18** exceptions summary |
| Risk exposure of upcoming releases | VP | none | **M21** |
| Deployment success, availability | VP | none | **M23**, after REOS-110 and REOS-111 |
| Gate cycle time, first pass, late certification | Director | M3, M4, M5 | By gate owner team and quarter |
| Blocker ageing | Director | M9 (as of now) | By team and product; any past date works, since `RaisedAt` and `ResolvedAt` are kept |
| Slip reasons | Director | none: target changes are only audit rows; a No-Go keeps `NewTargetReleaseDate` and free-text notes | **M20**, needs new data |
| Approval latency | Director | none | **M19** |
| Runbook variance and overrun | Director | M7, M8 | By step owner team |
| Scope churn | Director | M6 | By team, type |
| Comms timeliness | Director | M12 | By team |
| PIR follow-through | Director, VP | none | **M24** |
| Sync health | Operations | M14 | Point in time only (`LastSyncedAt` is overwritten) |

Proposed new metrics (numbered after M15; M16 is the gap analysis's P9):

| Key | Metric | Source | Data change |
|---|---|---|---|
| M16 | Time to restore: first Failed step to the end of a RolledBack Live run, median | `StepExecutions`, `RunbookRuns` | none |
| M17 | Predictability by period: M1 and M2 by quarter, workspace, team, type | as M1, M2 | dimensions only |
| M18 | Exceptions summary: waivers, freeze overrides, conditions left open past expiry, decertifications, Compliance gates certified late, PIRs overdue | `GateWaivers`, `FreezeOverrides`, `GoNoGoConditions`, `AuditEvents` (`Decertified`), `StageGates`, `PirDue` | none |
| M19 | Approval latency: waiver requested → approved; override requested (audit row) → granted; gate's last task done → certified | `GateWaivers`, `AuditEvents`, `FreezeOverrides`, `ChecklistTasks`, `GateTransitions` | none (REOS-113 adds per-approver times later) |
| M20 | Slip reasons: days slipped by reason | new `TargetChanges` rows | **yes** |
| M21 | Risk exposure: open trains by risk tier, days to target, readiness blockers, High/VeryHigh without a rehearsed rollback | `ReleaseTrains`, readiness | none (as of now) |
| M22 | Throughput by team and type | M15 + REOS-107 team + topic 3 type | through those stories |
| M23 | Deployment success and availability | REOS-110, REOS-111 tables | through those stories |
| M24 | PIR follow-through: reviews held by their due date; actions overdue | `PostImplementationReviews`, `PirActions`, `PirDue` | none |

For M20, a slip reason could be one row per target change after the baseline. For example: `TargetChanges(Id, ReleaseTrainId, FromDate, ToDate, ReasonCode, Note, ChangedByUserId, ChangedAt)`. The service writes it in the same transaction as the target change; the reason is required once a baseline exists.

### 1.4 Options: how each audience reaches it

| Option | What it is | Data model | Screens and outputs | Effort |
|---|---|---|---|---|
| **A. Per workspace only** | Today's model plus dimensions. The VP is a Viewer in every workspace and reads each in turn; a portfolio total is made outside the app from the XLSX | none | Analytics gains group-by and quarter | S |
| **B. Portfolio across the person's own workspaces** | Analytics take `workspaces=` (any set the person is a member of; default the current one) and `groupBy=workspace,team,type,quarter,riskTier`. A Director is a member of their workspaces; a VP is a Viewer in all. What a person sees is the union of their own memberships, so no new role | none | A "Portfolio" scope on the Analytics screen; portfolio PDF; workbook | M |
| **B2. B plus portfolio groups** | Workspaces grouped into named portfolios ("Payments", "Cards"), so a Director picks a group, not a list | `Portfolios(Id, Name, Version)`; `Workspaces.PortfolioId` | group picker | +S |
| **C. Host-wide executive role** | A host-level read role sees aggregates of every workspace without membership, with no drill-down to release detail | `Users.IsExecutive` or a `HostRoles` table; a new policy; an exception in REOS-105's isolation tests | as B | M–L; contradicts D14 (four roles) and Q-090e |

Outputs for any option:
- **Portfolio report PDF.** A new QuestPDF document with the period, headline figures, a quarter trend, a per-workspace or per-team table, the exceptions list and the top risks. `TrainDocument.cs` assumes one train (its footer prints the train version), so it needs a non-train base. Effort M.
- **Analytics workbook.** One XLSX with a parameters sheet and one sheet per metric, as PROJECT_SCOPE section 9 says (coverage G19). Effort S.
- **Scheduled delivery** (a monthly email or webhook) is not proposed. Auto-send is out of v1, and there is no SMTP (OI-7).
- **Trends of point-in-time metrics.** M9 can be recomputed for any past date. M14 and M21 cannot. Quarter-end snapshots would contradict D16 and the rejected "nightly jobs", so they are not proposed unless the owner wants those trends.

### 1.5 Recommendation

1. Add the workspace predicate to `db/analytics.sql` inside REOS-105.
2. Then build option **B** with group-by and quarter.
3. Add the metrics that need no new data: M16 to M19, M21 and M24.
4. Then the workbook, then the portfolio PDF.
5. M20 follows the owner's answer on slip reasons. M22 follows REOS-107 and topic 3; M23 follows REOS-110 and REOS-111.
6. No new role. B2 only if the owner says Directors own a fixed group of workspaces.

### 1.6 Owner questions

1. May anyone see figures across workspaces? (a) No, per workspace only. (b) Yes, across the workspaces the person is a member of. (c) Yes, a host-wide executive role sees all. **Recommended: (b).**
2. What separates a VP from a Director in the app? (a) Only which workspaces they are members of. (b) Named portfolio groups of workspaces. (c) New roles. **Recommended: (a)**, then (b) if needed.
3. Should a target-date move after the baseline require a reason? (a) No. (b) A required pick list plus a note; for example Scope, Defects, Dependency, Environment, Resourcing, Business request, Compliance. (c) Free text. **Recommended: (b).**
4. Is a quarter calendar or fiscal? If fiscal, which month starts the year (`Analytics:FiscalYearStartMonth`)?
5. Should the change-failure rate count an aborted train whose live run rolled back (P9)? **Recommended: yes.**
6. Which outputs first? (a) The screen only. (b) The screen and the workbook. (c) Those plus the portfolio PDF. **Recommended: (b), then (c).**

---

## 2. Configurable gates and milestones

### 2.1 Today

- **Templates are the configuration.** All are per workspace:
  - `TrainTemplates`: name; status Draft, Approved or Retired; default risk tier; approver; review due 12 months after approval.
  - `TemplateGates`: name, class Standard or Compliance, order, `OffsetDays` in business days before the target, `RequiredBeforeStatus` (Gated, Executing or Complete), owner team.
  - `TemplateSteps` (runbook skeleton) and `TemplateCommSchedule` (T-minus plan).
- **Who may change templates** (`TemplateService.cs`):
  - RTE and Release Manager create and edit Drafts.
  - Release Manager or Governance Officer approve and retire.
  - Approval needs at least one gate.
  - **Nothing stops the author approving their own template** (there is no author column).
- **No versions.** An Approved template cannot be edited. A change means a new Draft under a new name (names are unique per workspace) and retiring the old one. Nothing links the two.
- **Copy at creation.** A new train copies the template (`TrainCreationService.cs`). A later template change never reaches existing trains.
- **Not in templates.** Templates carry no checklist tasks (gap analysis P2) and no milestones (Q-0846e).
- **Gates can be changed on the train.** RTEs and Release Managers may add, edit and remove gates while it is Planning or Gated (Q-081b). The edit can change name, **class**, offset, owner and required-before (Q-081e). A Pending gate with no history can be removed (Q-081d). So a Compliance gate from an approved template can be downgraded to Standard, or removed, before it starts. This is audited, not prevented.
- **Fixed rules in triggers:**
  - gates certify in order (`trg_Gate_CertifyInOrder`);
  - a Compliance gate needs a Governance Officer certifier who did none of its tasks, and at least one task;
  - the baseline depends on the word "Freeze" in a gate name;
  - required-before has three levels.
- **Offsets** count business days before the target only, 0 to 365 (Q-081f). There is no other anchor and nothing after the target.
- **Milestones** (`TrainMilestones`) have an absolute date and are informational. They are not tied to the target (Q-0846b), not in templates or PDFs (Q-0846e), and have no reminders (Q-0846d).
- **Default gate set** (OI-10): Code Freeze T-5, QA Sign-off T-3, Compliance Sign-off T-2, CAB Approval T-1.

### 2.2 Gap

- A release type cannot name its gate set (there is no type; topic 3).
- Templates cannot carry milestones or tasks.
- No gate is protected: a planner can downgrade or remove it.
- Templates have no author/approver separation, no versions and no diff.
- There is one anchor date.
- The baseline depends on a gate's name.

### 2.3 Options

| Option | What it is | Data model | Screens and analytics | Effort |
|---|---|---|---|---|
| **A. Richer, versioned templates** | Templates carry checklist tasks (P2) and milestones with offsets. "New version" copies an Approved template to a Draft; approving it retires the old one in the same transaction. The approver must not be the author | New `TemplateTasks` (P2's shape) and `TemplateMilestones(Id, TemplateId, Name, OffsetDays, OwnerTeamId, SequenceOrder, Version)`; `TrainTemplates.CreatedByUserId`, `SupersedesTemplateId`, `VersionNo` (no triggers on that table) | Templates editor and history (coverage G7); New train shows the version. Analytics: compare by template version | M |
| **B. A plus mandatory gates** | A gate marked mandatory in the template cannot be removed from the train, lowered from Compliance to Standard, or moved to a later required-before. Skipping it stays possible through a waiver (D10) | `TemplateGates.IsMandatory`; `StageGates.TemplateGateId`, `StageGates.IsMandatory` (`ALTER TABLE ADD COLUMN`; the table has triggers); new triggers in `schema.sql` in file order, with oracle cases | Gate panel shows "mandatory, from template v3" | M–L |
| **C. Gate policy per release type and risk tier** | A workspace policy says which gates are required, their minimum class and maximum offset, and which milestones are required. A template is approved only if it meets the policy; readiness lists a train's policy breaches | `GatePolicies`, `GatePolicyRules`; overlaps REOS-113's approval policies (environment × risk tier) | A policy screen (REOS-113's "Manage policies") | L |

Smaller choices that go with any option:
- **Milestone offsets on the train.** `TrainMilestones.OffsetDays` (nullable). When set, the date moves with the target. This reverses Q-0846b for those milestones. Effort S.
- **Second anchor date.** `TemplateGates.Anchor` and `StageGates.Anchor`, for example Target or Production. Only needed if topic 3 gives a release two dates. It changes the `DueOn` calculation and its audit. Effort M.
- **Baseline by flag, not by name.** `StageGates.CapturesBaseline`, set from the template. It edits `trg_Gate_CertifyCapturesBaseline` verbatim in `schema.sql`. This matters for release types whose gate set has no "Freeze" gate. Effort S–M.

Who may configure, and how changes are governed:
- Today the RTE or Release Manager writes a template and the Release Manager or Governance Officer approves it.
- Options:
  - keep that;
  - require approver ≠ author, and a Governance Officer whenever a Compliance gate changes;
  - require two approvers (Release Manager and Governance Officer).
- Approved templates stay immutable. A new version is a new row linked to the old one, with a diff on screen. Trains keep the version they were created from (`ReleaseTrains.TemplateId`).

### 2.4 Recommendation

1. Build **A** now: template tasks and milestones (offsets from the target), versions, and approver ≠ author.
2. Add **B** for Compliance gates only, if Governance wants the template enforced on every train.
3. Fold **C** into REOS-113's policy model later rather than build a second policy system.
4. Take the second anchor date only if topic 3 chooses two dates per release.
5. Take the baseline flag if any release type has no "Freeze" gate.

### 2.5 Owner questions

1. May a planner remove or downgrade a gate that came from an approved template? (a) Yes, as today (audited). (b) Not a Compliance gate. (c) Not any template gate. **Recommended: (b).**
2. Should template milestones move with the target? (a) Yes, an offset from the target. (b) No, a fixed date set at creation. **Recommended: (a).**
3. Who approves a template? (a) A Release Manager or Governance Officer, as today. (b) Someone other than the author, and a Governance Officer when a Compliance gate changes. (c) Two approvers. **Recommended: (b).**
4. Should a new template version reach existing trains? (a) Never. (b) Offered to trains still in Planning. **Recommended: (a).**
5. Should the baseline be captured by a gate flag instead of the word "Freeze"? (a) Keep the name rule. (b) A flag set in the template. **Recommended: (b)** if any release type has no Freeze gate.

---

## 3. Release types: CERT, Production, Standard Production, On-Demand Production, and combining

### 3.1 Today

- **No release type or track.** Every release is a train with a risk tier. Templates have no type.
- **The one date is called the CERT date.** `TargetReleaseDate` is "the CERT date", and gate offsets count back from it.
- **On-time % may measure the wrong date.** M1 compares `ActualEndAt` with the baseline's `PlannedReleaseDate` (the target when the baseline was captured). `ActualEndAt` is set when the train is completed, after the production run and hypercare. If the target is a CERT date that comes before production, M1 measures production completion against the CERT date.
- **One window per train (D25).** A train cannot hold both a CERT window and a production window.
- **Environments.** REOS-109 adds environments of kind staging or production; there is no CERT kind. D42 deployments are service × environment × version, linked to a train and a run.
- **Freeze windows.** They are Freeze or Chill, with a product name pattern. They have no environment or type scope. The lockout stops Live-run Deploy steps only (`trg_Step_FreezeLockout`).
- **Calendar.** It shows trains and freezes, with no type. REOS-96's design shows "Production concurrency 3 of 4 slots", and there is no slot model (mapping section 2).
- **Baseline.** An on-demand template without a "Freeze" gate gets no automatic baseline. Such a train must use "capture baseline" before it can reach Executing.

### 3.2 What the terms could mean

| Term | Reading 1 | Reading 2 | Reading 3 |
|---|---|---|---|
| **CERT** | The certification date already stored as the target: the date a release is certified ready | A certification **environment** (pre-production, UAT or client certification) that a release is deployed to before production | A **release type** whose destination is CERT only, for example a build given to clients to certify, never deployed to production on its own |
| **Production** | A release deployed to production | The production environment (REOS-109 kind) | — |
| **Standard Production** | A production release in the regular **scheduled** cadence (release-train windows) | ITIL **standard change**: pre-approved, low risk, repeatable | — |
| **On-Demand Production** | An **off-cycle** production release outside the calendar (client request, hotfix, expedite) | ITIL normal or emergency change | A release made per client, on that client's request |
| **Combine Standard and On-Demand** | **One view**: a calendar, report or filter that shows both together | **One release carries both**: on-demand items ride in a standard release's window | **Merging** an on-demand release into a standard one after both exist |

### 3.3 Gap

There is no field for a type, no rules per type, and no type dimension in the calendar, analytics or exports. There is no scheduled cadence (slots). A release has one date and one window, so CERT and production cannot both be planned on it.

### 3.4 Options

| Option | What it is | Data model | Screens, analytics, freezes, calendar | Effort |
|---|---|---|---|---|
| **A. Release type as a configurable classification** | Each workspace keeps a list of release types, seeded with CERT, Standard Production and On-Demand Production. A type has a track (Cert or Production), a cadence (Scheduled or OnDemand) and a default template (topic 2). A release and a template each have one type | `ReleaseTypes(Id, WorkspaceId, Key, Name, Track CHECK IN ('Cert','Production'), Cadence CHECK IN ('Scheduled','OnDemand'), DefaultTemplateId, IsActive, Version)`; `ReleaseTrains.ReleaseTypeId` (`ALTER TABLE ADD COLUMN`; existing trains get the type the owner names); `TrainTemplates.ReleaseTypeId`; a section 10b boundary trigger (same workspace) | New train, release header, Releases filter, Calendar legend, Templates, Overview cards and every train-based metric gain a type; CSV import and export gain a column | M |
| **B. CERT and Production as stages of one release** | A plus CERT and Production as environments with their own dates and windows on the same release; gates anchored to either date (topic 2); D42 deployments per environment fill in | Windows keyed by (train, environment), which **reverses D25**; a second date such as `ProductionDate`; an environment kind for CERT in REOS-109; M1 must name which date counts | Release detail shows two windows; Calendar shows both; Orchestration stages per environment (REOS-98's design already draws stages per environment) | L |
| **C. Linked releases per track** | A plus links between releases: a CERT release "promotes to" a Production release; an On-Demand release can be "included in" a Standard Production release. Readiness shows the linked release's state, as an advisory or as a guard | `TrainLinks(FromTrainId, ToTrainId, Kind CHECK IN ('PromotesTo','Includes'), CreatedByUserId, CreatedAt)`; a trigger if the link becomes a guard. **Reverses** "cross-train dependencies out of v1" | Release detail "Dependencies" tab (REOS-97 has the label); Calendar draws the chain | L |

Ways to "combine", usable with any of A to C:

| Combine | Meaning | Data | Effort |
|---|---|---|---|
| C1 | One view: "Production (all)" selects both Standard and On-Demand in Releases, Calendar, Overview and Analytics | none beyond A | S |
| C2 | Ride along: an on-demand item joins a standard release as a product flagged on-demand, sharing its window, gates and change record. M6 scope churn counts it as added after the baseline | `BundledProducts.Cadence` (no triggers on that table), or C's `Includes` link | M |
| C3 | Merge two releases into one after both exist | breaks baselines and audit lineage | L, not recommended |

Related choices:
- **Freeze scope by cadence.** `FreezeWindows.AppliesToCadence` (All, Scheduled, OnDemand), so a freeze can stop only scheduled releases, or only on-demand ones. It edits `trg_Step_FreezeLockout`. Effort M.
- **Slots for the scheduled cadence.** This is REOS-96's "3 of 4 slots". `ReleaseSlots(Id, WorkspaceId, ReleaseTypeId, StartsAt, EndsAt, Capacity, Version)`; the Calendar shows used and free slots, and a scheduled release picks one. Effort M.
- **Analytics.** On-time % (M1, M2) is natural for scheduled releases. Lead time from request to production (M15 by type) suits on-demand ones better.
- **ITSM.** If reading 2 (ITIL types) is meant, the type maps to ServiceNow's change type, with a mismatch rule like those in PROJECT_SCOPE 5.3.7.

### 3.5 Recommendation

1. Build **A** with **C1** now. It is useful under every reading: a type on each release and template, a type dimension everywhere, and a combined Production view.
2. Do not choose B or C until the owner says what CERT is:
   - if CERT is a stage every production release passes through, choose B, after REOS-109 and with D25 reversed;
   - if CERT is a release of its own that feeds production, choose C.
3. Add C2 only if the owner means one release that carries both.
4. Settle the type names first, so "Standard" does not collide with ITIL's standard change.

### 3.6 Owner questions

1. What is CERT? (a) The date already stored as the target. (b) An environment every production release passes through first. (c) A release type of its own, delivered to CERT only. **Recommended until answered: build A, which works for (a), (b) and (c), and change nothing else.**
2. Which date does on-time % measure? (a) The CERT date (today's target). (b) The production date. (c) Both, as two dates on the release. **Recommended: (b)**: M1 compares completion, which happens after production.
3. What separates Standard from On-Demand Production? (a) Scheduled calendar windows versus off-cycle releases. (b) ITIL change types. **Recommended: (a)**, and rename the type to avoid "standard change".
4. What does "combine" mean? (a) One view of both. (b) One release that carries both. (c) Both. **Recommended: (a) now**; (b) as C2 if needed.
5. Do freeze windows apply to on-demand releases? (a) Yes, as today, with overrides. (b) The freeze names which cadences it stops. **Recommended: (a)** until an on-demand release is refused in practice.
6. Should scheduled production use fixed slots with a capacity (REOS-96's "3 of 4 slots")? (a) No: the Calendar shows windows. (b) Yes: a slot table. **Recommended: (a)** for now.

---

## 4. Beta 1, Beta 2, Beta 3 and PenTest releases

### 4.1 Today

- **No beta or pen-test concept.** Teams can express them with what exists:
  - train titles ("R26.12 Beta 2");
  - milestones ("Beta 1 drop");
  - a gate with tasks (test data already has a "Pen test" task, `tests/ReleaseMgmt.Api.Tests/PlanningWorkspaceTests.cs`);
  - evidence attachments (SHA-256, locked when the gate certifies);
  - `Blockers` (severity, optionally linked to a gate or product);
  - `KnownIssues` (hypercare).
- **Open blockers block nothing.** A gate certifies when its tasks are done (`trg_Gate_CertifyRequiresTasks`); no gate, guard or train move reads `Blockers`.
- **Any role can download evidence.** `GET /attachments/{id}` uses `Policies.Read`, so every Viewer in the workspace can read it. A pen-test report describes open vulnerabilities.
- **Releases are not linked** (cross-train dependencies are out of v1). There is one window per train (D25).

### 4.2 What the terms could mean

| Term | Reading 1 | Reading 2 | Reading 3 |
|---|---|---|---|
| **Beta 1, 2, 3** | Pre-release drops, each **deployed** to a beta or client environment with its own window, runbook and communications | **Checkpoint dates** in the production release's plan (code drops to test) | A release type with its own gates and sign-offs |
| **PenTest release** | A build **deployed** to a security test environment so a penetration test can run against it | A **sign-off** on the production release: the report is in, and the findings are fixed or accepted | — |

### 4.3 Gap

- There is no first-class phase, link or finding.
- A pen-test sign-off cannot be made to depend on the findings being closed.
- Pen-test evidence is readable by every role.
- Betas cannot have windows or runbooks of their own inside one release.

### 4.4 Options

| Option | What it is | Data model | Screens and analytics | Effort |
|---|---|---|---|---|
| **A. Phases inside one release** | Beta 1, 2, 3 are template milestones with offsets (topic 2 A). PenTest is a Compliance gate, "Penetration test sign-off", required before Executing and owned by a security team. Findings are Blockers linked to that gate; the report is gate evidence; separation of duties works as today. Optional rule: open Critical or High blockers linked to a gate stop it certifying | none beyond topic 2 A; the optional rule is a service check plus one trigger | Release detail shows the beta milestones and the pen-test gate. Analytics: milestone dates, findings through M9 | S (M with the rule) |
| **B. Separate linked releases** | Release types Beta and PenTest (topic 3 A) and links (topic 3 C, kind "Precedes"). Each beta has its own gates, window, runbook and communications. The production release's readiness lists its betas and its pen-test release, as an advisory or as a guard ("PenTest release Complete with no open Critical/High findings") | types, `TrainLinks`, guard trigger | Release detail "Dependencies" tab; Calendar shows the chain. Analytics: betas per release, beta-to-production lead time | M–L |
| **C. Phases as first-class rows** | `ReleasePhases(Id, ReleaseTrainId, Kind CHECK IN ('Beta','PenTest','Cert','Production'), Ordinal, StartsOn, EndsOn, EnvironmentId, Version)`, with gates and windows per phase | reverses D25; gates, readiness and the baseline change | Phase strip in release detail | L |

Ways to record pen-test findings:

| Choice | What it is | Data | Effort |
|---|---|---|---|
| F1 | Reuse Blockers, marked as pen-test findings | `Blockers.Source CHECK IN ('General','PenTest')` | S |
| F2 | A findings table with risk acceptance by a second person, as waivers work (D10) | `SecurityFindings(Id, ReleaseTrainId, StageGateId, ExternalKey, Severity, Status CHECK IN ('Open','Remediated','RiskAccepted'), RiskAcceptedByUserId, RiskAcceptedReason (≥ 20 chars), RaisedAt, ResolvedAt, Version)` | M |

New analytics:
- findings by severity, and time to remediate;
- pen-test first pass (no Critical or High finding at the first test);
- releases shipped with risk-accepted findings (feeds M18);
- betas per release, and beta-to-production lead time (option B only).

How each option chains to production:
- In A the phases are part of the production release.
- In B the links carry the order.
- In C the phase order does.

### 4.5 Recommendation

1. Build **A** now:
   - Beta 1 to 3 as template milestones and a Compliance "Penetration test sign-off" gate in the template for the release types that need them;
   - findings as Blockers marked PenTest (F1);
   - the rule that open Critical or High findings stop that gate from certifying, if Governance agrees.
2. Restrict who can read pen-test evidence (question 4).
3. Revisit B once topic 3 is answered, if betas turn out to have deployment windows of their own.

### 4.6 Owner questions

1. Is each beta a deployment of its own (own window, runbook, communications)? (a) No, a dated checkpoint in the production release. (b) Yes, a separate release linked to production. **Recommended default: (a)** until a beta needs its own window.
2. Is PenTest (a) a sign-off gate on the production release, or (b) a separate release to a test environment? **Recommended: (a).**
3. What happens to open Critical or High findings? (a) Advisory only. (b) The pen-test gate cannot certify until each is fixed or risk-accepted by a Governance Officer. (c) They block Executing directly. **Recommended: (b).**
4. Who may read pen-test evidence? (a) Everyone who can read the release, as today. (b) Governance Officers, Release Managers and the owning security team. **Recommended: (b).** This is a new read rule for attachments.
5. Is the order fixed (Beta 1 → Beta 2 → Beta 3 → PenTest → CERT → Production), or set per template? **Recommended: per template.**

---

## 5. Proposed sequence, fitted into the redesign build order

`docs/proposals/redesign-mapping.md` section 6 has nine steps. The proposed stories slot in as below; "E", "T", "R" and "P" mark the topic (executive analytics, templates, release types, phases).

| Build-order step | Existing work | Add |
|---|---|---|
| 3. Workspaces | REOS-104, REOS-105 | **E0** (into REOS-105's scope): the workspace predicate in every `db/analytics.sql` query, with a test that a second workspace's rows never count |
| 5. Release model, overview, approvals, first screens | REOS-107, REOS-108, REOS-95 to REOS-97, REOS-101 | **R1** release types (topic 3 A) in the same upgrade script as REOS-107, since both add columns to `ReleaseTrains`; **R2** combined Production view (C1); **T1** versioned templates with tasks and milestones, approver ≠ author (topic 2 A); **P1** beta milestones and the pen-test gate as template content, findings as Blockers marked PenTest (topic 4 A, F1); **E1** analytics group-by (workspace, team, type, quarter, risk tier) and the filters PROJECT_SCOPE already lists; **E2** metrics M16 to M19, M21, M24; **E3** analytics workbook |
| 5 (after the owner's answers) | | **E4** slip reasons (`TargetChanges`, M20); **E5** portfolio scope across a person's workspaces (option B); **E6** portfolio report PDF; **T2** mandatory Compliance gates (topic 2 B); **P2** pen-test findings block the gate; restricted read of pen-test evidence |
| 6. Deployments | REOS-109 to REOS-112, REOS-98, REOS-100 | **R3** only if CERT is a stage: a CERT environment kind and per-environment windows (topic 3 B, reverses D25); **E7** M23 deployment success and availability |
| 7. Approvals and policies | REOS-113, REOS-99 | **T3** gate policy per release type and risk tier, inside REOS-113's policy model (topic 2 C), only if wanted |
| After 7 | | **R4** linked releases (topic 3 C) and **P3** separate beta and pen-test releases (topic 4 B), only if the owner reverses "cross-train dependencies out of v1" |
| 9. QA | REOS-102 | the executive views and release-type filters join the QA pass |

E1 to E3, R1, R2, T1 and P1 need no owner decision beyond the type names. Everything else waits for the decisions in section 6.

## 6. Owner decisions needed

| # | Question | Options | Recommended |
|---|---|---|---|
| 1 | What is CERT? | (a) the date already stored as the target; (b) an environment every production release passes through; (c) a release type of its own | build the type list (topic 3 A) now; choose B or C after the answer |
| 2 | Which date does on-time % measure? | (a) CERT; (b) production; (c) both | (b) |
| 3 | What separates Standard from On-Demand Production? | (a) scheduled windows vs off-cycle; (b) ITIL change types | (a), with names that avoid "standard change" |
| 4 | What does "combine" mean? | (a) one view; (b) one release carries both; (c) both | (a) now |
| 5 | May anyone see analytics across workspaces? | (a) no; (b) across their own memberships; (c) a host-wide executive role | (b) |
| 6 | What separates a VP from a Director? | (a) membership only; (b) portfolio groups of workspaces; (c) new roles | (a) |
| 7 | Must a target move after the baseline give a reason? | (a) no; (b) a required pick list and a note; (c) free text | (b) |
| 8 | May a planner remove or downgrade a template's gate? | (a) yes, as today; (b) not a Compliance gate; (c) not any template gate | (b) |
| 9 | Do template milestones move with the target? | (a) yes, an offset; (b) no, a fixed date | (a) |
| 10 | Who approves a template? | (a) RM or GO, as today; (b) not the author, and a GO when a Compliance gate changes; (c) two approvers | (b) |
| 11 | Is a beta a deployment of its own? | (a) no, a checkpoint date; (b) yes, a linked release | (a) |
| 12 | Is PenTest a sign-off gate or a separate release? | (a) a gate; (b) a release | (a) |
| 13 | Do open Critical/High findings block? | (a) advisory; (b) block the pen-test gate unless risk-accepted by a GO; (c) block Executing | (b) |
| 14 | Who may read pen-test evidence? | (a) every role, as today; (b) GO, RM and the security team | (b) |
| 15 | Is a quarter calendar or fiscal? | (a) calendar; (b) fiscal, starting in a named month | owner's fact |

## 7. Decided 2026-10-23 (John), and the model they lead to

| # | Question | Answer | Recorded |
|---|---|---|---|
| 1 | What is CERT? | Build the release-type list now; define CERT further later | D46 |
| 3, 4 | Standard vs On-Demand, and "combine" | **Clients**, not schedules or change types: Standard clients and On-Demand clients. On-Demand clients have a CERT environment, Standard clients do not. On-Demand clients are scheduled by us but may choose another date than the one we give them; their production release is usually about 7 days after Standard's | D47 |
| 11, 12 | Betas and PenTest | Beta 1, 2 and 3 (for some products) are **separate releases** before the CERT release. PenTest Cert is a **separate cyber-review release**, apart from the CERT release | D48 |
| 5, 6 | Who sees executive analytics | Across the workspaces the person belongs to; no new role | D49 |

The answers overrule this document's recommendations on topics 3 and 4. Betas and PenTest Cert are releases, not milestones or gates. The production date varies by client segment and by client.

### 7.1 The model

| Part | What it is | Notes |
|---|---|---|
| **Release type** | A per-workspace list. Seeded with Beta, CERT, PenTest Cert and Production. Each type has a default template, so its gates and milestones are configured per type (topic 2) | A Beta release carries its number (1 to 3) |
| **Version** | Groups the releases of one product version in order: Beta 1, Beta 2, Beta 3 (when the product has betas), CERT, PenTest Cert, Production | Shown as a chain on the release, the calendar and the overview. A slip in one release shows against the next |
| **Client** | A per-workspace record: name and segment (Standard or On-Demand). On-Demand clients have a CERT environment | The clients a release reaches |
| **Client schedule** | On a CERT release: each On-Demand client's CERT date, offered and chosen. On a Production release: the Standard date for all Standard clients, and each On-Demand client's date. By default an On-Demand date is the Standard date plus an offset (`Release:OnDemandOffsetDays`, provisional 7). The client may choose another date. Who recorded it, and when, is audited | "Combined" is one view of both segments. When an On-Demand client takes the Standard date, its wave is the Standard one |
| **Analytics** | On-time % per type (CERT and production separately), per segment and per client, across the person's workspaces (D49). VP: portfolio and quarter rollups. Director: team and version operations | Quarter calendar or fiscal: still open (question 15) |

### 7.2 Questions the answers raise, with provisional defaults

| # | Question | Provisional default |
|---|---|---|
| 21 | Is "about 7 days" calendar days or business days, and is it fixed per workspace? | 7 calendar days, per workspace (`Release:OnDemandOffsetDays`) |
| 22 | May On-Demand clients choose their CERT date as well as their production date? | Yes, both |
| 23 | Limits on a chosen date: not before the Standard date? not inside a freeze? a latest date? | Not before the Standard production date; a freeze warns; no latest date |
| 24 | Are clients kept per workspace? | Yes |
| 25 | Do clients receive communications (per segment or per client)? | Later; not in the first story |
| 26 | Does each release in a version (Beta, CERT, PenTest Cert, Production) have its own owner and team? | Yes; each is a release |

### 7.3 Stories (epic REOS-115, "Release types, versions and client schedules")

| Jira | Story | Scope | Depends on |
|---|---|---|---|
| REOS-116 | RT1 Release types and per-type templates | Type list per workspace (Beta n, CERT, PenTest Cert, Production); a type on every release; each type's default template; filters by type | REOS-105, REOS-107 (same upgrade script as the release model) |
| REOS-117 | RT2 Versions | Group a product version's releases in order and show the chain; creating the next release in the chain from the previous one | RT1 |
| REOS-118 | RT3 Clients and segments | Client list per workspace: Standard or On-Demand, CERT environment; a management screen | REOS-105 |
| REOS-119 | RT4 Client schedules | Per-client dates on CERT and Production releases: offered, chosen, audited; the On-Demand offset; a combined Standard + On-Demand view; freeze warnings | RT1, RT3 |
| REOS-120 | RT5 Calendar, portfolio and overview show types, versions and client dates | Builds into REOS-95 and REOS-96 | RT2, RT4 |
| REOS-121 | RT6 Executive analytics | Across the person's workspaces (D49); by type, segment, client and quarter; VP and Director views; the analytics workbook and a portfolio PDF | REOS-105, RT1, RT4 |
| REOS-122 | RT7 Template governance | Templates carry tasks and milestones; versions; the approver is not the author; Compliance gates locked (pending questions 8 and 10) | RT1 |

The other questions in section 6 (2, 7, 8, 9, 10, 13, 14, 15) and those in redesign-coverage.md are still open.
