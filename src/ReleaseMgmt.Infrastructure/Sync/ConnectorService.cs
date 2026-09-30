using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Domain.Sync;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Reminders;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Infrastructure.Sync;

public sealed record ConnectorView(
    string Source, string BaseUrl, bool IsEnabled, bool HasCredentials, string? CredentialKind, string Status,
    DateTime? LastCycleStartedAt, DateTime? LastCycleCompletedAt, DateTime? LastSuccessAt, int ConsecutiveFailures,
    int OpenAlerts, int LinkCount, int StaleLinks, int MismatchLinks, int? Version);

public sealed record LinkWarning(string Rule, string Message, DateTime Since);

public sealed record LinkView(string Id, string TrainId, string EntityType, string EntityId, string SourceSystem, string ExternalKey,
    string? ExpectedStatus, string? LastSyncedStatus, DateTime? LastSyncedAt, string SyncState, bool Stale, int Version, IReadOnlyList<LinkWarning> Warnings);

public sealed record TestOutcome(bool Ok, string Source, long ElapsedMs);

public static class ConnectorGuards
{
    public const string ConnectorInvalid = "ConnectorInvalid", CredentialsInvalid = "CredentialsInvalid", ConnectorNotConfigured = "ConnectorNotConfigured",
        ConnectorTestFailed = "ConnectorTestFailed", LinkInvalid = "LinkInvalid", LinkExists = "LinkExists";
}

