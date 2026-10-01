using System.Net;
using System.Net.Sockets;

namespace ReleaseMgmt.Api.Reminders;

/// <summary>
/// The address rule every outbound call uses (connectors, team webhooks, comm webhooks; save time and connect time): <see cref="WebhookAddressPolicy"/>
/// plus the operator's NAT64 network-specific prefixes (REOS-77, Q-SEC-C1, decided 2026-09-30). An address inside one of
/// <c>Sync:Nat64Prefixes</c> is judged by the IPv4 address in its last 32 bits, exactly like the well-known prefix <c>64:ff9b::/96</c>, so
/// <c>&lt;prefix&gt;::10.0.0.5</c> is refused and <c>&lt;prefix&gt;::104.18.0.1</c> is allowed. One list serves connectors and webhooks: the translator is a
/// property of the network, not of the caller. Registered as a singleton; a bad value stops the start-up naming the key (rule 8).
/// </summary>
public sealed class OutboundAddressPolicy
{
    public const string Nat64PrefixesKey = "Sync:Nat64Prefixes";

    /// <summary>No operator prefixes: the fixed rules only.</summary>
    public static readonly OutboundAddressPolicy Default = new([]);

    /// <summary>The first 12 bytes of each configured /96 prefix.</summary>
    public IReadOnlyList<byte[]> Nat64Prefixes { get; }

    public OutboundAddressPolicy(IReadOnlyList<byte[]> nat64Prefixes) => Nat64Prefixes = nat64Prefixes;

    public bool IsBlocked(IPAddress ip) => WebhookAddressPolicy.IsBlocked(ip, Nat64Prefixes);

    /// <summary>
    /// Reads <c>Sync:Nat64Prefixes</c>: a list (array entries <c>Sync:Nat64Prefixes:0</c>, ... or one value separated by commas, semicolons or spaces) of IPv6
    /// prefixes written <c>2001:db8:64::/96</c>. Each must be exactly /96 with its last 32 bits zero and bits 64-71 zero (RFC 6052 section 2.2). Anything else throws
    /// <see cref="InvalidOperationException"/> naming the key and the value.
    /// </summary>
    public static OutboundAddressPolicy From(IConfiguration config)
    {
        var section = config.GetSection(Nat64PrefixesKey);
        var values = section.GetChildren().Select(c => c.Value).Where(v => v is not null).Select(v => v!).ToList();
        if (values.Count == 0 && section.Value is { } single)
            values = [.. single.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        var prefixes = new List<byte[]>();
        foreach (var v in values.Select(v => v.Trim()).Where(v => v.Length > 0))
            prefixes.Add(Parse(v) is { } p ? p : throw new InvalidOperationException(
                $"{Nat64PrefixesKey} holds '{v}', which is not a NAT64 /96 prefix. Write each prefix like 2001:db8:64::/96 (IPv6, exactly /96, the last 32 bits and bits 64-71 zero, RFC 6052)"));
        return prefixes.Count == 0 ? Default : new OutboundAddressPolicy(prefixes);
    }

    private static byte[]? Parse(string s)
    {
        var slash = s.IndexOf('/');
        if (slash < 0 || s[(slash + 1)..] != "96") return null;
        if (!IPAddress.TryParse(s[..slash], out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6 || s[..slash].Contains('%')) return null;
        var b = ip.GetAddressBytes();
        if (b.AsSpan(12, 4).IndexOfAnyExcept((byte)0) >= 0 || b[8] != 0) return null;   // host part zero; RFC 6052 "u" octet zero
        return b[..12];
    }
}
