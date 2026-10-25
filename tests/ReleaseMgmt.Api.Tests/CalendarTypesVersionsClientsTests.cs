using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ReleaseMgmt.Domain.Common;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;
using IcsCalendar = Ical.Net.Calendar;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// REOS-120 (RT5, D46 to D49): the calendar, the overview and the ICS feeds show each release's type, its version chain and the days its Standard and On-Demand clients get it. The calendar
/// filters by type and by client segment (Standard, On-Demand, or both together). A release appears on the calendar for a day one of its client dates falls on, even when its own dates do not.
/// </summary>
public class CalendarTypesVersionsClientsTests
{
    private static readonly Dictionary<string, string> Seeded = new() { ["ReleaseTypes:SeedAtStart"] = "true" };
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
    private static string Id(JsonElement e) => e.GetProperty("id").GetString()!;
    private const string Std = "2027-03-05";   // a Friday
    private const string Cert = "2027-02-19";

    private sealed record World(ApiFactory F, HttpClient Rte, Dictionary<string, string> Types, Dictionary<string, string> Clients, string Version, string Beta, string CertId, string Prod, string Solo);

    /// <summary>Orion 5.2: Beta 1, CERT (2027-02-19) and Production (2027-03-05); Acme and Zeta are Standard clients, Globex and Initech On-Demand. A lone Beta sits in no version.</summary>
    private static async Task<World> Build()
    {
        var f = new ApiFactory(settings: Seeded);
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var env = Id(await Json(await rte.PostAsJsonAsync("/api/v1/environments", new { name = "CERT", kind = "Staging" })));
        var types = (await rte.GetFromJsonAsync<JsonElement>("/api/v1/release-types")).EnumerateArray().ToDictionary(x => x.GetProperty("name").GetString()!, Id);
        var clients = new Dictionary<string, string>();
        foreach (var (name, segment) in new[] { ("Acme", "Standard"), ("Zeta", "Standard"), ("Globex", "OnDemand"), ("Initech", "OnDemand") })
            clients[name] = Id(await Json(await rte.PostAsJsonAsync("/api/v1/clients", new { name, segment, certEnvironmentId = segment == "OnDemand" ? env : null })));
        var version = Id(await Json(await rte.PostAsJsonAsync("/api/v1/versions", new { name = "Orion 5.2" })));
        async Task<string> Release(string title, string type, string target, bool inVersion, int? beta = null)
        {
            var r = await rte.PostAsJsonAsync("/api/v1/trains", new { title, targetReleaseDate = target, riskTier = "Low", releaseTypeId = types[type], betaNumber = beta, releaseVersionId = inVersion ? version : null });
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            return Id(await Json(r));
        }
        var beta1 = await Release("Orion Beta 1", "Beta", "2027-02-05", true, 1);
        var cert = await Release("Orion CERT", "CERT", Cert, true);
        var prod = await Release("Orion Production", "Production", Std, true);
        var solo = await Release("Lone Beta", "Beta", "2027-03-04", false, 1);
        return new(f, rte, types, clients, version, beta1, cert, prod, solo);
    }

