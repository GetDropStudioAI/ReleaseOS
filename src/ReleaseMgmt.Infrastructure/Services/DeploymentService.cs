using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

/// <summary>What a deployment tool pushes to <c>POST /api/v1/ingest/deployments</c>. The service is named by its catalog key, the environment by its name, the release by its key;
/// <see cref="ExternalId"/> is the tool's own id for the deployment, which makes every call idempotent. Fields left out are not changed.</summary>
public sealed record IngestDeployment(string? ExternalId, string? ServiceKey = null, string? Environment = null, string? ReleaseVersion = null, string? Strategy = null,
    IReadOnlyList<double>? CanarySteps = null, string? State = null, double? TrafficPercent = null, int? ReadyPods = null, int? DesiredPods = null, string? ReleaseKey = null,
    DateTime? StartedAt = null, DateTime? FinishedAt = null);

public sealed record IngestEvent(string? Level, string? Message, DateTime? OccurredAt = null, string? ExternalId = null);

public sealed record IngestResult(string Id, string ExternalId, string State, int Version, bool Created, bool Changed);
public sealed record IngestEventsResult(string DeploymentId, string ExternalId, int Accepted, int Duplicates, int Version);

public sealed record DeploymentView(string Id, string ExternalId, string ServiceId, string ServiceKey, string ServiceName, string EnvironmentId, string EnvironmentName, string EnvironmentKind,
    string? TrainId, string? ReleaseKey, string? TrainTitle, string? RunId, string ReleaseVersion, string Strategy, IReadOnlyList<double>? CanarySteps, string State, double TrafficPercent,
    int? ReadyPods, int? DesiredPods, string? StartedAt, string? FinishedAt, string? LastEventAt, string CreatedAt, string UpdatedAt, int Version);
public sealed record DeploymentEventView(string Id, string OccurredAt, string ReceivedAt, string Level, string Message);
public sealed record DeploymentDetail(DeploymentView Deployment, IReadOnlyList<DeploymentEventView> Events);
public sealed record DeploymentFilter(string? EnvironmentId = null, string? ServiceId = null, string? TrainId = null, string? State = null, int Take = 100);

public sealed record ServiceEnvironmentCell(string EnvironmentId, string State, string? DeploymentId, string? ReleaseVersion, double? TrafficPercent, string? UpdatedAt);
public sealed record ServiceEnvironmentRow(string ServiceId, string ServiceKey, string ServiceName, IReadOnlyList<ServiceEnvironmentCell> Cells);
/// <summary>Per-environment state of each service in a release (B6): the latest deployment of the service to the environment for this release, or NotStarted.</summary>
public sealed record ReleaseEnvironmentState(string TrainId, IReadOnlyList<EnvironmentRef> Environments, IReadOnlyList<ServiceEnvironmentRow> Services);

/// <summary>
/// Deployments, their rollout state and events (REOS-110, D42). The deployment tool pushes them through the ingest API (<see cref="UpsertAsync"/>, <see cref="AddEventsAsync"/>, called
/// with the workspace the API key belongs to); people read them (<see cref="ListAsync"/>, <see cref="GetAsync"/>, <see cref="ReleaseStateAsync"/>). State moves only through here, along
/// <see cref="DeploymentStates.CanMove"/>, with trg_Deployment_Transition as the backstop. Every write is one transaction with one audit row (actor: the system; the key is named in the row) and
/// a Version bump; a replay changes nothing and writes nothing. After the commit a <c>DeploymentChanged</c> hint goes to the workspace's live group.
/// </summary>
public sealed class DeploymentService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IRealtimePublisher? realtime = null) : ServiceBase(dbf, time, realtime)
{
    private const string Entity = "Deployment";
    private static readonly JsonSerializerOptions CanaryJson = new();

    private static GuardFailure Bad(string m) => new(ReleaseModelGuards.DeploymentInvalid, m);
    private static string? Iso(DateTime? t) => t?.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    // ---- ingest ---------------------------------------------------------------------------------------------------------------------

