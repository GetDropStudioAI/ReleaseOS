namespace ReleaseMgmt.Domain.Services;

/// <summary>One thing standing between a train and its target status. <see cref="Hop"/> is the status move it belongs to ("Gated", "Executing", ...).</summary>
public sealed record ReadinessBlocker(string Hop, GuardFailure Failure);

/// <summary>Answer to "what does this train still need?". Built from the same guards <c>:advance</c> runs, so the two cannot disagree.</summary>
public sealed record Readiness(string TrainId, int Version, string Status, string Target, bool Ready, IReadOnlyList<ReadinessBlocker> Blockers);

public interface IReadinessService
{
    Task<ServiceResult<Readiness>> GetAsync(string trainId, string target = "Executing", CancellationToken ct = default);
}

public sealed record NotificationRequest(string UserId, string Kind, string EntityType, string EntityId, string Message, int EscalationLevel = 0);

/// <summary>Writes an in-app notification and pushes NotificationCreated. M4 adds reminders, escalation and team webhooks behind this interface.</summary>
public interface INotifier
{
    Task<string> NotifyAsync(NotificationRequest request, CancellationToken ct = default);
}

/// <summary>Live-update seam (D7). Services call it after a commit; the Api host implements it with SignalR. Clients refetch, they do not trust payloads.</summary>
public interface IRealtimePublisher
{
    Task TrainChangedAsync(string trainId, int version, CancellationToken ct = default);
    Task NotificationCreatedAsync(string userId, string notificationId, CancellationToken ct = default);
    /// <summary>A step event changed a run's forecast; clients on that train refetch GET /runs/{id}/forecast.</summary>
    Task ForecastChangedAsync(string trainId, string runId, CancellationToken ct = default);
}

/// <summary>Used where no live channel exists (tests, tools).</summary>
public sealed class NullRealtimePublisher : IRealtimePublisher
{
    public Task TrainChangedAsync(string trainId, int version, CancellationToken ct = default) => Task.CompletedTask;
    public Task NotificationCreatedAsync(string userId, string notificationId, CancellationToken ct = default) => Task.CompletedTask;
    public Task ForecastChangedAsync(string trainId, string runId, CancellationToken ct = default) => Task.CompletedTask;
}
