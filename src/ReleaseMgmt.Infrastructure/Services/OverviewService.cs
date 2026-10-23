using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Reminders;

namespace ReleaseMgmt.Infrastructure.Services;

public sealed record ActiveReleasesCard(int Value, string Detail, int Planning, int Gated, int Executing, int Teams, int Products);
public sealed record RunningNowCard(int Value, string Detail, int WithLiveRun);
public sealed record NeedAttentionCard(int Value, string Detail, int ReadinessBlockers, int Approvals);
public sealed record DeploymentSuccessCard(double? Value, string Detail, int Completed, int Successful, int WithIssues, int Unsuccessful, double? PreviousValue, double? DeltaPts);
public sealed record OverviewCards(ActiveReleasesCard ActiveReleases, RunningNowCard RunningNow, NeedAttentionCard NeedAttention, DeploymentSuccessCard DeploymentSuccess);
/// <summary>One "Needs your attention" entry. Category is Approval, FailedGate or Conflict; Kind names the thing (see <see cref="OverviewService"/>).</summary>
public sealed record AttentionItem(string Category, string Kind, string Title, string Detail, string TrainId, string TrainTitle, string DueAt, string EntityId, string? ActionBy);
public sealed record DailyCompletions(string Date, int Successful, int WithIssues, int Unsuccessful);
public sealed record ActivityRow(string TrainId, string Title, string Status, string DisplayStatus, string? CloseCode, string RiskTier, string TargetReleaseDate,
    string? WindowStartsAt, string? WindowEndsAt, string? EndedAt, int Products, int Blockers);
public sealed record OverviewPayload(string From, string To, string AsOf, OverviewCards Cards, IReadOnlyList<AttentionItem> Attention,
    IReadOnlyList<DailyCompletions> Throughput, IReadOnlyList<ActivityRow> Activity);

/// <summary>
/// REOS-108 (B2): the Organization overview in one read, derived from existing tables (no schema change). Every number is defined in
/// docs/QUESTIONS.md Q-108a. In short:
/// <list type="bullet">
/// <item>Open train = Planning, Gated or Executing and not archived (as <see cref="AdvisoryService.OpenStatuses"/>).</item>
/// <item>Active releases = open trains. Running now = Executing open trains. Need attention = the readiness blockers of open trains (the Stream's
/// <c>blockers</c>, summed) plus the Approval items. Deployment success = Successful ÷ completed for trains completed in [from, to] by UTC date of
/// <c>ActualEndAt</c>, i.e. 100 − analytics M10's <c>change_fail_pct</c>.</item>
/// <item>Attention items are current state, not filtered by the range: approvals due (Compliance gates ready to certify, waivers to approve, open freeze-override
/// requests, Go/No-Go to record, Go conditions to close), failed gates, window collisions (REOS-85) and freeze conflicts, all on open trains, soonest due first.</item>
/// <item>Daily completions and the activity rows' closed trains use the same UTC-date range.</item>
/// </list>
/// A gate's due time is the end of its <c>DueOn</c> day in <c>Display:TimeZone</c> (business days are counted there). Read-only.
/// </summary>
public sealed class OverviewService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, TrainLifecycleService lifecycle, AdvisoryService advisories, DisplayClock clock)
{
    public const int DefaultDays = 14;
    public const int MaxRangeDays = 400;
    private const string Iso = "yyyy-MM-dd'T'HH:mm:ss'Z'";
    private static readonly string[] Closed = ["Certified", "Waived"];

