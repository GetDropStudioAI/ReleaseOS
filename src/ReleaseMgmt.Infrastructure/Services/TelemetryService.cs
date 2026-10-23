using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Reminders;

namespace ReleaseMgmt.Infrastructure.Services;

/// <summary>One pushed measurement. <see cref="Series"/> is Canary or Stable (Stable when left out); <see cref="At"/> is the tool's clock (our clock when left out).</summary>
public sealed record IngestSample(string? Series = null, DateTime? At = null, double? ErrorRatePct = null, double? LatencyP95Ms = null, double? AvailabilityPct = null, long? RequestCount = null);

public sealed record IngestSamplesResult(string DeploymentId, string ExternalId, int Accepted, int Duplicates, int Late, int BreachesOpened, int BreachesConfirmed, int BreachesResolved, int Version);

public sealed record MetricPoint(string At, double? ErrorRatePct, double? LatencyP95Ms, double? AvailabilityPct, long? RequestCount, int Samples);
public sealed record MetricSeriesView(string Series, IReadOnlyList<MetricPoint> Points);
public sealed record RollupPoint(string Series, string BucketAt, int Samples, double? ErrorRateAvgPct, double? ErrorRateMaxPct, double? LatencyP95AvgMs, double? LatencyP95MaxMs,
    double? AvailabilityAvgPct, double? AvailabilityMinPct, long? RequestCount);
public sealed record ThresholdLines(double? ErrorRateMaxPct, double? LatencyP95MaxMs, double? AvailabilityMinPct, int ForSeconds);
public sealed record SloTargets(double? AvailabilityPct, double? LatencyP95Ms, double? ErrorRatePct);

/// <summary>Status: Watching (beyond the threshold, not yet for its duration), Breached (confirmed, still beyond) or Resolved.</summary>
public sealed record BreachView(string Id, string DeploymentId, string Series, string Metric, double ThresholdValue, int ForSeconds, string StartedAt, string? ConfirmedAt, string? DetectedAt,
    string? ResolvedAt, double PeakValue, double LastValue, int SampleCount, string Status, int Version);

/// <summary>The series a chart draws, with the threshold lines and the time the canary began. Points are raw samples, or buckets (BucketSeconds above 0: worst value in the bucket) when the range holds more than MaxPoints.</summary>
public sealed record DeploymentMetrics(string DeploymentId, string ExternalId, string Strategy, string State, string EnvironmentId, string EnvironmentName, string? StartedAt, string? CanaryStartedAt,
    string? StepStartedAt, string? FinishedAt, ThresholdLines Thresholds, SloTargets Slo, string From, string To, int BucketSeconds, bool Truncated, string RetainsFrom, string? LastSampleAt,
    IReadOnlyList<MetricSeriesView> Series, IReadOnlyList<RollupPoint> Rollups, IReadOnlyList<BreachView> Breaches);

public sealed record CanaryCheck(string Metric, double? Value, double? Threshold, bool? Passed, string? BreachId);

/// <summary>Result: Pass (canary samples exist, none beyond a threshold), Fail (a confirmed breach on the canary series), Unknown (no canary samples or no thresholds). AtRisk: beyond a threshold but not yet for its duration.</summary>
public sealed record CanaryState(string DeploymentId, string Strategy, bool IsCanary, string State, IReadOnlyList<double> Steps, int CurrentStep, int StepCount, double TrafficPercent, double? NextTrafficPercent,
    string? CanaryStartedAt, string? StepStartedAt, int ObservationWindowSeconds, int ObservedSeconds, bool WindowComplete, string? NextPromotionAt, string Result, bool AtRisk, bool ReadyToPromote,
    string? LastSampleAt, IReadOnlyList<CanaryCheck> Checks, bool AutoRollbackArmed);

public sealed record SloMetric(double? Objective, double? Measured, bool? Meets);
public sealed record EnvironmentSlo(string EnvironmentId, string EnvironmentName, int Days, string From, string To, int Samples, int RolledUpSamples, SloMetric Availability, SloMetric LatencyP95, SloMetric ErrorRate,
    ErrorBudget? ErrorBudget);

public sealed record RollbackStatus(bool Armed, string? ArmedAt, bool WebhookConfigured, bool SigningSecretSet, bool CanArm, string? WhyNot);

public sealed record LatestSample(string Series, string At, double? ErrorRatePct, double? LatencyP95Ms, double? AvailabilityPct, long? RequestCount);

/// <summary>Everything the Deployment monitoring screen needs in one read.</summary>
public sealed record DeploymentMonitoring(DeploymentView Deployment, ThresholdLines Thresholds, SloTargets Slo, CanaryState Canary, IReadOnlyList<BreachView> Breaches, RollbackStatus Rollback,
    IReadOnlyList<ControlActionView> Actions, IReadOnlyList<LatestSample> Latest, EnvironmentSlo? SloStatus);

