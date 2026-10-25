using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Infrastructure.Analytics;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Reminders;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// REOS-121 acceptance: "the numbers match the underlying lists". Each executive metric (M16..M24), over all three workspaces of the portfolio scenario, is compared with the same figure counted
/// from the records themselves through EF (and, where the app already has a list, through that list: the Exceptions list for waivers and freeze overrides, <see cref="PirDue"/> for which reviews are
/// overdue). The oracle (seed_and_query.py) does the same with plain SQL; this proves the .NET path, the PIR due dates in the display zone included, agrees with the lists the screens show.
/// </summary>
public class ExecutiveCrossCheckTests(AnalyticsFixture fx) : IClassFixture<AnalyticsFixture>
{
    private static readonly DateOnly From = new(2026, 1, 1), To = new(2026, 12, 31);
    private static readonly DateTime Now = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string[] Portfolio = ["default", "ws-cards", "ws-pay"];
    private static readonly DisplayClock Clock = new(TimeZoneInfo.FindSystemTimeZoneById("America/Chicago"));
    private static readonly DateOnly Today = Clock.LocalDate(Now);

    private AnalyticsService Service => new(new SqliteAnalyticsConnectionFactory(fx.SeedCopy), new PirDueMap(Clock, 1));
    private AnalyticsWindow W(params string[] by) => new(From, To, Now) { Workspaces = Portfolio, GroupBy = by, LocalDay = Today };

