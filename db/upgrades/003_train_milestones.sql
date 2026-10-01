-- Upgrade 003 (REOS-84): train milestones, named key dates on a train.
-- From: migrations 20261001030509_Schema + 20261001030524_Triggers (after 002).
-- To:   migrations 20261001041930_Schema + 20261001041948_Triggers.
-- Run with the app stopped and after a backup (docs/RUNBOOK_OPERATIONS.md §8):  sqlite3 releaseos.db < db/upgrades/003_train_milestones.sql
-- Adds one table and its index, verbatim from db/schema.sql; no existing row changes. One transaction.

BEGIN IMMEDIATE;

-- Refuses to run twice or against the wrong database: the INSERT fails on the CHECK unless exactly 002's migrations are recorded.
CREATE TEMP TABLE _upgrade_guard (ok INTEGER CHECK (ok = 1));
INSERT INTO _upgrade_guard SELECT (SELECT group_concat(MigrationId, ',') FROM (SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId))
    = '20261001030509_Schema,20261001030524_Triggers';
DROP TABLE _upgrade_guard;

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
CREATE INDEX IX_Milestones_Train ON TrainMilestones(ReleaseTrainId, DueOn);

DELETE FROM __EFMigrationsHistory WHERE MigrationId IN ('20261001030509_Schema', '20261001030524_Triggers');
INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES
    ('20261001041930_Schema', '10.0.12'),
    ('20261001041948_Triggers', '10.0.12');

COMMIT;
PRAGMA foreign_key_check;
PRAGMA integrity_check;
