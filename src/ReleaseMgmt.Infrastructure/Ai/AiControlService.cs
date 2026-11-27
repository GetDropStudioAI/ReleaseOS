using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Ai;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Infrastructure.Ai;

/// <summary>
/// REOS-184 (D58): the workspace's AI controls (pause = the workspace kill switch, and the opt-out) kept in <c>AiWorkspaceSettings</c> so they survive a restart, each change versioned and audited in one
/// transaction (<c>AiWorkspace</c> rows: Paused, Resumed, OptedOut, OptedIn). A change to the value it already has writes nothing. The provider key being stored or removed is audited too, without the key.
/// </summary>
public sealed class AiControlService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IRealtimePublisher? realtime = null) : ServiceBase(dbf, time, realtime), IAiControls
{
    public Task<AiWorkspaceState> SetPausedAsync(string workspaceId, bool paused, Actor actor, CancellationToken ct = default) =>
        ChangeAsync(workspaceId, actor, s => s.Paused == paused, paused ? "Paused" : "Resumed", s => s.Paused = paused, ct);

    public Task<AiWorkspaceState> SetOptedOutAsync(string workspaceId, bool optedOut, Actor actor, CancellationToken ct = default) =>
        ChangeAsync(workspaceId, actor, s => s.OptedOut == optedOut, optedOut ? "OptedOut" : "OptedIn", s => s.OptedOut = optedOut, ct);

    private async Task<AiWorkspaceState> ChangeAsync(string workspaceId, Actor actor, Func<AiWorkspaceSettings, bool> alreadyDone, string action, Action<AiWorkspaceSettings> apply, CancellationToken ct)
    {
        using var _ = WorkspaceScope.Enter(workspaceId);
        var result = await RunAsync(async db =>
        {
            var row = await db.Set<AiWorkspaceSettings>().SingleOrDefaultAsync(s => s.WorkspaceId == workspaceId, ct);
            var isNew = row is null;
            row ??= new AiWorkspaceSettings { WorkspaceId = workspaceId };
            if (alreadyDone(row)) return ServiceResult<AiWorkspaceState>.Ok(new AiWorkspaceState(row.Paused, row.OptedOut));   // already so: nothing to write, nothing to audit
            var before = new { paused = row.Paused, optedOut = row.OptedOut };
            apply(row);
            row.LastChangedByUserId = actor.UserId; row.LastChangedAt = Now;
            if (isNew) db.Set<AiWorkspaceSettings>().Add(row); else row.Version++;
            Audit(db, actor, null, "AiWorkspace", workspaceId, action, before, new { paused = row.Paused, optedOut = row.OptedOut });
            await db.SaveChangesAsync(ct);
            return ServiceResult<AiWorkspaceState>.Ok(new AiWorkspaceState(row.Paused, row.OptedOut));
        }, ct);
        return result.IsOk ? result.Value! : throw new InvalidOperationException("The AI control could not be changed: " + string.Join("; ", result.Failures.Select(f => f.Message)));
    }

    public async Task RecordKeyChangeAsync(string action, Actor actor, CancellationToken ct = default)
    {
        using var _ = WorkspaceScope.HostWide();
        var result = await RunAsync(async db =>
        {
            var row = Audit(db, actor, null, "AiKey", "provider", action, null, new { changed = action });   // the key itself is never written anywhere but its Data Protection file
            row.WorkspaceId = null;                                                                            // the key belongs to the host, not a workspace
            await db.SaveChangesAsync(ct);
            return ServiceResult<bool>.Ok(true);
        }, ct);
        if (!result.IsOk) throw new InvalidOperationException("The AI key change could not be audited");
    }
}

/// <summary>REOS-184: the controls as the governed client reads them, from the database on every call (so a pause or an opt-out holds across a restart and across instances).</summary>
public sealed class DbAiWorkspaceSettings(IDbContextFactory<ReleaseDbContext> dbf) : IAiWorkspaceSettings
{
    public async Task<AiWorkspaceState> GetAsync(string workspaceId, CancellationToken ct = default)
    {
        using var _ = WorkspaceScope.HostWide();   // read the named workspace's row, not whatever scope the caller is in
        await using var db = await dbf.CreateDbContextAsync(ct);
        var row = await db.Set<AiWorkspaceSettings>().AsNoTracking().Where(s => s.WorkspaceId == workspaceId).Select(s => new AiWorkspaceState(s.Paused, s.OptedOut)).SingleOrDefaultAsync(ct);
        return row ?? new AiWorkspaceState(false, false);
    }
}
