using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

public sealed record ReleaseTypeView(string Id, string Name, int SortOrder, NamedRef? DefaultTemplate, bool Archived, int Releases, int Version, bool IsBeta = false);
public sealed record NewReleaseType(string? Name, string? DefaultTemplateId = null, int? SortOrder = null, bool? IsBeta = null);
/// <summary>Null leaves a field; <c>DefaultTemplateId = ""</c> clears the default template.</summary>
public sealed record ReleaseTypePatch(string? Name = null, string? DefaultTemplateId = null, int? SortOrder = null, bool? IsBeta = null);

/// <summary>
/// RT1 (D46): the release types of a workspace. Seeded with Beta, CERT, PenTest Cert and Production (<see cref="SeedAsync"/>); planners add, rename, reorder, archive and set each
/// type's default template. A release of a type with a default template starts from it when no template is named (<see cref="TrainCreationService"/>).
/// What CERT means beyond its name is defined later (D46). A Beta type (<c>IsBeta</c>; the seeded Beta has it) makes its releases carry a number, 1 to 3 (D48). Every
/// release has a type (REOS-116): <see cref="SeedAsync"/> also gives a release that has none the workspace's Production type.
/// </summary>
public sealed class ReleaseTypeService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IRealtimePublisher? realtime = null) : ServiceBase(dbf, time, realtime)
{
    public static readonly string[] SeedNames = ["Beta", "CERT", "PenTest Cert", "Production"];
    private const string Entity = "ReleaseType";

    private static GuardFailure Bad(string m) => new(ReleaseModelGuards.ReleaseTypeInvalid, m);

    public async Task<List<ReleaseTypeView>> ListAsync(bool includeArchived = false, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var rows = await db.Set<ReleaseTypes>().AsNoTracking().Where(x => includeArchived || x.ArchivedAt == null).OrderBy(x => x.SortOrder).ThenBy(x => x.Name).ToListAsync(ct);
        return await ViewsAsync(db, rows, ct);
    }