    public async Task<ServiceResult<IngestResult>> UpsertAsync(IngestDeployment d, IngestPrincipal key, CancellationToken ct = default)
    {
        using var _ = WorkspaceScope.Enter(key.WorkspaceId);
        var r = await RunAsync(db => UpsertCoreAsync(db, d, key, ct), ct);
        if (r.Kind == ResultKind.GuardFailed && IsRace(r)) r = await RunAsync(db => UpsertCoreAsync(db, d, key, ct), ct);   // two creates of one external id at once: the loser is now an update
        if (r.IsOk && r.Value!.Changed) await PublishAsync(key.WorkspaceId, r.Value.Id, r.Value.Version, ct);
        return r;
    }

    public async Task<ServiceResult<IngestEventsResult>> AddEventsAsync(string? externalId, IReadOnlyList<IngestEvent>? events, IngestPrincipal key, CancellationToken ct = default)
    {
        using var _ = WorkspaceScope.Enter(key.WorkspaceId);
        var r = await RunAsync(db => AddEventsCoreAsync(db, externalId, events, key, ct), ct);
        if (r.Kind == ResultKind.GuardFailed && IsRace(r)) r = await RunAsync(db => AddEventsCoreAsync(db, externalId, events, key, ct), ct);
        if (r.IsOk && r.Value!.Accepted > 0) await PublishAsync(key.WorkspaceId, r.Value.DeploymentId, r.Value.Version, ct);
        return r;
    }

    private static bool IsRace<T>(ServiceResult<T> r) => r.Failures.Any(f => f.Guard == Guards.DbRule && f.Message.Contains("UNIQUE constraint failed", StringComparison.Ordinal));

    private async Task PublishAsync(string workspaceId, string deploymentId, int version, CancellationToken ct)
    {
        if (realtime is null) return;
        await using var db = await OpenAsync(ct);
        var trainId = await db.Set<Deployments>().Where(x => x.Id == deploymentId).Select(x => x.ReleaseTrainId).SingleOrDefaultAsync(ct);
        await realtime.DeploymentChangedAsync(workspaceId, deploymentId, version, trainId, ct);
    }

    private static string? ValidExternalId(string? id, string what) =>
        string.IsNullOrWhiteSpace(id) ? $"{what} is required" : id.Length > ReleaseModelLimits.MaxExternalId ? $"{what} is at most {ReleaseModelLimits.MaxExternalId} characters" : id.Any(char.IsControl) ? $"{what} has a control character" : null;

    private static string? CanaryProblem(IReadOnlyList<double> steps)
    {
        if (steps.Count is 0 or > ReleaseModelLimits.MaxCanarySteps) return $"Canary steps are 1 to {ReleaseModelLimits.MaxCanarySteps} traffic percentages";
        if (steps.Any(s => double.IsNaN(s) || s <= 0 || s > 100)) return "A canary step is a traffic percentage above 0 and up to 100";
        for (var i = 1; i < steps.Count; i++) if (steps[i] <= steps[i - 1]) return "Canary steps rise from one to the next";
        return null;
    }

