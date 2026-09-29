using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>State changes are action endpoints (PROJECT_SCOPE §6); no endpoint PATCHes a Status column (CLAUDE.md rule 2).</summary>
public static class LifecycleEndpoints
{
    public sealed record AdvanceRequest(string To);
    public sealed record CompleteRequest(string CloseCode, string? Notes);
    public sealed record PatchTrainRequest(DateOnly? TargetReleaseDate);
    public sealed record WaiverRequestBody(string Reason);
    public sealed record AddTaskRequest(string Description, string? OwnerUserId, string? OwnerTeamId);

    private static Actor ActorOf(ClaimsPrincipal u) => new(u.FindFirstValue("uid") ?? throw new InvalidOperationException("Signed-in user has no uid claim"));

    public static void MapLifecycle(this RouteGroupBuilder api)
    {
        api.MapPost("/trains/{id}:advance", (string id, AdvanceRequest body, ClaimsPrincipal u, HttpRequest req, TrainLifecycleService s, CancellationToken ct) =>
            s.AdvanceAsync(id, body.To, ActorOf(u), req.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);
        api.MapPost("/trains/{id}:abort", (string id, ClaimsPrincipal u, HttpRequest req, TrainLifecycleService s, CancellationToken ct) =>
            s.AbortAsync(id, ActorOf(u), req.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);
        api.MapPost("/trains/{id}:complete", (string id, CompleteRequest body, ClaimsPrincipal u, HttpRequest req, TrainLifecycleService s, CancellationToken ct) =>
            s.CompleteAsync(id, body.CloseCode, body.Notes, ActorOf(u), req.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);
        api.MapPost("/trains/{id}:rehearsed-rollback", (string id, ClaimsPrincipal u, HttpRequest req, TrainLifecycleService s, CancellationToken ct) =>
            s.RecordRollbackRehearsedAsync(id, ActorOf(u), req.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);
        api.MapPost("/trains/{id}:capture-baseline", (string id, ClaimsPrincipal u, BaselineService s, CancellationToken ct) =>
            s.CaptureAsync(id, ActorOf(u), ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);
        api.MapPatch("/trains/{id}", (string id, PatchTrainRequest body, ClaimsPrincipal u, HttpRequest req, ScheduleService s, CancellationToken ct) =>
            body.TargetReleaseDate is { } d
                ? s.ChangeTargetDateAsync(id, d, ActorOf(u), req.IfMatch(), ct).ToHttpAsync()
                : Task.FromResult(Results.BadRequest(new { message = "Nothing to change: supply targetReleaseDate" }))).RequireAuthorization(Policies.Plan);

        // Gates: Standard gates by the gate owner or RTE/RM; Compliance certify/waive by a Governance Officer (PROJECT_SCOPE §1).
        api.MapPost("/gates/{id}:start", (string id, ClaimsPrincipal u, HttpRequest req, IDbContextFactory<ReleaseDbContext> dbf, GateService s, CancellationToken ct) =>
            Gate(id, u, dbf, ct, forbidComplianceNonGo: false, () => s.StartAsync(id, ActorOf(u), req.IfMatch(), ct)));
        api.MapPost("/gates/{id}:certify", (string id, ClaimsPrincipal u, HttpRequest req, IDbContextFactory<ReleaseDbContext> dbf, GateService s, CancellationToken ct) =>
            Gate(id, u, dbf, ct, forbidComplianceNonGo: true, () => s.CertifyAsync(id, ActorOf(u), req.IfMatch(), ct)));
        api.MapPost("/gates/{id}:fail", (string id, ClaimsPrincipal u, HttpRequest req, IDbContextFactory<ReleaseDbContext> dbf, GateService s, CancellationToken ct) =>
            Gate(id, u, dbf, ct, forbidComplianceNonGo: false, () => s.FailAsync(id, ActorOf(u), req.IfMatch(), ct)));
        api.MapPost("/gates/{id}:reopen", (string id, ClaimsPrincipal u, HttpRequest req, IDbContextFactory<ReleaseDbContext> dbf, GateService s, CancellationToken ct) =>
            Gate(id, u, dbf, ct, forbidComplianceNonGo: false, () => s.ReopenAsync(id, ActorOf(u), req.IfMatch(), ct)));
        api.MapPost("/gates/{id}:waive", (string id, ClaimsPrincipal u, HttpRequest req, GateService s, CancellationToken ct) =>
            s.WaiveAsync(id, ActorOf(u), req.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.GovernanceOfficer);

        api.MapPost("/gates/{id}/waivers", (string id, WaiverRequestBody body, ClaimsPrincipal u, WaiverService s, CancellationToken ct) =>
            s.RequestAsync(id, body.Reason, ActorOf(u), ct).ToHttpAsync()).RequireAuthorization(Policies.GovernanceOfficer);
        api.MapPost("/waivers/{id}:approve", (string id, ClaimsPrincipal u, HttpRequest req, WaiverService s, CancellationToken ct) =>
            s.ApproveAsync(id, ActorOf(u), req.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.GovernanceOfficer);

        api.MapPost("/gates/{id}/tasks", (string id, AddTaskRequest body, ClaimsPrincipal u, TaskService s, CancellationToken ct) =>
            s.AddAsync(id, body.Description, body.OwnerUserId, body.OwnerTeamId, ActorOf(u), ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);
        api.MapPost("/tasks/{id}:complete", (string id, ClaimsPrincipal u, HttpRequest req, TaskService s, CancellationToken ct) =>
            s.CompleteAsync(id, ActorOf(u), req.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.Read);
        api.MapPost("/tasks/{id}:reopen", (string id, ClaimsPrincipal u, HttpRequest req, TaskService s, CancellationToken ct) =>
            s.ReopenAsync(id, ActorOf(u), req.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.Read);
    }

    private static async Task<IResult> ToHttpAsync<T>(this Task<ServiceResult<T>> t) => (await t).ToHttp();

    /// <summary>Gate authorization: RTE/RM always; otherwise the gate owner; Compliance certification only by a Governance Officer.</summary>
    private static async Task<IResult> Gate(string id, ClaimsPrincipal u, IDbContextFactory<ReleaseDbContext> dbf, CancellationToken ct, bool forbidComplianceNonGo, Func<Task<ServiceResult<StageGates>>> act)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var g = await db.Set<StageGates>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (g is null) return Results.NotFound(new { message = "gate not found" });
        var uid = u.FindFirstValue("uid");
        var planner = u.IsInRole(Roles.RTE) || u.IsInRole(Roles.ReleaseManager);
        var owner = g.OwnerUserId == uid || (g.OwnerTeamId is not null && await db.Set<TeamMembers>().AnyAsync(m => m.TeamId == g.OwnerTeamId && m.UserId == uid, ct));
        var compliance = forbidComplianceNonGo && g.GateClass == "Compliance";
        var allowed = compliance ? u.IsInRole(Roles.GovernanceOfficer) : planner || owner;
        return allowed ? (await act()).ToHttp() : Results.Json(new { message = compliance ? "Compliance gates are certified by Governance Officers" : "Only the gate owner, an RTE or a Release Manager can do this" }, statusCode: StatusCodes.Status403Forbidden);
    }
}
