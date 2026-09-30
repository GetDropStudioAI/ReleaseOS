using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Sync;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Reminders;

namespace ReleaseMgmt.Infrastructure.Sync;

/// <summary>Outcome of one connector's cycle, for tests, "sync now" and the log.</summary>
public sealed record ConnectorCycleResult(string Source, string Outcome, int LinksChecked, string? ErrorKind = null, string? Message = null)
{
    public const string Clean = "Clean", Failed = "Failed", Backoff = "Backoff", Skipped = "Skipped", Idle = "Idle";
}

/// <summary>
/// The sync poller (PROJECT_SCOPE 5.3, D11): every <c>Sync:PollSeconds</c> (300), every <c>Sync:WindowPollSeconds</c> (60) while any deployment window is open,
/// each enabled connector reads the state of every ExternalLink of a non-archived train and stamps it (LastSyncedAt only on success). Read-only against the ITSM.
///
/// Failures are classified (<see cref="ConnectorErrorClassifier"/>) and upserted into SyncAlerts by fingerprint hash(system, kind, key) through
/// <see cref="SyncAlertWriter"/>, once per cycle per (kind, key), so 288 identical failures leave one row with OccurrenceCount 288.
/// Connector-wide failures (auth, unreachable, throttled) stop the cycle and share the key "connector"; NotFound and ParseError are per link (key = link id).
/// A 429 backs off exponentially (capped at one interval) and raises RateLimited only after 3 consecutive throttled cycles. One clean cycle auto-resolves
/// the connector's and each successfully read link's open alerts; the rows stay as history. Mismatch rules (5.3.7) raise Mismatch warnings only.
///
/// <see cref="RunOnceAsync"/> is the whole cycle and reads only the injected TimeProvider, so tests drive it on a fake clock.
/// </summary>
public sealed class SyncPollerService : BackgroundService
{
    public static string StallKey(string source) => "poller:" + source;
    public static string CycleKey(string source) => "cycle:" + source;

    private readonly IDbContextFactory<ReleaseDbContext> _dbf;
    private readonly TimeProvider _time;
    private readonly IConnectorFactory _factory;
    private readonly SyncAlertWriter _alerts;
    private readonly IAlertSink _sink;
    private readonly SyncCycleWriter _writer;
    private readonly SyncOptions _o;
    private readonly IConfiguration _config;
    private readonly ILogger<SyncPollerService> _log;

    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly Dictionary<string, int> _throttled = [];            // consecutive 429 cycles per source (in memory: a restart starts counting again, Q-039d)
    private readonly Dictionary<string, DateTime> _backoffUntil = [];

