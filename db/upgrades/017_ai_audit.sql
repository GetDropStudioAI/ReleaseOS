-- Upgrade 017 (REOS-184; D58): the AI audit trail and AI inventory.
-- From: migrations 20261126225422_Schema + 20261126225440_Triggers (after 016).
-- To:   migrations 20261126235833_Schema + 20261126235845_Triggers.
-- Run with the app stopped and after a backup (docs/RUNBOOK_OPERATIONS.md section 8):  sqlite3 releaseos.db < db/upgrades/017_ai_audit.sql
-- One transaction. Text below is verbatim from db/schema.sql (the three blocks delimited ">>> UPGRADE 017"):
--  * New tables: AiActions (one immutable record per AI-assisted action: the draft, the model, hashes of the inputs, the human decision) and AiWorkspaceSettings (the persisted workspace pause and opt-out). Both start EMPTY:
--    a workspace with no settings row has AI paused = off and opted out = off, exactly as before (until now both lived in memory and were lost at a restart).
--  * New triggers: 5 (workspace boundary, the record never changes, a decided action is final, never deleted). No existing table changes, so nothing is rebuilt and no existing trigger is dropped.
--  * New indexes: 3.
--
-- Chained after 016 (CAB). If another schema merge lands first the coordinator re-chains it: the three places marked CHAIN 1-3 carry the previous pair as the guard and the regenerated pair as the result.

PRAGMA foreign_keys=OFF;
BEGIN IMMEDIATE;

-- Refuses to run twice or against the wrong database: the INSERT fails on the CHECK unless exactly the previous upgrade's migrations are recorded.
CREATE TEMP TABLE _upgrade_guard (ok INTEGER CHECK (ok = 1));
INSERT INTO _upgrade_guard SELECT (SELECT group_concat(MigrationId, ',') FROM (SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId))
    = '20261126225422_Schema,20261126225440_Triggers';   -- CHAIN 1
DROP TABLE _upgrade_guard;

