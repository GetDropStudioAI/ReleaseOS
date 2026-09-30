namespace ReleaseMgmt.Domain.Sync;

/// <summary>
/// What a read connector returns for one external key. Keys, states and dates only (OI-12): never a summary, description, comment or person.
/// <see cref="State"/> is the source system's own status name (Jira status, ServiceNow state such as "Implement").
/// </summary>
public sealed record ExternalStatus(
    string Key,
    string State,
    DateTime? PlannedStart = null,      // ServiceNow change start_date
    DateTime? PlannedEnd = null,        // ServiceNow change end_date
    DateOnly? ReleaseDate = null,       // Jira fix version releaseDate
    bool? Released = null);             // Jira fix version released flag

/// <summary>A read-only ITSM connector (D11). Implementations map every failure to a <see cref="ConnectorException"/>.</summary>
public interface IConnector
{
    string SourceSystem { get; }

    /// <summary>One bounded read of one key. Throws <see cref="ConnectorException"/>; never returns partial data.</summary>
    Task<ExternalStatus> FetchStatusAsync(string externalKey, CancellationToken ct);

    /// <summary>One cheap authenticated read used by "Test connection". Throws <see cref="ConnectorException"/>; returns nothing.</summary>
    Task PingAsync(CancellationToken ct);
}

/// <summary>Credentials as the connector needs them, in memory only. <c>ToString</c> never prints the secret.</summary>
public sealed record ConnectorCredentials(string Kind, string Username, string Secret)
{
    public const string ApiToken = "ApiToken", Basic = "Basic", OAuth = "OAuth";
    public static readonly string[] Kinds = [ApiToken, Basic, OAuth];
    public override string ToString() => $"ConnectorCredentials {{ Kind = {Kind}, Username = ***, Secret = *** }}";
}

/// <summary>Where credentials live: ASP.NET Data Protection, never SQLite or config (D12, CLAUDE.md rule 11). Values are never returned by any endpoint.</summary>
public interface ICredentialStore
{
    Task SetAsync(string sourceSystem, ConnectorCredentials credentials, CancellationToken ct = default);
    /// <summary>Null when none are stored. Throws <see cref="ConnectorException"/> (Auth) when the stored value cannot be unprotected (key ring lost).</summary>
    Task<ConnectorCredentials?> GetAsync(string sourceSystem, CancellationToken ct = default);
    /// <summary>The kind ("ApiToken", "Basic", "OAuth") of the stored credentials, or null. Reveals nothing secret.</summary>
    Task<string?> KindAsync(string sourceSystem, CancellationToken ct = default);
    Task<bool> ClearAsync(string sourceSystem, CancellationToken ct = default);
}