    private sealed class Factory(string path) : IDbContextFactory<ReleaseDbContext>
    {
        public ReleaseDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ReleaseDbContext>().UseSqlite($"Data Source={path};Mode=ReadOnly;Pooling=False").Options);
    }

    private IDbContextFactory<ReleaseDbContext> Records => new Factory(fx.SeedCopy);
    private ReleaseDbContext Db() { using var _ = WorkspaceScope.HostWide(); return Records.CreateDbContext(); }

    private static bool InWindow(DateTime t) => DateOnly.FromDateTime(t) >= From && DateOnly.FromDateTime(t) <= To;
    private static bool InWindow(DateOnly d) => d >= From && d <= To;
    private static double Median(IEnumerable<double> xs) { var s = xs.Order().ToArray(); return s.Length == 0 ? double.NaN : s[(s.Length + 1) / 2 - 1]; }
    private static double P90(IEnumerable<double> xs) { var s = xs.Order().ToArray(); return s.Length == 0 ? double.NaN : s[Math.Max(1, (int)(0.9 * s.Length + 0.999)) - 1]; }
    private static void Near(double expected, double? actual, string what) { Assert.NotNull(actual); Assert.True(Math.Abs(expected - actual!.Value) <= 0.051, $"{what}: the records say {expected:0.###}, the metric says {actual}"); }

    private static string Track(string? type) => type switch { "CERT" => "CERT", "Production" => "Production", _ => "Other" };

    private async Task<(List<ReleaseTrains> Trains, Dictionary<string, string> Types)> LoadTrains(ReleaseDbContext db)
    {
        var types = await db.Set<ReleaseTypes>().IgnoreQueryFilters().ToDictionaryAsync(t => t.Id, t => t.Name);
        return (await db.Set<ReleaseTrains>().IgnoreQueryFilters().Where(t => Portfolio.Contains(t.WorkspaceId)).ToListAsync(), types);
    }

    [Fact]
    public async Task M16_time_to_restore_matches_the_rolled_back_live_runs()
    {
        using var db = Db();
        var trains = (await LoadTrains(db)).Trains.Select(t => t.Id).ToHashSet();
        var runs = await db.Set<RunbookRuns>().IgnoreQueryFilters().Where(r => r.Mode == "Live" && r.Outcome == "RolledBack" && r.EndedAt != null).ToListAsync();
        var fails = (await db.Set<StepExecutions>().IgnoreQueryFilters().Where(e => e.Status == "Failed").ToListAsync()).GroupBy(e => e.RunId).ToDictionary(g => g.Key, g => g.Min(e => e.ActualEndAt!.Value));
        var minutes = runs.Where(r => trains.Contains(r.ReleaseTrainId) && InWindow(r.EndedAt!.Value) && fails.ContainsKey(r.Id)).Select(r => (r.EndedAt!.Value - fails[r.Id]).TotalMinutes).ToList();
        Assert.NotEmpty(minutes);
        var row = Assert.Single(await Service.M16Async(W()));
        Assert.Equal(minutes.Count, row.Restores);
        Near(Median(minutes), row.MedianMin, "median minutes"); Near(minutes.Max(), row.MaxMin, "longest restore");
        Assert.True(runs.Count(r => r.Outcome == "RolledBack") >= minutes.Count);
    }

    [Fact]
    public async Task M17_predictability_matches_the_completed_releases_CERT_and_production_apart()
    {
        using var db = Db();
        var (trains, types) = await LoadTrains(db);
        var baselines = (await db.Set<Baselines>().IgnoreQueryFilters().ToListAsync()).ToDictionary(b => b.ReleaseTrainId);
        var done = trains.Where(t => t.CurrentStatus == "Complete" && InWindow(t.ActualEndAt!.Value)).ToList();
        var rows = await Service.M17Async(W());
        Assert.Equal(["CERT", "Other", "Production"], rows.Select(r => r.Track).Order().ToArray());
        foreach (var track in new[] { "CERT", "Production", "Other" })
        {
            var mine = done.Where(t => Track(t.ReleaseTypeId is null ? null : types[t.ReleaseTypeId]) == track).ToList();
            var slips = mine.Where(t => baselines.ContainsKey(t.Id)).Select(t => (double)(DateOnly.FromDateTime(t.ActualEndAt!.Value).DayNumber - baselines[t.Id].PlannedReleaseDate.DayNumber)).ToList();
            var row = rows.Single(r => r.Track == track);
            Assert.Equal(mine.Count, row.Completed);
            Assert.Equal(slips.Count, row.Measured);
            Assert.Equal(slips.Count(s => s <= 0), row.OnTime ?? 0);
            Assert.Equal(mine.Count(t => t.CloseCode != "Successful"), row.ChangeFailed ?? 0);
            if (slips.Count > 0) { Near(Math.Round(100.0 * slips.Count(s => s <= 0) / slips.Count, 1), row.OnTimePct, track + " on-time %"); Near(Median(slips), row.MedianSlipDays, track + " median slip"); Assert.Equal((long)slips.Max(), row.MaxSlipDays); }
            else { Assert.Null(row.OnTimePct); Assert.Null(row.MedianSlipDays); }
        }
        Assert.True(rows.Single(r => r.Track == "CERT").Completed > 0 && rows.Single(r => r.Track == "Production").Completed > 0);
        // by quarter, the rows add up to the same
        Assert.Equal(rows.Sum(r => r.Completed), (await Service.M17Async(W("quarter", "workspace"))).Sum(r => r.Completed));
    }

    [Fact]
    public async Task M18_exceptions_match_the_exceptions_list_the_audit_log_and_the_overdue_reviews()
    {
        using var db = Db();
        var (trains, _) = await LoadTrains(db);
        var ids = trains.Select(t => t.Id).ToHashSet();
        var rows = (await Service.M18Async(W())).ToDictionary(r => r.Exception);

        // the Exceptions list (waivers and freeze overrides), workspace by workspace as the screen reads it
        var waivers = 0; var overrides = 0;
        foreach (var ws in Portfolio)
        {
            using var _ = WorkspaceScope.Enter(ws);
            var list = new ExceptionService(Records, new FixedClock(Now));
            var all = (await list.ListAsync(take: 500)).Items;
            waivers += all.Count(i => i.Kind == ExceptionService.Waiver && i.State == "Approved" && InWindow(DateTime.Parse(i.DecidedAt!).ToUniversalTime()));
            overrides += all.Count(i => i.Kind == ExceptionService.FreezeOverride && i.State is "Approved" or "Expired" && i.DecidedAt is not null && InWindow(DateTime.Parse(i.DecidedAt).ToUniversalTime()));
        }
        Assert.True(waivers > 0 && overrides > 0);
        Assert.Equal(waivers, rows["Waiver granted"].Events);
        Assert.Equal(overrides, rows["Freeze override granted"].Events);

        var decerts = await db.Set<AuditEvents>().IgnoreQueryFilters().CountAsync(a => a.Action == "Decertified" && a.ReleaseTrainId != null && ids.Contains(a.ReleaseTrainId) && a.OccurredAt >= From.ToDateTime(TimeOnly.MinValue) && a.OccurredAt < To.AddDays(1).ToDateTime(TimeOnly.MinValue));
        Assert.True(decerts > 0);
        Assert.Equal(decerts, rows["Gate decertified"].Events);

        var gates = await db.Set<StageGates>().IgnoreQueryFilters().Where(g => g.GateClass == "Compliance" && g.CertifiedAt != null).ToListAsync();
        var late = gates.Count(g => ids.Contains(g.ReleaseTrainId) && InWindow(g.CertifiedAt!.Value) && DateOnly.FromDateTime(g.CertifiedAt!.Value) > g.DueOn);
        Assert.True(late > 0);
        Assert.Equal(late, rows["Compliance gate certified late"].Events);

        var decisions = (await db.Set<GoNoGoDecisions>().IgnoreQueryFilters().ToListAsync()).Where(d => ids.Contains(d.ReleaseTrainId)).ToDictionary(d => d.Id);
        var open = (await db.Set<GoNoGoConditions>().IgnoreQueryFilters().ToListAsync()).Count(c => decisions.ContainsKey(c.DecisionId) && c.ExpiresAt < Now && (c.ClosedAt == null || c.ClosedAt > c.ExpiresAt) && InWindow(c.ExpiresAt));
        Assert.True(open > 0);
        Assert.Equal(open, rows["Go condition open past expiry"].Events);

        // PIRs overdue: the reminders' own rule (PirDue)
        var overdue = 0;
        foreach (var ws in Portfolio)
        {
            using var _ = WorkspaceScope.Enter(ws);
            using var scoped = Records.CreateDbContext();
            var pirs = await scoped.Set<PostImplementationReviews>().ToListAsync();
            var due = await PirDue.DueDatesAsync(scoped, pirs, Clock, 1, default);
            overdue += pirs.Count(p => due.TryGetValue(p.Id, out var d) && PirDue.IsOverdue(p, d, Today) && InWindow(d));
        }
        Assert.True(overdue > 0);
        Assert.Equal(overdue, rows["PIR overdue"].Events);
    }

    [Fact]
    public async Task M19_approval_latency_matches_the_waivers_overrides_and_gates()
    {
        using var db = Db();
        var (trains, _) = await LoadTrains(db);
        var ids = trains.Select(t => t.Id).ToHashSet();
        var gates = (await db.Set<StageGates>().IgnoreQueryFilters().ToListAsync()).Where(g => ids.Contains(g.ReleaseTrainId)).ToDictionary(g => g.Id);
        var waiver = (await db.Set<GateWaivers>().IgnoreQueryFilters().ToListAsync()).Where(w => gates.ContainsKey(w.StageGateId) && w.ApprovedAt != null && InWindow(w.ApprovedAt.Value)).Select(w => (w.ApprovedAt!.Value - w.RequestedAt).TotalHours).ToList();
        var audit = await db.Set<AuditEvents>().IgnoreQueryFilters().Where(a => a.Action == "RequestOverride" && a.EntityType == "FreezeWindow").ToListAsync();
        var overrides = (await db.Set<FreezeOverrides>().IgnoreQueryFilters().ToListAsync()).Where(o => ids.Contains(o.ReleaseTrainId) && InWindow(o.ApprovedAt))
            .Select(o => audit.Where(a => a.EntityId == o.FreezeWindowId && a.ReleaseTrainId == o.ReleaseTrainId && a.OccurredAt <= o.ApprovedAt).Select(a => (DateTime?)a.OccurredAt).Max() is { } req ? (o.ApprovedAt - req).TotalHours : (double?)null)
            .Where(h => h is >= 0).Select(h => h!.Value).ToList();
        var tasks = (await db.Set<ChecklistTasks>().IgnoreQueryFilters().ToListAsync()).GroupBy(t => t.StageGateId).ToDictionary(g => g.Key, g => g.Max(t => t.CompletedAt));
        var certified = gates.Values.Where(g => g.Status == "Certified" && g.CertifiedAt != null && InWindow(g.CertifiedAt.Value) && tasks.GetValueOrDefault(g.Id) != null)
            .Select(g => (g.CertifiedAt!.Value - tasks[g.Id]!.Value).TotalHours).Where(h => h >= 0).ToList();

        var rows = (await Service.M19Async(W())).ToDictionary(r => r.Step);
        foreach (var (step, hours) in new[] { ("Waiver: requested to approved", waiver), ("Freeze override: requested to granted", overrides), ("Gate: last task done to certified", certified) })
        {
            Assert.NotEmpty(hours);
            Assert.Equal(hours.Count, rows[step].Samples);
            Near(Median(hours), rows[step].MedianH, step + " median"); Near(P90(hours), rows[step].P90H, step + " p90");
        }
    }

    [Fact]
    public async Task M20_slip_reasons_match_the_target_moves_after_the_baseline()
    {
        using var db = Db();
        var (trains, _) = await LoadTrains(db);
        var ids = trains.Select(t => t.Id).ToHashSet();
        var baselines = (await db.Set<Baselines>().IgnoreQueryFilters().ToListAsync()).ToDictionary(b => b.ReleaseTrainId);
        var nogo = (await db.Set<GoNoGoDecisions>().IgnoreQueryFilters().Where(d => d.Decision == "NoGo").ToListAsync());
        var moves = new List<(string Reason, int Days, string Train)>();
        foreach (var a in await db.Set<AuditEvents>().IgnoreQueryFilters().Where(a => a.Action == "ChangeTargetDate" && a.EntityType == "ReleaseTrain").ToListAsync())
        {
            if (a.ReleaseTrainId is null || !ids.Contains(a.ReleaseTrainId) || !baselines.TryGetValue(a.ReleaseTrainId, out var b) || a.OccurredAt < b.CapturedAt || !InWindow(a.OccurredAt)) continue;
            var before = DateOnly.Parse(JsonDocument.Parse(a.BeforeJson!).RootElement.GetProperty("target").GetString()!);
            var after = DateOnly.Parse(JsonDocument.Parse(a.AfterJson!).RootElement.GetProperty("target").GetString()!);
            moves.Add((nogo.Any(d => d.ReleaseTrainId == a.ReleaseTrainId && d.NewTargetReleaseDate == after) ? "No-Go decision" : "Not recorded", after.DayNumber - before.DayNumber, a.ReleaseTrainId));
        }
        var rows = (await Service.M20Async(W())).ToDictionary(r => r.Reason);
        Assert.Equal(["No-Go decision", "Not recorded"], rows.Keys.Order().ToArray());
        foreach (var reason in rows.Keys)
        {
            var mine = moves.Where(m => m.Reason == reason).ToList();
            Assert.Equal(mine.Count, rows[reason].Moves);
            Assert.Equal(mine.Select(m => m.Train).Distinct().Count(), rows[reason].Releases);
            Assert.Equal(mine.Where(m => m.Days > 0).Sum(m => m.Days), rows[reason].DaysLater ?? 0);
            Assert.Equal(mine.Where(m => m.Days < 0).Sum(m => -m.Days), rows[reason].DaysEarlier ?? 0);
        }
    }

    [Fact]
    public async Task M21_risk_exposure_matches_the_open_releases_and_their_blockers()
    {
        using var db = Db();
        var (trains, _) = await LoadTrains(db);
        var open = trains.Where(t => t.CurrentStatus is not ("Complete" or "Aborted") && t.ArchivedAt == null).ToList();
        var blockers = (await db.Set<Blockers>().IgnoreQueryFilters().Where(b => b.ResolvedAt == null).ToListAsync()).GroupBy(b => b.ReleaseTrainId).ToDictionary(g => g.Key, g => g.Count());
        var rows = (await Service.M21Async(W())).ToDictionary(r => r.RiskTier);
        Assert.Equal(open.Select(t => t.RiskTier).Distinct().Order().ToArray(), rows.Keys.Order().ToArray());
        foreach (var tier in rows.Keys)
        {
            var mine = open.Where(t => t.RiskTier == tier).ToList();
            var row = rows[tier];
            Assert.Equal(mine.Count, row.OpenReleases);
            Assert.Equal(mine.Min(t => t.TargetReleaseDate.DayNumber - DateOnly.FromDateTime(Now).DayNumber), (int)row.NearestDays!);
            Assert.Equal(mine.Count(t => t.TargetReleaseDate < DateOnly.FromDateTime(Now)), row.PastTarget ?? 0);
            Assert.Equal(mine.Count(t => blockers.ContainsKey(t.Id)), row.WithBlockers ?? 0);
            Assert.Equal(mine.Sum(t => blockers.GetValueOrDefault(t.Id)), row.OpenBlockers ?? 0);
            Assert.Equal(mine.Count(t => tier is "High" or "VeryHigh" && t.RollbackRehearsedAt == null), row.NoRehearsal ?? 0);
        }
        Assert.Contains(rows.Values, r => r.OpenBlockers > 0);
        Assert.Contains(rows.Values, r => r.NoRehearsal > 0);
    }

    [Fact]
    public async Task M22_throughput_by_group_matches_the_completed_releases_and_their_lead_time()
    {
        using var db = Db();
        var (trains, _) = await LoadTrains(db);
        var done = trains.Where(t => t.CurrentStatus == "Complete" && InWindow(t.ActualEndAt!.Value)).ToList();
        var row = Assert.Single(await Service.M22Async(W()));
        Assert.Equal(done.Count, row.Completed);
        var lead = done.Select(t => (t.ActualEndAt!.Value - t.CreatedAt).TotalDays).ToList();
        Near(Median(lead), row.MedianLeadDays, "median lead time"); Near(P90(lead), row.P90LeadDays, "90th percentile");
        // by workspace the rows are the workspaces' own lists
        foreach (var r in await Service.M22Async(W("workspace")))
        {
            var ws = r.Workspace switch { "Default" => "default", "Cards" => "ws-cards", _ => "ws-pay" };
            Assert.Equal(done.Count(t => t.WorkspaceId == ws), r.Completed);
        }
        // the monthly throughput of the classic M15 and this agree for the default workspace
        var m15 = (await Service.M15Async(W() with { Workspaces = ["default"] })).Sum(r => r.Completed);
        Assert.Equal(m15, (await Service.M22Async(W() with { Workspaces = ["default"] })).Sum(r => r.Completed));
    }

    [Fact]
    public async Task M23_deployment_success_matches_the_deployments_and_their_breaches()
    {
        using var db = Db();
        var (trains, _) = await LoadTrains(db);
        var ids = trains.Select(t => t.Id).ToHashSet();
        var deps = (await db.Set<Deployments>().IgnoreQueryFilters().ToListAsync()).Where(d => d.ReleaseTrainId != null && ids.Contains(d.ReleaseTrainId) && d.FinishedAt != null && InWindow(d.FinishedAt.Value)).ToList();
        var depIds = deps.Select(d => d.Id).ToHashSet();
        var breaches = (await db.Set<ThresholdBreaches>().IgnoreQueryFilters().ToListAsync()).Where(b => depIds.Contains(b.DeploymentId) && b.ConfirmedAt != null).ToList();
        var row = Assert.Single(await Service.M23Async(W()));
        Assert.Equal(deps.Count, row.Deployments);
        Assert.Equal(deps.Count(d => d.State == "Successful"), row.Successful ?? 0);
        Assert.Equal(deps.Count(d => d.State == "Failed"), row.Failed ?? 0);
        Assert.Equal(deps.Count(d => d.State == "RolledBack"), row.RolledBack ?? 0);
        Near(Math.Round(100.0 * deps.Count(d => d.State == "Successful") / deps.Count, 1), row.SuccessPct, "success %");
        Assert.Equal(breaches.Count, row.Breaches ?? 0);
        Assert.Equal(breaches.Count(b => b.Metric == "Availability"), row.AvailabilityBreaches ?? 0);
        Assert.True(deps.Count > 0 && breaches.Count > 0);
    }

    [Fact]
    public async Task M24_pir_follow_through_matches_the_reviews_their_due_dates_and_actions()
    {
        using var db = Db();
        var (trains, _) = await LoadTrains(db);
        var byId = trains.ToDictionary(t => t.Id);
        var mine = new List<(PostImplementationReviews P, DateOnly? Due, ReleaseTrains T)>();
        foreach (var ws in Portfolio)
        {
            using var _ = WorkspaceScope.Enter(ws);
            using var scoped = Records.CreateDbContext();
            var pirs = await scoped.Set<PostImplementationReviews>().ToListAsync();
            var due = await PirDue.DueDatesAsync(scoped, pirs, Clock, 1, default);
            mine.AddRange(pirs.Where(p => InWindow(byId[p.ReleaseTrainId].ActualEndAt ?? byId[p.ReleaseTrainId].UpdatedAt)).Select(p => (p, due.TryGetValue(p.Id, out var d) ? (DateOnly?)d : null, byId[p.ReleaseTrainId])));
        }
        var actions = (await db.Set<PirActions>().IgnoreQueryFilters().ToListAsync()).GroupBy(a => a.PirId).ToDictionary(g => g.Key, g => g.ToList());
        var row = Assert.Single(await Service.M24Async(W()));
        Assert.Equal(mine.Count, row.Reviews);
        Assert.Equal(mine.Count(x => x.Due != null), row.WithDueDate ?? 0);
        Assert.Equal(mine.Count(x => x.P.Status is "Held" or "Closed"), row.Held ?? 0);
        Assert.Equal(mine.Count(x => x.Due != null && x.P.HeldAt != null && DateOnly.FromDateTime(x.P.HeldAt.Value) <= x.Due), row.HeldByDue ?? 0);
        Assert.Equal(mine.Count(x => PirDue.IsOverdue(x.P, x.Due, Today)), row.Overdue ?? 0);
        var acts = mine.SelectMany(x => actions.GetValueOrDefault(x.P.Id) ?? []).ToList();
        Assert.Equal(acts.Count, row.Actions ?? 0);
        Assert.Equal(acts.Count(a => a.DoneAt != null), row.ActionsDone ?? 0);
        Assert.Equal(acts.Count(a => a.DoneAt == null && a.DueOn < Today), row.ActionsOverdue ?? 0);
        Assert.True(row.Held > 0 && row.Overdue > 0 && row.ActionsOverdue > 0 && row.ActionsDone > 0);
    }

    private sealed class FixedClock(DateTime now) : TimeProvider { public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(now, DateTimeKind.Utc)); }
}
