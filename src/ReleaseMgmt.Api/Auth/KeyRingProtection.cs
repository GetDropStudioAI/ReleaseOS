using System.Xml.Linq;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Options;

namespace ReleaseMgmt.Api.Auth;

/// <summary>
/// SEC-B12: the Data Protection key ring encrypts the connector credentials and signs every session cookie: whoever can read it can mint a cookie for any
/// user and role. On Linux and macOS the framework creates <c>keys/</c> as 0755 and each key as 0644 (readable by every local account). This keeps the
/// directory at 0700 and every key file at 0600, for the ring that exists at start-up and for each key the framework adds later (it rotates about every
/// 90 days). Windows is left to the service account's ACLs (runbook section 5). The keys themselves are encrypted by <see cref="KeyRingEncryption"/> (Q-052e).
/// </summary>
public sealed class KeyRingPermissions(ILogger<KeyRingPermissions> log) : IPostConfigureOptions<KeyManagementOptions>
{
    private const UnixFileMode Dir = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode Key = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public void PostConfigure(string? name, KeyManagementOptions options)
    {
        if (OperatingSystem.IsWindows() || options.XmlRepository is not FileSystemXmlRepository fs) return;
        Restrict(fs.Directory, log);
        options.XmlRepository = new Restricted(fs, log);
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void Restrict(DirectoryInfo dir, ILogger log)
    {
        try
        {
            if (!dir.Exists) Directory.CreateDirectory(dir.FullName, Dir);
            File.SetUnixFileMode(dir.FullName, Dir);
            foreach (var f in dir.EnumerateFiles("*.xml")) File.SetUnixFileMode(f.FullName, Key);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogError(ex, "Could not restrict the permissions of the Data Protection key ring at {Directory}; restrict them by hand (runbook section 5)", dir.FullName);
        }
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private sealed class Restricted(FileSystemXmlRepository inner, ILogger log) : IXmlRepository
    {
        public IReadOnlyCollection<XElement> GetAllElements() => inner.GetAllElements();

        public void StoreElement(XElement element, string friendlyName)
        {
            inner.StoreElement(element, friendlyName);
            Restrict(inner.Directory, log);
        }
    }
}

/// <summary>
/// SEC-B11: the session cookie is protected under a purpose that includes the environment name. A copy of the database and key ring restored onto another
/// host (the DR runbook's own procedure) and run in Development, where dev-login mints a cookie for any existing user and role, can then no longer produce
/// a cookie the Production instance accepts. Someone holding the key ring can still forge one by hand: the key ring stays the secret (Q-052e, SEC-B12).
/// </summary>
public sealed class CookieProtectionPurpose(IDataProtectionProvider dp, IHostEnvironment env) : IConfigureNamedOptions<CookieAuthenticationOptions>
{
    public void Configure(string? name, CookieAuthenticationOptions options)
    {
        if (name != CookieAuthenticationDefaults.AuthenticationScheme) return;
        options.TicketDataFormat = new Microsoft.AspNetCore.Authentication.TicketDataFormat(
            dp.CreateProtector("ReleaseMgmt.Auth.SessionCookie", env.EnvironmentName, "v1"));
    }

    public void Configure(CookieAuthenticationOptions options) => Configure(Options.DefaultName, options);
}
