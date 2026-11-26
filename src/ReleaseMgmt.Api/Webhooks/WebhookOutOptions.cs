namespace ReleaseMgmt.Api.Webhooks;

/// <summary>
/// REOS-164: configuration of the outbound webhooks (<c>Webhooks:Out:*</c>). The host allow-list is the administrator's (CLAUDE.md rule 8, D13): <c>AllowedHosts</c> is a list separated by commas, semicolons or spaces of host names
/// (<c>hooks.example.com</c>, <c>*.example.com</c>), each optionally with <c>:port</c> (443 when none). <b>Outside Development an empty list refuses every address</b> (nothing is posted anywhere until an administrator names a host);
/// in Development an empty list means any public host, so local work and the tests need no setup; <c>*</c> alone means any public host, by explicit choice. Private, loopback and link-local targets stay refused unless <c>AllowPrivateTargets</c>.
/// Defaults: Timeout 10 s per attempt, MaxAttempts 10, backoff 60 s doubling to at most 6 h (about 8.5 hours of retrying), Poll 5 s, Batch 20, RetentionDays 30 (finished deliveries), MaxPerWorkspace 20, Worker:Enabled true.
/// </summary>
public sealed record WebhookOutOptions(string[] AllowedHosts, bool AnyHost, bool AllowPrivateTargets, bool IsDevelopment, TimeSpan Timeout, int MaxAttempts, TimeSpan BackoffBase, TimeSpan BackoffCap,
    TimeSpan Poll, int Batch, int RetentionDays, int MaxPerWorkspace, bool WorkerEnabled)
{
    /// <summary>No restriction on the host (still never a private address): Development with no list, or the explicit <c>*</c>.</summary>
    public bool Open => AnyHost || (IsDevelopment && AllowedHosts.Length == 0);
    /// <summary>No host is allowed at all: an administrator has to name one.</summary>
    public bool Closed => !Open && AllowedHosts.Length == 0;
    public string Mode => Closed ? "closed" : Open ? "open" : "restricted";

    public static WebhookOutOptions From(IConfiguration c, bool isDevelopment)
    {
        static double D(IConfiguration c, string key, double def, double min, double max) =>
            Math.Clamp(double.TryParse(c[key], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : def, min, max);
        var hosts = (c["Webhooks:Out:AllowedHosts"] ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var any = hosts.Contains("*");
        return new(any ? [] : hosts, any, string.Equals(c["Webhooks:Out:AllowPrivateTargets"], "true", StringComparison.OrdinalIgnoreCase), isDevelopment,
            TimeSpan.FromSeconds(D(c, "Webhooks:Out:TimeoutSeconds", 10, 0.1, 60)),
            (int)D(c, "Webhooks:Out:MaxAttempts", 10, 1, 30),
            TimeSpan.FromSeconds(D(c, "Webhooks:Out:BackoffBaseSeconds", 60, 0, 3_600)),
            TimeSpan.FromSeconds(D(c, "Webhooks:Out:BackoffCapSeconds", 21_600, 0, 86_400)),
            TimeSpan.FromSeconds(D(c, "Webhooks:Out:PollSeconds", 5, 0.05, 3_600)),
            (int)D(c, "Webhooks:Out:Batch", 20, 1, 500),
            (int)D(c, "Webhooks:Out:RetentionDays", 30, 1, 3_650),
            (int)D(c, "Webhooks:Out:MaxPerWorkspace", 20, 1, 200),
            !string.Equals(c["Webhooks:Out:Worker:Enabled"], "false", StringComparison.OrdinalIgnoreCase));
    }
}
