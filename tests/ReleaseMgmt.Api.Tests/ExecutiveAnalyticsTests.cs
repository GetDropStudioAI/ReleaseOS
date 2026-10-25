using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Infrastructure.Analytics;
using ReleaseMgmt.Infrastructure.Reminders;

namespace ReleaseMgmt.Api.Tests;

/// <summary>Compares rows with the oracle's (tests/reference/seed_and_query.py), value by value; doubles at one decimal.</summary>
internal static class OracleRows
{
    private static string Norm(string s) => s.Replace("_", "").ToLowerInvariant();

    public static void Assert_(string what, JsonElement expected, IReadOnlyList<JsonElement> actual)
    {
        var cols = expected.GetProperty("columns").EnumerateArray().Select(c => c.GetString()!).ToArray();
        var rows = expected.GetProperty("rows").EnumerateArray().ToArray();
        Assert.True(rows.Length == actual.Count, $"{what}: expected {rows.Length} rows, got {actual.Count}");
        for (var r = 0; r < rows.Length; r++)
        {
            var props = actual[r].EnumerateObject().ToArray();
            Assert.True(props.Length == cols.Length, $"{what} row {r}: expected {cols.Length} columns, got {props.Length}");
            for (var c = 0; c < cols.Length; c++)
            {
                Assert.True(Norm(cols[c]) == Norm(props[c].Name), $"{what} row {r}: column {c} is {props[c].Name}, expected {cols[c]}");
                Value($"{what} row {r} {cols[c]}", rows[r][c], props[c].Value);
            }
        }
    }

    private static void Value(string what, JsonElement expected, JsonElement actual)
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

    public static IReadOnlyList<JsonElement> Serialise(IEnumerable<object> rows) =>
        JsonSerializer.SerializeToElement(rows, new JsonSerializerOptions(JsonSerializerDefaults.Web)).EnumerateArray().ToArray();
}

/// <summary>
/// REOS-121 (RT6) acceptance. M16..M24 reproduce the oracle's rows for every case in expected_metrics.json (workspace sets, groupings, a fiscal year), through the service and through the
/// endpoint; and an endpoint figure never includes a workspace the person is not a member of: a person who belongs to two of the three workspaces gets exactly the oracle's two-workspace
/// figures, and asking for the third is a 404 that does not say whether it exists (D49).
/// </summary>
public class ExecutiveAnalyticsTests(AnalyticsFixture fx) : IClassFixture<AnalyticsFixture>
{
    private const string Window = "from=2026-01-01&to=2026-12-31&now=2026-09-28T12%3A00%3A00Z";
    private static readonly string[] Executive = Enumerable.Range(16, 9).Select(i => $"M{i}").ToArray();

    private static void Same(IEnumerable<string?> expected, IEnumerable<string?> actual) => Assert.Equal(string.Join(" | ", expected), string.Join(" | ", actual));

