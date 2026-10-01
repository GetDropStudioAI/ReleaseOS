"""Enforcement tests for db/schema.sql. Run from the repo root: python3 tests/reference/test_schema.py
Port every case to xUnit in M1 (same cases, same expected messages)."""
import sqlite3, json, sys, os

SCHEMA = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "db", "schema.sql")
NOW = '2026-10-20T14:00:00Z'
passed = failed = 0

def db_fresh():
    db = sqlite3.connect(":memory:")
    db.execute("PRAGMA foreign_keys=ON")
    db.executescript(open(SCHEMA).read())
    db.executescript(f"""
    INSERT INTO Users(Id,Email,DisplayName,Role) VALUES
      ('rte','rte@x.com','Rae T.','RTE'),
      ('rm','rm@x.com','Rel Mgr','ReleaseManager'),
      ('gov1','g1@x.com','Gov One','GovernanceOfficer'),
      ('gov2','g2@x.com','Gov Two','GovernanceOfficer'),
      ('dev','dev@x.com','Dev','Viewer');
    INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CreatedAt,UpdatedAt)
      VALUES('t1','R26.10','2026-10-30','High','{NOW}','{NOW}');
    INSERT INTO BundledProducts(Id,ReleaseTrainId,ProductName,VersionTag,ProjectCode)
      VALUES('p1','t1','Payments API','4.5.0','PAY'),('p2','t1','Card Portal','2.1.0','CRD');
    INSERT INTO StageGates(Id,ReleaseTrainId,GateName,GateClass,SequenceOrder,OffsetDays,DueOn,RequiredBeforeStatus,OwnerUserId,Status,LastChangedByUserId) VALUES
      ('g1','t1','Code Freeze','Standard',1,5,'2026-10-23','Gated','rte','Pending','rte'),
      ('g2','t1','Compliance Sign-off','Compliance',2,2,'2026-10-28','Executing','gov1','InProgress','gov1');
    INSERT INTO ChecklistTasks(Id,StageGateId,TaskDescription,OwnerUserId,SequenceOrder) VALUES
      ('k1','g1','Tag repos','rte',1),('k2','g2','SOX evidence uploaded','gov1',1);
    INSERT INTO RunbookSteps(Id,ReleaseTrainId,BundledProductId,StepCode,Section,Title,OwnerUserId,PlannedStartAt,PlannedDurationMin) VALUES
      ('s1','t1','p1','R-001','PreCheck','Health check','rte','2026-10-30T02:00:00Z',10),
      ('s2','t1','p1','R-002','Deploy','Deploy Payments API','rte','2026-10-30T02:10:00Z',30);
    INSERT INTO StepDependencies(StepId,DependsOnStepId) VALUES('s2','s1');
    """)
    return db

def check(db, sql, should_pass, label, params=(), expect=None):
    """should_pass=False requires the error text to contain `expect` when given."""
    global passed, failed
    try:
        db.execute(sql, params)
        ok, msg = should_pass, "allowed"
    except sqlite3.IntegrityError as e:
        ok, msg = (not should_pass) and (expect is None or expect in str(e)), f"blocked: {e}"
    passed += ok; failed += (not ok)
    print(f"  {'PASS' if ok else 'FAIL'}  {label:<64} {msg}")

def assert_true(cond, label, detail=""):
    global passed, failed
    passed += bool(cond); failed += (not cond)
    print(f"  {'PASS' if cond else 'FAIL'}  {label:<64} {detail}")

def done_task(db, tid, who):
    db.execute("UPDATE ChecklistTasks SET IsCompleted=1,CompletedAt=?,CompletedByUserId=?,LastChangedByUserId=? WHERE Id=?", (NOW, who, who, tid))

def start(db, gid, who):
    db.execute("UPDATE StageGates SET Status='InProgress',LastChangedByUserId=? WHERE Id=?", (who, gid))

def certify(gid, who, at=NOW):
    return f"UPDATE StageGates SET Status='Certified',CertifiedByUserId='{who}',CertifiedAt='{at}',LastChangedByUserId='{who}',LastChangedAt='{at}' WHERE Id='{gid}'"

