using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using ReleaseMgmt.Domain.Common;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// REOS-179 (D56, D63): what the CAB workbench needs from the API beyond REOS-175/177: reordering the agenda in one transaction, the completion time, a Completed meeting that no longer
/// changes (decisions, agenda, roll), and who is connected. No schema change.
/// </summary>
public class CabWorkbenchApiTests
{
    private const string W = "2026-12-01T02:00:00Z", W2 = "2026-12-01T04:00:00Z";

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
    private static async Task<string> Guard(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == HttpStatusCode.UnprocessableEntity, $"{(int)r.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.GetProperty("guard").GetString()!;
    }
    private static string Id(JsonElement e) => e.GetProperty("id").GetString()!;
    private static int Ver(JsonElement e) => e.GetProperty("version").GetInt32();
    private static string[] Order(JsonElement meeting) => [.. meeting.GetProperty("agenda").EnumerateArray().Select(a => a.GetProperty("changeKey").GetString()!)];

    private sealed record World(ApiFactory F, HttpClient Rte, HttpClient Rm, HttpClient Gov, HttpClient Viewer, string RmId, string GovId) : IDisposable { public void Dispose() => F.Dispose(); }

    private static async Task<World> Enabled()
    {
        var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var rm = await As(f, Roles.ReleaseManager, "rm@x.com");
        var gov = await As(f, Roles.GovernanceOfficer, "gov@x.com");
        var viewer = await As(f, Roles.Viewer, "v@x.com");
        (await Send(rte, HttpMethod.Post, "/api/v1/modules/cab:enable", new { }, 0)).EnsureSuccessStatusCode();
        return new World(f, rte, rm, gov, viewer, UserId(f, "rm@x.com"), UserId(f, "gov@x.com"));
    }

    private static async Task<JsonElement> Submitted(HttpClient c, string title)
    {
        var ch = await Ok(await Send(c, HttpMethod.Post, "/api/v1/cab/changes", new { title, changeType = "Normal", rollbackNote = "Redeploy", plannedStartAt = W, plannedEndAt = W2 }));
        return await Ok(await Send(c, HttpMethod.Post, $"/api/v1/cab/changes/{Id(ch)}:submit", new { }, Ver(ch)));
    }

    /// <summary>A started meeting chaired by the RM with three changes on its agenda, in the order raised.</summary>
    private static async Task<(JsonElement Meeting, JsonElement[] Changes)> Held(World w)
    {
        var m = await Ok(await Send(w.Rm, HttpMethod.Post, "/api/v1/cab/meetings", new { title = "Weekly CAB", startsAt = W, endsAt = W2, chairUserId = w.RmId }));
        var changes = new List<JsonElement>();
        foreach (var t in new[] { "First", "Second", "Third" })
        {
            var c = await Submitted(w.Rte, t);
            changes.Add(c);
            m = await Ok(await Send(w.Rm, HttpMethod.Post, $"/api/v1/cab/meetings/{Id(m)}/agenda", new { changeRequestId = Id(c) }, Ver(m)));
        }
        m = await Ok(await Send(w.Rm, HttpMethod.Post, $"/api/v1/cab/meetings/{Id(m)}:start", new { }, Ver(m)));
        return (m, [.. changes]);
    }

