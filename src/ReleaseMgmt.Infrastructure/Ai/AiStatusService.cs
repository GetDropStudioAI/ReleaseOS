using Microsoft.Extensions.Options;
using ReleaseMgmt.Domain.Ai;
using ReleaseMgmt.Domain.Services;

namespace ReleaseMgmt.Infrastructure.Ai;

public sealed record AiFeatureRoute(string Key, string Name, string Tier, string? Model);
public sealed record AiModelStatus(string? Small, string? Drafting, bool DraftingFallsBackToSmall);

/// <summary>What the settings panel shows. <c>KeyPresent</c> is a yes or no: the key itself is never in any status, log or response.</summary>
public sealed record AiStatus(bool ModuleEnabled, bool ModuleEntitled, string Provider, bool KeyPresent, bool KeyRequired, bool GlobalKillSwitch, bool WorkspacePaused, bool WorkspaceOptedOut,
    AiModelStatus Models, IReadOnlyList<AiFeatureRoute> Features, int TimeoutSeconds, int MaxRetries, bool CanCall, string? BlockedBecause);

/// <summary>REOS-183: the read side of the platform for admins, and the two workspace controls (pause, opt out) and the key. Changes are logged, not yet audited in the database (REOS-184).</summary>
public sealed class AiStatusService(IAiProvider provider, IAiKeyStore keys, IOptionsMonitor<AiOptions> options, IAiWorkspaceSettings workspaces, IModuleGate modules, IModuleEntitlements entitlements,
    IAiClient client)
{
    public async Task<AiStatus> GetAsync(string workspaceId, CancellationToken ct = default)
    {
        var o = options.CurrentValue;
        var enabled = await modules.IsEnabledAsync(workspaceId, ModuleKeys.Ai, ct);
        var entitled = (await entitlements.CheckAsync(workspaceId, ModuleKeys.Ai, ct)).Allowed;
        var ws = workspaces.Get(workspaceId);
        var small = string.IsNullOrWhiteSpace(o.Models.Small) ? null : o.Models.Small.Trim();
        var drafting = string.IsNullOrWhiteSpace(o.Models.Drafting) ? null : o.Models.Drafting.Trim();
        var routes = AiFeatures.All.Select(f => new AiFeatureRoute(f.Key, f.Name, f.Tier.ToString(), GovernedAiClient.RouteModel(o, f))).ToList();
        var pre = await client.PreflightAsync(workspaceId, AiFeatures.RiskScore, ct);
        return new AiStatus(enabled, entitled, provider.Name, await keys.HasKeyAsync(ct), provider.RequiresKey, o.KillSwitch, ws.Paused, ws.OptedOut,
            new AiModelStatus(small, drafting, drafting is null), routes, (int)o.Timeout.TotalSeconds, o.EffectiveRetries, pre.Allowed, pre.Allowed ? null : pre.Detail);
    }
}
