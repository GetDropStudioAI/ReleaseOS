"""Seed ~6 months of synthetic history THROUGH the real triggers, run every analytics query, and write:
  tests/reference/fixtures/seed.db               the seeded SQLite database (schema + triggers + data)
  tests/reference/fixtures/expected_metrics.json  every query's full result for AS_OF and the date range below
Deterministic: fixed RNG seeds for data and ids, fixed AS_OF for anything time-relative.
M7 acceptance: the .NET analytics endpoints, pointed at a copy of seed.db with from/to/now below, must return
exactly expected_metrics.json. .NET never needs to reproduce Python's random stream.
Run from the repo root: python3 tests/reference/seed_and_query.py [--write]"""
import sqlite3, random, re, json, os, sys, datetime as dt

HERE = os.path.dirname(os.path.abspath(__file__))
SCHEMA = os.path.join(HERE, "..", "..", "db", "schema.sql")
ANALYTICS = os.path.join(HERE, "..", "..", "db", "analytics.sql")
FIX = os.path.join(HERE, "fixtures")
AS_OF = "2026-09-28T12:00:00Z"
PARAMS = {"from": "2026-01-01", "to": "2026-12-31", "now": AS_OF, "ws": "default"}   # ws: every metric reads one workspace (REOS-105)
BIND = {**PARAMS, "type": None}   # type: a release type id or NULL for all (REOS-116); not in the fixture's params, so the .NET tests' reading of them is unchanged
BIND["ws"] = json.dumps([PARAMS["ws"]])   # :ws is a JSON array of workspace ids (D49, REOS-121); the fixture's params keep the single id
BIND.update({"by": ",", "fy": 1, "localday": AS_OF[:10], "pir_due": "{}"})   # M16..M24: grouping, first month of the fiscal year, today in the display zone, PIR due dates (see below)

random.seed(7)                     # data stream
idrng = random.Random(1234)        # id stream, separate so ids never shift the data
gid = lambda: "%032x" % idrng.getrandbits(128)
iso = lambda d: d.strftime('%Y-%m-%dT%H:%M:%SZ')
asof = dt.datetime.strptime(AS_OF, '%Y-%m-%dT%H:%M:%SZ')

db = sqlite3.connect(":memory:")
db.execute("PRAGMA foreign_keys=ON")
db.executescript(open(SCHEMA).read())
for u, r in [('rte','RTE'),('rm','ReleaseManager'),('gov1','GovernanceOfficer'),('gov2','GovernanceOfficer'),('qa','RTE')]:
    db.execute("INSERT INTO Users(Id,Email,DisplayName,Role) VALUES(?,?,?,?)", (u, u+'@x.com', u, r))
    db.execute("INSERT INTO WorkspaceMembers(WorkspaceId,UserId,Role,AddedAt) VALUES('default',?,?,'2026-04-01T00:00:00Z')", (u, r))   # role triggers read it (REOS-105)

products = ['Payments API','Card Portal','Ledger Svc','Fraud Engine','Notif Svc','Merchant UI']
gates = [('Code Freeze','Standard',8,'Gated','rte'),('QA Sign-off','Standard',5,'Gated','qa'),
         ('Compliance Sign-off','Compliance',3,'Executing','gov1'),('CAB Approval','Compliance',2,'Executing','gov2')]