    private async Task<ServiceResult<IngestResult>> UpsertCoreAsync(ReleaseDbContext db, IngestDeployment d, IngestPrincipal key, CancellationToken ct)
    {
        var f = new List<GuardFailure>();
        if (ValidExternalId(d.ExternalId, "externalId") is { } idProblem) f.Add(Bad(idProblem));
        if (d.State is not null && !DeploymentStates.All.Contains(d.State)) f.Add(Bad($"State is one of {string.Join(", ", DeploymentStates.All)}"));
        if (d.Strategy is not null && !DeploymentStates.Strategies.Contains(d.Strategy)) f.Add(Bad($"Strategy is one of {string.Join(", ", DeploymentStates.Strategies)}"));
        if (d.TrafficPercent is { } tp && (double.IsNaN(tp) || tp < 0 || tp > 100)) f.Add(Bad("Traffic is a percentage between 0 and 100"));
        if (d.ReadyPods is < 0 || d.DesiredPods is < 0) f.Add(Bad("Pod counts are 0 or more"));
        if (d.ReleaseVersion is { } rv && (rv.Trim().Length is 0 || rv.Length > ReleaseModelLimits.MaxReleaseVersion)) f.Add(Bad($"releaseVersion is 1 to {ReleaseModelLimits.MaxReleaseVersion} characters"));
        if (d.CanarySteps is { } cs && CanaryProblem(cs) is { } cp) f.Add(Bad(cp));
        if (f.Count > 0) return ServiceResult<IngestResult>.Fail(f);

        var now = Now;
        var existing = await db.Set<Deployments>().SingleOrDefaultAsync(x => x.ExternalId == d.ExternalId, ct);
        ReleaseTrains? train = null;
        if (d.ReleaseKey is not null)
        {
            train = await db.Set<ReleaseTrains>().SingleOrDefaultAsync(t => t.ReleaseKey == d.ReleaseKey, ct);
            if (train is null) return ServiceResult<IngestResult>.Fail(new GuardFailure(ReleaseModelGuards.UnknownRelease, $"No release has the key \"{d.ReleaseKey}\" in this workspace"));
        }

        if (existing is null)
        {
            if (string.IsNullOrWhiteSpace(d.ServiceKey) || string.IsNullOrWhiteSpace(d.Environment) || string.IsNullOrWhiteSpace(d.ReleaseVersion))
                return ServiceResult<IngestResult>.Fail(Bad("A new deployment needs serviceKey, environment and releaseVersion"));
            var service = await ServiceCatalogService.FindByKeyAsync(db, d.ServiceKey.Trim(), ct);
            if (service is null || service.ArchivedAt is not null)
                return ServiceResult<IngestResult>.Fail(new GuardFailure(ReleaseModelGuards.UnknownService, service is null ? $"No service with the key \"{d.ServiceKey}\" is in the catalog" : $"The service \"{service.ServiceKey}\" is archived"));
            var env = await db.Set<Environments>().SingleOrDefaultAsync(e => e.Name == d.Environment.Trim(), ct);
            if (env is null || env.ArchivedAt is not null)
                return ServiceResult<IngestResult>.Fail(new GuardFailure(ReleaseModelGuards.UnknownEnvironment, env is null ? $"No environment is called \"{d.Environment}\"" : $"The environment \"{env.Name}\" is archived"));
            var strategy = d.Strategy ?? (d.CanarySteps is not null ? "Canary" : "Rolling");
            if (strategy != "Canary" && d.CanarySteps is not null) return ServiceResult<IngestResult>.Fail(Bad("Canary steps belong to the Canary strategy"));
            var state = d.State ?? DeploymentStates.Queued;
            var dep = new Deployments
            {
                Id = Ids.New(), ExternalId = d.ExternalId!, CatalogServiceId = service.Id, EnvironmentId = env.Id, ReleaseTrainId = train?.Id, ReleaseVersion = d.ReleaseVersion.Trim(),
                Strategy = strategy, CanarySteps = d.CanarySteps is null ? null : JsonSerializer.Serialize(d.CanarySteps, CanaryJson), State = state,
                TrafficPercent = d.TrafficPercent ?? (state == DeploymentStates.Successful ? 100 : 0), ReadyPods = d.ReadyPods, DesiredPods = d.DesiredPods, IngestKeyId = key.KeyId, CreatedAt = now, UpdatedAt = now,
            };
            if (state != DeploymentStates.Queued) dep.StartedAt = d.StartedAt ?? now;
            if (DeploymentStates.IsTerminal(state)) dep.FinishedAt = d.FinishedAt ?? now;
            if (train is not null) dep.RunId = await OpenLiveRunAsync(db, train.Id, ct);
            db.Set<Deployments>().Add(dep);
            Audit(db, null, train?.Id, Entity, dep.Id, "Create", null, Snapshot(dep, key));
            await db.SaveChangesAsync(ct);
            return ServiceResult<IngestResult>.Ok(new(dep.Id, dep.ExternalId, dep.State, dep.Version, Created: true, Changed: true));
        }

        // An existing deployment: its identity cannot be changed by a later call.
        var mismatch = new List<string>();
        if (d.ServiceKey is not null && !await db.Set<CatalogServices>().AnyAsync(s => s.Id == existing.CatalogServiceId && s.ServiceKey == d.ServiceKey.Trim(), ct)) mismatch.Add("serviceKey");
        if (d.Environment is not null && !await db.Set<Environments>().AnyAsync(e => e.Id == existing.EnvironmentId && e.Name == d.Environment.Trim(), ct)) mismatch.Add("environment");
        if (d.ReleaseVersion is not null && d.ReleaseVersion.Trim() != existing.ReleaseVersion) mismatch.Add("releaseVersion");
        if (train is not null && existing.ReleaseTrainId is not null && existing.ReleaseTrainId != train.Id) mismatch.Add("releaseKey");
        if (mismatch.Count > 0)
            return ServiceResult<IngestResult>.Fail(new GuardFailure(ReleaseModelGuards.DeploymentIdentityMismatch, $"Deployment \"{existing.ExternalId}\" already exists with a different {string.Join(", ", mismatch)}; use a new externalId for a different deployment", mismatch));

        var before = Snapshot(existing, key);
        var oldState = existing.State;
        var strategyNow = d.Strategy ?? existing.Strategy;
        if (strategyNow != "Canary" && (d.CanarySteps is not null || (d.Strategy is not null && existing.CanarySteps is not null && d.Strategy != "Canary")))
            return ServiceResult<IngestResult>.Fail(Bad("Canary steps belong to the Canary strategy"));
        if (d.State is not null && d.State != existing.State)
        {
            if (!DeploymentStates.CanMove(existing.State, d.State))
                return ServiceResult<IngestResult>.Fail(new GuardFailure(ReleaseModelGuards.IllegalDeploymentTransition, $"A deployment cannot move from {existing.State} to {d.State}", [existing.State]));
            existing.State = d.State;
            if (existing.StartedAt is null && d.State != DeploymentStates.Queued) existing.StartedAt = d.StartedAt ?? now;
            if (DeploymentStates.IsTerminal(d.State) && existing.FinishedAt is null) existing.FinishedAt = d.FinishedAt ?? now;
            if (d.State == DeploymentStates.Successful && d.TrafficPercent is null) existing.TrafficPercent = 100;
        }
        if (d.Strategy is not null) existing.Strategy = d.Strategy;
        if (d.CanarySteps is not null) existing.CanarySteps = JsonSerializer.Serialize(d.CanarySteps, CanaryJson);
        else if (d.Strategy is not null && d.Strategy != "Canary") existing.CanarySteps = null;
        if (d.TrafficPercent is { } t) existing.TrafficPercent = t;
        if (d.ReadyPods is { } rp) existing.ReadyPods = rp;
        if (d.DesiredPods is { } dp) existing.DesiredPods = dp;
        if (d.StartedAt is { } sa && existing.StartedAt is null) existing.StartedAt = sa;
        if (train is not null && existing.ReleaseTrainId is null) { existing.ReleaseTrainId = train.Id; existing.RunId = await OpenLiveRunAsync(db, train.Id, ct); }

        if (!db.Entry(existing).Properties.Any(p => p.IsModified)) return ServiceResult<IngestResult>.Ok(new(existing.Id, existing.ExternalId, existing.State, existing.Version, Created: false, Changed: false));   // a replay
        existing.Version++; existing.UpdatedAt = now;
        Audit(db, null, existing.ReleaseTrainId, Entity, existing.Id, existing.State != oldState ? "State" : "Update", before, Snapshot(existing, key));
        await db.SaveChangesAsync(ct);
        return ServiceResult<IngestResult>.Ok(new(existing.Id, existing.ExternalId, existing.State, existing.Version, Created: false, Changed: true));
    }

