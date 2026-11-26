-- Upgrade 015 (REOS-164; D61): outbound webhooks (subscriptions, the outbox and delivery log, the attempt log).
-- From: migrations 20261126010001_Schema + 20261126010017_Triggers (after 014).
-- To:   migrations 20261126134858_Schema + 20261126134908_Triggers.
-- Run with the app stopped and after a backup (docs/RUNBOOK_OPERATIONS.md section 8):  sqlite3 releaseos.db < db/upgrades/015_outbound_webhooks.sql
-- One transaction. Text below is verbatim from db/schema.sql (the blocks delimited ">>> UPGRADE 015"):
--  * New tables: WebhookSubscriptions, WebhookDeliveries and WebhookDeliveryAttempts. They start EMPTY: there is no subscription until an administrator makes one, so nothing is posted anywhere after the upgrade.
--    No secret is stored in them: a subscription's signing secret lives in a Data Protection file under the credentials directory (CLAUDE.md rule 11).
--  * Five triggers are new (trg_Ws_WebhookSubscriptions_Update, trg_Ws_WebhookDeliveries_Insert/Update, trg_Ws_WebhookDeliveryAttempts_Insert/Update, trg_Webhook_FinishedDeliveryIsFinal) and four indexes. They are on the new tables only,
--    so no existing trigger changes and none is dropped or created again.
-- No existing row is read or changed. The Webhook source and DeliveryFailed kind of SyncAlerts were already allowed.

PRAGMA foreign_keys=OFF;
BEGIN IMMEDIATE;

-- Refuses to run twice or against the wrong database: the INSERT fails on the CHECK unless exactly 014's migrations are recorded.
CREATE TEMP TABLE _upgrade_guard (ok INTEGER CHECK (ok = 1));
INSERT INTO _upgrade_guard SELECT (SELECT group_concat(MigrationId, ',') FROM (SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId))
    = '20261126010001_Schema,20261126010017_Triggers';
DROP TABLE _upgrade_guard;

CREATE TABLE WebhookSubscriptions (                       -- where a workspace's events are posted. The address is a secret like WebhookDestinations.Url (Teams/Slack put a token in the path): Data Protection payload + keyed hash. The HMAC signing secret is NOT here at all: it lives in a Data Protection file (rule 11) and is shown once
    Id TEXT PRIMARY KEY,
    Name TEXT NOT NULL CHECK (length(trim(Name)) BETWEEN 1 AND 100),
    ProtectedUrl TEXT NOT NULL CHECK (length(ProtectedUrl) BETWEEN 1 AND 8192),
    UrlHmac TEXT NOT NULL CHECK (length(UrlHmac) = 64 AND UrlHmac NOT GLOB '*[^0-9a-f]*'),   -- keyed hash of the normalised URL: the same address twice in a workspace is refused
    Host TEXT NOT NULL CHECK (length(Host) BETWEEN 1 AND 255),   -- the only part of the address that screens, alerts and audit rows may show
    EventTypes TEXT NOT NULL CHECK (length(EventTypes) BETWEEN 1 AND 1000 AND EventTypes NOT GLOB '*[^a-z._,*]*' AND EventTypes NOT GLOB ',*' AND EventTypes NOT GLOB '*,' AND EventTypes NOT GLOB '*,,*'),   -- comma list from the catalogue in code (WebhookCatalog), or '*' for every event. The service checks the words
    Status TEXT NOT NULL DEFAULT 'Active' CHECK (Status IN ('Active','Paused')),   -- Paused keeps the subscription and its log but queues nothing new
    SecretVersion INTEGER NOT NULL DEFAULT 1 CHECK (SecretVersion >= 1),   -- how many secrets this subscription has had; sent as a header so a receiver knows which to verify with
    SecretRotatedAt TEXT,
    CreatedByUserId TEXT NOT NULL REFERENCES Users(Id),
    CreatedAt TEXT NOT NULL,
    LastChangedByUserId TEXT REFERENCES Users(Id),
    LastChangedAt TEXT,
    Version INTEGER NOT NULL DEFAULT 1,
    WorkspaceId TEXT NOT NULL DEFAULT 'default' REFERENCES Workspaces(Id),
    UNIQUE (WorkspaceId, UrlHmac)
);

