using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

public sealed record NewFreeze(string Name, string? Kind, DateTime StartsAt, DateTime EndsAt, string? ProductPattern);
public sealed record NewOverride(string TrainId, string RequestedByUserId, string Reason, DateTime ExpiresAt);
public sealed record FreezeView(FreezeWindows Window, IReadOnlyList<FreezeOverrides> Overrides, bool Active, bool? CoversTrain, bool? TrainHasValidOverride);

/// <summary>
/// Freeze windows and their overrides (D29, D32). Overrides are immutable and renew by inserting a new row. An override records who asked
/// (RTE or Release Manager) and who approved (a different Release Manager or Governance Officer): the request itself is not stored (Q-034b),
/// it reaches the approvers as a notification. <see cref="FindBlockingAsync"/> is the service-side twin of trg_Step_FreezeLockout, so the
/// user gets a readable 422 first and the trigger stays the backstop.
/// </summary>
public sealed class FreezeService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IRealtimePublisher? realtime = null, INotifier? notifier = null, IAlertSink? alerts = null)
    : ServiceBase(dbf, time, realtime)
{
    public const int MinReasonLength = 20;
    public static readonly string[] Kinds = ["Freeze", "Chill"];
    private static readonly List<string> Approvers = [Roles.ReleaseManager, Roles.GovernanceOfficer];
    private static readonly List<string> Requesters = [Roles.RTE, Roles.ReleaseManager];

    /// <summary>The Freeze window that stops <paramref name="step"/> (a Live Deploy step) starting at <paramref name="at"/> for its train, or null. Same predicate as the trigger.</summary>
    public static async Task<FreezeWindows?> FindBlockingAsync(ReleaseDbContext db, RunbookSteps step, string mode, DateTime at, CancellationToken ct = default)
    {
        if (mode != "Live" || step.Section != "Deploy") return null;
        string? product = step.BundledProductId is null ? null : await db.Set<BundledProducts>().Where(p => p.Id == step.BundledProductId).Select(p => p.ProductName).SingleOrDefaultAsync(ct);
        var trainId = step.ReleaseTrainId;
        return await db.Set<FreezeWindows>().Where(f => f.Kind == "Freeze" && at >= f.StartsAt && at < f.EndsAt
                && (f.ProductPattern == null || (product != null && EF.Functions.Glob(product, f.ProductPattern)))
                && !db.Set<FreezeOverrides>().Any(o => o.FreezeWindowId == f.Id && o.ReleaseTrainId == trainId && o.ExpiresAt > at))
            .OrderBy(f => f.StartsAt).FirstOrDefaultAsync(ct);
    }

    public static GuardFailure LockoutFailure(FreezeWindows w, string stepCode) =>
        new(Guards.FreezeLockout, $"Step {stepCode} cannot start: freeze window '{w.Name}' is in force until {w.EndsAt:yyyy-MM-dd HH:mm}Z and this train has no unexpired override. A Release Manager or Governance Officer (not the requester) can grant one.");

    public async Task<List<FreezeView>> ListAsync(string? trainId = null, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var now = Now;
        var windows = await db.Set<FreezeWindows>().AsNoTracking().OrderByDescending(w => w.StartsAt).ToListAsync(ct);
        var ids = windows.Select(w => w.Id).ToList();
        var overrides = (await db.Set<FreezeOverrides>().AsNoTracking().Where(o => ids.Contains(o.FreezeWindowId)).OrderByDescending(o => o.ApprovedAt).ToListAsync(ct)).ToLookup(o => o.FreezeWindowId);
        var views = new List<FreezeView>();
        foreach (var w in windows)
        {
            bool? covers = null, valid = null;
            if (trainId is not null)
            {
                var pattern = w.ProductPattern;
                covers = pattern is null || await db.Set<BundledProducts>().AnyAsync(p => p.ReleaseTrainId == trainId && EF.Functions.Glob(p.ProductName, pattern), ct);
                valid = overrides[w.Id].Any(o => o.ReleaseTrainId == trainId && o.ExpiresAt > now);
            }
            views.Add(new(w, overrides[w.Id].ToList(), w.StartsAt <= now && now < w.EndsAt, covers, valid));
        }
        return views;
    }

    public Task<ServiceResult<FreezeWindows>> CreateAsync(NewFreeze n, Actor actor, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var f = new List<GuardFailure>();
            if (!Approvers.Contains(await RoleOf(db, actor.UserId) ?? "")) f.Add(new(Guards.FreezeCreateRole, "Freeze windows are created by a Release Manager or Governance Officer"));
            var kind = string.IsNullOrWhiteSpace(n.Kind) ? "Freeze" : n.Kind;
            var name = (n.Name ?? "").Trim();
            if (name.Length == 0) f.Add(new(Guards.FreezeInvalid, "A freeze window needs a name"));
            if (!Kinds.Contains(kind)) f.Add(new(Guards.FreezeInvalid, "Kind must be Freeze or Chill", Kinds));
            var starts = n.StartsAt.ToUniversalTime(); var ends = n.EndsAt.ToUniversalTime();
            if (ends <= starts) f.Add(new(Guards.FreezeInvalid, "A freeze window must end after it starts"));
            if (f.Count > 0) return ServiceResult<FreezeWindows>.Fail(f);

            var w = new FreezeWindows { Id = Ids.New(), Name = name, Kind = kind, StartsAt = starts, EndsAt = ends, ProductPattern = string.IsNullOrWhiteSpace(n.ProductPattern) ? null : n.ProductPattern.Trim(), CreatedByUserId = actor.UserId };
            db.Set<FreezeWindows>().Add(w);
            Audit(db, actor, null, "FreezeWindow", w.Id, "Create", null, new { w.Name, w.Kind, startsAt = w.StartsAt, endsAt = w.EndsAt, w.ProductPattern });
            await db.SaveChangesAsync(ct);
            return ServiceResult<FreezeWindows>.Ok(w);
        }, ct);

    /// <summary>Grant (or renew) an override: always inserts a new row. The actor is the approver and must differ from the requester.</summary>
    public Task<ServiceResult<FreezeOverrides>> GrantOverrideAsync(string windowId, NewOverride n, Actor actor, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var w = await db.Set<FreezeWindows>().SingleOrDefaultAsync(x => x.Id == windowId, ct);
            if (w is null) return ServiceResult<FreezeOverrides>.NotFound("freeze window");
            var train = await db.Set<ReleaseTrains>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == n.TrainId, ct);
            if (train is null) return ServiceResult<FreezeOverrides>.NotFound("train");

            var f = new List<GuardFailure>();
            var now = Now;
            var reason = (n.Reason ?? "").Trim();
            var expires = n.ExpiresAt.ToUniversalTime();
            if (reason.Length < MinReasonLength) f.Add(new(Guards.OverrideReason, $"An override needs a written reason of at least {MinReasonLength} characters"));
            if (expires <= now) f.Add(new(Guards.OverrideExpiry, "An override must expire in the future (no permanent exceptions)"));
            if (n.RequestedByUserId == actor.UserId) f.Add(new(Guards.OverrideSelfApproval, "An override needs two distinct people: you cannot approve your own request"));
            if (!Approvers.Contains(await RoleOf(db, actor.UserId) ?? "")) f.Add(new(Guards.OverrideApproverRole, "Overrides are approved by a Release Manager or Governance Officer"));
            var requester = await db.Set<Users>().AsNoTracking().SingleOrDefaultAsync(u => u.Id == n.RequestedByUserId, ct);
            if (requester is null || !requester.IsActive || !Requesters.Contains(requester.Role))
                f.Add(new(Guards.OverrideRequesterRole, "The requester must be an active RTE or Release Manager"));
            if (f.Count > 0) return ServiceResult<FreezeOverrides>.Fail(f);

            var renewal = await db.Set<FreezeOverrides>().AnyAsync(o => o.FreezeWindowId == windowId && o.ReleaseTrainId == n.TrainId, ct);
            var row = new FreezeOverrides { Id = Ids.New(), FreezeWindowId = windowId, ReleaseTrainId = n.TrainId, Reason = reason, RequestedByUserId = n.RequestedByUserId, ApprovedByUserId = actor.UserId, ApprovedAt = now, ExpiresAt = expires };
            db.Set<FreezeOverrides>().Add(row);
            Audit(db, actor, n.TrainId, "FreezeOverride", row.Id, renewal ? "Renew" : "Grant", null, new { windowId, window = w.Name, reason, requestedBy = row.RequestedByUserId, expiresAt = expires });
            await db.SaveChangesAsync(ct);
            return ServiceResult<FreezeOverrides>.Ok(row);
        }, ct);

    /// <summary>An RTE or Release Manager asks for an override. Nothing is stored beyond the audit row: the approvers are notified and one of them grants it (Q-034b).</summary>
    public async Task<ServiceResult<int>> RequestOverrideAsync(string windowId, string trainId, string reason, DateTime? proposedExpiry, Actor actor, CancellationToken ct = default)
    {
        List<string> approvers = [];
        var message = "";
        var r = await RunAsync(async db =>
        {
            var w = await db.Set<FreezeWindows>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == windowId, ct);
            if (w is null) return ServiceResult<int>.NotFound("freeze window");
            var t = await db.Set<ReleaseTrains>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == trainId, ct);
            if (t is null) return ServiceResult<int>.NotFound("train");
            var f = new List<GuardFailure>();
            var text = (reason ?? "").Trim();
            if (!Requesters.Contains(await RoleOf(db, actor.UserId) ?? "")) f.Add(new(Guards.OverrideRequesterRole, "An override is requested by an RTE or Release Manager"));
            if (text.Length < MinReasonLength) f.Add(new(Guards.OverrideReason, $"An override needs a written reason of at least {MinReasonLength} characters"));
            if (proposedExpiry is DateTime pe && pe.ToUniversalTime() <= Now) f.Add(new(Guards.OverrideExpiry, "An override must expire in the future (no permanent exceptions)"));
            if (f.Count > 0) return ServiceResult<int>.Fail(f);

            approvers = await db.Set<Users>().Where(u => u.IsActive && Approvers.Contains(u.Role) && u.Id != actor.UserId).Select(u => u.Id).ToListAsync(ct);
            var who = await db.Set<Users>().Where(u => u.Id == actor.UserId).Select(u => u.DisplayName).SingleAsync(ct);
            message = $"{who} asks for a freeze override on {t.Title} ({w.Name})" + (proposedExpiry is DateTime x ? $" until {x.ToUniversalTime():yyyy-MM-dd HH:mm}Z" : "") + $": {text}";
            Audit(db, actor, trainId, "FreezeWindow", w.Id, "RequestOverride", null, new { trainId, reason = text, proposedExpiry, notified = approvers.Count });
            await db.SaveChangesAsync(ct);
            return ServiceResult<int>.Ok(approvers.Count);
        }, ct);

        if (!r.IsOk || notifier is null) return r;
        foreach (var id in approvers)   // after the commit; a failed notification is raised, never swallowed (rule 8)
        {
            try { await notifier.NotifyAsync(new NotificationRequest(id, "FreezeOverrideRequested", "FreezeWindow", windowId, message, 1), ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (alerts is null) throw;
                await alerts.RaiseAsync("Notifications", "DeliveryFailed", $"freeze-request:{windowId}", $"Override request for window {windowId} was saved but notifying {id} failed: {ex.Message}", ct);
            }
        }
        return r;
    }
}
