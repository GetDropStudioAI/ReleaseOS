using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Infrastructure.Reminders;

/// <summary>
/// Reminders and escalation (PROJECT_SCOPE 5.5) behind <see cref="INotifier"/>. A timer calls <see cref="ScanAsync"/> every
/// <c>Notifications:ScanSeconds</c> (default 30); the scan reads only the injected <see cref="TimeProvider"/>, so tests drive it with a fake clock.
///
/// Rules: gate entered InProgress (owner) / 50% of entry-to-DueOn (owner) / overdue level 1 (Release Managers) / +1 business day level 2 (RTEs) /
/// Live step not started 5 min (owner + RTEs, level 1) and 30 min (Release Managers, level 2) after its plan / Go/No-Go condition 1 h before expiry (owner) /
/// open condition past expiry (owner + Release Managers).
///
/// Idempotence: a notice is identified by (recipient, Kind, EntityType, EntityId, EscalationLevel), and the Notifications row itself is the record
/// that it was sent, so a scan never repeats one, a restart forgets nothing, and a recipient added later still gets theirs. Team webhooks go out with
/// the first in-app row of a notice. A failed rule raises an alert (IAlertSink + SyncAlerts) and is retried by the next scan; the other rules still run.
/// </summary>
public sealed class NotificationScheduler : BackgroundService
{
    public const int StepWarnMin = RunService.WarnLateMin, StepCriticalMin = RunService.CriticalLateMin;
    public static readonly TimeSpan ConditionLead = TimeSpan.FromHours(1);

    private readonly IDbContextFactory<ReleaseDbContext> _dbf;
    private readonly TimeProvider _time;
    private readonly INotifier _notifier;
    private readonly SyncAlertWriter _syncAlerts;
    private readonly IAlertSink _alerts;
    private readonly IConfiguration _config;
    private readonly ILogger<NotificationScheduler> _log;
    private readonly ITeamWebhookSender? _webhooks;
    private DisplayClock? _clock;

    public NotificationScheduler(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, INotifier notifier, SyncAlertWriter syncAlerts, IAlertSink alerts,
        IConfiguration config, ILogger<NotificationScheduler> log, ITeamWebhookSender? webhooks = null)
    {
        _dbf = dbf; _time = time; _notifier = notifier; _syncAlerts = syncAlerts; _alerts = alerts; _config = config; _log = log; _webhooks = webhooks;
    }

