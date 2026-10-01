using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReleaseMgmt.Infrastructure.Reminders;

namespace ReleaseMgmt.Api.Reminders;

public static class NotificationRegistration
{
    /// <summary>REOS-37: the reminder/escalation scheduler, the inbox and My work services, and the team-webhook sender with its SSRF-guarded HTTP client.
    /// Needs INotifier, IAlertSink, TimeProvider and the DbContext factory registered first.</summary>
    public static IServiceCollection AddNotificationScheduling(this IServiceCollection services, IConfiguration config)
    {
        var allowPrivate = config.GetValue("Notifications:Webhooks:AllowPrivateTargets", false);
        services.TryAddSingleton(OutboundAddressPolicy.From(config));   // REOS-77: Sync:Nat64Prefixes apply to webhooks too (one address policy)
        services.AddHttpClient(TeamWebhookSender.ClientName)
            .ConfigurePrimaryHttpMessageHandler(sp => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,                       // a redirect is a second, unchecked destination
                UseProxy = false,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                ConnectCallback = async (ctx, ct) =>
                {
                    // Resolve and vet here, at connect time, so the address that is checked is the address that is used.
                    var addrs = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
                    var policy = sp.GetRequiredService<OutboundAddressPolicy>();
                    var ok = addrs.Where(a => allowPrivate || !policy.IsBlocked(a)).ToArray();
                    if (ok.Length == 0) throw new HttpRequestException("The webhook target resolves only to blocked addresses");
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try { await socket.ConnectAsync(ok, ctx.DnsEndPoint.Port, ct); return new NetworkStream(socket, ownsSocket: true); }
                    catch { socket.Dispose(); throw; }
                },
            });
        services.AddSingleton<SyncAlertWriter>();
        services.AddSingleton<ITeamWebhookSender, TeamWebhookSender>();
        services.AddSingleton<NotificationScheduler>();
        services.AddHostedService(sp => sp.GetRequiredService<NotificationScheduler>());
        services.AddSingleton<NotificationInboxService>();
        services.AddSingleton<MyWorkService>();
        return services;
    }
}
