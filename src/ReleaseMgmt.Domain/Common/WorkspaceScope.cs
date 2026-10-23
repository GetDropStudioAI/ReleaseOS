namespace ReleaseMgmt.Domain.Common;

/// <summary>
/// The current workspace of the code that is running (D45, REOS-105). A request enters the workspace it resolved (header, route segment or the person's
/// only or default membership); a background service enters each workspace in turn. A database context captures it when it is created: its query
/// filters then show only that workspace's rows and new root rows are written into it. Outside any workspace (start-up, host-wide jobs such as backups
/// and user accounts) nothing is filtered and new rows land in the default workspace, as before workspaces (Q-105a).
/// </summary>
public static class WorkspaceScope
{
    private static readonly AsyncLocal<string?> Ambient = new();

    /// <summary>The workspace entered on this flow, or null outside any.</summary>
    public static string? Current => Ambient.Value;

    /// <summary>Enters a workspace until the returned handle is disposed (the previous scope comes back). Synchronous on purpose: the value then flows
    /// to everything the caller awaits afterwards.</summary>
    public static IDisposable Enter(string workspaceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        var previous = Ambient.Value;
        Ambient.Value = workspaceId;
        return new Exit(previous);
    }

    /// <summary>Leaves every workspace until disposed: host-wide work (user accounts, backups) started from inside a request.</summary>
    public static IDisposable HostWide()
    {
        var previous = Ambient.Value;
        Ambient.Value = null;
        return new Exit(previous);
    }

    private sealed class Exit(string? previous) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            Ambient.Value = previous;
        }
    }
}
