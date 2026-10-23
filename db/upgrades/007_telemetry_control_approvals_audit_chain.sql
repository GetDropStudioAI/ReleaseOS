-- Upgrade 007 (REOS-111, 112, 113, 114; D42): telemetry, deployment control and run pause, approvals and policies, the hash-chained audit log.
-- From: migrations 20261023111406_Schema + 20261023111416_Triggers (after 006).
-- To:   migrations 20261023142626_Schema + 20261023142643_Triggers.
-- Run with the app stopped and after a backup (docs/RUNBOOK_OPERATIONS.md section 8):  sqlite3 releaseos.db < db/upgrades/007_telemetry_control_approvals_audit_chain.sql
-- One transaction. Text below is verbatim from db/schema.sql:
--  * New tables: MetricSamples, MetricRollups, ThresholdBreaches, DeploymentControlActions, ApprovalPolicies, ApprovalPolicyRequirements, Approvals,
--    ApprovalDecisions, AuditChainHeads.
--  * Columns added in place (ALTER TABLE ADD COLUMN puts them where db/schema.sql has them; each is nullable or has a constant default):
--    Environments.ThresholdForSeconds (120) and CanaryObservationSeconds (600); Deployments.StepStartedAt and AutoRollbackArmedAt;
--    RunbookRuns.PausedAt, PausedByUserId and PauseReason; GateWaivers.DeclinedByUserId, DeclinedAt and DeclineNote; AuditEvents.PrevHash and Hash.
--  * SyncAlerts is rebuilt: its CHECKs gain the sources Deployment, Audit and Telemetry and the kinds ControlFailed, IntegrityFailed and RetentionFailed
--    (SQLite cannot change a CHECK in place). Its rows are copied unchanged.
--  * Existing audit rows are NOT hashed here: SQLite has no SHA-256. They keep a NULL Hash until the app's sealer reaches them at its next start
--    (Audit:Seal*, docs/RUNBOOK_OPERATIONS.md), which seals every workspace's chain once, oldest first, and then the verification job checks it.
--    Until then the integrity endpoint reports them as pending, never as failed.
--  * Every trigger is dropped and all of db/schema.sql's triggers are created again, verbatim and in file order (same-event triggers fire newest-first;
--    trg_Audit_NoUpdate changes so the sealer can write a row's hash once; new triggers: waiver decline, run pause, approvals, workspace boundaries).
-- Foreign keys are off while the columns are added (a column with REFERENCES is only legal that way when it has a non-NULL default); the
-- foreign_key_check at the end proves the result.

PRAGMA foreign_keys=OFF;
BEGIN IMMEDIATE;

-- Refuses to run twice or against the wrong database: the INSERT fails on the CHECK unless exactly 006's migrations are recorded.
CREATE TEMP TABLE _upgrade_guard (ok INTEGER CHECK (ok = 1));
INSERT INTO _upgrade_guard SELECT (SELECT group_concat(MigrationId, ',') FROM (SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId))
    = '20261023111406_Schema,20261023111416_Triggers';
DROP TABLE _upgrade_guard;

DROP TRIGGER trg_Audit_NoUpdate;
DROP TRIGGER trg_Audit_NoDelete;
DROP TRIGGER trg_GateTrans_NoUpdate;
DROP TRIGGER trg_GateTrans_NoDelete;
DROP TRIGGER trg_Baseline_NoUpdate;
DROP TRIGGER trg_Baseline_NoDelete;
DROP TRIGGER trg_GoNoGo_NoUpdate;
DROP TRIGGER trg_GoNoGo_NoDelete;
DROP TRIGGER trg_Dispatch_NoUpdate;
DROP TRIGGER trg_Dispatch_NoDelete;
DROP TRIGGER trg_Attachment_LockedNoDelete;
DROP TRIGGER trg_Attachment_LockedNoUpdate;
DROP TRIGGER trg_Gate_Transition;
DROP TRIGGER trg_Train_Transition;
DROP TRIGGER trg_Train_GateLockout;
DROP TRIGGER trg_Train_ExecutingRequiresGo;
DROP TRIGGER trg_Train_ExecutingNoExpiredConditions;
DROP TRIGGER trg_Train_ExecutingRequiresBaseline;
DROP TRIGGER trg_Train_ExecutingRequiresRollback;
DROP TRIGGER trg_Train_CompleteRequiresPir;
DROP TRIGGER trg_Gate_CertifyCapturesBaseline;
DROP TRIGGER trg_Gate_RequireActor;
DROP TRIGGER trg_Gate_LogTransition;
DROP TRIGGER trg_Gate_CertifyRequiresTasks;
DROP TRIGGER trg_Gate_ComplianceNeedsTask;
DROP TRIGGER trg_Gate_CertifyInOrder;
DROP TRIGGER trg_Gate_ComplianceCertifierRole;
DROP TRIGGER trg_Gate_CertifierNotTaskAuthor;
DROP TRIGGER trg_Gate_WaiveRequiresApproval;
DROP TRIGGER trg_Gate_CertifyLocksEvidence;
DROP TRIGGER trg_Waiver_ApproverRole;
DROP TRIGGER trg_Waiver_ApproverRoleInsert;
DROP TRIGGER trg_Override_NoUpdate;
DROP TRIGGER trg_Override_NoDelete;
DROP TRIGGER trg_Override_ApproverRole;
DROP TRIGGER trg_Task_ReopenDecertifies;
DROP TRIGGER trg_Task_InsertDecertifies;
DROP TRIGGER trg_Run_LiveRequiresExecuting;
DROP TRIGGER trg_Step_DependenciesDone;
DROP TRIGGER trg_Step_FreezeLockout;
DROP TRIGGER trg_Condition_RequiresConditionalGo;
DROP TRIGGER trg_Import_NoCommitWithErrors;
DROP TRIGGER trg_Train_KeyImmutable;
DROP TRIGGER trg_Train_AssignKey;
DROP TRIGGER trg_Deployment_Transition;
DROP TRIGGER trg_DeploymentEvent_NoUpdate;
DROP TRIGGER trg_Ws_Holidays_Update;
DROP TRIGGER trg_Ws_TrainTemplates_Update;
DROP TRIGGER trg_Ws_CommTemplateLibrary_Update;
DROP TRIGGER trg_Ws_FreezeWindows_Update;
DROP TRIGGER trg_Ws_WebhookDestinations_Update;
DROP TRIGGER trg_Ws_Notifications_Update;
DROP TRIGGER trg_Ws_IcsTokens_Update;
DROP TRIGGER trg_Ws_ConnectorState_Update;
DROP TRIGGER trg_Ws_ReleaseTrains_Insert;
DROP TRIGGER trg_Ws_ReleaseTrains_Update;
DROP TRIGGER trg_Ws_Teams_Insert;
DROP TRIGGER trg_Ws_Teams_Update;
DROP TRIGGER trg_Ws_SyncAlerts_Insert;
DROP TRIGGER trg_Ws_SyncAlerts_Update;
DROP TRIGGER trg_Ws_ImportJobs_Insert;
DROP TRIGGER trg_Ws_ImportJobs_Update;
DROP TRIGGER trg_Ws_ExportJobs_Insert;
DROP TRIGGER trg_Ws_ExportJobs_Update;
DROP TRIGGER trg_Ws_AuditEvents_Insert;
DROP TRIGGER trg_Ws_TemplateGates_Insert;
DROP TRIGGER trg_Ws_TemplateGates_Update;
DROP TRIGGER trg_Ws_TemplateSteps_Insert;
DROP TRIGGER trg_Ws_TemplateSteps_Update;
DROP TRIGGER trg_Ws_TemplateCommSchedule_Insert;
DROP TRIGGER trg_Ws_TemplateCommSchedule_Update;
DROP TRIGGER trg_Ws_StageGates_Insert;
DROP TRIGGER trg_Ws_StageGates_Update;
DROP TRIGGER trg_Ws_RunbookSteps_Insert;
DROP TRIGGER trg_Ws_RunbookSteps_Update;
DROP TRIGGER trg_Ws_TrainMilestones_Insert;
DROP TRIGGER trg_Ws_TrainMilestones_Update;
DROP TRIGGER trg_Ws_Blockers_Insert;
DROP TRIGGER trg_Ws_Blockers_Update;
DROP TRIGGER trg_Ws_CommTemplates_Insert;
DROP TRIGGER trg_Ws_CommTemplates_Update;
DROP TRIGGER trg_Ws_CommSchedule_Insert;
DROP TRIGGER trg_Ws_CommSchedule_Update;
DROP TRIGGER trg_Ws_BundledProducts_Update;
DROP TRIGGER trg_Ws_RunbookRuns_Update;
DROP TRIGGER trg_Ws_ChecklistTasks_Insert;
DROP TRIGGER trg_Ws_ChecklistTasks_Update;
DROP TRIGGER trg_Ws_StepExecutions_Insert;
DROP TRIGGER trg_Ws_StepExecutions_Update;
DROP TRIGGER trg_Ws_StepDependencies_Insert;
DROP TRIGGER trg_Ws_StepDependencies_Update;
DROP TRIGGER trg_Ws_CommDispatches_Insert;
DROP TRIGGER trg_Ws_FreezeOverrides_Insert;
DROP TRIGGER trg_Ws_MetricSnapshots_Insert;
DROP TRIGGER trg_Ws_MetricSnapshots_Update;
DROP TRIGGER trg_Ws_ReleaseTypes_Insert;
DROP TRIGGER trg_Ws_ReleaseTypes_Update;
DROP TRIGGER trg_Ws_Environments_Insert;
DROP TRIGGER trg_Ws_Environments_Update;
DROP TRIGGER trg_Ws_CatalogServices_Insert;
DROP TRIGGER trg_Ws_CatalogServices_Update;
DROP TRIGGER trg_Ws_IngestKeys_Update;
DROP TRIGGER trg_Ws_TrainEnvironments_Insert;
DROP TRIGGER trg_Ws_TrainEnvironments_Update;
DROP TRIGGER trg_Ws_BundledProducts_Insert;
DROP TRIGGER trg_Ws_Deployments_Insert;
DROP TRIGGER trg_Ws_Deployments_Update;

-- New tables
-- =====================================================================
-- 9d. Telemetry, deployment control, approvals and the audit chain (REOS-111, 112, 113, 114; D42)
-- =====================================================================
CREATE TABLE MetricSamples (                              -- REOS-111: what the deployment tool pushed; kept for Telemetry:RetentionDays, then summarised into MetricRollups and dropped
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    DeploymentId TEXT NOT NULL REFERENCES Deployments(Id) ON DELETE CASCADE,
    Series TEXT NOT NULL CHECK (Series IN ('Canary','Stable')),
    SampledAt TEXT NOT NULL,                              -- the tool's clock
    ReceivedAt TEXT NOT NULL,                             -- ours
    ErrorRatePct REAL CHECK (ErrorRatePct IS NULL OR ErrorRatePct BETWEEN 0 AND 100),
    LatencyP95Ms REAL CHECK (LatencyP95Ms IS NULL OR LatencyP95Ms >= 0),
    AvailabilityPct REAL CHECK (AvailabilityPct IS NULL OR AvailabilityPct BETWEEN 0 AND 100),
    RequestCount INTEGER CHECK (RequestCount IS NULL OR RequestCount >= 0),
    WorkspaceId TEXT NOT NULL DEFAULT 'default' REFERENCES Workspaces(Id),
    UNIQUE (DeploymentId, Series, SampledAt),             -- a replayed sample is the same sample
    CHECK (ErrorRatePct IS NOT NULL OR LatencyP95Ms IS NOT NULL OR AvailabilityPct IS NOT NULL OR RequestCount IS NOT NULL)
);

CREATE TABLE MetricRollups (                              -- REOS-111: one row per deployment, series and hour for samples past retention: enough for SLO and error-budget maths
    DeploymentId TEXT NOT NULL REFERENCES Deployments(Id) ON DELETE CASCADE,
    Series TEXT NOT NULL CHECK (Series IN ('Canary','Stable')),
    BucketAt TEXT NOT NULL,                               -- the start of the UTC hour
    Samples INTEGER NOT NULL CHECK (Samples >= 1),
    ErrorRateAvgPct REAL,
    ErrorRateMaxPct REAL,
    LatencyP95AvgMs REAL,
    LatencyP95MaxMs REAL,
    AvailabilityAvgPct REAL,
    AvailabilityMinPct REAL,
    RequestCount INTEGER,
    WorkspaceId TEXT NOT NULL DEFAULT 'default' REFERENCES Workspaces(Id),
    PRIMARY KEY (DeploymentId, Series, BucketAt)
);

