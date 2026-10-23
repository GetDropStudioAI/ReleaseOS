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
-- by design (UserSessionState per tab, ParsePreviews, IcsTokens, SessionRevocations).
-- Trigger ORDER matters: SQLite fires same-event triggers newest-first. Apply them in file order.
-- Triggers are the backstop; the domain service is the primary enforcement point.

-- =====================================================================
-- 1. Identity and calendar
-- =====================================================================
CREATE TABLE Users (
    Id TEXT PRIMARY KEY,
    Email TEXT NOT NULL UNIQUE COLLATE NOCASE,          -- contact and display data; organisation sign-in matches IdpIssuer + IdpSubject
    DisplayName TEXT NOT NULL,
    Role TEXT NOT NULL CHECK (Role IN ('Viewer','ReleaseManager','RTE','GovernanceOfficer')),   -- the role from sign-in, held in the default workspace; others are per membership (REOS-105)
    Handle TEXT UNIQUE COLLATE NOCASE,                   -- optional @handle for the bulk parser
    IsActive INTEGER NOT NULL DEFAULT 1 CHECK (IsActive IN (0,1)),
    IdpIssuer TEXT,                                      -- OIDC 'iss' of the bound identity; NULL until the first organisation sign-in (Q-SEC-B8)
    IdpSubject TEXT,                                     -- OIDC 'sub' at that issuer: who the person is (email can change or be reissued)
    Version INTEGER NOT NULL DEFAULT 1,
    CHECK ((IdpIssuer IS NULL) = (IdpSubject IS NULL))
);
CREATE UNIQUE INDEX UX_Users_IdpIdentity ON Users(IdpIssuer, IdpSubject) WHERE IdpSubject IS NOT NULL;

CREATE TABLE Workspaces (                                 -- D45: several workspaces on one host; every root row below belongs to exactly one (REOS-104)
    Id TEXT PRIMARY KEY,                                  -- 'default' holds the data from before workspaces (a documented exception to UUIDv7)
    Name TEXT NOT NULL CHECK (length(trim(Name)) BETWEEN 1 AND 100),
    Slug TEXT NOT NULL UNIQUE COLLATE NOCASE CHECK (length(Slug) BETWEEN 1 AND 40 AND Slug NOT GLOB '*[^a-z0-9-]*'),   -- URL segment
    Region TEXT CHECK (Region IS NULL OR length(Region) BETWEEN 1 AND 60),   -- a label ("EU", "US-East"); every workspace's data stays on this host
    CreatedAt TEXT NOT NULL,
    ArchivedAt TEXT,
    Version INTEGER NOT NULL DEFAULT 1
);
-- The default workspace exists in every database: root rows written by raw SQL (fixtures, upgrades) land in it through the column DEFAULT.
-- The app names it from Workspace:DefaultName and Workspace:Region at start. SQLite's 'now' here: there is no service clock at schema time.
INSERT INTO Workspaces (Id, Name, Slug, CreatedAt) VALUES ('default', 'Default', 'default', strftime('%Y-%m-%dT%H:%M:%SZ','now'));

CREATE TABLE WorkspaceMembers (                           -- a person's role is held per workspace (D45); users and sign-in stay global
    WorkspaceId TEXT NOT NULL REFERENCES Workspaces(Id) ON DELETE CASCADE,
    UserId TEXT NOT NULL REFERENCES Users(Id) ON DELETE CASCADE,
    Role TEXT NOT NULL CHECK (Role IN ('Viewer','ReleaseManager','RTE','GovernanceOfficer')),   -- the role triggers read this role, in the train's workspace (REOS-105)
    AddedAt TEXT NOT NULL,
    Version INTEGER NOT NULL DEFAULT 1,
    PRIMARY KEY (WorkspaceId, UserId)
);
CREATE INDEX IX_WorkspaceMembers_User ON WorkspaceMembers(UserId);

CREATE TABLE Teams (
    Id TEXT PRIMARY KEY,
    Handle TEXT NOT NULL COLLATE NOCASE,                 -- @handle in the bulk parser and CSV; unique within the workspace
    Name TEXT NOT NULL,
    WebhookDestinationId TEXT REFERENCES WebhookDestinations(Id),   -- team channel for notifications
    Version INTEGER NOT NULL DEFAULT 1,
    WorkspaceId TEXT NOT NULL DEFAULT 'default' REFERENCES Workspaces(Id),
    UNIQUE (WorkspaceId, Handle)
);

CREATE TABLE TeamMembers (
    TeamId TEXT NOT NULL REFERENCES Teams(Id) ON DELETE CASCADE,
    UserId TEXT NOT NULL REFERENCES Users(Id) ON DELETE CASCADE,
    PRIMARY KEY (TeamId, UserId)
);

