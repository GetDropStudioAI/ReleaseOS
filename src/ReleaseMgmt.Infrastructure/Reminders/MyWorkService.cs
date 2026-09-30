using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Reminders;

/// <summary>Who an item is mine through: "me" (owner is the user) or the owning team (the user is a member).</summary>
public sealed record WorkVia(string Kind, string? TeamId, string? TeamName);

public sealed record WorkTask(string Id, string Description, string GateId, string GateName, string TrainId, string TrainTitle, DateOnly DueOn, bool Overdue, WorkVia Via);
public sealed record WorkGate(string Id, string Name, string Status, DateOnly DueOn, bool Overdue, int OpenTasks, string TrainId, string TrainTitle, WorkVia Via);
public sealed record WorkStep(string ExecutionId, string RunId, string RunMode, string StepId, string StepCode, string Title, string Section, string Status, DateTime PlannedStartAt, bool Late, string TrainId, string TrainTitle, WorkVia Via);
public sealed record WorkCondition(string Id, string Text, DateTime ExpiresAt, bool Expired, string DecisionId, string TrainId, string TrainTitle);
public sealed record WorkPirAction(string Id, string Text, DateOnly DueOn, bool Overdue, string PirId, string TrainId, string TrainTitle);
public sealed record WorkCounts(int Tasks, int Gates, int Steps, int Conditions, int PirActions, int Total, int Overdue);
public sealed record MyWork(WorkCounts Counts, IReadOnlyList<WorkTask> Tasks, IReadOnlyList<WorkGate> Gates, IReadOnlyList<WorkStep> Steps, IReadOnlyList<WorkCondition> Conditions, IReadOnlyList<WorkPirAction> PirActions);

/// <summary>
/// The My work queue (GET /me/work): what is open and mine, with due dates, across open trains. "Mine" is owned by the user or by a team the user belongs to.
/// Read-only; every list is sorted by due date, soonest first.
/// </summary>
public sealed class MyWorkService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IConfiguration config)
{
    private readonly DisplayClock _clock = DisplayClock.From(config);

    public async Task<MyWork> GetAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;
        var today = _clock.LocalDate(now);

        var teams = await (from m in db.Set<TeamMembers>().AsNoTracking() join t in db.Set<Teams>() on m.TeamId equals t.Id where m.UserId == userId select new { t.Id, t.Name }).ToListAsync(ct);
        var teamIds = teams.Select(t => t.Id).ToList();
        var teamName = teams.ToDictionary(t => t.Id, t => t.Name);
        WorkVia Via(string? ownerUserId, string? ownerTeamId) =>
            ownerUserId == userId ? new("me", null, null) : new("team", ownerTeamId, ownerTeamId is not null && teamName.TryGetValue(ownerTeamId, out var n) ? n : null);

        var open = new[] { "Planning", "Gated", "Executing" };

        var tasks = (await (from k in db.Set<ChecklistTasks>().AsNoTracking()
                            join g in db.Set<StageGates>() on k.StageGateId equals g.Id
                            join t in db.Set<ReleaseTrains>() on g.ReleaseTrainId equals t.Id
                            where !k.IsCompleted && open.Contains(t.CurrentStatus) && t.ArchivedAt == null
                                  && (k.OwnerUserId == userId || (k.OwnerTeamId != null && teamIds.Contains(k.OwnerTeamId)))
                            select new { k.Id, k.TaskDescription, k.OwnerUserId, k.OwnerTeamId, k.SequenceOrder, GateId = g.Id, g.GateName, g.DueOn, TrainId = t.Id, TrainTitle = t.Title }).ToListAsync(ct))
            .OrderBy(x => x.DueOn).ThenBy(x => x.TrainTitle, StringComparer.Ordinal).ThenBy(x => x.SequenceOrder)
            .Select(x => new WorkTask(x.Id, x.TaskDescription, x.GateId, x.GateName, x.TrainId, x.TrainTitle, x.DueOn, x.DueOn < today, Via(x.OwnerUserId, x.OwnerTeamId))).ToList();

