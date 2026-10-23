using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Reminders;

namespace ReleaseMgmt.Infrastructure.Services;

/// <summary>One decision waiting for the signed-in person. A gate or a waiver is due on a date; an override, a Go/No-Go or a condition at a time.</summary>
public sealed record ApprovalItem(string Kind, string Id, string Title, string? Detail, string TrainId, string TrainTitle, DateOnly? DueOn, DateTime? DueAt, bool Overdue, string? GateId = null);
public sealed record ApprovalCounts(int Certify, int Waivers, int Overrides, int GoNoGo, int Conditions, int Total);
public sealed record ApprovalQueue(ApprovalCounts Counts, IReadOnlyList<ApprovalItem> Items);

/// <summary>
/// The approval queue (B1 in docs/proposals/redesign-mapping.md; built with the app shell, REOS-94, whose Approvals badge reads <see cref="ApprovalCounts.Total"/>).
/// What waits for this person's decision, on open trains, with the same role and separation-of-duties rules as the action endpoints, so nothing is listed
/// that the person would then be refused:
/// <list type="bullet">
/// <item><c>CertifyGate</c>: a Compliance gate In progress whose tasks are all done (at least one), whose earlier gates are certified or waived, and in which the
/// person completed no task. Governance Officers (GateService, trg_Gate_ComplianceCertifierRole, trg_Gate_CertifierNotTaskAuthor).</item>
/// <item><c>ApproveWaiver</c>: a waiver not yet approved, requested by someone else, on a gate not yet passed. Governance Officers (WaiverService).</item>
/// <item><c>GrantOverride</c>: an open freeze-override request from someone else for a freeze that has not ended. Release Managers and Governance Officers (FreezeService).</item>
/// <item><c>RecordGoNoGo</c>: a Gated train whose latest decision is not a Go. Release Managers (GoNoGoService).</item>
/// <item><c>CloseCondition</c>: an open condition of the train's current decision owned by the person (as in My work).</item>
/// </list>
/// The role is the one held in the current workspace (REOS-105), and every row comes from that workspace. Read-only; sorted by due time, soonest first.
/// </summary>
public sealed class ApprovalQueueService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IConfiguration config)
{
    public const string CertifyGate = "CertifyGate", ApproveWaiver = "ApproveWaiver", GrantOverride = "GrantOverride", RecordGoNoGo = "RecordGoNoGo", CloseCondition = "CloseCondition";
    private static readonly string[] Open = ["Planning", "Gated", "Executing"];
    private readonly DisplayClock _clock = DisplayClock.From(config);

    public async Task<ApprovalQueue> GetAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;
        var today = _clock.LocalDate(now);
        var role = await db.Set<Users>().AsNoTracking().AnyAsync(u => u.Id == userId && u.IsActive, ct) ? await Memberships.RoleOf(db, userId, ct) : null;   // the role held in this workspace (REOS-105)
        var items = new List<ApprovalItem>();
        ApprovalItem OnDay(string kind, string id, string title, string? detail, string trainId, string trainTitle, DateOnly due, string? gateId = null) =>
            new(kind, id, title, detail, trainId, trainTitle, due, null, due < today, gateId);
        ApprovalItem AtTime(string kind, string id, string title, string? detail, string trainId, string trainTitle, DateTime due) =>
            new(kind, id, title, detail, trainId, trainTitle, null, due, due <= now);

        var trains = await db.Set<ReleaseTrains>().AsNoTracking().Where(t => Open.Contains(t.CurrentStatus) && t.ArchivedAt == null)
            .Select(t => new { t.Id, t.Title, t.CurrentStatus, t.TargetReleaseDate }).ToDictionaryAsync(t => t.Id, ct);
        var trainIds = trains.Keys.ToList();
        var windows = await db.Set<DeploymentWindows>().AsNoTracking().Where(w => trainIds.Contains(w.ReleaseTrainId)).ToDictionaryAsync(w => w.ReleaseTrainId, w => w.StartsAt, ct);

        if (role == Roles.GovernanceOfficer)
        {
            var gates = await db.Set<StageGates>().AsNoTracking().Where(g => trainIds.Contains(g.ReleaseTrainId)).ToListAsync(ct);
            var gateIds = gates.Select(g => g.Id).ToList();
            var tasks = (await db.Set<ChecklistTasks>().AsNoTracking().Where(k => gateIds.Contains(k.StageGateId))
                .Select(k => new { k.StageGateId, k.IsCompleted, k.CompletedByUserId }).ToListAsync(ct)).ToLookup(k => k.StageGateId);
            var passed = new[] { "Certified", "Waived" };
            foreach (var g in gates.Where(g => g.GateClass == "Compliance" && g.Status == "InProgress"))
            {
                var mine = tasks[g.Id].ToList();
                if (mine.Count == 0 || mine.Any(k => !k.IsCompleted) || mine.Any(k => k.CompletedByUserId == userId)) continue;
                if (gates.Any(p => p.ReleaseTrainId == g.ReleaseTrainId && p.SequenceOrder < g.SequenceOrder && !passed.Contains(p.Status))) continue;
                var t = trains[g.ReleaseTrainId];
                items.Add(OnDay(CertifyGate, g.Id, g.GateName, $"{mine.Count} of {mine.Count} tasks done", t.Id, t.Title, g.DueOn, g.Id));
            }
            var byId = gates.ToDictionary(g => g.Id);
            var waivers = await db.Set<GateWaivers>().AsNoTracking()
                .Where(w => w.ApprovedByUserId == null && w.RequestedByUserId != userId && gateIds.Contains(w.StageGateId)).ToListAsync(ct);
            foreach (var w in waivers.Where(w => !passed.Contains(byId[w.StageGateId].Status)))
            {
                var g = byId[w.StageGateId]; var t = trains[g.ReleaseTrainId];
                items.Add(OnDay(ApproveWaiver, w.Id, $"Waiver for {g.GateName}", w.Reason, t.Id, t.Title, g.DueOn, g.Id));
            }
        }

