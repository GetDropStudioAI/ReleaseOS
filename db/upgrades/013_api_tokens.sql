-- Upgrade 013 (REOS-162, REOS-163): API tokens for the public API and the idempotency keys of its POSTs.
-- From: migrations 20261025034543_Schema + 20261025034602_Triggers (after 012).
-- To:   migrations 20261125184717_Schema + 20261125184729_Triggers.
-- Run with the app stopped and after a backup (docs/RUNBOOK_OPERATIONS.md section 8):  sqlite3 releaseos.db < db/upgrades/013_api_tokens.sql
-- One transaction. Text below is verbatim from db/schema.sql:
--  * New tables: ApiTokens and IdempotencyKeys (empty: there is no token until an administrator makes one), with their two indexes.
--  * Four triggers are new (trg_Ws_ApiTokens_Insert/Update, trg_Ws_IdempotencyKeys_Insert/Update). They are on the new tables only, so no existing trigger changes and none is
--    dropped or created again; same-event triggers fire newest-first, but each of these tables has exactly its own.
-- No existing row is read or changed.

PRAGMA foreign_keys=OFF;
BEGIN IMMEDIATE;

-- Refuses to run twice or against the wrong database: the INSERT fails on the CHECK unless exactly 012's migrations are recorded.
CREATE TEMP TABLE _upgrade_guard (ok INTEGER CHECK (ok = 1));
INSERT INTO _upgrade_guard SELECT (SELECT group_concat(MigrationId, ',') FROM (SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId))
    = '20261025034543_Schema,20261025034602_Triggers';
DROP TABLE _upgrade_guard;

CREATE TABLE ApiTokens (                                  -- REOS-162: scoped service tokens for the public API (/api/public/v1). Only the SHA-256 is kept (like IngestKeys): the token is shown once, at creation or rotation
    Id TEXT PRIMARY KEY,
    Name TEXT NOT NULL CHECK (length(trim(Name)) BETWEEN 1 AND 100),
    TokenPrefix TEXT NOT NULL CHECK (length(TokenPrefix) BETWEEN 4 AND 12),   -- the first characters, so a person can tell tokens apart; not enough to use
    TokenSha256 TEXT NOT NULL UNIQUE CHECK (length(TokenSha256) = 64 AND TokenSha256 NOT GLOB '*[^0-9a-f]*'),
    Scopes TEXT NOT NULL CHECK (length(Scopes) BETWEEN 1 AND 100 AND Scopes NOT GLOB '*[^a-z,]*' AND Scopes NOT GLOB ',*' AND Scopes NOT GLOB '*,' AND Scopes NOT GLOB '*,,*'),   -- comma list from read, write, approvals, deployments (the service checks the words)
    ExpiresAt TEXT,                                       -- NULL = does not expire
    OwnerUserId TEXT NOT NULL REFERENCES Users(Id),       -- the person the token acts as: their role in this workspace, as it is now, limits it; the audit actor
    CreatedAt TEXT NOT NULL,
    RotatedAt TEXT,
    LastUsedAt TEXT,                                      -- written at most once a minute, without a Version bump: last write wins
    RevokedAt TEXT,
    RevokedByUserId TEXT REFERENCES Users(Id),
    Version INTEGER NOT NULL DEFAULT 1,
    WorkspaceId TEXT NOT NULL DEFAULT 'default' REFERENCES Workspaces(Id),
    CHECK ((RevokedAt IS NULL) = (RevokedByUserId IS NULL)),
    CHECK (ExpiresAt IS NULL OR ExpiresAt > CreatedAt)
);
CREATE INDEX IX_ApiTokens_Workspace ON ApiTokens(WorkspaceId, RevokedAt);

CREATE TABLE IdempotencyKeys (                            -- REOS-163: a POST to the public API with an Idempotency-Key; a replay of the same key and request returns the stored result and runs nothing
    Id TEXT PRIMARY KEY,
    ApiTokenId TEXT NOT NULL REFERENCES ApiTokens(Id) ON DELETE CASCADE,
    KeyValue TEXT NOT NULL CHECK (length(KeyValue) BETWEEN 1 AND 255),
    RequestSha256 TEXT NOT NULL CHECK (length(RequestSha256) = 64 AND RequestSha256 NOT GLOB '*[^0-9a-f]*'),   -- method, path, query and body: the same key with another request is refused
    StatusCode INTEGER CHECK (StatusCode IS NULL OR StatusCode BETWEEN 100 AND 599),   -- NULL while the first request is still running
    ContentType TEXT,
    ResponseBody TEXT CHECK (ResponseBody IS NULL OR length(ResponseBody) <= 4194304),
    CreatedAt TEXT NOT NULL,
    ExpiresAt TEXT NOT NULL,
    WorkspaceId TEXT NOT NULL DEFAULT 'default' REFERENCES Workspaces(Id),
    UNIQUE (ApiTokenId, KeyValue),
    CHECK ((StatusCode IS NULL) = (ResponseBody IS NULL))
);
CREATE INDEX IX_IdempotencyKeys_Expires ON IdempotencyKeys(ExpiresAt);

CREATE TRIGGER trg_Ws_ApiTokens_Insert BEFORE INSERT ON ApiTokens
BEGIN
    SELECT RAISE(ABORT, 'The token owner is not a member of this workspace')
     WHERE NOT EXISTS (SELECT 1 FROM WorkspaceMembers m WHERE m.UserId = NEW.OwnerUserId AND m.WorkspaceId = NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_ApiTokens_Update BEFORE UPDATE OF WorkspaceId, OwnerUserId ON ApiTokens
BEGIN
    SELECT RAISE(ABORT, 'A row cannot move to another workspace') WHERE NEW.WorkspaceId IS NOT OLD.WorkspaceId;
    SELECT RAISE(ABORT, 'The token owner is not a member of this workspace')
     WHERE NEW.OwnerUserId IS NOT OLD.OwnerUserId AND NOT EXISTS (SELECT 1 FROM WorkspaceMembers m WHERE m.UserId = NEW.OwnerUserId AND m.WorkspaceId = NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_IdempotencyKeys_Insert BEFORE INSERT ON IdempotencyKeys
BEGIN
    SELECT RAISE(ABORT, 'The token belongs to another workspace')
     WHERE EXISTS (SELECT 1 FROM ApiTokens x WHERE x.Id = NEW.ApiTokenId AND x.WorkspaceId IS NOT NEW.WorkspaceId);
END;
CREATE TRIGGER trg_Ws_IdempotencyKeys_Update BEFORE UPDATE OF WorkspaceId, ApiTokenId ON IdempotencyKeys WHEN NEW.WorkspaceId IS NOT OLD.WorkspaceId OR NEW.ApiTokenId IS NOT OLD.ApiTokenId
BEGIN SELECT RAISE(ABORT, 'A row cannot move to another workspace'); END;

-- The app's startup migration must see the new migrations as applied.
DELETE FROM __EFMigrationsHistory WHERE MigrationId IN ('20261025034543_Schema', '20261025034602_Triggers');
INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES
    ('20261125184717_Schema', '10.0.12'),
    ('20261125184729_Triggers', '10.0.12');

COMMIT;
PRAGMA foreign_keys=ON;
PRAGMA foreign_key_check;
PRAGMA integrity_check;
