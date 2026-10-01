using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Api.Reminders;
using ReleaseMgmt.Infrastructure.Comms;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Api.Sync;

/// <summary>Guard names of the Sync health screen (REOS-42). Each maps to 422 {guard, message}.</summary>
public static class SyncGuards
{
    public const string AlertAlreadyResolved = "AlertAlreadyResolved";
    public const string WebhookInvalidUrl = "WebhookInvalidUrl";
    public const string WebhookNotHttps = "WebhookNotHttps";
    public const string WebhookCredentialsInUrl = "WebhookCredentialsInUrl";
    public const string WebhookPrivateTarget = "WebhookPrivateTarget";
    public const string WebhookDuplicate = "WebhookDuplicate";
    public const string WebhookInvalidInput = "WebhookInvalidInput";
    public const string WebhookInUse = "WebhookInUse";
}

/// <summary>What one webhook URL becomes after checking: the normalised form that is stored and compared, and the host that is safe to show.</summary>
public sealed record CheckedWebhookUrl(string Normalised, string Host);

/// <summary>
/// The webhook allowlist rules (D13, PROJECT_SCOPE 5.2): https only, no credentials, no fragment, no loopback / private / link-local literal
/// or obviously internal host name unless Notifications:Webhooks:AllowPrivateTargets. DNS is not resolved when saving (an admin write must not
/// depend on the network); TeamWebhookSender vets the resolved address again at send and at connect time. Q-042d.
/// </summary>
public static class WebhookUrlPolicy
{
    public const int MaxUrlLength = 2048;
    private static readonly string[] InternalSuffixes = [".localhost", ".local", ".internal", ".localdomain", ".home.arpa", ".lan", ".intranet", ".corp"];

    public static ServiceResult<CheckedWebhookUrl> Check(string? raw, bool allowPrivate, OutboundAddressPolicy? addresses = null)
    {
        static ServiceResult<CheckedWebhookUrl> Bad(string guard, string message) => ServiceResult<CheckedWebhookUrl>.Fail(new GuardFailure(guard, message));
        raw = raw?.Trim();
        if (string.IsNullOrEmpty(raw)) return Bad(SyncGuards.WebhookInvalidUrl, "Enter the webhook address, starting with https://");
        if (raw.Length > MaxUrlLength) return Bad(SyncGuards.WebhookInvalidUrl, $"The address is longer than {MaxUrlLength} characters");
        if (raw.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) return Bad(SyncGuards.WebhookInvalidUrl, "The address contains spaces or control characters");
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return Bad(SyncGuards.WebhookInvalidUrl, "That is not a complete address. Start with https:// and a host name");
        if (uri.Scheme != Uri.UriSchemeHttps) return Bad(SyncGuards.WebhookNotHttps, $"Only https:// addresses are allowed ({uri.Scheme}:// was given). A webhook carries release data and its address is a secret");
        if (string.IsNullOrEmpty(uri.Host)) return Bad(SyncGuards.WebhookInvalidUrl, "That address has no host name. Start with https:// and a host name");
        if (uri.UserInfo.Length > 0) return Bad(SyncGuards.WebhookCredentialsInUrl, "Remove the user name and password from the address; credentials in a URL are stored and shown in the clear");
        if (uri.Fragment.Length > 0) return Bad(SyncGuards.WebhookInvalidUrl, "Remove the #fragment; it is never sent to the server");

        var host = uri.IdnHost.ToLowerInvariant();
        if (!allowPrivate && IsInternal(uri, host, addresses ?? OutboundAddressPolicy.Default))
            return Bad(SyncGuards.WebhookPrivateTarget, $"{host} is a loopback, private, link-local or internal address. Set Notifications:Webhooks:AllowPrivateTargets to allow one deliberately");
        var shownHost = uri.HostNameType == UriHostNameType.IPv6 && !host.StartsWith('[') ? $"[{host}]" : host;
        var port = uri.IsDefaultPort ? "" : $":{uri.Port}";
        return ServiceResult<CheckedWebhookUrl>.Ok(new CheckedWebhookUrl($"https://{shownHost}{port}{uri.PathAndQuery}", shownHost + port));
    }

