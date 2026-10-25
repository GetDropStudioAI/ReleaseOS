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
        INSERT INTO ReleaseTrains (Id, Title, TargetReleaseDate, CreatedAt, UpdatedAt) VALUES ('r2', 'R26.11', '2026-11-30', '2026-09-02T00:00:00Z', '2026-09-02T00:00:00Z');
        INSERT INTO StageGates (Id, ReleaseTrainId, GateName, SequenceOrder, OffsetDays, DueOn, OwnerTeamId) VALUES ('g1', 'r1', 'CAB', 1, 2, '2026-10-28', 'tm1');
        INSERT INTO FreezeWindows (Id, Name, StartsAt, EndsAt, CreatedByUserId) VALUES ('f1', 'Year end', '2026-12-20T00:00:00Z', '2027-01-02T00:00:00Z', 'u1');
        INSERT INTO AuditEvents (OccurredAt, ActorUserId, ReleaseTrainId, EntityType, EntityId, Action) VALUES ('2026-09-01T00:00:00Z', 'u1', 'r1', 'Train', 'r1', 'Created');
        INSERT INTO SyncAlerts (Id, SourceSystem, Kind, Fingerprint, ErrorMessage, FirstOccurredAt, LastOccurredAt, OccurrenceCount, Version) VALUES ('sa1', 'Jira', 'Stalled', 'fp-old', 'Jira has not answered', '2026-09-01T00:00:00Z', '2026-09-02T00:00:00Z', 3, 2);
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

# Upgrade 006 (REOS-107/109/110): existing releases get keys in creation order, the counter points past them, nothing else about them changes
check(db.execute("SELECT Id, ReleaseKey, OwnerUserId, OwnerTeamId, ReleaseBranch, OnCallUserId, OnCallTeamId, ReleaseTypeId FROM ReleaseTrains ORDER BY CreatedAt").fetchall()
      == [("r1", "RLS-1", None, None, None, None, None, None), ("r2", "RLS-2", None, None, None, None, None, None)], "006: existing releases get keys RLS-1, RLS-2 in creation order")
check(db.execute("SELECT KeyPrefix, NextKeyNumber FROM Workspaces WHERE Id = 'default'").fetchall() == [("RLS", 3)], "006: the workspace counter points past the last key")
db.execute("INSERT INTO ReleaseTrains (Id, Title, TargetReleaseDate, CreatedAt, UpdatedAt) VALUES ('r3', 'R26.12', '2026-12-15', '2026-09-03T00:00:00Z', '2026-09-03T00:00:00Z')")
check(db.execute("SELECT ReleaseKey FROM ReleaseTrains WHERE Id = 'r3'").fetchall() == [("RLS-3",)], "006: a release inserted afterwards takes the next key")
try:
    db.execute("UPDATE ReleaseTrains SET ReleaseKey = 'RLS-9' WHERE Id = 'r1'")
    check(False, "006: a key never changes")
except sqlite3.IntegrityError as e:
    check("never changes" in str(e), "006: a key never changes")
check(db.execute("SELECT count(*) FROM Environments").fetchone() == (0,) and db.execute("SELECT count(*) FROM Deployments").fetchone() == (0,), "006: the new tables start empty")
try:
    db.execute("INSERT INTO Environments (Id, Name, Kind, CreatedAt, WorkspaceId) VALUES ('e1', 'Staging', 'Staging', '2026-09-01T00:00:00Z', 'nowhere')")
    check(False, "006: the new tables' foreign keys are enforced")
except sqlite3.IntegrityError as e:
    check("FOREIGN KEY" in str(e), "006: the new tables' foreign keys are enforced")

# Upgrade 007 (REOS-111 to 114): existing audit rows are untouched and unsealed, the rebuilt alert table keeps its rows and takes the new sources, the new tables start empty
check(db.execute("SELECT count(*), count(Hash), count(PrevHash) FROM AuditEvents WHERE EntityId = 'r1' AND Action = 'Created'").fetchone() == (1, 0, 0), "007: existing audit rows keep their content and are not sealed yet")
check(db.execute("SELECT Id, SourceSystem, Kind, OccurrenceCount, Version, WorkspaceId FROM SyncAlerts").fetchall() == [("sa1", "Jira", "Stalled", 3, 2, "default")], "007: the rebuilt alert table keeps its rows")
db.execute("INSERT INTO SyncAlerts (Id, SourceSystem, Kind, Fingerprint, ErrorMessage, FirstOccurredAt, LastOccurredAt) VALUES ('sa2', 'Deployment', 'ControlFailed', 'fp-new', 'x', '2026-09-03T00:00:00Z', '2026-09-03T00:00:00Z')")
try:
    db.execute("INSERT INTO SyncAlerts (Id, SourceSystem, Kind, Fingerprint, ErrorMessage, FirstOccurredAt, LastOccurredAt) VALUES ('sa3', 'Jira', 'Stalled', 'fp-old', 'again', '2026-09-03T00:00:00Z', '2026-09-03T00:00:00Z')")
    check(False, "007: the open-alert fingerprint is still unique")