GO_COND = "INSERT INTO GoNoGoConditions(Id,DecisionId,Text,OwnerUserId,ExpiresAt) VALUES(?,?,?,?,?)"
FREEZE = "INSERT INTO FreezeWindows(Id,Name,Kind,StartsAt,EndsAt,ProductPattern,CreatedByUserId) VALUES(?,?,?,?,?,?,?)"
OVERRIDE = "INSERT INTO FreezeOverrides(Id,FreezeWindowId,ReleaseTrainId,Reason,RequestedByUserId,ApprovedByUserId,ApprovedAt,ExpiresAt) VALUES(?,?,?,?,?,?,?,?)"
DECISION = "INSERT INTO GoNoGoDecisions(Id,ReleaseTrainId,Decision,DecidedByUserId,DecidedAt,GateSnapshotJson,NewTargetReleaseDate) VALUES(?,?,?,?,?,'{}',?)"
RUN = "INSERT INTO RunbookRuns(Id,ReleaseTrainId,Mode,StartedAt,StartedByUserId) VALUES(?,?,?,?,?)"

print("Train lifecycle and gate lockout")
db = db_fresh()
check(db, "UPDATE ReleaseTrains SET CurrentStatus='Executing' WHERE Id='t1'", False, "skip Planning -> Executing", expect="Illegal train status transition")
check(db, "UPDATE ReleaseTrains SET CurrentStatus='Gated' WHERE Id='t1'", False, "Gated while Code Freeze uncertified", expect="Gate lockout")
check(db, certify('g1','rte'), False, "certify gate with an open task", expect="open checklist tasks")
check(db, certify('g2','gov2'), False, "certify gate 2 before gate 1", expect="earlier gate")
done_task(db,'k1','rte')
check(db, certify('g1','rte'), False, "Pending -> Certified skips In progress", expect="Illegal gate status transition")
check(db, "UPDATE StageGates SET Status='InProgress' WHERE Id='g1'", True, "Pending -> In progress")
check(db, certify('g1','rte','2026-10-22T17:05:00Z'), True, "certify Code Freeze")
b = db.execute("SELECT PlannedReleaseDate, SnapshotJson FROM Baselines WHERE ReleaseTrainId='t1'").fetchone()
assert_true(b and len(json.loads(b[1])['products']) == 2 and len(json.loads(b[1])['steps']) == 2, "baseline auto-captured at Code Freeze", b[0] if b else None)
assert_true(db.execute("SELECT count(*) FROM AuditEvents WHERE Action IN ('AutoCaptured','EvidenceLocked')").fetchone()[0] == 2, "cascaded effects are audited (baseline, evidence lock)")
check(db, "INSERT INTO Baselines(Id,ReleaseTrainId,CapturedAt,PlannedReleaseDate,SnapshotJson) VALUES('b2','t1',?,'2026-10-30','{}')", False, "second baseline for a train", (NOW,), expect="UNIQUE")
check(db, "UPDATE Baselines SET PlannedReleaseDate='2026-11-30'", False, "edit baseline", expect="immutable")
check(db, "DELETE FROM Baselines", False, "delete baseline", expect="immutable")
check(db, "UPDATE ReleaseTrains SET CurrentStatus='Gated' WHERE Id='t1'", True, "Planning -> Gated")
rows = db.execute("SELECT FromStatus,ToStatus,OccurredAt FROM GateTransitions WHERE StageGateId='g1' ORDER BY Id").fetchall()
assert_true(len(rows) == 2 and rows[1][2] == '2026-10-22T17:05:00Z', "transition log uses the service clock (LastChangedAt)", str(rows))
check(db, "DELETE FROM GateTransitions", False, "delete gate transition log", expect="append-only")
check(db, "UPDATE StageGates SET Status='Failed',CertifiedByUserId=NULL,CertifiedAt=NULL WHERE Id='g1'", False, "Certified -> Failed without reopening", expect="Illegal gate status transition")

