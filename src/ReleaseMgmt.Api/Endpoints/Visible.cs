using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>
/// REOS-105: a list under a train or gate answers 404 when that train or gate is not in the current workspace (or does not exist), as its detail
/// endpoint does, instead of an empty list that would confirm nothing but still answer for another workspace's id.
/// </summary>
public static class Visible
{
    public static RouteHandlerBuilder TrainInRoute(this RouteHandlerBuilder b) => b.AddEndpointFilter(async (ctx, next) =>
        await ExistsAsync(ctx.HttpContext, (db, id, ct) => db.Set<ReleaseTrains>().AnyAsync(t => t.Id == id, ct)) ? await next(ctx) : Results.NotFound(new { message = "train not found" }));

    public static RouteHandlerBuilder GateInRoute(this RouteHandlerBuilder b) => b.AddEndpointFilter(async (ctx, next) =>
        await ExistsAsync(ctx.HttpContext, (db, id, ct) => db.Set<StageGates>().AnyAsync(g => g.Id == id, ct)) ? await next(ctx) : Results.NotFound(new { message = "gate not found" }));

    private static async Task<bool> ExistsAsync(HttpContext http, Func<ReleaseDbContext, string, CancellationToken, Task<bool>> query)
    {
        if (http.Request.RouteValues["id"] is not string id) return false;
        await using var db = await http.RequestServices.GetRequiredService<IDbContextFactory<ReleaseDbContext>>().CreateDbContextAsync(http.RequestAborted);
        return await query(db, id, http.RequestAborted);
    }
}