    [Fact]
    public async Task Moving_an_agenda_item_renumbers_the_agenda_in_one_step_and_needs_the_current_meeting_version()
    {
        using var w = await Enabled();
        var (m, _) = await Held(w);
        var keys = Order(m);
        Assert.Equal(3, keys.Length);
        var third = m.GetProperty("agenda")[2];

        var moved = await Ok(await Send(w.Rm, HttpMethod.Post, $"/api/v1/cab/meetings/{Id(m)}/agenda/{Id(third)}:move", new { position = 1 }, Ver(m)));
        Assert.Equal([keys[2], keys[0], keys[1]], Order(moved));
        Assert.Equal([1, 2, 3], moved.GetProperty("agenda").EnumerateArray().Select(a => a.GetProperty("sequenceOrder").GetInt32()).ToArray());
        Assert.Equal(Ver(m) + 1, Ver(moved));

        // past the end means last; the same position again changes nothing but is still a recorded move
        var last = await Ok(await Send(w.Rm, HttpMethod.Post, $"/api/v1/cab/meetings/{Id(m)}/agenda/{Id(third)}:move", new { position = 99 }, Ver(moved)));
        Assert.Equal(keys, Order(last));
        Assert.Equal(2, long.Parse(Scalar(w.F, "SELECT COUNT(*) FROM AuditEvents WHERE Action='Reorder' AND EntityType='CabAgendaItem'")));

        // a second chair working from the older screen gets 409 with the meeting as it is now
        var stale = await Send(w.Rm, HttpMethod.Post, $"/api/v1/cab/meetings/{Id(m)}/agenda/{Id(third)}:move", new { position = 1 }, Ver(m));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var body = JsonDocument.Parse(await stale.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(Ver(last), body.GetProperty("current").GetProperty("version").GetInt32());

        Assert.Equal("CabMeetingInvalid", await Guard(await Send(w.Rm, HttpMethod.Post, $"/api/v1/cab/meetings/{Id(m)}/agenda/nope:move", new { position = 1 }, Ver(last))));
        Assert.Equal("CabMeetingInvalid", await Guard(await Send(w.Rm, HttpMethod.Post, $"/api/v1/cab/meetings/{Id(m)}/agenda/{Id(third)}:move", new { position = 0 }, Ver(last))));
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(w.Viewer, HttpMethod.Post, $"/api/v1/cab/meetings/{Id(m)}/agenda/{Id(third)}:move", new { position = 1 }, Ver(last))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(w.Gov, HttpMethod.Post, $"/api/v1/cab/meetings/{Id(m)}/agenda/{Id(third)}:move", new { position = 1 }, Ver(last))).StatusCode);
    }

    [Fact]
    public async Task A_completed_meeting_shows_when_it_ended_and_refuses_decisions_reordering_and_roll_changes()
    {
        using var w = await Enabled();
        var (m, changes) = await Held(w);
        Assert.Equal(JsonValueKind.Null, m.GetProperty("completedAt").ValueKind);
        var first = m.GetProperty("agenda")[0];

        // a decision made in the meeting is saved to its record
        var ch = await Ok(await Send(w.Gov, HttpMethod.Post, $"/api/v1/cab/changes/{Id(changes[0])}/decisions", new { decision = "Approved", rationale = "Rollback tested", meetingId = Id(m) }, Ver(changes[0])));
        Assert.Equal("Approved", ch.GetProperty("status").GetString());

        m = await Ok(await Send(w.Rm, HttpMethod.Post, $"/api/v1/cab/meetings/{Id(m)}:complete", new { }, Ver(m)));
        Assert.Equal("Completed", m.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.String, m.GetProperty("completedAt").ValueKind);
        Assert.Equal(1, m.GetProperty("decisions").GetArrayLength());

        var again = await Ok(await w.Viewer.GetAsync($"/api/v1/cab/meetings/{Id(m)}"));
        Assert.Equal(m.GetProperty("completedAt").GetString(), again.GetProperty("completedAt").GetString());

        // the record no longer changes: a decision, a move, a note on an item, an attendance mark
        Assert.Equal("CabMeetingClosed", await Guard(await Send(w.Gov, HttpMethod.Post, $"/api/v1/cab/changes/{Id(changes[1])}/decisions", new { decision = "Approved", rationale = "Late", meetingId = Id(m) }, Ver(changes[1]))));
        Assert.Equal("CabMeetingClosed", await Guard(await Send(w.Rm, HttpMethod.Post, $"/api/v1/cab/meetings/{Id(m)}/agenda/{Id(first)}:move", new { position = 3 }, Ver(m))));
        Assert.Equal("CabMeetingClosed", await Guard(await Send(w.Rm, new HttpMethod("PATCH"), $"/api/v1/cab/meetings/{Id(m)}/agenda/{Id(first)}", new { notes = "Edited" }, Ver(first))));
        var chair = m.GetProperty("attendees")[0];
        Assert.Equal("CabMeetingClosed", await Guard(await Send(w.Rm, new HttpMethod("PATCH"), $"/api/v1/cab/meetings/{Id(m)}/attendees/{w.RmId}", new { attendance = "Absent" }, chair.GetProperty("version").GetInt32())));
        Assert.Equal(1, long.Parse(Scalar(w.F, "SELECT COUNT(*) FROM CabDecisions")));
    }

    [Fact]
    public async Task Two_people_deciding_the_same_item_at_once_get_one_decision_and_a_409_with_the_current_row()
    {
        using var w = await Enabled();
        var (m, changes) = await Held(w);
        var c = changes[0];
        var results = await Task.WhenAll(
            Send(w.Gov, HttpMethod.Post, $"/api/v1/cab/changes/{Id(c)}/decisions", new { decision = "Approved", rationale = "Fine", meetingId = Id(m) }, Ver(c)),
            Send(w.Rm, HttpMethod.Post, $"/api/v1/cab/changes/{Id(c)}/decisions", new { decision = "Rejected", rationale = "Not ready", meetingId = Id(m) }, Ver(c)));
        var codes = results.Select(r => r.StatusCode).OrderBy(s => (int)s).ToArray();
        Assert.True(codes.SequenceEqual([HttpStatusCode.OK, HttpStatusCode.Conflict]) || codes.SequenceEqual([HttpStatusCode.OK, HttpStatusCode.UnprocessableEntity]), string.Join(",", codes));
        Assert.Equal(1, long.Parse(Scalar(w.F, "SELECT COUNT(*) FROM CabDecisions")));
        var loser = results.Single(r => r.StatusCode != HttpStatusCode.OK);
        if (loser.StatusCode == HttpStatusCode.Conflict)
            Assert.NotEqual(Ver(c), JsonDocument.Parse(await loser.Content.ReadAsStringAsync()).RootElement.GetProperty("current").GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Presence_lists_the_attendees_with_a_live_connection_and_the_hub_says_when_that_changes()
    {
        using var w = await Enabled();
        var m = await Ok(await Send(w.Rm, HttpMethod.Post, "/api/v1/cab/meetings", new { title = "Weekly CAB", startsAt = W, endsAt = W2, chairUserId = w.RmId }));
        m = await Ok(await Send(w.Rm, HttpMethod.Post, $"/api/v1/cab/meetings/{Id(m)}/attendees", new { userId = w.GovId }, Ver(m)));

        async Task<string[]> Connected() => [.. (await Ok(await w.Viewer.GetAsync($"/api/v1/cab/meetings/{Id(m)}/presence"))).GetProperty("userIds").EnumerateArray().Select(x => x.GetString()!)];
        Assert.Empty(await Connected());
        Assert.Equal(HttpStatusCode.NotFound, (await w.Viewer.GetAsync("/api/v1/cab/meetings/nope/presence")).StatusCode);

        var events = Channel.CreateUnbounded<string>();
        async Task<HubConnection> Open(string email, string role, bool listen)
        {
            var login = await w.F.CreateClient().PostAsJsonAsync("/auth/dev-login", new { email, name = email.Split('@')[0], role });
            login.EnsureSuccessStatusCode();
            var cookie = login.Headers.GetValues("Set-Cookie").First().Split(';')[0];
            var hub = new HubConnectionBuilder().WithUrl(new Uri(w.F.Server.BaseAddress, "/hub/trains"), o =>
            {
                o.HttpMessageHandlerFactory = _ => w.F.Server.CreateHandler(); o.Transports = HttpTransportType.LongPolling; o.Headers["Cookie"] = cookie;
            }).Build();
            if (listen) hub.On("CabPresence", () => events.Writer.TryWrite("CabPresence"));
            await hub.StartAsync();
            return hub;
        }
        async Task<bool> Heard()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try { await events.Reader.ReadAsync(cts.Token); return true; } catch (OperationCanceledException) { return false; }
        }

        await using var watcher = await Open("v@x.com", "Viewer", listen: true);
        Assert.True(await Heard());   // the watcher's own connection is a change too
        await using var gov = await Open("gov@x.com", "GovernanceOfficer", listen: false);
        Assert.True(await Heard());
        Assert.Equal([w.GovId], await Connected());   // the viewer is connected but not on the roll, so not listed
        await gov.StopAsync();
        Assert.True(await Heard());
        Assert.Empty(await Connected());
    }

    [Fact]
    public async Task With_the_module_off_the_new_routes_answer_404()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        foreach (var (m, url) in new[] { (HttpMethod.Post, "/api/v1/cab/meetings/x/agenda/y:move"), (HttpMethod.Get, "/api/v1/cab/meetings/x/presence") })
        {
            var r = await Send(rte, m, url, m == HttpMethod.Get ? null : new { position = 1 });
            Assert.True(r.StatusCode == HttpStatusCode.NotFound, $"{m} {url}: {(int)r.StatusCode}");
            Assert.Equal("", await r.Content.ReadAsStringAsync());
        }
    }
}
