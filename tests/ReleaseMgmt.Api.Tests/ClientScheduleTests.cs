using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Services;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// REOS-119 (RT4, D47): client dates on CERT and Production releases. One date for all Standard clients (the release's target date); each On-Demand client has an offered date (Standard date plus
/// <c>Release:OnDemandOffsetDays</c>) and may have a chosen one. Moving the Standard date moves every offered date and no chosen date. Who recorded a chosen date, and when, is on the row and in
/// the audit. The combined view lists every client with its date and segment, and warns (advisory) about a date inside a freeze or chill window.
/// </summary>
public class ClientScheduleTests
{
    private static readonly Dictionary<string, string> Seeded = new() { ["ReleaseTypes:SeedAtStart"] = "true" };
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
    private static long Count(ApiFactory f, string sql) => long.Parse(Scalar(f, sql));
    private static long Audits(ApiFactory f, string action) => Count(f, $"SELECT COUNT(*) FROM AuditEvents WHERE EntityType='ReleaseClientDate' AND Action='{action}'");
    private const string Std = "2027-03-05";   // a Friday, far enough ahead of any test clock

    private static async Task<HttpResponseMessage> Send(HttpClient c, HttpMethod m, string url, object? body, int? ifMatch)
    {
        using var req = new HttpRequestMessage(m, url);
        if (body is not null) req.Content = JsonContent.Create(body);
        if (ifMatch is int v) req.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{v}\""));
        return await c.SendAsync(req);
    }

    private sealed record World(ApiFactory F, HttpClient Rte, HttpClient Rm, string Cert, Dictionary<string, string> Types, Dictionary<string, string> Clients);

    /// <summary>A workspace with the four seeded types, a CERT environment, two Standard clients (Acme, Zeta) and three On-Demand clients (Globex, Initech, Umbrella).</summary>
    private static async Task<World> Build(Dictionary<string, string>? extra = null)
    {
        var f = new ApiFactory(settings: new Dictionary<string, string>(Seeded.Concat(extra ?? [])));
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var rm = await As(f, Roles.ReleaseManager, "rm@x.com");
        var env = (await Json(await rte.PostAsJsonAsync("/api/v1/environments", new { name = "CERT", kind = "Staging" }))).GetProperty("id").GetString()!;
        var types = (await rte.GetFromJsonAsync<JsonElement>("/api/v1/release-types")).EnumerateArray().ToDictionary(x => x.GetProperty("name").GetString()!, x => x.GetProperty("id").GetString()!);
        var clients = new Dictionary<string, string>();
        foreach (var (name, segment) in new[] { ("Acme", "Standard"), ("Zeta", "Standard"), ("Globex", "OnDemand"), ("Initech", "OnDemand"), ("Umbrella", "OnDemand") })
            clients[name] = (await Json(await rte.PostAsJsonAsync("/api/v1/clients", new { name, segment, certEnvironmentId = segment == "OnDemand" ? env : null }))).GetProperty("id").GetString()!;
        return new(f, rte, rm, env, types, clients);
    }

    private static async Task<(string Id, int Version)> Release(World w, string title, string type, string target = Std)
    {
        var r = await w.Rte.PostAsJsonAsync("/api/v1/trains", new { title, targetReleaseDate = target, riskTier = "Low", releaseTypeId = w.Types[type], betaNumber = type == "Beta" ? 1 : (int?)null });
        var b = await Json(r);
        Assert.True(r.IsSuccessStatusCode, b.ToString());
        return (b.GetProperty("id").GetString()!, b.GetProperty("version").GetInt32());
    }

    private static Task<JsonElement> View(HttpClient c, string trainId) => c.GetFromJsonAsync<JsonElement>($"/api/v1/trains/{trainId}/client-schedule");
    private static JsonElement RowOf(JsonElement view, string name) => view.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("name").GetString() == name);
    private static string[] Names(JsonElement view) => view.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("name").GetString()!).ToArray();

    [Fact]
    public async Task A_production_release_lists_every_client_with_its_date_and_segment_On_Demand_clients_at_the_standard_date_plus_seven()
    {
        var w = await Build(); using var _f = w.F;
        var (id, _) = await Release(w, "R27.03", "Production");
        var v = await View(w.Rte, id);
        Assert.Equal("Production", v.GetProperty("kind").GetString());
        Assert.Equal(Std, v.GetProperty("standardDate").GetString());
        Assert.Equal(7, v.GetProperty("offsetDays").GetInt32());
        Assert.Equal(["Acme", "Zeta", "Globex", "Initech", "Umbrella"], Names(v));   // by date, Standard first
        foreach (var n in new[] { "Acme", "Zeta" })
        {
            var r = RowOf(v, n);
            Assert.Equal("Standard", r.GetProperty("segment").GetString());
            Assert.Equal(Std, r.GetProperty("date").GetString());
            Assert.Equal("Standard", r.GetProperty("wave").GetString());
        }
        foreach (var n in new[] { "Globex", "Initech", "Umbrella" })
        {
            var r = RowOf(v, n);
            Assert.Equal("OnDemand", r.GetProperty("segment").GetString());
            Assert.Equal("2027-03-12", r.GetProperty("date").GetString());
            Assert.Equal("2027-03-12", r.GetProperty("offeredDate").GetString());
            Assert.Equal(JsonValueKind.Null, r.GetProperty("chosenDate").ValueKind);
            Assert.False(r.GetProperty("scheduled").GetBoolean());   // no row yet: the default offer is shown
            Assert.Equal("On-Demand", r.GetProperty("wave").GetString());
        }
        Assert.Equal(3, v.GetProperty("unscheduled").GetInt32());
        Assert.Equal(0, Count(w.F, "SELECT COUNT(*) FROM ReleaseClientDates"));   // reading writes nothing
    }

    [Fact]
    public async Task A_cert_release_lists_the_On_Demand_clients_only_because_Standard_clients_have_no_CERT_environment()
    {
        var w = await Build(); using var _f = w.F;
        var (id, _) = await Release(w, "R27.03 CERT", "CERT", "2027-02-26");
        var v = await View(w.Rte, id);
        Assert.Equal("Cert", v.GetProperty("kind").GetString());
        Assert.Equal(["Globex", "Initech", "Umbrella"], Names(v));
        Assert.Equal("2027-03-05", RowOf(v, "Globex").GetProperty("offeredDate").GetString());
    }

    [Fact]
    public async Task The_offset_is_the_configured_number_of_days_and_a_bad_value_is_refused_not_guessed()
    {
        var w = await Build(new() { ["Release:OnDemandOffsetDays"] = "3" }); using var _f = w.F;
        var (id, _) = await Release(w, "R27.03", "Production");
        var v = await View(w.Rte, id);
        Assert.Equal(3, v.GetProperty("offsetDays").GetInt32());
        Assert.Equal("2027-03-08", RowOf(v, "Globex").GetProperty("offeredDate").GetString());

        Assert.Equal(7, ClientScheduleKinds.OffsetFrom(null));
        Assert.Equal(0, ClientScheduleKinds.OffsetFrom("0"));
        foreach (var bad in new[] { "-1", "seven", "7.5", "91" })
            Assert.Contains("Release:OnDemandOffsetDays", Assert.Throws<InvalidOperationException>(() => ClientScheduleKinds.OffsetFrom(bad)).Message);
    }

    [Fact]
    public async Task Moving_the_Standard_date_moves_every_offered_date_and_no_chosen_date_each_move_audited()
    {
        var w = await Build(); using var _f = w.F;
        var (id, ver) = await Release(w, "R27.03", "Production");
        Assert.Equal(HttpStatusCode.OK, (await Send(w.Rte, HttpMethod.Post, $"/api/v1/trains/{id}/client-schedule:schedule", null, null)).StatusCode);
        var globex = w.Clients["Globex"];
        var chosen = await Send(w.Rte, HttpMethod.Patch, $"/api/v1/trains/{id}/client-schedule/{globex}", new { chosenDate = "2027-03-19" }, 1);
        Assert.Equal(HttpStatusCode.OK, chosen.StatusCode);

        // The Standard date moves a week later.
        var moved = await Send(w.Rte, HttpMethod.Patch, $"/api/v1/trains/{id}", new { targetReleaseDate = "2027-03-12" }, ver);
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        var v = await View(w.Rte, id);
        Assert.Equal("2027-03-12", v.GetProperty("standardDate").GetString());
        Assert.Equal("2027-03-12", RowOf(v, "Acme").GetProperty("date").GetString());
        foreach (var n in new[] { "Globex", "Initech", "Umbrella" }) Assert.Equal("2027-03-19", RowOf(v, n).GetProperty("offeredDate").GetString());   // every offered date moved by 7
        var g = RowOf(v, "Globex");
        Assert.Equal("2027-03-19", g.GetProperty("chosenDate").GetString());   // the chosen date is where the client put it
        Assert.Equal("2027-03-19", RowOf(v, "Initech").GetProperty("date").GetString());
        Assert.Equal("2027-03-19", Scalar(w.F, $"SELECT ChosenDate FROM ReleaseClientDates WHERE ClientId='{globex}'"));

        Assert.Equal(3, Audits(w.F, "OfferedMoved"));
        Assert.Equal("2027-03-12", Scalar(w.F, $"SELECT json_extract(BeforeJson,'$.offeredDate') FROM AuditEvents WHERE Action='OfferedMoved' AND EntityId=(SELECT Id FROM ReleaseClientDates WHERE ClientId='{globex}')"));
        Assert.Equal("2027-03-19", Scalar(w.F, $"SELECT json_extract(AfterJson,'$.offeredDate') FROM AuditEvents WHERE Action='OfferedMoved' AND EntityId=(SELECT Id FROM ReleaseClientDates WHERE ClientId='{globex}')"));
        Assert.Equal(3, Count(w.F, "SELECT COUNT(*) FROM ReleaseClientDates WHERE Version = 2 OR (ClientId = '" + globex + "' AND Version = 3)"));   // every moved row stamped
        Assert.Equal(3, Count(w.F, "SELECT COUNT(*) FROM ReleaseClientDates WHERE LastChangedByUserId IS NOT NULL AND LastChangedAt IS NOT NULL"));
        Assert.Equal(3, Count(w.F, "SELECT json_extract(AfterJson,'$.clientOfferedDatesMoved') FROM AuditEvents WHERE EntityType='ReleaseTrain' AND Action='ChangeTargetDate'"));

        // Moving it earlier moves them back; the chosen date, now equal to the Standard-plus-7, is still its own.
        var ver2 = (await Json(moved)).GetProperty("version").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await Send(w.Rte, HttpMethod.Patch, $"/api/v1/trains/{id}", new { targetReleaseDate = "2027-03-05" }, ver2)).StatusCode);
        v = await View(w.Rte, id);
        Assert.Equal("2027-03-12", RowOf(v, "Initech").GetProperty("offeredDate").GetString());
        Assert.Equal("2027-03-19", RowOf(v, "Globex").GetProperty("chosenDate").GetString());
    }

    [Fact]
    public async Task Moving_the_date_by_CSV_import_moves_the_offered_dates_too()
    {
        var w = await Build(); using var _f = w.F;
        var (id, _) = await Release(w, "R27.03", "Production");
        await w.Rte.PostAsync($"/api/v1/trains/{id}/client-schedule:schedule", null);
        var csv = "Title,TargetReleaseDate,RiskTier\r\nR27.03,2027-03-12,Low\r\n";
        var pv = await Json(await w.Rte.PostAsync("/api/v1/imports/Trains:preview?mode=Upsert&fileName=t.csv", new StringContent(csv, new UTF8Encoding(false), "text/csv")));
        var commit = await w.Rte.PostAsJsonAsync($"/api/v1/imports/{pv.GetProperty("jobId").GetString()}:commit", new { acknowledgeDecertify = false });
        Assert.True(commit.IsSuccessStatusCode, await commit.Content.ReadAsStringAsync());
        Assert.Equal("2027-03-19", RowOf(await View(w.Rte, id), "Globex").GetProperty("offeredDate").GetString());
        Assert.Equal(3, Audits(w.F, "OfferedMoved"));
    }

    [Fact]
    public async Task A_chosen_date_records_who_and_when_in_the_row_and_the_audit_and_can_change_and_clear()
    {
        var w = await Build(); using var _f = w.F;
        var (id, _) = await Release(w, "R27.03", "Production");
        var globex = w.Clients["Globex"]; var url = $"/api/v1/trains/{id}/client-schedule/{globex}";

        var first = await Send(w.Rte, HttpMethod.Patch, url, new { chosenDate = "2027-03-26" }, 0);   // no row yet: version 0
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var g = RowOf(await Json(first), "Globex");
        Assert.Equal("2027-03-26", g.GetProperty("chosenDate").GetString());
        Assert.Equal("2027-03-26", g.GetProperty("date").GetString());
        Assert.Equal("2027-03-12", g.GetProperty("offeredDate").GetString());
        Assert.Equal("rte", g.GetProperty("chosenBy").GetProperty("name").GetString());
        Assert.Equal(1, g.GetProperty("version").GetInt32());
        Assert.True(g.GetProperty("scheduled").GetBoolean());
        var rteId = UserId(w.F, "rte@x.com");
        Assert.Equal(rteId, Scalar(w.F, $"SELECT ChosenByUserId FROM ReleaseClientDates WHERE ClientId='{globex}'"));
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$", Scalar(w.F, $"SELECT ChosenAt FROM ReleaseClientDates WHERE ClientId='{globex}'"));
        Assert.Equal(1, Audits(w.F, "RecordChosen"));
        Assert.Equal(rteId, Scalar(w.F, "SELECT ActorUserId FROM AuditEvents WHERE Action='RecordChosen'"));
        Assert.Equal(id, Scalar(w.F, "SELECT ReleaseTrainId FROM AuditEvents WHERE Action='RecordChosen'"));
        Assert.Equal("2027-03-26", Scalar(w.F, "SELECT json_extract(AfterJson,'$.chosenDate') FROM AuditEvents WHERE Action='RecordChosen'"));

        // A second person changes it: the recorder is the second person.
        var change = await Send(w.Rm, HttpMethod.Patch, url, new { chosenDate = "2027-04-02" }, 1);
        Assert.Equal("rm", RowOf(await Json(change), "Globex").GetProperty("chosenBy").GetProperty("name").GetString());
        Assert.Equal(1, Audits(w.F, "ChangeChosen"));
        Assert.Equal("2027-03-26", Scalar(w.F, "SELECT json_extract(BeforeJson,'$.chosenDate') FROM AuditEvents WHERE Action='ChangeChosen'"));

        // The same date again writes nothing.
        var again = await Send(w.Rm, HttpMethod.Patch, url, new { chosenDate = "2027-04-02" }, 2);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("2", Scalar(w.F, $"SELECT Version FROM ReleaseClientDates WHERE ClientId='{globex}'"));
        Assert.Equal(2, Count(w.F, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='ReleaseClientDate'"));

        // Clearing returns the client to the offered date and forgets who and when.
        var clear = await Send(w.Rte, HttpMethod.Patch, url, new { chosenDate = "" }, 2);
        var c = RowOf(await Json(clear), "Globex");
        Assert.Equal(JsonValueKind.Null, c.GetProperty("chosenDate").ValueKind);
        Assert.Equal(JsonValueKind.Null, c.GetProperty("chosenBy").ValueKind);
        Assert.Equal("2027-03-12", c.GetProperty("date").GetString());
        Assert.Equal(1, Audits(w.F, "ClearChosen"));
        Assert.Equal("3", Scalar(w.F, $"SELECT Version FROM ReleaseClientDates WHERE ClientId='{globex}'"));
    }

    [Fact]
    public async Task A_stale_version_answers_409_with_the_current_row_and_nothing_is_written()
    {
        var w = await Build(); using var _f = w.F;
        var (id, _) = await Release(w, "R27.03", "Production");
        var url = $"/api/v1/trains/{id}/client-schedule/{w.Clients["Globex"]}";
        Assert.Equal(HttpStatusCode.OK, (await Send(w.Rte, HttpMethod.Patch, url, new { chosenDate = "2027-03-26" }, 0)).StatusCode);
        var stale = await Send(w.Rm, HttpMethod.Patch, url, new { chosenDate = "2027-04-02" }, 0);   // loaded before the row existed
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("2027-03-26", (await Json(stale)).GetProperty("current").GetProperty("chosenDate").GetString());
        var wrong = await Send(w.Rm, HttpMethod.Patch, url, new { chosenDate = "2027-04-02" }, 5);
        Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);
        Assert.Equal("2027-03-26", Scalar(w.F, "SELECT ChosenDate FROM ReleaseClientDates"));
        Assert.Equal(1, Count(w.F, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='ReleaseClientDate'"));
    }

    [Fact]
    public async Task The_provisional_rules_give_a_readable_422_and_write_nothing()
    {
        var w = await Build(); using var _f = w.F;
        var (id, _) = await Release(w, "R27.03", "Production");
        async Task<JsonElement> Refused(string client, object body, string contains)
        {
            var r = await Send(w.Rte, HttpMethod.Patch, $"/api/v1/trains/{id}/client-schedule/{client}", body, 0);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
            var b = await Json(r);
            Assert.Equal("ClientDateInvalid", b.GetProperty("guard").GetString());
            Assert.Contains(contains, b.GetProperty("message").GetString());
            return b;
        }
        var globex = w.Clients["Globex"];
        await Refused(globex, new { chosenDate = "2027-03-04" }, "earlier than the Standard date (2027-03-05)");   // not before the Standard date
        await Refused(globex, new { offeredDate = "2027-03-04" }, "earlier than the Standard date");
        await Refused(globex, new { chosenDate = "05/03/2027" }, "yyyy-MM-dd");
        await Refused(globex, new { chosenDate = "2027-02-30" }, "yyyy-MM-dd");
        await Refused(globex, new { }, "Give a chosen date");
        await Refused(w.Clients["Acme"], new { chosenDate = "2027-03-12" }, "Standard client");
        Assert.Equal(0, Count(w.F, "SELECT COUNT(*) FROM ReleaseClientDates"));
        Assert.Equal(0, Count(w.F, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='ReleaseClientDate'"));

        // The Standard date itself is allowed: the client takes the Standard wave.
        var same = await Send(w.Rte, HttpMethod.Patch, $"/api/v1/trains/{id}/client-schedule/{globex}", new { chosenDate = Std }, 0);
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        Assert.Equal("Standard", RowOf(await Json(same), "Globex").GetProperty("wave").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await Send(w.Rte, HttpMethod.Patch, $"/api/v1/trains/{id}/client-schedule/nobody", new { chosenDate = Std }, 0)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(w.Rte, HttpMethod.Patch, "/api/v1/trains/nothing/client-schedule/" + globex, new { chosenDate = Std }, 0)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.Rte.GetAsync("/api/v1/trains/nothing/client-schedule")).StatusCode);
    }

    [Fact]
    public async Task A_chosen_date_that_the_Standard_date_overtakes_is_flagged_not_changed()
    {
        var w = await Build(); using var _f = w.F;
        var (id, ver) = await Release(w, "R27.03", "Production");
        await Send(w.Rte, HttpMethod.Patch, $"/api/v1/trains/{id}/client-schedule/{w.Clients["Globex"]}", new { chosenDate = "2027-03-08" }, 0);
        Assert.Equal(HttpStatusCode.OK, (await Send(w.Rte, HttpMethod.Patch, $"/api/v1/trains/{id}", new { targetReleaseDate = "2027-03-15" }, ver)).StatusCode);
        var v = await View(w.Rte, id);
        var g = RowOf(v, "Globex");
        Assert.Equal("2027-03-08", g.GetProperty("chosenDate").GetString());
        Assert.True(g.GetProperty("beforeStandardDate").GetBoolean());
        Assert.Equal(1, v.GetProperty("warnings").GetInt32());
    }

    [Fact]
    public async Task A_CERT_release_takes_chosen_dates_and_other_types_carry_no_client_dates()
    {
        var w = await Build(); using var _f = w.F;
        var (cert, _) = await Release(w, "R27.03 CERT", "CERT", "2027-02-26");
        var ok = await Send(w.Rte, HttpMethod.Patch, $"/api/v1/trains/{cert}/client-schedule/{w.Clients["Initech"]}", new { chosenDate = "2027-03-09" }, 0);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("2027-03-09", RowOf(await Json(ok), "Initech").GetProperty("date").GetString());

        var (beta, _) = await Release(w, "R27.03 beta", "Beta");
        var v = await View(w.Rte, beta);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("kind").ValueKind);
        Assert.Empty(v.GetProperty("rows").EnumerateArray());
        Assert.Contains("CERT and Production", v.GetProperty("note").GetString());
        var refused = await Send(w.Rte, HttpMethod.Patch, $"/api/v1/trains/{beta}/client-schedule/{w.Clients["Initech"]}", new { chosenDate = "2027-03-09" }, 0);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("ClientDateInvalid", (await Json(refused)).GetProperty("guard").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await w.Rte.PostAsync($"/api/v1/trains/{beta}/client-schedule:schedule", null)).StatusCode);
    }

    [Fact]
    public async Task A_closed_or_archived_release_keeps_its_client_dates()
    {
        var w = await Build(); using var _f = w.F;
        var (id, _) = await Release(w, "R27.03", "Production");
        await w.Rte.PostAsync($"/api/v1/trains/{id}/client-schedule:schedule", null);
        Sql(w.F, $"UPDATE ReleaseTrains SET ArchivedAt='2027-04-01T00:00:00Z' WHERE Id='{id}'");
        var v = await View(w.Rte, id);
        Assert.False(v.GetProperty("open").GetBoolean());
        Assert.Equal(3, v.GetProperty("rows").EnumerateArray().Count(r => r.GetProperty("segment").GetString() == "OnDemand"));
        var r = await Send(w.Rte, HttpMethod.Patch, $"/api/v1/trains/{id}/client-schedule/{w.Clients["Globex"]}", new { chosenDate = "2027-03-26" }, 1);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Contains("no longer change", (await Json(r)).GetProperty("message").GetString());
    }

    [Fact]
    public async Task Scheduling_the_On_Demand_clients_makes_a_row_for_each_once_and_an_archived_client_gets_none()
    {
        var w = await Build(); using var _f = w.F;
        Assert.Equal(HttpStatusCode.OK, (await w.Rte.PostAsync($"/api/v1/clients/{w.Clients["Umbrella"]}:archive", null)).StatusCode);
        var (id, _) = await Release(w, "R27.03", "Production");
        var after = await w.Rte.PostAsync($"/api/v1/trains/{id}/client-schedule:schedule", null);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        var v = await Json(after);
        Assert.Equal(2, Count(w.F, "SELECT COUNT(*) FROM ReleaseClientDates"));
        Assert.Equal(0, v.GetProperty("unscheduled").GetInt32());
        Assert.Equal(["Acme", "Zeta", "Globex", "Initech"], Names(v));   // the archived client has no row and is not on the release
        Assert.Equal(2, Audits(w.F, "Schedule"));
        Assert.Equal(2, Count(w.F, "SELECT COUNT(*) FROM ReleaseClientDates WHERE OfferedDate='2027-03-12' AND Version=1 AND LastChangedByUserId IS NOT NULL"));

        await w.Rte.PostAsync($"/api/v1/trains/{id}/client-schedule:schedule", null);   // again: nothing to make
        Assert.Equal(2, Count(w.F, "SELECT COUNT(*) FROM ReleaseClientDates"));
        Assert.Equal(2, Audits(w.F, "Schedule"));

        // An archived client with a row stays listed, marked.
        await w.Rte.PostAsync($"/api/v1/clients/{w.Clients["Globex"]}:archive", null);
        Assert.True(RowOf(await View(w.Rte, id), "Globex").GetProperty("archived").GetBoolean());
        var archived = await Send(w.Rte, HttpMethod.Patch, $"/api/v1/trains/{id}/client-schedule/{w.Clients["Umbrella"]}", new { chosenDate = "2027-03-26" }, 0);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, archived.StatusCode);
        Assert.Contains("archived", (await Json(archived)).GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_date_inside_a_freeze_or_chill_window_is_an_advisory_not_a_refusal()
    {
        var w = await Build(); using var _f = w.F;
        var (id, _) = await Release(w, "R27.03", "Production");
        Sql(w.F, $"INSERT INTO BundledProducts(Id,ReleaseTrainId,ProductName,VersionTag,ProjectCode) VALUES('p1','{id}','Payments API','1.0','PAY')");
        async Task<string> Window(string name, string kind, string from, string to, string? pattern = null) =>
            (await Json(await w.Rm.PostAsJsonAsync("/api/v1/freezes", new { name, kind, startsAt = from, endsAt = to, productPattern = pattern }))).GetProperty("id").GetString()!;
        var freeze = await Window("Quarter close", "Freeze", "2027-03-11T22:00:00Z", "2027-03-12T06:00:00Z");   // touches 12 March (UTC) only
        var chill = await Window("Spring chill", "Chill", "2027-03-26T00:00:00Z", "2027-03-27T00:00:00Z", "Pay*");
        await Window("Other product", "Freeze", "2027-03-19T00:00:00Z", "2027-03-20T00:00:00Z", "Cards*");   // pattern does not cover this release
        await Window("Ends at midnight", "Freeze", "2027-03-18T00:00:00Z", "2027-03-19T00:00:00Z");           // 18 March only; 19 March starts at its end

        var v = await View(w.Rte, id);
        var g = RowOf(v, "Globex");   // offered 12 March
        Assert.Equal(freeze, g.GetProperty("freezes")[0].GetProperty("id").GetString());
        Assert.Equal("Freeze", g.GetProperty("freezes")[0].GetProperty("kind").GetString());
        Assert.Equal("Quarter close", g.GetProperty("freezes")[0].GetProperty("name").GetString());
        Assert.Empty(RowOf(v, "Acme").GetProperty("freezes").EnumerateArray());   // 5 March: none
        Assert.Equal(3, v.GetProperty("warnings").GetInt32());   // the three On-Demand clients share 12 March

        // A client choosing a date in the chill window is saved, with the advisory.
        var chose = await Send(w.Rte, HttpMethod.Patch, $"/api/v1/trains/{id}/client-schedule/{w.Clients["Globex"]}", new { chosenDate = "2027-03-26" }, 0);
        Assert.Equal(HttpStatusCode.OK, chose.StatusCode);
        var c = RowOf(await Json(chose), "Globex");
        Assert.Equal(chill, c.GetProperty("freezes")[0].GetProperty("id").GetString());
        Assert.Equal("Chill", c.GetProperty("freezes")[0].GetProperty("kind").GetString());
        // 19 March is not in a window that ended at its midnight, nor in the one for another product.
        var free = await Send(w.Rte, HttpMethod.Patch, $"/api/v1/trains/{id}/client-schedule/{w.Clients["Initech"]}", new { chosenDate = "2027-03-19" }, 0);
        Assert.Empty(RowOf(await Json(free), "Initech").GetProperty("freezes").EnumerateArray());
    }

    [Fact]
    public async Task Only_planners_change_dates_every_signed_in_role_reads_them()
    {
        var w = await Build(); using var _f = w.F;
        var (id, _) = await Release(w, "R27.03", "Production");
        var viewer = await As(w.F, Roles.Viewer, "v@x.com");
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/api/v1/trains/{id}/client-schedule")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(viewer, HttpMethod.Patch, $"/api/v1/trains/{id}/client-schedule/{w.Clients["Globex"]}", new { chosenDate = "2027-03-26" }, 0)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync($"/api/v1/trains/{id}/client-schedule:schedule", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await w.F.CreateClient().GetAsync($"/api/v1/trains/{id}/client-schedule")).StatusCode);
        Assert.Equal(0, Count(w.F, "SELECT COUNT(*) FROM ReleaseClientDates"));
    }

    [Fact]
    public async Task The_triggers_hold_the_settled_rules_behind_the_service()
    {
        var w = await Build(); using var _f = w.F;
        var (id, _) = await Release(w, "R27.03", "Production");
        string Fails(string sql) => Assert.Throws<SqliteException>(() => Sql(w.F, sql)).Message;
        const string cols = "(Id,ReleaseTrainId,ClientId,OfferedDate,CreatedAt)";
        Assert.Contains("Only an On-Demand client has its own date", Fails($"INSERT INTO ReleaseClientDates{cols} VALUES('x1','{id}','{w.Clients["Acme"]}','2027-03-12','2027-01-01T00:00:00Z')"));
        Sql(w.F, $"INSERT INTO ReleaseClientDates{cols} VALUES('ok','{id}','{w.Clients["Globex"]}','2027-03-12','2027-01-01T00:00:00Z')");
        Assert.Contains("keeps its release and client", Fails($"UPDATE ReleaseClientDates SET ClientId='{w.Clients["Initech"]}' WHERE Id='ok'"));
        Assert.Contains("CHECK", Fails("UPDATE ReleaseClientDates SET ChosenDate='2027-03-13' WHERE Id='ok'"));   // nobody recorded it

        Sql(w.F, @"INSERT INTO Workspaces(Id,Name,Slug,CreatedAt) VALUES('w2','EU','eu','2027-01-01T00:00:00Z');
                   INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,CreatedAt,UpdatedAt,WorkspaceId) VALUES('tw2','EU R','2027-03-05','2027-01-01T00:00:00Z','2027-01-01T00:00:00Z','w2');");
        Assert.Contains("belongs to another workspace", Fails($"INSERT INTO ReleaseClientDates{cols} VALUES('x2','tw2','{w.Clients["Initech"]}','2027-03-12','2027-01-01T00:00:00Z')"));
        Assert.Contains("cannot move to another workspace", Fails("UPDATE ReleaseClientDates SET WorkspaceId='w2' WHERE Id='ok'"));
    }

    [Fact]
    public async Task Another_workspace_sees_none_of_it()
    {
        var w = await Build(); using var _f = w.F;
        var (id, _) = await Release(w, "R27.03", "Production");
        await w.Rte.PostAsync($"/api/v1/trains/{id}/client-schedule:schedule", null);
        Assert.Equal(3, Count(w.F, "SELECT COUNT(*) FROM ReleaseClientDates WHERE WorkspaceId = 'default'"));
        Sql(w.F, "INSERT INTO Workspaces(Id,Name,Slug,CreatedAt) VALUES('w2','EU','eu','2027-01-01T00:00:00Z')");
        var rteId = UserId(w.F, "rte@x.com");
        Sql(w.F, $"INSERT INTO WorkspaceMembers(WorkspaceId,UserId,Role,AddedAt) VALUES('w2','{rteId}','RTE','2027-01-01T00:00:00Z')");
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/trains/{id}/client-schedule");
        req.Headers.Add("X-Workspace", "eu");
        var r = await w.Rte.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);   // the release is another workspace's
    }
}