CREATE TABLE ThresholdBreaches (                          -- REOS-111: a metric beyond its environment's threshold. Watching until it has lasted ThresholdForSeconds, then confirmed; resolved by the first sample back inside
    Id TEXT PRIMARY KEY,
    DeploymentId TEXT NOT NULL REFERENCES Deployments(Id) ON DELETE CASCADE,
    Series TEXT NOT NULL CHECK (Series IN ('Canary','Stable')),
    Metric TEXT NOT NULL CHECK (Metric IN ('ErrorRate','LatencyP95','Availability')),
    ThresholdValue REAL NOT NULL,                         -- the threshold when the breach began
    ForSeconds INTEGER NOT NULL CHECK (ForSeconds >= 0),   -- the duration it had to last
    StartedAt TEXT NOT NULL,                              -- the sample time of the first sample beyond the threshold
    ConfirmedAt TEXT,                                     -- the sample time of the sample that completed the duration; NULL while Watching
    DetectedAt TEXT,                                      -- our clock when it was confirmed
    ResolvedAt TEXT,
    PeakValue REAL NOT NULL,
    LastValue REAL NOT NULL,
    SampleCount INTEGER NOT NULL DEFAULT 1 CHECK (SampleCount >= 1),
    Version INTEGER NOT NULL DEFAULT 1,
    WorkspaceId TEXT NOT NULL DEFAULT 'default' REFERENCES Workspaces(Id),
    CHECK ((ConfirmedAt IS NULL) = (DetectedAt IS NULL)),
    CHECK (ConfirmedAt IS NULL OR ConfirmedAt >= StartedAt),
    CHECK (ResolvedAt IS NULL OR ResolvedAt >= StartedAt)
);

CREATE TABLE DeploymentControlActions (                   -- REOS-112: pause, resume, promote and rollback calls to the environment's webhook, and what came of them. Id is the delivery id the receiver sees
    Id TEXT PRIMARY KEY,
    DeploymentId TEXT NOT NULL REFERENCES Deployments(Id) ON DELETE CASCADE,
    Action TEXT NOT NULL CHECK (Action IN ('Pause','Resume','Promote','Rollback')),
    Source TEXT NOT NULL CHECK (Source IN ('Manual','Automatic')),
    RequestedByUserId TEXT REFERENCES Users(Id),          -- NULL = the system (automatic rollback)
    RequestedAt TEXT NOT NULL,
    Reason TEXT CHECK (Reason IS NULL OR length(trim(Reason)) BETWEEN 1 AND 500),
    BreachId TEXT REFERENCES ThresholdBreaches(Id),       -- the breach an automatic rollback answers: one rollback per breach (UX_ControlActions_OneRollbackPerBreach)
    Status TEXT NOT NULL DEFAULT 'Pending' CHECK (Status IN ('Pending','Succeeded','Failed')),
    HttpStatus INTEGER CHECK (HttpStatus IS NULL OR HttpStatus BETWEEN 100 AND 599),
    ErrorMessage TEXT CHECK (ErrorMessage IS NULL OR length(ErrorMessage) <= 500),   -- never the URL: it is a secret
    CompletedAt TEXT,
    Version INTEGER NOT NULL DEFAULT 1,
    WorkspaceId TEXT NOT NULL DEFAULT 'default' REFERENCES Workspaces(Id),
    CHECK ((Source = 'Manual') = (RequestedByUserId IS NOT NULL)),
    CHECK ((Status = 'Pending') = (CompletedAt IS NULL)),
    CHECK (Status <> 'Failed' OR ErrorMessage IS NOT NULL),
    CHECK (BreachId IS NULL OR (Source = 'Automatic' AND Action = 'Rollback'))
);

CREATE TABLE ApprovalPolicies (                           -- REOS-113: which roles must approve a release going to an environment, by environment kind and risk tier
    Id TEXT PRIMARY KEY,
    Name TEXT NOT NULL COLLATE NOCASE CHECK (length(trim(Name)) BETWEEN 1 AND 100),
    EnvironmentKind TEXT CHECK (EnvironmentKind IS NULL OR EnvironmentKind IN ('Staging','Production')),   -- NULL = any environment
    MinRiskTier TEXT CHECK (MinRiskTier IS NULL OR MinRiskTier IN ('Low','Moderate','High','VeryHigh')),   -- NULL = any risk tier; else this tier and above
    RequireGatesCertified INTEGER NOT NULL DEFAULT 0 CHECK (RequireGatesCertified IN (0,1)),   -- policy check: every gate of the release is Certified or Waived
    RequireOpenWindow INTEGER NOT NULL DEFAULT 0 CHECK (RequireOpenWindow IN (0,1)),   -- policy check: the release has a deployment window that has not ended
    ArchivedAt TEXT,
    LastChangedByUserId TEXT REFERENCES Users(Id),
    LastChangedAt TEXT,
    CreatedAt TEXT NOT NULL,
    Version INTEGER NOT NULL DEFAULT 1,
    WorkspaceId TEXT NOT NULL DEFAULT 'default' REFERENCES Workspaces(Id),
    UNIQUE (WorkspaceId, Name)
);

CREATE TABLE ApprovalPolicyRequirements (                 -- the seats of a policy; replaced as a whole with the policy (its Version)
    Id TEXT PRIMARY KEY,
    PolicyId TEXT NOT NULL REFERENCES ApprovalPolicies(Id) ON DELETE CASCADE,
    Label TEXT NOT NULL COLLATE NOCASE CHECK (length(trim(Label)) BETWEEN 1 AND 100),   -- "Security reviewer", "SRE approver"
    EligibleKind TEXT NOT NULL CHECK (EligibleKind IN ('WorkspaceRole','ReleaseOwner','Team')),
    EligibleValue TEXT,                                   -- the workspace role, or the team's id; NULL for the release owner
    DueHours INTEGER NOT NULL DEFAULT 24 CHECK (DueHours BETWEEN 1 AND 720),
    SortOrder INTEGER NOT NULL DEFAULT 0,
    UNIQUE (PolicyId, Label),
    CHECK ((EligibleKind = 'ReleaseOwner') = (EligibleValue IS NULL)),
    CHECK (EligibleKind <> 'WorkspaceRole' OR EligibleValue IN ('Viewer','ReleaseManager','RTE','GovernanceOfficer'))
);

CREATE TABLE Approvals (                                  -- REOS-113: one request to take a release to an environment, created from the matching policies
    Id TEXT PRIMARY KEY,
    ReleaseTrainId TEXT NOT NULL REFERENCES ReleaseTrains(Id) ON DELETE CASCADE,
    EnvironmentId TEXT NOT NULL REFERENCES Environments(Id),
    Status TEXT NOT NULL DEFAULT 'Pending' CHECK (Status IN ('Pending','Approved','ChangesRequested','Cancelled')),
    Round INTEGER NOT NULL DEFAULT 1 CHECK (Round >= 1),   -- raised by each resubmit after Request changes
    RequestedByUserId TEXT NOT NULL REFERENCES Users(Id),
    RequestedAt TEXT NOT NULL,
    DueAt TEXT NOT NULL,                                  -- the earliest due time of its decisions
    DecidedAt TEXT,                                       -- when the last seat approved
    LastChangedByUserId TEXT REFERENCES Users(Id),
    LastChangedAt TEXT,
    Version INTEGER NOT NULL DEFAULT 1,
    WorkspaceId TEXT NOT NULL DEFAULT 'default' REFERENCES Workspaces(Id),
    CHECK ((Status = 'Approved') = (DecidedAt IS NOT NULL))
);

CREATE TABLE ApprovalDecisions (                          -- one per seat of an approval; the seat is copied from the policy, so a later policy edit does not change an approval under way
    Id TEXT PRIMARY KEY,
    ApprovalId TEXT NOT NULL REFERENCES Approvals(Id) ON DELETE CASCADE,
    Label TEXT NOT NULL COLLATE NOCASE,
    EligibleKind TEXT NOT NULL CHECK (EligibleKind IN ('WorkspaceRole','ReleaseOwner','Team')),
    EligibleValue TEXT,
    DueAt TEXT NOT NULL,
    Decision TEXT CHECK (Decision IS NULL OR Decision IN ('Approved','ChangesRequested')),
    DecidedByUserId TEXT REFERENCES Users(Id),
    DecidedAt TEXT,
    Note TEXT CHECK (Note IS NULL OR length(Note) <= 2000),
    Version INTEGER NOT NULL DEFAULT 1,
    UNIQUE (ApprovalId, Label),
    CHECK ((Decision IS NULL) = (DecidedByUserId IS NULL) AND (Decision IS NULL) = (DecidedAt IS NULL)),
    CHECK (Decision IS NULL OR Decision <> 'ChangesRequested' OR (Note IS NOT NULL AND length(trim(Note)) >= 1))   -- Request changes needs a note
);

CREATE TABLE AuditChainHeads (                            -- REOS-114: where each audit chain has been sealed to, and what the last check found; system-owned, last write wins
    ChainId TEXT PRIMARY KEY,                             -- the workspace's id, or '*' for the host-wide rows (AuditEvents.WorkspaceId NULL)
    LastId INTEGER NOT NULL,                              -- the last sealed AuditEvents row
    LastHash TEXT NOT NULL CHECK (length(LastHash) = 64),
    SealedCount INTEGER NOT NULL CHECK (SealedCount >= 0),
    SealedAt TEXT NOT NULL,
    VerifiedAt TEXT,
    VerifiedRows INTEGER,
    VerifiedStatus TEXT CHECK (VerifiedStatus IS NULL OR VerifiedStatus IN ('Verified','Failed')),
    FirstBadId INTEGER,
    FirstBadReason TEXT,
    CHECK ((VerifiedStatus IS NULL) = (VerifiedAt IS NULL))
);


-- New columns
ALTER TABLE Environments ADD COLUMN ThresholdForSeconds INTEGER NOT NULL DEFAULT 120 CHECK (ThresholdForSeconds BETWEEN 0 AND 86400);
ALTER TABLE Environments ADD COLUMN CanaryObservationSeconds INTEGER NOT NULL DEFAULT 600 CHECK (CanaryObservationSeconds BETWEEN 30 AND 86400);
ALTER TABLE Deployments ADD COLUMN StepStartedAt TEXT;
ALTER TABLE Deployments ADD COLUMN AutoRollbackArmedAt TEXT;
ALTER TABLE RunbookRuns ADD COLUMN PausedAt TEXT CHECK (PausedAt IS NULL OR EndedAt IS NULL);
ALTER TABLE RunbookRuns ADD COLUMN PausedByUserId TEXT REFERENCES Users(Id) CHECK ((PausedAt IS NULL) = (PausedByUserId IS NULL));
ALTER TABLE RunbookRuns ADD COLUMN PauseReason TEXT CHECK ((PausedAt IS NULL) = (PauseReason IS NULL) AND (PauseReason IS NULL OR length(trim(PauseReason)) BETWEEN 1 AND 500));
ALTER TABLE GateWaivers ADD COLUMN DeclinedByUserId TEXT REFERENCES Users(Id) CHECK (DeclinedByUserId IS NULL OR ApprovedByUserId IS NULL);
ALTER TABLE GateWaivers ADD COLUMN DeclinedAt TEXT CHECK ((DeclinedAt IS NULL) = (DeclinedByUserId IS NULL));
ALTER TABLE GateWaivers ADD COLUMN DeclineNote TEXT CHECK ((DeclinedAt IS NULL) = (DeclineNote IS NULL) AND (DeclineNote IS NULL OR length(trim(DeclineNote)) >= 1));
ALTER TABLE AuditEvents ADD COLUMN PrevHash TEXT CHECK (PrevHash IS NULL OR (length(PrevHash) = 64 AND PrevHash NOT GLOB '*[^0-9a-f]*'));
ALTER TABLE AuditEvents ADD COLUMN Hash TEXT CHECK ((Hash IS NULL) = (PrevHash IS NULL) AND (Hash IS NULL OR (length(Hash) = 64 AND Hash NOT GLOB '*[^0-9a-f]*')));

