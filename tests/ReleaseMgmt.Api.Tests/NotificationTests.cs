using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using ReleaseMgmt.Api.Reminders;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Reminders;
using ReleaseMgmt.Infrastructure.Services;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>REOS-37: inbox and My work endpoints (own rows only, 403/404, read state, audited) and the team webhook sender (SSRF-safe, bounded, failure-visible).</summary>
public class NotificationTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private static void Note(ApiFactory f, string id, string user, string kind, string createdAt, bool read = false, string entityType = "StageGate", string entityId = "g1", int level = 0) =>
        Sql(f, $"INSERT INTO Notifications(Id,UserId,Kind,EntityType,EntityId,EscalationLevel,Message,CreatedAt,ReadAt) VALUES('{id}','{user}','{kind}','{entityType}','{entityId}',{level},'msg {id}','{createdAt}',{(read ? "'2026-10-01T00:00:00Z'" : "NULL")})");

    private static async Task<(ApiFactory F, HttpClient Me, HttpClient Other, string MeId, string OtherId)> Setup()
    {
        var f = new ApiFactory();
        var me = await As(f, Roles.RTE, "me@x.com");
        var other = await As(f, Roles.Viewer, "other@x.com");
        return (f, me, other, UserId(f, "me@x.com"), UserId(f, "other@x.com"));
    }

    // ---- inbox ------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task The_inbox_lists_only_my_notifications_newest_first_with_paging_and_an_unread_filter()
    {
        var (f, me, other, meId, otherId) = await Setup(); using var _ = f;
        SeedTrain(f, meId, meId);
        Note(f, "n1", meId, "GateEntered", "2026-10-01T10:00:00Z", read: true);
        Note(f, "n2", meId, "GateReminder", "2026-10-01T11:00:00Z");
        Note(f, "n3", meId, "GateOverdue", "2026-10-01T12:00:00Z", level: 1);
        Note(f, "x1", otherId, "GateEntered", "2026-10-01T13:00:00Z");

        var all = await Json(await me.GetAsync("/api/v1/me/notifications"));
        Assert.Equal(["n3", "n2", "n1"], all.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!).ToArray());
        Assert.Equal(3, all.GetProperty("total").GetInt32());
        Assert.Equal(2, all.GetProperty("unread").GetInt32());
        var first = all.GetProperty("items")[0];
        Assert.Equal("GateOverdue", first.GetProperty("kind").GetString());
        Assert.Equal(1, first.GetProperty("escalationLevel").GetInt32());
        Assert.Equal("t1", first.GetProperty("trainId").GetString());          // entity resolved to its train for the deep link
        Assert.Equal("R26.10", first.GetProperty("trainTitle").GetString());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("readAt").ValueKind);

        var unread = await Json(await me.GetAsync("/api/v1/me/notifications?unread=true"));
        Assert.Equal(["n3", "n2"], unread.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!).ToArray());
        Assert.Equal(2, unread.GetProperty("total").GetInt32());

        var page2 = await Json(await me.GetAsync("/api/v1/me/notifications?limit=2&offset=2"));
        Assert.Equal(["n1"], page2.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!).ToArray());
        Assert.Equal(3, page2.GetProperty("total").GetInt32());
        Assert.Equal(2, page2.GetProperty("limit").GetInt32());

        var theirs = await Json(await other.GetAsync("/api/v1/me/notifications"));
        Assert.Equal(["x1"], theirs.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!).ToArray());
    }

    [Fact]
    public async Task Paging_is_clamped_and_a_notification_with_no_train_still_lists()
    {
        var (f, me, _, meId, _) = await Setup(); using var _f = f;
        Note(f, "n1", meId, "SyncAlert", "2026-10-01T10:00:00Z", entityType: "SyncAlert", entityId: "nope");
        var page = await Json(await me.GetAsync("/api/v1/me/notifications?limit=100000&offset=-5"));
        Assert.Equal(100, page.GetProperty("limit").GetInt32());
        Assert.Equal(0, page.GetProperty("offset").GetInt32());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("items")[0].GetProperty("trainId").ValueKind);
    }

    [Fact]
    public async Task The_count_is_mine_and_falls_as_I_read()
    {
        var (f, me, other, meId, otherId) = await Setup(); using var _ = f;
        Note(f, "n1", meId, "GateEntered", "2026-10-01T10:00:00Z");
        Note(f, "n2", meId, "GateEntered", "2026-10-01T11:00:00Z", read: true);
        Note(f, "x1", otherId, "GateEntered", "2026-10-01T12:00:00Z");
        var c = await Json(await me.GetAsync("/api/v1/me/notifications/count"));
        Assert.Equal((1, 2), (c.GetProperty("unread").GetInt32(), c.GetProperty("total").GetInt32()));
        Assert.Equal(HttpStatusCode.OK, (await me.PostAsync("/api/v1/notifications/n1:read", null)).StatusCode);
        Assert.Equal(0, (await Json(await me.GetAsync("/api/v1/me/notifications/count"))).GetProperty("unread").GetInt32());
        Assert.Equal(1, (await Json(await other.GetAsync("/api/v1/me/notifications/count"))).GetProperty("unread").GetInt32());
    }

    [Fact]
    public async Task Marking_read_stamps_readAt_and_Version_audits_once_and_is_idempotent()
    {
        var (f, me, _, meId, _) = await Setup(); using var _f = f;
        Note(f, "n1", meId, "GateEntered", "2026-10-01T10:00:00Z");

        var r = await me.PostAsync("/api/v1/notifications/n1:read", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await Json(r);
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("readAt").ValueKind);
        Assert.Equal(2, body.GetProperty("version").GetInt32());
        Assert.NotEqual("", Scalar(f, "SELECT ReadAt FROM Notifications WHERE Id='n1'"));
        Assert.Equal("1", Scalar(f, $"SELECT COUNT(*) FROM AuditEvents WHERE EntityType='Notification' AND EntityId='n1' AND Action='Read' AND ActorUserId='{meId}'"));

        var again = await Json(await me.PostAsync("/api/v1/notifications/n1:read", null));
        Assert.Equal(2, again.GetProperty("version").GetInt32());              // nothing changed
        Assert.Equal(body.GetProperty("readAt").GetString(), again.GetProperty("readAt").GetString());
        Assert.Equal("1", Scalar(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='Notification' AND Action='Read'"));
    }

    [Fact]
    public async Task Someone_elses_notification_is_403_and_stays_unread_and_an_unknown_id_is_404()
    {
        var (f, me, other, meId, otherId) = await Setup(); using var _ = f;
        Note(f, "n1", meId, "GateEntered", "2026-10-01T10:00:00Z");

        var r = await other.PostAsync("/api/v1/notifications/n1:read", null);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("NotYourNotification", (await Json(r)).GetProperty("guard").GetString());
        Assert.Equal("", Scalar(f, "SELECT ReadAt FROM Notifications WHERE Id='n1'"));
        Assert.Equal("0", Scalar(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='Notification' AND Action='Read'"));

        Assert.Equal(HttpStatusCode.NotFound, (await me.PostAsync("/api/v1/notifications/nope:read", null)).StatusCode);
    }

    [Fact]
    public async Task Read_all_marks_only_my_unread_notifications_and_audits_once()
    {
        var (f, me, other, meId, otherId) = await Setup(); using var _ = f;
        Note(f, "n1", meId, "GateEntered", "2026-10-01T10:00:00Z");
        Note(f, "n2", meId, "GateEntered", "2026-10-01T11:00:00Z");
        Note(f, "n3", meId, "GateEntered", "2026-10-01T12:00:00Z", read: true);
        Note(f, "x1", otherId, "GateEntered", "2026-10-01T13:00:00Z");

        var r = await me.PostAsync("/api/v1/me/notifications:read-all", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(2, (await Json(r)).GetProperty("marked").GetInt32());
        Assert.Equal("0", Scalar(f, $"SELECT COUNT(*) FROM Notifications WHERE UserId='{meId}' AND ReadAt IS NULL"));
        Assert.Equal("1", Scalar(f, $"SELECT COUNT(*) FROM Notifications WHERE UserId='{otherId}' AND ReadAt IS NULL"));      // untouched
        Assert.Equal("2", Scalar(f, "SELECT Version FROM Notifications WHERE Id='n1'"));
        Assert.Equal("1", Scalar(f, "SELECT Version FROM Notifications WHERE Id='n3'"));                                    // was already read
        Assert.Equal("1", Scalar(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='Notification' AND Action='ReadAll'"));

        Assert.Equal(0, (await Json(await me.PostAsync("/api/v1/me/notifications:read-all", null))).GetProperty("marked").GetInt32());
        Assert.Equal("1", Scalar(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='Notification' AND Action='ReadAll'"));   // nothing to mark, nothing to audit
    }

    [Fact]
    public async Task The_inbox_and_my_work_need_a_signed_in_user()
    {
        using var f = new ApiFactory();
        var anon = f.CreateClient();
        foreach (var url in new[] { "/api/v1/me/notifications", "/api/v1/me/notifications/count", "/api/v1/me/work" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/api/v1/notifications/n1:read", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/api/v1/me/notifications:read-all", null)).StatusCode);
    }

    // ---- My work ----------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task My_work_lists_what_is_open_and_mine_or_my_teams_with_due_dates_and_counts()
    {
        var (f, me, other, meId, otherId) = await Setup(); using var _ = f;
        SeedTrain(f, meId, otherId);                    // t1 Planning: g1 Code Freeze (mine, Pending, due 2026-10-23), g2 Compliance (other's, InProgress); tasks k1 mine, k2 other's
        Sql(f, $@"
            INSERT INTO Teams(Id,Handle,Name) VALUES('tm1','platform','Platform'),('tm2','data','Data');
            INSERT INTO TeamMembers(TeamId,UserId) VALUES('tm1','{meId}');
            INSERT INTO StageGates(Id,ReleaseTrainId,GateName,GateClass,SequenceOrder,OffsetDays,DueOn,RequiredBeforeStatus,OwnerTeamId,Status) VALUES
              ('g3','t1','Perf test','Standard',3,3,'2020-01-01','Executing','tm1','InProgress'),
              ('g4','t1','Data check','Standard',4,3,'2099-01-01','Executing','tm2','Pending');
            INSERT INTO ChecklistTasks(Id,StageGateId,TaskDescription,OwnerTeamId,SequenceOrder) VALUES('k3','g3','Run soak test','tm1',1),('k4','g4','Check data','tm2',1);
            INSERT INTO ChecklistTasks(Id,StageGateId,TaskDescription,OwnerUserId,SequenceOrder,IsCompleted,CompletedAt,CompletedByUserId) VALUES('k5','g3','Done already','{meId}',2,1,'2026-10-01T00:00:00Z','{meId}');

            INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CurrentStatus,CreatedAt,UpdatedAt) VALUES('t3','Running','2026-10-30','Low','Executing','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z');
            INSERT INTO RunbookSteps(Id,ReleaseTrainId,StepCode,Section,Title,OwnerUserId,PlannedStartAt,PlannedDurationMin) VALUES
              ('s1','t3','R-001','Deploy','Deploy API','{meId}','2020-01-01T06:00:00Z',30),
              ('s2','t3','R-002','Deploy','Not mine','{otherId}','2020-01-01T07:00:00Z',30);
            INSERT INTO RunbookRuns(Id,ReleaseTrainId,Mode,StartedAt,StartedByUserId) VALUES('run1','t3','Live','2020-01-01T05:00:00Z','{meId}');
            INSERT INTO StepExecutions(Id,RunId,StepId) VALUES('e1','run1','s1'),('e2','run1','s2');

            INSERT INTO GoNoGoDecisions(Id,ReleaseTrainId,Decision,DecidedByUserId,DecidedAt,GateSnapshotJson) VALUES('d1','t3','GoWithConditions','{meId}','2026-10-01T00:00:00Z','{{}}');
            INSERT INTO GoNoGoConditions(Id,DecisionId,Text,OwnerUserId,ExpiresAt) VALUES('c1','d1','Load test signed off','{meId}','2099-01-01T00:00:00Z'),('c2','d1','Not mine','{otherId}','2099-01-01T00:00:00Z');

            INSERT INTO PostImplementationReviews(Id,ReleaseTrainId,RequiredReason) VALUES('p1','t1','Manual');
            INSERT INTO PirActions(Id,PirId,Text,OwnerUserId,DueOn) VALUES('a1','p1','Fix the alert','{meId}','2020-02-01'),('a2','p1','Not mine','{otherId}','2099-01-01');");

        var r = await me.GetAsync("/api/v1/me/work");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var w = await Json(r);
        string[] Ids(string list) => [.. w.GetProperty(list).EnumerateArray().Select(i => i.GetProperty("id").GetString()!)];

        Assert.Equal(["k3", "k1"], Ids("tasks"));                               // team task via tm1 (overdue, sorted first) then mine; not other's, not tm2's, not completed
        Assert.Equal(["g3", "g1"], Ids("gates"));                               // g2 is the other user's, g4 belongs to a team I am not in
        Assert.Equal(["e1"], w.GetProperty("steps").EnumerateArray().Select(i => i.GetProperty("executionId").GetString()!).ToArray());
        Assert.Equal(["c1"], Ids("conditions"));
        Assert.Equal(["a1"], Ids("pirActions"));

        var k3 = w.GetProperty("tasks")[0];
        Assert.Equal("Run soak test", k3.GetProperty("description").GetString());
        Assert.Equal("g3", k3.GetProperty("gateId").GetString());
        Assert.Equal("2020-01-01", k3.GetProperty("dueOn").GetString());
        Assert.True(k3.GetProperty("overdue").GetBoolean());
        Assert.Equal("team", k3.GetProperty("via").GetProperty("kind").GetString());
        Assert.Equal("Platform", k3.GetProperty("via").GetProperty("teamName").GetString());
        var k1 = w.GetProperty("tasks")[1];
        Assert.Equal("me", k1.GetProperty("via").GetProperty("kind").GetString());
        Assert.Equal("2026-10-23", k1.GetProperty("dueOn").GetString());
        Assert.Equal("R26.10", k1.GetProperty("trainTitle").GetString());

        var g3 = w.GetProperty("gates")[0];
        Assert.Equal(("InProgress", 1, true), (g3.GetProperty("status").GetString(), g3.GetProperty("openTasks").GetInt32(), g3.GetProperty("overdue").GetBoolean()));   // k5 is done: one open task

        var step = w.GetProperty("steps")[0];
        Assert.Equal(("R-001", "Live", "Scheduled", true), (step.GetProperty("stepCode").GetString(), step.GetProperty("runMode").GetString(), step.GetProperty("status").GetString(), step.GetProperty("late").GetBoolean()));
        Assert.Equal("2099-01-01T00:00:00Z", w.GetProperty("conditions")[0].GetProperty("expiresAt").GetString());
        Assert.False(w.GetProperty("conditions")[0].GetProperty("expired").GetBoolean());
        Assert.Equal(("2020-02-01", true), (w.GetProperty("pirActions")[0].GetProperty("dueOn").GetString(), w.GetProperty("pirActions")[0].GetProperty("overdue").GetBoolean()));

        var counts = w.GetProperty("counts");
        Assert.Equal((2, 2, 1, 1, 1, 7), (counts.GetProperty("tasks").GetInt32(), counts.GetProperty("gates").GetInt32(), counts.GetProperty("steps").GetInt32(),
            counts.GetProperty("conditions").GetInt32(), counts.GetProperty("pirActions").GetInt32(), counts.GetProperty("total").GetInt32()));
        Assert.Equal(4, counts.GetProperty("overdue").GetInt32());              // task k3, gate g3, late step e1, PIR action a1

        var theirs = await Json(await other.GetAsync("/api/v1/me/work"));       // the other user sees their own queue, never mine
        Assert.Equal(["k2"], theirs.GetProperty("tasks").EnumerateArray().Select(i => i.GetProperty("id").GetString()!).ToArray());
        Assert.Equal(["c2"], theirs.GetProperty("conditions").EnumerateArray().Select(i => i.GetProperty("id").GetString()!).ToArray());
    }

    [Fact]
    public async Task My_work_leaves_out_closed_conditions_finished_gates_ended_runs_and_closed_trains()
    {
        var (f, me, _, meId, otherId) = await Setup(); using var _f = f;
        SeedTrain(f, meId, otherId);
        Sql(f, $@"
            INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CurrentStatus,CreatedAt,UpdatedAt) VALUES('t3','Running','2026-10-30','Low','Executing','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z');
            INSERT INTO RunbookSteps(Id,ReleaseTrainId,StepCode,Section,Title,OwnerUserId,PlannedStartAt,PlannedDurationMin) VALUES('s1','t3','R-001','Deploy','Deploy API','{meId}','2026-10-30T06:00:00Z',30);
            INSERT INTO RunbookRuns(Id,ReleaseTrainId,Mode,StartedAt,StartedByUserId,EndedAt,Outcome) VALUES('run1','t3','Live','2026-10-30T05:00:00Z','{meId}','2026-10-30T08:00:00Z','Aborted');
            INSERT INTO StepExecutions(Id,RunId,StepId) VALUES('e1','run1','s1');
            INSERT INTO GoNoGoDecisions(Id,ReleaseTrainId,Decision,DecidedByUserId,DecidedAt,GateSnapshotJson) VALUES('d1','t3','GoWithConditions','{meId}','2026-10-01T00:00:00Z','{{}}'),('d2','t3','Go','{meId}','2026-10-02T00:00:00Z','{{}}');
            INSERT INTO GoNoGoConditions(Id,DecisionId,Text,OwnerUserId,ExpiresAt,ClosedAt,ClosedByUserId) VALUES('c1','d1','Superseded','{meId}','2099-01-01T00:00:00Z',NULL,NULL);
            UPDATE ReleaseTrains SET ArchivedAt='2026-10-05T00:00:00Z' WHERE Id='t1';");
        var w = await Json(await me.GetAsync("/api/v1/me/work"));
        Assert.Equal(0, w.GetProperty("counts").GetProperty("total").GetInt32());     // archived train, ended run, superseded decision's condition
    }

    // ---- team webhooks ----------------------------------------------------------------------------------------------------------------
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(Uri Uri, string Body)> Calls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add((request.RequestUri!, await request.Content!.ReadAsStringAsync(ct)));
            return respond(request);
        }
    }

    private sealed class StubFactory(HttpMessageHandler h) : IHttpClientFactory { public HttpClient CreateClient(string name) => new(h, disposeHandler: false); }
    private sealed class RecordingSink : IAlertSink
    {
        public List<(string Source, string Kind, string Key, string Message)> Raised { get; } = [];
        public Task RaiseAsync(string sourceSystem, string kind, string key, string message, CancellationToken ct = default) { Raised.Add((sourceSystem, kind, key, message)); return Task.CompletedTask; }
    }

    private sealed class DevEnv(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private const string Secret = "https://93.184.216.34/services/T000/B000/SECRETTOKEN";

    private static (TeamWebhookSender Sender, StubHandler Http, RecordingSink Sink, ApiFactory F) WebhookSetup(string url, string kind, Func<HttpRequestMessage, HttpResponseMessage> respond,
        Dictionary<string, string?>? cfg = null, string env = "Production")
    {
        var f = new ApiFactory();
        _ = f.Server;                                               // boot: migrations run
        Sql(f, $@"INSERT INTO Users(Id,Email,DisplayName,Role) VALUES('rte1','rte1@x.com','Rae','RTE');
                  INSERT INTO {WebhookTestRows.Into} VALUES {WebhookTestRows.Row(f.Services, "w1", "Platform channel", url, kind)};
                  INSERT INTO Teams(Id,Handle,Name,WebhookDestinationId) VALUES('tm1','platform','Platform','w1'),('tm2','quiet','Quiet',NULL);
                  INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CreatedAt,UpdatedAt) VALUES('t1','R26.10','2026-10-30','Low','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z')");
        var dbf = f.Services.GetRequiredService<IDbContextFactory<ReleaseDbContext>>();
        var time = TimeProvider.System;
        var sink = new RecordingSink();
        var http = new StubHandler(respond);
        var config = new ConfigurationBuilder().AddInMemoryCollection(cfg ?? []).AddInMemoryCollection(new Dictionary<string, string?> { ["Notifications:Webhooks:RetryDelayMs"] = "0" }).Build();
        var writer = new SyncAlertWriter(dbf, time, NullLogger<SyncAlertWriter>.Instance, f.Services.GetRequiredService<INotifier>());
        return (new TeamWebhookSender(dbf, time, new StubFactory(http), writer, sink, config, new DevEnv(env), NullLogger<TeamWebhookSender>.Instance,
            WebhookTestRows.Vault(f.Services), OutboundAddressPolicy.Default), http, sink, f);
    }

    private static WebhookNotice Notice(string team = "tm1") => new(team, "GateOverdue", "StageGate", "g1", 1, "Gate 'Code Freeze' is overdue", "t1");
    private static HttpResponseMessage Status(HttpStatusCode c) => new(c);

    [Fact]
    public async Task A_delivered_webhook_posts_the_text_for_Teams_and_Slack_and_the_full_notice_for_Generic_and_is_audited_without_the_url()
    {
        foreach (var kind in new[] { "Teams", "Slack", "Generic" })
        {
            var (sender, http, sink, f) = WebhookSetup(Secret, kind, _ => Status(HttpStatusCode.OK)); using var _ = f;
            Assert.True(await sender.SendAsync(Notice()));
            var call = Assert.Single(http.Calls);
            Assert.Equal(Secret, call.Uri.ToString());
            var body = JsonDocument.Parse(call.Body).RootElement;
            Assert.Equal("Gate 'Code Freeze' is overdue", body.GetProperty(kind == "Generic" ? "message" : "text").GetString());
            if (kind == "Generic") { Assert.Equal("GateOverdue", body.GetProperty("kind").GetString()); Assert.Equal("g1", body.GetProperty("entityId").GetString()); }
            else Assert.False(body.TryGetProperty("kind", out var ignored));
            Assert.Empty(sink.Raised);
            Assert.Equal("1", Scalar(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='WebhookDestination' AND EntityId='w1' AND Action='Delivered' AND ReleaseTrainId='t1'"));
            Assert.DoesNotContain("SECRETTOKEN", Scalar(f, "SELECT group_concat(COALESCE(AfterJson,''),' ') FROM AuditEvents"));
        }
    }

    [Fact]
    public async Task A_team_without_a_webhook_is_simply_skipped()
    {
        var (sender, http, sink, f) = WebhookSetup(Secret, "Teams", _ => Status(HttpStatusCode.OK)); using var _ = f;
        Assert.False(await sender.SendAsync(Notice("tm2")));
        Assert.False(await sender.SendAsync(Notice("missing-team")));
        Assert.Empty(http.Calls); Assert.Empty(sink.Raised);
        Assert.Equal("0", Scalar(f, "SELECT COUNT(*) FROM SyncAlerts"));
    }

    [Theory]
    [InlineData("https://127.0.0.1/hook")]
    [InlineData("https://10.1.2.3/hook")]
    [InlineData("https://172.16.0.9/hook")]
    [InlineData("https://192.168.1.1/hook")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://100.64.0.1/hook")]
    [InlineData("https://0.0.0.0/hook")]
    [InlineData("https://[::1]/hook")]
    [InlineData("https://[fe80::1]/hook")]
    [InlineData("https://[fd00::1]/hook")]
    [InlineData("https://[::ffff:10.0.0.1]/hook")]
    [InlineData("https://localhost/hook")]
    public async Task Private_and_local_targets_are_refused_before_anything_is_sent_and_raise_an_alert(string url)
    {
        var (sender, http, sink, f) = WebhookSetup(url, "Generic", _ => Status(HttpStatusCode.OK)); using var _ = f;
        Assert.False(await sender.SendAsync(Notice()));
        Assert.Empty(http.Calls);
        var alert = Assert.Single(sink.Raised);
        Assert.Equal(("Webhook", "DeliveryFailed", "w1"), (alert.Source, alert.Kind, alert.Key));
        Assert.Contains("private or local", alert.Message);
        Assert.Equal("1", Scalar(f, "SELECT COUNT(*) FROM SyncAlerts WHERE SourceSystem='Webhook' AND Kind='DeliveryFailed' AND ReleaseTrainId='t1'"));
    }

    [Fact]
    public async Task Private_targets_can_be_allowed_by_config_for_an_on_prem_relay()
    {
        var (sender, http, sink, f) = WebhookSetup("https://10.1.2.3/hook", "Generic", _ => Status(HttpStatusCode.OK), new() { ["Notifications:Webhooks:AllowPrivateTargets"] = "true" }); using var _ = f;
        Assert.True(await sender.SendAsync(Notice()));
        Assert.Single(http.Calls); Assert.Empty(sink.Raised);
    }

    [Fact]
    public async Task Credentials_in_the_url_are_refused_and_never_echoed()
    {
        var (sender, http, sink, f) = WebhookSetup("https://user:pw@93.184.216.34/hook", "Generic", _ => Status(HttpStatusCode.OK)); using var _ = f;
        Assert.False(await sender.SendAsync(Notice()));
        Assert.Contains("credentials", Assert.Single(sink.Raised).Message);
        Assert.Empty(http.Calls);
        Assert.DoesNotContain("pw@", sink.Raised[0].Message);
    }

    [Fact]
    public async Task A_server_error_is_retried_a_limited_number_of_times_and_then_raises_an_alert_that_never_shows_the_url()
    {
        var (sender, http, sink, f) = WebhookSetup(Secret, "Teams", _ => Status(HttpStatusCode.BadGateway), new() { ["Notifications:Webhooks:MaxAttempts"] = "3" }); using var _ = f;
        Assert.False(await sender.SendAsync(Notice()));
        Assert.Equal(3, http.Calls.Count);
        var alert = Assert.Single(sink.Raised);
        Assert.Contains("HTTP 502", alert.Message);
        Assert.Contains("3 attempts", alert.Message);
        Assert.Contains("Platform channel", alert.Message);
        Assert.Contains("93.184.216.34", alert.Message);                     // the host helps an admin; the path (the secret) is never included
        Assert.DoesNotContain("SECRETTOKEN", alert.Message);
        Assert.DoesNotContain("SECRETTOKEN", Scalar(f, "SELECT ErrorMessage FROM SyncAlerts"));
        Assert.DoesNotContain("SECRETTOKEN", Scalar(f, "SELECT group_concat(Message,' ') FROM Notifications"));

        Assert.False(await sender.SendAsync(Notice()));                       // a repeat lands on the same open alert
        Assert.Equal("1", Scalar(f, "SELECT COUNT(*) FROM SyncAlerts"));
        Assert.Equal("2", Scalar(f, "SELECT OccurrenceCount FROM SyncAlerts"));
    }

    [Fact]
    public async Task A_transient_failure_that_recovers_is_delivered_without_an_alert()
    {
        var n = 0;
        var (sender, http, sink, f) = WebhookSetup(Secret, "Slack", _ => Status(++n < 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)); using var _ = f;
        Assert.True(await sender.SendAsync(Notice()));
        Assert.Equal(3, http.Calls.Count);
        Assert.Empty(sink.Raised);
    }

    [Fact]
    public async Task A_client_error_is_not_retried()
    {
        var (sender, http, sink, f) = WebhookSetup(Secret, "Slack", _ => Status(HttpStatusCode.NotFound)); using var _ = f;
        Assert.False(await sender.SendAsync(Notice()));
        Assert.Single(http.Calls);
        Assert.Contains("HTTP 404 (not retried)", Assert.Single(sink.Raised).Message);
    }

    [Fact]
    public async Task A_transport_exception_is_reported_by_type_only()
    {
        var (sender, http, sink, f) = WebhookSetup(Secret, "Generic", _ => throw new HttpRequestException("connect to " + Secret + " failed"), new() { ["Notifications:Webhooks:MaxAttempts"] = "2" }); using var _ = f;
        Assert.False(await sender.SendAsync(Notice()));
        Assert.Equal(2, http.Calls.Count);
        var msg = Assert.Single(sink.Raised).Message;
        Assert.Contains("HttpRequestException", msg);
        Assert.DoesNotContain("SECRETTOKEN", msg);
    }

    [Fact]
    public void The_address_policy_blocks_every_private_range_and_lets_public_addresses_through()
    {
        foreach (var blocked in new[] { "0.1.2.3", "10.0.0.1", "100.127.255.255", "127.0.0.1", "169.254.1.1", "172.31.255.255", "192.168.0.1", "192.0.0.5", "198.19.0.1", "224.0.0.1", "255.255.255.255", "::1", "::", "fe80::1", "fc00::1", "ff02::1", "::ffff:127.0.0.1" })
            Assert.True(WebhookAddressPolicy.IsBlocked(System.Net.IPAddress.Parse(blocked)), blocked);
        foreach (var open in new[] { "93.184.216.34", "8.8.8.8", "172.32.0.1", "100.63.0.1", "198.20.0.1", "2606:4700:4700::1111" })
            Assert.False(WebhookAddressPolicy.IsBlocked(System.Net.IPAddress.Parse(open)), open);
    }
}
