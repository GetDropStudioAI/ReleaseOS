namespace ReleaseMgmt.Domain.Services;

/// <summary>
/// REOS-174 (D56, D57, D63): add-on modules. A module is off until an admin of the workspace enables it, lives in its own tables and services, and the core
/// product works without it. This is the registry of the modules that exist; the per-workspace state is the <c>WorkspaceModules</c> table (no row = off).
/// </summary>
public sealed record ModuleDefinition(string Key, string Name, string Summary, IReadOnlyList<string> TurnsOn);

public static class ModuleKeys
{
    public const string Cab = "cab";
    public const string Ai = "ai";
}

public static class ModuleCatalog
{
    /// <summary>The modules, in the order the settings page lists them. Two for now (D57): CAB and AI. Neither has a feature behind it yet; REOS-158 and the AI stories add them.</summary>
    public static readonly IReadOnlyList<ModuleDefinition> All =
    [
        new(ModuleKeys.Cab, "CAB module", "A lightweight change advisory board: change requests (Standard, Normal, Emergency) linked to releases, Go/No-Go decisions and freeze windows.",
            ["A Change advisory board entry appears in the navigation for everyone in this workspace", "The CAB endpoints start answering (until then they answer 404)", "Live updates for CAB records are sent to this workspace"]),
        new(ModuleKeys.Ai, "AI module", "Advisory AI assistance. It only suggests; it never approves or changes a status, and every use is audited (D58).",
            ["An AI assistant entry appears in the navigation for everyone in this workspace", "The AI endpoints start answering (until then they answer 404)", "Each AI-assisted action is recorded in the audit history with the model, its inputs and the human decision"]),
    ];

    public static ModuleDefinition? Find(string? key) => All.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.Ordinal));
}

/// <summary>Where an entitlement came from. <c>Default</c>: no billing is connected, so every module is allowed. <c>Plan</c>: the workspace's plan decided.</summary>
public static class PlanSources
{
    public const string Default = "Default";
    public const string Plan = "Plan";
}

/// <summary>The plan's answer for one module in one workspace. A denial carries the reason the person is shown.</summary>
public sealed record ModuleEntitlement(bool Allowed, string Source, string? Reason = null);

/// <summary>
/// The seam billing plugs into later (D57): can this workspace's plan have this module? Asked when a module is enabled, never when it is disabled (a lapsed plan can
/// always switch a module off). The default implementation allows everything.
/// </summary>
public interface IModuleEntitlements
{
    Task<ModuleEntitlement> CheckAsync(string workspaceId, string moduleKey, CancellationToken ct = default);
}

public sealed class AllowAllModuleEntitlements : IModuleEntitlements
{
    public Task<ModuleEntitlement> CheckAsync(string workspaceId, string moduleKey, CancellationToken ct = default) => Task.FromResult(new ModuleEntitlement(true, PlanSources.Default));
}

/// <summary>Is a module enabled in a workspace? Used by the API guard (a disabled module's endpoints answer 404) and by the live channel (a disabled module's events are not sent).</summary>
public interface IModuleGate
{
    Task<bool> IsEnabledAsync(string workspaceId, string moduleKey, CancellationToken ct = default);
}

public static class ModuleGuards
{
    /// <summary>The workspace's plan does not include the module (422).</summary>
    public const string NotEntitled = "ModuleNotEntitled";
}
