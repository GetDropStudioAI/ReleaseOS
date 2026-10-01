using System.Security.Claims;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>
/// New trains (REOS-80, PROJECT_SCOPE sections 2 and 6: <c>POST /trains</c>, <c>POST /trains/{id}:clone</c>). "Create/edit trains" is RTE and Release Manager
/// (section 1; the Plan policy, D32). The body sets no status: a new train is Planning and its gates Pending.
/// </summary>
public static class TrainCreationEndpoints
{
    private static Actor ActorOf(ClaimsPrincipal u) => new(u.FindFirstValue("uid") ?? throw new InvalidOperationException("Signed-in user has no uid claim"));

    public static void MapTrainCreation(this RouteGroupBuilder api)
    {
        api.MapPost("/trains", (NewTrainInput? b, ClaimsPrincipal u, TrainCreationService s, CancellationToken ct) =>
            s.CreateAsync(b ?? new NewTrainInput(null, null, null), ActorOf(u), ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);
        api.MapPost("/trains/{id}:clone", (string id, CloneTrainInput? b, ClaimsPrincipal u, TrainCreationService s, CancellationToken ct) =>
            s.CloneAsync(id, b ?? new CloneTrainInput(null, null), ActorOf(u), ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);
    }
}
