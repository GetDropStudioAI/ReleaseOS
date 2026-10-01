using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Tests;

/// <summary>
/// Port of tests/reference/test_schema.py: same 88 cases, same order, same expected messages, run against the
/// EF-migrated database (not schema.sql directly). The Python oracle stays and must keep passing.
/// Sections that share a database in the oracle share one in-order scenario here.
/// </summary>
public sealed class TriggerSuiteFixture : IDisposable
{
    public string Dir { get; } = Directory.CreateTempSubdirectory("reos-trg-").FullName;
    private string Template => Path.Combine(Dir, "template.db");
    private int _n;

    public const string Now = "2026-10-20T14:00:00Z";

    public TriggerSuiteFixture()
    {
        using (var ctx = new ReleaseDbContext(new DbContextOptionsBuilder<ReleaseDbContext>()
                   .UseSqlite($"Data Source={Template};Pooling=False").AddInterceptors(new SqliteConnectionInterceptor()).Options))
            ctx.Database.Migrate();
        using var c = Open(Template);
        Run(c, $@"
        INSERT INTO Users(Id,Email,DisplayName,Role) VALUES
          ('rte','rte@x.com','Rae T.','RTE'),
          ('rm','rm@x.com','Rel Mgr','ReleaseManager'),
          ('gov1','g1@x.com','Gov One','GovernanceOfficer'),
          ('gov2','g2@x.com','Gov Two','GovernanceOfficer'),
          ('dev','dev@x.com','Dev','Viewer');
        INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CreatedAt,UpdatedAt)
          VALUES('t1','R26.10','2026-10-30','High','{Now}','{Now}');
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
        INSERT INTO StepDependencies(StepId,DependsOnStepId) VALUES('s2','s1');");
    }

    public static SqliteConnection Open(string path)
    {
        var c = new SqliteConnection($"Data Source={path};Pooling=False");
        c.Open();
        Run(c, SqliteConnectionInterceptor.Pragmas);
        return c;
    }

    public static void Run(SqliteConnection c, string sql, params object?[] p)
    {
        using var cmd = c.CreateCommand();
        Bind(cmd, sql, p);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Translates positional '?' into named parameters (Microsoft.Data.Sqlite binds by name).</summary>
    public static void Bind(SqliteCommand cmd, string sql, object?[] p)
    {
        var i = 0;
        cmd.CommandText = System.Text.RegularExpressions.Regex.Replace(sql, @"\?", _ => $"$p{i++}");
        for (var k = 0; k < p.Length; k++) cmd.Parameters.AddWithValue($"$p{k}", p[k] ?? DBNull.Value);
    }

    public SqliteConnection Fresh() => Open(FreshPath());

    public string FreshPath()
    {
        var path = Path.Combine(Dir, $"case{Interlocked.Increment(ref _n)}.db");
        File.Copy(Template, path);
        return path;
    }

    public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(Dir, true); } catch (IOException) { } }
}

public class TriggerSuiteTests(TriggerSuiteFixture fx) : IClassFixture<TriggerSuiteFixture>
{
    private const string Now = TriggerSuiteFixture.Now;

    // ---- helpers mirroring the oracle -------------------------------------------------------------
    private static void Check(SqliteConnection db, string sql, bool shouldPass, string label, object?[]? p = null, string? expect = null)
    {
        bool ok; string msg;
        try { TriggerSuiteFixture.Run(db, sql, p ?? []); ok = shouldPass; msg = "allowed"; }
        catch (SqliteException e) when (e.SqliteErrorCode == 19)
        {
            ok = !shouldPass && (expect is null || e.Message.Contains(expect)); msg = $"blocked: {e.Message}";
        }
        Assert.True(ok, $"{label} ({(shouldPass ? "expected allowed" : $"expected blocked, containing '{expect}'")}) -> {msg}");
    }

    private static void AssertTrue(bool cond, string label, string detail = "") => Assert.True(cond, $"{label} {detail}");
    private static void Exec(SqliteConnection db, string sql, params object?[] p) => TriggerSuiteFixture.Run(db, sql, p);

