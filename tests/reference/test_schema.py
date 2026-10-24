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
    INSERT INTO WorkspaceMembers(WorkspaceId,UserId,Role,AddedAt) SELECT 'default', Id, Role, '{NOW}' FROM Users;   -- the role triggers read the membership role (REOS-105)
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

print("Identity binding and sign-out (Q-SEC-B8, Q-SEC-B5)")
check(db, "UPDATE Users SET IdpIssuer='https://idp.example/',IdpSubject='sub-rte' WHERE Id='rte'", True, "bind a user to an IdP issuer + subject")
check(db, "UPDATE Users SET IdpIssuer='https://idp.example/',IdpSubject='sub-rte' WHERE Id='rm'", False, "same issuer + subject bound to a second user", expect="UNIQUE")
check(db, "UPDATE Users SET IdpIssuer='https://other.example/',IdpSubject='sub-rte' WHERE Id='rm'", True, "same subject at another issuer is another identity")
check(db, "UPDATE Users SET IdpIssuer='https://idp.example/' WHERE Id='dev'", False, "issuer without subject", expect="CHECK")
check(db, "INSERT INTO SessionRevocations(SessionId,UserId,RevokedAt,ExpiresAt) VALUES('ab12','rte',?,'2026-10-21T02:00:00Z')", True, "record a signed-out session", (NOW,))
check(db, "INSERT INTO SessionRevocations(SessionId,UserId,RevokedAt,ExpiresAt) VALUES('ab12','rte',?,'2026-10-21T02:00:00Z')", False, "same session revoked twice", (NOW,), expect="UNIQUE")

print("Train milestones (informational key dates)")
db5 = db_fresh()
db5.execute("INSERT INTO Teams(Id,Handle,Name) VALUES('tm1','ops','Ops')")
M = "INSERT INTO TrainMilestones(Id,ReleaseTrainId,Name,DueOn,OwnerUserId,OwnerTeamId,IsDone,DoneAt,DoneByUserId) VALUES(?,'t1',?,?,?,?,?,?,?)"
check(db5, M, True, "milestone with no owner", ('m1','Code complete','2026-10-16',None,None,0,None,None))
check(db5, M, False, "milestone owned by a user and a team", ('m2','UAT sign-off','2026-10-21','rte','tm1',0,None,None), expect="CHECK")
check(db5, M, False, "milestone name over 200 characters", ('m3','x'*201,'2026-10-21',None,None,0,None,None), expect="CHECK")
check(db5, M, False, "milestone due date that is not a date", ('m4','UAT sign-off','2026-10-32',None,None,0,None,None), expect="CHECK")
check(db5, M, False, "done milestone without who and when", ('m5','UAT sign-off','2026-10-21',None,'tm1',1,None,None), expect="CHECK")

print("Workspaces: membership, per-workspace uniqueness, and the boundary (D45, REOS-104)")
db6 = db_fresh()
H = lambda c: c * 64
db6.executescript(f"""
INSERT INTO Workspaces(Id,Name,Slug,CreatedAt) VALUES('w2','Payments EU','payments-eu','{NOW}');
INSERT INTO Teams(Id,Handle,Name) VALUES('tm1','ops','Ops');
INSERT INTO Teams(Id,Handle,Name,WorkspaceId) VALUES('tm2','ops','Ops EU','w2');
INSERT INTO WebhookDestinations(Id,Name,Host,ProtectedUrl,UrlHmac,Kind,WorkspaceId) VALUES('h2','EU','hooks.example.com','CfDJ8EU','{H('e')}','Generic','w2');
INSERT INTO TrainTemplates(Id,Name) VALUES('tp1','Monthly');
INSERT INTO TrainTemplates(Id,Name,WorkspaceId) VALUES('tp2','Monthly','w2');
INSERT INTO CommTemplateLibrary(Id,TemplateType,Name,Audience,SubjectLine,MarkdownBody,WorkspaceId) VALUES('lib2','Tminus28','T-28','All','s','b','w2');
INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,CreatedAt,UpdatedAt,WorkspaceId) VALUES('t2','EU R1','2026-11-30','{NOW}','{NOW}','w2');
INSERT INTO BundledProducts(Id,ReleaseTrainId,ProductName,VersionTag,ProjectCode) VALUES('p9','t2','EU API','1.0.0','EU');
INSERT INTO StageGates(Id,ReleaseTrainId,GateName,SequenceOrder,OffsetDays,DueOn,OwnerUserId,LastChangedByUserId) VALUES('gw','t2','Readiness',1,3,'2026-11-25','rte','rte');
INSERT INTO RunbookSteps(Id,ReleaseTrainId,StepCode,Title,OwnerUserId,PlannedStartAt,PlannedDurationMin) VALUES('s9','t2','R-001','EU deploy','rte','2026-11-30T02:00:00Z',10);
INSERT INTO FreezeWindows(Id,Name,StartsAt,EndsAt,CreatedByUserId,WorkspaceId) VALUES('f2','EU freeze','2026-10-01T00:00:00Z','2026-12-31T00:00:00Z','rm','w2');
""")
assert_true(db6.execute("SELECT Name, Slug FROM Workspaces WHERE Id='default'").fetchone() == ('Default', 'default'), "the default workspace exists in a new database")
assert_true(db6.execute("SELECT WorkspaceId FROM ReleaseTrains WHERE Id='t1'").fetchone() == ('default',), "a root row written without a workspace lands in the default one")
roots = [t for (t,) in db6.execute("SELECT name FROM sqlite_master WHERE type='table' AND name NOT IN ('Workspaces','WorkspaceMembers')")
         if 'WorkspaceId' in [c[1] for c in db6.execute(f"PRAGMA table_info({t})")]]
assert_true(sorted(roots) == sorted(['Teams','Holidays','TrainTemplates','CommTemplateLibrary','ReleaseTrains','FreezeWindows','SyncAlerts','WebhookDestinations',
            'Notifications','ImportJobs','ExportJobs','IcsTokens','ConnectorState','AuditEvents',
            'ReleaseTypes','ReleaseVersions','Environments','CatalogServices','IngestKeys','Deployments','MetricSamples','MetricRollups','ThresholdBreaches','DeploymentControlActions',
            'ApprovalPolicies','Approvals']), "WorkspaceId on every root table, and only there", str(sorted(roots)))
