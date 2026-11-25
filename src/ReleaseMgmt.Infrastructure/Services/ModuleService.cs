using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

/// <summary>One module as the settings page and the navigation read it. <c>Version</c> is the If-Match value: 0 while the workspace has never enabled the module (no row yet).
/// <c>Entitled</c> is what the plan allows now; <c>Enabled</c> is the workspace's own switch, which stays as it is if the plan later stops allowing the module.</summary>
public sealed record ModuleView(string Key, string Name, string Summary, IReadOnlyList<string> TurnsOn, bool Enabled, bool Entitled, string? EntitlementReason, string PlanSource,
    NamedRef? EnabledBy, string? EnabledAt, string? LastChangedAt, int Version);

/// <summary>
/// REOS-174 (D56, D57, D63): per-workspace add-on modules. Off by default (no row means off). Enabling asks <see cref="IModuleEntitlements"/> first (billing plugs in there later) and
/// is refused with a readable 422 <c>ModuleNotEntitled</c> when the plan does not include the module; disabling is never refused and keeps the module's data. Every change is one
/// transaction with one <c>WorkspaceModule</c> audit row and a Version bump (If-Match; the version of a module never enabled is 0), and tells every open session of the workspace
/// to redraw its navigation. A repeat of the state a module already has is a no-op: no version, no audit row, no push.
/// </summary>
public sealed class ModuleService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IModuleEntitlements entitlements, IRealtimePublisher? realtime = null)
    : ServiceBase(dbf, time, realtime), IModuleGate
{
    private const string Entity = "WorkspaceModule";
    private readonly IRealtimePublisher? _realtime = realtime;

    private static string Iso(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    /// <summary>Every module in the registry with this workspace's state, in the registry's order.</summary>
    public async Task<List<ModuleView>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var ws = Memberships.WorkspaceOf(db);
        var rows = await db.Set<WorkspaceModules>().AsNoTracking().IgnoreQueryFilters().Where(m => m.WorkspaceId == ws).ToListAsync(ct);
        var views = new List<ModuleView>();
        foreach (var def in ModuleCatalog.All) views.Add(await ViewAsync(db, ws, def, rows.SingleOrDefault(r => r.ModuleKey == def.Key), ct));
        return views;
    }

    public async Task<bool> IsEnabledAsync(string workspaceId, string moduleKey, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        return await db.Set<WorkspaceModules>().AsNoTracking().IgnoreQueryFilters().AnyAsync(m => m.WorkspaceId == workspaceId && m.ModuleKey == moduleKey && m.Enabled, ct);
    }

    private async Task<ModuleView> ViewAsync(ReleaseDbContext db, string ws, ModuleDefinition def, WorkspaceModules? row, CancellationToken ct)
    {
        var ent = await entitlements.CheckAsync(ws, def.Key, ct);
        NamedRef? by = null;
        if (row?.EnabledByUserId is { } uid)
        {
            var name = await db.Set<Users>().AsNoTracking().Where(u => u.Id == uid).Select(u => u.DisplayName).SingleOrDefaultAsync(ct);
            by = new NamedRef(uid, name ?? uid);
        }
        return new ModuleView(def.Key, def.Name, def.Summary, def.TurnsOn, row?.Enabled ?? false, ent.Allowed, ent.Allowed ? null : ent.Reason, row?.PlanSource ?? ent.Source,
            by, row?.EnabledAt is { } at ? Iso(at) : null, row?.LastChangedAt is { } lc ? Iso(lc) : null, row?.Version ?? 0);
    }

    private static object Snapshot(WorkspaceModules? m) => new { enabled = m?.Enabled ?? false, planSource = m?.PlanSource, version = m?.Version ?? 0 };

    public Task<ServiceResult<ModuleView>> EnableAsync(string key, Actor actor, int? expectedVersion, CancellationToken ct = default) => SetAsync(key, true, actor, expectedVersion, ct);
    public Task<ServiceResult<ModuleView>> DisableAsync(string key, Actor actor, int? expectedVersion, CancellationToken ct = default) => SetAsync(key, false, actor, expectedVersion, ct);

    private async Task<ServiceResult<ModuleView>> SetAsync(string key, bool enable, Actor actor, int? expectedVersion, CancellationToken ct)
    {
        var def = ModuleCatalog.Find(key);
        if (def is null) return ServiceResult<ModuleView>.NotFound("module");
        string? changedIn = null;
        var result = await RunAsync(async db =>
        {
            var ws = Memberships.WorkspaceOf(db);
            var row = await db.Set<WorkspaceModules>().IgnoreQueryFilters().SingleOrDefaultAsync(m => m.WorkspaceId == ws && m.ModuleKey == key, ct);
            if (VersionMismatch(expectedVersion, row?.Version ?? 0)) return ServiceResult<ModuleView>.Conflict(await ViewAsync(db, ws, def, row, ct));
            if ((row?.Enabled ?? false) == enable) return ServiceResult<ModuleView>.Ok(await ViewAsync(db, ws, def, row, ct));   // already so: nothing to record
            var before = Snapshot(row);
            var now = Now;
            if (enable)
            {
                var ent = await entitlements.CheckAsync(ws, key, ct);
                if (!ent.Allowed) return ServiceResult<ModuleView>.Fail(new GuardFailure(ModuleGuards.NotEntitled, ent.Reason ?? $"The workspace's plan does not include the {def.Name}"));
                if (row is null)
                    db.Set<WorkspaceModules>().Add(row = new WorkspaceModules { WorkspaceId = ws, ModuleKey = key, Enabled = true, EnabledByUserId = actor.UserId, EnabledAt = now, PlanSource = ent.Source, LastChangedByUserId = actor.UserId, LastChangedAt = now });
                else
                {
                    row.Enabled = true; row.EnabledByUserId = actor.UserId; row.EnabledAt = now; row.PlanSource = ent.Source;
                    row.Version++; row.LastChangedByUserId = actor.UserId; row.LastChangedAt = now;
                }
            }
            else
            {
                row!.Enabled = false;   // a module can only be switched off from a row that says it is on
                row.Version++; row.LastChangedByUserId = actor.UserId; row.LastChangedAt = now;
            }
            Audit(db, actor, null, Entity, key, enable ? "Enable" : "Disable", before, Snapshot(row));
            await db.SaveChangesAsync(ct);
            changedIn = ws;
            return ServiceResult<ModuleView>.Ok(await ViewAsync(db, ws, def, row, ct));
        }, ct);
        if (result.IsOk && changedIn is not null && _realtime is not null) await _realtime.ModulesChangedAsync(changedIn, ct);
        return result;
    }
}
