using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Infrastructure.Reminders;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Api.Reminders;

/// <summary>Which addresses a webhook may never reach (SSRF): loopback, private, link-local, CGNAT, multicast, reserved, unspecified. IPv4-mapped IPv6 is judged as IPv4.</summary>
public static class WebhookAddressPolicy
{
    public static bool IsBlocked(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6None)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal || ip.IsIPv6Multicast || ip.IsIPv6Teredo;
        var b = ip.GetAddressBytes();
        return b[0] is 0 or 10 or >= 224                                   // this-network, private, multicast + reserved
            || (b[0] == 100 && b[1] is >= 64 and <= 127)                    // CGNAT
            || (b[0] == 169 && b[1] == 254)                                 // link-local, cloud metadata
            || (b[0] == 172 && b[1] is >= 16 and <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 192 && b[1] == 0 && b[2] == 0)
            || (b[0] == 198 && b[1] is 18 or 19);
    }
}

/// <summary>
/// Optional team webhooks (PROJECT_SCOPE 5.5): a JSON POST to the team's WebhookDestination. Bounded (per-attempt timeout, attempt limit),
/// https only outside Development, no redirects, and no private targets unless <c>Notifications:Webhooks:AllowPrivateTargets</c> (checked
/// before sending, and again at connect time by the named client's handler so a DNS change cannot slip past). The URL is a secret (Slack and
/// Teams put the token in the path): it is never logged, audited or put in an alert; only the destination's name and host are.
/// A failure raises an alert (IAlertSink + SyncAlerts, source Webhook) and returns false; success is audited.
/// </summary>
public sealed class TeamWebhookSender(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IHttpClientFactory http, SyncAlertWriter syncAlerts,
    IAlertSink alerts, IConfiguration config, IHostEnvironment env, ILogger<TeamWebhookSender> log) : ITeamWebhookSender
{
    public const string ClientName = "team-webhook";

    public async Task<bool> SendAsync(WebhookNotice n, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var dest = await (from t in db.Set<Teams>().AsNoTracking() join d in db.Set<WebhookDestinations>() on t.WebhookDestinationId equals d.Id where t.Id == n.TeamId select new { d.Id, d.Name, d.Url, d.Kind }).SingleOrDefaultAsync(ct);
        if (dest is null) return false;   // the team has no channel: nothing to do, not an error

        var label = $"Webhook '{dest.Name}'";
        string? host = null;
        try
        {
            var uri = Validate(dest.Url);
            host = uri.Host;
            if (!config.GetValue("Notifications:Webhooks:AllowPrivateTargets", false)) await EnsurePublicAsync(uri, ct);
            var error = await PostAsync(uri, dest.Kind, n, ct);
            if (error is null)
            {
                db.Set<AuditEvents>().Add(new AuditEvents
                {
                    OccurredAt = Truncate(time.GetUtcNow().UtcDateTime), ReleaseTrainId = n.TrainId, EntityType = "WebhookDestination", EntityId = dest.Id, Action = "Delivered",
                    AfterJson = JsonSerializer.Serialize(new { teamId = n.TeamId, n.Kind, n.EntityType, n.EntityId, level = n.EscalationLevel }),
                });
                await db.SaveChangesAsync(ct);
                return true;
            }
            await FailAsync(dest.Id, $"{label} ({host}) failed: {error}", n.TrainId, ct);
        }
        catch (WebhookRefused ex)
        {
            await FailAsync(dest.Id, $"{label}{(host is null ? "" : $" ({host})")} refused: {ex.Message}", n.TrainId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Type only: the message of a transport exception can carry the request URI, which is the secret.
            await FailAsync(dest.Id, $"{label}{(host is null ? "" : $" ({host})")} failed: {ex.GetType().Name}", n.TrainId, ct);
        }
        return false;
    }

    private sealed class WebhookRefused(string message) : Exception(message);

    private Uri Validate(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) throw new WebhookRefused("the destination is not a valid absolute URL");
        if (uri.Scheme != Uri.UriSchemeHttps && !(env.IsDevelopment() && uri.Scheme == Uri.UriSchemeHttp)) throw new WebhookRefused("only https destinations are allowed");
        if (!string.IsNullOrEmpty(uri.UserInfo)) throw new WebhookRefused("credentials in the URL are not allowed");
        return uri;
    }

    private static async Task EnsurePublicAsync(Uri uri, CancellationToken ct)
    {
        IPAddress[] addrs;
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var literal)) addrs = [literal];
        else
        {
            try { addrs = await Dns.GetHostAddressesAsync(uri.Host, ct); }
            catch (SocketException) { throw new WebhookRefused("the host name does not resolve"); }
        }
        if (addrs.Length == 0 || addrs.Any(WebhookAddressPolicy.IsBlocked))
            throw new WebhookRefused("the destination is a private or local address (set Notifications:Webhooks:AllowPrivateTargets to allow it)");
    }

    /// <summary>Returns null on success, else a short reason without the URL.</summary>
    private async Task<string?> PostAsync(Uri uri, string kind, WebhookNotice n, CancellationToken ct)
    {
        var attempts = Math.Clamp(config.GetValue("Notifications:Webhooks:MaxAttempts", 3), 1, 5);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(config.GetValue("Notifications:Webhooks:TimeoutSeconds", 10), 1, 60));
        var delay = TimeSpan.FromMilliseconds(Math.Clamp(config.GetValue("Notifications:Webhooks:RetryDelayMs", 500), 0, 30_000));
        var body = JsonSerializer.Serialize(kind == "Generic"
            ? new { kind = n.Kind, entityType = n.EntityType, entityId = n.EntityId, escalationLevel = n.EscalationLevel, message = n.Message, trainId = n.TrainId, at = time.GetUtcNow().UtcDateTime }
            : (object)new { text = n.Message });   // Teams and Slack incoming webhooks both take {"text": ...}

        var last = "no attempt was made";
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timer.CancelAfter(timeout);
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                using var res = await http.CreateClient(ClientName).SendAsync(req, timer.Token);
                if (res.IsSuccessStatusCode) return null;
                last = $"HTTP {(int)res.StatusCode}";
                if ((int)res.StatusCode is not (408 or 429 or >= 500)) return $"{last} (not retried)";   // a 4xx will not get better
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { last = $"no answer within {timeout.TotalSeconds:0}s"; }
            catch (HttpRequestException ex) { last = ex.GetType().Name + (ex.StatusCode is { } sc ? $" {(int)sc}" : ""); }
            if (attempt < attempts && delay > TimeSpan.Zero) await Task.Delay(delay * attempt, ct);
        }
        return $"{last} after {attempts} attempt{(attempts == 1 ? "" : "s")}";
    }

    private async Task FailAsync(string destinationId, string message, string? trainId, CancellationToken ct)
    {
        log.LogError("Team webhook delivery failed: {Message}", message);
        await alerts.RaiseAsync("Webhook", "DeliveryFailed", destinationId, message, ct);
        await syncAlerts.RaiseAsync("Webhook", "DeliveryFailed", destinationId, message, trainId, ct);
    }

    private static DateTime Truncate(DateTime t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
}
