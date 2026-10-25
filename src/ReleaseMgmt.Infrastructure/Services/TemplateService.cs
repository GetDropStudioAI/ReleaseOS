using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

/// <summary>Guard names of the train-template lifecycle (REOS-38) and its governance (REOS-122, RT7). Kept apart from <see cref="Guards"/> so parallel streams do not collide.</summary>
public static class TemplateGuards
{
    public const string TemplateNotDraft = "TemplateNotDraft";
    public const string IllegalTemplateTransition = "IllegalTemplateTransition";
    public const string TemplateNameTaken = "TemplateNameTaken";
    public const string TemplateInvalid = "TemplateInvalid";
    public const string TemplateNeedsGate = "TemplateNeedsGate";
    public const string TemplateApproverRole = "TemplateApproverRole";
    /// <summary>RT7: the person who wrote a template (created it or last changed it) does not approve it. trg_Template_ApproverNotAuthor_* is the backstop.</summary>
    public const string TemplateApproverIsAuthor = "TemplateApproverIsAuthor";
    /// <summary>RT7, open question Q10: a template that adds or changes a Compliance gate is approved by a Governance Officer (<c>Templates:ComplianceChangeNeedsGovernanceOfficer</c>).</summary>
    public const string TemplateNeedsGovernanceApproval = "TemplateNeedsGovernanceApproval";
    /// <summary>RT7: the next version of this template already exists.</summary>
    public const string TemplateVersionExists = "TemplateVersionExists";
    /// <summary>RT7, open question Q8: a planner cannot remove or downgrade a Compliance gate that came from an approved template. trg_Gate_TemplateLocked_* is the backstop.</summary>
    public const string GateLockedByTemplate = "GateLockedByTemplate";
}

/// <summary>
/// The three open owner questions of REOS-122 (proposal section 6, questions 8 to 10), built to the recommended defaults behind these keys (docs/QUESTIONS.md Q-122a):
/// <c>Templates:LockComplianceGates</c> (Q8), <c>Templates:MilestonesMoveWithTarget</c> (Q9) and <c>Templates:ComplianceChangeNeedsGovernanceOfficer</c> (Q10).
/// </summary>
public sealed class TemplateGovernanceOptions
{
    public const string Section = "Templates";
    /// <summary>Q8: a release made from a template locks the template's Compliance gates (no removing, no downgrading). Other gates stay editable. Affects releases made from now on.</summary>
    public bool LockComplianceGates { get; init; } = true;
    /// <summary>Q9: a milestone made from a template keeps its offset from the target and moves with it. False: it gets a fixed date when the release is made.</summary>
    public bool MilestonesMoveWithTarget { get; init; } = true;
    /// <summary>Q10: besides "not the author", a Governance Officer approves a template that adds or changes a Compliance gate.</summary>
    public bool ComplianceChangeNeedsGovernanceOfficer { get; init; } = true;

    /// <summary>Reads the three keys; a value that is not true or false stops the start rather than being guessed.</summary>
    public static TemplateGovernanceOptions From(Microsoft.Extensions.Configuration.IConfiguration c)
    {
        bool Flag(string name)
        {
            var text = c[$"{Section}:{name}"];
            if (string.IsNullOrWhiteSpace(text)) return true;
            return bool.TryParse(text, out var v) ? v : throw new InvalidOperationException($"{Section}:{name} must be true or false, not \"{text}\"");
        }
        return new() { LockComplianceGates = Flag(nameof(LockComplianceGates)), MilestonesMoveWithTarget = Flag(nameof(MilestonesMoveWithTarget)), ComplianceChangeNeedsGovernanceOfficer = Flag(nameof(ComplianceChangeNeedsGovernanceOfficer)) };
    }
}

public sealed record TemplateTaskInput(string TaskDescription, string? OwnerTeamId = null);
public sealed record TemplateGateInput(string GateName, string? GateClass, int OffsetDays, string RequiredBeforeStatus, string? OwnerTeamId, IReadOnlyList<TemplateTaskInput>? Tasks = null);
public sealed record TemplateStepInput(string StepCode, string Section, string Title, int OffsetMinutes, int PlannedDurationMin, string? OwnerTeamId);
public sealed record TemplateScheduleInput(string LibraryTemplateId, int OffsetDays);
public sealed record TemplateMilestoneInput(string Name, int OffsetDays, string? OwnerTeamId = null);
public sealed record TemplateInput(string? Name, string? DefaultRiskTier, IReadOnlyList<TemplateGateInput>? Gates, IReadOnlyList<TemplateStepInput>? Steps, IReadOnlyList<TemplateScheduleInput>? Schedule,
    IReadOnlyList<TemplateMilestoneInput>? Milestones = null);

