using System.Security.Claims;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>
/// REOS-81: add, edit and remove a train's products and gates (PROJECT_SCOPE section 6: POST /trains/{id}/products, PATCH/DELETE /products/{id},
/// POST /trains/{id}/gates, PATCH /gates/{id}, DELETE /gates/{id}). "Create/edit trains, products, gates" is RTE and Release Manager (section 1).
/// A gate's status only moves through :start/:certify/:fail/:reopen/:waive (CLAUDE.md rule 2): a PATCH that names a status is refused, never ignored.
/// </summary>
public static class PlanStructureEndpoints
{
    public sealed record ProductBody(string? Name, string? VersionTag, string? ProjectCode);
    public sealed record NewGateBody(string? GateName, string? GateClass, int? OffsetDays, DateOnly? DueOn, string? RequiredBeforeStatus,
                                     string? OwnerUserId, string? OwnerTeamId, int? SequenceOrder);
    public sealed record PatchGateBody(string? GateName, string? GateClass, int? OffsetDays, DateOnly? DueOn, string? RequiredBeforeStatus,
                                       string? OwnerUserId, string? OwnerTeamId, string? Status, int? SequenceOrder);

    private static Actor ActorOf(ClaimsPrincipal u) => new(u.FindFirstValue("uid") ?? throw new InvalidOperationException("Signed-in user has no uid claim"));

    private static IResult Refuse(string guard, string message) =>
        Results2.ToHttp(ServiceResult<object>.Fail(new GuardFailure(guard, message)));

    public static void MapPlanStructure(this RouteGroupBuilder api)
    {
        api.MapPost("/trains/{id}/products", (string id, ProductBody b, ClaimsPrincipal u, PlanStructureService s, CancellationToken ct) =>
            s.AddProductAsync(id, new NewProduct(b.Name, b.VersionTag, b.ProjectCode), ActorOf(u), ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);

        api.MapPatch("/products/{id}", (string id, ProductBody b, ClaimsPrincipal u, HttpRequest req, PlanStructureService s, CancellationToken ct) =>
            s.UpdateProductAsync(id, new ProductPatch(b.Name, b.VersionTag, b.ProjectCode), ActorOf(u), req.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);

        api.MapDelete("/products/{id}", (string id, ClaimsPrincipal u, HttpRequest req, PlanStructureService s, CancellationToken ct) =>
            s.RemoveProductAsync(id, ActorOf(u), req.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);

        api.MapPost("/trains/{id}/gates", (string id, NewGateBody b, ClaimsPrincipal u, PlanStructureService s, CancellationToken ct) =>
            s.AddGateAsync(id, new NewGate(b.GateName, b.GateClass, b.OffsetDays, b.DueOn, b.RequiredBeforeStatus, b.OwnerUserId, b.OwnerTeamId, b.SequenceOrder), ActorOf(u), ct)
             .ToHttpAsync()).RequireAuthorization(Policies.Plan);

        api.MapPatch("/gates/{id}", async (string id, PatchGateBody b, ClaimsPrincipal u, HttpRequest req, PlanStructureService s, CancellationToken ct) =>
        {
            if (b.Status is not null)
                return Refuse(Guards.StatusNotEditable, "A gate's status changes only through its actions (start, certify, fail, reopen, waive), never by editing it");
            if (b.SequenceOrder is not null)
                return Refuse(Guards.InvalidGate, "An existing gate keeps its position; choose the position when adding a gate");
            return await s.UpdateGateAsync(id, new GatePatch(b.GateName, b.GateClass, b.OffsetDays, b.DueOn, b.RequiredBeforeStatus, b.OwnerUserId, b.OwnerTeamId),
                ActorOf(u), req.IfMatch(), ct).ToHttpAsync();
        }).RequireAuthorization(Policies.Plan);

        api.MapDelete("/gates/{id}", (string id, ClaimsPrincipal u, HttpRequest req, PlanStructureService s, CancellationToken ct) =>
            s.RemoveGateAsync(id, ActorOf(u), req.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);
    }
}
