using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

public sealed record WebhookRef(string Id, string Name, string Host);
public sealed record EnvironmentView(string Id, string Name, string? Region, string Kind, int SortOrder, WebhookRef? ControlWebhook,
    double? ErrorRateMaxPct, double? LatencyP95MaxMs, double? AvailabilityMinPct, double? SloAvailabilityPct, double? SloLatencyP95Ms, double? SloErrorRatePct,
    bool Archived, int ActiveDeployments, int Version);

public sealed record NewEnvironment(string? Name, string? Kind, string? Region = null, int? SortOrder = null, string? ControlWebhookDestinationId = null,
    double? ErrorRateMaxPct = null, double? LatencyP95MaxMs = null, double? AvailabilityMinPct = null, double? SloAvailabilityPct = null, double? SloLatencyP95Ms = null, double? SloErrorRatePct = null);

/// <summary>Null leaves a field as it is. <see cref="Clear"/> names the optional fields to empty (region, controlWebhookDestinationId and the six limits), since null cannot say "empty".</summary>
public sealed record EnvironmentPatch(string? Name = null, string? Kind = null, string? Region = null, int? SortOrder = null, string? ControlWebhookDestinationId = null,
    double? ErrorRateMaxPct = null, double? LatencyP95MaxMs = null, double? AvailabilityMinPct = null, double? SloAvailabilityPct = null, double? SloLatencyP95Ms = null, double? SloErrorRatePct = null,
    IReadOnlyList<string>? Clear = null);

/// <summary>
/// Environments of a workspace (REOS-109, B6, D42): name, region, kind (Staging or Production), order, the control webhook destination that pause, promote and rollback will call, and
/// the thresholds and SLOs the environment is held to. Planners create, edit and archive; every write is one transaction with one <c>Environment</c> audit row and a Version bump (If-Match
/// on edits). An archived environment cannot be chosen for a release or deployed to, and stays on what already uses it.
/// </summary>
public sealed class EnvironmentService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IRealtimePublisher? realtime = null) : ServiceBase(dbf, time, realtime)
{
    public static readonly string[] Kinds = ["Staging", "Production"];
    public static readonly string[] Clearable = ["region", "controlWebhookDestinationId", "errorRateMaxPct", "latencyP95MaxMs", "availabilityMinPct", "sloAvailabilityPct", "sloLatencyP95Ms", "sloErrorRatePct"];
    private const string Entity = "Environment";

    private static GuardFailure Bad(string m) => new(ReleaseModelGuards.EnvironmentInvalid, m);

    public async Task<List<EnvironmentView>> ListAsync(bool includeArchived = false, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var rows = await db.Set<Environments>().AsNoTracking().Where(e => includeArchived || e.ArchivedAt == null).OrderBy(e => e.SortOrder).ThenBy(e => e.Name).ToListAsync(ct);
        return await ViewsAsync(db, rows, ct);
    }

