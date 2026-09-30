using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

public sealed record PirView(PostImplementationReviews? Pir, IReadOnlyList<PirActions> Actions);
public sealed record KnownIssuesView(string? HypercareExitAt, string? HypercareExitByUserId, IReadOnlyList<KnownIssues> Issues);
public sealed record RehearsalRunRow(string Id, DateTime StartedAt, DateTime? EndedAt, string? Outcome);
public sealed record RollbackAttestationView(bool Required, DateTime? RehearsedAt, string? RehearsedByUserId, string? RehearsedByName, IReadOnlyList<RehearsalRunRow> RehearsalRuns);
public sealed record NewPirAction(string Text, string OwnerUserId, DateOnly DueOn);
public sealed record PirActionPatch(string? Text, string? OwnerUserId, DateOnly? DueOn);
public sealed record NewKnownIssue(string Title, string Severity, string? Workaround, string? ExternalKey);
public sealed record KnownIssuePatch(string? Title, string? Severity, string? Workaround, string? ExternalKey);

/// <summary>
/// Close-out after a train completes: the post-implementation review and its actions, the hypercare known-issue log and the hypercare exit
/// (PROJECT_SCOPE section 6; Q-036a..Q-036f). The PIR row for a bad close code is created by the trigger; this service manages it from there.
/// Every write bumps the row's Version and writes one audit row in the same transaction. Status columns move only through actions.
/// </summary>
public sealed class CloseoutService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IRealtimePublisher? realtime = null) : ServiceBase(dbf, time, realtime)
{
    public static readonly string[] Severities = ["Critical", "High", "Medium", "Low"];
    private static readonly string[] CloseoutRoles = [Roles.RTE, Roles.ReleaseManager, Roles.GovernanceOfficer];

