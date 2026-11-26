using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ReleaseMgmt.Domain.Ai;

namespace ReleaseMgmt.Infrastructure.Ai.Providers;

/// <summary>Builds the configured adapter. In this folder because only this folder may name an adapter.</summary>
public static class AiProviderFactory
{
    public static IAiProvider Create(IServiceProvider sp)
    {
        var o = sp.GetRequiredService<IOptionsMonitor<AiOptions>>();
        if (o.CurrentValue.Provider == "fake") return new FakeAiClient();
        // No logging handler on this client: the framework's HttpClient logging can record headers, and one of them carries the key.
        var http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = Timeout.InfiniteTimeSpan };
        return new AnthropicAiClient(http, sp.GetRequiredService<IAiKeyStore>(), o, sp.GetRequiredService<TimeProvider>());
    }
}
