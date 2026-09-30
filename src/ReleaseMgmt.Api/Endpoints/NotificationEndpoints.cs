using System.Security.Claims;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Infrastructure.Reminders;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>
/// The signed-in user's inbox and My work queue (PROJECT_SCOPE 5.5, section 6). Every route works on the caller's own rows only:
/// the user id comes from the "uid" claim, never from the URL. Marking someone else's notification is 403, an unknown id 404.
/// </summary>
public static class NotificationEndpoints
{
    public static void MapNotifications(this RouteGroupBuilder api)
    {
        // GET /me/notifications?unread=true&limit=50&offset=0 -> { items[], total, unread, limit, offset }
        api.MapGet("/me/notifications", async (bool? unread, int? limit, int? offset, ClaimsPrincipal u, NotificationInboxService s, CancellationToken ct) =>
            Results.Ok(await s.ListAsync(Uid(u), unread ?? false, limit ?? 50, offset ?? 0, ct))).RequireAuthorization(Policies.Read);

        // GET /me/notifications/count -> { unread, total }
        api.MapGet("/me/notifications/count", async (ClaimsPrincipal u, NotificationInboxService s, CancellationToken ct) =>
            Results.Ok(await s.CountAsync(Uid(u), ct))).RequireAuthorization(Policies.Read);

        // POST /notifications/{id}:read -> the notification (idempotent)
        api.MapPost("/notifications/{id}:read", async (string id, ClaimsPrincipal u, NotificationInboxService s, CancellationToken ct) =>
        {
            var r = await s.MarkReadAsync(Uid(u), id, ct);
            if (!r.IsOk && r.Failures.Count > 0 && r.Failures[0].Guard == NotificationInboxService.NotYours)
                return Results.Json(new { guard = r.Failures[0].Guard, message = r.Failures[0].Message }, statusCode: StatusCodes.Status403Forbidden);
            return r.IsOk ? Results.Ok(new { id = r.Value!.Id, readAt = r.Value.ReadAt, version = r.Value.Version }) : r.ToHttp();
        }).RequireAuthorization(Policies.Read);

        // POST /me/notifications:read-all -> { marked }
        api.MapPost("/me/notifications:read-all", async (ClaimsPrincipal u, NotificationInboxService s, CancellationToken ct) =>
            Results.Ok(new { marked = await s.MarkAllReadAsync(Uid(u), ct) })).RequireAuthorization(Policies.Read);

        // GET /me/work -> { counts, tasks[], gates[], steps[], conditions[], pirActions[] }
        api.MapGet("/me/work", async (ClaimsPrincipal u, MyWorkService s, CancellationToken ct) => Results.Ok(await s.GetAsync(Uid(u), ct))).RequireAuthorization(Policies.Read);
    }

    private static string Uid(ClaimsPrincipal u) => u.FindFirstValue("uid") ?? throw new InvalidOperationException("Signed-in user has no uid claim");
}
