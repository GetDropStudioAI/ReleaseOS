using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

/// <summary>Guard names of approvals, policies and exceptions (REOS-113).</summary>
public static class ApprovalGuards
{
    public const string PolicyInvalid = "PolicyInvalid";
    public const string PolicyNameTaken = "PolicyNameTaken";
    public const string PolicyRole = "PolicyRole";
    public const string ApprovalInvalid = "ApprovalInvalid";
    public const string ApprovalRole = "ApprovalRole";
    public const string NoApprovalPolicy = "NoApprovalPolicy";
    public const string ApprovalAlreadyOpen = "ApprovalAlreadyOpen";
    public const string ApprovalNotOpen = "ApprovalNotOpen";
    public const string SeatNotFound = "SeatNotFound";
    public const string SeatDecided = "SeatDecided";
    public const string NotEligibleApprover = "NotEligibleApprover";
    public const string SeparationOfDuties = "SeparationOfDuties";
    public const string PolicyCheckFailed = "PolicyCheckFailed";
    public const string NoteRequired = "NoteRequired";
    public const string SeatUnfillable = "SeatUnfillable";
    public const string WaiverDeclineRole = "WaiverDeclineRole";
    public const string WaiverDeclineSelf = "WaiverDeclineSelf";
    public const string WaiverAlreadyDecided = "WaiverAlreadyDecided";
    public const string OverrideRequestNotFound = "OverrideRequestNotFound";
    public const string DeclineNote = "DeclineNote";
    public const string OverrideDeclineRole = "OverrideDeclineRole";
    public const string OverrideDeclineSelf = "OverrideDeclineSelf";
}

/// <summary>A seat of a policy. <see cref="Kind"/> is WorkspaceRole (<see cref="Value"/> a role), ReleaseOwner (no value) or Team (<see cref="Value"/> a team id or @handle).</summary>
public sealed record SeatInput(string? Label, string? Kind, string? Value = null, int? DueHours = null);

public sealed record NewPolicy(string? Name, string? EnvironmentKind = null, string? MinRiskTier = null, bool RequireGatesCertified = false, bool RequireOpenWindow = false, IReadOnlyList<SeatInput>? Seats = null);

/// <summary>Null leaves a field; <see cref="Clear"/> names optional fields to empty (environmentKind, minRiskTier). <see cref="Seats"/>, when given, replaces all the seats.</summary>
public sealed record PolicyPatch(string? Name = null, string? EnvironmentKind = null, string? MinRiskTier = null, bool? RequireGatesCertified = null, bool? RequireOpenWindow = null,
    IReadOnlyList<SeatInput>? Seats = null, IReadOnlyList<string>? Clear = null);

public sealed record SeatView(string Id, string Label, string Kind, string? Value, string? ValueName, int DueHours, int SortOrder);

public sealed record PolicyView(string Id, string Name, string? EnvironmentKind, string? MinRiskTier, bool RequireGatesCertified, bool RequireOpenWindow, IReadOnlyList<SeatView> Seats, bool Archived, int Version);

public sealed record UngovernedRelease(string TrainId, string? ReleaseKey, string Title, string RiskTier);

/// <summary>The share of Production releases that a policy governs (REOS-113). A Production release is one that targets a Production environment; it is governed when an active policy applies to it
/// (its environment kind and risk tier); <see cref="WithApproval"/> counts those that also have an approved approval for a Production environment.</summary>
public sealed record PolicyCoverage(string From, string To, int ProductionReleases, int Governed, int WithApproval, double? CoveragePct, IReadOnlyList<UngovernedRelease> Ungoverned);

