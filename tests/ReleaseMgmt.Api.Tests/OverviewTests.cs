using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using ReleaseMgmt.Domain.Common;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>REOS-108 (B2): GET /overview. Every number is checked against the list it summarises (Q-108a).</summary>
public class OverviewTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
    }

    private sealed record World(ApiFactory Root, WebApplicationFactory<Program> F, FakeTimeProvider Clock, HttpClient Rte, HttpClient Gov, HttpClient Viewer) : IDisposable
    {
        public void Dispose() { F.Dispose(); Root.Dispose(); }
    }

    private static async Task<HttpClient> Login(WebApplicationFactory<Program> f, string role, string email)
    {
        var c = f.CreateClient();
        (await c.PostAsJsonAsync("/auth/dev-login", new { email, name = email.Split('@')[0], role })).EnsureSuccessStatusCode();
        return c;
    }

    /// <summary>
    /// The clock is 2026-10-23 12:00Z (Display:TimeZone America/Chicago, CDT = UTC−5). Open trains: p1 Planning (a Compliance gate ready to certify, a failed
    /// gate), g1 Gated (no Go/No-Go, a waiver to approve, window colliding with g2 on CI ledger-db), g2 Gated (Go with an open condition; an open condition of its earlier decision no longer counts), e1 Executing (live run,
    /// window overlapping the Search* freeze, an override requested). Closed: c1–c3 completed in the last 14 days, c4 before that, a1 aborted; x1 archived.
    /// </summary>
    private static async Task<World> Setup()
    {
        var root = new ApiFactory();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-23T12:00:00Z"));
        var f = root.WithWebHostBuilder(b => b.ConfigureServices(s => { s.RemoveAll<TimeProvider>(); s.AddSingleton<TimeProvider>(clock); }));
        var rte = await Login(f, Roles.RTE, "rte@ov.test");
        var gov = await Login(f, Roles.GovernanceOfficer, "gov@ov.test");
        var rm = await Login(f, Roles.ReleaseManager, "rm@ov.test");
        var viewer = await Login(f, Roles.Viewer, "view@ov.test");
        string rteId = UserId(root, "rte@ov.test"), govId = UserId(root, "gov@ov.test"), rmId = UserId(root, "rm@ov.test");
        const string c = "'2026-09-01T00:00:00Z'";
        Sql(root, $@"
            INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CurrentStatus,CloseCode,ActualEndAt,ArchivedAt,CreatedAt,UpdatedAt) VALUES
              ('p1','R26.30 Payments','2026-11-06','Low','Planning',NULL,NULL,NULL,{c},{c}),
              ('g1','R26.31 Ledger','2026-10-26','Low','Gated',NULL,NULL,NULL,{c},{c}),
              ('g2','R26.32 Cards','2026-10-26','Low','Gated',NULL,NULL,NULL,{c},{c}),
              ('e1','R26.29 Search','2026-10-23','Low','Executing',NULL,NULL,NULL,{c},{c}),
              ('c1','R26.20 One','2026-10-20','Low','Complete','Successful','2026-10-20T15:00:00Z',NULL,{c},{c}),
              ('c2','R26.21 Two','2026-10-20','Low','Complete','SuccessfulWithIssues','2026-10-20T16:00:00Z',NULL,{c},{c}),
              ('c3','R26.22 Three','2026-10-22','Low','Complete','Unsuccessful','2026-10-22T09:00:00Z',NULL,{c},{c}),
              ('c4','R26.10 Older','2026-10-05','Low','Complete','Successful','2026-10-05T09:00:00Z',NULL,{c},{c}),
              ('a1','R26.23 Dropped','2026-10-21','Low','Aborted',NULL,NULL,NULL,{c},'2026-10-21T08:00:00Z'),
              ('x1','R26.01 Archived','2026-10-24','Low','Planning',NULL,NULL,'2026-10-01T00:00:00Z',{c},{c});
            INSERT INTO StageGates(Id,ReleaseTrainId,GateName,GateClass,SequenceOrder,OffsetDays,DueOn,RequiredBeforeStatus,OwnerUserId,Status) VALUES
              ('gp1','p1','Compliance Sign-off','Compliance',1,8,'2026-10-26','Gated','{govId}','InProgress'),
              ('gp2','p1','Security Review','Standard',2,6,'2026-10-28','Gated','{rteId}','Failed'),
              ('gg1','g1','Ops Readiness','Standard',1,1,'2026-10-23','Executing','{rteId}','InProgress');
            INSERT INTO ChecklistTasks(Id,StageGateId,TaskDescription,OwnerUserId,IsCompleted,SequenceOrder,CompletedAt,CompletedByUserId) VALUES
              ('k1','gp1','Evidence pack filed','{rteId}',1,1,'2026-10-22T10:00:00Z','{rteId}'),
              ('k2','gg1','Runbook reviewed','{rteId}',0,1,NULL,NULL);
            INSERT INTO GateWaivers(Id,StageGateId,Reason,RequestedByUserId,RequestedAt) VALUES('wv1','gg1','The ops review happens in the CAB on Monday','{rteId}','2026-10-22T10:00:00Z');
            INSERT INTO GoNoGoDecisions(Id,ReleaseTrainId,Decision,DecidedByUserId,DecidedAt,GateSnapshotJson) VALUES
              ('d1','g2','GoWithConditions','{rmId}','2026-10-21T10:00:00Z','{{}}'),('d2','g2','GoWithConditions','{rmId}','2026-10-22T10:00:00Z','{{}}');
            INSERT INTO GoNoGoConditions(Id,DecisionId,Text,OwnerUserId,ExpiresAt) VALUES
              ('cd0','d1','Replaced by the later decision','{rteId}','2026-10-24T12:00:00Z'),('cd1','d2','Load test signed off','{rteId}','2026-10-24T18:00:00Z');
            INSERT INTO DeploymentWindows(Id,ReleaseTrainId,StartsAt,EndsAt) VALUES
              ('w1','g1','2026-10-25T06:00:00Z','2026-10-25T10:00:00Z'),('w2','g2','2026-10-25T09:30:00Z','2026-10-25T11:00:00Z'),
              ('w3','e1','2026-10-23T10:00:00Z','2026-10-23T14:00:00Z'),('w4','c1','2026-10-20T13:00:00Z','2026-10-20T15:00:00Z');
            INSERT INTO AffectedCIs(Id,ReleaseTrainId,CiName) VALUES('ci1','g1','ledger-db'),('ci2','g2','Ledger-DB');
            INSERT INTO BundledProducts(Id,ReleaseTrainId,ProductName,VersionTag,ProjectCode) VALUES
              ('bp1','e1','Search API','4.0','SRCH'),('bp2','p1','Payments','2.1','PAY'),('bp3','c1','Cards','1.0','CRD');
            INSERT INTO FreezeWindows(Id,Name,Kind,StartsAt,EndsAt,ProductPattern,CreatedByUserId) VALUES
              ('fz1','Search freeze','Freeze','2026-10-23T13:00:00Z','2026-10-30T00:00:00Z','Search*','{govId}'),
              ('fz2','Quiet week','Chill','2026-10-23T00:00:00Z','2026-10-30T00:00:00Z',NULL,'{govId}');
            INSERT INTO RunbookRuns(Id,ReleaseTrainId,Mode,StartedAt,StartedByUserId) VALUES('r1','e1','Live','2026-10-23T10:00:00Z','{rteId}');");
        (await rte.PostAsJsonAsync("/api/v1/freezes/fz1/override-requests", new { trainId = "e1", reason = "The search index rebuild must finish in this window" })).EnsureSuccessStatusCode();
        return new World(root, f, clock, rte, gov, viewer);
    }

    private static string[] Kinds(JsonElement o, string? category = null) =>
        [.. o.GetProperty("attention").EnumerateArray().Where(a => category is null || a.GetProperty("category").GetString() == category).Select(a => a.GetProperty("kind").GetString()!)];

    [Fact]
    public async Task The_cards_match_the_lists_they_count()
    {
        using var w = await Setup();
        var o = await Json(await w.Rte.GetAsync("/api/v1/overview"));
        Assert.Equal(("2026-10-10", "2026-10-23", "2026-10-23T12:00:00Z"), (o.GetProperty("from").GetString(), o.GetProperty("to").GetString(), o.GetProperty("asOf").GetString()));
        var cards = o.GetProperty("cards");

        // Active releases = open trains = the activity rows whose status is Planning, Gated or Executing
        var activity = o.GetProperty("activity").EnumerateArray().ToList();
        var open = activity.Where(r => r.GetProperty("status").GetString() is "Planning" or "Gated" or "Executing").Select(r => r.GetProperty("trainId").GetString()).ToList();
        var active = cards.GetProperty("activeReleases");
        Assert.Equal(["e1", "g1", "g2", "p1"], open);   // soonest window or target first; x1 is archived
        Assert.Equal((4, 1, 2, 1), (active.GetProperty("value").GetInt32(), active.GetProperty("planning").GetInt32(), active.GetProperty("gated").GetInt32(), active.GetProperty("executing").GetInt32()));
        Assert.Equal("Across 0 teams · 2 products", active.GetProperty("detail").GetString());

        var running = cards.GetProperty("runningNow");
        Assert.Equal((1, 1, "1 with a live run in progress"), (running.GetProperty("value").GetInt32(), running.GetProperty("withLiveRun").GetInt32(), running.GetProperty("detail").GetString()));
        Assert.Equal(running.GetProperty("value").GetInt32(), activity.Count(r => r.GetProperty("displayStatus").GetString() == "Running"));

        // Need attention = the Stream's blockers on open trains + the Approval items
        var stream = (await Json(await w.Rte.GetAsync("/api/v1/trains"))).EnumerateArray().ToList();
        var blockers = stream.Where(s => open.Contains(s.GetProperty("id").GetString())).Sum(s => s.GetProperty("blockers").GetInt32());
        Assert.Equal(stream.Sum(s => s.GetProperty("blockers").GetInt32()), blockers);   // closed trains have none
        var approvals = Kinds(o, "Approval");
        var need = cards.GetProperty("needAttention");
        Assert.Equal((blockers, approvals.Length, blockers + approvals.Length), (need.GetProperty("readinessBlockers").GetInt32(), need.GetProperty("approvals").GetInt32(), need.GetProperty("value").GetInt32()));
        Assert.Equal($"{blockers} readiness blockers · 5 approvals", need.GetProperty("detail").GetString());

        // Deployment success = 100 − analytics M10's change-fail %, over the same range
        var success = cards.GetProperty("deploymentSuccess");
        var m10 = (await Json(await w.Rte.GetAsync("/api/v1/analytics/M10?from=2026-10-10&to=2026-10-23"))).GetProperty("rows")[0];
        Assert.Equal(m10.GetProperty("completed").GetInt32(), success.GetProperty("completed").GetInt32());
        Assert.Equal(100 - m10.GetProperty("changeFailPct").GetDouble(), success.GetProperty("value").GetDouble(), 1);
        Assert.Equal((3, 1, 1, 1, 33.3, 100.0, -66.7), (success.GetProperty("completed").GetInt32(), success.GetProperty("successful").GetInt32(), success.GetProperty("withIssues").GetInt32(),
            success.GetProperty("unsuccessful").GetInt32(), success.GetProperty("value").GetDouble(), success.GetProperty("previousValue").GetDouble(), success.GetProperty("deltaPts").GetDouble()));
        Assert.Equal("↓ 66.7 pts vs. previous 14 days", success.GetProperty("detail").GetString());

        // Daily completions: every day of the range, summing to the completed count
        var days = o.GetProperty("throughput").EnumerateArray().ToList();
        Assert.Equal(14, days.Count);
        Assert.Equal(("2026-10-10", "2026-10-23"), (days[0].GetProperty("date").GetString(), days[^1].GetProperty("date").GetString()));
        Assert.Equal(success.GetProperty("successful").GetInt32(), days.Sum(d => d.GetProperty("successful").GetInt32()));
        Assert.Equal(success.GetProperty("withIssues").GetInt32(), days.Sum(d => d.GetProperty("withIssues").GetInt32()));
        Assert.Equal(success.GetProperty("unsuccessful").GetInt32(), days.Sum(d => d.GetProperty("unsuccessful").GetInt32()));
        var d20 = days.Single(d => d.GetProperty("date").GetString() == "2026-10-20");
        Assert.Equal((1, 1, 0), (d20.GetProperty("successful").GetInt32(), d20.GetProperty("withIssues").GetInt32(), d20.GetProperty("unsuccessful").GetInt32()));
    }

    [Fact]
    public async Task Attention_items_name_the_release_and_the_due_time_soonest_first()
    {
        using var w = await Setup();
        var o = await Json(await w.Viewer.GetAsync("/api/v1/overview"));   // any signed-in role may read
        var items = o.GetProperty("attention").EnumerateArray().Select(a => (Kind: a.GetProperty("kind").GetString(), Train: a.GetProperty("trainId").GetString(),
            Due: a.GetProperty("dueAt").GetString(), Detail: a.GetProperty("detail").GetString())).ToList();
        Assert.Equal([
            ("FreezeConflict", "e1", "2026-10-23T13:00:00Z", "Window overlaps Search freeze with no override"),
            ("GrantOverride", "e1", "2026-10-23T13:00:00Z", "rte asks to override Search freeze"),
            ("ApproveWaiver", "g1", "2026-10-24T05:00:00Z", "Ops Readiness · requested by rte"),          // end of 23 Oct in America/Chicago
            ("CloseCondition", "g2", "2026-10-24T18:00:00Z", "Load test signed off"),
            ("RecordGoNoGo", "g1", "2026-10-25T06:00:00Z", "No decision recorded yet"),                    // when its window opens
            ("WindowCollision", "g1", "2026-10-25T06:00:00Z", "Overlaps R26.32 Cards · CI ledger-db"),     // once per pair
            ("CertifyGate", "p1", "2026-10-27T05:00:00Z", "Compliance Sign-off"),
            ("FailedGate", "p1", "2026-10-29T05:00:00Z", "Security Review failed"),
        ], items.Select(i => (i.Kind!, i.Train!, i.Due!, i.Detail!)).ToList());
        var grant = o.GetProperty("attention").EnumerateArray().Single(a => a.GetProperty("kind").GetString() == "GrantOverride");
        Assert.Equal($"fz1:e1:{UserId(w.Root, "rte@ov.test")}", grant.GetProperty("entityId").GetString());   // the id shape GET /approvals uses
        // g2 has a Go (with conditions); the Chill and p1 (no window) raise no conflict
        Assert.Equal("Blocked", o.GetProperty("activity").EnumerateArray().Single(r => r.GetProperty("trainId").GetString() == "p1").GetProperty("displayStatus").GetString());
    }

    [Fact]
    public async Task An_item_leaves_the_overview_once_it_is_decided()
    {
        using var w = await Setup();
        (await w.Gov.PostAsync("/api/v1/waivers/wv1:approve", null)).EnsureSuccessStatusCode();
        (await w.Gov.PostAsJsonAsync("/api/v1/freezes/fz1/overrides", new { trainId = "e1", requestedByUserId = UserId(w.Root, "rte@ov.test"), reason = "The search index rebuild must finish in this window", expiresAt = "2026-10-23T15:00:00Z" })).EnsureSuccessStatusCode();
        var o = await Json(await w.Rte.GetAsync("/api/v1/overview"));
        var kinds = Kinds(o);
        Assert.DoesNotContain("ApproveWaiver", kinds);
        Assert.DoesNotContain("GrantOverride", kinds);   // the grant consumed the request
        Assert.DoesNotContain("FreezeConflict", kinds);          // and the override lasts past the overlap
        Assert.Equal(3, o.GetProperty("cards").GetProperty("needAttention").GetProperty("approvals").GetInt32());
    }

    [Fact]
    public async Task The_range_is_validated_and_shapes_the_completions_and_the_closed_activity()
    {
        using var w = await Setup();
        foreach (var q in new[] { "from=2026-10-23&to=2026-10-01", "from=10/01/2026", "to=nope", "from=2025-01-01&to=2026-10-23" })
        {
            var r = await w.Rte.GetAsync($"/api/v1/overview?{q}");
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Equal("InvalidFilter", (await Json400(r)).GetProperty("guard").GetString());
        }
        var o = await Json(await w.Rte.GetAsync("/api/v1/overview?from=2026-10-01&to=2026-10-31"));
        Assert.Equal(31, o.GetProperty("throughput").GetArrayLength());
        var success = o.GetProperty("cards").GetProperty("deploymentSuccess");
        Assert.Equal((4, 50.0), (success.GetProperty("completed").GetInt32(), success.GetProperty("value").GetDouble()));
        var closed = o.GetProperty("activity").EnumerateArray().Where(r => r.GetProperty("status").GetString() is "Complete" or "Aborted")
            .Select(r => (r.GetProperty("trainId").GetString()!, r.GetProperty("displayStatus").GetString()!)).ToList();
        Assert.Equal([("c3", "Failed"), ("a1", "Failed"), ("c2", "Successful"), ("c1", "Successful"), ("c4", "Successful")], closed);   // latest end first

        var empty = await Json(await w.Rte.GetAsync("/api/v1/overview?from=2026-01-01&to=2026-01-31"));
        var none = empty.GetProperty("cards").GetProperty("deploymentSuccess");
        Assert.Equal((JsonValueKind.Null, "No releases completed in the range"), (none.GetProperty("value").ValueKind, none.GetProperty("detail").GetString()));
        Assert.Equal(4, empty.GetProperty("cards").GetProperty("activeReleases").GetProperty("value").GetInt32());   // cards other than success are current state
    }

    private static async Task<JsonElement> Json400(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
}
