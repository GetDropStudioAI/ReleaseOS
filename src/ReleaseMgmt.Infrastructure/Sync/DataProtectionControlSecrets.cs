using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Infrastructure.Comms;

namespace ReleaseMgmt.Infrastructure.Sync;

/// <summary>
/// Signing keys of control webhooks (REOS-112; <see cref="IControlSigningSecrets"/>): one file per destination under <c>{Sync:Credentials:Directory}/control-webhooks/{workspace}/</c>,
/// protected with ASP.NET Data Protection and a purpose that names the workspace and the destination, so a file copied to another destination or workspace cannot be read there.
/// A key is <c>whs_</c> plus 43 base64url characters (256 bits).
/// </summary>
public sealed class DataProtectionControlSecrets(IDataProtectionProvider provider, SyncOptions options) : IControlSigningSecrets
{
    public const string Purpose = "ReleaseMgmt.ControlWebhook.Signing.v1";
    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static string Workspace => WorkspaceScope.Current ?? WorkspaceIds.Default;

    private string PathFor(string destinationId)
    {
        if (destinationId.Length is 0 or > 64 || !destinationId.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) throw new ArgumentException("Not a destination id", nameof(destinationId));
        if (!Workspace.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) throw new InvalidOperationException("The workspace id cannot name a folder");
        return Path.Combine(options.CredentialsDirectory, "control-webhooks", Workspace, destinationId + ".secret");
    }

    private IDataProtector ProtectorFor(string destinationId) => _protector.CreateProtector("workspace:" + Workspace).CreateProtector("destination:" + destinationId);

    public Task<bool> ExistsAsync(string destinationId, CancellationToken ct = default) => Task.FromResult(File.Exists(PathFor(destinationId)));

    public async Task<string?> GetAsync(string destinationId, CancellationToken ct = default)
    {
        var path = PathFor(destinationId);
        string blob;
        await _gate.WaitAsync(ct);
        try { if (!File.Exists(path)) return null; blob = await File.ReadAllTextAsync(path, ct); }
        finally { _gate.Release(); }
        try { return ProtectorFor(destinationId).Unprotect(blob); }
        catch (CryptographicException) { throw new InvalidOperationException("The control webhook's signing key cannot be read (the Data Protection key ring changed); rotate it on the environment"); }
    }

    public async Task<string> RotateAsync(string destinationId, CancellationToken ct = default)
    {
        var path = PathFor(destinationId);
        var secret = "whs_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var blob = ProtectorFor(destinationId).Protect(secret);
        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            await File.WriteAllTextAsync(tmp, blob, ct);
            File.Move(tmp, path, overwrite: true);
            if (!OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { /* best effort: the content is encrypted anyway */ }
            }
        }
        finally { _gate.Release(); }
        return secret;
    }

    public async Task<bool> ClearAsync(string destinationId, CancellationToken ct = default)
    {
        var path = PathFor(destinationId);
        await _gate.WaitAsync(ct);
        try { var had = File.Exists(path); if (had) File.Delete(path); return had; }
        finally { _gate.Release(); }
    }
}
