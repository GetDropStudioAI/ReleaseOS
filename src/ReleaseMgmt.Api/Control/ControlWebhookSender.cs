using System.Net;
using System.Net.Sockets;
using System.Text;
using ReleaseMgmt.Api.Reminders;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Comms;

namespace ReleaseMgmt.Api.Control;

/// <summary>
/// Posts a signed control call (pause, resume, promote, rollback; REOS-112) to the environment's allowlisted webhook destination. Every protection of the other outbound webhooks applies: https
/// only, no credentials in the URL, no redirects, private, loopback and link-local targets refused (checked before sending and again at connect time by the named client's handler, so a DNS
/// change cannot slip past; <c>Control:Webhooks:AllowPrivateTargets</c> lifts it for a deployment tool on the same network), a hard timeout (<c>Control:Webhooks:TimeoutSeconds</c>, default 10),
/// the status code decides and the response body is never read. Exactly one attempt: a rollback must not be repeated blindly. The URL is a secret: it is never logged or returned; only the
/// destination's host, the status code and a reason are. Headers: <c>X-ReleaseOS-Delivery</c> (the action's id: a receiver that remembers the ids it has seen refuses a replay),
/// <c>X-ReleaseOS-Timestamp</c> (Unix seconds: refuse anything older than <see cref="ControlSignature.ToleranceSeconds"/>) and <c>X-ReleaseOS-Signature</c> (<see cref="ControlSignature"/>).
/// Never throws for delivery problems: returns a failed result.
/// </summary>
public sealed class ControlWebhookSender(IHttpClientFactory http, IConfiguration config, ILogger<ControlWebhookSender> log, IWebhookUrlVault urlVault, OutboundAddressPolicy addressPolicy) : IControlWebhookSender
{
    public const string ClientName = "control-webhook";
    public const double DefaultTimeoutSeconds = 10;

    public async Task<ControlSendResult> SendAsync(CommWebhookTarget target, ControlDelivery delivery, CancellationToken ct)
    {
        var host = target.Host;
        try
        {
            string url;
            try { url = urlVault.Unprotect(target.ProtectedUrl); }   // Q-053e: the address exists in clear only here, for this call
            catch (WebhookUrlUnreadableException ex) { return new(false, null, host, ex.Message); }
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return new(false, null, host, "the destination is not a valid absolute URL");
            if (uri.Scheme != Uri.UriSchemeHttps) return new(false, null, host, "only https destinations are allowed");
            if (!string.IsNullOrEmpty(uri.UserInfo)) return new(false, null, host, "credentials in the URL are not allowed");
            if (!config.GetValue("Control:Webhooks:AllowPrivateTargets", false))
            {
                var blocked = await BlockedAsync(uri, ct);
                if (blocked is not null) return new(false, null, host, blocked);
            }
            var timeout = TimeSpan.FromSeconds(Math.Clamp(config.GetValue("Control:Webhooks:TimeoutSeconds", DefaultTimeoutSeconds), 0.1, 30));
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timer.CancelAfter(timeout);
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(delivery.Body, new UTF8Encoding(false), "application/json") };
                req.Headers.TryAddWithoutValidation(ControlSignature.DeliveryHeader, delivery.DeliveryId);
                req.Headers.TryAddWithoutValidation(ControlSignature.TimestampHeader, delivery.UnixSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
                req.Headers.TryAddWithoutValidation(ControlSignature.SignatureHeader, delivery.Signature);
                // Headers only: the status decides, and the body is never read or buffered (SEC-C3).
                using var res = await http.CreateClient(ClientName).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timer.Token);
                var code = (int)res.StatusCode;
                if (res.IsSuccessStatusCode) return new(true, code, host, null);
                return new(false, code, host, code is >= 300 and < 400 ? $"HTTP {code} (redirects are not followed)" : $"HTTP {code}");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(false, null, host, $"no answer within {timeout.TotalSeconds:0.##}s"); }
            catch (HttpRequestException ex) { return new(false, null, host, ex.GetType().Name + (ex.StatusCode is { } sc ? $" {(int)sc}" : "")); }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Type only: the message of a transport exception can carry the request URI, which is the secret.
            log.LogError("Control webhook '{Destination}' failed with {Type}", target.Name, ex.GetType().Name);
            return new(false, null, host, "failed: " + ex.GetType().Name);
        }
    }

    private async Task<string?> BlockedAsync(Uri uri, CancellationToken ct)
    {
        IPAddress[] addrs;
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var literal)) addrs = [literal];
        else
        {
            try { addrs = await Dns.GetHostAddressesAsync(uri.Host, ct); }
            catch (SocketException) { return "the host name does not resolve"; }
        }
        return addrs.Length == 0 || addrs.Any(addressPolicy.IsBlocked)
            ? "the destination is a private or local address (Control:Webhooks:AllowPrivateTargets allows it)" : null;
    }
}