print("Compliance gate: role, segregation of duties, waiver")
done_task(db,'k2','gov1')
check(db, certify('g2','rm'), False, "Release Manager certifies Compliance gate", expect="Governance Officers only")
check(db, certify('g2','gov1'), False, "certifier also completed a task in the gate (SoD)", expect="Segregation of duties")
check(db, "UPDATE StageGates SET Status='Waived',CertifiedByUserId='gov1',CertifiedAt=?,LastChangedByUserId='gov1' WHERE Id='g2'", False, "waive without approved waiver", (NOW,), expect="second approver")
db.execute("INSERT INTO GateWaivers(Id,StageGateId,Reason,RequestedByUserId,RequestedAt) VALUES('w1','g2','Pen-test vendor slipped; CISO accepted risk','gov1',?)", (NOW,))
check(db, "UPDATE GateWaivers SET ApprovedByUserId='gov1',ApprovedAt=? WHERE Id='w1'", False, "waiver self-approval", (NOW,), expect="CHECK")
check(db, "UPDATE GateWaivers SET ApprovedByUserId='rm',ApprovedAt=? WHERE Id='w1'", False, "waiver approved by a Release Manager", (NOW,), expect="approved by a Governance Officer")
check(db, "INSERT INTO GateWaivers(Id,StageGateId,Reason,RequestedByUserId,RequestedAt) VALUES('w2','g2','too short','gov1',?)", False, "waiver reason under 20 chars", (NOW,), expect="CHECK")
check(db, "INSERT INTO GateWaivers(Id,StageGateId,Reason,RequestedByUserId,ApprovedByUserId,RequestedAt,ApprovedAt) VALUES('w3','g2','Inserted already approved by a viewer','gov1','dev',?,?)", False, "waiver inserted pre-approved by a Viewer", (NOW,NOW), expect="approved by a Governance Officer")
check(db, certify('g2','gov2'), True, "independent Governance Officer certifies")

print("Executing preconditions")
db.execute("UPDATE ReleaseTrains SET RiskTier='Moderate' WHERE Id='t1'")
check(db, "UPDATE ReleaseTrains SET CurrentStatus='Executing' WHERE Id='t1'", False, "Executing with no Go decision", expect="recorded Go decision")
db.execute(DECISION, ('d1','t1','NoGo','rm','2026-10-29T15:00:00Z','2026-11-06'))
check(db, "UPDATE ReleaseTrains SET CurrentStatus='Executing' WHERE Id='t1'", False, "Executing when latest decision is NoGo", expect="recorded Go decision")
check(db, GO_COND, False, "condition on a NoGo decision", ('c0','d1','x','rm','2099-01-01T00:00:00Z'), expect="GoWithConditions")
db.execute(DECISION, ('d2','t1','GoWithConditions','rm','2026-10-29T16:00:00Z',None))
check(db, GO_COND, True, "expiring condition on a conditional Go", ('c1','d2','Load test by 22:00','rte','2000-01-01T00:00:00Z'))
check(db, "UPDATE GoNoGoDecisions SET Decision='Go'", False, "edit a recorded decision", expect="immutable")
check(db, "UPDATE ReleaseTrains SET CurrentStatus='Executing' WHERE Id='t1'", False, "Executing with an expired, unclosed condition", expect="condition expired")
db.execute("UPDATE GoNoGoConditions SET ClosedAt=?, ClosedByUserId='rte' WHERE Id='c1'", (NOW,))
db.execute("UPDATE ReleaseTrains SET RiskTier='High' WHERE Id='t1'")
check(db, "UPDATE ReleaseTrains SET CurrentStatus='Executing' WHERE Id='t1'", False, "High risk with no rehearsed rollback", expect="rehearsed rollback")
check(db, "UPDATE ReleaseTrains SET RollbackRehearsedAt=?, RollbackRehearsedByUserId='rte' WHERE Id='t1'", True, "record rollback rehearsal", (NOW,))
check(db, RUN, False, "Live run before Executing", ('r1','t1','Live',NOW,'rte'), expect="requires the train to be Executing")
check(db, RUN, True, "Rehearsal run any time", ('r0','t1','Rehearsal',NOW,'rte'))
check(db, "UPDATE ReleaseTrains SET CurrentStatus='Executing' WHERE Id='t1'", True, "Gated -> Executing")

db2 = db_fresh()
db2.execute("UPDATE ReleaseTrains SET RiskTier='Low' WHERE Id='t1'")
db2.execute("UPDATE StageGates SET GateName='Build Verify' WHERE Id='g1'")   # not a freeze gate: no auto baseline
done_task(db2,'k1','rte'); start(db2,'g1','rte'); db2.execute(certify('g1','rte'))
db2.execute("UPDATE ReleaseTrains SET CurrentStatus='Gated' WHERE Id='t1'")
done_task(db2,'k2','gov1'); db2.execute(certify('g2','gov2'))
db2.execute(DECISION, ('d1','t1','Go','rm',NOW,None))
check(db2, "UPDATE ReleaseTrains SET CurrentStatus='Executing' WHERE Id='t1'", False, "Executing with no baseline captured", expect="baseline")
check(db2, "UPDATE StageGates SET Status='InProgress',CertifiedByUserId=NULL,CertifiedAt=NULL WHERE Id='g2'", True, "reopen a certified gate (Certified -> In progress)")
check(db2, "UPDATE StageGates SET Status='Failed' WHERE Id='g2'", True, "In progress -> Failed")
check(db2, "UPDATE StageGates SET Status='InProgress', LastChangedByUserId=NULL WHERE Id='g2'", False, "gate status change with no actor", expect="LastChangedByUserId")

