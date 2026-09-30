using System.Security.Claims;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Sync;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>
/// REOS-39 Connectors admin and ExternalLinks management. Reads are open to every signed-in role; writes are admin (RTE or Release Manager, D14/D32).
/// No response ever carries a credential: the list says only whether credentials are stored and of what kind.
/// </summary>
public static class ConnectorEndpoints
{
    public sealed record SettingsBody(string? BaseUrl, bool? IsEnabled);
    public sealed record CredentialsBody(string? Kind, string? Username, string? Secret);
    public sealed record LinkBody(string EntityType, string EntityId, string SourceSystem, string ExternalKey);

    private static Actor ActorOf(ClaimsPrincipal u) => new(u.FindFirstValue("uid") ?? throw new InvalidOperationException("Signed-in user has no uid claim"));

    public static void MapConnectors(this RouteGroupBuilder api)
    {
        api.MapGet("/connectors", async (ConnectorService s, CancellationToken ct) => Results.Ok(await s.ListAsync(ct))).RequireAuthorization(Policies.Read);

        api.MapPut("/connectors/{source}", async (string source, SettingsBody b, ClaimsPrincipal u, HttpRequest r, ConnectorService s, CancellationToken ct) =>
        {
            // The first save creates the row and has no Version to send; every later edit is If-Match like the rest of the API.
            var existing = (await s.ListAsync(ct)).FirstOrDefault(c => c.Source == source)?.Version;
            return (await s.SaveSettingsAsync(source, b.BaseUrl, b.IsEnabled, ActorOf(u), existing is null ? null : r.IfMatch(), ct)).ToHttp();
        }).RequireAuthorization(Policies.Admin);

        api.MapPut("/connectors/{source}/credentials", async (string source, CredentialsBody b, ClaimsPrincipal u, ConnectorService s, CancellationToken ct) =>
            (await s.SetCredentialsAsync(source, b.Kind, b.Username, b.Secret, ActorOf(u), ct)).ToHttp()).RequireAuthorization(Policies.Admin);
        api.MapDelete("/connectors/{source}/credentials", async (string source, ClaimsPrincipal u, ConnectorService s, CancellationToken ct) =>
            (await s.ClearCredentialsAsync(source, ActorOf(u), ct)).ToHttp()).RequireAuthorization(Policies.Admin);

        api.MapPost("/connectors/{source}:test", async (string source, ClaimsPrincipal u, ConnectorService s, CancellationToken ct) =>
            (await s.TestAsync(source, ActorOf(u), ct)).ToHttp()).RequireAuthorization(Policies.Admin);

        // "Sync now" for one connector: one cycle, same code path as the timer.
        api.MapPost("/connectors/{source}:sync", async (string source, SyncPollerService p, CancellationToken ct) =>
            ReleaseMgmt.Domain.Sync.ExternalKeys.Sources.Contains(source) ? Results.Ok(await p.RunConnectorAsync(source, ct)) : Results.NotFound(new { message = "connector not found" }))
            .RequireAuthorization(Policies.Admin);

        api.MapGet("/trains/{id}/links", async (string id, ConnectorService s, CancellationToken ct) => (await s.ListLinksAsync(id, ct)).ToHttp()).RequireAuthorization(Policies.Read);
        api.MapPost("/trains/{id}/links", async (string id, LinkBody b, ClaimsPrincipal u, ConnectorService s, CancellationToken ct) =>
            (await s.AddLinkAsync(id, b.EntityType, b.EntityId, b.SourceSystem, b.ExternalKey, ActorOf(u), ct)).ToHttp()).RequireAuthorization(Policies.Admin);
        api.MapDelete("/links/{id}", async (string id, ClaimsPrincipal u, HttpRequest r, ConnectorService s, CancellationToken ct) =>
            (await s.RemoveLinkAsync(id, ActorOf(u), r.IfMatch(), ct)).ToHttp()).RequireAuthorization(Policies.Admin);
    }
}