/// <summary>
/// Approval policies of a workspace (REOS-113, B5): which seats must approve a release going to an environment, by environment kind and risk tier ("Production needs a Security reviewer, the
/// release owner and an SRE approver"). A seat is filled by a workspace role, by a team's members or by the release owner. Policies and their seats are managed by RTEs, Release Managers and
/// Governance Officers; every write is one transaction with one <c>ApprovalPolicy</c> audit row and a Version bump (If-Match on edits). An approval copies its seats when it is created, so editing
/// a policy never changes an approval already under way.
/// </summary>
public sealed class ApprovalPolicyService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IRealtimePublisher? realtime = null) : ServiceBase(dbf, time, realtime)
{
    private const string Entity = "ApprovalPolicy";
    public static readonly string[] RiskTiers = ["Low", "Moderate", "High", "VeryHigh"];
    public static readonly string[] SeatKinds = ["WorkspaceRole", "ReleaseOwner", "Team"];
    public const int MaxSeats = 10;
    private static readonly List<string> Managers = [Roles.RTE, Roles.ReleaseManager, Roles.GovernanceOfficer];
    private static readonly string[] Clearable = ["environmentKind", "minRiskTier"];

    private static GuardFailure Bad(string m) => new(ApprovalGuards.PolicyInvalid, m);

    public static int TierIndex(string tier) => Array.IndexOf(RiskTiers, tier);

