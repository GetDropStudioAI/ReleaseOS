using System.Security.Claims;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>
/// Telemetry and deployment control (REOS-111, REOS-112, D42). The deployment tool pushes samples to <c>/api/v1/ingest/metrics</c> with its workspace API key. People read series, thresholds, canary state,
/// breaches, SLO and the rollback arming (any signed-in role: the Deployment monitoring screen binds to these) and RTEs and Release Managers pause, resume, promote and roll back (a confirmed action each),
/// arm automatic rollback and pause a run. A control call that does not reach the tool answers 502 with the recorded action.
/// </summary>
public static class DeploymentOpsEndpoints
{
    public sealed record IngestMetricsBody(string? DeploymentExternalId, IReadOnlyList<IngestSample>? Samples);
    public sealed record PauseBody(string? Reason);

    private static Actor ActorOf(ClaimsPrincipal u) => new(u.FindFirstValue("uid") ?? throw new InvalidOperationException("Signed-in user has no uid claim"));
    private static IngestPrincipal Key(ClaimsPrincipal u) => IngestKeyAuthentication.PrincipalOf(u) ?? throw new InvalidOperationException("Ingest request without a key principal");

    public static void MapDeploymentOps(this RouteGroupBuilder api)
    {
        // ---- telemetry -------------------------------------------------------------------------------------------------------------
        api.MapPost("/ingest/metrics", (IngestMetricsBody b, ClaimsPrincipal u, TelemetryService s, CancellationToken ct) =>
            s.IngestAsync(b.DeploymentExternalId, b.Samples, Key(u), ct).ToHttpAsync()).RequireAuthorization(Policies.Ingest);

        // GET /deployments/{id}/metrics?series=Canary|Stable&from=&to=&maxPoints= -> the chart: series (raw, or buckets of the worst value when long), threshold lines, SLO targets, canaryStartedAt, breaches
        api.MapGet("/deployments/{id}/metrics", async (string id, string? series, string? from, string? to, int? maxPoints, TelemetryService s, CancellationToken ct) =>
        {
            if (!AuditQueryService.TryParseBound(from, false, out var f) || !AuditQueryService.TryParseBound(to, true, out var t))
                return Results.Json(new { guard = DeploymentGuards.MetricInvalid, message = "from and to are UTC instants (2026-10-01T10:00:00Z) or days (2026-10-01)" }, statusCode: StatusCodes.Status422UnprocessableEntity);
            if (series is not null && !MetricSeries.All.Contains(series))
                return Results.Json(new { guard = DeploymentGuards.MetricInvalid, message = $"series is one of {string.Join(", ", MetricSeries.All)}" }, statusCode: StatusCodes.Status422UnprocessableEntity);
            return await s.MetricsAsync(id, new(series, f, t, maxPoints ?? TelemetryService.DefaultMaxPoints), ct) is { } v ? Results.Ok(v) : Results.NotFound(new { message = "deployment not found" });
        }).RequireAuthorization(Policies.Read);

        api.MapGet("/deployments/{id}/breaches", async (string id, bool? includeResolved, TelemetryService s, DeploymentService d, CancellationToken ct) =>
            await d.GetAsync(id, 0, ct) is null ? Results.NotFound(new { message = "deployment not found" }) : Results.Ok(await s.BreachesAsync(id, includeResolved != false, 200, ct))).RequireAuthorization(Policies.Read);

        api.MapGet("/deployments/{id}/canary", async (string id, TelemetryService s, CancellationToken ct) =>
            await s.CanaryAsync(id, ct) is { } v ? Results.Ok(v) : Results.NotFound(new { message = "deployment not found" })).RequireAuthorization(Policies.Read);

        // GET /deployments/{id}/monitoring -> deployment, thresholds, SLO targets and status, canary state, breaches, rollback arming, control actions, the latest sample of each series
        api.MapGet("/deployments/{id}/monitoring", async (string id, int? sloDays, TelemetryService s, DeploymentService d, DeploymentControlService c, CancellationToken ct) =>
            await s.MonitoringAsync(id, d, c, sloDays ?? 30, ct) is { } v ? Results.Ok(v) : Results.NotFound(new { message = "deployment not found" })).RequireAuthorization(Policies.Read);

        api.MapGet("/environments/{id}/slo", async (string id, int? days, string? serviceId, TelemetryService s, CancellationToken ct) =>
            await s.SloAsync(id, days ?? 30, serviceId, ct) is { } v ? Results.Ok(v) : Results.NotFound(new { message = "environment not found" })).RequireAuthorization(Policies.Read);

        // ---- control ---------------------------------------------------------------------------------------------------------------
        api.MapGet("/deployments/{id}/control-actions", async (string id, DeploymentControlService s, CancellationToken ct) =>
            await s.RollbackStatusAsync(id, ct) is null ? Results.NotFound(new { message = "deployment not found" }) : Results.Ok(await s.ActionsAsync(id, 100, ct))).RequireAuthorization(Policies.Read);
        api.MapGet("/deployments/{id}/rollback", async (string id, DeploymentControlService s, CancellationToken ct) =>
            await s.RollbackStatusAsync(id, ct) is { } v ? Results.Ok(v) : Results.NotFound(new { message = "deployment not found" })).RequireAuthorization(Policies.Read);

        foreach (var action in DeploymentControlService.Actions)
        {
            var a = action;
            api.MapPost($"/deployments/{{id}}:{a.ToLowerInvariant()}", async (string id, ControlRequest? b, ClaimsPrincipal u, HttpRequest req, DeploymentControlService s, CancellationToken ct) =>
            {
                var r = await s.ControlAsync(id, a, b ?? new(), ActorOf(u), req.IfMatch(), ct);
                if (!r.IsOk) return r.ToHttp();
                // The call reached the service but not the tool: loud (502), with the recorded action; the alert and the live push have already gone out.
                return r.Value!.Status == "Failed"
                    ? Results.Json(new { guard = DeploymentGuards.ControlFailed, message = $"The {a.ToLowerInvariant()} call did not succeed: {r.Value.Error}", action = r.Value }, statusCode: StatusCodes.Status502BadGateway)
                    : Results.Ok(r.Value);
            }).RequireAuthorization(Policies.Plan);
        }
        api.MapPost("/deployments/{id}:arm-rollback", (string id, ClaimsPrincipal u, HttpRequest req, DeploymentControlService s, CancellationToken ct) =>
            s.SetArmedAsync(id, true, ActorOf(u), req.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);
        api.MapPost("/deployments/{id}:disarm-rollback", (string id, ClaimsPrincipal u, HttpRequest req, DeploymentControlService s, CancellationToken ct) =>
            s.SetArmedAsync(id, false, ActorOf(u), req.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);

        // The key the receiver verifies the environment's control calls with: shown once, replaced on request (Admin)
        api.MapPost("/environments/{id}/control-webhook:signing-key", async (string id, ClaimsPrincipal u, EnvironmentService s, CancellationToken ct) =>
        {
            var r = await s.RotateSigningSecretAsync(id, ActorOf(u), ct);
            return r.IsOk
                ? Results.Json(new
                {
                    secret = r.Value, header = ControlSignature.SignatureHeader, deliveryHeader = ControlSignature.DeliveryHeader, timestampHeader = ControlSignature.TimestampHeader,
                    scheme = $"{ControlSignature.Version}=hex(HMAC-SHA256(secret, \"{{timestamp}}.{{deliveryId}}.{{body}}\"))", toleranceSeconds = ControlSignature.ToleranceSeconds,
                    note = "Shown once. Keep it where the deployment tool can read it; creating another replaces this one.",
                }, statusCode: StatusCodes.Status201Created)
                : r.ToHttp();
        }).RequireAuthorization(Policies.Admin);

        // ---- runs: pause, resume and the stage cards -------------------------------------------------------------------------------
        api.MapPost("/runs/{id}:pause", (string id, PauseBody b, ClaimsPrincipal u, HttpRequest req, RunService s, CancellationToken ct) =>
            s.PauseAsync(id, b.Reason, ActorOf(u), req.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);
        api.MapPost("/runs/{id}:resume", (string id, ClaimsPrincipal u, HttpRequest req, RunService s, CancellationToken ct) =>
            s.ResumeAsync(id, ActorOf(u), req.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);

        // GET /runs/{id}/stages -> the run's sections as stage cards (step counts, planned and actual span, status), pause state, and the deployments linked to the run
        api.MapGet("/runs/{id}/stages", async (string id, RunService s, DeploymentService d, CancellationToken ct) =>
            await s.StagesAsync(id, ct) is { } v ? Results.Ok(new { run = v, deployments = await d.ListAsync(new(RunId: id, Take: 200), ct) }) : Results.NotFound(new { message = "Run not found" })).RequireAuthorization(Policies.Read);
    }
}