    private static bool IsInternal(Uri uri, string host, OutboundAddressPolicy addresses)
    {
        // Any host that is an address in the form the handler connects to, whatever Uri.HostNameType says ("１２７.０.０.１" is a "Dns" name; SEC-C2)
        if (ReleaseMgmt.Infrastructure.Sync.ConnectorUrlPolicy.LiteralAddress(uri) is { } ip) return addresses.IsBlocked(ip);
        if (host == "localhost" || InternalSuffixes.Any(host.EndsWith)) return true;
        return !host.Contains('.');   // a single-label name only resolves inside the network (or a search domain)
    }

    /// <summary>Teams and Slack are recognised by host; anything else is Generic. The admin may override.</summary>
    public static string InferKind(string host) =>
        host.EndsWith("webhook.office.com") || host.EndsWith("outlook.office.com") || host.EndsWith("logic.azure.com") || host.EndsWith("powerplatform.com") ? "Teams"
        : host.EndsWith("hooks.slack.com") ? "Slack" : "Generic";
}

public sealed record AlertFilter(string? Source = null, string? Kind = null, string? Train = null, string State = "all", int Days = SyncHealthService.DefaultResolvedDays, int Limit = SyncHealthService.DefaultLimit);

public sealed record AlertRow(string Id, string? TrainId, string? TrainTitle, string Source, string Kind, string Fingerprint, string Message, int OccurrenceCount,
    DateTime FirstOccurredAt, DateTime LastOccurredAt, bool IsResolved, DateTime? ResolvedAt, string? ResolvedByUserId, string? ResolvedByName, int Version);

public sealed record AlertPage(IReadOnlyList<AlertRow> Items, int OpenCount, int ResolvedCount, int ResolvedWithinDays, int Limit, DateTime AsOf);

public sealed record LinkCounts(int Total, int InSync, int Mismatch, int Broken, int Unsynced, int Stale);

public sealed record ConnectorView(string Source, string BaseUrl, bool IsEnabled, DateTime? LastCycleStartedAt, DateTime? LastCycleCompletedAt, DateTime? LastSuccessAt,
    int ConsecutiveFailures, int Version, LinkCounts Links, int OpenAlerts, string State);

/// <summary>One thing that is failing across the board. Scope "connector" = Jira or ServiceNow; "engine" = the sync engine itself stalled (watchdog).</summary>
public sealed record FailingEntry(string Scope, string Source, string Kind, string[] Reasons, string? Message, DateTime? Since, string? AlertId, int Links);

public sealed record WatchdogView(bool Stalled, string? AlertId, DateTime? LastCycleCompletedAt);

public sealed record SyncState(bool ConnectorWide, IReadOnlyList<FailingEntry> Failing, IReadOnlyList<ConnectorView> Connectors, int OpenAlerts, WatchdogView Watchdog, DateTime AsOf);

public sealed record LinkProblem(string Id, string TrainId, string? TrainTitle, string EntityType, string EntityId, string Source, string ExternalKey, string? Expected, string? Reported, string SyncState, DateTime? LastSyncedAt);

public sealed record WebhookRow(string Id, string Name, string Kind, string Host, string DisplayUrl, int Version, int UsedByTeams, int UsedByDispatches);