-- SyncAlerts: wider CHECKs
CREATE TABLE SyncAlerts_upgrade (
    Id TEXT PRIMARY KEY,
    ReleaseTrainId TEXT REFERENCES ReleaseTrains(Id) ON DELETE CASCADE,  -- NULL = system-wide. Every background failure lands here
    SourceSystem TEXT NOT NULL CHECK (SourceSystem IN ('Jira','ServiceNow','SyncEngine','Backup','Export','Webhook','Notifications','Deployment','Audit','Telemetry')),
    Kind TEXT NOT NULL CHECK (Kind IN ('AuthFailed','Unreachable','NotFound','Mismatch','RateLimited','ParseError','Stalled','BackupFailed','ExportFailed','DeliveryFailed','ControlFailed','IntegrityFailed','RetentionFailed')),
    Fingerprint TEXT NOT NULL,                            -- hash(system, kind, key); dedups repeats
    ErrorMessage TEXT NOT NULL,
    OccurrenceCount INTEGER NOT NULL DEFAULT 1,
    FirstOccurredAt TEXT NOT NULL,
    LastOccurredAt TEXT NOT NULL,
    IsResolved INTEGER NOT NULL DEFAULT 0 CHECK (IsResolved IN (0,1)),
    ResolvedAt TEXT,
    ResolvedByUserId TEXT REFERENCES Users(Id),
    Version INTEGER NOT NULL DEFAULT 1,
    WorkspaceId TEXT DEFAULT 'default' REFERENCES Workspaces(Id)   -- NULL = host-wide (backups); the fingerprint carries the workspace
);
INSERT INTO SyncAlerts_upgrade (Id, ReleaseTrainId, SourceSystem, Kind, Fingerprint, ErrorMessage, OccurrenceCount, FirstOccurredAt, LastOccurredAt, IsResolved, ResolvedAt, ResolvedByUserId, Version, WorkspaceId)
    SELECT Id, ReleaseTrainId, SourceSystem, Kind, Fingerprint, ErrorMessage, OccurrenceCount, FirstOccurredAt, LastOccurredAt, IsResolved, ResolvedAt, ResolvedByUserId, Version, WorkspaceId FROM SyncAlerts;
DROP TABLE SyncAlerts;
ALTER TABLE SyncAlerts_upgrade RENAME TO SyncAlerts;
CREATE UNIQUE INDEX UX_SyncAlerts_OpenFingerprint ON SyncAlerts(Fingerprint) WHERE IsResolved = 0;

-- Indexes
CREATE INDEX IX_MetricSamples_Time ON MetricSamples(SampledAt);
CREATE UNIQUE INDEX UX_Breaches_Open ON ThresholdBreaches(DeploymentId, Series, Metric) WHERE ResolvedAt IS NULL;
CREATE INDEX IX_Breaches_Deployment ON ThresholdBreaches(DeploymentId, StartedAt);
CREATE INDEX IX_ControlActions_Deployment ON DeploymentControlActions(DeploymentId, RequestedAt);
CREATE UNIQUE INDEX UX_ControlActions_OneRollbackPerBreach ON DeploymentControlActions(BreachId) WHERE BreachId IS NOT NULL;
CREATE UNIQUE INDEX UX_Approvals_OneOpenPerTarget ON Approvals(ReleaseTrainId, EnvironmentId) WHERE Status IN ('Pending','ChangesRequested');
CREATE INDEX IX_Approvals_Workspace ON Approvals(WorkspaceId, Status, DueAt);
CREATE INDEX IX_Audit_Chain ON AuditEvents(WorkspaceId, Id);
CREATE INDEX IX_Audit_Unsealed ON AuditEvents(Id) WHERE Hash IS NULL;

-- Every trigger of db/schema.sql, verbatim and in file order
-- =====================================================================
-- 10. Enforcement triggers
-- =====================================================================
-- Append-only tables
-- The only update an audit row allows is the sealer writing its hash pair once (REOS-114); every content column must stay as it is.
CREATE TRIGGER trg_Audit_NoUpdate BEFORE UPDATE ON AuditEvents
WHEN NOT (OLD.Hash IS NULL AND NEW.Hash IS NOT NULL AND NEW.PrevHash IS NOT NULL
      AND NEW.Id IS OLD.Id AND NEW.OccurredAt IS OLD.OccurredAt AND NEW.ActorUserId IS OLD.ActorUserId AND NEW.ReleaseTrainId IS OLD.ReleaseTrainId
      AND NEW.EntityType IS OLD.EntityType AND NEW.EntityId IS OLD.EntityId AND NEW.Action IS OLD.Action AND NEW.BeforeJson IS OLD.BeforeJson
      AND NEW.AfterJson IS OLD.AfterJson AND NEW.WorkspaceId IS OLD.WorkspaceId)
BEGIN SELECT RAISE(ABORT, 'AuditEvents is append-only'); END;
CREATE TRIGGER trg_Audit_NoDelete BEFORE DELETE ON AuditEvents BEGIN SELECT RAISE(ABORT, 'AuditEvents is append-only'); END;
CREATE TRIGGER trg_GateTrans_NoUpdate BEFORE UPDATE ON GateTransitions BEGIN SELECT RAISE(ABORT, 'GateTransitions is append-only'); END;
CREATE TRIGGER trg_GateTrans_NoDelete BEFORE DELETE ON GateTransitions BEGIN SELECT RAISE(ABORT, 'GateTransitions is append-only'); END;
CREATE TRIGGER trg_Baseline_NoUpdate BEFORE UPDATE ON Baselines BEGIN SELECT RAISE(ABORT, 'Baselines are immutable'); END;
CREATE TRIGGER trg_Baseline_NoDelete BEFORE DELETE ON Baselines BEGIN SELECT RAISE(ABORT, 'Baselines are immutable'); END;
CREATE TRIGGER trg_GoNoGo_NoUpdate BEFORE UPDATE ON GoNoGoDecisions BEGIN SELECT RAISE(ABORT, 'Go/No-Go decisions are immutable'); END;
CREATE TRIGGER trg_GoNoGo_NoDelete BEFORE DELETE ON GoNoGoDecisions BEGIN SELECT RAISE(ABORT, 'Go/No-Go decisions are immutable'); END;
CREATE TRIGGER trg_Dispatch_NoUpdate BEFORE UPDATE ON CommDispatches BEGIN SELECT RAISE(ABORT, 'Dispatches are immutable'); END;
CREATE TRIGGER trg_Dispatch_NoDelete BEFORE DELETE ON CommDispatches BEGIN SELECT RAISE(ABORT, 'Dispatches are immutable'); END;
CREATE TRIGGER trg_Attachment_LockedNoDelete BEFORE DELETE ON Attachments WHEN OLD.IsLocked = 1
BEGIN SELECT RAISE(ABORT, 'Attachment is locked evidence'); END;
CREATE TRIGGER trg_Attachment_LockedNoUpdate BEFORE UPDATE ON Attachments WHEN OLD.IsLocked = 1
BEGIN SELECT RAISE(ABORT, 'Attachment is locked evidence'); END;

-- Gate: legal transitions only. Defined BEFORE the other gate triggers on purpose: SQLite fires
-- same-event triggers newest-first, so the specific rule messages below surface before this generic one.
-- Legal: Pending->InProgress; InProgress->Certified|Failed|Waived; Failed->InProgress;
-- Certified->InProgress (decertify). Certified->Failed and Waived->anything are illegal.
CREATE TRIGGER trg_Gate_Transition BEFORE UPDATE OF Status ON StageGates
WHEN NEW.Status <> OLD.Status AND NOT (
       (OLD.Status = 'Pending'    AND NEW.Status = 'InProgress')
    OR (OLD.Status = 'InProgress' AND NEW.Status IN ('Certified','Failed','Waived'))
    OR (OLD.Status = 'Failed'     AND NEW.Status = 'InProgress')
    OR (OLD.Status = 'Certified'  AND NEW.Status = 'InProgress')
)
BEGIN SELECT RAISE(ABORT, 'Illegal gate status transition'); END;

-- Train: legal transitions only
CREATE TRIGGER trg_Train_Transition BEFORE UPDATE OF CurrentStatus ON ReleaseTrains
WHEN NEW.CurrentStatus <> OLD.CurrentStatus AND NOT (
       (OLD.CurrentStatus = 'Planning'  AND NEW.CurrentStatus IN ('Gated','Aborted'))
    OR (OLD.CurrentStatus = 'Gated'     AND NEW.CurrentStatus IN ('Planning','Executing','Aborted'))
    OR (OLD.CurrentStatus = 'Executing' AND NEW.CurrentStatus IN ('Complete','Aborted'))
)
BEGIN SELECT RAISE(ABORT, 'Illegal train status transition'); END;

-- Train: gate lockout
CREATE TRIGGER trg_Train_GateLockout BEFORE UPDATE OF CurrentStatus ON ReleaseTrains
WHEN NEW.CurrentStatus IN ('Gated','Executing','Complete') AND NEW.CurrentStatus <> OLD.CurrentStatus AND (
       (OLD.CurrentStatus = 'Planning'  AND NEW.CurrentStatus IN ('Gated','Aborted'))
    OR (OLD.CurrentStatus = 'Gated'     AND NEW.CurrentStatus IN ('Planning','Executing','Aborted'))
    OR (OLD.CurrentStatus = 'Executing' AND NEW.CurrentStatus IN ('Complete','Aborted')))
 AND EXISTS (
    SELECT 1 FROM StageGates g
    WHERE g.ReleaseTrainId = NEW.Id
      AND g.Status NOT IN ('Certified','Waived')
      AND (CASE g.RequiredBeforeStatus WHEN 'Gated' THEN 1 WHEN 'Executing' THEN 2 ELSE 3 END)
       <= (CASE NEW.CurrentStatus      WHEN 'Gated' THEN 1 WHEN 'Executing' THEN 2 ELSE 3 END)
 )
BEGIN SELECT RAISE(ABORT, 'Gate lockout: an uncertified gate blocks this transition'); END;

-- Train: Executing requires a Go decision, a baseline, and (High/VeryHigh) a rehearsed rollback
CREATE TRIGGER trg_Train_ExecutingRequiresGo BEFORE UPDATE OF CurrentStatus ON ReleaseTrains
WHEN NEW.CurrentStatus = 'Executing' AND OLD.CurrentStatus = 'Gated' AND NOT EXISTS (
    SELECT 1 FROM GoNoGoDecisions d WHERE d.ReleaseTrainId = NEW.Id AND d.Decision IN ('Go','GoWithConditions')
      AND d.DecidedAt = (SELECT max(DecidedAt) FROM GoNoGoDecisions WHERE ReleaseTrainId = NEW.Id))
BEGIN SELECT RAISE(ABORT, 'Executing requires a recorded Go decision'); END;

CREATE TRIGGER trg_Train_ExecutingNoExpiredConditions BEFORE UPDATE OF CurrentStatus ON ReleaseTrains
WHEN NEW.CurrentStatus = 'Executing' AND OLD.CurrentStatus = 'Gated' AND EXISTS (
    SELECT 1 FROM GoNoGoConditions c JOIN GoNoGoDecisions d ON d.Id = c.DecisionId
    WHERE d.ReleaseTrainId = NEW.Id AND c.ClosedAt IS NULL
      AND c.ExpiresAt <= COALESCE(NEW.LastChangedAt, strftime('%Y-%m-%dT%H:%M:%SZ','now')))
BEGIN SELECT RAISE(ABORT, 'A Go/No-Go condition expired without being closed'); END;

CREATE TRIGGER trg_Train_ExecutingRequiresBaseline BEFORE UPDATE OF CurrentStatus ON ReleaseTrains
WHEN NEW.CurrentStatus = 'Executing' AND OLD.CurrentStatus = 'Gated'
 AND NOT EXISTS (SELECT 1 FROM Baselines b WHERE b.ReleaseTrainId = NEW.Id)
BEGIN SELECT RAISE(ABORT, 'Executing requires a captured baseline'); END;

