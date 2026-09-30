using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>
/// Close-out (REOS-36, PROJECT_SCOPE section 6): rollback attestation read, PIR and its actions, hypercare known issues and hypercare exit.
/// No endpoint PATCHes a status column: PIR and known-issue statuses move through action endpoints (CLAUDE.md rule 2).
/// </summary>
public static class CloseoutEndpoints
{
    public sealed record SummaryBody(string? Summary);
    public sealed record PirActionBody(string Text, string OwnerUserId, DateOnly DueOn);
    public sealed record PirActionPatchBody(string? Text, string? OwnerUserId, DateOnly? DueOn);
    public sealed record KnownIssueBody(string Title, string Severity, string? Workaround, string? ExternalKey);
    public sealed record KnownIssuePatchBody(string? Title, string? Severity, string? Workaround, string? ExternalKey);

    private static Actor ActorOf(ClaimsPrincipal u) => new(u.FindFirstValue("uid") ?? throw new InvalidOperationException("Signed-in user has no uid claim"));

    /// <summary>RTE, Release Manager and Governance Officer run the close-out (Q-036b); Viewers read.</summary>
    private static void Closeout(AuthorizationPolicyBuilder p) => p.RequireRole(Roles.RTE, Roles.ReleaseManager, Roles.GovernanceOfficer);

    public static void MapCloseout(this RouteGroupBuilder api)
    {
        api.MapGet("/trains/{id}/rollback-attestation", async (string id, CloseoutService s, CancellationToken ct) =>
            await s.GetAttestationAsync(id, ct) is { } v ? Results.Ok(v) : Results.NotFound(new { message = "train not found" })).RequireAuthorization(Policies.Read);

        // ---- PIR
        api.MapGet("/trains/{id}/pir", async (string id, CloseoutService s, CancellationToken ct) =>
            await s.GetPirAsync(id, ct) is { } v ? Results.Ok(v) : Results.NotFound(new { message = "train not found" })).RequireAuthorization(Policies.Read);
        api.MapPost("/trains/{id}/pir", (string id, ClaimsPrincipal u, CloseoutService s, CancellationToken ct) =>
            s.CreateManualPirAsync(id, ActorOf(u), ct).ToHttpAsync()).RequireAuthorization(Closeout);
        api.MapPatch("/trains/{id}/pir", (string id, SummaryBody b, ClaimsPrincipal u, HttpRequest r, CloseoutService s, CancellationToken ct) =>
            s.UpdateSummaryAsync(id, b.Summary, ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Closeout);
        api.MapPost("/trains/{id}/pir:schedule", (string id, ClaimsPrincipal u, HttpRequest r, CloseoutService s, CancellationToken ct) =>
            s.ScheduleAsync(id, ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Closeout);
        api.MapPost("/trains/{id}/pir:hold", (string id, SummaryBody? b, ClaimsPrincipal u, HttpRequest r, CloseoutService s, CancellationToken ct) =>
            s.HoldAsync(id, b?.Summary, ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Closeout);
        api.MapPost("/trains/{id}/pir:close", (string id, ClaimsPrincipal u, HttpRequest r, CloseoutService s, CancellationToken ct) =>
            s.CloseAsync(id, ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Closeout);

        // ---- PIR actions (owner may complete/reopen their own; the service checks)
        api.MapPost("/trains/{id}/pir/actions", (string id, PirActionBody b, ClaimsPrincipal u, CloseoutService s, CancellationToken ct) =>
            s.AddActionAsync(id, new(b.Text, b.OwnerUserId, b.DueOn), ActorOf(u), ct).ToHttpAsync()).RequireAuthorization(Closeout);
        api.MapPatch("/pir-actions/{id}", (string id, PirActionPatchBody b, ClaimsPrincipal u, HttpRequest r, CloseoutService s, CancellationToken ct) =>
            s.UpdateActionAsync(id, new(b.Text, b.OwnerUserId, b.DueOn), ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Closeout);
        api.MapPost("/pir-actions/{id}:complete", (string id, ClaimsPrincipal u, HttpRequest r, CloseoutService s, CancellationToken ct) =>
            s.CompleteActionAsync(id, ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.Read);
        api.MapPost("/pir-actions/{id}:reopen", (string id, ClaimsPrincipal u, HttpRequest r, CloseoutService s, CancellationToken ct) =>
            s.ReopenActionAsync(id, ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.Read);

        // ---- hypercare and known issues
        api.MapGet("/trains/{id}/known-issues", async (string id, CloseoutService s, CancellationToken ct) =>
            await s.GetKnownIssuesAsync(id, ct) is { } v ? Results.Ok(v) : Results.NotFound(new { message = "train not found" })).RequireAuthorization(Policies.Read);
        api.MapPost("/trains/{id}/known-issues", (string id, KnownIssueBody b, ClaimsPrincipal u, CloseoutService s, CancellationToken ct) =>
            s.AddIssueAsync(id, new(b.Title, b.Severity, b.Workaround, b.ExternalKey), ActorOf(u), ct).ToHttpAsync()).RequireAuthorization(Closeout);
        api.MapPatch("/trains/{id}/known-issues/{issueId}", (string id, string issueId, KnownIssuePatchBody b, ClaimsPrincipal u, HttpRequest r, CloseoutService s, CancellationToken ct) =>
            s.UpdateIssueAsync(id, issueId, new(b.Title, b.Severity, b.Workaround, b.ExternalKey), ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Closeout);
        api.MapPost("/trains/{id}/known-issues/{issueId}:accept", (string id, string issueId, ClaimsPrincipal u, HttpRequest r, CloseoutService s, CancellationToken ct) =>
            s.AcceptIssueAsync(id, issueId, ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Closeout);
        api.MapPost("/trains/{id}/known-issues/{issueId}:resolve", (string id, string issueId, ClaimsPrincipal u, HttpRequest r, CloseoutService s, CancellationToken ct) =>
            s.ResolveIssueAsync(id, issueId, ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Closeout);
        api.MapPost("/trains/{id}/known-issues/{issueId}:reopen", (string id, string issueId, ClaimsPrincipal u, HttpRequest r, CloseoutService s, CancellationToken ct) =>
            s.ReopenIssueAsync(id, issueId, ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Closeout);
        api.MapPost("/trains/{id}:exit-hypercare", (string id, ClaimsPrincipal u, HttpRequest r, CloseoutService s, CancellationToken ct) =>
            s.ExitHypercareAsync(id, ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);
    }
}