    private JsonElement Cases => fx.Expected.GetProperty("executive").GetProperty("cases");
    private JsonElement Case(string name) => Cases.EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);
    public static IEnumerable<object[]> CaseNames() => [["portfolio"], ["workspace and quarter"], ["two of three workspaces, by team and type"], ["fiscal year from October, by segment and version"], ["one workspace"]];

    private AnalyticsService Service => new(new SqliteAnalyticsConnectionFactory(fx.SeedCopy));

    private AnalyticsWindow WindowOf(JsonElement c)
    {
        var p = fx.Expected.GetProperty("executive").GetProperty("params");
        return new AnalyticsWindow(DateOnly.Parse(p.GetProperty("from").GetString()!), DateOnly.Parse(p.GetProperty("to").GetString()!), DateTime.Parse(p.GetProperty("now").GetString()!).ToUniversalTime())
        {
            Workspaces = c.GetProperty("workspaces").EnumerateArray().Select(x => x.GetString()!).ToArray(),
            GroupBy = c.GetProperty("groupBy").EnumerateArray().Select(x => x.GetString()!).ToArray(),
            FiscalYearStartMonth = c.GetProperty("fiscalYearStartMonth").GetInt32(),
            LocalDay = DateOnly.Parse(p.GetProperty("localDay").GetString()!),
            PirDueJson = p.GetProperty("pirDue").GetRawText(),
        };
    }

    [Fact]
    public void The_scenario_gives_every_executive_metric_rows()
    {
        foreach (var m in Case("portfolio").GetProperty("metrics").EnumerateObject()) Assert.True(m.Value.GetProperty("rows").GetArrayLength() > 0, $"{m.Name} has no rows in the portfolio case");
        Assert.Equal(Executive, Case("portfolio").GetProperty("metrics").EnumerateObject().Select(m => m.Value.GetProperty("key").GetString()));
    }

    [Theory, MemberData(nameof(CaseNames))]
    public async Task The_service_returns_exactly_the_oracles_rows_for_every_case(string name)
    {
        var c = Case(name);
        foreach (var m in c.GetProperty("metrics").EnumerateObject())
        {
            var key = m.Value.GetProperty("key").GetString()!;
            OracleRows.Assert_($"{name} / {key}", m.Value, OracleRows.Serialise(await Service.RunAsync(key, WindowOf(c))));
        }
    }

    // ---- through the endpoint, as people with different memberships ---------------------------------------------------------------------------------

    private void AppSql(string sql) => Members.AppSql(fx, sql);
    private string Uid(string email) => Members.Uid(fx, email);

    /// <summary>Signs a person in (a Viewer) and makes them a member of exactly the given workspaces, creating Cards and Payments in the app database as needed.</summary>
    private Task<HttpClient> Member(string email, params string[] workspaces) => Members.SignInAsync(fx, fx.Web, email, workspaces);

    private async Task<JsonElement> Get(HttpClient c, string url)
    {
        var res = await c.GetAsync(url);
        Assert.True(res.StatusCode == HttpStatusCode.OK, $"{url}: {(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
    }

    private async Task AssertCase(HttpClient c, string query, string caseName, string? header = null)
    {
        if (header is not null) c.DefaultRequestHeaders.Add("X-Workspace", header);
        foreach (var m in Case(caseName).GetProperty("metrics").EnumerateObject())
        {
            var key = m.Value.GetProperty("key").GetString()!;
            var body = await Get(c, $"/api/v1/analytics/{key}?{Window}&{query}");
            OracleRows.Assert_($"{caseName} / {key} over HTTP", m.Value, body.GetProperty("rows").EnumerateArray().ToArray());
        }
    }

    [Fact]
    public async Task A_person_in_all_three_workspaces_reads_the_portfolio_and_each_grouping()
    {
        var c = await Member("vp-all@x.com", "default", "ws-cards", "ws-pay");
        await AssertCase(c, "workspaces=all", "portfolio");
        await AssertCase(c, "workspaces=all&groupBy=quarter,workspace", "workspace and quarter");   // the order asked for does not matter
    }

    [Fact]
    public async Task The_fiscal_year_start_is_a_setting_and_names_the_quarters()
    {
        using var fiscal = fx.Web.WithWebHostBuilder(b => b.UseSetting(AnalyticsOptions.FiscalYearKey, "10"));
        var client = fiscal.CreateClient();
        (await client.PostAsJsonAsync("/auth/dev-login", new { email = "vp-fiscal@x.com", name = "vp", role = "Viewer" })).EnsureSuccessStatusCode();
        var uid = Uid("vp-fiscal@x.com");
        AppSql($"""
            INSERT OR IGNORE INTO Workspaces(Id,Name,Slug,CreatedAt) VALUES('ws-cards','Cards','cards','2026-03-01T00:00:00Z');
            INSERT OR IGNORE INTO Workspaces(Id,Name,Slug,CreatedAt) VALUES('ws-pay','Payments','payments','2026-03-01T00:00:00Z');
            DELETE FROM WorkspaceMembers WHERE UserId = '{uid}';
            INSERT INTO WorkspaceMembers(WorkspaceId,UserId,Role,AddedAt) VALUES('default','{uid}','Viewer','2026-03-01T00:00:00Z'),('ws-cards','{uid}','Viewer','2026-03-01T00:00:00Z'),('ws-pay','{uid}','Viewer','2026-03-01T00:00:00Z');
            """);
        await AssertCase(client, "workspaces=all&groupBy=segment,version", "fiscal year from October, by segment and version");
        var scope = JsonDocument.Parse(await client.GetStringAsync("/api/v1/analytics/scope")).RootElement;
        Assert.Equal(10, scope.GetProperty("fiscalYearStartMonth").GetInt32());
        Assert.Equal("fiscal", scope.GetProperty("quarters").GetString());
        var q = JsonDocument.Parse(await client.GetStringAsync($"/api/v1/analytics/M22?{Window}&workspaces=all&groupBy=quarter")).RootElement.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("quarter").GetString()!).ToArray();
        Assert.All(q, label => Assert.StartsWith("FY2026-Q", label));   // April to September 2026 is the third and fourth quarter of the year that ends in 2026 when it starts in October
        Assert.Equal(["FY2026-Q3", "FY2026-Q4"], q.Distinct().Order().ToArray());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("13")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("October")]
    public void A_fiscal_year_start_that_is_not_a_month_stops_the_start_and_names_the_key(string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => AnalyticsOptions.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [AnalyticsOptions.FiscalYearKey] = value }).Build()));
        Assert.Contains("Analytics:FiscalYearStartMonth", ex.Message);
        var host = Assert.ThrowsAny<Exception>(() => fx.Web.WithWebHostBuilder(b => b.UseSetting(AnalyticsOptions.FiscalYearKey, value)).CreateClient());
        Assert.Contains("Analytics:FiscalYearStartMonth", host.ToString());
    }

    [Theory]
    [InlineData(1, "2026-01-01", "2026-Q1")]
    [InlineData(1, "2026-09-28", "2026-Q3")]
    [InlineData(1, "2026-12-31", "2026-Q4")]
    [InlineData(10, "2026-09-30", "FY2026-Q4")]
    [InlineData(10, "2026-10-01", "FY2027-Q1")]
    [InlineData(10, "2027-01-15", "FY2027-Q2")]
    [InlineData(4, "2026-03-31", "FY2026-Q4")]
    [InlineData(4, "2026-04-01", "FY2027-Q1")]
    [InlineData(12, "2026-12-01", "FY2027-Q1")]
    public void Quarters_are_labelled_the_way_the_sql_labels_them(int start, string day, string label) =>
        Assert.Equal(label, new AnalyticsOptions(start).QuarterOf(DateOnly.Parse(day)));

    [Fact]
    public void The_default_is_calendar_quarters()
    {
        Assert.Equal(1, AnalyticsOptions.From(new ConfigurationBuilder().Build()).FiscalYearStartMonth);
        Assert.Equal(12, AnalyticsOptions.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [AnalyticsOptions.FiscalYearKey] = " 12 " }).Build()).FiscalYearStartMonth);
    }

    [Fact]
    public async Task A_person_in_two_of_three_workspaces_gets_exactly_the_two_workspace_figures_and_a_404_for_the_third()
    {
        var c = await Member("director-two@x.com", "default", "ws-cards");
        await AssertCase(c, "workspaces=all&groupBy=team,type", "two of three workspaces, by team and type");
        await AssertCase(c, "workspaces=default,cards&groupBy=type,team", "two of three workspaces, by team and type");   // by slug or id, any order

        foreach (var asked in new[] { "workspaces=payments", "workspaces=ws-pay", "workspaces=default,ws-cards,ws-pay", "workspaces=cards,payments" })
        {
            var res = await c.GetAsync($"/api/v1/analytics/M17?{Window}&{asked}");
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
            var body = await res.Content.ReadAsStringAsync();
            Assert.Equal("WorkspaceNotFound", JsonDocument.Parse(body).RootElement.GetProperty("guard").GetString());
            Assert.DoesNotContain("Payments", body);
            Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/v1/analytics/xlsx?{Window}&{asked}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/v1/analytics/portfolio.pdf?{Window}&{asked}")).StatusCode);
        }

        // the response says which workspaces it covers, and the third is not among them; no figure of Payments is in the totals
        var m22 = await Get(c, $"/api/v1/analytics/M22?{Window}&workspaces=all");
        Same(["Cards", "Default"], m22.GetProperty("workspaces").EnumerateArray().Select(w => w.GetProperty("name").GetString()!).Order().ToArray());
        var two = m22.GetProperty("rows").EnumerateArray().Sum(r => r.GetProperty("completed").GetInt64());
        var all = Case("portfolio").GetProperty("metrics").GetProperty("throughput_by_group").GetProperty("rows").EnumerateArray().Sum(r => r[6].GetInt64());
        var pay = Case("one workspace").GetProperty("metrics").GetProperty("throughput_by_group").GetProperty("rows").EnumerateArray().Sum(r => r[6].GetInt64());
        Assert.True(pay > 0);
        Assert.Equal(all - pay, two);
    }

    [Fact]
    public async Task A_person_in_one_workspace_sees_only_it_and_cannot_name_another()
    {
        var c = await Member("director-pay@x.com", "ws-pay");
        await AssertCase(c, "workspaces=all&groupBy=workspace,quarter", "one workspace");
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/v1/analytics/M17?{Window}&workspaces=default")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/v1/analytics/M17?{Window}&workspaces=cards")).StatusCode);
    }

    [Fact]
    public async Task Without_workspaces_only_the_current_workspace_is_read_as_before()
    {
        var c = await Member("viewer-current@x.com", "default", "ws-cards", "ws-pay");
        var solo = await Service.RunAsync("M22", WindowOf(Case("portfolio")) with { Workspaces = ["default"] });
        var body = await Get(c, $"/api/v1/analytics/M22?{Window}");
        Assert.Equal(solo.Cast<M22Row>().Sum(r => r.Completed), body.GetProperty("rows").EnumerateArray().Sum(r => r.GetProperty("completed").GetInt64()));
        Same(["Default"], body.GetProperty("workspaces").EnumerateArray().Select(w => w.GetProperty("name").GetString()).ToArray());
    }

    [Theory]
    [InlineData("groupBy=bogus")]
    [InlineData("groupBy=workspace;DROP")]
    [InlineData("workspaces=")]
    public async Task A_bad_grouping_is_400_InvalidFilter(string query)
    {
        var c = await Member("viewer-bad@x.com", "default", "ws-cards");
        var res = await c.GetAsync($"/api/v1/analytics/M17?{Window}&{query}");
        if (query == "workspaces=") { Assert.Equal(HttpStatusCode.OK, res.StatusCode); return; }   // an empty value is not a request for anything: the current workspace
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("InvalidFilter", JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("guard").GetString());
    }

    [Fact]
    public async Task Grouping_applies_to_the_executive_metrics_only()
    {
        var c = await Member("viewer-m1@x.com", "default");
        var res = await c.GetAsync($"/api/v1/analytics/M1?{Window}&groupBy=quarter");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("InvalidFilter", JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("guard").GetString());
        // the classic metrics also read the set: M1 over two workspaces is the sum of the two
        var cards = await Member("viewer-m1b@x.com", "default", "ws-cards");
        var one = await Get(cards, $"/api/v1/analytics/M1?{Window}");
        var both = await Get(cards, $"/api/v1/analytics/M1?{Window}&workspaces=all");
        Assert.True(both.GetProperty("rows")[0].GetProperty("completed").GetInt64() > one.GetProperty("rows")[0].GetProperty("completed").GetInt64());
    }

    [Fact]
    public async Task The_scope_endpoint_lists_only_the_persons_own_workspaces_and_how_quarters_count()
    {
        var c = await Member("viewer-scope@x.com", "default", "ws-cards");
        var body = await Get(c, "/api/v1/analytics/scope");
        Same(["cards", "default"], body.GetProperty("workspaces").EnumerateArray().Select(w => w.GetProperty("slug").GetString()!).Order().ToArray());
        Assert.Equal(1, body.GetProperty("fiscalYearStartMonth").GetInt32());
        Assert.Equal("calendar", body.GetProperty("quarters").GetString());
        Same(["workspace", "team", "type", "version", "segment", "quarter"], body.GetProperty("dimensions").EnumerateArray().Select(d => d.GetString()).ToArray());
    }

    [Fact]
    public async Task The_catalog_marks_the_executive_and_the_as_of_metrics()
    {
        var c = await Member("viewer-index@x.com", "default");
        var idx = await Get(c, "/api/v1/analytics");
        Same(Executive, idx.EnumerateArray().Where(m => m.GetProperty("grouped").GetBoolean()).Select(m => m.GetProperty("id").GetString()).ToArray());
        Same(["M6", "M9", "M14", "M21"], idx.EnumerateArray().Where(m => m.GetProperty("snapshot").GetBoolean()).Select(m => m.GetProperty("id").GetString()).ToArray());
    }
}