print("Condition expiry follows the service clock (LastChangedAt)")
db4 = db_fresh()
db4.execute("UPDATE ReleaseTrains SET RiskTier='Low' WHERE Id='t1'")
done_task(db4,'k1','rte'); start(db4,'g1','rte'); db4.execute(certify('g1','rte'))
db4.execute("UPDATE ReleaseTrains SET CurrentStatus='Gated' WHERE Id='t1'")
done_task(db4,'k2','gov1'); db4.execute(certify('g2','gov2'))
db4.execute(DECISION, ('d9','t1','GoWithConditions','rm','2026-10-29T15:00:00Z',None))
db4.execute(GO_COND, ('c9','d9','CISO sign-off','gov1','2026-10-30T05:30:00Z'))
check(db4, "UPDATE ReleaseTrains SET CurrentStatus='Executing', LastChangedAt='2026-10-30T06:00:00Z' WHERE Id='t1'", False, "service clock past condition expiry", expect="condition expired")
check(db4, "UPDATE ReleaseTrains SET CurrentStatus='Executing', LastChangedAt='2026-10-30T05:00:00Z' WHERE Id='t1'", True, "service clock before condition expiry")

print("Runbook execution")
check(db, RUN, True, "start Live run", ('r1','t1','Live',NOW,'rte'))
check(db, RUN, False, "second concurrent Live run", ('r2','t1','Live',NOW,'rte'), expect="UNIQUE")
db.execute("INSERT INTO StepExecutions(Id,RunId,StepId) VALUES('e1','r1','s1'),('e2','r1','s2')")
check(db, "UPDATE StepExecutions SET Status='Running',ActualStartAt='2026-10-30T02:12:00Z' WHERE Id='e2'", False, "start step before its dependency is done", expect="Dependency not complete")
check(db, "UPDATE StepExecutions SET Status='Skipped' WHERE Id='e1'", False, "skip without a comment", expect="CHECK")
check(db, "UPDATE StepExecutions SET Status='Running',ActualStartAt='2026-10-30T02:01:00Z' WHERE Id='e1'", True, "start step 1")
check(db, "UPDATE StepExecutions SET Status='Done',ActualEndAt='2026-10-30T02:14:00Z' WHERE Id='e1'", True, "finish step 1")
db.execute(FREEZE, ('f1','Q4 close freeze','Freeze','2026-10-30T00:00:00Z','2026-10-31T00:00:00Z','Payments*','gov1'))
check(db, "UPDATE StepExecutions SET Status='Running',ActualStartAt='2026-10-30T02:15:00Z' WHERE Id='e2'", False, "Deploy step inside matching freeze, no override", expect="Freeze window")
R = 'Regulatory fix must ship before month-end close'
check(db, OVERRIDE, False, "freeze override self-approved", ('o0','f1','t1',R,'rm','rm',NOW,'2026-10-30T06:00:00Z'), expect="CHECK")
check(db, OVERRIDE, False, "freeze override approved by an RTE", ('o0','f1','t1',R,'rm','rte',NOW,'2026-10-30T06:00:00Z'), expect="Release Manager or Governance Officer")
db.execute(OVERRIDE, ('o1','f1','t1',R,'rm','gov1',NOW,'2026-10-30T02:10:00Z'))
check(db, "UPDATE StepExecutions SET Status='Running',ActualStartAt='2026-10-30T02:15:00Z' WHERE Id='e2'", False, "override expired before step start", expect="Freeze window")
check(db, "UPDATE FreezeOverrides SET ExpiresAt='2099-01-01T00:00:00Z' WHERE Id='o1'", False, "extend an override in place", expect="immutable")
check(db, "DELETE FROM FreezeOverrides WHERE Id='o1'", False, "delete an override", expect="immutable")
check(db, OVERRIDE, True, "renew override with a new row", ('o2','f1','t1',R,'rm','gov1',NOW,'2026-10-30T06:00:00Z'))
check(db, "UPDATE StepExecutions SET Status='Running',ActualStartAt='2026-10-30T02:15:00Z' WHERE Id='e2'", True, "Deploy step with valid override")

