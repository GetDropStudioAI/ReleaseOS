using System.Net;

namespace ReleaseMgmt.Infrastructure.Sync;

/// <summary>
/// Which base URLs a connector may call (SSRF, like the team webhooks): absolute, https outside Development, no user-info, query or fragment,
/// host on <c>Sync:AllowedHosts</c> when that list is set, and no literal private/loopback address unless <c>Sync:AllowPrivateTargets</c>.
/// A host name is vetted again when the socket connects (see AddSyncEngine), so a DNS change cannot slip past.
/// </summary>
public static class ConnectorUrlPolicy
{
    /// <summary>Null when the URL is acceptable, else a readable reason.</summary>
    public static string? Validate(string? url, bool isDevelopment, SyncOptions o, Func<IPAddress, bool>? isBlockedAddress = null)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return "The base URL must be an absolute URL such as https://example.atlassian.net";
        if (uri.Scheme != Uri.UriSchemeHttps && !(isDevelopment && uri.Scheme == Uri.UriSchemeHttp)) return "The base URL must use https";
        if (!string.IsNullOrEmpty(uri.UserInfo)) return "The base URL must not contain credentials";
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return "The base URL must not contain a query or fragment";
        if (o.AllowedHosts.Length > 0 && !o.AllowedHosts.Any(p => HostMatches(uri.Host, p))) return $"The host {uri.Host} is not on the allowed list (Sync:AllowedHosts)";
        if (!o.AllowPrivateTargets && isBlockedAddress is not null && IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && isBlockedAddress(ip))
            return "The base URL is a private or local address (set Sync:AllowPrivateTargets to allow it)";
        return null;
    }

    public static bool HostMatches(string host, string pattern) =>
        pattern.StartsWith("*.", StringComparison.Ordinal)
            ? host.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase) && host.Length > pattern.Length - 1
            : string.Equals(host, pattern, StringComparison.OrdinalIgnoreCase);

    /// <summary>The URL without a trailing slash, ready to append "/rest/..." to.</summary>
    public static Uri Normalize(string url) => new(url.Trim().TrimEnd('/') + "/", UriKind.Absolute);
}
