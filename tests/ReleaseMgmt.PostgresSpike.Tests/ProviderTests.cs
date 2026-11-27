using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Infrastructure.Persistence;
using Xunit.Abstractions;

namespace ReleaseMgmt.PostgresSpike.Tests;

/// <summary>What changing the provider costs: the Npgsql swap, the conventions it forces, and what EF's own DDL generation gives compared with the contract schema.</summary>
public sealed class ProviderTests(PgFixture fx, ITestOutputHelper o) : IClassFixture<PgFixture>
{
    [PgFact]
    public async Task Naive_provider_swap_fails_on_bool_columns_in_both_directions()
    {
        var e = await fx.NewEnvAsync();
        var f = new PgFactory<PgNaiveContext>(e.ConnectionString);

        await using (var db = f.CreateDbContext())
        {
            // ReleaseTrains (int Version, DateTime and DateOnly through converters, string ids) reads fine with no change at all
            var t = await db.Set<ReleaseTrains>().SingleAsync(x => x.Id == "t1");
            Assert.Equal(1, t.Version);
            Assert.Equal(new DateOnly(2026, 10, 30), t.TargetReleaseDate);
        }
        await using (var db = f.CreateDbContext())
        {
            // 15 bool properties: the schema keeps them as 0/1 integers (CHECK (x IN (0,1)); CLAUDE.md rule 5 and the port plan's PG-8)
            var ex = await Assert.ThrowsAsync<InvalidCastException>(() => db.Set<ChecklistTasks>().SingleAsync(x => x.Id == "k1"));
            Assert.Contains("'System.Boolean' is not supported for fields having DataTypeName 'bigint'", ex.Message);
        }
        await using (var db = f.CreateDbContext())
        {
            db.Set<ChecklistTasks>().Add(new ChecklistTasks { StageGateId = "g1", TaskDescription = "x", OwnerUserId = "rte", SequenceOrder = 5 });
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal("42804", Assert.IsType<PostgresException>(ex.InnerException).SqlState);   // column "IsCompleted" is of type bigint but expression is of type boolean
        }
    }

    [PgFact]
    public async Task Spike_conventions_keep_booleans_as_0_1_and_round_trip()
    {
        var e = await fx.NewEnvAsync();
        await using (var db = e.Db.CreateDbContext())
        {
            db.Set<ChecklistTasks>().Add(new ChecklistTasks { Id = "k9", StageGateId = "g1", TaskDescription = "bool probe", OwnerUserId = "rte", SequenceOrder = 9, IsCompleted = false });
            await db.SaveChangesAsync();
            var k = await db.Set<ChecklistTasks>().SingleAsync(x => x.Id == "k1");
            k.IsCompleted = true; k.CompletedByUserId = "rte"; k.CompletedAt = new DateTime(2026, 10, 20, 14, 0, 0, DateTimeKind.Utc);
            await db.SaveChangesAsync();
        }
        Assert.Equal(1L, e.Scalar("SELECT \"IsCompleted\" FROM \"ChecklistTasks\" WHERE \"Id\"='k1'"));
        Assert.Equal(0L, e.Scalar("SELECT \"IsCompleted\" FROM \"ChecklistTasks\" WHERE \"Id\"='k9'"));
        await using var db2 = e.Db.CreateDbContext();
        Assert.True((await db2.Set<ChecklistTasks>().SingleAsync(x => x.Id == "k1")).IsCompleted);
    }

    /// <summary>EF's migration generation (what <c>dotnet ef migrations add</c> would script for Npgsql) against the contract schema as the oracle's generator builds it.
    /// The point is not that they match; it is to measure how much of the contract EF cannot express.</summary>
    [PgFact]
    public async Task EF_generated_DDL_covers_the_tables_but_not_triggers_checks_or_most_indexes()
    {
        var e = await fx.NewEnvAsync();
        string script;
        await using (var db = new PgReleaseDbContext(new DbContextOptionsBuilder<PgReleaseDbContext>().UseNpgsql(e.ConnectionString).Options))
            script = db.Database.GenerateCreateScript();

        var efTables = Regex.Matches(script, "CREATE TABLE \"([^\"]+)\"").Select(m => m.Groups[1].Value).ToHashSet();
        var contractTables = e.Query("SELECT table_name FROM information_schema.tables WHERE table_schema='public' AND table_type='BASE TABLE'").Select(r => (string)r[0]!).ToHashSet();
        var efIndexes = Regex.Matches(script, "CREATE (UNIQUE )?INDEX").Count;
        var contractIndexes = e.Count("SELECT count(*) FROM pg_indexes WHERE schemaname='public' AND indexname NOT LIKE '%\\_pkey' AND indexname NOT LIKE '%\\_key'");
        var contractTriggers = e.Count("SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal");
        var contractChecks = e.Count("SELECT count(*) FROM pg_constraint WHERE contype='c' AND connamespace='public'::regnamespace");
        var efChecks = Regex.Matches(script, "CHECK \\(").Count;
        var efFks = Regex.Matches(script, "FOREIGN KEY").Count;
        var contractFks = e.Count("SELECT count(*) FROM pg_constraint WHERE contype='f' AND connamespace='public'::regnamespace");

        o.WriteLine($"tables: EF {efTables.Count}, contract {contractTables.Count}; missing from EF: [{string.Join(", ", contractTables.Except(efTables).Order())}]; extra in EF: [{string.Join(", ", efTables.Except(contractTables).Order())}]");
        o.WriteLine($"triggers: EF 0, contract {contractTriggers}; CHECKs: EF {efChecks}, contract {contractChecks}; foreign keys: EF {efFks}, contract {contractFks}; indexes (non-key): EF {efIndexes}, contract {contractIndexes}");

        // column types, EF vs contract, for the tables both have
        var diffs = new List<string>();
        foreach (var t in efTables.Intersect(contractTables).Order())
        {
            var body = Regex.Match(script, "CREATE TABLE \"" + t + "\" \\((.*?)\\n\\);", RegexOptions.Singleline).Groups[1].Value;
            foreach (Match m in Regex.Matches(body, "^\\s+\"([^\"]+)\" (\\w+)", RegexOptions.Multiline))
            {
                var col = m.Groups[1].Value; var efType = m.Groups[2].Value.ToLowerInvariant();
                var real = e.Query("SELECT data_type FROM information_schema.columns WHERE table_name=$1 AND column_name=$2", t, col).SingleOrDefault()?[0] as string;
                if (real is null) { diffs.Add($"{t}.{col}: EF has it, contract does not"); continue; }
                var norm = efType == "double" ? "double precision" : efType;
                if (!string.Equals(norm, real, StringComparison.OrdinalIgnoreCase) && !(real == "USER-DEFINED" && norm == "text")) diffs.Add($"{t}.{col}: EF {efType}, contract {real}");
            }
        }
        var byKind = diffs.GroupBy(d => d[(d.IndexOf(": ", StringComparison.Ordinal) + 2)..]).OrderByDescending(g => g.Count());
        foreach (var g in byKind) o.WriteLine($"column type differences, {g.Count()} columns: {g.Key} (e.g. {g.First()[..g.First().IndexOf(':')]})");

        Assert.Equal(contractTables.OrderBy(x => x), efTables.OrderBy(x => x));   // the entity model covers every table of the contract
        Assert.DoesNotContain("CREATE TRIGGER", script);                           // so triggers can only come from a hand-written migration, as on SQLite
        Assert.Equal(0, efChecks);                                                 // and so can the CHECK constraints
        Assert.True(contractTriggers >= 188 && contractChecks > 100);
    }
}