    public async Task<ReleaseTypeView?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var row = await db.Set<ReleaseTypes>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        return row is null ? null : (await ViewsAsync(db, [row], ct))[0];
    }

    private static async Task<List<ReleaseTypeView>> ViewsAsync(ReleaseDbContext db, List<ReleaseTypes> rows, CancellationToken ct)
    {
        var templateIds = rows.Select(r => r.DefaultTemplateId).OfType<string>().Distinct().ToList();
        var templates = await db.Set<TrainTemplates>().AsNoTracking().Where(t => templateIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        var counts = await db.Set<ReleaseTrains>().AsNoTracking().Where(t => t.ReleaseTypeId != null).GroupBy(t => t.ReleaseTypeId!).Select(g => new { Id = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N, ct);
        return [.. rows.Select(r => new ReleaseTypeView(r.Id, r.Name, r.SortOrder, r.DefaultTemplateId is not null && templates.TryGetValue(r.DefaultTemplateId, out var n) ? new(r.DefaultTemplateId, n) : null,
            r.ArchivedAt is not null, counts.GetValueOrDefault(r.Id), r.Version, r.IsBeta))];
    }

    /// <summary>The four starting types of a workspace, not yet saved. Used by <see cref="SeedAsync"/>; a new workspace is seeded by it right after it is created (WorkspaceMembersService.CreateAsync).</summary>
    public static List<ReleaseTypes> SeedRows(string workspaceId) =>
        [.. SeedNames.Select((n, i) => new ReleaseTypes { Id = Ids.New(), Name = n, SortOrder = (i + 1) * 10, WorkspaceId = workspaceId, IsBeta = n == "Beta" })];

    /// <summary>
    /// Gives <paramref name="workspaceId"/> the four starting types when it has none (not even an archived one), and gives every release of it that has no type the workspace's
    /// Production type (REOS-116: "existing releases become Production"; a type called Production is added when the workspace has none). Idempotent; one audit row per change
    /// (system). Returns how many types were added plus how many releases were given a type.
    /// </summary>
    public async Task<int> SeedAsync(string workspaceId, CancellationToken ct = default)
    {
        using var _ = WorkspaceScope.Enter(workspaceId);
        var r = await RunAsync<int>(async db =>
        {
            var changed = 0;
            if (!await db.Set<ReleaseTypes>().AnyAsync(ct))
            {
                db.Set<ReleaseTypes>().AddRange(SeedRows(workspaceId));
                Audit(db, null, null, Entity, workspaceId, "Seed", null, new { names = SeedNames });
                changed += SeedNames.Length;
            }
            var typeless = await db.Set<ReleaseTrains>().Where(t => t.ReleaseTypeId == null).ToListAsync(ct);
            if (typeless.Count > 0)
            {
                await db.SaveChangesAsync(ct);   // the types before the releases that reference them
                var production = await db.Set<ReleaseTypes>().Where(x => x.Name == "Production").SingleOrDefaultAsync(ct);   // Name is NOCASE
                if (production is null)
                {
                    var last = await db.Set<ReleaseTypes>().Select(x => (int?)x.SortOrder).MaxAsync(ct) ?? 0;
                    production = new ReleaseTypes { Id = Ids.New(), Name = "Production", SortOrder = last + 10, WorkspaceId = workspaceId };
                    db.Set<ReleaseTypes>().Add(production);
                    Audit(db, null, null, Entity, production.Id, "Seed", null, new { names = new[] { production.Name }, reason = "Releases without a type became Production" });
                    await db.SaveChangesAsync(ct);
                    changed++;
                }
                var now = Now;
                foreach (var t in typeless)
                {
                    t.ReleaseTypeId = production.Id; t.Version++; t.UpdatedAt = now; t.LastChangedAt = now; t.LastChangedByUserId = null;
                    Audit(db, null, t.Id, "ReleaseTrain", t.Id, "ReleaseFields", new { releaseTypeId = (string?)null }, new { releaseTypeId = production.Id, releaseType = production.Name, reason = "Every release has a type (REOS-116)" });
                }
                changed += typeless.Count;
            }
            if (changed > 0) await db.SaveChangesAsync(ct);
            return ServiceResult<int>.Ok(changed);
        }, ct);
        return r.IsOk ? r.Value : throw new InvalidOperationException("Seeding release types failed: " + string.Join("; ", r.Failures.Select(f => f.Message)));
    }

    /// <summary>At start: every workspace that has no release types gets the starting four, and every release without a type becomes Production (<see cref="SeedAsync"/>). Returns how many workspaces changed.</summary>
    public async Task<int> SeedAllAsync(CancellationToken ct = default)
    {
        List<string> workspaces;
        using (WorkspaceScope.HostWide())
        {
            await using var db = await OpenAsync(ct);
            workspaces = await db.Set<Workspaces>().AsNoTracking().Where(w => w.ArchivedAt == null).Select(w => w.Id).ToListAsync(ct);
        }
        var seeded = 0;
        foreach (var w in workspaces) if (await SeedAsync(w, ct) > 0) seeded++;
        return seeded;
    }

    private static async Task<List<GuardFailure>> CheckAsync(ReleaseDbContext db, string name, string? defaultTemplateId, string? exceptId, CancellationToken ct)
    {
        var f = new List<GuardFailure>();
        if (name.Length is 0 or > ReleaseModelLimits.MaxReleaseTypeName) f.Add(Bad($"A release type name is 1 to {ReleaseModelLimits.MaxReleaseTypeName} characters"));
        else if (await db.Set<ReleaseTypes>().AnyAsync(x => x.Name == name && x.Id != exceptId, ct)) f.Add(new(ReleaseModelGuards.ReleaseTypeNameTaken, $"A release type called \"{name}\" exists"));
        if (defaultTemplateId is not null && !await db.Set<TrainTemplates>().AnyAsync(t => t.Id == defaultTemplateId, ct)) f.Add(Bad("The default template does not exist in this workspace"));
        return f;
    }

    private static object Snapshot(ReleaseTypes x) => new { x.Name, x.SortOrder, x.DefaultTemplateId, x.IsBeta, archived = x.ArchivedAt is not null, x.Version };

    public Task<ServiceResult<ReleaseTypes>> CreateAsync(NewReleaseType n, Actor actor, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var name = (n.Name ?? "").Trim();
            var tpl = string.IsNullOrWhiteSpace(n.DefaultTemplateId) ? null : n.DefaultTemplateId;
            var f = await CheckAsync(db, name, tpl, null, ct);
            if (f.Count > 0) return ServiceResult<ReleaseTypes>.Fail(f);
            var order = n.SortOrder ?? (await db.Set<ReleaseTypes>().Select(x => (int?)x.SortOrder).MaxAsync(ct) ?? 0) + 10;
            var row = new ReleaseTypes { Id = Ids.New(), Name = name, SortOrder = order, DefaultTemplateId = tpl, IsBeta = n.IsBeta == true };
            db.Set<ReleaseTypes>().Add(row);
            Audit(db, actor, null, Entity, row.Id, "Create", null, Snapshot(row));
            await db.SaveChangesAsync(ct);
            return ServiceResult<ReleaseTypes>.Ok(row);
        }, ct);

    public Task<ServiceResult<ReleaseTypes>> UpdateAsync(string id, ReleaseTypePatch p, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var row = await db.Set<ReleaseTypes>().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (row is null) return ServiceResult<ReleaseTypes>.NotFound("release type");
            if (VersionMismatch(expectedVersion, row.Version)) return ServiceResult<ReleaseTypes>.Conflict(row);
            var name = p.Name is null ? row.Name : p.Name.Trim();
            var tpl = p.DefaultTemplateId is null ? row.DefaultTemplateId : p.DefaultTemplateId.Length == 0 ? null : p.DefaultTemplateId;
            var order = p.SortOrder ?? row.SortOrder;
            var beta = p.IsBeta ?? row.IsBeta;
            var f = await CheckAsync(db, name, tpl == row.DefaultTemplateId ? null : tpl, id, ct);
            if (beta != row.IsBeta && await db.Set<ReleaseTrains>().AnyAsync(t => t.ReleaseTypeId == id, ct))
                f.Add(Bad($"Releases already use \"{row.Name}\", so it cannot {(beta ? "become" : "stop being")} a Beta type"));
            if (f.Count > 0) return ServiceResult<ReleaseTypes>.Fail(f);
            if (name == row.Name && tpl == row.DefaultTemplateId && order == row.SortOrder && beta == row.IsBeta) return ServiceResult<ReleaseTypes>.Ok(row);
            var before = Snapshot(row);
            row.Name = name; row.DefaultTemplateId = tpl; row.SortOrder = order; row.IsBeta = beta; row.Version++;
            Audit(db, actor, null, Entity, row.Id, "Update", before, Snapshot(row));
            await db.SaveChangesAsync(ct);
            return ServiceResult<ReleaseTypes>.Ok(row);
        }, ct);

    public Task<ServiceResult<ReleaseTypes>> SetArchivedAsync(string id, bool archived, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var row = await db.Set<ReleaseTypes>().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (row is null) return ServiceResult<ReleaseTypes>.NotFound("release type");
            if (VersionMismatch(expectedVersion, row.Version)) return ServiceResult<ReleaseTypes>.Conflict(row);
            if ((row.ArchivedAt is not null) == archived) return ServiceResult<ReleaseTypes>.Ok(row);   // repeat is a no-op
            if (archived && !await db.Set<ReleaseTypes>().AnyAsync(x => x.Id != id && x.ArchivedAt == null, ct))
                return ServiceResult<ReleaseTypes>.Fail(Bad("At least one release type must stay active: every new release needs one"));
            var before = Snapshot(row);
            row.ArchivedAt = archived ? Now : null; row.Version++;
            Audit(db, actor, null, Entity, row.Id, archived ? "Archive" : "Unarchive", before, Snapshot(row));
            await db.SaveChangesAsync(ct);
            return ServiceResult<ReleaseTypes>.Ok(row);
        }, ct);
}
