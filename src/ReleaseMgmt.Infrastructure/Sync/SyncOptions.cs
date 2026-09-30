using Microsoft.Extensions.Configuration;

namespace ReleaseMgmt.Infrastructure.Sync;

/// <summary>
/// The <c>Sync:*</c> configuration (Q-039). Every value is clamped to a sane range, so a typo cannot turn the poller into a busy loop.
/// PollSeconds 300 / WindowPollSeconds 60 (D11) / TimeoutSeconds 10 / MaxResponseBytes 1 MB / BackoffBaseSeconds 30 / WatchdogSeconds 60 /
/// StartDelaySeconds 5 / Enabled true / AllowPrivateTargets false / AllowedHosts (comma list, "*.example.com" allowed; empty = any public host) /
/// WindowToleranceMinutes 0 / Credentials:Directory (default data/secrets).
/// </summary>
public sealed record SyncOptions(
    TimeSpan Poll, TimeSpan WindowPoll, TimeSpan Timeout, int MaxResponseBytes, TimeSpan BackoffBase, TimeSpan Watchdog, TimeSpan StartDelay,
    bool Enabled, bool AllowPrivateTargets, string[] AllowedHosts, TimeSpan WindowTolerance, string CredentialsDirectory)
{
    public static SyncOptions From(IConfiguration c, string? defaultCredentialsDirectory = null)
    {
        static double D(IConfiguration c, string key, double def, double min, double max) =>
            Math.Clamp(double.TryParse(c[key], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : def, min, max);
        var hosts = (c["Sync:AllowedHosts"] ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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
