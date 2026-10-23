"""Manual upgrade scripts (db/upgrades/NNN_*.sql, Q-SEC-B8m): a database built from the baseline schema, with data in it, and upgraded with every script
in order must end with exactly db/schema.sql's tables, columns, CHECKs, indexes and triggers, keep its rows, and pass the foreign-key and integrity checks.
The EF side (the app's startup migration is a no-op afterwards) is UpgradeScriptTests in ReleaseMgmt.Infrastructure.Tests."""
import pathlib, re, sqlite3, sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
BASELINE = ROOT / "db/upgrades/baseline/schema-20260929.sql"
SCRIPTS = sorted((ROOT / "db/upgrades").glob("[0-9][0-9][0-9]_*.sql"))
OLD_MIGRATIONS = ("20260929110130_Schema", "20260929110136_Triggers")
# Tables a script cannot convert because the change needs the app's key ring: the app converts them at its next start (002: WebhookDestinations,
# WebhookUrlProtectionUpgrade). Here they must still be in the shape the app expects to convert; UpgradePathTests boots the app and checks the end result.
APP_CONVERTED = {"WebhookDestinations": "Url"}


def normalise(sql):
    sql = re.sub(r"--[^\n]*", "", sql or "")
    sql = re.sub(r"\s+", " ", sql).strip()
    sql = re.sub(r" ?([(),]) ?", r"\1", sql)   # ALTER TABLE ... ADD COLUMN writes "\n, col)" where a fresh CREATE has ",\n    col\n)"
    return re.sub(r'^(CREATE TABLE) "([A-Za-z_]+)"', r"\1 \2", sql)   # ALTER TABLE ... RENAME quotes the new name


def shape(db):
    rows = db.execute("SELECT type, name, tbl_name, sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' AND name <> '__EFMigrationsHistory' ORDER BY type, name")
    return {(t, n): (tbl, normalise(s)) for t, n, tbl, s in rows if tbl not in APP_CONVERTED}


def columns(db):
    tables = [r[0] for r in db.execute("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name <> '__EFMigrationsHistory'")]
    return {t: [tuple(c[1:]) for c in db.execute(f"PRAGMA table_xinfo({t})")] for t in tables if t not in APP_CONVERTED}


def baseline_db():
    db = sqlite3.connect(":memory:", isolation_level=None)
    db.executescript(BASELINE.read_text())
    db.execute("CREATE TABLE __EFMigrationsHistory (MigrationId TEXT NOT NULL PRIMARY KEY, ProductVersion TEXT NOT NULL)")
    db.executemany("INSERT INTO __EFMigrationsHistory VALUES (?, '10.0.12')", [(m,) for m in OLD_MIGRATIONS])
    db.execute("PRAGMA foreign_keys=ON")
    db.execute("INSERT INTO Users (Id, Email, DisplayName, Role, Handle) VALUES ('u1', 'gov@corp.example', 'Gov', 'GovernanceOfficer', 'gov')")
    db.execute("INSERT INTO Users (Id, Email, DisplayName, Role, IsActive, Version) VALUES ('u2', 'old@corp.example', 'Old', 'Viewer', 0, 4)")
    db.execute("INSERT INTO IcsTokens (Id, UserId, TokenSha256, CreatedAt) VALUES ('t1', 'u1', printf('%064d', 0), '2026-09-01T00:00:00Z')")
    db.executescript("""
        INSERT INTO Teams (Id, Handle, Name, Version) VALUES ('tm1', 'ops', 'Ops', 3);
        INSERT INTO Holidays (Day, Name) VALUES ('2026-12-25', 'Christmas');
        INSERT INTO TrainTemplates (Id, Name, Status, ApprovedByUserId, ApprovedAt) VALUES ('tp1', 'Monthly', 'Approved', 'u1', '2026-09-01T00:00:00Z');
        INSERT INTO CommTemplateLibrary (Id, TemplateType, Name, Audience, SubjectLine, MarkdownBody) VALUES ('lib1', 'Tminus28', 'T-28', 'All', 's', 'b');
        INSERT INTO ConnectorState (SourceSystem, BaseUrl, ConsecutiveFailures) VALUES ('Jira', 'https://jira.example', 2);
        INSERT INTO ReleaseTrains (Id, Title, TemplateId, TargetReleaseDate, CreatedAt, UpdatedAt) VALUES ('r1', 'R26.10', 'tp1', '2026-10-30', '2026-09-01T00:00:00Z', '2026-09-01T00:00:00Z');
        INSERT INTO StageGates (Id, ReleaseTrainId, GateName, SequenceOrder, OffsetDays, DueOn, OwnerTeamId) VALUES ('g1', 'r1', 'CAB', 1, 2, '2026-10-28', 'tm1');
        INSERT INTO FreezeWindows (Id, Name, StartsAt, EndsAt, CreatedByUserId) VALUES ('f1', 'Year end', '2026-12-20T00:00:00Z', '2027-01-02T00:00:00Z', 'u1');
        INSERT INTO AuditEvents (OccurredAt, ActorUserId, ReleaseTrainId, EntityType, EntityId, Action) VALUES ('2026-09-01T00:00:00Z', 'u1', 'r1', 'Train', 'r1', 'Created');
    """)
    return db