    public SyncPollerService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IConnectorFactory factory, SyncAlertWriter alerts, IAlertSink sink,
        SyncCycleWriter writer, SyncOptions options, IConfiguration config, ILogger<SyncPollerService> log)
    {
        _dbf = dbf; _time = time; _factory = factory; _alerts = alerts; _sink = sink; _writer = writer; _o = options; _config = config; _log = log;
    }

    private DateTime Now { get { var t = _time.GetUtcNow().UtcDateTime; return new DateTime(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc); } }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!_o.Enabled) { _log.LogWarning("Sync:Enabled is false; the sync poller is not running"); return; }
        try
        {
            if (_o.StartDelay > TimeSpan.Zero) await Task.Delay(_o.StartDelay, _time, stop);
            while (!stop.IsCancellationRequested)
            {
                try { await RunOnceAsync(stop); }
                catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogError(ex, "The sync poller's cycle failed"); }
                var open = await SyncSupport.WindowOpenSinceAsync(_dbf, Now, stop) is not null;
                await Task.Delay(SyncIntervals.PollInterval(open, _o.Poll, _o.WindowPoll), _time, stop);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    /// <summary>One pass over every enabled connector (Jira, then ServiceNow). Serialised: a "sync now" during a cycle waits for it.</summary>
    public async Task<IReadOnlyList<ConnectorCycleResult>> RunOnceAsync(CancellationToken ct = default)
    {
        await _running.WaitAsync(ct);
        try
        {
            var results = new List<ConnectorCycleResult>();
            foreach (var source in ExternalKeys.Sources) results.Add(await RunConnectorCoreAsync(source, ct));
            return results;
        }
        finally { _running.Release(); }
    }

    /// <summary>One cycle for one connector (the Connectors screen's "sync now").</summary>
    public async Task<ConnectorCycleResult> RunConnectorAsync(string source, CancellationToken ct = default)
    {
        await _running.WaitAsync(ct);
        try { return await RunConnectorCoreAsync(source, ct); }
        finally { _running.Release(); }
    }

    private async Task<ConnectorCycleResult> RunConnectorCoreAsync(string source, CancellationToken ct)
    {
        var started = Now;
        try
        {
            var cfgUrl = _config[$"Connectors:{source}:BaseUrl"];
            if (!string.IsNullOrWhiteSpace(cfgUrl)) await _writer.EnsureRowAsync(source, cfgUrl, ct);
            return await CycleAsync(source, started, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Sync cycle for {Source} failed unexpectedly", source);
            var msg = $"The {source} sync cycle failed unexpectedly: {ex.GetType().Name}";
            try { await RaiseAsync("SyncEngine", "Stalled", CycleKey(source), msg, null, ct); }
            catch (Exception inner) when (inner is not OperationCanceledException) { _log.LogError(inner, "Could not record the alert for the failed {Source} cycle", source); }
            return new(source, ConnectorCycleResult.Failed, 0, "Engine", msg);
        }
    }

    private sealed record LinkRow(string Id, string TrainId, string EntityType, string Key, string SyncState, string TrainStatus, DateOnly Target, DateTime? EndedAt, DateTime? WStart, DateTime? WEnd);

    private async Task<ConnectorCycleResult> CycleAsync(string source, DateTime started, CancellationToken ct)
    {
        ConnectorState? state; List<LinkRow> links;
        await using (var db = await _dbf.CreateDbContextAsync(ct))
        {
            state = await db.Set<ConnectorState>().AsNoTracking().SingleOrDefaultAsync(s => s.SourceSystem == source, ct);
            if (state is null || !state.IsEnabled) return new(source, ConnectorCycleResult.Skipped, 0);
            links = await (from l in db.Set<ExternalLinks>().AsNoTracking()
                           join t in db.Set<ReleaseTrains>().AsNoTracking() on l.ReleaseTrainId equals t.Id
                           join w in db.Set<DeploymentWindows>().AsNoTracking() on t.Id equals w.ReleaseTrainId into ws
                           from w in ws.DefaultIfEmpty()
                           where l.SourceSystem == source && t.ArchivedAt == null
                           orderby l.Id
                           select new LinkRow(l.Id, t.Id, l.EntityType, l.ExternalKey, l.SyncState, t.CurrentStatus, t.TargetReleaseDate, t.ActualEndAt,
                                              w == null ? null : w.StartsAt, w == null ? null : w.EndsAt)).ToListAsync(ct);
        }

        if (links.Count == 0)
        {
            // Nothing to read: no connector is built and no network call is made, so this proves nothing about the connector (no auto-resolve, no LastSuccessAt).
            // The cycle still completes, which is the poller's heartbeat for the watchdog.
            await _writer.ApplyAsync(source, started, connectorFailed: false, clean: false, [], ct);
            return new(source, ConnectorCycleResult.Idle, 0);
        }

        if (_backoffUntil.TryGetValue(source, out var until) && Now < until)
        {
            await _writer.ApplyAsync(source, started, connectorFailed: false, clean: false, [], ct);   // alive, but not a clean cycle
            return new(source, ConnectorCycleResult.Backoff, 0, "RateLimited", $"Backing off until {until:HH:mm:ss}Z after HTTP 429");
        }

        var updates = new List<LinkUpdate>();
        var raise = new List<(string Kind, string Key, string Msg, string? Train)>();
        var resolve = new List<(string Source, string Kind, string Key)>();
        ConnectorException? wide = null;
        var checkedCount = 0;

        try
        {
            var connector = await _factory.CreateAsync(source, state.BaseUrl, ct);
            foreach (var l in links)
            {
                checkedCount++;
                try
                {
                    var status = await connector.FetchStatusAsync(l.Key, ct);
                    var now = Now;
                    var findings = MismatchRules.Evaluate(new MismatchInput(source, l.Key, status, l.TrainStatus, l.Target, l.EndedAt, l.WStart, l.WEnd, now), _o.WindowTolerance);
                    updates.Add(new LinkUpdate(l.Id, findings.Count > 0 ? "Mismatch" : "InSync", status.State, MismatchRules.ExpectedStatus(source, l.Key, l.TrainStatus), now));
                    resolve.Add((source, "NotFound", l.Id)); resolve.Add((source, "ParseError", l.Id));
                    foreach (var f in findings) raise.Add(("Mismatch", $"{l.Id}:{f.Rule}", f.Message, l.TrainId));
                    foreach (var rule in MismatchRules.All.Where(r => findings.All(f => f.Rule != r))) resolve.Add((source, "Mismatch", $"{l.Id}:{rule}"));
                }
                catch (ConnectorException ex) when (!ConnectorErrorClassifier.IsConnectorWide(ex.Kind))
                {
                    updates.Add(new LinkUpdate(l.Id, ConnectorErrorClassifier.LinkState(ex.Kind), null, null, null));
                    raise.Add((ConnectorErrorClassifier.AlertKind(ex.Kind), l.Id, $"{l.Key}: {ex.Message}", l.TrainId));
                }
            }
        }
        catch (ConnectorException ex) { wide = ex; }

        if (wide is null)
        {
            _throttled.Remove(source); _backoffUntil.Remove(source);
            foreach (var kind in new[] { "AuthFailed", "Unreachable", "RateLimited" }) resolve.Add((source, kind, ConnectorErrorClassifier.ConnectorKey));
            resolve.Add(("SyncEngine", "Stalled", CycleKey(source)));
            await _writer.ApplyAsync(source, started, connectorFailed: false, clean: true, updates, ct);
            foreach (var r in raise) await RaiseAsync(source, r.Kind, r.Key, r.Msg, r.Train, ct);
            await _alerts.ResolveAsync(resolve, ct);
            return new(source, ConnectorCycleResult.Clean, checkedCount);
        }

        // connector-wide failure: the cycle stopped at the first link; every remaining link is stale by definition
        var failedState = ConnectorErrorClassifier.LinkState(wide.Kind);
        if (failedState is not null) updates.AddRange(links.Where(l => l.SyncState != failedState && updates.All(u => u.LinkId != l.Id)).Select(l => new LinkUpdate(l.Id, failedState, null, null, null)));
        await _writer.ApplyAsync(source, started, connectorFailed: true, clean: false, updates, ct);
        foreach (var r in raise) await RaiseAsync(source, r.Kind, r.Key, r.Msg, r.Train, ct);   // link-level findings that happened before the stop

        var alertKind = ConnectorErrorClassifier.AlertKind(wide.Kind);
        var raiseNow = true;
        if (wide.Kind == ConnectorErrorKind.RateLimited)
        {
            var n = _throttled[source] = _throttled.GetValueOrDefault(source) + 1;
            var open = await SyncSupport.WindowOpenSinceAsync(_dbf, Now, ct) is not null;
            var wait = SyncIntervals.Backoff(n, _o.BackoffBase, wide.RetryAfter, SyncIntervals.PollInterval(open, _o.Poll, _o.WindowPoll));
            _backoffUntil[source] = Now + wait;
            raiseNow = n >= SyncIntervals.RateLimitAlertAfter;
        }
        else { _throttled.Remove(source); _backoffUntil.Remove(source); }

        _log.LogWarning("{Source} sync cycle failed: {Kind} {Message}", source, wide.Kind, wide.Message);
        if (raiseNow) await RaiseAsync(source, alertKind, ConnectorErrorClassifier.ConnectorKey, wide.Message, null, ct);
        return new(source, ConnectorCycleResult.Failed, checkedCount, alertKind, wide.Message);
    }

    private async Task RaiseAsync(string source, string kind, string key, string message, string? trainId, CancellationToken ct)
    {
        await _sink.RaiseAsync(source, kind, key, message, ct);
        await _alerts.RaiseAsync(source, kind, key, message, trainId, ct);
    }
}
