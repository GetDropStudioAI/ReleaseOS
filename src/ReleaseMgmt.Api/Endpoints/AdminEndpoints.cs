using System.Security.Claims;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Api.Endpoints;

public static class AdminEndpoints
{
    public sealed record PatchUser(string? Handle, bool? IsActive);
    public sealed record TeamBody(string Handle, string Name, string[]? MemberIds);
    public sealed record TeamPatch(string? Name, string[]? MemberIds);
    public sealed record HolidayBody(DateOnly Day, string Name);

    private static Actor ActorOf(ClaimsPrincipal u) => new(u.FindFirstValue("uid") ?? throw new InvalidOperationException("Signed-in user has no uid claim"));

    /// <summary>Reads are open to any signed-in role (owners are picked from these lists); writes are admin (RTE or Release Manager in v1).</summary>
    public static void MapAdmin(this RouteGroupBuilder api)
    {
        api.MapGet("/users", (AdminService s, CancellationToken ct) => s.ListUsersAsync(ct)).RequireAuthorization(Policies.Read);
        api.MapPatch("/users/{id}", async (string id, PatchUser b, ClaimsPrincipal u, HttpRequest r, AdminService s, CancellationToken ct) =>
            (await s.PatchUserAsync(id, b.Handle, b.IsActive, ActorOf(u), r.IfMatch(), ct)).ToHttp()).RequireAuthorization(Policies.Admin);

        api.MapGet("/teams", (AdminService s, CancellationToken ct) => s.ListTeamsAsync(ct)).RequireAuthorization(Policies.Read);
        api.MapPost("/teams", async (TeamBody b, ClaimsPrincipal u, AdminService s, CancellationToken ct) =>
            (await s.CreateTeamAsync(b.Handle, b.Name, b.MemberIds ?? [], ActorOf(u), ct)).ToHttp()).RequireAuthorization(Policies.Admin);
        api.MapPatch("/teams/{id}", async (string id, TeamPatch b, ClaimsPrincipal u, HttpRequest r, AdminService s, CancellationToken ct) =>
            (await s.UpdateTeamAsync(id, b.Name, b.MemberIds, ActorOf(u), r.IfMatch(), ct)).ToHttp()).RequireAuthorization(Policies.Admin);

        api.MapGet("/holidays", (AdminService s, CancellationToken ct) => s.ListHolidaysAsync(ct)).RequireAuthorization(Policies.Read);
        api.MapPost("/holidays", async (HolidayBody b, ClaimsPrincipal u, AdminService s, CancellationToken ct) =>
            (await s.AddHolidayAsync(b.Day, b.Name, ActorOf(u), ct)).ToHttp()).RequireAuthorization(Policies.Admin);
        api.MapDelete("/holidays/{day}", async (DateOnly day, ClaimsPrincipal u, AdminService s, CancellationToken ct) =>
            (await s.RemoveHolidayAsync(day, ActorOf(u), ct)).ToHttp()).RequireAuthorization(Policies.Admin);
    }
}
