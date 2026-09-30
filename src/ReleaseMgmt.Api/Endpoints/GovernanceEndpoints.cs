using System.Security.Claims;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>Change record, affected CIs and the Go/No-Go record (PROJECT_SCOPE §6, D29).</summary>
public static class GovernanceEndpoints
{
    public sealed record ChangeRecordBody(string? Justification, string? ImplementationPlan, string? RiskImpactAnalysis, string? BackoutPlan, string? TestPlan, string? CommunicationPlan, DateOnly? CabDate);
    public sealed record CiBody(string CiName, string? CiExternalId);
    public sealed record ConditionBody(string Text, string OwnerUserId, DateTime ExpiresAt);
    public sealed record GoNoGoBody(string Decision, string? Notes, DateOnly? NewTargetReleaseDate, ConditionBody[]? Conditions);

    private static Actor ActorOf(ClaimsPrincipal u) => new(u.FindFirstValue("uid") ?? throw new InvalidOperationException("Signed-in user has no uid claim"));

    public static void MapGovernance(this RouteGroupBuilder api)
    {
        api.MapGet("/trains/{id}/change-record", async (string id, ChangeRecordService s, CancellationToken ct) =>
            Results.Ok(await s.GetAsync(id, ct) ?? new ReleaseMgmt.Domain.Entities.ChangeRecords { ReleaseTrainId = id, Version = 0 })).RequireAuthorization(Policies.Read);
        api.MapPut("/trains/{id}/change-record", (string id, ChangeRecordBody b, ClaimsPrincipal u, HttpRequest r, ChangeRecordService s, CancellationToken ct) =>
            s.SaveAsync(id, new(b.Justification, b.ImplementationPlan, b.RiskImpactAnalysis, b.BackoutPlan, b.TestPlan, b.CommunicationPlan, b.CabDate), ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);

        api.MapGet("/trains/{id}/cis", async (string id, ChangeRecordService s, CancellationToken ct) => Results.Ok(await s.ListCisAsync(id, ct))).RequireAuthorization(Policies.Read);
        api.MapPost("/trains/{id}/cis", (string id, CiBody b, ClaimsPrincipal u, ChangeRecordService s, CancellationToken ct) =>
            s.AddCiAsync(id, b.CiName, b.CiExternalId, ActorOf(u), ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);
        api.MapDelete("/trains/{id}/cis/{ciId}", (string id, string ciId, ClaimsPrincipal u, ChangeRecordService s, CancellationToken ct) =>
            s.RemoveCiAsync(id, ciId, ActorOf(u), ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);

        api.MapGet("/trains/{id}/gonogo", async (string id, GoNoGoService s, CancellationToken ct) => Results.Ok(await s.ListAsync(id, ct))).RequireAuthorization(Policies.Read);
        api.MapPost("/trains/{id}/gonogo", (string id, GoNoGoBody b, ClaimsPrincipal u, HttpRequest r, GoNoGoService s, CancellationToken ct) =>
            s.RecordAsync(id, b.Decision, b.Notes, b.NewTargetReleaseDate, b.Conditions?.Select(c => new NewCondition(c.Text, c.OwnerUserId, c.ExpiresAt)).ToList(), ActorOf(u), r.IfMatch(), ct).ToHttpAsync())
            .RequireAuthorization(Policies.ReleaseManager);
        api.MapPost("/gonogo/{id}/conditions", (string id, ConditionBody b, ClaimsPrincipal u, GoNoGoService s, CancellationToken ct) =>
            s.AddConditionAsync(id, new(b.Text, b.OwnerUserId, b.ExpiresAt), ActorOf(u), ct).ToHttpAsync()).RequireAuthorization(Policies.ReleaseManager);
        api.MapPost("/conditions/{id}:close", (string id, ClaimsPrincipal u, HttpRequest r, GoNoGoService s, CancellationToken ct) =>
            s.CloseConditionAsync(id, ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.Read);
    }
}
