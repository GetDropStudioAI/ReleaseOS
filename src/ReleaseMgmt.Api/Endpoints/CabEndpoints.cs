using System.Security.Claims;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>
/// REOS-175 and REOS-177 (D56, D63): the CAB module's API, all under <c>/cab</c> and behind <c>RequireModule("cab")</c>: while the module is off in the caller's workspace every route answers
/// 404 (after authorisation, so a caller who may not use it still gets 401 or 403). Status changes are actions (<c>:submit</c>, <c>:approve</c> does not exist: a decision is recorded with
/// <c>POST /cab/changes/{id}/decisions</c>), never a PATCH of a status. Writes carry If-Match; 409 returns the current row. Refusals are 422 <c>{guard, message}</c>.
/// </summary>
public static class CabEndpoints
{
    public sealed record DecisionBody(string? Decision, string? Rationale, string? MeetingId, bool Retroactive = false);
    public sealed record MeetingBody(string? Title, DateTime StartsAt, DateTime EndsAt, string? ChairUserId, string? Location);
    public sealed record MeetingPatchBody(string? Title, DateTime? StartsAt, DateTime? EndsAt, string? ChairUserId, string? Location, string? Minutes, bool ClearLocation = false, bool ClearMinutes = false);
    public sealed record AgendaBody(string? ChangeRequestId, string? Notes);
    public sealed record AgendaPatchBody(string? Notes, int? SequenceOrder);
    public sealed record InviteBody(string? UserId, string? Role);
    public sealed record AttendanceBody(string? Attendance);

    private static Actor ActorOf(ClaimsPrincipal u) => new(u.FindFirstValue("uid") ?? throw new InvalidOperationException("Signed-in user has no uid claim"));

