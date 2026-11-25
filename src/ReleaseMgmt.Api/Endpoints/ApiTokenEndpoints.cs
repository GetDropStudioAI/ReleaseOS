using System.Security.Claims;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>
/// API tokens (REOS-162), the Admin screen's endpoints: RTE and Release Manager list, make, rotate and revoke the workspace's tokens (a Viewer or Governance Officer gets 403). The secret is returned once, in the
/// answer to make or rotate; the list never has it. A token is used only at <c>/api/public/v1</c>, never here: a token cannot manage tokens.
/// </summary>
public static class ApiTokenEndpoints
{
    public sealed record CreateBody(string? Name, IReadOnlyList<string>? Scopes, DateTime? ExpiresAt);
    public sealed record RotateBody(DateTime? ExpiresAt);

    private static Actor ActorOf(ClaimsPrincipal u) => new(u.FindFirstValue("uid") ?? throw new InvalidOperationException("Signed-in user has no uid claim"));

    public static void MapApiTokens(this RouteGroupBuilder api)
    {
        api.MapGet("/api-tokens", async (ApiTokenService s, CancellationToken ct) => Results.Ok(new { scopes = ApiTokenScopes.All, tokens = await s.ListAsync(ct) })).RequireAuthorization(Policies.Admin);
        api.MapPost("/api-tokens", async (CreateBody b, ClaimsPrincipal u, ApiTokenService s, CancellationToken ct) =>
        {
            var r = await s.CreateAsync(b.Name, b.Scopes, b.ExpiresAt, ActorOf(u), ct);
            return r.IsOk ? Results.Json(r.Value, statusCode: StatusCodes.Status201Created) : r.ToHttp();
        }).RequireAuthorization(Policies.Admin);
        // The body is optional (a new expiry); it is read here, not bound, so a call with no body at all is a rotation with the same expiry.
        api.MapPost("/api-tokens/{id}:rotate", async (string id, ClaimsPrincipal u, HttpRequest req, ApiTokenService s, CancellationToken ct) =>
        {
            var body = req.HasJsonContentType() && req.ContentLength is not 0 ? await req.ReadFromJsonAsync<RotateBody>(ct) : null;
            return (await s.RotateAsync(id, body?.ExpiresAt, ActorOf(u), req.IfMatch(), ct)).ToHttp();
        }).RequireAuthorization(Policies.Admin);
        api.MapPost("/api-tokens/{id}:revoke", async (string id, ClaimsPrincipal u, HttpRequest req, ApiTokenService s, CancellationToken ct) =>
            (await s.RevokeAsync(id, ActorOf(u), req.IfMatch(), ct)).ToHttp()).RequireAuthorization(Policies.Admin);
    }
}
