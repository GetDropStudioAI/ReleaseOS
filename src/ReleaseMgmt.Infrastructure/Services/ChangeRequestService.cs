using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

public sealed record ReleaseRef(string Id, string Title);

public sealed record ChangeDecisionView(string Id, string Decision, bool Retroactive, string? MeetingId, string? Rationale, NamedRef DecidedBy, string DecidedAt);

/// <summary><c>NeedsReview</c>: an Emergency change that has been implemented and has no review by a second person yet (REOS-177 rule 3). <c>Version</c> is the If-Match value.</summary>
public sealed record ChangeView(string Id, string Key, string Title, string? Description, string ChangeType, string Status, string RiskLevel, string? RiskFactors,
    NamedRef RequestedBy, NamedRef CreatedBy, ReleaseRef? Release, string? GoNoGoDecisionId, string? PlannedStartAt, string? PlannedEndAt, string? RollbackNote,
    string? SubmittedAt, NamedRef? DecidedBy, string? DecidedAt, string? ImplementedAt, string? ClosedAt, string CreatedAt, string? LastChangedAt, int Version,
    IReadOnlyList<ChangeDecisionView> Decisions, bool NeedsReview, RiskView? Risk = null, GoNoGoRef? GoNoGo = null);

/// <summary>REOS-176: the risk level with every rule that was checked and what it found (<c>Applied</c> ones add <c>Points</c>; the level is the band of the score). <c>LevelRule</c> says the bands in words.</summary>
public sealed record RiskView(string Level, int Score, string LevelRule, IReadOnlyList<RiskFactor> Factors);

public sealed record GoNoGoRef(string Id, string Decision, string DecidedAt);

public sealed record NewChange(string? Title, string? ChangeType, string? Description = null, string? RequestedByUserId = null,
    string? ReleaseTrainId = null, string? GoNoGoDecisionId = null, DateTime? PlannedStartAt = null, DateTime? PlannedEndAt = null, string? RollbackNote = null);

/// <summary>Null leaves a field as it is; <see cref="Clear"/> names the optional fields to empty (description, releaseTrainId, goNoGoDecisionId, window, rollbackNote).</summary>
public sealed record ChangePatch(string? Title = null, string? ChangeType = null, string? Description = null,
    string? ReleaseTrainId = null, string? GoNoGoDecisionId = null, DateTime? PlannedStartAt = null, DateTime? PlannedEndAt = null, string? RollbackNote = null, IReadOnlyList<string>? Clear = null);

public sealed record DecisionInput(string? Decision, string? Rationale = null, string? MeetingId = null, bool Retroactive = false);

/// <summary>
/// REOS-175 and REOS-177 (D56, D63): change requests of the CAB module. Status changes only through this service (CLAUDE.md rule 2); every write is one transaction with one
/// <c>ChangeRequest</c> audit row (rule 3), clock from the injected TimeProvider (rule 4), UUIDv7 ids, If-Match with the current row on 409. The module guard is at the endpoints; the
/// service itself does not know whether the module is on (the tables simply stay empty while it is off).
/// <para>Separation of duties (REOS-177): the service checks first and answers 422 with a readable message; the same three rules are triggers in <c>db/schema.sql</c> as the backstop.
/// A refused attempt is audited (<c>DecisionRefused</c>, <c>CloseRefused</c>) in its own transaction, because the refused call itself rolls back.</para>
/// </summary>
public sealed class ChangeRequestService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IRealtimePublisher? realtime = null, ChangeRiskOptions? risk = null) : ServiceBase(dbf, time, realtime)
{
    private readonly ChangeRiskOptions _risk = risk ?? new();
    private const string Entity = "ChangeRequest";
    internal const string ModuleEvent = "CabChanged";
    private readonly IRealtimePublisher? _realtime = realtime;
    private readonly IDbContextFactory<ReleaseDbContext> _dbf = dbf;

