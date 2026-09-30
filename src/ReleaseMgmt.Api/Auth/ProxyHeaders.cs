using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace ReleaseMgmt.Api.Auth;

/// <summary>
/// SEC-E5: honour X-Forwarded-For and X-Forwarded-Proto from trusted proxies only, so the app judges the client, not the TLS-terminating proxy in front of
/// it (runbook section 2). Trusted by default: loopback (a proxy on the same host). Others by <c>Proxy:KnownProxies</c> (addresses) and
/// <c>Proxy:KnownNetworks</c> (CIDR). A header from anyone else is ignored. A bad value stops the app at start-up with the key named (rule 8).
/// </summary>
public static class ProxyHeaders
{
    public static IServiceCollection AddProxyHeaders(this IServiceCollection services, IConfiguration config) =>
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.ForwardLimit = 1;   // one proxy hop: the address the trusted proxy saw
            foreach (var v in config.GetSection("Proxy:KnownProxies").Get<string[]>() ?? [])
                o.KnownProxies.Add(IPAddress.TryParse(v, out var ip) ? ip : throw new InvalidOperationException($"Proxy:KnownProxies has '{v}', which is not an IP address"));
            foreach (var v in config.GetSection("Proxy:KnownNetworks").Get<string[]>() ?? [])
                o.KnownIPNetworks.Add(System.Net.IPNetwork.TryParse(v, out var n) ? n : throw new InvalidOperationException($"Proxy:KnownNetworks has '{v}', which is not a CIDR network such as 10.0.0.0/24"));
        });
}