    private static List<object?[]> Rows(SqliteConnection db, string sql, params object?[] p)
    {
        using var cmd = db.CreateCommand();
        TriggerSuiteFixture.Bind(cmd, sql, p);
        using var r = cmd.ExecuteReader();
        var rows = new List<object?[]>();
        while (r.Read()) { var row = new object?[r.FieldCount]; r.GetValues(row!); rows.Add(row.Select(v => v is DBNull ? null : v).ToArray()); }
        return rows;
    }

    private static long Count(SqliteConnection db, string sql, params object?[] p) => Convert.ToInt64(Rows(db, sql, p)[0][0]);

    private static void DoneTask(SqliteConnection db, string tid, string who) =>
        Exec(db, "UPDATE ChecklistTasks SET IsCompleted=1,CompletedAt=?,CompletedByUserId=?,LastChangedByUserId=? WHERE Id=?", Now, who, who, tid);

    private static void Start(SqliteConnection db, string gid, string who) =>
        Exec(db, "UPDATE StageGates SET Status='InProgress',LastChangedByUserId=? WHERE Id=?", who, gid);

    private static string Certify(string gid, string who, string at = Now) =>
        $"UPDATE StageGates SET Status='Certified',CertifiedByUserId='{who}',CertifiedAt='{at}',LastChangedByUserId='{who}',LastChangedAt='{at}' WHERE Id='{gid}'";

    private const string GoCond = "INSERT INTO GoNoGoConditions(Id,DecisionId,Text,OwnerUserId,ExpiresAt) VALUES(?,?,?,?,?)";
    private const string Freeze = "INSERT INTO FreezeWindows(Id,Name,Kind,StartsAt,EndsAt,ProductPattern,CreatedByUserId) VALUES(?,?,?,?,?,?,?)";
    private const string Override = "INSERT INTO FreezeOverrides(Id,FreezeWindowId,ReleaseTrainId,Reason,RequestedByUserId,ApprovedByUserId,ApprovedAt,ExpiresAt) VALUES(?,?,?,?,?,?,?,?)";
    private const string Decision = "INSERT INTO GoNoGoDecisions(Id,ReleaseTrainId,Decision,DecidedByUserId,DecidedAt,GateSnapshotJson,NewTargetReleaseDate) VALUES(?,?,?,?,?,'{}',?)";
    private const string Run = "INSERT INTO RunbookRuns(Id,ReleaseTrainId,Mode,StartedAt,StartedByUserId) VALUES(?,?,?,?,?)";
    private const string ToExecuting = "UPDATE ReleaseTrains SET CurrentStatus='Executing' WHERE Id='t1'";

