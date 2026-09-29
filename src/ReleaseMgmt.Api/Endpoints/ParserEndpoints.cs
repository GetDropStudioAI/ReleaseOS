using System.Security.Claims;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>POST /trains/{id}/tasks:parse and :commit (PROJECT_SCOPE section 6).</summary>
public static class ParserEndpoints
{
    public sealed record ParseBody(string? Text, string? DefaultGateId);
    public sealed record CommitBody(string PreviewId, bool AcknowledgeDecertify);

    public static void MapParser(this RouteGroupBuilder api)
    {
        api.MapPost("/trains/{id}/tasks:parse", async (string id, ParseBody b, ClaimsPrincipal u, TaskParserService s, CancellationToken ct) =>
            (await s.ParseAsync(id, b.Text, b.DefaultGateId, ActorOf(u), ct)).ToHttp()).RequireAuthorization(Policies.Plan);

        api.MapPost("/trains/{id}/tasks:commit", async (string id, CommitBody b, ClaimsPrincipal u, TaskParserService s, CancellationToken ct) =>
            (await s.CommitAsync(id, b.PreviewId, b.AcknowledgeDecertify, ActorOf(u), ct)).ToHttp()).RequireAuthorization(Policies.Plan);
    }

    private static Actor ActorOf(ClaimsPrincipal u) => new(u.FindFirstValue("uid") ?? throw new InvalidOperationException("Signed-in user has no uid claim"));
}