except sqlite3.IntegrityError as e:
    check("UNIQUE" in str(e), "007: the open-alert fingerprint is still unique")
check(all(db.execute(f"SELECT count(*) FROM {t}").fetchone() == (0,) for t in ("MetricSamples", "MetricRollups", "ThresholdBreaches", "DeploymentControlActions", "ApprovalPolicies",
      "ApprovalPolicyRequirements", "Approvals", "ApprovalDecisions", "AuditChainHeads")), "007: the new tables start empty")
db.execute("UPDATE AuditEvents SET PrevHash = printf('%064d', 0), Hash = printf('%064d', 1) WHERE EntityId = 'r1' AND Action = 'Created'")
try:
    db.execute("UPDATE AuditEvents SET Action = 'Edited' WHERE EntityId = 'r1'")
    check(False, "007: a sealed audit row stays append-only")
except sqlite3.IntegrityError as e:
    check("append-only" in str(e), "007: a sealed audit row stays append-only")
db.execute("INSERT INTO RunbookRuns (Id, ReleaseTrainId, Mode, StartedAt, StartedByUserId) VALUES ('run1', 'r1', 'Rehearsal', '2026-09-03T00:00:00Z', 'u1')")
check(db.execute("SELECT PausedAt, PausedByUserId, PauseReason FROM RunbookRuns WHERE Id = 'run1'").fetchone() == (None, None, None), "007: a run starts unpaused")

# Upgrade 008 (REOS-116): the Beta type is flagged, nothing else about existing types and releases changes, the new rules are live
t8 = baseline_db()
for s in SCRIPTS:
    if s.name.startswith("008_"):
        t8.execute("INSERT INTO ReleaseTypes (Id, Name, SortOrder) VALUES ('tyb', 'beta', 10), ('tyc', 'CERT', 20), ('typ', 'Production', 40)")
        t8.execute("UPDATE ReleaseTrains SET ReleaseTypeId = 'tyb' WHERE Id = 'r1'")
    t8.executescript(s.read_text())
check(t8.execute("SELECT Id, IsBeta FROM ReleaseTypes ORDER BY Id").fetchall() == [("tyb", 1), ("tyc", 0), ("typ", 0)], "008: the type called Beta (any case) is a Beta type, the others are not")
check(t8.execute("SELECT Id, ReleaseTypeId, BetaNumber FROM ReleaseTrains ORDER BY Id").fetchall() == [("r1", "tyb", None), ("r2", None, None)], "008: existing releases keep their type and have no number")
check(t8.execute("UPDATE ReleaseTrains SET Title = 'R26.10b' WHERE Id = 'r1'").rowcount == 1, "008: a Beta release without a number can be edited in other fields")
check(t8.execute("UPDATE ReleaseTrains SET Title = 'R26.11b' WHERE Id = 'r2'").rowcount == 1, "008: a release without a type can be edited in other fields")
for sql, want, label in [
    ("INSERT INTO ReleaseTrains (Id, Title, TargetReleaseDate, CreatedAt, UpdatedAt) VALUES ('r9', 'x', '2026-12-01', '2026-09-03T00:00:00Z', '2026-09-03T00:00:00Z')", "needs a release type", "008: a new release without a type is refused once the workspace has types"),
    ("UPDATE ReleaseTrains SET BetaNumber = 2 WHERE Id = 'r2'", "Only a Beta release carries a Beta number", "008: a number on a release that is not a Beta is refused"),
    ("UPDATE ReleaseTypes SET IsBeta = 1 WHERE Id = 'tyc'", None, "008: a type no release uses can become a Beta type"),
]:
    try:
        t8.execute(sql); check(want is None, label)
    except sqlite3.IntegrityError as e:
        check(want is not None and want in str(e), label)
t8.execute("UPDATE ReleaseTrains SET ReleaseTypeId = 'typ' WHERE Id = 'r2'")
check(t8.execute("SELECT ReleaseTypeId FROM ReleaseTrains WHERE Id = 'r2'").fetchone() == ("typ",), "008: a release without a type can be given one")