CREATE TRIGGER trg_Train_ExecutingRequiresRollback BEFORE UPDATE OF CurrentStatus ON ReleaseTrains
WHEN NEW.CurrentStatus = 'Executing' AND OLD.CurrentStatus = 'Gated'
 AND NEW.RiskTier IN ('High','VeryHigh') AND NEW.RollbackRehearsedAt IS NULL
BEGIN SELECT RAISE(ABORT, 'High-risk train requires a rehearsed rollback'); END;

-- Train: Complete with a bad close code requires a PIR row (service creates it in the same transaction)
CREATE TRIGGER trg_Train_CompleteRequiresPir AFTER UPDATE OF CurrentStatus ON ReleaseTrains
WHEN NEW.CurrentStatus = 'Complete' AND NEW.CloseCode IN ('SuccessfulWithIssues','Unsuccessful')
 AND NOT EXISTS (SELECT 1 FROM PostImplementationReviews p WHERE p.ReleaseTrainId = NEW.Id)
BEGIN
    INSERT INTO PostImplementationReviews(Id, ReleaseTrainId, RequiredReason, Status)
    VALUES (lower(hex(randomblob(16))), NEW.Id, 'CloseCode=' || NEW.CloseCode, 'Required');
    INSERT INTO AuditEvents(OccurredAt, ActorUserId, ReleaseTrainId, EntityType, EntityId, Action, AfterJson, WorkspaceId)
    VALUES (COALESCE(NEW.LastChangedAt, strftime('%Y-%m-%dT%H:%M:%SZ','now')), NEW.LastChangedByUserId, NEW.Id, 'PostImplementationReview',
            (SELECT Id FROM PostImplementationReviews WHERE ReleaseTrainId = NEW.Id),
            'AutoRequired', json_object('reason', 'CloseCode=' || NEW.CloseCode), NEW.WorkspaceId);
END;

-- Baseline: captured automatically when the first gate whose name contains 'Freeze' certifies (D15).
-- The service also exposes an explicit 'capture baseline' action for trains without a freeze gate.
CREATE TRIGGER trg_Gate_CertifyCapturesBaseline AFTER UPDATE OF Status ON StageGates
WHEN NEW.Status = 'Certified' AND OLD.Status <> 'Certified' AND NEW.GateName LIKE '%Freeze%'
 AND NOT EXISTS (SELECT 1 FROM Baselines b WHERE b.ReleaseTrainId = NEW.ReleaseTrainId)
BEGIN
    INSERT INTO Baselines(Id, ReleaseTrainId, CapturedAt, CapturedAtGateId, PlannedReleaseDate, SnapshotJson, CapturedByUserId)
    SELECT lower(hex(randomblob(16))), t.Id, NEW.CertifiedAt, NEW.Id, t.TargetReleaseDate,
           json_object(
             'products', (SELECT json_group_array(json_object('name',ProductName,'version',VersionTag)) FROM BundledProducts WHERE ReleaseTrainId = t.Id),
             'gates',    (SELECT json_group_array(json_object('name',GateName,'dueOn',DueOn)) FROM StageGates WHERE ReleaseTrainId = t.Id),
             'steps',    (SELECT json_group_array(json_object('code',StepCode,'plannedStart',PlannedStartAt,'plannedMin',PlannedDurationMin)) FROM RunbookSteps WHERE ReleaseTrainId = t.Id)
           ),
           NEW.CertifiedByUserId
    FROM ReleaseTrains t WHERE t.Id = NEW.ReleaseTrainId;
    INSERT INTO AuditEvents(OccurredAt, ActorUserId, ReleaseTrainId, EntityType, EntityId, Action, WorkspaceId)
    VALUES (COALESCE(NEW.LastChangedAt, strftime('%Y-%m-%dT%H:%M:%SZ','now')), NEW.CertifiedByUserId,
            NEW.ReleaseTrainId, 'Baseline', NEW.ReleaseTrainId, 'AutoCaptured', (SELECT WorkspaceId FROM ReleaseTrains WHERE Id = NEW.ReleaseTrainId));
END;

-- Gate: log every status change (actor from LastChangedByUserId, which must be set)
CREATE TRIGGER trg_Gate_RequireActor BEFORE UPDATE OF Status ON StageGates
WHEN NEW.Status <> OLD.Status AND NEW.LastChangedByUserId IS NULL
BEGIN SELECT RAISE(ABORT, 'Gate status change requires LastChangedByUserId'); END;

CREATE TRIGGER trg_Gate_LogTransition AFTER UPDATE OF Status ON StageGates
WHEN NEW.Status <> OLD.Status
BEGIN
    INSERT INTO GateTransitions(StageGateId, FromStatus, ToStatus, ActorUserId, OccurredAt)
    VALUES (NEW.Id, OLD.Status, NEW.Status, NEW.LastChangedByUserId, COALESCE(NEW.LastChangedAt, strftime('%Y-%m-%dT%H:%M:%SZ','now')));
END;

-- Gate: certification rules
CREATE TRIGGER trg_Gate_CertifyRequiresTasks BEFORE UPDATE OF Status ON StageGates
WHEN NEW.Status = 'Certified' AND EXISTS (SELECT 1 FROM ChecklistTasks t WHERE t.StageGateId = NEW.Id AND t.IsCompleted = 0)
BEGIN SELECT RAISE(ABORT, 'Gate has open checklist tasks'); END;

-- Open item 14 (provisional default): a Compliance gate cannot certify with zero tasks.
CREATE TRIGGER trg_Gate_ComplianceNeedsTask BEFORE UPDATE OF Status ON StageGates
WHEN NEW.Status = 'Certified' AND NEW.GateClass = 'Compliance'
 AND NOT EXISTS (SELECT 1 FROM ChecklistTasks t WHERE t.StageGateId = NEW.Id)
BEGIN SELECT RAISE(ABORT, 'Compliance gate needs at least one task'); END;

CREATE TRIGGER trg_Gate_CertifyInOrder BEFORE UPDATE OF Status ON StageGates
WHEN NEW.Status IN ('Certified','Waived') AND EXISTS (
    SELECT 1 FROM StageGates p WHERE p.ReleaseTrainId = NEW.ReleaseTrainId
      AND p.SequenceOrder < NEW.SequenceOrder AND p.Status NOT IN ('Certified','Waived'))
BEGIN SELECT RAISE(ABORT, 'An earlier gate is not certified'); END;

CREATE TRIGGER trg_Gate_ComplianceCertifierRole BEFORE UPDATE OF Status ON StageGates
WHEN NEW.Status IN ('Certified','Waived') AND NEW.GateClass = 'Compliance' AND NEW.CertifiedByUserId IS NOT NULL
 AND COALESCE((SELECT m.Role FROM WorkspaceMembers m JOIN ReleaseTrains t ON t.WorkspaceId = m.WorkspaceId
               WHERE t.Id = NEW.ReleaseTrainId AND m.UserId = NEW.CertifiedByUserId), '') <> 'GovernanceOfficer'
BEGIN SELECT RAISE(ABORT, 'Compliance gates are certified by Governance Officers only'); END;

-- Segregation of duties: the certifier of a Compliance gate completed none of its tasks
CREATE TRIGGER trg_Gate_CertifierNotTaskAuthor BEFORE UPDATE OF Status ON StageGates
WHEN NEW.Status = 'Certified' AND NEW.GateClass = 'Compliance'
 AND EXISTS (SELECT 1 FROM ChecklistTasks t WHERE t.StageGateId = NEW.Id AND t.CompletedByUserId = NEW.CertifiedByUserId)
BEGIN SELECT RAISE(ABORT, 'Segregation of duties: certifier completed a task in this gate'); END;

CREATE TRIGGER trg_Gate_WaiveRequiresApproval BEFORE UPDATE OF Status ON StageGates
WHEN NEW.Status = 'Waived' AND NOT EXISTS (
    SELECT 1 FROM GateWaivers w WHERE w.StageGateId = NEW.Id AND w.ApprovedByUserId IS NOT NULL)
BEGIN SELECT RAISE(ABORT, 'Waiver requires a second approver'); END;

-- Gate certified => lock its evidence
CREATE TRIGGER trg_Gate_CertifyLocksEvidence AFTER UPDATE OF Status ON StageGates
WHEN NEW.Status IN ('Certified','Waived')
BEGIN
    UPDATE Attachments SET IsLocked = 1
     WHERE IsLocked = 0 AND ((EntityType = 'Gate' AND EntityId = NEW.Id)
        OR (EntityType = 'Task' AND EntityId IN (SELECT Id FROM ChecklistTasks WHERE StageGateId = NEW.Id)));
    INSERT INTO AuditEvents(OccurredAt, ActorUserId, ReleaseTrainId, EntityType, EntityId, Action, WorkspaceId)
    SELECT COALESCE(NEW.LastChangedAt, strftime('%Y-%m-%dT%H:%M:%SZ','now')), NEW.LastChangedByUserId,
           NEW.ReleaseTrainId, 'StageGate', NEW.Id, 'EvidenceLocked', (SELECT WorkspaceId FROM ReleaseTrains WHERE Id = NEW.ReleaseTrainId)
    WHERE OLD.Status NOT IN ('Certified','Waived');
END;

-- Approver roles: waiver approver is a Governance Officer (D10); freeze-override approver is a
-- Release Manager or Governance Officer. Every role trigger reads the role held in the train's workspace (D45, REOS-105):
-- someone who is not a member of that workspace holds no role there.
CREATE TRIGGER trg_Waiver_ApproverRole BEFORE UPDATE OF ApprovedByUserId ON GateWaivers
WHEN NEW.ApprovedByUserId IS NOT NULL
 AND COALESCE((SELECT m.Role FROM WorkspaceMembers m JOIN ReleaseTrains t ON t.WorkspaceId = m.WorkspaceId JOIN StageGates g ON g.ReleaseTrainId = t.Id
               WHERE g.Id = NEW.StageGateId AND m.UserId = NEW.ApprovedByUserId), '') <> 'GovernanceOfficer'
BEGIN SELECT RAISE(ABORT, 'Waivers are approved by a Governance Officer'); END;

CREATE TRIGGER trg_Waiver_ApproverRoleInsert BEFORE INSERT ON GateWaivers
WHEN NEW.ApprovedByUserId IS NOT NULL
 AND COALESCE((SELECT m.Role FROM WorkspaceMembers m JOIN ReleaseTrains t ON t.WorkspaceId = m.WorkspaceId JOIN StageGates g ON g.ReleaseTrainId = t.Id
               WHERE g.Id = NEW.StageGateId AND m.UserId = NEW.ApprovedByUserId), '') <> 'GovernanceOfficer'
BEGIN SELECT RAISE(ABORT, 'Waivers are approved by a Governance Officer'); END;

-- A waiver is approved or declined (REOS-113): by a Governance Officer other than the requester, and never both.
CREATE TRIGGER trg_Waiver_DeclineRole BEFORE UPDATE OF DeclinedByUserId ON GateWaivers
WHEN NEW.DeclinedByUserId IS NOT NULL
 AND COALESCE((SELECT m.Role FROM WorkspaceMembers m JOIN ReleaseTrains t ON t.WorkspaceId = m.WorkspaceId JOIN StageGates g ON g.ReleaseTrainId = t.Id
               WHERE g.Id = NEW.StageGateId AND m.UserId = NEW.DeclinedByUserId), '') <> 'GovernanceOfficer'
BEGIN SELECT RAISE(ABORT, 'Waivers are declined by a Governance Officer'); END;

CREATE TRIGGER trg_Waiver_DeclineDistinct BEFORE UPDATE OF DeclinedByUserId ON GateWaivers
WHEN NEW.DeclinedByUserId IS NOT NULL AND NEW.DeclinedByUserId = NEW.RequestedByUserId
BEGIN SELECT RAISE(ABORT, 'A waiver needs two distinct people: the requester cannot decline it'); END;

CREATE TRIGGER trg_Waiver_ApprovedOrDeclined BEFORE UPDATE OF ApprovedByUserId, DeclinedByUserId ON GateWaivers
WHEN (NEW.ApprovedByUserId IS NOT NULL AND NEW.DeclinedByUserId IS NOT NULL) OR (OLD.DeclinedAt IS NOT NULL AND NEW.ApprovedByUserId IS NOT NULL)
BEGIN SELECT RAISE(ABORT, 'A waiver is approved or declined, not both'); END;

