using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

/// <summary>Gate transitions (D26). Certified/Waived stamp the certifier; cascades (evidence lock, baseline, transition log) are trigger-written.</summary>
public sealed class GateService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time) : ServiceBase(dbf, time)
{
    /// <summary>Why <paramref name="actor"/> cannot certify the gate right now (empty = eligible). Drives the Inspector's disabled-with-reason line in M2.</summary>
    public async Task<List<GuardFailure>> EvaluateCertifyAsync(ReleaseDbContext db, StageGates g, string actorUserId)
    {
        var f = new List<GuardFailure>();
        if (!Transitions.GateLegal(g.Status, "Certified"))
        {
            f.Add(new(Guards.IllegalGateTransition, $"Illegal gate status transition {g.Status} -> Certified"));
            return f;
        }
        var tasks = await db.Set<ChecklistTasks>().Where(t => t.StageGateId == g.Id).ToListAsync();
        var open = tasks.Where(t => !t.IsCompleted).Select(t => t.TaskDescription).ToList();
        if (open.Count > 0) f.Add(new(Guards.GateOpenTasks, "Gate has open checklist tasks", open));
        if (g.GateClass == "Compliance" && tasks.Count == 0) f.Add(new(Guards.ComplianceNeedsTask, "Compliance gate needs at least one task"));
        f.AddRange(await EarlierGates(db, g));
        if (g.GateClass == "Compliance")
        {
            if (await RoleOf(db, actorUserId) != "GovernanceOfficer")
                f.Add(new(Guards.ComplianceCertifierRole, "Compliance gates are certified by Governance Officers only"));
            if (tasks.Any(t => t.CompletedByUserId == actorUserId))
                f.Add(new(Guards.SegregationOfDuties, "Segregation of duties: certifier completed a task in this gate"));
        }
        return f;
    }

    private static async Task<List<GuardFailure>> EarlierGates(ReleaseDbContext db, StageGates g)
    {
        var earlier = await db.Set<StageGates>().Where(p => p.ReleaseTrainId == g.ReleaseTrainId && p.SequenceOrder < g.SequenceOrder
                                                            && p.Status != "Certified" && p.Status != "Waived").Select(p => p.GateName).ToListAsync();
        return earlier.Count == 0 ? [] : [new(Guards.EarlierGateOpen, "An earlier gate is not certified", earlier)];
    }

    public Task<ServiceResult<StageGates>> StartAsync(string gateId, Actor actor, int? expectedVersion = null, CancellationToken ct = default) =>
        MoveAsync(gateId, "InProgress", "Start", actor, expectedVersion, ["Pending"], ct);

    public Task<ServiceResult<StageGates>> FailAsync(string gateId, Actor actor, int? expectedVersion = null, CancellationToken ct = default) =>
        MoveAsync(gateId, "Failed", "Fail", actor, expectedVersion, ["InProgress"], ct);

    /// <summary>Failed -> InProgress, or Certified -> InProgress (decertify). Waived is terminal.</summary>
    public Task<ServiceResult<StageGates>> ReopenAsync(string gateId, Actor actor, int? expectedVersion = null, CancellationToken ct = default) =>
        MoveAsync(gateId, "InProgress", "Reopen", actor, expectedVersion, ["Failed", "Certified"], ct);

