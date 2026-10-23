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
public sealed class TrainsHub(TimeProvider time, HubConnections connections) : Hub
{
    public const string Path = "/hub/trains";
    public const string TrainChanged = "TrainChanged";
    public const string NotificationCreated = "NotificationCreated";
    public const string ServerTime = "ServerTime";
    public const string ForecastChanged = "ForecastChanged";
    public const string SyncAlertRaised = "SyncAlertRaised";

    /// <summary>REOS-105: a connection joins its workspace's group (and its own group within it) and hears only that workspace's changes.</summary>
    public static string WorkspaceGroup(string workspaceId) => "ws:" + workspaceId;
    public static string MemberGroup(string workspaceId, string userId) => $"ws:{workspaceId}:u:{userId}";

    /// <summary>A new connection gets the server clock straight away, so countdowns are right from the first second (the 10 s timer keeps them honest).
    /// Its workspace is the one the request resolved (<see cref="WorkspaceResolution"/>; the <c>workspace</c> query value names it).</summary>
    public override async Task OnConnectedAsync()
    {
        connections.Add(Context);   // REOS-63: closed on sign-out, refused session, deactivation, and by the periodic re-check
        if (Context.User?.FindFirst(WorkspaceResolution.Claim)?.Value is { } ws && ws != WorkspaceResolution.Nowhere)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, WorkspaceGroup(ws));
            if (Context.UserIdentifier is { } uid) await Groups.AddToGroupAsync(Context.ConnectionId, MemberGroup(ws, uid));
        }
        await Clients.Caller.SendAsync(ServerTime, time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"));
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}

/// <summary>Routes Clients.User(id) by the Users.Id in the "uid" claim (set at sign-in by UserProvisioner).</summary>
public sealed class UidUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) => connection.User?.FindFirst("uid")?.Value;
}

/// <summary>REOS-105: every hint goes to the group of the workspace it belongs to (a notification: to its user's connections in that workspace).</summary>
public sealed class SignalRPublisher(IHubContext<TrainsHub> hub) : IRealtimePublisher
{
    public Task TrainChangedAsync(string workspaceId, string trainId, int version, CancellationToken ct = default) =>
        hub.Clients.Group(TrainsHub.WorkspaceGroup(workspaceId)).SendAsync(TrainsHub.TrainChanged, trainId, version, ct);

    public Task NotificationCreatedAsync(string workspaceId, string userId, string notificationId, CancellationToken ct = default) =>
        hub.Clients.Group(TrainsHub.MemberGroup(workspaceId, userId)).SendAsync(TrainsHub.NotificationCreated, notificationId, ct);

    public Task ForecastChangedAsync(string workspaceId, string trainId, string runId, CancellationToken ct = default) =>
        hub.Clients.Group(TrainsHub.WorkspaceGroup(workspaceId)).SendAsync(TrainsHub.ForecastChanged, trainId, runId, ct);

    public Task SyncAlertRaisedAsync(string? workspaceId, string alertId, CancellationToken ct = default) =>
        (workspaceId is null ? hub.Clients.All : hub.Clients.Group(TrainsHub.WorkspaceGroup(workspaceId))).SendAsync(TrainsHub.SyncAlertRaised, alertId, ct);
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
                try { await hub.Clients.All.SendAsync(TrainsHub.ServerTime, time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"), stop); }
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
