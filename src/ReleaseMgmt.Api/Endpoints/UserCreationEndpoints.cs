using System.Security.Claims;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>REOS-83: POST /users adds a person ahead of their first sign-in. Admin policy, the same as PATCH /users/{id} (RTE or Release Manager in v1).</summary>
public static class UserCreationEndpoints
{
    public sealed record NewUser(string? Email, string? DisplayName, string? Role, string? Handle);

    public static void MapUserCreation(this RouteGroupBuilder api) =>
        api.MapPost("/users", async (NewUser b, ClaimsPrincipal u, UserCreationService s, CancellationToken ct) =>
            (await s.CreateUserAsync(b.Email, b.DisplayName, b.Role, b.Handle,
                new Actor(u.FindFirstValue("uid") ?? throw new InvalidOperationException("Signed-in user has no uid claim")), ct)).ToHttp())
            .RequireAuthorization(Policies.Admin);
}
