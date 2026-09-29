using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>GET/PUT /me/session/{clientId}: the signed-in user's own UI state only; there is no way to address another user's.</summary>
public static partial class SessionEndpoints
{
    public sealed record PutSessionBody(int SchemaVersion, string? ActiveTrainId, JsonElement Ui);

    [GeneratedRegex("^[A-Za-z0-9-]{8,64}$")] private static partial Regex ClientIdShape();

    public static void MapSession(this RouteGroupBuilder api)
    {
        api.MapGet("/me/session/{clientId}", async (string clientId, ClaimsPrincipal u, SessionService s, CancellationToken ct) =>
        {
            if (!ClientIdShape().IsMatch(clientId)) return Results.BadRequest(new { message = "clientId must be 8-64 letters, digits or dashes" });
            var st = await s.GetAsync(Uid(u), clientId, ct);
            return st is null ? Results.NotFound(new { message = "No saved state" })
                : Results.Ok(new { schemaVersion = st.SchemaVersion, activeTrainId = st.ActiveTrainId, ui = JsonDocument.Parse(st.UiJson).RootElement, lastActivityAt = st.LastActivityAt, inherited = st.Inherited });
        }).RequireAuthorization(Policies.Read);

        api.MapPut("/me/session/{clientId}", async (string clientId, PutSessionBody body, ClaimsPrincipal u, SessionService s, CancellationToken ct) =>
        {
            if (!ClientIdShape().IsMatch(clientId)) return Results.BadRequest(new { message = "clientId must be 8-64 letters, digits or dashes" });
            var r = await s.PutAsync(Uid(u), clientId, body.SchemaVersion, body.ActiveTrainId, body.Ui.GetRawText(), ct);
            return r.IsOk ? Results.NoContent() : Results2.ToHttp(r);
        }).RequireAuthorization(Policies.Read);
    }

    private static string Uid(ClaimsPrincipal u) => u.FindFirstValue("uid") ?? throw new InvalidOperationException("Signed-in user has no uid claim");
}
