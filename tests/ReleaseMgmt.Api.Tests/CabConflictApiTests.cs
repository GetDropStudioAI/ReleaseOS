using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ReleaseMgmt.Domain.Common;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// REOS-180 (D56, D9): conflicts of a change, derived on every read. Each conflict type (freeze, chill, blackout day, another change on the same release, service or environment, another release's
/// window) is found with the rule that fired, moving a window changes the answer at once (nothing is stored), a conflict never blocks, and approving over one needs a reason. Module off: 404.
/// </summary>
public class CabConflictApiTests
{
    private const string W1 = "2033-03-05T02:00:00Z", W2 = "2033-03-05T04:00:00Z";
    private static string Id(JsonElement e) => e.GetProperty("id").GetString()!;
    private static int Ver(JsonElement e) => e.GetProperty("version").GetInt32();

    private static async Task<HttpResponseMessage> Send(HttpClient c, HttpMethod m, string url, object? body = null, int? ifMatch = null)
    {
        using var req = new HttpRequestMessage(m, url);
        if (body is not null) req.Content = JsonContent.Create(body);
        if (ifMatch is int v) req.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{v}\""));
        return await c.SendAsync(req);
    }

    private static async Task<JsonElement> Ok(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    private sealed record World(ApiFactory F, HttpClient Rte, HttpClient Gov, HttpClient Viewer, string RteId) : IDisposable { public void Dispose() => F.Dispose(); }

    private static async Task<World> Enabled()
    {
        var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var gov = await As(f, Roles.GovernanceOfficer, "gov@x.com");
        var viewer = await As(f, Roles.Viewer, "v@x.com");
        (await Send(rte, HttpMethod.Post, "/api/v1/modules/cab:enable", new { }, 0)).EnsureSuccessStatusCode();
        return new World(f, rte, gov, viewer, UserId(f, "rte@x.com"));
    }

    private static async Task<JsonElement> Change(World w, string? release = null, string start = W1, string end = W2, string title = "Patch the gateway", bool submit = false)
    {
        var c = await Ok(await Send(w.Rte, HttpMethod.Post, "/api/v1/cab/changes", new { title, changeType = "Normal", rollbackNote = "Redeploy the previous build", plannedStartAt = start, plannedEndAt = end, releaseTrainId = release }));
        return submit ? await Ok(await Send(w.Rte, HttpMethod.Post, $"/api/v1/cab/changes/{Id(c)}:submit", new { }, Ver(c))) : c;
    }

    private static async Task<JsonElement> Conflicts(World w, JsonElement change) => await Ok(await w.Viewer.GetAsync($"/api/v1/cab/changes/{Id(change)}/conflicts"));
    private static string[] Rules(JsonElement report) => [.. report.GetProperty("conflicts").EnumerateArray().Select(c => c.GetProperty("rule").GetString()!)];

    private static void Train(World w, string id, string title, params string[] products) => Sql(w.F, $@"
        INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CreatedAt,UpdatedAt) VALUES('{id}','{title}','2033-03-05','Low','2033-01-01T00:00:00Z','2033-01-01T00:00:00Z');
        {string.Join("\n", products.Select((p, n) => $"INSERT INTO BundledProducts(Id,ReleaseTrainId,ProductName,VersionTag,ProjectCode) VALUES('{id}-p{n}','{id}','{p}','1.0','{id}-{n}');"))}");

    private static void Freeze(World w, string id, string kind, string start, string end, string? pattern = null) => Sql(w.F,
        $"INSERT INTO FreezeWindows(Id,Name,Kind,StartsAt,EndsAt,ProductPattern,CreatedByUserId) VALUES('{id}','{id} window','{kind}','{start}','{end}',{(pattern is null ? "NULL" : $"'{pattern}'")},'{w.RteId}')");

    [Fact]
    public async Task With_the_module_off_every_conflict_route_answers_404()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        foreach (var url in new[] { "/api/v1/cab/conflict-rules", "/api/v1/cab/changes/x/conflicts", "/api/v1/cab/meetings/x/conflicts", "/api/v1/cab/calendar?from=2033-03-01&to=2033-03-31" })
            Assert.Equal(HttpStatusCode.NotFound, (await rte.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task The_rules_are_listed_in_words_with_the_margin_and_the_reason_length()
    {
        using var w = await Enabled();
        var j = await Ok(await w.Viewer.GetAsync("/api/v1/cab/conflict-rules"));
        Assert.Equal(["FreezeOverlap", "ChillOverlap", "BlackoutDay", "SharedWindow", "ReleaseWindowOverlap"], j.GetProperty("rules").EnumerateArray().Select(r => r.GetProperty("rule").GetString()));
        Assert.Equal(30, j.GetProperty("bufferMinutes").GetInt32());
        Assert.Equal(20, j.GetProperty("minReasonLength").GetInt32());
        Assert.Contains("never blocks", j.GetProperty("approvalRule").GetString());
    }

    [Fact]
    public async Task A_freeze_and_a_chill_window_over_the_planned_window_are_found_and_a_window_that_moves_changes_the_answer_at_once()
    {
        using var w = await Enabled();
        var ch = await Change(w);
        Assert.Empty(Rules(await Conflicts(w, ch)));
        Freeze(w, "fz", "Freeze", "2033-03-05T00:00:00Z", "2033-03-06T00:00:00Z");
        Freeze(w, "ch", "Chill", "2033-03-05T03:00:00Z", "2033-03-05T09:00:00Z");
        var r = await Conflicts(w, ch);
        Assert.Equal(["FreezeOverlap", "ChillOverlap"], Rules(r));
        Assert.True(r.GetProperty("checked").GetBoolean());
        Assert.Contains("freeze window \"fz window\"", r.GetProperty("conflicts")[0].GetProperty("summary").GetString());
        Assert.Contains("UTC", r.GetProperty("conflicts")[0].GetProperty("summary").GetString());
        Sql(w.F, "UPDATE FreezeWindows SET StartsAt='2033-04-01T00:00:00Z', EndsAt='2033-04-02T00:00:00Z' WHERE Id='fz'");   // the window moves: no recompute step, no stored result
        Assert.Equal(["ChillOverlap"], Rules(await Conflicts(w, ch)));
        Assert.Equal("0", Scalar(w.F, "SELECT COUNT(*) FROM AuditEvents WHERE Action LIKE '%onflict%'"));
    }

    [Fact]
    public async Task A_window_that_covers_only_some_products_matters_only_to_a_release_that_ships_one()
    {
        using var w = await Enabled();
        Train(w, "tp", "Payments release", "Payments API"); Train(w, "tc", "Cards release", "Card Portal");
        Freeze(w, "fz", "Freeze", "2033-03-05T00:00:00Z", "2033-03-06T00:00:00Z", "Payments*");
        Assert.Equal(["FreezeOverlap"], Rules(await Conflicts(w, await Change(w, "tp"))));
        Assert.Empty(Rules(await Conflicts(w, await Change(w, "tc"))));
        Assert.Empty(Rules(await Conflicts(w, await Change(w))));   // no release: only windows over all products match it
    }

    [Fact]
    public async Task A_blackout_day_inside_the_window_is_found_and_the_edge_of_the_day_is_not()
    {
        using var w = await Enabled();
        Sql(w.F, "INSERT INTO Holidays(Day,Name) VALUES('2033-03-05','Founders Day'),('2033-03-06','Day after')");
        var r = await Conflicts(w, await Change(w));   // 02:00 to 04:00 on 5 March
        Assert.Equal(["BlackoutDay"], Rules(r));
        Assert.Contains("Founders Day", r.GetProperty("conflicts")[0].GetProperty("summary").GetString());
        Assert.Equal(["BlackoutDay"], Rules(await Conflicts(w, await Change(w, start: "2033-03-05T22:00:00Z", end: "2033-03-06T00:00:00Z"))));   // ends at midnight: only the 5th
        Assert.Empty(Rules(await Conflicts(w, await Change(w, start: "2033-03-07T02:00:00Z", end: "2033-03-07T04:00:00Z"))));
    }

    [Fact]
    public async Task Another_change_on_the_same_release_a_shared_service_or_a_shared_environment_is_found_but_a_draft_or_an_unrelated_one_is_not()
    {
        using var w = await Enabled();
        Sql(w.F, @"INSERT INTO CatalogServices(Id,ServiceKey,Name,CreatedAt) VALUES('sv1','payments-api','Payments API','2033-01-01T00:00:00Z');
                   INSERT INTO Environments(Id,Name,Kind,CreatedAt) VALUES('e1','Prod EU','Production','2033-01-01T00:00:00Z');");
        Train(w, "ta", "Release A", "Payments API"); Train(w, "tb", "Release B", "Gateway"); Train(w, "tc", "Release C", "Cards"); Train(w, "td", "Release D", "Other");
        Sql(w.F, @"UPDATE BundledProducts SET CatalogServiceId='sv1' WHERE ReleaseTrainId IN ('ta','tb');
                   INSERT INTO TrainEnvironments(ReleaseTrainId,EnvironmentId) VALUES('tc','e1'),('td','e1');");
        var mine = await Change(w, "ta", title: "Mine");
        var sameRelease = await Change(w, "ta", title: "Same release", submit: true);
        var sameService = await Change(w, "tb", title: "Same service", submit: true);
        await Change(w, "td", title: "Env only, but the mine release has none", submit: true);
        await Change(w, "ta", title: "A draft does not count");
        var r = await Conflicts(w, mine);
        Assert.Equal(["SharedWindow", "SharedWindow"], Rules(r));
        var text = string.Join(" | ", r.GetProperty("conflicts").EnumerateArray().Select(c => c.GetProperty("summary").GetString()));
        Assert.Contains("the same release", text); Assert.Contains("service Payments API", text);
        Assert.DoesNotContain("A draft", text);
        // the environment case: C and D both target Prod EU
        var envMine = await Change(w, "tc", title: "Env mine");
        var env = await Conflicts(w, envMine);
        Assert.Equal(["SharedWindow"], Rules(env));
        Assert.Contains("environment Prod EU", env.GetProperty("conflicts")[0].GetProperty("summary").GetString());
        // a change a day later is clear
        Assert.Empty(Rules(await Conflicts(w, await Change(w, "ta", start: "2033-03-06T02:00:00Z", end: "2033-03-06T04:00:00Z", title: "Later"))));
        _ = sameRelease; _ = sameService;
    }

    [Fact]
    public async Task Another_open_release_whose_deployment_window_overlaps_and_shares_a_product_is_found_but_the_own_release_is_not()
    {
        using var w = await Enabled();
        Train(w, "ta", "Release A", "Payments API"); Train(w, "tb", "Release B", "Payments API"); Train(w, "tc", "Release C", "Cards");
        Sql(w.F, @"UPDATE ReleaseTrains SET CurrentStatus='Planning';
                   INSERT INTO DeploymentWindows(Id,ReleaseTrainId,StartsAt,EndsAt) VALUES('wa','ta','2033-03-05T01:00:00Z','2033-03-05T05:00:00Z'),('wb','tb','2033-03-05T03:00:00Z','2033-03-05T06:00:00Z'),('wc','tc','2033-03-05T03:00:00Z','2033-03-05T06:00:00Z');");
        var r = await Conflicts(w, await Change(w, "ta"));
        Assert.Equal(["ReleaseWindowOverlap"], Rules(r));
        Assert.Equal("tb", r.GetProperty("conflicts")[0].GetProperty("refId").GetString());
        Assert.Contains("shares Payments API", r.GetProperty("conflicts")[0].GetProperty("summary").GetString());
    }

    [Fact]
    public async Task Approving_over_a_conflict_needs_a_reason_and_names_the_rules_in_the_audit_row_but_a_clear_change_needs_none()
    {
        using var w = await Enabled();
        Freeze(w, "fz", "Freeze", "2033-03-05T00:00:00Z", "2033-03-06T00:00:00Z");
        var ch = await Change(w, submit: true);
        async Task<HttpResponseMessage> Decide(JsonElement c, string? rationale) =>
            await Send(w.Gov, HttpMethod.Post, $"/api/v1/cab/changes/{Id(c)}/decisions", new { decision = "Approved", rationale }, Ver(c));
        foreach (var rationale in new string?[] { null, "", "Looks fine", "          too short          " })
        {
            var refused = await Decide(ch, rationale);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            var j = JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal("ChangeConflictNeedsReason", j.GetProperty("guard").GetString());
            Assert.Contains("Inside a freeze window", j.GetProperty("message").GetString());
        }
        Assert.Equal("Submitted", Scalar(w.F, $"SELECT Status FROM ChangeRequests WHERE Id='{Id(ch)}'"));   // warned, nothing moved
        var done = await Ok(await Decide(ch, "Customer-requested fix, freeze lead agrees"));
        Assert.Equal("Approved", done.GetProperty("status").GetString());
        Assert.Contains("overConflicts", Scalar(w.F, $"SELECT AfterJson FROM AuditEvents WHERE EntityId='{Id(ch)}' AND Action='Decide'"));
        Assert.Contains("FreezeOverlap", Scalar(w.F, $"SELECT AfterJson FROM AuditEvents WHERE EntityId='{Id(ch)}' AND Action='Decide'"));

        var clear = await Change(w, start: "2033-06-05T02:00:00Z", end: "2033-06-05T04:00:00Z", submit: true);
        Assert.Equal("Approved", (await Ok(await Decide(clear, null))).GetProperty("status").GetString());
        Assert.DoesNotContain("overConflicts", Scalar(w.F, $"SELECT AfterJson FROM AuditEvents WHERE EntityId='{Id(clear)}' AND Action='Decide'"));
    }

    [Fact]
    public async Task Rejecting_or_deferring_over_a_conflict_needs_no_extra_reason()
    {
        using var w = await Enabled();
        Freeze(w, "fz", "Freeze", "2033-03-05T00:00:00Z", "2033-03-06T00:00:00Z");
        var ch = await Change(w, submit: true);
        var deferred = await Send(w.Gov, HttpMethod.Post, $"/api/v1/cab/changes/{Id(ch)}/decisions", new { decision = "Deferred" }, Ver(ch));
        Assert.True(deferred.IsSuccessStatusCode, await deferred.Content.ReadAsStringAsync());
        var rejected = await Send(w.Gov, HttpMethod.Post, $"/api/v1/cab/changes/{Id(ch)}/decisions", new { decision = "Rejected", rationale = "Not in a freeze" }, Ver(ch));
        Assert.True(rejected.IsSuccessStatusCode, await rejected.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_change_without_a_window_and_a_closed_change_are_not_checked_and_say_why()
    {
        using var w = await Enabled();
        var noWindow = await Ok(await Send(w.Rte, HttpMethod.Post, "/api/v1/cab/changes", new { title = "No window yet", changeType = "Normal" }));
        var r = await Conflicts(w, noWindow);
        Assert.False(r.GetProperty("checked").GetBoolean());
        Assert.Contains("no planned window", r.GetProperty("reason").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await w.Viewer.GetAsync("/api/v1/cab/changes/nope/conflicts")).StatusCode);
    }

    [Fact]
    public async Task The_agenda_of_a_meeting_and_the_calendar_carry_the_same_conflicts()
    {
        using var w = await Enabled();
        Freeze(w, "fz", "Freeze", "2033-03-05T00:00:00Z", "2033-03-06T00:00:00Z");
        Sql(w.F, "INSERT INTO Holidays(Day,Name) VALUES('2033-03-05','Founders Day')");
        var ch = await Change(w, submit: true);
        var clear = await Change(w, start: "2033-03-10T02:00:00Z", end: "2033-03-10T04:00:00Z", title: "Clear", submit: true);
        var m = await Ok(await Send(w.Rte, HttpMethod.Post, "/api/v1/cab/meetings", new { title = "Weekly CAB", startsAt = "2033-03-04T10:00:00Z", endsAt = "2033-03-04T11:00:00Z", chairUserId = w.RteId }));
        m = await Ok(await Send(w.Rte, HttpMethod.Post, $"/api/v1/cab/meetings/{Id(m)}/agenda", new { changeRequestId = Id(ch) }, Ver(m)));
        m = await Ok(await Send(w.Rte, HttpMethod.Post, $"/api/v1/cab/meetings/{Id(m)}/agenda", new { changeRequestId = Id(clear) }, Ver(m)));
        var agenda = await Ok(await w.Viewer.GetAsync($"/api/v1/cab/meetings/{Id(m)}/conflicts"));
        var items = agenda.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal(["FreezeOverlap", "BlackoutDay"], items[0].GetProperty("conflicts").EnumerateArray().Select(c => c.GetProperty("rule").GetString()));
        Assert.Empty(items[1].GetProperty("conflicts").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await w.Viewer.GetAsync("/api/v1/cab/meetings/nope/conflicts")).StatusCode);

        var cal = await Ok(await w.Viewer.GetAsync("/api/v1/cab/calendar?from=2033-03-01&to=2033-03-31"));
        var rows = cal.GetProperty("changes").EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows[0].GetProperty("conflicts").GetArrayLength());
        Assert.Equal(0, rows[1].GetProperty("conflicts").GetArrayLength());
        Assert.Equal("2033-03-05", cal.GetProperty("blackouts")[0].GetProperty("day").GetString());
        Assert.Equal(0, (await Ok(await w.Viewer.GetAsync("/api/v1/cab/calendar?from=2033-05-01&to=2033-05-31"))).GetProperty("changes").GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Viewer.GetAsync("/api/v1/cab/calendar?from=nope&to=2033-05-31")).StatusCode);
    }
}