/// <summary>
/// The Connectors admin and ExternalLinks management (REOS-39). Credentials go straight into the Data Protection store: no method here returns them,
/// logs them or writes them to the audit trail (only the credential kind is audited). Every write audits one row in its transaction (rule 3).
/// Mismatch warnings come from the open Mismatch alerts the poller keeps (fingerprint of link id + rule), so they never block anything (OI-9).
/// </summary>
public sealed class ConnectorService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, ICredentialStore credentials, IConnectorFactory factory, SyncAlertWriter alerts,
    SyncOptions options, IHostEnvironment env, Func<IPAddress, bool>? isBlockedAddress = null, IRealtimePublisher? realtime = null) : ServiceBase(dbf, time, realtime)
{
    private readonly IDbContextFactory<ReleaseDbContext> _dbf = dbf;
    private readonly TimeProvider _time = time;

    private static bool KnownSource(string s) => ExternalKeys.Sources.Contains(s);

    // ---- connectors ----------------------------------------------------------------------------------------------------------------
    public async Task<IReadOnlyList<ConnectorView>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var now = Now;
        var since = await SyncSupport.WindowOpenSinceAsync(_dbf, now, ct);
        var threshold = SyncIntervals.StallThreshold(now, since, options.Poll, options.WindowPoll);
        var states = await db.Set<ConnectorState>().AsNoTracking().ToDictionaryAsync(s => s.SourceSystem, ct);
        var open = await db.Set<SyncAlerts>().AsNoTracking().Where(a => !a.IsResolved).Select(a => new { a.SourceSystem, a.Fingerprint }).ToListAsync(ct);
        var links = await db.Set<ExternalLinks>().AsNoTracking().Select(l => new { l.SourceSystem, l.SyncState, l.LastSyncedAt }).ToListAsync(ct);

        var list = new List<ConnectorView>();
        foreach (var src in ExternalKeys.Sources)
        {
            states.TryGetValue(src, out var s);
            var kind = await credentials.KindAsync(src, ct);
            var stalled = open.Any(a => a.Fingerprint == SyncAlertWriter.Fingerprint("SyncEngine", "Stalled", SyncPollerService.StallKey(src)));
            var mine = links.Where(l => l.SourceSystem == src).ToList();
            var status = s is null || string.IsNullOrWhiteSpace(s.BaseUrl) ? "NotConfigured"
                : !s.IsEnabled ? "Disabled"
                : kind is null ? "NoCredentials"
                : stalled ? "Stalled"
                : s.ConsecutiveFailures > 0 ? "Failing"
                : s.LastSuccessAt is null ? "NeverSynced"
                : "Ok";
            list.Add(new(src, s?.BaseUrl ?? "", s?.IsEnabled ?? false, kind is not null, kind, status,
                s?.LastCycleStartedAt, s?.LastCycleCompletedAt, s?.LastSuccessAt, s?.ConsecutiveFailures ?? 0,
                open.Count(a => a.SourceSystem == src), mine.Count, mine.Count(l => SyncIntervals.IsStale(l.LastSyncedAt, now, threshold)),
                mine.Count(l => l.SyncState == "Mismatch"), s?.Version));
        }
        return list;
    }

    /// <summary>Sets the base URL and enabled flag. Creates the row on first use; later edits need the row's Version (If-Match).</summary>
    public async Task<ServiceResult<ConnectorView>> SaveSettingsAsync(string source, string? baseUrl, bool? isEnabled, Actor actor, int? expectedVersion, CancellationToken ct = default)
    {
        if (!KnownSource(source)) return ServiceResult<ConnectorView>.NotFound("connector");
        if (baseUrl is not null)
        {
            var problem = ConnectorUrlPolicy.Validate(baseUrl, env.IsDevelopment(), options, isBlockedAddress);
            if (problem is not null) return ServiceResult<ConnectorView>.Fail(new GuardFailure(ConnectorGuards.ConnectorInvalid, problem));
        }
        var r = await RunAsync<bool>(async db =>
        {
            var s = await db.Set<ConnectorState>().SingleOrDefaultAsync(x => x.SourceSystem == source, ct);
            if (s is null)
            {
                if (baseUrl is null) return ServiceResult<bool>.Fail(new GuardFailure(ConnectorGuards.ConnectorInvalid, "Enter the base URL first"));
                db.Set<ConnectorState>().Add(new ConnectorState { SourceSystem = source, BaseUrl = baseUrl.Trim(), IsEnabled = isEnabled ?? true });
                Audit(db, actor, null, "ConnectorState", source, "Create", after: new { source, baseUrl = baseUrl.Trim(), isEnabled = isEnabled ?? true });
            }
            else
            {
                if (VersionMismatch(expectedVersion, s.Version)) return ServiceResult<bool>.Conflict(s);
                var before = new { s.BaseUrl, s.IsEnabled, s.Version };
                if (baseUrl is not null) s.BaseUrl = baseUrl.Trim();
                if (isEnabled is bool en) s.IsEnabled = en;
                s.Version++;
                Audit(db, actor, null, "ConnectorState", source, "Update", before, new { s.BaseUrl, s.IsEnabled, s.Version });
            }
            await db.SaveChangesAsync(ct);
            return ServiceResult<bool>.Ok(true);
        }, ct);
        if (!r.IsOk) return Carry<ConnectorView>(r);
        return ServiceResult<ConnectorView>.Ok((await ListAsync(ct)).Single(c => c.Source == source));
    }

    /// <summary>Write-only. Only the kind is audited; the username and secret are protected and stored, never echoed.</summary>
    public async Task<ServiceResult<ConnectorView>> SetCredentialsAsync(string source, string? kind, string? username, string? secret, Actor actor, CancellationToken ct = default)
    {
        if (!KnownSource(source)) return ServiceResult<ConnectorView>.NotFound("connector");
        var allowed = source == "Jira" ? new[] { ConnectorCredentials.ApiToken } : new[] { ConnectorCredentials.OAuth, ConnectorCredentials.Basic };
        if (kind is null || !allowed.Contains(kind))
            return ServiceResult<ConnectorView>.Fail(new GuardFailure(ConnectorGuards.CredentialsInvalid, $"{source} credentials are of kind {string.Join(" or ", allowed)}"));
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(secret))
            return ServiceResult<ConnectorView>.Fail(new GuardFailure(ConnectorGuards.CredentialsInvalid, kind == ConnectorCredentials.ApiToken ? "Enter the account email and the API token"
                : kind == ConnectorCredentials.OAuth ? "Enter the client id and the client secret" : "Enter the user name and the password"));
        if (username.Length > 256 || secret.Length > 2048)
            return ServiceResult<ConnectorView>.Fail(new GuardFailure(ConnectorGuards.CredentialsInvalid, "The user name or secret is too long"));

        await credentials.SetAsync(source, new ConnectorCredentials(kind, username.Trim(), secret), ct);
        var w = await RunAsync<bool>(async db =>
        {
            Audit(db, actor, null, "ConnectorCredentials", source, "Set", after: new { source, kind });   // never the values
            await db.SaveChangesAsync(ct);
            return ServiceResult<bool>.Ok(true);
        }, ct);
        if (!w.IsOk) return Carry<ConnectorView>(w);
        return ServiceResult<ConnectorView>.Ok((await ListAsync(ct)).Single(c => c.Source == source));
    }

    public async Task<ServiceResult<ConnectorView>> ClearCredentialsAsync(string source, Actor actor, CancellationToken ct = default)
    {
        if (!KnownSource(source)) return ServiceResult<ConnectorView>.NotFound("connector");
        var had = await credentials.ClearAsync(source, ct);
        var w = await RunAsync<bool>(async db =>
        {
            Audit(db, actor, null, "ConnectorCredentials", source, "Cleared", after: new { source, had });
            await db.SaveChangesAsync(ct);
            return ServiceResult<bool>.Ok(true);
        }, ct);
        if (!w.IsOk) return Carry<ConnectorView>(w);
        return ServiceResult<ConnectorView>.Ok((await ListAsync(ct)).Single(c => c.Source == source));
    }

    /// <summary>One bounded read against the saved base URL with the saved credentials. A failure is a readable 422; it is not a background failure, so it raises no alert (Q-039e).</summary>
    public async Task<ServiceResult<TestOutcome>> TestAsync(string source, Actor actor, CancellationToken ct = default)
    {
        if (!KnownSource(source)) return ServiceResult<TestOutcome>.NotFound("connector");
        string? url;
        await using (var db = await _dbf.CreateDbContextAsync(ct))
            url = await db.Set<ConnectorState>().AsNoTracking().Where(s => s.SourceSystem == source).Select(s => s.BaseUrl).SingleOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(url))
            return ServiceResult<TestOutcome>.Fail(new GuardFailure(ConnectorGuards.ConnectorNotConfigured, $"Save the {source} base URL first"));

        var t0 = _time.GetTimestamp();
        string? error = null; string outcome = "Ok";
        try
        {
            var connector = await factory.CreateAsync(source, url, ct);
            await connector.PingAsync(ct);
        }
        catch (ConnectorException ex) { error = ex.Message; outcome = ex.Kind.ToString(); }
        var ms = (long)_time.GetElapsedTime(t0).TotalMilliseconds;

        var w = await RunAsync<bool>(async db =>
        {
            Audit(db, actor, null, "ConnectorState", source, "Tested", after: new { source, outcome });
            await db.SaveChangesAsync(ct);
            return ServiceResult<bool>.Ok(true);
        }, ct);
        if (!w.IsOk) return Carry<TestOutcome>(w);
        return error is null ? ServiceResult<TestOutcome>.Ok(new(true, source, ms))
            : ServiceResult<TestOutcome>.Fail(new GuardFailure(ConnectorGuards.ConnectorTestFailed, error));
    }

    // ---- links ---------------------------------------------------------------------------------------------------------------------
    public async Task<ServiceResult<IReadOnlyList<LinkView>>> ListLinksAsync(string trainId, CancellationToken ct = default)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        if (!await db.Set<ReleaseTrains>().AnyAsync(t => t.Id == trainId, ct)) return ServiceResult<IReadOnlyList<LinkView>>.NotFound("train");
        var now = Now;
        var since = await SyncSupport.WindowOpenSinceAsync(_dbf, now, ct);
        var threshold = SyncIntervals.StallThreshold(now, since, options.Poll, options.WindowPoll);
        var links = await db.Set<ExternalLinks>().AsNoTracking().Where(l => l.ReleaseTrainId == trainId).OrderBy(l => l.SourceSystem).ThenBy(l => l.ExternalKey).ToListAsync(ct);
        var openMismatch = (await db.Set<SyncAlerts>().AsNoTracking().Where(a => a.ReleaseTrainId == trainId && a.Kind == "Mismatch" && !a.IsResolved)
            .Select(a => new { a.Fingerprint, a.ErrorMessage, a.FirstOccurredAt }).ToListAsync(ct)).ToDictionary(a => a.Fingerprint);

        var view = links.Select(l =>
        {
            var warnings = new List<LinkWarning>();
            foreach (var rule in MismatchRules.All)
                if (openMismatch.TryGetValue(SyncAlertWriter.Fingerprint(l.SourceSystem, "Mismatch", $"{l.Id}:{rule}"), out var a)) warnings.Add(new(rule, a.ErrorMessage, a.FirstOccurredAt));
            return new LinkView(l.Id, l.ReleaseTrainId, l.EntityType, l.EntityId, l.SourceSystem, l.ExternalKey, l.ExpectedStatus, l.LastSyncedStatus, l.LastSyncedAt, l.SyncState,
                SyncIntervals.IsStale(l.LastSyncedAt, now, threshold), l.Version, warnings);
        }).ToList();
        return ServiceResult<IReadOnlyList<LinkView>>.Ok(view);
    }

    public async Task<ServiceResult<LinkView>> AddLinkAsync(string trainId, string entityType, string entityId, string source, string key, Actor actor, CancellationToken ct = default)
    {
        if (!KnownSource(source)) return ServiceResult<LinkView>.Fail(new GuardFailure(ConnectorGuards.LinkInvalid, "The source system must be Jira or ServiceNow"));
        key = ExternalKeys.Normalize(source, key ?? "");
        if (ExternalKeys.Validate(source, key) is string bad) return ServiceResult<LinkView>.Fail(new GuardFailure(ConnectorGuards.LinkInvalid, bad));
        string[] types = ["Train", "Product", "Gate", "RunbookStep", "Blocker", "KnownIssue"];
        if (!types.Contains(entityType)) return ServiceResult<LinkView>.Fail(new GuardFailure(ConnectorGuards.LinkInvalid, $"The linked item must be one of {string.Join(", ", types)}"));

        var id = "";
        var r = await RunAsync<bool>(async db =>
        {
            if (!await db.Set<ReleaseTrains>().AnyAsync(t => t.Id == trainId, ct)) return ServiceResult<bool>.NotFound("train");
            var exists = entityType switch
            {
                "Train" => entityId == trainId,
                "Product" => await db.Set<BundledProducts>().AnyAsync(x => x.Id == entityId && x.ReleaseTrainId == trainId, ct),
                "Gate" => await db.Set<StageGates>().AnyAsync(x => x.Id == entityId && x.ReleaseTrainId == trainId, ct),
                "RunbookStep" => await db.Set<RunbookSteps>().AnyAsync(x => x.Id == entityId && x.ReleaseTrainId == trainId, ct),
                "Blocker" => await db.Set<Blockers>().AnyAsync(x => x.Id == entityId && x.ReleaseTrainId == trainId, ct),
                _ => await db.Set<KnownIssues>().AnyAsync(x => x.Id == entityId && x.ReleaseTrainId == trainId, ct),
            };
            if (!exists) return ServiceResult<bool>.Fail(new GuardFailure(ConnectorGuards.LinkInvalid, $"That {entityType} is not part of this train"));
            if (await db.Set<ExternalLinks>().AnyAsync(l => l.SourceSystem == source && l.ExternalKey == key && l.EntityType == entityType && l.EntityId == entityId, ct))
                return ServiceResult<bool>.Fail(new GuardFailure(ConnectorGuards.LinkExists, $"{key} is already linked to this item"));
            var link = new ExternalLinks { ReleaseTrainId = trainId, EntityType = entityType, EntityId = entityId, SourceSystem = source, ExternalKey = key };
            db.Set<ExternalLinks>().Add(link);
            Audit(db, actor, trainId, "ExternalLink", link.Id, "Add", after: new { entityType, entityId, source, key });
            await db.SaveChangesAsync(ct);
            id = link.Id;
            return ServiceResult<bool>.Ok(true);
        }, ct);
        if (!r.IsOk) return Carry<LinkView>(r);
        return ServiceResult<LinkView>.Ok((await ListLinksAsync(trainId, ct)).Value!.Single(l => l.Id == id));
    }

    public async Task<ServiceResult<bool>> RemoveLinkAsync(string linkId, Actor actor, int? expectedVersion, CancellationToken ct = default)
    {
        string? source = null;
        var r = await RunAsync<bool>(async db =>
        {
            var l = await db.Set<ExternalLinks>().SingleOrDefaultAsync(x => x.Id == linkId, ct);
            if (l is null) return ServiceResult<bool>.NotFound("link");
            if (VersionMismatch(expectedVersion, l.Version)) return ServiceResult<bool>.Conflict(l);
            db.Set<ExternalLinks>().Remove(l);
            source = l.SourceSystem;
            Audit(db, actor, l.ReleaseTrainId, "ExternalLink", l.Id, "Remove", before: new { l.EntityType, l.EntityId, l.SourceSystem, l.ExternalKey, l.SyncState });
            await db.SaveChangesAsync(ct);
            return ServiceResult<bool>.Ok(true);
        }, ct);
        if (!r.IsOk) return r;
        // the link is gone, so its alerts can never resolve on a cycle: close them now (history stays)
        var keys = new List<(string Source, string Kind, string Key)> { (source!, "NotFound", linkId), (source!, "ParseError", linkId) };
        keys.AddRange(MismatchRules.All.Select(rule => (source!, "Mismatch", $"{linkId}:{rule}")));
        await alerts.ResolveAsync(keys, ct);
        return r;
    }

    private static ServiceResult<T> Carry<T>(ServiceResult<bool> r) => new(r.Kind, default, r.Failures, r.Current, r.Missing);
}
