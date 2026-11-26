using System.Net;
using System.Net.Sockets;
using System.Text;
using ReleaseMgmt.Api.Reminders;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Comms;
using ReleaseMgmt.Infrastructure.Webhooks;

namespace ReleaseMgmt.Api.Webhooks;

/// <summary>The Api's one address rule, as the Infrastructure seam.</summary>
public sealed class OutboundAddressGuard(OutboundAddressPolicy policy) : IOutboundAddressGuard
{
    public bool IsBlocked(IPAddress address) => policy.IsBlocked(address);
}

/// <summary>
/// Posts one signed event to a subscription's address (REOS-164). The protections of <c>ControlWebhookSender</c> and <c>CommWebhookSender</c>: https only, no credentials in the URL, no redirects (a redirect is a second,
/// unchecked destination), private, loopback and link-local targets refused before sending and again when the socket connects (the named client's handler resolves and vets the address it then uses, so a DNS change cannot slip
/// past; <c>Webhooks:Out:AllowPrivateTargets</c> lifts it), a hard timeout (<c>Webhooks:Out:TimeoutSeconds</c>), the status code decides and the response body is never read. The allow-list is judged when the subscription is saved
/// and again here (an administrator who removes a host stops posting to it at once). One attempt per call: <see cref="WebhookDeliveryProcessor"/> owns retries. The address is a secret: it is never logged or returned, only the
/// host, the status code and a reason are. Never throws for delivery problems.
/// </summary>
public sealed class OutboundWebhookSender(IHttpClientFactory http, WebhookOutOptions options, ILogger<OutboundWebhookSender> log, IWebhookUrlVault urlVault, OutboundAddressPolicy addressPolicy) : IWebhookSender
{
    public const string ClientName = "outbound-webhook";

    public async Task<WebhookSendResult> SendAsync(WebhookTarget target, WebhookRequest request, CancellationToken ct)
    {
        try
        {
            string url;
            try { url = urlVault.Unprotect(target.ProtectedUrl); }   // Q-053e: the address exists in clear only here, for this call
            catch (WebhookUrlUnreadableException ex) { return new(false, null, ex.Message); }
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return new(false, null, "the address is not a valid absolute URL");
            if (uri.Scheme != Uri.UriSchemeHttps) return new(false, null, "only https addresses are allowed");
            if (!string.IsNullOrEmpty(uri.UserInfo)) return new(false, null, "credentials in the address are not allowed");
            if (options.Closed) return new(false, null, "no host is on the outbound allow-list (Webhooks:Out:AllowedHosts)");
            if (!options.Open && !options.AllowedHosts.Any(p => ReleaseMgmt.Infrastructure.Sync.ConnectorUrlPolicy.EntryMatches(uri, p)))
                return new(false, null, $"the host {target.Host} is no longer on the outbound allow-list (Webhooks:Out:AllowedHosts)");
            if (!options.AllowPrivateTargets)
            {
                var blocked = await BlockedAsync(uri, ct);
                if (blocked is not null) return new(false, null, blocked);
            }
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timer.CancelAfter(options.Timeout);
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(request.Body, new UTF8Encoding(false), "application/json") };
                req.Headers.UserAgent.ParseAdd("ReleaseOS-Webhooks/1");
                req.Headers.TryAddWithoutValidation(ControlSignature.DeliveryHeader, request.DeliveryId);
                req.Headers.TryAddWithoutValidation(ControlSignature.TimestampHeader, request.UnixSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
                req.Headers.TryAddWithoutValidation(ControlSignature.SignatureHeader, request.Signature);
                req.Headers.TryAddWithoutValidation(WebhookSignature.EventHeader, request.EventType);
                req.Headers.TryAddWithoutValidation(WebhookSignature.SecretVersionHeader, request.SecretVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
                // Headers only: the status decides, and the body is never read or buffered (SEC-C3).
                using var res = await http.CreateClient(ClientName).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timer.Token);
                var code = (int)res.StatusCode;
                if (res.IsSuccessStatusCode) return new(true, code, null);
                return new(false, code, code is >= 300 and < 400 ? $"HTTP {code} (redirects are not followed)" : $"HTTP {code}");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(false, null, $"no answer within {options.Timeout.TotalSeconds:0.##}s"); }
            catch (HttpRequestException ex) { return new(false, null, ex.GetType().Name + (ex.StatusCode is { } sc ? $" {(int)sc}" : "")); }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Type only: the message of a transport exception can carry the request URI, which is the secret.
            log.LogError("Outbound webhook '{Subscription}' failed with {Type}", target.Name, ex.GetType().Name);
            return new(false, null, "failed: " + ex.GetType().Name);
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
        return addrs.Length == 0 || addrs.Any(addressPolicy.IsBlocked) ? "the address is a private or local target (Webhooks:Out:AllowPrivateTargets allows it)" : null;
    }
}
