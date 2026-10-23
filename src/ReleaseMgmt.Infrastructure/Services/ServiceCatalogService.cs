using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

public sealed record CatalogServiceView(string Id, string ServiceKey, string Name, TeamRef? OwnerTeam, string? Repository, string? ProjectCode, bool Archived, int Products, int Deployments, int Version);
public sealed record NewCatalogService(string? ServiceKey, string? Name, string? OwnerTeamId = null, string? Repository = null, string? ProjectCode = null);
/// <summary>Null leaves a field as it is; an empty string clears the owner team, repository or project code. The key never changes: the import and the ingest API name a service by it.</summary>
public sealed record CatalogServicePatch(string? Name = null, string? OwnerTeamId = null, string? Repository = null, string? ProjectCode = null);
/// <summary>Link a product to a service by id or by key; both null removes the link.</summary>
public sealed record ProductServiceLink(string? ServiceId = null, string? ServiceKey = null);

/// <summary>
/// The service catalog of a workspace (REOS-109, B8, D42): name, owning team, repository or project code. A train's products link to a catalog service
/// (<see cref="LinkProductAsync"/>; the CSV import accepts the service key); deployments are of catalog services. Planners create, edit and archive; every write is one
/// transaction with one <c>CatalogService</c> audit row and a Version bump (If-Match on edits). An archived service cannot be linked or deployed, and stays on what uses it.
/// </summary>
public sealed partial class ServiceCatalogService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IRealtimePublisher? realtime = null) : ServiceBase(dbf, time, realtime)
{
    private const string Entity = "CatalogService";

    [GeneratedRegex("^[A-Za-z0-9._-]{1,60}$")]
    private static partial Regex KeyRegex();

    public static bool IsValidKey(string? key) => key is not null && KeyRegex().IsMatch(key);

    private static GuardFailure Bad(string m) => new(ReleaseModelGuards.ServiceInvalid, m);

    public async Task<List<CatalogServiceView>> ListAsync(bool includeArchived = false, string? q = null, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var query = db.Set<CatalogServices>().AsNoTracking().Where(s => includeArchived || s.ArchivedAt == null);
        if (!string.IsNullOrWhiteSpace(q)) { var like = $"%{q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%"; query = query.Where(s => EF.Functions.Like(s.ServiceKey, like, "\\") || EF.Functions.Like(s.Name, like, "\\")); }
        return await ViewsAsync(db, await query.OrderBy(s => s.ServiceKey).ToListAsync(ct), ct);
    }