# Upgrade 009 (REOS-118): the rebuilt ImportJobs keeps its rows and takes the Clients kind, Clients starts empty, the new rules are live
t9 = baseline_db()
for s in SCRIPTS:
    if s.name.startswith("009_"):
        t9.execute("INSERT INTO ImportJobs (Id, Kind, FileName, Sha256, RowCount, Status, UploadedByUserId, CreatedAt) VALUES ('ij1', 'Teams', 't.csv', 'ab', 3, 'Previewed', 'u1', '2026-09-03T00:00:00Z')")
        t9.execute("INSERT INTO Environments (Id, Name, Kind, CreatedAt) VALUES ('envc', 'CERT', 'Staging', '2026-09-03T00:00:00Z')")
    t9.executescript(s.read_text())
check(t9.execute("SELECT Id, Kind, RowCount, Status, WorkspaceId FROM ImportJobs").fetchall() == [("ij1", "Teams", 3, "Previewed", "default")], "009: import jobs kept by the rebuild")
check(t9.execute("SELECT COUNT(*) FROM Clients").fetchone() == (0,), "009: the client list starts empty")
check(t9.execute("INSERT INTO ImportJobs (Id, Kind, FileName, Sha256, RowCount, Status, UploadedByUserId, CreatedAt) VALUES ('ij2', 'Clients', 'c.csv', 'cd', 1, 'Previewed', 'u1', '2026-09-04T00:00:00Z')").rowcount == 1, "009: a Clients import job is accepted")
for sql, want, label in [
    ("INSERT INTO Clients (Id, Name, Segment, CreatedAt) VALUES ('c1', 'Acme', 'Standard', '2026-09-04T00:00:00Z')", None, "009: a Standard client"),
    ("INSERT INTO Clients (Id, Name, Segment, CertEnvironmentId, CreatedAt) VALUES ('c2', 'Globex', 'OnDemand', 'envc', '2026-09-04T00:00:00Z')", None, "009: an On-Demand client with its CERT environment"),
    ("INSERT INTO Clients (Id, Name, Segment, CertEnvironmentId, CreatedAt) VALUES ('c3', 'Initech', 'Standard', 'envc', '2026-09-04T00:00:00Z')", "Only an On-Demand client has a CERT environment", "009: a Standard client with a CERT environment is refused"),
    ("INSERT INTO Clients (Id, Name, Segment, CreatedAt) VALUES ('c4', 'Hooli', 'OnDemand', '2026-09-04T00:00:00Z')", "needs a CERT environment", "009: an On-Demand client without one is refused"),
]:
    try:
        t9.execute(sql); check(want is None, label)
    except sqlite3.IntegrityError as e:
        check(want is not None and want in str(e), label)

# Upgrade 011 (REOS-119): ReleaseClientDates starts empty, clients and releases are kept, the new rules are live
t11 = baseline_db()
for s in SCRIPTS:
    if s.name.startswith("011_"):
        t11.execute("INSERT INTO Environments (Id, Name, Kind, CreatedAt) VALUES ('envc', 'CERT', 'Staging', '2026-09-03T00:00:00Z')")
        t11.execute("INSERT INTO Clients (Id, Name, Segment, CertEnvironmentId, CreatedAt) VALUES ('c2', 'Globex', 'OnDemand', 'envc', '2026-09-04T00:00:00Z')")
        t11.execute("INSERT INTO Clients (Id, Name, Segment, CreatedAt) VALUES ('c1', 'Acme', 'Standard', '2026-09-04T00:00:00Z')")
    t11.executescript(s.read_text())
check(t11.execute("SELECT COUNT(*) FROM ReleaseClientDates").fetchone() == (0,), "011: no client date is made by the script")
check(t11.execute("SELECT Id, Segment, WorkspaceId FROM Clients ORDER BY Id").fetchall() == [("c1", "Standard", "default"), ("c2", "OnDemand", "default")], "011: clients kept")
check(t11.execute("SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND name LIKE 'trg_ClientDate_%'").fetchone() == (2,), "011: the client date rules are in place")
CD = "INSERT INTO ReleaseClientDates (Id, ReleaseTrainId, ClientId, OfferedDate, CreatedAt) VALUES (?, 'r1', ?, '2026-11-06', '2026-09-04T00:00:00Z')"
for params, want, label in [
    (("d1", "c2"), None, "011: an On-Demand client's offered date"),
    (("d2", "c1"), "Only an On-Demand client has its own date", "011: a Standard client's own date is refused"),
    (("d3", "c2"), "UNIQUE", "011: a second row for the same release and client is refused"),
]:
    try:
        t11.execute(CD, params); check(want is None, label)
    except sqlite3.IntegrityError as e:
        check(want is not None and want in str(e), label)

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