CREATE TABLE WebhookDeliveries (                          -- the outbox AND the delivery log: one row per event per subscription, written in the transaction that writes the audit row (rule 3). At-least-once: Pending until a 2xx, retried with backoff, DeadLetter when the attempts are used up
    Id TEXT PRIMARY KEY,                                  -- the X-ReleaseOS-Delivery header: stable across retries, so a receiver can drop a repeat
    SubscriptionId TEXT NOT NULL REFERENCES WebhookSubscriptions(Id) ON DELETE CASCADE,
    EventId TEXT NOT NULL CHECK (length(EventId) BETWEEN 1 AND 64),   -- the event (the audit row it came from): the same for every subscription and for a replay
    EventType TEXT NOT NULL CHECK (length(EventType) BETWEEN 1 AND 60 AND EventType NOT GLOB '*[^a-z._]*'),
    AuditEventId INTEGER,                                 -- AuditEvents.Id; no foreign key, like ReleaseTrainId there: the log outlives what it describes
    PayloadJson TEXT NOT NULL CHECK (length(PayloadJson) BETWEEN 2 AND 65536),   -- the exact body that is signed and posted; never a secret
    Status TEXT NOT NULL DEFAULT 'Pending' CHECK (Status IN ('Pending','Delivered','DeadLetter')),
    AttemptCount INTEGER NOT NULL DEFAULT 0 CHECK (AttemptCount >= 0),
    NextAttemptAt TEXT,                                   -- set while Pending: the earliest time the sender may try again
    LastAttemptAt TEXT,
    LastStatusCode INTEGER CHECK (LastStatusCode IS NULL OR LastStatusCode BETWEEN 100 AND 599),
    LastError TEXT CHECK (LastError IS NULL OR length(LastError) <= 500),   -- a short reason without the address
    DeliveredAt TEXT,
    FinishedAt TEXT,                                      -- when it became Delivered or DeadLetter
    ReplayOfDeliveryId TEXT REFERENCES WebhookDeliveries(Id) ON DELETE SET NULL,   -- a replay is a NEW row (new delivery id, same event id) that points at the one it repeats
    CreatedAt TEXT NOT NULL,
    Version INTEGER NOT NULL DEFAULT 1,
    WorkspaceId TEXT NOT NULL DEFAULT 'default' REFERENCES Workspaces(Id),
    CHECK ((Status = 'Pending') = (NextAttemptAt IS NOT NULL)),
    CHECK ((Status = 'Delivered') = (DeliveredAt IS NOT NULL)),
    CHECK ((Status = 'Pending') = (FinishedAt IS NULL))
);

CREATE TABLE WebhookDeliveryAttempts (                    -- one row per try: what the receiver answered, how long it took. Never the address, never a body
    Id TEXT PRIMARY KEY,
    DeliveryId TEXT NOT NULL REFERENCES WebhookDeliveries(Id) ON DELETE CASCADE,
    AttemptNo INTEGER NOT NULL CHECK (AttemptNo >= 1),
    AttemptedAt TEXT NOT NULL,
    StatusCode INTEGER CHECK (StatusCode IS NULL OR StatusCode BETWEEN 100 AND 599),   -- NULL = no answer (timeout, refused, blocked)
    DurationMs INTEGER NOT NULL DEFAULT 0 CHECK (DurationMs >= 0),
    Error TEXT CHECK (Error IS NULL OR length(Error) <= 500),
    WorkspaceId TEXT NOT NULL DEFAULT 'default' REFERENCES Workspaces(Id),
    UNIQUE (DeliveryId, AttemptNo)
);

CREATE TRIGGER trg_Ws_WebhookSubscriptions_Update BEFORE UPDATE OF WorkspaceId ON WebhookSubscriptions WHEN NEW.WorkspaceId IS NOT OLD.WorkspaceId
BEGIN SELECT RAISE(ABORT, 'A row cannot move to another workspace'); END;
CREATE TRIGGER trg_Ws_WebhookDeliveries_Insert BEFORE INSERT ON WebhookDeliveries
BEGIN
    SELECT RAISE(ABORT, 'The subscription belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM WebhookSubscriptions x WHERE x.Id = NEW.SubscriptionId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_WebhookDeliveries_Update BEFORE UPDATE OF WorkspaceId, SubscriptionId ON WebhookDeliveries WHEN NEW.WorkspaceId IS NOT OLD.WorkspaceId OR NEW.SubscriptionId IS NOT OLD.SubscriptionId
BEGIN SELECT RAISE(ABORT, 'A row cannot move to another workspace'); END;
CREATE TRIGGER trg_Ws_WebhookDeliveryAttempts_Insert BEFORE INSERT ON WebhookDeliveryAttempts
BEGIN
    SELECT RAISE(ABORT, 'The delivery belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM WebhookDeliveries x WHERE x.Id = NEW.DeliveryId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_WebhookDeliveryAttempts_Update BEFORE UPDATE OF WorkspaceId, DeliveryId ON WebhookDeliveryAttempts WHEN NEW.WorkspaceId IS NOT OLD.WorkspaceId OR NEW.DeliveryId IS NOT OLD.DeliveryId
BEGIN SELECT RAISE(ABORT, 'A row cannot move to another workspace'); END;
-- A delivered or dead-lettered delivery is history. To send it again, replay it: that makes a new delivery row and leaves this one as it was.
CREATE TRIGGER trg_Webhook_FinishedDeliveryIsFinal BEFORE UPDATE OF Status ON WebhookDeliveries
WHEN OLD.Status IN ('Delivered','DeadLetter') AND NEW.Status IS NOT OLD.Status
BEGIN SELECT RAISE(ABORT, 'A finished webhook delivery does not change; replay it to send it again'); END;

CREATE INDEX IX_WebhookSubscriptions_Workspace ON WebhookSubscriptions(WorkspaceId, Status);
CREATE INDEX IX_WebhookDeliveries_Due ON WebhookDeliveries(NextAttemptAt) WHERE Status = 'Pending';
CREATE INDEX IX_WebhookDeliveries_Subscription ON WebhookDeliveries(SubscriptionId, CreatedAt);
CREATE INDEX IX_WebhookDeliveries_Workspace ON WebhookDeliveries(WorkspaceId, Status, CreatedAt);

-- The app's startup migration must see the new migrations as applied.
DELETE FROM __EFMigrationsHistory WHERE MigrationId IN ('20261126010001_Schema', '20261126010017_Triggers');
INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES
    ('20261126134858_Schema', '10.0.12'),
    ('20261126134908_Triggers', '10.0.12');

COMMIT;
PRAGMA foreign_keys=ON;
PRAGMA foreign_key_check;
PRAGMA integrity_check;
