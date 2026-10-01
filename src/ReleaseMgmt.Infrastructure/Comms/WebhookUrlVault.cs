using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using ReleaseMgmt.Infrastructure.Sync;

namespace ReleaseMgmt.Infrastructure.Comms;

/// <summary>A webhook address as it is stored: the Data Protection payload and the keyed hash that makes it unique (schema: WebhookDestinations).</summary>
public sealed record ProtectedWebhookUrl(string ProtectedUrl, string UrlHmac);

/// <summary>The stored address of a webhook destination cannot be read (the key ring changed or the row was edited by hand). The message never holds the URL.</summary>
public sealed class WebhookUrlUnreadableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Q-053e (decided 2026-09-30, REOS-73): a webhook URL is a secret (Teams and Slack put the token in the path), so <c>WebhookDestinations</c> holds it only as an
/// ASP.NET Data Protection payload (<see cref="Purpose"/>, one purpose for this table) and an HMAC-SHA256 of the normalised URL for the UNIQUE constraint and for
/// finding a destination by address. Only the senders call <see cref="Unprotect"/>; every screen, alert and audit row uses the <c>Host</c> column.
/// </summary>
public interface IWebhookUrlVault
{
    ProtectedWebhookUrl Protect(string normalisedUrl);

    /// <summary>The keyed hash of an address, as stored in <c>UrlHmac</c>.</summary>
    string Hmac(string normalisedUrl);

    /// <summary>The URL in clear, for the HTTP call only. Throws <see cref="WebhookUrlUnreadableException"/> when it cannot be decrypted.</summary>
    string Unprotect(string protectedUrl);

    /// <summary>Loads the HMAC key, creating it when it does not exist. True when it was created now: hashes stored before then must be recomputed (<see cref="WebhookUrlProtectionUpgrade"/>).</summary>
    bool EnsureHmacKey();
}

/// <summary>
/// The Data Protection implementation. The HMAC key is 32 random bytes kept, protected with the key ring, in <c>webhook-url.hmackey</c> beside the connector
/// credentials (<c>Sync:Credentials:Directory</c>, default <c>secrets/</c>): the key ring rotates every 90 days, so a hash keyed by it would change; this key
/// does not. It is created on first use; the runbook backs it up with <c>secrets/</c>.
/// </summary>
public sealed class DataProtectionWebhookUrlVault : IWebhookUrlVault
{
    public const string Purpose = "ReleaseMgmt.WebhookDestinations.Url.v1";
    public const string HmacKeyPurpose = "ReleaseMgmt.WebhookDestinations.UrlHmacKey.v1";
    public const string HmacKeyFile = "webhook-url.hmackey";

    private readonly IDataProtector _protector, _keyProtector;
    private readonly string _keyPath;
    private readonly Lock _gate = new();
    private byte[]? _key;
    private bool _created;

    public DataProtectionWebhookUrlVault(IDataProtectionProvider provider, SyncOptions options)
    {
        _protector = provider.CreateProtector(Purpose);
        _keyProtector = provider.CreateProtector(HmacKeyPurpose);
        _keyPath = Path.Combine(options.CredentialsDirectory, HmacKeyFile);
    }

    public bool EnsureHmacKey()
    {
        if (_key is not null) return false;
        Key();
        return _created;
    }

    public ProtectedWebhookUrl Protect(string normalisedUrl) => new(_protector.Protect(normalisedUrl), Hmac(normalisedUrl));

    public string Hmac(string normalisedUrl) => Convert.ToHexStringLower(HMACSHA256.HashData(Key(), Encoding.UTF8.GetBytes(normalisedUrl)));

    public string Unprotect(string protectedUrl)
    {
        try { return _protector.Unprotect(protectedUrl); }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            throw new WebhookUrlUnreadableException("its stored address cannot be decrypted (the Data Protection key ring changed); remove the destination and add it again", ex);
        }
    }

    private byte[] Key()
    {
        if (_key is not null) return _key;
        lock (_gate)
        {
            if (_key is not null) return _key;
            if (File.Exists(_keyPath))
            {
                try { _key = Convert.FromBase64String(_keyProtector.Unprotect(File.ReadAllText(_keyPath).Trim())); }
                catch (Exception ex) when (ex is CryptographicException or FormatException)
                {
                    // Fail fast: a silently replaced key would let the same address be added twice and break lookups by address.
                    throw new WebhookUrlUnreadableException($"The webhook address key {_keyPath} cannot be read with this key ring. Restore keys/ and secrets/ together (runbook section 7)", ex);
                }
                return _key;
            }
            var key = RandomNumberGenerator.GetBytes(32);
            Directory.CreateDirectory(Path.GetDirectoryName(_keyPath)!);
            var tmp = _keyPath + ".tmp";
            File.WriteAllText(tmp, _keyProtector.Protect(Convert.ToBase64String(key)));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(tmp, _keyPath, overwrite: false);
            _created = true;
            return _key = key;
        }
    }
}
