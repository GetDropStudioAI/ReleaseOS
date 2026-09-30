using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Infrastructure.Reminders;

public sealed record InboxItem(string Id, string Kind, string EntityType, string EntityId, int EscalationLevel, string Message, DateTime CreatedAt, DateTime? ReadAt, int Version, string? TrainId, string? TrainTitle);
public sealed record InboxPage(IReadOnlyList<InboxItem> Items, int Total, int Unread, int Limit, int Offset);
public sealed record InboxCount(int Unread, int Total);

/// <summary>The signed-in user's inbox (Notifications). Every method takes the caller's own user id: there is no way to address someone else's rows.
/// Marking read is a service write: Version is stamped and one audit row is written in the same transaction.</summary>
public sealed class NotificationInboxService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time) : ServiceBase(dbf, time)
{
    public const string NotYours = "NotYourNotification";
    public const int MaxLimit = 100;
    private readonly IDbContextFactory<ReleaseDbContext> _dbf = dbf;

    public async Task<InboxPage> ListAsync(string userId, bool unreadOnly, int limit, int offset, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, MaxLimit); offset = Math.Max(0, offset);
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var mine = db.Set<Notifications>().AsNoTracking().Where(n => n.UserId == userId);
        var unread = await mine.CountAsync(n => n.ReadAt == null, ct);
        var q = unreadOnly ? mine.Where(n => n.ReadAt == null) : mine;
        var total = unreadOnly ? unread : await mine.CountAsync(ct);
        // Newest first; the row id breaks ties so paging is stable within one second.
        var rows = await q.OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id).Skip(offset).Take(limit).ToListAsync(ct);
        var trains = await TrainsForAsync(db, rows, ct);
        var items = rows.Select(n =>
        {
            trains.TryGetValue(n.Id, out var t);
            return new InboxItem(n.Id, n.Kind, n.EntityType, n.EntityId, n.EscalationLevel, n.Message, n.CreatedAt, n.ReadAt, n.Version, t.Id, t.Title);
        }).ToList();
        return new InboxPage(items, total, unread, limit, offset);
    }

    public async Task<InboxCount> CountAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var mine = db.Set<Notifications>().AsNoTracking().Where(n => n.UserId == userId);
        return new InboxCount(await mine.CountAsync(n => n.ReadAt == null, ct), await mine.CountAsync(ct));
    }

    /// <summary>Idempotent: reading an already-read notification changes nothing. Someone else's notification is refused with guard <see cref="NotYours"/>.</summary>
    public Task<ServiceResult<Notifications>> MarkReadAsync(string userId, string id, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var n = await db.Set<Notifications>().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (n is null) return ServiceResult<Notifications>.NotFound("notification");
            if (n.UserId != userId) return ServiceResult<Notifications>.Fail(new GuardFailure(NotYours, "That notification belongs to someone else"));
            if (n.ReadAt is not null) return ServiceResult<Notifications>.Ok(n);
            n.ReadAt = Now; n.Version++;
            Audit(db, new Actor(userId), null, "Notification", n.Id, "Read", after: new { n.Kind, n.EntityType, n.EntityId });
            await db.SaveChangesAsync(ct);
            return ServiceResult<Notifications>.Ok(n);
        }, ct);

    public async Task<int> MarkAllReadAsync(string userId, CancellationToken ct = default)
    {
        var r = await RunAsync(async db =>
        {
            var rows = await db.Set<Notifications>().Where(n => n.UserId == userId && n.ReadAt == null).ToListAsync(ct);
            if (rows.Count == 0) return ServiceResult<int>.Ok(0);
            var now = Now;
            foreach (var n in rows) { n.ReadAt = now; n.Version++; }
            Audit(db, new Actor(userId), null, "Notification", userId, "ReadAll", after: new { count = rows.Count });
            await db.SaveChangesAsync(ct);
            return ServiceResult<int>.Ok(rows.Count);
        }, ct);
        if (!r.IsOk) throw new InvalidOperationException("Could not mark notifications read: " + (r.Failures.Count > 0 ? r.Failures[0].Message : r.Missing));
        return r.Value;
    }

    /// <summary>Best-effort deep-link help for the UI: which train an entity belongs to (null when it has none or no longer exists).</summary>
    private static async Task<Dictionary<string, (string? Id, string? Title)>> TrainsForAsync(ReleaseDbContext db, List<Notifications> rows, CancellationToken ct)
    {
        List<string> Ids(params string[] types) => [.. rows.Where(n => types.Contains(n.EntityType)).Select(n => n.EntityId).Distinct()];
        var trainOf = new Dictionary<(string Type, string Id), string>();

        foreach (var t in Ids("ReleaseTrain")) trainOf[("ReleaseTrain", t)] = t;
        var gates = Ids("StageGate", "Gate");
        foreach (var g in await db.Set<StageGates>().AsNoTracking().Where(x => gates.Contains(x.Id)).Select(x => new { x.Id, x.ReleaseTrainId }).ToListAsync(ct))
        { trainOf[("StageGate", g.Id)] = g.ReleaseTrainId; trainOf[("Gate", g.Id)] = g.ReleaseTrainId; }
        var runs = Ids("RunbookRun");
        foreach (var r in await db.Set<RunbookRuns>().AsNoTracking().Where(x => runs.Contains(x.Id)).Select(x => new { x.Id, x.ReleaseTrainId }).ToListAsync(ct)) trainOf[("RunbookRun", r.Id)] = r.ReleaseTrainId;
        var execs = Ids("StepExecution");
        foreach (var e in await (from x in db.Set<StepExecutions>().AsNoTracking() join r in db.Set<RunbookRuns>() on x.RunId equals r.Id where execs.Contains(x.Id) select new { x.Id, r.ReleaseTrainId }).ToListAsync(ct))
            trainOf[("StepExecution", e.Id)] = e.ReleaseTrainId;
        var conds = Ids("GoNoGoCondition");
        foreach (var c in await (from x in db.Set<GoNoGoConditions>().AsNoTracking() join d in db.Set<GoNoGoDecisions>() on x.DecisionId equals d.Id where conds.Contains(x.Id) select new { x.Id, d.ReleaseTrainId }).ToListAsync(ct))
            trainOf[("GoNoGoCondition", c.Id)] = c.ReleaseTrainId;
        var alerts = Ids("SyncAlert");
        foreach (var a in await db.Set<SyncAlerts>().AsNoTracking().Where(x => alerts.Contains(x.Id) && x.ReleaseTrainId != null).Select(x => new { x.Id, x.ReleaseTrainId }).ToListAsync(ct)) trainOf[("SyncAlert", a.Id)] = a.ReleaseTrainId!;

        var trainIds = trainOf.Values.Distinct().ToList();
        var titles = await db.Set<ReleaseTrains>().AsNoTracking().Where(t => trainIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Title, ct);
        var result = new Dictionary<string, (string?, string?)>();
        foreach (var n in rows)
            if (trainOf.TryGetValue((n.EntityType, n.EntityId), out var tid) && titles.TryGetValue(tid, out var title)) result[n.Id] = (tid, title);
        return result;
    }
}
