using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;

namespace ReleaseMgmt.Api.Auth;

/// <summary>
/// REOS-74 / Q-052e (decided 2026-09-30): the key ring is encrypted at rest, not only access-controlled (<see cref="KeyRingPermissions"/> still applies).
/// <list type="number">
/// <item>A certificate, when configured, on every OS: <c>DataProtection:Certificate:Path</c> (a PKCS#12 file; its password in
/// <c>DataProtection:Certificate:Password</c>, from an environment variable or the OS secret store, never an appsettings file) or
/// <c>DataProtection:Certificate:Thumbprint</c> (from the CurrentUser or LocalMachine "My" store). The certificate needs an RSA private key. Keys written under an
/// earlier certificate stay readable while it is listed in <c>DataProtection:Certificate:PreviousThumbprints</c> (store) or
/// <c>DataProtection:Certificate:PreviousPath</c> (+ <c>PreviousPassword</c>). A certificate travels with a restore, so it is the choice for DR onto another host.</item>
/// <item>Otherwise, on Windows outside Development: DPAPI, scope <c>DataProtection:Dpapi:Scope</c> = <c>User</c> (default: the service account; only that account
/// on that machine can decrypt) or <c>Machine</c> (any account on that machine, for when several accounts must run the app).</item>
/// <item>Otherwise, outside Development: start-up is refused, naming the keys, unless <c>DataProtection:AllowUnprotectedKeys=true</c> (a pilot that accepts plain
/// XML keys), which is logged at Warning on every start.</item>
/// <item>Development: unchanged (plain XML) unless a certificate is configured.</item>
/// </list>
/// Keys written before this change stay readable (an unencrypted key element is read as it is); every key created afterwards is encrypted. Data protected
/// with an old key keeps working; an old key leaves the ring only if its file is deleted (never do that, see the runbook).
/// </summary>
public static class KeyRingEncryption
{
    public const string PathKey = "DataProtection:Certificate:Path", PasswordKey = "DataProtection:Certificate:Password", ThumbprintKey = "DataProtection:Certificate:Thumbprint";
    public const string PreviousPathKey = "DataProtection:Certificate:PreviousPath", PreviousPasswordKey = "DataProtection:Certificate:PreviousPassword";
    public const string PreviousThumbprintsKey = "DataProtection:Certificate:PreviousThumbprints";
    public const string DpapiScopeKey = "DataProtection:Dpapi:Scope", AllowUnprotectedKey = "DataProtection:AllowUnprotectedKeys";

    /// <summary>How the ring is protected, for the start-up log. <see cref="Warning"/>, when set, is logged at Warning on every start.</summary>
    public sealed record Outcome(string Protector, string? Warning);

