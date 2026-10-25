using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Api.Auth;

/// <summary>A workspace the signed-in person belongs to, with the role they hold there.</summary>
public sealed record WorkspaceChoice(string Id, string Slug, string Name, string Role);

/// <summary>
/// REOS-121 (D49): which workspaces an analytics request reads. <c>workspaces=</c> names some of them (by slug or id, comma-separated) or <c>all</c>; without it the request reads
/// the current workspace only, as before. Whatever is asked, only the person's own memberships can be read: a workspace they are not a member of, an archived one and one that does
/// not exist are the same 404 <c>WorkspaceNotFound</c>, so the answer does not say whether it exists (the rule of <see cref="WorkspaceResolution"/>). No new role: a VP or Director
/// is simply a member of more workspaces.
/// </summary>
public sealed class AnalyticsScope(IDbContextFactory<ReleaseDbContext> dbf)
{
    public const string Query = "workspaces";

    /// <summary>The person's workspaces (not archived), by name.</summary>
    public async Task<IReadOnlyList<WorkspaceChoice>> MineAsync(HttpContext http, CancellationToken ct)
    {
        var uid = http.User.FindFirst("uid")?.Value;
        if (uid is null) return [];
        await using var db = await dbf.CreateDbContextAsync(ct);
        var rows = await (from m in db.Set<WorkspaceMembers>().IgnoreQueryFilters().AsNoTracking()
                          join w in db.Set<Workspaces>().IgnoreQueryFilters().AsNoTracking() on m.WorkspaceId equals w.Id
                          where m.UserId == uid && w.ArchivedAt == null
                          select new WorkspaceChoice(w.Id, w.Slug, w.Name, m.Role)).ToListAsync(ct);
        return [.. rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Id, StringComparer.Ordinal)];
    }

    /// <summary>The workspaces to read, or an error result. Null workspaces with no error = the request did not ask, so the current workspace is read.</summary>
    public async Task<(IReadOnlyList<WorkspaceChoice>? Chosen, IResult? Error)> ResolveAsync(HttpContext http, CancellationToken ct)
    {
        var asked = http.Request.Query[Query].ToString();
        if (string.IsNullOrWhiteSpace(asked)) return (null, null);
        var mine = await MineAsync(http, ct);
        if (asked.Trim().Equals("all", StringComparison.OrdinalIgnoreCase)) return (mine, null);
        var chosen = new List<WorkspaceChoice>();
        foreach (var name in asked.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var hit = mine.FirstOrDefault(m => m.Id == name || string.Equals(m.Slug, name, StringComparison.OrdinalIgnoreCase));
            if (hit is null) return (null, Results.Json(new { guard = WorkspaceResolution.NotFound, message = "No workspace of yours has that name" }, statusCode: StatusCodes.Status404NotFound));
            if (chosen.All(c => c.Id != hit.Id)) chosen.Add(hit);
        }
        if (chosen.Count == 0) return (null, Results.Json(new { guard = "InvalidFilter", message = "workspaces names one or more of your workspaces by slug, or all" }, statusCode: StatusCodes.Status400BadRequest));
        return (chosen, null);
    }
}
