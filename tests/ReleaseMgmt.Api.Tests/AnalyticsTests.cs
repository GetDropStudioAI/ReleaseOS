using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Infrastructure.Analytics;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// REOS-46 / M7 "Done when": every analytics endpoint, pointed at a COPY of tests/reference/fixtures/seed.db (built from schema.sql, never migrated),
/// returns exactly the rows in expected_metrics.json (numbers compared at 1 decimal place). One test per metric so a failure names it.
/// </summary>
public sealed class AnalyticsFixture : IDisposable
{
    public static readonly string Root = FindRoot();
    public string Dir { get; } = Directory.CreateTempSubdirectory("reos-analytics-").FullName;
    public string SeedCopy => Path.Combine(Dir, "seed.db");
    public string OriginalSeed { get; } = Path.Combine(Root, "tests", "reference", "fixtures", "seed.db");
    public JsonElement Expected { get; }
    public string OriginalHashBefore { get; }
    public FakeTimeProvider Time { get; } = new(DateTimeOffset.Parse("2026-09-28T12:00:00Z"));
    public Factory Web { get; }

    public AnalyticsFixture()
    {
        OriginalHashBefore = Hash(OriginalSeed);
        File.Copy(OriginalSeed, SeedCopy);
        Expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "tests", "reference", "fixtures", "expected_metrics.json"))).RootElement;
        Web = new Factory(this);
    }

    public static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "db", "analytics.sql"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("repo root (db/analytics.sql) not found above " + AppContext.BaseDirectory);
    }

    public void Dispose()
    {
        Web.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, true); } catch (IOException) { }
    }

    /// <summary>The app host (its own empty, migrated app DB for users and login) with analytics re-pointed at the seed copy and a fixed clock.</summary>
    public sealed class Factory(AnalyticsFixture fx) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            var web = Path.Combine(fx.Dir, "wwwroot");
            Directory.CreateDirectory(web);
            File.WriteAllText(Path.Combine(web, "index.html"), "<html>spa</html>");
            builder.UseWebRoot(web);
            builder.UseSetting("Db:Path", Path.Combine(fx.Dir, "app.db"));
            builder.UseSetting("Seed:Demo", "false");
            builder.UseSetting("Api:RequireIfMatch", "false");
            builder.UseSetting("Realtime:ServerTimeSeconds", "1");
            builder.UseSetting("Attachments:Directory", Path.Combine(fx.Dir, "attachments"));
            builder.UseSetting("Notifications:ScanSeconds", "3600");
            builder.UseSetting("Backup:Directory", Path.Combine(fx.Dir, "bk"));
            builder.UseSetting("Logging:File", Path.Combine(fx.Dir, "log-.txt"));
            builder.ConfigureServices(s =>
            {
                s.RemoveAll<IAnalyticsConnectionFactory>();
                s.AddSingleton<IAnalyticsConnectionFactory>(new SqliteAnalyticsConnectionFactory(fx.SeedCopy));
                s.RemoveAll<TimeProvider>();
                s.AddSingleton<TimeProvider>(fx.Time);
            });
        }
    }
}

public class AnalyticsTests(AnalyticsFixture fx) : IClassFixture<AnalyticsFixture>
{
    public static IEnumerable<object[]> Metrics() => Enumerable.Range(1, 15).Select(i => new object[] { $"M{i}" });

    private AnalyticsService Service => new(new SqliteAnalyticsConnectionFactory(fx.SeedCopy));

    private async Task<HttpClient> As(string role)
    {
        var c = fx.Web.CreateClient();
        (await c.PostAsJsonAsync("/auth/dev-login", new { email = $"{role}@x.com", name = role, role })).EnsureSuccessStatusCode();
        return c;
    }

    private static string Norm(string s) => s.Replace("_", "").ToLowerInvariant();

    private (string Name, JsonElement Metric) Expected(string key)
    {
        foreach (var m in fx.Expected.GetProperty("metrics").EnumerateObject())
            if (m.Value.GetProperty("key").GetString() == key) return (m.Name, m.Value);
        throw new KeyNotFoundException(key);
    }

