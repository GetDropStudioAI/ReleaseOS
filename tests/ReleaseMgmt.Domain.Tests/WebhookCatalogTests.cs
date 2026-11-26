using System.Text.Json;
using ReleaseMgmt.Domain.Services;

namespace ReleaseMgmt.Domain.Tests;

/// <summary>REOS-164: the event catalogue, how an audit row becomes an event, the payload, the backoff and the signature a receiver verifies.</summary>
public class WebhookCatalogTests
{
    [Theory]
    [InlineData("ReleaseTrain", "Create", "release.created")]
    [InlineData("ReleaseTrain", "Advance", "release.status_changed")]
    [InlineData("ReleaseTrain", "Complete", "release.status_changed")]
    [InlineData("ReleaseTrain", "Abort", "release.status_changed")]
    [InlineData("StageGate", "Certify", "gate.certified")]
    [InlineData("StageGate", "Fail", "gate.failed")]
    [InlineData("Approval", "Request", "approval.requested")]
    [InlineData("Approval", "Resubmit", "approval.requested")]
    [InlineData("Approval", "Approved", "approval.decided")]
    [InlineData("Approval", "ChangesRequested", "approval.decided")]
    [InlineData("GoNoGo", "Record", "gonogo.recorded")]
    [InlineData("FreezeWindow", "RequestOverride", "freeze.override_requested")]
    [InlineData("FreezeOverride", "Grant", "freeze.override_granted")]
    [InlineData("FreezeOverride", "Renew", "freeze.override_granted")]
    [InlineData("Deployment", "Create", "deployment.created")]
    [InlineData("Deployment", "State", "deployment.state_changed")]
    [InlineData("DeploymentEvent", "Append", "deployment.events_appended")]
    [InlineData("PostImplementationReview", "AutoRequired", "pir.created")]
    [InlineData("SyncAlert", "Raised", "sync_alert.raised")]
    public void An_audit_row_stands_for_the_catalogue_event_it_names(string entity, string action, string key)
    {
        Assert.Equal(key, WebhookCatalog.EventFor(entity, action, null));
        Assert.Contains(key, WebhookCatalog.Keys);
    }

    [Theory]
    [InlineData("ReleaseTrain", "RollbackRehearsed")]
    [InlineData("StageGate", "Start")]
    [InlineData("StageGate", "Decertified")]
    [InlineData("ApiToken", "Create")]
    [InlineData("WebhookSubscription", "Create")]
    [InlineData("SyncAlert", "Repeated")]
    [InlineData("Notification", "Created")]
    public void Anything_else_is_not_an_event_and_never_leaves_the_app(string entity, string action) => Assert.Null(WebhookCatalog.EventFor(entity, action, null));

    [Fact]
    public void Every_catalogue_event_is_mapped_from_some_audit_row_and_has_a_name_and_a_description()
    {
        var mapped = new[]
        {
            ("ReleaseTrain","Create"),("ReleaseTrain","Advance"),("StageGate","Certify"),("StageGate","Fail"),("Approval","Request"),("Approval","Approved"),("GoNoGo","Record"),("FreezeWindow","RequestOverride"),
            ("FreezeOverride","Grant"),("Deployment","Create"),("Deployment","State"),("DeploymentEvent","Append"),("PostImplementationReview","AutoRequired"),("SyncAlert","Raised"),
        }.Select(x => WebhookCatalog.EventFor(x.Item1, x.Item2, null)).ToHashSet();
        Assert.Equal(WebhookCatalog.Keys.OrderBy(k => k), mapped.OfType<string>().OrderBy(k => k));
        Assert.All(WebhookCatalog.All, e => { Assert.False(string.IsNullOrWhiteSpace(e.Name)); Assert.False(string.IsNullOrWhiteSpace(e.Description)); Assert.Matches("^[a-z_]+\\.[a-z_]+$", e.Key); });
        Assert.DoesNotContain(WebhookCatalog.Ping, WebhookCatalog.Keys);   // a test event is never offered as something to subscribe to
    }

