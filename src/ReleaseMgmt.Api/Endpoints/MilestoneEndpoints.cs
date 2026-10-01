using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>
/// Train milestones (decision 2026-10-01, Q-0840..Q-0846). Read: any signed-in role. Create, edit and remove: planners (RTE, Release Manager), as for gates and tasks
/// (PROJECT_SCOPE section 1). Done/undone: planners, or the milestone's owner (the person, or a member of the owning team) unless that person is a Viewer: Viewers are
/// read-only (Q-0842). Every mutation but create needs If-Match (Q-004); a stale one answers 409 with the current row.
/// </summary>
public static class MilestoneEndpoints
{
    public sealed record AddMilestoneBody(string? Name, DateOnly? DueOn, string? OwnerUserId, string? OwnerTeamId, string? Note);
    public sealed record PatchMilestoneBody(string? Name, DateOnly? DueOn, string? OwnerUserId, string? OwnerTeamId, bool? ClearOwner, string? Note);

    private static Actor ActorOf(ClaimsPrincipal u) => new(u.FindFirstValue("uid") ?? throw new InvalidOperationException("Signed-in user has no uid claim"));

    public static void MapMilestones(this RouteGroupBuilder api)
    {
        api.MapGet("/trains/{id}/milestones", async (string id, MilestoneService s, CancellationToken ct) =>
            await s.ListAsync(id, ct) is { } rows ? Results.Ok(rows) : Results.NotFound(new { message = "train not found" })).RequireAuthorization(Policies.Read);
        api.MapPost("/trains/{id}/milestones", (string id, AddMilestoneBody b, ClaimsPrincipal u, MilestoneService s, CancellationToken ct) =>
            s.AddAsync(id, new(b.Name, b.DueOn, b.OwnerUserId, b.OwnerTeamId, b.Note), ActorOf(u), ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);
        api.MapPatch("/milestones/{id}", (string id, PatchMilestoneBody b, ClaimsPrincipal u, HttpRequest r, MilestoneService s, CancellationToken ct) =>
            s.UpdateAsync(id, new(b.Name, b.DueOn, b.OwnerUserId, b.OwnerTeamId, b.ClearOwner, b.Note), ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);
        api.MapPost("/milestones/{id}:done", async (string id, ClaimsPrincipal u, HttpRequest r, MilestoneService s, IDbContextFactory<ReleaseDbContext> dbf, CancellationToken ct) =>
            await Denial(id, u, dbf, ct) ?? await s.MarkDoneAsync(id, ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.Read);
        api.MapPost("/milestones/{id}:undone", async (string id, ClaimsPrincipal u, HttpRequest r, MilestoneService s, IDbContextFactory<ReleaseDbContext> dbf, CancellationToken ct) =>
            await Denial(id, u, dbf, ct) ?? await s.MarkUndoneAsync(id, ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.Read);
        api.MapDelete("/milestones/{id}", (string id, ClaimsPrincipal u, HttpRequest r, MilestoneService s, CancellationToken ct) =>
            s.DeleteAsync(id, ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);
    }

    /// <summary>Null when the caller may mark the milestone done or undone; otherwise a 403. An unknown milestone is left to the service (404) for non-Viewers.</summary>
    private static async Task<IResult?> Denial(string id, ClaimsPrincipal u, IDbContextFactory<ReleaseDbContext> dbf, CancellationToken ct)
    {
        if (u.IsInRole(Roles.RTE) || u.IsInRole(Roles.ReleaseManager)) return null;
        var refused = Results.Json(new { message = "Only the milestone's owner, an RTE or a Release Manager can do this" }, statusCode: StatusCodes.Status403Forbidden);
        if (!u.IsInRole(Roles.GovernanceOfficer)) return refused;   // Viewers are read-only, even for a milestone they own
        await using var db = await dbf.CreateDbContextAsync(ct);
        var m = await db.Set<TrainMilestones>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (m is null) return null;
        var uid = u.FindFirstValue("uid");
        var owner = m.OwnerUserId == uid || (m.OwnerTeamId is not null && await db.Set<TeamMembers>().AnyAsync(t => t.TeamId == m.OwnerTeamId && t.UserId == uid, ct));
        return owner ? null : refused;
    }
}
