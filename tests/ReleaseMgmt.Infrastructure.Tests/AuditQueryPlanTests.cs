using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Infrastructure.Tests;

/// <summary>
/// REOS-52: the load test found the audit viewer's entity-type filter planned as "IX_Audit_Entity, then sort every match" (~80 ms for 100 000 rows and
/// growing) instead of a walk down the primary key that stops after one page. This asserts the plan SQLite chooses for the SQL EF really sends.
/// </summary>
public sealed class AuditQueryPlanTests(TriggerSuiteFixture fx) : IClassFixture<TriggerSuiteFixture>
{
    private sealed class Capture : DbCommandInterceptor
    {
        public string? Sql; public List<(string Name, object? Value)> Params = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            Sql = command.CommandText; Params = [.. command.Parameters.Cast<DbParameter>().Select(p => (p.ParameterName, p.Value))];
            return base.ReaderExecutingAsync(command, eventData, result, ct);
        }
    }

    private sealed class Factory(string path, Capture cap) : IDbContextFactory<ReleaseDbContext>
    {
        public ReleaseDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ReleaseDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").AddInterceptors(new SqliteConnectionInterceptor(), cap).Options);
    }

    private string SeededPath()
    {
        var path = fx.FreshPath();
        using var c = TriggerSuiteFixture.Open(path);
        TriggerSuiteFixture.Run(c, @"
            WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < 50)
            INSERT INTO Users(Id, Email, DisplayName, Role) SELECT 'u' || x, 'u' || x || '@x.com', 'User ' || x, 'Viewer' FROM n;
            WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < 200)
            INSERT INTO ReleaseTrains(Id, Title, TargetReleaseDate, RiskTier, CreatedAt, UpdatedAt) SELECT 'tt' || x, 'Train ' || x, '2026-11-30', 'Low', '2026-10-01T00:00:00Z', '2026-10-01T00:00:00Z' FROM n;
            WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < 30000)
            INSERT INTO AuditEvents(OccurredAt, ActorUserId, ReleaseTrainId, EntityType, EntityId, Action)
            SELECT '2026-10-01T00:00:00Z', 'u' || (x % 50 + 1), 'tt' || (x % 200 + 1),
                   CASE x % 12 WHEN 0 THEN 'StageGate' WHEN 1 THEN 'ReleaseTrain' WHEN 2 THEN 'Blocker' WHEN 3 THEN 'Notification' WHEN 4 THEN 'StepExecution' WHEN 5 THEN 'StepExecution' ELSE 'ChecklistTask' END,
                   'e' || x, 'Update' FROM n;");
        return path;
    }

    private static string Plan(string path, Capture cap)
    {
        using var c = TriggerSuiteFixture.Open(path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN " + cap.Sql;
        foreach (var (name, value) in cap.Params) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        var lines = new List<string>();
        while (r.Read()) lines.Add(r.GetString(3));
        return string.Join(" | ", lines);
    }

    [Fact]
    public async Task An_entity_type_filter_walks_the_primary_key_instead_of_sorting_every_match()
    {
        var path = SeededPath(); var cap = new Capture();
        var page = await new AuditQueryService(new Factory(path, cap)).QueryAsync(new AuditFilter(Entity: "ChecklistTask"), null, 100);
        Assert.Equal(100, page.Items.Count);
        Assert.All(page.Items, r => Assert.Equal("ChecklistTask", r.EntityType));
        Assert.True(page.Items.Zip(page.Items.Skip(1)).All(p => p.First.Id > p.Second.Id), "newest first");
        var plan = Plan(path, cap);
        Assert.True(!plan.Contains("USE TEMP B-TREE FOR ORDER BY") && !plan.Contains("IX_Audit_Entity"), plan);
    }

    [Fact]
    public async Task An_entity_id_filter_still_uses_the_index()
    {
        var path = SeededPath(); var cap = new Capture();
        var page = await new AuditQueryService(new Factory(path, cap)).QueryAsync(new AuditFilter(Entity: "ChecklistTask", EntityId: "e6"), null, 100);
        Assert.Single(page.Items);
        Assert.Contains("IX_Audit_Entity", Plan(path, cap));
    }
}
