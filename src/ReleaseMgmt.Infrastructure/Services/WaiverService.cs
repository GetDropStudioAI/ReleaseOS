using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

/// <summary>Waiver request and approval (D10): written reason of at least 20 characters, approved by a different Governance Officer.</summary>
public sealed class WaiverService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time) : ServiceBase(dbf, time)
{
    public const int MinReasonLength = 20;

    public Task<ServiceResult<GateWaivers>> RequestAsync(string gateId, string reason, Actor actor, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var g = await db.Set<StageGates>().SingleOrDefaultAsync(x => x.Id == gateId, ct);
            if (g is null) return ServiceResult<GateWaivers>.NotFound("gate");
            var text = (reason ?? "").Trim();
            if (text.Length < MinReasonLength)
                return ServiceResult<GateWaivers>.Fail(new GuardFailure(Guards.WaiverReason, $"A waiver needs a written reason of at least {MinReasonLength} characters"));
            var w = new GateWaivers { Id = Ids.New(), StageGateId = gateId, Reason = text, RequestedByUserId = actor.UserId, RequestedAt = Now };
            db.Set<GateWaivers>().Add(w);
            Audit(db, actor, g.ReleaseTrainId, "GateWaiver", w.Id, "Request", null, new { gateId, reason = w.Reason });
            await db.SaveChangesAsync(ct);
            return ServiceResult<GateWaivers>.Ok(w);
        }, ct);

    public Task<ServiceResult<GateWaivers>> ApproveAsync(string waiverId, Actor actor, int? expectedVersion = null, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var w = await db.Set<GateWaivers>().SingleOrDefaultAsync(x => x.Id == waiverId, ct);
            if (w is null) return ServiceResult<GateWaivers>.NotFound("waiver");
            if (VersionMismatch(expectedVersion, w.Version)) return ServiceResult<GateWaivers>.Conflict(w);
            var f = new List<GuardFailure>();
            if (w.ApprovedByUserId is not null) f.Add(new(Guards.WaiverAlreadyDecided, "Waiver is already approved"));
            if (w.RequestedByUserId == actor.UserId) f.Add(new(Guards.WaiverSelfApproval, "A waiver needs two distinct people: you cannot approve your own request"));
            if (await RoleOf(db, actor.UserId) != Roles.GovernanceOfficer) f.Add(new(Guards.WaiverApproverRole, "Waivers are approved by a Governance Officer"));
            if (f.Count > 0) return ServiceResult<GateWaivers>.Fail(f);

            var trainId = await db.Set<StageGates>().Where(g => g.Id == w.StageGateId).Select(g => g.ReleaseTrainId).SingleAsync(ct);
            w.ApprovedByUserId = actor.UserId; w.ApprovedAt = Now; w.Version++;
            Audit(db, actor, trainId, "GateWaiver", w.Id, "Approve", null, new { gateId = w.StageGateId });
            await db.SaveChangesAsync(ct);
            return ServiceResult<GateWaivers>.Ok(w);
        }, ct);
}
