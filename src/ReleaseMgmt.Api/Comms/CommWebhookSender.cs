using System.Net;
using System.Net.Sockets;
using System.Text;
using ReleaseMgmt.Api.Reminders;
using ReleaseMgmt.Infrastructure.Comms;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>
/// Posts a dispatched message to an allowlisted webhook. Mirrors TeamWebhookSender's rules: https only, no credentials in the URL, no redirects,
/// private/loopback/link-local targets refused (checked before sending and again at connect time by the named client's handler), a hard per-attempt
/// timeout (<c>Comms:Webhooks:TimeoutSeconds</c>, default 10) and NO automatic retry by default (<c>Comms:Webhooks:MaxAttempts</c>, default 1: a retry of a
/// message a person chose to send can post it twice). The URL is a secret: it is never logged or returned; only the destination name, host and a reason are.
/// Never throws for delivery problems: returns a failed result.
/// </summary>
public sealed class CommWebhookSender(IHttpClientFactory http, IConfiguration config, ILogger<CommWebhookSender> log) : ICommWebhookSender
{
    public const string ClientName = "comm-webhook";
    public const double DefaultTimeoutSeconds = 10;

    public async Task<CommWebhookResult> SendAsync(CommWebhookTarget target, string jsonBody, CancellationToken ct)
    {
        string host = "";
        try
        {
            if (!Uri.TryCreate(target.Url, UriKind.Absolute, out var uri)) return new(false, host, "the destination is not a valid absolute URL");
            host = uri.Host;
            if (uri.Scheme != Uri.UriSchemeHttps) return new(false, host, "only https destinations are allowed");
            if (!string.IsNullOrEmpty(uri.UserInfo)) return new(false, host, "credentials in the URL are not allowed");
            if (!config.GetValue("Comms:Webhooks:AllowPrivateTargets", false))
            {
                var blocked = await BlockedAsync(uri, ct);
                if (blocked is not null) return new(false, host, blocked);
            }
            var reason = await PostAsync(uri, jsonBody, ct);
            return new(reason is null, host, reason);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Type only: the message of a transport exception can carry the request URI, which is the secret.
            log.LogError("Comm webhook '{Destination}' failed with {Type}", target.Name, ex.GetType().Name);
            return new(false, host, "failed: " + ex.GetType().Name);
        }
    }

    private static async Task<string?> BlockedAsync(Uri uri, CancellationToken ct)
    {
        IPAddress[] addrs;
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var literal)) addrs = [literal];
        else
        {
            try { addrs = await Dns.GetHostAddressesAsync(uri.Host, ct); }
            catch (SocketException) { return "the host name does not resolve"; }
        }
        return addrs.Length == 0 || addrs.Any(WebhookAddressPolicy.IsBlocked)
            ? "the destination is a private or local address (Comms:Webhooks:AllowPrivateTargets allows it)" : null;
    }

    /// <summary>Returns null on success, else a short reason without the URL.</summary>
    private async Task<string?> PostAsync(Uri uri, string body, CancellationToken ct)
    {
        var attempts = Math.Clamp(config.GetValue("Comms:Webhooks:MaxAttempts", 1), 1, 3);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(config.GetValue("Comms:Webhooks:TimeoutSeconds", DefaultTimeoutSeconds), 0.1, 60));
        var last = "no attempt was made";
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timer.CancelAfter(timeout);
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(body, new UTF8Encoding(false), "application/json") };
                using var res = await http.CreateClient(ClientName).SendAsync(req, timer.Token);
                if (res.IsSuccessStatusCode) return null;
                last = (int)res.StatusCode is >= 300 and < 400 ? $"HTTP {(int)res.StatusCode} (redirects are not followed)" : $"HTTP {(int)res.StatusCode}";
                if ((int)res.StatusCode is not (408 or 429 or >= 500)) return last;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { last = $"no answer within {timeout.TotalSeconds:0.##}s"; }
            catch (HttpRequestException ex) { last = ex.GetType().Name + (ex.StatusCode is { } sc ? $" {(int)sc}" : ""); }
        }
        return attempts == 1 ? last : $"{last} after {attempts} attempts";
    }
}