    public async Task<CatalogServiceView?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var row = await db.Set<CatalogServices>().AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct);
        return row is null ? null : (await ViewsAsync(db, [row], ct))[0];
    }

    /// <summary>The service a key names in the context's workspace (any case), or null.</summary>
    public static Task<CatalogServices?> FindByKeyAsync(ReleaseDbContext db, string key, CancellationToken ct = default) =>
        db.Set<CatalogServices>().SingleOrDefaultAsync(s => s.ServiceKey == key, ct);

    private static async Task<List<CatalogServiceView>> ViewsAsync(ReleaseDbContext db, List<CatalogServices> rows, CancellationToken ct)
    {
        var teamIds = rows.Select(r => r.OwnerTeamId).OfType<string>().Distinct().ToList();
        var teams = await db.Set<Teams>().AsNoTracking().Where(t => teamIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => new TeamRef(t.Id, t.Handle, t.Name), ct);
        var products = await db.Set<BundledProducts>().AsNoTracking().Where(p => p.CatalogServiceId != null).GroupBy(p => p.CatalogServiceId!).Select(g => new { Id = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N, ct);
        var deployments = await db.Set<Deployments>().AsNoTracking().GroupBy(d => d.CatalogServiceId).Select(g => new { Id = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N, ct);
        return [.. rows.Select(s => new CatalogServiceView(s.Id, s.ServiceKey, s.Name, s.OwnerTeamId is not null && teams.TryGetValue(s.OwnerTeamId, out var t) ? t : null, s.Repository, s.ProjectCode,
            s.ArchivedAt is not null, products.GetValueOrDefault(s.Id), deployments.GetValueOrDefault(s.Id), s.Version))];
    }

    private static object Snapshot(CatalogServices s) => new { s.ServiceKey, s.Name, s.OwnerTeamId, s.Repository, s.ProjectCode, archived = s.ArchivedAt is not null, s.Version };

    private static async Task<List<GuardFailure>> CheckAsync(ReleaseDbContext db, CatalogServices s, string? originalTeam, bool isNew, CancellationToken ct)
    {
        var f = new List<GuardFailure>();
        if (isNew)
        {
            if (!IsValidKey(s.ServiceKey)) f.Add(Bad($"A service key is 1 to {ReleaseModelLimits.MaxServiceKey} letters, digits, '.', '_' or '-'"));
            else if (await db.Set<CatalogServices>().AnyAsync(x => x.ServiceKey == s.ServiceKey && x.Id != s.Id, ct)) f.Add(new(ReleaseModelGuards.ServiceKeyTaken, $"A service with the key \"{s.ServiceKey}\" exists"));
        }
        if (s.Name.Length is 0 or > ReleaseModelLimits.MaxServiceName) f.Add(Bad($"A service name is 1 to {ReleaseModelLimits.MaxServiceName} characters"));
        if (s.Repository is { } r && (r.Length is 0 or > ReleaseModelLimits.MaxRepository)) f.Add(Bad($"A repository is 1 to {ReleaseModelLimits.MaxRepository} characters"));
        if (s.ProjectCode is { } c && (c.Length is 0 or > ReleaseModelLimits.MaxProjectCode)) f.Add(Bad($"A project code is 1 to {ReleaseModelLimits.MaxProjectCode} characters"));
        if (s.OwnerTeamId is not null && s.OwnerTeamId != originalTeam && !await db.Set<Teams>().AnyAsync(t => t.Id == s.OwnerTeamId, ct)) f.Add(Bad("The owning team does not exist in this workspace"));
        return f;
    }

    public Task<ServiceResult<CatalogServices>> CreateAsync(NewCatalogService n, Actor actor, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var now = Now;
            var s = new CatalogServices
            {
                Id = Ids.New(), ServiceKey = (n.ServiceKey ?? "").Trim(), Name = (n.Name ?? "").Trim(), OwnerTeamId = string.IsNullOrWhiteSpace(n.OwnerTeamId) ? null : n.OwnerTeamId,
                Repository = string.IsNullOrWhiteSpace(n.Repository) ? null : n.Repository.Trim(), ProjectCode = string.IsNullOrWhiteSpace(n.ProjectCode) ? null : n.ProjectCode.Trim(),
                CreatedAt = now, LastChangedByUserId = actor.UserId, LastChangedAt = now,
            };
            var f = await CheckAsync(db, s, null, true, ct);
            if (f.Count > 0) return ServiceResult<CatalogServices>.Fail(f);
            db.Set<CatalogServices>().Add(s);
            Audit(db, actor, null, Entity, s.Id, "Create", null, Snapshot(s));
            await db.SaveChangesAsync(ct);
            return ServiceResult<CatalogServices>.Ok(s);
        }, ct);

    public Task<ServiceResult<CatalogServices>> UpdateAsync(string id, CatalogServicePatch p, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var s = await db.Set<CatalogServices>().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (s is null) return ServiceResult<CatalogServices>.NotFound("service");
            if (VersionMismatch(expectedVersion, s.Version)) return ServiceResult<CatalogServices>.Conflict(s);
            var originalTeam = s.OwnerTeamId; var before = Snapshot(s);
            if (p.Name is not null) s.Name = p.Name.Trim();
            static string? Pick(string? patch, string? current) => patch is null ? current : patch.Trim().Length == 0 ? null : patch.Trim();
            s.OwnerTeamId = Pick(p.OwnerTeamId, s.OwnerTeamId); s.Repository = Pick(p.Repository, s.Repository); s.ProjectCode = Pick(p.ProjectCode, s.ProjectCode);
            var f = await CheckAsync(db, s, originalTeam, false, ct);
            if (f.Count > 0) { db.Entry(s).State = EntityState.Unchanged; return ServiceResult<CatalogServices>.Fail(f); }
            if (!db.Entry(s).Properties.Any(x => x.IsModified)) return ServiceResult<CatalogServices>.Ok(s);
            s.Version++; s.LastChangedByUserId = actor.UserId; s.LastChangedAt = Now;
            Audit(db, actor, null, Entity, s.Id, "Update", before, Snapshot(s));
            await db.SaveChangesAsync(ct);
            return ServiceResult<CatalogServices>.Ok(s);
        }, ct);

    public Task<ServiceResult<CatalogServices>> SetArchivedAsync(string id, bool archived, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var s = await db.Set<CatalogServices>().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (s is null) return ServiceResult<CatalogServices>.NotFound("service");
            if (VersionMismatch(expectedVersion, s.Version)) return ServiceResult<CatalogServices>.Conflict(s);
            if ((s.ArchivedAt is not null) == archived) return ServiceResult<CatalogServices>.Ok(s);
            var before = Snapshot(s);
            var now = Now;
            s.ArchivedAt = archived ? now : null; s.Version++; s.LastChangedByUserId = actor.UserId; s.LastChangedAt = now;
            Audit(db, actor, null, Entity, s.Id, archived ? "Archive" : "Unarchive", before, Snapshot(s));
            await db.SaveChangesAsync(ct);
            return ServiceResult<CatalogServices>.Ok(s);
        }, ct);

    /// <summary>Links a train's product to a catalog service, or removes the link. If-Match is the product's Version. One <c>BundledProduct</c> audit row on the train.</summary>
    public Task<ServiceResult<BundledProducts>> LinkProductAsync(string productId, ProductServiceLink link, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var p = await db.Set<BundledProducts>().SingleOrDefaultAsync(x => x.Id == productId, ct);
            if (p is null) return ServiceResult<BundledProducts>.NotFound("product");
            if (VersionMismatch(expectedVersion, p.Version)) return ServiceResult<BundledProducts>.Conflict(p);
            if (link.ServiceId is not null && link.ServiceKey is not null) return ServiceResult<BundledProducts>.Fail(Bad("Name the service by id or by key, not both"));
            CatalogServices? s = null;
            if (link.ServiceId is not null) s = await db.Set<CatalogServices>().SingleOrDefaultAsync(x => x.Id == link.ServiceId, ct);
            else if (link.ServiceKey is not null) s = await FindByKeyAsync(db, link.ServiceKey.Trim(), ct);
            if ((link.ServiceId is not null || link.ServiceKey is not null) && s is null) return ServiceResult<BundledProducts>.NotFound("service");
            if (s is not null && s.ArchivedAt is not null && s.Id != p.CatalogServiceId) return ServiceResult<BundledProducts>.Fail(Bad($"The service \"{s.ServiceKey}\" is archived"));
            if (p.CatalogServiceId == s?.Id) return ServiceResult<BundledProducts>.Ok(p);
            var before = new { p.ProductName, p.CatalogServiceId };
            p.CatalogServiceId = s?.Id; p.Version++;
            Audit(db, actor, p.ReleaseTrainId, "BundledProduct", p.Id, s is null ? "UnlinkService" : "LinkService", before, new { p.ProductName, p.CatalogServiceId, serviceKey = s?.ServiceKey });
            await db.SaveChangesAsync(ct);
            return ServiceResult<BundledProducts>.Ok(p);
        }, ct);
}
