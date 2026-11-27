using System.Diagnostics;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Services;
using Xunit.Abstractions;

namespace ReleaseMgmt.PostgresSpike.Tests;

/// <summary>Measurements for REOS-196: write latency, concurrent writers, pooling, and the If-Match race, SQLite (the shipped database, migrated by the shipped migrations) against Postgres,
/// through the same real services. Numbers are from ONE machine with the server on localhost and fsync as configured there: they compare the two engines under identical conditions,
/// they are not capacity figures for a hosted Postgres (network round trips, synchronous commit and storage are not in them). Reports instead of asserting a threshold.</summary>
public sealed class MeasurementTests(PgFixture fx, ITestOutputHelper o) : IClassFixture<PgFixture>
{
    private static readonly Actor Rm = new("rm"), Rte = new("rte");

    private sealed class SqliteFactory(string path) : IDbContextFactory<ReleaseDbContext>
    {
        public ReleaseDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ReleaseDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").AddInterceptors(new SqliteConnectionInterceptor()).Options);
    }

    private sealed record Engine(string Name, IDbContextFactory<ReleaseDbContext> Db, FakeTimeProvider Time)
    {
        public TaskService Tasks => new(Db, Time);
        public TrainLifecycleService Trains => new(Db, Time);
    }

    private static Engine NewSqlite()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("reos-pgspike-").FullName, "t.db");
        var f = new SqliteFactory(path);
        using (var ctx = f.CreateDbContext()) ctx.Database.Migrate();   // the shipped migrations, so triggers and indexes are the shipped ones
        using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = SqliteConnectionInterceptor.Pragmas + PgFixture.Seed;
            cmd.ExecuteNonQuery();
        }
        return new("SQLite (WAL)", f, new(new DateTimeOffset(2026, 10, 20, 14, 0, 0, TimeSpan.Zero)));
    }

    private async Task<Engine> NewPg(string? opts = null)
    {
        var e = await fx.NewEnvAsync(opts);
        return new("Postgres 16", e.Db, e.Time);
    }

    private static (double P50, double P95, double Mean) Stats(List<double> ms)
    {
        var s = ms.Order().ToList();
        return (s[s.Count / 2], s[(int)(s.Count * 0.95)], s.Average());
    }

    [PgFact]
    public async Task Measure()
    {
        var report = new StringBuilder();
        void Say(string line) { o.WriteLine(line); report.AppendLine(line); }

        foreach (var eng in new[] { NewSqlite(), await NewPg() })
        {
            // 1. sequential write latency: a service write = transaction, guard reads, insert, audit row, trigger rules, commit
            await eng.Tasks.AddAsync("g1", "warm-up", null, null, Rm);
            var lat = new List<double>();
            for (var i = 0; i < 300; i++)
            {
                var sw = Stopwatch.StartNew();
                var r = await eng.Tasks.AddAsync("g1", "task " + i, null, null, Rm);
                lat.Add(sw.Elapsed.TotalMilliseconds);
                Assert.True(r.IsOk);
            }
            var st = Stats(lat);
            Say($"[{eng.Name}] sequential service write (TaskService.AddAsync) x300: p50 {st.P50:F2} ms, p95 {st.P95:F2} ms, mean {st.Mean:F2} ms");
        }

        foreach (var make in new Func<Task<Engine>>[] { () => Task.FromResult(NewSqlite()), () => NewPg() })
        {
            var eng0 = await make();
            foreach (var workers in new[] { 1, 4, 8, 16 })
            {
                var eng = workers == 1 ? eng0 : await make();
                var errors = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
                var all = new System.Collections.Concurrent.ConcurrentBag<double>();
                var sw = Stopwatch.StartNew();
                await Task.WhenAll(Enumerable.Range(0, workers).Select(w => Task.Run(async () =>
                {
                    for (var i = 0; i < 40; i++)
                    {
                        var t0 = Stopwatch.StartNew();
                        try { var r = await eng.Tasks.AddAsync(w % 2 == 0 ? "g1" : "g2", $"w{w} {i}", null, null, Rm); if (!r.IsOk) errors.AddOrUpdate("guard:" + r.Failures[0].Guard, 1, (_, n) => n + 1); }
                        catch (Exception ex) { errors.AddOrUpdate((ex.InnerException ?? ex).GetType().Name + ": " + Short((ex.InnerException ?? ex).Message), 1, (_, n) => n + 1); }
                        all.Add(t0.Elapsed.TotalMilliseconds);
                    }
                })));
                var s = sw.Elapsed.TotalSeconds; var st = Stats([.. all]);
                Say($"[{eng.Name}] {workers,2} concurrent writers x40: {workers * 40 / s,7:F0} writes/s, p50 {st.P50:F1} ms, p95 {st.P95:F1} ms, failed {errors.Values.Sum()}{(errors.IsEmpty ? "" : " (" + string.Join("; ", errors.Select(k => $"{k.Value}x {k.Key}")) + ")")}");
            }
        }

        // 3. pooling: what a connection costs, and what a too-small pool does under load
        var e1 = await fx.NewEnvAsync();
        foreach (var pooled in new[] { false, true })
        {
            var cs = new NpgsqlConnectionStringBuilder(e1.ConnectionString) { Pooling = pooled }.ConnectionString;
            var ms = new List<double>();
            for (var i = 0; i < 200; i++)
            {
                var sw = Stopwatch.StartNew();
                await using var c = new NpgsqlConnection(cs); await c.OpenAsync();
                await using var cmd = new NpgsqlCommand("SELECT 1", c); await cmd.ExecuteScalarAsync();
                ms.Add(sw.Elapsed.TotalMilliseconds);
            }
            var st = Stats(ms);
            Say($"[Postgres 16] open + SELECT 1 x200, Pooling={(pooled ? "true " : "false")}: p50 {st.P50:F2} ms, p95 {st.P95:F2} ms");
        }
        foreach (var max in new[] { 2, 4, 100 })
        {
            var eng = await NewPg($"Maximum Pool Size={max};Timeout=3;Pooling=true");
            var failed = 0; var sw = Stopwatch.StartNew();
            await Task.WhenAll(Enumerable.Range(0, 24).Select(w => Task.Run(async () =>
            {
                for (var i = 0; i < 15; i++)
                    try { await eng.Tasks.AddAsync("g1", $"p{w}-{i}", null, null, Rm); } catch (Exception) { Interlocked.Increment(ref failed); }
            })));
            Say($"[Postgres 16] 24 concurrent writers x15, Maximum Pool Size={max,3}: {24 * 15 / sw.Elapsed.TotalSeconds:F0} writes/s, failed {failed}");
        }

        // 4. the If-Match race through the real service, both engines
        foreach (var make in new Func<Task<Engine>>[] { () => Task.FromResult(NewSqlite()), () => NewPg() })
        {
            int ok = 0, conflict = 0, thrown = 0; var kinds = new HashSet<string>();
            for (var round = 0; round < 5; round++)
            {
                var eng = await make();
                var rs = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
                {
                    try { return (await eng.Trains.RecordRollbackRehearsedAsync("t1", Rte, 1)).Kind; }
                    catch (Exception ex) { lock (kinds) kinds.Add((ex.InnerException ?? ex).GetType().Name + ": " + Short((ex.InnerException ?? ex).Message)); return (ResultKind?)null; }
                })));
                ok += rs.Count(r => r == ResultKind.Ok); conflict += rs.Count(r => r == ResultKind.Conflict); thrown += rs.Count(r => r is null);
            }
            Say($"[{(await make()).Name}] If-Match race, 5 rounds x 16 callers (ideal: 5 ok, 75 conflict): ok {ok}, 409 {conflict}, exception {thrown}{(kinds.Count == 0 ? "" : " (" + string.Join("; ", kinds) + ")")}");
        }

        var outPath = Environment.GetEnvironmentVariable("REOS_SPIKE_REPORT");
        if (!string.IsNullOrEmpty(outPath)) await File.WriteAllTextAsync(outPath, report.ToString());
    }

    private static string Short(string s) { var l = s.ReplaceLineEndings(" "); return l.Length > 90 ? l[..90] : l; }
}