/// <summary>
/// REOS-42 read model and writes for the Sync health screen: alerts (open plus recently resolved, history kept), connector state and the
/// connector-wide failure test behind the banner, mismatched links, manual resolve, and the webhook allowlist admin. Every write is one
/// transaction with one AuditEvents row (rules 2-4); the clock is the injected TimeProvider. The connector, poller and watchdog code that
/// WRITES ConnectorState and raises alerts belongs to REOS-39..41; this only reads them.
/// </summary>
public sealed class SyncHealthService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IConfiguration config, ILogger<SyncHealthService> log, IWebhookUrlVault urlVault,
    OutboundAddressPolicy addressPolicy, IRealtimePublisher? realtime = null)
    : ServiceBase(dbf, time, realtime)
{
    private readonly IDbContextFactory<ReleaseDbContext> _dbf = dbf;
    private readonly IRealtimePublisher? _realtime = realtime;

    public const int DefaultResolvedDays = 7, MaxResolvedDays = 365, DefaultLimit = 200, MaxLimit = 500;
    /// <summary>Consecutive failed cycles after which a connector counts as down (PROJECT_SCOPE 5.3: alert after 3 consecutive).</summary>
    public const int FailureThreshold = 3;
    public static readonly string[] States = ["all", "open", "resolved"];
    /// <summary>Alert kinds that mean the whole connector is unusable (as opposed to one link, such as NotFound).</summary>
    public static readonly string[] ConnectorKinds = ["AuthFailed", "Unreachable"];

    private static readonly string[] BrokenStates = ["NotFound", "AuthFailed"];

    /// <summary>Config Sync:StaleAfterMinutes (default 15 = three 5-minute intervals, PROJECT_SCOPE 5.3.6): a link last synced longer ago is stale.</summary>
    private int StaleMinutes => Math.Max(1, config.GetValue("Sync:StaleAfterMinutes", 15));

    // ---- alerts -----------------------------------------------------------------------------------------------------------------------

    public async Task<AlertPage> ListAlertsAsync(AlertFilter f, CancellationToken ct = default)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var now = Now;
        var days = Math.Clamp(f.Days, 1, MaxResolvedDays);
        var cutoff = now.AddDays(-days);
        var limit = Math.Clamp(f.Limit, 1, MaxLimit);

        IQueryable<SyncAlerts> scope = db.Set<SyncAlerts>().AsNoTracking();
        if (!string.IsNullOrEmpty(f.Source)) scope = scope.Where(a => a.SourceSystem == f.Source);
        if (!string.IsNullOrEmpty(f.Kind)) scope = scope.Where(a => a.Kind == f.Kind);
        if (!string.IsNullOrEmpty(f.Train)) scope = scope.Where(a => a.ReleaseTrainId == f.Train);
        var openCount = await scope.CountAsync(a => !a.IsResolved, ct);
        var resolvedCount = await scope.CountAsync(a => a.IsResolved && a.ResolvedAt >= cutoff, ct);

        var q = f.State switch
        {
            "open" => scope.Where(a => !a.IsResolved),
            "resolved" => scope.Where(a => a.IsResolved && a.ResolvedAt >= cutoff),
            _ => scope.Where(a => !a.IsResolved || a.ResolvedAt >= cutoff),
        };
        var items = await (from a in q
                           join t in db.Set<ReleaseTrains>() on a.ReleaseTrainId equals t.Id into ts
                           from t in ts.DefaultIfEmpty()
                           join u in db.Set<Users>() on a.ResolvedByUserId equals u.Id into us
                           from u in us.DefaultIfEmpty()
                           orderby a.IsResolved, a.LastOccurredAt descending, a.Id descending   // open first, each newest first
                           select new AlertRow(a.Id, a.ReleaseTrainId, t.Title, a.SourceSystem, a.Kind, a.Fingerprint, a.ErrorMessage, a.OccurrenceCount,
                               a.FirstOccurredAt, a.LastOccurredAt, a.IsResolved, a.ResolvedAt, a.ResolvedByUserId, u.DisplayName, a.Version))
            .Take(limit).ToListAsync(ct);
        return new AlertPage(items, openCount, resolvedCount, days, limit, now);
    }

    /// <summary>Resolves an open alert by hand (POST /sync/alerts/{id}:resolve). History stays: the row is kept with who and when. If the fault is
    /// still there the next failed cycle raises a new open row (the unique index is on open fingerprints only), so a manual resolve never hides a live fault for long.</summary>
    public async Task<ServiceResult<AlertRow>> ResolveAsync(string id, Actor actor, int? expectedVersion, CancellationToken ct = default)
    {
        var r = await RunAsync(async db =>
        {
            var a = await db.Set<SyncAlerts>().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (a is null) return ServiceResult<SyncAlerts>.NotFound("alert");
            if (VersionMismatch(expectedVersion, a.Version)) return ServiceResult<SyncAlerts>.Conflict(a);
            if (a.IsResolved) return ServiceResult<SyncAlerts>.Fail(new GuardFailure(SyncGuards.AlertAlreadyResolved, "This alert is already resolved"));
            var before = new { a.IsResolved, a.OccurrenceCount };
            a.IsResolved = true; a.ResolvedAt = Now; a.ResolvedByUserId = actor.UserId; a.Version++;
            Audit(db, actor, a.ReleaseTrainId, "SyncAlert", a.Id, "Resolve", before, new { a.IsResolved, a.ResolvedAt, Source = a.SourceSystem, a.Kind });
            await db.SaveChangesAsync(ct);
            return ServiceResult<SyncAlerts>.Ok(a);
        }, ct);
        if (!r.IsOk) return new ServiceResult<AlertRow>(r.Kind, null, r.Failures, r.Current, r.Missing);
        if (_realtime is not null)
        {
            try { await _realtime.SyncAlertRaisedAsync(id, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Alert {AlertId} was resolved but the live push failed", id); }
        }
        var page = await ListAlertsAsync(new AlertFilter(State: "resolved", Limit: MaxLimit), ct);
        return ServiceResult<AlertRow>.Ok(page.Items.FirstOrDefault(x => x.Id == id) ?? ToRow(r.Value!));
    }

    private static AlertRow ToRow(SyncAlerts a) => new(a.Id, a.ReleaseTrainId, null, a.SourceSystem, a.Kind, a.Fingerprint, a.ErrorMessage, a.OccurrenceCount, a.FirstOccurredAt, a.LastOccurredAt, a.IsResolved, a.ResolvedAt, a.ResolvedByUserId, null, a.Version);

    // ---- connector state and the connector-wide test ----------------------------------------------------------------------------------

    public async Task<SyncState> GetStateAsync(CancellationToken ct = default)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var now = Now;
        var cutoff = now.AddMinutes(-StaleMinutes);
        var connectors = await db.Set<ConnectorState>().AsNoTracking().OrderBy(c => c.SourceSystem).ToListAsync(ct);
        var open = await db.Set<SyncAlerts>().AsNoTracking().Where(a => !a.IsResolved).ToListAsync(ct);
        var byState = await db.Set<ExternalLinks>().AsNoTracking().GroupBy(l => new { l.SourceSystem, l.SyncState }).Select(g => new { g.Key.SourceSystem, g.Key.SyncState, N = g.Count() }).ToListAsync(ct);
        var stale = await db.Set<ExternalLinks>().AsNoTracking().Where(l => l.LastSyncedAt != null && l.LastSyncedAt < cutoff).GroupBy(l => l.SourceSystem).Select(g => new { Source = g.Key, N = g.Count() }).ToListAsync(ct);

        var views = new List<ConnectorView>();
        var failing = new List<FailingEntry>();
        foreach (var c in connectors)
        {
            var mine = byState.Where(b => b.SourceSystem == c.SourceSystem).ToList();
            int N(params string[] s) => mine.Where(b => s.Contains(b.SyncState)).Sum(b => b.N);
            var counts = new LinkCounts(mine.Sum(b => b.N), N("InSync"), N("Mismatch"), N(BrokenStates), N("Unsynced"), stale.FirstOrDefault(s => s.Source == c.SourceSystem)?.N ?? 0);
            var alerts = open.Where(a => a.SourceSystem == c.SourceSystem).ToList();

            // Connector-wide = the whole connector is unusable, not one link: (1) an open AuthFailed / Unreachable alert, (2) three or more failed cycles
            // in a row (no successful poll), or (3) every link it owns reports AuthFailed. Disabled connectors are excluded. Q-042a.
            var reasons = new List<string>();
            var wide = c.IsEnabled ? alerts.Where(a => ConnectorKinds.Contains(a.Kind)).OrderBy(a => a.FirstOccurredAt).ToList() : [];
            reasons.AddRange(wide.Select(a => a.Kind).Distinct());
            if (c.IsEnabled && c.ConsecutiveFailures >= FailureThreshold) reasons.Add("ConsecutiveFailures");
            if (c.IsEnabled && counts.Total > 0 && N("AuthFailed") == counts.Total) reasons.Add("AllLinksAuthFailed");
            if (reasons.Count > 0)
            {
                var lead = wide.FirstOrDefault() ?? alerts.OrderBy(a => a.FirstOccurredAt).FirstOrDefault();
                var kind = wide.FirstOrDefault()?.Kind ?? (reasons.Contains("AllLinksAuthFailed") ? "AuthFailed" : "Failing");
                failing.Add(new FailingEntry("connector", c.SourceSystem, kind, [.. reasons], lead?.ErrorMessage, lead?.FirstOccurredAt ?? c.LastSuccessAt, lead?.Id, counts.Total));
            }
            var state = !c.IsEnabled ? "Disabled" : reasons.Count > 0 ? "Failing" : c.LastCycleCompletedAt is null ? "NotRun"
                : (alerts.Count > 0 || counts.Mismatch > 0 || counts.Broken > 0 || counts.Stale > 0 || c.ConsecutiveFailures > 0) ? "Degraded" : "Healthy";
            views.Add(new ConnectorView(c.SourceSystem, c.BaseUrl, c.IsEnabled, c.LastCycleStartedAt, c.LastCycleCompletedAt, c.LastSuccessAt, c.ConsecutiveFailures, c.Version, counts, alerts.Count, state));
        }

        // The watchdog (D30): the engine itself has stopped cycling. Nothing on any screen is current, so it is connector-wide too.
        var stalled = open.Where(a => a.SourceSystem == "SyncEngine" && a.Kind == "Stalled").OrderBy(a => a.FirstOccurredAt).FirstOrDefault();
        if (stalled is not null) failing.Add(new FailingEntry("engine", "SyncEngine", "Stalled", ["Stalled"], stalled.ErrorMessage, stalled.FirstOccurredAt, stalled.Id, 0));
        var lastCycle = connectors.Where(c => c.IsEnabled).Max(c => c.LastCycleCompletedAt);
        return new SyncState(failing.Count > 0, failing, views, open.Count, new WatchdogView(stalled is not null, stalled?.Id, lastCycle), now);
    }

    public async Task<IReadOnlyList<LinkProblem>> ListProblemLinksAsync(int limit, CancellationToken ct = default)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        string[] bad = ["Mismatch", "NotFound", "AuthFailed"];
        return await (from l in db.Set<ExternalLinks>().AsNoTracking()
                      where bad.Contains(l.SyncState)
                      join t in db.Set<ReleaseTrains>() on l.ReleaseTrainId equals t.Id into ts
                      from t in ts.DefaultIfEmpty()
                      orderby t.Title, l.SourceSystem, l.ExternalKey
                      select new LinkProblem(l.Id, l.ReleaseTrainId, t.Title, l.EntityType, l.EntityId, l.SourceSystem, l.ExternalKey, l.ExpectedStatus, l.LastSyncedStatus, l.SyncState, l.LastSyncedAt))
            .Take(Math.Clamp(limit, 1, MaxLimit)).ToListAsync(ct);
    }

    // ---- webhook allowlist ------------------------------------------------------------------------------------------------------------

    private bool AllowPrivate => config.GetValue("Notifications:Webhooks:AllowPrivateTargets", false);

    private static string Show(string host) => $"https://{host}/…";   // the path carries the secret token (Teams, Slack): never sent back, not even to an admin; it is stored encrypted (Q-053e)

    /// <summary>The allowlist as the screen shows it. The stored URL is a secret (kept encrypted, never decrypted here), so rows carry the host and an elided address only.</summary>
    public async Task<IReadOnlyList<WebhookRow>> ListWebhooksAsync(CancellationToken ct = default)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var rows = await db.Set<WebhookDestinations>().AsNoTracking().OrderBy(w => w.Name).ToListAsync(ct);
        var teams = await db.Set<Teams>().AsNoTracking().Where(t => t.WebhookDestinationId != null).GroupBy(t => t.WebhookDestinationId!).Select(g => new { Id = g.Key, N = g.Count() }).ToListAsync(ct);
        var sent = await db.Set<CommDispatches>().AsNoTracking().Where(d => d.WebhookDestinationId != null).GroupBy(d => d.WebhookDestinationId!).Select(g => new { Id = g.Key, N = g.Count() }).ToListAsync(ct);
        return [.. rows.Select(w => new WebhookRow(w.Id, w.Name, w.Kind, w.Host, Show(w.Host), w.Version,
            teams.FirstOrDefault(t => t.Id == w.Id)?.N ?? 0, sent.FirstOrDefault(t => t.Id == w.Id)?.N ?? 0))];
    }

    public Task<ServiceResult<WebhookRow>> AddWebhookAsync(string? name, string? url, string? kind, Actor actor, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            static ServiceResult<WebhookRow> Bad(string guard, string message) => ServiceResult<WebhookRow>.Fail(new GuardFailure(guard, message));
            name = name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 80) return Bad(SyncGuards.WebhookInvalidInput, "Give the channel a name of 1 to 80 characters, for example #release-ops");
            var checkedUrl = WebhookUrlPolicy.Check(url, AllowPrivate, addressPolicy);
            if (!checkedUrl.IsOk) return ServiceResult<WebhookRow>.Fail(checkedUrl.Failures);
            var norm = checkedUrl.Value!;
            var k = string.IsNullOrWhiteSpace(kind) ? WebhookUrlPolicy.InferKind(norm.Host) : kind.Trim();
            if (k is not ("Teams" or "Slack" or "Generic")) return Bad(SyncGuards.WebhookInvalidInput, "Kind is Teams, Slack or Generic");
            var dup = $"{norm.Host} with that exact address is already on the allowlist";
            var stored = urlVault.Protect(norm.Normalised);   // Q-053e: encrypted at rest; the keyed hash finds a duplicate without decrypting anything
            if (await db.Set<WebhookDestinations>().AnyAsync(w => w.UrlHmac == stored.UrlHmac, ct)) return Bad(SyncGuards.WebhookDuplicate, dup);
            var w = new WebhookDestinations { Name = name, Host = norm.Host, ProtectedUrl = stored.ProtectedUrl, UrlHmac = stored.UrlHmac, Kind = k };
            db.Set<WebhookDestinations>().Add(w);
            Audit(db, actor, null, "WebhookDestination", w.Id, "Create", null, new { w.Name, w.Kind, norm.Host });   // the URL is a secret: host only
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 } s && s.Message.Contains("UNIQUE"))
            {
                return Bad(SyncGuards.WebhookDuplicate, dup);   // lost a race with another admin; the unique index caught it
            }
            return ServiceResult<WebhookRow>.Ok(new WebhookRow(w.Id, w.Name, w.Kind, w.Host, Show(w.Host), w.Version, 0, 0));
        }, ct);

    public Task<ServiceResult<WebhookRow>> RemoveWebhookAsync(string id, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var w = await db.Set<WebhookDestinations>().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (w is null) return ServiceResult<WebhookRow>.NotFound("webhook");
            if (VersionMismatch(expectedVersion, w.Version)) return ServiceResult<WebhookRow>.Conflict(new { w.Id, w.Name, w.Kind, w.Version });   // never echo the URL
            var teams = await db.Set<Teams>().CountAsync(t => t.WebhookDestinationId == id, ct);
            var sent = await db.Set<CommDispatches>().CountAsync(d => d.WebhookDestinationId == id, ct);
            if (teams + sent > 0)
                return ServiceResult<WebhookRow>.Fail(new GuardFailure(SyncGuards.WebhookInUse,
                    $"{w.Name} is used by {teams} team{(teams == 1 ? "" : "s")} and {sent} recorded dispatch{(sent == 1 ? "" : "es")}. Dispatch records are permanent, so a used destination cannot be removed"));
            var host = w.Host;
            db.Set<WebhookDestinations>().Remove(w);
            Audit(db, actor, null, "WebhookDestination", w.Id, "Delete", new { w.Name, w.Kind, Host = host });
            await db.SaveChangesAsync(ct);
            return ServiceResult<WebhookRow>.Ok(new WebhookRow(w.Id, w.Name, w.Kind, host, Show(host), w.Version, 0, 0));
        }, ct);
}