public sealed record MetricQuery(string? Series = null, DateTime? From = null, DateTime? To = null, int MaxPoints = 600);

/// <summary>
/// Telemetry (REOS-111, D42): the metric samples a deployment tool pushes, breach detection against the environment's thresholds, canary analysis, SLO and error-budget
/// maths, and the reads the monitoring screen binds to. A sample is evaluated in the transaction that stores it, so a breach is confirmed by the sample that completes its
/// duration (at most one sample interval after the duration is reached). A breach is Watching from the first sample beyond a threshold; Breached once that has lasted
/// <c>Environments.ThresholdForSeconds</c> (the span of the samples' own timestamps); Resolved by the first sample back inside. Samples older than the newest already stored for
/// the same series (late arrivals) are kept but not evaluated. Everything is workspace-scoped (the ingest key's workspace, or the signed-in person's); a replay changes nothing.
/// Every write is one transaction with one <c>MetricSample</c> audit row plus one <c>ThresholdBreach</c> row per breach event, and a Version bump on the deployment.
/// </summary>
public sealed class TelemetryService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IConfiguration config, IRealtimePublisher? realtime = null,
    DeploymentControlService? control = null, SyncAlertWriter? alerts = null)
    : ServiceBase(dbf, time, realtime)
{
    private const string Entity = "MetricSample", BreachEntity = "ThresholdBreach";
    private readonly IRealtimePublisher? _live = realtime;
    public const int DefaultRetentionDays = 30, HardMaxSamplesPerCall = 1000, DefaultMaxPoints = 600, MaxPointsCap = 2000, RawRowCap = 250_000, FutureSkewMinutes = 5;

    public int RetentionDays => Math.Clamp(config.GetValue("Telemetry:RetentionDays", DefaultRetentionDays), 1, 3650);
    private int MaxSamplesPerCall => Math.Clamp(config.GetValue("Telemetry:MaxSamplesPerCall", 200), 1, HardMaxSamplesPerCall);

    private static GuardFailure Bad(string m) => new(DeploymentGuards.MetricInvalid, m);
    private static string? Iso(DateTime? t) => t?.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
    private static DateTime Trunc(DateTime t) { t = t.ToUniversalTime(); return new DateTime(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc); }

    // ---- ingest ---------------------------------------------------------------------------------------------------------------------

    public async Task<ServiceResult<IngestSamplesResult>> IngestAsync(string? deploymentExternalId, IReadOnlyList<IngestSample>? samples, IngestPrincipal key, CancellationToken ct = default)
    {
        using var _ = WorkspaceScope.Enter(key.WorkspaceId);
        var confirmed = new List<string>();
        var r = await RunAsync(db => IngestCoreAsync(db, deploymentExternalId, samples, key, confirmed, ct), ct);
        if (r.Kind == ResultKind.GuardFailed && r.Failures.Any(f => f.Guard == Guards.DbRule && f.Message.Contains("UNIQUE constraint failed", StringComparison.Ordinal)))
            r = await RunAsync(db => IngestCoreAsync(db, deploymentExternalId, samples, key, confirmed, ct), ct);   // two identical calls at once: the loser is now a replay
        if (!r.IsOk) return r;
        if (r.Value!.Accepted > 0 && _live is not null)
        {
            await using var db = await OpenAsync(ct);
            var trainId = await db.Set<Deployments>().Where(x => x.Id == r.Value.DeploymentId).Select(x => x.ReleaseTrainId).SingleOrDefaultAsync(ct);
            await _live.DeploymentChangedAsync(key.WorkspaceId, r.Value.DeploymentId, r.Value.Version, trainId, ct);
        }
        // After the commit: a confirmed breach on an armed deployment calls the rollback webhook, once per breach. The control service records its own failures (action row, alert, push);
        // anything that still escapes is raised here, never swallowed (CLAUDE.md rule 8).
        if (control is not null)
            foreach (var breachId in confirmed)
            {
                try { await control.OnBreachConfirmedAsync(breachId, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (alerts is null) throw;
                    await alerts.RaiseAsync("Deployment", "ControlFailed", $"auto-rollback:{breachId}", $"Automatic rollback for breach {breachId} could not be started: {ex.GetType().Name}", ct: ct);
                }
            }
        return r;
    }

    private async Task<ServiceResult<IngestSamplesResult>> IngestCoreAsync(ReleaseDbContext db, string? externalId, IReadOnlyList<IngestSample>? samples, IngestPrincipal key, List<string> confirmed, CancellationToken ct)
    {
        confirmed.Clear();
        if (string.IsNullOrWhiteSpace(externalId)) return ServiceResult<IngestSamplesResult>.Fail(Bad("deploymentExternalId is required"));
        if (samples is null || samples.Count == 0) return ServiceResult<IngestSamplesResult>.Fail(Bad("Send at least one sample"));
        if (samples.Count > MaxSamplesPerCall) return ServiceResult<IngestSamplesResult>.Fail(Bad($"At most {MaxSamplesPerCall} samples in one call"));
        var now = Now;
        var oldest = now.AddDays(-RetentionDays);
        var f = new List<GuardFailure>();
        var clean = new List<(string Series, DateTime At, double? Er, double? Lat, double? Av, long? Req)>();
        for (var i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            var series = s.Series ?? MetricSeries.Stable;
            if (!MetricSeries.All.Contains(series)) f.Add(Bad($"samples[{i}].series is one of {string.Join(", ", MetricSeries.All)}"));
            var at = Trunc(s.At ?? now);
            if (at > now.AddMinutes(FutureSkewMinutes)) f.Add(Bad($"samples[{i}].at is in the future (more than {FutureSkewMinutes} minutes ahead of this server's clock)"));
            else if (at < oldest) f.Add(Bad($"samples[{i}].at is older than the {RetentionDays} days telemetry is kept"));
            static bool Bad01(double? v, double max) => v is { } x && (double.IsNaN(x) || double.IsInfinity(x) || x < 0 || x > max);
            if (Bad01(s.ErrorRatePct, 100)) f.Add(Bad($"samples[{i}].errorRatePct is a percentage between 0 and 100"));
            if (s.LatencyP95Ms is { } l && (double.IsNaN(l) || double.IsInfinity(l) || l < 0)) f.Add(Bad($"samples[{i}].latencyP95Ms is 0 or more milliseconds"));
            if (Bad01(s.AvailabilityPct, 100)) f.Add(Bad($"samples[{i}].availabilityPct is a percentage between 0 and 100"));
            if (s.RequestCount is < 0) f.Add(Bad($"samples[{i}].requestCount is 0 or more"));
            if (s.ErrorRatePct is null && s.LatencyP95Ms is null && s.AvailabilityPct is null && s.RequestCount is null) f.Add(Bad($"samples[{i}] carries no metric"));
            clean.Add((series, at, s.ErrorRatePct, s.LatencyP95Ms, s.AvailabilityPct, s.RequestCount));
        }
        if (f.Count > 0) return ServiceResult<IngestSamplesResult>.Fail(f);

        var dep = await db.Set<Deployments>().SingleOrDefaultAsync(x => x.ExternalId == externalId, ct);
        if (dep is null) return ServiceResult<IngestSamplesResult>.NotFound("deployment");
        var env = await db.Set<Environments>().AsNoTracking().SingleAsync(e => e.Id == dep.EnvironmentId, ct);
        var thresholds = new Thresholds(env.ErrorRateMaxPct, env.LatencyP95MaxMs, env.AvailabilityMinPct, env.ThresholdForSeconds);

        var times = clean.Select(c => c.At).Distinct().ToList();
        var have = (await db.Set<MetricSamples>().AsNoTracking().Where(m => m.DeploymentId == dep.Id && times.Contains(m.SampledAt)).Select(m => new { m.Series, m.SampledAt }).ToListAsync(ct))
            .Select(m => (m.Series, m.SampledAt)).ToHashSet();
        var newest = new Dictionary<string, DateTime>();
        foreach (var g in await db.Set<MetricSamples>().AsNoTracking().Where(m => m.DeploymentId == dep.Id).GroupBy(m => m.Series).Select(g => new { Series = g.Key, At = g.Max(x => x.SampledAt) }).ToListAsync(ct))
            newest[g.Series] = g.At;

        var fresh = new List<(string Series, DateTime At, double? Er, double? Lat, double? Av, long? Req)>();
        int duplicates = 0, late = 0;
        foreach (var c in clean.OrderBy(c => c.At))
        {
            if (!have.Add((c.Series, c.At))) { duplicates++; continue; }
            db.Set<MetricSamples>().Add(new MetricSamples { DeploymentId = dep.Id, Series = c.Series, SampledAt = c.At, ReceivedAt = now, ErrorRatePct = c.Er, LatencyP95Ms = c.Lat, AvailabilityPct = c.Av, RequestCount = c.Req });
            if (newest.TryGetValue(c.Series, out var n) && c.At < n) { late++; continue; }   // stored, not evaluated: the breach state already moved past it
            fresh.Add(c);
        }
        if (fresh.Count + late == 0) return ServiceResult<IngestSamplesResult>.Ok(new(dep.Id, dep.ExternalId, 0, duplicates, 0, 0, 0, 0, dep.Version));   // a replay: nothing written

        // breach detection, oldest sample first
        var open = (await db.Set<ThresholdBreaches>().Where(b => b.DeploymentId == dep.Id && b.ResolvedAt == null).ToListAsync(ct)).ToDictionary(b => (b.Series, b.Metric));
        int opened = 0, confirmedNow = 0, resolved = 0;
        foreach (var c in fresh.OrderBy(c => c.At))
        {
            foreach (var metric in MetricNames.All)
            {
                if (ThresholdRules.ValueOf(metric, c.Er, c.Lat, c.Av) is not { } value) continue;
                var limit = thresholds.LimitOf(metric);
                var beyond = limit is { } lim && ThresholdRules.IsBeyond(metric, value, lim);
                open.TryGetValue((c.Series, metric), out var b);
                if (beyond && b is null)
                {
                    b = new ThresholdBreaches
                    {
                        Id = Ids.New(), DeploymentId = dep.Id, Series = c.Series, Metric = metric, ThresholdValue = limit!.Value, ForSeconds = thresholds.ForSeconds, StartedAt = c.At, PeakValue = value, LastValue = value,
                    };
                    db.Set<ThresholdBreaches>().Add(b);
                    open[(c.Series, metric)] = b;
                    opened++;
                    Audit(db, null, dep.ReleaseTrainId, BreachEntity, b.Id, "Open", null, BreachAudit(b, dep, key));
                }
                else if (beyond && b is not null)
                {
                    b.LastValue = value; b.PeakValue = ThresholdRules.Worse(metric, b.PeakValue, value); b.SampleCount++; b.ThresholdValue = limit!.Value; b.Version++;
                }
                else if (!beyond && b is not null)
                {
                    b.LastValue = value; b.ResolvedAt = c.At < b.StartedAt ? b.StartedAt : c.At; b.Version++;
                    open.Remove((c.Series, metric));
                    resolved++;
                    Audit(db, null, dep.ReleaseTrainId, BreachEntity, b.Id, "Resolve", null, BreachAudit(b, dep, key));
                    continue;
                }
                if (beyond && b is { ConfirmedAt: null } && (c.At - b.StartedAt).TotalSeconds >= b.ForSeconds)
                {
                    b.ConfirmedAt = c.At; b.DetectedAt = now; b.Version++;
                    confirmedNow++;
                    confirmed.Add(b.Id);
                    Audit(db, null, dep.ReleaseTrainId, BreachEntity, b.Id, "Confirm", null, BreachAudit(b, dep, key));
                }
            }
        }

        dep.UpdatedAt = now; dep.Version++;
        Audit(db, null, dep.ReleaseTrainId, Entity, dep.Id, "Append", null, new
        {
            accepted = fresh.Count + late, duplicates, late, breachesOpened = opened, breachesConfirmed = confirmedNow, breachesResolved = resolved,
            via = new { keyId = key.KeyId, key = key.KeyName },
        });
        await db.SaveChangesAsync(ct);
        return ServiceResult<IngestSamplesResult>.Ok(new(dep.Id, dep.ExternalId, fresh.Count + late, duplicates, late, opened, confirmedNow, resolved, dep.Version));
    }

    private static object BreachAudit(ThresholdBreaches b, Deployments dep, IngestPrincipal key) => new
    {
        b.Series, b.Metric, threshold = b.ThresholdValue, b.ForSeconds, startedAt = b.StartedAt, confirmedAt = b.ConfirmedAt, resolvedAt = b.ResolvedAt, peak = b.PeakValue, last = b.LastValue,
        deployment = dep.ExternalId, via = new { keyId = key.KeyId, key = key.KeyName },
    };

    // ---- reads ----------------------------------------------------------------------------------------------------------------------

    private static BreachView View(ThresholdBreaches b) => new(b.Id, b.DeploymentId, b.Series, b.Metric, b.ThresholdValue, b.ForSeconds, Iso(b.StartedAt)!, Iso(b.ConfirmedAt), Iso(b.DetectedAt), Iso(b.ResolvedAt),
        b.PeakValue, b.LastValue, b.SampleCount, b.ResolvedAt is not null ? "Resolved" : b.ConfirmedAt is not null ? "Breached" : "Watching", b.Version);

    private static ThresholdLines Lines(Environments e) => new(e.ErrorRateMaxPct, e.LatencyP95MaxMs, e.AvailabilityMinPct, e.ThresholdForSeconds);
    private static SloTargets Targets(Environments e) => new(e.SloAvailabilityPct, e.SloLatencyP95Ms, e.SloErrorRatePct);

    public async Task<List<BreachView>> BreachesAsync(string deploymentId, bool includeResolved = true, int take = 200, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var q = db.Set<ThresholdBreaches>().AsNoTracking().Where(b => b.DeploymentId == deploymentId);
        if (!includeResolved) q = q.Where(b => b.ResolvedAt == null);
        return [.. (await q.OrderByDescending(b => b.StartedAt).ThenByDescending(b => b.Id).Take(Math.Clamp(take, 1, 1000)).ToListAsync(ct)).Select(View)];
    }

    /// <summary>The chart read: series (raw or bucketed), threshold lines, SLO targets, the canary's start and the breaches. Null when the deployment does not exist in this workspace.</summary>
    public async Task<DeploymentMetrics?> MetricsAsync(string deploymentId, MetricQuery? query = null, CancellationToken ct = default)
    {
        query ??= new();
        await using var db = await OpenAsync(ct);
        var dep = await db.Set<Deployments>().AsNoTracking().SingleOrDefaultAsync(d => d.Id == deploymentId, ct);
        if (dep is null) return null;
        var env = await db.Set<Environments>().AsNoTracking().SingleAsync(e => e.Id == dep.EnvironmentId, ct);
        var now = Now;
        var retainsFrom = now.AddDays(-RetentionDays);
        var lastSampleAt = await db.Set<MetricSamples>().AsNoTracking().Where(m => m.DeploymentId == deploymentId).MaxAsync(m => (DateTime?)m.SampledAt, ct);
        var end = dep.FinishedAt ?? now;
        var to = Trunc(query.To ?? (lastSampleAt is { } ls && ls > end ? ls : end));   // by default up to the newest sample: a tool's clock may run a little ahead of ours
        var from = Trunc(query.From ?? (dep.StartedAt ?? dep.CreatedAt).AddMinutes(-5));
        if (to < from) (from, to) = (to, from);
        string[] series = query.Series is null ? MetricSeries.All : MetricSeries.All.Contains(query.Series) ? new[] { query.Series } : Array.Empty<string>();
        var maxPoints = Math.Clamp(query.MaxPoints <= 0 ? DefaultMaxPoints : query.MaxPoints, 10, MaxPointsCap);

        var rows = await db.Set<MetricSamples>().AsNoTracking().Where(m => m.DeploymentId == deploymentId && m.SampledAt >= from && m.SampledAt <= to && series.Contains(m.Series))
            .OrderBy(m => m.SampledAt).Take(RawRowCap + 1).ToListAsync(ct);
        var truncated = rows.Count > RawRowCap;
        if (truncated) rows.RemoveAt(RawRowCap);
        var span = Math.Max(1, (int)(to - from).TotalSeconds);
        var bucket = rows.Count <= maxPoints * series.Length ? 0 : (int)Math.Ceiling(span / (double)maxPoints);
        var views = series.Select(sn =>
        {
            var mine = rows.Where(m => m.Series == sn).ToList();
            if (bucket == 0) return new MetricSeriesView(sn, [.. mine.Select(m => new MetricPoint(Iso(m.SampledAt)!, m.ErrorRatePct, m.LatencyP95Ms, m.AvailabilityPct, m.RequestCount, 1))]);
            var points = mine.GroupBy(m => (long)Math.Floor((m.SampledAt - from).TotalSeconds / bucket)).OrderBy(g => g.Key).Select(g => new MetricPoint(
                Iso(from.AddSeconds(g.Key * (long)bucket))!,
                g.Where(x => x.ErrorRatePct is not null).Select(x => x.ErrorRatePct).DefaultIfEmpty().Max(),   // the worst in the bucket, so a spike is never averaged away
                g.Where(x => x.LatencyP95Ms is not null).Select(x => x.LatencyP95Ms).DefaultIfEmpty().Max(),
                g.Where(x => x.AvailabilityPct is not null).Select(x => x.AvailabilityPct).DefaultIfEmpty().Min(),
                g.Any(x => x.RequestCount is not null) ? g.Sum(x => x.RequestCount ?? 0) : null, g.Count()));
            return new MetricSeriesView(sn, [.. points]);
        }).ToList();

        var rollups = new List<RollupPoint>();
        if (from < retainsFrom)
            rollups = [.. (await db.Set<MetricRollups>().AsNoTracking().Where(r => r.DeploymentId == deploymentId && r.BucketAt >= from && r.BucketAt <= to && series.Contains(r.Series)).OrderBy(r => r.BucketAt).Take(5000).ToListAsync(ct))
                .Select(r => new RollupPoint(r.Series, Iso(r.BucketAt)!, r.Samples, r.ErrorRateAvgPct, r.ErrorRateMaxPct, r.LatencyP95AvgMs, r.LatencyP95MaxMs, r.AvailabilityAvgPct, r.AvailabilityMinPct, r.RequestCount))];
        var breaches = (await db.Set<ThresholdBreaches>().AsNoTracking().Where(b => b.DeploymentId == deploymentId).OrderBy(b => b.StartedAt).ToListAsync(ct)).Select(View).ToList();
        return new(dep.Id, dep.ExternalId, dep.Strategy, dep.State, env.Id, env.Name, Iso(dep.StartedAt), dep.Strategy == "Canary" ? Iso(dep.StartedAt) : null, Iso(dep.StepStartedAt), Iso(dep.FinishedAt),
            Lines(env), Targets(env), Iso(from)!, Iso(to)!, bucket, truncated, Iso(retainsFrom)!, Iso(lastSampleAt), views, rollups, breaches);
    }

    private static IReadOnlyList<double> StepsOf(Deployments d) => d.CanarySteps is null ? [] : JsonSerializer.Deserialize<double[]>(d.CanarySteps) ?? [];

    /// <summary>Canary analysis: step n of m, traffic, the observation window, pass or fail against the thresholds, the next promotion time. Null when the deployment does not exist here.</summary>
    public async Task<CanaryState?> CanaryAsync(string deploymentId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var dep = await db.Set<Deployments>().AsNoTracking().SingleOrDefaultAsync(d => d.Id == deploymentId, ct);
        if (dep is null) return null;
        return await CanaryCoreAsync(db, dep, ct);
    }

    private async Task<CanaryState> CanaryCoreAsync(ReleaseDbContext db, Deployments dep, CancellationToken ct)
    {
        var env = await db.Set<Environments>().AsNoTracking().SingleAsync(e => e.Id == dep.EnvironmentId, ct);
        var now = Now;
        var isCanary = dep.Strategy == "Canary";
        var steps = StepsOf(dep);
        var current = isCanary ? CanaryMath.CurrentStep(steps, dep.TrafficPercent) : 0;
        var next = isCanary ? CanaryMath.NextTraffic(steps, dep.TrafficPercent) : null;
        var window = env.CanaryObservationSeconds;
        var running = dep.State == DeploymentStates.Running;
        var stepStart = dep.StepStartedAt ?? dep.StartedAt;
        var observed = stepStart is { } ss && running ? Math.Max(0, (int)(now - ss).TotalSeconds) : 0;
        var complete = running && observed >= window;
        var nextPromotion = isCanary && running && next is not null && stepStart is { } s0 ? s0.AddSeconds(window) : (DateTime?)null;

        var watched = isCanary ? MetricSeries.Canary : MetricSeries.Stable;
        var lines = Lines(env);
        var latest = await db.Set<MetricSamples>().AsNoTracking().Where(m => m.DeploymentId == dep.Id && m.Series == watched).OrderByDescending(m => m.SampledAt).FirstOrDefaultAsync(ct);
        var breaches = await db.Set<ThresholdBreaches>().AsNoTracking().Where(b => b.DeploymentId == dep.Id && b.Series == watched && b.ResolvedAt == null).ToListAsync(ct);
        var checks = new List<CanaryCheck>();
        foreach (var metric in MetricNames.All)
        {
            var limit = new Thresholds(lines.ErrorRateMaxPct, lines.LatencyP95MaxMs, lines.AvailabilityMinPct, lines.ForSeconds).LimitOf(metric);
            var value = latest is null ? null : ThresholdRules.ValueOf(metric, latest.ErrorRatePct, latest.LatencyP95Ms, latest.AvailabilityPct);
            var b = breaches.FirstOrDefault(x => x.Metric == metric);
            bool? passed = limit is null || value is null ? null : (b is { ConfirmedAt: not null } ? false : !ThresholdRules.IsBeyond(metric, value.Value, limit.Value));
            checks.Add(new(metric, value, limit, passed, b?.Id));
        }
        var failed = breaches.Any(b => b.ConfirmedAt is not null);
        var atRisk = !failed && breaches.Count > 0;
        var evaluated = checks.Any(c => c.Passed is not null);
        var result = failed ? "Fail" : evaluated ? "Pass" : "Unknown";
        return new(dep.Id, dep.Strategy, isCanary, dep.State, steps, current, steps.Count, dep.TrafficPercent, next, Iso(isCanary ? dep.StartedAt : null), Iso(dep.StepStartedAt), window, observed, complete,
            Iso(nextPromotion), result, atRisk, running && complete && result == "Pass" && next is not null, Iso(latest?.SampledAt), checks, dep.AutoRollbackArmedAt is not null);
    }

    /// <summary>Availability against the SLO, the error budget and the other two objectives for an environment over the last <paramref name="days"/> days (samples, plus the rollups of older ones).</summary>
    public async Task<EnvironmentSlo?> SloAsync(string environmentId, int days = 30, string? serviceId = null, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var env = await db.Set<Environments>().AsNoTracking().SingleOrDefaultAsync(e => e.Id == environmentId, ct);
        return env is null ? null : await SloCoreAsync(db, env, days, serviceId, ct);
    }

    private async Task<EnvironmentSlo> SloCoreAsync(ReleaseDbContext db, Environments env, int days, string? serviceId, CancellationToken ct)
    {
        days = Math.Clamp(days, 1, 365);
        var to = Now;
        var from = to.AddDays(-days);
        var deps = db.Set<Deployments>().AsNoTracking().Where(d => d.EnvironmentId == env.Id);
        if (serviceId is not null) deps = deps.Where(d => d.CatalogServiceId == serviceId);
        var depIds = deps.Select(d => d.Id);
        var samples = await db.Set<MetricSamples>().AsNoTracking().Where(m => depIds.Contains(m.DeploymentId) && m.SampledAt >= from)   // no upper bound: nothing newer than the clock-skew allowance is ever stored
            .Select(m => new { m.ErrorRatePct, m.LatencyP95Ms, m.AvailabilityPct, m.RequestCount }).Take(RawRowCap).ToListAsync(ct);
        var rolled = await db.Set<MetricRollups>().AsNoTracking().Where(m => depIds.Contains(m.DeploymentId) && m.BucketAt >= from && m.BucketAt <= to)
            .Select(m => new { m.Samples, m.ErrorRateAvgPct, m.LatencyP95AvgMs, m.AvailabilityAvgPct, m.RequestCount }).Take(RawRowCap).ToListAsync(ct);

        // request-weighted means (an unweighted mean when no weights were pushed)
        double? Mean(IEnumerable<(double? V, double W)> items)
        {
            var list = items.Where(i => i.V is not null).ToList();
            if (list.Count == 0) return null;
            var w = list.Sum(i => i.W);
            return w > 0 ? list.Sum(i => i.V!.Value * i.W) / w : list.Average(i => i.V!.Value);
        }
        double WS(long? req) => req ?? 1;
        double WR(long? req, int n) => req ?? n;
        var availability = Mean(samples.Select(s => (s.AvailabilityPct, WS(s.RequestCount))).Concat(rolled.Select(r => (r.AvailabilityAvgPct, WR(r.RequestCount, r.Samples)))));
        var latency = Mean(samples.Select(s => (s.LatencyP95Ms, WS(s.RequestCount))).Concat(rolled.Select(r => (r.LatencyP95AvgMs, WR(r.RequestCount, r.Samples)))));
        var errorRate = Mean(samples.Select(s => (s.ErrorRatePct, WS(s.RequestCount))).Concat(rolled.Select(r => (r.ErrorRateAvgPct, WR(r.RequestCount, r.Samples)))));
        ErrorBudget? budget = env.SloAvailabilityPct is { } obj && availability is { } av ? SloMath.Budget(obj, av) : null;
        return new(env.Id, env.Name, days, Iso(from)!, Iso(to)!, samples.Count, rolled.Sum(r => r.Samples),
            new(env.SloAvailabilityPct, availability, env.SloAvailabilityPct is { } a && availability is { } m1 ? m1 >= a : null),
            new(env.SloLatencyP95Ms, latency, env.SloLatencyP95Ms is { } l && latency is { } m2 ? m2 <= l : null),
            new(env.SloErrorRatePct, errorRate, env.SloErrorRatePct is { } e && errorRate is { } m3 ? m3 <= e : null), budget);
    }

    /// <summary>One read for the monitoring screen: the deployment, thresholds, SLO targets, canary state, breaches, the rollback arming, the control actions and the latest sample of each series.</summary>
    public async Task<DeploymentMonitoring?> MonitoringAsync(string deploymentId, DeploymentService deployments, DeploymentControlService controls, int sloDays = 30, CancellationToken ct = default)
    {
        var detail = await deployments.GetAsync(deploymentId, 0, ct);
        if (detail is null) return null;
        await using var db = await OpenAsync(ct);
        var dep = await db.Set<Deployments>().AsNoTracking().SingleAsync(d => d.Id == deploymentId, ct);
        var env = await db.Set<Environments>().AsNoTracking().SingleAsync(e => e.Id == dep.EnvironmentId, ct);
        var canary = await CanaryCoreAsync(db, dep, ct);
        var breaches = (await db.Set<ThresholdBreaches>().AsNoTracking().Where(b => b.DeploymentId == deploymentId).OrderByDescending(b => b.StartedAt).Take(100).ToListAsync(ct)).Select(View).ToList();
        var latest = new List<LatestSample>();
        foreach (var sn in MetricSeries.All)
            if (await db.Set<MetricSamples>().AsNoTracking().Where(m => m.DeploymentId == deploymentId && m.Series == sn).OrderByDescending(m => m.SampledAt).FirstOrDefaultAsync(ct) is { } m)
                latest.Add(new(sn, Iso(m.SampledAt)!, m.ErrorRatePct, m.LatencyP95Ms, m.AvailabilityPct, m.RequestCount));
        var rollback = await controls.RollbackStatusAsync(deploymentId, ct);
        if (rollback is null) return null;   // the deployment went away while this was read
        return new(detail.Deployment, Lines(env), Targets(env), canary, breaches, rollback, await controls.ActionsAsync(deploymentId, 20, ct), latest,
            await SloCoreAsync(db, env, sloDays, null, ct));
    }

    // ---- retention ------------------------------------------------------------------------------------------------------------------

    public sealed record RetentionResult(int SamplesDropped, int BucketsWritten, DateTime Cutoff);

    /// <summary>
    /// Summarises samples older than <c>Telemetry:RetentionDays</c> into hourly rollups per deployment and series, then drops them, in the current workspace. Idempotent: a bucket
    /// that already exists (an hour split by an earlier cutoff) is merged, not replaced. One audit row, actor the system, when anything was dropped.
    /// </summary>
    public Task<ServiceResult<RetentionResult>> RunRetentionAsync(CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var cutoff = Trunc(Now.AddDays(-RetentionDays));
            var old = await db.Set<MetricSamples>().Where(m => m.SampledAt < cutoff).OrderBy(m => m.Id).Take(100_000).ToListAsync(ct);
            if (old.Count == 0) return ServiceResult<RetentionResult>.Ok(new(0, 0, cutoff));
            var buckets = 0;
            foreach (var g in old.GroupBy(m => (m.DeploymentId, m.Series, Hour: new DateTime(m.SampledAt.Ticks - m.SampledAt.Ticks % TimeSpan.TicksPerHour, DateTimeKind.Utc))))
            {
                var er = g.Where(x => x.ErrorRatePct is not null).Select(x => x.ErrorRatePct!.Value).ToList();
                var lat = g.Where(x => x.LatencyP95Ms is not null).Select(x => x.LatencyP95Ms!.Value).ToList();
                var av = g.Where(x => x.AvailabilityPct is not null).Select(x => x.AvailabilityPct!.Value).ToList();
                long? req = g.Any(x => x.RequestCount is not null) ? g.Sum(x => x.RequestCount ?? 0) : null;
                var row = await db.Set<MetricRollups>().SingleOrDefaultAsync(r => r.DeploymentId == g.Key.DeploymentId && r.Series == g.Key.Series && r.BucketAt == g.Key.Hour, ct);
                if (row is null)
                {
                    db.Set<MetricRollups>().Add(new MetricRollups
                    {
                        DeploymentId = g.Key.DeploymentId, Series = g.Key.Series, BucketAt = g.Key.Hour, Samples = g.Count(), ErrorRateAvgPct = er.Count == 0 ? null : er.Average(), ErrorRateMaxPct = er.Count == 0 ? null : er.Max(),
                        LatencyP95AvgMs = lat.Count == 0 ? null : lat.Average(), LatencyP95MaxMs = lat.Count == 0 ? null : lat.Max(), AvailabilityAvgPct = av.Count == 0 ? null : av.Average(),
                        AvailabilityMinPct = av.Count == 0 ? null : av.Min(), RequestCount = req,
                    });
                }
                else
                {
                    double? Mix(double? oldAvg, IList<double> add) => add.Count == 0 ? oldAvg : oldAvg is null ? add.Average() : (oldAvg.Value * row.Samples + add.Sum()) / (row.Samples + add.Count);
                    row.ErrorRateAvgPct = Mix(row.ErrorRateAvgPct, er); row.LatencyP95AvgMs = Mix(row.LatencyP95AvgMs, lat); row.AvailabilityAvgPct = Mix(row.AvailabilityAvgPct, av);
                    if (er.Count > 0) row.ErrorRateMaxPct = Math.Max(row.ErrorRateMaxPct ?? 0, er.Max());
                    if (lat.Count > 0) row.LatencyP95MaxMs = Math.Max(row.LatencyP95MaxMs ?? 0, lat.Max());
                    if (av.Count > 0) row.AvailabilityMinPct = Math.Min(row.AvailabilityMinPct ?? 100, av.Min());
                    row.RequestCount = req is null ? row.RequestCount : (row.RequestCount ?? 0) + req;
                    row.Samples += g.Count();
                }
                buckets++;
            }
            db.Set<MetricSamples>().RemoveRange(old);
            Audit(db, null, null, "MetricSample", "retention", "Summarise", null, new { dropped = old.Count, buckets, cutoff = Iso(cutoff), days = RetentionDays });
            await db.SaveChangesAsync(ct);
            return ServiceResult<RetentionResult>.Ok(new(old.Count, buckets, cutoff));
        }, ct);
}