failures = []


def check(cond, label):
    print(("ok   " if cond else "FAIL ") + label)
    if not cond: failures.append(label)


expected = sqlite3.connect(":memory:")
expected.executescript((ROOT / "db/schema.sql").read_text())

db = baseline_db()
for s in SCRIPTS:
    db.executescript(s.read_text())
check(shape(db) == shape(expected), "upgraded database has exactly db/schema.sql's tables, indexes and triggers (normalised SQL)")
for k in sorted(set(shape(db)) ^ set(shape(expected))): print("     differs:", k)
for k in sorted(k for k in set(shape(db)) & set(shape(expected)) if shape(db)[k] != shape(expected)[k]): print("     differs:", k)
for t, legacy in APP_CONVERTED.items():
    check(legacy in [c[1] for c in db.execute(f"PRAGMA table_xinfo({t})")], f"{t} left in the shape the app converts at start (has {legacy})")
check(columns(db) == columns(expected), "every table has the same columns, in the same order, with the same types and defaults")
check(db.execute("SELECT Id, Email, Role, Handle, IsActive, Version, IdpIssuer, IdpSubject FROM Users ORDER BY Id").fetchall()
      == [("u1", "gov@corp.example", "GovernanceOfficer", "gov", 1, 1, None, None), ("u2", "old@corp.example", "Viewer", None, 0, 4, None, None)],
      "existing users kept, unbound")
check(db.execute("SELECT UserId FROM IcsTokens").fetchall() == [("u1",)], "rows that reference Users kept")
# Upgrade 004 (REOS-104): every existing row is in the default workspace, every user a member of it with their role, and the rebuilt tables keep their rows
roots = ["Teams", "Holidays", "TrainTemplates", "CommTemplateLibrary", "ConnectorState", "ReleaseTrains", "FreezeWindows", "IcsTokens", "AuditEvents"]
check(all(db.execute(f"SELECT count(*) = sum(WorkspaceId = 'default') AND count(*) > 0 FROM {t}").fetchone()[0] for t in roots),
      "every existing root row is in the default workspace")
check(db.execute("SELECT UserId, Role FROM WorkspaceMembers WHERE WorkspaceId = 'default' ORDER BY UserId").fetchall()
      == [("u1", "GovernanceOfficer"), ("u2", "Viewer")], "every existing user is a member of the default workspace with their role (inactive ones too)")
check(db.execute("SELECT Handle, Version FROM Teams").fetchall() == [("ops", 3)]
      and db.execute("SELECT BaseUrl, ConsecutiveFailures FROM ConnectorState").fetchall() == [("https://jira.example", 2)]
      and db.execute("SELECT Status, ApprovedByUserId FROM TrainTemplates").fetchall() == [("Approved", "u1")],
      "rebuilt tables keep their rows and values")
check(db.execute("SELECT OwnerTeamId FROM StageGates WHERE Id = 'g1'").fetchall() == [("tm1",)] and
      db.execute("SELECT TemplateId FROM ReleaseTrains WHERE Id = 'r1'").fetchall() == [("tp1",)], "references to rebuilt tables kept")