    private Task<ServiceResult<StageGates>> MoveAsync(string gateId, string to, string action, Actor actor, int? expectedVersion, string[] allowedFrom, CancellationToken ct) =>
        RunAsync(async db =>
        {
            var g = await db.Set<StageGates>().SingleOrDefaultAsync(x => x.Id == gateId, ct);
            if (g is null) return ServiceResult<StageGates>.NotFound("gate");
            if (VersionMismatch(expectedVersion, g.Version)) return ServiceResult<StageGates>.Conflict(g);
            if (!Transitions.GateLegal(g.Status, to) || !allowedFrom.Contains(g.Status))
                return ServiceResult<StageGates>.Fail(new GuardFailure(Guards.IllegalGateTransition, $"Illegal gate status transition {g.Status} -> {to}"));
            var before = new { status = g.Status, version = g.Version };
            var now = Now;
            g.Status = to;
            g.CertifiedByUserId = null; g.CertifiedAt = null;   // Certified -> InProgress clears the certification (CHECK ties them to status)
            Stamp(g, actor, now);
            Audit(db, actor, g.ReleaseTrainId, "StageGate", g.Id, action, before, new { status = g.Status, version = g.Version });
            await db.SaveChangesAsync(ct);
            await db.Entry(g).ReloadAsync(ct);
            return ServiceResult<StageGates>.Ok(g);
        }, ct);

    public Task<ServiceResult<StageGates>> CertifyAsync(string gateId, Actor actor, int? expectedVersion = null, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var g = await db.Set<StageGates>().SingleOrDefaultAsync(x => x.Id == gateId, ct);
            if (g is null) return ServiceResult<StageGates>.NotFound("gate");
            if (VersionMismatch(expectedVersion, g.Version)) return ServiceResult<StageGates>.Conflict(g);
            var failures = await EvaluateCertifyAsync(db, g, actor.UserId);
            if (failures.Count > 0) return ServiceResult<StageGates>.Fail(failures);
            var before = new { status = g.Status, version = g.Version };
            var now = Now;
            g.Status = "Certified"; g.CertifiedByUserId = actor.UserId; g.CertifiedAt = now;
            Stamp(g, actor, now);
            Audit(db, actor, g.ReleaseTrainId, "StageGate", g.Id, "Certify", before, new { status = g.Status, version = g.Version });
            await db.SaveChangesAsync(ct);
            await db.Entry(g).ReloadAsync(ct);
            return ServiceResult<StageGates>.Ok(g);
        }, ct);

    /// <summary>Waive (D10): needs a waiver approved by a Governance Officer other than its requester. Waived is terminal and is not Certified.</summary>
    public Task<ServiceResult<StageGates>> WaiveAsync(string gateId, Actor actor, int? expectedVersion = null, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var g = await db.Set<StageGates>().SingleOrDefaultAsync(x => x.Id == gateId, ct);
            if (g is null) return ServiceResult<StageGates>.NotFound("gate");
            if (VersionMismatch(expectedVersion, g.Version)) return ServiceResult<StageGates>.Conflict(g);
            var f = new List<GuardFailure>();
            if (!Transitions.GateLegal(g.Status, "Waived")) f.Add(new(Guards.IllegalGateTransition, $"Illegal gate status transition {g.Status} -> Waived"));
            var waiver = await db.Set<GateWaivers>().Where(w => w.StageGateId == g.Id && w.ApprovedByUserId != null).OrderByDescending(w => w.ApprovedAt).FirstOrDefaultAsync(ct);
            if (waiver is null) f.Add(new(Guards.WaiverRequired, "Waiver requires a second approver"));
            f.AddRange(await EarlierGates(db, g));
            if (f.Count > 0) return ServiceResult<StageGates>.Fail(f);

            var before = new { status = g.Status, version = g.Version };
            var now = Now;
            g.Status = "Waived"; g.CertifiedByUserId = waiver!.ApprovedByUserId; g.CertifiedAt = now; // the approver stands in as the resolver; Compliance gates need a Governance Officer here
            Stamp(g, actor, now);
            Audit(db, actor, g.ReleaseTrainId, "StageGate", g.Id, "Waive", before, new { status = g.Status, version = g.Version, waiverId = waiver.Id });
            await db.SaveChangesAsync(ct);
            await db.Entry(g).ReloadAsync(ct);
            return ServiceResult<StageGates>.Ok(g);
        }, ct);

    private static void Stamp(StageGates g, Actor actor, DateTime now) { g.LastChangedByUserId = actor.UserId; g.LastChangedAt = now; g.Version++; }
}
