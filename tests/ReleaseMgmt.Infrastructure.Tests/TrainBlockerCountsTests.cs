using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Infrastructure.Tests;

/// <summary>
/// REOS-52: GET /trains counts each train's next-hop blockers with set-based queries (NextHopBlockerCountsAsync) instead of one readiness call per train.
/// The count must equal what EvaluateAsync (the guards :advance runs) reports for that hop, for every combination of status, risk, gate, Go/No-Go,
/// condition, baseline and rollback state, so the two can never drift apart silently.
/// </summary>
public sealed class TrainBlockerCountsTests(TriggerSuiteFixture fx) : IClassFixture<TriggerSuiteFixture>
{
    private sealed class Factory(string path) : IDbContextFactory<ReleaseDbContext>
    {
        public ReleaseDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ReleaseDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").AddInterceptors(new SqliteConnectionInterceptor()).Options);
    }

    [Fact]
    public async Task The_batched_count_equals_EvaluateAsync_for_every_combination()
    {
        var path = fx.FreshPath();
        var sql = new StringBuilder();
        var n = 0;
        string[] statuses = ["Planning", "Gated", "Executing"], risks = ["Low", "High"], gates = ["none", "Gated", "Executing", "Complete", "certified"];
        string[] decisions = ["none", "go", "nogo", "conditionalExpired", "conditionalOpen", "conditionalClosed", "goThenNoGo", "noGoThenGo"];
        foreach (var status in statuses) foreach (var risk in risks) foreach (var gate in gates) foreach (var dec in decisions) foreach (var baseline in new[] { false, true }) foreach (var rehearsed in new[] { false, true })
        {
            if (status != "Gated" && (dec != "none" || baseline || rehearsed)) continue;   // only the Gated to Executing hop has the Go, condition, baseline and rollback guards
            var id = $"c{++n}";
            var rb = rehearsed ? "'2026-10-10T00:00:00Z'" : "NULL";
            sql.AppendLine($"INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CurrentStatus,RollbackRehearsedAt,CreatedAt,UpdatedAt) VALUES('{id}','{id}','2026-11-30','{risk}','{status}',{rb},'2026-10-01T00:00:00Z','2026-10-01T00:00:00Z');");
            if (gate == "certified") sql.AppendLine($"INSERT INTO StageGates(Id,ReleaseTrainId,GateName,GateClass,SequenceOrder,OffsetDays,DueOn,RequiredBeforeStatus,OwnerUserId,Status,CertifiedByUserId,CertifiedAt) VALUES('{id}g','{id}','G','Standard',1,1,'2026-11-29','Gated','rte','Certified','rte','2026-10-05T00:00:00Z');");
            else if (gate != "none") sql.AppendLine($"INSERT INTO StageGates(Id,ReleaseTrainId,GateName,GateClass,SequenceOrder,OffsetDays,DueOn,RequiredBeforeStatus,OwnerUserId,Status) VALUES('{id}g','{id}','G','Standard',1,1,'2026-11-29','{gate}','rte','InProgress');");
            if (baseline) sql.AppendLine($"INSERT INTO Baselines(Id,ReleaseTrainId,CapturedAt,PlannedReleaseDate,SnapshotJson) VALUES('{id}b','{id}','2026-10-05T00:00:00Z','2026-11-30','{{}}');");
            void Decision(string suffix, string decision, string at, string? cond = null, string? closedAt = null)
            {
                sql.AppendLine($"INSERT INTO GoNoGoDecisions(Id,ReleaseTrainId,Decision,DecidedByUserId,DecidedAt,GateSnapshotJson) VALUES('{id}d{suffix}','{id}','{decision}','rm','{at}','[]');");
                if (cond is not null) sql.AppendLine($"INSERT INTO GoNoGoConditions(Id,DecisionId,Text,OwnerUserId,ExpiresAt,ClosedAt,ClosedByUserId) VALUES('{id}c{suffix}','{id}d{suffix}','Fix it','rte','{cond}',{(closedAt is null ? "NULL" : $"'{closedAt}'")},{(closedAt is null ? "NULL" : "'rte'")});");
            }
            switch (dec)
            {
                case "go": Decision("1", "Go", "2026-10-10T00:00:00Z"); break;
                case "nogo": Decision("1", "NoGo", "2026-10-10T00:00:00Z"); break;
                case "conditionalExpired": Decision("1", "GoWithConditions", "2026-10-10T00:00:00Z", "2026-10-19T00:00:00Z"); break;
                case "conditionalOpen": Decision("1", "GoWithConditions", "2026-10-10T00:00:00Z", "2026-10-25T00:00:00Z"); break;
                case "conditionalClosed": Decision("1", "GoWithConditions", "2026-10-10T00:00:00Z", "2026-10-19T00:00:00Z", "2026-10-15T00:00:00Z"); break;
                case "goThenNoGo": Decision("1", "Go", "2026-10-10T00:00:00Z"); Decision("2", "NoGo", "2026-10-12T00:00:00Z"); break;
                case "noGoThenGo": Decision("1", "NoGo", "2026-10-10T00:00:00Z"); Decision("2", "Go", "2026-10-12T00:00:00Z"); break;
            }
        }
        sql.AppendLine("INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CurrentStatus,ActualEndAt,CloseCode,CreatedAt,UpdatedAt) VALUES('done','done','2026-10-01','Low','Complete','2026-10-02T00:00:00Z','Successful','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z');");
        sql.AppendLine("INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CurrentStatus,CreatedAt,UpdatedAt) VALUES('gone','gone','2026-10-01','Low','Aborted','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z');");
        using (var c = TriggerSuiteFixture.Open(path)) TriggerSuiteFixture.Run(c, sql.ToString());

        var svc = new TrainLifecycleService(new Factory(path), new FakeTimeProvider(DateTimeOffset.Parse("2026-10-20T14:00:00Z")));
        await using var db = new Factory(path).CreateDbContext();
        var trains = await db.Set<ReleaseTrains>().AsNoTracking().Where(t => t.Id.StartsWith("c") || t.Id == "done" || t.Id == "gone").ToListAsync();
        var batched = await svc.NextHopBlockerCountsAsync(db, trains);

        string[] path4 = ["Planning", "Gated", "Executing", "Complete"];
        var distinct = new HashSet<int>();
        foreach (var t in trains)
        {
            var i = Array.IndexOf(path4, t.CurrentStatus);
            var expected = 0;
            if (i is >= 0 and < 3)
            {
                var next = path4[i + 1];
                expected = (await svc.EvaluateAsync(db, t, next, closeCode: next == "Complete" ? "Successful" : null)).Count;   // what the old per-train readiness call reported
            }
            Assert.True(expected == batched[t.Id], $"{t.Id} ({t.CurrentStatus}, {t.RiskTier}): EvaluateAsync says {expected}, batched says {batched[t.Id]}");
            distinct.Add(expected);
        }
        Assert.Equal(trains.Count, batched.Count);
        Assert.True(distinct.Count >= 4, "the combinations must exercise several different blocker counts, not only zero and one");
        Assert.Equal(0, batched["done"]); Assert.Equal(0, batched["gone"]);
    }
}