-- >>> UPGRADE 017 (REOS-184, D58): AI audit trail and AI inventory. TABLES. Delimited so a merge with another session's block is a paste. >>>
-- AI output lives ONLY here, apart from every status-bearing row: nothing in these tables is a Status that any gate, train or run reads, and no column points at one with a foreign key (SubjectType/SubjectId and ReleaseTrainId are plain references, like AuditEvents.ReleaseTrainId).
-- Each call to the AI platform is one AiActions row, written by the AI action log in the transaction that writes its audit row (rule 3); the human decision is added once (Pending > Accepted | Edited | Rejected), after which the row never changes again.
CREATE TABLE AiActions (                                  -- one AI-assisted action: what was asked (hashes, not customer text), which model answered, what it said, who asked, and what the human decided. Never edited, never deleted
    Id TEXT PRIMARY KEY,
    WorkspaceId TEXT NOT NULL DEFAULT 'default' REFERENCES Workspaces(Id),
    Feature TEXT NOT NULL CHECK (length(Feature) BETWEEN 1 AND 60 AND Feature NOT GLOB '*[^a-z0-9-]*'),   -- the registry is in code (AiFeatures): 'risk-score', 'release-notes-draft', ...
    Provider TEXT NOT NULL CHECK (length(Provider) BETWEEN 1 AND 40),
    Model TEXT CHECK (Model IS NULL OR length(Model) BETWEEN 1 AND 100),               -- the model the feature was routed to; NULL when none was configured
    ServedModel TEXT CHECK (ServedModel IS NULL OR length(ServedModel) BETWEEN 1 AND 100),   -- the exact model version the provider says answered, when it says
    PromptTemplateVersion TEXT NOT NULL CHECK (length(PromptTemplateVersion) BETWEEN 1 AND 60),   -- 'risk-score/1': the version of the prompt text the feature used; 'untracked' when the caller did not say
    PromptHash TEXT CHECK (PromptHash IS NULL OR (length(PromptHash) = 64 AND PromptHash NOT GLOB '*[^0-9a-f]*')),   -- SHA-256 of the system prompt as sent (after redaction)
    InputRefsJson TEXT NOT NULL DEFAULT '[]' CHECK (length(InputRefsJson) BETWEEN 2 AND 20000),   -- [{label, class, sha256, chars}] one per context item: references and hashes, never the text
    InputHash TEXT CHECK (InputHash IS NULL OR (length(InputHash) = 64 AND InputHash NOT GLOB '*[^0-9a-f]*')),   -- SHA-256 over everything sent (system, instruction, context), after redaction
    Outcome TEXT NOT NULL CHECK (Outcome IN ('Completed','ModuleOff','KillSwitch','WorkspacePaused','OptedOut','NotConfigured','NoKey','Blocked','TimedOut','ProviderError')),   -- AiOutcome. A refusal is recorded too: the trail shows what was NOT sent
    Detail TEXT CHECK (Detail IS NULL OR length(Detail) <= 500),   -- why a call was refused or failed; never prompt text
    OutputText TEXT CHECK (OutputText IS NULL OR length(OutputText) <= 200000),        -- the draft, exactly as the model returned it; only for a Completed call
    OutputHash TEXT CHECK (OutputHash IS NULL OR (length(OutputHash) = 64 AND OutputHash NOT GLOB '*[^0-9a-f]*')),
    InputTokens INTEGER NOT NULL DEFAULT 0 CHECK (InputTokens >= 0),
    OutputTokens INTEGER NOT NULL DEFAULT 0 CHECK (OutputTokens >= 0),
    CacheReadTokens INTEGER NOT NULL DEFAULT 0 CHECK (CacheReadTokens >= 0),
    CacheWriteTokens INTEGER NOT NULL DEFAULT 0 CHECK (CacheWriteTokens >= 0),
    Attempts INTEGER NOT NULL DEFAULT 0 CHECK (Attempts >= 0),
    Redactions INTEGER NOT NULL DEFAULT 0 CHECK (Redactions >= 0),
    Batch INTEGER NOT NULL DEFAULT 0 CHECK (Batch IN (0, 1)),
    SubjectType TEXT CHECK (SubjectType IS NULL OR length(SubjectType) BETWEEN 1 AND 40),   -- what the action is about ('Train', 'Gate', ...): a reference, no foreign key
    SubjectId TEXT CHECK (SubjectId IS NULL OR length(SubjectId) BETWEEN 1 AND 64),
    ReleaseTrainId TEXT,                                  -- no foreign key, like AuditEvents.ReleaseTrainId: the trail outlives what it describes
    RequestedByUserId TEXT REFERENCES Users(Id),          -- who asked; NULL = the system (a scheduled or batch run)
    RequestedAt TEXT NOT NULL,
    Decision TEXT NOT NULL DEFAULT 'Pending' CHECK (Decision IN ('Pending','Accepted','Edited','Rejected','NotApplicable')),   -- NotApplicable = there was no draft to decide on (a refused or failed call)
    FinalText TEXT CHECK (FinalText IS NULL OR length(FinalText) <= 200000),           -- the human edit; set only when Decision = 'Edited'. Accepted means the draft was used as it stood
    FinalHash TEXT CHECK (FinalHash IS NULL OR (length(FinalHash) = 64 AND FinalHash NOT GLOB '*[^0-9a-f]*')),
    DecisionNote TEXT CHECK (DecisionNote IS NULL OR length(DecisionNote) <= 1000),
    DecidedByUserId TEXT REFERENCES Users(Id),
    DecidedAt TEXT,
    LastChangedByUserId TEXT REFERENCES Users(Id),
    LastChangedAt TEXT,
    Version INTEGER NOT NULL DEFAULT 1,
    CHECK ((SubjectType IS NULL) = (SubjectId IS NULL)),
    CHECK ((Outcome = 'Completed') = (OutputText IS NOT NULL)),
    CHECK ((OutputText IS NULL) = (OutputHash IS NULL)),
    CHECK ((Outcome = 'Completed') = (Decision <> 'NotApplicable')),
    CHECK ((Decision IN ('Pending','NotApplicable')) = (DecidedAt IS NULL)),
    CHECK ((Decision IN ('Pending','NotApplicable')) = (DecidedByUserId IS NULL)),
    CHECK ((Decision = 'Edited') = (FinalText IS NOT NULL)),
    CHECK ((FinalText IS NULL) = (FinalHash IS NULL)),
    CHECK (Decision <> 'Edited' OR FinalHash <> OutputHash),
    CHECK (Outcome NOT IN ('Completed','TimedOut','ProviderError') OR InputHash IS NOT NULL)
);

CREATE TABLE AiWorkspaceSettings (                        -- the workspace's AI controls, kept across restarts: the workspace kill switch (Paused) and the opt-out. No row means both are off. Child of Workspaces like WorkspaceModules: no trigger, the service writes WorkspaceId
    WorkspaceId TEXT PRIMARY KEY REFERENCES Workspaces(Id) ON DELETE CASCADE,
    Paused INTEGER NOT NULL DEFAULT 0 CHECK (Paused IN (0, 1)),
    OptedOut INTEGER NOT NULL DEFAULT 0 CHECK (OptedOut IN (0, 1)),
    LastChangedByUserId TEXT REFERENCES Users(Id),
    LastChangedAt TEXT,
    Version INTEGER NOT NULL DEFAULT 1
);
-- <<< UPGRADE 017 TABLES <<<

-- >>> UPGRADE 017 (REOS-184, D58): AI audit trail. INDEXES. >>>
CREATE INDEX IX_AiActions_Workspace ON AiActions(WorkspaceId, RequestedAt);
CREATE INDEX IX_AiActions_Pending ON AiActions(WorkspaceId, RequestedAt) WHERE Decision = 'Pending';
CREATE INDEX IX_AiActions_Subject ON AiActions(SubjectType, SubjectId) WHERE SubjectId IS NOT NULL;
-- <<< UPGRADE 017 INDEXES <<<