public sealed record TemplateSummary(string Id, string Name, string Status, string DefaultRiskTier, string? ApprovedByUserId, string? ApprovedByName, DateTime? ApprovedAt,
    DateOnly? ReviewDueOn, bool ReviewOverdue, int GateCount, int StepCount, int ScheduleCount, int Version,
    int VersionNo = 1, string? SupersedesTemplateId = null, string? SupersededByTemplateId = null, string? CreatedByUserId = null, string? CreatedByName = null,
    string? LastEditedByUserId = null, string? LastEditedByName = null, int TaskCount = 0, int MilestoneCount = 0);
public sealed record TemplateTaskView(string Id, string TaskDescription, int SequenceOrder, string? OwnerTeamId, string? OwnerTeamName);
public sealed record TemplateGateView(string Id, string GateName, string GateClass, int SequenceOrder, int OffsetDays, string RequiredBeforeStatus, string? OwnerTeamId, string? OwnerTeamName,
    IReadOnlyList<TemplateTaskView> Tasks);
public sealed record TemplateStepView(string Id, string StepCode, string Section, string Title, int OffsetMinutes, int PlannedDurationMin, string? OwnerTeamId, string? OwnerTeamName);
public sealed record TemplateScheduleView(string Id, string LibraryTemplateId, string LibraryName, string TemplateType, int OffsetDays);
public sealed record TemplateMilestoneView(string Id, string Name, int OffsetDays, int SequenceOrder, string? OwnerTeamId, string? OwnerTeamName);
/// <summary>One version of the template's name, newest first in <see cref="TemplateDetail.Versions"/>.</summary>
public sealed record TemplateVersionLine(string Id, int VersionNo, string Status, string? ApprovedByName, DateTime? ApprovedAt);
public sealed record TemplateDetail(TemplateSummary Template, IReadOnlyList<TemplateGateView> Gates, IReadOnlyList<TemplateStepView> Steps, IReadOnlyList<TemplateScheduleView> Schedule,
    IReadOnlyList<TemplateMilestoneView> Milestones, IReadOnlyList<TemplateVersionLine> Versions, IReadOnlyList<TemplateChange> Changes, bool ComplianceChanged, bool NeedsGovernanceApproval)
{
    // Flattened so a client reads one object; also what a 409 carries as "current".
    public string Id => Template.Id;
    public string Name => Template.Name;
    public string Status => Template.Status;
    public int Version => Template.Version;
}
public sealed record LibraryOption(string Id, string Name, string TemplateType);

/// <summary>
/// Train templates: Draft -> Approved -> Retired (PROJECT_SCOPE section 2, D-level governance). Only a Draft is editable; approval stamps the
/// approver and a review date 12 months out; retiring is final. Every write bumps Version and writes one AuditEvents row in the same transaction.
/// REOS-122 (RT7): a template also carries checklist tasks (on its gates) and milestones (days from the target); an Approved template gets a new version (a Draft under the same name, VersionNo + 1)
/// whose approval retires the one it replaces; releases already made keep what they were made from. The approver is not the author, and a Governance Officer approves a change to a Compliance gate.
/// </summary>
public sealed class TemplateService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IRealtimePublisher? realtime = null, TemplateGovernanceOptions? governance = null) : ServiceBase(dbf, time, realtime)
{
    /// <summary>Re-review cycle applied on approval (schema comment: default +12 months).</summary>
    public const int ReviewMonths = 12;
    public const int MaxTaskDescription = 500, MaxMilestoneName = 200, MaxOffsetDays = 365;

    private readonly TemplateGovernanceOptions options = governance ?? new();

    private static readonly string[] Classes = ["Standard", "Compliance"];
    private static readonly string[] Before = ["Gated", "Executing", "Complete"];
    private static readonly string[] Sections = ["PreCheck", "Deploy", "Verify", "Rollback", "Hypercare"];
    private static readonly string[] Tiers = ["Low", "Moderate", "High", "VeryHigh"];

    private DateOnly Today => DateOnly.FromDateTime(Now);