    private AnalyticsWindow ExpectedWindow()
    {
        var p = fx.Expected.GetProperty("params");
        return new AnalyticsWindow(DateOnly.Parse(p.GetProperty("from").GetString()!), DateOnly.Parse(p.GetProperty("to").GetString()!), DateTime.Parse(p.GetProperty("now").GetString()!).ToUniversalTime());
    }

    private static string QueryString(JsonElement p) => $"from={p.GetProperty("from").GetString()}&to={p.GetProperty("to").GetString()}&now={Uri.EscapeDataString(p.GetProperty("now").GetString()!)}";

    /// <summary>Compares rows (each a JSON object whose property order is the column order, or an array) with the oracle's rows, value by value; doubles at 1 decimal.</summary>
    private static void AssertRows(string key, JsonElement expected, IReadOnlyList<JsonElement> actual)
    {
        var cols = expected.GetProperty("columns").EnumerateArray().Select(c => c.GetString()!).ToArray();
        var rows = expected.GetProperty("rows").EnumerateArray().ToArray();
        Assert.True(rows.Length == actual.Count, $"{key}: expected {rows.Length} rows, got {actual.Count}");
        for (var r = 0; r < rows.Length; r++)
        {
            var props = actual[r].EnumerateObject().ToArray();
            Assert.True(props.Length == cols.Length, $"{key} row {r}: expected {cols.Length} columns, got {props.Length}");
            for (var c = 0; c < cols.Length; c++)
            {
                Assert.True(Norm(cols[c]) == Norm(props[c].Name), $"{key} row {r}: column {c} is {props[c].Name}, expected {cols[c]}");
                AssertValue($"{key} row {r} {cols[c]}", rows[r][c], props[c].Value);
            }
        }
    }

    private static void AssertValue(string what, JsonElement expected, JsonElement actual)
    {
        switch (expected.ValueKind)
        {
            case JsonValueKind.Null: Assert.True(actual.ValueKind == JsonValueKind.Null, $"{what}: expected null, got {actual}"); break;
            case JsonValueKind.String: Assert.Equal(expected.GetString(), actual.GetString()); break;
            case JsonValueKind.Number:
                Assert.True(actual.ValueKind == JsonValueKind.Number, $"{what}: expected number {expected}, got {actual}");
                Assert.True(Math.Round(expected.GetDouble(), 1) == Math.Round(actual.GetDouble(), 1), $"{what}: expected {expected}, got {actual}");
                break;
            default: throw new NotSupportedException($"{what}: {expected.ValueKind}");
        }
    }

    /// <summary>Typed service rows serialised the way the endpoint does, so both paths are checked by the same comparer.</summary>
    private static IReadOnlyList<JsonElement> Serialise(IEnumerable<object> rows) =>
        JsonSerializer.SerializeToElement(rows, new JsonSerializerOptions(JsonSerializerDefaults.Web)).EnumerateArray().ToArray();

