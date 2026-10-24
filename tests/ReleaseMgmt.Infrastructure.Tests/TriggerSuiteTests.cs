using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Tests;

/// <summary>
/// Port of tests/reference/test_schema.py: same 362 cases, same order, same expected messages, run against the
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
        INSERT INTO StepDependencies(StepId,DependsOnStepId) VALUES('s2','s1');
        INSERT INTO WorkspaceMembers(WorkspaceId,UserId,Role,AddedAt) SELECT 'default', Id, Role, '{Now}' FROM Users;");   // the role triggers read the membership role (REOS-105)
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
        AssertTrue(Count(db, "SELECT count(*) FROM AuditEvents WHERE WorkspaceId IS NOT 'default'") == 0, "every audit row of the default workspace's release (service and trigger rows) is filed under it");
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
        Check(db, "INSERT INTO WebhookDestinations(Id,Name,Host,ProtectedUrl,UrlHmac,Kind) VALUES('h1','x','hooks.example.com','https://hooks.example.com/T/B/secret',?,'Generic')", false, "webhook URL stored in clear", [new string('a', 64)], "CHECK");
        Exec(db, "INSERT INTO WebhookDestinations(Id,Name,Host,ProtectedUrl,UrlHmac,Kind) VALUES('h2','x','hooks.example.com','CfDJ8AAAA',?,'Generic')", new string('b', 64));
        Check(db, "INSERT INTO WebhookDestinations(Id,Name,Host,ProtectedUrl,UrlHmac,Kind) VALUES('h3','y','hooks.example.com','CfDJ8BBBB',?,'Generic')", false, "same webhook address twice (keyed hash)", [new string('b', 64)], "UNIQUE");
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
        AssertTrue(Count(db, "SELECT count(*) FROM AuditEvents WHERE WorkspaceId IS NOT 'default'") == 0, "every audit row of the default workspace's release (service and trigger rows) is filed under it");
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

    [Fact]
    public void Train_milestones_informational_key_dates()
    {
        using var db5 = fx.Fresh();
        Exec(db5, "INSERT INTO Teams(Id,Handle,Name) VALUES('tm1','ops','Ops')");
        const string M = "INSERT INTO TrainMilestones(Id,ReleaseTrainId,Name,DueOn,OwnerUserId,OwnerTeamId,IsDone,DoneAt,DoneByUserId) VALUES(?,'t1',?,?,?,?,?,?,?)";
        Check(db5, M, true, "milestone with no owner", ["m1", "Code complete", "2026-10-16", null, null, 0, null, null]);
        Check(db5, M, false, "milestone owned by a user and a team", ["m2", "UAT sign-off", "2026-10-21", "rte", "tm1", 0, null, null], "CHECK");
        Check(db5, M, false, "milestone name over 200 characters", ["m3", new string('x', 201), "2026-10-21", null, null, 0, null, null], "CHECK");
        Check(db5, M, false, "milestone due date that is not a date", ["m4", "UAT sign-off", "2026-10-32", null, null, 0, null, null], "CHECK");
        Check(db5, M, false, "done milestone without who and when", ["m5", "UAT sign-off", "2026-10-21", null, "tm1", 1, null, null], "CHECK");
    }

    [Fact]
    public void Workspaces_membership_uniqueness_and_boundary()
    {
        using var db6 = fx.Fresh();
        static string H(char c) => new(c, 64);
        Exec(db6, $@"
        INSERT INTO Workspaces(Id,Name,Slug,CreatedAt) VALUES('w2','Payments EU','payments-eu','{Now}');
        INSERT INTO Teams(Id,Handle,Name) VALUES('tm1','ops','Ops');
        INSERT INTO Teams(Id,Handle,Name,WorkspaceId) VALUES('tm2','ops','Ops EU','w2');
        INSERT INTO WebhookDestinations(Id,Name,Host,ProtectedUrl,UrlHmac,Kind,WorkspaceId) VALUES('h2','EU','hooks.example.com','CfDJ8EU','{H('e')}','Generic','w2');
        INSERT INTO TrainTemplates(Id,Name) VALUES('tp1','Monthly');
        INSERT INTO TrainTemplates(Id,Name,WorkspaceId) VALUES('tp2','Monthly','w2');
        INSERT INTO CommTemplateLibrary(Id,TemplateType,Name,Audience,SubjectLine,MarkdownBody,WorkspaceId) VALUES('lib2','Tminus28','T-28','All','s','b','w2');
        INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,CreatedAt,UpdatedAt,WorkspaceId) VALUES('t2','EU R1','2026-11-30','{Now}','{Now}','w2');
        INSERT INTO BundledProducts(Id,ReleaseTrainId,ProductName,VersionTag,ProjectCode) VALUES('p9','t2','EU API','1.0.0','EU');
        INSERT INTO StageGates(Id,ReleaseTrainId,GateName,SequenceOrder,OffsetDays,DueOn,OwnerUserId,LastChangedByUserId) VALUES('gw','t2','Readiness',1,3,'2026-11-25','rte','rte');
        INSERT INTO RunbookSteps(Id,ReleaseTrainId,StepCode,Title,OwnerUserId,PlannedStartAt,PlannedDurationMin) VALUES('s9','t2','R-001','EU deploy','rte','2026-11-30T02:00:00Z',10);
        INSERT INTO FreezeWindows(Id,Name,StartsAt,EndsAt,CreatedByUserId,WorkspaceId) VALUES('f2','EU freeze','2026-10-01T00:00:00Z','2026-12-31T00:00:00Z','rm','w2');");
        var dw = Rows(db6, "SELECT Name, Slug FROM Workspaces WHERE Id='default'").Single();
        AssertTrue((string)dw[0]! == "Default" && (string)dw[1]! == "default", "the default workspace exists in a new database");
        AssertTrue((string)Rows(db6, "SELECT WorkspaceId FROM ReleaseTrains WHERE Id='t1'").Single()[0]! == "default", "a root row written without a workspace lands in the default one");
        var roots = Rows(db6, "SELECT m.name FROM sqlite_master m WHERE m.type='table' AND m.name NOT IN ('Workspaces','WorkspaceMembers') AND m.name NOT LIKE '__EF%' AND EXISTS (SELECT 1 FROM pragma_table_info(m.name) WHERE name='WorkspaceId')")
            .Select(r => (string)r[0]!).Order().ToList();
        string[] expectedRoots = ["Teams", "Holidays", "TrainTemplates", "CommTemplateLibrary", "ReleaseTrains", "FreezeWindows", "SyncAlerts", "WebhookDestinations",
            "Notifications", "ImportJobs", "ExportJobs", "IcsTokens", "ConnectorState", "AuditEvents",
            "ReleaseTypes", "ReleaseVersions", "Environments", "CatalogServices", "IngestKeys", "Deployments", "MetricSamples", "MetricRollups", "ThresholdBreaches", "DeploymentControlActions",
            "ApprovalPolicies", "Approvals"];
        AssertTrue(roots.SequenceEqual(expectedRoots.Order()), "WorkspaceId on every root table, and only there", string.Join(",", roots));
        var moving = roots.Where(t => t != "AuditEvents" && Count(db6, "SELECT count(*) FROM sqlite_master WHERE type='trigger' AND name=?", $"trg_Ws_{t}_Update") == 0).ToList();
        AssertTrue(moving.Count == 0, "every root table but the append-only audit log refuses a change of workspace", string.Join(",", moving));
        Check(db6, "INSERT INTO Workspaces(Id,Name,Slug,CreatedAt) VALUES('w3','X','Payments-EU',?)", false, "workspace slug with capitals", [Now], "CHECK");
        Check(db6, "INSERT INTO Workspaces(Id,Name,Slug,CreatedAt) VALUES('w3','X','payments eu',?)", false, "workspace slug with a space", [Now], "CHECK");
        Check(db6, "INSERT INTO Workspaces(Id,Name,Slug,CreatedAt) VALUES('w3','  ','w3',?)", false, "blank workspace name", [Now], "CHECK");
        Check(db6, "INSERT INTO WorkspaceMembers(WorkspaceId,UserId,Role,AddedAt) VALUES('w2','rte','RTE',?)", true, "a person is a member of a second workspace", [Now]);
        Check(db6, "INSERT INTO WorkspaceMembers(WorkspaceId,UserId,Role,AddedAt) VALUES('w2','rte','Viewer',?)", false, "the same person twice in one workspace", [Now], "UNIQUE");
        Check(db6, "INSERT INTO WorkspaceMembers(WorkspaceId,UserId,Role,AddedAt) VALUES('w2','rm','Admin',?)", false, "a role outside the four", [Now], "CHECK");
        Check(db6, "INSERT INTO Teams(Id,Handle,Name,WorkspaceId) VALUES('tm3','OPS','Ops again','w2')", false, "team handle twice in one workspace (any case)", expect: "UNIQUE");
        Check(db6, "INSERT INTO Holidays(Day,Name) VALUES('2026-12-25','Christmas')", true, "a holiday in the default workspace");
        Check(db6, "INSERT INTO Holidays(Day,Name,WorkspaceId) VALUES('2026-12-25','Christmas','w2')", true, "the same holiday in another workspace");
        Check(db6, "INSERT INTO Holidays(Day,Name) VALUES('2026-12-25','Xmas')", false, "the same day twice in one workspace", expect: "UNIQUE");
        Check(db6, "INSERT INTO TrainTemplates(Id,Name,WorkspaceId) VALUES('tp3','Monthly','w2')", false, "template name twice in one workspace", expect: "UNIQUE");
        Check(db6, "INSERT INTO ConnectorState(SourceSystem,BaseUrl) VALUES('Jira','https://a.example')", true, "Jira connector in the default workspace");
        Check(db6, "INSERT INTO ConnectorState(SourceSystem,BaseUrl,WorkspaceId) VALUES('Jira','https://b.example','w2')", true, "a second Jira connector in another workspace");
        Exec(db6, "INSERT INTO IcsTokens(Id,UserId,TokenSha256,CreatedAt) VALUES('ic1','rte',?,?)", H('1'), Now);
        Check(db6, "INSERT INTO IcsTokens(Id,UserId,TokenSha256,CreatedAt,WorkspaceId) VALUES('ic2','rte',?,?,'w2')", true, "a calendar feed per workspace for the same person", [H('2'), Now]);
        Check(db6, "INSERT INTO WebhookDestinations(Id,Name,Host,ProtectedUrl,UrlHmac,Kind) VALUES('h3','Same','hooks.example.com','CfDJ8X',?,'Generic')", false, "one address is one destination on the host", [H('e')], "UNIQUE");
        Check(db6, "UPDATE ReleaseTrains SET WorkspaceId='w2' WHERE Id='t1'", false, "move a train to another workspace", expect: "cannot move to another workspace");
        Check(db6, "UPDATE Holidays SET WorkspaceId='w2' WHERE WorkspaceId='default'", false, "move a holiday to another workspace", expect: "cannot move to another workspace");
        Check(db6, "UPDATE SyncAlerts SET WorkspaceId=NULL", true, "no alert rows: nothing to move");
        Check(db6, "UPDATE Teams SET Name='Operations' WHERE Id='tm1'", true, "edit a root row in place");
        const string Tr = "INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,CreatedAt,UpdatedAt,TemplateId,ClonedFromTrainId) VALUES(?,'x','2026-12-01',?,?,?,?)";
        Check(db6, Tr, false, "train from another workspace's template", ["t5", Now, Now, "tp2", null], "template belongs to another workspace");
        Check(db6, Tr, false, "train cloned from another workspace's train", ["t5", Now, Now, null, "t2"], "cloned-from train belongs to another workspace");
        Check(db6, Tr, true, "train from its own workspace's template, cloned from its own train", ["t5", Now, Now, "tp1", "t1"]);
        Check(db6, "UPDATE ReleaseTrains SET TemplateId='tp2' WHERE Id='t5'", false, "point a train at another workspace's template", expect: "template belongs to another workspace");
        Check(db6, Tr, false, "a missing template is still a foreign-key error", ["t6", Now, Now, "nope", null], "FOREIGN KEY");
        Check(db6, "UPDATE Teams SET WebhookDestinationId='h2' WHERE Id='tm1'", false, "team channel from another workspace", expect: "webhook destination belongs to another workspace");
        Check(db6, "INSERT INTO StageGates(Id,ReleaseTrainId,GateName,SequenceOrder,OffsetDays,DueOn,OwnerTeamId) VALUES('g9','t1','CAB',9,1,'2026-10-29','tm2')", false, "gate owned by another workspace's team", expect: "team belongs to another workspace");
        Check(db6, "UPDATE StageGates SET OwnerUserId=NULL, OwnerTeamId='tm2' WHERE Id='g1'", false, "gate handed to another workspace's team", expect: "team belongs to another workspace");
        Check(db6, "UPDATE StageGates SET OwnerUserId=NULL, OwnerTeamId='tm1' WHERE Id='g1'", true, "gate handed to its own workspace's team");
        Check(db6, "INSERT INTO StageGates(Id,ReleaseTrainId,GateName,SequenceOrder,OffsetDays,DueOn,OwnerTeamId) VALUES('g9','t1','CAB',9,1,'2026-10-29','nope')", false, "a missing team is still a foreign-key error", expect: "FOREIGN KEY");
        Check(db6, "INSERT INTO ChecklistTasks(Id,StageGateId,TaskDescription,OwnerTeamId,SequenceOrder) VALUES('k9','g1','x','tm2',9)", false, "task owned by another workspace's team", expect: "team belongs to another workspace");
        Check(db6, "INSERT INTO ChecklistTasks(Id,StageGateId,BundledProductId,TaskDescription,OwnerUserId,SequenceOrder) VALUES('k9','g1','p9','x','rte',9)", false, "task for another train's product", expect: "product belongs to another train");
        Check(db6, "UPDATE ChecklistTasks SET StageGateId='gw' WHERE Id='k1'", false, "task moved to another train's gate", expect: "cannot move to another train");
        Check(db6, "UPDATE ChecklistTasks SET StageGateId='g2' WHERE Id='k1'", true, "task moved to another gate of its train");
        Check(db6, "UPDATE StageGates SET ReleaseTrainId='t2' WHERE Id='g2'", false, "gate moved to another train", expect: "cannot move to another train");
        Check(db6, "UPDATE BundledProducts SET ReleaseTrainId='t2' WHERE Id='p2'", false, "product moved to another train", expect: "cannot move to another train");
        Check(db6, "INSERT INTO RunbookSteps(Id,ReleaseTrainId,BundledProductId,StepCode,Title,OwnerUserId,PlannedStartAt,PlannedDurationMin) VALUES('s8','t1','p9','R-009','x','rte',?,5)", false, "runbook step for another train's product", [Now], "product belongs to another train");
        Check(db6, "INSERT INTO RunbookSteps(Id,ReleaseTrainId,StepCode,Title,OwnerTeamId,PlannedStartAt,PlannedDurationMin) VALUES('s8','t1','R-009','x','tm2',?,5)", false, "runbook step owned by another workspace's team", [Now], "team belongs to another workspace");
        Check(db6, "INSERT INTO StepDependencies(StepId,DependsOnStepId) VALUES('s1','s9')", false, "step depends on another train's step", expect: "step belongs to another train");
        Exec(db6, "INSERT INTO RunbookRuns(Id,ReleaseTrainId,Mode,StartedAt,StartedByUserId) VALUES('r9','t1','Rehearsal',?,'rte')", Now);
        Check(db6, "INSERT INTO StepExecutions(Id,RunId,StepId) VALUES('e9','r9','s9')", false, "a run executes another train's step", expect: "step belongs to another train");
        Check(db6, "INSERT INTO StepExecutions(Id,RunId,StepId) VALUES('e9','r9','s1')", true, "a run executes its own train's step");
        Check(db6, "INSERT INTO Blockers(Id,ReleaseTrainId,StageGateId,Title,Severity,RaisedAt) VALUES('b9','t1','gw','x','High',?)", false, "blocker on another train's gate", [Now], "gate belongs to another train");
        Check(db6, "INSERT INTO TrainMilestones(Id,ReleaseTrainId,Name,DueOn,OwnerTeamId) VALUES('m9','t1','UAT','2026-10-21','tm2')", false, "milestone owned by another workspace's team", expect: "team belongs to another workspace");
        Check(db6, "INSERT INTO TemplateGates(Id,TemplateId,GateName,SequenceOrder,OffsetDays,RequiredBeforeStatus,OwnerTeamId) VALUES('tg1','tp1','CAB',1,2,'Gated','tm2')", false, "template gate owned by another workspace's team", expect: "team belongs to another workspace");
        Check(db6, "INSERT INTO TemplateSteps(Id,TemplateId,StepCode,Section,Title,OffsetMinutes,PlannedDurationMin,OwnerTeamId) VALUES('ts1','tp2','R-1','Deploy','x',0,5,'tm2')", true, "template step owned by its own workspace's team");
        Check(db6, "INSERT INTO TemplateCommSchedule(Id,TemplateId,LibraryTemplateId,OffsetDays) VALUES('tc1','tp1','lib2',-28)", false, "template schedules another workspace's library template", expect: "library template belongs to another workspace");
        Check(db6, "INSERT INTO CommTemplates(Id,ReleaseTrainId,LibraryTemplateId,TemplateType,Audience,SubjectLine,MarkdownBody) VALUES('ct1','t1','lib2','Tminus28','All','s','b')", false, "train copies another workspace's library template", expect: "library template belongs to another workspace");
        Exec(db6, "INSERT INTO CommTemplates(Id,ReleaseTrainId,TemplateType,Audience,SubjectLine,MarkdownBody) VALUES('ct2','t2','Tminus28','All','s','b')");
        Check(db6, "INSERT INTO CommSchedule(Id,ReleaseTrainId,CommTemplateId,DueAt) VALUES('cs1','t1','ct2',?)", false, "train schedules another train's message", [Now], "communication template belongs to another train");
        Exec(db6, "INSERT INTO CommTemplates(Id,ReleaseTrainId,TemplateType,Audience,SubjectLine,MarkdownBody) VALUES('ct3','t1','Tminus28','All','s','b')");
        Check(db6, "INSERT INTO CommDispatches(Id,CommTemplateId,Channel,WebhookDestinationId,HydratedSubject,HydratedBody,DispatchedByUserId,DispatchedAt,Outcome) VALUES('d1','ct3','Webhook','h2','s','b','rte',?,'Handed')", false, "message sent to another workspace's webhook", [Now], "webhook destination belongs to another workspace");
        Check(db6, "INSERT INTO FreezeOverrides(Id,FreezeWindowId,ReleaseTrainId,Reason,RequestedByUserId,ApprovedByUserId,ApprovedAt,ExpiresAt) VALUES('o9','f2','t1','Emergency payments fix for EU',?,?,?,?)", false, "override of another workspace's freeze", ["rm", "gov1", Now, "2026-10-30T06:00:00Z"], "freeze window belongs to another workspace");
        Check(db6, "INSERT INTO SyncAlerts(Id,ReleaseTrainId,SourceSystem,Kind,Fingerprint,ErrorMessage,FirstOccurredAt,LastOccurredAt,WorkspaceId) VALUES('a9','t1','Jira','NotFound','fp9','x',?,?,'w2')", false, "alert about another workspace's train", [Now, Now], "train belongs to another workspace");
        Check(db6, "INSERT INTO SyncAlerts(Id,SourceSystem,Kind,Fingerprint,ErrorMessage,FirstOccurredAt,LastOccurredAt,WorkspaceId) VALUES('a9','Backup','BackupFailed','fp9','disk full',?,?,NULL)", true, "host-wide alert (no workspace)", [Now, Now]);
        Exec(db6, "INSERT INTO ExportJobs(Id,Kind,FileName,RequestedByUserId,CreatedAt,WorkspaceId) VALUES('x9','EvidencePackPdf','e.pdf','rte',?,'w2')", Now);
        Check(db6, "INSERT INTO MetricSnapshots(ExportJobId,ReleaseTrainId,MetricKey,Value,CapturedAt) VALUES('x9','t1','lead_time',1,?)", false, "evidence pack snapshots another workspace's train", [Now], "train belongs to another workspace");
        Check(db6, "INSERT INTO ImportJobs(Id,Kind,ReleaseTrainId,FileName,Sha256,RowCount,Status,UploadedByUserId,CreatedAt,WorkspaceId) VALUES('i9','Tasks','t1','f.csv','x',1,'Previewed','rte',?,'w2')", false, "import into another workspace's train", [Now], "train belongs to another workspace");
        Check(db6, "INSERT INTO AuditEvents(OccurredAt,ReleaseTrainId,EntityType,EntityId,Action,WorkspaceId) VALUES(?,'t1','Train','t1','x','w2')", false, "audit row filed under another workspace than its train", [Now], "train belongs to another workspace");
        Check(db6, "INSERT INTO AuditEvents(OccurredAt,EntityType,EntityId,Action,WorkspaceId) VALUES(?,'User','rte','Created',NULL)", true, "host-wide audit row (accounts)", [Now]);
        Exec(db6, "UPDATE StageGates SET Status='InProgress', LastChangedByUserId='rte' WHERE Id='gw'");
        Exec(db6, Certify("gw", "rte"));
        var locked = Rows(db6, "SELECT WorkspaceId FROM AuditEvents WHERE EntityId='gw' AND Action='EvidenceLocked'");
        AssertTrue(locked.Count == 1 && (string)locked[0][0]! == "w2", "an audit row written by a trigger carries the train's workspace");
        // Freeze lockout reads the train's own workspace: an Executing train inserted directly, a Live run, a Deploy step
        Exec(db6, $@"
        INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,CurrentStatus,CreatedAt,UpdatedAt) VALUES('t7','Hotfix','2026-10-30','Executing','{Now}','{Now}');
        INSERT INTO RunbookSteps(Id,ReleaseTrainId,StepCode,Section,Title,OwnerUserId,PlannedStartAt,PlannedDurationMin) VALUES('s7','t7','R-001','Deploy','Deploy','rte','2026-10-30T02:00:00Z',10);
        INSERT INTO RunbookRuns(Id,ReleaseTrainId,Mode,StartedAt,StartedByUserId) VALUES('r7','t7','Live','{Now}','rte');
        INSERT INTO StepExecutions(Id,RunId,StepId) VALUES('e7','r7','s7');");
        const string StartE7 = "UPDATE StepExecutions SET Status='Running',ActualStartAt='2026-10-30T02:00:00Z' WHERE Id='e7'";
        Check(db6, StartE7, true, "another workspace's freeze does not lock this train out");
        Exec(db6, "UPDATE StepExecutions SET Status='Scheduled',ActualStartAt=NULL WHERE Id='e7'");
        Exec(db6, "INSERT INTO FreezeWindows(Id,Name,StartsAt,EndsAt,CreatedByUserId) VALUES('f7','Year end','2026-10-01T00:00:00Z','2026-12-31T00:00:00Z','rm')");
        Check(db6, StartE7, false, "its own workspace's freeze does", expect: "Freeze window");
        // Role triggers read the role held in the train's workspace (REOS-105), never Users.Role
        Exec(db6, $@"
        INSERT INTO WorkspaceMembers(WorkspaceId,UserId,Role,AddedAt) VALUES('w2','gov1','Viewer','{Now}'),('w2','dev','GovernanceOfficer','{Now}'),('w2','gov2','RTE','{Now}');
        INSERT INTO StageGates(Id,ReleaseTrainId,GateName,GateClass,SequenceOrder,OffsetDays,DueOn,OwnerUserId,Status,LastChangedByUserId) VALUES('gc','t2','EU Compliance','Compliance',2,2,'2026-11-28','dev','InProgress','dev');
        INSERT INTO ChecklistTasks(Id,StageGateId,TaskDescription,OwnerUserId,SequenceOrder,IsCompleted,CompletedAt,CompletedByUserId) VALUES('kc','gc','EU evidence','rte',1,1,'{Now}','rte');
        INSERT INTO GateWaivers(Id,StageGateId,Reason,RequestedByUserId,RequestedAt) VALUES('wv9','gc','EU regulator extension granted in writing','rte','{Now}');");
        Check(db6, Certify("gc", "gov1"), false, "Compliance certifier who is only a Viewer in the train's workspace", expect: "Governance Officers only");
        Check(db6, Certify("gc", "rm"), false, "Compliance certifier who is not a member of the train's workspace", expect: "Governance Officers only");
        Check(db6, "UPDATE GateWaivers SET ApprovedByUserId='gov1',ApprovedAt=? WHERE Id='wv9'", false, "waiver approver who is a Governance Officer elsewhere only", [Now], "approved by a Governance Officer");
        Check(db6, "INSERT INTO GateWaivers(Id,StageGateId,Reason,RequestedByUserId,ApprovedByUserId,RequestedAt,ApprovedAt) VALUES('wv8','gc','Inserted approved by an outsider','rte','gov2',?,?)", false, "waiver inserted pre-approved by an RTE of the workspace", [Now, Now], "approved by a Governance Officer");
        Check(db6, "UPDATE GateWaivers SET ApprovedByUserId='dev',ApprovedAt=? WHERE Id='wv9'", true, "waiver approved by the workspace's Governance Officer (a Viewer by Users.Role)", [Now]);
        Check(db6, Certify("gc", "dev"), true, "Compliance gate certified by the workspace's Governance Officer");
        Check(db6, Override, false, "freeze override approved by someone who is not a member of the train's workspace", ["o8", "f2", "t2", "Emergency payments fix for EU", "gov2", "rm", Now, "2026-10-30T06:00:00Z"], "approved by a Release Manager or Governance Officer");
        Check(db6, Override, false, "freeze override approved by a Viewer of the train's workspace", ["o8", "f2", "t2", "Emergency payments fix for EU", "gov2", "gov1", Now, "2026-10-30T06:00:00Z"], "approved by a Release Manager or Governance Officer");
        Check(db6, Override, true, "freeze override approved by the workspace's Governance Officer", ["o8", "f2", "t2", "Emergency payments fix for EU", "gov2", "dev", Now, "2026-10-30T06:00:00Z"]);
        Exec(db6, "UPDATE WorkspaceMembers SET Role='Viewer' WHERE WorkspaceId='default' AND UserId='gov2'");
        Exec(db6, "UPDATE ChecklistTasks SET IsCompleted=1,CompletedAt=?,CompletedByUserId='gov1',LastChangedByUserId='gov1' WHERE Id='k2'", Now);
        Check(db6, Certify("g2", "gov2"), false, "a Governance Officer by Users.Role whose membership is now Viewer", expect: "Governance Officers only");
    }

    [Fact]
    public void Release_model_environments_catalog_and_deployments()
    {
        using var db7 = fx.Fresh();
        Exec(db7, $@"
INSERT INTO Workspaces(Id,Name,Slug,CreatedAt,KeyPrefix) VALUES('w2','EU','eu','{Now}','EU');
INSERT INTO Teams(Id,Handle,Name) VALUES('tm1','ops','Ops');
INSERT INTO Teams(Id,Handle,Name,WorkspaceId) VALUES('tm2','euops','EU Ops','w2');
INSERT INTO WorkspaceMembers(WorkspaceId,UserId,Role,AddedAt) VALUES('w2','rm','ReleaseManager','{Now}');
INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,CreatedAt,UpdatedAt,WorkspaceId) VALUES('t2','EU R1','2026-11-30','{Now}','{Now}','w2');
INSERT INTO Environments(Id,Name,Kind,CreatedAt) VALUES('e1','Staging','Staging','{Now}'),('e2','Prod EU','Production','{Now}');
INSERT INTO Environments(Id,Name,Kind,CreatedAt,WorkspaceId) VALUES('e9','Prod','Production','{Now}','w2');
INSERT INTO CatalogServices(Id,ServiceKey,Name,CreatedAt) VALUES('sv1','payments-api','Payments API','{Now}');
INSERT INTO CatalogServices(Id,ServiceKey,Name,CreatedAt,WorkspaceId) VALUES('sv9','payments-api','Payments API EU','{Now}','w2');
INSERT INTO IngestKeys(Id,Name,KeyPrefix,KeySha256,CreatedAt) VALUES('ik1','CI','rk_ab12',printf('%064d',1),'{Now}');");
        AssertTrue((string)Rows(db7, "SELECT ReleaseKey FROM ReleaseTrains WHERE Id='t1'").Single()[0]! == "RLS-1", "a train inserted without a key takes the workspace's next one");
        AssertTrue((string)Rows(db7, "SELECT ReleaseKey FROM ReleaseTrains WHERE Id='t2'").Single()[0]! == "EU-1", "the key uses the train's workspace prefix and counter");
        Exec(db7, $"INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,CreatedAt,UpdatedAt) VALUES('t3','R2','2026-11-30','{Now}','{Now}')");
        var t3 = Rows(db7, "SELECT ReleaseKey, (SELECT NextKeyNumber FROM Workspaces WHERE Id='default') FROM ReleaseTrains WHERE Id='t3'").Single();
        AssertTrue((string)t3[0]! == "RLS-2" && (long)t3[1]! == 3, "keys count up");
        Exec(db7, "DELETE FROM ReleaseTrains WHERE Id='t3'");
        Exec(db7, $"INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,CreatedAt,UpdatedAt) VALUES('t4','R3','2026-11-30','{Now}','{Now}')");
        AssertTrue((string)Rows(db7, "SELECT ReleaseKey FROM ReleaseTrains WHERE Id='t4'").Single()[0]! == "RLS-3", "a deleted release's number is not reused");
        Check(db7, "UPDATE ReleaseTrains SET ReleaseKey='RLS-99' WHERE Id='t1'", false, "a release key never changes", expect: "release key never changes");
        Check(db7, "UPDATE ReleaseTrains SET ReleaseKey='RLS-1' WHERE Id='t4'", false, "a release key never changes (to another's)", expect: "release key never changes");
        Check(db7, $"INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,CreatedAt,UpdatedAt,ReleaseKey) VALUES('t5','x','2026-11-30','{Now}','{Now}','RLS-1')", false, "two releases with one key in a workspace", expect: "UNIQUE");
        Check(db7, $"INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,CreatedAt,UpdatedAt,ReleaseKey,WorkspaceId) VALUES('t5','x','2026-11-30','{Now}','{Now}','RLS-1','w2')", true, "the same key in another workspace");
        Check(db7, $"INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,CreatedAt,UpdatedAt,ReleaseKey) VALUES('t6','x','2026-11-30','{Now}','{Now}','rls 7')", false, "a key with a space or lower case", expect: "CHECK");
        Check(db7, "UPDATE Workspaces SET KeyPrefix='r' WHERE Id='w2'", false, "a one-letter key prefix", expect: "CHECK");
        Check(db7, "UPDATE Workspaces SET KeyPrefix='rel' WHERE Id='w2'", false, "a lower-case key prefix", expect: "CHECK");
        Check(db7, "UPDATE Workspaces SET KeyPrefix='EU2' WHERE Id='w2'", true, "a valid key prefix");
        Check(db7, "UPDATE ReleaseTrains SET OwnerUserId='rte' WHERE Id='t1'", true, "owner who is a member of the workspace");
        Check(db7, "UPDATE ReleaseTrains SET OwnerUserId='rte' WHERE Id='t2'", false, "owner who is not a member of the train's workspace", expect: "owner is not a member of this workspace");
        Check(db7, "UPDATE ReleaseTrains SET OwnerTeamId='tm2' WHERE Id='t1'", false, "owning team of another workspace", expect: "owning team belongs to another workspace");
        Check(db7, "UPDATE ReleaseTrains SET OwnerTeamId='tm1', ReleaseBranch='release/26.10', OnCallTeamId='tm1' WHERE Id='t1'", true, "team, branch and on-call team");
        Check(db7, "UPDATE ReleaseTrains SET OnCallUserId='rm' WHERE Id='t1'", false, "on-call a person and a team", expect: "CHECK");
        Check(db7, "UPDATE ReleaseTrains SET OnCallTeamId=NULL, OnCallUserId='rm' WHERE Id='t1'", true, "on-call a person");
        Check(db7, "UPDATE ReleaseTrains SET OnCallUserId='dev' WHERE Id='t2'", false, "on-call person who is not a member of the train's workspace", expect: "on-call person is not a member of this workspace");
        Check(db7, "UPDATE ReleaseTrains SET ReleaseBranch='  ' WHERE Id='t1'", false, "a blank release branch", expect: "CHECK");
        Check(db7, "INSERT INTO ReleaseTypes(Id,Name) VALUES('ty1','CERT')", true, "a release type");
        Check(db7, "INSERT INTO ReleaseTypes(Id,Name) VALUES('ty2','cert')", false, "a release type name twice in a workspace (any case)", expect: "UNIQUE");
        Check(db7, "INSERT INTO ReleaseTypes(Id,Name,WorkspaceId) VALUES('ty3','CERT','w2')", true, "the same type name in another workspace");
        Check(db7, "UPDATE ReleaseTrains SET ReleaseTypeId='ty3' WHERE Id='t1'", false, "a release of another workspace's type", expect: "release type belongs to another workspace");
        Check(db7, "UPDATE ReleaseTrains SET ReleaseTypeId='ty1' WHERE Id='t1'", true, "a release of its workspace's type");
        Check(db7, "UPDATE ReleaseTypes SET WorkspaceId='w2' WHERE Id='ty1'", false, "a release type moved to another workspace", expect: "cannot move to another workspace");
        Check(db7, "INSERT INTO Environments(Id,Name,Kind,CreatedAt) VALUES('e3','Dev','Dev',?)", false, "an environment kind outside staging and production", [Now], "CHECK");
        Check(db7, "INSERT INTO Environments(Id,Name,Kind,CreatedAt) VALUES('e3','staging','Staging',?)", false, "an environment name twice in a workspace (any case)", [Now], "UNIQUE");
        Check(db7, "INSERT INTO Environments(Id,Name,Kind,CreatedAt,ErrorRateMaxPct) VALUES('e3','UAT','Staging',?,120)", false, "an error-rate threshold over 100 %", [Now], "CHECK");
        Check(db7, "INSERT INTO Environments(Id,Name,Kind,CreatedAt,ControlWebhookDestinationId) VALUES('e3','UAT','Staging',?,'nope')", false, "a control webhook that does not exist", [Now], "FOREIGN KEY");
        Exec(db7, "INSERT INTO WebhookDestinations(Id,Name,Host,ProtectedUrl,UrlHmac,Kind,WorkspaceId) VALUES('hw2','EU hook','hooks.eu.example','CfDJ8X',printf('%064d',7),'Generic','w2')");
        Check(db7, "UPDATE Environments SET ControlWebhookDestinationId='hw2' WHERE Id='e1'", false, "environment control webhook of another workspace", expect: "webhook destination belongs to another workspace");
        Check(db7, "INSERT INTO TrainEnvironments(ReleaseTrainId,EnvironmentId) VALUES('t1','e1')", true, "a release targets an environment");
        Check(db7, "INSERT INTO TrainEnvironments(ReleaseTrainId,EnvironmentId) VALUES('t1','e9')", false, "a release targets another workspace's environment", expect: "environment belongs to another workspace");
        Check(db7, "INSERT INTO TrainEnvironments(ReleaseTrainId,EnvironmentId) VALUES('t1','e1')", false, "the same environment twice on a release", expect: "UNIQUE");
        Check(db7, "INSERT INTO CatalogServices(Id,ServiceKey,Name,CreatedAt) VALUES('sv2','PAYMENTS-API','x',?)", false, "a service key twice in a workspace (any case)", [Now], "UNIQUE");
        Check(db7, "INSERT INTO CatalogServices(Id,ServiceKey,Name,CreatedAt) VALUES('sv2','pay ments','x',?)", false, "a service key with a space", [Now], "CHECK");
        Check(db7, "INSERT INTO CatalogServices(Id,ServiceKey,Name,CreatedAt,OwnerTeamId) VALUES('sv2','card-portal','Card Portal',?,'tm2')", false, "a service owned by another workspace's team", [Now], "team belongs to another workspace");
        Check(db7, "INSERT INTO CatalogServices(Id,ServiceKey,Name,CreatedAt,OwnerTeamId) VALUES('sv2','card-portal','Card Portal',?,'tm1')", true, "a service owned by its workspace's team", [Now]);
        Check(db7, "UPDATE BundledProducts SET CatalogServiceId='sv1' WHERE Id='p1'", true, "a product linked to a service of its workspace");
        Check(db7, "UPDATE BundledProducts SET CatalogServiceId='sv9' WHERE Id='p2'", false, "a product linked to another workspace's service", expect: "catalog service belongs to another workspace");
        Check(db7, "INSERT INTO IngestKeys(Id,Name,KeyPrefix,KeySha256,CreatedAt) VALUES('ik2','x','rk_zz99',printf('%064d',1),?)", false, "one key hash twice", [Now], "UNIQUE");
        Check(db7, "INSERT INTO IngestKeys(Id,Name,KeyPrefix,KeySha256,CreatedAt) VALUES('ik2','x','rk_zz99','NOTHEX',?)", false, "a key hash that is not 64 hex characters", [Now], "CHECK");
        Check(db7, "UPDATE IngestKeys SET RevokedAt=? WHERE Id='ik1'", false, "revoked without saying by whom", [Now], "CHECK");
        Check(db7, "UPDATE IngestKeys SET RevokedAt=?, RevokedByUserId='rte' WHERE Id='ik1'", true, "a key revoked by a person", [Now]);
        Check(db7, "UPDATE IngestKeys SET WorkspaceId='w2' WHERE Id='ik1'", false, "a key moved to another workspace", expect: "cannot move to another workspace");
        const string Dep_Sql = "INSERT INTO Deployments(Id,ExternalId,CatalogServiceId,EnvironmentId,ReleaseTrainId,RunId,ReleaseVersion,Strategy,CanarySteps,State,StartedAt,FinishedAt,CreatedAt,UpdatedAt,WorkspaceId) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)";
        static object?[] Dep(string id, string ext = "ci-1", string svc = "sv1", string env = "e1", string? train = "t1", string? run = null, string ver = "4.5.0", string strat = "Rolling",
            string? steps = null, string state = "Queued", string? started = null, string? finished = null, string ws = "default") =>
            [id, ext, svc, env, train, run, ver, strat, steps, state, started, finished, Now, Now, ws];
        Check(db7, Dep_Sql, true, "a deployment of a service to an environment for a release", Dep("d1"));
        Check(db7, Dep_Sql, false, "the same external id twice in a workspace", Dep("d2"), "UNIQUE");
        Check(db7, Dep_Sql, true, "the same external id in another workspace", Dep("d2", svc: "sv9", env: "e9", train: "t2", ws: "w2"));
        Check(db7, Dep_Sql, false, "a deployment of another workspace's service", Dep("d3", ext: "ci-3", svc: "sv9"), "service belongs to another workspace");
        Check(db7, Dep_Sql, false, "a deployment to another workspace's environment", Dep("d3", ext: "ci-3", env: "e9"), "environment belongs to another workspace");
        Check(db7, Dep_Sql, false, "a deployment for another workspace's release", Dep("d3", ext: "ci-3", train: "t2"), "train belongs to another workspace");
        Check(db7, Dep_Sql, false, "an unknown strategy", Dep("d3", ext: "ci-3", strat: "BlueGreen"), "CHECK");
        Check(db7, Dep_Sql, false, "canary steps on a rolling deployment", Dep("d3", ext: "ci-3", steps: "[5,50,100]"), "CHECK");
        Check(db7, Dep_Sql, false, "canary steps that are not JSON", Dep("d3", ext: "ci-3", strat: "Canary", steps: "5,50"), "CHECK");
        Check(db7, Dep_Sql, true, "canary steps as a JSON array", Dep("d3", ext: "ci-3", strat: "Canary", steps: "[5,50,100]"));
        Check(db7, Dep_Sql, false, "running without a start time", Dep("d4", ext: "ci-4", state: "Running"), "CHECK");
        Check(db7, Dep_Sql, false, "finished without a finish time", Dep("d4", ext: "ci-4", state: "Successful", started: Now), "CHECK");
        Check(db7, Dep_Sql, false, "a finish time on a queued deployment", Dep("d4", ext: "ci-4", finished: Now), "CHECK");
        Check(db7, "UPDATE Deployments SET TrafficPercent=101 WHERE Id='d1'", false, "traffic over 100 %", expect: "CHECK");
        Check(db7, "UPDATE Deployments SET State='Successful', StartedAt=?, FinishedAt=? WHERE Id='d1'", false, "queued straight to successful", [Now, Now], "Illegal deployment state transition");
        Check(db7, "UPDATE Deployments SET State='Running', StartedAt=? WHERE Id='d1'", true, "queued to running", [Now]);
        Check(db7, "UPDATE Deployments SET State='Paused' WHERE Id='d1'", true, "running to paused");
        Check(db7, "UPDATE Deployments SET State='Successful', FinishedAt=? WHERE Id='d1'", false, "paused straight to successful", [Now], "Illegal deployment state transition");
        Check(db7, "UPDATE Deployments SET State='Running' WHERE Id='d1'", true, "paused back to running");
        Check(db7, "UPDATE Deployments SET State='Successful', FinishedAt=? WHERE Id='d1'", true, "running to successful", [Now]);
        Check(db7, "UPDATE Deployments SET State='Running', FinishedAt=NULL WHERE Id='d1'", false, "successful back to running", expect: "Illegal deployment state transition");
        Check(db7, "UPDATE Deployments SET State='RolledBack' WHERE Id='d1'", true, "successful to rolled back (the finish time stays)");
        Check(db7, "UPDATE Deployments SET State='Failed' WHERE Id='d1'", false, "rolled back is final", expect: "Illegal deployment state transition");
        Check(db7, "UPDATE Deployments SET EnvironmentId='e2' WHERE Id='d1'", false, "a deployment moved to another environment", expect: "keeps its external id, service and environment");
        Check(db7, "UPDATE Deployments SET ReleaseTrainId='t2' WHERE Id='d1'", false, "a deployment linked to another workspace's release", expect: "train belongs to another workspace");
        Exec(db7, "INSERT INTO RunbookRuns(Id,ReleaseTrainId,Mode,StartedAt,StartedByUserId) VALUES('r1','t4','Rehearsal',?,'rte')", Now);
        Check(db7, "UPDATE Deployments SET RunId='r1' WHERE Id='d3'", false, "a deployment linked to a run of another release", expect: "run belongs to another train");
        Check(db7, "UPDATE Deployments SET ReleaseTrainId='t4', RunId='r1' WHERE Id='d3'", true, "a deployment linked to a release and its run");
        Check(db7, "INSERT INTO DeploymentEvents(DeploymentId,OccurredAt,ReceivedAt,Level,Message,ExternalId) VALUES('d3',?,?,'Info','Canary at 5 %','e-1')", true, "a deployment event", [Now, Now]);
        Check(db7, "INSERT INTO DeploymentEvents(DeploymentId,OccurredAt,ReceivedAt,Level,Message,ExternalId) VALUES('d3',?,?,'Info','again','e-1')", false, "an event id twice on a deployment", [Now, Now], "UNIQUE");
        Check(db7, "INSERT INTO DeploymentEvents(DeploymentId,OccurredAt,ReceivedAt,Level,Message) VALUES('d3',?,?,'Info','no id')", true, "an event without an id", [Now, Now]);
        Check(db7, "INSERT INTO DeploymentEvents(DeploymentId,OccurredAt,ReceivedAt,Level,Message) VALUES('d3',?,?,'Debug','x')", false, "an event level outside the three", [Now, Now], "CHECK");
        Check(db7, "INSERT INTO DeploymentEvents(DeploymentId,OccurredAt,ReceivedAt,Level,Message) VALUES('d3',?,?,'Info','')", false, "an empty event message", [Now, Now], "CHECK");
        Check(db7, "UPDATE DeploymentEvents SET Message='edited' WHERE Id=1", false, "an event edited", expect: "append-only");
        Check(db7, "DELETE FROM Deployments WHERE Id='d3'", true, "deleting a deployment removes its events with it");
        AssertTrue(Count(db7, "SELECT count(*) FROM DeploymentEvents") == 0, "no orphan events");
    }

    /// <summary>Oracle section "Telemetry, deployment control, approvals, audit chain" (REOS-111 to 114): one database, cases in order.</summary>
    [Fact]
    public void Telemetry_control_approvals_and_the_audit_chain()
    {
        using var db8 = fx.Fresh();
        Exec(db8, @"
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
UPDATE ReleaseTrains SET OwnerUserId='rm' WHERE Id='t1';");
        Check(db8, "INSERT INTO MetricSamples(DeploymentId,Series,SampledAt,ReceivedAt,ErrorRatePct,LatencyP95Ms) VALUES('d1','Canary','2026-10-20T14:00:00Z','2026-10-20T14:00:00Z',0.2,120)", true, "a metric sample");
        Check(db8, "INSERT INTO MetricSamples(DeploymentId,Series,SampledAt,ReceivedAt,ErrorRatePct) VALUES('d1','Canary','2026-10-20T14:00:00Z','2026-10-20T14:00:00Z',0.3)", false, "the same sample twice (deployment, series, time)", expect: "UNIQUE");
        Check(db8, "INSERT INTO MetricSamples(DeploymentId,Series,SampledAt,ReceivedAt,ErrorRatePct) VALUES('d1','Stable','2026-10-20T14:00:00Z','2026-10-20T14:00:00Z',0.1)", true, "the same time in the other series");
        Check(db8, "INSERT INTO MetricSamples(DeploymentId,Series,SampledAt,ReceivedAt) VALUES('d1','Canary','2026-10-20T14:00:30Z','2026-10-20T14:00:00Z')", false, "a sample with no metric at all", expect: "CHECK");
        Check(db8, "INSERT INTO MetricSamples(DeploymentId,Series,SampledAt,ReceivedAt,ErrorRatePct) VALUES('d1','Canary','2026-10-20T14:00:30Z','2026-10-20T14:00:00Z',120)", false, "an error rate over 100 %", expect: "CHECK");
        Check(db8, "INSERT INTO MetricSamples(DeploymentId,Series,SampledAt,ReceivedAt,LatencyP95Ms) VALUES('d1','Canary','2026-10-20T14:00:30Z','2026-10-20T14:00:00Z',-1)", false, "a negative latency", expect: "CHECK");
        Check(db8, "INSERT INTO MetricSamples(DeploymentId,Series,SampledAt,ReceivedAt,ErrorRatePct) VALUES('d1','Beta','2026-10-20T14:00:30Z','2026-10-20T14:00:00Z',1)", false, "a series outside canary and stable", expect: "CHECK");
        Check(db8, "INSERT INTO MetricSamples(DeploymentId,Series,SampledAt,ReceivedAt,ErrorRatePct,WorkspaceId) VALUES('d1','Canary','2026-10-20T14:00:30Z','2026-10-20T14:00:00Z',1,'w2')", false, "a sample filed under another workspace than its deployment", expect: "deployment belongs to another workspace");
        Check(db8, "UPDATE MetricSamples SET WorkspaceId='w2' WHERE Id=1", false, "a sample moved to another workspace", expect: "cannot move to another workspace");
        Check(db8, "INSERT INTO MetricRollups(DeploymentId,Series,BucketAt,Samples,ErrorRateAvgPct,ErrorRateMaxPct) VALUES('d1','Canary','2026-09-01T10:00:00Z',120,0.2,0.9)", true, "an hourly rollup");
        Check(db8, "INSERT INTO MetricRollups(DeploymentId,Series,BucketAt,Samples) VALUES('d1','Canary','2026-09-01T10:00:00Z',1)", false, "a rollup bucket twice", expect: "UNIQUE");
        Check(db8, "INSERT INTO MetricRollups(DeploymentId,Series,BucketAt,Samples) VALUES('d1','Stable','2026-09-01T10:00:00Z',0)", false, "a rollup of no samples", expect: "CHECK");
        Check(db8, "INSERT INTO MetricRollups(DeploymentId,Series,BucketAt,Samples,WorkspaceId) VALUES('d1','Stable','2026-09-01T10:00:00Z',1,'w2')", false, "a rollup filed under another workspace than its deployment", expect: "deployment belongs to another workspace");
        Check(db8, "UPDATE Environments SET ThresholdForSeconds=-1 WHERE Id='e2'", false, "a threshold duration below zero", expect: "CHECK");
        Check(db8, "UPDATE Environments SET CanaryObservationSeconds=5 WHERE Id='e2'", false, "a canary observation window under 30 seconds", expect: "CHECK");
        Check(db8, "UPDATE Environments SET ThresholdForSeconds=120, CanaryObservationSeconds=300 WHERE Id='e2'", true, "a threshold duration and observation window");
        Check(db8, "INSERT INTO ThresholdBreaches(Id,DeploymentId,Series,Metric,ThresholdValue,ForSeconds,StartedAt,PeakValue,LastValue) VALUES('b1','d1','Canary','ErrorRate',0.5,120,'2026-10-20T14:00:00Z',0.9,0.9)", true, "a threshold breach, watching");
        Check(db8, "INSERT INTO ThresholdBreaches(Id,DeploymentId,Series,Metric,ThresholdValue,ForSeconds,StartedAt,PeakValue,LastValue) VALUES('b2','d1','Canary','ErrorRate',0.5,120,'2026-10-20T14:00:00Z',0.9,0.9)", false, "a second open breach of one metric in one series", expect: "UNIQUE");
        Check(db8, "INSERT INTO ThresholdBreaches(Id,DeploymentId,Series,Metric,ThresholdValue,ForSeconds,StartedAt,PeakValue,LastValue) VALUES('b3','d1','Canary','LatencyP95',300,120,'2026-10-20T14:00:00Z',410,410)", true, "an open breach of another metric");
        Check(db8, "UPDATE ThresholdBreaches SET ConfirmedAt='2026-10-20T14:02:00Z' WHERE Id='b1'", false, "a breach confirmed without a detection time", expect: "CHECK");
        Check(db8, "UPDATE ThresholdBreaches SET ConfirmedAt='2026-10-20T14:02:00Z', DetectedAt='2026-10-20T14:02:01Z' WHERE Id='b1'", true, "a breach confirmed");
        Check(db8, "UPDATE ThresholdBreaches SET ResolvedAt='2026-10-20T13:00:00Z' WHERE Id='b1'", false, "a breach resolved before it started", expect: "CHECK");
        Check(db8, "UPDATE ThresholdBreaches SET ResolvedAt='2026-10-20T14:05:00Z' WHERE Id='b1'", true, "a breach resolved");
        Check(db8, "INSERT INTO ThresholdBreaches(Id,DeploymentId,Series,Metric,ThresholdValue,ForSeconds,StartedAt,PeakValue,LastValue) VALUES('b2','d1','Canary','ErrorRate',0.5,120,'2026-10-20T14:10:00Z',0.9,0.9)", true, "a new breach once the earlier one is resolved");
        Check(db8, "INSERT INTO ThresholdBreaches(Id,DeploymentId,Series,Metric,ThresholdValue,ForSeconds,StartedAt,PeakValue,LastValue,WorkspaceId) VALUES('b8','d1','Stable','ErrorRate',0.5,120,'2026-10-20T14:00:00Z',0.9,0.9,'w2')", false, "a breach filed under another workspace than its deployment", expect: "deployment belongs to another workspace");
        Check(db8, "INSERT INTO ThresholdBreaches(Id,DeploymentId,Series,Metric,ThresholdValue,ForSeconds,StartedAt,PeakValue,LastValue,WorkspaceId) VALUES('b9','d9','Canary','ErrorRate',0.5,120,'2026-10-20T14:00:00Z',0.9,0.9,'w2')", true, "a breach of the other workspace's deployment");
        Check(db8, "UPDATE ThresholdBreaches SET WorkspaceId='w2' WHERE Id='b2'", false, "a breach moved to another workspace", expect: "cannot move to another workspace");
        Check(db8, "INSERT INTO DeploymentControlActions(Id,DeploymentId,Action,Source,RequestedByUserId,RequestedAt,Reason) VALUES('ca1','d1','Pause','Manual','rte','2026-10-20T14:00:00Z','Error rate is climbing')", true, "a manual pause call");
        Check(db8, "INSERT INTO DeploymentControlActions(Id,DeploymentId,Action,Source,RequestedAt) VALUES('ca2','d1','Pause','Manual','2026-10-20T14:00:00Z')", false, "a manual call with nobody who asked", expect: "CHECK");
        Check(db8, "INSERT INTO DeploymentControlActions(Id,DeploymentId,Action,Source,RequestedByUserId,RequestedAt) VALUES('ca2','d1','Rollback','Automatic','rte','2026-10-20T14:00:00Z')", false, "an automatic call that names a person", expect: "CHECK");
        Check(db8, "INSERT INTO DeploymentControlActions(Id,DeploymentId,Action,Source,RequestedByUserId,RequestedAt) VALUES('ca2','d1','Abort','Manual','rte','2026-10-20T14:00:00Z')", false, "a control action outside pause, resume, promote and rollback", expect: "CHECK");
        Check(db8, "INSERT INTO DeploymentControlActions(Id,DeploymentId,Action,Source,RequestedByUserId,RequestedAt,Status,CompletedAt) VALUES('ca2','d1','Pause','Manual','rte','2026-10-20T14:00:00Z','Failed','2026-10-20T14:00:00Z')", false, "a failed call without its error", expect: "CHECK");
        Check(db8, "INSERT INTO DeploymentControlActions(Id,DeploymentId,Action,Source,RequestedByUserId,RequestedAt,CompletedAt) VALUES('ca2','d1','Pause','Manual','rte','2026-10-20T14:00:00Z','2026-10-20T14:00:00Z')", false, "a pending call that has a completion time", expect: "CHECK");
        Check(db8, "INSERT INTO DeploymentControlActions(Id,DeploymentId,Action,Source,RequestedByUserId,RequestedAt,Status,CompletedAt,ErrorMessage,HttpStatus) VALUES('ca3','d1','Resume','Manual','rte','2026-10-20T14:00:00Z','Failed','2026-10-20T14:00:00Z','HTTP 503',503)", true, "a failed call with its error");
        Check(db8, "INSERT INTO DeploymentControlActions(Id,DeploymentId,Action,Source,RequestedByUserId,RequestedAt,BreachId) VALUES('ca2','d1','Rollback','Manual','rte','2026-10-20T14:00:00Z','b2')", false, "a breach named on a manual call", expect: "CHECK");
        Check(db8, "INSERT INTO DeploymentControlActions(Id,DeploymentId,Action,Source,RequestedAt,BreachId) VALUES('ca4','d1','Rollback','Automatic','2026-10-20T14:00:00Z','b2')", true, "an automatic rollback for a breach");
        Check(db8, "INSERT INTO DeploymentControlActions(Id,DeploymentId,Action,Source,RequestedAt,BreachId) VALUES('ca5','d1','Rollback','Automatic','2026-10-20T14:00:00Z','b2')", false, "a second automatic rollback for the same breach", expect: "UNIQUE");
        Check(db8, "INSERT INTO DeploymentControlActions(Id,DeploymentId,Action,Source,RequestedAt,BreachId) VALUES('ca5','d1','Rollback','Automatic','2026-10-20T14:00:00Z','b9')", false, "an automatic rollback naming another deployment's breach", expect: "breach belongs to another deployment");
        Check(db8, "INSERT INTO DeploymentControlActions(Id,DeploymentId,Action,Source,RequestedByUserId,RequestedAt,WorkspaceId) VALUES('ca5','d1','Pause','Manual','rte','2026-10-20T14:00:00Z','w2')", false, "a control action filed under another workspace than its deployment", expect: "deployment belongs to another workspace");
        Check(db8, "UPDATE DeploymentControlActions SET WorkspaceId='w2' WHERE Id='ca1'", false, "a control action moved to another workspace", expect: "cannot move to another workspace");
        Check(db8, "UPDATE Deployments SET AutoRollbackArmedAt='2026-10-20T14:00:00Z', StepStartedAt='2026-10-20T14:00:00Z' WHERE Id='d1'", true, "a deployment armed for automatic rollback");
        Exec(db8, @"
INSERT INTO RunbookRuns(Id,ReleaseTrainId,Mode,StartedAt,StartedByUserId) VALUES('r1','t1','Rehearsal','2026-10-20T14:00:00Z','rte');
INSERT INTO StepExecutions(Id,RunId,StepId) VALUES('x1','r1','s1'),('x2','r1','s2');");
        Check(db8, "UPDATE RunbookRuns SET PausedAt='2026-10-20T14:00:00Z', PauseReason='Hold for the incident' WHERE Id='r1'", false, "a run paused with nobody who paused it", expect: "CHECK");
        Check(db8, "UPDATE RunbookRuns SET PausedAt='2026-10-20T14:00:00Z', PausedByUserId='rte' WHERE Id='r1'", false, "a run paused without a reason", expect: "CHECK");
        Check(db8, "UPDATE RunbookRuns SET PausedAt='2026-10-20T14:00:00Z', PausedByUserId='rte', PauseReason='Hold for the incident' WHERE Id='r1'", true, "a run paused");
        Check(db8, "UPDATE StepExecutions SET Status='Running', ActualStartAt='2026-10-20T14:00:00Z', ActorUserId='rte' WHERE Id='x1'", false, "a step started in a paused run", expect: "run is paused");
        Check(db8, "UPDATE StepExecutions SET Status='Running', ActualStartAt='2026-10-20T14:00:00Z', ActorUserId='rte' WHERE Id='x2'", false, "a second step started in a paused run", expect: "run is paused");
        Check(db8, "UPDATE StepExecutions SET Status='Skipped', Note='not needed' WHERE Id='x2'", true, "a step skipped in a paused run (not a start)");
        Check(db8, "UPDATE RunbookRuns SET PausedAt=NULL, PausedByUserId=NULL, PauseReason=NULL WHERE Id='r1'", true, "a run resumed");
        Check(db8, "UPDATE StepExecutions SET Status='Running', ActualStartAt='2026-10-20T14:00:00Z', ActorUserId='rte' WHERE Id='x1'", true, "a step started once the run is resumed");
        Check(db8, "UPDATE RunbookRuns SET EndedAt='2026-10-20T14:00:00Z', Outcome='Aborted', PausedAt='2026-10-20T14:00:00Z', PausedByUserId='rte', PauseReason='x' WHERE Id='r1'", false, "a run that has ended and is still paused", expect: "CHECK");
        Exec(db8, @"
INSERT INTO GateWaivers(Id,StageGateId,Reason,RequestedByUserId,RequestedAt) VALUES
 ('wv1','g1','This gate cannot be met before the freeze','gov2','2026-10-20T14:00:00Z'),('wv2','g1','This gate cannot be met before the freeze','rte','2026-10-20T14:00:00Z'),('wv3','g1','This gate cannot be met before the freeze','rte','2026-10-20T14:00:00Z');");
        Check(db8, "UPDATE GateWaivers SET DeclinedByUserId='rm', DeclinedAt='2026-10-20T14:00:00Z', DeclineNote='No' WHERE Id='wv2'", false, "a waiver declined by a Release Manager", expect: "declined by a Governance Officer");
        Check(db8, "UPDATE GateWaivers SET DeclinedByUserId='gov2', DeclinedAt='2026-10-20T14:00:00Z', DeclineNote='Withdrawn' WHERE Id='wv1'", false, "a waiver declined by the person who asked for it", expect: "two distinct people");
        Check(db8, "UPDATE GateWaivers SET DeclinedByUserId='gov1', DeclinedAt='2026-10-20T14:00:00Z' WHERE Id='wv2'", false, "a waiver declined without a note", expect: "CHECK");
        Check(db8, "UPDATE GateWaivers SET DeclinedByUserId='gov1', DeclinedAt='2026-10-20T14:00:00Z', DeclineNote='The risk is not covered' WHERE Id='wv2'", true, "a waiver declined with a note");
        Check(db8, "UPDATE GateWaivers SET ApprovedByUserId='gov1', ApprovedAt='2026-10-20T14:00:00Z' WHERE Id='wv2'", false, "a declined waiver approved", expect: "approved or declined");
        Check(db8, "UPDATE GateWaivers SET ApprovedByUserId='gov1', ApprovedAt='2026-10-20T14:00:00Z' WHERE Id='wv3'", true, "a waiver approved");
        Check(db8, "UPDATE GateWaivers SET DeclinedByUserId='gov2', DeclinedAt='2026-10-20T14:00:00Z', DeclineNote='Changed my mind' WHERE Id='wv3'", false, "an approved waiver declined", expect: "approved or declined");
        Check(db8, "INSERT INTO SyncAlerts(Id,SourceSystem,Kind,Fingerprint,ErrorMessage,FirstOccurredAt,LastOccurredAt) VALUES('al1','Deployment','ControlFailed','fp1','The rollback call failed','2026-10-20T14:00:00Z','2026-10-20T14:00:00Z')", true, "an alert from the deployment side");
        Check(db8, "INSERT INTO SyncAlerts(Id,SourceSystem,Kind,Fingerprint,ErrorMessage,FirstOccurredAt,LastOccurredAt) VALUES('al2','Audit','IntegrityFailed','fp2','Audit row 5 does not verify','2026-10-20T14:00:00Z','2026-10-20T14:00:00Z')", true, "an alert from the audit chain");
        Check(db8, "INSERT INTO SyncAlerts(Id,SourceSystem,Kind,Fingerprint,ErrorMessage,FirstOccurredAt,LastOccurredAt) VALUES('al3','Telemetry','RetentionFailed','fp3','Summarising failed','2026-10-20T14:00:00Z','2026-10-20T14:00:00Z')", true, "an alert from telemetry retention");
        Check(db8, "INSERT INTO SyncAlerts(Id,SourceSystem,Kind,Fingerprint,ErrorMessage,FirstOccurredAt,LastOccurredAt) VALUES('al4','Pager','ControlFailed','fp4','x','2026-10-20T14:00:00Z','2026-10-20T14:00:00Z')", false, "an alert from a source that does not exist", expect: "CHECK");
        Check(db8, "INSERT INTO ApprovalPolicies(Id,Name,EnvironmentKind,MinRiskTier,RequireGatesCertified,CreatedAt) VALUES('pol1','Production','Production','High',1,'2026-10-20T14:00:00Z')", true, "an approval policy");
        Check(db8, "INSERT INTO ApprovalPolicies(Id,Name,CreatedAt) VALUES('pol2','production','2026-10-20T14:00:00Z')", false, "a policy name twice in a workspace (any case)", expect: "UNIQUE");
        Check(db8, "INSERT INTO ApprovalPolicies(Id,Name,CreatedAt,WorkspaceId) VALUES('pol9','Production','2026-10-20T14:00:00Z','w2')", true, "the same policy name in another workspace");
        Check(db8, "INSERT INTO ApprovalPolicies(Id,Name,EnvironmentKind,CreatedAt) VALUES('pol2','Dev','Dev','2026-10-20T14:00:00Z')", false, "a policy for an environment kind that does not exist", expect: "CHECK");
        Check(db8, "INSERT INTO ApprovalPolicies(Id,Name,MinRiskTier,CreatedAt) VALUES('pol2','Risky','Severe','2026-10-20T14:00:00Z')", false, "a policy for a risk tier that does not exist", expect: "CHECK");
        Check(db8, "UPDATE ApprovalPolicies SET WorkspaceId='w2' WHERE Id='pol1'", false, "a policy moved to another workspace", expect: "cannot move to another workspace");
        Check(db8, "INSERT INTO ApprovalPolicyRequirements(Id,PolicyId,Label,EligibleKind,EligibleValue,DueHours,SortOrder) VALUES('rq1','pol1','Security reviewer','Team','tm1',24,1)", true, "a policy seat for a team");
        Check(db8, "INSERT INTO ApprovalPolicyRequirements(Id,PolicyId,Label,EligibleKind,DueHours,SortOrder) VALUES('rq2','pol1','Release owner','ReleaseOwner',24,2)", true, "a policy seat for the release owner");
        Check(db8, "INSERT INTO ApprovalPolicyRequirements(Id,PolicyId,Label,EligibleKind,EligibleValue,DueHours,SortOrder) VALUES('rq3','pol1','SRE approver','WorkspaceRole','RTE',8,3)", true, "a policy seat for a workspace role");
        Check(db8, "INSERT INTO ApprovalPolicyRequirements(Id,PolicyId,Label,EligibleKind,EligibleValue) VALUES('rq4','pol1','EU security','Team','tm2')", false, "a policy seat for another workspace's team", expect: "team belongs to another workspace");
        Check(db8, "INSERT INTO ApprovalPolicyRequirements(Id,PolicyId,Label,EligibleKind,EligibleValue) VALUES('rq4','pol1','SRE','WorkspaceRole','SRE')", false, "a policy seat for a role that does not exist", expect: "CHECK");
        Check(db8, "INSERT INTO ApprovalPolicyRequirements(Id,PolicyId,Label,EligibleKind,EligibleValue) VALUES('rq4','pol1','Owner again','ReleaseOwner','rm')", false, "a release owner seat that names a value", expect: "CHECK");
        Check(db8, "INSERT INTO ApprovalPolicyRequirements(Id,PolicyId,Label,EligibleKind,EligibleValue,DueHours) VALUES('rq4','pol1','Quick','WorkspaceRole','RTE',0)", false, "a policy seat due in 0 hours", expect: "CHECK");
        Check(db8, "INSERT INTO ApprovalPolicyRequirements(Id,PolicyId,Label,EligibleKind,EligibleValue) VALUES('rq4','pol1','sre approver','WorkspaceRole','RTE')", false, "a seat label twice in a policy (any case)", expect: "UNIQUE");
        Check(db8, "INSERT INTO Approvals(Id,ReleaseTrainId,EnvironmentId,Status,RequestedByUserId,RequestedAt,DueAt) VALUES('ap0','t1','e2','Approved','gov2','2026-10-20T14:00:00Z','2026-10-21T14:00:00Z')", false, "an approval that is approved without a decision time", expect: "CHECK");
        Check(db8, "INSERT INTO Approvals(Id,ReleaseTrainId,EnvironmentId,RequestedByUserId,RequestedAt,DueAt) VALUES('ap1','t1','e2','gov2','2026-10-20T14:00:00Z','2026-10-21T14:00:00Z')", true, "an approval requested for a release and environment");
        Check(db8, "INSERT INTO Approvals(Id,ReleaseTrainId,EnvironmentId,RequestedByUserId,RequestedAt,DueAt) VALUES('ap2','t1','e2','gov2','2026-10-20T14:00:00Z','2026-10-21T14:00:00Z')", false, "a second open approval for the same release and environment", expect: "UNIQUE");
        Check(db8, "INSERT INTO Approvals(Id,ReleaseTrainId,EnvironmentId,RequestedByUserId,RequestedAt,DueAt) VALUES('ap2','t1','e1','gov2','2026-10-20T14:00:00Z','2026-10-21T14:00:00Z')", true, "an open approval for the same release in another environment");
        Check(db8, "INSERT INTO Approvals(Id,ReleaseTrainId,EnvironmentId,RequestedByUserId,RequestedAt,DueAt) VALUES('ap3','t1','e9','gov2','2026-10-20T14:00:00Z','2026-10-21T14:00:00Z')", false, "an approval for another workspace's environment", expect: "environment belongs to another workspace");
        Check(db8, "INSERT INTO Approvals(Id,ReleaseTrainId,EnvironmentId,RequestedByUserId,RequestedAt,DueAt) VALUES('ap3','t2','e2','gov2','2026-10-20T14:00:00Z','2026-10-21T14:00:00Z')", false, "an approval for another workspace's release", expect: "train belongs to another workspace");
        Check(db8, "UPDATE Approvals SET EnvironmentId='e1' WHERE Id='ap1'", false, "an approval moved to another environment", expect: "keeps its release and environment");
        Exec(db8, @"
INSERT INTO ApprovalDecisions(Id,ApprovalId,Label,EligibleKind,EligibleValue,DueAt) VALUES
 ('dc1','ap1','Security reviewer','Team','tm1','2026-10-21T14:00:00Z'),('dc2','ap1','Release owner','ReleaseOwner',NULL,'2026-10-21T14:00:00Z'),
 ('dc3','ap1','SRE approver','WorkspaceRole','RTE','2026-10-20T22:00:00Z'),('dc4','ap1','Second reviewer','Team','tm1','2026-10-21T14:00:00Z');");
        Check(db8, "UPDATE ApprovalDecisions SET Decision='ChangesRequested', DecidedByUserId='dev', DecidedAt='2026-10-20T14:00:00Z' WHERE Id='dc1'", false, "Request changes without a note", expect: "CHECK");
        Check(db8, "UPDATE ApprovalDecisions SET Decision='Approved', DecidedAt='2026-10-20T14:00:00Z' WHERE Id='dc1'", false, "a decision that names no decider", expect: "Not an eligible approver");
        Check(db8, "UPDATE ApprovalDecisions SET Decision='Approved', DecidedByUserId='rte', DecidedAt='2026-10-20T14:00:00Z' WHERE Id='dc1'", false, "an approval by a workspace member who does not hold the seat", expect: "Not an eligible approver");
        Check(db8, "UPDATE ApprovalDecisions SET Decision='Approved', DecidedByUserId='gov2', DecidedAt='2026-10-20T14:00:00Z' WHERE Id='dc3'", false, "an approval by the person who requested the change", expect: "you cannot approve a change you requested");
        Check(db8, "UPDATE ApprovalDecisions SET Decision='Approved', DecidedByUserId='rm', DecidedAt='2026-10-20T14:00:00Z' WHERE Id='dc1'", false, "the release owner approving a Production seat that is not theirs", expect: "release owner approves only the release owner seat");
        Check(db8, "UPDATE ApprovalDecisions SET Decision='Approved', DecidedByUserId='rm', DecidedAt='2026-10-20T14:00:00Z' WHERE Id='dc2'", true, "the release owner approving the release owner seat");
        Check(db8, "UPDATE ApprovalDecisions SET Decision='Approved', DecidedByUserId='dev', DecidedAt='2026-10-20T14:00:00Z' WHERE Id='dc1'", true, "a team member approving the security seat");
        Check(db8, "UPDATE ApprovalDecisions SET Decision='Approved', DecidedByUserId='dev', DecidedAt='2026-10-20T14:00:00Z' WHERE Id='dc4'", false, "one person approving a second seat of a Production change", expect: "two seats on a Production change");
        Check(db8, "UPDATE ApprovalDecisions SET Decision='ChangesRequested', DecidedByUserId='dev', DecidedAt='2026-10-20T14:00:00Z', Note='Changed my mind' WHERE Id='dc1'", false, "a decision changed once made", expect: "A decision is final");
        Check(db8, "UPDATE Approvals SET Status='Approved', DecidedAt='2026-10-20T14:00:00Z' WHERE Id='ap1'", false, "an approval approved while seats are open", expect: "Every required approver must approve first");
        Check(db8, "UPDATE ApprovalDecisions SET Decision='ChangesRequested', DecidedByUserId='rte', DecidedAt='2026-10-20T14:00:00Z', Note='Rollback plan is missing' WHERE Id='dc3'", true, "Request changes with a note");
        Check(db8, "UPDATE Approvals SET Status='ChangesRequested', Version=Version+1 WHERE Id='ap1'", true, "the approval marked as changes requested");
        Check(db8, "UPDATE ApprovalDecisions SET Decision=NULL, DecidedByUserId=NULL, DecidedAt=NULL, Note=NULL WHERE Id='dc3'", true, "a seat reset for a new round");
        Check(db8, "UPDATE Approvals SET Status='Pending', Round=2 WHERE Id='ap1'", true, "the approval back to pending for the new round");
        Check(db8, "UPDATE ApprovalDecisions SET Decision='Approved', DecidedByUserId='gov1', DecidedAt='2026-10-20T14:00:00Z' WHERE Id='dc4'", true, "a second team member approving the other team seat");
        Check(db8, "UPDATE ApprovalDecisions SET Decision='Approved', DecidedByUserId='rte', DecidedAt='2026-10-20T14:00:00Z' WHERE Id='dc3'", true, "the SRE seat approved by an RTE who did not request it");
        Check(db8, "UPDATE Approvals SET Status='Approved', DecidedAt='2026-10-20T14:00:00Z' WHERE Id='ap1'", true, "the approval approved once every seat has");
        Check(db8, "UPDATE Approvals SET Status='Pending', DecidedAt=NULL WHERE Id='ap1'", false, "an approved approval reopened", expect: "final");
        Check(db8, "UPDATE ApprovalDecisions SET Note='late edit' WHERE Id='dc1'", false, "a decision changed after the approval closed", expect: "The approval is closed");
        Exec(db8, @"
INSERT INTO AuditEvents(OccurredAt,EntityType,EntityId,Action,WorkspaceId) VALUES ('2026-10-20T14:00:00Z','Train','t1','Probe','default'),('2026-10-20T14:00:00Z','Train','t1','Probe2','default');");
        Check(db8, "UPDATE AuditEvents SET Hash=printf('%064d',1) WHERE Action='Probe'", false, "a hash without the previous hash", expect: "append-only");
        Check(db8, "UPDATE AuditEvents SET PrevHash='xyz', Hash=printf('%064d',1) WHERE Action='Probe'", false, "a hash that is not 64 hex characters", expect: "CHECK");
        Check(db8, "UPDATE AuditEvents SET Action='Edited' WHERE Action='Probe'", false, "an audit row edited", expect: "append-only");
        Check(db8, "UPDATE AuditEvents SET Action='Edited', PrevHash=printf('%064d',0), Hash=printf('%064d',1) WHERE Action='Probe'", false, "an audit row edited while it is sealed", expect: "append-only");
        Check(db8, "UPDATE AuditEvents SET PrevHash=printf('%064d',0), Hash=printf('%064d',1) WHERE Action='Probe'", true, "an audit row sealed once");
        Check(db8, "UPDATE AuditEvents SET PrevHash=printf('%064d',0), Hash=printf('%064d',2) WHERE Action='Probe'", false, "a sealed audit row sealed again", expect: "append-only");
        Check(db8, "UPDATE AuditEvents SET PrevHash=NULL, Hash=NULL WHERE Action='Probe'", false, "a sealed audit row's hash cleared", expect: "append-only");
        Check(db8, "DELETE FROM AuditEvents WHERE Action='Probe'", false, "a sealed audit row deleted", expect: "append-only");
        Check(db8, "DELETE FROM AuditEvents WHERE Action='Probe2'", false, "an unsealed audit row deleted", expect: "append-only");
        Check(db8, "INSERT INTO AuditChainHeads(ChainId,LastId,LastHash,SealedCount,SealedAt) VALUES('default',1,printf('%064d',1),1,'2026-10-20T14:00:00Z')", true, "the audit chain's head recorded");
        Check(db8, "INSERT INTO AuditChainHeads(ChainId,LastId,LastHash,SealedCount,SealedAt) VALUES('default',2,printf('%064d',2),2,'2026-10-20T14:00:00Z')", false, "a chain head twice", expect: "UNIQUE");
        Check(db8, "UPDATE AuditChainHeads SET VerifiedStatus='Verified' WHERE ChainId='default'", false, "a verification status without its time", expect: "CHECK");
        Check(db8, "UPDATE AuditChainHeads SET VerifiedStatus='Maybe', VerifiedAt='2026-10-20T14:00:00Z' WHERE ChainId='default'", false, "a verification status that is neither verified nor failed", expect: "CHECK");
        Check(db8, "UPDATE AuditChainHeads SET VerifiedStatus='Failed', VerifiedAt='2026-10-20T14:00:00Z', FirstBadId=1, FirstBadReason='Row 1 does not verify' WHERE ChainId='default'", true, "a verification recorded");
    }

    /// <summary>REOS-116 (D46, D48): a type on every release once the workspace has types; a Beta type carries a number 1 to 3. Port of the oracle's last section.</summary>
    [Fact]
    public void Release_types_a_type_on_every_release_and_a_Beta_number()
    {
        using var db9 = fx.Fresh();
        Exec(db9, "INSERT INTO Workspaces(Id,Name,Slug,CreatedAt) VALUES('w2','EU','eu','2026-10-20T14:00:00Z'),('w3','NoTypes','notypes','2026-10-20T14:00:00Z')");
        const string T = "INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,CreatedAt,UpdatedAt,ReleaseTypeId,BetaNumber,WorkspaceId) VALUES(?,?,'2026-11-30',?,?,?,?,?)";
        Check(db9, T, true, "a release in a workspace with no types needs none yet", ["n1", "x", Now, Now, null, null, "default"]);
        Exec(db9, "INSERT INTO ReleaseTypes(Id,Name,SortOrder,IsBeta) VALUES('tyB','Beta',10,1),('tyC','CERT',20,0),('tyP','Production',40,0)");
        Exec(db9, "INSERT INTO ReleaseTypes(Id,Name,WorkspaceId) VALUES('tyX','Production','w2')");
        Check(db9, T, false, "a release created without a type once the workspace has types", ["n2", "x", Now, Now, null, null, "default"], "needs a release type");
        Check(db9, T, true, "a release created with the type of its own workspace", ["n3", "x", Now, Now, "tyX", null, "w2"]);
        Check(db9, T, true, "a release in a workspace that still has no types", ["n4", "x", Now, Now, null, null, "w3"]);
        Check(db9, T, true, "a Production release", ["n5", "x", Now, Now, "tyP", null, "default"]);
        Check(db9, T, false, "a Production release with a Beta number", ["n6", "x", Now, Now, "tyP", 2, "default"], "Only a Beta release carries a Beta number");
        Check(db9, T, false, "a Beta release without its number", ["n7", "x", Now, Now, "tyB", null, "default"], "needs its number");
        Check(db9, T, false, "a Beta release numbered 4", ["n8", "x", Now, Now, "tyB", 4, "default"], "CHECK");
        Check(db9, T, false, "a Beta release numbered 0", ["n9", "x", Now, Now, "tyB", 0, "default"], "CHECK");
        Check(db9, T, true, "Beta 1", ["b1", "x", Now, Now, "tyB", 1, "default"]);
        Check(db9, T, true, "Beta 3", ["b3", "x", Now, Now, "tyB", 3, "default"]);
        Check(db9, "UPDATE ReleaseTrains SET ReleaseTypeId=NULL WHERE Id='n5'", false, "a release that loses its type", expect: "cannot lose its release type");
        Check(db9, "UPDATE ReleaseTrains SET ReleaseTypeId='tyC' WHERE Id='b1'", false, "a Beta release changed to CERT with its number kept", expect: "Only a Beta release carries a Beta number");
        Check(db9, "UPDATE ReleaseTrains SET ReleaseTypeId='tyC', BetaNumber=NULL WHERE Id='b1'", true, "a Beta release changed to CERT with the number cleared");
        Check(db9, "UPDATE ReleaseTrains SET ReleaseTypeId='tyB' WHERE Id='b1'", false, "a release changed to Beta without a number", expect: "needs its number");
        Check(db9, "UPDATE ReleaseTrains SET ReleaseTypeId='tyB', BetaNumber=2 WHERE Id='b1'", true, "a release changed to Beta 2");
        Check(db9, "UPDATE ReleaseTrains SET BetaNumber=NULL WHERE Id='b3'", false, "a Beta release's number cleared", expect: "needs its number");
        Check(db9, "UPDATE ReleaseTrains SET BetaNumber=2 WHERE Id='n5'", false, "a number set on a Production release", expect: "Only a Beta release carries a Beta number");
        Check(db9, "UPDATE ReleaseTrains SET Title='renamed' WHERE Id='n1'", true, "a release that predates types edited in another field");
        Check(db9, "UPDATE ReleaseTrains SET ReleaseTypeId='tyP' WHERE Id='n1'", true, "a release that predates types given its type");
        Check(db9, "UPDATE ReleaseTypes SET IsBeta=1 WHERE Id='tyP'", false, "a type in use made a Beta type", expect: "cannot become or stop being a Beta type");
        Check(db9, "UPDATE ReleaseTypes SET IsBeta=0 WHERE Id='tyB'", false, "a Beta type in use stops being one", expect: "cannot become or stop being a Beta type");
        Check(db9, "INSERT INTO ReleaseTypes(Id,Name,IsBeta) VALUES('tyD','Spare',1)", true, "a Beta type with no releases");
        Check(db9, "UPDATE ReleaseTypes SET IsBeta=0 WHERE Id='tyD'", true, "an unused type stops being a Beta type");
        Check(db9, "INSERT INTO ReleaseTypes(Id,Name,IsBeta) VALUES('tyE','Odd',2)", false, "a Beta flag that is not 0 or 1", expect: "CHECK");
    }

    /// <summary>REOS-117 (RT2, D48): a version groups the releases of one product version; one live release per type (and Beta number) in it. Port of the oracle's version section.</summary>
    [Fact]
    public void Versions_link_releases_one_place_per_type_and_stay_in_their_workspace()
    {
        using var db10 = fx.Fresh();
        Exec(db10, "INSERT INTO Workspaces(Id,Name,Slug,CreatedAt) VALUES('w2','EU','eu','2026-10-20T14:00:00Z')");
        Exec(db10, "INSERT INTO ReleaseTypes(Id,Name,SortOrder,IsBeta) VALUES('tyB','Beta',10,1),('tyC','CERT',20,0),('tyP','Production',40,0)");
        Exec(db10, "INSERT INTO ReleaseTypes(Id,Name,WorkspaceId) VALUES('tyX','Production','w2')");
        const string V = "INSERT INTO ReleaseVersions(Id,Name,CreatedAt,WorkspaceId) VALUES(?,?,?,?)";
        Check(db10, V, true, "a version", ["v1", "Orion 5.2", Now, "default"]);
        Check(db10, V, false, "a version name twice in a workspace (any case)", ["v2", "ORION 5.2", Now, "default"], "UNIQUE");
        Check(db10, V, true, "the same version name in another workspace", ["v3", "Orion 5.2", Now, "w2"]);
        Check(db10, V, false, "a blank version name", ["v4", "  ", Now, "default"], "CHECK");
        Check(db10, "UPDATE ReleaseVersions SET WorkspaceId='w2' WHERE Id='v1'", false, "a version moved to another workspace", expect: "cannot move to another workspace");
        const string T = "INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,CreatedAt,UpdatedAt,ReleaseTypeId,BetaNumber,ReleaseVersionId,WorkspaceId,CurrentStatus) VALUES(?,?,'2026-11-30',?,?,?,?,?,?,?)";
        Check(db10, T, true, "Beta 1 of a version", ["r1", "a", Now, Now, "tyB", 1, "v1", "default", "Planning"]);
        Check(db10, T, true, "Beta 2 of the same version", ["r2", "b", Now, Now, "tyB", 2, "v1", "default", "Planning"]);
        Check(db10, T, true, "CERT of the same version", ["r3", "c", Now, Now, "tyC", null, "v1", "default", "Planning"]);
        Check(db10, T, false, "a second Beta 1 in the same version", ["r4", "d", Now, Now, "tyB", 1, "v1", "default", "Planning"], "UNIQUE");
        Check(db10, T, false, "a second CERT in the same version", ["r4", "d", Now, Now, "tyC", null, "v1", "default", "Gated"], "UNIQUE");
        Check(db10, T, true, "a CERT outside any version", ["r5", "e", Now, Now, "tyC", null, null, "default", "Planning"]);
        Check(db10, T, true, "another CERT outside any version", ["r6", "f", Now, Now, "tyC", null, null, "default", "Planning"]);
        Check(db10, T, false, "a release in a version without a type", ["r7", "g", Now, Now, null, null, "v1", "default", "Planning"], "needs a release type");
        Check(db10, T, false, "a release in another workspace's version", ["r8", "h", Now, Now, "tyX", null, "v1", "w2", "Planning"], "version belongs to another workspace");
        Check(db10, "UPDATE ReleaseTrains SET CurrentStatus='Aborted', ActualEndAt=NULL WHERE Id='r3'", true, "a release in a version aborted");
        Check(db10, T, true, "a new CERT once the first one was aborted", ["r9", "i", Now, Now, "tyC", null, "v1", "default", "Planning"]);
        Check(db10, "UPDATE ReleaseTrains SET ReleaseVersionId='v1' WHERE Id='r5'", false, "a second live CERT linked into the version", expect: "UNIQUE");
        Check(db10, "UPDATE ReleaseTrains SET ReleaseVersionId='v3' WHERE Id='r5'", false, "a release linked to another workspace's version", expect: "version belongs to another workspace");
        Check(db10, "UPDATE ReleaseTrains SET ReleaseVersionId=NULL WHERE Id='r2'", true, "a release unlinked from its version");
        Check(db10, "UPDATE ReleaseTrains SET BetaNumber=1 WHERE Id='r2'", true, "a Beta release renumbered while outside a version");
        Check(db10, "UPDATE ReleaseTrains SET ReleaseVersionId='v1' WHERE Id='r2'", false, "a release linked into a place a live release holds", expect: "UNIQUE");
        Check(db10, "UPDATE ReleaseTrains SET BetaNumber=3 WHERE Id='r2'", true, "a release given a free number");
        Check(db10, "UPDATE ReleaseTrains SET ReleaseVersionId='v1' WHERE Id='r2'", true, "a release linked into a free place");
        Check(db10, "INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,CreatedAt,UpdatedAt,ReleaseVersionId) VALUES('r10','j','2026-11-30',?,?,'v1')", false, "a release linked to a version at insert without a type", [Now, Now], "needs a release type");
    }
}
