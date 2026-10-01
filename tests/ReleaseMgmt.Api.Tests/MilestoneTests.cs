using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ReleaseMgmt.Domain.Common;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;
using IcsCalendar = Ical.Net.Calendar;

namespace ReleaseMgmt.Api.Tests;

/// <summary>Train milestones (decision 2026-10-01, Q-0840..Q-0846): CRUD + done/undone with Version, If-Match and one audit row per write; who may do what;
/// informational only (never a readiness guard); shown on the calendar and in the ICS feeds with stable UIDs; exported as a grid.</summary>
public class MilestoneTests
{
    private sealed record Ctx(ApiFactory F, HttpClient Rte, HttpClient Rm, HttpClient Gov, HttpClient Viewer, string RteId, string GovId, string ViewerId);

    private static async Task<Ctx> Setup(bool requireIfMatch = false)
    {
        var f = new ApiFactory(requireIfMatch: requireIfMatch);
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var rm = await As(f, Roles.ReleaseManager, "rm@x.com");
        var gov = await As(f, Roles.GovernanceOfficer, "gov@x.com");
        var viewer = await As(f, Roles.Viewer, "v@x.com");
        var (rteId, govId, viewerId) = (UserId(f, "rte@x.com"), UserId(f, "gov@x.com"), UserId(f, "v@x.com"));
        SeedTrain(f, rteId, govId);
        return new Ctx(f, rte, rm, gov, viewer, rteId, govId, viewerId);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private static async Task<HttpResponseMessage> Send(HttpClient c, HttpMethod m, string url, object? body = null, int? ifMatch = null)
    {
        var req = new HttpRequestMessage(m, url);
        if (body is not null || m == HttpMethod.Post) req.Content = JsonContent.Create(body ?? new { });
        if (ifMatch is int v) req.Headers.TryAddWithoutValidation("If-Match", v.ToString());
        return await c.SendAsync(req);
    }

    private static async Task<JsonElement> Add(Ctx c, object body)
    {
        var r = await Send(c.Rte, HttpMethod.Post, "/api/v1/trains/t1/milestones", body);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await Json(r);
    }

    private static string Audit(ApiFactory f, string id) =>
        Scalar(f, $"SELECT group_concat(Action || ':' || COALESCE(ActorUserId,'-') || ':' || COALESCE(ReleaseTrainId,'-'), ',') FROM (SELECT * FROM AuditEvents WHERE EntityType='Milestone' AND EntityId='{id}' ORDER BY Id)");

    [Fact]
    public async Task Each_action_bumps_Version_stamps_the_actor_and_writes_one_audit_row()
    {
        var c = await Setup(); using var _ = c.F;
        var m = await Add(c, new { name = "  Code complete ", dueOn = "2026-10-16", ownerUserId = c.RteId, note = "All feature branches merged" });
        var id = m.GetProperty("id").GetString()!;
        Assert.Equal("Code complete", m.GetProperty("name").GetString());   // trimmed
        Assert.Equal(1, m.GetProperty("version").GetInt32());
        Assert.Equal(c.RteId, Scalar(c.F, $"SELECT LastChangedByUserId FROM TrainMilestones WHERE Id='{id}'"));
        Assert.NotEqual("", Scalar(c.F, $"SELECT LastChangedAt FROM TrainMilestones WHERE Id='{id}'"));

        // list: business days to target (Fri 16 Oct -> Fri 30 Oct = 10), owner name, ordered by date
        await Add(c, new { name = "UAT sign-off", dueOn = "2026-10-21" });
        var list = (await Json(await c.Viewer.GetAsync("/api/v1/trains/t1/milestones"))).EnumerateArray().ToList();
        Assert.Equal(["Code complete", "UAT sign-off"], list.Select(x => x.GetProperty("name").GetString()));
        Assert.Equal(10, list[0].GetProperty("tMinus").GetInt32());
        Assert.Equal("rte", list[0].GetProperty("ownerName").GetString());
        Assert.Equal(JsonValueKind.Null, list[1].GetProperty("ownerName").ValueKind);   // no owner is allowed (Q-0841)
        Assert.False(list[0].GetProperty("done").GetBoolean());

        var r = await Send(c.Rm, HttpMethod.Patch, $"/api/v1/milestones/{id}", new { name = "Code complete (all repos)", dueOn = "2026-10-19", clearOwner = true, note = "" }, 1);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var p = await Json(r);
        Assert.Equal(2, p.GetProperty("version").GetInt32());
        Assert.Equal("2026-10-19", p.GetProperty("dueOn").GetString());
        Assert.Equal(JsonValueKind.Null, p.GetProperty("ownerUserId").ValueKind);
        Assert.Equal(JsonValueKind.Null, p.GetProperty("note").ValueKind);
        Assert.Equal(UserId(c.F, "rm@x.com"), Scalar(c.F, $"SELECT LastChangedByUserId FROM TrainMilestones WHERE Id='{id}'"));

        // an edit that changes nothing is not a write
        Assert.Equal(2, (await Json(await Send(c.Rte, HttpMethod.Patch, $"/api/v1/milestones/{id}", new { name = "Code complete (all repos)" }, 2))).GetProperty("version").GetInt32());

        var d = await Json(await Send(c.Rte, HttpMethod.Post, $"/api/v1/milestones/{id}:done", null, 2));
        Assert.True(d.GetProperty("isDone").GetBoolean());
        Assert.Equal(3, d.GetProperty("version").GetInt32());
        Assert.Equal(c.RteId, d.GetProperty("doneByUserId").GetString());
        Assert.NotEqual(JsonValueKind.Null, d.GetProperty("doneAt").ValueKind);
        // done again: a no-op, not a second audit row
        Assert.Equal(3, (await Json(await Send(c.Rm, HttpMethod.Post, $"/api/v1/milestones/{id}:done", null, 3))).GetProperty("version").GetInt32());
        var listed = (await Json(await c.Rte.GetAsync("/api/v1/trains/t1/milestones"))).EnumerateArray().Single(x => x.GetProperty("id").GetString() == id);
        Assert.True(listed.GetProperty("done").GetBoolean());
        Assert.Equal("rte", listed.GetProperty("doneBy").GetString());

        var u = await Json(await Send(c.Rte, HttpMethod.Post, $"/api/v1/milestones/{id}:undone", null, 3));
        Assert.False(u.GetProperty("isDone").GetBoolean());
        Assert.Equal(JsonValueKind.Null, u.GetProperty("doneAt").ValueKind);
        Assert.Equal(4, u.GetProperty("version").GetInt32());

        Assert.Equal(HttpStatusCode.OK, (await Send(c.Rte, HttpMethod.Delete, $"/api/v1/milestones/{id}", null, 4)).StatusCode);
        Assert.Equal("0", Scalar(c.F, $"SELECT count(*) FROM TrainMilestones WHERE Id='{id}'"));
        var rmId = UserId(c.F, "rm@x.com");
        Assert.Equal($"Add:{c.RteId}:t1,Update:{rmId}:t1,Done:{c.RteId}:t1,Undone:{c.RteId}:t1,Delete:{c.RteId}:t1", Audit(c.F, id));
        Assert.Contains("Code complete (all repos)", Scalar(c.F, $"SELECT BeforeJson FROM AuditEvents WHERE EntityType='Milestone' AND EntityId='{id}' AND Action='Delete'"));   // what was removed
        Assert.Equal(HttpStatusCode.NotFound, (await Send(c.Rte, HttpMethod.Delete, $"/api/v1/milestones/{id}", null, 4)).StatusCode);
    }

    [Fact]
    public async Task A_stale_If_Match_answers_409_with_the_current_row_and_changes_nothing()
    {
        var c = await Setup(); using var _ = c.F;
        var id = (await Add(c, new { name = "UAT sign-off", dueOn = "2026-10-21" })).GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await Send(c.Rte, HttpMethod.Patch, $"/api/v1/milestones/{id}", new { note = "moved" }, 1)).StatusCode);
        foreach (var (m, url, body) in new (HttpMethod, string, object?)[]
                 {
                     (HttpMethod.Patch, $"/api/v1/milestones/{id}", new { name = "Mine" }), (HttpMethod.Post, $"/api/v1/milestones/{id}:done", null),
                     (HttpMethod.Post, $"/api/v1/milestones/{id}:undone", null), (HttpMethod.Delete, $"/api/v1/milestones/{id}", null),
                 })
        {
            var r = await Send(c.Rm, m, url, body, 1);
            Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
            var cur = (await Json(r)).GetProperty("current");
            Assert.Equal(2, cur.GetProperty("version").GetInt32());
            Assert.Equal("moved", cur.GetProperty("note").GetString());
        }
        Assert.Equal("2", Scalar(c.F, $"SELECT Version FROM TrainMilestones WHERE Id='{id}'"));
        Assert.Equal("UAT sign-off", Scalar(c.F, $"SELECT Name FROM TrainMilestones WHERE Id='{id}'"));
        Assert.Equal("2", Scalar(c.F, $"SELECT count(*) FROM AuditEvents WHERE EntityType='Milestone' AND EntityId='{id}'"));
    }

    [Fact]
    public async Task Without_If_Match_a_versioned_change_is_refused_with_428()
    {
        var c = await Setup(requireIfMatch: true); using var _ = c.F;
        var id = (await Add(c, new { name = "UAT sign-off", dueOn = "2026-10-21" })).GetProperty("id").GetString()!;   // create needs none
        Assert.Equal((HttpStatusCode)428, (await Send(c.Rte, HttpMethod.Post, $"/api/v1/milestones/{id}:done")).StatusCode);
        Assert.Equal((HttpStatusCode)428, (await Send(c.Rte, HttpMethod.Delete, $"/api/v1/milestones/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(c.Rte, HttpMethod.Post, $"/api/v1/milestones/{id}:done", null, 1)).StatusCode);
    }

    [Fact]
    public async Task Bad_input_is_422_InvalidMilestone_before_the_database_sees_it()
    {
        var c = await Setup(); using var _ = c.F;
        Sql(c.F, $"INSERT INTO Teams(Id,Handle,Name) VALUES('tm1','ops','Ops'); INSERT INTO Users(Id,Email,DisplayName,Role,IsActive) VALUES('gone','gone@x.com','Gone','RTE',0);");
        foreach (var body in new object[]
                 {
                     new { name = "  ", dueOn = "2026-10-21" }, new { name = new string('x', 201), dueOn = "2026-10-21" }, new { name = "UAT" },
                     new { name = "UAT", dueOn = "2026-10-21", ownerUserId = c.RteId, ownerTeamId = "tm1" }, new { name = "UAT", dueOn = "2026-10-21", ownerUserId = "gone" },
                     new { name = "UAT", dueOn = "2026-10-21", ownerTeamId = "nope" }, new { name = "UAT", dueOn = "2026-10-21", note = new string('n', 2001) },
                 })
        {
            var r = await Send(c.Rte, HttpMethod.Post, "/api/v1/trains/t1/milestones", body);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
            Assert.Equal("InvalidMilestone", (await Json(r)).GetProperty("guard").GetString());
        }
        Assert.Equal(HttpStatusCode.OK, (await Send(c.Rte, HttpMethod.Post, "/api/v1/trains/t1/milestones", new { name = new string('x', 200), dueOn = "2026-10-21", ownerTeamId = "tm1" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(c.Rte, HttpMethod.Post, "/api/v1/trains/nope/milestones", new { name = "UAT", dueOn = "2026-10-21" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.Rte.GetAsync("/api/v1/trains/nope/milestones")).StatusCode);
        Assert.Equal("0", Scalar(c.F, "SELECT count(*) FROM AuditEvents WHERE EntityType='Milestone' AND Action<>'Add'"));
    }

    [Fact]
    public async Task Viewers_are_read_only_and_owners_who_are_not_planners_may_only_mark_done_or_undone()
    {
        var c = await Setup(); using var _ = c.F;
        Sql(c.F, $"INSERT INTO Teams(Id,Handle,Name) VALUES('tm1','gov','Governance'); INSERT INTO TeamMembers(TeamId,UserId) VALUES('tm1','{c.GovId}');");
        var mine = (await Add(c, new { name = "Viewer's milestone", dueOn = "2026-10-21", ownerUserId = c.ViewerId })).GetProperty("id").GetString()!;
        var team = (await Add(c, new { name = "Governance review", dueOn = "2026-10-22", ownerTeamId = "tm1" })).GetProperty("id").GetString()!;
        var other = (await Add(c, new { name = "Someone else's", dueOn = "2026-10-23", ownerUserId = c.RteId })).GetProperty("id").GetString()!;

        // Viewer: 403 on every write, even on the milestone they own; reads are fine
        Assert.Equal(HttpStatusCode.OK, (await c.Viewer.GetAsync("/api/v1/trains/t1/milestones")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(c.Viewer, HttpMethod.Post, "/api/v1/trains/t1/milestones", new { name = "x", dueOn = "2026-10-21" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(c.Viewer, HttpMethod.Patch, $"/api/v1/milestones/{mine}", new { name = "x" }, 1)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(c.Viewer, HttpMethod.Post, $"/api/v1/milestones/{mine}:done", null, 1)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(c.Viewer, HttpMethod.Post, $"/api/v1/milestones/{mine}:undone", null, 1)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(c.Viewer, HttpMethod.Delete, $"/api/v1/milestones/{mine}", null, 1)).StatusCode);

        // Governance Officer: marks done a milestone their team owns, not someone else's; never edits or removes
        Assert.Equal(HttpStatusCode.OK, (await Send(c.Gov, HttpMethod.Post, $"/api/v1/milestones/{team}:done", null, 1)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(c.Gov, HttpMethod.Post, $"/api/v1/milestones/{team}:undone", null, 2)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(c.Gov, HttpMethod.Post, $"/api/v1/milestones/{other}:done", null, 1)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(c.Gov, HttpMethod.Patch, $"/api/v1/milestones/{team}", new { name = "x" }, 3)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(c.Gov, HttpMethod.Delete, $"/api/v1/milestones/{team}", null, 3)).StatusCode);

        // planners act on any milestone
        Assert.Equal(HttpStatusCode.OK, (await Send(c.Rm, HttpMethod.Post, $"/api/v1/milestones/{mine}:done", null, 1)).StatusCode);
        Assert.Equal("0", Scalar(c.F, $"SELECT count(*) FROM AuditEvents WHERE EntityType='Milestone' AND ActorUserId='{c.ViewerId}'"));
    }

    [Fact]
    public async Task A_milestone_never_blocks_readiness_or_a_train_move()
    {
        var c = await Setup(); using var _ = c.F;
        var before = (await Json(await c.Rte.GetAsync("/api/v1/trains/t1/readiness?target=Gated"))).GetProperty("blockers").GetRawText();
        await Add(c, new { name = "Overdue and open", dueOn = "2026-01-05" });
        var after = await Json(await c.Rte.GetAsync("/api/v1/trains/t1/readiness?target=Gated"));
        Assert.Equal(before, after.GetProperty("blockers").GetRawText());
        Assert.DoesNotContain("Overdue and open", after.GetRawText());
        // With the gates that guard Gated resolved, the train advances although the milestone is open and overdue.
        var rteId = c.RteId;
        Sql(c.F, $"UPDATE ChecklistTasks SET IsCompleted=1,CompletedAt='2026-10-01T00:00:00Z',CompletedByUserId='{rteId}' WHERE Id='k1'; " +
                 $"UPDATE StageGates SET Status='InProgress',LastChangedByUserId='{rteId}' WHERE Id='g1'; " +
                 $"UPDATE StageGates SET Status='Certified',CertifiedByUserId='{rteId}',CertifiedAt='2026-10-01T00:00:00Z',LastChangedByUserId='{rteId}' WHERE Id='g1';");
        var r = await Send(c.Rte, HttpMethod.Post, "/api/v1/trains/t1:advance", new { to = "Gated" }, 1);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Calendar_and_ICS_feeds_carry_milestones_with_stable_UIDs()
    {
        var c = await Setup(); using var _ = c.F;
        var a = (await Add(c, new { name = "Code complete", dueOn = "2026-10-16", ownerUserId = c.RteId })).GetProperty("id").GetString()!;
        var b = (await Add(c, new { name = "UAT sign-off", dueOn = "2026-11-03" })).GetProperty("id").GetString()!;

        var oct = await Json(await c.Viewer.GetAsync("/api/v1/calendar?from=2026-10-01&to=2026-10-31"));
        var cm = Assert.Single(oct.GetProperty("milestones").EnumerateArray());
        Assert.Equal(a, cm.GetProperty("id").GetString());
        Assert.Equal("Code complete", cm.GetProperty("name").GetString());
        Assert.Equal("2026-10-16", cm.GetProperty("dueOn").GetString());
        Assert.False(cm.GetProperty("done").GetBoolean());
        Assert.Equal("t1", cm.GetProperty("trainId").GetString());
        Assert.Equal("R26.10", cm.GetProperty("trainTitle").GetString());
        Assert.Single((await Json(await c.Viewer.GetAsync("/api/v1/calendar?from=2026-11-01&to=2026-11-30"))).GetProperty("milestones").EnumerateArray());

        var tok = (await Json(await c.Rte.PostAsJsonAsync("/api/v1/me/ics-tokens", new { scope = "all" }))).GetProperty("token").GetString()!;
        async Task<string> Feed(string path) { using var anon = c.F.CreateClient(); var r = await anon.GetAsync(path); Assert.Equal(HttpStatusCode.OK, r.StatusCode); return await r.Content.ReadAsStringAsync(); }
        var all = await Feed($"/api/v1/ics/{tok}.ics");
        var ev = IcsCalendar.Load(all)!.Events.ToDictionary(e => e.Uid!);
        var e1 = ev[$"{a}-milestone@releasemgmt"];
        Assert.Equal("R26.10: Code complete", e1.Summary);
        Assert.False(e1.DtStart!.HasTime);
        Assert.Equal(new DateTime(2026, 10, 16), e1.DtStart.Value.Date);
        Assert.Equal(new DateTime(2026, 10, 17), e1.DtEnd!.Value.Date);
        Assert.Contains($"{b}-milestone@releasemgmt", ev.Keys);
        Assert.Contains($"{a}-milestone@releasemgmt", await Feed($"/api/v1/ics/{tok}/trains/t1.ics"));
        Assert.Equal(all, await Feed($"/api/v1/ics/{tok}.ics"));   // unchanged database, byte-identical file

        var mine = await Feed($"/api/v1/ics/{tok}/mine.ics");   // the RTE owns a, nobody owns b
        Assert.Contains($"{a}-milestone@releasemgmt", mine);
        Assert.DoesNotContain($"{b}-milestone@releasemgmt", mine);

        // moved and marked done: same UID, higher SEQUENCE, title says done; done milestones leave "my work"
        Assert.Equal(HttpStatusCode.OK, (await Send(c.Rte, HttpMethod.Patch, $"/api/v1/milestones/{a}", new { dueOn = "2026-10-19" }, 1)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(c.Rte, HttpMethod.Post, $"/api/v1/milestones/{a}:done", null, 2)).StatusCode);
        var moved = IcsCalendar.Load(await Feed($"/api/v1/ics/{tok}.ics"))!.Events.Single(e => e.Uid == $"{a}-milestone@releasemgmt");
        Assert.Equal(new DateTime(2026, 10, 19), moved.DtStart!.Value.Date);
        Assert.True(moved.Sequence > e1.Sequence);
        Assert.Equal("R26.10: Code complete (done)", moved.Summary);
        Assert.DoesNotContain($"{a}-milestone@releasemgmt", await Feed($"/api/v1/ics/{tok}/mine.ics"));
        Assert.True((await Json(await c.Viewer.GetAsync("/api/v1/calendar?from=2026-10-01&to=2026-10-31"))).GetProperty("milestones")[0].GetProperty("done").GetBoolean());
    }

    [Fact]
    public async Task The_milestones_grid_exports_as_CSV_with_owner_email_or_team_handle()
    {
        var c = await Setup(); using var _ = c.F;
        Sql(c.F, "INSERT INTO Teams(Id,Handle,Name) VALUES('tm1','qa','QA')");
        await Add(c, new { name = "=Code complete", dueOn = "2026-10-16", ownerUserId = c.RteId });
        await Add(c, new { name = "UAT sign-off", dueOn = "2026-10-21", ownerTeamId = "tm1", note = "with business" });
        var csv = Encoding.UTF8.GetString(await c.Viewer.GetByteArrayAsync("/api/v1/exports/milestones.csv?train=t1")).TrimStart('﻿');
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Train,Name,DueOn,Owner,Note,Done,DoneAt,DoneBy,#Id,#Version", lines[0]);
        Assert.StartsWith("R26.10,'=Code complete,2026-10-16,rte@x.com,,No,,,", lines[1]);   // CSV injection escaped
        Assert.StartsWith("R26.10,UAT sign-off,2026-10-21,'@qa,with business,No,,,", lines[2]);   // a team handle is escaped like any cell starting with @
        Assert.Equal(3, lines.Length);
    }
}