moving = [t for t in roots if t != 'AuditEvents' and not db6.execute(f"SELECT 1 FROM sqlite_master WHERE type='trigger' AND name='trg_Ws_{t}_Update'").fetchone()]
assert_true(moving == [], "every root table but the append-only audit log refuses a change of workspace", str(moving))
check(db6, "INSERT INTO Workspaces(Id,Name,Slug,CreatedAt) VALUES('w3','X','Payments-EU',?)", False, "workspace slug with capitals", (NOW,), expect="CHECK")
check(db6, "INSERT INTO Workspaces(Id,Name,Slug,CreatedAt) VALUES('w3','X','payments eu',?)", False, "workspace slug with a space", (NOW,), expect="CHECK")
check(db6, "INSERT INTO Workspaces(Id,Name,Slug,CreatedAt) VALUES('w3','  ','w3',?)", False, "blank workspace name", (NOW,), expect="CHECK")
check(db6, "INSERT INTO WorkspaceMembers(WorkspaceId,UserId,Role,AddedAt) VALUES('w2','rte','RTE',?)", True, "a person is a member of a second workspace", (NOW,))
check(db6, "INSERT INTO WorkspaceMembers(WorkspaceId,UserId,Role,AddedAt) VALUES('w2','rte','Viewer',?)", False, "the same person twice in one workspace", (NOW,), expect="UNIQUE")
check(db6, "INSERT INTO WorkspaceMembers(WorkspaceId,UserId,Role,AddedAt) VALUES('w2','rm','Admin',?)", False, "a role outside the four", (NOW,), expect="CHECK")
check(db6, "INSERT INTO Teams(Id,Handle,Name,WorkspaceId) VALUES('tm3','OPS','Ops again','w2')", False, "team handle twice in one workspace (any case)", expect="UNIQUE")
check(db6, "INSERT INTO Holidays(Day,Name) VALUES('2026-12-25','Christmas')", True, "a holiday in the default workspace")
check(db6, "INSERT INTO Holidays(Day,Name,WorkspaceId) VALUES('2026-12-25','Christmas','w2')", True, "the same holiday in another workspace")
check(db6, "INSERT INTO Holidays(Day,Name) VALUES('2026-12-25','Xmas')", False, "the same day twice in one workspace", expect="UNIQUE")
check(db6, "INSERT INTO TrainTemplates(Id,Name,WorkspaceId) VALUES('tp3','Monthly','w2')", False, "template name twice in one workspace", expect="UNIQUE")
check(db6, "INSERT INTO ConnectorState(SourceSystem,BaseUrl) VALUES('Jira','https://a.example')", True, "Jira connector in the default workspace")
check(db6, "INSERT INTO ConnectorState(SourceSystem,BaseUrl,WorkspaceId) VALUES('Jira','https://b.example','w2')", True, "a second Jira connector in another workspace")
db6.execute("INSERT INTO IcsTokens(Id,UserId,TokenSha256,CreatedAt) VALUES('ic1','rte',?,?)", (H('1'), NOW))
check(db6, "INSERT INTO IcsTokens(Id,UserId,TokenSha256,CreatedAt,WorkspaceId) VALUES('ic2','rte',?,?,'w2')", True, "a calendar feed per workspace for the same person", (H('2'), NOW))
check(db6, "INSERT INTO WebhookDestinations(Id,Name,Host,ProtectedUrl,UrlHmac,Kind) VALUES('h3','Same','hooks.example.com','CfDJ8X',?,'Generic')", False, "one address is one destination on the host", (H('e'),), expect="UNIQUE")
check(db6, "UPDATE ReleaseTrains SET WorkspaceId='w2' WHERE Id='t1'", False, "move a train to another workspace", expect="cannot move to another workspace")
check(db6, "UPDATE Holidays SET WorkspaceId='w2' WHERE WorkspaceId='default'", False, "move a holiday to another workspace", expect="cannot move to another workspace")
check(db6, "UPDATE SyncAlerts SET WorkspaceId=NULL", True, "no alert rows: nothing to move")
check(db6, "UPDATE Teams SET Name='Operations' WHERE Id='tm1'", True, "edit a root row in place")
TR = "INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,CreatedAt,UpdatedAt,TemplateId,ClonedFromTrainId) VALUES(?,'x','2026-12-01',?,?,?,?)"
check(db6, TR, False, "train from another workspace's template", ('t5', NOW, NOW, 'tp2', None), expect="template belongs to another workspace")
check(db6, TR, False, "train cloned from another workspace's train", ('t5', NOW, NOW, None, 't2'), expect="cloned-from train belongs to another workspace")
check(db6, TR, True, "train from its own workspace's template, cloned from its own train", ('t5', NOW, NOW, 'tp1', 't1'))
check(db6, "UPDATE ReleaseTrains SET TemplateId='tp2' WHERE Id='t5'", False, "point a train at another workspace's template", expect="template belongs to another workspace")
check(db6, TR, False, "a missing template is still a foreign-key error", ('t6', NOW, NOW, 'nope', None), expect="FOREIGN KEY")
check(db6, "UPDATE Teams SET WebhookDestinationId='h2' WHERE Id='tm1'", False, "team channel from another workspace", expect="webhook destination belongs to another workspace")
check(db6, "INSERT INTO StageGates(Id,ReleaseTrainId,GateName,SequenceOrder,OffsetDays,DueOn,OwnerTeamId) VALUES('g9','t1','CAB',9,1,'2026-10-29','tm2')", False, "gate owned by another workspace's team", expect="team belongs to another workspace")
check(db6, "UPDATE StageGates SET OwnerUserId=NULL, OwnerTeamId='tm2' WHERE Id='g1'", False, "gate handed to another workspace's team", expect="team belongs to another workspace")
check(db6, "UPDATE StageGates SET OwnerUserId=NULL, OwnerTeamId='tm1' WHERE Id='g1'", True, "gate handed to its own workspace's team")
check(db6, "INSERT INTO StageGates(Id,ReleaseTrainId,GateName,SequenceOrder,OffsetDays,DueOn,OwnerTeamId) VALUES('g9','t1','CAB',9,1,'2026-10-29','nope')", False, "a missing team is still a foreign-key error", expect="FOREIGN KEY")
check(db6, "INSERT INTO ChecklistTasks(Id,StageGateId,TaskDescription,OwnerTeamId,SequenceOrder) VALUES('k9','g1','x','tm2',9)", False, "task owned by another workspace's team", expect="team belongs to another workspace")
check(db6, "INSERT INTO ChecklistTasks(Id,StageGateId,BundledProductId,TaskDescription,OwnerUserId,SequenceOrder) VALUES('k9','g1','p9','x','rte',9)", False, "task for another train's product", expect="product belongs to another train")
check(db6, "UPDATE ChecklistTasks SET StageGateId='gw' WHERE Id='k1'", False, "task moved to another train's gate", expect="cannot move to another train")
check(db6, "UPDATE ChecklistTasks SET StageGateId='g2' WHERE Id='k1'", True, "task moved to another gate of its train")
check(db6, "UPDATE StageGates SET ReleaseTrainId='t2' WHERE Id='g2'", False, "gate moved to another train", expect="cannot move to another train")
check(db6, "UPDATE BundledProducts SET ReleaseTrainId='t2' WHERE Id='p2'", False, "product moved to another train", expect="cannot move to another train")
check(db6, "INSERT INTO RunbookSteps(Id,ReleaseTrainId,BundledProductId,StepCode,Title,OwnerUserId,PlannedStartAt,PlannedDurationMin) VALUES('s8','t1','p9','R-009','x','rte',?,5)", False, "runbook step for another train's product", (NOW,), expect="product belongs to another train")
check(db6, "INSERT INTO RunbookSteps(Id,ReleaseTrainId,StepCode,Title,OwnerTeamId,PlannedStartAt,PlannedDurationMin) VALUES('s8','t1','R-009','x','tm2',?,5)", False, "runbook step owned by another workspace's team", (NOW,), expect="team belongs to another workspace")
check(db6, "INSERT INTO StepDependencies(StepId,DependsOnStepId) VALUES('s1','s9')", False, "step depends on another train's step", expect="step belongs to another train")
db6.execute("INSERT INTO RunbookRuns(Id,ReleaseTrainId,Mode,StartedAt,StartedByUserId) VALUES('r9','t1','Rehearsal',?,'rte')", (NOW,))
check(db6, "INSERT INTO StepExecutions(Id,RunId,StepId) VALUES('e9','r9','s9')", False, "a run executes another train's step", expect="step belongs to another train")
check(db6, "INSERT INTO StepExecutions(Id,RunId,StepId) VALUES('e9','r9','s1')", True, "a run executes its own train's step")
check(db6, "INSERT INTO Blockers(Id,ReleaseTrainId,StageGateId,Title,Severity,RaisedAt) VALUES('b9','t1','gw','x','High',?)", False, "blocker on another train's gate", (NOW,), expect="gate belongs to another train")
check(db6, "INSERT INTO TrainMilestones(Id,ReleaseTrainId,Name,DueOn,OwnerTeamId) VALUES('m9','t1','UAT','2026-10-21','tm2')", False, "milestone owned by another workspace's team", expect="team belongs to another workspace")
check(db6, "INSERT INTO TemplateGates(Id,TemplateId,GateName,SequenceOrder,OffsetDays,RequiredBeforeStatus,OwnerTeamId) VALUES('tg1','tp1','CAB',1,2,'Gated','tm2')", False, "template gate owned by another workspace's team", expect="team belongs to another workspace")
check(db6, "INSERT INTO TemplateSteps(Id,TemplateId,StepCode,Section,Title,OffsetMinutes,PlannedDurationMin,OwnerTeamId) VALUES('ts1','tp2','R-1','Deploy','x',0,5,'tm2')", True, "template step owned by its own workspace's team")
check(db6, "INSERT INTO TemplateCommSchedule(Id,TemplateId,LibraryTemplateId,OffsetDays) VALUES('tc1','tp1','lib2',-28)", False, "template schedules another workspace's library template", expect="library template belongs to another workspace")
check(db6, "INSERT INTO CommTemplates(Id,ReleaseTrainId,LibraryTemplateId,TemplateType,Audience,SubjectLine,MarkdownBody) VALUES('ct1','t1','lib2','Tminus28','All','s','b')", False, "train copies another workspace's library template", expect="library template belongs to another workspace")
db6.execute("INSERT INTO CommTemplates(Id,ReleaseTrainId,TemplateType,Audience,SubjectLine,MarkdownBody) VALUES('ct2','t2','Tminus28','All','s','b')")
check(db6, "INSERT INTO CommSchedule(Id,ReleaseTrainId,CommTemplateId,DueAt) VALUES('cs1','t1','ct2',?)", False, "train schedules another train's message", (NOW,), expect="communication template belongs to another train")
db6.execute("INSERT INTO CommTemplates(Id,ReleaseTrainId,TemplateType,Audience,SubjectLine,MarkdownBody) VALUES('ct3','t1','Tminus28','All','s','b')")
check(db6, "INSERT INTO CommDispatches(Id,CommTemplateId,Channel,WebhookDestinationId,HydratedSubject,HydratedBody,DispatchedByUserId,DispatchedAt,Outcome) VALUES('d1','ct3','Webhook','h2','s','b','rte',?,'Handed')", False, "message sent to another workspace's webhook", (NOW,), expect="webhook destination belongs to another workspace")
check(db6, "INSERT INTO FreezeOverrides(Id,FreezeWindowId,ReleaseTrainId,Reason,RequestedByUserId,ApprovedByUserId,ApprovedAt,ExpiresAt) VALUES('o9','f2','t1','Emergency payments fix for EU',?,?,?,?)", False, "override of another workspace's freeze", ('rm', 'gov1', NOW, '2026-10-30T06:00:00Z'), expect="freeze window belongs to another workspace")
check(db6, "INSERT INTO SyncAlerts(Id,ReleaseTrainId,SourceSystem,Kind,Fingerprint,ErrorMessage,FirstOccurredAt,LastOccurredAt,WorkspaceId) VALUES('a9','t1','Jira','NotFound','fp9','x',?,?,'w2')", False, "alert about another workspace's train", (NOW, NOW), expect="train belongs to another workspace")
check(db6, "INSERT INTO SyncAlerts(Id,SourceSystem,Kind,Fingerprint,ErrorMessage,FirstOccurredAt,LastOccurredAt,WorkspaceId) VALUES('a9','Backup','BackupFailed','fp9','disk full',?,?,NULL)", True, "host-wide alert (no workspace)", (NOW, NOW))
db6.execute("INSERT INTO ExportJobs(Id,Kind,FileName,RequestedByUserId,CreatedAt,WorkspaceId) VALUES('x9','EvidencePackPdf','e.pdf','rte',?,'w2')", (NOW,))
check(db6, "INSERT INTO MetricSnapshots(ExportJobId,ReleaseTrainId,MetricKey,Value,CapturedAt) VALUES('x9','t1','lead_time',1,?)", False, "evidence pack snapshots another workspace's train", (NOW,), expect="train belongs to another workspace")
check(db6, "INSERT INTO ImportJobs(Id,Kind,ReleaseTrainId,FileName,Sha256,RowCount,Status,UploadedByUserId,CreatedAt,WorkspaceId) VALUES('i9','Tasks','t1','f.csv','x',1,'Previewed','rte',?,'w2')", False, "import into another workspace's train", (NOW,), expect="train belongs to another workspace")
check(db6, "INSERT INTO AuditEvents(OccurredAt,ReleaseTrainId,EntityType,EntityId,Action,WorkspaceId) VALUES(?,'t1','Train','t1','x','w2')", False, "audit row filed under another workspace than its train", (NOW,), expect="train belongs to another workspace")
check(db6, "INSERT INTO AuditEvents(OccurredAt,EntityType,EntityId,Action,WorkspaceId) VALUES(?,'User','rte','Created',NULL)", True, "host-wide audit row (accounts)", (NOW,))
db6.execute("UPDATE StageGates SET Status='InProgress', LastChangedByUserId='rte' WHERE Id='gw'")
db6.execute(certify('gw', 'rte'))
assert_true(db6.execute("SELECT WorkspaceId FROM AuditEvents WHERE EntityId='gw' AND Action='EvidenceLocked'").fetchall() == [('w2',)],
            "an audit row written by a trigger carries the train's workspace")
