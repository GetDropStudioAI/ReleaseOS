using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Infrastructure.Sync;

/// <summary>What one fetch did to one link. <see cref="SyncedAt"/> is set only on success (LastSyncedAt is stamped only on success, 5.3.2).</summary>
public sealed record LinkUpdate(string LinkId, string? SyncState, string? LastSyncedStatus, string? ExpectedStatus, DateTime? SyncedAt);

/// <summary>
/// Writes the result of one connector cycle in a single transaction: link states and dates, then the ConnectorState bookkeeping.
/// Audited when a link's state or reported status changes (one row per change, actor = system), not for the timestamp/counter bookkeeping
/// of every cycle (that would add hundreds of rows a day per link; Q-039c). A link deleted while the cycle ran is skipped.
/// </summary>
public sealed class SyncCycleWriter(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IRealtimePublisher? realtime = null) : ServiceBase(dbf, time, realtime)
{
    public async Task ApplyAsync(string source, DateTime startedAt, bool connectorFailed, bool clean, IReadOnlyList<LinkUpdate> updates, CancellationToken ct)
    {
        var result = await RunAsync<bool>(async db =>
        {
            var now = Now;
            var ids = updates.Select(u => u.LinkId).ToList();
            var links = await db.Set<ExternalLinks>().Where(l => ids.Contains(l.Id)).ToDictionaryAsync(l => l.Id, ct);
            foreach (var u in updates)
            {
                if (!links.TryGetValue(u.LinkId, out var l)) continue;
                var before = new { l.SyncState, l.LastSyncedStatus, l.ExpectedStatus };
                var changed = false;
                if (u.SyncState is not null && l.SyncState != u.SyncState) { l.SyncState = u.SyncState; changed = true; }
                if (u.SyncedAt is DateTime at)
                {
                    l.LastSyncedAt = at;
                    if (l.LastSyncedStatus != u.LastSyncedStatus) { l.LastSyncedStatus = u.LastSyncedStatus; changed = true; }
                }
                if (u.SyncedAt is not null && l.ExpectedStatus != u.ExpectedStatus) { l.ExpectedStatus = u.ExpectedStatus; changed = true; }
                if (changed)
                {
                    l.Version++;
                    Audit(db, null, l.ReleaseTrainId, "ExternalLink", l.Id, "Synced", before, new { l.SyncState, l.LastSyncedStatus, l.ExpectedStatus });
                }
            }

            var s = await db.Set<ConnectorState>().SingleOrDefaultAsync(x => x.SourceSystem == source, ct);
            if (s is not null)
            {
                s.LastCycleStartedAt = startedAt; s.LastCycleCompletedAt = now;
                if (clean) { s.LastSuccessAt = now; s.ConsecutiveFailures = 0; }
                else if (connectorFailed) s.ConsecutiveFailures++;
                s.Version++;
            }
            await db.SaveChangesAsync(ct);
            return ServiceResult<bool>.Ok(true);
        }, ct);
        if (!result.IsOk) throw new InvalidOperationException("Could not record the sync cycle: " + (result.Failures.Count > 0 ? result.Failures[0].Message : result.Missing));
    }

    /// <summary>Creates the ConnectorState row from <c>Connectors:{source}:BaseUrl</c> when there is none, so a configured connector starts without an admin visit.</summary>
    public async Task EnsureRowAsync(string source, string baseUrl, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        if (await db.Set<ConnectorState>().AnyAsync(x => x.SourceSystem == source, ct)) return;
        var result = await RunAsync<bool>(async tx =>
        {
            if (await tx.Set<ConnectorState>().AnyAsync(x => x.SourceSystem == source, ct)) return ServiceResult<bool>.Ok(false);
            tx.Set<ConnectorState>().Add(new ConnectorState { SourceSystem = source, BaseUrl = baseUrl.Trim() });
            Audit(tx, null, null, "ConnectorState", source, "Create", after: new { source, baseUrl = baseUrl.Trim(), fromConfig = true });
            await tx.SaveChangesAsync(ct);
            return ServiceResult<bool>.Ok(true);
        }, ct);
        if (!result.IsOk) throw new InvalidOperationException("Could not create the connector row: " + (result.Failures.Count > 0 ? result.Failures[0].Message : result.Missing));
    }
}
