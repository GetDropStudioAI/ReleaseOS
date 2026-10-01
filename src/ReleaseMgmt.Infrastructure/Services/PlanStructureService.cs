using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

public sealed record NewProduct(string? Name, string? VersionTag, string? ProjectCode);

/// <summary>Partial update: null leaves a field alone. An empty ProjectCode clears it.</summary>
public sealed record ProductPatch(string? Name, string? VersionTag, string? ProjectCode);

/// <summary>Exactly one of OffsetDays / DueOn, and exactly one of OwnerUserId / OwnerTeamId. SequenceOrder null = after the last gate.</summary>
public sealed record NewGate(string? GateName, string? GateClass, int? OffsetDays, DateOnly? DueOn, string? RequiredBeforeStatus,
                             string? OwnerUserId, string? OwnerTeamId, int? SequenceOrder);

/// <summary>Partial update: null leaves a field alone. At most one of OffsetDays / DueOn; setting either owner replaces the other. Never Status (rule 2).</summary>
public sealed record GatePatch(string? GateName, string? GateClass, int? OffsetDays, DateOnly? DueOn, string? RequiredBeforeStatus,
                               string? OwnerUserId, string? OwnerTeamId);

/// <summary>
/// REOS-81: the structure of a train's plan, products and gates, added, edited and removed from the workspace.
/// No trigger guards these rows by train state, so every rule lives here and is readable (docs/QUESTIONS.md Q-081a..):
/// <list type="bullet">
/// <item>Locked once the train is Executing, Complete, Aborted or archived (the CSV plan-import lock, PROJECT_SCOPE 9).</item>
/// <item>A gate's Status is never written here; Certified and Waived gates cannot be redefined (the Gates import rule); only a Pending gate with no history, evidence or references can be removed.</item>
/// <item>DueOn is always TargetReleaseDate minus OffsetDays business days (D9); a given date is turned into its offset, and refused if no offset lands on it.</item>
/// <item>A new gate goes after every Certified or Waived gate (trg_Gate_CertifyInOrder would otherwise hold a certified gate behind a pending one); inserting shifts later gates (UNIQUE train+sequence).</item>
/// <item>Once a train is Gated, a gate cannot be made "required before Gated": the lockout for that hop has already been passed.</item>
/// </list>
/// Every write bumps the train's Version too (as the CSV import and the bulk parser do): open parser previews resolve products and gates by name and must go stale.
/// </summary>
public sealed class PlanStructureService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IRealtimePublisher? realtime = null) : ServiceBase(dbf, time, realtime)
{
    public const int MaxOffsetDays = 365;          // the Gates import's OffsetDays range (ImportSpecs), Q-081f
    public const int MaxNameLength = 200;          // ProductName / GateName, as the import
    public const int MaxVersionTagLength = 100;    // VersionTag / ProjectCode, as the import
    private static readonly string[] Classes = ["Standard", "Compliance"];
    private static readonly string[] RequiredBefore = ["Gated", "Executing", "Complete"];
    private static readonly string[] StatusOrder = ["Planning", "Gated", "Executing", "Complete"];

    // ---- products ------------------------------------------------------------------------------------------------------------------------------

