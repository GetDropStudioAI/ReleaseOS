using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>
/// REOS-180 (D56, D9): the conflicts of a change, derived on every read (nothing is stored as truth), under <c>/cab</c> and behind <c>RequireModule("cab")</c> like the rest of the module: module off,
/// every route answers 404. Read policy. A conflict warns and never blocks; the approval-needs-a-reason rule is in <c>POST /cab/changes/{id}/decisions</c> (422 <c>ChangeConflictNeedsReason</c>).
/// </summary>
public static class CabConflictEndpoints
{
    public static void MapCabConflicts(this RouteGroupBuilder api)
    {
        var cab = api.MapGroup("/cab").RequireModule(ModuleKeys.Cab);

        // GET /cab/conflict-rules -> { rules: [{ type, rule, label, description }], bufferMinutes, minReasonLength, approvalRule }
        cab.MapGet("/conflict-rules", (ChangeConflictService s) => Results.Ok(s.Rules())).RequireAuthorization(Policies.Read);

        // GET /cab/changes/{id}/conflicts -> { changeId, key, status, plannedStartAt, plannedEndAt, checked, reason, bufferMinutes, conflicts: [{ type, rule, ruleLabel, summary, refId, refLabel, startsAt, endsAt }] }
        cab.MapGet("/changes/{id}/conflicts", async (string id, ChangeConflictService s, CancellationToken ct) =>
            await s.ForChangeAsync(id, ct) is { } r ? Results.Ok(r) : Results.NotFound(new { message = "change request not found" })).RequireAuthorization(Policies.Read);

        // GET /cab/meetings/{id}/conflicts -> { meetingId, items: [{ changeId, key, title, status, checked, reason, conflicts }] }, one per agenda item, in agenda order
        cab.MapGet("/meetings/{id}/conflicts", async (string id, ChangeConflictService s, CancellationToken ct) =>
            await s.ForMeetingAsync(id, ct) is { } r ? Results.Ok(r) : Results.NotFound(new { message = "meeting not found" })).RequireAuthorization(Policies.Read);

        // GET /cab/calendar?from=yyyy-MM-dd&to=yyyy-MM-dd -> { from, to, changes: [{ id, key, title, changeType, status, riskLevel, startsAt, endsAt, checked, conflicts }], blackouts: [{ day, name }] }
        cab.MapGet("/calendar", async (string? from, string? to, ChangeConflictService s, CancellationToken ct) =>
            CalendarService.Validate(from, to, out var f, out var t) is { } why
                ? Results.Json(new { guard = CalendarGuards.InvalidFilter, message = why }, statusCode: StatusCodes.Status400BadRequest)
                : Results.Ok(await s.CalendarAsync(f, t, ct))).RequireAuthorization(Policies.Read);
    }
}
