using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using ReleaseMgmt.Domain.Ai;

namespace ReleaseMgmt.Infrastructure.Ai;

/// <summary>
/// The provider key (D12, CLAUDE.md rule 11): protected with ASP.NET Data Protection in one file under <c>Ai:CredentialsDirectory</c>; never in SQLite, appsettings or a log, and never returned
/// by an endpoint. Like the connector credentials it depends on the key ring (part of the DR runbook): if the ring changed, the key reads as absent and the status says so, so the person enters it again.
/// </summary>
public sealed class DataProtectionAiKeyStore(IDataProtectionProvider provider, IOptionsMonitor<AiOptions> options) : IAiKeyStore
{
    public const string Purpose = "ReleaseMgmt.Ai.ProviderKey.v1";
    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string Dir => options.CurrentValue.CredentialsDirectory;
    private string FilePath => Path.Combine(Dir, "provider.key");

    public async Task<bool> HasKeyAsync(CancellationToken ct = default) => await GetKeyAsync(ct) is not null;

    public async Task<string?> GetKeyAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(FilePath)) return null;
            var blob = await File.ReadAllTextAsync(FilePath, ct);
            try { return _protector.Unprotect(blob); }
            catch (CryptographicException) { return null; }   // key ring changed: enter the key again (the message never carries the key)
        }
        finally { _gate.Release(); }
    }

    public async Task SetKeyAsync(string key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("The key is empty", nameof(key));
        var blob = _protector.Protect(key.Trim());
        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = FilePath + ".tmp";
            await File.WriteAllTextAsync(tmp, blob, ct);
            File.Move(tmp, FilePath, overwrite: true);
            if (!OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { /* best effort: the content is encrypted anyway */ }
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> ClearKeyAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { var had = File.Exists(FilePath); if (had) File.Delete(FilePath); return had; }
        finally { _gate.Release(); }
    }
}
