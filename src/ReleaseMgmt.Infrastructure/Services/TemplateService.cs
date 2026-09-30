using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

/// <summary>Guard names of the train-template lifecycle (REOS-38). Kept apart from <see cref="Guards"/> so parallel streams do not collide.</summary>
public static class TemplateGuards
{
    public const string TemplateNotDraft = "TemplateNotDraft";
    public const string IllegalTemplateTransition = "IllegalTemplateTransition";
    public const string TemplateNameTaken = "TemplateNameTaken";
    public const string TemplateInvalid = "TemplateInvalid";
    public const string TemplateNeedsGate = "TemplateNeedsGate";
    public const string TemplateApproverRole = "TemplateApproverRole";
}

public sealed record TemplateGateInput(string GateName, string? GateClass, int OffsetDays, string RequiredBeforeStatus, string? OwnerTeamId);
public sealed record TemplateStepInput(string StepCode, string Section, string Title, int OffsetMinutes, int PlannedDurationMin, string? OwnerTeamId);
public sealed record TemplateScheduleInput(string LibraryTemplateId, int OffsetDays);
public sealed record TemplateInput(string? Name, string? DefaultRiskTier, IReadOnlyList<TemplateGateInput>? Gates, IReadOnlyList<TemplateStepInput>? Steps, IReadOnlyList<TemplateScheduleInput>? Schedule);

public sealed record TemplateSummary(string Id, string Name, string Status, string DefaultRiskTier, string? ApprovedByUserId, string? ApprovedByName, DateTime? ApprovedAt,
    DateOnly? ReviewDueOn, bool ReviewOverdue, int GateCount, int StepCount, int ScheduleCount, int Version);
public sealed record TemplateGateView(string Id, string GateName, string GateClass, int SequenceOrder, int OffsetDays, string RequiredBeforeStatus, string? OwnerTeamId, string? OwnerTeamName);
public sealed record TemplateStepView(string Id, string StepCode, string Section, string Title, int OffsetMinutes, int PlannedDurationMin, string? OwnerTeamId, string? OwnerTeamName);
public sealed record TemplateScheduleView(string Id, string LibraryTemplateId, string LibraryName, string TemplateType, int OffsetDays);
public sealed record TemplateDetail(TemplateSummary Template, IReadOnlyList<TemplateGateView> Gates, IReadOnlyList<TemplateStepView> Steps, IReadOnlyList<TemplateScheduleView> Schedule)
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
/// </summary>
public sealed class TemplateService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IRealtimePublisher? realtime = null) : ServiceBase(dbf, time, realtime)
{
    /// <summary>Re-review cycle applied on approval (schema comment: default +12 months).</summary>
    public const int ReviewMonths = 12;

    private static readonly string[] Classes = ["Standard", "Compliance"];
    private static readonly string[] Before = ["Gated", "Executing", "Complete"];
    private static readonly string[] Sections = ["PreCheck", "Deploy", "Verify", "Rollback", "Hypercare"];
    private static readonly string[] Tiers = ["Low", "Moderate", "High", "VeryHigh"];

    private DateOnly Today => DateOnly.FromDateTime(Now);

