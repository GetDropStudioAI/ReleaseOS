using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Reminders;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Infrastructure.Tests;

/// <summary>REOS-80: a new train blank, from an Approved template, or cloned from a prior train, against the migrated schema (fixture train t1, see TriggerSuiteFixture).</summary>
public sealed class TrainCreationServiceTests(TriggerSuiteFixture fx) : IClassFixture<TriggerSuiteFixture>
{
    private sealed class Factory(string path) : IDbContextFactory<ReleaseDbContext>
    {
        public ReleaseDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ReleaseDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").AddInterceptors(new SqliteConnectionInterceptor()).Options);
    }

    private sealed class Env(string path)
    {
        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 20, 14, 0, 0, TimeSpan.Zero));
        public TrainCreationService Svc => new(new Factory(path), Time, new DisplayClock(TimeZoneInfo.FindSystemTimeZoneById("America/Chicago")));

        public void Sql(string sql, params object?[] p) { using var c = TriggerSuiteFixture.Open(path); TriggerSuiteFixture.Run(c, sql, p); }

        public List<object?[]> Rows(string sql, params object?[] p)
        {
            using var c = TriggerSuiteFixture.Open(path);
            using var cmd = c.CreateCommand();
            TriggerSuiteFixture.Bind(cmd, sql, p);
            using var r = cmd.ExecuteReader();
            var rows = new List<object?[]>();
            while (r.Read()) { var row = new object?[r.FieldCount]; r.GetValues(row!); rows.Add([.. row.Select(v => v is DBNull ? null : v)]); }
            return rows;
        }

        public object? One(string sql, params object?[] p) => Rows(sql, p)[0][0];
        public long Count(string sql, params object?[] p) => Convert.ToInt64(One(sql, p));
    }

    private static readonly Actor Rm = new("rm");

    private Env NewEnv()
    {
        var e = new Env(fx.FreshPath());
        // Thanksgiving: gate due dates skip it (D9).
        e.Sql("INSERT INTO Holidays(Day,Name) VALUES('2026-11-26','Thanksgiving')");
        return e;
    }

    /// <summary>A template with a team-owned gate, a gate with no team, two steps and a T-minus plan of one library message.</summary>
    private static void SeedTemplate(Env e, string status)
    {
        var approved = status == "Approved";
        e.Sql($@"
            INSERT INTO Teams(Id,Handle,Name) VALUES('team1','platform','Platform');
            INSERT INTO TrainTemplates(Id,Name,Status,DefaultRiskTier,ApprovedByUserId,ApprovedAt) VALUES('tpl','Quarterly','{status}','VeryHigh',{(approved ? "'rm','2026-10-01T00:00:00Z'" : "NULL,NULL")});
            INSERT INTO TemplateGates(Id,TemplateId,GateName,GateClass,SequenceOrder,OffsetDays,RequiredBeforeStatus,OwnerTeamId) VALUES
              ('tg1','tpl','Code Freeze','Standard',1,3,'Gated','team1'),
              ('tg2','tpl','CAB Approval','Compliance',2,1,'Executing',NULL);
            INSERT INTO TemplateSteps(Id,TemplateId,StepCode,Section,Title,OffsetMinutes,PlannedDurationMin,OwnerTeamId) VALUES
              ('ts1','tpl','PRE-1','PreCheck','Health check',-30,15,NULL),
              ('ts2','tpl','DEP-1','Deploy','Deploy',0,45,'team1');
            INSERT INTO CommTemplateLibrary(Id,TemplateType,Name,Audience,SubjectLine,MarkdownBody) VALUES('lib1','Tminus','T-3 Checkpoint','All','Checkpoint','Body');
            INSERT INTO TemplateCommSchedule(Id,TemplateId,LibraryTemplateId,OffsetDays) VALUES('tcs1','tpl','lib1',-3);");
    }

    [Fact]
    public async Task Blank_train_is_Planning_with_its_products_and_one_audit_row_per_created_row()
    {
        var e = NewEnv();
        var r = await e.Svc.CreateAsync(new NewTrainInput("R26.12 Winter", "2026-12-04", "Low", Products: [new("Payments API", "5.0.0", "PAY"), new("Card Portal", "3.0.0", "CRD")]), Rm);
        Assert.True(r.IsOk, string.Join("; ", r.Failures.Select(f => f.Message)));
        var id = r.Value!.Id;
        Assert.Equal("Blank", r.Value.Source);
        Assert.Equal(["Planning", "Low", "2026-12-04", null, null, "rm", "2026-10-20T14:00:00Z", 1L],
            e.Rows("SELECT CurrentStatus,RiskTier,TargetReleaseDate,TemplateId,ClonedFromTrainId,LastChangedByUserId,LastChangedAt,Version FROM ReleaseTrains WHERE Id=?", id)[0]);
        Assert.Equal(2, e.Count("SELECT count(*) FROM BundledProducts WHERE ReleaseTrainId=?", id));
        Assert.Equal(0, e.Count("SELECT count(*) FROM StageGates WHERE ReleaseTrainId=?", id));
        Assert.Equal(1, e.Count("SELECT count(*) FROM AuditEvents WHERE EntityType='ReleaseTrain' AND EntityId=? AND Action='Create' AND ActorUserId='rm'", id));
        Assert.Equal(2, e.Count("SELECT count(*) FROM AuditEvents WHERE EntityType='BundledProduct' AND ReleaseTrainId=? AND Action='Create'", id));
        Assert.Equal(3, e.Count("SELECT count(*) FROM AuditEvents WHERE ReleaseTrainId=?", id));
        Assert.Equal(36, Guid.Parse(id).ToString().Length);   // UUIDv7 text
        Assert.Equal(7, Guid.Parse(id).Version);
    }

    [Fact]
    public async Task From_an_Approved_template_copies_gates_steps_and_the_T_minus_schedule()
    {
        var e = NewEnv();
        SeedTemplate(e, "Approved");
        var r = await e.Svc.CreateAsync(new NewTrainInput("R26.11 Autumn", "2026-11-27", null, "tpl", "2026-11-27T08:00:00Z", "2026-11-27T12:00:00Z"), Rm);
        Assert.True(r.IsOk, string.Join("; ", r.Failures.Select(f => f.Message)));
        var id = r.Value!.Id;
        Assert.Equal(["tpl", "VeryHigh", "Planning"], e.Rows("SELECT TemplateId,RiskTier,CurrentStatus FROM ReleaseTrains WHERE Id=?", id)[0]);   // risk tier defaults to the template's

        var gates = e.Rows("SELECT GateName,GateClass,SequenceOrder,OffsetDays,DueOn,RequiredBeforeStatus,OwnerUserId,OwnerTeamId,Status FROM StageGates WHERE ReleaseTrainId=? ORDER BY SequenceOrder", id);
        Assert.Equal(["Code Freeze", "Standard", 1L, 3L, "2026-11-23", "Gated", null, "team1", "Pending"], gates[0]);        // 3 business days, skipping Thanksgiving
        Assert.Equal(["CAB Approval", "Compliance", 2L, 1L, "2026-11-25", "Executing", "rm", null, "Pending"], gates[1]);   // no team on the template: the creator (Q-080a)

        var steps = e.Rows("SELECT StepCode,PlannedStartAt,PlannedDurationMin,OwnerUserId,OwnerTeamId FROM RunbookSteps WHERE ReleaseTrainId=? ORDER BY PlannedStartAt", id);
        Assert.Equal(["PRE-1", "2026-11-27T07:30:00Z", 15L, "rm", null], steps[0]);   // relative to the window start
        Assert.Equal(["DEP-1", "2026-11-27T08:00:00Z", 45L, null, "team1"], steps[1]);
        Assert.Equal(1, e.Count("SELECT count(*) FROM DeploymentWindows WHERE ReleaseTrainId=?", id));
        Assert.Equal(1, e.Count("SELECT count(*) FROM CommSchedule WHERE ReleaseTrainId=?", id));
        Assert.Equal(1, e.Count("SELECT count(*) FROM CommTemplates WHERE ReleaseTrainId=? AND LibraryTemplateId='lib1'", id));
        Assert.Equal(1, e.Count("SELECT count(*) FROM AuditEvents WHERE ReleaseTrainId=? AND EntityType='CommSchedule' AND Action='Seed'", id));
        // train + window + 2 gates + 2 steps + seed
        Assert.Equal(7, e.Count("SELECT count(*) FROM AuditEvents WHERE ReleaseTrainId=?", id));
        Assert.Equal(new CreatedCounts(0, 2, 0, 2, 0, 1, 1, true), r.Value.Created);
        Assert.Empty(r.Value.Notes);
    }

    [Fact]
    public async Task From_a_template_without_a_window_the_steps_are_left_out_and_the_note_says_why()
    {
        var e = NewEnv();
        SeedTemplate(e, "Approved");
        var r = await e.Svc.CreateAsync(new NewTrainInput("R26.11 No window", "2026-11-27", "Moderate", "tpl"), Rm);
        Assert.True(r.IsOk);
        Assert.Equal("Moderate", r.Value!.RiskTier);   // an explicit tier wins over the template default
        Assert.Equal(0, e.Count("SELECT count(*) FROM RunbookSteps WHERE ReleaseTrainId=?", r.Value.Id));
        Assert.Equal(2, e.Count("SELECT count(*) FROM StageGates WHERE ReleaseTrainId=?", r.Value.Id));
        Assert.Contains(r.Value.Notes, n => n.Contains("2 runbook steps were not added"));
    }

    [Theory]
    [InlineData("Draft")]
    [InlineData("Retired")]
    public async Task A_template_that_is_not_Approved_is_refused_and_nothing_is_written(string status)
    {
        var e = NewEnv();
        SeedTemplate(e, status);
        var before = e.Count("SELECT count(*) FROM AuditEvents");
        var r = await e.Svc.CreateAsync(new NewTrainInput("R26.11 Refused", "2026-11-27", null, "tpl"), Rm);
        Assert.Equal(ResultKind.GuardFailed, r.Kind);
        Assert.Equal(TrainCreationGuards.TemplateNotApproved, Assert.Single(r.Failures).Guard);
        Assert.Equal(0, e.Count("SELECT count(*) FROM ReleaseTrains WHERE Title='R26.11 Refused'"));
        Assert.Equal(before, e.Count("SELECT count(*) FROM AuditEvents"));
    }

    [Fact]
    public async Task An_unknown_template_or_source_is_not_found()
    {
        var e = NewEnv();
        Assert.Equal(ResultKind.NotFound, (await e.Svc.CreateAsync(new NewTrainInput("X", "2026-11-27", null, "nope"), Rm)).Kind);
        Assert.Equal(ResultKind.NotFound, (await e.Svc.CloneAsync("nope", new CloneTrainInput("X", "2026-11-27"), Rm)).Kind);
    }

    [Theory]
    [InlineData("", "2026-11-27", null, "TrainInvalid")]
    [InlineData("r26.10", "2026-11-27", null, "TrainTitleTaken")]          // t1 is R26.10: titles are unique ignoring case
    [InlineData("New", "", null, "TrainInvalid")]
    [InlineData("New", "27/11/2026", null, "TrainInvalid")]
    [InlineData("New", "2026-02-30", null, "TrainInvalid")]
    [InlineData("New", "2026-10-19", null, "TrainInvalid")]                // before today in the display zone
    [InlineData("New", "2026-11-27", "Extreme", "TrainInvalid")]
    public async Task Bad_input_is_refused_with_a_readable_guard(string title, string target, string? tier, string guard)
    {
        var e = NewEnv();
        var r = await e.Svc.CreateAsync(new NewTrainInput(title, target, tier), Rm);
        Assert.Equal(ResultKind.GuardFailed, r.Kind);
        Assert.Contains(guard, r.Failures.Select(f => f.Guard));
        var c = await e.Svc.CloneAsync("t1", new CloneTrainInput(title, target, tier), Rm);
        Assert.Equal(ResultKind.GuardFailed, c.Kind);
        Assert.Contains(guard, c.Failures.Select(f => f.Guard));
    }

    [Fact]
    public async Task Titles_and_products_follow_the_import_limits_and_windows_must_make_sense()
    {
        var e = NewEnv();
        Assert.Equal(ResultKind.GuardFailed, (await e.Svc.CreateAsync(new NewTrainInput(new string('x', 201), "2026-11-27", null), Rm)).Kind);
        Assert.True((await e.Svc.CreateAsync(new NewTrainInput(new string('x', 200), "2026-11-27", null), Rm)).IsOk);
        var dup = await e.Svc.CreateAsync(new NewTrainInput("P", "2026-11-27", null, Products: [new("A", "1", "A"), new("a", "2", "B")]), Rm);
        Assert.Contains(dup.Failures, f => f.Message.Contains("listed twice"));
        var longTag = await e.Svc.CreateAsync(new NewTrainInput("P", "2026-11-27", null, Products: [new("A", new string('1', 101), "A")]), Rm);
        Assert.Contains(longTag.Failures, f => f.Message.Contains("100 characters"));
        var half = await e.Svc.CreateAsync(new NewTrainInput("P", "2026-11-27", null, WindowStartsAt: "2026-11-27T08:00:00Z"), Rm);
        Assert.Contains(half.Failures, f => f.Message.Contains("both a start and an end"));
        var backwards = await e.Svc.CreateAsync(new NewTrainInput("P", "2026-11-27", null, WindowStartsAt: "2026-11-27T08:00:00Z", WindowEndsAt: "2026-11-27T07:00:00Z"), Rm);
        Assert.Contains(backwards.Failures, f => f.Message.Contains("end after it starts"));
        Assert.Equal(0, e.Count("SELECT count(*) FROM ReleaseTrains WHERE Title='P'"));
    }

    [Fact]
    public async Task Clone_copies_the_plan_shifted_to_the_new_target_and_never_the_history()
    {
        var e = NewEnv();
        // History on the source that must not travel: a completed task, a run with an execution, a Go/No-Go, a comm message tied to a gate, an inactive owner.
        e.Sql(@"
            UPDATE ChecklistTasks SET IsCompleted=1, CompletedAt='2026-10-20T10:00:00Z', CompletedByUserId='rte' WHERE Id='k1';
            UPDATE ChecklistTasks SET OwnerUserId='dev' WHERE Id='k2';
            UPDATE Users SET IsActive=0 WHERE Id='dev';
            INSERT INTO DeploymentWindows(Id,ReleaseTrainId,StartsAt,EndsAt) VALUES('w1','t1','2026-10-30T01:00:00Z','2026-10-30T06:00:00Z');
            INSERT INTO RunbookRuns(Id,ReleaseTrainId,Mode,StartedAt,StartedByUserId) VALUES('run1','t1','Rehearsal','2026-10-20T10:00:00Z','rte');
            INSERT INTO StepExecutions(Id,RunId,StepId,Status,ActualStartAt,ActualEndAt,ActorUserId) VALUES('x1','run1','s1','Done','2026-10-20T10:00:00Z','2026-10-20T10:05:00Z','rte');
            INSERT INTO CommTemplates(Id,ReleaseTrainId,StageGateId,TemplateType,Audience,SubjectLine,MarkdownBody) VALUES('ct1','t1','g1','Tminus','All','Freeze','Body');");
        var r = await e.Svc.CloneAsync("t1", new CloneTrainInput("R26.11 Clone", "2026-11-27"), Rm);
        Assert.True(r.IsOk, string.Join("; ", r.Failures.Select(f => f.Message)));
        var id = r.Value!.Id;
        Assert.Equal(["t1", null, "High", "Planning", null], e.Rows("SELECT ClonedFromTrainId,TemplateId,RiskTier,CurrentStatus,ChangeTicketNumber FROM ReleaseTrains WHERE Id=?", id)[0]);

        Assert.Equal([["Card Portal", "2.1.0", "CRD"], ["Payments API", "4.5.0", "PAY"]],
            e.Rows("SELECT ProductName,VersionTag,ProjectCode FROM BundledProducts WHERE ReleaseTrainId=? ORDER BY ProductName", id));
        var gates = e.Rows("SELECT GateName,DueOn,Status,CertifiedByUserId,OwnerUserId FROM StageGates WHERE ReleaseTrainId=? ORDER BY SequenceOrder", id);
        Assert.Equal(["Code Freeze", "2026-11-19", "Pending", null, "rte"], gates[0]);          // T-5 business days, Thanksgiving skipped
        Assert.Equal(["Compliance Sign-off", "2026-11-24", "Pending", null, "gov1"], gates[1]);  // the source gate was InProgress: the copy starts Pending

        var tasks = e.Rows(@"SELECT t.TaskDescription,t.IsCompleted,t.CompletedAt,t.CompletedByUserId,t.OwnerUserId FROM ChecklistTasks t JOIN StageGates g ON g.Id=t.StageGateId
                             WHERE g.ReleaseTrainId=? ORDER BY g.SequenceOrder", id);
        Assert.Equal(["Tag repos", 0L, null, null, "rte"], tasks[0]);            // open again
        Assert.Equal(["SOX evidence uploaded", 0L, null, null, "rm"], tasks[1]);  // its owner is deactivated: given to the person cloning
        Assert.Contains(r.Value.Notes, n => n.Contains("deactivated"));

        var steps = e.Rows("SELECT s.StepCode,s.PlannedStartAt,p.ProductName FROM RunbookSteps s LEFT JOIN BundledProducts p ON p.Id=s.BundledProductId WHERE s.ReleaseTrainId=? ORDER BY s.StepCode", id);
        Assert.Equal(["R-001", "2026-11-27T02:00:00Z", "Payments API"], steps[0]);   // 28 calendar days later, product remapped to the copy
        Assert.Equal(1, e.Count(@"SELECT count(*) FROM StepDependencies d JOIN RunbookSteps a ON a.Id=d.StepId JOIN RunbookSteps b ON b.Id=d.DependsOnStepId
                                  WHERE a.ReleaseTrainId=? AND a.StepCode='R-002' AND b.StepCode='R-001'", id));
        Assert.Equal(["2026-11-27T01:00:00Z", "2026-11-27T06:00:00Z"], e.Rows("SELECT StartsAt,EndsAt FROM DeploymentWindows WHERE ReleaseTrainId=?", id)[0]);
        Assert.Equal(1, e.Count("SELECT count(*) FROM CommTemplates c JOIN StageGates g ON g.Id=c.StageGateId WHERE c.ReleaseTrainId=? AND g.ReleaseTrainId=?", id, id));   // gate link remapped

        foreach (var table in new[] { "RunbookRuns", "GoNoGoDecisions", "Attachments", "CommSchedule", "Baselines", "ChangeRecords" })
            Assert.Equal(0, e.Count($"SELECT count(*) FROM {table} WHERE ReleaseTrainId=?", id));
        Assert.Equal(0, e.Count("SELECT count(*) FROM StepExecutions x JOIN RunbookSteps s ON s.Id=x.StepId WHERE s.ReleaseTrainId=?", id));
        // train + window + 2 products + 2 gates + 2 tasks + 2 steps + 1 message
        Assert.Equal(11, e.Count("SELECT count(*) FROM AuditEvents WHERE ReleaseTrainId=? AND Action='Create' AND ActorUserId='rm'", id));
        Assert.Equal(new CreatedCounts(2, 2, 2, 2, 1, 1, 0, true), r.Value.Created);
        // the source is untouched
        Assert.Equal(1L, e.One("SELECT IsCompleted FROM ChecklistTasks WHERE Id='k1'"));
        Assert.Equal(1L, e.One("SELECT Version FROM ReleaseTrains WHERE Id='t1'"));
    }
}
