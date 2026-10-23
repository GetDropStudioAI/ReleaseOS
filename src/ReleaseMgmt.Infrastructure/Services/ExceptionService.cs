using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

/// <summary>
/// One exception to a rule: a gate waiver or a freeze override. State: Pending (asked, not decided), Approved (a waiver approved; an override granted and still in force), Expired (an override past its
/// end), Declined. A freeze override request has no row of its own (Q-034b); it is the audited <c>RequestOverride</c> call, and a decline is an audited <c>DeclineOverride</c> call.
/// </summary>
public sealed record ExceptionItem(string Kind, string Id, string TrainId, string? ReleaseKey, string TrainTitle, string Subject, string State, string? RequestedByUserId, string? RequestedByName,
    string? RequestedAt, string? DecidedByUserId, string? DecidedByName, string? DecidedAt, string? Reason, string? Note, string? ExpiresAt, string? GateId, string? WindowId);

public sealed record ExceptionList(IReadOnlyList<ExceptionItem> Items, int Pending, int Approved, int Declined, int Expired);

/// <summary>Waivers and freeze overrides in one list, with the declined state (REOS-113). Read-only: deciding is done by <see cref="WaiverService"/> and <see cref="FreezeService"/>.</summary>
public sealed class ExceptionService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time)
{
    public const string Waiver = "Waiver", FreezeOverride = "FreezeOverride";
    public static readonly string[] States = ["Pending", "Approved", "Declined", "Expired"];

    private static string Iso(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
    private static string? Iso(DateTime? t) => t?.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    public async Task<ExceptionList> ListAsync(string? state = null, string? kind = null, string? trainId = null, int take = 200, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;
        var trains = await db.Set<ReleaseTrains>().AsNoTracking().Where(t => trainId == null || t.Id == trainId).Select(t => new { t.Id, t.ReleaseKey, t.Title }).ToDictionaryAsync(t => t.Id, ct);
        var people = await db.Set<Users>().AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        string? Name(string? id) => id is null ? null : people.GetValueOrDefault(id);
        var items = new List<ExceptionItem>();

        if (kind is null or Waiver)
        {
            var gates = await db.Set<StageGates>().AsNoTracking().Where(g => trains.Keys.Contains(g.ReleaseTrainId)).ToDictionaryAsync(g => g.Id, ct);
            var gateIds = gates.Keys.ToList();
            foreach (var w in await db.Set<GateWaivers>().AsNoTracking().Where(w => gateIds.Contains(w.StageGateId)).ToListAsync(ct))
            {
                var g = gates[w.StageGateId]; var t = trains[g.ReleaseTrainId];
                var decided = w.DeclinedAt is not null ? (w.DeclinedByUserId, w.DeclinedAt) : (w.ApprovedByUserId, w.ApprovedAt);
                items.Add(new(Waiver, w.Id, t.Id, t.ReleaseKey, t.Title, g.GateName, w.DeclinedAt is not null ? "Declined" : w.ApprovedAt is not null ? "Approved" : "Pending", w.RequestedByUserId, Name(w.RequestedByUserId),
                    Iso(w.RequestedAt), decided.Item1, Name(decided.Item1), Iso(decided.Item2), w.Reason, w.DeclineNote, null, g.Id, null));
            }
        }

        if (kind is null or FreezeOverride)
        {
            var windows = await db.Set<FreezeWindows>().AsNoTracking().ToDictionaryAsync(w => w.Id, w => w.Name, ct);
            foreach (var o in await db.Set<FreezeOverrides>().AsNoTracking().Where(o => trains.Keys.Contains(o.ReleaseTrainId)).ToListAsync(ct))
            {
                var t = trains[o.ReleaseTrainId];
                items.Add(new(FreezeOverride, o.Id, t.Id, t.ReleaseKey, t.Title, windows.GetValueOrDefault(o.FreezeWindowId, "Freeze window"), o.ExpiresAt > now ? "Approved" : "Expired", o.RequestedByUserId, Name(o.RequestedByUserId),
                    null, o.ApprovedByUserId, Name(o.ApprovedByUserId), Iso(o.ApprovedAt), o.Reason, null, Iso(o.ExpiresAt), null, o.FreezeWindowId));
            }
            foreach (var r in await FreezeService.OpenRequestsAsync(db, null, null, ct))
            {
                if (!trains.TryGetValue(r.TrainId, out var t)) continue;
                items.Add(new(FreezeOverride, $"{r.WindowId}:{r.TrainId}:{r.RequesterId}", t.Id, t.ReleaseKey, t.Title, windows.GetValueOrDefault(r.WindowId, "Freeze window"), "Pending", r.RequesterId, Name(r.RequesterId),
                    Iso(r.RequestedAt), null, null, null, r.Reason, null, null, null, r.WindowId));
            }
            foreach (var d in await db.Set<AuditEvents>().AsNoTracking().Where(a => a.EntityType == "FreezeWindow" && a.Action == FreezeService.DeclineAction && a.ReleaseTrainId != null && a.AfterJson != null).ToListAsync(ct))
            {
                if (!trains.TryGetValue(d.ReleaseTrainId!, out var t)) continue;
                using var doc = JsonDocument.Parse(d.AfterJson!);
                var by = doc.RootElement.TryGetProperty("requestedBy", out var b) ? b.GetString() : null;
                var note = doc.RootElement.TryGetProperty("note", out var n) ? n.GetString() : null;
                items.Add(new(FreezeOverride, $"{d.EntityId}:{d.ReleaseTrainId}:{by}:declined:{d.Id}", t.Id, t.ReleaseKey, t.Title, windows.GetValueOrDefault(d.EntityId, "Freeze window"), "Declined", by, Name(by), null,
                    d.ActorUserId, Name(d.ActorUserId), Iso(d.OccurredAt), null, note, null, null, d.EntityId));
            }
        }

        var counts = (Pending: items.Count(i => i.State == "Pending"), Approved: items.Count(i => i.State == "Approved"), Declined: items.Count(i => i.State == "Declined"), Expired: items.Count(i => i.State == "Expired"));
        var shown = items.Where(i => state is null || i.State == state)
            .OrderBy(i => i.State == "Pending" ? 0 : 1).ThenByDescending(i => i.DecidedAt ?? i.RequestedAt, StringComparer.Ordinal).ThenBy(i => i.Id, StringComparer.Ordinal).Take(Math.Clamp(take, 1, 500)).ToList();
        return new(shown, counts.Pending, counts.Approved, counts.Declined, counts.Expired);
    }
}
