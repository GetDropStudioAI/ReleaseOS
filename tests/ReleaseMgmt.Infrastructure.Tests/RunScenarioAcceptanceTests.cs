using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Infrastructure.Tests;

/// <summary>
/// M3 acceptance (docs/MILESTONES.md): the scenario in tests/reference/fixtures/r26-24-scenario.json. Setup and plan are inserted by a helper, the actuals are applied
/// through RunService at the given times on a FakeTimeProvider, and ForecastService must yield exactly the expected block. Also checks the escalations and pushes.
/// </summary>
public sealed class RunScenarioAcceptanceTests
{
    private static string Fixtures([CallerFilePath] string file = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", "reference", "fixtures"));
    private static DateTime U(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
    private static string Iso(DateTime d) => d.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private sealed class Factory(string path) : IDbContextFactory<ReleaseDbContext>
    {
        public ReleaseDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ReleaseDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").AddInterceptors(new SqliteConnectionInterceptor()).Options);
    }

    private sealed class RecordingPublisher : IRealtimePublisher
    {
        public List<(string Train, string Run)> Forecasts { get; } = [];
        public List<string> Notified { get; } = [];
        public Task TrainChangedAsync(string trainId, int version, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotificationCreatedAsync(string userId, string notificationId, CancellationToken ct = default) { lock (Notified) Notified.Add(userId); return Task.CompletedTask; }
        public Task ForecastChangedAsync(string trainId, string runId, CancellationToken ct = default) { lock (Forecasts) Forecasts.Add((trainId, runId)); return Task.CompletedTask; }
    }

    private static List<object?[]> Query(string path, string sql, params object?[] p)
    {
        using var c = TriggerSuiteFixture.Open(path);
        using var cmd = c.CreateCommand();
        TriggerSuiteFixture.Bind(cmd, sql, p);
        using var r = cmd.ExecuteReader();
        var rows = new List<object?[]>();
        while (r.Read()) { var row = new object?[r.FieldCount]; r.GetValues(row!); rows.Add(row.Select(v => v is DBNull ? null : v).ToArray()); }
        return rows;
    }

    [Fact]
    public async Task The_r26_24_scenario_yields_exactly_its_expected_block_with_the_escalations_on_the_fake_clock()
    {
        var scenario = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures(), "r26-24-scenario.json"))).RootElement;
        var expected = scenario.GetProperty("expected");
        var setup = scenario.GetProperty("setup");

        // ---- a migrated database and the setup helper (direct inserts: the train is inserted as Executing, INSERT is not guarded) ----------------------------------
        var dir = Directory.CreateTempSubdirectory("reos-scn-").FullName;
        var path = Path.Combine(dir, "app.db");
        var db = new Factory(path);
        await using (var ctx = db.CreateDbContext()) await ctx.Database.MigrateAsync();

        var users = setup.GetProperty("users").EnumerateArray().Select((u, i) => (Id: $"u{i}", Email: u.GetProperty("email").GetString()!, Name: u.GetProperty("displayName").GetString()!, Role: u.GetProperty("role").GetString()!)).ToList();
        var teams = setup.GetProperty("teams").EnumerateArray().Select((t, i) => (Id: $"team{i}", Handle: t.GetProperty("handle").GetString()!, Name: t.GetProperty("name").GetString()!)).ToList();
        var products = setup.GetProperty("products").EnumerateArray().Select((p, i) => (Id: $"p{i}", Name: p.GetProperty("productName").GetString()!, Version: p.GetProperty("versionTag").GetString()!, Code: p.GetProperty("projectCode").GetString()!)).ToList();
        using (var c = TriggerSuiteFixture.Open(path))
        {
            foreach (var u in users) TriggerSuiteFixture.Run(c, "INSERT INTO Users(Id,Email,DisplayName,Role) VALUES(?,?,?,?)", u.Id, u.Email, u.Name, u.Role);
            foreach (var t in teams) TriggerSuiteFixture.Run(c, "INSERT INTO Teams(Id,Handle,Name) VALUES(?,?,?)", t.Id, t.Handle, t.Name);
            TriggerSuiteFixture.Run(c, "INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CurrentStatus,CreatedAt,UpdatedAt) VALUES('train','R26.24','2026-10-30','High','Executing','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z')");
            var w = setup.GetProperty("deploymentWindow");
            TriggerSuiteFixture.Run(c, "INSERT INTO DeploymentWindows(Id,ReleaseTrainId,StartsAt,EndsAt) VALUES('win','train',?,?)", w.GetProperty("startsAt").GetString(), w.GetProperty("endsAt").GetString());
            foreach (var p in products) TriggerSuiteFixture.Run(c, "INSERT INTO BundledProducts(Id,ReleaseTrainId,ProductName,VersionTag,ProjectCode) VALUES(?,'train',?,?,?)", p.Id, p.Name, p.Version, p.Code);

            // the plan, from the same CSV the import tests will use (columns: Train,StepCode,Title,Section,PlannedStart,DurationMin,Owner,Product,DependsOn,#ActualStart,#ActualEnd)
            foreach (var l in File.ReadAllLines(Path.Combine(Fixtures(), "r26-24-runbook.csv")).Skip(1))
            {
                var f = l.Split(',');
                var ownerUser = f[6].StartsWith('@') ? null : users.Single(u => u.Email == f[6]).Id;
                var ownerTeam = f[6].StartsWith('@') ? teams.Single(t => "@" + t.Handle == f[6]).Id : null;
                var product = f[7] == "" ? null : products.Single(p => p.Name == f[7]).Id;
                TriggerSuiteFixture.Run(c, "INSERT INTO RunbookSteps(Id,ReleaseTrainId,BundledProductId,StepCode,Section,Title,OwnerUserId,OwnerTeamId,PlannedStartAt,PlannedDurationMin) VALUES(?,'train',?,?,?,?,?,?,?,?)",
                    "s-" + f[1], product, f[1], f[3], f[2], ownerUser, ownerTeam, f[4], int.Parse(f[5]));
            }
            foreach (var l in File.ReadAllLines(Path.Combine(Fixtures(), "r26-24-runbook.csv")).Skip(1))
            {
                var f = l.Split(',');
                if (f[8] != "") foreach (var dep in f[8].Split(';')) TriggerSuiteFixture.Run(c, "INSERT INTO StepDependencies(StepId,DependsOnStepId) VALUES(?,?)", "s-" + f[1], "s-" + dep);
            }
        }

        // ---- services on a fake clock ---------------------------------------------------------------------------------------------------------------------------
        var time = new FakeTimeProvider(new DateTimeOffset(U(setup.GetProperty("run").GetProperty("startedAt").GetString()!)));
        var push = new RecordingPublisher();
        var notifier = new Notifier(db, time, push);
        var runs = new RunService(db, time, push, notifier);
        var marcus = new Actor(users.Single(u => u.Name == "Marcus Bell").Id);

        var started = await runs.StartRunAsync("train", "Live", marcus);
        Assert.True(started.IsOk, string.Join("; ", started.Failures.Select(f => f.Message)));
        var runId = started.Value!.Id;

        // actuals: each CSV row with #ActualStart -> :start at that time; with #ActualEnd -> :done at that time (a done precedes a start at the same instant)
        var events = new List<(DateTime At, int Order, string Code, bool IsStart)>();
        foreach (var l in File.ReadAllLines(Path.Combine(Fixtures(), "r26-24-runbook.csv")).Skip(1))
        {
            var f = l.Split(',');
            if (f[9] != "") events.Add((U(f[9]), 1, f[1], true));
            if (f[10] != "") events.Add((U(f[10]), 0, f[1], false));
        }
        foreach (var e in events.OrderBy(e => e.At).ThenBy(e => e.Order))
        {
            time.SetUtcNow(new DateTimeOffset(e.At));
            var r = e.IsStart ? await runs.StartStepAsync(runId, "s-" + e.Code, null, marcus) : await runs.DoneStepAsync(runId, "s-" + e.Code, null, marcus);
            Assert.True(r.IsOk, $"{(e.IsStart ? "start" : "done")} {e.Code}: {string.Join("; ", r.Failures.Select(f => f.Message))}");
        }
        time.SetUtcNow(new DateTimeOffset(U(scenario.GetProperty("clockNow").GetString()!)));

        // ---- the expected block, exactly ------------------------------------------------------------------------------------------------------------------
        var fc = (await new ForecastService(db, time).ComputeAsync(runId)).Value!;
        Assert.Equal(expected.GetProperty("forecastFinish").GetString(), fc.ForecastFinish);
        Assert.Equal(expected.GetProperty("plannedFinish").GetString(), fc.PlannedFinish);
        Assert.Equal(expected.GetProperty("rollbackPlannedMin").GetInt32(), fc.RollbackPlannedMin);
        Assert.Equal(expected.GetProperty("rollbackDeadline").GetString(), fc.RollbackDeadline);
        Assert.Equal(expected.GetProperty("crossesDeadlineByMin").GetInt32(), fc.CrossesDeadlineByMin);
        Assert.Equal(expected.GetProperty("alertRaised").GetBoolean(), fc.AlertRaised);
        Assert.Equal(expected.GetProperty("windowClosesInSec").GetInt32(), fc.WindowClosesInSec);      // 8,566
        var exp = expected.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal(exp.Count, fc.Steps.Count);
        for (var i = 0; i < exp.Count; i++)
        {
            Assert.Equal(exp[i].GetProperty("step").GetString(), fc.Steps[i].Step);
            Assert.Equal(exp[i].GetProperty("state").GetString(), fc.Steps[i].State);
            Assert.Equal(exp[i].GetProperty("start").GetString(), fc.Steps[i].Start);
            Assert.Equal(exp[i].GetProperty("end").GetString(), fc.Steps[i].End);
            Assert.Equal(exp[i].GetProperty("endVarianceMin").GetInt32(), fc.Steps[i].EndVarianceMin);
        }

        // ---- escalations (PROJECT_SCOPE 3 and 5.5), recorded on the fake clock ---------------------------------------------------------------------------
        // Starts that were 5+ minutes behind plan: R-004 (+5), R-005 (+16), R-006 (+19). Warning: owner + RTEs. None reached 30 minutes, so no Release Manager paging for lateness.
        var late = Query(path, "SELECT EntityId, UserId, EscalationLevel, Message, CreatedAt FROM Notifications WHERE Kind='StepLate' ORDER BY CreatedAt, UserId");
        Assert.Equal(9, late.Count);                                                                             // 3 late starts x {Dana, Lee, Rae}
        Assert.All(late, n => Assert.Equal(1L, Convert.ToInt64(n[2])));
        var rtes = users.Where(u => u.Role == "RTE").Select(u => u.Id).Order().ToList();
        Assert.Equal(rtes, late.Select(n => (string)n[1]!).Distinct().Order().ToList());                        // Marcus (Release Manager) is not paged for a warning
        Assert.Contains(late, n => ((string)n[3]!).StartsWith("Step R-004 started 5 min"));
        Assert.Equal("2026-10-30T06:35:00Z", late.First(n => ((string)n[3]!).StartsWith("Step R-004"))[4]);   // the row carries the fake time of the step start, not the wall clock

        // The forecast first crossed the rollback deadline when R-004 finished (07:06): RTEs + the Release Manager, once, however many events followed.
        var crossed = Query(path, "SELECT UserId, EscalationLevel, CreatedAt FROM Notifications WHERE Kind='RollbackDeadline' ORDER BY UserId");
        Assert.Equal(users.Count, crossed.Count);
        Assert.All(crossed, n => { Assert.Equal(2L, Convert.ToInt64(n[1])); Assert.Equal("2026-10-30T07:06:00Z", n[2]); });

        Assert.Equal(11, push.Forecasts.Count);                                                                  // ForecastChanged after each of the 6 starts and 5 dones
        Assert.All(push.Forecasts, f => Assert.Equal(runId, f.Run));
        Assert.Equal("2026-10-30T07:06:00Z", Query(path, "SELECT MAX(OccurredAt) FROM AuditEvents WHERE Action='Done' AND AfterJson LIKE '%R-004%'")[0][0]);   // audit rows carry the fake time too
    }
}