CREATE TRIGGER trg_Override_NoUpdate BEFORE UPDATE ON FreezeOverrides
BEGIN SELECT RAISE(ABORT, 'Freeze overrides are immutable; renew with a new row'); END;
CREATE TRIGGER trg_Override_NoDelete BEFORE DELETE ON FreezeOverrides
BEGIN SELECT RAISE(ABORT, 'Freeze overrides are immutable; renew with a new row'); END;

CREATE TRIGGER trg_Override_ApproverRole BEFORE INSERT ON FreezeOverrides
WHEN COALESCE((SELECT m.Role FROM WorkspaceMembers m JOIN ReleaseTrains t ON t.WorkspaceId = m.WorkspaceId
               WHERE t.Id = NEW.ReleaseTrainId AND m.UserId = NEW.ApprovedByUserId), '') NOT IN ('ReleaseManager','GovernanceOfficer')
BEGIN SELECT RAISE(ABORT, 'Freeze overrides are approved by a Release Manager or Governance Officer'); END;

-- Task reopen or insert decertifies its gate
CREATE TRIGGER trg_Task_ReopenDecertifies AFTER UPDATE OF IsCompleted ON ChecklistTasks
WHEN OLD.IsCompleted = 1 AND NEW.IsCompleted = 0
BEGIN
    INSERT INTO AuditEvents(OccurredAt, ActorUserId, ReleaseTrainId, EntityType, EntityId, Action, WorkspaceId)
    SELECT COALESCE(NEW.LastChangedAt, strftime('%Y-%m-%dT%H:%M:%SZ','now')), NEW.LastChangedByUserId,
           g.ReleaseTrainId, 'StageGate', g.Id, 'Decertified', (SELECT WorkspaceId FROM ReleaseTrains WHERE Id = g.ReleaseTrainId)
      FROM StageGates g WHERE g.Id = NEW.StageGateId AND g.Status = 'Certified';
    UPDATE StageGates SET Status = 'InProgress', CertifiedByUserId = NULL, CertifiedAt = NULL,
           LastChangedByUserId = NEW.LastChangedByUserId, LastChangedAt = NEW.LastChangedAt, Version = Version + 1
     WHERE Id = NEW.StageGateId AND Status = 'Certified';
END;

CREATE TRIGGER trg_Task_InsertDecertifies AFTER INSERT ON ChecklistTasks
WHEN NEW.IsCompleted = 0
BEGIN
    INSERT INTO AuditEvents(OccurredAt, ActorUserId, ReleaseTrainId, EntityType, EntityId, Action, WorkspaceId)
    SELECT COALESCE(NEW.LastChangedAt, strftime('%Y-%m-%dT%H:%M:%SZ','now')), NEW.LastChangedByUserId,
           g.ReleaseTrainId, 'StageGate', g.Id, 'Decertified', (SELECT WorkspaceId FROM ReleaseTrains WHERE Id = g.ReleaseTrainId)
      FROM StageGates g WHERE g.Id = NEW.StageGateId AND g.Status = 'Certified';
    UPDATE StageGates SET Status = 'InProgress', CertifiedByUserId = NULL, CertifiedAt = NULL,
           LastChangedByUserId = NEW.LastChangedByUserId, LastChangedAt = NEW.LastChangedAt, Version = Version + 1
     WHERE Id = NEW.StageGateId AND Status = 'Certified';
END;

-- Runbook: a Live run may start only while the train is Executing
CREATE TRIGGER trg_Run_LiveRequiresExecuting BEFORE INSERT ON RunbookRuns
WHEN NEW.Mode = 'Live' AND (SELECT CurrentStatus FROM ReleaseTrains WHERE Id = NEW.ReleaseTrainId) <> 'Executing'
BEGIN SELECT RAISE(ABORT, 'Live run requires the train to be Executing'); END;

-- Runbook: a step cannot start before its dependencies are Done in the same run
CREATE TRIGGER trg_Step_DependenciesDone BEFORE UPDATE OF Status ON StepExecutions
WHEN NEW.Status = 'Running' AND EXISTS (
    SELECT 1 FROM StepDependencies d
    LEFT JOIN StepExecutions e ON e.StepId = d.DependsOnStepId AND e.RunId = NEW.RunId
    WHERE d.StepId = NEW.StepId AND COALESCE(e.Status,'Scheduled') NOT IN ('Done','Skipped'))
BEGIN SELECT RAISE(ABORT, 'Dependency not complete'); END;

-- Freeze: a Live Deploy-section step cannot start inside an active freeze without an unexpired override
CREATE TRIGGER trg_Step_FreezeLockout BEFORE UPDATE OF Status ON StepExecutions
WHEN NEW.Status = 'Running'
 AND (SELECT Mode FROM RunbookRuns WHERE Id = NEW.RunId) = 'Live'
 AND (SELECT Section FROM RunbookSteps WHERE Id = NEW.StepId) = 'Deploy'
 AND EXISTS (
    SELECT 1 FROM FreezeWindows f
    JOIN RunbookSteps s ON s.Id = NEW.StepId
    LEFT JOIN BundledProducts p ON p.Id = s.BundledProductId
    WHERE f.Kind = 'Freeze'
      AND f.WorkspaceId = (SELECT WorkspaceId FROM ReleaseTrains WHERE Id = s.ReleaseTrainId)   -- another workspace's freeze never applies
      AND NEW.ActualStartAt >= f.StartsAt AND NEW.ActualStartAt < f.EndsAt
      AND (f.ProductPattern IS NULL OR (p.ProductName IS NOT NULL AND p.ProductName GLOB f.ProductPattern))
      AND NOT EXISTS (
          SELECT 1 FROM FreezeOverrides o
          WHERE o.FreezeWindowId = f.Id AND o.ReleaseTrainId = s.ReleaseTrainId AND o.ExpiresAt > NEW.ActualStartAt))
BEGIN SELECT RAISE(ABORT, 'Freeze window: no valid override for this train'); END;

-- Go/No-Go: conditions must belong to a GoWithConditions decision
CREATE TRIGGER trg_Condition_RequiresConditionalGo BEFORE INSERT ON GoNoGoConditions
WHEN (SELECT Decision FROM GoNoGoDecisions WHERE Id = NEW.DecisionId) <> 'GoWithConditions'
BEGIN SELECT RAISE(ABORT, 'Conditions belong to a GoWithConditions decision'); END;

-- Imports: a job with errors cannot commit (also a CHECK; trigger gives the message)
CREATE TRIGGER trg_Import_NoCommitWithErrors BEFORE UPDATE OF Status ON ImportJobs
WHEN NEW.Status = 'Committed' AND NEW.ErrorCount > 0
BEGIN SELECT RAISE(ABORT, 'Import has row errors; fix the file and re-preview'); END;

-- Release keys, deployments (REOS-107, REOS-110)
CREATE TRIGGER trg_Train_KeyImmutable BEFORE UPDATE OF ReleaseKey ON ReleaseTrains WHEN OLD.ReleaseKey IS NOT NULL AND NEW.ReleaseKey IS NOT OLD.ReleaseKey
BEGIN SELECT RAISE(ABORT, 'A release key never changes'); END;
CREATE TRIGGER trg_Train_AssignKey AFTER INSERT ON ReleaseTrains WHEN NEW.ReleaseKey IS NULL
BEGIN
    UPDATE Workspaces SET NextKeyNumber = NextKeyNumber + 1 WHERE Id = NEW.WorkspaceId;
    UPDATE ReleaseTrains SET ReleaseKey = (SELECT KeyPrefix || '-' || (NextKeyNumber - 1) FROM Workspaces WHERE Id = NEW.WorkspaceId) WHERE Id = NEW.Id;
END;
CREATE TRIGGER trg_Deployment_Transition BEFORE UPDATE OF State ON Deployments WHEN NEW.State <> OLD.State
BEGIN
    SELECT RAISE(ABORT, 'Illegal deployment state transition')
     WHERE NOT ((OLD.State = 'Queued' AND NEW.State IN ('Running','Failed'))
             OR (OLD.State = 'Running' AND NEW.State IN ('Paused','Successful','Failed','RolledBack'))
             OR (OLD.State = 'Paused' AND NEW.State IN ('Running','Failed','RolledBack'))
             OR (OLD.State IN ('Successful','Failed') AND NEW.State = 'RolledBack'));
END;
CREATE TRIGGER trg_DeploymentEvent_NoUpdate BEFORE UPDATE ON DeploymentEvents BEGIN SELECT RAISE(ABORT, 'DeploymentEvents is append-only'); END;

-- Run pause (REOS-112): while a run is paused no step of it can start. Created after the other step triggers so its message is the one a caller sees.
CREATE TRIGGER trg_Step_RunPaused BEFORE UPDATE OF Status ON StepExecutions
WHEN NEW.Status = 'Running' AND (SELECT PausedAt FROM RunbookRuns WHERE Id = NEW.RunId) IS NOT NULL
BEGIN SELECT RAISE(ABORT, 'The run is paused: no step can start until it is resumed'); END;

-- Approvals (REOS-113). Eligibility and separation of duties are the database's rules too; the service checks them first for a readable reason.
CREATE TRIGGER trg_Approval_ApprovedNeedsAll BEFORE UPDATE OF Status ON Approvals
WHEN NEW.Status = 'Approved' AND (NOT EXISTS (SELECT 1 FROM ApprovalDecisions d WHERE d.ApprovalId = NEW.Id)
                               OR EXISTS (SELECT 1 FROM ApprovalDecisions d WHERE d.ApprovalId = NEW.Id AND d.Decision IS NOT 'Approved'))
BEGIN SELECT RAISE(ABORT, 'Every required approver must approve first'); END;

CREATE TRIGGER trg_Approval_Final BEFORE UPDATE OF Status ON Approvals
WHEN OLD.Status IN ('Approved','Cancelled') AND NEW.Status <> OLD.Status
BEGIN SELECT RAISE(ABORT, 'An approved or cancelled approval is final'); END;

CREATE TRIGGER trg_ApprovalDecision_ApprovalOpen BEFORE UPDATE ON ApprovalDecisions
WHEN (SELECT Status FROM Approvals WHERE Id = NEW.ApprovalId) IN ('Approved','Cancelled')
BEGIN SELECT RAISE(ABORT, 'The approval is closed'); END;

CREATE TRIGGER trg_ApprovalDecision_Final BEFORE UPDATE OF Decision ON ApprovalDecisions
WHEN OLD.Decision IS NOT NULL AND NEW.Decision IS NOT NULL
BEGIN SELECT RAISE(ABORT, 'A decision is final; the requester resubmits the approval for a new round'); END;

CREATE TRIGGER trg_ApprovalDecision_Eligible BEFORE UPDATE OF Decision ON ApprovalDecisions
WHEN NEW.Decision IS NOT NULL
BEGIN
    SELECT RAISE(ABORT, 'Not an eligible approver for this seat')
     WHERE NOT EXISTS (
        SELECT 1 FROM Approvals a JOIN ReleaseTrains t ON t.Id = a.ReleaseTrainId
         WHERE a.Id = NEW.ApprovalId
           AND EXISTS (SELECT 1 FROM WorkspaceMembers m WHERE m.WorkspaceId = a.WorkspaceId AND m.UserId = NEW.DecidedByUserId)
           AND ((NEW.EligibleKind = 'ReleaseOwner' AND t.OwnerUserId = NEW.DecidedByUserId)
             OR (NEW.EligibleKind = 'WorkspaceRole' AND EXISTS (SELECT 1 FROM WorkspaceMembers m WHERE m.WorkspaceId = a.WorkspaceId AND m.UserId = NEW.DecidedByUserId AND m.Role = NEW.EligibleValue))
             OR (NEW.EligibleKind = 'Team' AND EXISTS (SELECT 1 FROM TeamMembers tm WHERE tm.TeamId = NEW.EligibleValue AND tm.UserId = NEW.DecidedByUserId))));
END;