CREATE TABLE Holidays (                                   -- business-day offset calendar (D9), one per workspace
    Day TEXT NOT NULL,
    Name TEXT NOT NULL,
    Version INTEGER NOT NULL DEFAULT 1,
    WorkspaceId TEXT NOT NULL DEFAULT 'default' REFERENCES Workspaces(Id),
    PRIMARY KEY (WorkspaceId, Day)
);

-- =====================================================================
-- 2. Templates (governed: Draft -> Approved -> Retired, with re-review date)
-- =====================================================================
CREATE TABLE TrainTemplates (
    Id TEXT PRIMARY KEY,
    Name TEXT NOT NULL,                                   -- unique within the workspace
    Status TEXT NOT NULL DEFAULT 'Draft' CHECK (Status IN ('Draft','Approved','Retired')),
    DefaultRiskTier TEXT NOT NULL DEFAULT 'Moderate' CHECK (DefaultRiskTier IN ('Low','Moderate','High','VeryHigh')),
    ApprovedByUserId TEXT REFERENCES Users(Id),
    ApprovedAt TEXT,
    ReviewDueOn TEXT,                                     -- re-review cycle (default +12 months on approval)
    Version INTEGER NOT NULL DEFAULT 1,
    WorkspaceId TEXT NOT NULL DEFAULT 'default' REFERENCES Workspaces(Id),
    UNIQUE (WorkspaceId, Name),
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
    Name TEXT NOT NULL,                                   -- unique within the workspace
    Audience TEXT NOT NULL,                               -- Exec, Ops, SupportDesk, Clients, All
    SubjectLine TEXT NOT NULL,
    MarkdownBody TEXT NOT NULL,                           -- tokens {ReleaseTitle} {BlockerCount} ... (allowlisted)
    Version INTEGER NOT NULL DEFAULT 1,
    WorkspaceId TEXT NOT NULL DEFAULT 'default' REFERENCES Workspaces(Id),
    UNIQUE (WorkspaceId, Name)
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
    WorkspaceId TEXT NOT NULL DEFAULT 'default' REFERENCES Workspaces(Id),
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
    WorkspaceId TEXT NOT NULL DEFAULT 'default' REFERENCES Workspaces(Id),
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
    Version INTEGER NOT NULL DEFAULT 1,
    WorkspaceId TEXT DEFAULT 'default' REFERENCES Workspaces(Id)   -- NULL = host-wide (backups); the fingerprint carries the workspace
);
CREATE UNIQUE INDEX UX_SyncAlerts_OpenFingerprint ON SyncAlerts(Fingerprint) WHERE IsResolved = 0;

CREATE TABLE WebhookDestinations (                       -- the URL's path is its token (Teams, Slack): never stored in clear (Q-053e)
    Id TEXT PRIMARY KEY,
    Name TEXT NOT NULL,
    Host TEXT NOT NULL CHECK (length(Host) BETWEEN 1 AND 300 AND Host NOT GLOB '*[/?#@ ]*'),   -- host[:port] only: what screens, alerts and audit rows show
    ProtectedUrl TEXT NOT NULL CHECK (ProtectedUrl LIKE 'CfDJ8%'),   -- ASP.NET Data Protection payload of the https URL; decrypted only by the senders
    UrlHmac TEXT NOT NULL UNIQUE CHECK (length(UrlHmac) = 64 AND UrlHmac NOT GLOB '*[^0-9a-f]*'),   -- HMAC-SHA256 (hex) of the normalised URL: one row per address
    Kind TEXT NOT NULL CHECK (Kind IN ('Teams','Slack','Generic')),
    Version INTEGER NOT NULL DEFAULT 1,
    WorkspaceId TEXT NOT NULL DEFAULT 'default' REFERENCES Workspaces(Id)   -- UrlHmac stays unique on the host: one address, one destination (Q-104a)
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
    Version INTEGER NOT NULL DEFAULT 1,
    WorkspaceId TEXT NOT NULL DEFAULT 'default' REFERENCES Workspaces(Id)
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
    WorkspaceId TEXT NOT NULL DEFAULT 'default' REFERENCES Workspaces(Id),
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
    Version INTEGER NOT NULL DEFAULT 1,
    WorkspaceId TEXT NOT NULL DEFAULT 'default' REFERENCES Workspaces(Id)
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
    RevokedAt TEXT,
    WorkspaceId TEXT NOT NULL DEFAULT 'default' REFERENCES Workspaces(Id)   -- one feed per person per workspace
);
CREATE UNIQUE INDEX UX_IcsTokens_ActivePerUser ON IcsTokens(WorkspaceId, UserId) WHERE RevokedAt IS NULL;

