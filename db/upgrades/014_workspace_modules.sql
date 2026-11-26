-- Upgrade 014 (REOS-174; D56, D57, D63): per-workspace add-on modules.
-- From: migrations 20261125184717_Schema + 20261125184729_Triggers (after 013).
-- To:   migrations 20261126010001_Schema + 20261126010017_Triggers.
-- Run with the app stopped and after a backup (docs/RUNBOOK_OPERATIONS.md section 8):  sqlite3 releaseos.db < db/upgrades/014_workspace_modules.sql
-- One transaction. Text below is verbatim from db/schema.sql:
--  * New table: WorkspaceModules (which add-on modules a workspace has switched on). It starts EMPTY: no row means a module is off, so every module is off after the upgrade.
--  * No trigger is new and no existing table changes, so nothing else is rebuilt.

PRAGMA foreign_keys=OFF;
BEGIN IMMEDIATE;

-- Refuses to run twice or against the wrong database: the INSERT fails on the CHECK unless exactly 013's migrations are recorded.
CREATE TEMP TABLE _upgrade_guard (ok INTEGER CHECK (ok = 1));
INSERT INTO _upgrade_guard SELECT (SELECT group_concat(MigrationId, ',') FROM (SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId))
    = '20261125184717_Schema,20261125184729_Triggers';
DROP TABLE _upgrade_guard;

CREATE TABLE WorkspaceModules (                           -- a module is OFF until a row says Enabled = 1; no row means off. The module's own data lives in its own tables and is kept when it is switched off
    WorkspaceId TEXT NOT NULL REFERENCES Workspaces(Id) ON DELETE CASCADE,
    ModuleKey TEXT NOT NULL CHECK (length(ModuleKey) BETWEEN 1 AND 40 AND ModuleKey NOT GLOB '*[^a-z0-9-]*'),   -- the registry of known keys is in code (ModuleCatalog: 'cab', 'ai'); a new module needs no schema change
    Enabled INTEGER NOT NULL DEFAULT 0 CHECK (Enabled IN (0, 1)),
    EnabledByUserId TEXT REFERENCES Users(Id),            -- who last enabled it; kept when it is disabled (the audit row says who disabled it)
    EnabledAt TEXT,                                       -- UTC, from the service clock; set whenever Enabled = 1
    PlanSource TEXT NOT NULL DEFAULT 'Default' CHECK (PlanSource IN ('Default','Plan')),   -- who allowed it: Default = no billing connected (IModuleEntitlements allows everything), Plan = the plan's entitlement
    LastChangedByUserId TEXT REFERENCES Users(Id),
    LastChangedAt TEXT,
    Version INTEGER NOT NULL DEFAULT 1,
    PRIMARY KEY (WorkspaceId, ModuleKey),
    CHECK (Enabled = 0 OR (EnabledByUserId IS NOT NULL AND EnabledAt IS NOT NULL))
);

-- The app's startup migration must see the new migrations as applied.
DELETE FROM __EFMigrationsHistory WHERE MigrationId IN ('20261125184717_Schema', '20261125184729_Triggers');
INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES
    ('20261126010001_Schema', '10.0.12'),
    ('20261126010017_Triggers', '10.0.12');

COMMIT;
PRAGMA foreign_keys=ON;
PRAGMA foreign_key_check;
PRAGMA integrity_check;
