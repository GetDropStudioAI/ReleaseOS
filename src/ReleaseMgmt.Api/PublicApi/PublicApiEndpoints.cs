using System.Security.Claims;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Api.Endpoints;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Api.PublicApi;

/// <summary>The public operations that are not a forward of an internal endpoint (REOS-161): recording deployments with a token, and the OpenAPI document. Every other row of <see cref="PublicApiCatalog"/> is forwarded.</summary>
public static class DeploymentsNative
{
    public sealed record EventsBody(string? DeploymentExternalId, IReadOnlyList<IngestEvent>? Events);

    private static IngestPrincipal Who(ClaimsPrincipal u) =>
        new(null, u.FindFirstValue(PublicApiGateway.TokenNameClaim) ?? "API token", u.FindFirstValue(ReleaseMgmt.Api.Auth.WorkspaceResolution.Claim) ?? throw new InvalidOperationException("API token request without a workspace"));

    public static void MapPublicApi(this WebApplication app)
    {
        var pub = app.MapGroup(PublicApiCatalog.Prefix).AddEndpointFilterFactory(FieldLimits.Filter);   // the same per-field limits as /api/v1 (REOS-66)
        pub.MapGet("/openapi.json", (HttpContext http) =>
            Results.Text(OpenApiBuilder.Json(), "application/json")).AllowAnonymous();

        // Role: the person the token acts as must be a planner (RTE or Release Manager), as for the controls on a deployment; the ingest keys carry no role and stay the way a tool pushes without a person.
        pub.MapPost("/deployments", async (ReleaseMgmt.Infrastructure.Services.IngestDeployment b, ClaimsPrincipal u, DeploymentService s, CancellationToken ct) =>
        {
            var r = await s.UpsertAsync(b, Who(u), ct);
            return r.IsOk ? Results.Json(r.Value, statusCode: r.Value!.Created ? StatusCodes.Status201Created : StatusCodes.Status200OK) : r.ToHttp();
        }).RequireAuthorization(Policies.Plan);
        pub.MapPost("/deployments/events", (EventsBody b, ClaimsPrincipal u, DeploymentService s, CancellationToken ct) =>
            s.AddEventsAsync(b.DeploymentExternalId, b.Events, Who(u), ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);
    }
}
