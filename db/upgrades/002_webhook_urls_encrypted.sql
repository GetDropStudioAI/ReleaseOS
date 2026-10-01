-- Upgrade 002 (REOS-73, Q-053e): webhook URLs encrypted at rest.
-- From: migrations 20260930234604_Schema + 20260930234616_Triggers (after 001).
-- To:   migrations 20261001030509_Schema + 20261001030524_Triggers.
-- Run with the app stopped and after a backup (docs/RUNBOOK_OPERATIONS.md §8):  sqlite3 releaseos.db < db/upgrades/002_webhook_urls_encrypted.sql
-- The table change itself cannot be done in SQL: encrypting each URL needs the Data Protection key ring. So this script only records the new
-- migrations, and the app converts WebhookDestinations (plain Url -> Host, ProtectedUrl, UrlHmac, rebuilt with schema.sql's DDL) at its next start,
-- in one transaction, before anything reads the table (WebhookUrlProtectionUpgrade, logged and audited per destination).

BEGIN IMMEDIATE;

-- Refuses to run twice or against the wrong database: the INSERT fails on the CHECK unless exactly 001's migrations are recorded.
CREATE TEMP TABLE _upgrade_guard (ok INTEGER CHECK (ok = 1));
INSERT INTO _upgrade_guard SELECT (SELECT group_concat(MigrationId, ',') FROM (SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId))
    = '20260930234604_Schema,20260930234616_Triggers';
DROP TABLE _upgrade_guard;

DELETE FROM __EFMigrationsHistory WHERE MigrationId IN ('20260930234604_Schema', '20260930234616_Triggers');
INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES
    ('20261001030509_Schema', '10.0.12'),
    ('20261001030524_Triggers', '10.0.12');

COMMIT;
PRAGMA integrity_check;
