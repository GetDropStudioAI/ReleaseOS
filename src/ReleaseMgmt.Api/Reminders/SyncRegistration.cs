using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReleaseMgmt.Domain.Sync;
using ReleaseMgmt.Infrastructure.Reminders;
using ReleaseMgmt.Infrastructure.Sync;

namespace ReleaseMgmt.Api.Reminders;

public static class SyncRegistration
{
    /// <summary>
    /// REOS-39/40/41: the ITSM sync engine. Registers the Data Protection key ring and credential store, the SSRF-guarded HTTP client for the read
    /// connectors, the poller and watchdog (hosted), and the Connectors service. Needs TimeProvider, IAlertSink and the DbContext factory registered first;
    /// SyncAlertWriter is added here if AddNotificationScheduling has not already added it.
    /// Config: Sync:PollSeconds, Sync:WindowPollSeconds, Sync:TimeoutSeconds, Sync:MaxResponseBytes, Sync:BackoffBaseSeconds, Sync:WatchdogSeconds, Sync:StartDelaySeconds,
    /// Sync:Enabled, Sync:AllowPrivateTargets, Sync:AllowedHosts, Sync:WindowToleranceMinutes, Sync:Credentials:Directory (default: "secrets" beside the database),
    /// DataProtection:KeysDirectory (default: "keys" beside the database; back it up, D12), Connectors:{Jira|ServiceNow}:BaseUrl, Connectors:ServiceNow:TokenPath.
    /// </summary>
    public static IServiceCollection AddSyncEngine(this IServiceCollection services, IConfiguration config)
    {
        var dbDir = Path.GetDirectoryName(Path.GetFullPath(config["Db:Path"] ?? "data/releasemgmt.db"))!;
        var options = SyncOptions.From(config, Path.Combine(dbDir, "secrets"));
        services.AddSingleton(options);

        services.AddDataProtection().SetApplicationName("ReleaseMgmt")
            .PersistKeysToFileSystem(new DirectoryInfo(config["DataProtection:KeysDirectory"] ?? Path.Combine(dbDir, "keys")));

        services.AddHttpClient(ConnectorHttp.ClientName)
            .ConfigureHttpClient(c => c.Timeout = Timeout.InfiniteTimeSpan)   // ConnectorHttp bounds every call itself (Sync:TimeoutSeconds)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,                       // a redirect is a second, unchecked destination
                UseProxy = false,                                // the connect callback below is the SSRF check, and a proxy would bypass it
                ConnectTimeout = options.Timeout,
                ConnectCallback = async (ctx, ct) =>
                {
                    // Resolve and vet here, at connect time, so the address that is checked is the address that is used.
                    var addrs = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
                    var ok = addrs.Where(a => options.AllowPrivateTargets || !WebhookAddressPolicy.IsBlocked(a)).ToArray();
                    if (ok.Length == 0) throw new HttpRequestException("The connector host resolves only to blocked addresses");
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try { await socket.ConnectAsync(ok, ctx.DnsEndPoint.Port, ct); return new NetworkStream(socket, ownsSocket: true); }
                    catch { socket.Dispose(); throw; }
                },
            });

        services.TryAddSingleton<SyncAlertWriter>();
        services.AddSingleton<ICredentialStore, DataProtectionCredentialStore>();
        services.AddSingleton<IConnectorFactory>(sp => new ConnectorFactory(sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<ICredentialStore>(), options,
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<IHostEnvironment>(), sp.GetRequiredService<IConfiguration>(), WebhookAddressPolicy.IsBlocked));
        services.AddSingleton<SyncCycleWriter>();
        services.AddSingleton<SyncPollerService>();
        services.AddHostedService(sp => sp.GetRequiredService<SyncPollerService>());
        services.AddSingleton<SyncWatchdogService>();
        services.AddHostedService(sp => sp.GetRequiredService<SyncWatchdogService>());
        services.AddSingleton(sp => new ConnectorService(sp.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<ReleaseMgmt.Infrastructure.Persistence.ReleaseDbContext>>(),
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ICredentialStore>(), sp.GetRequiredService<IConnectorFactory>(), sp.GetRequiredService<SyncAlertWriter>(),
            options, sp.GetRequiredService<IHostEnvironment>(), WebhookAddressPolicy.IsBlocked, sp.GetService<ReleaseMgmt.Domain.Services.IRealtimePublisher>()));
        return services;
    }
}
