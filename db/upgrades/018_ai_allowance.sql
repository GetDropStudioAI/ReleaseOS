-- Upgrade 018 (REOS-185; D57, D58): AI allowance, usage ledger and caps.
-- From: migrations 20261126235833_Schema + 20261126235845_Triggers (after 017).
-- To:   migrations 20261127190737_Schema + 20261127190749_Triggers.
-- Run with the app stopped and after a backup (docs/RUNBOOK_OPERATIONS.md section 8):  sqlite3 releaseos.db < db/upgrades/018_ai_allowance.sql
-- One transaction. Text below is verbatim from db/schema.sql (the three blocks delimited ">>> UPGRADE 018"):
--  * New tables: AiAllowancePeriods (one row per workspace per UTC month: the included allowance, credit packs, used and reserved), AiUsageLedger (one row per AI call that asked for allowance) and
--    AiCreditPacks (credits added to a month: the extension point for billing). All three start EMPTY: a workspace gets its first period, with the policy of that moment, at its next AI call, so nothing is
--    invented by the script and nothing already used is counted (AI calls made before this upgrade are in AiActions, not in the ledger).
--  * New triggers: 15 (a period never moves, its allowance is fixed and its usage never falls, a reservation cannot take it over its allowance, chat is capped first, the ledger and packs are never
--    rewritten or deleted, workspace boundary). No existing table changes, so nothing is rebuilt and no existing trigger is dropped.
--  * New indexes: 6.
--
-- Chained after 017 (AI audit trail). If another schema merge lands first the coordinator re-chains it: the three places marked CHAIN 1-3 carry the previous pair as the guard and the regenerated pair as the result.

PRAGMA foreign_keys=OFF;
BEGIN IMMEDIATE;

-- Refuses to run twice or against the wrong database: the INSERT fails on the CHECK unless exactly the previous upgrade's migrations are recorded.
CREATE TEMP TABLE _upgrade_guard (ok INTEGER CHECK (ok = 1));
INSERT INTO _upgrade_guard SELECT (SELECT group_concat(MigrationId, ',') FROM (SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId))
    = '20261126235833_Schema,20261126235845_Triggers';   -- CHAIN 1
DROP TABLE _upgrade_guard;