    [Fact]
    public void The_alert_the_webhooks_raise_about_themselves_is_not_an_event()
    {
        Assert.Equal("sync_alert.raised", WebhookCatalog.EventFor("SyncAlert", "Raised", "{\"source\":\"Jira\"}"));
        Assert.Null(WebhookCatalog.EventFor("SyncAlert", "Raised", "{\"source\":\"Webhook\",\"kind\":\"DeliveryFailed\"}"));
        Assert.Equal("sync_alert.raised", WebhookCatalog.EventFor("SyncAlert", "Raised", "not json"));
    }

    [Theory]
    [InlineData("*", "gate.failed", true)]
    [InlineData("gate.failed", "gate.failed", true)]
    [InlineData("release.created,gate.failed", "gate.failed", true)]
    [InlineData("release.created,gate.failed", "gate.certified", false)]
    [InlineData("gate.failed", "gate", false)]
    public void A_subscription_matches_the_events_it_asked_for(string list, string key, bool expected) => Assert.Equal(expected, WebhookCatalog.Matches(list, key));

    [Fact]
    public void The_event_list_is_checked_and_put_in_catalogue_order()
    {
        Assert.Equal("release.created,gate.failed", WebhookCatalog.Normalise(["gate.failed", " release.created ", "gate.failed"]));
        Assert.Equal("*", WebhookCatalog.Normalise(["*"]));
        Assert.Null(WebhookCatalog.Normalise(null));
        Assert.Null(WebhookCatalog.Normalise([]));
        Assert.Null(WebhookCatalog.Normalise([" ", ""]));
        Assert.Null(WebhookCatalog.Normalise(["release.created", "nope"]));
        Assert.Null(WebhookCatalog.Normalise(["*", "release.created"]));
        Assert.Null(WebhookCatalog.Normalise([WebhookCatalog.Ping]));
        Assert.Equal(["*"], WebhookCatalog.Split("*"));
        Assert.Equal(["a.b", "c.d"], WebhookCatalog.Split("a.b,c.d"));
    }

    [Fact]
    public void The_payload_names_the_event_the_time_the_workspace_and_what_changed()
    {
        var json = WebhookCatalog.BuildPayload("evt_42", "gate.failed", DateTime.Parse("2026-11-26T12:00:00Z").ToUniversalTime(), "default", "StageGate", "g1", "Fail", "t1", "u1", "{\"status\":\"InProgress\"}", "{\"status\":\"Failed\"}");
        var r = JsonDocument.Parse(json).RootElement;
        Assert.Equal(("evt_42", "gate.failed", "2026-11-26T12:00:00Z", "default"), (r.GetProperty("id").GetString(), r.GetProperty("type").GetString(), r.GetProperty("occurredAt").GetString(), r.GetProperty("workspaceId").GetString()));
        var d = r.GetProperty("data");
        Assert.Equal(("StageGate", "g1", "Fail", "t1", "u1"), (d.GetProperty("entityType").GetString(), d.GetProperty("entityId").GetString(), d.GetProperty("action").GetString(), d.GetProperty("releaseId").GetString(), d.GetProperty("actorUserId").GetString()));
        Assert.Equal("Failed", d.GetProperty("after").GetProperty("status").GetString());
        Assert.Equal("InProgress", d.GetProperty("before").GetProperty("status").GetString());
        Assert.DoesNotContain("\n", json);
    }