-- >>> UPGRADE 017 (REOS-184, D58): AI audit trail. TRIGGERS, in file order (SQLite fires same-event triggers newest-first, so the rule written last is the one reported first). >>>
-- An AI action is a record, not a draft in progress: only the human decision is ever added, once. The rules the service checks first and the triggers back up for any other writer (DbRule 422):
--   1  The person who asked and the person who decided are members of the action's workspace    2  An action never moves workspace
--   3  The record (what was asked, the model, the output, the tokens) never changes               4  A decided (or refused) action does not change at all   5  An action is never deleted
CREATE TRIGGER trg_Ws_AiActions_Insert BEFORE INSERT ON AiActions
BEGIN
    SELECT RAISE(ABORT, 'The person who asked is not a member of this workspace')
     WHERE NEW.RequestedByUserId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM WorkspaceMembers m WHERE m.UserId = NEW.RequestedByUserId AND m.WorkspaceId = NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The person who decided is not a member of this workspace')
     WHERE NEW.DecidedByUserId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM WorkspaceMembers m WHERE m.UserId = NEW.DecidedByUserId AND m.WorkspaceId = NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_AiActions_Update BEFORE UPDATE OF WorkspaceId, RequestedByUserId, DecidedByUserId ON AiActions
BEGIN
    SELECT RAISE(ABORT, 'A row cannot move to another workspace') WHERE NEW.WorkspaceId IS NOT OLD.WorkspaceId;
    SELECT RAISE(ABORT, 'The person who asked is not a member of this workspace')
     WHERE NEW.RequestedByUserId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM WorkspaceMembers m WHERE m.UserId = NEW.RequestedByUserId AND m.WorkspaceId = NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The person who decided is not a member of this workspace')
     WHERE NEW.DecidedByUserId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM WorkspaceMembers m WHERE m.UserId = NEW.DecidedByUserId AND m.WorkspaceId = NEW.WorkspaceId);
END;
CREATE TRIGGER trg_AiAction_RecordIsImmutable BEFORE UPDATE ON AiActions
WHEN NEW.Feature IS NOT OLD.Feature OR NEW.Provider IS NOT OLD.Provider OR NEW.Model IS NOT OLD.Model OR NEW.ServedModel IS NOT OLD.ServedModel
  OR NEW.PromptTemplateVersion IS NOT OLD.PromptTemplateVersion OR NEW.PromptHash IS NOT OLD.PromptHash OR NEW.InputRefsJson IS NOT OLD.InputRefsJson OR NEW.InputHash IS NOT OLD.InputHash
  OR NEW.Outcome IS NOT OLD.Outcome OR NEW.Detail IS NOT OLD.Detail OR NEW.OutputText IS NOT OLD.OutputText OR NEW.OutputHash IS NOT OLD.OutputHash
  OR NEW.InputTokens IS NOT OLD.InputTokens OR NEW.OutputTokens IS NOT OLD.OutputTokens OR NEW.CacheReadTokens IS NOT OLD.CacheReadTokens OR NEW.CacheWriteTokens IS NOT OLD.CacheWriteTokens
  OR NEW.Attempts IS NOT OLD.Attempts OR NEW.Redactions IS NOT OLD.Redactions OR NEW.Batch IS NOT OLD.Batch OR NEW.SubjectType IS NOT OLD.SubjectType OR NEW.SubjectId IS NOT OLD.SubjectId
  OR NEW.ReleaseTrainId IS NOT OLD.ReleaseTrainId OR NEW.RequestedByUserId IS NOT OLD.RequestedByUserId OR NEW.RequestedAt IS NOT OLD.RequestedAt
BEGIN SELECT RAISE(ABORT, 'An AI action record does not change; only the human decision is added'); END;
CREATE TRIGGER trg_AiAction_NoDelete BEFORE DELETE ON AiActions
BEGIN SELECT RAISE(ABORT, 'AI actions are kept for the record'); END;
-- Written last, so a decided row reports this whatever else the update touched
CREATE TRIGGER trg_AiAction_DecisionIsFinal BEFORE UPDATE ON AiActions
WHEN OLD.Decision <> 'Pending'
BEGIN SELECT RAISE(ABORT, 'The human decision on an AI action is final'); END;
-- <<< UPGRADE 017 TRIGGERS <<<

-- The app's startup migration must see the new migrations as applied.
DELETE FROM __EFMigrationsHistory WHERE MigrationId IN ('20261126225422_Schema', '20261126225440_Triggers');   -- CHAIN 2
INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES   -- CHAIN 3
    ('20261126235833_Schema', '10.0.12'),
    ('20261126235845_Triggers', '10.0.12');

COMMIT;
PRAGMA foreign_keys=ON;
PRAGMA foreign_key_check;
PRAGMA integrity_check;