    // ---- reads
    public async Task<List<TemplateSummary>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var rows = await db.Set<TrainTemplates>().AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct);
        var result = new List<TemplateSummary>();
        foreach (var t in rows) result.Add(await SummaryAsync(db, t, ct));
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
            var t = new TrainTemplates { Id = Ids.New(), Name = name, Status = "Draft", DefaultRiskTier = input.DefaultRiskTier ?? "Moderate" };
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
                return ServiceResult<TemplateDetail>.Fail(new GuardFailure(TemplateGuards.TemplateNotDraft, $"Only a Draft template can be edited; this one is {t.Status}. Create a new draft instead"));
            var name = (input.Name ?? t.Name).Trim();
            var bad = await ValidateAsync(db, input, name, t.Id, ct);
            if (bad.Count > 0) return ServiceResult<TemplateDetail>.Fail(bad);

            var before = await DetailAsync(db, t, ct);
            // Children are replaced wholesale (SequenceOrder is unique per template, so delete before insert).
            await db.Set<TemplateGates>().Where(x => x.TemplateId == id).ExecuteDeleteAsync(ct);
            await db.Set<TemplateSteps>().Where(x => x.TemplateId == id).ExecuteDeleteAsync(ct);
            await db.Set<TemplateCommSchedule>().Where(x => x.TemplateId == id).ExecuteDeleteAsync(ct);
            t.Name = name;
            t.DefaultRiskTier = input.DefaultRiskTier ?? t.DefaultRiskTier;
            t.Version++;
            AddChildren(db, id, input);
            Audit(db, actor, null, "TrainTemplate", id, "Update", Shape(before), Shape(t, input));
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
            if (t.Status != "Draft") f.Add(new(TemplateGuards.IllegalTemplateTransition, $"Only a Draft template can be approved; this one is {t.Status}"));
            if (await RoleOf(db, actor.UserId) is not (Roles.ReleaseManager or Roles.GovernanceOfficer))
                f.Add(new(TemplateGuards.TemplateApproverRole, "Templates are approved by a Release Manager or a Governance Officer"));
            if (t.Status == "Draft" && !await db.Set<TemplateGates>().AnyAsync(g => g.TemplateId == id, ct))
                f.Add(new(TemplateGuards.TemplateNeedsGate, "A template needs at least one gate before it can be approved"));
            if (f.Count > 0) return ServiceResult<TemplateDetail>.Fail(f);

            var now = Now;
            t.Status = "Approved"; t.ApprovedByUserId = actor.UserId; t.ApprovedAt = now;
            t.ReviewDueOn = DateOnly.FromDateTime(now).AddMonths(ReviewMonths);
            t.Version++;
            Audit(db, actor, null, "TrainTemplate", id, "Approve", new { status = "Draft" }, new { status = t.Status, t.ReviewDueOn });
            await db.SaveChangesAsync(ct);
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
    private async Task<List<GuardFailure>> ValidateAsync(ReleaseDbContext db, TemplateInput i, string name, string? selfId, CancellationToken ct)
    {
        var f = new List<GuardFailure>();
        GuardFailure Bad(string m) => new(TemplateGuards.TemplateInvalid, m);
        if (name.Length == 0) f.Add(Bad("A template needs a name"));
        else if (await db.Set<TrainTemplates>().AnyAsync(x => x.Id != selfId && x.Name.ToLower() == name.ToLower(), ct))
            f.Add(new(TemplateGuards.TemplateNameTaken, $"A template named \"{name}\" already exists"));
        if (i.DefaultRiskTier is not null && !Tiers.Contains(i.DefaultRiskTier)) f.Add(Bad($"Default risk tier must be one of {string.Join(", ", Tiers)}"));

        var teams = (await db.Set<Teams>().Select(t => t.Id).ToListAsync(ct)).ToHashSet();
        var gates = i.Gates ?? [];
        for (var n = 0; n < gates.Count; n++)
        {
            var g = gates[n]; var at = $"Gate {n + 1}";
            if (string.IsNullOrWhiteSpace(g.GateName)) f.Add(Bad($"{at} needs a name"));
            if (g.GateClass is not null && !Classes.Contains(g.GateClass)) f.Add(Bad($"{at}: class must be Standard or Compliance"));
            if (g.OffsetDays < 0) f.Add(Bad($"{at}: offset is business days before the target and cannot be negative"));
            if (!Before.Contains(g.RequiredBeforeStatus)) f.Add(Bad($"{at}: required-before status must be Gated, Executing or Complete"));
            if (g.OwnerTeamId is not null && !teams.Contains(g.OwnerTeamId)) f.Add(Bad($"{at}: unknown owner team"));
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
        return f;
    }

    private static void AddChildren(ReleaseDbContext db, string templateId, TemplateInput i)
    {
        var order = 0;
        foreach (var g in i.Gates ?? [])
            db.Set<TemplateGates>().Add(new TemplateGates { Id = Ids.New(), TemplateId = templateId, GateName = g.GateName.Trim(), GateClass = g.GateClass ?? "Standard", SequenceOrder = ++order, OffsetDays = g.OffsetDays, RequiredBeforeStatus = g.RequiredBeforeStatus, OwnerTeamId = g.OwnerTeamId });
        foreach (var s in i.Steps ?? [])
            db.Set<TemplateSteps>().Add(new TemplateSteps { Id = Ids.New(), TemplateId = templateId, StepCode = s.StepCode.Trim(), Section = s.Section, Title = s.Title.Trim(), OffsetMinutes = s.OffsetMinutes, PlannedDurationMin = s.PlannedDurationMin, OwnerTeamId = s.OwnerTeamId });
        foreach (var c in i.Schedule ?? [])
            db.Set<TemplateCommSchedule>().Add(new TemplateCommSchedule { Id = Ids.New(), TemplateId = templateId, LibraryTemplateId = c.LibraryTemplateId, OffsetDays = c.OffsetDays });
    }

    private static object Shape(TrainTemplates t, TemplateInput i) => new { t.Name, t.DefaultRiskTier, gates = i.Gates?.Count ?? 0, steps = i.Steps?.Count ?? 0, schedule = i.Schedule?.Count ?? 0, i.Gates, i.Steps, i.Schedule };
    private static object Shape(TemplateDetail d) => new { d.Template.Name, d.Template.DefaultRiskTier, gates = d.Gates.Count, steps = d.Steps.Count, schedule = d.Schedule.Count, d.Gates, d.Steps, d.Schedule };

    private async Task<TemplateSummary> SummaryAsync(ReleaseDbContext db, TrainTemplates t, CancellationToken ct)
    {
        var approver = t.ApprovedByUserId is null ? null : await db.Set<Users>().Where(u => u.Id == t.ApprovedByUserId).Select(u => u.DisplayName).SingleOrDefaultAsync(ct);
        return new TemplateSummary(t.Id, t.Name, t.Status, t.DefaultRiskTier, t.ApprovedByUserId, approver, t.ApprovedAt, t.ReviewDueOn,
            t.Status == "Approved" && t.ReviewDueOn is DateOnly d && d < Today,
            await db.Set<TemplateGates>().CountAsync(x => x.TemplateId == t.Id, ct),
            await db.Set<TemplateSteps>().CountAsync(x => x.TemplateId == t.Id, ct),
            await db.Set<TemplateCommSchedule>().CountAsync(x => x.TemplateId == t.Id, ct), t.Version);
    }

    private async Task<TemplateDetail> DetailAsync(ReleaseDbContext db, TrainTemplates t, CancellationToken ct)
    {
        var teams = await db.Set<Teams>().AsNoTracking().ToDictionaryAsync(x => x.Id, x => "@" + x.Handle, ct);
        string? Team(string? id) => id is not null && teams.TryGetValue(id, out var n) ? n : null;
        var gates = (await db.Set<TemplateGates>().AsNoTracking().Where(x => x.TemplateId == t.Id).OrderBy(x => x.SequenceOrder).ToListAsync(ct))
            .Select(g => new TemplateGateView(g.Id, g.GateName, g.GateClass, g.SequenceOrder, g.OffsetDays, g.RequiredBeforeStatus, g.OwnerTeamId, Team(g.OwnerTeamId))).ToList();
        var steps = (await db.Set<TemplateSteps>().AsNoTracking().Where(x => x.TemplateId == t.Id).OrderBy(x => x.OffsetMinutes).ThenBy(x => x.StepCode).ToListAsync(ct))
            .Select(s => new TemplateStepView(s.Id, s.StepCode, s.Section, s.Title, s.OffsetMinutes, s.PlannedDurationMin, s.OwnerTeamId, Team(s.OwnerTeamId))).ToList();
        var lib = await db.Set<CommTemplateLibrary>().AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
        var schedule = (await db.Set<TemplateCommSchedule>().AsNoTracking().Where(x => x.TemplateId == t.Id).ToListAsync(ct))
            .OrderBy(x => x.OffsetDays).Select(c => new TemplateScheduleView(c.Id, c.LibraryTemplateId, lib[c.LibraryTemplateId].Name, lib[c.LibraryTemplateId].TemplateType, c.OffsetDays)).ToList();
        return new TemplateDetail(await SummaryAsync(db, t, ct), gates, steps, schedule);
    }
}