    public async Task<OverviewPayload> GetAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var t0 = time.GetUtcNow().UtcDateTime;
        var now = new DateTime(t0.Ticks - t0.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

        var trains = await db.Set<ReleaseTrains>().AsNoTracking().ToListAsync(ct);
        var open = trains.Where(t => t.ArchivedAt == null && AdvisoryService.OpenStatuses.Contains(t.CurrentStatus)).ToList();
        var openById = open.ToDictionary(t => t.Id);
        var openIds = open.Select(t => t.Id).ToList();

        var gates = await db.Set<StageGates>().AsNoTracking().Where(g => openIds.Contains(g.ReleaseTrainId)).ToListAsync(ct);
        var gateIds = gates.Select(g => g.Id).ToList();
        var tasks = await db.Set<ChecklistTasks>().AsNoTracking().Where(k => gateIds.Contains(k.StageGateId))
            .Select(k => new { k.StageGateId, k.IsCompleted, k.OwnerTeamId }).ToListAsync(ct);
        var windows = await db.Set<DeploymentWindows>().AsNoTracking().Where(w => openIds.Contains(w.ReleaseTrainId)).ToDictionaryAsync(w => w.ReleaseTrainId, ct);
        var products = await db.Set<BundledProducts>().AsNoTracking().Where(p => openIds.Contains(p.ReleaseTrainId)).Select(p => new { p.ReleaseTrainId, p.ProductName }).ToListAsync(ct);
        var blockers = await lifecycle.NextHopBlockerCountsAsync(db, open, ct);

        var attention = new List<AttentionItem>();
        attention.AddRange(await ApprovalsAsync(db, open, openById, gates, tasks.Select(k => (k.StageGateId, k.IsCompleted)).ToList(), windows, now, ct));
        var approvals = attention.Count;
        attention.AddRange(gates.Where(g => g.Status == "Failed").Select(g => Item("FailedGate", "FailedGate", "Gate failed", $"{g.GateName} failed", openById[g.ReleaseTrainId], GateDue(g), g.Id, null)));
        attention.AddRange(await CollisionItemsAsync(db, openById, now, ct));
        attention.AddRange(await FreezeConflictsAsync(db, open, windows, now, ct));
        attention = [.. attention.OrderBy(a => a.DueAt, StringComparer.Ordinal).ThenBy(a => a.Kind, StringComparer.Ordinal).ThenBy(a => a.EntityId, StringComparer.Ordinal)];

        // Cards
        int Count(string s) => open.Count(t => t.CurrentStatus == s);
        var teams = gates.Where(g => g.OwnerTeamId != null).Select(g => g.OwnerTeamId!).Concat(tasks.Where(k => k.OwnerTeamId != null).Select(k => k.OwnerTeamId!)).Distinct().Count();
        var productCount = products.Select(p => p.ProductName.Trim().ToLowerInvariant()).Distinct().Count();
        var active = new ActiveReleasesCard(open.Count, $"Across {Plural(teams, "team")} · {Plural(productCount, "product")}", Count("Planning"), Count("Gated"), Count("Executing"), teams, productCount);

        var executingIds = open.Where(t => t.CurrentStatus == "Executing").Select(t => t.Id).ToList();
        var withLive = await db.Set<RunbookRuns>().AsNoTracking().Where(r => executingIds.Contains(r.ReleaseTrainId) && r.Mode == "Live" && r.EndedAt == null)
            .Select(r => r.ReleaseTrainId).Distinct().CountAsync(ct);
        var running = new RunningNowCard(executingIds.Count, executingIds.Count == 0 ? "Nothing executing" : $"{withLive} with a live run in progress", withLive);

        var blockerSum = blockers.Values.Sum();
        var need = new NeedAttentionCard(blockerSum + approvals, $"{Plural(blockerSum, "readiness blocker")} · {Plural(approvals, "approval")}", blockerSum, approvals);

        var (lo, hi) = (Start(from), Start(to.AddDays(1)));
        var completed = trains.Where(t => t.CurrentStatus == "Complete" && t.ActualEndAt >= lo && t.ActualEndAt < hi).ToList();
        var span = to.DayNumber - from.DayNumber + 1;
        var (plo, phi) = (Start(from.AddDays(-span)), lo);
        var previous = trains.Where(t => t.CurrentStatus == "Complete" && t.ActualEndAt >= plo && t.ActualEndAt < phi).ToList();
        double? pct = SuccessPct(completed), prev = SuccessPct(previous);
        double? delta = pct is double a && prev is double b ? Math.Round(a - b, 1, MidpointRounding.AwayFromZero) : null;
        var successDetail = pct is null ? "No releases completed in the range"
            : delta is double d ? $"{(d > 0 ? "↑" : d < 0 ? "↓" : "→")} {Math.Abs(d):0.0} pts vs. previous {Plural(span, "day")}"
            : $"{Plural(completed.Count, "release")} completed; none in the previous {Plural(span, "day")}";
        var success = new DeploymentSuccessCard(pct, successDetail, completed.Count, completed.Count(t => t.CloseCode == "Successful"),
            completed.Count(t => t.CloseCode == "SuccessfulWithIssues"), completed.Count(t => t.CloseCode == "Unsuccessful"), prev, delta);

        // Throughput: one entry per UTC day in [from, to], zeros included, so the chart has every bar
        var byDay = completed.ToLookup(t => DateOnly.FromDateTime(t.ActualEndAt!.Value));
        var throughput = Enumerable.Range(0, span).Select(i => from.AddDays(i)).Select(day => new DailyCompletions(day.ToString("yyyy-MM-dd"),
            byDay[day].Count(t => t.CloseCode == "Successful"), byDay[day].Count(t => t.CloseCode == "SuccessfulWithIssues"), byDay[day].Count(t => t.CloseCode == "Unsuccessful"))).ToList();

        // Activity: open trains (soonest window or target first), then trains closed in the range (latest first); archived trains never
        var failedTrains = gates.Where(g => g.Status == "Failed").Select(g => g.ReleaseTrainId).ToHashSet();
        var productsPerTrain = products.GroupBy(p => p.ReleaseTrainId).ToDictionary(g => g.Key, g => g.Count());
        var closedInRange = trains.Where(t => t.ArchivedAt == null && Ended(t) is DateTime e && e >= lo && e < hi).ToList();
        var closedIds = closedInRange.Select(t => t.Id).ToList();
        var closedProducts = await db.Set<BundledProducts>().AsNoTracking().Where(p => closedIds.Contains(p.ReleaseTrainId)).GroupBy(p => p.ReleaseTrainId)
            .Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var closedWindows = await db.Set<DeploymentWindows>().AsNoTracking().Where(w => closedIds.Contains(w.ReleaseTrainId)).ToDictionaryAsync(w => w.ReleaseTrainId, ct);
        ActivityRow Row(ReleaseTrains t, IReadOnlyDictionary<string, DeploymentWindows> ws, IReadOnlyDictionary<string, int> ps, int b)
        {
            ws.TryGetValue(t.Id, out var w);
            var display = t.CurrentStatus switch
            {
                "Executing" => "Running",
                "Planning" or "Gated" => failedTrains.Contains(t.Id) ? "Blocked" : "Planned",
                "Complete" => t.CloseCode == "Unsuccessful" ? "Failed" : "Successful",
                _ => "Failed",   // Aborted
            };
            return new ActivityRow(t.Id, t.Title, t.CurrentStatus, display, t.CloseCode, t.RiskTier, t.TargetReleaseDate.ToString("yyyy-MM-dd"),
                w?.StartsAt.ToString(Iso), w?.EndsAt.ToString(Iso), Ended(t)?.ToString(Iso), ps.GetValueOrDefault(t.Id), b);
        }
        var activity = open.OrderBy(t => windows.TryGetValue(t.Id, out var w) ? w.StartsAt : Start(t.TargetReleaseDate)).ThenBy(t => t.Title, StringComparer.Ordinal).ThenBy(t => t.Id, StringComparer.Ordinal)
            .Select(t => Row(t, windows, productsPerTrain, blockers[t.Id]))
            .Concat(closedInRange.OrderByDescending(t => Ended(t)).ThenBy(t => t.Id, StringComparer.Ordinal).Select(t => Row(t, closedWindows, closedProducts, 0))).ToList();

        return new OverviewPayload(from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"), now.ToString(Iso), new OverviewCards(active, running, need, success), attention, throughput, activity);
    }

    /// <summary>Same as analytics M10: 100 − round(100 × not-Successful ÷ completed, 1). Null when nothing completed.</summary>
    public static double? SuccessPct(IReadOnlyCollection<ReleaseTrains> completed) =>
        completed.Count == 0 ? null : 100 - Math.Round(100.0 * completed.Count(t => t.CloseCode != "Successful") / completed.Count, 1, MidpointRounding.AwayFromZero);

    /// <summary>When a closed train ended: Complete by <c>ActualEndAt</c>; Aborted, which has no end time, by its last change (the abort), as the Stream does.</summary>
    private static DateTime? Ended(ReleaseTrains t) => t.CurrentStatus switch { "Complete" => t.ActualEndAt, "Aborted" => t.UpdatedAt, _ => null };

    private static DateTime Start(DateOnly d) => d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
    private static string Plural(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";

    /// <summary>A gate (or anything dated by a business day) is due by the end of that day in Display:TimeZone.</summary>
    private string EndOfDay(DateOnly d) => clock.StartOfDayUtc(d.AddDays(1)).ToString(Iso);
    private string GateDue(StageGates g) => EndOfDay(g.DueOn);

    private static AttentionItem Item(string category, string kind, string title, string detail, ReleaseTrains t, string dueAt, string entityId, string? actionBy) =>
        new(category, kind, title, detail, t.Id, t.Title, dueAt, entityId, actionBy);

    private async Task<List<AttentionItem>> ApprovalsAsync(ReleaseDbContext db, List<ReleaseTrains> open, Dictionary<string, ReleaseTrains> byId, List<StageGates> gates,
        List<(string GateId, bool Done)> tasks, Dictionary<string, DeploymentWindows> windows, DateTime now, CancellationToken ct)
    {
        var items = new List<AttentionItem>();
        var taskLookup = tasks.ToLookup(k => k.GateId);
        var byTrain = gates.ToLookup(g => g.ReleaseTrainId);

        // Compliance gates waiting only for a Governance Officer: InProgress, at least one task and all done, every earlier gate Certified or Waived
        // (GateService.EvaluateCertifyAsync without the actor's own role and segregation-of-duties checks: those decide who, not whether)
        foreach (var g in gates.Where(g => g.GateClass == "Compliance" && g.Status == "InProgress"))
        {
            var ts = taskLookup[g.Id].ToList();
            if (ts.Count == 0 || ts.Any(k => !k.Done)) continue;
            if (byTrain[g.ReleaseTrainId].Any(p => p.SequenceOrder < g.SequenceOrder && !Closed.Contains(p.Status))) continue;
            items.Add(Item("Approval", "CertifyComplianceGate", "Compliance gate to certify", g.GateName, byId[g.ReleaseTrainId], GateDue(g), g.Id, "Governance Officer"));
        }

        // Waivers requested and not yet approved, on a gate that is still open
        var openGates = gates.Where(g => !Closed.Contains(g.Status)).ToDictionary(g => g.Id);
        var openGateIds = openGates.Keys.ToList();
        var waivers = await (from w in db.Set<GateWaivers>().AsNoTracking()
                             join u in db.Set<Users>().AsNoTracking() on w.RequestedByUserId equals u.Id
                             where w.ApprovedAt == null && openGateIds.Contains(w.StageGateId)
                             select new { w.Id, w.StageGateId, u.DisplayName }).ToListAsync(ct);
        foreach (var w in waivers)
        {
            var g = openGates[w.StageGateId];
            items.Add(Item("Approval", "ApproveWaiver", "Waiver to approve", $"{g.GateName} · requested by {w.DisplayName}", byId[g.ReleaseTrainId], GateDue(g), w.Id, "Governance Officer"));
        }

        items.AddRange(await OverrideRequestsAsync(db, byId, now, ct));

        // Go/No-Go to record: a Gated train whose latest decision is missing or NoGo. Due when its window opens, else by the end of its target date.
        var gated = open.Where(t => t.CurrentStatus == "Gated").Select(t => t.Id).ToList();
        var latest = new Dictionary<string, string>();
        foreach (var d in (await db.Set<GoNoGoDecisions>().AsNoTracking().Where(d => gated.Contains(d.ReleaseTrainId)).Select(d => new { d.ReleaseTrainId, d.Decision, d.DecidedAt }).ToListAsync(ct))
                 .OrderByDescending(d => d.DecidedAt))
            latest.TryAdd(d.ReleaseTrainId, d.Decision);
        foreach (var id in gated.Where(id => !latest.TryGetValue(id, out var d) || d == "NoGo"))
        {
            var t = byId[id];
            var due = windows.TryGetValue(id, out var w) ? w.StartsAt.ToString(Iso) : EndOfDay(t.TargetReleaseDate);
            items.Add(Item("Approval", "RecordGoNoGo", "Go/No-Go to record", latest.ContainsKey(id) ? "The last decision was No-Go" : "No decision recorded yet", t, due, id, "Release Manager"));
        }

        // Go conditions still open on an open train, due when they expire
        var openIds = byId.Keys.ToList();
        var conditions = await (from c in db.Set<GoNoGoConditions>().AsNoTracking()
                                join d in db.Set<GoNoGoDecisions>().AsNoTracking() on c.DecisionId equals d.Id
                                where c.ClosedAt == null && openIds.Contains(d.ReleaseTrainId)
                                select new { c.Id, c.Text, c.ExpiresAt, d.ReleaseTrainId }).ToListAsync(ct);
        items.AddRange(conditions.Select(c => Item("Approval", "CloseCondition", "Go condition to close", c.Text, byId[c.ReleaseTrainId], c.ExpiresAt.ToString(Iso), c.Id, "Condition owner or Release Manager")));
        return items;
    }

    /// <summary>
    /// Open freeze-override requests across the workspace: a <c>RequestOverride</c> audit row not yet consumed by a later Grant/Renew naming the same requester for the
    /// same window and train (the rule FreezeService applies per window, SEC-A1), on an open train, for a freeze that has not ended. Due when the freeze starts.
    /// </summary>
    private static async Task<List<AttentionItem>> OverrideRequestsAsync(ReleaseDbContext db, Dictionary<string, ReleaseTrains> byId, DateTime now, CancellationToken ct)
    {
        var openIds = byId.Keys.ToList();
        var requests = await db.Set<AuditEvents>().AsNoTracking()
            .Where(a => a.EntityType == "FreezeWindow" && a.Action == "RequestOverride" && a.ActorUserId != null && a.ReleaseTrainId != null && openIds.Contains(a.ReleaseTrainId))
            .GroupBy(a => new { a.EntityId, a.ReleaseTrainId, a.ActorUserId }).Select(g => new { g.Key.EntityId, g.Key.ReleaseTrainId, g.Key.ActorUserId, Last = g.Max(a => a.Id) }).ToListAsync(ct);
        if (requests.Count == 0) return [];
        var grants = await db.Set<AuditEvents>().AsNoTracking()
            .Where(a => a.EntityType == "FreezeOverride" && (a.Action == "Grant" || a.Action == "Renew") && a.AfterJson != null && a.ReleaseTrainId != null && openIds.Contains(a.ReleaseTrainId))
            .Select(a => new { a.Id, a.ReleaseTrainId, a.AfterJson }).ToListAsync(ct);
        var lastGrant = new Dictionary<(string Window, string Train, string User), long>();
        foreach (var g in grants)
        {
            using var doc = JsonDocument.Parse(g.AfterJson!);
            var after = doc.RootElement;
            if (after.TryGetProperty("windowId", out var win) && win.GetString() is { } w && after.TryGetProperty("requestedBy", out var by) && by.GetString() is { } u)
                lastGrant[(w, g.ReleaseTrainId!, u)] = Math.Max(lastGrant.GetValueOrDefault((w, g.ReleaseTrainId!, u)), g.Id);
        }
        var pending = requests.Where(r => r.Last > lastGrant.GetValueOrDefault((r.EntityId, r.ReleaseTrainId!, r.ActorUserId!))).ToList();
        var windowIds = pending.Select(r => r.EntityId).Distinct().ToList();
        var freezes = await db.Set<FreezeWindows>().AsNoTracking().Where(f => windowIds.Contains(f.Id)).ToDictionaryAsync(f => f.Id, ct);
        var userIds = pending.Select(r => r.ActorUserId!).Distinct().ToList();
        var names = await db.Set<Users>().AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        return [.. pending.Where(r => freezes.TryGetValue(r.EntityId, out var f) && f.EndsAt > now)
            .Select(r => { var f = freezes[r.EntityId]; return Item("Approval", "ApproveFreezeOverride", "Freeze override to approve",
                $"{names.GetValueOrDefault(r.ActorUserId!, "Someone")} asks to override {f.Name}", byId[r.ReleaseTrainId!], f.StartsAt.ToString(Iso), $"{f.Id}:{r.ActorUserId}", "Release Manager or Governance Officer (not the requester)"); })];
    }

    /// <summary>REOS-85 collisions, one item per pair of trains (on the train whose window opens first), while either window is still to end. Due when the first window opens.</summary>
    private async Task<List<AttentionItem>> CollisionItemsAsync(ReleaseDbContext db, Dictionary<string, ReleaseTrains> byId, DateTime now, CancellationToken ct)
    {
        var all = await advisories.CollisionsAsync(db, null, ct);
        var ids = all.Keys.ToList();
        var windows = await db.Set<DeploymentWindows>().AsNoTracking().Where(w => ids.Contains(w.ReleaseTrainId)).ToDictionaryAsync(w => w.ReleaseTrainId, ct);
        var items = new List<AttentionItem>();
        foreach (var (id, list) in all)
        {
            if (!byId.TryGetValue(id, out var t)) continue;
            var mine = windows[id];
            foreach (var c in list)
            {
                // the pair is reported once: by the train whose window starts first (ties by id)
                if (c.OtherStartsAt < mine.StartsAt || (c.OtherStartsAt == mine.StartsAt && string.CompareOrdinal(c.OtherTrainId, id) < 0)) continue;
                if (mine.EndsAt <= now && c.OtherEndsAt <= now) continue;
                var shared = string.Join(", ", c.SharedCis.Select(x => $"CI {x}").Concat(c.SharedProducts));
                items.Add(Item("Conflict", "WindowCollision", "Change window conflict", $"Overlaps {c.OtherTitle} · {shared}", t, mine.StartsAt.ToString(Iso), $"{id}:{c.OtherTrainId}", null));
            }
        }
        return items;
    }

    /// <summary>
    /// An open train whose deployment window overlaps a Freeze (not a Chill: that only warns) covering the train (no product pattern, or one of its products matches it),
    /// where the overlap has not ended and no override for that freeze and train lasts to the end of the overlap. Due when the overlap starts.
    /// </summary>
    private static async Task<List<AttentionItem>> FreezeConflictsAsync(ReleaseDbContext db, List<ReleaseTrains> open, Dictionary<string, DeploymentWindows> windows, DateTime now, CancellationToken ct)
    {
        var freezes = await db.Set<FreezeWindows>().AsNoTracking().Where(f => f.Kind == "Freeze" && f.EndsAt > now).ToListAsync(ct);
        if (freezes.Count == 0) return [];
        var items = new List<AttentionItem>();
        foreach (var t in open)
        {
            if (!windows.TryGetValue(t.Id, out var w)) continue;
            foreach (var f in freezes)
            {
                var (start, end) = (w.StartsAt > f.StartsAt ? w.StartsAt : f.StartsAt, w.EndsAt < f.EndsAt ? w.EndsAt : f.EndsAt);
                if (start >= end || end <= now) continue;
                var pattern = f.ProductPattern;
                if (pattern is not null && !await db.Set<BundledProducts>().AnyAsync(p => p.ReleaseTrainId == t.Id && EF.Functions.Glob(p.ProductName, pattern), ct)) continue;
                if (await db.Set<FreezeOverrides>().AnyAsync(o => o.FreezeWindowId == f.Id && o.ReleaseTrainId == t.Id && o.ExpiresAt >= end, ct)) continue;
                items.Add(Item("Conflict", "FreezeConflict", "Freeze conflict", $"Window overlaps {f.Name} with no override", t, start.ToString(Iso), $"{t.Id}:{f.Id}", null));
            }
        }
        return items;
    }
}
