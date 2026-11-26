using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReleaseMgmt.Domain.Services;

/// <summary>One event a subscription can ask for. <see cref="Key"/> is the <c>type</c> member of the posted body and the <c>X-ReleaseOS-Event</c> header.</summary>
public sealed record WebhookEventDefinition(string Key, string Name, string Description);

/// <summary>Guard names of outbound webhooks (REOS-164). They are the <c>guard</c> member of the 422 body.</summary>
public static class WebhookGuards
{
    public const string Invalid = "WebhookInvalid";                    // name, address or event list refused
    public const string UrlRefused = "WebhookUrlRefused";              // not https, not on the allow-list, a private or local address, credentials in the address
    public const string DuplicateUrl = "WebhookDuplicateUrl";          // the same address is already a subscription of this workspace
    public const string Paused = "WebhookPaused";                      // the action needs an active subscription
    public const string NotReplayable = "WebhookNotReplayable";        // only a delivered or dead-lettered delivery can be replayed
    public const string TooMany = "WebhookTooMany";                    // the workspace has the most subscriptions it may have
}

/// <summary>
/// REOS-164 (D61): the event catalogue of the outbound webhooks and how an audit row becomes an event. The catalogue is code, not data: adding an event is a code change with a test and a line in
/// <c>docs/WEBHOOKS.md</c> (D56: a fixed catalogue, no scripting). An event is an audit row (rule 3), so everything a service or a trigger records can be offered without a second write path.
/// </summary>
public static class WebhookCatalog
{
    /// <summary>Sent by "Send test event"; always allowed, never listed as something to subscribe to.</summary>
    public const string Ping = "webhook.ping";
    /// <summary>The whole list in a subscription's <c>EventTypes</c>.</summary>
    public const string AllEvents = "*";
    public const int MaxPayloadBytes = 60_000;

    public static readonly IReadOnlyList<WebhookEventDefinition> All =
    [
        new("release.created", "Release created", "A release was created, from scratch, a template or another release."),
        new("release.status_changed", "Release status changed", "A release moved to another status (advanced, completed or aborted)."),
        new("gate.certified", "Gate certified", "A gate was certified."),
        new("gate.failed", "Gate failed", "A gate was marked failed."),
        new("approval.requested", "Approval requested", "An approval was requested for a release in an environment, or sent again after changes."),
        new("approval.decided", "Approval decided", "An approval was approved by every seat, or sent back with changes requested."),
        new("gonogo.recorded", "Go/No-Go recorded", "A Go, Conditional Go or No-Go decision was recorded."),
        new("freeze.override_requested", "Freeze override requested", "A release asked for an exception to a freeze window."),
        new("freeze.override_granted", "Freeze override granted", "A freeze override was granted or renewed."),
        new("deployment.created", "Deployment created", "A deployment was reported by the deployment tool."),
        new("deployment.state_changed", "Deployment state changed", "A deployment moved to another state (running, successful, failed, rolled back)."),
        new("deployment.events_appended", "Deployment events appended", "The deployment tool added events to a deployment."),
        new("pir.created", "Post-implementation review created", "A review was required for a release (a bad close code, or an abort during execution)."),
        new("sync_alert.raised", "Sync alert raised", "A background failure was recorded as an alert (the first time it occurs, not each repeat)."),
    ];