    private static Task<string?> OpenLiveRunAsync(ReleaseDbContext db, string trainId, CancellationToken ct) =>
        db.Set<RunbookRuns>().Where(r => r.ReleaseTrainId == trainId && r.Mode == "Live" && r.EndedAt == null).Select(r => r.Id).SingleOrDefaultAsync(ct)!;

    private static object Snapshot(Deployments d, IngestPrincipal key) => new
    {
        state = d.State, d.Strategy, d.CanarySteps, d.TrafficPercent, d.ReadyPods, d.DesiredPods, d.ReleaseVersion, d.ExternalId, d.CatalogServiceId, d.EnvironmentId, d.ReleaseTrainId, d.RunId,
        via = new { keyId = key.KeyId, key = key.KeyName },
    };

    private async Task<ServiceResult<IngestEventsResult>> AddEventsCoreAsync(ReleaseDbContext db, string? externalId, IReadOnlyList<IngestEvent>? events, IngestPrincipal key, CancellationToken ct)
    {
        if (ValidExternalId(externalId, "deploymentExternalId") is { } idProblem) return ServiceResult<IngestEventsResult>.Fail(Bad(idProblem));
        if (events is null || events.Count == 0) return ServiceResult<IngestEventsResult>.Fail(Bad("Send at least one event"));
        if (events.Count > ReleaseModelLimits.MaxEventsPerCall) return ServiceResult<IngestEventsResult>.Fail(Bad($"At most {ReleaseModelLimits.MaxEventsPerCall} events in one call"));
        var f = new List<GuardFailure>();
        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            var message = e.Message?.Trim() ?? "";
            if (message.Length is 0 or > ReleaseModelLimits.MaxEventMessage) f.Add(Bad($"events[{i}].message is 1 to {ReleaseModelLimits.MaxEventMessage} characters"));
            if (e.Level is not null && !DeploymentStates.Levels.Contains(e.Level)) f.Add(Bad($"events[{i}].level is one of {string.Join(", ", DeploymentStates.Levels)}"));
            if (e.ExternalId is not null && ValidExternalId(e.ExternalId, $"events[{i}].externalId") is { } p) f.Add(Bad(p));
        }
        if (f.Count > 0) return ServiceResult<IngestEventsResult>.Fail(f);