# Freeze lockout reads the train's own workspace: an Executing train inserted directly, a Live run, a Deploy step
db6.executescript(f"""
INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,CurrentStatus,CreatedAt,UpdatedAt) VALUES('t7','Hotfix','2026-10-30','Executing','{NOW}','{NOW}');
INSERT INTO RunbookSteps(Id,ReleaseTrainId,StepCode,Section,Title,OwnerUserId,PlannedStartAt,PlannedDurationMin) VALUES('s7','t7','R-001','Deploy','Deploy','rte','2026-10-30T02:00:00Z',10);
INSERT INTO RunbookRuns(Id,ReleaseTrainId,Mode,StartedAt,StartedByUserId) VALUES('r7','t7','Live','{NOW}','rte');
INSERT INTO StepExecutions(Id,RunId,StepId) VALUES('e7','r7','s7');
""")
check(db6, "UPDATE StepExecutions SET Status='Running',ActualStartAt='2026-10-30T02:00:00Z' WHERE Id='e7'", True, "another workspace's freeze does not lock this train out")
db6.execute("UPDATE StepExecutions SET Status='Scheduled',ActualStartAt=NULL WHERE Id='e7'")
db6.execute("INSERT INTO FreezeWindows(Id,Name,StartsAt,EndsAt,CreatedByUserId) VALUES('f7','Year end','2026-10-01T00:00:00Z','2026-12-31T00:00:00Z','rm')")
check(db6, "UPDATE StepExecutions SET Status='Running',ActualStartAt='2026-10-30T02:00:00Z' WHERE Id='e7'", False, "its own workspace's freeze does", expect="Freeze window")
# Role triggers read the role held in the train's workspace (REOS-105), never Users.Role: gov1 is a Governance Officer in the default workspace only,
# dev is a Viewer by Users.Role but a Governance Officer in w2, and rte is in no role in w2's sense for approving.
db6.executescript(f"""
INSERT INTO WorkspaceMembers(WorkspaceId,UserId,Role,AddedAt) VALUES('w2','gov1','Viewer','{NOW}'),('w2','dev','GovernanceOfficer','{NOW}'),('w2','gov2','RTE','{NOW}');
INSERT INTO StageGates(Id,ReleaseTrainId,GateName,GateClass,SequenceOrder,OffsetDays,DueOn,OwnerUserId,Status,LastChangedByUserId) VALUES('gc','t2','EU Compliance','Compliance',2,2,'2026-11-28','dev','InProgress','dev');
INSERT INTO ChecklistTasks(Id,StageGateId,TaskDescription,OwnerUserId,SequenceOrder,IsCompleted,CompletedAt,CompletedByUserId) VALUES('kc','gc','EU evidence','rte',1,1,'{NOW}','rte');
INSERT INTO GateWaivers(Id,StageGateId,Reason,RequestedByUserId,RequestedAt) VALUES('wv9','gc','EU regulator extension granted in writing','rte','{NOW}');
""")
check(db6, certify('gc', 'gov1'), False, "Compliance certifier who is only a Viewer in the train's workspace", expect="Governance Officers only")
check(db6, certify('gc', 'rm'), False, "Compliance certifier who is not a member of the train's workspace", expect="Governance Officers only")
check(db6, "UPDATE GateWaivers SET ApprovedByUserId='gov1',ApprovedAt=? WHERE Id='wv9'", False, "waiver approver who is a Governance Officer elsewhere only", (NOW,), expect="approved by a Governance Officer")
check(db6, "INSERT INTO GateWaivers(Id,StageGateId,Reason,RequestedByUserId,ApprovedByUserId,RequestedAt,ApprovedAt) VALUES('wv8','gc','Inserted approved by an outsider','rte','gov2',?,?)", False, "waiver inserted pre-approved by an RTE of the workspace", (NOW,NOW), expect="approved by a Governance Officer")
check(db6, "UPDATE GateWaivers SET ApprovedByUserId='dev',ApprovedAt=? WHERE Id='wv9'", True, "waiver approved by the workspace's Governance Officer (a Viewer by Users.Role)", (NOW,))
check(db6, certify('gc', 'dev'), True, "Compliance gate certified by the workspace's Governance Officer")
check(db6, OVERRIDE, False, "freeze override approved by someone who is not a member of the train's workspace", ('o8', 'f2', 't2', 'Emergency payments fix for EU', 'gov2', 'rm', NOW, '2026-10-30T06:00:00Z'), expect="approved by a Release Manager or Governance Officer")
check(db6, OVERRIDE, False, "freeze override approved by a Viewer of the train's workspace", ('o8', 'f2', 't2', 'Emergency payments fix for EU', 'gov2', 'gov1', NOW, '2026-10-30T06:00:00Z'), expect="approved by a Release Manager or Governance Officer")
check(db6, OVERRIDE, True, "freeze override approved by the workspace's Governance Officer", ('o8', 'f2', 't2', 'Emergency payments fix for EU', 'gov2', 'dev', NOW, '2026-10-30T06:00:00Z'))
db6.execute("UPDATE WorkspaceMembers SET Role='Viewer' WHERE WorkspaceId='default' AND UserId='gov2'")
db6.execute("UPDATE StageGates SET Status='InProgress', LastChangedByUserId='gov1' WHERE Id='g2'") if db6.execute("SELECT Status FROM StageGates WHERE Id='g2'").fetchone()[0] != 'InProgress' else None
db6.execute("UPDATE ChecklistTasks SET IsCompleted=1,CompletedAt=?,CompletedByUserId='gov1',LastChangedByUserId='gov1' WHERE Id='k2'", (NOW,))
check(db6, certify('g2', 'gov2'), False, "a Governance Officer by Users.Role whose membership is now Viewer", expect="Governance Officers only")
assert_true(db.execute("SELECT count(*), sum(WorkspaceId IS NOT 'default') FROM AuditEvents").fetchone()[1] == 0,
            "every audit row of the default workspace's release (service and trigger rows) is filed under it")

# ---- Release model, environments, service catalog, deployments (REOS-107, 109, 110) ----
print("\nRelease model, environments, catalog, deployments")
db7 = db_fresh()
db7.executescript(f"""
INSERT INTO Workspaces(Id,Name,Slug,CreatedAt,KeyPrefix) VALUES('w2','EU','eu','{NOW}','EU');
INSERT INTO Teams(Id,Handle,Name) VALUES('tm1','ops','Ops');
INSERT INTO Teams(Id,Handle,Name,WorkspaceId) VALUES('tm2','euops','EU Ops','w2');
INSERT INTO WorkspaceMembers(WorkspaceId,UserId,Role,AddedAt) VALUES('w2','rm','ReleaseManager','{NOW}');
INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,CreatedAt,UpdatedAt,WorkspaceId) VALUES('t2','EU R1','2026-11-30','{NOW}','{NOW}','w2');
INSERT INTO Environments(Id,Name,Kind,CreatedAt) VALUES('e1','Staging','Staging','{NOW}'),('e2','Prod EU','Production','{NOW}');
INSERT INTO Environments(Id,Name,Kind,CreatedAt,WorkspaceId) VALUES('e9','Prod','Production','{NOW}','w2');
INSERT INTO CatalogServices(Id,ServiceKey,Name,CreatedAt) VALUES('sv1','payments-api','Payments API','{NOW}');
INSERT INTO CatalogServices(Id,ServiceKey,Name,CreatedAt,WorkspaceId) VALUES('sv9','payments-api','Payments API EU','{NOW}','w2');
INSERT INTO IngestKeys(Id,Name,KeyPrefix,KeySha256,CreatedAt) VALUES('ik1','CI','rk_ab12',printf('%064d',1),'{NOW}');
""")
assert_true(db7.execute("SELECT ReleaseKey FROM ReleaseTrains WHERE Id='t1'").fetchone() == ('RLS-1',), "a train inserted without a key takes the workspace's next one")
assert_true(db7.execute("SELECT ReleaseKey FROM ReleaseTrains WHERE Id='t2'").fetchone() == ('EU-1',), "the key uses the train's workspace prefix and counter")
db7.execute(f"INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,CreatedAt,UpdatedAt) VALUES('t3','R2','2026-11-30','{NOW}','{NOW}')")
assert_true(db7.execute("SELECT ReleaseKey, (SELECT NextKeyNumber FROM Workspaces WHERE Id='default') FROM ReleaseTrains WHERE Id='t3'").fetchone() == ('RLS-2', 3), "keys count up")
db7.execute("DELETE FROM ReleaseTrains WHERE Id='t3'")
db7.execute(f"INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,CreatedAt,UpdatedAt) VALUES('t4','R3','2026-11-30','{NOW}','{NOW}')")
assert_true(db7.execute("SELECT ReleaseKey FROM ReleaseTrains WHERE Id='t4'").fetchone() == ('RLS-3',), "a deleted release's number is not reused")
check(db7, "UPDATE ReleaseTrains SET ReleaseKey='RLS-99' WHERE Id='t1'", False, "a release key never changes", expect="release key never changes")
check(db7, "UPDATE ReleaseTrains SET ReleaseKey='RLS-1' WHERE Id='t4'", False, "a release key never changes (to another's)", expect="release key never changes")
check(db7, f"INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,CreatedAt,UpdatedAt,ReleaseKey) VALUES('t5','x','2026-11-30','{NOW}','{NOW}','RLS-1')", False, "two releases with one key in a workspace", expect="UNIQUE")
check(db7, f"INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,CreatedAt,UpdatedAt,ReleaseKey,WorkspaceId) VALUES('t5','x','2026-11-30','{NOW}','{NOW}','RLS-1','w2')", True, "the same key in another workspace")
check(db7, f"INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,CreatedAt,UpdatedAt,ReleaseKey) VALUES('t6','x','2026-11-30','{NOW}','{NOW}','rls 7')", False, "a key with a space or lower case", expect="CHECK")
check(db7, "UPDATE Workspaces SET KeyPrefix='r' WHERE Id='w2'", False, "a one-letter key prefix", expect="CHECK")
check(db7, "UPDATE Workspaces SET KeyPrefix='rel' WHERE Id='w2'", False, "a lower-case key prefix", expect="CHECK")
check(db7, "UPDATE Workspaces SET KeyPrefix='EU2' WHERE Id='w2'", True, "a valid key prefix")
check(db7, "UPDATE ReleaseTrains SET OwnerUserId='rte' WHERE Id='t1'", True, "owner who is a member of the workspace")
check(db7, "UPDATE ReleaseTrains SET OwnerUserId='rte' WHERE Id='t2'", False, "owner who is not a member of the train's workspace", expect="owner is not a member of this workspace")
check(db7, "UPDATE ReleaseTrains SET OwnerTeamId='tm2' WHERE Id='t1'", False, "owning team of another workspace", expect="owning team belongs to another workspace")
check(db7, "UPDATE ReleaseTrains SET OwnerTeamId='tm1', ReleaseBranch='release/26.10', OnCallTeamId='tm1' WHERE Id='t1'", True, "team, branch and on-call team")
check(db7, "UPDATE ReleaseTrains SET OnCallUserId='rm' WHERE Id='t1'", False, "on-call a person and a team", expect="CHECK")
check(db7, "UPDATE ReleaseTrains SET OnCallTeamId=NULL, OnCallUserId='rm' WHERE Id='t1'", True, "on-call a person")
check(db7, "UPDATE ReleaseTrains SET OnCallUserId='dev' WHERE Id='t2'", False, "on-call person who is not a member of the train's workspace", expect="on-call person is not a member of this workspace")
check(db7, "UPDATE ReleaseTrains SET ReleaseBranch='  ' WHERE Id='t1'", False, "a blank release branch", expect="CHECK")
check(db7, "INSERT INTO ReleaseTypes(Id,Name) VALUES('ty1','CERT')", True, "a release type")
check(db7, "INSERT INTO ReleaseTypes(Id,Name) VALUES('ty2','cert')", False, "a release type name twice in a workspace (any case)", expect="UNIQUE")
check(db7, "INSERT INTO ReleaseTypes(Id,Name,WorkspaceId) VALUES('ty3','CERT','w2')", True, "the same type name in another workspace")
check(db7, "UPDATE ReleaseTrains SET ReleaseTypeId='ty3' WHERE Id='t1'", False, "a release of another workspace's type", expect="release type belongs to another workspace")
check(db7, "UPDATE ReleaseTrains SET ReleaseTypeId='ty1' WHERE Id='t1'", True, "a release of its workspace's type")
check(db7, "UPDATE ReleaseTypes SET WorkspaceId='w2' WHERE Id='ty1'", False, "a release type moved to another workspace", expect="cannot move to another workspace")
check(db7, "INSERT INTO Environments(Id,Name,Kind,CreatedAt) VALUES('e3','Dev','Dev',?)", False, "an environment kind outside staging and production", (NOW,), expect="CHECK")
check(db7, "INSERT INTO Environments(Id,Name,Kind,CreatedAt) VALUES('e3','staging','Staging',?)", False, "an environment name twice in a workspace (any case)", (NOW,), expect="UNIQUE")
check(db7, "INSERT INTO Environments(Id,Name,Kind,CreatedAt,ErrorRateMaxPct) VALUES('e3','UAT','Staging',?,120)", False, "an error-rate threshold over 100 %", (NOW,), expect="CHECK")
check(db7, "INSERT INTO Environments(Id,Name,Kind,CreatedAt,ControlWebhookDestinationId) VALUES('e3','UAT','Staging',?,'nope')", False, "a control webhook that does not exist", (NOW,), expect="FOREIGN KEY")
db7.execute("INSERT INTO WebhookDestinations(Id,Name,Host,ProtectedUrl,UrlHmac,Kind,WorkspaceId) VALUES('hw2','EU hook','hooks.eu.example','CfDJ8X',printf('%064d',7),'Generic','w2')")
check(db7, "UPDATE Environments SET ControlWebhookDestinationId='hw2' WHERE Id='e1'", False, "environment control webhook of another workspace", expect="webhook destination belongs to another workspace")
check(db7, "INSERT INTO TrainEnvironments(ReleaseTrainId,EnvironmentId) VALUES('t1','e1')", True, "a release targets an environment")
check(db7, "INSERT INTO TrainEnvironments(ReleaseTrainId,EnvironmentId) VALUES('t1','e9')", False, "a release targets another workspace's environment", expect="environment belongs to another workspace")
check(db7, "INSERT INTO TrainEnvironments(ReleaseTrainId,EnvironmentId) VALUES('t1','e1')", False, "the same environment twice on a release", expect="UNIQUE")
check(db7, "INSERT INTO CatalogServices(Id,ServiceKey,Name,CreatedAt) VALUES('sv2','PAYMENTS-API','x',?)", False, "a service key twice in a workspace (any case)", (NOW,), expect="UNIQUE")
check(db7, "INSERT INTO CatalogServices(Id,ServiceKey,Name,CreatedAt) VALUES('sv2','pay ments','x',?)", False, "a service key with a space", (NOW,), expect="CHECK")
check(db7, "INSERT INTO CatalogServices(Id,ServiceKey,Name,CreatedAt,OwnerTeamId) VALUES('sv2','card-portal','Card Portal',?,'tm2')", False, "a service owned by another workspace's team", (NOW,), expect="team belongs to another workspace")
check(db7, "INSERT INTO CatalogServices(Id,ServiceKey,Name,CreatedAt,OwnerTeamId) VALUES('sv2','card-portal','Card Portal',?,'tm1')", True, "a service owned by its workspace's team", (NOW,))
check(db7, "UPDATE BundledProducts SET CatalogServiceId='sv1' WHERE Id='p1'", True, "a product linked to a service of its workspace")
check(db7, "UPDATE BundledProducts SET CatalogServiceId='sv9' WHERE Id='p2'", False, "a product linked to another workspace's service", expect="catalog service belongs to another workspace")
check(db7, "INSERT INTO IngestKeys(Id,Name,KeyPrefix,KeySha256,CreatedAt) VALUES('ik2','x','rk_zz99',printf('%064d',1),?)", False, "one key hash twice", (NOW,), expect="UNIQUE")
check(db7, "INSERT INTO IngestKeys(Id,Name,KeyPrefix,KeySha256,CreatedAt) VALUES('ik2','x','rk_zz99','NOTHEX',?)", False, "a key hash that is not 64 hex characters", (NOW,), expect="CHECK")
check(db7, "UPDATE IngestKeys SET RevokedAt=? WHERE Id='ik1'", False, "revoked without saying by whom", (NOW,), expect="CHECK")
check(db7, "UPDATE IngestKeys SET RevokedAt=?, RevokedByUserId='rte' WHERE Id='ik1'", True, "a key revoked by a person", (NOW,))
check(db7, "UPDATE IngestKeys SET WorkspaceId='w2' WHERE Id='ik1'", False, "a key moved to another workspace", expect="cannot move to another workspace")
DEP = "INSERT INTO Deployments(Id,ExternalId,CatalogServiceId,EnvironmentId,ReleaseTrainId,RunId,ReleaseVersion,Strategy,CanarySteps,State,StartedAt,FinishedAt,CreatedAt,UpdatedAt,WorkspaceId) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)"
def dep(id, ext='ci-1', svc='sv1', env='e1', train='t1', run=None, ver='4.5.0', strat='Rolling', steps=None, state='Queued', started=None, finished=None, ws='default'):
    return (id, ext, svc, env, train, run, ver, strat, steps, state, started, finished, NOW, NOW, ws)
