using System.Collections.Concurrent;
using ReleaseMgmt.Domain.Ai;

namespace ReleaseMgmt.Infrastructure.Ai;

/// <summary>REOS-183: per-workspace kill switch and opt-out, kept in memory (lost at restart, which fails open for the opt-out: say so in docs/QUESTIONS.md). REOS-184 persists them.</summary>
public sealed class InMemoryAiWorkspaceSettings : IAiWorkspaceSettings
{
    private readonly ConcurrentDictionary<string, AiWorkspaceState> _state = new();

    public AiWorkspaceState Get(string workspaceId) => _state.TryGetValue(workspaceId, out var s) ? s : new AiWorkspaceState(false, false);
    public void SetPaused(string workspaceId, bool paused) => _state.AddOrUpdate(workspaceId, new AiWorkspaceState(paused, false), (_, s) => s with { Paused = paused });
    public void SetOptedOut(string workspaceId, bool optedOut) => _state.AddOrUpdate(workspaceId, new AiWorkspaceState(false, optedOut), (_, s) => s with { OptedOut = optedOut });
}

public sealed class NoopAiUsageSink : IAiUsageSink
{
    public void Record(AiResult result) { }
}
