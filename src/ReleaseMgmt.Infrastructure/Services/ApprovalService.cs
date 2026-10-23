using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Reminders;

namespace ReleaseMgmt.Infrastructure.Services;

public sealed record NewApproval(string? TrainId, string? EnvironmentId);

/// <summary><see cref="Seat"/> is the seat's label (or id); <see cref="Decision"/> is Approved or ChangesRequested; a note is required with ChangesRequested.</summary>
public sealed record DecideRequest(string? Seat, string? Decision, string? Note = null);

public sealed record ApprovalFilter(string? TrainId = null, string? EnvironmentId = null, string? Status = null, bool Mine = false, int Take = 100);

/// <summary>One seat of an approval. <see cref="CanDecide"/> says whether the signed-in person may decide it now; if not, <see cref="WhyNot"/> says why in words.</summary>
public sealed record SeatDecisionView(string Id, string Label, string Kind, string? Value, string? ValueName, string DueAt, bool Overdue, string? Decision, string? DecidedByUserId, string? DecidedByName,
    string? DecidedAt, string? Note, int Version, bool CanDecide, string? WhyNot);

/// <summary>A policy check. <see cref="Required"/> false = the policy does not ask for it (it is listed so the pane can show it as not required).</summary>
public sealed record PolicyCheck(string Name, bool Required, bool Passed, string? Detail);

public sealed record ApprovalView(string Id, string TrainId, string? ReleaseKey, string TrainTitle, string RiskTier, string EnvironmentId, string EnvironmentName, string EnvironmentKind, string Status, int Round,
    string RequestedByUserId, string? RequestedByName, string RequestedAt, string DueAt, bool Overdue, string? DecidedAt, int Version, IReadOnlyList<string> PolicyNames, IReadOnlyList<SeatDecisionView> Decisions,
    IReadOnlyList<PolicyCheck> Checks);

/// <summary>One seat waiting for a person (the approval queue, REOS-94 / REOS-99).</summary>
public sealed record SeatWaiting(string ApprovalId, string DecisionId, string Label, string TrainId, string TrainTitle, string EnvironmentName, int Round, DateTime DueAt);