CREATE TRIGGER trg_ApprovalDecision_Separation BEFORE UPDATE OF Decision ON ApprovalDecisions
WHEN NEW.Decision = 'Approved'
BEGIN
    SELECT RAISE(ABORT, 'Separation of duties: you cannot approve a change you requested')
     WHERE EXISTS (SELECT 1 FROM Approvals a WHERE a.Id = NEW.ApprovalId AND a.RequestedByUserId = NEW.DecidedByUserId);
    SELECT RAISE(ABORT, 'Separation of duties: on a Production change the release owner approves only the release owner seat')
     WHERE NEW.EligibleKind <> 'ReleaseOwner'
       AND EXISTS (SELECT 1 FROM Approvals a JOIN ReleaseTrains t ON t.Id = a.ReleaseTrainId JOIN Environments e ON e.Id = a.EnvironmentId
                    WHERE a.Id = NEW.ApprovalId AND e.Kind = 'Production' AND t.OwnerUserId = NEW.DecidedByUserId);
    SELECT RAISE(ABORT, 'Separation of duties: one person cannot hold two seats on a Production change')
     WHERE EXISTS (SELECT 1 FROM ApprovalDecisions d JOIN Approvals a ON a.Id = d.ApprovalId JOIN Environments e ON e.Id = a.EnvironmentId
                    WHERE d.ApprovalId = NEW.ApprovalId AND d.Id <> NEW.Id AND d.Decision = 'Approved' AND d.DecidedByUserId = NEW.DecidedByUserId AND e.Kind = 'Production');
END;

-- =====================================================================
-- 10b. Workspace boundary (D45, REOS-104)
-- =====================================================================
-- A root row never moves to another workspace, and no row references a row of another workspace. Child rows take their
-- workspace from their parent (train, template, export job); rows of one train that point at each other (product, gate, step,
-- run, communication template, dispatch) must be in the same train, and such rows never move to another train.
-- One trigger per table and event; each check raises its own message. A reference to a missing row is left to the foreign key.

-- Roots: WorkspaceId never changes
CREATE TRIGGER trg_Ws_Holidays_Update BEFORE UPDATE OF WorkspaceId ON Holidays WHEN NEW.WorkspaceId IS NOT OLD.WorkspaceId
BEGIN SELECT RAISE(ABORT, 'A row cannot move to another workspace'); END;
CREATE TRIGGER trg_Ws_TrainTemplates_Update BEFORE UPDATE OF WorkspaceId ON TrainTemplates WHEN NEW.WorkspaceId IS NOT OLD.WorkspaceId
BEGIN SELECT RAISE(ABORT, 'A row cannot move to another workspace'); END;
CREATE TRIGGER trg_Ws_CommTemplateLibrary_Update BEFORE UPDATE OF WorkspaceId ON CommTemplateLibrary WHEN NEW.WorkspaceId IS NOT OLD.WorkspaceId
BEGIN SELECT RAISE(ABORT, 'A row cannot move to another workspace'); END;
CREATE TRIGGER trg_Ws_FreezeWindows_Update BEFORE UPDATE OF WorkspaceId ON FreezeWindows WHEN NEW.WorkspaceId IS NOT OLD.WorkspaceId
BEGIN SELECT RAISE(ABORT, 'A row cannot move to another workspace'); END;
CREATE TRIGGER trg_Ws_WebhookDestinations_Update BEFORE UPDATE OF WorkspaceId ON WebhookDestinations WHEN NEW.WorkspaceId IS NOT OLD.WorkspaceId
BEGIN SELECT RAISE(ABORT, 'A row cannot move to another workspace'); END;
CREATE TRIGGER trg_Ws_Notifications_Update BEFORE UPDATE OF WorkspaceId ON Notifications WHEN NEW.WorkspaceId IS NOT OLD.WorkspaceId
BEGIN SELECT RAISE(ABORT, 'A row cannot move to another workspace'); END;
CREATE TRIGGER trg_Ws_IcsTokens_Update BEFORE UPDATE OF WorkspaceId ON IcsTokens WHEN NEW.WorkspaceId IS NOT OLD.WorkspaceId
BEGIN SELECT RAISE(ABORT, 'A row cannot move to another workspace'); END;
CREATE TRIGGER trg_Ws_ConnectorState_Update BEFORE UPDATE OF WorkspaceId ON ConnectorState WHEN NEW.WorkspaceId IS NOT OLD.WorkspaceId
BEGIN SELECT RAISE(ABORT, 'A row cannot move to another workspace'); END;

-- Roots that point at other roots
CREATE TRIGGER trg_Ws_ReleaseTrains_Insert BEFORE INSERT ON ReleaseTrains
BEGIN
    SELECT RAISE(ABORT, 'The template belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM TrainTemplates x WHERE x.Id = NEW.TemplateId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The cloned-from train belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM ReleaseTrains x WHERE x.Id = NEW.ClonedFromTrainId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The owner is not a member of this workspace')
     WHERE NEW.OwnerUserId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM WorkspaceMembers m WHERE m.UserId = NEW.OwnerUserId AND m.WorkspaceId = NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The on-call person is not a member of this workspace')
     WHERE NEW.OnCallUserId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM WorkspaceMembers m WHERE m.UserId = NEW.OnCallUserId AND m.WorkspaceId = NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The owning team belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Teams x WHERE x.Id = NEW.OwnerTeamId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The on-call team belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Teams x WHERE x.Id = NEW.OnCallTeamId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The release type belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM ReleaseTypes x WHERE x.Id = NEW.ReleaseTypeId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_ReleaseTrains_Update BEFORE UPDATE OF WorkspaceId, TemplateId, ClonedFromTrainId, OwnerUserId, OwnerTeamId, OnCallUserId, OnCallTeamId, ReleaseTypeId ON ReleaseTrains
BEGIN
    SELECT RAISE(ABORT, 'A row cannot move to another workspace') WHERE NEW.WorkspaceId IS NOT OLD.WorkspaceId;
    SELECT RAISE(ABORT, 'The template belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM TrainTemplates x WHERE x.Id = NEW.TemplateId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The cloned-from train belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM ReleaseTrains x WHERE x.Id = NEW.ClonedFromTrainId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The owner is not a member of this workspace')
     WHERE NEW.OwnerUserId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM WorkspaceMembers m WHERE m.UserId = NEW.OwnerUserId AND m.WorkspaceId = NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The on-call person is not a member of this workspace')
     WHERE NEW.OnCallUserId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM WorkspaceMembers m WHERE m.UserId = NEW.OnCallUserId AND m.WorkspaceId = NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The owning team belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Teams x WHERE x.Id = NEW.OwnerTeamId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The on-call team belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Teams x WHERE x.Id = NEW.OnCallTeamId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The release type belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM ReleaseTypes x WHERE x.Id = NEW.ReleaseTypeId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_Teams_Insert BEFORE INSERT ON Teams
