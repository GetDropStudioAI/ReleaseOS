using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Comms;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Reminders;

namespace ReleaseMgmt.Infrastructure.Tests;

/// <summary>REOS-45 dispatch service against the migrated schema with a test clock, a fake renderer and a fake webhook sender.</summary>
public sealed class CommDispatchServiceTests(TriggerSuiteFixture fx) : IClassFixture<TriggerSuiteFixture>
{
    private sealed class Factory(string path) : IDbContextFactory<ReleaseDbContext>
    {
        public ReleaseDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ReleaseDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").AddInterceptors(new SqliteConnectionInterceptor()).Options);
    }

    private sealed class Renderer(Func<string, string, RenderedComm> f) : ICommDispatchRenderer
    {
        public Task<RenderedComm> RenderAsync(string trainId, string? templateId, string? text, string target, CancellationToken ct) => Task.FromResult(f(text ?? "body", target));
    }

    private sealed class Sender(Func<CommWebhookResult> f) : ICommWebhookSender
    {
        public int Calls; public string? LastBody;
        public Task<CommWebhookResult> SendAsync(CommWebhookTarget target, string jsonBody, CancellationToken ct) { Calls++; LastBody = jsonBody; return Task.FromResult(f()); }
    }

    private static readonly Actor Rte = new("rte"), Dev = new("dev");
    private static RenderedComm Good(string text) => new(text, [], new DateTime(2026, 10, 20, 14, 0, 0, DateTimeKind.Utc), 1);

