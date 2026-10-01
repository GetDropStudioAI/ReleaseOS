using System.Net;

namespace ReleaseMgmt.Infrastructure.Sync;

/// <summary>
/// Which base URLs a connector may call (SSRF, like the team webhooks): absolute, at most 2048 characters with no whitespace or control character,
/// https outside Development, no user-info, query or fragment, host and port on <c>Sync:AllowedHosts</c> (see <see cref="SyncOptions"/>; outside Development it
/// defaults to Atlassian Cloud and ServiceNow on 443), and no literal
/// private/loopback address unless <c>Sync:AllowPrivateTargets</c>. The host is judged in the form the HTTP handler connects to (<see cref="Uri.IdnHost"/>),
/// so fullwidth or enclosed digits cannot dress an address up as a name (SEC-C2).
/// A host name is vetted again when the socket connects (see AddSyncEngine), so a DNS change cannot slip past.
/// </summary>
public static class ConnectorUrlPolicy
{
    public const int MaxUrlLength = 2048;

    /// <summary>Null when the URL is acceptable, else a readable reason.</summary>
    public static string? Validate(string? url, bool isDevelopment, SyncOptions o, Func<IPAddress, bool>? isBlockedAddress = null)
    {
        url = url?.Trim();
        if (string.IsNullOrEmpty(url)) return "The base URL must be an absolute URL such as https://example.atlassian.net";
        if (url.Length > MaxUrlLength) return $"The base URL is longer than {MaxUrlLength} characters";
        if (url.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) return "The base URL contains spaces or control characters";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "The base URL must be an absolute URL such as https://example.atlassian.net";
        if (uri.Scheme != Uri.UriSchemeHttps && !(isDevelopment && uri.Scheme == Uri.UriSchemeHttp)) return "The base URL must use https";
        if (!string.IsNullOrEmpty(uri.UserInfo)) return "The base URL must not contain credentials";
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return "The base URL must not contain a query or fragment";
        // A private literal says so first (the more specific reason); then the host list.
        if (!o.AllowPrivateTargets && isBlockedAddress is not null && LiteralAddress(uri) is { } ip && isBlockedAddress(ip))
            return "The base URL is a private or local address (set Sync:AllowPrivateTargets to allow it)";
        if (o.AllowedHosts.Length > 0 && !o.AllowedHosts.Any(p => EntryMatches(uri, p)))
            return o.AllowedHosts.Any(p => HostMatches(uri.IdnHost, SplitEntry(p).Host))
                ? $"Port {uri.Port} of {uri.IdnHost} is not on the allowed list (Sync:AllowedHosts: {string.Join(", ", o.AllowedHosts)}). Add \"{uri.IdnHost}:{uri.Port}\" to Sync:AllowedHosts to allow it"
                : $"The host {uri.IdnHost} is not on the allowed list (Sync:AllowedHosts: {string.Join(", ", o.AllowedHosts)}). Ask the administrator to add it to Sync:AllowedHosts";
        return null;
    }

    /// <summary>
    /// The IP address a URL's host denotes, or null for a name. Judged first as the handler will connect to it (<see cref="Uri.IdnHost"/>: "１２７.０.０.１"
    /// and "①②⑦.0.0.1" are DNS names to <see cref="Uri.HostNameType"/> but 127.0.0.1 on the wire), then as written; an IPv6 zone is ignored.
    /// </summary>
    public static IPAddress? LiteralAddress(Uri uri)
    {
        foreach (var form in new[] { uri.IdnHost, uri.Host, uri.DnsSafeHost })
        {
            var s = form.Trim('[', ']');
            var zone = s.IndexOf('%');
            if (zone >= 0) s = s[..zone];
            if (IPAddress.TryParse(s, out var ip)) return ip;
        }
        return null;
    }

    /// <summary>An allowlist entry: host or <c>*.suffix</c>, optionally <c>:port</c>; without a port it allows the scheme's default port only.</summary>
    public static bool EntryMatches(Uri uri, string entry)
    {
        var (host, port) = SplitEntry(entry);
        return HostMatches(uri.IdnHost, host) && (port is int p ? uri.Port == p : uri.IsDefaultPort);
    }

    public static (string Host, int? Port) SplitEntry(string entry)
    {
        var colon = entry.LastIndexOf(':');
        return colon > 0 && !entry.EndsWith(']') && int.TryParse(entry[(colon + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port)
            ? (entry[..colon], port) : (entry, null);
    }

    public static bool HostMatches(string host, string pattern) =>
        pattern.StartsWith("*.", StringComparison.Ordinal)
            ? host.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase) && host.Length > pattern.Length - 1
            : string.Equals(host, pattern, StringComparison.OrdinalIgnoreCase);

    /// <summary>The URL without a trailing slash, ready to append "/rest/..." to.</summary>
    public static Uri Normalize(string url) => new(url.Trim().TrimEnd('/') + "/", UriKind.Absolute);
}