    // ---- scenarios ----------------------------------------------------------------------------------
    /// <summary>Oracle sections 1-3 and 5-6 share one database, so they run in order in one scenario.</summary>
    [Fact]
    public void Lifecycle_compliance_executing_runbook_and_completion()
    {
        using var db = fx.Fresh();

        // Train lifecycle and gate lockout
        Check(db, "UPDATE ReleaseTrains SET CurrentStatus='Executing' WHERE Id='t1'", false, "skip Planning -> Executing", expect: "Illegal train status transition");
        Check(db, "UPDATE ReleaseTrains SET CurrentStatus='Gated' WHERE Id='t1'", false, "Gated while Code Freeze uncertified", expect: "Gate lockout");
        Check(db, Certify("g1", "rte"), false, "certify gate with an open task", expect: "open checklist tasks");
        Check(db, Certify("g2", "gov2"), false, "certify gate 2 before gate 1", expect: "earlier gate");
        DoneTask(db, "k1", "rte");
        Check(db, Certify("g1", "rte"), false, "Pending -> Certified skips In progress", expect: "Illegal gate status transition");
        Check(db, "UPDATE StageGates SET Status='InProgress' WHERE Id='g1'", true, "Pending -> In progress");
        Check(db, Certify("g1", "rte", "2026-10-22T17:05:00Z"), true, "certify Code Freeze");
        var b = Rows(db, "SELECT PlannedReleaseDate, SnapshotJson FROM Baselines WHERE ReleaseTrainId='t1'").SingleOrDefault();
        JsonElement? snap = b is null ? null : JsonDocument.Parse((string)b[1]!).RootElement;
        AssertTrue(b is not null && snap!.Value.GetProperty("products").GetArrayLength() == 2 && snap.Value.GetProperty("steps").GetArrayLength() == 2,
            "baseline auto-captured at Code Freeze", b?[0]?.ToString() ?? "");
        AssertTrue(Count(db, "SELECT count(*) FROM AuditEvents WHERE Action IN ('AutoCaptured','EvidenceLocked')") == 2, "cascaded effects are audited (baseline, evidence lock)");
        Check(db, "INSERT INTO Baselines(Id,ReleaseTrainId,CapturedAt,PlannedReleaseDate,SnapshotJson) VALUES('b2','t1',?,'2026-10-30','{}')", false, "second baseline for a train", [Now], "UNIQUE");
        Check(db, "UPDATE Baselines SET PlannedReleaseDate='2026-11-30'", false, "edit baseline", expect: "immutable");
        Check(db, "DELETE FROM Baselines", false, "delete baseline", expect: "immutable");
        Check(db, "UPDATE ReleaseTrains SET CurrentStatus='Gated' WHERE Id='t1'", true, "Planning -> Gated");
        var rows = Rows(db, "SELECT FromStatus,ToStatus,OccurredAt FROM GateTransitions WHERE StageGateId='g1' ORDER BY Id");
        AssertTrue(rows.Count == 2 && (string)rows[1][2]! == "2026-10-22T17:05:00Z", "transition log uses the service clock (LastChangedAt)", string.Join(";", rows.Select(r => string.Join(",", r))));
        Check(db, "DELETE FROM GateTransitions", false, "delete gate transition log", expect: "append-only");
        Check(db, "UPDATE StageGates SET Status='Failed',CertifiedByUserId=NULL,CertifiedAt=NULL WHERE Id='g1'", false, "Certified -> Failed without reopening", expect: "Illegal gate status transition");

        // Compliance gate: role, segregation of duties, waiver
        DoneTask(db, "k2", "gov1");
        Check(db, Certify("g2", "rm"), false, "Release Manager certifies Compliance gate", expect: "Governance Officers only");
        Check(db, Certify("g2", "gov1"), false, "certifier also completed a task in the gate (SoD)", expect: "Segregation of duties");
        Check(db, "UPDATE StageGates SET Status='Waived',CertifiedByUserId='gov1',CertifiedAt=?,LastChangedByUserId='gov1' WHERE Id='g2'", false, "waive without approved waiver", [Now], "second approver");
        Exec(db, "INSERT INTO GateWaivers(Id,StageGateId,Reason,RequestedByUserId,RequestedAt) VALUES('w1','g2','Pen-test vendor slipped; CISO accepted risk','gov1',?)", Now);
        Check(db, "UPDATE GateWaivers SET ApprovedByUserId='gov1',ApprovedAt=? WHERE Id='w1'", false, "waiver self-approval", [Now], "CHECK");
        Check(db, "UPDATE GateWaivers SET ApprovedByUserId='rm',ApprovedAt=? WHERE Id='w1'", false, "waiver approved by a Release Manager", [Now], "approved by a Governance Officer");
        Check(db, "INSERT INTO GateWaivers(Id,StageGateId,Reason,RequestedByUserId,RequestedAt) VALUES('w2','g2','too short','gov1',?)", false, "waiver reason under 20 chars", [Now], "CHECK");
        Check(db, "INSERT INTO GateWaivers(Id,StageGateId,Reason,RequestedByUserId,ApprovedByUserId,RequestedAt,ApprovedAt) VALUES('w3','g2','Inserted already approved by a viewer','gov1','dev',?,?)", false, "waiver inserted pre-approved by a Viewer", [Now, Now], "approved by a Governance Officer");
        Check(db, Certify("g2", "gov2"), true, "independent Governance Officer certifies");

        // Executing preconditions
        Exec(db, "UPDATE ReleaseTrains SET RiskTier='Moderate' WHERE Id='t1'");
        Check(db, ToExecuting, false, "Executing with no Go decision", expect: "recorded Go decision");
        Exec(db, Decision, "d1", "t1", "NoGo", "rm", "2026-10-29T15:00:00Z", "2026-11-06");
        Check(db, ToExecuting, false, "Executing when latest decision is NoGo", expect: "recorded Go decision");
        Check(db, GoCond, false, "condition on a NoGo decision", ["c0", "d1", "x", "rm", "2099-01-01T00:00:00Z"], "GoWithConditions");
        Exec(db, Decision, "d2", "t1", "GoWithConditions", "rm", "2026-10-29T16:00:00Z", null);
        Check(db, GoCond, true, "expiring condition on a conditional Go", ["c1", "d2", "Load test by 22:00", "rte", "2000-01-01T00:00:00Z"]);
        Check(db, "UPDATE GoNoGoDecisions SET Decision='Go'", false, "edit a recorded decision", expect: "immutable");
        Check(db, ToExecuting, false, "Executing with an expired, unclosed condition", expect: "condition expired");
        Exec(db, "UPDATE GoNoGoConditions SET ClosedAt=?, ClosedByUserId='rte' WHERE Id='c1'", Now);
        Exec(db, "UPDATE ReleaseTrains SET RiskTier='High' WHERE Id='t1'");
        Check(db, ToExecuting, false, "High risk with no rehearsed rollback", expect: "rehearsed rollback");
        Check(db, "UPDATE ReleaseTrains SET RollbackRehearsedAt=?, RollbackRehearsedByUserId='rte' WHERE Id='t1'", true, "record rollback rehearsal", [Now]);
        Check(db, Run, false, "Live run before Executing", ["r1", "t1", "Live", Now, "rte"], "requires the train to be Executing");
        Check(db, Run, true, "Rehearsal run any time", ["r0", "t1", "Rehearsal", Now, "rte"]);
        Check(db, ToExecuting, true, "Gated -> Executing");

        // Runbook execution
        Check(db, Run, true, "start Live run", ["r1", "t1", "Live", Now, "rte"]);
        Check(db, Run, false, "second concurrent Live run", ["r2", "t1", "Live", Now, "rte"], "UNIQUE");
        Exec(db, "INSERT INTO StepExecutions(Id,RunId,StepId) VALUES('e1','r1','s1'),('e2','r1','s2')");
        Check(db, "UPDATE StepExecutions SET Status='Running',ActualStartAt='2026-10-30T02:12:00Z' WHERE Id='e2'", false, "start step before its dependency is done", expect: "Dependency not complete");
        Check(db, "UPDATE StepExecutions SET Status='Skipped' WHERE Id='e1'", false, "skip without a comment", expect: "CHECK");
        Check(db, "UPDATE StepExecutions SET Status='Running',ActualStartAt='2026-10-30T02:01:00Z' WHERE Id='e1'", true, "start step 1");
        Check(db, "UPDATE StepExecutions SET Status='Done',ActualEndAt='2026-10-30T02:14:00Z' WHERE Id='e1'", true, "finish step 1");
        Exec(db, Freeze, "f1", "Q4 close freeze", "Freeze", "2026-10-30T00:00:00Z", "2026-10-31T00:00:00Z", "Payments*", "gov1");
        const string StartDeploy = "UPDATE StepExecutions SET Status='Running',ActualStartAt='2026-10-30T02:15:00Z' WHERE Id='e2'";
        Check(db, StartDeploy, false, "Deploy step inside matching freeze, no override", expect: "Freeze window");
        const string R = "Regulatory fix must ship before month-end close";
        Check(db, Override, false, "freeze override self-approved", ["o0", "f1", "t1", R, "rm", "rm", Now, "2026-10-30T06:00:00Z"], "CHECK");
        Check(db, Override, false, "freeze override approved by an RTE", ["o0", "f1", "t1", R, "rm", "rte", Now, "2026-10-30T06:00:00Z"], "Release Manager or Governance Officer");
        Exec(db, Override, "o1", "f1", "t1", R, "rm", "gov1", Now, "2026-10-30T02:10:00Z");
        Check(db, StartDeploy, false, "override expired before step start", expect: "Freeze window");
        Check(db, "UPDATE FreezeOverrides SET ExpiresAt='2099-01-01T00:00:00Z' WHERE Id='o1'", false, "extend an override in place", expect: "immutable");
        Check(db, "DELETE FROM FreezeOverrides WHERE Id='o1'", false, "delete an override", expect: "immutable");
        Check(db, Override, true, "renew override with a new row", ["o2", "f1", "t1", R, "rm", "gov1", Now, "2026-10-30T06:00:00Z"]);
        Check(db, StartDeploy, true, "Deploy step with valid override");

        // Completion and PIR
        Check(db, "UPDATE ReleaseTrains SET CurrentStatus='Complete' WHERE Id='t1'", false, "Complete without close code", expect: "CHECK");
        Check(db, "UPDATE ReleaseTrains SET CurrentStatus='Complete', CloseCode='SuccessfulWithIssues', LastChangedByUserId='rte', LastChangedAt='2026-10-30T09:30:00Z' WHERE Id='t1'", true, "Complete, successful with issues");
        var pir = Rows(db, "SELECT RequiredReason, Status FROM PostImplementationReviews WHERE ReleaseTrainId='t1'").SingleOrDefault();
        AssertTrue(pir is not null && (string)pir[0]! == "CloseCode=SuccessfulWithIssues" && (string)pir[1]! == "Required", "PIR auto-required", pir is null ? "none" : string.Join(",", pir));
        var a = Rows(db, "SELECT a.ActorUserId, a.OccurredAt, a.EntityId = p.Id FROM AuditEvents a JOIN PostImplementationReviews p ON p.ReleaseTrainId='t1' WHERE a.Action='AutoRequired'").SingleOrDefault();
        AssertTrue(a is not null && (string)a[0]! == "rte" && (string)a[1]! == "2026-10-30T09:30:00Z" && Convert.ToInt64(a[2]) == 1, "PIR creation audited: actor, service time, PIR id", a is null ? "none" : string.Join(",", a));
    }

