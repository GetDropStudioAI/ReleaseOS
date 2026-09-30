using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using ReleaseMgmt.Domain.Sync;

namespace ReleaseMgmt.Infrastructure.Sync;

/// <summary>
/// Connector credentials (D12, CLAUDE.md rule 11): protected with ASP.NET Data Protection and kept in one file per connector under
/// <c>Sync:Credentials:Directory</c>, never in SQLite (the schema comment on ConnectorState says the same) and never in config. Protected on write,
/// unprotected in memory only when a cycle needs them, and never returned, logged or audited. The key ring is part of the DR runbook (D12):
/// without it the files cannot be read and the connector reports AuthFailed until the credentials are entered again.
/// </summary>
public sealed class DataProtectionCredentialStore(IDataProtectionProvider provider, SyncOptions options) : ICredentialStore
{
    public const string Purpose = "ReleaseMgmt.Connector.Credentials.v1";
    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);
    private readonly SemaphoreSlim _gate = new(1, 1);

    private sealed record Payload(string Kind, string Username, string Secret);

    private string PathFor(string source)
    {
        if (!ExternalKeys.Sources.Contains(source)) throw new ArgumentException("Unknown source system", nameof(source));
        return Path.Combine(options.CredentialsDirectory, source + ".cred");
    }

    public async Task SetAsync(string source, ConnectorCredentials c, CancellationToken ct = default)
    {
        var path = PathFor(source);
        var blob = _protector.Protect(JsonSerializer.Serialize(new Payload(c.Kind, c.Username, c.Secret)));
        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(options.CredentialsDirectory);
            var tmp = path + ".tmp";
            await File.WriteAllTextAsync(tmp, blob, ct);
            File.Move(tmp, path, overwrite: true);
            TryRestrict(path);
        }
        finally { _gate.Release(); }
    }

    public async Task<ConnectorCredentials?> GetAsync(string source, CancellationToken ct = default)
    {
        var p = await ReadAsync(source, ct);
        return p is null ? null : new ConnectorCredentials(p.Kind, p.Username, p.Secret);
    }

    public async Task<string?> KindAsync(string source, CancellationToken ct = default) => (await ReadAsync(source, ct, tolerant: true))?.Kind;

    public async Task<bool> ClearAsync(string source, CancellationToken ct = default)
    {
        var path = PathFor(source);
        await _gate.WaitAsync(ct);
        try { var had = File.Exists(path); if (had) File.Delete(path); return had; }
        finally { _gate.Release(); }
    }

    private async Task<Payload?> ReadAsync(string source, CancellationToken ct, bool tolerant = false)
    {
        var path = PathFor(source);
        string blob;
        await _gate.WaitAsync(ct);
        try { if (!File.Exists(path)) return null; blob = await File.ReadAllTextAsync(path, ct); }
        finally { _gate.Release(); }
        try { return JsonSerializer.Deserialize<Payload>(_protector.Unprotect(blob)); }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            if (tolerant) return null;   // listing must not fail because the key ring changed; the cycle reports it
            throw new ConnectorException(ConnectorErrorKind.Auth, $"The stored {source} credentials cannot be read (the Data Protection key ring changed); enter them again");
        }
    }

    private static void TryRestrict(string path)
    {
        if (OperatingSystem.IsWindows()) return;   // the profile ACL applies; POSIX modes do not exist there
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { /* best effort: the content is encrypted anyway */ }
    }
}