    internal static string Iso(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
    private static GuardFailure Bad(string m) => new(CabGuards.ChangeInvalid, m);
    private static readonly string[] Clearable = ["description", "releaseTrainId", "goNoGoDecisionId", "window", "rollbackNote"];

    // ---- reads -----------------------------------------------------------------------------------------------------------------------

    public async Task<List<ChangeView>> ListAsync(string? status = null, string? type = null, string? releaseTrainId = null, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var q = db.Set<ChangeRequests>().AsNoTracking().AsQueryable();
        if (status is not null) q = q.Where(c => c.Status == status);
        if (type is not null) q = q.Where(c => c.ChangeType == type);
        if (releaseTrainId is not null) q = q.Where(c => c.ReleaseTrainId == releaseTrainId);
        var rows = await q.OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.ChangeKey).ToListAsync(ct);
        return await ViewsAsync(db, rows, ct);
    }

    public async Task<ChangeView?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var row = await db.Set<ChangeRequests>().AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct);
        return row is null ? null : (await ViewsAsync(db, [row], ct))[0];
    }

    private static async Task<List<ChangeView>> ViewsAsync(ReleaseDbContext db, List<ChangeRequests> rows, CancellationToken ct)
    {
        var ids = rows.Select(r => r.Id).ToList();
        var decisions = (await db.Set<CabDecisions>().AsNoTracking().Where(d => ids.Contains(d.ChangeRequestId)).OrderBy(d => d.DecidedAt).ThenBy(d => d.Id).ToListAsync(ct)).ToLookup(d => d.ChangeRequestId);
        var userIds = rows.SelectMany(r => new[] { r.RequestedByUserId, r.CreatedByUserId, r.DecidedByUserId }).Concat(decisions.SelectMany(g => g.Select(d => d.DecidedByUserId))).OfType<string>().Distinct().ToList();
        var names = await db.Set<Users>().AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        NamedRef Who(string id) => new(id, names.GetValueOrDefault(id, id));
        var trainIds = rows.Select(r => r.ReleaseTrainId).OfType<string>().Distinct().ToList();
        var trains = await db.Set<ReleaseTrains>().AsNoTracking().Where(t => trainIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Title, ct);
        var goIds = rows.Select(r => r.GoNoGoDecisionId).OfType<string>().Distinct().ToList();
        var gos = await db.Set<GoNoGoDecisions>().AsNoTracking().Where(g => goIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, ct);
        return [.. rows.Select(r =>
        {
            var ds = decisions[r.Id].ToList();
            var reviewed = ds.Any(d => CabDecisionKinds.IsVerdict(d.Decision) && d.DecidedByUserId != r.RequestedByUserId);
            return new ChangeView(r.Id, r.ChangeKey, r.Title, r.Description, r.ChangeType, r.Status, r.RiskLevel, r.RiskFactors, Who(r.RequestedByUserId), Who(r.CreatedByUserId),
                r.ReleaseTrainId is { } t ? new ReleaseRef(t, trains.GetValueOrDefault(t, t)) : null, r.GoNoGoDecisionId,
                r.PlannedStartAt is { } ps ? Iso(ps) : null, r.PlannedEndAt is { } pe ? Iso(pe) : null, r.RollbackNote, r.SubmittedAt is { } sa ? Iso(sa) : null,
                r.DecidedByUserId is { } db2 ? Who(db2) : null, r.DecidedAt is { } da ? Iso(da) : null, r.ImplementedAt is { } ia ? Iso(ia) : null, r.ClosedAt is { } ca ? Iso(ca) : null,
                Iso(r.CreatedAt), r.LastChangedAt is { } lc ? Iso(lc) : null, r.Version,
                [.. ds.Select(d => new ChangeDecisionView(d.Id, d.Decision, d.Retroactive, d.MeetingId, d.Rationale, Who(d.DecidedByUserId), Iso(d.DecidedAt)))],
                r.ChangeType == ChangeTypes.Emergency && r.Status == ChangeStatuses.Implemented && !reviewed,
                ParseRisk(r), r.GoNoGoDecisionId is { } g && gos.TryGetValue(g, out var go) ? new GoNoGoRef(go.Id, go.Decision, Iso(go.DecidedAt)) : null);
        })];
    }

    // ---- risk (REOS-176) -------------------------------------------------------------------------------------------------------------

    private sealed record StoredRisk(int Score, List<RiskFactor> Factors);

    /// <summary>The stored factors, or null for a row that predates the rules (its RiskFactors is free text, shown as it is).</summary>
    private static RiskView? ParseRisk(ChangeRequests r)
    {
        if (r.RiskFactors is not { Length: > 0 } text || text[0] != '{') return null;
        try
        {
            var stored = System.Text.Json.JsonSerializer.Deserialize<StoredRisk>(text, Json);
            return stored is null ? null : new RiskView(r.RiskLevel, stored.Score, ChangeRiskRules.LevelRuleText, stored.Factors);
        }
        catch (System.Text.Json.JsonException) { return null; }   // not ours: shown as free text by the view's RiskFactors
    }

    /// <summary>
    /// Runs the five rules for the change as it now stands and stores the level and the factors (as JSON in RiskFactors) on the row, so the list and the detail can never disagree. Called on
    /// create, on every edit of a Draft and again on submit (a freeze window may have been added meanwhile). A change with no release is matched only by windows that cover all products.
    /// </summary>
    private async Task ApplyRiskAsync(ReleaseDbContext db, ChangeRequests c, CancellationToken ct)
    {
        var ws = Memberships.WorkspaceOf(db);
        var products = c.ReleaseTrainId is null ? [] : await db.Set<BundledProducts>().AsNoTracking().Where(p => p.ReleaseTrainId == c.ReleaseTrainId).Select(p => new { p.ProductName, p.CatalogServiceId }).ToListAsync(ct);
        var freezes = new List<RiskFreeze>();
        if (c.PlannedStartAt is { } s && c.PlannedEndAt is { } e)
        {
            var near = TimeSpan.FromHours(_risk.NearFreezeHours);
            var lo = s - near; var hi = e + near;
            var windows = await db.Set<FreezeWindows>().AsNoTracking().Where(f => f.WorkspaceId == ws && f.StartsAt < hi && f.EndsAt > lo).OrderBy(f => f.StartsAt).ToListAsync(ct);
            foreach (var f in windows)
            {
                var pattern = f.ProductPattern;   // the same GLOB predicate FreezeService uses; a window with no pattern covers every product
                if (pattern is null || (c.ReleaseTrainId is not null && await db.Set<BundledProducts>().AnyAsync(p => p.ReleaseTrainId == c.ReleaseTrainId && EF.Functions.Glob(p.ProductName, pattern), ct)))
                    freezes.Add(new RiskFreeze(f.Name, f.Kind, f.StartsAt, f.EndsAt));
            }
        }
        var failures = new List<RiskPriorFailure>();
        var services = products.Select(p => p.CatalogServiceId).OfType<string>().Distinct().ToList();
        if (c.ReleaseTrainId is not null && services.Count > 0)
        {
            var since = Now.AddDays(-_risk.PriorFailureLookbackDays);
            var others = await (from o in db.Set<ChangeRequests>().AsNoTracking()
                                join t in db.Set<ReleaseTrains>().AsNoTracking() on o.ReleaseTrainId equals t.Id
                                where o.WorkspaceId == ws && o.Id != c.Id && o.ReleaseTrainId != c.ReleaseTrainId && o.Status != ChangeStatuses.Cancelled && o.Status != ChangeStatuses.Draft
                                    && (t.CloseCode == "Unsuccessful" || t.CurrentStatus == "Aborted")
                                select new { o.ChangeKey, TrainId = t.Id, t.Title, t.CloseCode, t.CurrentStatus, At = t.ActualEndAt ?? t.LastChangedAt ?? t.CreatedAt }).ToListAsync(ct);
            foreach (var o in others.Where(x => x.At >= since))
            {
                var shared = await db.Set<BundledProducts>().AsNoTracking().Where(p => p.ReleaseTrainId == o.TrainId && p.CatalogServiceId != null && services.Contains(p.CatalogServiceId))
                    .Join(db.Set<CatalogServices>().AsNoTracking(), p => p.CatalogServiceId, sv => sv.Id, (p, sv) => sv.Name).FirstOrDefaultAsync(ct);
                if (shared is not null) failures.Add(new RiskPriorFailure(o.ChangeKey, shared, o.Title, o.CurrentStatus == "Aborted" ? "Aborted" : "Unsuccessful", o.At));
            }
        }
        var a = ChangeRiskRules.Assess(new RiskInput(c.ChangeType, c.PlannedStartAt, c.PlannedEndAt, c.RollbackNote, c.ReleaseTrainId is not null, products.Count, freezes, failures), _risk);
        c.RiskLevel = a.Level;
        c.RiskFactors = System.Text.Json.JsonSerializer.Serialize(new StoredRisk(a.Score, [.. a.Factors]), Json);
    }

    private static object Snapshot(ChangeRequests c) => new
    {
        c.ChangeKey, c.Title, c.ChangeType, c.Status, c.RiskLevel, c.RequestedByUserId, c.CreatedByUserId, c.ReleaseTrainId, c.GoNoGoDecisionId,
        plannedStartAt = c.PlannedStartAt is { } s ? Iso(s) : null, plannedEndAt = c.PlannedEndAt is { } e ? Iso(e) : null, hasRollbackNote = !string.IsNullOrWhiteSpace(c.RollbackNote), c.DecidedByUserId, c.Version,
    };

    // ---- create and edit -------------------------------------------------------------------------------------------------------------

    private static async Task<List<GuardFailure>> CheckAsync(ReleaseDbContext db, ChangeRequests c, string requester, CancellationToken ct)
    {
        var f = new List<GuardFailure>();
        if (c.Title.Length is 0 or > 200) f.Add(Bad("A title is 1 to 200 characters"));
        if (!ChangeTypes.All.Contains(c.ChangeType)) f.Add(Bad("Type must be Standard, Normal or Emergency"));
        if (c.Description is { Length: > 4000 } || c.RollbackNote is { Length: > 4000 }) f.Add(Bad("Description and rollback note are at most 4000 characters"));
        if ((c.PlannedStartAt is null) != (c.PlannedEndAt is null)) f.Add(new(CabGuards.ChangeInvalid, "A planned window needs both a start and an end"));
        else if (c.PlannedStartAt is { } s && c.PlannedEndAt is { } e && e <= s) f.Add(Bad("The planned window must end after it starts"));
        if (await Memberships.RoleOf(db, requester, ct) is null) f.Add(new(CabGuards.CabNotMember, "The requester is not a member of this workspace"));
        if (c.ReleaseTrainId is not null && !await db.Set<ReleaseTrains>().AnyAsync(t => t.Id == c.ReleaseTrainId, ct)) f.Add(Bad("The release does not exist in this workspace"));
        if (c.GoNoGoDecisionId is not null)
        {
            var g = await db.Set<GoNoGoDecisions>().AsNoTracking().Where(d => d.Id == c.GoNoGoDecisionId).Select(d => d.ReleaseTrainId).SingleOrDefaultAsync(ct);
            if (g is null || !await db.Set<ReleaseTrains>().AnyAsync(t => t.Id == g, ct)) f.Add(Bad("The Go/No-Go decision does not exist in this workspace"));
            else if (c.ReleaseTrainId is not null && g != c.ReleaseTrainId) f.Add(Bad("The Go/No-Go decision belongs to another release"));
        }
        return f;
    }

    /// <summary>The next CHG number of the workspace: one more than the highest CHG-n there is. SQLite has one writer, so two creates cannot take the same number.</summary>
    private static async Task<string> NextKeyAsync(ReleaseDbContext db, CancellationToken ct)
    {
        var ws = Memberships.WorkspaceOf(db);
        var keys = await db.Set<ChangeRequests>().IgnoreQueryFilters().Where(c => c.WorkspaceId == ws).Select(c => c.ChangeKey).ToListAsync(ct);
        var max = keys.Select(k => k.StartsWith("CHG-") && int.TryParse(k[4..], out var n) ? n : 0).DefaultIfEmpty(0).Max();
        return $"CHG-{max + 1:0000}";
    }

    public Task<ServiceResult<ChangeView>> CreateAsync(NewChange n, Actor actor, CancellationToken ct = default)
    {
        string? ws = null;
        var task = RunAsync(async db =>
        {
            ws = Memberships.WorkspaceOf(db);
            var now = Now;
            var requester = string.IsNullOrWhiteSpace(n.RequestedByUserId) ? actor.UserId : n.RequestedByUserId;
            var c = new ChangeRequests
            {
                Id = Ids.New(), ChangeKey = await NextKeyAsync(db, ct), Title = (n.Title ?? "").Trim(), Description = Blank(n.Description), ChangeType = n.ChangeType ?? "",
                RequestedByUserId = requester, CreatedByUserId = actor.UserId,
                ReleaseTrainId = Blank(n.ReleaseTrainId), GoNoGoDecisionId = Blank(n.GoNoGoDecisionId), PlannedStartAt = n.PlannedStartAt, PlannedEndAt = n.PlannedEndAt, RollbackNote = Blank(n.RollbackNote),
                CreatedAt = now, LastChangedByUserId = actor.UserId, LastChangedAt = now,
            };
            if (c.GoNoGoDecisionId is not null && c.ReleaseTrainId is null)   // a Go/No-Go decision names its release
                c.ReleaseTrainId = await db.Set<GoNoGoDecisions>().Where(d => d.Id == c.GoNoGoDecisionId).Select(d => d.ReleaseTrainId).SingleOrDefaultAsync(ct);
            var f = await CheckAsync(db, c, requester, ct);
            if (f.Count > 0) return ServiceResult<ChangeView>.Fail(f);
            await ApplyRiskAsync(db, c, ct);
            db.Set<ChangeRequests>().Add(c);
            Audit(db, actor, c.ReleaseTrainId, Entity, c.Id, "Create", null, Snapshot(c));
            await db.SaveChangesAsync(ct);
            return ServiceResult<ChangeView>.Ok((await ViewsAsync(db, [c], ct))[0]);
        }, ct);
        return Announce(task, () => ws);
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public Task<ServiceResult<ChangeView>> UpdateAsync(string id, ChangePatch p, Actor actor, int? expectedVersion, CancellationToken ct = default)
    {
        string? ws = null;
        var task = RunAsync(async db =>
        {
            ws = Memberships.WorkspaceOf(db);
            var c = await db.Set<ChangeRequests>().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (c is null) return ServiceResult<ChangeView>.NotFound("change request");
            if (VersionMismatch(expectedVersion, c.Version)) return ServiceResult<ChangeView>.Conflict((await ViewsAsync(db, [c], ct))[0]);
            if (c.Status != ChangeStatuses.Draft) return ServiceResult<ChangeView>.Fail(new GuardFailure(CabGuards.ChangeNotEditable, $"A change request is edited while it is a Draft; this one is {c.Status}"));
            var clear = (p.Clear ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (clear.Except(Clearable, StringComparer.OrdinalIgnoreCase).FirstOrDefault() is { } unknown)
                return ServiceResult<ChangeView>.Fail(Bad($"\"{unknown}\" cannot be cleared; one of {string.Join(", ", Clearable)}"));
            var before = Snapshot(c);
            if (p.Title is not null) c.Title = p.Title.Trim();
            if (p.ChangeType is not null) c.ChangeType = p.ChangeType;
            if (p.Description is not null) c.Description = Blank(p.Description);
            if (p.ReleaseTrainId is not null) c.ReleaseTrainId = Blank(p.ReleaseTrainId);
            if (p.GoNoGoDecisionId is not null) c.GoNoGoDecisionId = Blank(p.GoNoGoDecisionId);
            if (p.RollbackNote is not null) c.RollbackNote = Blank(p.RollbackNote);
            if (p.PlannedStartAt is not null) c.PlannedStartAt = p.PlannedStartAt;
            if (p.PlannedEndAt is not null) c.PlannedEndAt = p.PlannedEndAt;
            if (clear.Contains("description")) c.Description = null;
            if (clear.Contains("releaseTrainId")) c.ReleaseTrainId = null;
            if (clear.Contains("goNoGoDecisionId")) c.GoNoGoDecisionId = null;
            if (clear.Contains("rollbackNote")) c.RollbackNote = null;
            if (clear.Contains("window")) { c.PlannedStartAt = null; c.PlannedEndAt = null; }
            var f = await CheckAsync(db, c, c.RequestedByUserId, ct);
            if (f.Count > 0) { db.Entry(c).State = EntityState.Unchanged; return ServiceResult<ChangeView>.Fail(f); }
            await ApplyRiskAsync(db, c, ct);
            c.Version++; c.LastChangedByUserId = actor.UserId; c.LastChangedAt = Now;
            Audit(db, actor, c.ReleaseTrainId, Entity, c.Id, "Update", before, Snapshot(c));
            await db.SaveChangesAsync(ct);
            return ServiceResult<ChangeView>.Ok((await ViewsAsync(db, [c], ct))[0]);
        }, ct);
        return Announce(task, () => ws);
    }

    // ---- status moves (only here: CLAUDE.md rule 2) ------------------------------------------------------------------------------------

    public Task<ServiceResult<ChangeView>> SubmitAsync(string id, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        MoveAsync(id, ChangeStatuses.Submitted, "Submit", actor, expectedVersion,
            async Task<GuardFailure?> (db, c) =>
            {
                if (string.IsNullOrWhiteSpace(c.RollbackNote)) return new GuardFailure(CabGuards.ChangeNeedsRollbackNote, "A change is submitted with a rollback note: how it is undone if it goes wrong");
                if (c.ChangeType != ChangeTypes.Emergency && c.PlannedStartAt is null) return new GuardFailure(CabGuards.ChangeNeedsWindow, "A Standard or Normal change is submitted with a planned window (start and end, UTC)");
                await ApplyRiskAsync(db, c, ct);   // the rules are run again as it goes to the CAB: a freeze window or a failed release may have appeared since it was drafted
                return null;
            },
            c => c.SubmittedAt = Now, ct);

    public Task<ServiceResult<ChangeView>> CancelAsync(string id, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        MoveAsync(id, ChangeStatuses.Cancelled, "Cancel", actor, expectedVersion, null, null, ct);

    /// <summary>Approved &gt; Implemented, or Submitted &gt; Implemented for an Emergency change (its review by a second person then follows, see <see cref="DecideAsync"/>).</summary>
    public Task<ServiceResult<ChangeView>> ImplementAsync(string id, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        MoveAsync(id, ChangeStatuses.Implemented, "Implement", actor, expectedVersion, null, c => c.ImplementedAt = Now, ct);

    /// <summary>REOS-177 rule 3: an Emergency change is closed only after someone other than the requester has reviewed it.</summary>
    public Task<ServiceResult<ChangeView>> CloseAsync(string id, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        MoveAsync(id, ChangeStatuses.Closed, "Close", actor, expectedVersion, async (db, c) =>
            c.ChangeType == ChangeTypes.Emergency
            && !await db.Set<CabDecisions>().AnyAsync(d => d.ChangeRequestId == c.Id && (d.Decision == CabDecisionKinds.Approved || d.Decision == CabDecisionKinds.Rejected) && d.DecidedByUserId != c.RequestedByUserId, ct)
                ? new(CabGuards.ChangeEmergencyNeedsReview, CabRuleText.EmergencyNeedsReview) : null,
            c => c.ClosedAt = Now, ct);

    private static readonly HashSet<string> SeparationGuards = [CabGuards.ChangeSelfApproval, CabGuards.ChangeChairSoleApprover, CabGuards.ChangeEmergencyNeedsReview];

    private Task<ServiceResult<ChangeView>> MoveAsync(string id, string to, string action, Actor actor, int? expectedVersion, Func<ReleaseDbContext, ChangeRequests, Task<GuardFailure?>>? extra, Action<ChangeRequests>? stamp, CancellationToken ct)
    {
        string? ws = null;
        (string? TrainId, GuardFailure Failure)? refused = null;
        var task = RunAsync(async db =>
        {
            ws = Memberships.WorkspaceOf(db);
            var c = await db.Set<ChangeRequests>().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (c is null) return ServiceResult<ChangeView>.NotFound("change request");
            if (VersionMismatch(expectedVersion, c.Version)) return ServiceResult<ChangeView>.Conflict((await ViewsAsync(db, [c], ct))[0]);
            if (await Memberships.RoleOf(db, actor.UserId, ct) is null) return ServiceResult<ChangeView>.Fail(new GuardFailure(CabGuards.CabNotMember, "You are not a member of this workspace"));
            if (!CabRules.IsLegalChangeMove(c.Status, to))
                return ServiceResult<ChangeView>.Fail(new GuardFailure(CabGuards.IllegalChangeTransition, $"A change request cannot move from {c.Status} to {to}"));
            if (c.Status == ChangeStatuses.Submitted && to == ChangeStatuses.Implemented && c.ChangeType != ChangeTypes.Emergency)
                return ServiceResult<ChangeView>.Fail(new GuardFailure(CabGuards.OnlyEmergencyBeforeApproval, "Only an Emergency change can be implemented before it is approved"));
            var before = Snapshot(c);
            if (extra is not null && await extra(db, c) is { } f)
            {
                if (SeparationGuards.Contains(f.Guard)) refused = (c.ReleaseTrainId, f);
                return ServiceResult<ChangeView>.Fail(f);
            }
            c.Status = to; stamp?.Invoke(c);
            c.Version++; c.LastChangedByUserId = actor.UserId; c.LastChangedAt = Now;
            Audit(db, actor, c.ReleaseTrainId, Entity, c.Id, action, before, Snapshot(c));
            await db.SaveChangesAsync(ct);
            return ServiceResult<ChangeView>.Ok((await ViewsAsync(db, [c], ct))[0]);
        }, ct);
        return AnnounceRefusal(task, () => ws, () => refused, id, action + "Refused", actor, ct);
    }

    // ---- decisions (REOS-177) --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Records one person's decision on a change. Approved or Rejected by someone other than the requester moves the change (Approved or Rejected) in the same transaction; Deferred only
    /// records. For an Emergency change that has been implemented, <c>Retroactive</c> records the review by a second person; it moves nothing (the change is closed with <see cref="CloseAsync"/>).
    /// Refusals (REOS-177), each audited as <c>DecisionRefused</c>: <c>ChangeSelfApproval</c> (rule 1) and <c>ChangeChairSoleApprover</c> (rule 2, the chair who raised the change approves only
    /// after a different approver has).
    /// </summary>
    public Task<ServiceResult<ChangeView>> DecideAsync(string id, DecisionInput d, Actor actor, int? expectedVersion, CancellationToken ct = default)
    {
        string? ws = null;
        (string? TrainId, GuardFailure Failure)? refused = null;
        var task = RunAsync(async db =>
        {
            ws = Memberships.WorkspaceOf(db);
            var c = await db.Set<ChangeRequests>().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (c is null) return ServiceResult<ChangeView>.NotFound("change request");
            if (VersionMismatch(expectedVersion, c.Version)) return ServiceResult<ChangeView>.Conflict((await ViewsAsync(db, [c], ct))[0]);
            var kind = d.Decision ?? "";
            if (!CabDecisionKinds.All.Contains(kind)) return ServiceResult<ChangeView>.Fail(Bad("Decision must be Approved, Rejected or Deferred"));
            if (d.Rationale is { Length: > 2000 }) return ServiceResult<ChangeView>.Fail(Bad("A rationale is at most 2000 characters"));
            if (kind == CabDecisionKinds.Rejected && string.IsNullOrWhiteSpace(d.Rationale)) return ServiceResult<ChangeView>.Fail(Bad("A rejection needs a rationale"));
            if (d.Retroactive && !CabDecisionKinds.IsVerdict(kind)) return ServiceResult<ChangeView>.Fail(Bad("A retroactive review is an approval or a rejection"));
            if (await Memberships.RoleOf(db, actor.UserId, ct) is null) return ServiceResult<ChangeView>.Fail(new GuardFailure(CabGuards.CabNotMember, "You are not a member of this workspace"));

            // rule 1: the requester does not approve or reject their own change (checked first, like the trigger)
            if (CabRules.IsSelfApproval(c.RequestedByUserId, actor.UserId, kind))
            {
                var f = new GuardFailure(CabGuards.ChangeSelfApproval, CabRuleText.SelfApproval);
                refused = (c.ReleaseTrainId, f);
                return ServiceResult<ChangeView>.Fail(f);
            }
            if (d.Retroactive)
            {
                if (c.ChangeType != ChangeTypes.Emergency || c.Status != ChangeStatuses.Implemented)
                    return ServiceResult<ChangeView>.Fail(new GuardFailure(CabGuards.ChangeNotAwaitingDecision, "A retroactive review is only for an Emergency change that has been implemented"));
            }
            else if (c.Status != ChangeStatuses.Submitted)
                return ServiceResult<ChangeView>.Fail(new GuardFailure(CabGuards.ChangeNotAwaitingDecision, $"A decision is recorded while the change is waiting for the CAB (Submitted); this one is {c.Status}"));

            var meetingId = Blank(d.MeetingId);
            if (meetingId is not null)
            {
                var m = await db.Set<CabMeetings>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == meetingId, ct);
                if (m is null) return ServiceResult<ChangeView>.Fail(new GuardFailure(CabGuards.CabMeetingInvalid, "The meeting does not exist in this workspace"));
                if (m.Status == CabMeetingStatuses.Cancelled) return ServiceResult<ChangeView>.Fail(new GuardFailure(CabGuards.CabMeetingClosed, "A cancelled meeting records no decisions"));
            }

            // rule 2: the chair who raised the change is not its only approver. Chair of the named meeting, or of any meeting with the change on its agenda (so recording outside a meeting does not dodge it).
            if (kind == CabDecisionKinds.Approved && !d.Retroactive)
            {
                var chairs = await db.Set<CabMeetings>().AsNoTracking().AnyAsync(m => m.ChairUserId == actor.UserId
                    && (m.Id == meetingId || db.Set<CabAgendaItems>().Any(a => a.MeetingId == m.Id && a.ChangeRequestId == c.Id)), ct);
                var others = await db.Set<CabDecisions>().AsNoTracking().Where(x => x.ChangeRequestId == c.Id && x.Decision == CabDecisionKinds.Approved && !x.Retroactive && x.DecidedByUserId != actor.UserId)
                    .Select(x => x.DecidedByUserId).Distinct().ToListAsync(ct);
                if (CabRules.ChairIsSoleApprover(actor.UserId, chairs, c.RequestedByUserId, c.CreatedByUserId, others))
                {
                    var f = new GuardFailure(CabGuards.ChangeChairSoleApprover, CabRuleText.ChairSoleApprover);
                    refused = (c.ReleaseTrainId, f);
                    return ServiceResult<ChangeView>.Fail(f);
                }
            }

            var now = Now;
            var decision = new CabDecisions { Id = Ids.New(), ChangeRequestId = c.Id, MeetingId = meetingId, Decision = kind, Retroactive = d.Retroactive, Rationale = Blank(d.Rationale), DecidedByUserId = actor.UserId, DecidedAt = now };
            db.Set<CabDecisions>().Add(decision);
            var before = Snapshot(c);
            var moves = CabDecisionKinds.IsVerdict(kind) && !d.Retroactive;
            if (moves)
            {
                c.Status = kind;   // Approved or Rejected: the decision's own words
                c.DecidedByUserId = actor.UserId; c.DecidedAt = now;
                c.Version++; c.LastChangedByUserId = actor.UserId; c.LastChangedAt = now;
            }
            Audit(db, actor, c.ReleaseTrainId, Entity, c.Id, d.Retroactive ? "RetroactiveReview" : "Decide", before, new { decision = kind, retroactive = d.Retroactive, meetingId, decisionId = decision.Id, status = c.Status, version = c.Version });
            await db.SaveChangesAsync(ct);
            return ServiceResult<ChangeView>.Ok((await ViewsAsync(db, [c], ct))[0]);
        }, ct);
        return AnnounceRefusal(task, () => ws, () => refused, id, "DecisionRefused", actor, ct);
    }

    // ---- live push and the audit of a refused attempt ----------------------------------------------------------------------------------

    private async Task<ServiceResult<ChangeView>> Announce(Task<ServiceResult<ChangeView>> task, Func<string?> workspace)
    {
        var r = await task;
        if (r.IsOk && workspace() is { } ws && _realtime is not null) await _realtime.ModuleEventAsync(ws, ModuleKeys.Cab, ModuleEvent, [r.Value!.Id, r.Value.Version]);
        return r;
    }

    private async Task<ServiceResult<ChangeView>> AnnounceRefusal(Task<ServiceResult<ChangeView>> task, Func<string?> workspace, Func<(string? TrainId, GuardFailure Failure)?> refused,
        string id, string action, Actor actor, CancellationToken ct)
    {
        var r = await Announce(task, workspace);
        if (!r.IsOk && refused() is { } x) await RecordRefusalAsync(id, x.TrainId, action, actor, x.Failure, ct);
        return r;
    }

    /// <summary>The refused call rolled back, so the refusal gets its own transaction: one audit row naming who tried, what, and which rule said no.</summary>
    private async Task RecordRefusalAsync(string changeId, string? trainId, string action, Actor actor, GuardFailure failure, CancellationToken ct)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        Audit(db, actor, trainId, Entity, changeId, action, null, new { refused = true, guard = failure.Guard, message = failure.Message });
        await db.SaveChangesAsync(ct);
        AuditSealSignal.Notify();
    }
}
