-- Release Management App — SQLite schema v1 (revised after landscape + governance research)
-- Connection pragmas: journal_mode=WAL persists in the file once set; foreign_keys and busy_timeout
-- do NOT persist and must be set on every connection:
--   PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;
-- Ids: the app creates UUIDv7 TEXT ids. Rows created BY TRIGGERS (PIR, Baseline) use
-- lower(hex(randomblob(16))): a documented exception.
-- EF Core: tables with AFTER triggers need ToTable(t => t.UseSqlReturningClause(false)); apply it to every entity.
-- Conventions: ids are UUIDv7 TEXT; timestamps are UTC ISO-8601 TEXT 'YYYY-MM-DDTHH:MM:SSZ';
-- dates are 'YYYY-MM-DD'; booleans are INTEGER 0/1 with CHECK; every user-editable table has Version.
-- Version-exempt: append-only/immutable tables (AuditEvents, GateTransitions, Baselines, GoNoGoDecisions,
-- CommDispatches, FreezeOverrides, MetricSnapshots, TeamMembers) and system-owned rows with last-write-wins
-- by design (UserSessionState per tab, ParsePreviews, IcsTokens).
-- Trigger ORDER matters: SQLite fires same-event triggers newest-first. Apply them in file order.
-- Triggers are the backstop; the domain service is the primary enforcement point.