check(db7, DEP, True, "a deployment of a service to an environment for a release", dep('d1'))
check(db7, DEP, False, "the same external id twice in a workspace", dep('d2'), expect="UNIQUE")
check(db7, DEP, True, "the same external id in another workspace", dep('d2', svc='sv9', env='e9', train='t2', ws='w2'))
check(db7, DEP, False, "a deployment of another workspace's service", dep('d3', ext='ci-3', svc='sv9'), expect="service belongs to another workspace")
check(db7, DEP, False, "a deployment to another workspace's environment", dep('d3', ext='ci-3', env='e9'), expect="environment belongs to another workspace")
check(db7, DEP, False, "a deployment for another workspace's release", dep('d3', ext='ci-3', train='t2'), expect="train belongs to another workspace")
check(db7, DEP, False, "an unknown strategy", dep('d3', ext='ci-3', strat='BlueGreen'), expect="CHECK")
check(db7, DEP, False, "canary steps on a rolling deployment", dep('d3', ext='ci-3', steps='[5,50,100]'), expect="CHECK")
check(db7, DEP, False, "canary steps that are not JSON", dep('d3', ext='ci-3', strat='Canary', steps='5,50'), expect="CHECK")
check(db7, DEP, True, "canary steps as a JSON array", dep('d3', ext='ci-3', strat='Canary', steps='[5,50,100]'))
check(db7, DEP, False, "running without a start time", dep('d4', ext='ci-4', state='Running'), expect="CHECK")
check(db7, DEP, False, "finished without a finish time", dep('d4', ext='ci-4', state='Successful', started=NOW), expect="CHECK")
check(db7, DEP, False, "a finish time on a queued deployment", dep('d4', ext='ci-4', finished=NOW), expect="CHECK")
check(db7, "UPDATE Deployments SET TrafficPercent=101 WHERE Id='d1'", False, "traffic over 100 %", expect="CHECK")
check(db7, "UPDATE Deployments SET State='Successful', StartedAt=?, FinishedAt=? WHERE Id='d1'", False, "queued straight to successful", (NOW, NOW), expect="Illegal deployment state transition")
check(db7, "UPDATE Deployments SET State='Running', StartedAt=? WHERE Id='d1'", True, "queued to running", (NOW,))
check(db7, "UPDATE Deployments SET State='Paused' WHERE Id='d1'", True, "running to paused")
check(db7, "UPDATE Deployments SET State='Successful', FinishedAt=? WHERE Id='d1'", False, "paused straight to successful", (NOW,), expect="Illegal deployment state transition")
check(db7, "UPDATE Deployments SET State='Running' WHERE Id='d1'", True, "paused back to running")
check(db7, "UPDATE Deployments SET State='Successful', FinishedAt=? WHERE Id='d1'", True, "running to successful", (NOW,))
check(db7, "UPDATE Deployments SET State='Running', FinishedAt=NULL WHERE Id='d1'", False, "successful back to running", expect="Illegal deployment state transition")
check(db7, "UPDATE Deployments SET State='RolledBack' WHERE Id='d1'", True, "successful to rolled back (the finish time stays)")
check(db7, "UPDATE Deployments SET State='Failed' WHERE Id='d1'", False, "rolled back is final", expect="Illegal deployment state transition")
check(db7, "UPDATE Deployments SET EnvironmentId='e2' WHERE Id='d1'", False, "a deployment moved to another environment", expect="keeps its external id, service and environment")
check(db7, "UPDATE Deployments SET ReleaseTrainId='t2' WHERE Id='d1'", False, "a deployment linked to another workspace's release", expect="train belongs to another workspace")
db7.execute("INSERT INTO RunbookRuns(Id,ReleaseTrainId,Mode,StartedAt,StartedByUserId) VALUES('r1','t4','Rehearsal',?,'rte')", (NOW,))
check(db7, "UPDATE Deployments SET RunId='r1' WHERE Id='d3'", False, "a deployment linked to a run of another release", expect="run belongs to another train")
check(db7, "UPDATE Deployments SET ReleaseTrainId='t4', RunId='r1' WHERE Id='d3'", True, "a deployment linked to a release and its run")
check(db7, "INSERT INTO DeploymentEvents(DeploymentId,OccurredAt,ReceivedAt,Level,Message,ExternalId) VALUES('d3',?,?,'Info','Canary at 5 %','e-1')", True, "a deployment event", (NOW, NOW))
check(db7, "INSERT INTO DeploymentEvents(DeploymentId,OccurredAt,ReceivedAt,Level,Message,ExternalId) VALUES('d3',?,?,'Info','again','e-1')", False, "an event id twice on a deployment", (NOW, NOW), expect="UNIQUE")
check(db7, "INSERT INTO DeploymentEvents(DeploymentId,OccurredAt,ReceivedAt,Level,Message) VALUES('d3',?,?,'Info','no id')", True, "an event without an id", (NOW, NOW))
check(db7, "INSERT INTO DeploymentEvents(DeploymentId,OccurredAt,ReceivedAt,Level,Message) VALUES('d3',?,?,'Debug','x')", False, "an event level outside the three", (NOW, NOW), expect="CHECK")
check(db7, "INSERT INTO DeploymentEvents(DeploymentId,OccurredAt,ReceivedAt,Level,Message) VALUES('d3',?,?,'Info','')", False, "an empty event message", (NOW, NOW), expect="CHECK")
check(db7, "UPDATE DeploymentEvents SET Message='edited' WHERE Id=1", False, "an event edited", expect="append-only")
check(db7, "DELETE FROM Deployments WHERE Id='d3'", True, "deleting a deployment removes its events with it")
assert_true(db7.execute("SELECT count(*) FROM DeploymentEvents").fetchone() == (0,), "no orphan events")