    [Fact]
    public void Executing_needs_a_baseline_and_gate_reopen_needs_an_actor()
    {
        using var db2 = fx.Fresh();
        Exec(db2, "UPDATE ReleaseTrains SET RiskTier='Low' WHERE Id='t1'");
        Exec(db2, "UPDATE StageGates SET GateName='Build Verify' WHERE Id='g1'"); // not a freeze gate: no auto baseline
        DoneTask(db2, "k1", "rte"); Start(db2, "g1", "rte"); Exec(db2, Certify("g1", "rte"));
        Exec(db2, "UPDATE ReleaseTrains SET CurrentStatus='Gated' WHERE Id='t1'");
        DoneTask(db2, "k2", "gov1"); Exec(db2, Certify("g2", "gov2"));
        Exec(db2, Decision, "d1", "t1", "Go", "rm", Now, null);
        Check(db2, ToExecuting, false, "Executing with no baseline captured", expect: "baseline");
        Check(db2, "UPDATE StageGates SET Status='InProgress',CertifiedByUserId=NULL,CertifiedAt=NULL WHERE Id='g2'", true, "reopen a certified gate (Certified -> In progress)");
        Check(db2, "UPDATE StageGates SET Status='Failed' WHERE Id='g2'", true, "In progress -> Failed");
        Check(db2, "UPDATE StageGates SET Status='InProgress', LastChangedByUserId=NULL WHERE Id='g2'", false, "gate status change with no actor", expect: "LastChangedByUserId");
    }

