using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

/// <summary>
/// In-app notifications (D7): writes the Notifications row and its audit row in one transaction, then pushes NotificationCreated
/// so the recipient's open tab refetches. Reminders, escalation and team webhooks (M4) sit behind <see cref="INotifier"/>.
/// </summary>
public sealed class Notifier(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IRealtimePublisher? realtime = null)
    : ServiceBase(dbf, time, realtime), INotifier
{
    private readonly IRealtimePublisher? _realtime = realtime;

    public async Task<string> NotifyAsync(NotificationRequest request, CancellationToken ct = default)
    {
        var r = await RunAsync<string>(async db =>
        {
            if (string.IsNullOrWhiteSpace(request.Message)) return ServiceResult<string>.Fail(new GuardFailure(Guards.DbRule, "A notification needs a message"));
            if (!await db.Set<Users>().AnyAsync(u => u.Id == request.UserId, ct)) return ServiceResult<string>.NotFound("User");
            var n = new Notifications
            {
                UserId = request.UserId, Kind = request.Kind, EntityType = request.EntityType, EntityId = request.EntityId,
                EscalationLevel = request.EscalationLevel, Message = request.Message.Trim(), CreatedAt = Now,
            };
            db.Set<Notifications>().Add(n);
            Audit(db, null, null, "Notification", n.Id, "Created", after: new { n.UserId, n.Kind, n.EntityType, n.EntityId, n.EscalationLevel });
            await db.SaveChangesAsync(ct);
            return ServiceResult<string>.Ok(n.Id);
        }, ct);

        if (!r.IsOk) throw new InvalidOperationException(r.Failures.Count > 0 ? r.Failures[0].Message : $"{r.Missing} not found");
        // After the commit: the client refetches, so the row must already be visible.
        if (_realtime is not null) await _realtime.NotificationCreatedAsync(request.UserId, r.Value!, ct);
        return r.Value!;
    }
}