    public static readonly IReadOnlySet<string> Keys = All.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);

    // (audit EntityType, audit Action) -> event key
    private static readonly Dictionary<(string, string), string> Map = new()
    {
        [("ReleaseTrain", "Create")] = "release.created",
        [("ReleaseTrain", "Advance")] = "release.status_changed",
        [("ReleaseTrain", "Complete")] = "release.status_changed",
        [("ReleaseTrain", "Abort")] = "release.status_changed",
        [("StageGate", "Certify")] = "gate.certified",
        [("StageGate", "Fail")] = "gate.failed",
        [("Approval", "Request")] = "approval.requested",
        [("Approval", "Resubmit")] = "approval.requested",
        [("Approval", "Approved")] = "approval.decided",
        [("Approval", "ChangesRequested")] = "approval.decided",
        [("GoNoGo", "Record")] = "gonogo.recorded",
        [("FreezeWindow", "RequestOverride")] = "freeze.override_requested",
        [("FreezeOverride", "Grant")] = "freeze.override_granted",
        [("FreezeOverride", "Renew")] = "freeze.override_granted",
        [("Deployment", "Create")] = "deployment.created",
        [("Deployment", "State")] = "deployment.state_changed",
        [("DeploymentEvent", "Append")] = "deployment.events_appended",
        [("PostImplementationReview", "AutoRequired")] = "pir.created",
        [("SyncAlert", "Raised")] = "sync_alert.raised",
    };

    /// <summary>The event an audit row stands for, or null when it is not one the catalogue offers.</summary>
    public static string? EventFor(string entityType, string action, string? afterJson)
    {
        if (!Map.TryGetValue((entityType, action), out var key)) return null;
        // A failure of the webhooks themselves is an alert in the app, not an event posted to the receiver that is failing.
        if (key == "sync_alert.raised" && afterJson is not null && AfterSource(afterJson) == "Webhook") return null;
        return key;
    }

    private static string? AfterSource(string afterJson)
    {
        try { return JsonNode.Parse(afterJson) is JsonObject o && o["source"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null; }
        catch (JsonException) { return null; }
    }

    /// <summary>True when a subscription's event list (comma list or <c>*</c>) includes the event.</summary>
    public static bool Matches(string eventTypes, string key) =>
        eventTypes == AllEvents || eventTypes.Split(',', StringSplitOptions.RemoveEmptyEntries).Contains(key, StringComparer.Ordinal);

    /// <summary>The cleaned event list, in catalogue order (<c>*</c> when it asks for all), or null when it is empty or names an event the catalogue does not have.</summary>
    public static string? Normalise(IEnumerable<string>? names)
    {
        if (names is null) return null;
        var set = names.Select(n => (n ?? "").Trim()).Where(n => n.Length > 0).ToHashSet(StringComparer.Ordinal);
        if (set.Count == 0) return null;
        if (set.Contains(AllEvents)) return set.Count == 1 ? AllEvents : null;
        if (set.Any(n => !Keys.Contains(n))) return null;
        return string.Join(',', All.Select(e => e.Key).Where(set.Contains));
    }

    public static IReadOnlyList<string> Split(string eventTypes) => eventTypes == AllEvents ? [AllEvents] : eventTypes.Split(',', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The event's id: the same for every subscription and for a replay, so a receiver that stores ids it has handled can drop a duplicate delivery of one event.</summary>
    public static string EventId(long auditEventId) => "evt_" + auditEventId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    /// <summary>
    /// The exact body that is signed and posted: <c>{id, type, occurredAt, workspaceId, data}</c>. <c>data</c> names the thing the audit row is about and carries its before and after as the audit row
    /// recorded them (the audit rows never hold a secret, rule 11). A body over <see cref="MaxPayloadBytes"/> keeps the identifying members and drops before/after, saying so with <c>truncated</c>.
    /// </summary>
    public static string BuildPayload(string eventId, string type, DateTime occurredAt, string workspaceId, string entityType, string entityId, string action, string? trainId, string? actorUserId, string? beforeJson, string? afterJson)
    {
        JsonNode? Parse(string? j)
        {
            if (j is null) return null;
            try { return JsonNode.Parse(j); } catch (JsonException) { return JsonValue.Create(j); }
        }
        JsonObject Body(bool full)
        {
            var data = new JsonObject
            {
                ["entityType"] = entityType, ["entityId"] = entityId, ["action"] = action, ["releaseId"] = trainId, ["actorUserId"] = actorUserId,
            };
            if (full) { data["before"] = Parse(beforeJson); data["after"] = Parse(afterJson); } else data["truncated"] = true;
            return new JsonObject
            {
                ["id"] = eventId, ["type"] = type, ["occurredAt"] = occurredAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), ["workspaceId"] = workspaceId, ["data"] = data,
            };
        }
        var json = Body(full: true).ToJsonString(Compact);
        return Encoding.UTF8.GetByteCount(json) <= MaxPayloadBytes ? json : Body(full: false).ToJsonString(Compact);
    }

    /// <summary>The body of "Send test event".</summary>
    public static string BuildPing(string eventId, DateTime occurredAt, string workspaceId, string subscriptionName) =>
        new JsonObject
        {
            ["id"] = eventId, ["type"] = Ping, ["occurredAt"] = occurredAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), ["workspaceId"] = workspaceId,
            ["data"] = new JsonObject { ["message"] = "A test event from ReleaseOS. Your endpoint is reachable and the signature verifies.", ["subscription"] = subscriptionName },
        }.ToJsonString(Compact);
}

/// <summary>Retry timing: exponential, deterministic (the tests use a frozen clock), capped.</summary>
public static class WebhookBackoff
{
    /// <summary>The wait after the <paramref name="failedAttempts"/>th failed attempt: <c>base * 2^(n-1)</c>, never more than <paramref name="cap"/>.</summary>
    public static TimeSpan Delay(int failedAttempts, TimeSpan baseDelay, TimeSpan cap)
    {
        if (failedAttempts < 1) return TimeSpan.Zero;
        var factor = Math.Pow(2, Math.Min(failedAttempts - 1, 30));
        var seconds = Math.Min(baseDelay.TotalSeconds * factor, cap.TotalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }
}

/// <summary>
/// The request a receiver verifies. The same scheme as the control webhooks (<see cref="ControlSignature"/>): headers <c>X-ReleaseOS-Delivery</c> (stable across retries), <c>X-ReleaseOS-Timestamp</c> (Unix
/// seconds of this attempt) and <c>X-ReleaseOS-Signature</c> = <c>v1=hex(HMAC-SHA256(secret, "{timestamp}.{deliveryId}.{body}"))</c>; plus <c>X-ReleaseOS-Event</c> (the type) and
/// <c>X-ReleaseOS-Secret-Version</c> (which of the subscription's secrets signed it). A receiver refuses a timestamp more than <see cref="ControlSignature.ToleranceSeconds"/> away from its own clock.
/// </summary>
public static class WebhookSignature
{
    public const string EventHeader = "X-ReleaseOS-Event", SecretVersionHeader = "X-ReleaseOS-Secret-Version";
    public const string SecretPrefix = "whsec_";

    public static string NewSecret() => SecretPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Sign(string secret, long unixSeconds, string deliveryId, string body) => ControlSignature.Sign(secret, unixSeconds, deliveryId, body);

    /// <summary>What a receiver does: true when the signature matches (constant time) and the timestamp is within the replay window of <paramref name="nowUnixSeconds"/>.</summary>
    public static bool Verify(string secret, long unixSeconds, string deliveryId, string body, string? signatureHeader, long nowUnixSeconds, int toleranceSeconds = ControlSignature.ToleranceSeconds)
    {
        if (signatureHeader is null || Math.Abs(nowUnixSeconds - unixSeconds) > toleranceSeconds) return false;
        var expected = Encoding.ASCII.GetBytes(Sign(secret, unixSeconds, deliveryId, body));
        return CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(signatureHeader));
    }
}
