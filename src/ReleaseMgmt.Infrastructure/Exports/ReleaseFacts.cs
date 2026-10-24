using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Infrastructure.Exports;

/// <summary>One release as the files print it (REOS-137): names, not ids.</summary>
public sealed record ReleaseFact(string TrainId, string Title, string? Key, string? Owner, string? Team, IReadOnlyList<string> Environments)
{
    public string EnvironmentsText => string.Join(", ", Environments);
}

/// <summary>
/// The release model and workspace that every file leaving the app carries (REOS-137, D45): key, owner, team, environments and the workspace name.
/// Read in a few set-based queries, so a grid of 50,000 rows costs the same four lookups as a grid of one.
/// </summary>
public static class ReleaseFacts
{
    /// <summary>The read-only columns appended to a grid that is about releases. <c>#</c> columns are skipped by an import, so an export still re-imports unchanged.</summary>
    public static readonly string[] GridColumns = ["#Workspace", "#ReleaseKey", "#ReleaseOwner", "#ReleaseTeam", "#ReleaseEnvironments"];

    public static async Task<string> WorkspaceNameAsync(ReleaseDbContext db, CancellationToken ct)
    {
        var ws = Memberships.WorkspaceOf(db);
        return await db.Set<Workspaces>().IgnoreQueryFilters().AsNoTracking().Where(w => w.Id == ws).Select(w => w.Name).SingleOrDefaultAsync(ct) ?? "";
    }

    /// <summary>The releases with these ids, or every release of the context's workspace when <paramref name="trainIds"/> is null.</summary>
    public static async Task<IReadOnlyDictionary<string, ReleaseFact>> LoadAsync(ReleaseDbContext db, IReadOnlyCollection<string>? trainIds, CancellationToken ct)
    {
        var q = db.Set<ReleaseTrains>().AsNoTracking().AsQueryable();
        if (trainIds is not null) q = q.Where(t => trainIds.Contains(t.Id));
        var trains = await q.Select(t => new { t.Id, t.Title, t.ReleaseKey, t.OwnerUserId, t.OwnerTeamId }).ToListAsync(ct);
        var users = (await db.Set<Users>().AsNoTracking().Select(u => new { u.Id, u.DisplayName }).ToListAsync(ct)).ToDictionary(u => u.Id, u => u.DisplayName);
        var teams = (await db.Set<Teams>().AsNoTracking().Select(t => new { t.Id, t.Name }).ToListAsync(ct)).ToDictionary(t => t.Id, t => t.Name);
        var envs = await (from te in db.Set<TrainEnvironments>().AsNoTracking() join e in db.Set<Environments>().AsNoTracking() on te.EnvironmentId equals e.Id
                          select new { te.ReleaseTrainId, e.Name }).ToListAsync(ct);
        var byTrain = envs.GroupBy(e => e.ReleaseTrainId).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(x => x.Name).Order(StringComparer.Ordinal)]);
        return trains.ToDictionary(t => t.Id, t => new ReleaseFact(t.Id, t.Title, t.ReleaseKey,
            t.OwnerUserId is not null ? users.GetValueOrDefault(t.OwnerUserId) : null, t.OwnerTeamId is not null ? teams.GetValueOrDefault(t.OwnerTeamId) : null,
            byTrain.GetValueOrDefault(t.Id) ?? []));
    }

    /// <summary>The five cells of <see cref="GridColumns"/> for a row about <paramref name="trainId"/> (blank release cells when it is not about one).</summary>
    public static string[] GridCells(string workspace, ReleaseFact? f) => [workspace, f?.Key ?? "", f?.Owner ?? "", f?.Team ?? "", f?.EnvironmentsText ?? ""];
}
