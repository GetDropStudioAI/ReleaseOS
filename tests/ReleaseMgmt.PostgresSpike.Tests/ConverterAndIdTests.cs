using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using Xunit.Abstractions;

namespace ReleaseMgmt.PostgresSpike.Tests;

/// <summary>CLAUDE.md rules 5 and 6 on Postgres: timestamps are UTC ISO-8601 TEXT through the existing converters; ids are UUIDv7 TEXT.</summary>
public sealed class ConverterAndIdTests(PgFixture fx, ITestOutputHelper o) : IClassFixture<PgFixture>
{
    [PgFact]
    public async Task Timestamps_and_dates_are_stored_as_the_same_ISO_text_with_no_Npgsql_specific_converter()
    {
        var e = await fx.NewEnvAsync();
        // a Local-kind value with sub-second ticks: the converter must emit UTC, whole seconds, 'Z' (triggers compare these strings)
        var local = new DateTime(2026, 10, 20, 16, 30, 5, 987, DateTimeKind.Utc).ToLocalTime();
        await using (var db = e.Db.CreateDbContext())
        {
            db.Set<ReleaseTrains>().Add(new ReleaseTrains { Id = "t2", Title = "Converter probe", TargetReleaseDate = new DateOnly(2026, 12, 31), CreatedAt = local, UpdatedAt = local, ActualStartAt = local });
            await db.SaveChangesAsync();
        }
        Assert.Equal("2026-10-20T16:30:05Z", e.Scalar("SELECT \"CreatedAt\" FROM \"ReleaseTrains\" WHERE \"Id\"='t2'"));
        Assert.Equal("2026-12-31", e.Scalar("SELECT \"TargetReleaseDate\" FROM \"ReleaseTrains\" WHERE \"Id\"='t2'"));
        Assert.Equal("text", e.Scalar("SELECT data_type FROM information_schema.columns WHERE table_name='ReleaseTrains' AND column_name='CreatedAt'"));
        await using var db2 = e.Db.CreateDbContext();
        var t = await db2.Set<ReleaseTrains>().AsNoTracking().SingleAsync(x => x.Id == "t2");
        Assert.Equal(DateTimeKind.Utc, t.CreatedAt.Kind);
        Assert.Equal(new DateTime(2026, 10, 20, 16, 30, 5, DateTimeKind.Utc), t.CreatedAt);
        Assert.Equal(new DateOnly(2026, 12, 31), t.TargetReleaseDate);
    }

    [PgFact]
    public async Task Text_timestamps_compare_and_range_filter_in_SQL_the_way_the_triggers_and_the_analytics_need()
    {
        var e = await fx.NewEnvAsync();
        await using var db = e.Db.CreateDbContext();
        var from = new DateTime(2026, 10, 20, 14, 0, 0, DateTimeKind.Utc);
        // translated to a text comparison on the column (a converter-aware predicate), not pulled client-side
        var q = db.Set<AuditEvents>().Where(a => a.OccurredAt >= from && a.OccurredAt < from.AddDays(1));
        var sql = q.ToQueryString();
        o.WriteLine(sql.ReplaceLineEndings(" "));
        Assert.Contains("\"OccurredAt\" >=", sql);
        await q.ToListAsync();
        // ordering: ISO text sorts as time, and 'T…Z' form is what the contract's CHECKs and the audit sealer rely on
        Assert.True(string.CompareOrdinal("2026-10-20T14:00:05Z", "2026-10-20T14:00:05.123Z") > 0, "the reason sub-second values are banned: '…05.123Z' sorts before '…05Z'");
    }

    [PgFact]
    public async Task UUIDv7_text_ids_sort_in_creation_order_under_ordinal_and_ICU_collations_alike()
    {
        var e = await fx.NewEnvAsync();
        var ids = new List<string>();
        for (var i = 0; i < 3000; i++) { ids.Add(Guid.CreateVersion7(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(i * 7)).ToString()); }
        Assert.All(ids, id => Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", id));
        await using var c = e.Open();
        await using (var cmd = c.CreateCommand()) { cmd.CommandText = "CREATE TEMP TABLE x(id text)"; await cmd.ExecuteNonQueryAsync(); }
        await using (var cmd = c.CreateCommand()) { cmd.CommandText = "INSERT INTO x SELECT unnest($1::text[])"; cmd.Parameters.AddWithValue(ids.OrderBy(_ => Guid.NewGuid()).ToArray()); await cmd.ExecuteNonQueryAsync(); }
        async Task<List<string>> Order(string collate)
        {
            await using var q = c.CreateCommand(); q.CommandText = $"SELECT id FROM x ORDER BY id {collate}";
            await using var r = await q.ExecuteReaderAsync(); var l = new List<string>(); while (await r.ReadAsync()) l.Add(r.GetString(0)); return l;
        }
        var dbDefault = await Order("");
        var icu = await Order("COLLATE \"und-x-icu\"");
        var ordinal = await Order("COLLATE \"C\"");
        Assert.Equal(ids, ordinal);
        Assert.Equal(ids, icu);
        Assert.Equal(ids, dbDefault);
        o.WriteLine($"database collation: {e.Scalar("SELECT datcollate FROM pg_database WHERE datname=current_database()")}; 3000 UUIDv7 ids ordered identically under default, C and ICU");
        // rows made by triggers use 32 random hex chars, no hyphens (rule 6 exception): they never sort among v7 ids by time, same as on SQLite
        Assert.Equal(32, ((string)e.Scalar("SELECT replace(gen_random_uuid()::text,'-','')")!).Length);
    }

    [PgFact]
    public void Application_ids_are_UUIDv7_and_the_trigger_made_ids_are_the_documented_exception()
    {
        Assert.True(Guid.TryParseExact(Ids.New(), "D", out var g));
        Assert.Equal(7, g.Version);
        // rows made by triggers (PIR, baseline) take 32 hex chars from gen_random_uuid() with the hyphens stripped: the oracle generator's translation of lower(hex(randomblob(16)))
        // (the PIR and baseline cascades themselves are exercised by SliceServiceTests)
    }
}