# ---- Telemetry, deployment control, approvals and the audit chain (REOS-111, 112, 113, 114) ----
print("\nTelemetry, deployment control, approvals, audit chain")
db8 = db_fresh()
db8.executescript("""
INSERT INTO Workspaces(Id,Name,Slug,CreatedAt) VALUES('w2','EU','eu','2026-10-20T14:00:00Z');
INSERT INTO Teams(Id,Handle,Name) VALUES('tm1','sec','Security');
INSERT INTO Teams(Id,Handle,Name,WorkspaceId) VALUES('tm2','eusec','EU Security','w2');
INSERT INTO TeamMembers(TeamId,UserId) VALUES('tm1','dev'),('tm1','gov1');
INSERT INTO WorkspaceMembers(WorkspaceId,UserId,Role,AddedAt) VALUES('w2','rm','ReleaseManager','2026-10-20T14:00:00Z');
INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,CreatedAt,UpdatedAt,WorkspaceId) VALUES('t2','EU R1','2026-11-30','2026-10-20T14:00:00Z','2026-10-20T14:00:00Z','w2');
INSERT INTO Environments(Id,Name,Kind,CreatedAt) VALUES('e1','Staging','Staging','2026-10-20T14:00:00Z'),('e2','Prod','Production','2026-10-20T14:00:00Z');
INSERT INTO Environments(Id,Name,Kind,CreatedAt,WorkspaceId) VALUES('e9','Prod EU','Production','2026-10-20T14:00:00Z','w2');
INSERT INTO CatalogServices(Id,ServiceKey,Name,CreatedAt) VALUES('sv1','payments-api','Payments API','2026-10-20T14:00:00Z');
INSERT INTO CatalogServices(Id,ServiceKey,Name,CreatedAt,WorkspaceId) VALUES('sv9','payments-api','Payments API EU','2026-10-20T14:00:00Z','w2');
INSERT INTO Deployments(Id,ExternalId,CatalogServiceId,EnvironmentId,ReleaseTrainId,ReleaseVersion,CreatedAt,UpdatedAt) VALUES('d1','ci-1','sv1','e2','t1','4.5.0','2026-10-20T14:00:00Z','2026-10-20T14:00:00Z');
INSERT INTO Deployments(Id,ExternalId,CatalogServiceId,EnvironmentId,ReleaseTrainId,ReleaseVersion,CreatedAt,UpdatedAt,WorkspaceId) VALUES('d9','ci-9','sv9','e9','t2','4.5.0','2026-10-20T14:00:00Z','2026-10-20T14:00:00Z','w2');
UPDATE ReleaseTrains SET OwnerUserId='rm' WHERE Id='t1';
""")
check(db8, "INSERT INTO MetricSamples(DeploymentId,Series,SampledAt,ReceivedAt,ErrorRatePct,LatencyP95Ms) VALUES('d1','Canary','2026-10-20T14:00:00Z','2026-10-20T14:00:00Z',0.2,120)", True, "a metric sample")
check(db8, "INSERT INTO MetricSamples(DeploymentId,Series,SampledAt,ReceivedAt,ErrorRatePct) VALUES('d1','Canary','2026-10-20T14:00:00Z','2026-10-20T14:00:00Z',0.3)", False, "the same sample twice (deployment, series, time)", expect="UNIQUE")
check(db8, "INSERT INTO MetricSamples(DeploymentId,Series,SampledAt,ReceivedAt,ErrorRatePct) VALUES('d1','Stable','2026-10-20T14:00:00Z','2026-10-20T14:00:00Z',0.1)", True, "the same time in the other series")
check(db8, "INSERT INTO MetricSamples(DeploymentId,Series,SampledAt,ReceivedAt) VALUES('d1','Canary','2026-10-20T14:00:30Z','2026-10-20T14:00:00Z')", False, "a sample with no metric at all", expect="CHECK")
check(db8, "INSERT INTO MetricSamples(DeploymentId,Series,SampledAt,ReceivedAt,ErrorRatePct) VALUES('d1','Canary','2026-10-20T14:00:30Z','2026-10-20T14:00:00Z',120)", False, "an error rate over 100 %", expect="CHECK")
check(db8, "INSERT INTO MetricSamples(DeploymentId,Series,SampledAt,ReceivedAt,LatencyP95Ms) VALUES('d1','Canary','2026-10-20T14:00:30Z','2026-10-20T14:00:00Z',-1)", False, "a negative latency", expect="CHECK")
check(db8, "INSERT INTO MetricSamples(DeploymentId,Series,SampledAt,ReceivedAt,ErrorRatePct) VALUES('d1','Beta','2026-10-20T14:00:30Z','2026-10-20T14:00:00Z',1)", False, "a series outside canary and stable", expect="CHECK")
check(db8, "INSERT INTO MetricSamples(DeploymentId,Series,SampledAt,ReceivedAt,ErrorRatePct,WorkspaceId) VALUES('d1','Canary','2026-10-20T14:00:30Z','2026-10-20T14:00:00Z',1,'w2')", False, "a sample filed under another workspace than its deployment", expect="deployment belongs to another workspace")
check(db8, "UPDATE MetricSamples SET WorkspaceId='w2' WHERE Id=1", False, "a sample moved to another workspace", expect="cannot move to another workspace")
check(db8, "INSERT INTO MetricRollups(DeploymentId,Series,BucketAt,Samples,ErrorRateAvgPct,ErrorRateMaxPct) VALUES('d1','Canary','2026-09-01T10:00:00Z',120,0.2,0.9)", True, "an hourly rollup")
check(db8, "INSERT INTO MetricRollups(DeploymentId,Series,BucketAt,Samples) VALUES('d1','Canary','2026-09-01T10:00:00Z',1)", False, "a rollup bucket twice", expect="UNIQUE")
check(db8, "INSERT INTO MetricRollups(DeploymentId,Series,BucketAt,Samples) VALUES('d1','Stable','2026-09-01T10:00:00Z',0)", False, "a rollup of no samples", expect="CHECK")
check(db8, "INSERT INTO MetricRollups(DeploymentId,Series,BucketAt,Samples,WorkspaceId) VALUES('d1','Stable','2026-09-01T10:00:00Z',1,'w2')", False, "a rollup filed under another workspace than its deployment", expect="deployment belongs to another workspace")
check(db8, "UPDATE Environments SET ThresholdForSeconds=-1 WHERE Id='e2'", False, "a threshold duration below zero", expect="CHECK")
check(db8, "UPDATE Environments SET CanaryObservationSeconds=5 WHERE Id='e2'", False, "a canary observation window under 30 seconds", expect="CHECK")
check(db8, "UPDATE Environments SET ThresholdForSeconds=120, CanaryObservationSeconds=300 WHERE Id='e2'", True, "a threshold duration and observation window")
check(db8, "INSERT INTO ThresholdBreaches(Id,DeploymentId,Series,Metric,ThresholdValue,ForSeconds,StartedAt,PeakValue,LastValue) VALUES('b1','d1','Canary','ErrorRate',0.5,120,'2026-10-20T14:00:00Z',0.9,0.9)", True, "a threshold breach, watching")
check(db8, "INSERT INTO ThresholdBreaches(Id,DeploymentId,Series,Metric,ThresholdValue,ForSeconds,StartedAt,PeakValue,LastValue) VALUES('b2','d1','Canary','ErrorRate',0.5,120,'2026-10-20T14:00:00Z',0.9,0.9)", False, "a second open breach of one metric in one series", expect="UNIQUE")
check(db8, "INSERT INTO ThresholdBreaches(Id,DeploymentId,Series,Metric,ThresholdValue,ForSeconds,StartedAt,PeakValue,LastValue) VALUES('b3','d1','Canary','LatencyP95',300,120,'2026-10-20T14:00:00Z',410,410)", True, "an open breach of another metric")
check(db8, "UPDATE ThresholdBreaches SET ConfirmedAt='2026-10-20T14:02:00Z' WHERE Id='b1'", False, "a breach confirmed without a detection time", expect="CHECK")
check(db8, "UPDATE ThresholdBreaches SET ConfirmedAt='2026-10-20T14:02:00Z', DetectedAt='2026-10-20T14:02:01Z' WHERE Id='b1'", True, "a breach confirmed")
check(db8, "UPDATE ThresholdBreaches SET ResolvedAt='2026-10-20T13:00:00Z' WHERE Id='b1'", False, "a breach resolved before it started", expect="CHECK")
check(db8, "UPDATE ThresholdBreaches SET ResolvedAt='2026-10-20T14:05:00Z' WHERE Id='b1'", True, "a breach resolved")
check(db8, "INSERT INTO ThresholdBreaches(Id,DeploymentId,Series,Metric,ThresholdValue,ForSeconds,StartedAt,PeakValue,LastValue) VALUES('b2','d1','Canary','ErrorRate',0.5,120,'2026-10-20T14:10:00Z',0.9,0.9)", True, "a new breach once the earlier one is resolved")
check(db8, "INSERT INTO ThresholdBreaches(Id,DeploymentId,Series,Metric,ThresholdValue,ForSeconds,StartedAt,PeakValue,LastValue,WorkspaceId) VALUES('b8','d1','Stable','ErrorRate',0.5,120,'2026-10-20T14:00:00Z',0.9,0.9,'w2')", False, "a breach filed under another workspace than its deployment", expect="deployment belongs to another workspace")
check(db8, "INSERT INTO ThresholdBreaches(Id,DeploymentId,Series,Metric,ThresholdValue,ForSeconds,StartedAt,PeakValue,LastValue,WorkspaceId) VALUES('b9','d9','Canary','ErrorRate',0.5,120,'2026-10-20T14:00:00Z',0.9,0.9,'w2')", True, "a breach of the other workspace's deployment")
check(db8, "UPDATE ThresholdBreaches SET WorkspaceId='w2' WHERE Id='b2'", False, "a breach moved to another workspace", expect="cannot move to another workspace")
check(db8, "INSERT INTO DeploymentControlActions(Id,DeploymentId,Action,Source,RequestedByUserId,RequestedAt,Reason) VALUES('ca1','d1','Pause','Manual','rte','2026-10-20T14:00:00Z','Error rate is climbing')", True, "a manual pause call")
check(db8, "INSERT INTO DeploymentControlActions(Id,DeploymentId,Action,Source,RequestedAt) VALUES('ca2','d1','Pause','Manual','2026-10-20T14:00:00Z')", False, "a manual call with nobody who asked", expect="CHECK")
check(db8, "INSERT INTO DeploymentControlActions(Id,DeploymentId,Action,Source,RequestedByUserId,RequestedAt) VALUES('ca2','d1','Rollback','Automatic','rte','2026-10-20T14:00:00Z')", False, "an automatic call that names a person", expect="CHECK")
check(db8, "INSERT INTO DeploymentControlActions(Id,DeploymentId,Action,Source,RequestedByUserId,RequestedAt) VALUES('ca2','d1','Abort','Manual','rte','2026-10-20T14:00:00Z')", False, "a control action outside pause, resume, promote and rollback", expect="CHECK")
check(db8, "INSERT INTO DeploymentControlActions(Id,DeploymentId,Action,Source,RequestedByUserId,RequestedAt,Status,CompletedAt) VALUES('ca2','d1','Pause','Manual','rte','2026-10-20T14:00:00Z','Failed','2026-10-20T14:00:00Z')", False, "a failed call without its error", expect="CHECK")
check(db8, "INSERT INTO DeploymentControlActions(Id,DeploymentId,Action,Source,RequestedByUserId,RequestedAt,CompletedAt) VALUES('ca2','d1','Pause','Manual','rte','2026-10-20T14:00:00Z','2026-10-20T14:00:00Z')", False, "a pending call that has a completion time", expect="CHECK")
check(db8, "INSERT INTO DeploymentControlActions(Id,DeploymentId,Action,Source,RequestedByUserId,RequestedAt,Status,CompletedAt,ErrorMessage,HttpStatus) VALUES('ca3','d1','Resume','Manual','rte','2026-10-20T14:00:00Z','Failed','2026-10-20T14:00:00Z','HTTP 503',503)", True, "a failed call with its error")
check(db8, "INSERT INTO DeploymentControlActions(Id,DeploymentId,Action,Source,RequestedByUserId,RequestedAt,BreachId) VALUES('ca2','d1','Rollback','Manual','rte','2026-10-20T14:00:00Z','b2')", False, "a breach named on a manual call", expect="CHECK")
check(db8, "INSERT INTO DeploymentControlActions(Id,DeploymentId,Action,Source,RequestedAt,BreachId) VALUES('ca4','d1','Rollback','Automatic','2026-10-20T14:00:00Z','b2')", True, "an automatic rollback for a breach")
check(db8, "INSERT INTO DeploymentControlActions(Id,DeploymentId,Action,Source,RequestedAt,BreachId) VALUES('ca5','d1','Rollback','Automatic','2026-10-20T14:00:00Z','b2')", False, "a second automatic rollback for the same breach", expect="UNIQUE")
check(db8, "INSERT INTO DeploymentControlActions(Id,DeploymentId,Action,Source,RequestedAt,BreachId) VALUES('ca5','d1','Rollback','Automatic','2026-10-20T14:00:00Z','b9')", False, "an automatic rollback naming another deployment's breach", expect="breach belongs to another deployment")
check(db8, "INSERT INTO DeploymentControlActions(Id,DeploymentId,Action,Source,RequestedByUserId,RequestedAt,WorkspaceId) VALUES('ca5','d1','Pause','Manual','rte','2026-10-20T14:00:00Z','w2')", False, "a control action filed under another workspace than its deployment", expect="deployment belongs to another workspace")
check(db8, "UPDATE DeploymentControlActions SET WorkspaceId='w2' WHERE Id='ca1'", False, "a control action moved to another workspace", expect="cannot move to another workspace")
check(db8, "UPDATE Deployments SET AutoRollbackArmedAt='2026-10-20T14:00:00Z', StepStartedAt='2026-10-20T14:00:00Z' WHERE Id='d1'", True, "a deployment armed for automatic rollback")
db8.executescript("""
INSERT INTO RunbookRuns(Id,ReleaseTrainId,Mode,StartedAt,StartedByUserId) VALUES('r1','t1','Rehearsal','2026-10-20T14:00:00Z','rte');
INSERT INTO StepExecutions(Id,RunId,StepId) VALUES('x1','r1','s1'),('x2','r1','s2');
""")
check(db8, "UPDATE RunbookRuns SET PausedAt='2026-10-20T14:00:00Z', PauseReason='Hold for the incident' WHERE Id='r1'", False, "a run paused with nobody who paused it", expect="CHECK")
check(db8, "UPDATE RunbookRuns SET PausedAt='2026-10-20T14:00:00Z', PausedByUserId='rte' WHERE Id='r1'", False, "a run paused without a reason", expect="CHECK")
check(db8, "UPDATE RunbookRuns SET PausedAt='2026-10-20T14:00:00Z', PausedByUserId='rte', PauseReason='Hold for the incident' WHERE Id='r1'", True, "a run paused")
check(db8, "UPDATE StepExecutions SET Status='Running', ActualStartAt='2026-10-20T14:00:00Z', ActorUserId='rte' WHERE Id='x1'", False, "a step started in a paused run", expect="run is paused")
check(db8, "UPDATE StepExecutions SET Status='Running', ActualStartAt='2026-10-20T14:00:00Z', ActorUserId='rte' WHERE Id='x2'", False, "a second step started in a paused run", expect="run is paused")
check(db8, "UPDATE StepExecutions SET Status='Skipped', Note='not needed' WHERE Id='x2'", True, "a step skipped in a paused run (not a start)")
check(db8, "UPDATE RunbookRuns SET PausedAt=NULL, PausedByUserId=NULL, PauseReason=NULL WHERE Id='r1'", True, "a run resumed")
check(db8, "UPDATE StepExecutions SET Status='Running', ActualStartAt='2026-10-20T14:00:00Z', ActorUserId='rte' WHERE Id='x1'", True, "a step started once the run is resumed")
check(db8, "UPDATE RunbookRuns SET EndedAt='2026-10-20T14:00:00Z', Outcome='Aborted', PausedAt='2026-10-20T14:00:00Z', PausedByUserId='rte', PauseReason='x' WHERE Id='r1'", False, "a run that has ended and is still paused", expect="CHECK")
db8.executescript("""
INSERT INTO GateWaivers(Id,StageGateId,Reason,RequestedByUserId,RequestedAt) VALUES
 ('wv1','g1','This gate cannot be met before the freeze','gov2','2026-10-20T14:00:00Z'),('wv2','g1','This gate cannot be met before the freeze','rte','2026-10-20T14:00:00Z'),('wv3','g1','This gate cannot be met before the freeze','rte','2026-10-20T14:00:00Z');
""")
check(db8, "UPDATE GateWaivers SET DeclinedByUserId='rm', DeclinedAt='2026-10-20T14:00:00Z', DeclineNote='No' WHERE Id='wv2'", False, "a waiver declined by a Release Manager", expect="declined by a Governance Officer")
check(db8, "UPDATE GateWaivers SET DeclinedByUserId='gov2', DeclinedAt='2026-10-20T14:00:00Z', DeclineNote='Withdrawn' WHERE Id='wv1'", False, "a waiver declined by the person who asked for it", expect="two distinct people")
check(db8, "UPDATE GateWaivers SET DeclinedByUserId='gov1', DeclinedAt='2026-10-20T14:00:00Z' WHERE Id='wv2'", False, "a waiver declined without a note", expect="CHECK")
check(db8, "UPDATE GateWaivers SET DeclinedByUserId='gov1', DeclinedAt='2026-10-20T14:00:00Z', DeclineNote='The risk is not covered' WHERE Id='wv2'", True, "a waiver declined with a note")
check(db8, "UPDATE GateWaivers SET ApprovedByUserId='gov1', ApprovedAt='2026-10-20T14:00:00Z' WHERE Id='wv2'", False, "a declined waiver approved", expect="approved or declined")
check(db8, "UPDATE GateWaivers SET ApprovedByUserId='gov1', ApprovedAt='2026-10-20T14:00:00Z' WHERE Id='wv3'", True, "a waiver approved")
check(db8, "UPDATE GateWaivers SET DeclinedByUserId='gov2', DeclinedAt='2026-10-20T14:00:00Z', DeclineNote='Changed my mind' WHERE Id='wv3'", False, "an approved waiver declined", expect="approved or declined")
check(db8, "INSERT INTO SyncAlerts(Id,SourceSystem,Kind,Fingerprint,ErrorMessage,FirstOccurredAt,LastOccurredAt) VALUES('al1','Deployment','ControlFailed','fp1','The rollback call failed','2026-10-20T14:00:00Z','2026-10-20T14:00:00Z')", True, "an alert from the deployment side")
check(db8, "INSERT INTO SyncAlerts(Id,SourceSystem,Kind,Fingerprint,ErrorMessage,FirstOccurredAt,LastOccurredAt) VALUES('al2','Audit','IntegrityFailed','fp2','Audit row 5 does not verify','2026-10-20T14:00:00Z','2026-10-20T14:00:00Z')", True, "an alert from the audit chain")
check(db8, "INSERT INTO SyncAlerts(Id,SourceSystem,Kind,Fingerprint,ErrorMessage,FirstOccurredAt,LastOccurredAt) VALUES('al3','Telemetry','RetentionFailed','fp3','Summarising failed','2026-10-20T14:00:00Z','2026-10-20T14:00:00Z')", True, "an alert from telemetry retention")
check(db8, "INSERT INTO SyncAlerts(Id,SourceSystem,Kind,Fingerprint,ErrorMessage,FirstOccurredAt,LastOccurredAt) VALUES('al4','Pager','ControlFailed','fp4','x','2026-10-20T14:00:00Z','2026-10-20T14:00:00Z')", False, "an alert from a source that does not exist", expect="CHECK")
check(db8, "INSERT INTO ApprovalPolicies(Id,Name,EnvironmentKind,MinRiskTier,RequireGatesCertified,CreatedAt) VALUES('pol1','Production','Production','High',1,'2026-10-20T14:00:00Z')", True, "an approval policy")
check(db8, "INSERT INTO ApprovalPolicies(Id,Name,CreatedAt) VALUES('pol2','production','2026-10-20T14:00:00Z')", False, "a policy name twice in a workspace (any case)", expect="UNIQUE")
check(db8, "INSERT INTO ApprovalPolicies(Id,Name,CreatedAt,WorkspaceId) VALUES('pol9','Production','2026-10-20T14:00:00Z','w2')", True, "the same policy name in another workspace")
check(db8, "INSERT INTO ApprovalPolicies(Id,Name,EnvironmentKind,CreatedAt) VALUES('pol2','Dev','Dev','2026-10-20T14:00:00Z')", False, "a policy for an environment kind that does not exist", expect="CHECK")
check(db8, "INSERT INTO ApprovalPolicies(Id,Name,MinRiskTier,CreatedAt) VALUES('pol2','Risky','Severe','2026-10-20T14:00:00Z')", False, "a policy for a risk tier that does not exist", expect="CHECK")
check(db8, "UPDATE ApprovalPolicies SET WorkspaceId='w2' WHERE Id='pol1'", False, "a policy moved to another workspace", expect="cannot move to another workspace")
check(db8, "INSERT INTO ApprovalPolicyRequirements(Id,PolicyId,Label,EligibleKind,EligibleValue,DueHours,SortOrder) VALUES('rq1','pol1','Security reviewer','Team','tm1',24,1)", True, "a policy seat for a team")
check(db8, "INSERT INTO ApprovalPolicyRequirements(Id,PolicyId,Label,EligibleKind,DueHours,SortOrder) VALUES('rq2','pol1','Release owner','ReleaseOwner',24,2)", True, "a policy seat for the release owner")
check(db8, "INSERT INTO ApprovalPolicyRequirements(Id,PolicyId,Label,EligibleKind,EligibleValue,DueHours,SortOrder) VALUES('rq3','pol1','SRE approver','WorkspaceRole','RTE',8,3)", True, "a policy seat for a workspace role")
check(db8, "INSERT INTO ApprovalPolicyRequirements(Id,PolicyId,Label,EligibleKind,EligibleValue) VALUES('rq4','pol1','EU security','Team','tm2')", False, "a policy seat for another workspace's team", expect="team belongs to another workspace")
check(db8, "INSERT INTO ApprovalPolicyRequirements(Id,PolicyId,Label,EligibleKind,EligibleValue) VALUES('rq4','pol1','SRE','WorkspaceRole','SRE')", False, "a policy seat for a role that does not exist", expect="CHECK")
check(db8, "INSERT INTO ApprovalPolicyRequirements(Id,PolicyId,Label,EligibleKind,EligibleValue) VALUES('rq4','pol1','Owner again','ReleaseOwner','rm')", False, "a release owner seat that names a value", expect="CHECK")
check(db8, "INSERT INTO ApprovalPolicyRequirements(Id,PolicyId,Label,EligibleKind,EligibleValue,DueHours) VALUES('rq4','pol1','Quick','WorkspaceRole','RTE',0)", False, "a policy seat due in 0 hours", expect="CHECK")
check(db8, "INSERT INTO ApprovalPolicyRequirements(Id,PolicyId,Label,EligibleKind,EligibleValue) VALUES('rq4','pol1','sre approver','WorkspaceRole','RTE')", False, "a seat label twice in a policy (any case)", expect="UNIQUE")
check(db8, "INSERT INTO Approvals(Id,ReleaseTrainId,EnvironmentId,Status,RequestedByUserId,RequestedAt,DueAt) VALUES('ap0','t1','e2','Approved','gov2','2026-10-20T14:00:00Z','2026-10-21T14:00:00Z')", False, "an approval that is approved without a decision time", expect="CHECK")
check(db8, "INSERT INTO Approvals(Id,ReleaseTrainId,EnvironmentId,RequestedByUserId,RequestedAt,DueAt) VALUES('ap1','t1','e2','gov2','2026-10-20T14:00:00Z','2026-10-21T14:00:00Z')", True, "an approval requested for a release and environment")
check(db8, "INSERT INTO Approvals(Id,ReleaseTrainId,EnvironmentId,RequestedByUserId,RequestedAt,DueAt) VALUES('ap2','t1','e2','gov2','2026-10-20T14:00:00Z','2026-10-21T14:00:00Z')", False, "a second open approval for the same release and environment", expect="UNIQUE")
check(db8, "INSERT INTO Approvals(Id,ReleaseTrainId,EnvironmentId,RequestedByUserId,RequestedAt,DueAt) VALUES('ap2','t1','e1','gov2','2026-10-20T14:00:00Z','2026-10-21T14:00:00Z')", True, "an open approval for the same release in another environment")
check(db8, "INSERT INTO Approvals(Id,ReleaseTrainId,EnvironmentId,RequestedByUserId,RequestedAt,DueAt) VALUES('ap3','t1','e9','gov2','2026-10-20T14:00:00Z','2026-10-21T14:00:00Z')", False, "an approval for another workspace's environment", expect="environment belongs to another workspace")
check(db8, "INSERT INTO Approvals(Id,ReleaseTrainId,EnvironmentId,RequestedByUserId,RequestedAt,DueAt) VALUES('ap3','t2','e2','gov2','2026-10-20T14:00:00Z','2026-10-21T14:00:00Z')", False, "an approval for another workspace's release", expect="train belongs to another workspace")
check(db8, "UPDATE Approvals SET EnvironmentId='e1' WHERE Id='ap1'", False, "an approval moved to another environment", expect="keeps its release and environment")
db8.executescript("""
INSERT INTO ApprovalDecisions(Id,ApprovalId,Label,EligibleKind,EligibleValue,DueAt) VALUES
 ('dc1','ap1','Security reviewer','Team','tm1','2026-10-21T14:00:00Z'),('dc2','ap1','Release owner','ReleaseOwner',NULL,'2026-10-21T14:00:00Z'),
 ('dc3','ap1','SRE approver','WorkspaceRole','RTE','2026-10-20T22:00:00Z'),('dc4','ap1','Second reviewer','Team','tm1','2026-10-21T14:00:00Z');
""")
check(db8, "UPDATE ApprovalDecisions SET Decision='ChangesRequested', DecidedByUserId='dev', DecidedAt='2026-10-20T14:00:00Z' WHERE Id='dc1'", False, "Request changes without a note", expect="CHECK")
check(db8, "UPDATE ApprovalDecisions SET Decision='Approved', DecidedAt='2026-10-20T14:00:00Z' WHERE Id='dc1'", False, "a decision that names no decider", expect="Not an eligible approver")
check(db8, "UPDATE ApprovalDecisions SET Decision='Approved', DecidedByUserId='rte', DecidedAt='2026-10-20T14:00:00Z' WHERE Id='dc1'", False, "an approval by a workspace member who does not hold the seat", expect="Not an eligible approver")
check(db8, "UPDATE ApprovalDecisions SET Decision='Approved', DecidedByUserId='gov2', DecidedAt='2026-10-20T14:00:00Z' WHERE Id='dc3'", False, "an approval by the person who requested the change", expect="you cannot approve a change you requested")
check(db8, "UPDATE ApprovalDecisions SET Decision='Approved', DecidedByUserId='rm', DecidedAt='2026-10-20T14:00:00Z' WHERE Id='dc1'", False, "the release owner approving a Production seat that is not theirs", expect="release owner approves only the release owner seat")
check(db8, "UPDATE ApprovalDecisions SET Decision='Approved', DecidedByUserId='rm', DecidedAt='2026-10-20T14:00:00Z' WHERE Id='dc2'", True, "the release owner approving the release owner seat")
check(db8, "UPDATE ApprovalDecisions SET Decision='Approved', DecidedByUserId='dev', DecidedAt='2026-10-20T14:00:00Z' WHERE Id='dc1'", True, "a team member approving the security seat")
check(db8, "UPDATE ApprovalDecisions SET Decision='Approved', DecidedByUserId='dev', DecidedAt='2026-10-20T14:00:00Z' WHERE Id='dc4'", False, "one person approving a second seat of a Production change", expect="two seats on a Production change")
check(db8, "UPDATE ApprovalDecisions SET Decision='ChangesRequested', DecidedByUserId='dev', DecidedAt='2026-10-20T14:00:00Z', Note='Changed my mind' WHERE Id='dc1'", False, "a decision changed once made", expect="A decision is final")
check(db8, "UPDATE Approvals SET Status='Approved', DecidedAt='2026-10-20T14:00:00Z' WHERE Id='ap1'", False, "an approval approved while seats are open", expect="Every required approver must approve first")
check(db8, "UPDATE ApprovalDecisions SET Decision='ChangesRequested', DecidedByUserId='rte', DecidedAt='2026-10-20T14:00:00Z', Note='Rollback plan is missing' WHERE Id='dc3'", True, "Request changes with a note")
check(db8, "UPDATE Approvals SET Status='ChangesRequested', Version=Version+1 WHERE Id='ap1'", True, "the approval marked as changes requested")
check(db8, "UPDATE ApprovalDecisions SET Decision=NULL, DecidedByUserId=NULL, DecidedAt=NULL, Note=NULL WHERE Id='dc3'", True, "a seat reset for a new round")
check(db8, "UPDATE Approvals SET Status='Pending', Round=2 WHERE Id='ap1'", True, "the approval back to pending for the new round")
check(db8, "UPDATE ApprovalDecisions SET Decision='Approved', DecidedByUserId='gov1', DecidedAt='2026-10-20T14:00:00Z' WHERE Id='dc4'", True, "a second team member approving the other team seat")
check(db8, "UPDATE ApprovalDecisions SET Decision='Approved', DecidedByUserId='rte', DecidedAt='2026-10-20T14:00:00Z' WHERE Id='dc3'", True, "the SRE seat approved by an RTE who did not request it")
check(db8, "UPDATE Approvals SET Status='Approved', DecidedAt='2026-10-20T14:00:00Z' WHERE Id='ap1'", True, "the approval approved once every seat has")
check(db8, "UPDATE Approvals SET Status='Pending', DecidedAt=NULL WHERE Id='ap1'", False, "an approved approval reopened", expect="final")
check(db8, "UPDATE ApprovalDecisions SET Note='late edit' WHERE Id='dc1'", False, "a decision changed after the approval closed", expect="The approval is closed")
db8.executescript("""
INSERT INTO AuditEvents(OccurredAt,EntityType,EntityId,Action,WorkspaceId) VALUES ('2026-10-20T14:00:00Z','Train','t1','Probe','default'),('2026-10-20T14:00:00Z','Train','t1','Probe2','default');
""")
check(db8, "UPDATE AuditEvents SET Hash=printf('%064d',1) WHERE Action='Probe'", False, "a hash without the previous hash", expect="append-only")
check(db8, "UPDATE AuditEvents SET PrevHash='xyz', Hash=printf('%064d',1) WHERE Action='Probe'", False, "a hash that is not 64 hex characters", expect="CHECK")
check(db8, "UPDATE AuditEvents SET Action='Edited' WHERE Action='Probe'", False, "an audit row edited", expect="append-only")
check(db8, "UPDATE AuditEvents SET Action='Edited', PrevHash=printf('%064d',0), Hash=printf('%064d',1) WHERE Action='Probe'", False, "an audit row edited while it is sealed", expect="append-only")
check(db8, "UPDATE AuditEvents SET PrevHash=printf('%064d',0), Hash=printf('%064d',1) WHERE Action='Probe'", True, "an audit row sealed once")
check(db8, "UPDATE AuditEvents SET PrevHash=printf('%064d',0), Hash=printf('%064d',2) WHERE Action='Probe'", False, "a sealed audit row sealed again", expect="append-only")
check(db8, "UPDATE AuditEvents SET PrevHash=NULL, Hash=NULL WHERE Action='Probe'", False, "a sealed audit row's hash cleared", expect="append-only")
check(db8, "DELETE FROM AuditEvents WHERE Action='Probe'", False, "a sealed audit row deleted", expect="append-only")
check(db8, "DELETE FROM AuditEvents WHERE Action='Probe2'", False, "an unsealed audit row deleted", expect="append-only")
check(db8, "INSERT INTO AuditChainHeads(ChainId,LastId,LastHash,SealedCount,SealedAt) VALUES('default',1,printf('%064d',1),1,'2026-10-20T14:00:00Z')", True, "the audit chain's head recorded")
check(db8, "INSERT INTO AuditChainHeads(ChainId,LastId,LastHash,SealedCount,SealedAt) VALUES('default',2,printf('%064d',2),2,'2026-10-20T14:00:00Z')", False, "a chain head twice", expect="UNIQUE")
check(db8, "UPDATE AuditChainHeads SET VerifiedStatus='Verified' WHERE ChainId='default'", False, "a verification status without its time", expect="CHECK")
check(db8, "UPDATE AuditChainHeads SET VerifiedStatus='Maybe', VerifiedAt='2026-10-20T14:00:00Z' WHERE ChainId='default'", False, "a verification status that is neither verified nor failed", expect="CHECK")
check(db8, "UPDATE AuditChainHeads SET VerifiedStatus='Failed', VerifiedAt='2026-10-20T14:00:00Z', FirstBadId=1, FirstBadReason='Row 1 does not verify' WHERE ChainId='default'", True, "a verification recorded")