-- >>> UPGRADE 018 (REOS-185, D57, D58): AI allowance, usage ledger and caps. TABLES. Delimited so a merge with another session's block is a paste. >>>
-- AI cost can never surprise the customer or us. Money is whole micro-US-dollars (1 USD = 1,000,000; a credit is 10,000 of them, a cent), never a decimal or float (CLAUDE.md rule 5).
-- A call RESERVES its worst-case cost before the model is called (a Reserved ledger row and the period's Reserved counters, in one write transaction that holds SQLite's write lock, so concurrent calls cannot overspend)
-- and SETTLES it afterwards with the real cost (Used goes up, Reserved goes down, in the transaction that records the AI action). Reservations are never deleted: they become Settled, Released or stay as the record.
CREATE TABLE AiAllowancePeriods (                         -- one row per workspace per UTC calendar month: what was included, what was added by credit packs, what was used. The next month starts a new row, which is how the allowance resets
    Id TEXT PRIMARY KEY,
    WorkspaceId TEXT NOT NULL DEFAULT 'default' REFERENCES Workspaces(Id),
    PeriodStart TEXT NOT NULL CHECK (length(PeriodStart) = 10 AND PeriodStart GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-01'),   -- first day of the month, UTC
    PeriodEnd TEXT NOT NULL CHECK (length(PeriodEnd) = 10 AND PeriodEnd GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-01' AND PeriodEnd > PeriodStart),   -- first day of the next month
    IncludedMicroUsd INTEGER NOT NULL CHECK (IncludedMicroUsd >= 0),      -- the included allowance as it was when the period opened (the policy of that moment, so a later change never rewrites a past month)
    PackMicroUsd INTEGER NOT NULL DEFAULT 0 CHECK (PackMicroUsd >= 0),    -- credit packs granted into this period; only goes up
    SoftCapPercent INTEGER NOT NULL CHECK (SoftCapPercent BETWEEN 1 AND 100),   -- at this share of the allowance chat stops and admins are told; everything else goes on
    ChatSharePercent INTEGER NOT NULL CHECK (ChatSharePercent BETWEEN 0 AND 100),   -- the most of the allowance chat may spend (0 = no chat)
    UsedMicroUsd INTEGER NOT NULL DEFAULT 0 CHECK (UsedMicroUsd >= 0),    -- settled cost; never falls
    ReservedMicroUsd INTEGER NOT NULL DEFAULT 0 CHECK (ReservedMicroUsd >= 0),   -- worst-case cost of calls in flight
    ChatUsedMicroUsd INTEGER NOT NULL DEFAULT 0 CHECK (ChatUsedMicroUsd >= 0),
    ChatReservedMicroUsd INTEGER NOT NULL DEFAULT 0 CHECK (ChatReservedMicroUsd >= 0),
    SoftCapNotifiedAt TEXT,                               -- when admins were told the soft cap was reached (once a period)
    HardCapNotifiedAt TEXT,                               -- when admins were told the allowance was used up (once a period)
    OpenedAt TEXT NOT NULL,
    LastChangedAt TEXT,
    Version INTEGER NOT NULL DEFAULT 1,
    CHECK (ChatUsedMicroUsd <= UsedMicroUsd),
    CHECK (ChatReservedMicroUsd <= ReservedMicroUsd)
);

CREATE TABLE AiUsageLedger (                              -- one row per AI call that asked for allowance (refusals by a cap included): feature, user, model, tokens and cost. The usage ledger the admin screen and the reconciliation read
    Id TEXT PRIMARY KEY,
    WorkspaceId TEXT NOT NULL DEFAULT 'default' REFERENCES Workspaces(Id),
    PeriodId TEXT NOT NULL REFERENCES AiAllowancePeriods(Id),
    Feature TEXT NOT NULL CHECK (length(Feature) BETWEEN 1 AND 60 AND Feature NOT GLOB '*[^a-z0-9-]*'),
    Class TEXT NOT NULL CHECK (Class IN ('Chat','Standard')),   -- Chat is capped first
    Model TEXT CHECK (Model IS NULL OR length(Model) BETWEEN 1 AND 100),
    Batch INTEGER NOT NULL DEFAULT 0 CHECK (Batch IN (0, 1)),
    RequestedByUserId TEXT REFERENCES Users(Id),          -- NULL = the system (a scheduled or batch run); the per-user rate limit applies to people only
    State TEXT NOT NULL CHECK (State IN ('Reserved','Settled','Released','Refused')),   -- Reserved: in flight. Settled: the real cost is in. Released: the call failed, nothing is charged. Refused: a cap or the rate limit stopped it
    RefusalReason TEXT CHECK (RefusalReason IS NULL OR RefusalReason IN ('HardCap','ChatCap','RateLimit')),
    ReservedMicroUsd INTEGER NOT NULL DEFAULT 0 CHECK (ReservedMicroUsd >= 0),   -- the worst case held while the call was in flight
    CostMicroUsd INTEGER NOT NULL DEFAULT 0 CHECK (CostMicroUsd >= 0),           -- what it cost: input, output, cache and batch priced from the provider's usage
    InputTokens INTEGER NOT NULL DEFAULT 0 CHECK (InputTokens >= 0),
    OutputTokens INTEGER NOT NULL DEFAULT 0 CHECK (OutputTokens >= 0),
    CacheReadTokens INTEGER NOT NULL DEFAULT 0 CHECK (CacheReadTokens >= 0),
    CacheWriteTokens INTEGER NOT NULL DEFAULT 0 CHECK (CacheWriteTokens >= 0),
    AiActionId TEXT REFERENCES AiActions(Id),             -- the audit-trail record of the same call
    ReservedAt TEXT NOT NULL,
    SettledAt TEXT,
    LastChangedAt TEXT,
    Version INTEGER NOT NULL DEFAULT 1,
    CHECK ((State = 'Refused') = (RefusalReason IS NOT NULL)),
    CHECK ((State = 'Reserved') = (SettledAt IS NULL)),
    CHECK (State <> 'Reserved' OR ReservedMicroUsd > 0),
    CHECK (State <> 'Refused' OR (ReservedMicroUsd = 0 AND CostMicroUsd = 0)),
    CHECK (State NOT IN ('Reserved','Released') OR CostMicroUsd = 0),
    CHECK (State NOT IN ('Settled','Refused') OR AiActionId IS NOT NULL)
);

CREATE TABLE AiCreditPacks (                              -- the extension point for billing: credits added to the period they are granted in. Never edited, never deleted. A pack does not roll over (Q-185a)
    Id TEXT PRIMARY KEY,
    WorkspaceId TEXT NOT NULL DEFAULT 'default' REFERENCES Workspaces(Id),
    PeriodId TEXT NOT NULL REFERENCES AiAllowancePeriods(Id),
    MicroUsd INTEGER NOT NULL CHECK (MicroUsd > 0),
    Source TEXT NOT NULL CHECK (Source IN ('Manual','Billing')),
    Reference TEXT CHECK (Reference IS NULL OR length(Reference) BETWEEN 1 AND 100),   -- the billing system's id for the purchase; a grant with the same (Source, Reference) is the same grant
    Note TEXT CHECK (Note IS NULL OR length(Note) <= 500),
    GrantedByUserId TEXT REFERENCES Users(Id),            -- NULL = the billing system
    GrantedAt TEXT NOT NULL,
    Version INTEGER NOT NULL DEFAULT 1
);
-- <<< UPGRADE 018 TABLES <<<

-- >>> UPGRADE 018 (REOS-185, D57, D58): AI allowance, usage ledger and caps. INDEXES. >>>
CREATE UNIQUE INDEX UX_AiAllowancePeriods_Period ON AiAllowancePeriods(WorkspaceId, PeriodStart);
CREATE INDEX IX_AiUsageLedger_Period ON AiUsageLedger(PeriodId, Feature);
CREATE INDEX IX_AiUsageLedger_User ON AiUsageLedger(WorkspaceId, RequestedByUserId, ReservedAt) WHERE RequestedByUserId IS NOT NULL;
CREATE INDEX IX_AiUsageLedger_InFlight ON AiUsageLedger(ReservedAt) WHERE State = 'Reserved';
CREATE INDEX IX_AiCreditPacks_Period ON AiCreditPacks(PeriodId);
CREATE UNIQUE INDEX UX_AiCreditPacks_Reference ON AiCreditPacks(WorkspaceId, Source, Reference) WHERE Reference IS NOT NULL;
-- <<< UPGRADE 018 INDEXES <<<

-- >>> UPGRADE 018 (REOS-185, D57, D58): AI allowance, usage ledger and caps. TRIGGERS, in file order (SQLite fires same-event triggers newest-first, so the rule written last is the one reported first). >>>
-- The services check every rule first and answer in words; these back them up for any other writer (DbRule 422):
--   Periods  1 never moves workspace   2 the allowance of a period is fixed once it opens   3 usage never falls   4 a reservation never takes a period over its allowance   5 chat is capped first   6 never deleted
--   Ledger   7 members and same workspace   8 the record never changes   9 a settled, released or refused entry is final   10 never deleted
--   Packs    11 members and same workspace   12 never edited   13 never deleted
CREATE TRIGGER trg_Ws_AiAllowancePeriods_Update BEFORE UPDATE OF WorkspaceId ON AiAllowancePeriods
BEGIN
    SELECT RAISE(ABORT, 'A row cannot move to another workspace') WHERE NEW.WorkspaceId IS NOT OLD.WorkspaceId;
END;
CREATE TRIGGER trg_AiPeriod_RecordIsFixed BEFORE UPDATE ON AiAllowancePeriods
WHEN NEW.PeriodStart IS NOT OLD.PeriodStart OR NEW.PeriodEnd IS NOT OLD.PeriodEnd OR NEW.IncludedMicroUsd IS NOT OLD.IncludedMicroUsd
  OR NEW.SoftCapPercent IS NOT OLD.SoftCapPercent OR NEW.ChatSharePercent IS NOT OLD.ChatSharePercent OR NEW.OpenedAt IS NOT OLD.OpenedAt
BEGIN SELECT RAISE(ABORT, 'The allowance of an AI period is fixed when the period opens; credit packs are what add to it'); END;
CREATE TRIGGER trg_AiPeriod_UsageNeverFalls BEFORE UPDATE ON AiAllowancePeriods
WHEN NEW.UsedMicroUsd < OLD.UsedMicroUsd OR NEW.ChatUsedMicroUsd < OLD.ChatUsedMicroUsd OR NEW.PackMicroUsd < OLD.PackMicroUsd
BEGIN SELECT RAISE(ABORT, 'AI usage and credit packs never go down; the ledger is the record'); END;
CREATE TRIGGER trg_AiPeriod_ReservationWithinAllowance BEFORE UPDATE ON AiAllowancePeriods
WHEN NEW.ReservedMicroUsd > OLD.ReservedMicroUsd AND NEW.UsedMicroUsd + NEW.ReservedMicroUsd > NEW.IncludedMicroUsd + NEW.PackMicroUsd
BEGIN SELECT RAISE(ABORT, 'The AI allowance for this period is used up'); END;
CREATE TRIGGER trg_AiPeriod_ChatCappedFirst BEFORE UPDATE ON AiAllowancePeriods
WHEN NEW.ChatReservedMicroUsd > OLD.ChatReservedMicroUsd
 AND (NEW.ChatUsedMicroUsd + NEW.ChatReservedMicroUsd > (NEW.IncludedMicroUsd + NEW.PackMicroUsd) * NEW.ChatSharePercent / 100
   OR NEW.UsedMicroUsd + NEW.ReservedMicroUsd > (NEW.IncludedMicroUsd + NEW.PackMicroUsd) * NEW.SoftCapPercent / 100)
BEGIN SELECT RAISE(ABORT, 'Chat is capped first: its share of the AI allowance or the soft cap is reached'); END;
CREATE TRIGGER trg_AiPeriod_NoDelete BEFORE DELETE ON AiAllowancePeriods
BEGIN SELECT RAISE(ABORT, 'AI allowance periods are kept for the record'); END;

CREATE TRIGGER trg_Ws_AiUsageLedger_Insert BEFORE INSERT ON AiUsageLedger
BEGIN
    SELECT RAISE(ABORT, 'The person who asked is not a member of this workspace')
     WHERE NEW.RequestedByUserId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM WorkspaceMembers m WHERE m.UserId = NEW.RequestedByUserId AND m.WorkspaceId = NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The allowance period belongs to another workspace')
     WHERE NOT EXISTS (SELECT 1 FROM AiAllowancePeriods p WHERE p.Id = NEW.PeriodId AND p.WorkspaceId = NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The AI action belongs to another workspace')
     WHERE NEW.AiActionId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM AiActions a WHERE a.Id = NEW.AiActionId AND a.WorkspaceId = NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_AiUsageLedger_Update BEFORE UPDATE OF WorkspaceId, PeriodId, RequestedByUserId, AiActionId ON AiUsageLedger
BEGIN
    SELECT RAISE(ABORT, 'A row cannot move to another workspace') WHERE NEW.WorkspaceId IS NOT OLD.WorkspaceId;
    SELECT RAISE(ABORT, 'The person who asked is not a member of this workspace')
     WHERE NEW.RequestedByUserId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM WorkspaceMembers m WHERE m.UserId = NEW.RequestedByUserId AND m.WorkspaceId = NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The allowance period belongs to another workspace')
     WHERE NOT EXISTS (SELECT 1 FROM AiAllowancePeriods p WHERE p.Id = NEW.PeriodId AND p.WorkspaceId = NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The AI action belongs to another workspace')
     WHERE NEW.AiActionId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM AiActions a WHERE a.Id = NEW.AiActionId AND a.WorkspaceId = NEW.WorkspaceId);
END;
CREATE TRIGGER trg_AiLedger_RecordIsFixed BEFORE UPDATE ON AiUsageLedger
WHEN NEW.PeriodId IS NOT OLD.PeriodId OR NEW.Feature IS NOT OLD.Feature OR NEW.Class IS NOT OLD.Class OR NEW.Model IS NOT OLD.Model OR NEW.Batch IS NOT OLD.Batch
  OR NEW.RequestedByUserId IS NOT OLD.RequestedByUserId OR NEW.ReservedMicroUsd IS NOT OLD.ReservedMicroUsd OR NEW.ReservedAt IS NOT OLD.ReservedAt
BEGIN SELECT RAISE(ABORT, 'An AI usage entry does not change once written; only its settlement is added'); END;
CREATE TRIGGER trg_AiLedger_NoDelete BEFORE DELETE ON AiUsageLedger
BEGIN SELECT RAISE(ABORT, 'AI usage entries are kept for the record'); END;
-- Written last, so a settled, released or refused entry reports this whatever else the update touched
CREATE TRIGGER trg_AiLedger_SettledIsFinal BEFORE UPDATE ON AiUsageLedger
WHEN OLD.State <> 'Reserved'
BEGIN SELECT RAISE(ABORT, 'A settled AI usage entry is final'); END;

CREATE TRIGGER trg_Ws_AiCreditPacks_Insert BEFORE INSERT ON AiCreditPacks
BEGIN
    SELECT RAISE(ABORT, 'The person who granted the pack is not a member of this workspace')
     WHERE NEW.GrantedByUserId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM WorkspaceMembers m WHERE m.UserId = NEW.GrantedByUserId AND m.WorkspaceId = NEW.WorkspaceId);
    SELECT RAISE(ABORT, 'The allowance period belongs to another workspace')
     WHERE NOT EXISTS (SELECT 1 FROM AiAllowancePeriods p WHERE p.Id = NEW.PeriodId AND p.WorkspaceId = NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_AiCreditPacks_Update BEFORE UPDATE OF WorkspaceId ON AiCreditPacks
BEGIN
    SELECT RAISE(ABORT, 'A row cannot move to another workspace') WHERE NEW.WorkspaceId IS NOT OLD.WorkspaceId;
END;
CREATE TRIGGER trg_AiPack_Immutable BEFORE UPDATE ON AiCreditPacks
BEGIN SELECT RAISE(ABORT, 'A credit pack never changes once granted'); END;
CREATE TRIGGER trg_AiPack_NoDelete BEFORE DELETE ON AiCreditPacks
BEGIN SELECT RAISE(ABORT, 'Credit packs are kept for the record'); END;
-- <<< UPGRADE 018 TRIGGERS <<<

-- The app's startup migration must see the new migrations as applied.
DELETE FROM __EFMigrationsHistory WHERE MigrationId IN ('20261126235833_Schema', '20261126235845_Triggers');   -- CHAIN 2
INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES   -- CHAIN 3
    ('20261127190737_Schema', '10.0.12'),
    ('20261127190749_Triggers', '10.0.12');

COMMIT;
PRAGMA foreign_keys=ON;
PRAGMA foreign_key_check;
PRAGMA integrity_check;