        var dep = await db.Set<Deployments>().SingleOrDefaultAsync(x => x.ExternalId == externalId, ct);
        if (dep is null) return ServiceResult<IngestEventsResult>.NotFound("deployment");

        var wanted = events.Where(e => e.ExternalId is not null).Select(e => e.ExternalId!).Distinct().ToList();
        var seen = wanted.Count == 0 ? [] : (await db.Set<DeploymentEvents>().AsNoTracking().Where(e => e.DeploymentId == dep.Id && e.ExternalId != null && wanted.Contains(e.ExternalId)).Select(e => e.ExternalId!).ToListAsync(ct)).ToHashSet();
        var now = Now;
        int accepted = 0, duplicates = 0;
        foreach (var e in events)
        {
            if (e.ExternalId is not null && !seen.Add(e.ExternalId)) { duplicates++; continue; }
            db.Set<DeploymentEvents>().Add(new DeploymentEvents
            {
                Id = Ids.New(), DeploymentId = dep.Id, OccurredAt = (e.OccurredAt ?? now).ToUniversalTime(), ReceivedAt = now, Level = e.Level ?? "Info", Message = e.Message!.Trim(), ExternalId = e.ExternalId,
            });
            accepted++;
        }
        if (accepted == 0) return ServiceResult<IngestEventsResult>.Ok(new(dep.Id, dep.ExternalId, 0, duplicates, dep.Version));   // a replay: nothing written
        dep.LastEventAt = now; dep.UpdatedAt = now; dep.Version++;
        Audit(db, null, dep.ReleaseTrainId, "DeploymentEvent", dep.Id, "Append", null, new { accepted, duplicates, errors = events.Count(e => e.Level == "Error"), via = new { keyId = key.KeyId, key = key.KeyName } });
        await db.SaveChangesAsync(ct);
        return ServiceResult<IngestEventsResult>.Ok(new(dep.Id, dep.ExternalId, accepted, duplicates, dep.Version));
    }

    // ---- reads ----------------------------------------------------------------------------------------------------------------------

    public async Task<List<DeploymentView>> ListAsync(DeploymentFilter? filter = null, CancellationToken ct = default)
    {
        filter ??= new();
        await using var db = await OpenAsync(ct);
        var q = db.Set<Deployments>().AsNoTracking().AsQueryable();
        if (filter.EnvironmentId is not null) q = q.Where(d => d.EnvironmentId == filter.EnvironmentId);
        if (filter.ServiceId is not null) q = q.Where(d => d.CatalogServiceId == filter.ServiceId);
        if (filter.TrainId is not null) q = q.Where(d => d.ReleaseTrainId == filter.TrainId);
        if (filter.State is not null) q = q.Where(d => d.State == filter.State);
        var rows = await q.OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Id).Take(Math.Clamp(filter.Take, 1, 500)).ToListAsync(ct);
        return await ViewsAsync(db, rows, ct);
    }

    public async Task<DeploymentDetail?> GetAsync(string id, int events = 200, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var row = await db.Set<Deployments>().AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct);
        if (row is null) return null;
        var list = await db.Set<DeploymentEvents>().AsNoTracking().Where(e => e.DeploymentId == id).OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id).Take(Math.Clamp(events, 1, 1000)).ToListAsync(ct);
        list.Reverse();
        return new((await ViewsAsync(db, [row], ct))[0], [.. list.Select(e => new DeploymentEventView(e.Id, Iso(e.OccurredAt)!, Iso(e.ReceivedAt)!, e.Level, e.Message))]);
    }

    private static async Task<List<DeploymentView>> ViewsAsync(ReleaseDbContext db, List<Deployments> rows, CancellationToken ct)
    {
        var serviceIds = rows.Select(r => r.CatalogServiceId).Distinct().ToList();
        var envIds = rows.Select(r => r.EnvironmentId).Distinct().ToList();
        var trainIds = rows.Select(r => r.ReleaseTrainId).OfType<string>().Distinct().ToList();
        var services = await db.Set<CatalogServices>().AsNoTracking().Where(s => serviceIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        var envs = await db.Set<Environments>().AsNoTracking().Where(e => envIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);
        var trains = await db.Set<ReleaseTrains>().AsNoTracking().Where(t => trainIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => new { t.ReleaseKey, t.Title }, ct);
        return [.. rows.Select(r =>
        {
            var s = services[r.CatalogServiceId]; var e = envs[r.EnvironmentId];
            var t = r.ReleaseTrainId is not null && trains.TryGetValue(r.ReleaseTrainId, out var tt) ? tt : null;
            return new DeploymentView(r.Id, r.ExternalId, s.Id, s.ServiceKey, s.Name, e.Id, e.Name, e.Kind, r.ReleaseTrainId, t?.ReleaseKey, t?.Title, r.RunId, r.ReleaseVersion, r.Strategy,
                r.CanarySteps is null ? null : JsonSerializer.Deserialize<double[]>(r.CanarySteps), r.State, r.TrafficPercent, r.ReadyPods, r.DesiredPods, Iso(r.StartedAt), Iso(r.FinishedAt),
                Iso(r.LastEventAt), Iso(r.CreatedAt)!, Iso(r.UpdatedAt)!, r.Version);
        })];
    }

    /// <summary>Per-environment state of each service in a release (B6). Null when the release does not exist.</summary>
    public async Task<ReleaseEnvironmentState?> ReleaseStateAsync(string trainId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        if (!await db.Set<ReleaseTrains>().AnyAsync(t => t.Id == trainId, ct)) return null;
        var deployments = await db.Set<Deployments>().AsNoTracking().Where(d => d.ReleaseTrainId == trainId).OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Id).ToListAsync(ct);
        var targetIds = await db.Set<TrainEnvironments>().AsNoTracking().Where(e => e.ReleaseTrainId == trainId).Select(e => e.EnvironmentId).ToListAsync(ct);
        var envIds = targetIds.Concat(deployments.Select(d => d.EnvironmentId)).Distinct().ToList();
        var envs = (await db.Set<Environments>().AsNoTracking().Where(e => envIds.Contains(e.Id)).ToListAsync(ct)).OrderBy(e => e.SortOrder).ThenBy(e => e.Name).ToList();
        var serviceIds = (await db.Set<BundledProducts>().AsNoTracking().Where(p => p.ReleaseTrainId == trainId && p.CatalogServiceId != null).Select(p => p.CatalogServiceId!).ToListAsync(ct))
            .Concat(deployments.Select(d => d.CatalogServiceId)).Distinct().ToList();
        var services = (await db.Set<CatalogServices>().AsNoTracking().Where(s => serviceIds.Contains(s.Id)).ToListAsync(ct)).OrderBy(s => s.ServiceKey, StringComparer.OrdinalIgnoreCase).ToList();
        var rows = services.Select(s => new ServiceEnvironmentRow(s.Id, s.ServiceKey, s.Name, [.. envs.Select(e =>
        {
            var latest = deployments.FirstOrDefault(d => d.CatalogServiceId == s.Id && d.EnvironmentId == e.Id);
            return latest is null ? new ServiceEnvironmentCell(e.Id, "NotStarted", null, null, null, null)
                : new ServiceEnvironmentCell(e.Id, latest.State, latest.Id, latest.ReleaseVersion, latest.TrafficPercent, Iso(latest.UpdatedAt));
        })])).ToList();
        return new(trainId, [.. envs.Select(e => new EnvironmentRef(e.Id, e.Name, e.Kind, e.Region))], rows);
    }
}
