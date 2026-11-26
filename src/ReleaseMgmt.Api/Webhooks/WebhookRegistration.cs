using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReleaseMgmt.Api.Reminders;
using ReleaseMgmt.Infrastructure.Webhooks;

namespace ReleaseMgmt.Api.Webhooks;

public static class WebhookRegistration
{
    /// <summary>
    /// REOS-164: outbound webhooks (subscriptions, signing secrets, the SSRF-guarded sender, the delivery processor and its worker). Needs the DbContext factory, TimeProvider, Data Protection, the sync options, the webhook URL vault
    /// and the alert writer registered first (<c>AddSyncEngine</c>, <c>AddNotificationScheduling</c>). A test replaces <see cref="IWebhookSender"/> with a fake.
    /// </summary>
    public static IServiceCollection AddOutboundWebhooks(this IServiceCollection services, IConfiguration config, bool isDevelopment)
    {
        var options = WebhookOutOptions.From(config, isDevelopment);
        services.AddSingleton(options);
        services.TryAddSingleton(OutboundAddressPolicy.From(config));
        services.TryAddSingleton<IOutboundAddressGuard, OutboundAddressGuard>();
        services.TryAddSingleton<IWebhookSigningSecrets, DataProtectionWebhookSecrets>();
        services.AddHttpClient(OutboundWebhookSender.ClientName)
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
                    var ok = addrs.Where(a => options.AllowPrivateTargets || !policy.IsBlocked(a)).ToArray();
                    if (ok.Length == 0) throw new HttpRequestException("The webhook target resolves only to blocked addresses");
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try { await socket.ConnectAsync(ok, ctx.DnsEndPoint.Port, ct); return new NetworkStream(socket, ownsSocket: true); }
                    catch { socket.Dispose(); throw; }
                },
            });
        services.TryAddSingleton<IWebhookSender, OutboundWebhookSender>();
        services.AddSingleton<WebhookSubscriptionService>();
        services.AddSingleton<WebhookDeliveryProcessor>();
        services.AddHostedService<WebhookDeliveryWorker>();
        return services;
    }
}
