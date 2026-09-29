using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Services;

namespace ReleaseMgmt.Api.Realtime;

/// <summary>
/// Live channel (D7). Server-to-client only: TrainChanged(trainId, version), NotificationCreated(notificationId) to one user,
/// ServerTime(iso) every 10 s. Payloads are hints: clients refetch through the normal, authorised API.
/// </summary>
[Authorize(Policy = Policies.Read)]
public sealed class TrainsHub : Hub
{
    public const string Path = "/hub/trains";
    public const string TrainChanged = "TrainChanged";
    public const string NotificationCreated = "NotificationCreated";
    public const string ServerTime = "ServerTime";
}

/// <summary>Routes Clients.User(id) by the Users.Id in the "uid" claim (set at sign-in by UserProvisioner).</summary>
public sealed class UidUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) => connection.User?.FindFirst("uid")?.Value;
}

public sealed class SignalRPublisher(IHubContext<TrainsHub> hub) : IRealtimePublisher
{
    public Task TrainChangedAsync(string trainId, int version, CancellationToken ct = default) =>
        hub.Clients.All.SendAsync(TrainsHub.TrainChanged, trainId, version, ct);

    public Task NotificationCreatedAsync(string userId, string notificationId, CancellationToken ct = default) =>
        hub.Clients.User(userId).SendAsync(TrainsHub.NotificationCreated, notificationId, ct);
}

/// <summary>
/// Pushes the server clock so every tab shows the same time and can tell when the connection has gone quiet.
/// A failed tick is logged, raised through <see cref="IAlertSink"/> and retried on the next tick: never swallowed (rule 8).
/// </summary>
public sealed class ServerTimeBroadcaster(IHubContext<TrainsHub> hub, TimeProvider time, IAlertSink alerts, IConfiguration config, ILogger<ServerTimeBroadcaster> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var seconds = Math.Max(1, config.GetValue("Realtime:ServerTimeSeconds", 10));
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stop))
            {
                try { await hub.Clients.All.SendAsync(TrainsHub.ServerTime, time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), stop); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "ServerTime broadcast failed");
                    await alerts.RaiseAsync("Realtime", "ServerTimeFailed", "hub", "ServerTime broadcast failed: " + ex.Message, stop);
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}