    public async Task<EnvironmentView?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var row = await db.Set<Environments>().AsNoTracking().SingleOrDefaultAsync(e => e.Id == id, ct);
        return row is null ? null : (await ViewsAsync(db, [row], ct))[0];
    }

    private static async Task<List<EnvironmentView>> ViewsAsync(ReleaseDbContext db, List<Environments> rows, CancellationToken ct)
    {
        var hookIds = rows.Select(r => r.ControlWebhookDestinationId).OfType<string>().Distinct().ToList();
        var hooks = await db.Set<WebhookDestinations>().AsNoTracking().Where(w => hookIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => new WebhookRef(w.Id, w.Name, w.Host), ct);
        var active = await db.Set<Deployments>().AsNoTracking().Where(d => d.State == DeploymentStates.Running || d.State == DeploymentStates.Paused)
            .GroupBy(d => d.EnvironmentId).Select(g => new { Id = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N, ct);
        return [.. rows.Select(e => new EnvironmentView(e.Id, e.Name, e.Region, e.Kind, e.SortOrder, e.ControlWebhookDestinationId is not null && hooks.TryGetValue(e.ControlWebhookDestinationId, out var h) ? h : null,
            e.ErrorRateMaxPct, e.LatencyP95MaxMs, e.AvailabilityMinPct, e.SloAvailabilityPct, e.SloLatencyP95Ms, e.SloErrorRatePct, e.ArchivedAt is not null, active.GetValueOrDefault(e.Id), e.Version))];
    }

    private static object Snapshot(Environments e) => new
    {
        e.Name, e.Kind, e.Region, e.SortOrder, e.ControlWebhookDestinationId, e.ErrorRateMaxPct, e.LatencyP95MaxMs, e.AvailabilityMinPct, e.SloAvailabilityPct, e.SloLatencyP95Ms, e.SloErrorRatePct,
        archived = e.ArchivedAt is not null, e.Version,
    };

    private static async Task<List<GuardFailure>> CheckAsync(ReleaseDbContext db, Environments e, Environments? original, CancellationToken ct)
    {
        var f = new List<GuardFailure>();
        if (e.Name.Length is 0 or > ReleaseModelLimits.MaxEnvironmentName) f.Add(Bad($"An environment name is 1 to {ReleaseModelLimits.MaxEnvironmentName} characters"));
        else if (await db.Set<Environments>().AnyAsync(x => x.Name == e.Name && x.Id != e.Id, ct)) f.Add(new(ReleaseModelGuards.EnvironmentNameTaken, $"An environment called \"{e.Name}\" exists"));
        if (!Kinds.Contains(e.Kind)) f.Add(Bad("Kind must be Staging or Production"));
        if (e.Region is { } r && (r.Length is 0 or > ReleaseModelLimits.MaxRegion)) f.Add(Bad($"A region is 1 to {ReleaseModelLimits.MaxRegion} characters"));
        static bool Pct(double? v) => v is null || (v >= 0 && v <= 100);
        static bool NonNeg(double? v) => v is null || v >= 0;
        if (!Pct(e.ErrorRateMaxPct) || !Pct(e.AvailabilityMinPct) || !Pct(e.SloAvailabilityPct) || !Pct(e.SloErrorRatePct)) f.Add(Bad("A percentage is between 0 and 100"));
        if (!NonNeg(e.LatencyP95MaxMs) || !NonNeg(e.SloLatencyP95Ms)) f.Add(Bad("A latency is 0 or more milliseconds"));
        if (e.ControlWebhookDestinationId is not null && e.ControlWebhookDestinationId != original?.ControlWebhookDestinationId
            && !await db.Set<WebhookDestinations>().AnyAsync(w => w.Id == e.ControlWebhookDestinationId, ct)) f.Add(Bad("The control webhook destination does not exist in this workspace"));
        return f;
    }

    public Task<ServiceResult<Environments>> CreateAsync(NewEnvironment n, Actor actor, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var now = Now;
            var e = new Environments
            {
                Id = Ids.New(), Name = (n.Name ?? "").Trim(), Kind = n.Kind ?? "", Region = string.IsNullOrWhiteSpace(n.Region) ? null : n.Region.Trim(),
                SortOrder = n.SortOrder ?? (await db.Set<Environments>().Select(x => (int?)x.SortOrder).MaxAsync(ct) ?? 0) + 10,
                ControlWebhookDestinationId = string.IsNullOrWhiteSpace(n.ControlWebhookDestinationId) ? null : n.ControlWebhookDestinationId,
                ErrorRateMaxPct = n.ErrorRateMaxPct, LatencyP95MaxMs = n.LatencyP95MaxMs, AvailabilityMinPct = n.AvailabilityMinPct,
                SloAvailabilityPct = n.SloAvailabilityPct, SloLatencyP95Ms = n.SloLatencyP95Ms, SloErrorRatePct = n.SloErrorRatePct,
                CreatedAt = now, LastChangedByUserId = actor.UserId, LastChangedAt = now,
            };
            var f = await CheckAsync(db, e, null, ct);
            if (f.Count > 0) return ServiceResult<Environments>.Fail(f);
            db.Set<Environments>().Add(e);
            Audit(db, actor, null, Entity, e.Id, "Create", null, Snapshot(e));
            await db.SaveChangesAsync(ct);
            return ServiceResult<Environments>.Ok(e);
        }, ct);

    public Task<ServiceResult<Environments>> UpdateAsync(string id, EnvironmentPatch p, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var e = await db.Set<Environments>().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (e is null) return ServiceResult<Environments>.NotFound("environment");
            if (VersionMismatch(expectedVersion, e.Version)) return ServiceResult<Environments>.Conflict(e);
            var clear = (p.Clear ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (clear.Except(Clearable, StringComparer.OrdinalIgnoreCase).FirstOrDefault() is { } unknown)
                return ServiceResult<Environments>.Fail(Bad($"\"{unknown}\" cannot be cleared; one of {string.Join(", ", Clearable)}"));
            var original = new Environments { ControlWebhookDestinationId = e.ControlWebhookDestinationId };
            var before = Snapshot(e);
            if (p.Name is not null) e.Name = p.Name.Trim();
            if (p.Kind is not null) e.Kind = p.Kind;
            if (p.SortOrder is int so) e.SortOrder = so;
            if (p.Region is not null) e.Region = p.Region.Trim();
            if (p.ControlWebhookDestinationId is not null) e.ControlWebhookDestinationId = p.ControlWebhookDestinationId;
            e.ErrorRateMaxPct = p.ErrorRateMaxPct ?? e.ErrorRateMaxPct; e.LatencyP95MaxMs = p.LatencyP95MaxMs ?? e.LatencyP95MaxMs; e.AvailabilityMinPct = p.AvailabilityMinPct ?? e.AvailabilityMinPct;
            e.SloAvailabilityPct = p.SloAvailabilityPct ?? e.SloAvailabilityPct; e.SloLatencyP95Ms = p.SloLatencyP95Ms ?? e.SloLatencyP95Ms; e.SloErrorRatePct = p.SloErrorRatePct ?? e.SloErrorRatePct;
            if (clear.Contains("region")) e.Region = null;
            if (clear.Contains("controlWebhookDestinationId")) e.ControlWebhookDestinationId = null;
            if (clear.Contains("errorRateMaxPct")) e.ErrorRateMaxPct = null;
            if (clear.Contains("latencyP95MaxMs")) e.LatencyP95MaxMs = null;
            if (clear.Contains("availabilityMinPct")) e.AvailabilityMinPct = null;
            if (clear.Contains("sloAvailabilityPct")) e.SloAvailabilityPct = null;
            if (clear.Contains("sloLatencyP95Ms")) e.SloLatencyP95Ms = null;
            if (clear.Contains("sloErrorRatePct")) e.SloErrorRatePct = null;
            if (e.Region?.Length == 0) e.Region = null;
            var f = await CheckAsync(db, e, original, ct);
            if (f.Count > 0) { db.Entry(e).State = EntityState.Unchanged; return ServiceResult<Environments>.Fail(f); }
            if (!db.Entry(e).Properties.Any(x => x.IsModified)) return ServiceResult<Environments>.Ok(e);   // nothing to change: no Version bump, no audit row
            e.Version++; e.LastChangedByUserId = actor.UserId; e.LastChangedAt = Now;
            Audit(db, actor, null, Entity, e.Id, "Update", before, Snapshot(e));
            await db.SaveChangesAsync(ct);
            return ServiceResult<Environments>.Ok(e);
        }, ct);

    public Task<ServiceResult<Environments>> SetArchivedAsync(string id, bool archived, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var e = await db.Set<Environments>().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (e is null) return ServiceResult<Environments>.NotFound("environment");
            if (VersionMismatch(expectedVersion, e.Version)) return ServiceResult<Environments>.Conflict(e);
            if ((e.ArchivedAt is not null) == archived) return ServiceResult<Environments>.Ok(e);
            if (archived && await db.Set<Deployments>().AnyAsync(d => d.EnvironmentId == id && (d.State == DeploymentStates.Running || d.State == DeploymentStates.Paused || d.State == DeploymentStates.Queued), ct))
                return ServiceResult<Environments>.Fail(Bad("The environment has deployments in progress; archive it when they have finished"));
            var before = Snapshot(e);
            var now = Now;
            e.ArchivedAt = archived ? now : null; e.Version++; e.LastChangedByUserId = actor.UserId; e.LastChangedAt = now;
            Audit(db, actor, null, Entity, e.Id, archived ? "Archive" : "Unarchive", before, Snapshot(e));
            await db.SaveChangesAsync(ct);
            return ServiceResult<Environments>.Ok(e);
        }, ct);
}