    // ---- acceptance: one test per metric, through the HTTP endpoint and through the service -----------------------------------------------
    [Theory, MemberData(nameof(Metrics))]
    public async Task Endpoint_returns_exactly_the_expected_rows(string key)
    {
        var (_, metric) = Expected(key);
        var client = await As(Roles.Viewer);
        var res = await client.GetAsync($"/api/v1/analytics/{key.ToLowerInvariant()}?{QueryString(fx.Expected.GetProperty("params"))}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(key, body.GetProperty("metric").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("unit").GetString()));
        Assert.Equal("2026-01-01", body.GetProperty("from").GetString());
        Assert.Equal("2026-12-31", body.GetProperty("to").GetString());
        Assert.Equal("2026-09-28T12:00:00Z", body.GetProperty("asOf").GetString());
        AssertRows(key, metric, body.GetProperty("rows").EnumerateArray().ToArray());
    }

    [Theory, MemberData(nameof(Metrics))]
    public async Task Service_returns_exactly_the_expected_rows(string key)
    {
        var (_, metric) = Expected(key);
        var rows = await Service.RunAsync(key, ExpectedWindow());
        AssertRows(key, metric, Serialise(rows));
    }

    [Fact]
    public void The_catalog_covers_every_query_in_the_oracle_in_order()
    {
        var names = fx.Expected.GetProperty("metrics").EnumerateObject().Select(m => (m.Value.GetProperty("key").GetString()!, m.Name)).ToArray();
        Assert.Equal(names, AnalyticsService.Catalog.Select(m => (m.Id, m.Name)).ToArray());
        foreach (var m in AnalyticsService.Catalog)
            Assert.Equal(Expected(m.Id).Metric.GetProperty("columns").EnumerateArray().Select(c => c.GetString()), m.Columns.Select(c => c.Name));
    }

    [Fact]
    public void Embedded_sql_is_db_analytics_sql()
    {
        var onDisk = AnalyticsSql.Parse(File.ReadAllText(Path.Combine(AnalyticsFixture.Root, "db", "analytics.sql")));
        Assert.Equal(15, onDisk.Count);
        foreach (var (name, sql) in onDisk) Assert.Equal(sql, AnalyticsSql.Get(name));
        Assert.Equal(onDisk.Keys.OrderBy(x => x), AnalyticsSql.Names.OrderBy(x => x));
    }

    // ---- parameter binding ------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task From_and_to_narrow_the_result_as_the_sql_says()
    {
        var m2 = Expected("M2").Metric.GetProperty("rows").EnumerateArray().Select(r => r[2].GetString()!).ToArray();
        var june = m2.Count(a => string.CompareOrdinal(a, "2026-06-01") >= 0 && string.CompareOrdinal(a, "2026-06-30") <= 0);
        Assert.InRange(june, 1, m2.Length - 1);
        var w = ExpectedWindow() with { From = new DateOnly(2026, 6, 1), To = new DateOnly(2026, 6, 30) };
        Assert.Equal(june, (await Service.M1Async(w))[0].Completed);
        Assert.Equal(june, (await Service.M2Async(w)).Count);

        var client = await As(Roles.RTE);
        var body = JsonDocument.Parse(await client.GetStringAsync("/api/v1/analytics/M1?from=2026-06-01&to=2026-06-30&now=2026-09-28T12:00:00Z")).RootElement;
        Assert.Equal(june, body.GetProperty("rows")[0].GetProperty("completed").GetInt64());

        var none = await Service.M2Async(ExpectedWindow() with { From = new DateOnly(2030, 1, 1), To = new DateOnly(2030, 1, 2) });
        Assert.Empty(none);
        var empty = (await Service.M1Async(ExpectedWindow() with { From = new DateOnly(2030, 1, 1), To = new DateOnly(2030, 1, 2) }))[0];
        Assert.Equal(new M1Row(0, null, null), empty);   // an empty window is null percentages, never 0 or a crash
    }

    [Fact]
    public async Task Now_is_bound_as_the_as_of_time()
    {
        var low = Expected("M9").Metric.GetProperty("rows").EnumerateArray().First(r => r[0].GetString() == "Low");
        var later = await Service.M9Async(ExpectedWindow() with { Now = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc) });
        Assert.Equal(Math.Round(low[5].GetDouble() + 1, 1), later.First(r => r.Severity == "Low").OldestDays);
    }

    [Fact]
    public async Task Defaults_are_the_last_180_days_up_to_the_injected_clock()
    {
        var client = await As(Roles.Viewer);
        var body = JsonDocument.Parse(await client.GetStringAsync("/api/v1/analytics/M1")).RootElement;
        Assert.Equal("2026-09-28T12:00:00Z", body.GetProperty("asOf").GetString());
        Assert.Equal("2026-09-28", body.GetProperty("to").GetString());
        Assert.Equal("2026-04-01", body.GetProperty("from").GetString());
    }

