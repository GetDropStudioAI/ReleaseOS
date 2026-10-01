using Microsoft.Extensions.Configuration;

namespace ReleaseMgmt.Infrastructure.Sync;

/// <summary>
/// The <c>Sync:*</c> configuration (Q-039). Every value is clamped to a sane range, so a typo cannot turn the poller into a busy loop.
/// PollSeconds 300 / WindowPollSeconds 60 (D11) / TimeoutSeconds 10 / MaxResponseBytes 1 MB / BackoffBaseSeconds 30 / WatchdogSeconds 60 /
/// StartDelaySeconds 5 / Enabled true / AllowPrivateTargets false / AllowedHosts / WindowToleranceMinutes 0 / Credentials:Directory (default data/secrets).
/// <para><c>Sync:AllowedHosts</c> (REOS-75, Q-SEC-C3, decided 2026-09-30): a list separated by commas, semicolons or spaces of host names (<c>jira.example.com</c>) or
/// subdomain patterns (<c>*.atlassian.net</c>), each optionally with a port (<c>jira.example.com:8443</c>); an entry without a port allows the scheme's default
/// port only (443 for https). Unset outside Development it is <see cref="DefaultAllowedHosts"/> (Atlassian Cloud and ServiceNow, 443 only); an explicit value
/// replaces that default entirely; <c>*</c> alone means any public host on any port (today's behaviour, by explicit choice). In Development an unset list keeps
/// meaning any host, so the fakes and local stubs work.</para>
/// </summary>
public sealed record SyncOptions(
    TimeSpan Poll, TimeSpan WindowPoll, TimeSpan Timeout, int MaxResponseBytes, TimeSpan BackoffBase, TimeSpan Watchdog, TimeSpan StartDelay,
    bool Enabled, bool AllowPrivateTargets, string[] AllowedHosts, TimeSpan WindowTolerance, string CredentialsDirectory)
{
    /// <summary>The production default of <c>Sync:AllowedHosts</c>: the real Atlassian Cloud and ServiceNow hosts, port 443 only.</summary>
    public static readonly string[] DefaultAllowedHosts = ["*.atlassian.net", "*.service-now.com"];

    /// <param name="isDevelopment">False (the default, so a forgotten argument fails safe) applies <see cref="DefaultAllowedHosts"/> when the key is unset.</param>
    public static SyncOptions From(IConfiguration c, string? defaultCredentialsDirectory = null, bool isDevelopment = false)
    {
        static double D(IConfiguration c, string key, double def, double min, double max) =>
            Math.Clamp(double.TryParse(c[key], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : def, min, max);
        var hosts = (c["Sync:AllowedHosts"] ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (hosts.Length == 0 && !isDevelopment) hosts = [.. DefaultAllowedHosts];
        if (hosts.Contains("*")) hosts = [];   // explicit "any public host, any port"
        return new(
            TimeSpan.FromSeconds(D(c, "Sync:PollSeconds", 300, 0.05, 86_400)),
            TimeSpan.FromSeconds(D(c, "Sync:WindowPollSeconds", 60, 0.05, 86_400)),
            TimeSpan.FromSeconds(D(c, "Sync:TimeoutSeconds", 10, 0.05, 120)),
            (int)D(c, "Sync:MaxResponseBytes", 1_000_000, 1_024, 50_000_000),
            TimeSpan.FromSeconds(D(c, "Sync:BackoffBaseSeconds", 30, 0, 3_600)),
            TimeSpan.FromSeconds(D(c, "Sync:WatchdogSeconds", 60, 0.05, 86_400)),
            TimeSpan.FromSeconds(D(c, "Sync:StartDelaySeconds", 5, 0, 3_600)),
            !string.Equals(c["Sync:Enabled"], "false", StringComparison.OrdinalIgnoreCase),
            string.Equals(c["Sync:AllowPrivateTargets"], "true", StringComparison.OrdinalIgnoreCase),
            hosts,
            TimeSpan.FromMinutes(D(c, "Sync:WindowToleranceMinutes", 0, 0, 1_440)),
            c["Sync:Credentials:Directory"] ?? defaultCredentialsDirectory ?? Path.Combine("data", "secrets"));
    }
}
