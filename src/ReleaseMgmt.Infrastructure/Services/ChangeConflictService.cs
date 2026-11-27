using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

/// <summary><c>Checked</c> is false when the change has no planned window, or is past the point where a conflict matters (Implemented, Closed, Rejected, Cancelled): <c>Reason</c> then says which.</summary>
public sealed record ChangeConflictReport(string ChangeId, string Key, string Status, string? PlannedStartAt, string? PlannedEndAt, bool Checked, string? Reason, int BufferMinutes, IReadOnlyList<ChangeConflict> Conflicts);

public sealed record AgendaConflictItem(string ChangeId, string Key, string Title, string Status, bool Checked, string? Reason, IReadOnlyList<ChangeConflict> Conflicts);
public sealed record MeetingConflicts(string MeetingId, IReadOnlyList<AgendaConflictItem> Items);

public sealed record CabCalendarChange(string Id, string Key, string Title, string ChangeType, string Status, string RiskLevel, string StartsAt, string EndsAt, bool Checked, IReadOnlyList<ChangeConflict> Conflicts);
public sealed record CabCalendarHoliday(string Day, string Name);
public sealed record CabCalendarPayload(string From, string To, IReadOnlyList<CabCalendarChange> Changes, IReadOnlyList<CabCalendarHoliday> Blackouts);

/// <summary>
/// REOS-180 (D56, D9): what a change collides with, derived on every read and never stored. Four kinds, all warnings: freeze or chill windows (the same product match the risk rule uses),
/// blackout days (the Holidays calendar), other Submitted or Approved changes with an overlapping window on the same release, a shared catalog service or a shared target environment, and the
/// deployment windows of other open releases that share a configuration item or product with the linked release (the existing cross-train rule, <see cref="WindowCollisions"/>, with the same
/// <c>Calendar:CollisionBufferMin</c> margin). A change has no service or environment of its own (no schema change): both come from its linked release, so a change with no release is matched only
/// by windows that cover all products and by blackout days. Read-only; the module guard is at the endpoints.
/// </summary>
public sealed class ChangeConflictService(IDbContextFactory<ReleaseDbContext> dbf, IConfiguration? config = null)
{
    public int BufferMinutes { get; } = AdvisoryService.Setting(config, "Calendar:CollisionBufferMin", 30, 0, 1440);

    /// <summary>Changes in these statuses are still ahead of their implementation: their conflicts matter. The others are history.</summary>
    private static readonly string[] Open = [ChangeStatuses.Draft, ChangeStatuses.Submitted, ChangeStatuses.Approved];
    /// <summary>Other changes a change can collide with: the ones the CAB has been asked about or has agreed.</summary>
    private static readonly string[] Committed = [ChangeStatuses.Submitted, ChangeStatuses.Approved];
    private const string Iso = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    public ConflictRulesView Rules() => ChangeConflictRules.Catalogue(BufferMinutes);

    // ---- reads ------------------------------------------------------------------------------------------------------------------------

    public async Task<ChangeConflictReport?> ForChangeAsync(string id, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var c = await db.Set<ChangeRequests>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        return c is null ? null : (await ReportsAsync(db, [c], ct))[0];
    }

    public async Task<MeetingConflicts?> ForMeetingAsync(string meetingId, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        if (!await db.Set<CabMeetings>().AnyAsync(m => m.Id == meetingId, ct)) return null;
        var ids = await db.Set<CabAgendaItems>().AsNoTracking().Where(a => a.MeetingId == meetingId).OrderBy(a => a.SequenceOrder).Select(a => a.ChangeRequestId).ToListAsync(ct);
        var rows = await db.Set<ChangeRequests>().AsNoTracking().Where(c => ids.Contains(c.Id)).ToListAsync(ct);
        var reports = (await ReportsAsync(db, rows, ct)).ToDictionary(r => r.ChangeId);
        return new(meetingId, [.. ids.Where(reports.ContainsKey).Select(i => { var r = reports[i]; return new AgendaConflictItem(r.ChangeId, r.Key, rows.First(x => x.Id == i).Title, r.Status, r.Checked, r.Reason, r.Conflicts); })]);
    }