    private static async Task<HttpResponseMessage> Send(HttpClient c, HttpMethod m, string url, object? body, int? ifMatch)
    {
        using var req = new HttpRequestMessage(m, url) { Content = body is null ? null : JsonContent.Create(body) };
        if (ifMatch is int v) req.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{v}\""));
        return await c.SendAsync(req);
    }

    private static Task<JsonElement> Calendar(HttpClient c, string from, string to, string query = "") => c.GetFromJsonAsync<JsonElement>($"/api/v1/calendar?from={from}&to={to}{query}");
    private static string[] Titles(JsonElement cal) => [.. cal.GetProperty("trains").EnumerateArray().Select(t => t.GetProperty("title").GetString()!)];
    private static JsonElement[] ClientDates(JsonElement cal) => [.. cal.GetProperty("clientDates").EnumerateArray()];

    [Fact]
    public async Task The_calendar_carries_each_releases_type_its_version_chain_and_the_client_dates_in_range()
    {
        var w = await Build(); using var _f = w.F;
        var cal = await Calendar(w.Rte, "2027-02-01", "2027-03-31");
        var trains = cal.GetProperty("trains").EnumerateArray().ToDictionary(t => t.GetProperty("title").GetString()!);
        Assert.Equal("Production", trains["Orion Production"].GetProperty("type").GetProperty("name").GetString());
        Assert.Equal("Beta", trains["Lone Beta"].GetProperty("type").GetProperty("name").GetString());
        // the chain (RT2) is whole and in order
        var chain = trains["Orion CERT"].GetProperty("chain");
        Assert.Equal("Orion 5.2", chain.GetProperty("versionName").GetString());
        Assert.Equal(["Beta 1", "CERT", "Production"], chain.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("label").GetString()!).ToArray());
        Assert.Equal(JsonValueKind.Null, trains["Lone Beta"].GetProperty("chain").ValueKind);
        // which clients each release reaches: CERT only On-Demand, Production both, Beta none
        Assert.Equal(["OnDemand"], trains["Orion CERT"].GetProperty("segments").EnumerateArray().Select(x => x.GetString()!).ToArray());
        Assert.Equal(["Standard", "OnDemand"], trains["Orion Production"].GetProperty("segments").EnumerateArray().Select(x => x.GetString()!).ToArray());
        Assert.Empty(trains["Orion Beta 1"].GetProperty("segments").EnumerateArray());
        Assert.Equal("Cert", trains["Orion CERT"].GetProperty("clientKind").GetString());
        Assert.Equal(JsonValueKind.Null, trains["Orion Beta 1"].GetProperty("clientKind").ValueKind);

        // client dates: the Standard clients share the Production date; the On-Demand clients are offered Standard + 7 on Production and CERT + 7 on CERT
        var dates = ClientDates(cal).Select(d => $"{d.GetProperty("trainTitle").GetString()}|{d.GetProperty("segment").GetString()}|{d.GetProperty("date").GetString()}|{d.GetProperty("count").GetInt32()}|{d.GetProperty("wave").GetString()}").ToArray();
        Assert.Equal(["Orion CERT|OnDemand|2027-02-26|2|On-Demand", "Orion Production|Standard|2027-03-05|2|Standard", "Orion Production|OnDemand|2027-03-12|2|On-Demand"], dates);
        Assert.Equal(["Globex", "Initech"], ClientDates(cal)[2].GetProperty("clients").EnumerateArray().Select(x => x.GetString()!).ToArray());
    }

    [Fact]
    public async Task A_release_is_listed_for_a_day_one_of_its_client_dates_falls_on_even_when_its_own_dates_do_not()
    {
        var w = await Build(); using var _f = w.F;
        // 2027-03-08..14 holds only the On-Demand production date (03-12): neither the target (03-05) nor any gate or window is in it
        var cal = await Calendar(w.Rte, "2027-03-08", "2027-03-14");
        Assert.Equal(["Orion Production"], Titles(cal));
        var only = Assert.Single(ClientDates(cal));
        Assert.Equal("2027-03-12", only.GetProperty("date").GetString());
        // and a week with none lists neither the release nor a date
        var quiet = await Calendar(w.Rte, "2027-04-05", "2027-04-11");
        Assert.Empty(Titles(quiet)); Assert.Empty(ClientDates(quiet));
    }

    [Fact]
    public async Task A_chosen_date_moves_that_clients_entry_and_a_client_taking_the_standard_date_is_in_the_standard_wave()
    {
        var w = await Build(); using var _f = w.F;
        Assert.Equal(HttpStatusCode.OK, (await Send(w.Rte, HttpMethod.Patch, $"/api/v1/trains/{w.Prod}/client-schedule/{w.Clients["Globex"]}", new { chosenDate = "2027-03-19" }, 0)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(w.Rte, HttpMethod.Patch, $"/api/v1/trains/{w.Prod}/client-schedule/{w.Clients["Initech"]}", new { chosenDate = Std }, 0)).StatusCode);
        var dates = ClientDates(await Calendar(w.Rte, "2027-03-01", "2027-03-31")).Select(d => $"{d.GetProperty("segment").GetString()}|{d.GetProperty("date").GetString()}|{d.GetProperty("wave").GetString()}|{string.Join("+", d.GetProperty("clients").EnumerateArray().Select(x => x.GetString()))}").ToArray();
        Assert.Equal(["Standard|2027-03-05|Standard|Acme+Zeta", "OnDemand|2027-03-05|Standard|Initech", "OnDemand|2027-03-19|On-Demand|Globex"], dates);
    }

    [Fact]
    public async Task Filtering_by_type_and_by_segment_narrows_the_releases_and_their_client_dates()
    {
        var w = await Build(); using var _f = w.F;
        const string range = "from=2027-02-01&to=2027-03-31";
        async Task<JsonElement> Get(string q) => await w.Rte.GetFromJsonAsync<JsonElement>($"/api/v1/calendar?{range}{q}");

        Assert.Equal(["Orion Beta 1", "Orion CERT", "Lone Beta", "Orion Production"], Titles(await Get("")));   // by target date
        Assert.Equal(["Orion Beta 1", "Lone Beta"], Titles(await Get($"&releaseTypeId={w.Types["Beta"]}")));
        Assert.Empty(ClientDates(await Get($"&releaseTypeId={w.Types["Beta"]}")));

        var standard = await Get("&segment=Standard");
        Assert.Equal(["Orion Production"], Titles(standard));
        Assert.Equal(["Standard"], ClientDates(standard).Select(d => d.GetProperty("segment").GetString()!).Distinct());

        var onDemand = await Get("&segment=OnDemand");
        Assert.Equal(["Orion CERT", "Orion Production"], Titles(onDemand));
        Assert.Equal(["OnDemand"], ClientDates(onDemand).Select(d => d.GetProperty("segment").GetString()!).Distinct());

        var combined = await Get("&segment=Combined");
        Assert.Equal(["Orion CERT", "Orion Production"], Titles(combined));   // the releases that reach clients, both segments together
        Assert.Equal(3, ClientDates(combined).Length);

        // type and segment together: CERT is not Production, so On-Demand clients of Production only
        var both = await Get($"&segment=OnDemand&releaseTypeId={w.Types["Production"]}");
        Assert.Equal(["Orion Production"], Titles(both));
        Assert.Single(ClientDates(both));

        // a type filter limits the releases listed, never the chain they sit in
        Assert.Equal(3, (await Get($"&releaseTypeId={w.Types["CERT"]}")).GetProperty("trains")[0].GetProperty("chain").GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task A_segment_that_is_not_Standard_OnDemand_or_Combined_is_refused()
    {
        var w = await Build(); using var _f = w.F;
        var r = await w.Rte.GetAsync("/api/v1/calendar?from=2027-02-01&to=2027-03-31&segment=Gold");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("InvalidFilter", (await Json(r)).GetProperty("guard").GetString());
    }

    [Fact]
    public async Task The_overview_rows_carry_the_type_the_chain_and_the_client_dates()
    {
        var w = await Build(); using var _f = w.F;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var o = await w.Rte.GetFromJsonAsync<JsonElement>($"/api/v1/overview?from={today.AddDays(-7):yyyy-MM-dd}&to={today:yyyy-MM-dd}");
        var rows = o.GetProperty("activity").EnumerateArray().ToDictionary(x => x.GetProperty("title").GetString()!);
        var prod = rows["Orion Production"];
        Assert.Equal("Production", prod.GetProperty("type").GetProperty("name").GetString());
        Assert.Equal("Production", prod.GetProperty("clientKind").GetString());
        Assert.Equal(["Standard|2027-03-05|2", "OnDemand|2027-03-12|2"], prod.GetProperty("clientDates").EnumerateArray().Select(d => $"{d.GetProperty("segment").GetString()}|{d.GetProperty("date").GetString()}|{d.GetProperty("count").GetInt32()}").ToArray());
        Assert.Equal(3, prod.GetProperty("chain").GetProperty("count").GetInt32());
        Assert.Equal(["OnDemand"], rows["Orion CERT"].GetProperty("clientDates").EnumerateArray().Select(d => d.GetProperty("segment").GetString()!).ToArray());
        Assert.Equal(JsonValueKind.Null, rows["Orion Beta 1"].GetProperty("clientDates").ValueKind);
        Assert.Equal("Beta", rows["Lone Beta"].GetProperty("type").GetProperty("name").GetString());
    }

    // ------------------------------------------------------------------ ICS

    /// <summary>The all-releases feed as a calendar app fetches it: no cookie, the token in the path. One token per user, so the token is made once per call to this and reused by the returned reader.</summary>
    private static async Task<Func<Task<string>>> Feeder(World w, string scope = "all", string? trainId = null)
    {
        var token = (await Json(await w.Rte.PostAsJsonAsync("/api/v1/me/ics-tokens", new { scope, trainId }))).GetProperty("token").GetString()!;
        return async () =>
        {
            using var anon = w.F.CreateClient();
            var r = await anon.GetAsync(trainId is null ? $"/api/v1/ics/{token}/all.ics" : $"/api/v1/ics/{token}/trains/{trainId}.ics");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            return await r.Content.ReadAsStringAsync();
        };
    }

    [Fact]
    public async Task The_feed_carries_the_type_the_version_and_one_event_per_client_date_with_stable_UIDs()
    {
        var w = await Build(); using var _f = w.F;
        var ics = await (await Feeder(w))();
        var events = IcsCalendar.Load(ics)!.Events.ToDictionary(e => e.Uid!);

        // type and version on the release's own events, titles untouched
        var target = events[$"{w.Prod}-target@releasemgmt"];
        Assert.Equal(["Production"], target.Categories);
        Assert.Contains("Release type: Production", target.Description);
        Assert.Contains("Version: Orion 5.2 · Production, 3 of 3", target.Description);
        Assert.EndsWith("Orion Production: target release date", target.Summary);
        Assert.Contains("Release type: Beta 1", events[$"{w.Solo}-target@releasemgmt"].Description);   // a Beta carries its number
        Assert.DoesNotContain("Version:", events[$"{w.Solo}-target@releasemgmt"].Description);

        // the Standard clients' one event and each On-Demand client's own, all-day (times are UTC dates), workspace first
        var standard = events[$"{w.Prod}-standard-clients@releasemgmt"];
        Assert.StartsWith("Default · ", standard.Summary);
        Assert.EndsWith("Orion Production: Standard clients get Production", standard.Summary);
        Assert.False(standard.DtStart!.HasTime);
        Assert.Equal(new DateTime(2027, 3, 5), standard.DtStart.Value.Date);
        Assert.Contains("Acme, Zeta", standard.Description);
        var globex = events[$"{w.Prod}-{w.Clients["Globex"]}-clientdate@releasemgmt"];
        Assert.EndsWith("Orion Production: On-Demand Production for Globex", globex.Summary);
        Assert.StartsWith("Default · ", globex.Summary);
        Assert.Equal(new DateTime(2027, 3, 12), globex.DtStart!.Value.Date);
        Assert.Equal(new DateTime(2027, 3, 13), globex.DtEnd!.Value.Date);   // DTEND of an all-day event is exclusive
        Assert.EndsWith("Orion CERT: On-Demand CERT for Initech", events[$"{w.CertId}-{w.Clients["Initech"]}-clientdate@releasemgmt"].Summary);
        Assert.DoesNotContain(events.Keys, k => k.StartsWith(w.Beta + "-") && k.Contains("client"));   // a Beta has no client dates
        Assert.DoesNotContain(events.Keys, k => k.StartsWith(w.CertId + "-standard"));                 // Standard clients have no CERT environment
    }

    [Fact]
    public async Task A_chosen_date_updates_the_same_event_and_raises_its_SEQUENCE_and_the_feed_is_otherwise_byte_identical()
    {
        var w = await Build(); using var _f = w.F;
        var uid = $"{w.Prod}-{w.Clients["Globex"]}-clientdate@releasemgmt";
        var feed = await Feeder(w);
        var before = await feed();
        Assert.Equal(before, await feed());   // an unchanged database yields the same file
        var e0 = IcsCalendar.Load(before)!.Events.Single(e => e.Uid == uid);
        Assert.Equal(HttpStatusCode.OK, (await Send(w.Rte, HttpMethod.Patch, $"/api/v1/trains/{w.Prod}/client-schedule/{w.Clients["Globex"]}", new { chosenDate = "2027-03-19" }, 0)).StatusCode);
        var after = IcsCalendar.Load(await feed())!.Events.ToDictionary(e => e.Uid!);
        var e1 = after[uid];
        Assert.Equal(new DateTime(2027, 3, 19), e1.DtStart!.Value.Date);   // same UID, new day
        Assert.True(e1.Sequence > e0.Sequence, $"SEQUENCE must grow when the date changes ({e0.Sequence} -> {e1.Sequence})");
        Assert.Equal(IcsCalendar.Load(before)!.Events.Single(e => e.Uid == $"{w.Prod}-{w.Clients["Initech"]}-clientdate@releasemgmt").Sequence, after[$"{w.Prod}-{w.Clients["Initech"]}-clientdate@releasemgmt"].Sequence);   // other clients are untouched
    }

    [Fact]
    public async Task Moving_the_Standard_date_moves_the_offered_dates_in_the_feed_under_the_same_UIDs()
    {
        var w = await Build(); using var _f = w.F;
        var uid = $"{w.Prod}-{w.Clients["Initech"]}-clientdate@releasemgmt";
        var feed = await Feeder(w);
        var e0 = IcsCalendar.Load(await feed())!.Events.Single(e => e.Uid == uid);
        var train = await w.Rte.GetFromJsonAsync<JsonElement>($"/api/v1/trains/{w.Prod}");
        var r = await Send(w.Rte, HttpMethod.Patch, $"/api/v1/trains/{w.Prod}", new { targetReleaseDate = "2027-03-08" }, train.GetProperty("version").GetInt32());
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var e1 = IcsCalendar.Load(await feed())!.Events.Single(e => e.Uid == uid);
        Assert.Equal(new DateTime(2027, 3, 15), e1.DtStart!.Value.Date);
        Assert.True(e1.Sequence > e0.Sequence);
    }

    [Fact]
    public async Task The_train_feed_carries_that_releases_client_dates_only()
    {
        var w = await Build(); using var _f = w.F;
        var ics = await (await Feeder(w, "train", w.CertId))();
        var uids = IcsCalendar.Load(ics)!.Events.Select(e => e.Uid!).ToArray();
        Assert.Contains($"{w.CertId}-{w.Clients["Globex"]}-clientdate@releasemgmt", uids);
        Assert.DoesNotContain(uids, u => u.StartsWith(w.Prod));
    }
}