    [Theory]
    [InlineData("from=2026-01-01'%20OR%20'1'='1&to=2026-12-31")]
    [InlineData("from=2026-01-01&to=2026-12-31'%3B%20DROP%20TABLE%20ReleaseTrains%3B--")]
    [InlineData("from=2026-01-01&to=2026-12-31&now=x'%3B%20DELETE%20FROM%20Blockers%3B--")]
    [InlineData("from=2026-12-31&to=2026-01-01")]
    [InlineData("from=01/02/2026")]
    [InlineData("from=2026-02-30")]
    [InlineData("to=tomorrow")]
    [InlineData("train=t1")]
    public async Task Bad_filters_are_400_InvalidFilter_and_never_reach_the_database(string query)
    {
        var client = await As(Roles.RTE);
        var res = await client.GetAsync("/api/v1/analytics/m1?" + query);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("InvalidFilter", JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("guard").GetString());
        Assert.Equal(fx.OriginalHashBefore, AnalyticsFixture.Hash(fx.SeedCopy));
    }

    [Fact]
    public async Task An_unknown_metric_is_404_and_a_query_name_is_accepted()
    {
        var client = await As(Roles.Viewer);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/analytics/m16")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/analytics/x'%20OR%201=1")).StatusCode);
        var body = JsonDocument.Parse(await client.GetStringAsync("/api/v1/analytics/on_time_rate?now=2026-09-28T12:00:00Z")).RootElement;
        Assert.Equal("M1", body.GetProperty("metric").GetString());
    }

    [Fact]
    public async Task The_index_lists_the_fifteen_metrics()
    {
        var client = await As(Roles.Viewer);
        var idx = JsonDocument.Parse(await client.GetStringAsync("/api/v1/analytics")).RootElement;
        Assert.Equal(Enumerable.Range(1, 15).Select(i => $"M{i}"), idx.EnumerateArray().Select(m => m.GetProperty("id").GetString()));
        Assert.All(idx.EnumerateArray(), m =>
        {
            Assert.False(string.IsNullOrEmpty(m.GetProperty("title").GetString()));
            Assert.False(string.IsNullOrEmpty(m.GetProperty("unit").GetString()));
            Assert.True(m.GetProperty("columns").GetArrayLength() >= 3);
        });
    }

    // ---- roles ------------------------------------------------------------------------------------------------------------------------
    [Theory]
    [InlineData("/api/v1/analytics")]
    [InlineData("/api/v1/analytics/m1")]
    [InlineData("/api/v1/analytics/m14")]
    public async Task Anonymous_is_401_and_every_signed_in_role_may_read(string url)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.Web.CreateClient().GetAsync(url)).StatusCode);
        foreach (var role in Roles.All) Assert.Equal(HttpStatusCode.OK, (await (await As(role)).GetAsync(url)).StatusCode);
    }

    // ---- read-only and untouched ------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task The_analytics_connection_cannot_write()
    {
        var factory = new SqliteAnalyticsConnectionFactory(fx.SeedCopy);
        await using var c = await factory.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM Blockers";
        Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
        cmd.CommandText = "PRAGMA query_only";
        Assert.Equal(1L, cmd.ExecuteScalar());
    }

    [Fact]
    public async Task Running_every_metric_never_modifies_the_seed_and_the_seed_is_not_migrated()
    {
        foreach (var i in Enumerable.Range(1, 15)) await Service.RunAsync($"M{i}", ExpectedWindow());
        foreach (var i in Enumerable.Range(1, 15)) Assert.Equal(HttpStatusCode.OK, (await (await As(Roles.RTE)).GetAsync($"/api/v1/analytics/m{i}")).StatusCode);
        Assert.Equal(fx.OriginalHashBefore, AnalyticsFixture.Hash(fx.SeedCopy));
        Assert.Equal(fx.OriginalHashBefore, AnalyticsFixture.Hash(fx.OriginalSeed));   // and the checked-in fixture itself
        using var c = new SqliteConnection($"Data Source={fx.SeedCopy};Mode=ReadOnly;Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE name = '__EFMigrationsHistory'";
        Assert.Equal(0L, cmd.ExecuteScalar());
    }
}
