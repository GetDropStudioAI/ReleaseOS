-- Upgrade 019 (REOS-167; D61): email delivery (the outbox and send log, the suppression list, per-person channel preferences).
-- From: migrations 20261126235833_Schema + 20261126235845_Triggers (after 017).
-- To:   migrations 20261127183701_Schema + 20261127183710_Triggers.
-- Run with the app stopped and after a backup (docs/RUNBOOK_OPERATIONS.md section 8):  sqlite3 releaseos.db < db/upgrades/019_email_delivery.sql
-- One transaction. Text below is verbatim from db/schema.sql (the three blocks delimited ">>> UPGRADE 019"):
--  * New tables: EmailOutbox (every message and its delivery state), EmailSuppressions (addresses never mailed again until an administrator lifts them) and NotificationPreferences (a person's choice per kind and channel).
--    All start EMPTY: email is off by default (Notifications:Smtp:Enabled), no preference row means the defaults, and nothing is mailed after the upgrade unless an administrator turns it on.
--    No secret is stored in them: the provider's password or API key lives in a Data Protection file under the credentials directory (CLAUDE.md rule 11).
--  * New triggers: 7 (workspace boundary, a queued message never changes, a finished message is final). New indexes: 3. No existing table changes, so nothing is rebuilt and no existing trigger is dropped.
--
-- Written against the state after 017. Upgrade 018 (REOS-185, AI allowance) is built in parallel and chains after 017 too: the coordinator re-chains this script after whichever merges second.
-- The three places marked CHAIN 1-3 carry the previous pair as the guard and the regenerated pair as the result.

PRAGMA foreign_keys=OFF;
BEGIN IMMEDIATE;

-- Refuses to run twice or against the wrong database: the INSERT fails on the CHECK unless exactly the previous upgrade's migrations are recorded.
CREATE TEMP TABLE _upgrade_guard (ok INTEGER CHECK (ok = 1));
INSERT INTO _upgrade_guard SELECT (SELECT group_concat(MigrationId, ',') FROM (SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId))
    = '20261126235833_Schema,20261126235845_Triggers';   -- CHAIN 1
DROP TABLE _upgrade_guard;

-- >>> UPGRADE 019 (REOS-167, D61): email delivery. TABLES. Delimited so a merge with another session's block is a paste. >>>
-- Email is a channel of the notification path, not a second source of truth: the in-app Notifications row is always written, and these tables only record what was (or was not) mailed. Off by default (Notifications:Smtp:Enabled): with the flag off nothing is queued here.
-- No provider secret is stored in any of them (a password or API key lives in a Data Protection file, CLAUDE.md rule 11). Bodies are built from templates out of structure only (workspace, release key and title, kind, times, a link): never ticket summaries, notes or customer data (D12).
CREATE TABLE EmailOutbox (                                -- the outbox AND the send log: one row per message. At-least-once: Pending until the provider accepts it, retried with backoff, DeadLetter when the attempts are used up; a bounce or complaint turns a Sent row into Bounced
    Id TEXT PRIMARY KEY,
    WorkspaceId TEXT NOT NULL DEFAULT 'default' REFERENCES Workspaces(Id),
    UserId TEXT REFERENCES Users(Id),                     -- the person it is for; NULL for a test to an address typed by an administrator
    ToAddress TEXT NOT NULL COLLATE NOCASE CHECK (length(ToAddress) BETWEEN 3 AND 320 AND ToAddress GLOB '*@*' AND ToAddress NOT GLOB '*[ <>,;"]*'),
    Category TEXT NOT NULL CHECK (Category IN ('ApprovalRequested','DecisionMade','EscalationLevel1','EscalationLevel2','DailyDigest','Test')),   -- also the template: one template per category
    Subject TEXT NOT NULL CHECK (length(Subject) BETWEEN 1 AND 200 AND Subject NOT GLOB '*[' || char(10) || char(13) || ']*'),
    BodyText TEXT NOT NULL CHECK (length(BodyText) BETWEEN 1 AND 20000),
    BodyHtml TEXT NOT NULL CHECK (length(BodyHtml) BETWEEN 1 AND 40000),
    EntityType TEXT CHECK (EntityType IS NULL OR length(EntityType) BETWEEN 1 AND 40),   -- what the message is about: a reference, no foreign key (the log outlives what it describes)
    EntityId TEXT CHECK (EntityId IS NULL OR length(EntityId) BETWEEN 1 AND 64),
    DedupKey TEXT CHECK (DedupKey IS NULL OR length(DedupKey) BETWEEN 1 AND 100),   -- makes a message that must exist once (a person's digest for a day) idempotent
    Status TEXT NOT NULL DEFAULT 'Pending' CHECK (Status IN ('Pending','Sent','Bounced','DeadLetter','Suppressed')),   -- Suppressed = the address is on the suppression list, so it was recorded but never sent
    AttemptCount INTEGER NOT NULL DEFAULT 0 CHECK (AttemptCount >= 0),
    NextAttemptAt TEXT,                                   -- set while Pending: the earliest time the sender may try again
    LastAttemptAt TEXT,
    LastError TEXT CHECK (LastError IS NULL OR length(LastError) <= 500),   -- a short reason; never a credential
    Provider TEXT CHECK (Provider IS NULL OR length(Provider) BETWEEN 1 AND 40),   -- which provider accepted it
    ProviderMessageId TEXT CHECK (ProviderMessageId IS NULL OR length(ProviderMessageId) BETWEEN 1 AND 200),   -- how a later bounce or complaint finds the row
    SentAt TEXT,
    BouncedAt TEXT,
    FinishedAt TEXT,                                      -- when it stopped being Pending
    CreatedAt TEXT NOT NULL,
    Version INTEGER NOT NULL DEFAULT 1,
    CHECK ((Status = 'Pending') = (NextAttemptAt IS NOT NULL)),
    CHECK ((Status = 'Pending') = (FinishedAt IS NULL)),
    CHECK (Status NOT IN ('Sent') OR SentAt IS NOT NULL),
    CHECK ((Status = 'Bounced') = (BouncedAt IS NOT NULL))
);

CREATE TABLE EmailSuppressions (                          -- addresses that are never mailed again from a workspace until an administrator lifts the entry: a hard bounce, a spam complaint, or one added by hand. Removing one is a delete (audited)
    Id TEXT PRIMARY KEY,
    WorkspaceId TEXT NOT NULL DEFAULT 'default' REFERENCES Workspaces(Id),
    Address TEXT NOT NULL COLLATE NOCASE CHECK (length(Address) BETWEEN 3 AND 320 AND Address GLOB '*@*' AND Address NOT GLOB '*[ <>,;"]*'),
    Reason TEXT NOT NULL CHECK (Reason IN ('Bounce','Complaint','Manual')),
    Detail TEXT CHECK (Detail IS NULL OR length(Detail) <= 500),
    SourceMessageId TEXT CHECK (SourceMessageId IS NULL OR length(SourceMessageId) BETWEEN 1 AND 64),   -- the EmailOutbox row that caused it; no foreign key
    CreatedByUserId TEXT REFERENCES Users(Id),            -- NULL = the system (a bounce)
    CreatedAt TEXT NOT NULL,
    Version INTEGER NOT NULL DEFAULT 1,
    UNIQUE (WorkspaceId, Address)
);

CREATE TABLE NotificationPreferences (                    -- a person's choice per kind of notification and channel. No row means the default (email immediately, team webhook off). The in-app inbox is always on, so it has no row: escalations and My work depend on it
    Id TEXT PRIMARY KEY,
    WorkspaceId TEXT NOT NULL DEFAULT 'default' REFERENCES Workspaces(Id),
    UserId TEXT NOT NULL REFERENCES Users(Id) ON DELETE CASCADE,
    Category TEXT NOT NULL CHECK (Category IN ('ApprovalRequested','DecisionMade','EscalationLevel1','EscalationLevel2')),
    Channel TEXT NOT NULL CHECK (Channel IN ('Email','Webhook')),
    Mode TEXT NOT NULL CHECK (Mode IN ('Off','Immediate','Daily')),   -- Daily = in the person's once-a-day digest; email only
    UpdatedAt TEXT NOT NULL,
    Version INTEGER NOT NULL DEFAULT 1,
    CHECK (Channel = 'Email' OR Mode <> 'Daily'),
    UNIQUE (WorkspaceId, UserId, Category, Channel)
);
-- <<< UPGRADE 019 TABLES <<<

-- >>> UPGRADE 019 (REOS-167, D61): email delivery. TRIGGERS, in file order (SQLite fires same-event triggers newest-first, so the rule written last is the one reported first). >>>
-- The rules the services check first and the triggers back up for any other writer (DbRule 422):
--   1  The person a message or preference is for is a member of its workspace    2  Nothing moves workspace    3  A queued message (who, what, which template) never changes
--   4  A message that stopped being Pending is final, except that a Sent one can later be marked Bounced (a bounce or complaint arrives after the provider accepted it)
CREATE TRIGGER trg_Ws_EmailOutbox_Insert BEFORE INSERT ON EmailOutbox
BEGIN
    SELECT RAISE(ABORT, 'The recipient is not a member of this workspace')
     WHERE NEW.UserId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM WorkspaceMembers m WHERE m.UserId = NEW.UserId AND m.WorkspaceId = NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_EmailOutbox_Update BEFORE UPDATE OF WorkspaceId, UserId ON EmailOutbox WHEN NEW.WorkspaceId IS NOT OLD.WorkspaceId OR NEW.UserId IS NOT OLD.UserId
BEGIN SELECT RAISE(ABORT, 'A row cannot move to another workspace'); END;
CREATE TRIGGER trg_Ws_EmailSuppressions_Update BEFORE UPDATE OF WorkspaceId ON EmailSuppressions WHEN NEW.WorkspaceId IS NOT OLD.WorkspaceId
BEGIN SELECT RAISE(ABORT, 'A row cannot move to another workspace'); END;
CREATE TRIGGER trg_Ws_NotificationPreferences_Insert BEFORE INSERT ON NotificationPreferences
BEGIN
    SELECT RAISE(ABORT, 'The person is not a member of this workspace')
     WHERE NOT EXISTS (SELECT 1 FROM WorkspaceMembers m WHERE m.UserId = NEW.UserId AND m.WorkspaceId = NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_NotificationPreferences_Update BEFORE UPDATE OF WorkspaceId, UserId ON NotificationPreferences WHEN NEW.WorkspaceId IS NOT OLD.WorkspaceId OR NEW.UserId IS NOT OLD.UserId
BEGIN SELECT RAISE(ABORT, 'A row cannot move to another workspace'); END;
CREATE TRIGGER trg_Email_MessageIsImmutable BEFORE UPDATE ON EmailOutbox
WHEN NEW.ToAddress IS NOT OLD.ToAddress OR NEW.Category IS NOT OLD.Category OR NEW.Subject IS NOT OLD.Subject OR NEW.BodyText IS NOT OLD.BodyText OR NEW.BodyHtml IS NOT OLD.BodyHtml
  OR NEW.EntityType IS NOT OLD.EntityType OR NEW.EntityId IS NOT OLD.EntityId OR NEW.DedupKey IS NOT OLD.DedupKey OR NEW.CreatedAt IS NOT OLD.CreatedAt
BEGIN SELECT RAISE(ABORT, 'A queued email does not change; only its delivery state does'); END;
-- Written last, so a finished message reports this whatever else the update touched
CREATE TRIGGER trg_Email_FinishedIsFinal BEFORE UPDATE OF Status ON EmailOutbox
WHEN NEW.Status IS NOT OLD.Status AND (OLD.Status IN ('Bounced','DeadLetter','Suppressed') OR (OLD.Status = 'Sent' AND NEW.Status <> 'Bounced'))
BEGIN SELECT RAISE(ABORT, 'A finished email does not change state again'); END;
-- <<< UPGRADE 019 TRIGGERS <<<

-- >>> UPGRADE 019 (REOS-167, D61): email delivery. INDEXES. >>>
CREATE INDEX IX_EmailOutbox_Due ON EmailOutbox(NextAttemptAt) WHERE Status = 'Pending';
CREATE INDEX IX_EmailOutbox_Workspace ON EmailOutbox(WorkspaceId, Status, CreatedAt);
CREATE UNIQUE INDEX UX_EmailOutbox_Dedup ON EmailOutbox(WorkspaceId, DedupKey) WHERE DedupKey IS NOT NULL;
-- <<< UPGRADE 019 INDEXES <<<

-- The app's startup migration must see the new migrations as applied.
DELETE FROM __EFMigrationsHistory WHERE MigrationId IN ('20261126235833_Schema', '20261126235845_Triggers');   -- CHAIN 2
INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES   -- CHAIN 3
    ('20261127183701_Schema', '10.0.12'),
    ('20261127183710_Triggers', '10.0.12');

COMMIT;
PRAGMA foreign_keys=ON;
PRAGMA foreign_key_check;
PRAGMA integrity_check;