        var gateRows = await (from g in db.Set<StageGates>().AsNoTracking()
                              join t in db.Set<ReleaseTrains>() on g.ReleaseTrainId equals t.Id
                              where (g.Status == "Pending" || g.Status == "InProgress" || g.Status == "Failed") && open.Contains(t.CurrentStatus) && t.ArchivedAt == null
                                    && (g.OwnerUserId == userId || (g.OwnerTeamId != null && teamIds.Contains(g.OwnerTeamId)))
                              select new { g.Id, g.GateName, g.Status, g.DueOn, g.OwnerUserId, g.OwnerTeamId, g.SequenceOrder, TrainId = t.Id, TrainTitle = t.Title }).ToListAsync(ct);
        var gateIds = gateRows.Select(g => g.Id).ToList();
        var openTaskCount = (await db.Set<ChecklistTasks>().AsNoTracking().Where(k => !k.IsCompleted && gateIds.Contains(k.StageGateId)).Select(k => k.StageGateId).ToListAsync(ct))
            .GroupBy(x => x).ToDictionary(x => x.Key, x => x.Count());
        var gates = gateRows.OrderBy(x => x.DueOn).ThenBy(x => x.TrainTitle, StringComparer.Ordinal).ThenBy(x => x.SequenceOrder)
            .Select(x => new WorkGate(x.Id, x.GateName, x.Status, x.DueOn, x.DueOn < today, openTaskCount.GetValueOrDefault(x.Id), x.TrainId, x.TrainTitle, Via(x.OwnerUserId, x.OwnerTeamId))).ToList();

        var stepRows = await (from e in db.Set<StepExecutions>().AsNoTracking()
                              join r in db.Set<RunbookRuns>() on e.RunId equals r.Id
                              join s in db.Set<RunbookSteps>() on e.StepId equals s.Id
                              join t in db.Set<ReleaseTrains>() on r.ReleaseTrainId equals t.Id
                              where r.EndedAt == null && (e.Status == "Scheduled" || e.Status == "Running")
                                    && (s.OwnerUserId == userId || (s.OwnerTeamId != null && teamIds.Contains(s.OwnerTeamId)))
                              select new { ExecId = e.Id, RunId = r.Id, r.Mode, StepId = s.Id, s.StepCode, s.Title, s.Section, e.Status, s.PlannedStartAt, s.OwnerUserId, s.OwnerTeamId, TrainId = t.Id, TrainTitle = t.Title }).ToListAsync(ct);
        var steps = stepRows.OrderBy(x => x.PlannedStartAt).ThenBy(x => x.StepCode, StringComparer.Ordinal)
            .Select(x => new WorkStep(x.ExecId, x.RunId, x.Mode, x.StepId, x.StepCode, x.Title, x.Section, x.Status, x.PlannedStartAt,
                x.Mode == "Live" && x.Status == "Scheduled" && now >= x.PlannedStartAt.AddMinutes(NotificationScheduler.StepWarnMin), x.TrainId, x.TrainTitle, Via(x.OwnerUserId, x.OwnerTeamId))).ToList();

        var latest = (await db.Set<GoNoGoDecisions>().AsNoTracking().Select(d => new { d.Id, d.ReleaseTrainId, d.DecidedAt }).ToListAsync(ct))
            .GroupBy(d => d.ReleaseTrainId).Select(g => g.MaxBy(x => x.DecidedAt)!.Id).ToList();
        var conditions = (await (from c in db.Set<GoNoGoConditions>().AsNoTracking()
                                 join d in db.Set<GoNoGoDecisions>() on c.DecisionId equals d.Id
                                 join t in db.Set<ReleaseTrains>() on d.ReleaseTrainId equals t.Id
                                 where c.OwnerUserId == userId && c.ClosedAt == null && latest.Contains(c.DecisionId) && open.Contains(t.CurrentStatus) && t.ArchivedAt == null
                                 select new { c.Id, c.Text, c.ExpiresAt, DecisionId = d.Id, TrainId = t.Id, TrainTitle = t.Title }).ToListAsync(ct))
            .OrderBy(x => x.ExpiresAt).Select(x => new WorkCondition(x.Id, x.Text, x.ExpiresAt, now >= x.ExpiresAt, x.DecisionId, x.TrainId, x.TrainTitle)).ToList();

        var pir = (await (from a in db.Set<PirActions>().AsNoTracking()
                          join p in db.Set<PostImplementationReviews>() on a.PirId equals p.Id
                          join t in db.Set<ReleaseTrains>() on p.ReleaseTrainId equals t.Id
                          where a.OwnerUserId == userId && a.DoneAt == null
                          select new { a.Id, a.Text, a.DueOn, PirId = p.Id, TrainId = t.Id, TrainTitle = t.Title }).ToListAsync(ct))
            .OrderBy(x => x.DueOn).Select(x => new WorkPirAction(x.Id, x.Text, x.DueOn, x.DueOn < today, x.PirId, x.TrainId, x.TrainTitle)).ToList();

        var overdue = tasks.Count(t => t.Overdue) + gates.Count(g => g.Overdue) + steps.Count(s => s.Late) + conditions.Count(c => c.Expired) + pir.Count(a => a.Overdue);
        var counts = new WorkCounts(tasks.Count, gates.Count, steps.Count, conditions.Count, pir.Count, tasks.Count + gates.Count + steps.Count + conditions.Count + pir.Count, overdue);
        return new MyWork(counts, tasks, gates, steps, conditions, pir);
    }
}