    /// <summary>Changes with a planned window that touches [from, to] (UTC days, inclusive), except Cancelled and Rejected ones, each with its conflicts, and the blackout days in the range.</summary>
    public async Task<CabCalendarPayload> CalendarAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var ws = Memberships.WorkspaceOf(db);
        var start = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var end = to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var rows = await db.Set<ChangeRequests>().AsNoTracking()
            .Where(c => c.WorkspaceId == ws && c.PlannedStartAt != null && c.PlannedStartAt < end && c.PlannedEndAt > start && c.Status != ChangeStatuses.Cancelled && c.Status != ChangeStatuses.Rejected)
            .OrderBy(c => c.PlannedStartAt).ThenBy(c => c.ChangeKey).ToListAsync(ct);
        var reports = await ReportsAsync(db, rows, ct);
        var holidays = await db.Set<Holidays>().AsNoTracking().Where(h => h.WorkspaceId == ws && h.Day >= from && h.Day <= to).OrderBy(h => h.Day).ToListAsync(ct);
        return new(from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"),
            [.. rows.Select((c, n) => new CabCalendarChange(c.Id, c.ChangeKey, c.Title, c.ChangeType, c.Status, c.RiskLevel, c.PlannedStartAt!.Value.ToString(Iso), c.PlannedEndAt!.Value.ToString(Iso), reports[n].Checked, reports[n].Conflicts))],
            [.. holidays.Select(h => new CabCalendarHoliday(h.Day.ToString("yyyy-MM-dd"), h.Name))]);
    }

    /// <summary>The conflicts of one change as it stands now, for the approval check in <see cref="ChangeRequestService"/>. Empty for a change with no window.</summary>
    public async Task<IReadOnlyList<ChangeConflict>> ForDecisionAsync(ReleaseDbContext db, ChangeRequests c, CancellationToken ct = default) => (await ReportsAsync(db, [c], ct))[0].Conflicts;

    // ---- the derivation ------------------------------------------------------------------------------------------------------------------

    private sealed record TrainFacts(HashSet<string> Services, HashSet<string> Environments);

    /// <summary>One report per change, in the order given. The shared facts (windows, holidays, other changes, releases) are read once for the whole batch.</summary>
    private async Task<List<ChangeConflictReport>> ReportsAsync(ReleaseDbContext db, List<ChangeRequests> rows, CancellationToken ct)
    {
        var ws = Memberships.WorkspaceOf(db);
        var buffer = TimeSpan.FromMinutes(BufferMinutes);
        var live = rows.Where(c => Open.Contains(c.Status) && c.PlannedStartAt is not null).ToList();

        var freezes = live.Count == 0 ? [] : await db.Set<FreezeWindows>().AsNoTracking().Where(f => f.WorkspaceId == ws).ToListAsync(ct);
        var holidays = live.Count == 0 ? [] : await db.Set<Holidays>().AsNoTracking().Where(h => h.WorkspaceId == ws).ToListAsync(ct);
        var others = live.Count == 0 ? [] : await db.Set<ChangeRequests>().AsNoTracking().Where(o => o.WorkspaceId == ws && o.PlannedStartAt != null && Committed.Contains(o.Status)).ToListAsync(ct);

        // what each linked release ships and targets (catalog services, environments, CIs, products)
        var trainIds = live.Concat(others).Select(c => c.ReleaseTrainId).OfType<string>().Distinct().ToList();
        var products = (await db.Set<BundledProducts>().AsNoTracking().Where(p => trainIds.Contains(p.ReleaseTrainId)).Select(p => new { p.ReleaseTrainId, p.ProductName, p.ProjectCode, p.CatalogServiceId }).ToListAsync(ct)).ToLookup(p => p.ReleaseTrainId);
        var serviceNames = await db.Set<CatalogServices>().AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var envs = (await db.Set<TrainEnvironments>().AsNoTracking().Where(t => trainIds.Contains(t.ReleaseTrainId)).Join(db.Set<Environments>().AsNoTracking(), t => t.EnvironmentId, e => e.Id, (t, e) => new { t.ReleaseTrainId, e.Name }).ToListAsync(ct)).ToLookup(x => x.ReleaseTrainId, x => x.Name);
        var cis = (await db.Set<AffectedCIs>().AsNoTracking().Where(x => trainIds.Contains(x.ReleaseTrainId)).Select(x => new { x.ReleaseTrainId, x.CiName }).ToListAsync(ct)).ToLookup(x => x.ReleaseTrainId, x => x.CiName);
        TrainFacts Facts(string? trainId) => trainId is null ? new([], []) : new(
            [.. products[trainId].Select(p => p.CatalogServiceId).OfType<string>().Where(serviceNames.ContainsKey).Select(i => serviceNames[i])],
            [.. envs[trainId]]);

        // the deployment windows of every open release, for the cross-train rule (the same loading AdvisoryService does, without the change's own release)
        var windowed = live.Count == 0 ? [] : await (from t in db.Set<ReleaseTrains>().AsNoTracking()
                                                      join w in db.Set<DeploymentWindows>().AsNoTracking() on t.Id equals w.ReleaseTrainId
                                                      where t.ArchivedAt == null && AdvisoryService.OpenStatuses.Contains(t.CurrentStatus)
                                                      select new { t.Id, t.Title, t.CurrentStatus, w.StartsAt, w.EndsAt }).ToListAsync(ct);
        var wIds = windowed.Select(w => w.Id).ToList();
        var wCis = (await db.Set<AffectedCIs>().AsNoTracking().Where(x => wIds.Contains(x.ReleaseTrainId)).Select(x => new { x.ReleaseTrainId, x.CiName }).ToListAsync(ct)).ToLookup(x => x.ReleaseTrainId, x => x.CiName);
        var wProducts = (await db.Set<BundledProducts>().AsNoTracking().Where(p => wIds.Contains(p.ReleaseTrainId)).Select(p => new { p.ReleaseTrainId, p.ProductName, p.ProjectCode }).ToListAsync(ct)).ToLookup(p => p.ReleaseTrainId);
        var openTrains = windowed.Select(w => new WindowedTrain(w.Id, w.Title, w.CurrentStatus, w.StartsAt, w.EndsAt, [.. wCis[w.Id]], [.. wProducts[w.Id].Select(p => (p.ProductName, (string?)p.ProjectCode))])).ToList();

        var reports = new List<ChangeConflictReport>(rows.Count);
        foreach (var c in rows)
        {
            string? s = c.PlannedStartAt is { } a ? a.ToString(Iso) : null, e = c.PlannedEndAt is { } b ? b.ToString(Iso) : null;
            string? skip = !Open.Contains(c.Status) ? $"A {c.Status} change is no longer ahead of its implementation, so its conflicts are not checked."
                : c.PlannedStartAt is null ? "There is no planned window yet, so conflicts cannot be checked." : null;
            if (skip is not null) { reports.Add(new(c.Id, c.ChangeKey, c.Status, s, e, false, skip, BufferMinutes, [])); continue; }

            var mine = Facts(c.ReleaseTrainId);
            var covering = new List<ConflictFreeze>();
            foreach (var f in freezes.Where(f => f.StartsAt < c.PlannedEndAt && f.EndsAt > c.PlannedStartAt))
            {
                var pattern = f.ProductPattern;   // the same predicate the risk rule and FreezeService use: no pattern covers every product
                if (pattern is null || (c.ReleaseTrainId is not null && await db.Set<BundledProducts>().AnyAsync(p => p.ReleaseTrainId == c.ReleaseTrainId && EF.Functions.Glob(p.ProductName, pattern), ct)))
                    covering.Add(new(f.Id, f.Name, f.Kind, f.StartsAt, f.EndsAt));
            }
            var changes = new List<ConflictChange>();
            foreach (var o in others.Where(o => o.Id != c.Id && ChangeConflictRules.Overlaps(c.PlannedStartAt!.Value, c.PlannedEndAt!.Value, o.PlannedStartAt!.Value, o.PlannedEndAt!.Value, buffer)))
            {
                var theirs = Facts(o.ReleaseTrainId);
                var sameRelease = c.ReleaseTrainId is not null && c.ReleaseTrainId == o.ReleaseTrainId;
                changes.Add(new(o.Id, o.ChangeKey, o.Title, o.Status, o.PlannedStartAt!.Value, o.PlannedEndAt!.Value, sameRelease,
                    [.. mine.Services.Intersect(theirs.Services, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)],
                    [.. mine.Environments.Intersect(theirs.Environments, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)]));
            }
            IReadOnlyList<WindowCollision> windows = [];
            if (c.ReleaseTrainId is { } train)   // the existing cross-train rule, with this change's window standing in for the release's own
                windows = WindowCollisions.Find(new WindowedTrain(train, c.Title, c.Status, c.PlannedStartAt!.Value, c.PlannedEndAt!.Value, [.. cis[train]],
                    [.. products[train].Select(p => (p.ProductName, (string?)p.ProjectCode))]), openTrains, BufferMinutes);
            var conflicts = ChangeConflictRules.Evaluate(new(c.Id, c.PlannedStartAt, c.PlannedEndAt, covering, [.. holidays.Select(h => new ConflictHoliday(h.Day, h.Name))], changes, windows));
            reports.Add(new(c.Id, c.ChangeKey, c.Status, s, e, true, null, BufferMinutes, conflicts));
        }
        return reports;
    }
}
