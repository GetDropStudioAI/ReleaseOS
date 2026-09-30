using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using ReleaseMgmt.Domain.Sync;

namespace ReleaseMgmt.Infrastructure.Sync;

/// <summary>Builds the connector for one source system from its stored base URL and its Data Protection credentials.</summary>
public interface IConnectorFactory
{
    /// <summary>Throws <see cref="ConnectorException"/>: Auth when no credentials are stored or they cannot be read, Network when the base URL is not allowed.</summary>
    Task<IConnector> CreateAsync(string source, string baseUrl, CancellationToken ct);
}

public sealed class ConnectorFactory(IHttpClientFactory http, ICredentialStore credentials, SyncOptions options, TimeProvider time, IHostEnvironment env, IConfiguration config,
    Func<IPAddress, bool>? isBlockedAddress = null) : IConnectorFactory
{
    public async Task<IConnector> CreateAsync(string source, string baseUrl, CancellationToken ct)
    {
        var problem = ConnectorUrlPolicy.Validate(baseUrl, env.IsDevelopment(), options, isBlockedAddress);
        if (problem is not null) throw new ConnectorException(ConnectorErrorKind.Network, $"The {source} base URL is not allowed: {problem}");
        var creds = await credentials.GetAsync(source, ct)
            ?? throw new ConnectorException(ConnectorErrorKind.Auth, $"No {source} credentials are stored; enter them on the Connectors screen");
        var h = new ConnectorHttp(http, options, time, source, ConnectorUrlPolicy.Normalize(baseUrl));
        return source switch
        {
            "Jira" => new JiraCloudConnector(h, creds),
            "ServiceNow" => new ServiceNowConnector(h, creds, time, (config["Connectors:ServiceNow:TokenPath"] ?? "oauth_token.do").TrimStart('/')),
            _ => throw new ConnectorException(ConnectorErrorKind.Network, $"Unknown source system {source}"),
        };
    }
}
