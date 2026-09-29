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
PARAMS = {"from": "2026-01-01", "to": "2026-12-31", "now": AS_OF}

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

print("trains:", db.execute("select count(*), sum(CurrentStatus='Complete') from ReleaseTrains").fetchone(),
      " PIRs auto-created:", db.execute("select count(*) from PostImplementationReviews").fetchone()[0],
      " baselines:", db.execute("select count(*) from Baselines").fetchone()[0])
sql = open(ANALYTICS).read()
blocks = re.split(r"\n-- (M\d+ .*)\n-- name: (\w+)\n", sql)
expected = {"params": PARAMS, "metrics": {}}
for i in range(1, len(blocks), 3):
    title, name, q = blocks[i], blocks[i+1], blocks[i+2].strip().rstrip(';')
    cur = db.execute(q, {k: v for k, v in PARAMS.items() if ':'+k in q})
    rows = cur.fetchall(); cols = [c[0] for c in cur.description]
    expected["metrics"][name] = {"key": title.split()[0], "columns": cols, "rows": [list(r) for r in rows]}
    print(f"\n{title}  [{name}]  {len(rows)} rows"); print("  ", cols)
    for r in rows[:6]: print("  ", r)

if "--write" in sys.argv:
    os.makedirs(FIX, exist_ok=True)
    out = os.path.join(FIX, "seed.db")
    if os.path.exists(out): os.remove(out)
    disk = sqlite3.connect(out); db.backup(disk); disk.execute("VACUUM"); disk.close()
    json.dump(expected, open(os.path.join(FIX, "expected_metrics.json"), "w"), indent=1)
    print("\nwrote", out, "and expected_metrics.json")
