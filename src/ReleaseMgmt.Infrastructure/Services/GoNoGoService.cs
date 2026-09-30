using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

public sealed record NewCondition(string Text, string OwnerUserId, DateTime ExpiresAt);
public sealed record DecisionView(GoNoGoDecisions Decision, IReadOnlyList<GoNoGoConditions> Conditions);

/// <summary>
/// Go/No-Go record (D29). Decisions are immutable (trigger backstop); the latest one counts. Only a Release Manager records one.
/// A GoWithConditions decision carries conditions that must expire; an open condition past its expiry blocks Gated to Executing
/// (checked in <see cref="TrainLifecycleService"/>) and is closed by its owner or a Release Manager.
/// </summary>
public sealed class GoNoGoService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IRealtimePublisher? realtime = null) : ServiceBase(dbf, time, realtime)
{
    public static readonly string[] Decisions = ["Go", "NoGo", "GoWithConditions"];

    public async Task<List<DecisionView>> ListAsync(string trainId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var ds = await db.Set<GoNoGoDecisions>().Where(d => d.ReleaseTrainId == trainId).OrderByDescending(d => d.DecidedAt).ToListAsync(ct);
        var ids = ds.Select(d => d.Id).ToList();
        var cs = await db.Set<GoNoGoConditions>().Where(c => ids.Contains(c.DecisionId)).OrderBy(c => c.ExpiresAt).ToListAsync(ct);
        return ds.Select(d => new DecisionView(d, cs.Where(c => c.DecisionId == d.Id).ToList())).ToList();
    }