print("Completion and PIR")
check(db, "UPDATE ReleaseTrains SET CurrentStatus='Complete' WHERE Id='t1'", False, "Complete without close code", expect="CHECK")
check(db, "UPDATE ReleaseTrains SET CurrentStatus='Complete', CloseCode='SuccessfulWithIssues', LastChangedByUserId='rte', LastChangedAt='2026-10-30T09:30:00Z' WHERE Id='t1'", True, "Complete, successful with issues")
pir = db.execute("SELECT RequiredReason, Status FROM PostImplementationReviews WHERE ReleaseTrainId='t1'").fetchone()
assert_true(pir == ('CloseCode=SuccessfulWithIssues','Required'), "PIR auto-required", str(pir))
a = db.execute("SELECT a.ActorUserId, a.OccurredAt, a.EntityId = p.Id FROM AuditEvents a JOIN PostImplementationReviews p ON p.ReleaseTrainId='t1' WHERE a.Action='AutoRequired'").fetchone()
assert_true(a == ('rte','2026-10-30T09:30:00Z',1), "PIR creation audited: actor, service time, PIR id", str(a))

print("Decertify on change, evidence locking")
db = db_fresh()
db.execute("INSERT INTO Attachments(Id,ReleaseTrainId,EntityType,EntityId,FileName,ContentType,SizeBytes,Sha256,StoragePath,UploadedByUserId,UploadedAt) VALUES('a1','t1','Task','k1','tags.txt','text/plain',120,?,'/ev/a1','rte',?)", ('a'*64, NOW))
done_task(db,'k1','rte'); start(db,'g1','rte'); db.execute(certify('g1','rte'))
assert_true(db.execute("SELECT IsLocked FROM Attachments WHERE Id='a1'").fetchone()[0] == 1, "evidence locked when gate certifies")
check(db, "UPDATE Attachments SET IsLocked=0 WHERE Id='a1'", False, "unlock locked evidence", expect="locked evidence")
check(db, "DELETE FROM Attachments WHERE Id='a1'", False, "delete locked evidence", expect="locked evidence")
check(db, "UPDATE Attachments SET FileName='x' WHERE Id='a1'", False, "rename locked evidence", expect="locked evidence")
db.execute("UPDATE ChecklistTasks SET IsCompleted=0,CompletedAt=NULL,CompletedByUserId=NULL,LastChangedByUserId='dev',LastChangedAt='2026-10-21T09:00:00Z' WHERE Id='k1'")
g = db.execute("SELECT Status, LastChangedByUserId FROM StageGates WHERE Id='g1'").fetchone()
assert_true(g == ('InProgress','dev'), "reopening a task decertifies its gate, actor = reopener", str(g))
assert_true(db.execute("SELECT count(*) FROM AuditEvents WHERE Action='Decertified' AND ActorUserId='dev'").fetchone()[0] == 1, "decertify is audited with the reopener")
done_task(db,'k1','rte'); db.execute(certify('g1','rte'))
db.execute("INSERT INTO ChecklistTasks(Id,StageGateId,TaskDescription,OwnerUserId,SequenceOrder,LastChangedByUserId) VALUES('k9','g1','Late add','rte',2,'rm')")
g = db.execute("SELECT Status, LastChangedByUserId FROM StageGates WHERE Id='g1'").fetchone()
assert_true(g == ('InProgress','rm'), "adding a task to a certified gate decertifies it, actor = adder", str(g))

print("Open item 14: empty Compliance gate")
db3 = db_fresh()
done_task(db3,'k1','rte'); start(db3,'g1','rte'); db3.execute(certify('g1','rte'))
db3.execute("DELETE FROM ChecklistTasks WHERE Id='k2'")
check(db3, certify('g2','gov2'), False, "certify Compliance gate with zero tasks", expect="at least one task")
db3.execute("UPDATE StageGates SET GateClass='Standard' WHERE Id='g2'")
check(db3, certify('g2','gov2'), True, "Standard gate with zero tasks may certify")