    [Fact]
    public void Condition_expiry_follows_the_service_clock()
    {
        using var db4 = fx.Fresh();
        Exec(db4, "UPDATE ReleaseTrains SET RiskTier='Low' WHERE Id='t1'");
        DoneTask(db4, "k1", "rte"); Start(db4, "g1", "rte"); Exec(db4, Certify("g1", "rte"));
        Exec(db4, "UPDATE ReleaseTrains SET CurrentStatus='Gated' WHERE Id='t1'");
        DoneTask(db4, "k2", "gov1"); Exec(db4, Certify("g2", "gov2"));
        Exec(db4, Decision, "d9", "t1", "GoWithConditions", "rm", "2026-10-29T15:00:00Z", null);
        Exec(db4, GoCond, "c9", "d9", "CISO sign-off", "gov1", "2026-10-30T05:30:00Z");
        Check(db4, "UPDATE ReleaseTrains SET CurrentStatus='Executing', LastChangedAt='2026-10-30T06:00:00Z' WHERE Id='t1'", false, "service clock past condition expiry", expect: "condition expired");
        Check(db4, "UPDATE ReleaseTrains SET CurrentStatus='Executing', LastChangedAt='2026-10-30T05:00:00Z' WHERE Id='t1'", true, "service clock before condition expiry");
    }

