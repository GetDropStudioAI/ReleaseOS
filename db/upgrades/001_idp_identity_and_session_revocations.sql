-- Upgrade 001 (REOS-61/62): Users bound to the identity provider's issuer + subject; signed-out sessions persisted.
-- From: the schema created by migrations 20260929110130_Schema + 20260929110136_Triggers (db/upgrades/baseline/schema-20260929.sql).
-- To:   db/schema.sql as of migrations 20260930234604_Schema + 20260930234616_Triggers.
-- Run with the app stopped and after a backup (docs/RUNBOOK_OPERATIONS.md §8):  sqlite3 releaseos.db < db/upgrades/001_idp_identity_and_session_revocations.sql
-- The whole script is one transaction: it either applies completely or leaves the database as it was.
-- Users gains a table CHECK, which SQLite can only add by rebuilding the table (https://sqlite.org/lang_altertable.html#otheralter).
-- legacy_alter_table keeps the rename from re-parsing the triggers that read Users while the old table is gone.

PRAGMA foreign_keys=OFF;
PRAGMA legacy_alter_table=ON;
BEGIN IMMEDIATE;

-- Refuses to run twice or against the wrong database: the INSERT fails on the CHECK unless exactly the two baseline migrations are recorded.
CREATE TEMP TABLE _upgrade_guard (ok INTEGER CHECK (ok = 1));
INSERT INTO _upgrade_guard SELECT (SELECT group_concat(MigrationId, ',') FROM (SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId))
    = '20260929110130_Schema,20260929110136_Triggers';
DROP TABLE _upgrade_guard;

CREATE TABLE Users_upgrade (
    Id TEXT PRIMARY KEY,
    Email TEXT NOT NULL UNIQUE COLLATE NOCASE,          -- contact and display data; organisation sign-in matches IdpIssuer + IdpSubject
    DisplayName TEXT NOT NULL,
    Role TEXT NOT NULL CHECK (Role IN ('Viewer','ReleaseManager','RTE','GovernanceOfficer')),
    Handle TEXT UNIQUE COLLATE NOCASE,                   -- optional @handle for the bulk parser
    IsActive INTEGER NOT NULL DEFAULT 1 CHECK (IsActive IN (0,1)),
    IdpIssuer TEXT,                                      -- OIDC 'iss' of the bound identity; NULL until the first organisation sign-in (Q-SEC-B8)
    IdpSubject TEXT,                                     -- OIDC 'sub' at that issuer: who the person is (email can change or be reissued)
    Version INTEGER NOT NULL DEFAULT 1,
    CHECK ((IdpIssuer IS NULL) = (IdpSubject IS NULL))
);
INSERT INTO Users_upgrade (Id, Email, DisplayName, Role, Handle, IsActive, Version)
    SELECT Id, Email, DisplayName, Role, Handle, IsActive, Version FROM Users;
DROP TABLE Users;
ALTER TABLE Users_upgrade RENAME TO Users;
CREATE UNIQUE INDEX UX_Users_IdpIdentity ON Users(IdpIssuer, IdpSubject) WHERE IdpSubject IS NOT NULL;

CREATE TABLE SessionRevocations (                         -- signed-out session ids, refused until the session would have ended anyway (Q-SEC-B5)
    SessionId TEXT PRIMARY KEY,                           -- random id stamped in the cookie ticket at sign-in
    UserId TEXT REFERENCES Users(Id) ON DELETE CASCADE,
    RevokedAt TEXT NOT NULL,
    ExpiresAt TEXT NOT NULL                               -- sign-in time + Auth:Session:AbsoluteHours; the row is pruned after it
);
CREATE INDEX IX_SessionRevocations_Expiry ON SessionRevocations(ExpiresAt);

-- The app's startup migration must see the new migrations as applied (they would otherwise try to create every table again).
DELETE FROM __EFMigrationsHistory WHERE MigrationId IN ('20260929110130_Schema', '20260929110136_Triggers');
INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES
    ('20260930234604_Schema', '10.0.12'),
    ('20260930234616_Triggers', '10.0.12');

COMMIT;
PRAGMA legacy_alter_table=OFF;
PRAGMA foreign_keys=ON;
PRAGMA foreign_key_check;
PRAGMA integrity_check;