    public Task<ServiceResult<BundledProducts>> AddProductAsync(string trainId, NewProduct n, Actor actor, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var train = await db.Set<ReleaseTrains>().SingleOrDefaultAsync(t => t.Id == trainId, ct);
            if (train is null) return ServiceResult<BundledProducts>.NotFound("train");
            if (Locked(train, "products") is { } locked) return ServiceResult<BundledProducts>.Fail(locked);
            var p = new BundledProducts { ReleaseTrainId = trainId, ProductName = Trim(n.Name), VersionTag = Trim(n.VersionTag), ProjectCode = Trim(n.ProjectCode) };
            if (await ValidateAsync(db, p, ct) is { } bad) return ServiceResult<BundledProducts>.Fail(bad);
            db.Set<BundledProducts>().Add(p);
            Touch(train, actor);
            Audit(db, actor, trainId, "BundledProduct", p.Id, "Add", null, Snapshot(p));
            await db.SaveChangesAsync(ct);
            return ServiceResult<BundledProducts>.Ok(p);
        }, ct);

    public Task<ServiceResult<BundledProducts>> UpdateProductAsync(string productId, ProductPatch patch, Actor actor, int? expectedVersion = null, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var p = await db.Set<BundledProducts>().SingleOrDefaultAsync(x => x.Id == productId, ct);
            if (p is null) return ServiceResult<BundledProducts>.NotFound("product");
            if (VersionMismatch(expectedVersion, p.Version)) return ServiceResult<BundledProducts>.Conflict(p);
            var train = await db.Set<ReleaseTrains>().SingleAsync(t => t.Id == p.ReleaseTrainId, ct);
            if (Locked(train, "products") is { } locked) return ServiceResult<BundledProducts>.Fail(locked);
            var before = Snapshot(p);
            if (patch.Name is not null) p.ProductName = Trim(patch.Name);
            if (patch.VersionTag is not null) p.VersionTag = Trim(patch.VersionTag);
            if (patch.ProjectCode is not null) p.ProjectCode = Trim(patch.ProjectCode);
            if (await ValidateAsync(db, p, ct) is { } bad) return ServiceResult<BundledProducts>.Fail(bad);
            if (Equals(before, Snapshot(p))) return ServiceResult<BundledProducts>.Ok(p);   // nothing changed: no version bump, no audit row
            p.Version++;
            Touch(train, actor);
            Audit(db, actor, train.Id, "BundledProduct", p.Id, "Update", before, Snapshot(p));
            await db.SaveChangesAsync(ct);
            return ServiceResult<BundledProducts>.Ok(p);
        }, ct);

    /// <summary>Only when nothing points at the product: its checklist tasks, runbook steps, blockers and Jira/ServiceNow links would otherwise lose it silently (ON DELETE SET NULL).</summary>
    public Task<ServiceResult<BundledProducts>> RemoveProductAsync(string productId, Actor actor, int? expectedVersion = null, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var p = await db.Set<BundledProducts>().SingleOrDefaultAsync(x => x.Id == productId, ct);
            if (p is null) return ServiceResult<BundledProducts>.NotFound("product");
            if (VersionMismatch(expectedVersion, p.Version)) return ServiceResult<BundledProducts>.Conflict(p);
            var train = await db.Set<ReleaseTrains>().SingleAsync(t => t.Id == p.ReleaseTrainId, ct);
            if (Locked(train, "products") is { } locked) return ServiceResult<BundledProducts>.Fail(locked);
            var refs = new List<string>();
            Count(refs, await db.Set<ChecklistTasks>().CountAsync(x => x.BundledProductId == p.Id, ct), "checklist task");
            Count(refs, await db.Set<RunbookSteps>().CountAsync(x => x.BundledProductId == p.Id, ct), "runbook step");
            Count(refs, await db.Set<Blockers>().CountAsync(x => x.BundledProductId == p.Id, ct), "blocker");
            Count(refs, await db.Set<ExternalLinks>().CountAsync(x => x.EntityType == "Product" && x.EntityId == p.Id, ct), "external link");
            if (refs.Count > 0)
                return ServiceResult<BundledProducts>.Fail(new GuardFailure(Guards.ProductInUse,
                    $"{p.ProductName} is still used by {Join(refs)}; move or remove those first", refs));
            db.Set<BundledProducts>().Remove(p);
            Touch(train, actor);
            Audit(db, actor, train.Id, "BundledProduct", p.Id, "Remove", Snapshot(p));
            await db.SaveChangesAsync(ct);
            return ServiceResult<BundledProducts>.Ok(p);
        }, ct);

    private static async Task<GuardFailure?> ValidateAsync(ReleaseDbContext db, BundledProducts p, CancellationToken ct)
    {
        GuardFailure Bad(string m) => new(Guards.InvalidProduct, m);
        if (p.ProductName.Length == 0) return Bad("A product needs a name");
        if (p.ProductName.Length > MaxNameLength) return Bad($"A product name is at most {MaxNameLength} characters");
        if (p.VersionTag.Length == 0) return Bad("A product needs a version tag (for example 4.2.0)");
        if (p.VersionTag.Length > MaxVersionTagLength) return Bad($"A version tag is at most {MaxVersionTagLength} characters");
        if (p.ProjectCode.Length > MaxVersionTagLength) return Bad($"A project code is at most {MaxVersionTagLength} characters");
        // UNIQUE (train, name) is case-sensitive in SQLite; the parser's [Product] lookup is not, so two names differing only in case would be ambiguous.
        var names = await db.Set<BundledProducts>().Where(x => x.ReleaseTrainId == p.ReleaseTrainId && x.Id != p.Id).Select(x => x.ProductName).ToListAsync(ct);
        if (names.Any(x => string.Equals(x, p.ProductName, StringComparison.OrdinalIgnoreCase)))
            return new GuardFailure(Guards.DuplicateProduct, $"This train already bundles a product named {p.ProductName}");
        return null;
    }

    private static object Snapshot(BundledProducts p) => new ProductSnapshot(p.ProductName, p.VersionTag, p.ProjectCode);
    private sealed record ProductSnapshot(string Name, string VersionTag, string ProjectCode);

    // ---- gates ---------------------------------------------------------------------------------------------------------------------------------

    public Task<ServiceResult<StageGates>> AddGateAsync(string trainId, NewGate n, Actor actor, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var train = await db.Set<ReleaseTrains>().SingleOrDefaultAsync(t => t.Id == trainId, ct);
            if (train is null) return ServiceResult<StageGates>.NotFound("train");
            if (Locked(train, "gates") is { } locked) return ServiceResult<StageGates>.Fail(locked);
            if (n.OffsetDays is null == n.DueOn is null)
                return ServiceResult<StageGates>.Fail(new GuardFailure(Guards.InvalidGate, "Give the gate either a business-day offset before the target or a due date, not both"));
            if ((n.OwnerUserId is null or "") == (n.OwnerTeamId is null or ""))
                return ServiceResult<StageGates>.Fail(new GuardFailure(Guards.InvalidGate, "A gate needs exactly one owner: a person or a team"));

            var gates = await db.Set<StageGates>().Where(g => g.ReleaseTrainId == trainId).ToListAsync(ct);
            var g = new StageGates
            {
                ReleaseTrainId = trainId, GateName = Trim(n.GateName), GateClass = Trim(n.GateClass) is { Length: > 0 } c ? c : "Standard",
                RequiredBeforeStatus = Trim(n.RequiredBeforeStatus) is { Length: > 0 } r ? r : "Gated",
                OwnerUserId = Blank(n.OwnerUserId), OwnerTeamId = Blank(n.OwnerTeamId), Status = "Pending",
                LastChangedByUserId = actor.UserId, LastChangedAt = Now,
            };
            if (await ScheduleAsync(db, g, train, n.OffsetDays, n.DueOn, ct) is { } badDue) return ServiceResult<StageGates>.Fail(badDue);
            if (await ValidateAsync(db, g, gates, train, requiredBeforeChanged: true, ct) is { } bad) return ServiceResult<StageGates>.Fail(bad);

            // Position: after the last gate by default. An explicit position must come after every Certified/Waived gate; later gates move up one.
            var last = gates.Count == 0 ? 0 : gates.Max(x => x.SequenceOrder);
            var seq = n.SequenceOrder ?? last + 1;
            if (seq < 1) return ServiceResult<StageGates>.Fail(new GuardFailure(Guards.InvalidGate, "A gate's position starts at 1"));
            var certifiedAfter = gates.Where(x => x.Status is "Certified" or "Waived" && x.SequenceOrder >= seq).OrderBy(x => x.SequenceOrder).Select(x => x.GateName).ToList();
            if (certifiedAfter.Count > 0)
                return ServiceResult<StageGates>.Fail(new GuardFailure(Guards.SequenceBeforeCertified,
                    $"A new gate cannot go before a gate that is already certified or waived ({string.Join(", ", certifiedAfter)}); place it after them", certifiedAfter));
            var shifted = gates.Where(x => x.SequenceOrder >= seq).OrderBy(x => x.SequenceOrder).ToList();
            g.SequenceOrder = Math.Min(seq, last + 1);   // no gaps are introduced by asking for position 99 on a 3-gate train
            if (shifted.Count > 0)
            {
                // UNIQUE (train, sequence) is checked row by row, so move the later gates out of the way first (negative), then to their new places.
                foreach (var x in shifted) x.SequenceOrder = -x.SequenceOrder;
                await db.SaveChangesAsync(ct);
                foreach (var x in shifted) { x.SequenceOrder = -x.SequenceOrder + 1; x.Version++; x.LastChangedByUserId = actor.UserId; x.LastChangedAt = Now; }
            }
            db.Set<StageGates>().Add(g);
            Touch(train, actor);
            Audit(db, actor, trainId, "StageGate", g.Id, "Add", null,
                new { gate = Snapshot(g), shifted = shifted.Count == 0 ? null : shifted.Select(x => new { x.GateName, sequenceOrder = x.SequenceOrder }).ToList() });
            await db.SaveChangesAsync(ct);
            return ServiceResult<StageGates>.Ok(g);
        }, ct);

    public Task<ServiceResult<StageGates>> UpdateGateAsync(string gateId, GatePatch patch, Actor actor, int? expectedVersion = null, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var g = await db.Set<StageGates>().SingleOrDefaultAsync(x => x.Id == gateId, ct);
            if (g is null) return ServiceResult<StageGates>.NotFound("gate");
            if (VersionMismatch(expectedVersion, g.Version)) return ServiceResult<StageGates>.Conflict(g);
            var train = await db.Set<ReleaseTrains>().SingleAsync(t => t.Id == g.ReleaseTrainId, ct);
            if (Locked(train, "gates") is { } locked) return ServiceResult<StageGates>.Fail(locked);
            if (g.Status is "Certified" or "Waived")
                return ServiceResult<StageGates>.Fail(new GuardFailure(Guards.GateNotEditable, g.Status == "Certified"
                    ? $"{g.GateName} is certified; reopen it before changing it"
                    : $"{g.GateName} is waived, which is final; add a new gate instead"));
            if (patch.OffsetDays is not null && patch.DueOn is not null)
                return ServiceResult<StageGates>.Fail(new GuardFailure(Guards.InvalidGate, "Give either a business-day offset or a due date, not both"));
            if (Blank(patch.OwnerUserId) is not null && Blank(patch.OwnerTeamId) is not null)
                return ServiceResult<StageGates>.Fail(new GuardFailure(Guards.InvalidGate, "A gate needs exactly one owner: a person or a team"));

            var before = Snapshot(g);
            var oldRequired = g.RequiredBeforeStatus;
            if (patch.GateName is not null) g.GateName = Trim(patch.GateName);
            if (patch.GateClass is not null) g.GateClass = Trim(patch.GateClass);
            if (patch.RequiredBeforeStatus is not null) g.RequiredBeforeStatus = Trim(patch.RequiredBeforeStatus);
            if (Blank(patch.OwnerUserId) is string u) { g.OwnerUserId = u; g.OwnerTeamId = null; }
            else if (Blank(patch.OwnerTeamId) is string tm) { g.OwnerTeamId = tm; g.OwnerUserId = null; }
            if ((patch.OffsetDays is not null || patch.DueOn is not null) && await ScheduleAsync(db, g, train, patch.OffsetDays, patch.DueOn, ct) is { } badDue)
                return ServiceResult<StageGates>.Fail(badDue);
            var gates = await db.Set<StageGates>().Where(x => x.ReleaseTrainId == g.ReleaseTrainId).ToListAsync(ct);
            if (await ValidateAsync(db, g, gates, train, requiredBeforeChanged: g.RequiredBeforeStatus != oldRequired, ct) is { } bad) return ServiceResult<StageGates>.Fail(bad);
            if (Equals(before, Snapshot(g))) return ServiceResult<StageGates>.Ok(g);   // nothing changed

            g.LastChangedByUserId = actor.UserId; g.LastChangedAt = Now; g.Version++;   // Status untouched: no gate trigger fires
            Touch(train, actor);
            Audit(db, actor, train.Id, "StageGate", g.Id, "Update", before, Snapshot(g));
            await db.SaveChangesAsync(ct);
            return ServiceResult<StageGates>.Ok(g);
        }, ct);

    /// <summary>Only a Pending gate that has never moved and carries no evidence, waiver or reference. Its checklist tasks go with it (ON DELETE CASCADE); the audit row counts them.</summary>
    public Task<ServiceResult<StageGates>> RemoveGateAsync(string gateId, Actor actor, int? expectedVersion = null, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var g = await db.Set<StageGates>().SingleOrDefaultAsync(x => x.Id == gateId, ct);
            if (g is null) return ServiceResult<StageGates>.NotFound("gate");
            if (VersionMismatch(expectedVersion, g.Version)) return ServiceResult<StageGates>.Conflict(g);
            var train = await db.Set<ReleaseTrains>().SingleAsync(t => t.Id == g.ReleaseTrainId, ct);
            if (Locked(train, "gates") is { } locked) return ServiceResult<StageGates>.Fail(locked);
            if (g.Status != "Pending")
                return ServiceResult<StageGates>.Fail(new GuardFailure(Guards.GateNotRemovable, $"{g.GateName} is {Word(g.Status)}; only a pending gate can be removed"));
            // A Pending gate has normally never moved, but the transition log is append-only (a trigger refuses the cascade), so say so plainly first.
            if (await db.Set<GateTransitions>().AnyAsync(x => x.StageGateId == g.Id, ct))
                return ServiceResult<StageGates>.Fail(new GuardFailure(Guards.GateNotRemovable, $"{g.GateName} has a transition history; it cannot be removed"));

            var taskIds = await db.Set<ChecklistTasks>().Where(x => x.StageGateId == g.Id).Select(x => x.Id).ToListAsync(ct);
            var refs = new List<string>();
            Count(refs, await db.Set<Attachments>().CountAsync(a => (a.EntityType == "Gate" && a.EntityId == g.Id) || (a.EntityType == "Task" && taskIds.Contains(a.EntityId)), ct), "evidence attachment");
            Count(refs, await db.Set<GateWaivers>().CountAsync(x => x.StageGateId == g.Id, ct), "waiver");
            Count(refs, await db.Set<Blockers>().CountAsync(x => x.StageGateId == g.Id, ct), "blocker");
            Count(refs, await db.Set<CommTemplates>().CountAsync(x => x.StageGateId == g.Id, ct), "communication");
            Count(refs, await db.Set<ExternalLinks>().CountAsync(x => x.EntityType == "Gate" && x.EntityId == g.Id, ct), "external link");
            if (refs.Count > 0)
                return ServiceResult<StageGates>.Fail(new GuardFailure(Guards.GateInUse, $"{g.GateName} still has {Join(refs)}; remove or move those first", refs));

            db.Set<StageGates>().Remove(g);
            Touch(train, actor);
            Audit(db, actor, train.Id, "StageGate", g.Id, "Remove", Snapshot(g), new { tasksRemoved = taskIds.Count });
            await db.SaveChangesAsync(ct);
            return ServiceResult<StageGates>.Ok(g);
        }, ct);

    /// <summary>Sets OffsetDays and DueOn together: DueOn = target minus OffsetDays business days (D9), from either input.</summary>
    private static async Task<GuardFailure?> ScheduleAsync(ReleaseDbContext db, StageGates g, ReleaseTrains train, int? offsetDays, DateOnly? dueOn, CancellationToken ct)
    {
        var holidays = (await db.Set<Holidays>().Select(h => h.Day).ToListAsync(ct)).ToHashSet();
        if (offsetDays is int n)
        {
            if (n < 0 || n > MaxOffsetDays) return new(Guards.InvalidGate, $"The offset is business days before the target: 0 to {MaxOffsetDays}");
            g.OffsetDays = n;
            g.DueOn = BusinessDays.SubtractBusinessDays(train.TargetReleaseDate, n, holidays);
            return null;
        }
        var day = dueOn!.Value;
        if (day > train.TargetReleaseDate) return new(Guards.InvalidGate, $"A gate is due on or before the target date ({train.TargetReleaseDate:yyyy-MM-dd})");
        if (BusinessDays.OffsetOf(train.TargetReleaseDate, day, holidays, MaxOffsetDays) is not int offset)
        {
            if (BusinessDays.IsBusinessDay(day, holidays))
                return new(Guards.InvalidGate, $"{day:yyyy-MM-dd} is more than {MaxOffsetDays} business days before the target");
            // The nearest dates an offset can land on: the business day before, and the business day after (or the target itself, which is offset 0).
            var earlier = day; do earlier = earlier.AddDays(-1); while (!BusinessDays.IsBusinessDay(earlier, holidays));
            var later = day; do later = later.AddDays(1); while (!BusinessDays.IsBusinessDay(later, holidays) && later < train.TargetReleaseDate);
            return new(Guards.InvalidGate, $"{day:yyyy-MM-dd} is not a business day; the nearest due dates are {earlier:yyyy-MM-dd} and {later:yyyy-MM-dd}");
        }
        g.OffsetDays = offset;
        g.DueOn = day;
        return null;
    }

    private static async Task<GuardFailure?> ValidateAsync(ReleaseDbContext db, StageGates g, List<StageGates> gates, ReleaseTrains train, bool requiredBeforeChanged, CancellationToken ct)
    {
        GuardFailure Bad(string m) => new(Guards.InvalidGate, m);
        if (g.GateName.Length == 0) return Bad("A gate needs a name");
        if (g.GateName.Length > MaxNameLength) return Bad($"A gate name is at most {MaxNameLength} characters");
        if (!Classes.Contains(g.GateClass)) return Bad($"Gate class must be {string.Join(" or ", Classes)}");
        if (!RequiredBefore.Contains(g.RequiredBeforeStatus)) return Bad($"Required before must be one of {string.Join(", ", RequiredBefore)}");
        if ((g.OwnerUserId is null) == (g.OwnerTeamId is null)) return Bad("A gate needs exactly one owner: a person or a team");
        if (g.OwnerUserId is not null)
        {
            var u = await db.Set<Users>().SingleOrDefaultAsync(x => x.Id == g.OwnerUserId, ct);
            if (u is null) return Bad("The owner does not exist");
            if (!u.IsActive) return Bad($"{u.DisplayName} is inactive and cannot be given new work");
        }
        if (g.OwnerTeamId is not null && !await db.Set<Teams>().AnyAsync(t => t.Id == g.OwnerTeamId, ct)) return Bad("The owning team does not exist");
        // The bulk parser switches gates by name, case-insensitively: two gates with the same name would be ambiguous.
        if (gates.Any(x => x.Id != g.Id && string.Equals(x.GateName, g.GateName, StringComparison.OrdinalIgnoreCase)))
            return new(Guards.DuplicateGateName, $"This train already has a gate named {g.GateName}");
        if (requiredBeforeChanged && Array.IndexOf(StatusOrder, g.RequiredBeforeStatus) <= Array.IndexOf(StatusOrder, train.CurrentStatus))
            return new(Guards.RequiredBeforePassed,
                $"The train is already {train.CurrentStatus}; a gate can only be required before a status it has not reached yet (move the train back to Planning to add a gate required before Gated)");
        return null;
    }

    private static object Snapshot(StageGates g) => new GateSnapshot(g.GateName, g.GateClass, g.SequenceOrder, g.OffsetDays, g.DueOn.ToString("yyyy-MM-dd"), g.RequiredBeforeStatus, g.OwnerUserId, g.OwnerTeamId);
    private sealed record GateSnapshot(string GateName, string GateClass, int SequenceOrder, int OffsetDays, string DueOn, string RequiredBeforeStatus, string? OwnerUserId, string? OwnerTeamId);

    // ---- shared --------------------------------------------------------------------------------------------------------------------------------

    /// <summary>The plan-import lock (PROJECT_SCOPE 9): structure is frozen once the train is Executing, and for good once it is Complete, Aborted or archived.</summary>
    private static GuardFailure? Locked(ReleaseTrains t, string what)
    {
        if (t.ArchivedAt is not null) return new(Guards.TrainClosed, $"The train is archived; its {what} can no longer be changed");
        if (t.CurrentStatus is "Complete" or "Aborted") return new(Guards.TrainClosed, $"The train is {t.CurrentStatus}; its {what} can no longer be changed");
        if (t.CurrentStatus == "Executing") return new(Guards.PlanLocked, $"The train is Executing; its {what} are locked once deployment has started");
        return null;
    }

    /// <summary>The train moved: bump its Version so open parser previews (which resolve products and gates by name) go stale, as the CSV import does.</summary>
    private void Touch(ReleaseTrains t, Actor actor)
    {
        var now = Now;
        t.Version++; t.UpdatedAt = now; t.LastChangedByUserId = actor.UserId; t.LastChangedAt = now;
    }

    private static void Count(List<string> into, int n, string what) { if (n > 0) into.Add($"{n} {what}{(n == 1 ? "" : "s")}"); }
    private static string Join(List<string> parts) => parts.Count == 1 ? parts[0] : $"{string.Join(", ", parts[..^1])} and {parts[^1]}";
    private static string Word(string status) => status == "InProgress" ? "in progress" : status.ToLowerInvariant();
    private static string Trim(string? s) => (s ?? "").Trim();
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