    /// <summary>The active policies that apply to a release going to an environment: the environment kind matches (or the policy names none) and the release's risk tier reaches the policy's minimum.</summary>
    public static async Task<List<ApprovalPolicies>> MatchingAsync(ReleaseDbContext db, ReleaseTrains train, Environments env, CancellationToken ct)
    {
        var tier = TierIndex(train.RiskTier);
        var all = await db.Set<ApprovalPolicies>().AsNoTracking().Where(p => p.ArchivedAt == null).ToListAsync(ct);
        return [.. all.Where(p => (p.EnvironmentKind == null || p.EnvironmentKind == env.Kind) && (p.MinRiskTier == null || tier >= TierIndex(p.MinRiskTier))).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
    }

    // ---- reads ----------------------------------------------------------------------------------------------------------------------

    public async Task<List<PolicyView>> ListAsync(bool includeArchived = false, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var rows = await db.Set<ApprovalPolicies>().AsNoTracking().Where(p => includeArchived || p.ArchivedAt == null).OrderBy(p => p.Name).ToListAsync(ct);
        return await ViewsAsync(db, rows, ct);
    }

    public async Task<PolicyView?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var row = await db.Set<ApprovalPolicies>().AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct);
        return row is null ? null : (await ViewsAsync(db, [row], ct))[0];
    }

    private static async Task<List<PolicyView>> ViewsAsync(ReleaseDbContext db, List<ApprovalPolicies> rows, CancellationToken ct)
    {
        var ids = rows.Select(r => r.Id).ToList();
        var seats = (await db.Set<ApprovalPolicyRequirements>().AsNoTracking().Where(s => ids.Contains(s.PolicyId)).ToListAsync(ct)).OrderBy(s => s.SortOrder).ThenBy(s => s.Label, StringComparer.OrdinalIgnoreCase).ToList();
        var teamIds = seats.Where(s => s.EligibleKind == "Team").Select(s => s.EligibleValue!).Distinct().ToList();
        var handles = await db.Set<Teams>().AsNoTracking().Where(t => teamIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => "@" + t.Handle, ct);
        return [.. rows.Select(p => new PolicyView(p.Id, p.Name, p.EnvironmentKind, p.MinRiskTier, p.RequireGatesCertified, p.RequireOpenWindow,
            [.. seats.Where(s => s.PolicyId == p.Id).Select(s => new SeatView(s.Id, s.Label, s.EligibleKind, s.EligibleValue, s.EligibleKind == "Team" ? handles.GetValueOrDefault(s.EligibleValue!) : s.EligibleValue, s.DueHours, s.SortOrder))],
            p.ArchivedAt is not null, p.Version))];
    }

    private static object Snapshot(ApprovalPolicies p, IEnumerable<ApprovalPolicyRequirements> seats) => new
    {
        p.Name, p.EnvironmentKind, p.MinRiskTier, p.RequireGatesCertified, p.RequireOpenWindow, archived = p.ArchivedAt is not null, p.Version,
        seats = seats.OrderBy(s => s.SortOrder).Select(s => new { s.Label, kind = s.EligibleKind, value = s.EligibleValue, s.DueHours }),
    };

    // ---- writes ---------------------------------------------------------------------------------------------------------------------

    public Task<ServiceResult<PolicyView>> CreateAsync(NewPolicy n, Actor actor, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var f = new List<GuardFailure>();
            if (!Managers.Contains(await RoleOf(db, actor.UserId) ?? "")) f.Add(new(ApprovalGuards.PolicyRole, "Approval policies are managed by an RTE, Release Manager or Governance Officer"));
            var p = new ApprovalPolicies
            {
                Id = Ids.New(), Name = (n.Name ?? "").Trim(), EnvironmentKind = Blank(n.EnvironmentKind), MinRiskTier = Blank(n.MinRiskTier), RequireGatesCertified = n.RequireGatesCertified, RequireOpenWindow = n.RequireOpenWindow,
                CreatedAt = Now, LastChangedByUserId = actor.UserId, LastChangedAt = Now,
            };
            f.AddRange(await CheckAsync(db, p, ct));
            var seats = await BuildSeatsAsync(db, p.Id, n.Seats, f, ct);
            if (f.Count > 0) return ServiceResult<PolicyView>.Fail(f);
            db.Set<ApprovalPolicies>().Add(p);
            await db.SaveChangesAsync(ct);   // the seats' boundary trigger reads the policy
            db.Set<ApprovalPolicyRequirements>().AddRange(seats);
            Audit(db, actor, null, Entity, p.Id, "Create", null, Snapshot(p, seats));
            await db.SaveChangesAsync(ct);
            return ServiceResult<PolicyView>.Ok((await ViewsAsync(db, [p], ct))[0]);
        }, ct);

    public Task<ServiceResult<PolicyView>> UpdateAsync(string id, PolicyPatch patch, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var p = await db.Set<ApprovalPolicies>().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (p is null) return ServiceResult<PolicyView>.NotFound("policy");
            if (VersionMismatch(expectedVersion, p.Version)) return ServiceResult<PolicyView>.Conflict(p);
            var f = new List<GuardFailure>();
            if (!Managers.Contains(await RoleOf(db, actor.UserId) ?? "")) f.Add(new(ApprovalGuards.PolicyRole, "Approval policies are managed by an RTE, Release Manager or Governance Officer"));
            var clear = (patch.Clear ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (clear.Except(Clearable, StringComparer.OrdinalIgnoreCase).FirstOrDefault() is { } unknown) f.Add(Bad($"\"{unknown}\" cannot be cleared; one of {string.Join(", ", Clearable)}"));
            var existing = await db.Set<ApprovalPolicyRequirements>().Where(s => s.PolicyId == id).ToListAsync(ct);
            var before = Snapshot(p, existing);
            if (patch.Name is not null) p.Name = patch.Name.Trim();
            if (patch.EnvironmentKind is not null) p.EnvironmentKind = Blank(patch.EnvironmentKind);
            if (patch.MinRiskTier is not null) p.MinRiskTier = Blank(patch.MinRiskTier);
            if (clear.Contains("environmentKind")) p.EnvironmentKind = null;
            if (clear.Contains("minRiskTier")) p.MinRiskTier = null;
            p.RequireGatesCertified = patch.RequireGatesCertified ?? p.RequireGatesCertified;
            p.RequireOpenWindow = patch.RequireOpenWindow ?? p.RequireOpenWindow;
            f.AddRange(await CheckAsync(db, p, ct));
            List<ApprovalPolicyRequirements>? seats = null;
            if (patch.Seats is not null) seats = await BuildSeatsAsync(db, p.Id, patch.Seats, f, ct);
            if (f.Count > 0) { db.Entry(p).State = EntityState.Unchanged; return ServiceResult<PolicyView>.Fail(f); }
            var seatsChanged = seats is not null && JsonSerializer.Serialize(SeatKey(seats)) != JsonSerializer.Serialize(SeatKey(existing));
            if (!db.Entry(p).Properties.Any(x => x.IsModified) && !seatsChanged) return ServiceResult<PolicyView>.Ok((await ViewsAsync(db, [p], ct))[0]);   // nothing to change: no Version bump, no audit row
            p.Version++; p.LastChangedByUserId = actor.UserId; p.LastChangedAt = Now;
            if (seatsChanged)
            {
                db.Set<ApprovalPolicyRequirements>().RemoveRange(existing);
                await db.SaveChangesAsync(ct);   // the replaced seats go first: (PolicyId, Label) is unique
                db.Set<ApprovalPolicyRequirements>().AddRange(seats!);
            }
            Audit(db, actor, null, Entity, p.Id, "Update", before, Snapshot(p, seatsChanged ? seats! : existing));
            await db.SaveChangesAsync(ct);
            return ServiceResult<PolicyView>.Ok((await ViewsAsync(db, [p], ct))[0]);
        }, ct);

    private static object SeatKey(IEnumerable<ApprovalPolicyRequirements> seats) => seats.OrderBy(s => s.SortOrder).Select(s => new { Label = s.Label.ToLowerInvariant(), s.EligibleKind, s.EligibleValue, s.DueHours });

    public Task<ServiceResult<PolicyView>> SetArchivedAsync(string id, bool archived, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var p = await db.Set<ApprovalPolicies>().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (p is null) return ServiceResult<PolicyView>.NotFound("policy");
            if (VersionMismatch(expectedVersion, p.Version)) return ServiceResult<PolicyView>.Conflict(p);
            if (!Managers.Contains(await RoleOf(db, actor.UserId) ?? "")) return ServiceResult<PolicyView>.Fail(new GuardFailure(ApprovalGuards.PolicyRole, "Approval policies are managed by an RTE, Release Manager or Governance Officer"));
            if ((p.ArchivedAt is not null) == archived) return ServiceResult<PolicyView>.Ok((await ViewsAsync(db, [p], ct))[0]);
            var seats = await db.Set<ApprovalPolicyRequirements>().AsNoTracking().Where(s => s.PolicyId == id).ToListAsync(ct);
            var before = Snapshot(p, seats);
            p.ArchivedAt = archived ? Now : null; p.Version++; p.LastChangedByUserId = actor.UserId; p.LastChangedAt = Now;
            Audit(db, actor, null, Entity, p.Id, archived ? "Archive" : "Unarchive", before, Snapshot(p, seats));
            await db.SaveChangesAsync(ct);
            return ServiceResult<PolicyView>.Ok((await ViewsAsync(db, [p], ct))[0]);
        }, ct);

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static async Task<List<GuardFailure>> CheckAsync(ReleaseDbContext db, ApprovalPolicies p, CancellationToken ct)
    {
        var f = new List<GuardFailure>();
        if (p.Name.Length is 0 or > 100) f.Add(Bad("A policy name is 1 to 100 characters"));
        else if (await db.Set<ApprovalPolicies>().AnyAsync(x => x.Name == p.Name && x.Id != p.Id, ct)) f.Add(new(ApprovalGuards.PolicyNameTaken, $"A policy called \"{p.Name}\" exists"));
        if (p.EnvironmentKind is not null && !EnvironmentService.Kinds.Contains(p.EnvironmentKind)) f.Add(Bad("An environment kind is Staging or Production"));
        if (p.MinRiskTier is not null && !RiskTiers.Contains(p.MinRiskTier)) f.Add(Bad($"A risk tier is one of {string.Join(", ", RiskTiers)}"));
        return f;
    }

    private static async Task<List<ApprovalPolicyRequirements>> BuildSeatsAsync(ReleaseDbContext db, string policyId, IReadOnlyList<SeatInput>? input, List<GuardFailure> f, CancellationToken ct)
    {
        var seats = new List<ApprovalPolicyRequirements>();
        if (input is null || input.Count == 0) { f.Add(Bad("A policy needs at least one seat")); return seats; }
        if (input.Count > MaxSeats) { f.Add(Bad($"A policy has at most {MaxSeats} seats")); return seats; }
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < input.Count; i++)
        {
            var s = input[i];
            var label = (s.Label ?? "").Trim();
            if (label.Length is 0 or > 100) { f.Add(Bad($"seats[{i}].label is 1 to 100 characters")); continue; }
            if (!labels.Add(label)) { f.Add(Bad($"Two seats are called \"{label}\"")); continue; }
            var kind = s.Kind ?? "";
            string? value = Blank(s.Value);
            if (!SeatKinds.Contains(kind)) { f.Add(Bad($"seats[{i}].kind is one of {string.Join(", ", SeatKinds)}")); continue; }
            switch (kind)
            {
                case "ReleaseOwner":
                    if (value is not null) { f.Add(Bad($"seats[{i}]: the release owner seat takes no value")); continue; }
                    break;
                case "WorkspaceRole":
                    if (value is null || !Roles.All.Contains(value)) { f.Add(Bad($"seats[{i}].value is a workspace role: {string.Join(", ", Roles.All)}")); continue; }
                    break;
                case "Team":
                    var handle = value is null ? null : value.TrimStart('@');
                    var team = value is null ? null : await db.Set<Teams>().AsNoTracking().FirstOrDefaultAsync(t => t.Id == value || t.Handle == handle, ct);
                    if (team is null) { f.Add(Bad($"seats[{i}].value is a team of this workspace (its id or @handle)")); continue; }
                    value = team.Id;
                    break;
            }
            var due = s.DueHours ?? 24;
            if (due is < 1 or > 720) { f.Add(Bad($"seats[{i}].dueHours is 1 to 720")); continue; }
            seats.Add(new ApprovalPolicyRequirements { Id = Ids.New(), PolicyId = policyId, Label = label, EligibleKind = kind, EligibleValue = value, DueHours = due, SortOrder = (i + 1) * 10 });
        }
        return seats;
    }

    // ---- coverage -------------------------------------------------------------------------------------------------------------------

    /// <summary>The policy coverage metric: of the Production releases whose target date falls in the range (default: from 90 days ago on), how many an active policy governs.</summary>
    public async Task<PolicyCoverage> CoverageAsync(DateOnly? from = null, DateOnly? to = null, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var today = DateOnly.FromDateTime(Now);
        var lo = from ?? today.AddDays(-90);
        var hi = to ?? today.AddYears(10);
        var production = db.Set<Environments>().AsNoTracking().Where(e => e.Kind == "Production").Select(e => e.Id);
        var trainIds = await db.Set<TrainEnvironments>().AsNoTracking().Where(te => production.Contains(te.EnvironmentId)).Select(te => te.ReleaseTrainId).Distinct().ToListAsync(ct);
        var trains = await db.Set<ReleaseTrains>().AsNoTracking().Where(t => trainIds.Contains(t.Id) && t.ArchivedAt == null && t.CurrentStatus != "Aborted" && t.TargetReleaseDate >= lo && t.TargetReleaseDate <= hi)
            .OrderBy(t => t.TargetReleaseDate).ToListAsync(ct);
        var policies = await db.Set<ApprovalPolicies>().AsNoTracking().Where(p => p.ArchivedAt == null && (p.EnvironmentKind == null || p.EnvironmentKind == "Production")).ToListAsync(ct);
        bool Governed(ReleaseTrains t) => policies.Any(p => p.MinRiskTier == null || TierIndex(t.RiskTier) >= TierIndex(p.MinRiskTier));
        var ids = trains.Select(t => t.Id).ToList();
        var approved = (await db.Set<Approvals>().AsNoTracking().Where(a => ids.Contains(a.ReleaseTrainId) && a.Status == "Approved" && production.Contains(a.EnvironmentId)).Select(a => a.ReleaseTrainId).Distinct().ToListAsync(ct)).ToHashSet();
        var governed = trains.Where(Governed).ToList();
        var pct = trains.Count == 0 ? (double?)null : Math.Round(governed.Count * 100.0 / trains.Count, 1);
        return new(lo.ToString("yyyy-MM-dd"), hi.ToString("yyyy-MM-dd"), trains.Count, governed.Count, trains.Count(t => approved.Contains(t.Id)), pct,
            [.. trains.Where(t => !Governed(t)).Take(20).Select(t => new UngovernedRelease(t.Id, t.ReleaseKey, t.Title, t.RiskTier))]);
    }
}
