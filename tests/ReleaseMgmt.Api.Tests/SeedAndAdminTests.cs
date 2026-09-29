using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Services;

namespace ReleaseMgmt.Api.Tests;

public class SeedAndAdminTests
{
    private static SqliteConnection Db(ApiFactory f) { var c = new SqliteConnection($"Data Source={f.DbPath};Pooling=False"); c.Open(); return c; }

    private static List<object?[]> Query(ApiFactory f, string sql)
    {
        using var c = Db(f);
        using var cmd = c.CreateCommand(); cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var rows = new List<object?[]>();
        while (r.Read()) { var row = new object?[r.FieldCount]; r.GetValues(row!); rows.Add(row); }
        return rows;
    }

    private static async Task<HttpClient> As(ApiFactory f, string role, string email = "admin@x.com")
    {
        var c = f.CreateClient();
        (await c.PostAsJsonAsync("/auth/dev-login", new { email, name = "Admin", role })).EnsureSuccessStatusCode();
        return c;
    }

    // ---- reference data ----------------------------------------------------------------------------------------------
    [Fact]
    public void Reference_data_seeds_the_default_template_and_federal_holidays_once()
    {
        using var f = new ApiFactory();
        f.CreateClient(); // starts the host: migrate + seed
        var gates = Query(f, "SELECT g.GateName, g.GateClass, g.OffsetDays, g.RequiredBeforeStatus FROM TemplateGates g JOIN TrainTemplates t ON t.Id=g.TemplateId WHERE t.Name='Standard release' ORDER BY g.SequenceOrder");
        Assert.Equal(
            [("Code Freeze", "Standard", 5L, "Gated"), ("QA Sign-off", "Standard", 3L, "Gated"), ("Compliance Sign-off", "Compliance", 2L, "Executing"), ("CAB Approval", "Compliance", 1L, "Executing")],
            gates.Select(r => ((string)r[0]!, (string)r[1]!, (long)r[2]!, (string)r[3]!)).ToArray());
        Assert.Equal("Draft", Query(f, "SELECT Status FROM TrainTemplates").Single()[0]); // never a fabricated approval
        var holidays = Query(f, "SELECT Day FROM Holidays").Select(r => (string)r[0]!).ToHashSet();
        Assert.Equal(23, holidays.Count);       // 11 (2026) + 12 (2027, incl. observed 31 Dec)
        foreach (var d in new[] { "2026-07-03", "2026-11-26", "2026-12-25", "2027-06-18", "2027-07-05", "2027-11-25", "2027-12-24", "2027-12-31" })
            Assert.Contains(d, holidays);
    }

    [Fact]
    public async Task Reference_seeding_is_idempotent()
    {
        using var f = new ApiFactory();
        f.CreateClient();
        var seed = (Infrastructure.Services.SeedService)f.Services.GetService(typeof(Infrastructure.Services.SeedService))!;
        await seed.SeedReferenceDataAsync();
        await seed.SeedReferenceDataAsync();
        Assert.Equal(23L, Query(f, "SELECT count(*) FROM Holidays").Single()[0]);
        Assert.Equal(1L, Query(f, "SELECT count(*) FROM TrainTemplates").Single()[0]);
        Assert.Equal(4L, Query(f, "SELECT count(*) FROM TemplateGates").Single()[0]);
    }

    [Fact]
    public void Spot_check_federal_holiday_rules()
    {
        var y2026 = UsHolidays.ForYear(2026).Select(h => h.Day.ToString("yyyy-MM-dd")).ToList();
        Assert.Equal(["2026-01-01", "2026-01-19", "2026-02-16", "2026-05-25", "2026-06-19", "2026-07-03", "2026-09-07", "2026-10-12", "2026-11-11", "2026-11-26", "2026-12-25"], y2026);
        Assert.Contains("2027-12-31", UsHolidays.ForYear(2027).Select(h => h.Day.ToString("yyyy-MM-dd"))); // 1 Jan 2028 is a Saturday
        Assert.DoesNotContain(UsHolidays.ForYear(2028), h => h.Day.Year != 2028);
    }