    // ---- reads ------------------------------------------------------------------------------------------------------
    public async Task<RollbackAttestationView?> GetAttestationAsync(string trainId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var t = await db.Set<ReleaseTrains>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == trainId, ct);
        if (t is null) return null;
        var by = t.RollbackRehearsedByUserId is null ? null : await db.Set<Users>().Where(u => u.Id == t.RollbackRehearsedByUserId).Select(u => u.DisplayName).SingleOrDefaultAsync(ct);
        var runs = await db.Set<RunbookRuns>().AsNoTracking().Where(r => r.ReleaseTrainId == trainId && r.Mode == "Rehearsal" && r.EndedAt != null)
            .OrderByDescending(r => r.StartedAt).Select(r => new RehearsalRunRow(r.Id, r.StartedAt, r.EndedAt, r.Outcome)).ToListAsync(ct);
        return new(t.RiskTier is "High" or "VeryHigh", t.RollbackRehearsedAt, t.RollbackRehearsedByUserId, by, runs);
    }

    public async Task<PirView?> GetPirAsync(string trainId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        if (!await db.Set<ReleaseTrains>().AnyAsync(t => t.Id == trainId, ct)) return null;
        var pir = await db.Set<PostImplementationReviews>().AsNoTracking().SingleOrDefaultAsync(p => p.ReleaseTrainId == trainId, ct);
        var actions = pir is null ? [] : await db.Set<PirActions>().AsNoTracking().Where(a => a.PirId == pir.Id).OrderBy(a => a.DueOn).ThenBy(a => a.Id).ToListAsync(ct);
        return new(pir, actions);
    }

    public async Task<KnownIssuesView?> GetKnownIssuesAsync(string trainId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var t = await db.Set<ReleaseTrains>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == trainId, ct);
        if (t is null) return null;
        var issues = await db.Set<KnownIssues>().AsNoTracking().Where(i => i.ReleaseTrainId == trainId).OrderBy(i => i.RaisedAt).ThenBy(i => i.Id).ToListAsync(ct);
        // Critical first, then High ... (Severity is text; order in memory)
        issues = [.. issues.OrderBy(i => i.Status == "Resolved" ? 1 : 0).ThenBy(i => Array.IndexOf(Severities, i.Severity)).ThenBy(i => i.RaisedAt)];
        return new(t.HypercareExitAt?.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), t.HypercareExitByUserId, issues);
    }

    // ---- PIR --------------------------------------------------------------------------------------------------------
    /// <summary>Manual PIR (RequiredReason 'Manual') for a Complete train that has none, e.g. a Successful close that later went wrong.</summary>
    public Task<ServiceResult<PostImplementationReviews>> CreateManualPirAsync(string trainId, Actor actor, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var t = await db.Set<ReleaseTrains>().SingleOrDefaultAsync(x => x.Id == trainId, ct);
            if (t is null) return ServiceResult<PostImplementationReviews>.NotFound("train");
            var f = await RoleFailure<PostImplementationReviews>(db, actor);
            if (f is not null) return f;
            if (t.CurrentStatus != "Complete") return ServiceResult<PostImplementationReviews>.Fail(new GuardFailure(CloseoutGuards.PirRequiresComplete, "A PIR is held after the train is Complete"));
            if (await db.Set<PostImplementationReviews>().AnyAsync(p => p.ReleaseTrainId == trainId, ct))
                return ServiceResult<PostImplementationReviews>.Fail(new GuardFailure(CloseoutGuards.PirAlreadyExists, "This train already has a PIR"));
            var p = new PostImplementationReviews { Id = Ids.New(), ReleaseTrainId = trainId, RequiredReason = "Manual", Status = "Required" };
            db.Set<PostImplementationReviews>().Add(p);
            Audit(db, actor, trainId, "PostImplementationReview", p.Id, "CreateManual", null, new { reason = p.RequiredReason });
            await db.SaveChangesAsync(ct);
            return ServiceResult<PostImplementationReviews>.Ok(p);
        }, ct);

    /// <summary>Edit the summary text (the only free field). A closed PIR is read-only. Status moves through Schedule/Hold/Close.</summary>
    public Task<ServiceResult<PostImplementationReviews>> UpdateSummaryAsync(string trainId, string? summary, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        MutatePirAsync(trainId, actor, expectedVersion, "UpdateSummary", (_, p, _) =>
        {
            p.Summary = Clean(summary);
            return Task.FromResult<GuardFailure?>(null);
        }, ct);

    public Task<ServiceResult<PostImplementationReviews>> ScheduleAsync(string trainId, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        MutatePirAsync(trainId, actor, expectedVersion, "Schedule", (_, p, _) =>
        {
            if (p.Status != "Required") return Task.FromResult<GuardFailure?>(new(CloseoutGuards.IllegalPirTransition, $"Illegal PIR status transition {p.Status} -> Scheduled"));
            p.Status = "Scheduled";
            return Task.FromResult<GuardFailure?>(null);
        }, ct);

    /// <summary>The review took place: needs a summary (passed here or already saved). Required or Scheduled -> Held.</summary>
    public Task<ServiceResult<PostImplementationReviews>> HoldAsync(string trainId, string? summary, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        MutatePirAsync(trainId, actor, expectedVersion, "Hold", (_, p, now) =>
        {
            if (p.Status is not ("Required" or "Scheduled")) return Task.FromResult<GuardFailure?>(new(CloseoutGuards.IllegalPirTransition, $"Illegal PIR status transition {p.Status} -> Held"));
            if (!string.IsNullOrWhiteSpace(summary)) p.Summary = Clean(summary);
            if (string.IsNullOrWhiteSpace(p.Summary)) return Task.FromResult<GuardFailure?>(new(CloseoutGuards.PirSummaryRequired, "Record a summary of the review before marking it held"));
            p.Status = "Held"; p.HeldAt = now;
            return Task.FromResult<GuardFailure?>(null);
        }, ct);

    /// <summary>Held -> Closed once every action is done (Q-036c). A closed PIR is final.</summary>
    public Task<ServiceResult<PostImplementationReviews>> CloseAsync(string trainId, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        MutatePirAsync(trainId, actor, expectedVersion, "Close", async (db, p, _) =>
        {
            if (p.Status != "Held") return new(CloseoutGuards.IllegalPirTransition, $"Illegal PIR status transition {p.Status} -> Closed; hold the review first");
            var open = await db.Set<PirActions>().Where(a => a.PirId == p.Id && a.DoneAt == null).Select(a => a.Text).ToListAsync();
            if (open.Count > 0) return new(CloseoutGuards.PirActionsOpen, "Every PIR action must be done before the PIR is closed", open);
            p.Status = "Closed";
            return null;
        }, ct);

    private Task<ServiceResult<PostImplementationReviews>> MutatePirAsync(string trainId, Actor actor, int? expectedVersion, string action,
        Func<ReleaseDbContext, PostImplementationReviews, DateTime, Task<GuardFailure?>> apply, CancellationToken ct) =>
        RunAsync(async db =>
        {
            var p = await db.Set<PostImplementationReviews>().SingleOrDefaultAsync(x => x.ReleaseTrainId == trainId, ct);
            if (p is null) return ServiceResult<PostImplementationReviews>.NotFound("PIR");
            if (VersionMismatch(expectedVersion, p.Version)) return ServiceResult<PostImplementationReviews>.Conflict(p);
            var role = await RoleFailure<PostImplementationReviews>(db, actor);
            if (role is not null) return role;
            if (p.Status == "Closed") return ServiceResult<PostImplementationReviews>.Fail(new GuardFailure(CloseoutGuards.PirClosed, "The PIR is closed and read-only"));
            var before = new { status = p.Status, summary = p.Summary, heldAt = p.HeldAt };
            var failure = await apply(db, p, Now);
            if (failure is not null) return ServiceResult<PostImplementationReviews>.Fail(failure);
            p.Version++;
            Audit(db, actor, trainId, "PostImplementationReview", p.Id, action, before, new { status = p.Status, summary = p.Summary, heldAt = p.HeldAt });
            await db.SaveChangesAsync(ct);
            return ServiceResult<PostImplementationReviews>.Ok(p);
        }, ct);

    // ---- PIR actions ------------------------------------------------------------------------------------------------
    public Task<ServiceResult<PirActions>> AddActionAsync(string trainId, NewPirAction a, Actor actor, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var p = await db.Set<PostImplementationReviews>().SingleOrDefaultAsync(x => x.ReleaseTrainId == trainId, ct);
            if (p is null) return ServiceResult<PirActions>.NotFound("PIR");
            var role = await RoleFailure<PirActions>(db, actor);
            if (role is not null) return role;
            if (p.Status == "Closed") return ServiceResult<PirActions>.Fail(new GuardFailure(CloseoutGuards.PirClosed, "The PIR is closed; actions can no longer be added"));
            var f = await ValidateActionAsync(db, a.Text, a.OwnerUserId, a.DueOn, ct);
            if (f is not null) return ServiceResult<PirActions>.Fail(f);
            var row = new PirActions { Id = Ids.New(), PirId = p.Id, Text = a.Text.Trim(), OwnerUserId = a.OwnerUserId, DueOn = a.DueOn };
            db.Set<PirActions>().Add(row);
            Audit(db, actor, trainId, "PirAction", row.Id, "Add", null, new { row.Text, row.OwnerUserId, dueOn = row.DueOn.ToString("yyyy-MM-dd") });
            await db.SaveChangesAsync(ct);
            return ServiceResult<PirActions>.Ok(row);
        }, ct);

    /// <summary>Change the wording, owner or due date of an open action.</summary>
    public Task<ServiceResult<PirActions>> UpdateActionAsync(string actionId, PirActionPatch patch, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        MutateActionAsync(actionId, actor, expectedVersion, "Update", requirePlanner: true, async (db, a, _) =>
        {
            if (a.DoneAt is not null) return new(CloseoutGuards.PirActionDone, "A done action is not edited; reopen it first");
            var f = await ValidateActionAsync(db, patch.Text ?? a.Text, patch.OwnerUserId ?? a.OwnerUserId, patch.DueOn ?? a.DueOn, ct, dueMustBeFuture: patch.DueOn is not null);
            if (f is not null) return f;
            a.Text = (patch.Text ?? a.Text).Trim(); a.OwnerUserId = patch.OwnerUserId ?? a.OwnerUserId; a.DueOn = patch.DueOn ?? a.DueOn;
            return null;
        }, ct);

    /// <summary>The owner or a closeout role marks it done.</summary>
    public Task<ServiceResult<PirActions>> CompleteActionAsync(string actionId, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        MutateActionAsync(actionId, actor, expectedVersion, "Complete", requirePlanner: false, (_, a, now) =>
        {
            if (a.DoneAt is not null) return Task.FromResult<GuardFailure?>(new(CloseoutGuards.PirActionDone, "The action is already done"));
            a.DoneAt = now;
            return Task.FromResult<GuardFailure?>(null);
        }, ct);

    public Task<ServiceResult<PirActions>> ReopenActionAsync(string actionId, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        MutateActionAsync(actionId, actor, expectedVersion, "Reopen", requirePlanner: false, (_, a, _) =>
        {
            if (a.DoneAt is null) return Task.FromResult<GuardFailure?>(new(CloseoutGuards.PirActionNotDone, "The action is not done"));
            a.DoneAt = null;
            return Task.FromResult<GuardFailure?>(null);
        }, ct);

    private Task<ServiceResult<PirActions>> MutateActionAsync(string actionId, Actor actor, int? expectedVersion, string auditAction, bool requirePlanner,
        Func<ReleaseDbContext, PirActions, DateTime, Task<GuardFailure?>> apply, CancellationToken ct) =>
        RunAsync(async db =>
        {
            var a = await db.Set<PirActions>().SingleOrDefaultAsync(x => x.Id == actionId, ct);
            if (a is null) return ServiceResult<PirActions>.NotFound("PIR action");
            if (VersionMismatch(expectedVersion, a.Version)) return ServiceResult<PirActions>.Conflict(a);
            var p = await db.Set<PostImplementationReviews>().SingleAsync(x => x.Id == a.PirId, ct);
            var role = await RoleOf(db, actor.UserId);
            var planner = role is not null && CloseoutRoles.Contains(role);
            if (!planner && (requirePlanner || a.OwnerUserId != actor.UserId))
                return ServiceResult<PirActions>.Fail(new GuardFailure(requirePlanner ? CloseoutGuards.CloseoutRole : CloseoutGuards.PirActionRole,
                    requirePlanner ? "Only an RTE, Release Manager or Governance Officer does this" : "An action is completed by its owner, an RTE, a Release Manager or a Governance Officer"));
            if (p.Status == "Closed") return ServiceResult<PirActions>.Fail(new GuardFailure(CloseoutGuards.PirClosed, "The PIR is closed and read-only"));
            var before = new { a.Text, a.OwnerUserId, dueOn = a.DueOn.ToString("yyyy-MM-dd"), a.DoneAt };
            var failure = await apply(db, a, Now);
            if (failure is not null) return ServiceResult<PirActions>.Fail(failure);
            a.Version++;
            Audit(db, actor, p.ReleaseTrainId, "PirAction", a.Id, auditAction, before, new { a.Text, a.OwnerUserId, dueOn = a.DueOn.ToString("yyyy-MM-dd"), a.DoneAt });
            await db.SaveChangesAsync(ct);
            return ServiceResult<PirActions>.Ok(a);
        }, ct);

    private async Task<GuardFailure?> ValidateActionAsync(ReleaseDbContext db, string text, string ownerId, DateOnly dueOn, CancellationToken ct, bool dueMustBeFuture = true)
    {
        if (string.IsNullOrWhiteSpace(text)) return new(CloseoutGuards.InvalidPirAction, "An action needs text");
        if (!await db.Set<Users>().AnyAsync(u => u.Id == ownerId && u.IsActive, ct)) return new(CloseoutGuards.InvalidPirAction, "An action needs an active owner");
        var today = DateOnly.FromDateTime(Now);
        if (dueMustBeFuture && dueOn < today) return new(CloseoutGuards.InvalidPirAction, "An action cannot be due in the past");
        return null;
    }

    // ---- known issues -----------------------------------------------------------------------------------------------
    public Task<ServiceResult<KnownIssues>> AddIssueAsync(string trainId, NewKnownIssue n, Actor actor, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var t = await db.Set<ReleaseTrains>().SingleOrDefaultAsync(x => x.Id == trainId, ct);
            if (t is null) return ServiceResult<KnownIssues>.NotFound("train");
            var role = await RoleFailure<KnownIssues>(db, actor);
            if (role is not null) return role;
            if (t.CurrentStatus == "Aborted") return ServiceResult<KnownIssues>.Fail(new GuardFailure(Guards.TrainClosed, "The train is Aborted; no known issues are logged"));
            if (t.HypercareExitAt is not null) return ServiceResult<KnownIssues>.Fail(new GuardFailure(CloseoutGuards.HypercareEnded, "Hypercare has ended; raise a new incident instead"));
            var bad = ValidateIssue(n.Title, n.Severity);
            if (bad is not null) return ServiceResult<KnownIssues>.Fail(bad);
            var row = new KnownIssues { Id = Ids.New(), ReleaseTrainId = trainId, Title = n.Title.Trim(), Severity = n.Severity, Workaround = Clean(n.Workaround), ExternalKey = Clean(n.ExternalKey), RaisedAt = Now };
            db.Set<KnownIssues>().Add(row);
            Audit(db, actor, trainId, "KnownIssue", row.Id, "Raise", null, Snapshot(row));
            await db.SaveChangesAsync(ct);
            return ServiceResult<KnownIssues>.Ok(row);
        }, ct);

    /// <summary>Edit title, severity, workaround or external key. Status is never patched (rule 2); a resolved issue is reopened first.</summary>
    public Task<ServiceResult<KnownIssues>> UpdateIssueAsync(string trainId, string issueId, KnownIssuePatch p, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        MutateIssueAsync(trainId, issueId, actor, expectedVersion, "Update", acceptRole: false, i =>
        {
            if (i.Status == "Resolved") return new(CloseoutGuards.KnownIssueResolved, "A resolved issue is read-only; reopen it first");
            var bad = ValidateIssue(p.Title ?? i.Title, p.Severity ?? i.Severity);
            if (bad is not null) return bad;
            i.Title = (p.Title ?? i.Title).Trim(); i.Severity = p.Severity ?? i.Severity;
            if (p.Workaround is not null) i.Workaround = Clean(p.Workaround);
            if (p.ExternalKey is not null) i.ExternalKey = Clean(p.ExternalKey);
            return null;
        }, ct);

    /// <summary>Open -> Accepted: the team ships with it knowingly. Needs a workaround and a Release Manager or Governance Officer (Q-036e).</summary>
    public Task<ServiceResult<KnownIssues>> AcceptIssueAsync(string trainId, string issueId, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        MutateIssueAsync(trainId, issueId, actor, expectedVersion, "Accept", acceptRole: true, i =>
        {
            if (i.Status != "Open") return new(CloseoutGuards.IllegalKnownIssueTransition, $"Illegal known-issue transition {i.Status} -> Accepted");
            if (string.IsNullOrWhiteSpace(i.Workaround)) return new(CloseoutGuards.WorkaroundRequired, "Record a workaround before accepting an issue");
            i.Status = "Accepted";
            return null;
        }, ct);

    public Task<ServiceResult<KnownIssues>> ResolveIssueAsync(string trainId, string issueId, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        MutateIssueAsync(trainId, issueId, actor, expectedVersion, "Resolve", acceptRole: false, i =>
        {
            if (i.Status == "Resolved") return new(CloseoutGuards.IllegalKnownIssueTransition, "The issue is already resolved");
            i.Status = "Resolved"; i.ResolvedAt = Now;
            return null;
        }, ct);

    public Task<ServiceResult<KnownIssues>> ReopenIssueAsync(string trainId, string issueId, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        MutateIssueAsync(trainId, issueId, actor, expectedVersion, "Reopen", acceptRole: false, i =>
        {
            if (i.Status == "Open") return new(CloseoutGuards.IllegalKnownIssueTransition, "The issue is already open");
            i.Status = "Open"; i.ResolvedAt = null;
            return null;
        }, ct);

    private Task<ServiceResult<KnownIssues>> MutateIssueAsync(string trainId, string issueId, Actor actor, int? expectedVersion, string action, bool acceptRole,
        Func<KnownIssues, GuardFailure?> apply, CancellationToken ct) =>
        RunAsync(async db =>
        {
            var i = await db.Set<KnownIssues>().SingleOrDefaultAsync(x => x.Id == issueId && x.ReleaseTrainId == trainId, ct);
            if (i is null) return ServiceResult<KnownIssues>.NotFound("known issue");
            if (VersionMismatch(expectedVersion, i.Version)) return ServiceResult<KnownIssues>.Conflict(i);
            var role = await RoleOf(db, actor.UserId);
            if (acceptRole && role is not (Roles.ReleaseManager or Roles.GovernanceOfficer))
                return ServiceResult<KnownIssues>.Fail(new GuardFailure(CloseoutGuards.KnownIssueAcceptRole, "Only a Release Manager or Governance Officer accepts a known issue"));
            if (role is null || !CloseoutRoles.Contains(role))
                return ServiceResult<KnownIssues>.Fail(new GuardFailure(CloseoutGuards.CloseoutRole, "Only an RTE, Release Manager or Governance Officer does this"));
            var before = Snapshot(i);
            var failure = apply(i);
            if (failure is not null) return ServiceResult<KnownIssues>.Fail(failure);
            i.Version++;
            Audit(db, actor, trainId, "KnownIssue", i.Id, action, before, Snapshot(i));
            await db.SaveChangesAsync(ct);
            return ServiceResult<KnownIssues>.Ok(i);
        }, ct);

    private static GuardFailure? ValidateIssue(string? title, string? severity)
    {
        if (string.IsNullOrWhiteSpace(title)) return new(CloseoutGuards.InvalidKnownIssue, "A known issue needs a title");
        if (severity is null || !Severities.Contains(severity)) return new(CloseoutGuards.InvalidKnownIssue, "Severity must be Critical, High, Medium or Low", Severities);
        return null;
    }

    // ---- hypercare exit ---------------------------------------------------------------------------------------------
    /// <summary>
    /// Ends hypercare (Q-036f): the train is Complete, and no Critical or High issue is still Open (accept it with a workaround or resolve it).
    /// Sets HypercareExitAt/By on the train, stamps its Version, and freezes the known-issue log.
    /// </summary>
    public Task<ServiceResult<ReleaseTrains>> ExitHypercareAsync(string trainId, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var t = await db.Set<ReleaseTrains>().SingleOrDefaultAsync(x => x.Id == trainId, ct);
            if (t is null) return ServiceResult<ReleaseTrains>.NotFound("train");
            if (VersionMismatch(expectedVersion, t.Version)) return ServiceResult<ReleaseTrains>.Conflict(t);
            var f = new List<GuardFailure>();
            if (t.CurrentStatus != "Complete") f.Add(new(CloseoutGuards.HypercareRequiresComplete, "Hypercare follows completion; the train is not Complete"));
            if (t.HypercareExitAt is not null) f.Add(new(CloseoutGuards.HypercareEnded, "Hypercare has already ended"));
            var open = await db.Set<KnownIssues>().Where(i => i.ReleaseTrainId == trainId && i.Status == "Open" && (i.Severity == "Critical" || i.Severity == "High")).Select(i => i.Title).ToListAsync(ct);
            if (open.Count > 0) f.Add(new(CloseoutGuards.OpenKnownIssues, "Critical and High known issues must be resolved or accepted before hypercare ends", open));
            if (f.Count > 0) return ServiceResult<ReleaseTrains>.Fail(f);
            var now = Now;
            t.HypercareExitAt = now; t.HypercareExitByUserId = actor.UserId;
            t.LastChangedByUserId = actor.UserId; t.LastChangedAt = now; t.UpdatedAt = now; t.Version++;
            Audit(db, actor, trainId, "ReleaseTrain", trainId, "ExitHypercare", null, new { at = now });
            await db.SaveChangesAsync(ct);
            return ServiceResult<ReleaseTrains>.Ok(t);
        }, ct);

    // ---- helpers ----------------------------------------------------------------------------------------------------
    private async Task<ServiceResult<T>?> RoleFailure<T>(ReleaseDbContext db, Actor actor)
    {
        var role = await RoleOf(db, actor.UserId);
        return role is not null && CloseoutRoles.Contains(role) ? null
            : ServiceResult<T>.Fail(new GuardFailure(CloseoutGuards.CloseoutRole, "Only an RTE, Release Manager or Governance Officer does this"));
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static object Snapshot(KnownIssues i) => new { i.Title, i.Severity, i.Workaround, i.Status, i.ExternalKey, i.ResolvedAt };
}