BEGIN
    SELECT RAISE(ABORT, 'The webhook destination belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM WebhookDestinations x WHERE x.Id = NEW.WebhookDestinationId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_Teams_Update BEFORE UPDATE OF WorkspaceId, WebhookDestinationId ON Teams
BEGIN
    SELECT RAISE(ABORT, 'A row cannot move to another workspace') WHERE NEW.WorkspaceId IS NOT OLD.WorkspaceId;
    SELECT RAISE(ABORT, 'The webhook destination belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM WebhookDestinations x WHERE x.Id = NEW.WebhookDestinationId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_SyncAlerts_Insert BEFORE INSERT ON SyncAlerts
BEGIN
    SELECT RAISE(ABORT, 'The train belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM ReleaseTrains x WHERE x.Id = NEW.ReleaseTrainId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_SyncAlerts_Update BEFORE UPDATE OF WorkspaceId, ReleaseTrainId ON SyncAlerts
BEGIN
    SELECT RAISE(ABORT, 'A row cannot move to another workspace') WHERE NEW.WorkspaceId IS NOT OLD.WorkspaceId;
    SELECT RAISE(ABORT, 'The train belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM ReleaseTrains x WHERE x.Id = NEW.ReleaseTrainId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_ImportJobs_Insert BEFORE INSERT ON ImportJobs
BEGIN
    SELECT RAISE(ABORT, 'The train belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM ReleaseTrains x WHERE x.Id = NEW.ReleaseTrainId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_ImportJobs_Update BEFORE UPDATE OF WorkspaceId, ReleaseTrainId ON ImportJobs
BEGIN
    SELECT RAISE(ABORT, 'A row cannot move to another workspace') WHERE NEW.WorkspaceId IS NOT OLD.WorkspaceId;
    SELECT RAISE(ABORT, 'The train belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM ReleaseTrains x WHERE x.Id = NEW.ReleaseTrainId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_ExportJobs_Insert BEFORE INSERT ON ExportJobs
BEGIN
    SELECT RAISE(ABORT, 'The train belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM ReleaseTrains x WHERE x.Id = NEW.ReleaseTrainId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_ExportJobs_Update BEFORE UPDATE OF WorkspaceId, ReleaseTrainId ON ExportJobs
BEGIN
    SELECT RAISE(ABORT, 'A row cannot move to another workspace') WHERE NEW.WorkspaceId IS NOT OLD.WorkspaceId;
    SELECT RAISE(ABORT, 'The train belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM ReleaseTrains x WHERE x.Id = NEW.ReleaseTrainId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_AuditEvents_Insert BEFORE INSERT ON AuditEvents
BEGIN
    SELECT RAISE(ABORT, 'The train belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM ReleaseTrains x WHERE x.Id = NEW.ReleaseTrainId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
END;

-- Template rows: owner teams and library templates from the template's workspace
CREATE TRIGGER trg_Ws_TemplateGates_Insert BEFORE INSERT ON TemplateGates
BEGIN
    SELECT RAISE(ABORT, 'The team belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Teams x JOIN TrainTemplates p ON p.Id = NEW.TemplateId
                    WHERE x.Id = NEW.OwnerTeamId AND x.WorkspaceId IS NOT p.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_TemplateGates_Update BEFORE UPDATE OF TemplateId, OwnerTeamId ON TemplateGates
BEGIN
    SELECT RAISE(ABORT, 'The team belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Teams x JOIN TrainTemplates p ON p.Id = NEW.TemplateId
                    WHERE x.Id = NEW.OwnerTeamId AND x.WorkspaceId IS NOT p.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_TemplateSteps_Insert BEFORE INSERT ON TemplateSteps
BEGIN
    SELECT RAISE(ABORT, 'The team belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Teams x JOIN TrainTemplates p ON p.Id = NEW.TemplateId
                    WHERE x.Id = NEW.OwnerTeamId AND x.WorkspaceId IS NOT p.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_TemplateSteps_Update BEFORE UPDATE OF TemplateId, OwnerTeamId ON TemplateSteps
BEGIN
    SELECT RAISE(ABORT, 'The team belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Teams x JOIN TrainTemplates p ON p.Id = NEW.TemplateId
                    WHERE x.Id = NEW.OwnerTeamId AND x.WorkspaceId IS NOT p.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_TemplateCommSchedule_Insert BEFORE INSERT ON TemplateCommSchedule
BEGIN
    SELECT RAISE(ABORT, 'The library template belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM CommTemplateLibrary x JOIN TrainTemplates p ON p.Id = NEW.TemplateId
                    WHERE x.Id = NEW.LibraryTemplateId AND x.WorkspaceId IS NOT p.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_TemplateCommSchedule_Update BEFORE UPDATE OF TemplateId, LibraryTemplateId ON TemplateCommSchedule
BEGIN
    SELECT RAISE(ABORT, 'The library template belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM CommTemplateLibrary x JOIN TrainTemplates p ON p.Id = NEW.TemplateId
                    WHERE x.Id = NEW.LibraryTemplateId AND x.WorkspaceId IS NOT p.WorkspaceId);
END;

-- Train rows: teams, library templates and webhooks from the train's workspace; products, gates and comm templates from the same train
CREATE TRIGGER trg_Ws_StageGates_Insert BEFORE INSERT ON StageGates
BEGIN
    SELECT RAISE(ABORT, 'The team belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Teams x JOIN ReleaseTrains p ON p.Id = NEW.ReleaseTrainId
                    WHERE x.Id = NEW.OwnerTeamId AND x.WorkspaceId IS NOT p.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_StageGates_Update BEFORE UPDATE OF ReleaseTrainId, OwnerTeamId ON StageGates
BEGIN
    SELECT RAISE(ABORT, 'A row cannot move to another train') WHERE NEW.ReleaseTrainId IS NOT OLD.ReleaseTrainId;
    SELECT RAISE(ABORT, 'The team belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Teams x JOIN ReleaseTrains p ON p.Id = NEW.ReleaseTrainId
                    WHERE x.Id = NEW.OwnerTeamId AND x.WorkspaceId IS NOT p.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_RunbookSteps_Insert BEFORE INSERT ON RunbookSteps
BEGIN
    SELECT RAISE(ABORT, 'The team belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Teams x JOIN ReleaseTrains p ON p.Id = NEW.ReleaseTrainId
                    WHERE x.Id = NEW.OwnerTeamId AND x.WorkspaceId IS NOT p.WorkspaceId);
    SELECT RAISE(ABORT, 'The product belongs to another train')
     WHERE EXISTS (SELECT 1 FROM BundledProducts x WHERE x.Id = NEW.BundledProductId AND x.ReleaseTrainId IS NOT NEW.ReleaseTrainId);
END;
CREATE TRIGGER trg_Ws_RunbookSteps_Update BEFORE UPDATE OF ReleaseTrainId, OwnerTeamId, BundledProductId ON RunbookSteps
BEGIN
    SELECT RAISE(ABORT, 'A row cannot move to another train') WHERE NEW.ReleaseTrainId IS NOT OLD.ReleaseTrainId;
    SELECT RAISE(ABORT, 'The team belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Teams x JOIN ReleaseTrains p ON p.Id = NEW.ReleaseTrainId
                    WHERE x.Id = NEW.OwnerTeamId AND x.WorkspaceId IS NOT p.WorkspaceId);
    SELECT RAISE(ABORT, 'The product belongs to another train')
     WHERE EXISTS (SELECT 1 FROM BundledProducts x WHERE x.Id = NEW.BundledProductId AND x.ReleaseTrainId IS NOT NEW.ReleaseTrainId);
END;
CREATE TRIGGER trg_Ws_TrainMilestones_Insert BEFORE INSERT ON TrainMilestones
BEGIN
    SELECT RAISE(ABORT, 'The team belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Teams x JOIN ReleaseTrains p ON p.Id = NEW.ReleaseTrainId
                    WHERE x.Id = NEW.OwnerTeamId AND x.WorkspaceId IS NOT p.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_TrainMilestones_Update BEFORE UPDATE OF ReleaseTrainId, OwnerTeamId ON TrainMilestones
BEGIN
    SELECT RAISE(ABORT, 'A row cannot move to another train') WHERE NEW.ReleaseTrainId IS NOT OLD.ReleaseTrainId;
    SELECT RAISE(ABORT, 'The team belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Teams x JOIN ReleaseTrains p ON p.Id = NEW.ReleaseTrainId
                    WHERE x.Id = NEW.OwnerTeamId AND x.WorkspaceId IS NOT p.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_Blockers_Insert BEFORE INSERT ON Blockers
BEGIN
    SELECT RAISE(ABORT, 'The product belongs to another train')
     WHERE EXISTS (SELECT 1 FROM BundledProducts x WHERE x.Id = NEW.BundledProductId AND x.ReleaseTrainId IS NOT NEW.ReleaseTrainId);
    SELECT RAISE(ABORT, 'The gate belongs to another train')
     WHERE EXISTS (SELECT 1 FROM StageGates x WHERE x.Id = NEW.StageGateId AND x.ReleaseTrainId IS NOT NEW.ReleaseTrainId);
END;
CREATE TRIGGER trg_Ws_Blockers_Update BEFORE UPDATE OF ReleaseTrainId, BundledProductId, StageGateId ON Blockers
BEGIN
    SELECT RAISE(ABORT, 'A row cannot move to another train') WHERE NEW.ReleaseTrainId IS NOT OLD.ReleaseTrainId;
    SELECT RAISE(ABORT, 'The product belongs to another train')
     WHERE EXISTS (SELECT 1 FROM BundledProducts x WHERE x.Id = NEW.BundledProductId AND x.ReleaseTrainId IS NOT NEW.ReleaseTrainId);
    SELECT RAISE(ABORT, 'The gate belongs to another train')
     WHERE EXISTS (SELECT 1 FROM StageGates x WHERE x.Id = NEW.StageGateId AND x.ReleaseTrainId IS NOT NEW.ReleaseTrainId);
END;
CREATE TRIGGER trg_Ws_CommTemplates_Insert BEFORE INSERT ON CommTemplates
BEGIN
    SELECT RAISE(ABORT, 'The library template belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM CommTemplateLibrary x JOIN ReleaseTrains p ON p.Id = NEW.ReleaseTrainId
                    WHERE x.Id = NEW.LibraryTemplateId AND x.WorkspaceId IS NOT p.WorkspaceId);
    SELECT RAISE(ABORT, 'The gate belongs to another train')
     WHERE EXISTS (SELECT 1 FROM StageGates x WHERE x.Id = NEW.StageGateId AND x.ReleaseTrainId IS NOT NEW.ReleaseTrainId);
END;
CREATE TRIGGER trg_Ws_CommTemplates_Update BEFORE UPDATE OF ReleaseTrainId, LibraryTemplateId, StageGateId ON CommTemplates
BEGIN
    SELECT RAISE(ABORT, 'A row cannot move to another train') WHERE NEW.ReleaseTrainId IS NOT OLD.ReleaseTrainId;
    SELECT RAISE(ABORT, 'The library template belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM CommTemplateLibrary x JOIN ReleaseTrains p ON p.Id = NEW.ReleaseTrainId
                    WHERE x.Id = NEW.LibraryTemplateId AND x.WorkspaceId IS NOT p.WorkspaceId);
    SELECT RAISE(ABORT, 'The gate belongs to another train')
     WHERE EXISTS (SELECT 1 FROM StageGates x WHERE x.Id = NEW.StageGateId AND x.ReleaseTrainId IS NOT NEW.ReleaseTrainId);
END;
CREATE TRIGGER trg_Ws_CommSchedule_Insert BEFORE INSERT ON CommSchedule
BEGIN
    SELECT RAISE(ABORT, 'The communication template belongs to another train')
     WHERE EXISTS (SELECT 1 FROM CommTemplates x WHERE x.Id = NEW.CommTemplateId AND x.ReleaseTrainId IS NOT NEW.ReleaseTrainId);
    SELECT RAISE(ABORT, 'The dispatch belongs to another train')
     WHERE EXISTS (SELECT 1 FROM CommDispatches x JOIN CommTemplates c ON c.Id = x.CommTemplateId
                    WHERE x.Id = NEW.DispatchId AND c.ReleaseTrainId IS NOT NEW.ReleaseTrainId);
END;
CREATE TRIGGER trg_Ws_CommSchedule_Update BEFORE UPDATE OF ReleaseTrainId, CommTemplateId, DispatchId ON CommSchedule
BEGIN
    SELECT RAISE(ABORT, 'A row cannot move to another train') WHERE NEW.ReleaseTrainId IS NOT OLD.ReleaseTrainId;
    SELECT RAISE(ABORT, 'The communication template belongs to another train')
     WHERE EXISTS (SELECT 1 FROM CommTemplates x WHERE x.Id = NEW.CommTemplateId AND x.ReleaseTrainId IS NOT NEW.ReleaseTrainId);
    SELECT RAISE(ABORT, 'The dispatch belongs to another train')
     WHERE EXISTS (SELECT 1 FROM CommDispatches x JOIN CommTemplates c ON c.Id = x.CommTemplateId
                    WHERE x.Id = NEW.DispatchId AND c.ReleaseTrainId IS NOT NEW.ReleaseTrainId);
END;
CREATE TRIGGER trg_Ws_BundledProducts_Update BEFORE UPDATE OF ReleaseTrainId, CatalogServiceId ON BundledProducts
BEGIN
    SELECT RAISE(ABORT, 'A row cannot move to another train') WHERE NEW.ReleaseTrainId IS NOT OLD.ReleaseTrainId;
    SELECT RAISE(ABORT, 'The catalog service belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM CatalogServices x JOIN ReleaseTrains p ON p.Id = NEW.ReleaseTrainId WHERE x.Id = NEW.CatalogServiceId AND x.WorkspaceId IS NOT p.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_RunbookRuns_Update BEFORE UPDATE OF ReleaseTrainId ON RunbookRuns WHEN NEW.ReleaseTrainId IS NOT OLD.ReleaseTrainId
BEGIN SELECT RAISE(ABORT, 'A row cannot move to another train'); END;

-- Rows below a gate, a run, a communication template or an export job; freeze overrides
CREATE TRIGGER trg_Ws_ChecklistTasks_Insert BEFORE INSERT ON ChecklistTasks
BEGIN
    SELECT RAISE(ABORT, 'The team belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Teams x JOIN StageGates g ON g.Id = NEW.StageGateId JOIN ReleaseTrains p ON p.Id = g.ReleaseTrainId
                    WHERE x.Id = NEW.OwnerTeamId AND x.WorkspaceId IS NOT p.WorkspaceId);
    SELECT RAISE(ABORT, 'The product belongs to another train')
     WHERE EXISTS (SELECT 1 FROM BundledProducts x JOIN StageGates g ON g.Id = NEW.StageGateId
                    WHERE x.Id = NEW.BundledProductId AND x.ReleaseTrainId IS NOT g.ReleaseTrainId);
END;
CREATE TRIGGER trg_Ws_ChecklistTasks_Update BEFORE UPDATE OF StageGateId, OwnerTeamId, BundledProductId ON ChecklistTasks
BEGIN
    SELECT RAISE(ABORT, 'A row cannot move to another train')
     WHERE EXISTS (SELECT 1 FROM StageGates x JOIN StageGates o ON o.Id = OLD.StageGateId
                    WHERE x.Id = NEW.StageGateId AND x.ReleaseTrainId IS NOT o.ReleaseTrainId);
    SELECT RAISE(ABORT, 'The team belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Teams x JOIN StageGates g ON g.Id = NEW.StageGateId JOIN ReleaseTrains p ON p.Id = g.ReleaseTrainId
                    WHERE x.Id = NEW.OwnerTeamId AND x.WorkspaceId IS NOT p.WorkspaceId);
    SELECT RAISE(ABORT, 'The product belongs to another train')
     WHERE EXISTS (SELECT 1 FROM BundledProducts x JOIN StageGates g ON g.Id = NEW.StageGateId
                    WHERE x.Id = NEW.BundledProductId AND x.ReleaseTrainId IS NOT g.ReleaseTrainId);
END;
CREATE TRIGGER trg_Ws_StepExecutions_Insert BEFORE INSERT ON StepExecutions
BEGIN
    SELECT RAISE(ABORT, 'The step belongs to another train')
     WHERE EXISTS (SELECT 1 FROM RunbookSteps x JOIN RunbookRuns r ON r.Id = NEW.RunId
                    WHERE x.Id = NEW.StepId AND x.ReleaseTrainId IS NOT r.ReleaseTrainId);
END;
CREATE TRIGGER trg_Ws_StepExecutions_Update BEFORE UPDATE OF RunId, StepId ON StepExecutions
BEGIN
    SELECT RAISE(ABORT, 'The step belongs to another train')
     WHERE EXISTS (SELECT 1 FROM RunbookSteps x JOIN RunbookRuns r ON r.Id = NEW.RunId
                    WHERE x.Id = NEW.StepId AND x.ReleaseTrainId IS NOT r.ReleaseTrainId);
END;
CREATE TRIGGER trg_Ws_StepDependencies_Insert BEFORE INSERT ON StepDependencies
BEGIN
    SELECT RAISE(ABORT, 'The step belongs to another train')
     WHERE EXISTS (SELECT 1 FROM RunbookSteps x JOIN RunbookSteps s ON s.Id = NEW.StepId
                    WHERE x.Id = NEW.DependsOnStepId AND x.ReleaseTrainId IS NOT s.ReleaseTrainId);
END;
CREATE TRIGGER trg_Ws_StepDependencies_Update BEFORE UPDATE OF StepId, DependsOnStepId ON StepDependencies
BEGIN
    SELECT RAISE(ABORT, 'The step belongs to another train')
     WHERE EXISTS (SELECT 1 FROM RunbookSteps x JOIN RunbookSteps s ON s.Id = NEW.StepId
                    WHERE x.Id = NEW.DependsOnStepId AND x.ReleaseTrainId IS NOT s.ReleaseTrainId);
END;
CREATE TRIGGER trg_Ws_CommDispatches_Insert BEFORE INSERT ON CommDispatches
BEGIN
    SELECT RAISE(ABORT, 'The webhook destination belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM WebhookDestinations x JOIN CommTemplates c ON c.Id = NEW.CommTemplateId JOIN ReleaseTrains p ON p.Id = c.ReleaseTrainId
                    WHERE x.Id = NEW.WebhookDestinationId AND x.WorkspaceId IS NOT p.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_FreezeOverrides_Insert BEFORE INSERT ON FreezeOverrides
BEGIN
    SELECT RAISE(ABORT, 'The freeze window belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM FreezeWindows x JOIN ReleaseTrains p ON p.Id = NEW.ReleaseTrainId
                    WHERE x.Id = NEW.FreezeWindowId AND x.WorkspaceId IS NOT p.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_MetricSnapshots_Insert BEFORE INSERT ON MetricSnapshots
BEGIN
    SELECT RAISE(ABORT, 'The train belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM ReleaseTrains x JOIN ExportJobs p ON p.Id = NEW.ExportJobId
                    WHERE x.Id = NEW.ReleaseTrainId AND x.WorkspaceId IS NOT p.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_MetricSnapshots_Update BEFORE UPDATE OF ExportJobId, ReleaseTrainId ON MetricSnapshots
BEGIN
    SELECT RAISE(ABORT, 'The train belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM ReleaseTrains x JOIN ExportJobs p ON p.Id = NEW.ExportJobId
                    WHERE x.Id = NEW.ReleaseTrainId AND x.WorkspaceId IS NOT p.WorkspaceId);
END;

-- Release model, environments, services, deployments (REOS-107, 109, 110)
CREATE TRIGGER trg_Ws_ReleaseTypes_Insert BEFORE INSERT ON ReleaseTypes
BEGIN
    SELECT RAISE(ABORT, 'The template belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM TrainTemplates x WHERE x.Id = NEW.DefaultTemplateId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_ReleaseTypes_Update BEFORE UPDATE OF WorkspaceId, DefaultTemplateId ON ReleaseTypes
BEGIN
    SELECT RAISE(ABORT, 'A row cannot move to another workspace') WHERE NEW.WorkspaceId IS NOT OLD.WorkspaceId;
    SELECT RAISE(ABORT, 'The template belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM TrainTemplates x WHERE x.Id = NEW.DefaultTemplateId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_Environments_Insert BEFORE INSERT ON Environments
BEGIN
    SELECT RAISE(ABORT, 'The webhook destination belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM WebhookDestinations x WHERE x.Id = NEW.ControlWebhookDestinationId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_Environments_Update BEFORE UPDATE OF WorkspaceId, ControlWebhookDestinationId ON Environments
BEGIN
    SELECT RAISE(ABORT, 'A row cannot move to another workspace') WHERE NEW.WorkspaceId IS NOT OLD.WorkspaceId;
    SELECT RAISE(ABORT, 'The webhook destination belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM WebhookDestinations x WHERE x.Id = NEW.ControlWebhookDestinationId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_CatalogServices_Insert BEFORE INSERT ON CatalogServices
BEGIN
    SELECT RAISE(ABORT, 'The team belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Teams x WHERE x.Id = NEW.OwnerTeamId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_CatalogServices_Update BEFORE UPDATE OF WorkspaceId, OwnerTeamId ON CatalogServices
BEGIN
    SELECT RAISE(ABORT, 'A row cannot move to another workspace') WHERE NEW.WorkspaceId IS NOT OLD.WorkspaceId;
    SELECT RAISE(ABORT, 'The team belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Teams x WHERE x.Id = NEW.OwnerTeamId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_IngestKeys_Update BEFORE UPDATE OF WorkspaceId ON IngestKeys WHEN NEW.WorkspaceId IS NOT OLD.WorkspaceId
BEGIN SELECT RAISE(ABORT, 'A row cannot move to another workspace'); END;
CREATE TRIGGER trg_Ws_TrainEnvironments_Insert BEFORE INSERT ON TrainEnvironments
BEGIN
    SELECT RAISE(ABORT, 'The environment belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Environments x JOIN ReleaseTrains p ON p.Id = NEW.ReleaseTrainId WHERE x.Id = NEW.EnvironmentId AND x.WorkspaceId IS NOT p.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_TrainEnvironments_Update BEFORE UPDATE OF ReleaseTrainId, EnvironmentId ON TrainEnvironments
BEGIN
    SELECT RAISE(ABORT, 'A row cannot move to another train') WHERE NEW.ReleaseTrainId IS NOT OLD.ReleaseTrainId;
    SELECT RAISE(ABORT, 'The environment belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Environments x JOIN ReleaseTrains p ON p.Id = NEW.ReleaseTrainId WHERE x.Id = NEW.EnvironmentId AND x.WorkspaceId IS NOT p.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_BundledProducts_Insert BEFORE INSERT ON BundledProducts
BEGIN
    SELECT RAISE(ABORT, 'The catalog service belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM CatalogServices x JOIN ReleaseTrains p ON p.Id = NEW.ReleaseTrainId WHERE x.Id = NEW.CatalogServiceId AND x.WorkspaceId IS NOT p.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_Deployments_Insert BEFORE INSERT ON Deployments
BEGIN
    SELECT RAISE(ABORT, 'The service belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM CatalogServices x WHERE x.Id = NEW.CatalogServiceId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The environment belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Environments x WHERE x.Id = NEW.EnvironmentId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The train belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM ReleaseTrains x WHERE x.Id = NEW.ReleaseTrainId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The ingest key belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM IngestKeys x WHERE x.Id = NEW.IngestKeyId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The run belongs to another train')
     WHERE EXISTS (SELECT 1 FROM RunbookRuns x WHERE x.Id = NEW.RunId AND x.ReleaseTrainId IS NOT NEW.ReleaseTrainId);
END;
CREATE TRIGGER trg_Ws_Deployments_Update BEFORE UPDATE OF WorkspaceId, ExternalId, CatalogServiceId, EnvironmentId, ReleaseTrainId, RunId ON Deployments
BEGIN
    SELECT RAISE(ABORT, 'A row cannot move to another workspace') WHERE NEW.WorkspaceId IS NOT OLD.WorkspaceId;
    SELECT RAISE(ABORT, 'A deployment keeps its external id, service and environment')
     WHERE NEW.ExternalId IS NOT OLD.ExternalId OR NEW.CatalogServiceId IS NOT OLD.CatalogServiceId OR NEW.EnvironmentId IS NOT OLD.EnvironmentId;
    SELECT RAISE(ABORT, 'The train belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM ReleaseTrains x WHERE x.Id = NEW.ReleaseTrainId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The run belongs to another train')
     WHERE EXISTS (SELECT 1 FROM RunbookRuns x WHERE x.Id = NEW.RunId AND x.ReleaseTrainId IS NOT NEW.ReleaseTrainId);
END;

-- REOS-111 to 114: telemetry, control actions, approvals
CREATE TRIGGER trg_Ws_MetricSamples_Insert BEFORE INSERT ON MetricSamples
BEGIN
    SELECT RAISE(ABORT, 'The deployment belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Deployments x WHERE x.Id = NEW.DeploymentId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_MetricRollups_Insert BEFORE INSERT ON MetricRollups
BEGIN
    SELECT RAISE(ABORT, 'The deployment belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Deployments x WHERE x.Id = NEW.DeploymentId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_ThresholdBreaches_Insert BEFORE INSERT ON ThresholdBreaches
BEGIN
    SELECT RAISE(ABORT, 'The deployment belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Deployments x WHERE x.Id = NEW.DeploymentId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_ControlActions_Insert BEFORE INSERT ON DeploymentControlActions
BEGIN
    SELECT RAISE(ABORT, 'The deployment belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Deployments x WHERE x.Id = NEW.DeploymentId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The breach belongs to another deployment')
     WHERE EXISTS (SELECT 1 FROM ThresholdBreaches x WHERE x.Id = NEW.BreachId AND x.DeploymentId IS NOT NEW.DeploymentId);
END;
CREATE TRIGGER trg_Ws_MetricSamples_Update BEFORE UPDATE OF WorkspaceId ON MetricSamples WHEN NEW.WorkspaceId IS NOT OLD.WorkspaceId
BEGIN SELECT RAISE(ABORT, 'A row cannot move to another workspace'); END;
CREATE TRIGGER trg_Ws_MetricRollups_Update BEFORE UPDATE OF WorkspaceId ON MetricRollups WHEN NEW.WorkspaceId IS NOT OLD.WorkspaceId
BEGIN SELECT RAISE(ABORT, 'A row cannot move to another workspace'); END;
CREATE TRIGGER trg_Ws_ThresholdBreaches_Update BEFORE UPDATE OF WorkspaceId ON ThresholdBreaches WHEN NEW.WorkspaceId IS NOT OLD.WorkspaceId
BEGIN SELECT RAISE(ABORT, 'A row cannot move to another workspace'); END;
CREATE TRIGGER trg_Ws_DeploymentControlActions_Update BEFORE UPDATE OF WorkspaceId ON DeploymentControlActions WHEN NEW.WorkspaceId IS NOT OLD.WorkspaceId
BEGIN SELECT RAISE(ABORT, 'A row cannot move to another workspace'); END;
CREATE TRIGGER trg_Ws_ApprovalPolicies_Update BEFORE UPDATE OF WorkspaceId ON ApprovalPolicies WHEN NEW.WorkspaceId IS NOT OLD.WorkspaceId
BEGIN SELECT RAISE(ABORT, 'A row cannot move to another workspace'); END;
CREATE TRIGGER trg_Ws_PolicyRequirements_Insert BEFORE INSERT ON ApprovalPolicyRequirements
BEGIN
    SELECT RAISE(ABORT, 'The team belongs to another workspace')
     WHERE NEW.EligibleKind = 'Team'
       AND NOT EXISTS (SELECT 1 FROM Teams x JOIN ApprovalPolicies p ON p.Id = NEW.PolicyId WHERE x.Id = NEW.EligibleValue AND x.WorkspaceId = p.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_PolicyRequirements_Update BEFORE UPDATE OF PolicyId, EligibleKind, EligibleValue ON ApprovalPolicyRequirements
BEGIN
    SELECT RAISE(ABORT, 'The team belongs to another workspace')
     WHERE NEW.EligibleKind = 'Team'
       AND NOT EXISTS (SELECT 1 FROM Teams x JOIN ApprovalPolicies p ON p.Id = NEW.PolicyId WHERE x.Id = NEW.EligibleValue AND x.WorkspaceId = p.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_Approvals_Insert BEFORE INSERT ON Approvals
BEGIN
    SELECT RAISE(ABORT, 'The train belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM ReleaseTrains x WHERE x.Id = NEW.ReleaseTrainId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The environment belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM Environments x WHERE x.Id = NEW.EnvironmentId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_Approvals_Update BEFORE UPDATE OF WorkspaceId, ReleaseTrainId, EnvironmentId ON Approvals
BEGIN
    SELECT RAISE(ABORT, 'A row cannot move to another workspace') WHERE NEW.WorkspaceId IS NOT OLD.WorkspaceId;
    SELECT RAISE(ABORT, 'An approval keeps its release and environment')
     WHERE NEW.ReleaseTrainId IS NOT OLD.ReleaseTrainId OR NEW.EnvironmentId IS NOT OLD.EnvironmentId;
END;


-- The app's startup migration must see the new migrations as applied.
DELETE FROM __EFMigrationsHistory WHERE MigrationId IN ('20261023111406_Schema', '20261023111416_Triggers');
INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES
    ('20261023142626_Schema', '10.0.12'),
    ('20261023142643_Triggers', '10.0.12');

COMMIT;
PRAGMA foreign_keys=ON;
PRAGMA foreign_key_check;
PRAGMA integrity_check;