    // ---- reads
    public async Task<List<TemplateSummary>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var rows = await db.Set<TrainTemplates>().AsNoTracking().ToListAsync(ct);
        var result = new List<TemplateSummary>();
        foreach (var t in rows.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ThenByDescending(t => t.VersionNo)) result.Add(await SummaryAsync(db, t, ct));
        return result;
    }

    public async Task<TemplateDetail?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var t = await db.Set<TrainTemplates>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        return t is null ? null : await DetailAsync(db, t, ct);
    }

    public async Task<List<LibraryOption>> LibraryOptionsAsync(CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        return await db.Set<CommTemplateLibrary>().AsNoTracking().OrderBy(l => l.Name).Select(l => new LibraryOption(l.Id, l.Name, l.TemplateType)).ToListAsync(ct);
    }

    // ---- writes
    public Task<ServiceResult<TemplateDetail>> CreateAsync(TemplateInput input, Actor actor, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var name = (input.Name ?? "").Trim();
            var bad = await ValidateAsync(db, input, name, null, ct);
            if (bad.Count > 0) return ServiceResult<TemplateDetail>.Fail(bad);
            var t = new TrainTemplates
            {
                Id = Ids.New(), Name = name, Status = "Draft", DefaultRiskTier = input.DefaultRiskTier ?? "Moderate",
                CreatedByUserId = actor.UserId, LastEditedByUserId = actor.UserId, VersionNo = 1,
            };
            db.Set<TrainTemplates>().Add(t);
            await db.SaveChangesAsync(ct);   // the parent row first: children reference it
            AddChildren(db, t.Id, input);
            Audit(db, actor, null, "TrainTemplate", t.Id, "Create", null, Shape(t, input));
            await db.SaveChangesAsync(ct);
            return ServiceResult<TemplateDetail>.Ok(await DetailAsync(db, t, ct));
        }, ct);

    public Task<ServiceResult<TemplateDetail>> UpdateAsync(string id, TemplateInput input, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var t = await db.Set<TrainTemplates>().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (t is null) return ServiceResult<TemplateDetail>.NotFound("template");
            if (VersionMismatch(expectedVersion, t.Version)) return ServiceResult<TemplateDetail>.Conflict(await DetailAsync(db, t, ct));
            if (t.Status != "Draft")
                return ServiceResult<TemplateDetail>.Fail(new GuardFailure(TemplateGuards.TemplateNotDraft, $"Only a Draft template can be edited; this one is {t.Status}. Create a new version instead"));
            var name = (input.Name ?? t.Name).Trim();
            // A new version keeps its lineage's name: renaming would split the versions apart.
            if (t.SupersedesTemplateId is not null && !string.Equals(name, t.Name, StringComparison.Ordinal))
                return ServiceResult<TemplateDetail>.Fail(new GuardFailure(TemplateGuards.TemplateInvalid, $"A new version keeps the name \"{t.Name}\" of the template it replaces"));
            var bad = await ValidateAsync(db, input, name, t, ct);
            if (bad.Count > 0) return ServiceResult<TemplateDetail>.Fail(bad);

            var before = await DetailAsync(db, t, ct);
            // Children are replaced wholesale (SequenceOrder is unique per template, so delete before insert). Tasks go with their gates (ON DELETE CASCADE) but are named for clarity.
            await db.Set<TemplateTasks>().Where(x => x.TemplateId == id).ExecuteDeleteAsync(ct);
            await db.Set<TemplateGates>().Where(x => x.TemplateId == id).ExecuteDeleteAsync(ct);
            await db.Set<TemplateSteps>().Where(x => x.TemplateId == id).ExecuteDeleteAsync(ct);
            await db.Set<TemplateCommSchedule>().Where(x => x.TemplateId == id).ExecuteDeleteAsync(ct);
            await db.Set<TemplateMilestones>().Where(x => x.TemplateId == id).ExecuteDeleteAsync(ct);
            t.Name = name;
            t.DefaultRiskTier = input.DefaultRiskTier ?? t.DefaultRiskTier;
            t.LastEditedByUserId = actor.UserId;
            t.Version++;
            AddChildren(db, id, input);
            Audit(db, actor, null, "TrainTemplate", id, "Update", Shape(before), Shape(t, input));
            await db.SaveChangesAsync(ct);
            return ServiceResult<TemplateDetail>.Ok(await DetailAsync(db, t, ct));
        }, ct);

    /// <summary>
    /// RT7: copies an Approved template into a Draft under the same name, VersionNo + 1, that supersedes it. The person who asks is its author. Nothing about the Approved template changes
    /// and no release is touched; approving the new version retires the old one (<see cref="ApproveAsync"/>).
    /// </summary>
    public Task<ServiceResult<TemplateDetail>> NewVersionAsync(string id, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var src = await db.Set<TrainTemplates>().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (src is null) return ServiceResult<TemplateDetail>.NotFound("template");
            if (VersionMismatch(expectedVersion, src.Version)) return ServiceResult<TemplateDetail>.Conflict(await DetailAsync(db, src, ct));
            if (src.Status != "Approved")
                return ServiceResult<TemplateDetail>.Fail(new GuardFailure(TemplateGuards.IllegalTemplateTransition, $"Only an Approved template can get a new version; this one is {src.Status}"));
            var next = src.VersionNo + 1;
            var existing = await db.Set<TrainTemplates>().Where(x => x.Name == src.Name && x.VersionNo >= next).OrderBy(x => x.VersionNo).FirstOrDefaultAsync(ct);
            if (existing is not null)
                return ServiceResult<TemplateDetail>.Fail(new GuardFailure(TemplateGuards.TemplateVersionExists,
                    $"Version {existing.VersionNo} of \"{src.Name}\" already exists ({existing.Status}); open that one instead of starting another"));

            var t = new TrainTemplates
            {
                Id = Ids.New(), Name = src.Name, Status = "Draft", DefaultRiskTier = src.DefaultRiskTier, VersionNo = next, SupersedesTemplateId = src.Id,
                CreatedByUserId = actor.UserId, LastEditedByUserId = actor.UserId,
            };
            db.Set<TrainTemplates>().Add(t);
            await db.SaveChangesAsync(ct);
            var gateIds = new Dictionary<string, string>();
            foreach (var g in await db.Set<TemplateGates>().AsNoTracking().Where(x => x.TemplateId == id).OrderBy(x => x.SequenceOrder).ToListAsync(ct))
            {
                var c = new TemplateGates { Id = Ids.New(), TemplateId = t.Id, GateName = g.GateName, GateClass = g.GateClass, SequenceOrder = g.SequenceOrder, OffsetDays = g.OffsetDays, RequiredBeforeStatus = g.RequiredBeforeStatus, OwnerTeamId = g.OwnerTeamId };
                gateIds[g.Id] = c.Id; db.Set<TemplateGates>().Add(c);
            }
            await db.SaveChangesAsync(ct);   // gates before the tasks that reference them
            foreach (var k in await db.Set<TemplateTasks>().AsNoTracking().Where(x => x.TemplateId == id).OrderBy(x => x.SequenceOrder).ToListAsync(ct))
                db.Set<TemplateTasks>().Add(new TemplateTasks { Id = Ids.New(), TemplateId = t.Id, TemplateGateId = gateIds[k.TemplateGateId], TaskDescription = k.TaskDescription, OwnerTeamId = k.OwnerTeamId, SequenceOrder = k.SequenceOrder });
            foreach (var s in await db.Set<TemplateSteps>().AsNoTracking().Where(x => x.TemplateId == id).ToListAsync(ct))
                db.Set<TemplateSteps>().Add(new TemplateSteps { Id = Ids.New(), TemplateId = t.Id, StepCode = s.StepCode, Section = s.Section, Title = s.Title, OffsetMinutes = s.OffsetMinutes, PlannedDurationMin = s.PlannedDurationMin, OwnerTeamId = s.OwnerTeamId });
            foreach (var c in await db.Set<TemplateCommSchedule>().AsNoTracking().Where(x => x.TemplateId == id).ToListAsync(ct))
                db.Set<TemplateCommSchedule>().Add(new TemplateCommSchedule { Id = Ids.New(), TemplateId = t.Id, LibraryTemplateId = c.LibraryTemplateId, OffsetDays = c.OffsetDays });
            foreach (var m in await db.Set<TemplateMilestones>().AsNoTracking().Where(x => x.TemplateId == id).OrderBy(x => x.SequenceOrder).ToListAsync(ct))
                db.Set<TemplateMilestones>().Add(new TemplateMilestones { Id = Ids.New(), TemplateId = t.Id, Name = m.Name, OffsetDays = m.OffsetDays, OwnerTeamId = m.OwnerTeamId, SequenceOrder = m.SequenceOrder });
            Audit(db, actor, null, "TrainTemplate", t.Id, "NewVersion", new { supersedes = src.Id, versionNo = src.VersionNo }, new { t.Name, t.VersionNo, supersedes = src.Id });
            await db.SaveChangesAsync(ct);
            return ServiceResult<TemplateDetail>.Ok(await DetailAsync(db, t, ct));
        }, ct);

    public Task<ServiceResult<TemplateDetail>> ApproveAsync(string id, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var t = await db.Set<TrainTemplates>().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (t is null) return ServiceResult<TemplateDetail>.NotFound("template");
            if (VersionMismatch(expectedVersion, t.Version)) return ServiceResult<TemplateDetail>.Conflict(await DetailAsync(db, t, ct));
            var f = new List<GuardFailure>();
            var role = await RoleOf(db, actor.UserId);
            if (t.Status != "Draft") f.Add(new(TemplateGuards.IllegalTemplateTransition, $"Only a Draft template can be approved; this one is {t.Status}"));
            if (role is not (Roles.ReleaseManager or Roles.GovernanceOfficer))
                f.Add(new(TemplateGuards.TemplateApproverRole, "Templates are approved by a Release Manager or a Governance Officer"));
            if (t.Status == "Draft")
            {
                if (!await db.Set<TemplateGates>().AnyAsync(g => g.TemplateId == id, ct))
                    f.Add(new(TemplateGuards.TemplateNeedsGate, "A template needs at least one gate before it can be approved"));
                // Separation of duties (RT7): whoever wrote it, by creating it or by changing it last, does not approve it.
                if (t.CreatedByUserId == actor.UserId || t.LastEditedByUserId == actor.UserId)
                    f.Add(new(TemplateGuards.TemplateApproverIsAuthor, $"You {(t.CreatedByUserId == actor.UserId ? "created" : "last changed")} this template, so someone else approves it (separation of duties)"));
                if (options.ComplianceChangeNeedsGovernanceOfficer && role != Roles.GovernanceOfficer)
                {
                    var d = await DetailAsync(db, t, ct);
                    if (d.ComplianceChanged)
                    {
                        var which = d.Changes.Where(c => c.Compliance && c.Area == "Gate").Select(c => c.Subject).Distinct().ToList();
                        f.Add(new(TemplateGuards.TemplateNeedsGovernanceApproval,
                            which.Count > 0 ? $"This version changes a Compliance gate ({string.Join(", ", which)}), so a Governance Officer approves it"
                                            : "This template has Compliance gates that no approved version has had, so a Governance Officer approves it"));
                    }
                }
            }
            if (f.Count > 0) return ServiceResult<TemplateDetail>.Fail(f);

            var now = Now;
            t.Status = "Approved"; t.ApprovedByUserId = actor.UserId; t.ApprovedAt = now;
            t.ReviewDueOn = DateOnly.FromDateTime(now).AddMonths(ReviewMonths);
            t.Version++;
            Audit(db, actor, null, "TrainTemplate", id, "Approve", new { status = "Draft" }, new { status = t.Status, t.ReviewDueOn, t.VersionNo, supersedes = t.SupersedesTemplateId });
            await db.SaveChangesAsync(ct);

            // The version it replaces is retired in the same transaction, and a release type whose default template it was moves to this version.
            if (t.SupersedesTemplateId is not null && await db.Set<TrainTemplates>().SingleOrDefaultAsync(x => x.Id == t.SupersedesTemplateId, ct) is { } old)
            {
                if (old.Status == "Approved")
                {
                    var before = new { status = old.Status, approvedBy = old.ApprovedByUserId, old.ApprovedAt, old.ReviewDueOn };
                    old.Status = "Retired"; old.ApprovedByUserId = null; old.ApprovedAt = null; old.Version++;
                    Audit(db, actor, null, "TrainTemplate", old.Id, "Retire", before, new { status = old.Status, supersededBy = t.Id, supersededByVersionNo = t.VersionNo });
                }
                foreach (var rt in await db.Set<ReleaseTypes>().Where(x => x.DefaultTemplateId == old.Id).ToListAsync(ct))
                {
                    rt.DefaultTemplateId = t.Id; rt.Version++;
                    Audit(db, actor, null, "ReleaseType", rt.Id, "DefaultTemplateMoved", new { defaultTemplateId = old.Id }, new { defaultTemplateId = t.Id, reason = "New template version approved", rt.Version });
                }
                await db.SaveChangesAsync(ct);
            }
            return ServiceResult<TemplateDetail>.Ok(await DetailAsync(db, t, ct));
        }, ct);

    public Task<ServiceResult<TemplateDetail>> RetireAsync(string id, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var t = await db.Set<TrainTemplates>().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (t is null) return ServiceResult<TemplateDetail>.NotFound("template");
            if (VersionMismatch(expectedVersion, t.Version)) return ServiceResult<TemplateDetail>.Conflict(await DetailAsync(db, t, ct));
            var f = new List<GuardFailure>();
            if (t.Status != "Approved") f.Add(new(TemplateGuards.IllegalTemplateTransition, $"Only an Approved template can be retired; this one is {t.Status}"));
            if (await RoleOf(db, actor.UserId) is not (Roles.ReleaseManager or Roles.GovernanceOfficer))
                f.Add(new(TemplateGuards.TemplateApproverRole, "Templates are retired by a Release Manager or a Governance Officer"));
            if (f.Count > 0) return ServiceResult<TemplateDetail>.Fail(f);

            var before = new { status = t.Status, approvedBy = t.ApprovedByUserId, t.ApprovedAt, t.ReviewDueOn };
            // The schema CHECK ties ApprovedBy/ApprovedAt to Status = 'Approved', so retiring clears them; the audit row above keeps who approved and when.
            t.Status = "Retired"; t.ApprovedByUserId = null; t.ApprovedAt = null;
            t.Version++;
            Audit(db, actor, null, "TrainTemplate", id, "Retire", before, new { status = t.Status, t.ReviewDueOn });
            await db.SaveChangesAsync(ct);
            return ServiceResult<TemplateDetail>.Ok(await DetailAsync(db, t, ct));
        }, ct);

    // ---- helpers
    private async Task<List<GuardFailure>> ValidateAsync(ReleaseDbContext db, TemplateInput i, string name, TrainTemplates? self, CancellationToken ct)
    {
        var f = new List<GuardFailure>();
        GuardFailure Bad(string m) => new(TemplateGuards.TemplateInvalid, m);
        if (name.Length == 0) f.Add(Bad("A template needs a name"));
        else
        {
            // Names are unique per workspace; the versions of one template share its name, so a name is taken unless the other template is this one's own lineage.
            var lineage = self is null ? new HashSet<string>() : await LineageAsync(db, self, ct);
            var others = await db.Set<TrainTemplates>().Where(x => x.Name.ToLower() == name.ToLower()).Select(x => x.Id).ToListAsync(ct);
            if (others.Any(o => o != self?.Id && !lineage.Contains(o)))
                f.Add(new(TemplateGuards.TemplateNameTaken, $"A template named \"{name}\" already exists"));
        }
        if (i.DefaultRiskTier is not null && !Tiers.Contains(i.DefaultRiskTier)) f.Add(Bad($"Default risk tier must be one of {string.Join(", ", Tiers)}"));

        var teams = (await db.Set<Teams>().Select(t => t.Id).ToListAsync(ct)).ToHashSet();
        var gates = i.Gates ?? [];
        var gateNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var n = 0; n < gates.Count; n++)
        {
            var g = gates[n]; var at = $"Gate {n + 1}";
            if (string.IsNullOrWhiteSpace(g.GateName)) f.Add(Bad($"{at} needs a name"));
            else if (!gateNames.Add(g.GateName.Trim())) f.Add(Bad($"{at}: the gate name {g.GateName.Trim()} is used twice (a release cannot have two gates of one name)"));
            if (g.GateClass is not null && !Classes.Contains(g.GateClass)) f.Add(Bad($"{at}: class must be Standard or Compliance"));
            if (g.OffsetDays < 0) f.Add(Bad($"{at}: offset is business days before the target and cannot be negative"));
            if (!Before.Contains(g.RequiredBeforeStatus)) f.Add(Bad($"{at}: required-before status must be Gated, Executing or Complete"));
            if (g.OwnerTeamId is not null && !teams.Contains(g.OwnerTeamId)) f.Add(Bad($"{at}: unknown owner team"));
            var tasks = g.Tasks ?? [];
            for (var k = 0; k < tasks.Count; k++)
            {
                var task = tasks[k]; var tat = $"{at}, task {k + 1}";
                var text = (task.TaskDescription ?? "").Trim();
                if (text.Length == 0) f.Add(Bad($"{tat} needs a description"));
                else if (text.Length > MaxTaskDescription) f.Add(Bad($"{tat}: a task description is at most {MaxTaskDescription} characters (this one has {text.Length})"));
                if (task.OwnerTeamId is not null && !teams.Contains(task.OwnerTeamId)) f.Add(Bad($"{tat}: unknown owner team"));
            }
        }
        var steps = i.Steps ?? [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var n = 0; n < steps.Count; n++)
        {
            var s = steps[n]; var at = $"Step {n + 1}";
            if (string.IsNullOrWhiteSpace(s.StepCode)) f.Add(Bad($"{at} needs a code"));
            else if (!seen.Add(s.StepCode.Trim())) f.Add(Bad($"{at}: step code {s.StepCode.Trim()} is used twice"));
            if (string.IsNullOrWhiteSpace(s.Title)) f.Add(Bad($"{at} needs a title"));
            if (!Sections.Contains(s.Section)) f.Add(Bad($"{at}: section must be one of {string.Join(", ", Sections)}"));
            if (s.PlannedDurationMin <= 0) f.Add(Bad($"{at}: planned duration must be at least 1 minute"));
            if (s.OwnerTeamId is not null && !teams.Contains(s.OwnerTeamId)) f.Add(Bad($"{at}: unknown owner team"));
        }
        var schedule = i.Schedule ?? [];
        var lib = (await db.Set<CommTemplateLibrary>().Select(l => l.Id).ToListAsync(ct)).ToHashSet();
        var pairs = new HashSet<(string, int)>();
        for (var n = 0; n < schedule.Count; n++)
        {
            var c = schedule[n]; var at = $"T-minus item {n + 1}";
            if (!lib.Contains(c.LibraryTemplateId)) f.Add(Bad($"{at}: unknown message template"));
            else if (!pairs.Add((c.LibraryTemplateId, c.OffsetDays))) f.Add(Bad($"{at}: the same message on the same day is listed twice"));
        }
        var milestones = i.Milestones ?? [];
        for (var n = 0; n < milestones.Count; n++)
        {
            var m = milestones[n]; var at = $"Milestone {n + 1}";
            var text = (m.Name ?? "").Trim();
            if (text.Length == 0) f.Add(Bad($"{at} needs a name"));
            else if (text.Length > MaxMilestoneName) f.Add(Bad($"{at}: a milestone name is at most {MaxMilestoneName} characters (this one has {text.Length})"));
            if (m.OffsetDays is < -MaxOffsetDays or > MaxOffsetDays) f.Add(Bad($"{at}: the offset is calendar days from the target, from -{MaxOffsetDays} (before) to {MaxOffsetDays} (after)"));
            if (m.OwnerTeamId is not null && !teams.Contains(m.OwnerTeamId)) f.Add(Bad($"{at}: unknown owner team"));
        }
        return f;
    }

    /// <summary>The ids of every template in this one's lineage (the same name, any version).</summary>
    private static async Task<HashSet<string>> LineageAsync(ReleaseDbContext db, TrainTemplates t, CancellationToken ct) =>
        (await db.Set<TrainTemplates>().AsNoTracking().Where(x => x.Name == t.Name).Select(x => x.Id).ToListAsync(ct)).ToHashSet();

    private static void AddChildren(ReleaseDbContext db, string templateId, TemplateInput i)
    {
        var order = 0;
        foreach (var g in i.Gates ?? [])
        {
            var row = new TemplateGates { Id = Ids.New(), TemplateId = templateId, GateName = g.GateName.Trim(), GateClass = g.GateClass ?? "Standard", SequenceOrder = ++order, OffsetDays = g.OffsetDays, RequiredBeforeStatus = g.RequiredBeforeStatus, OwnerTeamId = g.OwnerTeamId };
            db.Set<TemplateGates>().Add(row);
            var k = 0;
            foreach (var task in g.Tasks ?? [])
                db.Set<TemplateTasks>().Add(new TemplateTasks { Id = Ids.New(), TemplateId = templateId, TemplateGateId = row.Id, TaskDescription = task.TaskDescription.Trim(), OwnerTeamId = task.OwnerTeamId, SequenceOrder = ++k });
        }
        foreach (var s in i.Steps ?? [])
            db.Set<TemplateSteps>().Add(new TemplateSteps { Id = Ids.New(), TemplateId = templateId, StepCode = s.StepCode.Trim(), Section = s.Section, Title = s.Title.Trim(), OffsetMinutes = s.OffsetMinutes, PlannedDurationMin = s.PlannedDurationMin, OwnerTeamId = s.OwnerTeamId });
        foreach (var c in i.Schedule ?? [])
            db.Set<TemplateCommSchedule>().Add(new TemplateCommSchedule { Id = Ids.New(), TemplateId = templateId, LibraryTemplateId = c.LibraryTemplateId, OffsetDays = c.OffsetDays });
        var mo = 0;
        foreach (var m in i.Milestones ?? [])
            db.Set<TemplateMilestones>().Add(new TemplateMilestones { Id = Ids.New(), TemplateId = templateId, Name = m.Name.Trim(), OffsetDays = m.OffsetDays, OwnerTeamId = m.OwnerTeamId, SequenceOrder = ++mo });
    }

    private static object Shape(TrainTemplates t, TemplateInput i) => new
    {
        t.Name, t.DefaultRiskTier, t.VersionNo, gates = i.Gates?.Count ?? 0, tasks = i.Gates?.Sum(g => g.Tasks?.Count ?? 0) ?? 0, steps = i.Steps?.Count ?? 0, schedule = i.Schedule?.Count ?? 0, milestones = i.Milestones?.Count ?? 0,
        i.Gates, i.Steps, i.Schedule, i.Milestones,
    };
    private static object Shape(TemplateDetail d) => new
    {
        d.Template.Name, d.Template.DefaultRiskTier, d.Template.VersionNo, gates = d.Gates.Count, tasks = d.Gates.Sum(g => g.Tasks.Count), steps = d.Steps.Count, schedule = d.Schedule.Count, milestones = d.Milestones.Count,
        d.Gates, d.Steps, d.Schedule, d.Milestones,
    };

    private async Task<TemplateSummary> SummaryAsync(ReleaseDbContext db, TrainTemplates t, CancellationToken ct)
    {
        async Task<string?> NameOf(string? userId) => userId is null ? null : await db.Set<Users>().Where(u => u.Id == userId).Select(u => u.DisplayName).SingleOrDefaultAsync(ct);
        var next = await db.Set<TrainTemplates>().AsNoTracking().Where(x => x.SupersedesTemplateId == t.Id).Select(x => x.Id).FirstOrDefaultAsync(ct);
        return new TemplateSummary(t.Id, t.Name, t.Status, t.DefaultRiskTier, t.ApprovedByUserId, await NameOf(t.ApprovedByUserId), t.ApprovedAt, t.ReviewDueOn,
            t.Status == "Approved" && t.ReviewDueOn is DateOnly d && d < Today,
            await db.Set<TemplateGates>().CountAsync(x => x.TemplateId == t.Id, ct),
            await db.Set<TemplateSteps>().CountAsync(x => x.TemplateId == t.Id, ct),
            await db.Set<TemplateCommSchedule>().CountAsync(x => x.TemplateId == t.Id, ct), t.Version,
            t.VersionNo, t.SupersedesTemplateId, next, t.CreatedByUserId, await NameOf(t.CreatedByUserId), t.LastEditedByUserId, await NameOf(t.LastEditedByUserId),
            await db.Set<TemplateTasks>().CountAsync(x => x.TemplateId == t.Id, ct), await db.Set<TemplateMilestones>().CountAsync(x => x.TemplateId == t.Id, ct));
    }

    private async Task<TemplateDetail> DetailAsync(ReleaseDbContext db, TrainTemplates t, CancellationToken ct)
    {
        var core = await CoreAsync(db, t, ct);
        TemplateDetail? previous = t.SupersedesTemplateId is null ? null
            : await db.Set<TrainTemplates>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == t.SupersedesTemplateId, ct) is { } p ? await CoreAsync(db, p, ct) : null;
        var now = Snapshot(core);
        var before = previous is null ? null : Snapshot(previous);
        // The changes list is for a version: a first version would only list everything it has.
        var changes = previous is null ? [] : TemplateDiff.Between(before, now);
        var complianceChanged = TemplateDiff.ComplianceChanged(before, now);
        var lineage = (await db.Set<TrainTemplates>().AsNoTracking().Where(x => x.Name == t.Name).ToListAsync(ct)).OrderByDescending(x => x.VersionNo).ToList();
        var versions = new List<TemplateVersionLine>();
        foreach (var v in lineage)
            versions.Add(new TemplateVersionLine(v.Id, v.VersionNo, v.Status, v.ApprovedByUserId is null ? null : await db.Set<Users>().Where(u => u.Id == v.ApprovedByUserId).Select(u => u.DisplayName).SingleOrDefaultAsync(ct), v.ApprovedAt));
        return core with
        {
            Versions = versions, Changes = changes, ComplianceChanged = complianceChanged,
            NeedsGovernanceApproval = options.ComplianceChangeNeedsGovernanceOfficer && t.Status == "Draft" && complianceChanged,
        };
    }

    /// <summary>The template's own rows, without its history.</summary>
    private async Task<TemplateDetail> CoreAsync(ReleaseDbContext db, TrainTemplates t, CancellationToken ct)
    {
        var teams = await db.Set<Teams>().AsNoTracking().ToDictionaryAsync(x => x.Id, x => "@" + x.Handle, ct);
        string? Team(string? id) => id is not null && teams.TryGetValue(id, out var n) ? n : null;
        var tasks = (await db.Set<TemplateTasks>().AsNoTracking().Where(x => x.TemplateId == t.Id).OrderBy(x => x.SequenceOrder).ToListAsync(ct))
            .GroupBy(x => x.TemplateGateId).ToDictionary(g => g.Key, g => g.Select(k => new TemplateTaskView(k.Id, k.TaskDescription, k.SequenceOrder, k.OwnerTeamId, Team(k.OwnerTeamId))).ToList());
        var gates = (await db.Set<TemplateGates>().AsNoTracking().Where(x => x.TemplateId == t.Id).OrderBy(x => x.SequenceOrder).ToListAsync(ct))
            .Select(g => new TemplateGateView(g.Id, g.GateName, g.GateClass, g.SequenceOrder, g.OffsetDays, g.RequiredBeforeStatus, g.OwnerTeamId, Team(g.OwnerTeamId), tasks.GetValueOrDefault(g.Id) ?? [])).ToList();
        var steps = (await db.Set<TemplateSteps>().AsNoTracking().Where(x => x.TemplateId == t.Id).OrderBy(x => x.OffsetMinutes).ThenBy(x => x.StepCode).ToListAsync(ct))
            .Select(s => new TemplateStepView(s.Id, s.StepCode, s.Section, s.Title, s.OffsetMinutes, s.PlannedDurationMin, s.OwnerTeamId, Team(s.OwnerTeamId))).ToList();
        var lib = await db.Set<CommTemplateLibrary>().AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
        var schedule = (await db.Set<TemplateCommSchedule>().AsNoTracking().Where(x => x.TemplateId == t.Id).ToListAsync(ct))
            .OrderBy(x => x.OffsetDays).Select(c => new TemplateScheduleView(c.Id, c.LibraryTemplateId, lib[c.LibraryTemplateId].Name, lib[c.LibraryTemplateId].TemplateType, c.OffsetDays)).ToList();
        var milestones = (await db.Set<TemplateMilestones>().AsNoTracking().Where(x => x.TemplateId == t.Id).OrderBy(x => x.SequenceOrder).ToListAsync(ct))
            .Select(m => new TemplateMilestoneView(m.Id, m.Name, m.OffsetDays, m.SequenceOrder, m.OwnerTeamId, Team(m.OwnerTeamId))).ToList();
        return new TemplateDetail(await SummaryAsync(db, t, ct), gates, steps, schedule, milestones, [], [], false, false);
    }

    private static TemplateSnapshot Snapshot(TemplateDetail d) => new(d.Template.DefaultRiskTier,
        [.. d.Gates.Select(g => new TemplateGateSnap(g.GateName, g.GateClass, g.OffsetDays, g.RequiredBeforeStatus, g.OwnerTeamId, [.. g.Tasks.Select(k => new TemplateTaskSnap(k.TaskDescription, k.OwnerTeamId))]))],
        [.. d.Steps.Select(s => new TemplateStepSnap(s.StepCode, s.Section, s.Title, s.OffsetMinutes, s.PlannedDurationMin, s.OwnerTeamId))],
        [.. d.Schedule.Select(c => new TemplateScheduleSnap(c.LibraryTemplateId, c.LibraryName, c.OffsetDays))],
        [.. d.Milestones.Select(m => new TemplateMilestoneSnap(m.Name, m.OffsetDays, m.OwnerTeamId))]);
}
