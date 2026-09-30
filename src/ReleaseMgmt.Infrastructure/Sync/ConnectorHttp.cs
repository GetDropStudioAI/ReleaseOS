using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ReleaseMgmt.Domain.Sync;

namespace ReleaseMgmt.Infrastructure.Sync;

/// <summary>
/// The one bounded HTTP path every connector uses: read-only GET (plus the OAuth token POST), per-call timeout, response size cap, no redirects
/// (the named client is built with AllowAutoRedirect=false, and a 3xx is an error here), and every failure mapped to a <see cref="ConnectorException"/>
/// whose message carries the status class only, never the URL, headers or body (a credential or a customer's text could be in them).
/// </summary>
public sealed class ConnectorHttp(IHttpClientFactory http, SyncOptions options, TimeProvider time, string source, Uri baseUri)
{
    public const string ClientName = "sync-connector";

    public async Task<JsonDocument> SendJsonAsync(HttpMethod method, string relative, HttpContent? content, string? authorization, CancellationToken ct)
    {
        var uri = new Uri(baseUri, relative);
        if (uri.Scheme != baseUri.Scheme || !string.Equals(uri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase) || uri.Port != baseUri.Port)
            throw new ConnectorException(ConnectorErrorKind.Network, "The request would leave the configured host");

        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(options.Timeout);
        try
        {
            using var req = new HttpRequestMessage(method, uri) { Content = content };
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (authorization is not null) req.Headers.TryAddWithoutValidation("Authorization", authorization);
            using var res = await http.CreateClient(ClientName).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timer.Token);

            var status = (int)res.StatusCode;
            if (ConnectorErrorClassifier.FromHttpStatus(status) is ConnectorErrorKind bad)
                throw new ConnectorException(bad, Describe(bad, status), status, bad == ConnectorErrorKind.RateLimited ? RetryAfter(res) : null);

            if (res.Content.Headers.ContentLength is long len && len > options.MaxResponseBytes)
                throw new ConnectorException(ConnectorErrorKind.Parse, $"{source} answered with more than {options.MaxResponseBytes} bytes");
            await using var body = await res.Content.ReadAsStreamAsync(timer.Token);
            var buf = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int n;
            while ((n = await body.ReadAsync(chunk, timer.Token)) > 0)
            {
                buf.Write(chunk, 0, n);
                if (buf.Length > options.MaxResponseBytes) throw new ConnectorException(ConnectorErrorKind.Parse, $"{source} answered with more than {options.MaxResponseBytes} bytes");
            }
            buf.Position = 0;
            return await JsonDocument.ParseAsync(buf, cancellationToken: timer.Token);
        }
        catch (ConnectorException) { throw; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ConnectorException(ConnectorErrorKind.Timeout, $"{source} did not answer within {options.Timeout.TotalSeconds:0.##} s");
        }
        catch (JsonException) { throw new ConnectorException(ConnectorErrorKind.Parse, $"{source} answered with something that is not JSON"); }
        catch (HttpRequestException ex)
        {
            // Type and status only: a transport exception's message can carry the request URI.
            throw new ConnectorException(ConnectorErrorKind.Network, $"Could not reach {source} ({ex.GetType().Name}{(ex.StatusCode is { } sc ? $" {(int)sc}" : "")})");
        }
    }

    public Task<JsonDocument> GetJsonAsync(string relative, string? authorization, CancellationToken ct) => SendJsonAsync(HttpMethod.Get, relative, null, authorization, ct);

    private string Describe(ConnectorErrorKind k, int status) => k switch
    {
        ConnectorErrorKind.Auth => $"{source} refused the credentials (HTTP {status})",
        ConnectorErrorKind.NotFound => $"{source} does not know that key (HTTP {status})",
        ConnectorErrorKind.RateLimited => $"{source} is rate limiting requests (HTTP 429)",
        ConnectorErrorKind.Timeout => $"{source} timed out (HTTP {status})",
        _ => status is >= 300 and < 400 ? $"{source} answered with a redirect (HTTP {status}); redirects are not followed" : $"{source} answered with HTTP {status}",
    };

    private TimeSpan? RetryAfter(HttpResponseMessage res)
    {
        var ra = res.Headers.RetryAfter;
        if (ra is null) return null;
        if (ra.Delta is TimeSpan d) return d < TimeSpan.Zero ? null : d;
        if (ra.Date is DateTimeOffset at) { var w = at - time.GetUtcNow(); return w < TimeSpan.Zero ? null : w; }
        return null;
    }

    public static string Basic(string user, string secret) => "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{user}:{secret}"));
}