    private (string Path, FakeTimeProvider Time, CommDispatchService Svc, Sender Sender) Env(Func<string, string, RenderedComm>? render = null, Func<CommWebhookResult>? send = null, bool withAlerts = true)
    {
        var path = fx.FreshPath();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 30, 6, 30, 45, 900, TimeSpan.Zero));
        var db = new Factory(path);
        using (var c = TriggerSuiteFixture.Open(path))
            TriggerSuiteFixture.Run(c, @"
                INSERT INTO CommTemplates(Id,ReleaseTrainId,TemplateType,Audience,SubjectLine,MarkdownBody) VALUES('c1','t1','GoNoGo','All','Subject','Body');
                INSERT INTO CommSchedule(Id,ReleaseTrainId,CommTemplateId,DueAt) VALUES('s1','t1','c1','2026-10-30T06:00:00Z');
                INSERT INTO WebhookDestinations(Id,Name,Host,ProtectedUrl,UrlHmac,Kind) VALUES('w1','ops','hooks.example.test','CfDJ8-protected-by-the-vault','" + new string('0', 64) + "','Slack');");
        var sender = new Sender(send ?? (() => new CommWebhookResult(true, "hooks.example.test", null)));
        var alerts = withAlerts ? new SyncAlertWriter(db, time, NullLogger<SyncAlertWriter>.Instance) : null;
        return (path, time, new CommDispatchService(db, time, new Renderer(render ?? ((t, _) => Good(t))), sender, alerts), sender);
    }

    private static object? Scalar(string path, string sql)
    {
        using var c = TriggerSuiteFixture.Open(path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }

    [Fact]
    public async Task SentAt_and_DispatchedAt_come_from_the_injected_clock_in_whole_seconds_and_the_schedule_version_moves()
    {
        var e = Env();
        var r = await e.Svc.DispatchAsync("t1", new(null, "s1", "Copy", "Markdown", null, null), Rte, expectedTrainVersion: 1);
        Assert.True(r.IsOk);
        Assert.Equal(new DateTime(2026, 10, 30, 6, 30, 45, DateTimeKind.Utc), r.Value!.SentAt);
        Assert.True(r.Value.Late);   // due 06:00
        Assert.Equal("2026-10-30T06:30:45Z", Scalar(e.Path, "SELECT SentAt FROM CommSchedule WHERE Id='s1'"));
        Assert.Equal("2026-10-30T06:30:45Z", Scalar(e.Path, "SELECT DispatchedAt FROM CommDispatches"));
        Assert.Equal(2L, Scalar(e.Path, "SELECT Version FROM CommSchedule WHERE Id='s1'"));
        Assert.Equal("2026-10-30T06:30:45Z", Scalar(e.Path, "SELECT OccurredAt FROM AuditEvents WHERE EntityType='CommDispatch'"));
        Assert.Equal(1L, Scalar(e.Path, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='CommDispatch'"));
    }

    [Fact]
    public async Task Only_RTE_and_ReleaseManager_may_dispatch()
    {
        var e = Env();
        var r = await e.Svc.DispatchAsync("t1", new("c1", null, "Copy", null, null, null), Dev, null);
        Assert.Equal(CommGuards.DispatchRole, r.Failures[0].Guard);
        Assert.True((await e.Svc.DispatchAsync("t1", new("c1", null, "Copy", null, null, null), new Actor("rm"), null)).IsOk);
        Assert.Equal(CommGuards.DispatchRole, (await e.Svc.DispatchAsync("t1", new("c1", null, "Copy", null, null, null), new Actor("gov1"), null)).Failures[0].Guard);
        Assert.Equal(1L, Scalar(e.Path, "SELECT COUNT(*) FROM CommDispatches"));
    }

    [Fact]
    public async Task Token_errors_block_the_dispatch_and_the_renderer_moving_the_train_is_a_conflict()
    {
        var e = Env((t, _) => new RenderedComm("", ["Unknown token {X}"], DateTime.UtcNow, 1));
        var r = await e.Svc.DispatchAsync("t1", new("c1", null, "Mailto", null, null, null), Rte, null);
        Assert.Equal(CommGuards.TokenErrors, r.Failures[0].Guard);
        Assert.Equal(["Unknown token {X}"], r.Failures[0].Items);
        Assert.Equal(0L, Scalar(e.Path, "SELECT COUNT(*) FROM CommDispatches"));

        var moved = Env((t, _) => new RenderedComm(t, [], DateTime.UtcNow, 5));   // the hydrated snapshot is of train version 5, the caller previewed 1
        Assert.Equal(ResultKind.Conflict, (await moved.Svc.DispatchAsync("t1", new("c1", null, "Copy", null, null, null), Rte, expectedTrainVersion: 1)).Kind);
        Assert.Equal(ResultKind.Conflict, (await moved.Svc.DispatchAsync("t1", new("c1", null, "Copy", null, null, null), Rte, expectedTrainVersion: 9)).Kind);
        Assert.Equal(0L, Scalar(moved.Path, "SELECT COUNT(*) FROM CommDispatches"));
    }

    [Fact]
    public async Task A_failed_webhook_is_recorded_as_Failed_leaves_the_item_unsent_and_raises_one_alert_without_the_url()
    {
        var e = Env(send: () => new CommWebhookResult(false, "hooks.example.test", "HTTP 503"));
        var r = await e.Svc.DispatchAsync("t1", new(null, "s1", "Webhook", null, "w1", null), Rte, null);
        Assert.True(r.IsOk);
        Assert.Equal("Failed", r.Value!.Outcome);
        Assert.Null(Scalar(e.Path, "SELECT SentAt FROM CommSchedule WHERE Id='s1'"));
        Assert.Equal("Failed", Scalar(e.Path, "SELECT Outcome FROM CommDispatches"));
        Assert.Equal("Webhook|DeliveryFailed|t1", Scalar(e.Path, "SELECT SourceSystem||'|'||Kind||'|'||ReleaseTrainId FROM SyncAlerts"));
        var msg = (string)Scalar(e.Path, "SELECT ErrorMessage FROM SyncAlerts")!;
        Assert.Equal("Webhook 'ops' (hooks.example.test) failed: HTTP 503", msg);
        Assert.DoesNotContain("SECRET", msg);
        Assert.DoesNotContain("SECRET", (string)Scalar(e.Path, "SELECT AfterJson FROM AuditEvents WHERE EntityType='CommDispatch'")!);
    }

    [Fact]
    public async Task A_failed_webhook_is_still_recorded_when_no_alert_writer_is_registered()
    {
        var e = Env(send: () => new CommWebhookResult(false, "h", "HTTP 500"), withAlerts: false);
        var r = await e.Svc.DispatchAsync("t1", new("c1", null, "Webhook", null, "w1", null), Rte, null);
        Assert.Equal("Failed", r.Value!.Outcome);   // the row is the visible record; the missing writer is logged
        Assert.Equal(1L, Scalar(e.Path, "SELECT COUNT(*) FROM CommDispatches WHERE Outcome='Failed'"));
    }

    [Fact]
    public async Task Webhook_payload_is_valid_JSON_for_each_kind_and_is_what_the_sender_gets_and_the_row_stores()
    {
        var e = Env((t, target) => Good(target == CommTargets.JsonString ? t + " \\n end" : t));
        var r = await e.Svc.DispatchAsync("t1", new("c1", null, "Webhook", null, "w1", null), Rte, null);
        Assert.True(r.IsOk);
        Assert.Equal(e.Sender.LastBody, r.Value!.Body);
        Assert.Equal(e.Sender.LastBody, Scalar(e.Path, "SELECT HydratedBody FROM CommDispatches"));
        Assert.Equal("Subject \n end\n\nbody \n end", System.Text.Json.JsonDocument.Parse(e.Sender.LastBody!).RootElement.GetProperty("text").GetString());   // Slack: {"text": subject, blank line, body}
        Assert.Equal("Subject \n end", r.Value.Subject);   // the stored subject is the readable, unescaped one
    }

    [Fact]
    public async Task Dispatch_is_keyset_paged_and_the_stored_body_hash_matches()
    {
        var e = Env();
        for (var i = 0; i < 3; i++) { Assert.True((await e.Svc.DispatchAsync("t1", new("c1", null, "Copy", null, null, null), Rte, null)).IsOk); e.Time.Advance(TimeSpan.FromSeconds(5)); }
        var p1 = (await e.Svc.ListAsync("t1", 2, null))!;
        var p2 = (await e.Svc.ListAsync("t1", 2, p1.NextCursor))!;
        Assert.Equal(2, p1.Items.Count); Assert.Single(p2.Items); Assert.Null(p2.NextCursor);
        Assert.True(p1.Items[0].DispatchedAt >= p1.Items[1].DispatchedAt && p1.Items[1].DispatchedAt >= p2.Items[0].DispatchedAt);
        var one = (await e.Svc.GetAsync(p1.Items[0].Id))!;
        Assert.Equal(CommDispatchService.Sha256Hex(one.Body), one.BodySha256);
        Assert.Null(await e.Svc.ListAsync("nope", null, null));
    }
}
