using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReleaseMgmt.Domain.Ai;
using ReleaseMgmt.Infrastructure.Ai.Providers;

namespace ReleaseMgmt.Infrastructure.Ai;

public static class AiServiceCollectionExtensions
{
    /// <summary>
    /// REOS-183: registers the AI platform. <see cref="IAiClient"/> resolves to the governed client only; the provider adapter is registered as <see cref="IAiProvider"/> and is meant to be
    /// reached by nothing but the governed client. Needs <c>IModuleGate</c>, <c>IModuleEntitlements</c>, <c>TimeProvider</c> and Data Protection from the host.
    /// </summary>
    public static IServiceCollection AddAiPlatform(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<AiOptions>().Bind(config.GetSection(AiOptions.Section)).Validate(o => o.Provider is "anthropic" or "fake", "Ai:Provider must be 'anthropic' or 'fake'")
            .Validate(o => o.TimeoutSeconds > 0, "Ai:TimeoutSeconds must be positive").ValidateOnStart();
        services.AddSingleton<IAiKeyStore, DataProtectionAiKeyStore>();
        services.AddSingleton<IAiWorkspaceSettings, InMemoryAiWorkspaceSettings>();
        services.AddSingleton<IAiUsageSink, NoopAiUsageSink>();
        services.AddSingleton<IAiProvider>(AiProviderFactory.Create);
        services.AddSingleton<IAiClient, GovernedAiClient>();
        services.AddSingleton<AiStatusService>();
        return services;
    }
}