        if (role is Roles.ReleaseManager or Roles.GovernanceOfficer)
        {
            var requests = (await FreezeService.OpenRequestsAsync(db, null, null, ct)).Where(r => r.RequesterId != userId && trains.ContainsKey(r.TrainId)).ToList();
            var windowIds = requests.Select(r => r.WindowId).Distinct().ToList();
            var freezes = await db.Set<FreezeWindows>().AsNoTracking().Where(f => windowIds.Contains(f.Id) && f.EndsAt > now).ToDictionaryAsync(f => f.Id, ct);
            var requesterIds = requests.Select(r => r.RequesterId).Distinct().ToList();
            var names = await db.Set<Users>().AsNoTracking().Where(u => requesterIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
            foreach (var r in requests.Where(r => freezes.ContainsKey(r.WindowId)))
            {
                var f = freezes[r.WindowId]; var t = trains[r.TrainId];
                var due = windows.TryGetValue(t.Id, out var start) && start > f.StartsAt ? start : f.StartsAt;
                items.Add(AtTime(GrantOverride, $"{r.WindowId}:{r.TrainId}:{r.RequesterId}", $"Freeze override: {f.Name}", $"Requested by {names.GetValueOrDefault(r.RequesterId, "someone")}",
                    t.Id, t.Title, due));
            }
        }

        if (role == Roles.ReleaseManager)
        {
            var gated = trains.Values.Where(t => t.CurrentStatus == "Gated").Select(t => t.Id).ToList();
            var latest = (await db.Set<GoNoGoDecisions>().AsNoTracking().Where(d => gated.Contains(d.ReleaseTrainId)).Select(d => new { d.ReleaseTrainId, d.Decision, d.DecidedAt }).ToListAsync(ct))
                .GroupBy(d => d.ReleaseTrainId).ToDictionary(g => g.Key, g => g.MaxBy(d => d.DecidedAt)!.Decision);
            foreach (var id in gated.Where(id => latest.GetValueOrDefault(id) is not ("Go" or "GoWithConditions")))
            {
                var t = trains[id];
                var detail = latest.ContainsKey(id) ? "Last decision: No-go" : "No decision recorded";
                items.Add(windows.TryGetValue(id, out var start)
                    ? AtTime(RecordGoNoGo, id, "Go/No-Go decision", detail, t.Id, t.Title, start)
                    : OnDay(RecordGoNoGo, id, "Go/No-Go decision", detail, t.Id, t.Title, t.TargetReleaseDate));
            }
        }

        if (role is not null)
        {
            var latestIds = (await db.Set<GoNoGoDecisions>().AsNoTracking().Select(d => new { d.Id, d.ReleaseTrainId, d.DecidedAt }).ToListAsync(ct))
                .GroupBy(d => d.ReleaseTrainId).Select(g => g.MaxBy(x => x.DecidedAt)!.Id).ToList();
            var conditions = await (from c in db.Set<GoNoGoConditions>().AsNoTracking()
                                    join d in db.Set<GoNoGoDecisions>() on c.DecisionId equals d.Id
                                    where c.OwnerUserId == userId && c.ClosedAt == null && latestIds.Contains(c.DecisionId)
                                    select new { c.Id, c.Text, c.ExpiresAt, d.ReleaseTrainId }).ToListAsync(ct);
            foreach (var c in conditions.Where(c => trains.ContainsKey(c.ReleaseTrainId)))
            {
                var t = trains[c.ReleaseTrainId];
                items.Add(AtTime(CloseCondition, c.Id, "Close a Go condition", c.Text, t.Id, t.Title, c.ExpiresAt));
            }
        }

        // a date sorts at the start of that day in the display zone, so a gate due today comes before a condition expiring tonight
        DateTime Key(ApprovalItem i) => i.DueAt ?? TimeZoneInfo.ConvertTimeToUtc(i.DueOn!.Value.ToDateTime(TimeOnly.MinValue), _clock.Zone);
        var sorted = items.OrderBy(Key).ThenBy(i => i.TrainTitle, StringComparer.Ordinal).ThenBy(i => i.Title, StringComparer.Ordinal).ToList();
        int N(string kind) => sorted.Count(i => i.Kind == kind);
        return new ApprovalQueue(new ApprovalCounts(N(CertifyGate), N(ApproveWaiver), N(GrantOverride), N(RecordGoNoGo), N(CloseCondition), sorted.Count), sorted);
    }
}