    private DisplayClock Clock => _clock ??= DisplayClock.From(_config);
    private DateTime Now { get { var t = _time.GetUtcNow().UtcDateTime; return new DateTime(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc); } }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var every = TimeSpan.FromSeconds(Math.Max(1, int.TryParse(_config["Notifications:ScanSeconds"], out var secs) ? secs : 30));
        using var timer = new PeriodicTimer(every, _time);
        try
        {
            // Wait first: notices are deduplicated from the Notifications table, so a restart loses nothing and startup stays quiet.
            while (await timer.WaitForNextTickAsync(stop)) await ScanAsync(stop);
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    /// <summary>One pass over every rule. Returns how many new in-app notifications it wrote.</summary>
    public async Task<int> ScanAsync(CancellationToken ct = default)
    {
        var fired = 0;
        var rules = new (string Name, Func<CancellationToken, Task<int>> Run)[]
        {
            ("gates", GateRulesAsync), ("steps", StepRulesAsync), ("conditions", ConditionRulesAsync),
        };
        foreach (var (name, run) in rules)
        {
            try { fired += await run(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { await RuleFailedAsync(name, ex, ct); }
        }
        return fired;
    }

    private async Task RuleFailedAsync(string rule, Exception ex, CancellationToken ct)
    {
        _log.LogError(ex, "Notification rule {Rule} failed", rule);
        var msg = $"Reminder and escalation rule '{rule}' failed: {ex.GetType().Name}: {ex.Message}";
        try
        {
            await _alerts.RaiseAsync("Notifications", "DeliveryFailed", "scheduler:" + rule, msg, ct);
            await _syncAlerts.RaiseAsync("Notifications", "DeliveryFailed", "scheduler:" + rule, msg, null, ct);
        }
        catch (Exception inner) when (inner is not OperationCanceledException)
        {
            _log.LogError(inner, "Could not record the alert for notification rule {Rule}", rule);   // the store itself is failing; the log is all that is left
        }
    }

    // ---- gates ----------------------------------------------------------------------------------------------------------------------------
    private sealed record GateRow(string Id, string Name, DateOnly DueOn, string Status, string? OwnerUserId, string? OwnerTeamId, DateTime? LastChangedAt, string TrainId, string TrainTitle);

    private async Task<int> GateRulesAsync(CancellationToken ct)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var gates = await (from g in db.Set<StageGates>()
                           join t in db.Set<ReleaseTrains>() on g.ReleaseTrainId equals t.Id
                           where g.Status != "Certified" && g.Status != "Waived" && t.ArchivedAt == null
                                 && (t.CurrentStatus == "Planning" || t.CurrentStatus == "Gated" || t.CurrentStatus == "Executing")
                           select new GateRow(g.Id, g.GateName, g.DueOn, g.Status, g.OwnerUserId, g.OwnerTeamId, g.LastChangedAt, t.Id, t.Title)).ToListAsync(ct);
        if (gates.Count == 0) return 0;

        var holidays = (await db.Set<Holidays>().Select(h => h.Day).ToListAsync(ct)).ToHashSet();
        var inProgress = gates.Where(g => g.Status == "InProgress").Select(g => g.Id).ToList();
        var entered = (await db.Set<GateTransitions>().Where(x => x.ToStatus == "InProgress" && inProgress.Contains(x.StageGateId)).Select(x => new { x.StageGateId, x.OccurredAt }).ToListAsync(ct))
            .GroupBy(x => x.StageGateId).ToDictionary(x => x.Key, x => x.Max(y => y.OccurredAt));

        var now = Now;
        var fired = 0;
        foreach (var g in gates)
        {
            var owner = await OwnerRecipientsAsync(db, g.OwnerUserId, g.OwnerTeamId, ct);
            var due = Clock.StartOfDayUtc(g.DueOn.AddDays(1));   // due at the end of DueOn in the display zone
            string Say(string what) => $"Gate '{g.Name}' on {g.TrainTitle} {what}";

            if (g.Status == "InProgress")
            {
                fired += await FireAsync(new("GateEntered", "StageGate", g.Id, 0, Say($"is now in progress (due {g.DueOn:yyyy-MM-dd})"), owner, g.OwnerTeamId, g.TrainId), ct);

                // 50% of the time from entry to DueOn; a gate entered after its due time skips straight to overdue.
                DateTime? entry = entered.TryGetValue(g.Id, out var e) ? e : g.LastChangedAt;
                if (entry is DateTime start && start < due && now < due && now >= start + (due - start) / 2)
                    fired += await FireAsync(new("GateReminder", "StageGate", g.Id, 0, Say($"is half way to its due date ({g.DueOn:yyyy-MM-dd})"), owner, g.OwnerTeamId, g.TrainId), ct);
            }

            if (now >= due)
            {
                fired += await FireAsync(new("GateOverdue", "StageGate", g.Id, 1, Say($"is overdue (was due {g.DueOn:yyyy-MM-dd}, still {g.Status})"),
                    await RoleRecipientsAsync(db, Roles.ReleaseManager, ct), g.OwnerTeamId, g.TrainId), ct);
                // Level 2 once a whole business day has passed after the due day (Q-037b): holidays and weekends do not count.
                var level2 = Clock.StartOfDayUtc(BusinessDays.AddBusinessDays(g.DueOn, 1, holidays).AddDays(1));
                if (now >= level2)
                    fired += await FireAsync(new("GateOverdue", "StageGate", g.Id, 2, Say($"is more than a business day overdue (was due {g.DueOn:yyyy-MM-dd}, still {g.Status})"),
                        await RoleRecipientsAsync(db, Roles.RTE, ct), g.OwnerTeamId, g.TrainId), ct);
            }
        }
        return fired;
    }

    // ---- steps (the timer half of Q-011: a step that has not started at all) ---------------------------------------------------------------
    private async Task<int> StepRulesAsync(CancellationToken ct)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var rows = await (from e in db.Set<StepExecutions>()
                          join r in db.Set<RunbookRuns>() on e.RunId equals r.Id
                          join s in db.Set<RunbookSteps>() on e.StepId equals s.Id
                          join t in db.Set<ReleaseTrains>() on r.ReleaseTrainId equals t.Id
                          where r.Mode == "Live" && r.EndedAt == null && e.Status == "Scheduled" && s.Section != "Rollback"
                          select new { e.Id, s.StepCode, StepTitle = s.Title, s.PlannedStartAt, s.OwnerUserId, s.OwnerTeamId, TrainId = t.Id, TrainTitle = t.Title }).ToListAsync(ct);

        var now = Now;
        var fired = 0;
        foreach (var s in rows)
        {
            var late = (int)Math.Floor((now - s.PlannedStartAt).TotalMinutes);
            if (late < StepWarnMin) continue;
            string Say(int lvl) => $"Step {s.StepCode} '{s.StepTitle}' on {s.TrainTitle} has not started {late} min after its plan" + (lvl == 2 ? " (critical)" : "");

            var first = await OwnerRecipientsAsync(db, s.OwnerUserId, s.OwnerTeamId, ct);
            first.UnionWith(await RoleRecipientsAsync(db, Roles.RTE, ct));
            fired += await FireAsync(new("StepLate", "StepExecution", s.Id, 1, Say(1), first, s.OwnerTeamId, s.TrainId), ct);
            if (late >= StepCriticalMin)
                fired += await FireAsync(new("StepLate", "StepExecution", s.Id, 2, Say(2), await RoleRecipientsAsync(db, Roles.ReleaseManager, ct), s.OwnerTeamId, s.TrainId), ct);
        }
        return fired;
    }

    // ---- Go/No-Go conditions (D29) ---------------------------------------------------------------------------------------------------------
    private async Task<int> ConditionRulesAsync(CancellationToken ct)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        // Only the latest decision's conditions count: a superseding decision starts a fresh list (Q-012).
        var latest = (await db.Set<GoNoGoDecisions>().Select(d => new { d.Id, d.ReleaseTrainId, d.DecidedAt }).ToListAsync(ct))
            .GroupBy(d => d.ReleaseTrainId).Select(g => g.MaxBy(x => x.DecidedAt)!.Id).ToList();
        if (latest.Count == 0) return 0;
        var rows = await (from c in db.Set<GoNoGoConditions>()
                          join d in db.Set<GoNoGoDecisions>() on c.DecisionId equals d.Id
                          join t in db.Set<ReleaseTrains>() on d.ReleaseTrainId equals t.Id
                          where c.ClosedAt == null && latest.Contains(c.DecisionId) && t.CurrentStatus != "Complete" && t.CurrentStatus != "Aborted" && t.ArchivedAt == null
                          select new { c.Id, c.Text, c.OwnerUserId, c.ExpiresAt, TrainId = t.Id, TrainTitle = t.Title }).ToListAsync(ct);

        var now = Now;
        var fired = 0;
        foreach (var c in rows)
        {
            if (now >= c.ExpiresAt)
            {
                var who = await RoleRecipientsAsync(db, Roles.ReleaseManager, ct);
                who.Add(c.OwnerUserId);
                fired += await FireAsync(new("ConditionExpired", "GoNoGoCondition", c.Id, 1,
                    $"Go/No-Go condition '{c.Text}' on {c.TrainTitle} expired at {c.ExpiresAt:yyyy-MM-dd HH:mm}Z and is still open", who, null, c.TrainId), ct);
            }
            else if (now >= c.ExpiresAt - ConditionLead)
            {
                fired += await FireAsync(new("ConditionExpiring", "GoNoGoCondition", c.Id, 0,
                    $"Go/No-Go condition '{c.Text}' on {c.TrainTitle} expires at {c.ExpiresAt:yyyy-MM-dd HH:mm}Z (within the hour)", [c.OwnerUserId], null, c.TrainId), ct);
            }
        }
        return fired;
    }