try:
    db.execute("INSERT INTO Teams (Id, Handle, Name, WorkspaceId) VALUES ('tm9', 'ops', 'Ops', 'nowhere')")
    check(False, "foreign keys enforced again after the upgrade (unknown workspace refused)")
except sqlite3.IntegrityError as e:
    check("FOREIGN KEY" in str(e), "foreign keys enforced again after the upgrade (unknown workspace refused)")
try:
    db.execute("UPDATE ReleaseTrains SET WorkspaceId = 'nowhere' WHERE Id = 'r1'")
    check(False, "workspace boundary triggers active after the upgrade")
except sqlite3.IntegrityError as e:
    check("cannot move to another workspace" in str(e), "workspace boundary triggers active after the upgrade")
check(db.execute("PRAGMA foreign_key_check").fetchall() == [], "foreign_key_check clean")
check(db.execute("PRAGMA integrity_check").fetchone()[0] == "ok", "integrity_check ok")
check(db.execute("PRAGMA legacy_alter_table").fetchone()[0] == 0 and db.execute("PRAGMA foreign_keys").fetchone()[0] == 1, "pragmas restored")
names = [r[0] for r in db.execute("SELECT MigrationId FROM __EFMigrationsHistory ORDER BY 1")]
check(not any(m in names for m in OLD_MIGRATIONS) and len(names) == 2, f"migration history moved to the current migrations ({names})")
try:
    db.execute("UPDATE Users SET IdpIssuer = 'https://idp/' WHERE Id = 'u1'")
    check(False, "CHECK: issuer without subject refused")
except sqlite3.IntegrityError:
    check(True, "CHECK: issuer without subject refused")
try:   # a trigger that reads Users still works after the rebuild (certification needs a Governance Officer)
    db.execute("SELECT Role FROM Users WHERE Id='u1'").fetchone()
    check(True, "Users readable after rebuild")
except sqlite3.Error as e:
    check(False, f"Users readable after rebuild: {e}")

# Upgrade 005 (REOS-105): a user left without a membership after 004 (a raw-SQL import) keeps their role in the default workspace, and the role
# triggers read the membership role from then on
mid = baseline_db()
for s in SCRIPTS:
    if s.name.startswith("005_"):
        mid.execute("INSERT INTO Users (Id, Email, DisplayName, Role) VALUES ('u3', 'late@corp.example', 'Late', 'GovernanceOfficer')")
    mid.executescript(s.read_text())
check(mid.execute("SELECT Role FROM WorkspaceMembers WHERE WorkspaceId = 'default' AND UserId = 'u3'").fetchall() == [("GovernanceOfficer",)],
      "005: a user without a membership becomes a member of the default workspace with their role")
mid.execute("UPDATE WorkspaceMembers SET Role = 'Viewer' WHERE UserId = 'u1'")
mid.execute("INSERT INTO GateWaivers (Id, StageGateId, Reason, RequestedByUserId, RequestedAt) VALUES ('wv1', 'g1', 'Vendor slipped; accepted risk in writing', 'u3', '2026-09-01T00:00:00Z')")
try:
    mid.execute("UPDATE GateWaivers SET ApprovedByUserId = 'u1', ApprovedAt = '2026-09-01T00:00:00Z' WHERE Id = 'wv1'")
    check(False, "005: the waiver-approver trigger reads the membership role, not Users.Role")
except sqlite3.IntegrityError as e:
    check("approved by a Governance Officer" in str(e), "005: the waiver-approver trigger reads the membership role, not Users.Role")

again = baseline_db()
for s in SCRIPTS:
    again.executescript(s.read_text())
before = shape(again)
try:
    again.executescript(SCRIPTS[0].read_text())
    check(False, "running an upgrade twice is refused")
except sqlite3.IntegrityError:
    again.execute("ROLLBACK") if again.in_transaction else None
    check(shape(again) == before, "running an upgrade twice is refused and changes nothing")

print(f"\n{len(failures)} failure(s)")
sys.exit(1 if failures else 0)