-- =====================================================================
-- 1. Identity and calendar
-- =====================================================================
CREATE TABLE Users (
    Id TEXT PRIMARY KEY,
    Email TEXT NOT NULL UNIQUE COLLATE NOCASE,
    DisplayName TEXT NOT NULL,
    Role TEXT NOT NULL CHECK (Role IN ('Viewer','ReleaseManager','RTE','GovernanceOfficer')),
    Handle TEXT UNIQUE COLLATE NOCASE,                   -- optional @handle for the bulk parser
    IsActive INTEGER NOT NULL DEFAULT 1 CHECK (IsActive IN (0,1)),
    Version INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE Teams (
    Id TEXT PRIMARY KEY,
    Handle TEXT NOT NULL UNIQUE COLLATE NOCASE,          -- @handle in the bulk parser and CSV
    Name TEXT NOT NULL,
    WebhookDestinationId TEXT REFERENCES WebhookDestinations(Id),   -- team channel for notifications
    Version INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE TeamMembers (
    TeamId TEXT NOT NULL REFERENCES Teams(Id) ON DELETE CASCADE,
    UserId TEXT NOT NULL REFERENCES Users(Id) ON DELETE CASCADE,
    PRIMARY KEY (TeamId, UserId)
);

CREATE TABLE Holidays (                                   -- business-day offset calendar (D9)
    Day TEXT PRIMARY KEY,
    Name TEXT NOT NULL,
    Version INTEGER NOT NULL DEFAULT 1
);

-- =====================================================================
-- 2. Templates (governed: Draft -> Approved -> Retired, with re-review date)
-- =====================================================================
CREATE TABLE TrainTemplates (
    Id TEXT PRIMARY KEY,
    Name TEXT NOT NULL UNIQUE,
    Status TEXT NOT NULL DEFAULT 'Draft' CHECK (Status IN ('Draft','Approved','Retired')),
    DefaultRiskTier TEXT NOT NULL DEFAULT 'Moderate' CHECK (DefaultRiskTier IN ('Low','Moderate','High','VeryHigh')),
    ApprovedByUserId TEXT REFERENCES Users(Id),
    ApprovedAt TEXT,
    ReviewDueOn TEXT,                                     -- re-review cycle (default +12 months on approval)
    Version INTEGER NOT NULL DEFAULT 1,
    CHECK ((Status = 'Approved') = (ApprovedByUserId IS NOT NULL AND ApprovedAt IS NOT NULL))
);

CREATE TABLE TemplateGates (
    Id TEXT PRIMARY KEY,
    TemplateId TEXT NOT NULL REFERENCES TrainTemplates(Id) ON DELETE CASCADE,
    GateName TEXT NOT NULL,
    GateClass TEXT NOT NULL DEFAULT 'Standard' CHECK (GateClass IN ('Standard','Compliance')),
    SequenceOrder INTEGER NOT NULL,
    OffsetDays INTEGER NOT NULL,                          -- business days before target (T-5 => 5)
    RequiredBeforeStatus TEXT NOT NULL CHECK (RequiredBeforeStatus IN ('Gated','Executing','Complete')),
    OwnerTeamId TEXT REFERENCES Teams(Id),
    Version INTEGER NOT NULL DEFAULT 1,
    UNIQUE (TemplateId, SequenceOrder)
);

CREATE TABLE TemplateSteps (                              -- runbook skeleton
    Id TEXT PRIMARY KEY,
    TemplateId TEXT NOT NULL REFERENCES TrainTemplates(Id) ON DELETE CASCADE,
    StepCode TEXT NOT NULL,
    Section TEXT NOT NULL CHECK (Section IN ('PreCheck','Deploy','Verify','Rollback','Hypercare')),
    Title TEXT NOT NULL,
    OffsetMinutes INTEGER NOT NULL,                       -- from deployment window start
    PlannedDurationMin INTEGER NOT NULL CHECK (PlannedDurationMin > 0),
    OwnerTeamId TEXT REFERENCES Teams(Id),
    Version INTEGER NOT NULL DEFAULT 1,
    UNIQUE (TemplateId, StepCode)
);

CREATE TABLE TemplateCommSchedule (                       -- T-minus plan
    Id TEXT PRIMARY KEY,
    TemplateId TEXT NOT NULL REFERENCES TrainTemplates(Id) ON DELETE CASCADE,
    LibraryTemplateId TEXT NOT NULL REFERENCES CommTemplateLibrary(Id),
    OffsetDays INTEGER NOT NULL,                          -- negative = before target (T-7 => -7), positive = after
    Version INTEGER NOT NULL DEFAULT 1,
    UNIQUE (TemplateId, LibraryTemplateId, OffsetDays)
);

CREATE TABLE CommTemplateLibrary (
    Id TEXT PRIMARY KEY,
    TemplateType TEXT NOT NULL,                           -- Tminus28, GoNoGo, Cutover, Complete, HypercareExit, ...
    Name TEXT NOT NULL UNIQUE,
    Audience TEXT NOT NULL,                               -- Exec, Ops, SupportDesk, Clients, All
    SubjectLine TEXT NOT NULL,
    MarkdownBody TEXT NOT NULL,                           -- tokens {ReleaseTitle} {BlockerCount} ... (allowlisted)
    Version INTEGER NOT NULL DEFAULT 1
);

-- =====================================================================
-- 3. Trains
-- =====================================================================
CREATE TABLE ReleaseTrains (
    Id TEXT PRIMARY KEY,
    Title TEXT NOT NULL,
    TemplateId TEXT REFERENCES TrainTemplates(Id),
    ClonedFromTrainId TEXT REFERENCES ReleaseTrains(Id),
    TargetReleaseDate TEXT NOT NULL,                      -- the CERT date; gate offsets hang off it
    RiskTier TEXT NOT NULL DEFAULT 'Moderate' CHECK (RiskTier IN ('Low','Moderate','High','VeryHigh')),
    CurrentStatus TEXT NOT NULL DEFAULT 'Planning'
        CHECK (CurrentStatus IN ('Planning','Gated','Executing','Complete','Aborted')),
    ChangeTicketNumber TEXT,                              -- primary CHG reference
    ActualStartAt TEXT,
    ActualEndAt TEXT,
    CloseCode TEXT CHECK (CloseCode IN ('Successful','SuccessfulWithIssues','Unsuccessful')),
    CloseNotes TEXT,
    RollbackRehearsedAt TEXT,
    RollbackRehearsedByUserId TEXT REFERENCES Users(Id),
    HypercareExitAt TEXT,
    HypercareExitByUserId TEXT REFERENCES Users(Id),
    ArchivedAt TEXT,
    LastChangedByUserId TEXT REFERENCES Users(Id),
    LastChangedAt TEXT,                                   -- service clock; used by lifecycle triggers
    Version INTEGER NOT NULL DEFAULT 1,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    CHECK ((CurrentStatus = 'Complete') = (CloseCode IS NOT NULL))
);

CREATE TABLE ChangeRecords (                              -- 1:1 with the train; the audit field set
    ReleaseTrainId TEXT PRIMARY KEY REFERENCES ReleaseTrains(Id) ON DELETE CASCADE,
    Justification TEXT,
    ImplementationPlan TEXT,
    RiskImpactAnalysis TEXT,
    BackoutPlan TEXT,
    TestPlan TEXT,
    CommunicationPlan TEXT,
    CabDate TEXT,
    Version INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE AffectedCIs (
    Id TEXT PRIMARY KEY,
    ReleaseTrainId TEXT NOT NULL REFERENCES ReleaseTrains(Id) ON DELETE CASCADE,
    CiName TEXT NOT NULL,
    CiExternalId TEXT,                                    -- CMDB sys_id when known
    Version INTEGER NOT NULL DEFAULT 1,
    UNIQUE (ReleaseTrainId, CiName)
);

CREATE TABLE DeploymentWindows (                          -- exactly one per train in v1
    Id TEXT PRIMARY KEY,
    ReleaseTrainId TEXT NOT NULL UNIQUE REFERENCES ReleaseTrains(Id) ON DELETE CASCADE,
    StartsAt TEXT NOT NULL,
    EndsAt TEXT NOT NULL,
    Version INTEGER NOT NULL DEFAULT 1,
    CHECK (EndsAt > StartsAt)
);

CREATE TABLE BundledProducts (
    Id TEXT PRIMARY KEY,
    ReleaseTrainId TEXT NOT NULL REFERENCES ReleaseTrains(Id) ON DELETE CASCADE,
    ProductName TEXT NOT NULL,
    VersionTag TEXT NOT NULL,
    ProjectCode TEXT NOT NULL,                            -- Jira project key or app code
    Version INTEGER NOT NULL DEFAULT 1,
    UNIQUE (ReleaseTrainId, ProductName)
);

-- =====================================================================
-- 4. Gates, tasks, waivers, evidence
-- =====================================================================
CREATE TABLE StageGates (
    Id TEXT PRIMARY KEY,
    ReleaseTrainId TEXT NOT NULL REFERENCES ReleaseTrains(Id) ON DELETE CASCADE,
    GateName TEXT NOT NULL,
    GateClass TEXT NOT NULL DEFAULT 'Standard' CHECK (GateClass IN ('Standard','Compliance')),
    SequenceOrder INTEGER NOT NULL,
    OffsetDays INTEGER NOT NULL,
    DueOn TEXT NOT NULL,                                  -- computed: TargetReleaseDate - OffsetDays business days
    RequiredBeforeStatus TEXT NOT NULL DEFAULT 'Gated'
        CHECK (RequiredBeforeStatus IN ('Gated','Executing','Complete')),
    OwnerUserId TEXT REFERENCES Users(Id),
    OwnerTeamId TEXT REFERENCES Teams(Id),
    Status TEXT NOT NULL DEFAULT 'Pending'
        CHECK (Status IN ('Pending','InProgress','Certified','Failed','Waived')),
    CertifiedByUserId TEXT REFERENCES Users(Id),
    CertifiedAt TEXT,
    LastChangedByUserId TEXT REFERENCES Users(Id),        -- service sets on every write; trigger logs it
    LastChangedAt TEXT,                                   -- service sets from its clock (test clock in tests)
    Version INTEGER NOT NULL DEFAULT 1,
    UNIQUE (ReleaseTrainId, SequenceOrder),
    CHECK ((OwnerUserId IS NULL) <> (OwnerTeamId IS NULL)),
    CHECK ((Status IN ('Certified','Waived')) = (CertifiedByUserId IS NOT NULL AND CertifiedAt IS NOT NULL))
);

CREATE TABLE GateTransitions (                            -- append-only; source for gate cycle time
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    StageGateId TEXT NOT NULL REFERENCES StageGates(Id) ON DELETE CASCADE,
    FromStatus TEXT NOT NULL,
    ToStatus TEXT NOT NULL,
    ActorUserId TEXT REFERENCES Users(Id),
    OccurredAt TEXT NOT NULL
);

CREATE TABLE GateWaivers (
    Id TEXT PRIMARY KEY,
    StageGateId TEXT NOT NULL REFERENCES StageGates(Id) ON DELETE CASCADE,
    Reason TEXT NOT NULL CHECK (length(Reason) >= 20),
    RequestedByUserId TEXT NOT NULL REFERENCES Users(Id),
    ApprovedByUserId TEXT REFERENCES Users(Id),
    RequestedAt TEXT NOT NULL,
    ApprovedAt TEXT,
    Version INTEGER NOT NULL DEFAULT 1,
    CHECK (ApprovedByUserId IS NULL OR ApprovedByUserId <> RequestedByUserId)   -- D10 two distinct people
);

CREATE TABLE ChecklistTasks (
    Id TEXT PRIMARY KEY,
    StageGateId TEXT NOT NULL REFERENCES StageGates(Id) ON DELETE CASCADE,
    BundledProductId TEXT REFERENCES BundledProducts(Id) ON DELETE SET NULL,
    TaskDescription TEXT NOT NULL,
    OwnerUserId TEXT REFERENCES Users(Id),
    OwnerTeamId TEXT REFERENCES Teams(Id),
    IsCompleted INTEGER NOT NULL DEFAULT 0 CHECK (IsCompleted IN (0,1)),
    SequenceOrder INTEGER NOT NULL,
    CompletedAt TEXT,
    CompletedByUserId TEXT REFERENCES Users(Id),
    LastChangedByUserId TEXT REFERENCES Users(Id),        -- actor for cascaded decertify
    LastChangedAt TEXT,
    Version INTEGER NOT NULL DEFAULT 1,
    CHECK ((OwnerUserId IS NULL) <> (OwnerTeamId IS NULL)),
    CHECK ((IsCompleted = 1) = (CompletedAt IS NOT NULL AND CompletedByUserId IS NOT NULL))
);

CREATE TABLE Attachments (                                -- evidence; locked once its gate is certified
    Id TEXT PRIMARY KEY,
    ReleaseTrainId TEXT NOT NULL REFERENCES ReleaseTrains(Id) ON DELETE CASCADE,
    EntityType TEXT NOT NULL CHECK (EntityType IN ('Train','Gate','Task','RunbookStep','Blocker','GoNoGo','PIR')),
    EntityId TEXT NOT NULL,
    FileName TEXT NOT NULL,
    ContentType TEXT NOT NULL,
    SizeBytes INTEGER NOT NULL CHECK (SizeBytes > 0 AND SizeBytes <= 52428800),   -- 50 MB
    Sha256 TEXT NOT NULL CHECK (length(Sha256) = 64),
    StoragePath TEXT NOT NULL UNIQUE,
    UploadedByUserId TEXT NOT NULL REFERENCES Users(Id),
    UploadedAt TEXT NOT NULL,
    IsLocked INTEGER NOT NULL DEFAULT 0 CHECK (IsLocked IN (0,1))
);

-- =====================================================================
-- 5. Runbook: plan (steps) vs executions (per run; rehearsal or live)
-- =====================================================================
CREATE TABLE RunbookSteps (
    Id TEXT PRIMARY KEY,
    ReleaseTrainId TEXT NOT NULL REFERENCES ReleaseTrains(Id) ON DELETE CASCADE,
    BundledProductId TEXT REFERENCES BundledProducts(Id) ON DELETE SET NULL,
    StepCode TEXT NOT NULL,                               -- 'R-014', stable for dependency refs and CSV
    Section TEXT NOT NULL DEFAULT 'Deploy' CHECK (Section IN ('PreCheck','Deploy','Verify','Rollback','Hypercare')),
    Title TEXT NOT NULL,
    Instructions TEXT,
    OwnerUserId TEXT REFERENCES Users(Id),
    OwnerTeamId TEXT REFERENCES Teams(Id),
    PlannedStartAt TEXT NOT NULL,
    PlannedDurationMin INTEGER NOT NULL CHECK (PlannedDurationMin > 0),
    Version INTEGER NOT NULL DEFAULT 1,
    UNIQUE (ReleaseTrainId, StepCode),
    CHECK ((OwnerUserId IS NULL) <> (OwnerTeamId IS NULL))
);

CREATE TABLE StepDependencies (
    StepId TEXT NOT NULL REFERENCES RunbookSteps(Id) ON DELETE CASCADE,
    DependsOnStepId TEXT NOT NULL REFERENCES RunbookSteps(Id) ON DELETE CASCADE,
    PRIMARY KEY (StepId, DependsOnStepId),
    CHECK (StepId <> DependsOnStepId)                     -- longer cycles rejected in the domain service
);

CREATE TABLE RunbookRuns (
    Id TEXT PRIMARY KEY,
    ReleaseTrainId TEXT NOT NULL REFERENCES ReleaseTrains(Id) ON DELETE CASCADE,
    Mode TEXT NOT NULL CHECK (Mode IN ('Rehearsal','Live')),
    StartedAt TEXT NOT NULL,
    StartedByUserId TEXT NOT NULL REFERENCES Users(Id),
    EndedAt TEXT,
    Outcome TEXT CHECK (Outcome IN ('Completed','RolledBack','Aborted')),
    Version INTEGER NOT NULL DEFAULT 1
);
CREATE UNIQUE INDEX UX_RunbookRuns_OneOpenLive ON RunbookRuns(ReleaseTrainId) WHERE Mode = 'Live' AND EndedAt IS NULL;

CREATE TABLE StepExecutions (                             -- actuals never overwrite the plan
    Id TEXT PRIMARY KEY,
    RunId TEXT NOT NULL REFERENCES RunbookRuns(Id) ON DELETE CASCADE,
    StepId TEXT NOT NULL REFERENCES RunbookSteps(Id) ON DELETE CASCADE,
    Status TEXT NOT NULL DEFAULT 'Scheduled' CHECK (Status IN ('Scheduled','Running','Done','Failed','Skipped')),
    ActualStartAt TEXT,
    ActualEndAt TEXT,
    ActorUserId TEXT REFERENCES Users(Id),
    Note TEXT,
    Version INTEGER NOT NULL DEFAULT 1,
    UNIQUE (RunId, StepId),
    CHECK ((Status IN ('Running','Done','Failed')) = (ActualStartAt IS NOT NULL)),
    CHECK ((Status IN ('Done','Failed')) = (ActualEndAt IS NOT NULL)),
    CHECK (Status <> 'Skipped' OR Note IS NOT NULL)      -- skip requires a comment
);

-- =====================================================================
-- 6. Blockers, known issues, go/no-go, PIR
-- =====================================================================
CREATE TABLE Blockers (
    Id TEXT PRIMARY KEY,
    ReleaseTrainId TEXT NOT NULL REFERENCES ReleaseTrains(Id) ON DELETE CASCADE,
    BundledProductId TEXT REFERENCES BundledProducts(Id) ON DELETE SET NULL,
    StageGateId TEXT REFERENCES StageGates(Id) ON DELETE SET NULL,
    Title TEXT NOT NULL,
    Severity TEXT NOT NULL CHECK (Severity IN ('Critical','High','Medium','Low')),
    OwnerUserId TEXT REFERENCES Users(Id),
    RaisedAt TEXT NOT NULL,
    ResolvedAt TEXT,
    Version INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE KnownIssues (                                -- hypercare log
    Id TEXT PRIMARY KEY,
    ReleaseTrainId TEXT NOT NULL REFERENCES ReleaseTrains(Id) ON DELETE CASCADE,
    Title TEXT NOT NULL,
    Severity TEXT NOT NULL CHECK (Severity IN ('Critical','High','Medium','Low')),
    Workaround TEXT,
    Status TEXT NOT NULL DEFAULT 'Open' CHECK (Status IN ('Open','Accepted','Resolved')),
    ExternalKey TEXT,                                     -- incident/defect reference
    RaisedAt TEXT NOT NULL,
    ResolvedAt TEXT,
    Version INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE GoNoGoDecisions (
    Id TEXT PRIMARY KEY,
    ReleaseTrainId TEXT NOT NULL REFERENCES ReleaseTrains(Id) ON DELETE CASCADE,
    Decision TEXT NOT NULL CHECK (Decision IN ('Go','NoGo','GoWithConditions')),
    DecidedByUserId TEXT NOT NULL REFERENCES Users(Id),
    DecidedAt TEXT NOT NULL,
    GateSnapshotJson TEXT NOT NULL CHECK (json_valid(GateSnapshotJson)),   -- gate states at the moment of decision
    Notes TEXT,
    NewTargetReleaseDate TEXT                              -- on NoGo
);

CREATE TABLE GoNoGoConditions (
    Id TEXT PRIMARY KEY,
    DecisionId TEXT NOT NULL REFERENCES GoNoGoDecisions(Id) ON DELETE CASCADE,
    Text TEXT NOT NULL,
    OwnerUserId TEXT NOT NULL REFERENCES Users(Id),
    ExpiresAt TEXT NOT NULL,                              -- conditions must expire
    ClosedAt TEXT,
    ClosedByUserId TEXT REFERENCES Users(Id),
    Version INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE PostImplementationReviews (
    Id TEXT PRIMARY KEY,
    ReleaseTrainId TEXT NOT NULL UNIQUE REFERENCES ReleaseTrains(Id) ON DELETE CASCADE,
    RequiredReason TEXT NOT NULL,                         -- 'CloseCode=Unsuccessful', 'RollbackPerformed', 'Manual'
    Status TEXT NOT NULL DEFAULT 'Required' CHECK (Status IN ('Required','Scheduled','Held','Closed')),
    HeldAt TEXT,
    Summary TEXT,
    Version INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE PirActions (
    Id TEXT PRIMARY KEY,
    PirId TEXT NOT NULL REFERENCES PostImplementationReviews(Id) ON DELETE CASCADE,
    Text TEXT NOT NULL,
    OwnerUserId TEXT NOT NULL REFERENCES Users(Id),
    DueOn TEXT NOT NULL,
    DoneAt TEXT,
    Version INTEGER NOT NULL DEFAULT 1
);

-- =====================================================================
-- 7. Freeze windows and baselines
-- =====================================================================
CREATE TABLE FreezeWindows (
    Id TEXT PRIMARY KEY,
    Name TEXT NOT NULL,
    Kind TEXT NOT NULL DEFAULT 'Freeze' CHECK (Kind IN ('Freeze','Chill')),
    StartsAt TEXT NOT NULL,
    EndsAt TEXT NOT NULL,
    ProductPattern TEXT,                                  -- NULL = all products; else GLOB on ProductName
    CreatedByUserId TEXT NOT NULL REFERENCES Users(Id),
    Version INTEGER NOT NULL DEFAULT 1,
    CHECK (EndsAt > StartsAt)
);

CREATE TABLE FreezeOverrides (
    Id TEXT PRIMARY KEY,
    FreezeWindowId TEXT NOT NULL REFERENCES FreezeWindows(Id) ON DELETE CASCADE,
    ReleaseTrainId TEXT NOT NULL REFERENCES ReleaseTrains(Id) ON DELETE CASCADE,
    Reason TEXT NOT NULL CHECK (length(Reason) >= 20),
    RequestedByUserId TEXT NOT NULL REFERENCES Users(Id),
    ApprovedByUserId TEXT NOT NULL REFERENCES Users(Id),
    ApprovedAt TEXT NOT NULL,
    ExpiresAt TEXT NOT NULL,                              -- TTL; no permanent exceptions
    CHECK (ApprovedByUserId <> RequestedByUserId),
    CHECK (ExpiresAt > ApprovedAt)                        -- renewal = a new row; lockout accepts any unexpired row
);

CREATE TABLE Baselines (                                  -- immutable snapshot at Code Freeze (D15)
    Id TEXT PRIMARY KEY,
    ReleaseTrainId TEXT NOT NULL UNIQUE REFERENCES ReleaseTrains(Id) ON DELETE CASCADE,   -- one per train in v1
    CapturedAt TEXT NOT NULL,
    CapturedAtGateId TEXT REFERENCES StageGates(Id),
    PlannedReleaseDate TEXT NOT NULL,
    SnapshotJson TEXT NOT NULL CHECK (json_valid(SnapshotJson)),   -- products, gates+DueOn, steps+planned times
    CapturedByUserId TEXT REFERENCES Users(Id)
);

-- =====================================================================
-- 8. Integrations and communications
-- =====================================================================
CREATE TABLE ExternalLinks (
    Id TEXT PRIMARY KEY,
    ReleaseTrainId TEXT NOT NULL REFERENCES ReleaseTrains(Id) ON DELETE CASCADE,
    EntityType TEXT NOT NULL CHECK (EntityType IN ('Train','Product','Gate','RunbookStep','Blocker','KnownIssue')),
    EntityId TEXT NOT NULL,
    SourceSystem TEXT NOT NULL CHECK (SourceSystem IN ('Jira','ServiceNow')),
    ExternalKey TEXT NOT NULL,
    ExpectedStatus TEXT,                                  -- what this app's state implies
    LastSyncedStatus TEXT,                                -- what the source system reports
    LastSyncedAt TEXT,
    SyncState TEXT NOT NULL DEFAULT 'Unsynced'
        CHECK (SyncState IN ('Unsynced','InSync','Mismatch','NotFound','AuthFailed')),
    Version INTEGER NOT NULL DEFAULT 1,
    UNIQUE (SourceSystem, ExternalKey, EntityType, EntityId)
);

CREATE TABLE SyncAlerts (
    Id TEXT PRIMARY KEY,
    ReleaseTrainId TEXT REFERENCES ReleaseTrains(Id) ON DELETE CASCADE,  -- NULL = system-wide. Every background failure lands here
    SourceSystem TEXT NOT NULL CHECK (SourceSystem IN ('Jira','ServiceNow','SyncEngine','Backup','Export','Webhook','Notifications')),
    Kind TEXT NOT NULL CHECK (Kind IN ('AuthFailed','Unreachable','NotFound','Mismatch','RateLimited','ParseError','Stalled','BackupFailed','ExportFailed','DeliveryFailed')),
    Fingerprint TEXT NOT NULL,                            -- hash(system, kind, key); dedups repeats
    ErrorMessage TEXT NOT NULL,
    OccurrenceCount INTEGER NOT NULL DEFAULT 1,
    FirstOccurredAt TEXT NOT NULL,
    LastOccurredAt TEXT NOT NULL,
    IsResolved INTEGER NOT NULL DEFAULT 0 CHECK (IsResolved IN (0,1)),
    ResolvedAt TEXT,
    ResolvedByUserId TEXT REFERENCES Users(Id),
    Version INTEGER NOT NULL DEFAULT 1
);
CREATE UNIQUE INDEX UX_SyncAlerts_OpenFingerprint ON SyncAlerts(Fingerprint) WHERE IsResolved = 0;

CREATE TABLE WebhookDestinations (                       -- the URL's path is its token (Teams, Slack): never stored in clear (Q-053e)
    Id TEXT PRIMARY KEY,
    Name TEXT NOT NULL,
    Host TEXT NOT NULL CHECK (length(Host) BETWEEN 1 AND 300 AND Host NOT GLOB '*[/?#@ ]*'),   -- host[:port] only: what screens, alerts and audit rows show
    ProtectedUrl TEXT NOT NULL CHECK (ProtectedUrl LIKE 'CfDJ8%'),   -- ASP.NET Data Protection payload of the https URL; decrypted only by the senders
    UrlHmac TEXT NOT NULL UNIQUE CHECK (length(UrlHmac) = 64 AND UrlHmac NOT GLOB '*[^0-9a-f]*'),   -- HMAC-SHA256 (hex) of the normalised URL: one row per address
    Kind TEXT NOT NULL CHECK (Kind IN ('Teams','Slack','Generic')),
    Version INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE CommTemplates (                              -- per-train copy, editable
    Id TEXT PRIMARY KEY,
    ReleaseTrainId TEXT NOT NULL REFERENCES ReleaseTrains(Id) ON DELETE CASCADE,
    LibraryTemplateId TEXT REFERENCES CommTemplateLibrary(Id) ON DELETE SET NULL,
    StageGateId TEXT REFERENCES StageGates(Id) ON DELETE SET NULL,
    TemplateType TEXT NOT NULL,
    Audience TEXT NOT NULL,
    SubjectLine TEXT NOT NULL,
    MarkdownBody TEXT NOT NULL,
    Version INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE CommSchedule (                               -- T-minus items; SentAt feeds the timeliness KPI
    Id TEXT PRIMARY KEY,
    ReleaseTrainId TEXT NOT NULL REFERENCES ReleaseTrains(Id) ON DELETE CASCADE,
    CommTemplateId TEXT NOT NULL REFERENCES CommTemplates(Id) ON DELETE CASCADE,
    DueAt TEXT NOT NULL,
    SentAt TEXT,
    DispatchId TEXT REFERENCES CommDispatches(Id),
    Version INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE CommDispatches (
    Id TEXT PRIMARY KEY,
    CommTemplateId TEXT NOT NULL REFERENCES CommTemplates(Id) ON DELETE CASCADE,
    Channel TEXT NOT NULL CHECK (Channel IN ('Copy','Mailto','Webhook')),
    WebhookDestinationId TEXT REFERENCES WebhookDestinations(Id),
    HydratedSubject TEXT NOT NULL,
    HydratedBody TEXT NOT NULL,                           -- exact text sent, frozen for audit
    DispatchedByUserId TEXT NOT NULL REFERENCES Users(Id),
    DispatchedAt TEXT NOT NULL,
    IsRehearsal INTEGER NOT NULL DEFAULT 0 CHECK (IsRehearsal IN (0,1)),
    Outcome TEXT NOT NULL CHECK (Outcome IN ('Handed','Delivered','Failed')),
    CHECK ((Channel = 'Webhook') = (WebhookDestinationId IS NOT NULL))
);

CREATE TABLE Notifications (                              -- in-app inbox; escalation ladder
    Id TEXT PRIMARY KEY,
    UserId TEXT NOT NULL REFERENCES Users(Id) ON DELETE CASCADE,
    Kind TEXT NOT NULL,                                   -- GateEntered, GateReminder, GateOverdue, StepLate, SyncAlert, ...
    EntityType TEXT NOT NULL,
    EntityId TEXT NOT NULL,
    EscalationLevel INTEGER NOT NULL DEFAULT 0 CHECK (EscalationLevel BETWEEN 0 AND 2),
    Message TEXT NOT NULL,
    CreatedAt TEXT NOT NULL,
    ReadAt TEXT,
    Version INTEGER NOT NULL DEFAULT 1
);

-- =====================================================================
-- 9. Session state, imports/exports, analytics snapshots, audit
-- =====================================================================
CREATE TABLE UserSessionState (
    UserId TEXT NOT NULL REFERENCES Users(Id) ON DELETE CASCADE,
    ClientId TEXT NOT NULL,                               -- per browser tab, from sessionStorage
    ActiveTrainId TEXT REFERENCES ReleaseTrains(Id) ON DELETE SET NULL,
    SchemaVersion INTEGER NOT NULL,
    UIStateJson TEXT NOT NULL CHECK (json_valid(UIStateJson) AND length(UIStateJson) <= 262144),
    LastActivityAt TEXT NOT NULL,
    PRIMARY KEY (UserId, ClientId)
);

CREATE TABLE ImportJobs (
    Id TEXT PRIMARY KEY,
    Kind TEXT NOT NULL CHECK (Kind IN ('Trains','Gates','Tasks','RunbookSteps','Products','Holidays','Users','Teams','ExternalLinks')),
    ReleaseTrainId TEXT REFERENCES ReleaseTrains(Id) ON DELETE SET NULL,
    FileName TEXT NOT NULL,
    Sha256 TEXT NOT NULL,
    RowCount INTEGER NOT NULL,
    ErrorCount INTEGER NOT NULL DEFAULT 0,
    ErrorsJson TEXT NOT NULL DEFAULT '[]' CHECK (json_valid(ErrorsJson)),   -- [{row, column, message}]
    Status TEXT NOT NULL CHECK (Status IN ('Previewed','Committed','Rejected','Expired')),
    UploadedByUserId TEXT NOT NULL REFERENCES Users(Id),
    CreatedAt TEXT NOT NULL,
    CommittedAt TEXT,
    Version INTEGER NOT NULL DEFAULT 1,
    CHECK (Status <> 'Committed' OR ErrorCount = 0)      -- a batch with any error never commits
);

CREATE TABLE ExportJobs (
    Id TEXT PRIMARY KEY,
    Kind TEXT NOT NULL CHECK (Kind IN ('ReleaseReportPdf','RunbookPdf','EvidencePackPdf','ScorecardPdf','Csv','Xlsx','Ics')),
    ReleaseTrainId TEXT REFERENCES ReleaseTrains(Id) ON DELETE SET NULL,
    Parameters TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(Parameters)),
    FileName TEXT NOT NULL,
    Sha256 TEXT,
    StoragePath TEXT,
    RequestedByUserId TEXT NOT NULL REFERENCES Users(Id),
    CreatedAt TEXT NOT NULL,
    CompletedAt TEXT,
    Status TEXT NOT NULL DEFAULT 'Queued' CHECK (Status IN ('Queued','Running','Done','Failed')),
    Version INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE MetricSnapshots (                            -- written only when an evidence pack is generated (D16)
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    ExportJobId TEXT NOT NULL REFERENCES ExportJobs(Id) ON DELETE CASCADE,
    ReleaseTrainId TEXT REFERENCES ReleaseTrains(Id) ON DELETE SET NULL,
    MetricKey TEXT NOT NULL,
    Value REAL NOT NULL,
    PeriodStart TEXT,
    PeriodEnd TEXT,
    CapturedAt TEXT NOT NULL
);

CREATE TABLE IcsTokens (                                  -- per-user calendar feed secret (D21)
    Id TEXT PRIMARY KEY,
    UserId TEXT NOT NULL REFERENCES Users(Id) ON DELETE CASCADE,
    TokenSha256 TEXT NOT NULL UNIQUE CHECK (length(TokenSha256) = 64),   -- store the hash, never the token
    CreatedAt TEXT NOT NULL,
    RevokedAt TEXT
);
CREATE UNIQUE INDEX UX_IcsTokens_ActivePerUser ON IcsTokens(UserId) WHERE RevokedAt IS NULL;

CREATE TABLE ParsePreviews (                              -- bulk-parser previews, valid 30 min
    Id TEXT PRIMARY KEY,
    ReleaseTrainId TEXT NOT NULL REFERENCES ReleaseTrains(Id) ON DELETE CASCADE,
    UserId TEXT NOT NULL REFERENCES Users(Id),
    TrainVersion INTEGER NOT NULL,                        -- commit refused (409) if the train moved
    ResultJson TEXT NOT NULL CHECK (json_valid(ResultJson)),
    ErrorCount INTEGER NOT NULL,
    CreatedAt TEXT NOT NULL,
    ExpiresAt TEXT NOT NULL,
    CommittedAt TEXT,
    CHECK (CommittedAt IS NULL OR ErrorCount = 0)
);

CREATE TABLE ConnectorState (                             -- poller bookkeeping; secrets live in Data Protection (D12)
    SourceSystem TEXT PRIMARY KEY CHECK (SourceSystem IN ('Jira','ServiceNow')),
    BaseUrl TEXT NOT NULL,
    IsEnabled INTEGER NOT NULL DEFAULT 1 CHECK (IsEnabled IN (0,1)),
    LastCycleStartedAt TEXT,
    LastCycleCompletedAt TEXT,
    LastSuccessAt TEXT,
    ConsecutiveFailures INTEGER NOT NULL DEFAULT 0,
    Version INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE AuditEvents (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    OccurredAt TEXT NOT NULL,
    ActorUserId TEXT,                                     -- NULL = system (scheduler, sync)
    ReleaseTrainId TEXT,
    EntityType TEXT NOT NULL,
    EntityId TEXT NOT NULL,
    Action TEXT NOT NULL,
    BeforeJson TEXT,
    AfterJson TEXT
);

-- =====================================================================
-- 10. Enforcement triggers
-- =====================================================================
-- Append-only tables
CREATE TRIGGER trg_Audit_NoUpdate BEFORE UPDATE ON AuditEvents BEGIN SELECT RAISE(ABORT, 'AuditEvents is append-only'); END;
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
    INSERT INTO AuditEvents(OccurredAt, ActorUserId, ReleaseTrainId, EntityType, EntityId, Action, AfterJson)
    VALUES (COALESCE(NEW.LastChangedAt, strftime('%Y-%m-%dT%H:%M:%SZ','now')), NEW.LastChangedByUserId, NEW.Id, 'PostImplementationReview',
            (SELECT Id FROM PostImplementationReviews WHERE ReleaseTrainId = NEW.Id),
            'AutoRequired', json_object('reason', 'CloseCode=' || NEW.CloseCode));
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
    INSERT INTO AuditEvents(OccurredAt, ActorUserId, ReleaseTrainId, EntityType, EntityId, Action)
    VALUES (COALESCE(NEW.LastChangedAt, strftime('%Y-%m-%dT%H:%M:%SZ','now')), NEW.CertifiedByUserId,
            NEW.ReleaseTrainId, 'Baseline', NEW.ReleaseTrainId, 'AutoCaptured');
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
WHEN NEW.Status IN ('Certified','Waived') AND NEW.GateClass = 'Compliance'
 AND (SELECT Role FROM Users WHERE Id = NEW.CertifiedByUserId) <> 'GovernanceOfficer'
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
    INSERT INTO AuditEvents(OccurredAt, ActorUserId, ReleaseTrainId, EntityType, EntityId, Action)
    SELECT COALESCE(NEW.LastChangedAt, strftime('%Y-%m-%dT%H:%M:%SZ','now')), NEW.LastChangedByUserId,
           NEW.ReleaseTrainId, 'StageGate', NEW.Id, 'EvidenceLocked'
    WHERE OLD.Status NOT IN ('Certified','Waived');
END;

-- Approver roles: waiver approver is a Governance Officer (D10); freeze-override approver is a
-- Release Manager or Governance Officer.
CREATE TRIGGER trg_Waiver_ApproverRole BEFORE UPDATE OF ApprovedByUserId ON GateWaivers
WHEN NEW.ApprovedByUserId IS NOT NULL
 AND (SELECT Role FROM Users WHERE Id = NEW.ApprovedByUserId) <> 'GovernanceOfficer'
BEGIN SELECT RAISE(ABORT, 'Waivers are approved by a Governance Officer'); END;

CREATE TRIGGER trg_Waiver_ApproverRoleInsert BEFORE INSERT ON GateWaivers
WHEN NEW.ApprovedByUserId IS NOT NULL
 AND (SELECT Role FROM Users WHERE Id = NEW.ApprovedByUserId) <> 'GovernanceOfficer'
BEGIN SELECT RAISE(ABORT, 'Waivers are approved by a Governance Officer'); END;

CREATE TRIGGER trg_Override_NoUpdate BEFORE UPDATE ON FreezeOverrides
BEGIN SELECT RAISE(ABORT, 'Freeze overrides are immutable; renew with a new row'); END;
CREATE TRIGGER trg_Override_NoDelete BEFORE DELETE ON FreezeOverrides
BEGIN SELECT RAISE(ABORT, 'Freeze overrides are immutable; renew with a new row'); END;

CREATE TRIGGER trg_Override_ApproverRole BEFORE INSERT ON FreezeOverrides
WHEN (SELECT Role FROM Users WHERE Id = NEW.ApprovedByUserId) NOT IN ('ReleaseManager','GovernanceOfficer')
BEGIN SELECT RAISE(ABORT, 'Freeze overrides are approved by a Release Manager or Governance Officer'); END;

-- Task reopen or insert decertifies its gate
CREATE TRIGGER trg_Task_ReopenDecertifies AFTER UPDATE OF IsCompleted ON ChecklistTasks
WHEN OLD.IsCompleted = 1 AND NEW.IsCompleted = 0
BEGIN
    INSERT INTO AuditEvents(OccurredAt, ActorUserId, ReleaseTrainId, EntityType, EntityId, Action)
    SELECT COALESCE(NEW.LastChangedAt, strftime('%Y-%m-%dT%H:%M:%SZ','now')), NEW.LastChangedByUserId,
           g.ReleaseTrainId, 'StageGate', g.Id, 'Decertified'
      FROM StageGates g WHERE g.Id = NEW.StageGateId AND g.Status = 'Certified';
    UPDATE StageGates SET Status = 'InProgress', CertifiedByUserId = NULL, CertifiedAt = NULL,
           LastChangedByUserId = NEW.LastChangedByUserId, LastChangedAt = NEW.LastChangedAt, Version = Version + 1
     WHERE Id = NEW.StageGateId AND Status = 'Certified';
END;

CREATE TRIGGER trg_Task_InsertDecertifies AFTER INSERT ON ChecklistTasks
WHEN NEW.IsCompleted = 0
BEGIN
    INSERT INTO AuditEvents(OccurredAt, ActorUserId, ReleaseTrainId, EntityType, EntityId, Action)
    SELECT COALESCE(NEW.LastChangedAt, strftime('%Y-%m-%dT%H:%M:%SZ','now')), NEW.LastChangedByUserId,
           g.ReleaseTrainId, 'StageGate', g.Id, 'Decertified'
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

-- =====================================================================
-- 11. Indexes
-- =====================================================================
CREATE INDEX IX_Trains_Status ON ReleaseTrains(CurrentStatus, TargetReleaseDate);
CREATE INDEX IX_Gates_Train ON StageGates(ReleaseTrainId, SequenceOrder);
CREATE INDEX IX_Gates_Owner ON StageGates(OwnerUserId, Status);
CREATE INDEX IX_GateTrans_Gate ON GateTransitions(StageGateId, OccurredAt);
CREATE INDEX IX_Tasks_Gate ON ChecklistTasks(StageGateId, SequenceOrder);
CREATE INDEX IX_Tasks_Owner ON ChecklistTasks(OwnerUserId, IsCompleted);
CREATE INDEX IX_Steps_Train ON RunbookSteps(ReleaseTrainId, PlannedStartAt);
CREATE INDEX IX_StepExec_Run ON StepExecutions(RunId, Status);
CREATE INDEX IX_Blockers_Open ON Blockers(ReleaseTrainId, Severity) WHERE ResolvedAt IS NULL;
CREATE INDEX IX_Links_Entity ON ExternalLinks(EntityType, EntityId);
CREATE INDEX IX_Links_State ON ExternalLinks(SyncState) WHERE SyncState <> 'InSync';
CREATE INDEX IX_Attach_Entity ON Attachments(EntityType, EntityId);
CREATE INDEX IX_Comm_Due ON CommSchedule(ReleaseTrainId, DueAt);
CREATE INDEX IX_Notif_User ON Notifications(UserId, ReadAt, CreatedAt);
CREATE INDEX IX_Freeze_Range ON FreezeWindows(StartsAt, EndsAt);
CREATE INDEX IX_Audit_Train ON AuditEvents(ReleaseTrainId, OccurredAt);
CREATE INDEX IX_Audit_Entity ON AuditEvents(EntityType, EntityId);