# ---- Release types (REOS-116, D46, D48): a type on every release once the workspace has types; a Beta type carries a number 1 to 3 ----
db9 = db_fresh()
db9.executescript("""
INSERT INTO Workspaces(Id,Name,Slug,CreatedAt) VALUES('w2','EU','eu','2026-10-20T14:00:00Z');
""")
T = "INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,CreatedAt,UpdatedAt,ReleaseTypeId,BetaNumber,WorkspaceId) VALUES(?,?,'2026-11-30',?,?,?,?,?)"
check(db9, T, True, "a release in a workspace with no types needs none yet", ('n1', 'x', NOW, NOW, None, None, 'default'))
db9.execute("INSERT INTO ReleaseTypes(Id,Name,SortOrder,IsBeta) VALUES('tyB','Beta',10,1),('tyC','CERT',20,0),('tyP','Production',40,0)")
db9.execute("INSERT INTO ReleaseTypes(Id,Name,WorkspaceId) VALUES('tyX','Production','w2')")
check(db9, T, False, "a release created without a type once the workspace has types", ('n2', 'x', NOW, NOW, None, None, 'default'), expect="needs a release type")
check(db9, T, True, "a release created with the type of its own workspace", ('n3', 'x', NOW, NOW, 'tyX', None, 'w2'))
db9.execute("INSERT INTO Workspaces(Id,Name,Slug,CreatedAt) VALUES('w3','NoTypes','notypes','2026-10-20T14:00:00Z')")
check(db9, T, True, "a release in a workspace that still has no types", ('n4', 'x', NOW, NOW, None, None, 'w3'))
check(db9, T, True, "a Production release", ('n5', 'x', NOW, NOW, 'tyP', None, 'default'))
check(db9, T, False, "a Production release with a Beta number", ('n6', 'x', NOW, NOW, 'tyP', 2, 'default'), expect="Only a Beta release carries a Beta number")
check(db9, T, False, "a Beta release without its number", ('n7', 'x', NOW, NOW, 'tyB', None, 'default'), expect="needs its number")
check(db9, T, False, "a Beta release numbered 4", ('n8', 'x', NOW, NOW, 'tyB', 4, 'default'), expect="CHECK")
check(db9, T, False, "a Beta release numbered 0", ('n9', 'x', NOW, NOW, 'tyB', 0, 'default'), expect="CHECK")
check(db9, T, True, "Beta 1", ('b1', 'x', NOW, NOW, 'tyB', 1, 'default'))
check(db9, T, True, "Beta 3", ('b3', 'x', NOW, NOW, 'tyB', 3, 'default'))
check(db9, "UPDATE ReleaseTrains SET ReleaseTypeId=NULL WHERE Id='n5'", False, "a release that loses its type", expect="cannot lose its release type")
check(db9, "UPDATE ReleaseTrains SET ReleaseTypeId='tyC' WHERE Id='b1'", False, "a Beta release changed to CERT with its number kept", expect="Only a Beta release carries a Beta number")
check(db9, "UPDATE ReleaseTrains SET ReleaseTypeId='tyC', BetaNumber=NULL WHERE Id='b1'", True, "a Beta release changed to CERT with the number cleared")
check(db9, "UPDATE ReleaseTrains SET ReleaseTypeId='tyB' WHERE Id='b1'", False, "a release changed to Beta without a number", expect="needs its number")
check(db9, "UPDATE ReleaseTrains SET ReleaseTypeId='tyB', BetaNumber=2 WHERE Id='b1'", True, "a release changed to Beta 2")
check(db9, "UPDATE ReleaseTrains SET BetaNumber=NULL WHERE Id='b3'", False, "a Beta release's number cleared", expect="needs its number")
check(db9, "UPDATE ReleaseTrains SET BetaNumber=2 WHERE Id='n5'", False, "a number set on a Production release", expect="Only a Beta release carries a Beta number")
check(db9, "UPDATE ReleaseTrains SET Title='renamed' WHERE Id='n1'", True, "a release that predates types edited in another field")
check(db9, "UPDATE ReleaseTrains SET ReleaseTypeId='tyP' WHERE Id='n1'", True, "a release that predates types given its type")
check(db9, "UPDATE ReleaseTypes SET IsBeta=1 WHERE Id='tyP'", False, "a type in use made a Beta type", expect="cannot become or stop being a Beta type")
check(db9, "UPDATE ReleaseTypes SET IsBeta=0 WHERE Id='tyB'", False, "a Beta type in use stops being one", expect="cannot become or stop being a Beta type")
check(db9, "INSERT INTO ReleaseTypes(Id,Name,IsBeta) VALUES('tyD','Spare',1)", True, "a Beta type with no releases")
check(db9, "UPDATE ReleaseTypes SET IsBeta=0 WHERE Id='tyD'", True, "an unused type stops being a Beta type")
check(db9, "INSERT INTO ReleaseTypes(Id,Name,IsBeta) VALUES('tyE','Odd',2)", False, "a Beta flag that is not 0 or 1", expect="CHECK")

