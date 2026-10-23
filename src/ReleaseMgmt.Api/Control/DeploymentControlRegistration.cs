using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReleaseMgmt.Api.Reminders;
using ReleaseMgmt.Infrastructure.Comms;
using ReleaseMgmt.Infrastructure.Services;
using ReleaseMgmt.Infrastructure.Sync;

namespace ReleaseMgmt.Api.Control;

public static class DeploymentControlRegistration
{
    /// <summary>
    /// REOS-111 to 114: telemetry, deployment control (the signed webhook sender with its SSRF-guarded client, the signing keys), approvals, the audit chain and their background workers.
    /// Needs the DbContext factory, TimeProvider, IRealtimePublisher, Data Protection, the sync options and the alert writer registered first.
    /// </summary>
    public static IServiceCollection AddDeploymentControl(this IServiceCollection services, IConfiguration config)
    {
        services.TryAddSingleton(OutboundAddressPolicy.From(config));
        services.AddHttpClient(ControlWebhookSender.ClientName)
            .ConfigurePrimaryHttpMessageHandler(sp => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,                       // a redirect is a second, unchecked destination
                UseProxy = false,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                ConnectCallback = async (ctx, ct) =>
                {
                    // Resolve and vet here, at connect time, so the address that is checked is the address that is used.
                    var allowPrivate = config.GetValue("Control:Webhooks:AllowPrivateTargets", false);
                    var addrs = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
                    var policy = sp.GetRequiredService<OutboundAddressPolicy>();
                    var ok = addrs.Where(a => allowPrivate || !policy.IsBlocked(a)).ToArray();
                    if (ok.Length == 0) throw new HttpRequestException("The webhook target resolves only to blocked addresses");
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try { await socket.ConnectAsync(ok, ctx.DnsEndPoint.Port, ct); return new NetworkStream(socket, ownsSocket: true); }
                    catch { socket.Dispose(); throw; }
                },
            });
        services.TryAddSingleton<IControlWebhookSender, ControlWebhookSender>();
        services.TryAddSingleton<IControlSigningSecrets, DataProtectionControlSecrets>();
        services.AddSingleton<DeploymentControlService>();
        services.AddSingleton<TelemetryService>();
        services.AddSingleton<ApprovalPolicyService>();
        services.AddSingleton<ApprovalService>();
        services.AddSingleton<ExceptionService>();
        services.AddSingleton<AuditChainService>();
        services.AddHostedService<AuditChainWorker>();
        services.AddHostedService<TelemetryMaintenanceWorker>();
        return services;
    }
}