/// <summary>
/// Approvals (REOS-113, B5): a request to take a release to an environment, created from the workspace's matching policies, with one seat per required approver, each with a due time and its own
/// decision (Approved, or Changes requested with a note). The approval is Approved when every seat has approved; one Request changes sends it back (ChangesRequested) until the requester resubmits
/// for a new round. A seat is decided only by an eligible person (the seat's workspace role, a member of its team, or the release owner), and never against separation of duties: the person who
/// requested the approval cannot approve it, on a Production change the release owner approves only the release owner's seat, and no one holds two seats of a Production change. Approving is refused,
/// with each reason in words, until every check the policies ask for passes (every gate certified or waived; a deployment window that has not ended). The database enforces eligibility,
/// separation of duties and the decision rules too (triggers), so a race or a second client cannot get past them. Every request and decision is audited on the release. Gates, waivers, freeze
/// overrides and the Go/No-Go keep their own rules; an approval does not replace them.
/// </summary>
public sealed class ApprovalService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IRealtimePublisher? realtime = null, INotifier? notifier = null, SyncAlertWriter? alerts = null)
    : ServiceBase(dbf, time, realtime)
{
    private const string Entity = "Approval", SeatEntity = "ApprovalDecision";
    private static readonly List<string> Planners = [Roles.RTE, Roles.ReleaseManager];
    private static readonly string[] Open = ["Pending", "ChangesRequested"];

    private static GuardFailure Bad(string m) => new(ApprovalGuards.ApprovalInvalid, m);
    private static string Iso(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
    private static string? Iso(DateTime? t) => t?.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    private static string RoleName(string? role) => role switch
    {
        Roles.RTE => "RTE", Roles.ReleaseManager => "Release Manager", Roles.GovernanceOfficer => "Governance Officer", Roles.Viewer => "Viewer", _ => role ?? "",
    };

    // ---- request --------------------------------------------------------------------------------------------------------------------

    public async Task<ServiceResult<ApprovalView>> RequestAsync(NewApproval n, Actor actor, CancellationToken ct = default)
    {
        List<string> notify = []; string message = "";
        var r = await RunAsync(async db =>
        {
            var f = new List<GuardFailure>();
            if (!Planners.Contains(await RoleOf(db, actor.UserId) ?? "")) f.Add(new(ApprovalGuards.ApprovalRole, "An approval is requested by an RTE or Release Manager"));
            var train = string.IsNullOrWhiteSpace(n.TrainId) ? null : await db.Set<ReleaseTrains>().SingleOrDefaultAsync(t => t.Id == n.TrainId, ct);
            if (train is null) return ServiceResult<ApprovalView>.NotFound("train");
            var env = string.IsNullOrWhiteSpace(n.EnvironmentId) ? null : await db.Set<Environments>().SingleOrDefaultAsync(e => e.Id == n.EnvironmentId, ct);
            if (env is null) return ServiceResult<ApprovalView>.NotFound("environment");
            if (train.CurrentStatus is "Complete" or "Aborted" || train.ArchivedAt is not null) f.Add(new(Guards.TrainClosed, $"The release is {(train.ArchivedAt is not null ? "archived" : train.CurrentStatus)}; there is nothing left to approve"));
            if (env.ArchivedAt is not null) f.Add(Bad($"The environment {env.Name} is archived"));
            if (await db.Set<Approvals>().AnyAsync(a => a.ReleaseTrainId == train.Id && a.EnvironmentId == env.Id && (a.Status == "Pending" || a.Status == "ChangesRequested"), ct))
                f.Add(new(ApprovalGuards.ApprovalAlreadyOpen, $"{train.Title} already has an open approval for {env.Name}"));
            if (f.Count > 0) return ServiceResult<ApprovalView>.Fail(f);

            var policies = await ApprovalPolicyService.MatchingAsync(db, train, env, ct);
            if (policies.Count == 0)
                return ServiceResult<ApprovalView>.Fail(new GuardFailure(ApprovalGuards.NoApprovalPolicy, $"No approval policy applies to a {train.RiskTier} risk release going to {env.Name} ({env.Kind}); add one under approval policies"));
            var pids = policies.Select(p => p.Id).ToList();
            var wanted = (await db.Set<ApprovalPolicyRequirements>().AsNoTracking().Where(s => pids.Contains(s.PolicyId)).ToListAsync(ct))
                .OrderBy(s => policies.FindIndex(p => p.Id == s.PolicyId)).ThenBy(s => s.SortOrder).ToList();
            var seats = new List<ApprovalPolicyRequirements>();
            foreach (var s in wanted) if (!seats.Any(x => string.Equals(x.Label, s.Label, StringComparison.OrdinalIgnoreCase))) seats.Add(s);

            // a seat nobody can fill would hold the release forever: say so now, in words
            var unfillable = new List<string>();
            foreach (var s in seats)
            {
                var people = await EligiblePeopleAsync(db, train, s.EligibleKind, s.EligibleValue, ct);
                if (s.EligibleKind == "ReleaseOwner" && train.OwnerUserId is null) unfillable.Add($"{s.Label}: the release has no owner; set one first");
                else if (people.Count == 0) unfillable.Add($"{s.Label}: nobody holds it");
                else if (people.All(p => p == actor.UserId)) unfillable.Add($"{s.Label}: only you could approve it, and nobody approves what they requested; ask someone else to request the approval");
            }
            if (unfillable.Count > 0) return ServiceResult<ApprovalView>.Fail(new GuardFailure(ApprovalGuards.SeatUnfillable, "These seats cannot be filled: " + string.Join("; ", unfillable), unfillable));

            var now = Now;
            var a = new Approvals { Id = Ids.New(), ReleaseTrainId = train.Id, EnvironmentId = env.Id, RequestedByUserId = actor.UserId, RequestedAt = now, DueAt = now.AddHours(seats.Min(s => s.DueHours)), LastChangedByUserId = actor.UserId, LastChangedAt = now };
            db.Set<Approvals>().Add(a);
            await db.SaveChangesAsync(ct);   // the seats' triggers read the approval
            var rows = seats.Select(s => new ApprovalDecisions { Id = Ids.New(), ApprovalId = a.Id, Label = s.Label, EligibleKind = s.EligibleKind, EligibleValue = s.EligibleValue, DueAt = now.AddHours(s.DueHours) }).ToList();
            db.Set<ApprovalDecisions>().AddRange(rows);
            Audit(db, actor, train.Id, Entity, a.Id, "Request", null, new { environment = env.Name, kind = env.Kind, policies = policies.Select(p => p.Name), seats = seats.Select(s => s.Label), dueAt = a.DueAt });
            await db.SaveChangesAsync(ct);

            foreach (var s in seats) foreach (var p in await EligiblePeopleAsync(db, train, s.EligibleKind, s.EligibleValue, ct)) if (p != actor.UserId && !notify.Contains(p)) notify.Add(p);
            message = $"Approval requested for {train.Title} going to {env.Name}: your decision is due {a.DueAt:yyyy-MM-dd HH:mm}Z";
            return ServiceResult<ApprovalView>.Ok((await ViewsAsync(db, [a], actor.UserId, ct))[0]);
        }, ct);
        if (r.IsOk) await NotifyAsync(notify, "ApprovalRequested", r.Value!.Id, message, ct);
        return r;
    }

    private static async Task<List<string>> EligiblePeopleAsync(ReleaseDbContext db, ReleaseTrains train, string kind, string? value, CancellationToken ct)
    {
        var ws = Memberships.WorkspaceOf(db);
        var members = db.Set<WorkspaceMembers>().IgnoreQueryFilters().Where(m => m.WorkspaceId == ws);
        var active = db.Set<Users>().Where(u => u.IsActive);
        switch (kind)
        {
            case "ReleaseOwner":
                return train.OwnerUserId is { } o && await active.AnyAsync(u => u.Id == o, ct) && await members.AnyAsync(m => m.UserId == o, ct) ? [o] : [];
            case "WorkspaceRole":
                return await (from m in members join u in active on m.UserId equals u.Id where m.Role == value select u.Id).ToListAsync(ct);
            default:
                return await (from tm in db.Set<TeamMembers>() join m in members on tm.UserId equals m.UserId join u in active on tm.UserId equals u.Id where tm.TeamId == value select tm.UserId).ToListAsync(ct);
        }
    }

    private async Task NotifyAsync(IEnumerable<string> userIds, string kind, string approvalId, string message, CancellationToken ct)
    {
        if (notifier is null) return;
        foreach (var id in userIds.Distinct())   // after the commit; a failed notification is raised, never swallowed (rule 8)
        {
            try { await notifier.NotifyAsync(new NotificationRequest(id, kind, "Approval", approvalId, message, 0), ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (alerts is null) throw;
                await alerts.RaiseAsync("Notifications", "DeliveryFailed", $"approval:{approvalId}:{id}", $"Approval {approvalId} was saved but notifying {id} failed: {ex.Message}", ct: ct);
            }
        }
    }

    // ---- reads ----------------------------------------------------------------------------------------------------------------------

    public async Task<List<ApprovalView>> ListAsync(ApprovalFilter? filter, string? userId, CancellationToken ct = default)
    {
        filter ??= new();
        await using var db = await OpenAsync(ct);
        var q = db.Set<Approvals>().AsNoTracking().AsQueryable();
        if (filter.TrainId is not null) q = q.Where(a => a.ReleaseTrainId == filter.TrainId);
        if (filter.EnvironmentId is not null) q = q.Where(a => a.EnvironmentId == filter.EnvironmentId);
        if (filter.Status is not null) q = q.Where(a => a.Status == filter.Status);
        var rows = await q.OrderBy(a => a.Status == "Pending" ? 0 : a.Status == "ChangesRequested" ? 1 : 2).ThenBy(a => a.DueAt).ThenBy(a => a.Id).Take(Math.Clamp(filter.Take, 1, 500)).ToListAsync(ct);
        var views = await ViewsAsync(db, rows, userId, ct);
        return filter.Mine ? [.. views.Where(v => v.Decisions.Any(d => d.CanDecide))] : views;
    }

    public async Task<ApprovalView?> GetAsync(string id, string? userId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var row = await db.Set<Approvals>().AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct);
        return row is null ? null : (await ViewsAsync(db, [row], userId, ct))[0];
    }

    private async Task<List<ApprovalView>> ViewsAsync(ReleaseDbContext db, List<Approvals> rows, string? userId, CancellationToken ct)
    {
        var now = Now;
        var ids = rows.Select(r => r.Id).ToList();
        var seats = (await db.Set<ApprovalDecisions>().AsNoTracking().Where(d => ids.Contains(d.ApprovalId)).ToListAsync(ct)).OrderBy(d => d.DueAt).ThenBy(d => d.Label, StringComparer.OrdinalIgnoreCase).ToList();
        var trainIds = rows.Select(r => r.ReleaseTrainId).Distinct().ToList();
        var trains = await db.Set<ReleaseTrains>().AsNoTracking().Where(t => trainIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        var envIds = rows.Select(r => r.EnvironmentId).Distinct().ToList();
        var envs = await db.Set<Environments>().AsNoTracking().Where(e => envIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);
        var userIds = rows.Select(r => r.RequestedByUserId).Concat(seats.Select(s => s.DecidedByUserId).OfType<string>()).Distinct().ToList();
        var names = await db.Set<Users>().AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var teamIds = seats.Where(s => s.EligibleKind == "Team").Select(s => s.EligibleValue!).Distinct().ToList();
        var handles = await db.Set<Teams>().AsNoTracking().Where(t => teamIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => "@" + t.Handle, ct);
        var views = new List<ApprovalView>();
        foreach (var a in rows)
        {
            var t = trains[a.ReleaseTrainId]; var e = envs[a.EnvironmentId];
            var mine = seats.Where(s => s.ApprovalId == a.Id).ToList();
            var policies = await ApprovalPolicyService.MatchingAsync(db, t, e, ct);
            var decisions = new List<SeatDecisionView>();
            foreach (var s in mine)
            {
                string? why = null; var can = false;
                if (userId is not null && s.Decision is null)
                {
                    why = a.Status != "Pending" ? (a.Status == "ChangesRequested" ? "Changes were requested; the requester resubmits for a new round" : $"The approval is {a.Status}") : await WhyNotAsync(db, a, t, e, s, mine, userId, approving: false, ct);
                    can = why is null;
                }
                decisions.Add(new(s.Id, s.Label, s.EligibleKind, s.EligibleValue, s.EligibleKind == "Team" ? handles.GetValueOrDefault(s.EligibleValue!) : s.EligibleKind == "WorkspaceRole" ? RoleName(s.EligibleValue) : null,
                    Iso(s.DueAt), s.Decision is null && a.Status is "Pending" && s.DueAt <= now, s.Decision, s.DecidedByUserId, s.DecidedByUserId is null ? null : names.GetValueOrDefault(s.DecidedByUserId), Iso(s.DecidedAt), s.Note, s.Version, can, why));
            }
            views.Add(new(a.Id, a.ReleaseTrainId, t.ReleaseKey, t.Title, t.RiskTier, e.Id, e.Name, e.Kind, a.Status, a.Round, a.RequestedByUserId, names.GetValueOrDefault(a.RequestedByUserId), Iso(a.RequestedAt), Iso(a.DueAt),
                a.Status == "Pending" && a.DueAt <= now, Iso(a.DecidedAt), a.Version, [.. policies.Select(p => p.Name)], decisions, await ChecksAsync(db, a, t, policies, userId, ct)));
        }
        return views;
    }

    // ---- rules ----------------------------------------------------------------------------------------------------------------------

    /// <summary>The first reason this person cannot decide this seat now, or null. With <paramref name="approving"/>, separation of duties applies too.</summary>
    private async Task<string?> WhyNotAsync(ReleaseDbContext db, Approvals a, ReleaseTrains t, Environments e, ApprovalDecisions seat, List<ApprovalDecisions> all, string userId, bool approving, CancellationToken ct)
    {
        var f = await ReasonsAsync(db, a, t, e, seat, all, userId, approving, ct);
        return f.Count == 0 ? null : f[0].Message;
    }

    private async Task<List<GuardFailure>> ReasonsAsync(ReleaseDbContext db, Approvals a, ReleaseTrains t, Environments e, ApprovalDecisions seat, List<ApprovalDecisions> all, string userId, bool approving, CancellationToken ct)
    {
        var f = new List<GuardFailure>();
        var role = await RoleOf(db, userId);
        if (role is null || !await db.Set<Users>().AnyAsync(u => u.Id == userId && u.IsActive, ct)) { f.Add(new(ApprovalGuards.NotEligibleApprover, "You are not an active member of this workspace")); return f; }
        switch (seat.EligibleKind)
        {
            case "ReleaseOwner" when t.OwnerUserId != userId:
                var owner = t.OwnerUserId is null ? null : await db.Set<Users>().AsNoTracking().Where(u => u.Id == t.OwnerUserId).Select(u => u.DisplayName).SingleOrDefaultAsync(ct);
                f.Add(new(ApprovalGuards.NotEligibleApprover, owner is null ? $"The {seat.Label} seat is for the release owner, and this release has none" : $"The {seat.Label} seat is for the release owner ({owner})"));
                break;
            case "WorkspaceRole" when role != seat.EligibleValue:
                f.Add(new(ApprovalGuards.NotEligibleApprover, $"The {seat.Label} seat is for {RoleName(seat.EligibleValue)}s; you are {(role == Roles.RTE ? "an" : "a")} {RoleName(role)}"));
                break;
            case "Team" when !await db.Set<TeamMembers>().AnyAsync(m => m.TeamId == seat.EligibleValue && m.UserId == userId, ct):
                var handle = await db.Set<Teams>().AsNoTracking().Where(x => x.Id == seat.EligibleValue).Select(x => x.Handle).SingleOrDefaultAsync(ct);
                f.Add(new(ApprovalGuards.NotEligibleApprover, $"The {seat.Label} seat is for members of @{handle}"));
                break;
        }
        if (!approving) return f;
        if (a.RequestedByUserId == userId)
            f.Add(new(ApprovalGuards.SeparationOfDuties, "Separation of duties: you requested this approval, so you cannot approve it"));
        if (e.Kind == "Production")
        {
            if (t.OwnerUserId == userId && seat.EligibleKind != "ReleaseOwner")
                f.Add(new(ApprovalGuards.SeparationOfDuties, "Separation of duties: you own this release, and on a Production change the release owner approves only the release owner seat"));
            var held = all.FirstOrDefault(d => d.Id != seat.Id && d.Decision == "Approved" && d.DecidedByUserId == userId);
            if (held is not null)
                f.Add(new(ApprovalGuards.SeparationOfDuties, $"Separation of duties: you already approved the {held.Label} seat, and one person cannot hold two seats of a Production change"));
        }
        return f;
    }

    private async Task<List<PolicyCheck>> ChecksAsync(ReleaseDbContext db, Approvals a, ReleaseTrains t, List<ApprovalPolicies> policies, string? userId, CancellationToken ct)
    {
        var needGates = policies.Any(p => p.RequireGatesCertified);
        var needWindow = policies.Any(p => p.RequireOpenWindow);
        var now = Now;
        var gates = await db.Set<StageGates>().AsNoTracking().Where(g => g.ReleaseTrainId == t.Id).OrderBy(g => g.SequenceOrder).ToListAsync(ct);
        var open = gates.Where(g => g.Status is not ("Certified" or "Waived")).Select(g => g.GateName).ToList();
        var window = await db.Set<DeploymentWindows>().AsNoTracking().SingleOrDefaultAsync(w => w.ReleaseTrainId == t.Id, ct);
        var windowOk = window is not null && window.EndsAt > now;
        return
        [
            new("Evidence complete", needGates, !needGates || open.Count == 0, open.Count == 0 ? (needGates ? $"All {gates.Count} gates are certified or waived" : "Not required by the policy")
                : $"{(needGates ? "" : "Not required, but ")}{open.Count} gate{(open.Count == 1 ? "" : "s")} not yet certified or waived: {string.Join(", ", open)}"),
            new("Within change window", needWindow, !needWindow || windowOk, window is null ? (needWindow ? "The release has no deployment window" : "Not required by the policy")
                : windowOk ? (needWindow ? $"The window ends {window.EndsAt:yyyy-MM-dd HH:mm}Z" : "Not required by the policy") : $"The deployment window ended {window.EndsAt:yyyy-MM-dd HH:mm}Z"),
            new("Independent approver", true, userId is null || userId != a.RequestedByUserId, userId is not null && userId == a.RequestedByUserId ? "You requested this approval" : "Nobody approves what they requested"),
        ];
    }

    // ---- decide ---------------------------------------------------------------------------------------------------------------------

    public async Task<ServiceResult<ApprovalView>> DecideAsync(string approvalId, DecideRequest d, Actor actor, int? expectedVersion, CancellationToken ct = default)
    {
        string? requester = null; string message = ""; string kind = "";
        var r = await RunAsync(async db =>
        {
            var a = await db.Set<Approvals>().SingleOrDefaultAsync(x => x.Id == approvalId, ct);
            if (a is null) return ServiceResult<ApprovalView>.NotFound("approval");
            if (VersionMismatch(expectedVersion, a.Version)) return ServiceResult<ApprovalView>.Conflict(a);
            if (a.Status != "Pending")
                return ServiceResult<ApprovalView>.Fail(new GuardFailure(ApprovalGuards.ApprovalNotOpen, a.Status == "ChangesRequested" ? "Changes were requested on this approval; the requester resubmits it for a new round before anyone decides again" : $"This approval is {a.Status}"));
            var seats = await db.Set<ApprovalDecisions>().Where(s => s.ApprovalId == a.Id).ToListAsync(ct);
            var key = (d.Seat ?? "").Trim();
            var seat = seats.FirstOrDefault(s => s.Id == key) ?? seats.FirstOrDefault(s => string.Equals(s.Label, key, StringComparison.OrdinalIgnoreCase));
            if (seat is null) return ServiceResult<ApprovalView>.Fail(new GuardFailure(ApprovalGuards.SeatNotFound, $"This approval has no seat \"{key}\"; its seats are {string.Join(", ", seats.Select(s => s.Label))}", [.. seats.Select(s => s.Label)]));
            if (seat.Decision is not null) return ServiceResult<ApprovalView>.Fail(new GuardFailure(ApprovalGuards.SeatDecided, $"The {seat.Label} seat has already been decided ({seat.Decision})"));
            if (d.Decision is not ("Approved" or "ChangesRequested")) return ServiceResult<ApprovalView>.Fail(Bad("The decision is Approved or ChangesRequested"));
            var note = string.IsNullOrWhiteSpace(d.Note) ? null : d.Note.Trim();
            var f = new List<GuardFailure>();
            if (d.Decision == "ChangesRequested" && note is null) f.Add(new(ApprovalGuards.NoteRequired, "Say what has to change: a note is required with Request changes"));
            if (note is { Length: > 2000 }) f.Add(Bad("A note is at most 2,000 characters"));
            var t = await db.Set<ReleaseTrains>().SingleAsync(x => x.Id == a.ReleaseTrainId, ct);
            var e = await db.Set<Environments>().AsNoTracking().SingleAsync(x => x.Id == a.EnvironmentId, ct);
            f.AddRange(await ReasonsAsync(db, a, t, e, seat, seats, actor.UserId, d.Decision == "Approved", ct));
            if (d.Decision == "Approved")
            {
                var failing = (await ChecksAsync(db, a, t, await ApprovalPolicyService.MatchingAsync(db, t, e, ct), actor.UserId, ct)).Where(c => c.Name != "Independent approver" && c.Required && !c.Passed).ToList();
                if (failing.Count > 0) f.Add(new(ApprovalGuards.PolicyCheckFailed, "A policy check has not passed: " + string.Join("; ", failing.Select(c => $"{c.Name} ({c.Detail})")), [.. failing.Select(c => c.Name)]));
            }
            if (f.Count > 0) return ServiceResult<ApprovalView>.Fail(f);

            var now = Now;
            var before = new { status = a.Status, seat = seat.Label, version = a.Version };
            seat.Decision = d.Decision; seat.DecidedByUserId = actor.UserId; seat.DecidedAt = now; seat.Note = note; seat.Version++;
            await db.SaveChangesAsync(ct);   // the approval's own trigger reads the seats, so the seat is saved first
            Audit(db, actor, t.Id, SeatEntity, seat.Id, d.Decision == "Approved" ? "Approve" : "RequestChanges", before, new { approval = a.Id, seat = seat.Label, environment = e.Name, a.Round, note });
            a.LastChangedByUserId = actor.UserId; a.LastChangedAt = now; a.Version++;
            if (d.Decision == "ChangesRequested")
            {
                a.Status = "ChangesRequested";
                Audit(db, actor, t.Id, Entity, a.Id, "ChangesRequested", before, new { by = seat.Label, note, environment = e.Name });
                requester = a.RequestedByUserId; kind = "ApprovalChangesRequested";
                message = $"Changes requested on {t.Title} going to {e.Name} ({seat.Label}): {note}";
            }
            else if (seats.All(s => s.Decision == "Approved"))
            {
                a.Status = "Approved"; a.DecidedAt = now;
                Audit(db, actor, t.Id, Entity, a.Id, "Approved", before, new { environment = e.Name, a.Round, seats = seats.Select(s => s.Label) });
                requester = a.RequestedByUserId; kind = "ApprovalApproved";
                message = $"{t.Title} is approved to go to {e.Name}: every required approver has approved";
            }
            await db.SaveChangesAsync(ct);
            return ServiceResult<ApprovalView>.Ok((await ViewsAsync(db, [a], actor.UserId, ct))[0]);
        }, ct);
        if (r.IsOk && requester is not null) await NotifyAsync([requester], kind, approvalId, message, ct);
        return r;
    }

    // ---- resubmit and cancel --------------------------------------------------------------------------------------------------------

    /// <summary>After Request changes: the requester (or a planner) sends the approval back for a new round. Every seat is open again with its due time counted from now.</summary>
    public async Task<ServiceResult<ApprovalView>> ResubmitAsync(string approvalId, Actor actor, int? expectedVersion, CancellationToken ct = default)
    {
        List<string> notify = []; string message = "";
        var r = await RunAsync(async db =>
        {
            var a = await db.Set<Approvals>().SingleOrDefaultAsync(x => x.Id == approvalId, ct);
            if (a is null) return ServiceResult<ApprovalView>.NotFound("approval");
            if (VersionMismatch(expectedVersion, a.Version)) return ServiceResult<ApprovalView>.Conflict(a);
            var f = new List<GuardFailure>();
            if (a.RequestedByUserId != actor.UserId && !Planners.Contains(await RoleOf(db, actor.UserId) ?? "")) f.Add(new(ApprovalGuards.ApprovalRole, "An approval is resubmitted by whoever requested it, or by an RTE or Release Manager"));
            if (a.Status != "ChangesRequested") f.Add(new(ApprovalGuards.ApprovalNotOpen, a.Status == "Pending" ? "This approval is waiting for its approvers; there is nothing to resubmit" : $"This approval is {a.Status}"));
            if (f.Count > 0) return ServiceResult<ApprovalView>.Fail(f);
            var t = await db.Set<ReleaseTrains>().SingleAsync(x => x.Id == a.ReleaseTrainId, ct);
            var e = await db.Set<Environments>().AsNoTracking().SingleAsync(x => x.Id == a.EnvironmentId, ct);
            var seats = await db.Set<ApprovalDecisions>().Where(s => s.ApprovalId == a.Id).ToListAsync(ct);
            var now = Now;
            var before = new { status = a.Status, a.Round, seatsReopened = seats.Count(s => s.Decision is not null) };
            foreach (var s in seats)
            {
                var offset = s.DueAt - a.RequestedAt;   // the seat's allowance, counted again from this round's request
                s.Decision = null; s.DecidedByUserId = null; s.DecidedAt = null; s.Note = null; s.DueAt = now + offset; s.Version++;
            }
            await db.SaveChangesAsync(ct);
            a.Status = "Pending"; a.Round++; a.RequestedAt = now; a.DueAt = seats.Min(s => s.DueAt); a.LastChangedByUserId = actor.UserId; a.LastChangedAt = now; a.Version++;
            Audit(db, actor, t.Id, Entity, a.Id, "Resubmit", before, new { a.Round, environment = e.Name, dueAt = a.DueAt });
            await db.SaveChangesAsync(ct);
            foreach (var s in seats) foreach (var p in await EligiblePeopleAsync(db, t, s.EligibleKind, s.EligibleValue, ct)) if (p != a.RequestedByUserId && !notify.Contains(p)) notify.Add(p);
            message = $"{t.Title} going to {e.Name} was resubmitted for approval (round {a.Round}): your decision is due {a.DueAt:yyyy-MM-dd HH:mm}Z";
            return ServiceResult<ApprovalView>.Ok((await ViewsAsync(db, [a], actor.UserId, ct))[0]);
        }, ct);
        if (r.IsOk) await NotifyAsync(notify, "ApprovalRequested", approvalId, message, ct);
        return r;
    }

    public Task<ServiceResult<ApprovalView>> CancelAsync(string approvalId, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var a = await db.Set<Approvals>().SingleOrDefaultAsync(x => x.Id == approvalId, ct);
            if (a is null) return ServiceResult<ApprovalView>.NotFound("approval");
            if (VersionMismatch(expectedVersion, a.Version)) return ServiceResult<ApprovalView>.Conflict(a);
            var f = new List<GuardFailure>();
            if (a.RequestedByUserId != actor.UserId && !Planners.Contains(await RoleOf(db, actor.UserId) ?? "")) f.Add(new(ApprovalGuards.ApprovalRole, "An approval is cancelled by whoever requested it, or by an RTE or Release Manager"));
            if (!Open.Contains(a.Status)) f.Add(new(ApprovalGuards.ApprovalNotOpen, $"This approval is {a.Status}"));
            if (f.Count > 0) return ServiceResult<ApprovalView>.Fail(f);
            var before = new { status = a.Status, a.Round };
            a.Status = "Cancelled"; a.LastChangedByUserId = actor.UserId; a.LastChangedAt = Now; a.Version++;
            Audit(db, actor, a.ReleaseTrainId, Entity, a.Id, "Cancel", before, new { status = a.Status });
            await db.SaveChangesAsync(ct);
            return ServiceResult<ApprovalView>.Ok((await ViewsAsync(db, [a], actor.UserId, ct))[0]);
        }, ct);

    // ---- the queue ------------------------------------------------------------------------------------------------------------------

    /// <summary>The seats of pending approvals that this person may decide now (eligible, not the requester, not already holding another seat of a Production change). Read-only; used by the approval queue.</summary>
    public async Task<List<SeatWaiting>> SeatsWaitingAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var approvals = await db.Set<Approvals>().AsNoTracking().Where(a => a.Status == "Pending").ToListAsync(ct);
        if (approvals.Count == 0) return [];
        var ids = approvals.Select(a => a.Id).ToList();
        var seats = await db.Set<ApprovalDecisions>().AsNoTracking().Where(s => ids.Contains(s.ApprovalId)).ToListAsync(ct);
        var trains = await db.Set<ReleaseTrains>().AsNoTracking().Where(t => approvals.Select(a => a.ReleaseTrainId).Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        var envs = await db.Set<Environments>().AsNoTracking().ToDictionaryAsync(e => e.Id, ct);
        var result = new List<SeatWaiting>();
        foreach (var a in approvals.Where(a => trains[a.ReleaseTrainId].ArchivedAt == null))
        {
            var mine = seats.Where(s => s.ApprovalId == a.Id).ToList();
            foreach (var s in mine.Where(s => s.Decision is null))
                if (await WhyNotAsync(db, a, trains[a.ReleaseTrainId], envs[a.EnvironmentId], s, mine, userId, approving: true, ct) is null)
                    result.Add(new(a.Id, s.Id, s.Label, a.ReleaseTrainId, trains[a.ReleaseTrainId].Title, envs[a.EnvironmentId].Name, a.Round, s.DueAt));
        }
        return result;
    }
}