start = dt.datetime(2026,4,6)
tid = None
for w in range(24):
    n = w + 1
    target = start + dt.timedelta(weeks=w, days=3)
    tid = gid()
    created = target - dt.timedelta(days=random.randint(18,35))
    risk = random.choice(['Low','Moderate','Moderate','High'])
    db.execute("INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CreatedAt,UpdatedAt,LastChangedByUserId) VALUES(?,?,?,?,?,?,'rte')",
               (tid, f"R26.{n:02d}", target.date().isoformat(), risk, iso(created), iso(created)))
    for p in random.sample(products, random.randint(2,4)):
        db.execute("INSERT INTO BundledProducts(Id,ReleaseTrainId,ProductName,VersionTag,ProjectCode) VALUES(?,?,?,?,?)",
                   (gid(), tid, p, f"{random.randint(1,5)}.{random.randint(0,9)}.0", p[:3].upper()))
    gids = []
    for i, (gn, gc, off, req, own) in enumerate(gates, 1):
        g = gid(); gids.append((g, gn, gc, off, own))
        due = (target - dt.timedelta(days=off + off//5*2)).date().isoformat()   # fixture only; the app uses Holidays
        db.execute("INSERT INTO StageGates(Id,ReleaseTrainId,GateName,GateClass,SequenceOrder,OffsetDays,DueOn,RequiredBeforeStatus,OwnerUserId,LastChangedByUserId) VALUES(?,?,?,?,?,?,?,?,?,?)",
                   (g, tid, gn, gc, i, off, due, req, own, own))
        db.execute("INSERT INTO ChecklistTasks(Id,StageGateId,TaskDescription,OwnerUserId,IsCompleted,SequenceOrder,CompletedAt,CompletedByUserId,LastChangedByUserId) VALUES(?,?,?,?,1,1,?,?,?)",
                   (gid(), g, gn+' evidence', 'qa', iso(created), 'qa', 'qa'))
    wstart = target.replace(hour=2)
    steps = []
    for j in range(8):
        s = gid(); steps.append(s)
        sec = 'PreCheck' if j < 2 else ('Verify' if j >= 6 else 'Deploy')
        db.execute("INSERT INTO RunbookSteps(Id,ReleaseTrainId,StepCode,Section,Title,OwnerUserId,PlannedStartAt,PlannedDurationMin) VALUES(?,?,?,?,?,?,?,?)",
                   (s, tid, f"R-{j+1:03d}", sec, f"Step {j+1}", 'rte', iso(wstart + dt.timedelta(minutes=15*j)), 15))
        if j: db.execute("INSERT INTO StepDependencies(StepId,DependsOnStepId) VALUES(?,?)", (s, steps[j-1]))
    slip = random.choice([0,0,0,0,1,2,5]) if w < 22 else 0
    for g, gn, gc, off, own in gids:
        due = target - dt.timedelta(days=off + off//5*2)
        entered = due - dt.timedelta(days=random.randint(2,5))
        db.execute("UPDATE StageGates SET Status='InProgress',LastChangedByUserId=?,LastChangedAt=? WHERE Id=?", (own, iso(entered), g))
        certifier = 'gov2' if (gc == 'Compliance' and own == 'gov1') else ('gov1' if gc == 'Compliance' else own)
        certat = due + dt.timedelta(days=random.choice([-1,-1,0,0,1,2]) + (slip if gn == 'QA Sign-off' else 0), hours=10)
        if w >= 22 and gn in ('Compliance Sign-off','CAB Approval'): break          # in-flight trains
        if gc == 'Compliance' and random.random() < 0.08:
            db.execute("INSERT INTO GateWaivers(Id,StageGateId,Reason,RequestedByUserId,ApprovedByUserId,RequestedAt,ApprovedAt) VALUES(?,?,?,?,?,?,?)",
                       (gid(), g, 'Vendor pen-test report late; risk accepted by CISO', 'gov1', 'gov2', iso(certat), iso(certat)))
            db.execute("UPDATE StageGates SET Status='Waived',CertifiedByUserId='gov1',CertifiedAt=?,LastChangedByUserId='gov1',LastChangedAt=? WHERE Id=?", (iso(certat), iso(certat), g))
        else:
            db.execute("UPDATE StageGates SET Status='Certified',CertifiedByUserId=?,CertifiedAt=?,LastChangedByUserId=?,LastChangedAt=? WHERE Id=?",
                       (certifier, iso(certat), certifier, iso(certat), g))
        if gn == 'QA Sign-off':
            db.execute("UPDATE ReleaseTrains SET CurrentStatus='Gated' WHERE Id=?", (tid,))
        if gn == 'Code Freeze' and random.random() < 0.3:                           # late scope add
            db.execute("INSERT INTO BundledProducts(Id,ReleaseTrainId,ProductName,VersionTag,ProjectCode) VALUES(?,?,?,?,?)", (gid(), tid, 'Hotfix '+str(n), '0.0.1', 'HFX'))
    if w >= 22: continue
    actual_target = target + dt.timedelta(days=slip)
    db.execute("INSERT INTO GoNoGoDecisions(Id,ReleaseTrainId,Decision,DecidedByUserId,DecidedAt,GateSnapshotJson) VALUES(?,?,'Go','rm',?,'{}')",
               (gid(), tid, iso(actual_target - dt.timedelta(days=1))))
    if risk == 'High':
        db.execute("UPDATE ReleaseTrains SET RollbackRehearsedAt=?,RollbackRehearsedByUserId='rte' WHERE Id=?", (iso(actual_target - dt.timedelta(days=2)), tid))
    db.execute("UPDATE ReleaseTrains SET CurrentStatus='Executing',ActualStartAt=? WHERE Id=?", (iso(actual_target.replace(hour=2)), tid))
    run = gid()
    db.execute("INSERT INTO RunbookRuns(Id,ReleaseTrainId,Mode,StartedAt,StartedByUserId) VALUES(?,?,'Live',?,'rte')", (run, tid, iso(actual_target.replace(hour=2))))
    t = actual_target.replace(hour=2) + dt.timedelta(minutes=random.randint(0,6))
    rolled = random.random() < 0.08
    for j, s in enumerate(steps):
        e = gid()
        db.execute("INSERT INTO StepExecutions(Id,RunId,StepId) VALUES(?,?,?)", (e, run, s))
        dur = 15 + random.choice([-3,0,0,2,4,8,15 if j == 4 else 0])
        db.execute("UPDATE StepExecutions SET Status='Running',ActualStartAt=? WHERE Id=?", (iso(t), e))
        db.execute("UPDATE StepExecutions SET Status='Done',ActualEndAt=? WHERE Id=?", (iso(t + dt.timedelta(minutes=dur)), e))
        t += dt.timedelta(minutes=dur + random.randint(0,2))
    code = 'Unsuccessful' if rolled else random.choice(['Successful']*8 + ['SuccessfulWithIssues'])
    db.execute("UPDATE RunbookRuns SET EndedAt=?,Outcome=? WHERE Id=?", (iso(t), 'RolledBack' if rolled else 'Completed', run))
    db.execute("UPDATE ReleaseTrains SET CurrentStatus='Complete',CloseCode=?,ActualEndAt=? WHERE Id=?", (code, iso(t), tid))
    for off in (-7,-1,0):
        ct = gid()
        db.execute("INSERT INTO CommTemplates(Id,ReleaseTrainId,TemplateType,Audience,SubjectLine,MarkdownBody) VALUES(?,?,?,?,?,?)", (ct, tid, f"T{off}", 'All', 's', 'b'))
        due = target + dt.timedelta(days=off, hours=9)
        sent = due + dt.timedelta(hours=random.choice([-2,-1,0,0,3]))
        db.execute("INSERT INTO CommSchedule(Id,ReleaseTrainId,CommTemplateId,DueAt,SentAt) VALUES(?,?,?,?,?)", (gid(), tid, ct, iso(due), iso(sent)))

for sev, age in [('Critical',0.4),('High',2.5),('High',8),('Medium',4),('Low',12)]:     # open blockers on the last train
    db.execute("INSERT INTO Blockers(Id,ReleaseTrainId,Title,Severity,RaisedAt) VALUES(?,?,?,?,?)", (gid(), tid, 'b', sev, iso(asof - dt.timedelta(days=age))))
db.execute("INSERT INTO FreezeWindows(Id,Name,Kind,StartsAt,EndsAt,CreatedByUserId) VALUES('f1','Q3 close','Freeze','2026-09-28T00:00:00Z','2026-10-02T00:00:00Z','gov1')")
for i, (sysn, st, mins) in enumerate([('Jira','InSync',3),('Jira','InSync',4),('Jira','Mismatch',4),('Jira','NotFound',4),('ServiceNow','InSync',52),('ServiceNow','Mismatch',52)]):
    db.execute("INSERT INTO ExternalLinks(Id,ReleaseTrainId,EntityType,EntityId,SourceSystem,ExternalKey,SyncState,LastSyncedAt) VALUES(?,?,'Train',?,?,?,?,?)",
               (gid(), tid, tid, sysn, f"KEY-{i}", st, iso(asof - dt.timedelta(minutes=mins))))
db.execute("INSERT INTO SyncAlerts(Id,SourceSystem,Kind,Fingerprint,ErrorMessage,FirstOccurredAt,LastOccurredAt,OccurrenceCount) VALUES(?,?,?,?,?,?,?,?)",
           (gid(), 'Jira', 'NotFound', 'jira-notfound-KEY-3', '404', iso(asof - dt.timedelta(hours=5)), iso(asof), 60))
db.commit()

# ---- REOS-121 (RT6): the portfolio scenario. Everything below ADDS rows in two more workspaces and gives the default workspace's existing trains a type, a team and a client
# date; nothing above is changed and M1..M15 (which read workspace 'default' and never filter by type, team or client) return exactly what they did before. A separate random
# stream and a separate id stream keep the original data identical.
xr = random.Random(121)
xidr = random.Random(2110)
xg = lambda: "%032x" % xidr.getrandbits(128)
xd = lambda day, hour=10: dt.datetime(day.year, day.month, day.day, hour)
D = lambda y, m, d: dt.date(y, m, d)
EXEC_WS = {"ws-cards": ("Cards", "cards"), "ws-pay": ("Payments", "payments")}
PEOPLE = [('rte', 'RTE'), ('rm', 'ReleaseManager'), ('gov1', 'GovernanceOfficer'), ('gov2', 'GovernanceOfficer'), ('qa', 'RTE')]
for wid, (wname, slug) in EXEC_WS.items():
    db.execute("INSERT INTO Workspaces(Id,Name,Slug,CreatedAt) VALUES(?,?,?,'2026-03-01T00:00:00Z')", (wid, wname, slug))
    for u, r in PEOPLE:
        db.execute("INSERT INTO WorkspaceMembers(WorkspaceId,UserId,Role,AddedAt) VALUES(?,?,?,'2026-03-01T00:00:00Z')", (wid, u, r))

WSX = {}   # per workspace: type ids, team ids, version ids, clients, a service and an environment
for wid in ["default", *EXEC_WS]:
    x = {"types": {}, "teams": {}, "versions": {}, "clients": {}}
    for i, (name, beta) in enumerate([("Beta", 1), ("CERT", 0), ("PenTest Cert", 0), ("Production", 0)]):
        x["types"][name] = xg()
        db.execute("INSERT INTO ReleaseTypes(Id,Name,SortOrder,WorkspaceId,IsBeta) VALUES(?,?,?,?,?)", (x["types"][name], name, i, wid, beta))
    for h, n in [("core", "Core Platform"), ("channels", "Channels")]:
        x["teams"][h] = xg()
        db.execute("INSERT INTO Teams(Id,Handle,Name,WorkspaceId) VALUES(?,?,?,?)", (x["teams"][h], h, n, wid))
    x["env"] = xg()
    db.execute("INSERT INTO Environments(Id,Name,Kind,WorkspaceId,CreatedAt) VALUES(?,?,'Staging',?,'2026-03-01T00:00:00Z')", (x["env"], "CERT-" + wid, wid))
    x["prod_env"] = xg()
    db.execute("INSERT INTO Environments(Id,Name,Kind,WorkspaceId,CreatedAt) VALUES(?,?,'Production',?,'2026-03-01T00:00:00Z')", (x["prod_env"], "Prod-" + wid, wid))
    x["service"] = xg()
    db.execute("INSERT INTO CatalogServices(Id,ServiceKey,Name,WorkspaceId,CreatedAt) VALUES(?,?,?,?,'2026-03-01T00:00:00Z')", (x["service"], "svc-" + wid, "Service " + wid, wid))
    for name, seg in [("Acme Bank", "Standard"), ("Birch Credit", "Standard"), ("Cedar Pay", "OnDemand"), ("Dune Cards", "OnDemand")]:
        x["clients"][name] = xg()
        db.execute("INSERT INTO Clients(Id,Name,Segment,CertEnvironmentId,CreatedAt,WorkspaceId) VALUES(?,?,?,?,'2026-03-01T00:00:00Z',?)",
                   (x["clients"][name], name, seg, x["env"] if seg == "OnDemand" else None, wid))
    WSX[wid] = x

def client_dates(wid, tid, target, chosen_shift=None):
    for name in ("Cedar Pay", "Dune Cards"):
        offered = target + dt.timedelta(days=7)
        chosen = offered + dt.timedelta(days=chosen_shift) if (chosen_shift is not None and name == "Dune Cards") else None
        db.execute("INSERT INTO ReleaseClientDates(Id,ReleaseTrainId,ClientId,OfferedDate,ChosenDate,ChosenByUserId,ChosenAt,CreatedAt,WorkspaceId) VALUES(?,?,?,?,?,?,?,?,?)",
                   (xg(), tid, WSX[wid]["clients"][name], offered.isoformat(), chosen.isoformat() if chosen else None, 'rte' if chosen else None, iso(xd(target, 9)) if chosen else None, iso(xd(target, 9)), wid))

# the default workspace's 24 existing trains: CERT and Production alternate, two teams (one train has none), pairs form a version, every third Production release reaches On-Demand clients
dx = WSX["default"]
rows = db.execute("SELECT Id, TargetReleaseDate FROM ReleaseTrains WHERE WorkspaceId='default' ORDER BY Title").fetchall()
for i, (rid, tdate) in enumerate(rows):
    kind = "CERT" if i % 2 == 0 else "Production"
    team = None if i == 5 else ("core" if i % 3 else "channels")
    ver = None
    if i < 12:
        vn = f"v26.{i // 2 + 1}"
        if vn not in dx["versions"]:
            dx["versions"][vn] = xg()
            db.execute("INSERT INTO ReleaseVersions(Id,Name,CreatedAt,WorkspaceId) VALUES(?,?,'2026-03-01T00:00:00Z','default')", (dx["versions"][vn], vn))
        ver = dx["versions"][vn]
    db.execute("UPDATE ReleaseTrains SET ReleaseTypeId=?, OwnerTeamId=?, ReleaseVersionId=? WHERE Id=?", (dx["types"][kind], dx["teams"][team] if team else None, ver, rid))
    if kind == "Production" and i % 3 == 1: client_dates("default", rid, dt.date.fromisoformat(tdate), chosen_shift=2 if i % 2 else None)

GATES = [('Code Freeze', 'Standard', 8, 'rte'), ('Compliance Sign-off', 'Compliance', 3, 'gov1')]

def build(wid, title, kind, team, target, risk="Moderate", slip=0, close="Successful", rolled=False, version=None, beta=None, stop=None, late_compliance=0, waiver_lag_h=None,
          decertify=False, move=None, nogo=False, override_lag_h=None, condition=None, pir=None, blockers=(), clients=False, deployments=(), failed_after_min=None, rehearsed=True):
    """One release from creation to `stop` (None = Complete), through the real triggers. Times are mid-day UTC so the display zone never moves a date."""
    x = WSX[wid]
    tid = xg()
    created = xd(target - dt.timedelta(days=24), 9)
    db.execute("INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CreatedAt,UpdatedAt,LastChangedByUserId,WorkspaceId,ReleaseTypeId,BetaNumber,OwnerTeamId,ReleaseVersionId,OwnerUserId) VALUES(?,?,?,?,?,?,'rte',?,?,?,?,?,'rte')",
               (tid, title, target.isoformat(), risk, iso(created), iso(created), wid, x["types"][kind], beta, x["teams"][team] if team else None, x["versions"][version] if version else None))
    db.execute("INSERT INTO BundledProducts(Id,ReleaseTrainId,ProductName,VersionTag,ProjectCode) VALUES(?,?,?,?,?)", (xg(), tid, 'Svc ' + title, '1.0.0', 'SVC'))
    gates = []
    for i, (gn, gc, off, own) in enumerate(GATES, 1):
        g = xg(); due = target - dt.timedelta(days=off + off // 5 * 2); gates.append((g, gn, gc, off, own, due))
        db.execute("INSERT INTO StageGates(Id,ReleaseTrainId,GateName,GateClass,SequenceOrder,OffsetDays,DueOn,RequiredBeforeStatus,OwnerUserId,LastChangedByUserId) VALUES(?,?,?,?,?,?,?,?,?,?)",
                   (g, tid, gn, gc, i, off, due.isoformat(), 'Gated' if gc == 'Standard' else 'Executing', own, own))
        db.execute("INSERT INTO ChecklistTasks(Id,StageGateId,TaskDescription,OwnerUserId,IsCompleted,SequenceOrder,CompletedAt,CompletedByUserId,LastChangedByUserId) VALUES(?,?,?,?,1,1,?,?,?)",
                   (xg(), g, gn + ' evidence', 'qa', iso(xd(due, 8)), 'qa', 'qa'))
    if stop == "Planning": pass
    else:
        for g, gn, gc, off, own, due in gates:
            if stop == "Gated" and gc == 'Compliance':
                db.execute("UPDATE StageGates SET Status='InProgress',LastChangedByUserId=?,LastChangedAt=? WHERE Id=?", (own, iso(xd(due, 9)), g)); continue
            db.execute("UPDATE StageGates SET Status='InProgress',LastChangedByUserId=?,LastChangedAt=? WHERE Id=?", (own, iso(xd(due - dt.timedelta(days=3), 9)), g))
            certifier = 'gov2' if gc == 'Compliance' else own
            certat = xd(due + dt.timedelta(days=(late_compliance if gc == 'Compliance' else 0)), 13)
            if gc == 'Compliance' and waiver_lag_h is not None:
                req = certat - dt.timedelta(hours=waiver_lag_h)
                db.execute("INSERT INTO GateWaivers(Id,StageGateId,Reason,RequestedByUserId,ApprovedByUserId,RequestedAt,ApprovedAt) VALUES(?,?,?,?,?,?,?)",
                           (xg(), g, 'Vendor pen-test report late; risk accepted by the CISO', 'gov1', 'gov2', iso(req), iso(certat)))
                db.execute("UPDATE StageGates SET Status='Waived',CertifiedByUserId='gov1',CertifiedAt=?,LastChangedByUserId='gov1',LastChangedAt=? WHERE Id=?", (iso(certat), iso(certat), g))
            else:
                db.execute("UPDATE StageGates SET Status='Certified',CertifiedByUserId=?,CertifiedAt=?,LastChangedByUserId=?,LastChangedAt=? WHERE Id=?", (certifier, iso(certat), certifier, iso(certat), g))
                if decertify and gc == 'Compliance':
                    t2 = certat + dt.timedelta(hours=1)
                    db.execute("INSERT INTO ChecklistTasks(Id,StageGateId,TaskDescription,OwnerUserId,IsCompleted,SequenceOrder,LastChangedByUserId,LastChangedAt) VALUES(?,?,?,?,0,2,'rte',?)", (xg(), g, 'Late evidence', 'qa', iso(t2)))
                    db.execute("UPDATE ChecklistTasks SET IsCompleted=1,CompletedAt=?,CompletedByUserId='qa',LastChangedByUserId='qa',LastChangedAt=? WHERE StageGateId=? AND SequenceOrder=2", (iso(t2 + dt.timedelta(hours=3)), iso(t2 + dt.timedelta(hours=3)), g))
                    recert = t2 + dt.timedelta(hours=5)
                    db.execute("UPDATE StageGates SET Status='Certified',CertifiedByUserId='gov2',CertifiedAt=?,LastChangedByUserId='gov2',LastChangedAt=? WHERE Id=?", (iso(recert), iso(recert), g))
        db.execute("UPDATE ReleaseTrains SET CurrentStatus='Gated',LastChangedByUserId='rte',LastChangedAt=? WHERE Id=?", (iso(xd(target - dt.timedelta(days=2), 9)), tid)) if stop != "Gated" else None
        if stop == "Gated":
            # Gated needs the Standard gate only (RequiredBeforeStatus Gated); the Compliance gate is still in progress
            db.execute("UPDATE ReleaseTrains SET CurrentStatus='Gated',LastChangedByUserId='rte',LastChangedAt=? WHERE Id=?", (iso(xd(target - dt.timedelta(days=2), 9)), tid))
    for sev, age_days in blockers:
        db.execute("INSERT INTO Blockers(Id,ReleaseTrainId,Title,Severity,RaisedAt) VALUES(?,?,?,?,?)", (xg(), tid, 'Blocker', sev, iso(asof - dt.timedelta(days=age_days))))
    if risk in ('High', 'VeryHigh') and rehearsed and stop != "Planning":
        db.execute("UPDATE ReleaseTrains SET RollbackRehearsedAt=?,RollbackRehearsedByUserId='rte' WHERE Id=?", (iso(xd(target - dt.timedelta(days=3), 11)), tid))
    if clients: client_dates(wid, tid, target, chosen_shift=3)
    if condition == "open":     # a Go condition that expired and is still open, on a release that has not started
        dec = xg()
        db.execute("INSERT INTO GoNoGoDecisions(Id,ReleaseTrainId,Decision,DecidedByUserId,DecidedAt,GateSnapshotJson) VALUES(?,?,'GoWithConditions','rm',?,'{}')", (dec, tid, iso(asof - dt.timedelta(days=12))))
        db.execute("INSERT INTO GoNoGoConditions(Id,DecisionId,Text,OwnerUserId,ExpiresAt) VALUES(?,?,?,?,?)", (xg(), dec, 'Capacity test signed off', 'rte', iso(asof - dt.timedelta(days=5))))
    if stop is not None:
        return tid
    # ---- a release that ran
    actual_target = target + dt.timedelta(days=slip)
    if move or nogo:
        new_target = target + dt.timedelta(days=(move or 4))
        when = xd(target - dt.timedelta(days=1), 15)
        if nogo:
            db.execute("INSERT INTO GoNoGoDecisions(Id,ReleaseTrainId,Decision,DecidedByUserId,DecidedAt,GateSnapshotJson,Notes,NewTargetReleaseDate) VALUES(?,?,'NoGo','rm',?,'{}','Environment not ready',?)", (xg(), tid, iso(when - dt.timedelta(hours=1)), new_target.isoformat()))
        db.execute("INSERT INTO AuditEvents(OccurredAt,ActorUserId,ReleaseTrainId,EntityType,EntityId,Action,BeforeJson,AfterJson,WorkspaceId) VALUES(?,?,?,?,?,?,?,?,?)",
                   (iso(when), 'rte', tid, 'ReleaseTrain', tid, 'ChangeTargetDate', json.dumps({"target": target.isoformat()}), json.dumps({"target": new_target.isoformat()}), wid))
        db.execute("UPDATE ReleaseTrains SET TargetReleaseDate=? WHERE Id=?", (new_target.isoformat(), tid))
    dec = xg()
    cond_sql = []
    db.execute("INSERT INTO GoNoGoDecisions(Id,ReleaseTrainId,Decision,DecidedByUserId,DecidedAt,GateSnapshotJson) VALUES(?,?,?,'rm',?,'{}')",
               (dec, tid, 'GoWithConditions' if condition == "closed-late" else 'Go', iso(xd(actual_target - dt.timedelta(days=1), 12))))
    if condition == "closed-late":
        exp = xd(actual_target - dt.timedelta(days=1), 18)
        db.execute("INSERT INTO GoNoGoConditions(Id,DecisionId,Text,OwnerUserId,ExpiresAt,ClosedAt,ClosedByUserId) VALUES(?,?,?,?,?,?,'rte')", (xg(), dec, 'Load test report', 'rte', iso(exp), iso(exp + dt.timedelta(hours=20))))
    if override_lag_h is not None:
        fw = xg(); ws_, we = xd(actual_target + dt.timedelta(days=20), 0), xd(actual_target + dt.timedelta(days=24), 0)
        db.execute("INSERT INTO FreezeWindows(Id,Name,Kind,StartsAt,EndsAt,CreatedByUserId,WorkspaceId) VALUES(?,?,'Freeze',?,?,'gov1',?)", (fw, 'Quarter close ' + title, iso(ws_), iso(we), wid))
        granted = xd(actual_target - dt.timedelta(days=1), 16)
        db.execute("INSERT INTO AuditEvents(OccurredAt,ActorUserId,ReleaseTrainId,EntityType,EntityId,Action,AfterJson,WorkspaceId) VALUES(?,'rte',?,'FreezeWindow',?,'RequestOverride',?,?)",
                   (iso(granted - dt.timedelta(hours=override_lag_h)), tid, fw, json.dumps({"trainId": tid, "reason": "hotfix"}), wid))
        db.execute("INSERT INTO FreezeOverrides(Id,FreezeWindowId,ReleaseTrainId,Reason,RequestedByUserId,ApprovedByUserId,ApprovedAt,ExpiresAt) VALUES(?,?,?,?,'rte','rm',?,?)",
                   (xg(), fw, tid, 'Payments hotfix agreed with the business owner', iso(granted), iso(granted + dt.timedelta(days=3))))
    start = xd(actual_target, 14)
    db.execute("UPDATE ReleaseTrains SET CurrentStatus='Executing',ActualStartAt=?,LastChangedByUserId='rte',LastChangedAt=? WHERE Id=?", (iso(start), iso(start), tid))
    run = xg()
    db.execute("INSERT INTO RunbookRuns(Id,ReleaseTrainId,Mode,StartedAt,StartedByUserId) VALUES(?,?,'Live',?,'rte')", (run, tid, iso(start)))
    steps = []
    t = start
    for j in range(3):
        sid = xg(); steps.append(sid)
        db.execute("INSERT INTO RunbookSteps(Id,ReleaseTrainId,StepCode,Section,Title,OwnerUserId,PlannedStartAt,PlannedDurationMin) VALUES(?,?,?,?,?,'rte',?,20)", (sid, tid, f"R-{j+1:03d}", 'Deploy', f"Step {j+1}", iso(start + dt.timedelta(minutes=20 * j))))
        e = xg()
        db.execute("INSERT INTO StepExecutions(Id,RunId,StepId) VALUES(?,?,?)", (e, run, sid))
        db.execute("UPDATE StepExecutions SET Status='Running',ActualStartAt=? WHERE Id=?", (iso(t), e))
        if rolled and j == 1:
            fail_at = t + dt.timedelta(minutes=failed_after_min or 12)
            db.execute("UPDATE StepExecutions SET Status='Failed',ActualEndAt=? WHERE Id=?", (iso(fail_at), e))
            t = fail_at + dt.timedelta(minutes=(failed_after_min or 12) * 2 + 3)   # the restore takes a while
            break
        db.execute("UPDATE StepExecutions SET Status='Done',ActualEndAt=? WHERE Id=?", (iso(t + dt.timedelta(minutes=20)), e))
        t += dt.timedelta(minutes=22)
    db.execute("UPDATE RunbookRuns SET EndedAt=?,Outcome=? WHERE Id=?", (iso(t), 'RolledBack' if rolled else 'Completed', run))
    db.execute("UPDATE ReleaseTrains SET CurrentStatus='Complete',CloseCode=?,ActualEndAt=?,LastChangedByUserId='rte',LastChangedAt=? WHERE Id=?", (close, iso(t), iso(t), tid))
    if pir is not None:
        state = pir.get("status", "Required")
        if rolled: db.execute("UPDATE PostImplementationReviews SET RequiredReason='RollbackPerformed' WHERE ReleaseTrainId=?", (tid,))
        pid = db.execute("SELECT Id FROM PostImplementationReviews WHERE ReleaseTrainId=?", (tid,)).fetchone()[0]
        if state in ("Held", "Closed"): db.execute("UPDATE PostImplementationReviews SET Status=?,HeldAt=? WHERE Id=?", (state, iso(xd((t + dt.timedelta(days=pir.get('held_after', 1))).date(), 11)), pid))
        for k, (due_off, done) in enumerate(pir.get("actions", [])):
            db.execute("INSERT INTO PirActions(Id,PirId,Text,OwnerUserId,DueOn,DoneAt) VALUES(?,?,?,?,?,?)", (xg(), pid, f"Action {k+1}", 'rte', (t + dt.timedelta(days=due_off)).date().isoformat(), iso(xd((t + dt.timedelta(days=due_off - 1)).date(), 10)) if done else None))
    for k, (state, dur_min, breaches) in enumerate(deployments):
        did = xg(); fin = t - dt.timedelta(minutes=5 * k)
        db.execute("INSERT INTO Deployments(Id,ExternalId,CatalogServiceId,EnvironmentId,ReleaseTrainId,RunId,ReleaseVersion,State,TrafficPercent,StartedAt,FinishedAt,LastEventAt,CreatedAt,UpdatedAt,WorkspaceId) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                   (did, f"ext-{did[:8]}", x["service"], x["prod_env"], tid, run, '1.0.0', state, 100 if state == 'Successful' else 0, iso(fin - dt.timedelta(minutes=dur_min)), iso(fin), iso(fin), iso(start), iso(fin), wid))
        for metric, confirmed in breaches:
            conf = iso(fin - dt.timedelta(minutes=2)) if confirmed else None
            db.execute("INSERT INTO ThresholdBreaches(Id,DeploymentId,Series,Metric,ThresholdValue,ForSeconds,StartedAt,ConfirmedAt,DetectedAt,PeakValue,LastValue,WorkspaceId) VALUES(?,?,'Canary',?,1.0,120,?,?,?,2.0,2.0,?)",
                       (xg(), did, metric, iso(fin - dt.timedelta(minutes=4)), conf, conf, wid))
    return tid

# Cards: Q2 and Q3, CERT and Production, a rolled-back release, a waiver, a decertification, a late Compliance gate, a No-Go move, a freeze override, Beta and PenTest releases, open releases
cards = [
    dict(title="C-1", kind="CERT", team="core", target=D(2026,4,16), version="v1"),
    dict(title="C-2", kind="Production", team="core", target=D(2026,4,23), slip=3, version="v1", clients=True, override_lag_h=6, nogo=True, move=3,
         deployments=[("Successful", 25, []), ("Failed", 10, [("ErrorRate", True)])]),
    dict(title="C-3", kind="CERT", team="channels", target=D(2026,5,14), close="SuccessfulWithIssues", waiver_lag_h=30, late_compliance=1, version="v2",
         pir=dict(status="Held", actions=[(5, True), (9, False)])),
    dict(title="C-4", kind="Production", team="channels", target=D(2026,6,18), risk="High", close="Unsuccessful", rolled=True, version="v2", clients=True, failed_after_min=14,
         pir=dict(status="Closed", held_after=1, actions=[(3, True)]), deployments=[("RolledBack", 30, [("Availability", True), ("ErrorRate", False)])]),
    dict(title="C-5", kind="Production", team="core", target=D(2026,7,16), decertify=True, condition="closed-late", slip=1, clients=True, deployments=[("Successful", 20, [])]),
    dict(title="C-6", kind="Beta", team="core", target=D(2026,7,2), beta=1),
    dict(title="C-7", kind="PenTest Cert", team=None, target=D(2026,8,13), late_compliance=2),
    dict(title="C-8", kind="Production", team="channels", target=D(2026,9,10), risk="VeryHigh", close="Unsuccessful", rolled=True, move=5, failed_after_min=25,
         pir=dict(status="Required", actions=[(2, False), (4, False)]), deployments=[("RolledBack", 40, [("Availability", True)])]),
    dict(title="C-9", kind="Production", team="core", target=D(2026,10,22), risk="High", stop="Gated", blockers=[("Critical", 3), ("High", 9)], condition="open", rehearsed=False),
    dict(title="C-10", kind="CERT", team="channels", target=D(2026,11,12), risk="Moderate", stop="Planning", blockers=[("Medium", 1)]),
]
# Payments: a smaller portfolio with quicker, cleaner releases and one very late CERT
pay = [
    dict(title="P-1", kind="CERT", team="core", target=D(2026,4,9), version="v1"),
    dict(title="P-2", kind="Production", team="core", target=D(2026,4,16), version="v1", clients=True),
    dict(title="P-3", kind="CERT", team="channels", target=D(2026,6,11), slip=6, waiver_lag_h=52),
    dict(title="P-4", kind="Production", team="channels", target=D(2026,7,23), slip=2, clients=True, override_lag_h=20, late_compliance=3, deployments=[("Successful", 15, []), ("Successful", 18, [])]),
    dict(title="P-5", kind="Beta", team="core", target=D(2026,8,6), beta=2, close="SuccessfulWithIssues", pir=dict(status="Required", actions=[(2, True)])),
    dict(title="P-6", kind="Production", team="core", target=D(2026,9,17), risk="Low", slip=0, deployments=[("Failed", 12, [("LatencyP95", True)])], close="Unsuccessful", pir=dict(status="Required")),
    dict(title="P-7", kind="CERT", team="core", target=D(2026,10,8), risk="High", stop="Gated", blockers=[("High", 6)]),
    dict(title="P-8", kind="Production", team="channels", target=D(2026,10,15), risk="Low", stop="Planning"),
]
for wid, specs in (("ws-cards", cards), ("ws-pay", pay)):
    x = WSX[wid]
    for sp in specs:
        sp = dict(sp)
        v = sp.get("version")
        if v and v not in x["versions"]:
            x["versions"][v] = xg()
            db.execute("INSERT INTO ReleaseVersions(Id,Name,CreatedAt,WorkspaceId) VALUES(?,?,'2026-03-01T00:00:00Z',?)", (x["versions"][v], v, wid))
        build(wid, sp.pop("title"), sp.pop("kind"), sp.pop("team"), sp.pop("target"), **sp)
db.commit()

print("trains:", db.execute("select count(*), sum(CurrentStatus='Complete') from ReleaseTrains").fetchone(),
      " PIRs auto-created:", db.execute("select count(*) from PostImplementationReviews").fetchone()[0],
      " baselines:", db.execute("select count(*) from Baselines").fetchone()[0])
sql = open(ANALYTICS).read()
blocks = re.split(r"\n-- (M\d+ .*)\n-- name: (\w+)\n", sql)

# analytics.sql shares text between the M16..M24 statements; the API expands it the same way (AnalyticsSql.Expand)
FRAGMENTS = {m.group(1): m.group(2) for m in re.finditer(r"-- @fragment (\w+)\n(.*?)\n-- @end", blocks[0], re.S)}
KEY_PARTS = [("workspace", "workspace", "rel.ws_name"), ("team", "team", "rel.team"), ("type", "release_type", "rel.rtype"), ("version", "version", "rel.version"), ("segment", "segment", "rel.segment")]
KEY_COLUMNS = ["workspace", "team", "release_type", "version", "segment", "quarter"]
def quarter(x):
    return (f"(CASE WHEN :fy = 1 THEN strftime('%Y', {x}) ELSE 'FY' || (CAST(strftime('%Y', {x}) AS INTEGER) + (CAST(strftime('%m', {x}) AS INTEGER) >= :fy)) END "
            f"|| '-Q' || (((CAST(strftime('%m', {x}) AS INTEGER) - :fy + 12) % 12) / 3 + 1))")
def expand(q):
    q = re.sub(r"\{\{keys:(.*?)\}\}", lambda m: ",\n       ".join([f"CASE WHEN instr(:by, ',{d},') > 0 THEN {c} ELSE '' END AS {a}" for d, a, c in KEY_PARTS]
                                                                 + [f"CASE WHEN instr(:by, ',quarter,') > 0 THEN {quarter(m.group(1))} ELSE '' END AS quarter"]), q, flags=re.S)
    q = re.sub(r"\{\{quarter:(.*?)\}\}", lambda m: quarter(m.group(1)), q, flags=re.S)
    q = q.replace("{{keycols}}", ", ".join(KEY_COLUMNS))
    return re.sub(r"\{\{(\w+)\}\}", lambda m: FRAGMENTS[m.group(1)], q)
for i in range(1, len(blocks), 3): blocks[i+2] = expand(blocks[i+2])
CLASSIC = [i for i in range(1, len(blocks), 3) if int(blocks[i].split()[0][1:]) <= 15]   # M1..M15: the metrics of one workspace without grouping, whose results the fixture has always held
EXEC = [i for i in range(1, len(blocks), 3) if int(blocks[i].split()[0][1:]) > 15]       # M16..M24 (REOS-121)
expected = {"params": PARAMS, "metrics": {}}
for i in CLASSIC:
    title, name, q = blocks[i], blocks[i+1], blocks[i+2].strip().rstrip(';')
    cur = db.execute(q, {k: v for k, v in BIND.items() if ':'+k in q})
    rows = cur.fetchall(); cols = [c[0] for c in cur.description]
    expected["metrics"][name] = {"key": title.split()[0], "columns": cols, "rows": [list(r) for r in rows]}
    print(f"\n{title}  [{name}]  {len(rows)} rows"); print("  ", cols)
    for r in rows[:6]: print("  ", r)

# REOS-105: another workspace's records never reach these metrics. A second workspace with a completed train, an open blocker, a freeze and a
# scheduled message leaves every default-workspace result unchanged, and shows up in its own workspace's results.
other = sqlite3.connect(":memory:"); db.backup(other)
other.executescript(f"""
INSERT INTO Workspaces(Id,Name,Slug,CreatedAt) VALUES('w2','Other','other','{AS_OF}');
INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,CurrentStatus,CreatedAt,UpdatedAt,ActualEndAt,CloseCode,WorkspaceId)
  VALUES('w2t','Other train','2026-06-01','Complete','2026-05-01T00:00:00Z','2026-06-01T00:00:00Z','2026-06-01T08:00:00Z','Unsuccessful','w2');
INSERT INTO Blockers(Id,ReleaseTrainId,Title,Severity,RaisedAt) VALUES('w2b','w2t','Other blocker','Critical','2026-09-01T00:00:00Z');
INSERT INTO FreezeWindows(Id,Name,StartsAt,EndsAt,CreatedByUserId,WorkspaceId) VALUES('w2f','Other freeze','2026-07-01T00:00:00Z','2026-07-05T00:00:00Z','rm','w2');
""")
leaks, seen = [], 0
for i in CLASSIC:
    name, q = blocks[i+1], blocks[i+2].strip().rstrip(';')
    rows = [list(r) for r in other.execute(q, {k: v for k, v in BIND.items() if ':'+k in q}).fetchall()]
    if rows != expected["metrics"][name]["rows"]: leaks.append(name)
    w2 = other.execute(q, {k: (v if k != "ws" else json.dumps(["w2"])) for k, v in BIND.items() if ':'+k in q}).fetchall()
    seen += any(any(c is not None and c != 0 for c in r) for r in w2)
print("\nworkspace isolation:", "every metric unchanged by another workspace's records" if not leaks else f"LEAK in {leaks}", f"({seen} metrics show the other workspace's own records)")
if leaks or seen < 4: sys.exit(1)

# REOS-116: the type filter. Giving the trains two types splits every train-based metric in two: the parts add up to the whole, and a type nobody has gives nothing.
typed = sqlite3.connect(":memory:"); db.backup(typed)
typed.executescript("""
INSERT INTO ReleaseTypes(Id,Name) VALUES('tA','Type A'),('tB','Type B'),('tNone','Type none');
UPDATE ReleaseTrains SET ReleaseTypeId = CASE WHEN rowid % 2 = 0 THEN 'tA' ELSE 'tB' END WHERE WorkspaceId = 'default';
""")
def run_typed(name, q, t):
    return typed.execute(q, {k: v for k, v in {**BIND, "type": t}.items() if ':'+k in q}).fetchall()
type_problems = []
for i in CLASSIC:
    name, q = blocks[i+1], blocks[i+2].strip().rstrip(';')
    if ":type" not in q: continue
    whole, a, b, none = (run_typed(name, q, t) for t in (None, "tA", "tB", "tNone"))
    if [list(r) for r in whole] != expected["metrics"][name]["rows"]: type_problems.append(f"{name}: all types differs from the unfiltered result")
    if name in ("on_time_rate", "outcomes", "comms_timeliness"):
        for col in ((0,) if name != "comms_timeliness" else (0, 1)):
            if (a[0][col] or 0) + (b[0][col] or 0) != (whole[0][col] or 0): type_problems.append(f"{name}: column {col} of the two types does not add up")
    elif name in ("slip_days", "scope_churn", "runbook_summary", "runbook_variance"):
        if len(a) + len(b) != len(whole): type_problems.append(f"{name}: rows of the two types do not add up")
    elif name == "throughput":
        if sum(r[1] for r in a) + sum(r[1] for r in b) != sum(r[1] for r in whole): type_problems.append(f"{name}: completed trains of the two types do not add up")
    if name in ("slip_days", "scope_churn", "runbook_summary", "runbook_variance") and none: type_problems.append(f"{name}: a type nobody has returned rows")
print("type filter:", "every filtered metric adds up to the whole" if not type_problems else type_problems)
if type_problems: sys.exit(1)


# REOS-121: M16..M24 across a set of workspaces, grouped. Each case is a (workspaces, grouping, fiscal-year start) the .NET tests replay and must reproduce row for row.
def add_business_days(day, n):
    d = dt.date.fromisoformat(day)
    while n > 0:
        d += dt.timedelta(days=1)
        if d.weekday() < 5: n -= 1
    return d.isoformat()
PIR_DUE = {pid: add_business_days(first[:10], 1) for pid, reason, first in db.execute(
    "SELECT p.Id, p.RequiredReason, (SELECT min(OccurredAt) FROM AuditEvents a WHERE a.EntityType='PostImplementationReview' AND a.EntityId=p.Id) FROM PostImplementationReviews p")
    if reason in ("RollbackPerformed", "Aborted") and first}      # Pir:DueBusinessDays = 1, no holidays, UTC: the app computes the same from PirDue
ALL_WS = ["default", "ws-cards", "ws-pay"]
CASES = [
    ("portfolio", ALL_WS, [], 1),
    ("workspace and quarter", ALL_WS, ["workspace", "quarter"], 1),
    ("two of three workspaces, by team and type", ["default", "ws-cards"], ["team", "type"], 1),
    ("fiscal year from October, by segment and version", ALL_WS, ["segment", "version"], 10),
    ("one workspace", ["ws-pay"], ["workspace", "quarter"], 1),
]
def run_exec(wss, by, fy, only=None, conn=None):
    out = {}
    params = {**BIND, "ws": json.dumps(wss), "by": "," + "".join(b + "," for b in by) if by else ",", "fy": fy, "pir_due": json.dumps(PIR_DUE, sort_keys=True)}
    for i in EXEC:
        name, q = blocks[i+1], blocks[i+2].strip().rstrip(';')
        if only and name not in only: continue
        cur = (conn or db).execute(q, {k: v for k, v in params.items() if ':'+k in q})
        out[name] = {"key": blocks[i].split()[0], "columns": [c[0] for c in cur.description], "rows": [list(r) for r in cur.fetchall()]}
    return out
expected["executive"] = {"params": {"from": PARAMS["from"], "to": PARAMS["to"], "now": AS_OF, "localDay": AS_OF[:10], "pirDue": PIR_DUE}, "cases": []}
for cname, wss, by, fy in CASES:
    res = run_exec(wss, by, fy)
    expected["executive"]["cases"].append({"name": cname, "workspaces": wss, "groupBy": by, "fiscalYearStartMonth": fy, "metrics": res})
    print(f"\nexecutive case: {cname}  ({', '.join(wss)}; by {by or 'nothing'}; fiscal year from month {fy})")
    for name, m in res.items():
        print(f"  {m['key']} {name}: {len(m['rows'])} rows", m['rows'][0] if m['rows'] else '')

# Cross-checks: every figure agrees with the records it summarises, counted a different way (plain queries on the tables), and the groups add up to the whole.
problems = []
def check(what, got, want):
    if got != want: problems.append(f"{what}: metric says {got}, the records say {want}")
case = {c["name"]: c["metrics"] for c in expected["executive"]["cases"]}
whole, by_wq, two, seg, pay1 = (case[n] for n in [c[0] for c in CASES])
inws = lambda wss: "(" + ",".join(f"'{w}'" for w in wss) + ")"
q1 = lambda sql: db.execute(sql).fetchone()[0] or 0
col = lambda m, c: m["columns"].index(c)
tot = lambda m, c, filt=None: sum((r[col(m, c)] or 0) for r in m["rows"] if filt is None or filt(r, m))
for nm, mset, wss in [("portfolio", whole, ALL_WS), ("two of three", None, ["default", "ws-cards"])]:
    m = mset or run_exec(wss, [], 1)
    W = inws(wss)
    check(f"M17 completed ({nm})", tot(m["predictability"], "completed"), q1(f"SELECT count(*) FROM ReleaseTrains WHERE WorkspaceId IN {W} AND CurrentStatus='Complete' AND date(ActualEndAt) BETWEEN '{PARAMS['from']}' AND '{PARAMS['to']}'"))
    check(f"M17 measured ({nm})", tot(m["predictability"], "measured"), q1(f"SELECT count(*) FROM ReleaseTrains t JOIN Baselines b ON b.ReleaseTrainId=t.Id WHERE t.WorkspaceId IN {W} AND t.CurrentStatus='Complete' AND date(t.ActualEndAt) BETWEEN '{PARAMS['from']}' AND '{PARAMS['to']}'"))
    check(f"M17 on time ({nm})", tot(m["predictability"], "on_time"), q1(f"SELECT count(*) FROM ReleaseTrains t JOIN Baselines b ON b.ReleaseTrainId=t.Id WHERE t.WorkspaceId IN {W} AND t.CurrentStatus='Complete' AND date(t.ActualEndAt) <= b.PlannedReleaseDate AND date(t.ActualEndAt) BETWEEN '{PARAMS['from']}' AND '{PARAMS['to']}'"))
    check(f"M17 change failed ({nm})", tot(m["predictability"], "change_failed"), q1(f"SELECT count(*) FROM ReleaseTrains WHERE WorkspaceId IN {W} AND CurrentStatus='Complete' AND CloseCode<>'Successful' AND date(ActualEndAt) BETWEEN '{PARAMS['from']}' AND '{PARAMS['to']}'"))
    for track, tname in [("CERT", "CERT"), ("Production", "Production")]:
        check(f"M17 {track} completed ({nm})", tot(m["predictability"], "completed", lambda r, mm, tr=track: r[col(mm, "track")] == tr),
              q1(f"SELECT count(*) FROM ReleaseTrains t JOIN ReleaseTypes y ON y.Id=t.ReleaseTypeId WHERE t.WorkspaceId IN {W} AND y.Name='{tname}' AND t.CurrentStatus='Complete' AND date(t.ActualEndAt) BETWEEN '{PARAMS['from']}' AND '{PARAMS['to']}'"))
    check(f"M16 restores ({nm})", tot(m["time_to_restore"], "restores"), q1(f"SELECT count(*) FROM RunbookRuns r JOIN ReleaseTrains t ON t.Id=r.ReleaseTrainId WHERE t.WorkspaceId IN {W} AND r.Mode='Live' AND r.Outcome='RolledBack' AND EXISTS (SELECT 1 FROM StepExecutions e WHERE e.RunId=r.Id AND e.Status='Failed') AND date(r.EndedAt) BETWEEN '{PARAMS['from']}' AND '{PARAMS['to']}'"))
    ex_ = {r[col(m["exceptions_summary"], "exception")]: 0 for r in m["exceptions_summary"]["rows"]}
    for r in m["exceptions_summary"]["rows"]: ex_[r[col(m["exceptions_summary"], "exception")]] += r[col(m["exceptions_summary"], "events")]
    check(f"M18 waivers ({nm})", ex_.get("Waiver granted", 0), q1(f"SELECT count(*) FROM GateWaivers w JOIN StageGates g ON g.Id=w.StageGateId JOIN ReleaseTrains t ON t.Id=g.ReleaseTrainId WHERE t.WorkspaceId IN {W} AND w.ApprovedAt IS NOT NULL AND date(w.ApprovedAt) BETWEEN '{PARAMS['from']}' AND '{PARAMS['to']}'"))
    check(f"M18 overrides ({nm})", ex_.get("Freeze override granted", 0), q1(f"SELECT count(*) FROM FreezeOverrides o JOIN ReleaseTrains t ON t.Id=o.ReleaseTrainId WHERE t.WorkspaceId IN {W} AND date(o.ApprovedAt) BETWEEN '{PARAMS['from']}' AND '{PARAMS['to']}'"))
    check(f"M18 decertified ({nm})", ex_.get("Gate decertified", 0), q1(f"SELECT count(*) FROM AuditEvents WHERE WorkspaceId IN {W} AND Action='Decertified' AND date(OccurredAt) BETWEEN '{PARAMS['from']}' AND '{PARAMS['to']}'"))
    check(f"M18 compliance late ({nm})", ex_.get("Compliance gate certified late", 0), q1(f"SELECT count(*) FROM StageGates g JOIN ReleaseTrains t ON t.Id=g.ReleaseTrainId WHERE t.WorkspaceId IN {W} AND g.GateClass='Compliance' AND date(g.CertifiedAt) > g.DueOn AND date(g.CertifiedAt) BETWEEN '{PARAMS['from']}' AND '{PARAMS['to']}'"))
    check(f"M18 conditions ({nm})", ex_.get("Go condition open past expiry", 0), q1(f"SELECT count(*) FROM GoNoGoConditions c JOIN GoNoGoDecisions d ON d.Id=c.DecisionId JOIN ReleaseTrains t ON t.Id=d.ReleaseTrainId WHERE t.WorkspaceId IN {W} AND c.ExpiresAt < '{AS_OF}' AND (c.ClosedAt IS NULL OR c.ClosedAt > c.ExpiresAt) AND date(c.ExpiresAt) BETWEEN '{PARAMS['from']}' AND '{PARAMS['to']}'"))
    overdue = sum(1 for pid, due in PIR_DUE.items() if due < AS_OF[:10] and db.execute(f"SELECT 1 FROM PostImplementationReviews p JOIN ReleaseTrains t ON t.Id=p.ReleaseTrainId WHERE p.Id='{pid}' AND p.Status IN ('Required','Scheduled') AND t.WorkspaceId IN {W} AND '{PARAMS['from']}' <= '{due}' AND '{due}' <= '{PARAMS['to']}'").fetchone())
    check(f"M18 PIRs overdue ({nm})", ex_.get("PIR overdue", 0), overdue)
    check(f"M19 waiver samples ({nm})", tot(m["approval_latency"], "samples", lambda r, mm: r[col(mm, "step")].startswith("Waiver")), q1(f"SELECT count(*) FROM GateWaivers w JOIN StageGates g ON g.Id=w.StageGateId JOIN ReleaseTrains t ON t.Id=g.ReleaseTrainId WHERE t.WorkspaceId IN {W} AND w.ApprovedAt IS NOT NULL AND date(w.ApprovedAt) BETWEEN '{PARAMS['from']}' AND '{PARAMS['to']}'"))
    check(f"M20 moves ({nm})", tot(m["slip_reasons"], "moves"), q1(f"SELECT count(*) FROM AuditEvents a JOIN Baselines b ON b.ReleaseTrainId=a.ReleaseTrainId WHERE a.WorkspaceId IN {W} AND a.Action='ChangeTargetDate' AND a.OccurredAt >= b.CapturedAt AND date(a.OccurredAt) BETWEEN '{PARAMS['from']}' AND '{PARAMS['to']}'"))
    for tier in ("VeryHigh", "High", "Moderate", "Low"):
        check(f"M21 {tier} ({nm})", tot(m["risk_exposure"], "open_releases", lambda r, mm, t=tier: r[col(mm, "risk_tier")] == t), q1(f"SELECT count(*) FROM ReleaseTrains WHERE WorkspaceId IN {W} AND RiskTier='{tier}' AND CurrentStatus NOT IN ('Complete','Aborted') AND ArchivedAt IS NULL"))
    check(f"M21 open blockers ({nm})", tot(m["risk_exposure"], "open_blockers"), q1(f"SELECT count(*) FROM Blockers b JOIN ReleaseTrains t ON t.Id=b.ReleaseTrainId WHERE t.WorkspaceId IN {W} AND b.ResolvedAt IS NULL AND t.CurrentStatus NOT IN ('Complete','Aborted') AND t.ArchivedAt IS NULL"))
    check(f"M22 completed ({nm})", tot(m["throughput_by_group"], "completed"), q1(f"SELECT count(*) FROM ReleaseTrains WHERE WorkspaceId IN {W} AND CurrentStatus='Complete' AND date(ActualEndAt) BETWEEN '{PARAMS['from']}' AND '{PARAMS['to']}'"))
    check(f"M23 deployments ({nm})", tot(m["deployment_success"], "deployments"), q1(f"SELECT count(*) FROM Deployments WHERE WorkspaceId IN {W} AND ReleaseTrainId IS NOT NULL AND FinishedAt IS NOT NULL AND date(FinishedAt) BETWEEN '{PARAMS['from']}' AND '{PARAMS['to']}'"))
    check(f"M24 reviews ({nm})", tot(m["pir_follow_through"], "reviews"), q1(f"SELECT count(*) FROM PostImplementationReviews p JOIN ReleaseTrains t ON t.Id=p.ReleaseTrainId WHERE t.WorkspaceId IN {W} AND date(COALESCE(t.ActualEndAt,t.UpdatedAt)) BETWEEN '{PARAMS['from']}' AND '{PARAMS['to']}'"))
    check(f"M24 actions ({nm})", tot(m["pir_follow_through"], "actions"), q1(f"SELECT count(*) FROM PirActions a JOIN PostImplementationReviews p ON p.Id=a.PirId JOIN ReleaseTrains t ON t.Id=p.ReleaseTrainId WHERE t.WorkspaceId IN {W} AND date(COALESCE(t.ActualEndAt,t.UpdatedAt)) BETWEEN '{PARAMS['from']}' AND '{PARAMS['to']}'"))
# the classic metrics and the new ones agree where they overlap: M1 reads the default workspace, so does M17 with only that workspace
solo = run_exec(["default"], [], 1)
check("M17 vs M1 completed", tot(solo["predictability"], "measured"), expected["metrics"]["on_time_rate"]["rows"][0][0])
check("M17 vs M1 on time", tot(solo["predictability"], "on_time"), expected["metrics"]["on_time_rate"]["rows"][0][1])
check("M22 vs M15 completed", tot(solo["throughput_by_group"], "completed"), sum(r[1] for r in expected["metrics"]["throughput"]["rows"]))
# groups add up to the whole (quarter and workspace are exclusive; segment rows overlap by design and are not summed)
for name, cols_ in [("time_to_restore", ["restores"]), ("predictability", ["completed", "measured", "on_time", "change_failed"]), ("exceptions_summary", ["events"]), ("approval_latency", ["samples"]),
                    ("slip_reasons", ["moves", "days_later", "days_earlier"]), ("risk_exposure", ["open_releases", "open_blockers", "no_rehearsal"]), ("throughput_by_group", ["completed"]),
                    ("deployment_success", ["deployments", "successful", "breaches"]), ("pir_follow_through", ["reviews", "actions", "actions_done", "actions_overdue", "overdue"])]:
    for c in cols_: check(f"{name}.{c}: workspace and quarter rows add up to the portfolio", tot(by_wq[name], c), tot(whole[name], c))
# the three workspaces split into two + one
for name, c in [("predictability", "completed"), ("throughput_by_group", "completed"), ("risk_exposure", "open_releases"), ("deployment_success", "deployments")]:
    only_pay = tot(pay1[name], c)
    check(f"{name}.{c}: two of three plus the third is the portfolio", tot(two[name], c) + only_pay, tot(whole[name], c))
# segment: a release is in Standard when its type is Production, in On-Demand when it has client dates, in neither otherwise
sg = lambda segname, c, m="predictability": tot(seg[m], c, lambda r, mm: r[col(mm, "segment")] == segname)
check("segment Standard = Production releases", sg("Standard", "completed"), q1(f"SELECT count(*) FROM ReleaseTrains t JOIN ReleaseTypes y ON y.Id=t.ReleaseTypeId WHERE t.WorkspaceId IN {inws(ALL_WS)} AND y.Name='Production' AND t.CurrentStatus='Complete' AND date(t.ActualEndAt) BETWEEN '{PARAMS['from']}' AND '{PARAMS['to']}'"))
check("segment On-Demand = releases with client dates", sg("On-Demand", "completed"), q1(f"SELECT count(*) FROM ReleaseTrains t WHERE t.WorkspaceId IN {inws(ALL_WS)} AND EXISTS (SELECT 1 FROM ReleaseClientDates d WHERE d.ReleaseTrainId=t.Id) AND t.CurrentStatus='Complete' AND date(t.ActualEndAt) BETWEEN '{PARAMS['from']}' AND '{PARAMS['to']}'"))
check("segment None = the rest", sg("No client segment", "completed"), q1(f"SELECT count(*) FROM ReleaseTrains t LEFT JOIN ReleaseTypes y ON y.Id=t.ReleaseTypeId WHERE t.WorkspaceId IN {inws(ALL_WS)} AND COALESCE(y.Name,'')<>'Production' AND NOT EXISTS (SELECT 1 FROM ReleaseClientDates d WHERE d.ReleaseTrainId=t.Id) AND t.CurrentStatus='Complete' AND date(t.ActualEndAt) BETWEEN '{PARAMS['from']}' AND '{PARAMS['to']}'"))
# quarters: calendar labels, and fiscal labels for a year that starts in October (FY2027 = Oct 2026 to Sep 2027)
def cal_q(day): return f"{day[:4]}-Q{(int(day[5:7]) - 1) // 3 + 1}"
def fis_q(day, fy):
    mo = int(day[5:7]); return f"FY{int(day[:4]) + (1 if mo >= fy else 0)}-Q{((mo - fy + 12) % 12) // 3 + 1}"
want = sorted({cal_q(r[0]) for r in db.execute(f"SELECT date(ActualEndAt) FROM ReleaseTrains WHERE WorkspaceId IN {inws(ALL_WS)} AND CurrentStatus='Complete'")})
check("calendar quarters of M22", sorted({r[col(by_wq['throughput_by_group'], 'quarter')] for r in by_wq["throughput_by_group"]["rows"]}), want)
fy10 = run_exec(ALL_WS, ["quarter"], 10, only=["throughput_by_group"])["throughput_by_group"]
check("fiscal quarters (year from October) of M22", sorted({r[col(fy10, 'quarter')] for r in fy10["rows"]}), sorted({fis_q(r[0], 10) for r in db.execute(f"SELECT date(ActualEndAt) FROM ReleaseTrains WHERE WorkspaceId IN {inws(ALL_WS)} AND CurrentStatus='Complete'")}))
# every metric has data in the scenario, so no new metric is covered by an empty result
for name in [blocks[i+1] for i in EXEC]:
    if not whole[name]["rows"]: problems.append(f"{name}: the portfolio scenario gives it no rows")
print("\nexecutive metrics:", "every figure agrees with the records it summarises" if not problems else problems)
if problems: sys.exit(1)

if "--write" in sys.argv:
    os.makedirs(FIX, exist_ok=True)
    out = os.path.join(FIX, "seed.db")
    if os.path.exists(out): os.remove(out)
    disk = sqlite3.connect(out); db.backup(disk); disk.execute("VACUUM"); disk.close()
    json.dump(expected, open(os.path.join(FIX, "expected_metrics.json"), "w"), indent=1)
    print("\nwrote", out, "and expected_metrics.json")
