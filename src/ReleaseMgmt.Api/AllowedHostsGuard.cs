namespace ReleaseMgmt.Api;

/// <summary>
/// REOS-68 (SEC-E1, Q-SEC-E1 option b): outside Development the app refuses to start while <c>AllowedHosts</c> is <c>*</c> or empty. Host filtering then
/// accepts any <c>Host</c> header, so a DNS-rebinding page can reach the app under its own name and the cross-site guard, which compares <c>Origin</c>
/// with the request's own host, sees it as same-origin. Fail fast (CLAUDE.md rule 8): every install names the host(s) users type.
/// Development keeps its loopback-only value from <c>appsettings.Development.json</c> and is not checked here.
/// </summary>
public static class AllowedHostsGuard
{
    public const string Key = "AllowedHosts";

    /// <summary>The reason the value is refused, or null when it names at least one host and no entry is the wildcard.</summary>
    public static string? Problem(string? value)
    {
        var entries = (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Length == 0)
            return $"{Key} is empty. Outside Development set {Key} to the host name(s) users type, ';'-separated (e.g. releases.example.com); see RUNBOOK_OPERATIONS.md section 4.";
        if (entries.Contains("*"))
            return $"{Key} is \"*\", which accepts any Host header (DNS rebinding). Outside Development set {Key} to the host name(s) users type, ';'-separated (e.g. releases.example.com); see RUNBOOK_OPERATIONS.md section 4.";
        return null;
    }

    public static void Enforce(IHostEnvironment env, IConfiguration config)
    {
        if (env.IsDevelopment()) return;
        if (Problem(config[Key]) is { } why) throw new InvalidOperationException(why);
    }
}