# ---- Versions (REOS-117, RT2, D48): a version groups the releases of one product version; one release per type (and Beta number) in it ----
db10 = db_fresh()
db10.executescript("""
INSERT INTO Workspaces(Id,Name,Slug,CreatedAt) VALUES('w2','EU','eu','2026-10-20T14:00:00Z');
INSERT INTO ReleaseTypes(Id,Name,SortOrder,IsBeta) VALUES('tyB','Beta',10,1),('tyC','CERT',20,0),('tyP','Production',40,0);
INSERT INTO ReleaseTypes(Id,Name,WorkspaceId) VALUES('tyX','Production','w2');
""")
V = "INSERT INTO ReleaseVersions(Id,Name,CreatedAt,WorkspaceId) VALUES(?,?,?,?)"
check(db10, V, True, "a version", ('v1', 'Orion 5.2', NOW, 'default'))
check(db10, V, False, "a version name twice in a workspace (any case)", ('v2', 'ORION 5.2', NOW, 'default'), expect="UNIQUE")
check(db10, V, True, "the same version name in another workspace", ('v3', 'Orion 5.2', NOW, 'w2'))
check(db10, V, False, "a blank version name", ('v4', '  ', NOW, 'default'), expect="CHECK")
check(db10, "UPDATE ReleaseVersions SET WorkspaceId='w2' WHERE Id='v1'", False, "a version moved to another workspace", expect="cannot move to another workspace")
T = "INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,CreatedAt,UpdatedAt,ReleaseTypeId,BetaNumber,ReleaseVersionId,WorkspaceId,CurrentStatus) VALUES(?,?,'2026-11-30',?,?,?,?,?,?,?)"
check(db10, T, True, "Beta 1 of a version", ('r1', 'a', NOW, NOW, 'tyB', 1, 'v1', 'default', 'Planning'))
check(db10, T, True, "Beta 2 of the same version", ('r2', 'b', NOW, NOW, 'tyB', 2, 'v1', 'default', 'Planning'))
check(db10, T, True, "CERT of the same version", ('r3', 'c', NOW, NOW, 'tyC', None, 'v1', 'default', 'Planning'))
check(db10, T, False, "a second Beta 1 in the same version", ('r4', 'd', NOW, NOW, 'tyB', 1, 'v1', 'default', 'Planning'), expect="UNIQUE")
check(db10, T, False, "a second CERT in the same version", ('r4', 'd', NOW, NOW, 'tyC', None, 'v1', 'default', 'Gated'), expect="UNIQUE")
check(db10, T, True, "a CERT outside any version", ('r5', 'e', NOW, NOW, 'tyC', None, None, 'default', 'Planning'))
check(db10, T, True, "another CERT outside any version", ('r6', 'f', NOW, NOW, 'tyC', None, None, 'default', 'Planning'))
check(db10, T, False, "a release in a version without a type", ('r7', 'g', NOW, NOW, None, None, 'v1', 'default', 'Planning'), expect="needs a release type")
check(db10, T, False, "a release in another workspace's version", ('r8', 'h', NOW, NOW, 'tyX', None, 'v1', 'w2', 'Planning'), expect="version belongs to another workspace")
check(db10, "UPDATE ReleaseTrains SET CurrentStatus='Aborted', ActualEndAt=NULL WHERE Id='r3'", True, "a release in a version aborted")
check(db10, T, True, "a new CERT once the first one was aborted", ('r9', 'i', NOW, NOW, 'tyC', None, 'v1', 'default', 'Planning'))
check(db10, "UPDATE ReleaseTrains SET ReleaseVersionId='v1' WHERE Id='r5'", False, "a second live CERT linked into the version", expect="UNIQUE")
check(db10, "UPDATE ReleaseTrains SET ReleaseVersionId='v3' WHERE Id='r5'", False, "a release linked to another workspace's version", expect="version belongs to another workspace")
check(db10, "UPDATE ReleaseTrains SET ReleaseVersionId=NULL WHERE Id='r2'", True, "a release unlinked from its version")
check(db10, "UPDATE ReleaseTrains SET BetaNumber=1 WHERE Id='r2'", True, "a Beta release renumbered while outside a version")
check(db10, "UPDATE ReleaseTrains SET ReleaseVersionId='v1' WHERE Id='r2'", False, "a release linked into a slot a live release holds", expect="UNIQUE")
check(db10, "UPDATE ReleaseTrains SET BetaNumber=3 WHERE Id='r2'", True, "a release given a free number")
check(db10, "UPDATE ReleaseTrains SET ReleaseVersionId='v1' WHERE Id='r2'", True, "a release linked into a free slot")
check(db10, "INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,CreatedAt,UpdatedAt,ReleaseVersionId) VALUES('r10','j','2026-11-30',?,?,'v1')", False, "a release linked to a version at insert without a type", (NOW, NOW), expect="needs a release type")

print(f"\n{passed} passed, {failed} failed")
sys.exit(1 if failed else 0)