print("Audit, dispatches, imports, integrations, session")
db.execute("INSERT INTO AuditEvents(OccurredAt,EntityType,EntityId,Action) VALUES(?,'Train','t1','Create')", (NOW,))
check(db, "UPDATE AuditEvents SET Action='x'", False, "edit audit row", expect="append-only")
check(db, "DELETE FROM AuditEvents", False, "delete audit row", expect="append-only")
db.execute("INSERT INTO CommTemplates(Id,ReleaseTrainId,TemplateType,Audience,SubjectLine,MarkdownBody) VALUES('ct1','t1','T-1','All','s','b')")
db.execute("INSERT INTO CommDispatches(Id,CommTemplateId,Channel,HydratedSubject,HydratedBody,DispatchedByUserId,DispatchedAt,Outcome) VALUES('cd1','ct1','Copy','s','b','rte',?,'Handed')", (NOW,))
check(db, "UPDATE CommDispatches SET HydratedBody='edited'", False, "edit a sent message", expect="immutable")
check(db, "DELETE FROM CommDispatches", False, "delete a sent message", expect="immutable")
db.execute("INSERT INTO ImportJobs(Id,Kind,FileName,Sha256,RowCount,ErrorCount,ErrorsJson,Status,UploadedByUserId,CreatedAt) VALUES('i1','Tasks','t.csv','x',40,2,'[{\"row\":7,\"column\":\"Owner\",\"message\":\"unknown @ops-db\"}]','Previewed','rte',?)", (NOW,))
check(db, "UPDATE ImportJobs SET Status='Committed' WHERE Id='i1'", False, "commit import with row errors", expect="row errors")
check(db, "INSERT INTO WebhookDestinations(Id,Name,Host,ProtectedUrl,UrlHmac,Kind) VALUES('h1','x','hooks.example.com','https://hooks.example.com/T/B/secret',?,'Generic')", False, "webhook URL stored in clear", ('a'*64,), expect="CHECK")
db.execute("INSERT INTO WebhookDestinations(Id,Name,Host,ProtectedUrl,UrlHmac,Kind) VALUES('h2','x','hooks.example.com','CfDJ8AAAA',?,'Generic')", ('b'*64,))
check(db, "INSERT INTO WebhookDestinations(Id,Name,Host,ProtectedUrl,UrlHmac,Kind) VALUES('h3','y','hooks.example.com','CfDJ8BBBB',?,'Generic')", False, "same webhook address twice (keyed hash)", ('b'*64,), expect="UNIQUE")
check(db, "INSERT INTO UserSessionState(UserId,ClientId,SchemaVersion,UIStateJson,LastActivityAt) VALUES('rte','c1',1,'{bad',?)", False, "invalid UI state JSON", (NOW,), expect="CHECK")
check(db, "INSERT INTO SyncAlerts(Id,SourceSystem,Kind,Fingerprint,ErrorMessage,FirstOccurredAt,LastOccurredAt) VALUES('x1','Jira','AuthFailed','fp1','401',?,?)", True, "open sync alert", (NOW,NOW))
check(db, "INSERT INTO SyncAlerts(Id,SourceSystem,Kind,Fingerprint,ErrorMessage,FirstOccurredAt,LastOccurredAt) VALUES('x2','Jira','AuthFailed','fp1','401',?,?)", False, "duplicate open alert (must increment count)", (NOW,NOW), expect="UNIQUE")
check(db, "INSERT INTO SyncAlerts(Id,SourceSystem,Kind,Fingerprint,ErrorMessage,FirstOccurredAt,LastOccurredAt) VALUES('x3','SyncEngine','Stalled','fp2','no cycle in 15 min',?,?)", True, "watchdog stall is storable", (NOW,NOW))
check(db, "INSERT INTO SyncAlerts(Id,SourceSystem,Kind,Fingerprint,ErrorMessage,FirstOccurredAt,LastOccurredAt) VALUES('x4','Backup','BackupFailed','fp3','disk full',?,?)", True, "backup failure is storable", (NOW,NOW))
db.execute("INSERT INTO IcsTokens(Id,UserId,TokenSha256,CreatedAt) VALUES('it1','rte',?,?)", ('b'*64, NOW))
check(db, "INSERT INTO IcsTokens(Id,UserId,TokenSha256,CreatedAt) VALUES('it2','rte',?,?)", False, "second active ICS token for a user", ('c'*64, NOW), expect="UNIQUE")
check(db, "INSERT INTO DeploymentWindows(Id,ReleaseTrainId,StartsAt,EndsAt) VALUES('dw1','t1','2026-10-30T06:00:00Z','2026-10-30T10:00:00Z')", True, "deployment window")
check(db, "INSERT INTO DeploymentWindows(Id,ReleaseTrainId,StartsAt,EndsAt) VALUES('dw2','t1','2026-10-31T06:00:00Z','2026-10-31T10:00:00Z')", False, "second window for a train (one per train in v1)", expect="UNIQUE")

print(f"\n{passed} passed, {failed} failed")
sys.exit(1 if failed else 0)