    /// <summary>Applies the protector to <paramref name="dp"/>. Throws <see cref="InvalidOperationException"/> (naming the keys) when the ring would be unprotected outside Development or the configuration is unusable.</summary>
    public static Outcome Configure(IDataProtectionBuilder dp, IConfiguration config, IHostEnvironment env)
    {
        var cert = Certificate(config, PathKey, PasswordKey, ThumbprintKey);
        if (cert is not null)
        {
            dp.ProtectKeysWithCertificate(cert);
            var previous = new List<X509Certificate2>();
            if (Certificate(config, PreviousPathKey, PreviousPasswordKey, null) is { } old) previous.Add(old);
            foreach (var t in Split(config[PreviousThumbprintsKey])) previous.Add(FromStore(t, PreviousThumbprintsKey));
            if (previous.Count > 0) dp.UnprotectKeysWithAnyCertificate([cert, .. previous]);
            return new($"certificate {cert.Subject} ({cert.Thumbprint})" + (previous.Count > 0 ? $", plus {previous.Count} previous certificate(s) for reading older keys" : ""), null);
        }
        if (env.IsDevelopment()) return new("none (Development)", null);
        if (OperatingSystem.IsWindows())
        {
            var scope = (config[DpapiScopeKey] ?? "User").Trim();
            var machine = scope.Equals("Machine", StringComparison.OrdinalIgnoreCase);
            if (!machine && !scope.Equals("User", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{DpapiScopeKey} is '{scope}'. Use User (the service account, the default) or Machine.");
            dp.ProtectKeysWithDpapi(protectToLocalMachine: machine);
            return new($"Windows DPAPI ({(machine ? "machine" : "user")} scope)", null);
        }
        if (config.GetValue(AllowUnprotectedKey, false))
            return new("none", $"The Data Protection key ring is stored unencrypted ({AllowUnprotectedKey}=true). Anyone who can read the keys directory can forge sessions and read "
                + $"the connector credentials and webhook addresses. Configure {PathKey} or {ThumbprintKey} (runbook section 4) and remove {AllowUnprotectedKey}.");
        throw new InvalidOperationException(
            $"The Data Protection key ring would be stored unencrypted. Outside Development set {PathKey} (a PKCS#12 file with an RSA key; its password in {PasswordKey}, "
            + $"from an environment variable or the OS secret store) or {ThumbprintKey} (a certificate in the CurrentUser or LocalMachine My store). "
            + $"A pilot that accepts plain keys may set {AllowUnprotectedKey}=true instead (logged at every start). See the operations runbook, section 4.");
    }

    private static X509Certificate2? Certificate(IConfiguration config, string pathKey, string passwordKey, string? thumbprintKey)
    {
        var path = config[pathKey];
        var thumb = thumbprintKey is null ? null : config[thumbprintKey];
        if (!string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(thumb))
            throw new InvalidOperationException($"Set either {pathKey} or {thumbprintKey}, not both.");
        if (!string.IsNullOrWhiteSpace(thumb)) return FromStore(thumb, thumbprintKey!);
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (FromAppSettingsFile(config, passwordKey) is { } file)
            throw new InvalidOperationException($"{passwordKey} is set in {file}. Supply it from an environment variable or the OS secret store, never an appsettings file (CLAUDE.md rule 11).");
        if (!File.Exists(path)) throw new InvalidOperationException($"{pathKey} names {path}, which does not exist.");
        X509Certificate2 cert;
        try
        {
            // Ephemeral: the private key is never written to the OS key store (macOS does not support it and keeps the key in a temporary keychain instead).
            var flags = OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet;
            cert = X509CertificateLoader.LoadPkcs12FromFile(path, config[passwordKey], flags);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException($"{pathKey} ({path}) could not be opened: {ex.Message}. Check the file and {passwordKey}.", ex);
        }
        return Usable(cert, pathKey);
    }

    private static X509Certificate2 FromStore(string thumbprint, string key)
    {
        var t = thumbprint.Replace(" ", "", StringComparison.Ordinal).Trim();
        foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            try
            {
                using var store = new X509Store(StoreName.My, location);
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                var found = store.Certificates.Find(X509FindType.FindByThumbprint, t, validOnly: false);
                if (found.Count > 0) return Usable(found[0], key);
            }
            catch (CryptographicException) { /* this store does not exist for the account or location: look in the next one */ }
        }
        throw new InvalidOperationException($"{key}: no certificate with thumbprint {t} is in the CurrentUser or LocalMachine My store of the account running the app.");
    }

    private static X509Certificate2 Usable(X509Certificate2 cert, string key)
    {
        if (!cert.HasPrivateKey) throw new InvalidOperationException($"{key}: the certificate {cert.Subject} has no private key, so the key ring could not be read back.");
        using var rsa = cert.GetRSAPublicKey();
        if (rsa is null) throw new InvalidOperationException($"{key}: the certificate {cert.Subject} is not an RSA certificate (the key ring encryption needs RSA).");
        return cert;
    }

    /// <summary>The appsettings file that defines <paramref name="key"/>, if any (a secret there ends up in git or in every copy of the app folder).</summary>
    private static string? FromAppSettingsFile(IConfiguration config, string key)
    {
        if (config is not IConfigurationRoot root) return null;
        foreach (var p in root.Providers)
            if (p is FileConfigurationProvider fp && fp.Source.Path is { } file
                && Path.GetFileName(file).StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) && p.TryGet(key, out var v) && !string.IsNullOrEmpty(v))
                return Path.GetFileName(file);
        return null;
    }

    private static IEnumerable<string> Split(string? s) => (s ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
