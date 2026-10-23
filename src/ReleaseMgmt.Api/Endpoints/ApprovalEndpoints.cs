using System.Security.Claims;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>
/// Approvals (REOS-113, B5): policies (read by everyone, managed by RTEs, Release Managers and Governance Officers), approval requests created from them (requested by planners, decided by the eligible
/// person of each seat, whatever their role: the service says who may), and the exceptions list that shows waivers and freeze overrides together with the declined state.
/// The queue of what waits for a person stays at <c>GET /approvals</c> (REOS-94); it gains the seats waiting for them.
/// </summary>
public static class ApprovalEndpoints
{
    public sealed record DeclineBody(string? Note);
    public sealed record OverrideDeclineBody(string TrainId, string RequestedByUserId, string? Note);

    private static Actor ActorOf(ClaimsPrincipal u) => new(u.FindFirstValue("uid") ?? throw new InvalidOperationException("Signed-in user has no uid claim"));
    private static string UserId(ClaimsPrincipal u) => u.FindFirstValue("uid") ?? throw new InvalidOperationException("Signed-in user has no uid claim");

    public static void MapApprovals(this RouteGroupBuilder api)
    {
        // ---- policies --------------------------------------------------------------------------------------------------------------
        api.MapGet("/approval-policies", async (bool? includeArchived, ApprovalPolicyService s, CancellationToken ct) => Results.Ok(await s.ListAsync(includeArchived == true, ct))).RequireAuthorization(Policies.Read);
        // GET /approval-policies/coverage?from=&to= -> { productionReleases, governed, withApproval, coveragePct, ungoverned[] }
        api.MapGet("/approval-policies/coverage", async (string? from, string? to, ApprovalPolicyService s, CancellationToken ct) =>
        {
            static bool TryDay(string? text, out DateOnly? day)
            {
                day = null;
                if (string.IsNullOrWhiteSpace(text)) return true;
                if (!DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", out var d)) return false;
                day = d; return true;
            }
            if (!TryDay(from, out var f) || !TryDay(to, out var t))
                return Results.Json(new { guard = ApprovalGuards.PolicyInvalid, message = "from and to are days (2026-10-01)" }, statusCode: StatusCodes.Status422UnprocessableEntity);
            return Results.Ok(await s.CoverageAsync(f, t, ct));
        }).RequireAuthorization(Policies.Read);
        api.MapGet("/approval-policies/{id}", async (string id, ApprovalPolicyService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } v ? Results.Ok(v) : Results.NotFound(new { message = "policy not found" })).RequireAuthorization(Policies.Read);
        api.MapPost("/approval-policies", (NewPolicy b, ClaimsPrincipal u, ApprovalPolicyService s, CancellationToken ct) => s.CreateAsync(b, ActorOf(u), ct).ToHttpAsync()).RequireAuthorization(Policies.PolicyManage);
        api.MapPatch("/approval-policies/{id}", (string id, PolicyPatch b, ClaimsPrincipal u, HttpRequest r, ApprovalPolicyService s, CancellationToken ct) =>
            s.UpdateAsync(id, b, ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.PolicyManage);
        api.MapPost("/approval-policies/{id}:archive", (string id, ClaimsPrincipal u, HttpRequest r, ApprovalPolicyService s, CancellationToken ct) =>
            s.SetArchivedAsync(id, true, ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.PolicyManage);
        api.MapPost("/approval-policies/{id}:unarchive", (string id, ClaimsPrincipal u, HttpRequest r, ApprovalPolicyService s, CancellationToken ct) =>
            s.SetArchivedAsync(id, false, ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.PolicyManage);

        // ---- approval requests -----------------------------------------------------------------------------------------------------
        api.MapGet("/approval-requests", async (string? trainId, string? environmentId, string? status, bool? mine, int? take, ClaimsPrincipal u, ApprovalService s, CancellationToken ct) =>
            Results.Ok(await s.ListAsync(new(trainId, environmentId, status, mine == true, take ?? 100), UserId(u), ct))).RequireAuthorization(Policies.Read);
        api.MapGet("/approval-requests/{id}", async (string id, ClaimsPrincipal u, ApprovalService s, CancellationToken ct) =>
            await s.GetAsync(id, UserId(u), ct) is { } v ? Results.Ok(v) : Results.NotFound(new { message = "approval not found" })).RequireAuthorization(Policies.Read);
        api.MapGet("/trains/{id}/approval-requests", async (string id, ClaimsPrincipal u, ApprovalService s, CancellationToken ct) =>
            Results.Ok(await s.ListAsync(new(TrainId: id, Take: 100), UserId(u), ct))).RequireAuthorization(Policies.Read).TrainInRoute();
        api.MapPost("/approval-requests", async (NewApproval b, ClaimsPrincipal u, ApprovalService s, CancellationToken ct) =>
        {
            var r = await s.RequestAsync(b, ActorOf(u), ct);
            return r.IsOk ? Results.Json(r.Value, statusCode: StatusCodes.Status201Created) : r.ToHttp();
        }).RequireAuthorization(Policies.Plan);
        // Decided by whoever holds the seat, in any role: the service checks eligibility, separation of duties and the policy checks, and says why not.
        api.MapPost("/approval-requests/{id}:decide", (string id, DecideRequest b, ClaimsPrincipal u, HttpRequest r, ApprovalService s, CancellationToken ct) =>
            s.DecideAsync(id, b, ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.Read);
        api.MapPost("/approval-requests/{id}:resubmit", (string id, ClaimsPrincipal u, HttpRequest r, ApprovalService s, CancellationToken ct) =>
            s.ResubmitAsync(id, ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.Read);
        api.MapPost("/approval-requests/{id}:cancel", (string id, ClaimsPrincipal u, HttpRequest r, ApprovalService s, CancellationToken ct) =>
            s.CancelAsync(id, ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.Read);

        // ---- exceptions ------------------------------------------------------------------------------------------------------------
        api.MapGet("/exceptions", async (string? state, string? kind, string? trainId, int? take, ExceptionService s, CancellationToken ct) =>
            (state is not null && !ExceptionService.States.Contains(state)) || (kind is not null && kind is not (ExceptionService.Waiver or ExceptionService.FreezeOverride))
                ? Results.Json(new { guard = ApprovalGuards.ApprovalInvalid, message = $"state is one of {string.Join(", ", ExceptionService.States)}; kind is Waiver or FreezeOverride" }, statusCode: StatusCodes.Status422UnprocessableEntity)
                : Results.Ok(await s.ListAsync(state, kind, trainId, take ?? 200, ct))).RequireAuthorization(Policies.Read);
        api.MapPost("/waivers/{id}:decline", (string id, DeclineBody b, ClaimsPrincipal u, HttpRequest r, WaiverService s, CancellationToken ct) =>
            s.DeclineAsync(id, b.Note, ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.GovernanceOfficer);
        api.MapPost("/freezes/{id}/override-requests:decline", async (string id, OverrideDeclineBody b, ClaimsPrincipal u, FreezeService s, CancellationToken ct) =>
        {
            var r = await s.DeclineOverrideAsync(id, b.TrainId, b.RequestedByUserId, b.Note, ActorOf(u), ct);
            return r.IsOk ? Results.Ok(new { declined = true }) : r.ToHttp();
        }).RequireAuthorization(Policies.FreezeApprove);
    }
}