CREATE TABLE SessionRevocations (                         -- signed-out session ids, refused until the session would have ended anyway (Q-SEC-B5)
    SessionId TEXT PRIMARY KEY,                           -- random id stamped in the cookie ticket at sign-in
    UserId TEXT REFERENCES Users(Id) ON DELETE CASCADE,
    RevokedAt TEXT NOT NULL,
    ExpiresAt TEXT NOT NULL                               -- sign-in time + Auth:Session:AbsoluteHours; the row is pruned after it
);
CREATE INDEX IX_SessionRevocations_Expiry ON SessionRevocations(ExpiresAt);

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
    SourceSystem TEXT NOT NULL CHECK (SourceSystem IN ('Jira','ServiceNow')),
    BaseUrl TEXT NOT NULL,
    IsEnabled INTEGER NOT NULL DEFAULT 1 CHECK (IsEnabled IN (0,1)),
    LastCycleStartedAt TEXT,
    LastCycleCompletedAt TEXT,
    LastSuccessAt TEXT,
    ConsecutiveFailures INTEGER NOT NULL DEFAULT 0,
    Version INTEGER NOT NULL DEFAULT 1,
    WorkspaceId TEXT NOT NULL DEFAULT 'default' REFERENCES Workspaces(Id),
    PRIMARY KEY (WorkspaceId, SourceSystem)
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
    AfterJson TEXT,
    WorkspaceId TEXT DEFAULT 'default'                    -- NULL = host-wide (accounts, backups); no foreign key, like ReleaseTrainId: the log outlives what it describes
);

-- =====================================================================
-- 9b. Train milestones (named key dates; informational only)
-- =====================================================================
CREATE TABLE TrainMilestones (                            -- "Code complete", "UAT sign-off": never certified, never block a gate or train move
    Id TEXT PRIMARY KEY,
    ReleaseTrainId TEXT NOT NULL REFERENCES ReleaseTrains(Id) ON DELETE CASCADE,
    Name TEXT NOT NULL CHECK (length(trim(Name)) BETWEEN 1 AND 200),
    DueOn TEXT NOT NULL CHECK (date(DueOn) IS DueOn),    -- a calendar date 'YYYY-MM-DD'
    OwnerUserId TEXT REFERENCES Users(Id),                -- optional; at most one of user/team
    OwnerTeamId TEXT REFERENCES Teams(Id),
    Note TEXT CHECK (Note IS NULL OR length(Note) <= 2000),
    IsDone INTEGER NOT NULL DEFAULT 0 CHECK (IsDone IN (0,1)),
    DoneAt TEXT,
    DoneByUserId TEXT REFERENCES Users(Id),
    LastChangedByUserId TEXT REFERENCES Users(Id),
    LastChangedAt TEXT,
    Version INTEGER NOT NULL DEFAULT 1,
    CHECK (OwnerUserId IS NULL OR OwnerTeamId IS NULL),
    CHECK ((IsDone = 1) = (DoneAt IS NOT NULL AND DoneByUserId IS NOT NULL))
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
END;
CREATE TRIGGER trg_Ws_ReleaseTrains_Update BEFORE UPDATE OF WorkspaceId, TemplateId, ClonedFromTrainId ON ReleaseTrains
BEGIN
    SELECT RAISE(ABORT, 'A row cannot move to another workspace') WHERE NEW.WorkspaceId IS NOT OLD.WorkspaceId;
    SELECT RAISE(ABORT, 'The template belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM TrainTemplates x WHERE x.Id = NEW.TemplateId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The cloned-from train belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM ReleaseTrains x WHERE x.Id = NEW.ClonedFromTrainId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
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
CREATE TRIGGER trg_Ws_BundledProducts_Update BEFORE UPDATE OF ReleaseTrainId ON BundledProducts WHEN NEW.ReleaseTrainId IS NOT OLD.ReleaseTrainId
BEGIN SELECT RAISE(ABORT, 'A row cannot move to another train'); END;
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

-- =====================================================================
-- 11. Indexes
-- =====================================================================
CREATE INDEX IX_Trains_Status ON ReleaseTrains(CurrentStatus, TargetReleaseDate);
CREATE INDEX IX_Trains_Workspace ON ReleaseTrains(WorkspaceId, TargetReleaseDate);
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
CREATE INDEX IX_Freeze_Range ON FreezeWindows(WorkspaceId, StartsAt, EndsAt);
CREATE INDEX IX_Audit_Train ON AuditEvents(ReleaseTrainId, OccurredAt);
CREATE INDEX IX_Audit_Entity ON AuditEvents(EntityType, EntityId);
CREATE INDEX IX_Audit_Workspace ON AuditEvents(WorkspaceId, OccurredAt);
CREATE INDEX IX_Milestones_Train ON TrainMilestones(ReleaseTrainId, DueOn);