    // ---- delivery -------------------------------------------------------------------------------------------------------------------------
    private sealed record Notice(string Kind, string EntityType, string EntityId, int Level, string Message, HashSet<string> Recipients, string? TeamId, string TrainId);

    /// <summary>Writes the notice for every recipient that does not have it yet; the team webhook goes out with the notice's first in-app row.</summary>
    private async Task<int> FireAsync(Notice n, CancellationToken ct)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var have = (await db.Set<Notifications>().Where(x => x.Kind == n.Kind && x.EntityType == n.EntityType && x.EntityId == n.EntityId && x.EscalationLevel == n.Level)
            .Select(x => x.UserId).ToListAsync(ct)).ToHashSet();
        var todo = n.Recipients.Where(u => !have.Contains(u)).Order(StringComparer.Ordinal).ToList();
        if (todo.Count == 0) return 0;

        var firstOfNotice = have.Count == 0;
        foreach (var u in todo)
            await _notifier.NotifyAsync(new NotificationRequest(u, n.Kind, n.EntityType, n.EntityId, n.Message, n.Level), ct);
        if (firstOfNotice && n.TeamId is not null && _webhooks is not null)
            await _webhooks.SendAsync(new WebhookNotice(n.TeamId, n.Kind, n.EntityType, n.EntityId, n.Level, n.Message, n.TrainId), ct);
        return todo.Count;
    }

    private static async Task<HashSet<string>> OwnerRecipientsAsync(ReleaseDbContext db, string? userId, string? teamId, CancellationToken ct)
    {
        var ids = new HashSet<string>();
        if (userId is not null) ids.Add(userId);
        if (teamId is not null)
            foreach (var m in await (from tm in db.Set<TeamMembers>() join u in db.Set<Users>() on tm.UserId equals u.Id where tm.TeamId == teamId && u.IsActive select tm.UserId).ToListAsync(ct)) ids.Add(m);
        return ids;
    }

    private static async Task<HashSet<string>> RoleRecipientsAsync(ReleaseDbContext db, string role, CancellationToken ct) =>
        (await db.Set<Users>().Where(u => u.IsActive && u.Role == role).Select(u => u.Id).ToListAsync(ct)).ToHashSet();
}