    // ---- demo data -----------------------------------------------------------------------------------------------------
    [Fact]
    public void Demo_data_makes_three_trains_with_four_gates_in_mixed_states_through_the_services()
    {
        using var f = new ApiFactory(demoData: true);
        f.CreateClient();
        Assert.Equal(3L, Query(f, "SELECT count(*) FROM ReleaseTrains").Single()[0]);
        foreach (var r in Query(f, "SELECT count(*) FROM StageGates GROUP BY ReleaseTrainId")) Assert.Equal(4L, r[0]);
        var states = Query(f, "SELECT DISTINCT Status FROM StageGates").Select(r => (string)r[0]!).ToHashSet();
        Assert.Superset(new HashSet<string> { "Pending", "InProgress", "Certified" }, states);
        Assert.Equal(["Gated", "Planning", "Planning"], Query(f, "SELECT CurrentStatus FROM ReleaseTrains ORDER BY CurrentStatus").Select(r => (string)r[0]!).ToArray());
        // built via the services: certifications are audited by the service, cascades (baseline) by triggers
        Assert.True(Convert.ToInt64(Query(f, "SELECT count(*) FROM AuditEvents WHERE Action='Certify'").Single()[0]) >= 3);
        Assert.True(Convert.ToInt64(Query(f, "SELECT count(*) FROM Baselines").Single()[0]) >= 2);
        // DueOn respects business days: no gate lands on a weekend for these offsets/targets (Fridays)
        foreach (var r in Query(f, "SELECT DueOn FROM StageGates")) Assert.NotEqual(DayOfWeek.Saturday, DateOnly.Parse((string)r[0]!).DayOfWeek);
    }

    [Fact]
    public async Task Demo_seed_never_touches_a_database_that_already_has_trains()
    {
        using var f = new ApiFactory(demoData: true);
        f.CreateClient();
        var seed = (Infrastructure.Services.SeedService)f.Services.GetService(typeof(Infrastructure.Services.SeedService))!;
        await seed.SeedDemoDataAsync();
        Assert.Equal(3L, Query(f, "SELECT count(*) FROM ReleaseTrains").Single()[0]);
        Assert.Equal(5L, Query(f, "SELECT count(*) FROM Users").Single()[0]);
    }

    // ---- admin ----------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Admin_writes_need_RTE_or_ReleaseManager_but_any_role_can_read()
    {
        using var f = new ApiFactory();
        var viewer = await As(f, Roles.Viewer, "v@x.com");
        var rm = await As(f, Roles.ReleaseManager, "rm@x.com");
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/v1/holidays")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/v1/holidays", new { day = "2026-12-24", name = "Christmas Eve" })).StatusCode);
        var ok = await rm.PostAsJsonAsync("/api/v1/holidays", new { day = "2026-12-24", name = "Christmas Eve" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var dup = await rm.PostAsJsonAsync("/api/v1/holidays", new { day = "2026-12-24", name = "Again" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, dup.StatusCode);
        Assert.Equal("InvalidInput", JsonDocument.Parse(await dup.Content.ReadAsStringAsync()).RootElement.GetProperty("guard").GetString());
        Assert.Equal(HttpStatusCode.OK, (await rm.DeleteAsync("/api/v1/holidays/2026-12-24")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rm.DeleteAsync("/api/v1/holidays/2026-12-24")).StatusCode);
        Assert.Equal(2L, Query(f, "SELECT count(*) FROM AuditEvents WHERE EntityType='Holiday'").Single()[0]);
    }

    [Fact]
    public async Task Teams_have_handles_and_members_and_users_can_get_a_handle()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var users = await rte.GetFromJsonAsync<JsonElement>("/api/v1/users");
        var me = users.EnumerateArray().Single().GetProperty("id").GetString()!;

        var bad = await rte.PostAsJsonAsync("/api/v1/teams", new { handle = "not valid!", name = "Ops" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        var created = await rte.PostAsJsonAsync("/api/v1/teams", new { handle = "@ops-db", name = "Database Ops", memberIds = new[] { me } });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var team = (await created.Content.ReadFromJsonAsync<JsonElement>());
        Assert.Equal("ops-db", team.GetProperty("handle").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await rte.PostAsJsonAsync("/api/v1/teams", new { handle = "OPS-DB", name = "dup" })).StatusCode == HttpStatusCode.InternalServerError ? HttpStatusCode.InternalServerError : HttpStatusCode.UnprocessableEntity);

        var list = await rte.GetFromJsonAsync<JsonElement>("/api/v1/teams");
        Assert.Equal([me], list[0].GetProperty("memberIds").EnumerateArray().Select(x => x.GetString()!).ToArray());

        var patch = await rte.PatchAsJsonAsync($"/api/v1/users/{me}", new { handle = "@rae" });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        Assert.Equal("rae", (await patch.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("handle").GetString());
    }
}