    /// <summary>Also the state the audit/dispatch/import section of the oracle continues from.</summary>
    [Fact]
    public void Decertify_on_change_evidence_locking_and_supporting_tables()
    {
        using var db = fx.Fresh();
        Exec(db, "INSERT INTO Attachments(Id,ReleaseTrainId,EntityType,EntityId,FileName,ContentType,SizeBytes,Sha256,StoragePath,UploadedByUserId,UploadedAt) VALUES('a1','t1','Task','k1','tags.txt','text/plain',120,?,'/ev/a1','rte',?)", new string('a', 64), Now);
        DoneTask(db, "k1", "rte"); Start(db, "g1", "rte"); Exec(db, Certify("g1", "rte"));
        AssertTrue(Count(db, "SELECT IsLocked FROM Attachments WHERE Id='a1'") == 1, "evidence locked when gate certifies");
        Check(db, "UPDATE Attachments SET IsLocked=0 WHERE Id='a1'", false, "unlock locked evidence", expect: "locked evidence");
        Check(db, "DELETE FROM Attachments WHERE Id='a1'", false, "delete locked evidence", expect: "locked evidence");
        Check(db, "UPDATE Attachments SET FileName='x' WHERE Id='a1'", false, "rename locked evidence", expect: "locked evidence");
        Exec(db, "UPDATE ChecklistTasks SET IsCompleted=0,CompletedAt=NULL,CompletedByUserId=NULL,LastChangedByUserId='dev',LastChangedAt='2026-10-21T09:00:00Z' WHERE Id='k1'");
        var g = Rows(db, "SELECT Status, LastChangedByUserId FROM StageGates WHERE Id='g1'").Single();
        AssertTrue((string)g[0]! == "InProgress" && (string)g[1]! == "dev", "reopening a task decertifies its gate, actor = reopener", string.Join(",", g));
        AssertTrue(Count(db, "SELECT count(*) FROM AuditEvents WHERE Action='Decertified' AND ActorUserId='dev'") == 1, "decertify is audited with the reopener");
        DoneTask(db, "k1", "rte"); Exec(db, Certify("g1", "rte"));
        Exec(db, "INSERT INTO ChecklistTasks(Id,StageGateId,TaskDescription,OwnerUserId,SequenceOrder,LastChangedByUserId) VALUES('k9','g1','Late add','rte',2,'rm')");
        g = Rows(db, "SELECT Status, LastChangedByUserId FROM StageGates WHERE Id='g1'").Single();
        AssertTrue((string)g[0]! == "InProgress" && (string)g[1]! == "rm", "adding a task to a certified gate decertifies it, actor = adder", string.Join(",", g));

        // Audit, dispatches, imports, integrations, session (same database, as in the oracle)
        Exec(db, "INSERT INTO AuditEvents(OccurredAt,EntityType,EntityId,Action) VALUES(?,'Train','t1','Create')", Now);
        Check(db, "UPDATE AuditEvents SET Action='x'", false, "edit audit row", expect: "append-only");
        Check(db, "DELETE FROM AuditEvents", false, "delete audit row", expect: "append-only");
        Exec(db, "INSERT INTO CommTemplates(Id,ReleaseTrainId,TemplateType,Audience,SubjectLine,MarkdownBody) VALUES('ct1','t1','T-1','All','s','b')");
        Exec(db, "INSERT INTO CommDispatches(Id,CommTemplateId,Channel,HydratedSubject,HydratedBody,DispatchedByUserId,DispatchedAt,Outcome) VALUES('cd1','ct1','Copy','s','b','rte',?,'Handed')", Now);
        Check(db, "UPDATE CommDispatches SET HydratedBody='edited'", false, "edit a sent message", expect: "immutable");
        Check(db, "DELETE FROM CommDispatches", false, "delete a sent message", expect: "immutable");
        Exec(db, "INSERT INTO ImportJobs(Id,Kind,FileName,Sha256,RowCount,ErrorCount,ErrorsJson,Status,UploadedByUserId,CreatedAt) VALUES('i1','Tasks','t.csv','x',40,2,'[{\"row\":7,\"column\":\"Owner\",\"message\":\"unknown @ops-db\"}]','Previewed','rte',?)", Now);
        Check(db, "UPDATE ImportJobs SET Status='Committed' WHERE Id='i1'", false, "commit import with row errors", expect: "row errors");
        Check(db, "INSERT INTO WebhookDestinations(Id,Name,Url,Kind) VALUES('h1','x','http://internal','Generic')", false, "non-HTTPS webhook", expect: "CHECK");
        Check(db, "INSERT INTO UserSessionState(UserId,ClientId,SchemaVersion,UIStateJson,LastActivityAt) VALUES('rte','c1',1,'{bad',?)", false, "invalid UI state JSON", [Now], "CHECK");
        const string Alert = "INSERT INTO SyncAlerts(Id,SourceSystem,Kind,Fingerprint,ErrorMessage,FirstOccurredAt,LastOccurredAt) VALUES('{0}','{1}','{2}','{3}','{4}',?,?)";
        Check(db, string.Format(Alert, "x1", "Jira", "AuthFailed", "fp1", "401"), true, "open sync alert", [Now, Now]);
        Check(db, string.Format(Alert, "x2", "Jira", "AuthFailed", "fp1", "401"), false, "duplicate open alert (must increment count)", [Now, Now], "UNIQUE");
        Check(db, string.Format(Alert, "x3", "SyncEngine", "Stalled", "fp2", "no cycle in 15 min"), true, "watchdog stall is storable", [Now, Now]);
        Check(db, string.Format(Alert, "x4", "Backup", "BackupFailed", "fp3", "disk full"), true, "backup failure is storable", [Now, Now]);
        Exec(db, "INSERT INTO IcsTokens(Id,UserId,TokenSha256,CreatedAt) VALUES('it1','rte',?,?)", new string('b', 64), Now);
        Check(db, "INSERT INTO IcsTokens(Id,UserId,TokenSha256,CreatedAt) VALUES('it2','rte',?,?)", false, "second active ICS token for a user", [new string('c', 64), Now], "UNIQUE");
        Check(db, "INSERT INTO DeploymentWindows(Id,ReleaseTrainId,StartsAt,EndsAt) VALUES('dw1','t1','2026-10-30T06:00:00Z','2026-10-30T10:00:00Z')", true, "deployment window");
        Check(db, "INSERT INTO DeploymentWindows(Id,ReleaseTrainId,StartsAt,EndsAt) VALUES('dw2','t1','2026-10-31T06:00:00Z','2026-10-31T10:00:00Z')", false, "second window for a train (one per train in v1)", expect: "UNIQUE");

        // Identity binding and sign-out (Q-SEC-B8, Q-SEC-B5)
        Check(db, "UPDATE Users SET IdpIssuer='https://idp.example/',IdpSubject='sub-rte' WHERE Id='rte'", true, "bind a user to an IdP issuer + subject");
        Check(db, "UPDATE Users SET IdpIssuer='https://idp.example/',IdpSubject='sub-rte' WHERE Id='rm'", false, "same issuer + subject bound to a second user", expect: "UNIQUE");
        Check(db, "UPDATE Users SET IdpIssuer='https://other.example/',IdpSubject='sub-rte' WHERE Id='rm'", true, "same subject at another issuer is another identity");
        Check(db, "UPDATE Users SET IdpIssuer='https://idp.example/' WHERE Id='dev'", false, "issuer without subject", expect: "CHECK");
        Check(db, "INSERT INTO SessionRevocations(SessionId,UserId,RevokedAt,ExpiresAt) VALUES('ab12','rte',?,'2026-10-21T02:00:00Z')", true, "record a signed-out session", [Now]);
        Check(db, "INSERT INTO SessionRevocations(SessionId,UserId,RevokedAt,ExpiresAt) VALUES('ab12','rte',?,'2026-10-21T02:00:00Z')", false, "same session revoked twice", [Now], "UNIQUE");
    }

    [Fact]
    public void Open_item_14_empty_Compliance_gate()
    {
        using var db3 = fx.Fresh();
        DoneTask(db3, "k1", "rte"); Start(db3, "g1", "rte"); Exec(db3, Certify("g1", "rte"));
        Exec(db3, "DELETE FROM ChecklistTasks WHERE Id='k2'");
        Check(db3, Certify("g2", "gov2"), false, "certify Compliance gate with zero tasks", expect: "at least one task");
        Exec(db3, "UPDATE StageGates SET GateClass='Standard' WHERE Id='g2'");
        Check(db3, Certify("g2", "gov2"), true, "Standard gate with zero tasks may certify");
    }
}
