using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReleaseMgmt.Infrastructure.Comms;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// Webhook destinations are stored encrypted (Q-053e): tests that seed one by SQL protect its URL with the app's own vault, as the allowlist service does.
/// Use: <c>INSERT INTO {WebhookTestRows.Into} VALUES {WebhookTestRows.Row(f.Services, "w1", "ops", "https://...", "Teams")}</c>.
/// </summary>
internal static class WebhookTestRows
{
    public const string Into = "WebhookDestinations(Id,Name,Host,ProtectedUrl,UrlHmac,Kind)";

    public static IWebhookUrlVault Vault(IServiceProvider sp) => sp.GetRequiredService<IWebhookUrlVault>();

    /// <summary>A vault with a throw-away key ring and HMAC key, for senders tested without an app.</summary>
    public static IWebhookUrlVault Ephemeral() => new DataProtectionWebhookUrlVault(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(),
        ReleaseMgmt.Infrastructure.Sync.SyncOptions.From(new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Sync:Credentials:Directory"] = Directory.CreateTempSubdirectory("reos-hook-").FullName,
        }).Build()));

    public static string Row(IServiceProvider sp, string id, string name, string url, string kind)
    {
        var p = Vault(sp).Protect(url);
        var host = WebhookUrlProtectionUpgrade.HostOf(new Uri(url));
        return $"('{id}','{name}','{host}','{p.ProtectedUrl}','{p.UrlHmac}','{kind}')";
    }
}