    [Fact]
    public void A_payload_over_the_limit_keeps_what_identifies_the_event_and_says_it_was_cut()
    {
        var big = "{\"x\":\"" + new string('a', WebhookCatalog.MaxPayloadBytes) + "\"}";
        var json = WebhookCatalog.BuildPayload("evt_1", "deployment.events_appended", DateTime.UtcNow, "default", "DeploymentEvent", "d1", "Append", null, null, null, big);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(json) < WebhookCatalog.MaxPayloadBytes);
        var d = JsonDocument.Parse(json).RootElement.GetProperty("data");
        Assert.True(d.GetProperty("truncated").GetBoolean());
        Assert.False(d.TryGetProperty("after", out _));
        Assert.Equal("d1", d.GetProperty("entityId").GetString());
    }

    [Fact]
    public void The_test_event_has_its_own_type_and_the_subscription_name()
    {
        var r = JsonDocument.Parse(WebhookCatalog.BuildPing("evt_test_1", DateTime.UtcNow, "w2", "Dashboard")).RootElement;
        Assert.Equal("webhook.ping", r.GetProperty("type").GetString());
        Assert.Equal("Dashboard", r.GetProperty("data").GetProperty("subscription").GetString());
        Assert.Equal("evt_7", WebhookCatalog.EventId(7));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 60)]
    [InlineData(2, 120)]
    [InlineData(3, 240)]
    [InlineData(4, 480)]
    [InlineData(8, 7680)]
    [InlineData(9, 15360)]
    [InlineData(10, 21600)]   // capped at 6 h
    [InlineData(40, 21600)]
    public void The_wait_doubles_with_each_failure_up_to_the_cap(int failed, double seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), WebhookBackoff.Delay(failed, TimeSpan.FromSeconds(60), TimeSpan.FromHours(6)));

    [Fact]
    public void A_receiver_verifies_the_signature_and_refuses_a_changed_body_a_wrong_secret_and_an_old_timestamp()
    {
        var secret = WebhookSignature.NewSecret();
        Assert.StartsWith("whsec_", secret);
        Assert.NotEqual(secret, WebhookSignature.NewSecret());
        var sig = WebhookSignature.Sign(secret, 1_000, "d1", "{\"a\":1}");
        Assert.StartsWith("v1=", sig);
        Assert.True(WebhookSignature.Verify(secret, 1_000, "d1", "{\"a\":1}", sig, 1_000));
        Assert.True(WebhookSignature.Verify(secret, 1_000, "d1", "{\"a\":1}", sig, 1_300));
        Assert.False(WebhookSignature.Verify(secret, 1_000, "d1", "{\"a\":1}", sig, 1_301));   // replay window
        Assert.False(WebhookSignature.Verify(secret, 1_000, "d1", "{\"a\":1}", sig, 699));     // a clock far behind is refused as well
        Assert.False(WebhookSignature.Verify(secret, 1_000, "d1", "{\"a\":2}", sig, 1_000));
        Assert.False(WebhookSignature.Verify(secret, 1_000, "d2", "{\"a\":1}", sig, 1_000));
        Assert.False(WebhookSignature.Verify(secret, 1_001, "d1", "{\"a\":1}", sig, 1_000));
        Assert.False(WebhookSignature.Verify("whsec_other", 1_000, "d1", "{\"a\":1}", sig, 1_000));
        Assert.False(WebhookSignature.Verify(secret, 1_000, "d1", "{\"a\":1}", null, 1_000));
        Assert.False(WebhookSignature.Verify(secret, 1_000, "d1", "{\"a\":1}", sig + "0", 1_000));
        // the same scheme as the control webhooks, so one verifier serves both
        Assert.Equal(ControlSignature.Sign(secret, 1_000, "d1", "{\"a\":1}"), sig);
    }

    [Fact]
    public void A_known_signature_is_fixed_so_the_documented_samples_can_be_checked_against_it()
    {
        // docs/WEBHOOKS.md quotes this vector; the C#, JavaScript and Python samples must reproduce it
        Assert.Equal("v1=ac276cd9bebb669b711a37e14b70c1398c4d7b8f6a6ed3f563b8813fb4f43dff", WebhookVector.Expected);   // the literal in docs/WEBHOOKS.md and the three samples
        Assert.Equal(WebhookVector.Expected, WebhookSignature.Sign(WebhookVector.Secret, WebhookVector.Timestamp, WebhookVector.DeliveryId, WebhookVector.Body));
    }
}

/// <summary>The signature test vector of docs/WEBHOOKS.md.</summary>
public static class WebhookVector
{
    public const string Secret = "whsec_test_secret";
    public const long Timestamp = 1792504800;
    public const string DeliveryId = "7d1c0a52-8d3e-4f5e-9d3b-0c1b6f6e2a11";
    public const string Body = "{\"id\":\"evt_1\",\"type\":\"gate.failed\"}";
    public static readonly string Expected = Compute();

    private static string Compute()
    {
        var mac = System.Security.Cryptography.HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Secret), System.Text.Encoding.UTF8.GetBytes($"{Timestamp}.{DeliveryId}.{Body}"));
        return "v1=" + Convert.ToHexStringLower(mac);
    }
}