    public static void MapCab(this RouteGroupBuilder api)
    {
        var cab = api.MapGroup("/cab").RequireModule(ModuleKeys.Cab);

        // ---- change requests ----
        // GET /cab/changes?status=&type=&releaseId= -> [ChangeView]
        cab.MapGet("/changes", async (string? status, string? type, string? releaseId, ChangeRequestService s, CancellationToken ct) => Results.Ok(await s.ListAsync(status, type, releaseId, ct))).RequireAuthorization(Policies.Read);
        cab.MapGet("/changes/{id}", async (string id, ChangeRequestService s, CancellationToken ct) => await s.GetAsync(id, ct) is { } c ? Results.Ok(c) : Results.NotFound(new { message = "change request not found" })).RequireAuthorization(Policies.Read);
        cab.MapPost("/changes", (NewChange b, ClaimsPrincipal u, ChangeRequestService s, CancellationToken ct) => s.CreateAsync(b, ActorOf(u), ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);
        cab.MapPatch("/changes/{id}", (string id, ChangePatch b, ClaimsPrincipal u, HttpRequest r, ChangeRequestService s, CancellationToken ct) => s.UpdateAsync(id, b, ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);
        cab.MapPost("/changes/{id}:submit", (string id, ClaimsPrincipal u, HttpRequest r, ChangeRequestService s, CancellationToken ct) => s.SubmitAsync(id, ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);
        cab.MapPost("/changes/{id}:cancel", (string id, ClaimsPrincipal u, HttpRequest r, ChangeRequestService s, CancellationToken ct) => s.CancelAsync(id, ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);
        cab.MapPost("/changes/{id}:implement", (string id, ClaimsPrincipal u, HttpRequest r, ChangeRequestService s, CancellationToken ct) => s.ImplementAsync(id, ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);
        cab.MapPost("/changes/{id}:close", (string id, ClaimsPrincipal u, HttpRequest r, ChangeRequestService s, CancellationToken ct) => s.CloseAsync(id, ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);
        // POST /cab/changes/{id}/decisions { decision: Approved|Rejected|Deferred, rationale, meetingId?, retroactive? } -> ChangeView. 422 ChangeSelfApproval, ChangeChairSoleApprover (REOS-177).
        cab.MapPost("/changes/{id}/decisions", (string id, DecisionBody b, ClaimsPrincipal u, HttpRequest r, ChangeRequestService s, CancellationToken ct) =>
            s.DecideAsync(id, new(b.Decision, b.Rationale, b.MeetingId, b.Retroactive), ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.CabDecide);

        // ---- meetings ----
        cab.MapGet("/meetings", async (string? status, CabService s, CancellationToken ct) => Results.Ok(await s.ListAsync(status, ct))).RequireAuthorization(Policies.Read);
        cab.MapGet("/meetings/{id}", async (string id, CabService s, CancellationToken ct) => await s.GetAsync(id, ct) is { } m ? Results.Ok(m) : Results.NotFound(new { message = "meeting not found" })).RequireAuthorization(Policies.Read);
        cab.MapPost("/meetings", (MeetingBody b, ClaimsPrincipal u, CabService s, CancellationToken ct) => s.CreateAsync(new(b.Title, b.StartsAt, b.EndsAt, b.ChairUserId, b.Location), ActorOf(u), ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);
        cab.MapPatch("/meetings/{id}", (string id, MeetingPatchBody b, ClaimsPrincipal u, HttpRequest r, CabService s, CancellationToken ct) =>
            s.UpdateAsync(id, new(b.Title, b.StartsAt, b.EndsAt, b.ChairUserId, b.Location, b.Minutes, b.ClearLocation, b.ClearMinutes), ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);
        cab.MapPost("/meetings/{id}:start", (string id, ClaimsPrincipal u, HttpRequest r, CabService s, CancellationToken ct) => s.StartAsync(id, ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);
        cab.MapPost("/meetings/{id}:complete", (string id, ClaimsPrincipal u, HttpRequest r, CabService s, CancellationToken ct) => s.CompleteAsync(id, ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);
        cab.MapPost("/meetings/{id}:cancel", (string id, ClaimsPrincipal u, HttpRequest r, CabService s, CancellationToken ct) => s.CancelAsync(id, ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);

        // agenda: add and remove carry the meeting's version; editing an item carries the item's
        cab.MapPost("/meetings/{id}/agenda", (string id, AgendaBody b, ClaimsPrincipal u, HttpRequest r, CabService s, CancellationToken ct) =>
            s.AddAgendaItemAsync(id, b.ChangeRequestId ?? "", b.Notes, ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);
        cab.MapPatch("/meetings/{id}/agenda/{itemId}", (string id, string itemId, AgendaPatchBody b, ClaimsPrincipal u, HttpRequest r, CabService s, CancellationToken ct) =>
            s.UpdateAgendaItemAsync(id, itemId, b.Notes, b.SequenceOrder, ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);
        cab.MapDelete("/meetings/{id}/agenda/{itemId}", (string id, string itemId, ClaimsPrincipal u, HttpRequest r, CabService s, CancellationToken ct) =>
            s.RemoveAgendaItemAsync(id, itemId, ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);

        // attendees: invite and remove carry the meeting's version; attendance carries the attendee row's
        cab.MapPost("/meetings/{id}/attendees", (string id, InviteBody b, ClaimsPrincipal u, HttpRequest r, CabService s, CancellationToken ct) =>
            s.InviteAsync(id, b.UserId ?? "", b.Role, ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);
        cab.MapPatch("/meetings/{id}/attendees/{userId}", (string id, string userId, AttendanceBody b, ClaimsPrincipal u, HttpRequest r, CabService s, CancellationToken ct) =>
            s.SetAttendanceAsync(id, userId, b.Attendance ?? "", ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);
        cab.MapDelete("/meetings/{id}/attendees/{userId}", (string id, string userId, ClaimsPrincipal u, HttpRequest r, CabService s, CancellationToken ct) =>
            s.RemoveAttendeeAsync(id, userId, ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);
    }
}
