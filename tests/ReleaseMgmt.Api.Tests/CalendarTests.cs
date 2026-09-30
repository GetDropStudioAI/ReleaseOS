using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Services;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;
using IcsCalendar = Ical.Net.Calendar;

namespace ReleaseMgmt.Api.Tests;

/// <summary>REOS-51: calendar payload, ICS feeds (stable UIDs, byte-identical when unchanged), token lifecycle (hash only, rotate and revoke kill the old URL).</summary>
public class CalendarTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private sealed record Ctx(ApiFactory F, HttpClient Rte, HttpClient Gov, HttpClient Viewer, string RteId, string GovId);

    /// <summary>Train t1 (target 2026-10-30, gates g1 due 10-23 owned by the RTE, g2 due 10-28 owned by the governance officer), a window on the target day, a step owned by the RTE, a freeze and a chill.</summary>
    private static async Task<Ctx> Setup()
    {
        var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var gov = await As(f, Roles.GovernanceOfficer, "go@x.com");
        var viewer = await As(f, Roles.Viewer, "v@x.com");
        var rteId = UserId(f, "rte@x.com"); var govId = UserId(f, "go@x.com");
        SeedTrain(f, rteId, govId);
        Sql(f, $@"
            INSERT INTO DeploymentWindows(Id,ReleaseTrainId,StartsAt,EndsAt) VALUES('w1','t1','2026-10-30T06:00:00Z','2026-10-30T10:00:00Z');
            INSERT INTO RunbookSteps(Id,ReleaseTrainId,StepCode,Section,Title,OwnerUserId,PlannedStartAt,PlannedDurationMin) VALUES('s1','t1','R-001','Deploy','Deploy API, ""blue"";green','{rteId}','2026-10-30T06:10:00Z',30);
            INSERT INTO FreezeWindows(Id,Name,Kind,StartsAt,EndsAt,CreatedByUserId) VALUES('f1','Q4 close','Freeze','2026-12-20T00:00:00Z','2026-12-24T00:00:00Z','{govId}'),
                                                                                    ('c1','Peak week','Chill','2026-11-25T00:00:00Z','2026-11-27T12:00:00Z','{govId}');");
        return new Ctx(f, rte, gov, viewer, rteId, govId);
    }

    private static async Task<(string Id, string Token, string Path)> NewToken(HttpClient c, string scope = "all", string? trainId = null)
    {
        var r = await c.PostAsJsonAsync("/api/v1/me/ics-tokens", new { scope, trainId });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await Json(r);
        return (j.GetProperty("id").GetString()!, j.GetProperty("token").GetString()!, j.GetProperty("path").GetString()!);
    }

    private static async Task<HttpResponseMessage> Anon(ApiFactory f, string path) { using var c = f.CreateClient(); return await c.GetAsync(path); }
    private static async Task<string> Feed(ApiFactory f, string path)
    {
        var r = await Anon(f, path);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await r.Content.ReadAsStringAsync();
    }
    private static IcsCalendar Parse(string ics) => IcsCalendar.Load(ics)!;
    private static string[] Uids(string ics) => [.. Parse(ics).Events.Select(e => e.Uid!).Order(StringComparer.Ordinal)];

    // ------------------------------------------------------------------ calendar payload

    [Fact]
    public async Task Calendar_lists_trains_windows_gates_and_freeze_ranges_for_the_visible_range()
    {
        var c = await Setup(); using var _ = c.F;
        var r = await c.Viewer.GetAsync("/api/v1/calendar?from=2026-10-01&to=2026-10-31");   // any signed-in role reads
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await Json(r);
        var t = Assert.Single(j.GetProperty("trains").EnumerateArray());
        Assert.Equal("t1", t.GetProperty("id").GetString());
        Assert.Equal("R26.10", t.GetProperty("title").GetString());
        Assert.Equal("Planning", t.GetProperty("status").GetString());
        Assert.Equal("2026-10-30", t.GetProperty("targetDate").GetString());
        Assert.Equal("2026-10-30T06:00:00Z", t.GetProperty("window").GetProperty("startsAt").GetString());
        var gates = j.GetProperty("gates").EnumerateArray().ToList();
        Assert.Equal(["g1", "g2"], gates.Select(g => g.GetProperty("id").GetString()));
        Assert.Equal("2026-10-23", gates[0].GetProperty("dueOn").GetString());
        Assert.All(gates, g => { Assert.Equal("t1", g.GetProperty("trainId").GetString()); Assert.Equal("R26.10", g.GetProperty("trainTitle").GetString()); Assert.Equal("Planning", g.GetProperty("trainStatus").GetString()); });
        Assert.Empty(j.GetProperty("freezeWindows").EnumerateArray());   // December freeze and late-November chill are outside October
    }

    [Fact]
    public async Task Calendar_filters_by_range_and_returns_freeze_and_chill_as_ranges_with_their_kind()
    {
        var c = await Setup(); using var _ = c.F;
        var nov = await Json(await c.Rte.GetAsync("/api/v1/calendar?from=2026-11-01&to=2026-11-30"));
        Assert.Empty(nov.GetProperty("trains").EnumerateArray());
        Assert.Empty(nov.GetProperty("gates").EnumerateArray());
        var chill = Assert.Single(nov.GetProperty("freezeWindows").EnumerateArray());
        Assert.Equal("Chill", chill.GetProperty("kind").GetString());
        Assert.Equal("Peak week", chill.GetProperty("name").GetString());
        Assert.Equal("2026-11-25T00:00:00Z", chill.GetProperty("startsAt").GetString());
        Assert.Equal("2026-11-27T12:00:00Z", chill.GetProperty("endsAt").GetString());
        Assert.Equal("All products", chill.GetProperty("scope").GetString());

        // A range that starts inside a freeze still returns it (overlap, not containment); one that starts exactly at its end does not.
        Assert.Single((await Json(await c.Rte.GetAsync("/api/v1/calendar?from=2026-12-23&to=2026-12-31"))).GetProperty("freezeWindows").EnumerateArray());
        Assert.Empty((await Json(await c.Rte.GetAsync("/api/v1/calendar?from=2026-12-24&to=2026-12-31"))).GetProperty("freezeWindows").EnumerateArray());
        // A train whose window (not its target date) touches the range is listed.
        Sql(c.F, "UPDATE DeploymentWindows SET StartsAt='2026-11-01T02:00:00Z', EndsAt='2026-11-01T04:00:00Z' WHERE Id='w1'");
        Assert.Single((await Json(await c.Rte.GetAsync("/api/v1/calendar?from=2026-11-01&to=2026-11-01"))).GetProperty("trains").EnumerateArray());
        // Archived trains are never listed.
        Sql(c.F, "UPDATE ReleaseTrains SET ArchivedAt='2026-10-02T00:00:00Z' WHERE Id='t1'");
        var oct = await Json(await c.Rte.GetAsync("/api/v1/calendar?from=2026-10-01&to=2026-10-31"));
        Assert.Empty(oct.GetProperty("trains").EnumerateArray());
        Assert.Empty(oct.GetProperty("gates").EnumerateArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData("?from=2026-10-01")]
    [InlineData("?from=2026-13-01&to=2026-13-31")]
    [InlineData("?from=yesterday&to=today")]
    [InlineData("?from=2026-10-31&to=2026-10-01")]
    [InlineData("?from=2020-01-01&to=2026-01-01")]
    public async Task Calendar_rejects_bad_dates_with_400_InvalidFilter(string query)
    {
        var c = await Setup(); using var _ = c.F;
        var r = await c.Rte.GetAsync("/api/v1/calendar" + query);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("InvalidFilter", (await Json(r)).GetProperty("guard").GetString());
    }

    [Fact]
    public async Task Calendar_needs_a_signed_in_user()
    {
        var c = await Setup(); using var _ = c.F;
        Assert.Equal(HttpStatusCode.Unauthorized, (await Anon(c.F, "/api/v1/calendar?from=2026-10-01&to=2026-10-31")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Anon(c.F, "/api/v1/me/ics-tokens")).StatusCode);
        using var anon = c.F.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/v1/me/ics-tokens", new { scope = "all" })).StatusCode);
    }

    // ------------------------------------------------------------------ the feed

    [Fact]
    public async Task All_trains_feed_is_anonymous_by_token_and_parses_back_with_the_expected_events()
    {
        var c = await Setup(); using var _ = c.F;
        var tok = await NewToken(c.Rte);
        Assert.Equal($"/api/v1/ics/{tok.Token}.ics", tok.Path);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", tok.Token);   // 256 bits, base64url, no padding

        var r = await Anon(c.F, tok.Path);   // a client with no cookie
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("text/calendar", r.Content.Headers.ContentType!.MediaType);
        Assert.Contains("private", r.Headers.CacheControl!.ToString());
        Assert.NotNull(r.Headers.ETag);
        var ics = await r.Content.ReadAsStringAsync();
        Assert.Contains("PRODID:-//ReleaseMgmt//", ics);
        Assert.Contains("X-WR-CALNAME:Release trains", ics);

        var cal = Parse(ics);
        var ev = cal.Events.ToDictionary(e => e.Uid!);
        Assert.Equal(["g1-gate@releasemgmt", "g2-gate@releasemgmt", "t1-target@releasemgmt", "w1-window@releasemgmt"], Uids(ics).Where(u => !u.StartsWith("c1") && !u.StartsWith("f1")));
        Assert.Contains("c1-chill@releasemgmt", ev.Keys);
        Assert.Contains("f1-freeze@releasemgmt", ev.Keys);
        Assert.NotEmpty(ev.Keys);

        // gate due date: all-day, DTEND exclusive next day
        var g1 = ev["g1-gate@releasemgmt"];
        Assert.Equal("R26.10: Code Freeze due", g1.Summary);
        Assert.False(g1.DtStart!.HasTime);
        Assert.Equal(new DateTime(2026, 10, 23), g1.DtStart.Value.Date);
        Assert.Equal(new DateTime(2026, 10, 24), g1.DtEnd!.Value.Date);
        // target date all-day
        Assert.False(ev["t1-target@releasemgmt"].DtStart!.HasTime);
        Assert.Equal(new DateTime(2026, 10, 30), ev["t1-target@releasemgmt"].DtStart!.Value.Date);
        // window: timed, UTC in the file
        var w = ev["w1-window@releasemgmt"];
        Assert.True(w.DtStart!.HasTime);
        Assert.Equal(new DateTime(2026, 10, 30, 6, 0, 0), w.DtStart.Value);
        Assert.Equal(new DateTime(2026, 10, 30, 10, 0, 0), w.DtEnd!.Value);
        Assert.Contains("DTSTART:20261030T060000Z", ics);
        Assert.Contains("DTEND:20261030T100000Z", ics);
        // freeze: all-day range, 20 -> 24 Dec with the end at midnight = exclusive 24th; chill ends at noon so it runs through the 27th (exclusive 28th)
        Assert.Equal(new DateTime(2026, 12, 20), ev["f1-freeze@releasemgmt"].DtStart!.Value.Date);
        Assert.Equal(new DateTime(2026, 12, 24), ev["f1-freeze@releasemgmt"].DtEnd!.Value.Date);
        Assert.Equal("Freeze: Q4 close", ev["f1-freeze@releasemgmt"].Summary);
        Assert.Equal(new DateTime(2026, 11, 28), ev["c1-chill@releasemgmt"].DtEnd!.Value.Date);
        Assert.Equal("Chill: Peak week", ev["c1-chill@releasemgmt"].Summary);
        // titles and times only
        Assert.DoesNotContain("DESCRIPTION", ics);
        // the all-trains feed has no runbook steps (they are personal)
        Assert.DoesNotContain("s1-step@releasemgmt", ics);
    }

    [Fact]
    public async Task Other_feed_scopes_train_freezes_and_mine()
    {
        var c = await Setup(); using var _ = c.F;
        Sql(c.F, $@"INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CreatedAt,UpdatedAt) VALUES('t2','Other','2026-11-13','Low','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z')");
        var tok = await NewToken(c.Rte);
        var train = await Feed(c.F, $"/api/v1/ics/{tok.Token}/trains/t1.ics");
        Assert.Contains("t1-target@releasemgmt", train);
        Assert.DoesNotContain("t2-target@releasemgmt", train);
        Assert.Contains("X-WR-CALNAME:R26.10", train);
        Assert.Equal(HttpStatusCode.NotFound, (await Anon(c.F, $"/api/v1/ics/{tok.Token}/trains/nope.ics")).StatusCode);
        Assert.Contains("t2-target@releasemgmt", await Feed(c.F, $"/api/v1/ics/{tok.Token}/all.ics"));

        var freezes = await Feed(c.F, $"/api/v1/ics/{tok.Token}/freezes.ics");
        Assert.Equal(["c1-chill@releasemgmt", "f1-freeze@releasemgmt"], Uids(freezes));

        var mine = await Feed(c.F, $"/api/v1/ics/{tok.Token}/mine.ics");
        // the RTE owns g1 and step s1; the governance officer owns g2
        Assert.Equal(["g1-gate@releasemgmt", "s1-step@releasemgmt"], Uids(mine));
        var step = Parse(mine).Events.Single(e => e.Uid == "s1-step@releasemgmt");
        Assert.Equal("R26.10: R-001 Deploy API, \"blue\";green", step.Summary);   // commas, quotes and semicolons survive Ical.Net's escaping
        Assert.Equal(new DateTime(2026, 10, 30, 6, 10, 0), step.DtStart!.Value);
        Assert.Equal(new DateTime(2026, 10, 30, 6, 40, 0), step.DtEnd!.Value);
        Assert.Contains("\\,", mine);

        var gov = await NewToken(c.Gov);
        Assert.Equal(["g2-gate@releasemgmt"], Uids(await Feed(c.F, $"/api/v1/ics/{gov.Token}/mine.ics")));
        // a certified gate leaves "mine"
        Sql(c.F, "UPDATE ChecklistTasks SET IsCompleted=1, CompletedAt='2026-10-21T00:00:00Z', CompletedByUserId=OwnerUserId WHERE Id='k1'");
        Sql(c.F, "UPDATE StageGates SET Status='InProgress' WHERE Id='g1'");
        Sql(c.F, "UPDATE StageGates SET Status='Certified', CertifiedByUserId=OwnerUserId, CertifiedAt='2026-10-22T00:00:00Z', LastChangedAt='2026-10-22T00:00:00Z' WHERE Id='g1'");
        Assert.Equal(["s1-step@releasemgmt"], Uids(await Feed(c.F, $"/api/v1/ics/{tok.Token}/mine.ics")));
    }

    [Fact]
    public async Task Team_owned_work_shows_in_the_feed_of_a_team_member()
    {
        var c = await Setup(); using var _ = c.F;
        Sql(c.F, $@"INSERT INTO Teams(Id,Handle,Name) VALUES('tm1','ops','Ops');
                    INSERT INTO TeamMembers(TeamId,UserId) VALUES('tm1','{c.GovId}');
                    UPDATE StageGates SET OwnerUserId=NULL, OwnerTeamId='tm1' WHERE Id='g1'");
        var gov = await NewToken(c.Gov);
        Assert.Equal(["g1-gate@releasemgmt", "g2-gate@releasemgmt"], Uids(await Feed(c.F, $"/api/v1/ics/{gov.Token}/mine.ics")));
    }

    // ------------------------------------------------------------------ stability

    [Fact]
    public async Task Uids_are_stable_when_a_gate_date_moves_and_sequence_is_bumped()
    {
        var c = await Setup(); using var _ = c.F;
        var tok = await NewToken(c.Rte);
        var before = await Feed(c.F, tok.Path);
        var beforeMine = await Feed(c.F, $"/api/v1/ics/{tok.Token}/mine.ics");
        var evBefore = Parse(before).Events.ToDictionary(e => e.Uid!);

        // the real service: changing the target date recomputes every gate's DueOn (business days) and bumps the versions
        var schedule = c.F.Services.GetRequiredService<ScheduleService>();
        var moved = await schedule.ChangeTargetDateAsync("t1", new DateOnly(2026, 11, 6), new Actor(c.RteId), ct: CancellationToken.None);
        Assert.True(moved.IsOk);

        var after = await Feed(c.F, tok.Path);
        var evAfter = Parse(after).Events.ToDictionary(e => e.Uid!);
        Assert.Equal(evBefore.Keys.Order(), evAfter.Keys.Order());                                  // same UIDs: an update, not a delete + add
        Assert.NotEqual(evBefore["g1-gate@releasemgmt"].DtStart!.Value, evAfter["g1-gate@releasemgmt"].DtStart!.Value);   // the date moved
        Assert.NotEqual(evBefore["t1-target@releasemgmt"].DtStart!.Value, evAfter["t1-target@releasemgmt"].DtStart!.Value);
        foreach (var uid in new[] { "g1-gate@releasemgmt", "g2-gate@releasemgmt", "t1-target@releasemgmt", "w1-window@releasemgmt" })
            Assert.True(evAfter[uid].Sequence > evBefore[uid].Sequence, $"{uid}: SEQUENCE must rise when the event changed ({evBefore[uid].Sequence} -> {evAfter[uid].Sequence})");
        Assert.Equal(evBefore["f1-freeze@releasemgmt"].Sequence, evAfter["f1-freeze@releasemgmt"].Sequence);   // untouched freeze
        Assert.Equal(new DateTime(2026, 11, 6), evAfter["t1-target@releasemgmt"].DtStart!.Value.Date);
        // the same event moved in the "mine" feed too
        Assert.Equal(["g1-gate@releasemgmt", "s1-step@releasemgmt"], Uids(await Feed(c.F, $"/api/v1/ics/{tok.Token}/mine.ics")));
        Assert.Equal(Uids(beforeMine), Uids(await Feed(c.F, $"/api/v1/ics/{tok.Token}/mine.ics")));
    }

    [Fact]
    public async Task An_unchanged_feed_is_byte_identical_across_requests_and_time()
    {
        var c = await Setup(); using var _ = c.F;
        var tok = await NewToken(c.Rte);
        var a = await Feed(c.F, tok.Path);
        await Task.Delay(1100);   // DTSTAMP comes from stored timestamps, never from the wall clock
        var b = await Feed(c.F, tok.Path);
        Assert.Equal(a, b);
        Assert.Equal(await Feed(c.F, $"/api/v1/ics/{tok.Token}/mine.ics"), await Feed(c.F, $"/api/v1/ics/{tok.Token}/mine.ics"));
        // a rotated token serves the same bytes: the file does not depend on the token
        var rot = await (await c.Rte.PostAsync($"/api/v1/me/ics-tokens/{tok.Id}:rotate", JsonContent.Create(new { scope = "all" }))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(a, await Feed(c.F, rot.GetProperty("path").GetString()!));
    }

    [Fact]
    public async Task Etag_gives_304_and_changes_when_the_feed_changes()
    {
        var c = await Setup(); using var _ = c.F;
        var tok = await NewToken(c.Rte);
        var r1 = await Anon(c.F, tok.Path);
        var etag = r1.Headers.ETag!.ToString();
        using var client = c.F.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, tok.Path); req.Headers.TryAddWithoutValidation("If-None-Match", etag);
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(req)).StatusCode);
        Sql(c.F, "UPDATE ReleaseTrains SET Title='R26.10b', Version=Version+1, UpdatedAt='2026-10-02T00:00:00Z' WHERE Id='t1'");
        var req2 = new HttpRequestMessage(HttpMethod.Get, tok.Path); req2.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var r2 = await client.SendAsync(req2);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        Assert.NotEqual(etag, r2.Headers.ETag!.ToString());
    }

    // ------------------------------------------------------------------ tokens

    [Fact]
    public async Task A_rotated_token_stops_working_and_the_new_one_works()
    {
        var c = await Setup(); using var _ = c.F;
        var old = await NewToken(c.Rte);
        Assert.Equal(HttpStatusCode.OK, (await Anon(c.F, old.Path)).StatusCode);
        var r = await c.Rte.PostAsync($"/api/v1/me/ics-tokens/{old.Id}:rotate", JsonContent.Create(new { scope = "all" }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await Json(r);
        var fresh = j.GetProperty("token").GetString()!;
        Assert.NotEqual(old.Token, fresh);
        Assert.Equal(HttpStatusCode.NotFound, (await Anon(c.F, old.Path)).StatusCode);                          // immediately
        Assert.Equal(HttpStatusCode.NotFound, (await Anon(c.F, $"/api/v1/ics/{old.Token}/mine.ics")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Anon(c.F, $"/api/v1/ics/{fresh}.ics")).StatusCode);
        Assert.Equal("1", Scalar(c.F, "SELECT COUNT(*) FROM IcsTokens WHERE RevokedAt IS NULL"));
        Assert.Equal("2", Scalar(c.F, "SELECT COUNT(*) FROM IcsTokens"));
        // the old token's row cannot be rotated again
        var again = await c.Rte.PostAsync($"/api/v1/me/ics-tokens/{old.Id}:rotate", JsonContent.Create(new { scope = "all" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
        Assert.Equal("IcsTokenRevoked", (await Json(again)).GetProperty("guard").GetString());
    }

    [Fact]
    public async Task A_revoked_token_gets_404_indistinguishable_from_one_that_never_existed()
    {
        var c = await Setup(); using var _ = c.F;
        var tok = await NewToken(c.Rte);
        var rev = await c.Rte.PostAsync($"/api/v1/me/ics-tokens/{tok.Id}:revoke", null);
        Assert.Equal(HttpStatusCode.OK, rev.StatusCode);
        var gone = await Anon(c.F, tok.Path);
        var never = await Anon(c.F, $"/api/v1/ics/{new string('A', 43)}.ics");
        var junk = await Anon(c.F, "/api/v1/ics/x.ics");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, never.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, junk.StatusCode);
        Assert.Equal(await never.Content.ReadAsStringAsync(), await gone.Content.ReadAsStringAsync());
        Assert.Equal(never.Content.Headers.ContentType?.ToString(), gone.Content.Headers.ContentType?.ToString());
        // revoke is idempotent, and after it a new link can be created
        Assert.Equal(HttpStatusCode.OK, (await c.Rte.PostAsync($"/api/v1/me/ics-tokens/{tok.Id}:revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.Rte.PostAsJsonAsync("/api/v1/me/ics-tokens", new { scope = "mine" })).StatusCode);
    }

    [Fact]
    public async Task One_active_token_per_user_and_nobody_else_can_touch_it()
    {
        var c = await Setup(); using var _ = c.F;
        var tok = await NewToken(c.Rte);
        var second = await c.Rte.PostAsJsonAsync("/api/v1/me/ics-tokens", new { scope = "all" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        Assert.Equal("IcsTokenExists", (await Json(second)).GetProperty("guard").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await c.Gov.PostAsync($"/api/v1/me/ics-tokens/{tok.Id}:revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.Gov.PostAsync($"/api/v1/me/ics-tokens/{tok.Id}:rotate", JsonContent.Create(new { scope = "all" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Anon(c.F, tok.Path)).StatusCode);   // still alive
        Assert.Empty((await Json(await c.Gov.GetAsync("/api/v1/me/ics-tokens"))).EnumerateArray());   // and invisible to others
        foreach (var bad in new object[] { new { scope = "everything" }, new { scope = "train" }, new { scope = "train", trainId = "nope" } })
        {
            var r = await c.Viewer.PostAsJsonAsync("/api/v1/me/ics-tokens", bad);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
            Assert.Equal("InvalidIcsScope", (await Json(r)).GetProperty("guard").GetString());
        }
        var trainTok = await c.Viewer.PostAsJsonAsync("/api/v1/me/ics-tokens", new { scope = "train", trainId = "t1" });
        Assert.EndsWith("/trains/t1.ics", (await Json(trainTok)).GetProperty("path").GetString());
    }

    [Fact]
    public async Task Listing_never_returns_the_token_or_its_hash_and_last_used_is_stamped()
    {
        var c = await Setup(); using var _ = c.F;
        var tok = await NewToken(c.Rte);
        var before = await Json(await c.Rte.GetAsync("/api/v1/me/ics-tokens"));
        Assert.Equal(JsonValueKind.Null, before[0].GetProperty("lastUsedAt").ValueKind);
        await Feed(c.F, tok.Path);
        var raw = await (await c.Rte.GetAsync("/api/v1/me/ics-tokens")).Content.ReadAsStringAsync();
        var row = Assert.Single(JsonDocument.Parse(raw).RootElement.EnumerateArray());
        Assert.Equal(tok.Id, row.GetProperty("id").GetString());
        Assert.True(row.GetProperty("active").GetBoolean());
        Assert.NotNull(row.GetProperty("createdAt").GetString());
        Assert.NotNull(row.GetProperty("lastUsedAt").GetString());
        Assert.DoesNotContain(tok.Token, raw);
        Assert.DoesNotContain(IcsTokenService.Hash(tok.Token), raw);
        Assert.DoesNotContain("token", raw, StringComparison.OrdinalIgnoreCase);   // no property carries a secret, hash or path
        Assert.DoesNotContain("sha", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Only_the_hash_is_stored_the_plaintext_is_in_no_file_audit_row_or_log()
    {
        var c = await Setup(); using var _ = c.F;
        var tok = await NewToken(c.Rte);
        await Feed(c.F, tok.Path);
        await Anon(c.F, $"/api/v1/ics/{new string('Z', 43)}.ics");
        var rot = await Json(await c.Rte.PostAsync($"/api/v1/me/ics-tokens/{tok.Id}:rotate", JsonContent.Create(new { scope = "all" })));
        var tok2 = rot.GetProperty("token").GetString()!;
        await Feed(c.F, $"/api/v1/ics/{tok2}.ics");

        Assert.Equal(IcsTokenService.Hash(tok2), Scalar(c.F, "SELECT TokenSha256 FROM IcsTokens WHERE RevokedAt IS NULL"));
        Assert.Equal(64, Scalar(c.F, "SELECT TokenSha256 FROM IcsTokens WHERE RevokedAt IS NULL").Length);
        Assert.Equal("0", Scalar(c.F, $"SELECT COUNT(*) FROM AuditEvents WHERE COALESCE(BeforeJson,'') || COALESCE(AfterJson,'') || EntityId LIKE '%{tok.Token}%' OR COALESCE(BeforeJson,'') || COALESCE(AfterJson,'') LIKE '%{tok2}%'"));
        Assert.Equal("0", Scalar(c.F, $"SELECT COUNT(*) FROM AuditEvents WHERE COALESCE(AfterJson,'') LIKE '%{IcsTokenService.Hash(tok2)}%'"));

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var dir = Path.GetDirectoryName(c.F.DbPath)!;
        var checkedFiles = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            byte[] bytes;
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var ms = new MemoryStream()) { fs.CopyTo(ms); bytes = ms.ToArray(); }
            checkedFiles++;
            foreach (var secret in new[] { tok.Token, tok2 })
                Assert.False(Contains(bytes, Encoding.UTF8.GetBytes(secret)), $"the plaintext token is in {Path.GetFileName(file)}");
        }
        Assert.True(checkedFiles >= 2, "expected the database and the log to be scanned");
        Assert.Contains(Directory.EnumerateFiles(dir, "*.db*"), _ => true);
    }

    private static bool Contains(byte[] hay, byte[] needle) => hay.AsSpan().IndexOf(needle) >= 0;

    [Fact]
    public async Task Token_writes_are_audited_in_the_same_transaction_without_the_secret()
    {
        var c = await Setup(); using var _ = c.F;
        var tok = await NewToken(c.Rte);
        Assert.Equal("1", Scalar(c.F, $"SELECT COUNT(*) FROM AuditEvents WHERE EntityType='IcsToken' AND EntityId='{tok.Id}' AND Action='Create' AND ActorUserId='{c.RteId}'"));
        var rot = await Json(await c.Rte.PostAsync($"/api/v1/me/ics-tokens/{tok.Id}:rotate", JsonContent.Create(new { scope = "mine" })));
        var newId = rot.GetProperty("id").GetString()!;
        Assert.Equal("1", Scalar(c.F, $"SELECT COUNT(*) FROM AuditEvents WHERE EntityType='IcsToken' AND EntityId='{newId}' AND Action='Rotate' AND BeforeJson LIKE '%{tok.Id}%'"));
        await c.Rte.PostAsync($"/api/v1/me/ics-tokens/{newId}:revoke", null);
        Assert.Equal("1", Scalar(c.F, $"SELECT COUNT(*) FROM AuditEvents WHERE EntityType='IcsToken' AND EntityId='{newId}' AND Action='Revoke'"));
        Assert.Equal("3", Scalar(c.F, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='IcsToken'"));
        // a rejected write leaves nothing behind
        await c.Rte.PostAsJsonAsync("/api/v1/me/ics-tokens", new { scope = "bogus" });
        Assert.Equal("3", Scalar(c.F, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='IcsToken'"));
        Assert.Equal("2", Scalar(c.F, "SELECT COUNT(*) FROM IcsTokens"));
    }

    [Fact]
    public async Task Feed_needs_no_cookie_but_a_signed_in_cookie_alone_is_not_a_token()
    {
        var c = await Setup(); using var _ = c.F;
        Assert.Equal(HttpStatusCode.NotFound, (await c.Rte.GetAsync("/api/v1/ics/whatever-is-not-a-token-at-all.ics")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Anon(c.F, "/api/v1/ics/whatever-is-not-a-token-at-all/mine.ics")).StatusCode);
    }

    [Fact]
    public async Task Repeated_bad_tokens_are_throttled_with_429()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var tok = await NewToken(rte);
        using var c = f.CreateClient();
        HttpStatusCode last = 0;
        for (var i = 0; i < 70; i++) last = (await c.GetAsync($"/api/v1/ics/{new string('B', 43)}.ics")).StatusCode;
        Assert.Equal(HttpStatusCode.TooManyRequests, last);
    }
}
