using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReleaseMgmt.Domain.Ai;
using ReleaseMgmt.Infrastructure.Ai.Providers;

namespace ReleaseMgmt.Infrastructure.Ai;

public static class AiServiceCollectionExtensions
{
    /// <summary>
    /// REOS-183: registers the AI platform. <see cref="IAiClient"/> resolves to the governed client only; the provider adapter is registered as <see cref="IAiProvider"/> and is meant to be
    /// reached by nothing but the governed client. Needs <c>IModuleGate</c>, <c>IModuleEntitlements</c>, <c>TimeProvider</c>, the <c>ReleaseDbContext</c> factory and Data Protection from the host.
    /// </summary>
    public static IServiceCollection AddAiPlatform(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<AiOptions>().Bind(config.GetSection(AiOptions.Section)).Validate(o => o.Provider is "anthropic" or "fake", "Ai:Provider must be 'anthropic' or 'fake'")
            .Validate(o => o.TimeoutSeconds > 0, "Ai:TimeoutSeconds must be positive")
            .Validate(o => o.Allowance.IncludedCredits >= 0 && o.Allowance.SoftCapPercent is >= 1 and <= 100 && o.Allowance.ChatSharePercent is >= 0 and <= 100 && o.Allowance.UserPerMinute >= 0 && o.Allowance.UserPerHour >= 0,
                "Ai:Allowance: IncludedCredits and the rate limits cannot be negative, SoftCapPercent is 1 to 100 and ChatSharePercent is 0 to 100")
            .Validate(o => o.Pricing.CacheReadMultiplier >= 0 && o.Pricing.CacheWriteMultiplier >= 0 && o.Pricing.BatchMultiplier > 0 && o.Pricing.Models.Values.All(m => m.InputPerMTok >= 0 && m.OutputPerMTok >= 0),
                "Ai:Pricing: prices and multipliers cannot be negative and the batch multiplier must be above zero").ValidateOnStart();
        services.AddSingleton<IAiKeyStore, DataProtectionAiKeyStore>();
        services.AddSingleton<IAiWorkspaceSettings, DbAiWorkspaceSettings>();   // REOS-184: persisted, so the opt-out holds across a restart
        services.AddSingleton<AiActionService>();                                // REOS-184: the audit trail; IAiActionLog is the only door AI code uses
        services.AddSingleton<IAiActionLog>(sp => sp.GetRequiredService<AiActionService>());
        services.AddSingleton<IAiAllowancePolicy, ConfigAiAllowancePolicy>();    // REOS-185: the included allowance and the caps; the plans story supplies a per-plan one
        services.AddSingleton<AiAllowanceService>();
        services.AddSingleton<IAiAllowance>(sp => sp.GetRequiredService<AiAllowanceService>());
        services.AddSingleton<AiControlService>();
        services.AddSingleton<IAiControls>(sp => sp.GetRequiredService<AiControlService>());
        services.AddSingleton<IAiUsageSink, NoopAiUsageSink>();
        services.AddSingleton<IAiProvider>(AiProviderFactory.Create);
        services.AddSingleton<IAiClient, GovernedAiClient>();
        services.AddSingleton<AiStatusService>();
        services.AddSingleton<AiInventoryService>();
        return services;
    }
}