/// <summary>People with chosen workspace memberships, for the analytics tests: the app database is separate from the seed copy the analytics read, so memberships are written to it directly.</summary>
internal static class Members
{
    public static void AppSql(AnalyticsFixture fx, string sql)
    {
        using var c = new SqliteConnection($"Data Source={Path.Combine(fx.Dir, "app.db")};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=5000; " + sql;
        cmd.ExecuteNonQuery();
    }

    public static string Uid(AnalyticsFixture fx, string email)
    {
        using var c = new SqliteConnection($"Data Source={Path.Combine(fx.Dir, "app.db")};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Id FROM Users WHERE Email = $e";
        cmd.Parameters.AddWithValue("$e", email);
        return (string)cmd.ExecuteScalar()!;
    }

    public static async Task<HttpClient> SignInAsync(AnalyticsFixture fx, Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> web, string email, params string[] workspaces)
    {
        var client = web.CreateClient();
        (await client.PostAsJsonAsync("/auth/dev-login", new { email, name = email, role = "Viewer" })).EnsureSuccessStatusCode();
        var uid = Uid(fx, email);
        AppSql(fx, """
            INSERT OR IGNORE INTO Workspaces(Id,Name,Slug,CreatedAt) VALUES('ws-cards','Cards','cards','2026-03-01T00:00:00Z');
            INSERT OR IGNORE INTO Workspaces(Id,Name,Slug,CreatedAt) VALUES('ws-pay','Payments','payments','2026-03-01T00:00:00Z');
            """);
        AppSql(fx, $"DELETE FROM WorkspaceMembers WHERE UserId = '{uid}'");
        foreach (var w in workspaces) AppSql(fx, $"INSERT INTO WorkspaceMembers(WorkspaceId,UserId,Role,AddedAt) VALUES('{w}','{uid}','Viewer','2026-03-01T00:00:00Z')");
        return client;
    }
}