    public Task<ServiceResult<DecisionView>> RecordAsync(string trainId, string decision, string? notes, DateOnly? newTargetDate, IReadOnlyList<NewCondition>? conditions, Actor actor, int? expectedVersion = null, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var t = await db.Set<ReleaseTrains>().SingleOrDefaultAsync(x => x.Id == trainId, ct);
            if (t is null) return ServiceResult<DecisionView>.NotFound("train");
            if (VersionMismatch(expectedVersion, t.Version)) return ServiceResult<DecisionView>.Conflict(t);
            var f = new List<GuardFailure>();
            var conds = conditions ?? [];
            var now = Now;
            if (await RoleOf(db, actor.UserId) != Roles.ReleaseManager) f.Add(new(Guards.GoNoGoRole, "Only a Release Manager records the Go/No-Go decision"));
            if (!Decisions.Contains(decision)) f.Add(new(Guards.InvalidDecision, "Decision must be Go, NoGo or GoWithConditions", Decisions));
            if (t.CurrentStatus is "Complete" or "Aborted") f.Add(new(Guards.TrainClosed, $"The train is {t.CurrentStatus}; no further decisions"));
            if (decision == "GoWithConditions" && conds.Count == 0) f.Add(new(Guards.ConditionsRequired, "GoWithConditions needs at least one condition"));
            if (decision is "Go" or "NoGo" && conds.Count > 0) f.Add(new(Guards.ConditionsNotAllowed, "Conditions belong to a GoWithConditions decision"));
            foreach (var c in conds) f.AddRange(await ValidateConditionAsync(db, c, now, ct));
            if (f.Count > 0) return ServiceResult<DecisionView>.Fail(f);

            var lastAt = await db.Set<GoNoGoDecisions>().Where(x => x.ReleaseTrainId == trainId).MaxAsync(x => (DateTime?)x.DecidedAt, ct);
            if (lastAt is DateTime la && la >= now)   // the latest decision is picked by DecidedAt (also inside the trigger), so two in one second would be ambiguous
                return ServiceResult<DecisionView>.Fail(new GuardFailure(Guards.DecisionTooSoon, "A decision was recorded a moment ago; wait a second and record again"));

            var gates = await db.Set<StageGates>().Where(g => g.ReleaseTrainId == trainId).OrderBy(g => g.SequenceOrder).ToListAsync(ct);
            var snapshot = JsonSerializer.Serialize(new
            {
                trainStatus = t.CurrentStatus,
                gates = gates.Select(g => new { name = g.GateName, status = g.Status, dueOn = g.DueOn.ToString("yyyy-MM-dd"), requiredBefore = g.RequiredBeforeStatus }),
            }, Json);
            var d = new GoNoGoDecisions
            {
                Id = Ids.New(), ReleaseTrainId = trainId, Decision = decision, DecidedByUserId = actor.UserId, DecidedAt = now,
                GateSnapshotJson = snapshot, Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
                NewTargetReleaseDate = decision == "NoGo" ? newTargetDate : null,
            };
            db.Set<GoNoGoDecisions>().Add(d);
            await db.SaveChangesAsync(ct);   // the condition trigger reads the decision row
            var added = conds.Select(c => NewRow(d.Id, c)).ToList();
            db.Set<GoNoGoConditions>().AddRange(added);
            t.Version++; t.UpdatedAt = now; t.LastChangedByUserId = actor.UserId; t.LastChangedAt = now;
            Audit(db, actor, trainId, "GoNoGo", d.Id, "Record", null, new { decision, notes = d.Notes, conditions = conds.Count });
            await db.SaveChangesAsync(ct);
            return ServiceResult<DecisionView>.Ok(new(d, added));
        }, ct);

    public Task<ServiceResult<GoNoGoConditions>> AddConditionAsync(string decisionId, NewCondition c, Actor actor, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var d = await db.Set<GoNoGoDecisions>().SingleOrDefaultAsync(x => x.Id == decisionId, ct);
            if (d is null) return ServiceResult<GoNoGoConditions>.NotFound("decision");
            var f = new List<GuardFailure>();
            if (await RoleOf(db, actor.UserId) != Roles.ReleaseManager) f.Add(new(Guards.GoNoGoRole, "Only a Release Manager records the Go/No-Go decision"));
            if (d.Decision != "GoWithConditions") f.Add(new(Guards.ConditionsNotAllowed, "Conditions belong to a GoWithConditions decision"));
            var latest = await db.Set<GoNoGoDecisions>().Where(x => x.ReleaseTrainId == d.ReleaseTrainId).OrderByDescending(x => x.DecidedAt).Select(x => x.Id).FirstAsync(ct);
            if (latest != d.Id) f.Add(new(Guards.NotLatestDecision, "A newer decision has superseded this one"));
            f.AddRange(await ValidateConditionAsync(db, c, Now, ct));
            if (f.Count > 0) return ServiceResult<GoNoGoConditions>.Fail(f);

            var row = NewRow(d.Id, c);
            db.Set<GoNoGoConditions>().Add(row);
            await TouchTrainAsync(db, d.ReleaseTrainId, actor, ct);
            Audit(db, actor, d.ReleaseTrainId, "GoNoGoCondition", row.Id, "Add", null, new { row.Text, row.OwnerUserId, expiresAt = row.ExpiresAt });
            await db.SaveChangesAsync(ct);
            return ServiceResult<GoNoGoConditions>.Ok(row);
        }, ct);

    public Task<ServiceResult<GoNoGoConditions>> CloseConditionAsync(string conditionId, Actor actor, int? expectedVersion = null, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var c = await db.Set<GoNoGoConditions>().SingleOrDefaultAsync(x => x.Id == conditionId, ct);
            if (c is null) return ServiceResult<GoNoGoConditions>.NotFound("condition");
            if (VersionMismatch(expectedVersion, c.Version)) return ServiceResult<GoNoGoConditions>.Conflict(c);
            var trainId = await db.Set<GoNoGoDecisions>().Where(d => d.Id == c.DecisionId).Select(d => d.ReleaseTrainId).SingleAsync(ct);
            var f = new List<GuardFailure>();
            if (c.ClosedAt is not null) f.Add(new(Guards.ConditionClosed, "Condition is already closed"));
            if (c.OwnerUserId != actor.UserId && await RoleOf(db, actor.UserId) != Roles.ReleaseManager)
                f.Add(new(Guards.ConditionCloseRole, "A condition is closed by its owner or a Release Manager"));
            if (f.Count > 0) return ServiceResult<GoNoGoConditions>.Fail(f);

            c.ClosedAt = Now; c.ClosedByUserId = actor.UserId; c.Version++;
            await TouchTrainAsync(db, trainId, actor, ct);
            Audit(db, actor, trainId, "GoNoGoCondition", c.Id, "Close", null, new { c.Text });
            await db.SaveChangesAsync(ct);
            return ServiceResult<GoNoGoConditions>.Ok(c);
        }, ct);

    private static GoNoGoConditions NewRow(string decisionId, NewCondition c) =>
        new() { Id = Ids.New(), DecisionId = decisionId, Text = c.Text.Trim(), OwnerUserId = c.OwnerUserId, ExpiresAt = c.ExpiresAt };

    private async Task<List<GuardFailure>> ValidateConditionAsync(ReleaseDbContext db, NewCondition c, DateTime now, CancellationToken ct)
    {
        var f = new List<GuardFailure>();
        if (string.IsNullOrWhiteSpace(c.Text)) f.Add(new(Guards.InvalidCondition, "A condition needs text"));
        if (c.ExpiresAt <= now) f.Add(new(Guards.InvalidCondition, "A condition must expire in the future"));
        if (!await db.Set<Users>().AnyAsync(u => u.Id == c.OwnerUserId && u.IsActive, ct)) f.Add(new(Guards.InvalidCondition, "A condition needs an active owner"));
        return f;
    }

    /// <summary>Readiness changed: other open views of the train are stale, and the expiry trigger reads the service clock from the train.</summary>
    private async Task TouchTrainAsync(ReleaseDbContext db, string trainId, Actor actor, CancellationToken ct)
    {
        var t = await db.Set<ReleaseTrains>().SingleAsync(x => x.Id == trainId, ct);
        t.Version++; t.UpdatedAt = Now; t.LastChangedByUserId = actor.UserId; t.LastChangedAt = Now;
    }
}
