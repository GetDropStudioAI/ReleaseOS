using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Reminders;

namespace ReleaseMgmt.Infrastructure.Services;

/// <summary>Guard names of train creation (REOS-80). Kept apart from <see cref="Guards"/> so parallel streams do not collide.</summary>
public static class TrainCreationGuards
{
    public const string TrainInvalid = "TrainInvalid";
    public const string TrainTitleTaken = "TrainTitleTaken";
    public const string TemplateNotApproved = "TemplateNotApproved";
}

/// <summary><see cref="ServiceKey"/> (REOS-109) links the product to the workspace's service catalog entry with that key; optional.</summary>
public sealed record NewProductInput(string? ProductName, string? VersionTag, string? ProjectCode, string? ServiceKey = null);

/// <summary><c>POST /trains</c>: a blank train, or one built from an Approved template when <see cref="TemplateId"/> is set. Dates are strings so a bad one is a readable 422, not a 400.
/// REOS-107: the release's owner, team, branch, on-call and target environments are optional. REOS-116 (D46, D48): the release type is required once the workspace has types (a Beta type also takes its number, 1 to 3),
/// and the type's default template is used when no template is named.</summary>
public sealed record NewTrainInput(string? Title, string? TargetReleaseDate, string? RiskTier, string? TemplateId = null, string? WindowStartsAt = null, string? WindowEndsAt = null,
    IReadOnlyList<NewProductInput>? Products = null, string? OwnerUserId = null, string? OwnerTeamId = null, string? ReleaseBranch = null, string? OnCallUserId = null,
    string? OnCallTeamId = null, string? ReleaseTypeId = null, IReadOnlyList<string>? EnvironmentIds = null, int? BetaNumber = null);

/// <summary><c>POST /trains/{id}:clone</c>. The risk tier defaults to the source train's.</summary>
public sealed record CloneTrainInput(string? Title, string? TargetReleaseDate, string? RiskTier = null);

public sealed record CreatedCounts(int Products, int Gates, int Tasks, int Steps, int Dependencies, int CommTemplates, int CommSchedule, bool Window);

/// <summary>What was created. <see cref="Notes"/> says what was deliberately not copied or was substituted, in words the drawer shows as they are.</summary>
public sealed record CreatedTrain(string Id, string Title, string Status, string RiskTier, string TargetReleaseDate, string Source, string? TemplateId, string? ClonedFromTrainId,
    int Version, CreatedCounts Created, IReadOnlyList<string> Notes, string? ReleaseKey = null);

/// <summary>
/// Creates a train three ways (PROJECT_SCOPE section 2: "Create, clone from template or prior train"; REOS-80, Q-038t3, Q-080a): blank, from an Approved template,
/// or as a copy of a prior train's plan. Everything happens in one transaction; every created row writes its own <c>Create</c> audit row (as the CSV import writes one
/// row per inserted row), except the T-minus schedule, which reuses the seed and writes its one <c>Seed</c> row (Q-043e). New trains always start in Planning:
/// no status is set here beyond the column default, and gates start Pending.
/// </summary>
public sealed class TrainCreationService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, DisplayClock clock, IRealtimePublisher? realtime = null) : ServiceBase(dbf, time, realtime)
{
    // Field limits follow the CSV import (ImportKinds): Title 200, ProductName 200, VersionTag 100, ProjectCode 100.
    public const int MaxTitle = 200, MaxProductName = 200, MaxVersionTag = 100, MaxProjectCode = 100, MaxProducts = 200;
    private static readonly string[] Tiers = ["Low", "Moderate", "High", "VeryHigh"];

    private static GuardFailure Bad(string m) => new(TrainCreationGuards.TrainInvalid, m);

    public Task<ServiceResult<CreatedTrain>> CreateAsync(NewTrainInput input, Actor actor, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var f = new List<GuardFailure>();
            var title = (input.Title ?? "").Trim();
            await CheckTitleAsync(db, title, f, ct);
            var target = ParseTarget(input.TargetReleaseDate, f);
            if (input.RiskTier is not null && !Tiers.Contains(input.RiskTier)) f.Add(Bad($"Risk tier must be one of {string.Join(", ", Tiers)}"));
            var window = ParseWindow(input.WindowStartsAt, input.WindowEndsAt, f);
            var products = CheckProducts(input.Products ?? [], f);
            var serviceIds = await ResolveServicesAsync(db, products, f, ct);
            var fields = new NewReleaseFields(Blank(input.OwnerUserId), Blank(input.OwnerTeamId), Blank(input.ReleaseBranch), Blank(input.OnCallUserId), Blank(input.OnCallTeamId), Blank(input.ReleaseTypeId),
                input.EnvironmentIds is { Count: > 0 } ? input.EnvironmentIds : null, input.BetaNumber);
            f.AddRange(await ReleaseModelService.ValidateAsync(db, fields.OwnerUserId, fields.OwnerTeamId, fields.ReleaseBranch, fields.OnCallUserId, fields.OnCallTeamId, fields.ReleaseTypeId, fields.EnvironmentIds, ct,
                betaNumber: fields.BetaNumber, requireType: true, checkBeta: true));

            var notes = new List<string>();
            TrainTemplates? template = null;
            var templateId = Blank(input.TemplateId);
            var fromType = false;
            if (templateId is null && fields.ReleaseTypeId is not null)
            {
                templateId = await db.Set<ReleaseTypes>().AsNoTracking().Where(x => x.Id == fields.ReleaseTypeId).Select(x => x.DefaultTemplateId).SingleOrDefaultAsync(ct);
                fromType = templateId is not null;
            }
            if (templateId is not null)
            {
                template = await db.Set<TrainTemplates>().AsNoTracking().SingleOrDefaultAsync(t => t.Id == templateId, ct);
                if (template is null) return ServiceResult<CreatedTrain>.NotFound("template");
                if (template.Status != "Approved" && fromType)
                {
                    notes.Add($"The release type's default template \"{template.Name}\" is {template.Status}, so the release started blank.");
                    template = null;
                }
                else if (template.Status != "Approved")
                    f.Add(new(TrainCreationGuards.TemplateNotApproved, $"Template \"{template.Name}\" is {template.Status}; only an Approved template can start a train"));
            }
            if (f.Count > 0) return ServiceResult<CreatedTrain>.Fail(f);

            var now = Now;
            var train = new ReleaseTrains
            {
                Id = Ids.New(), Title = title, TemplateId = template?.Id, TargetReleaseDate = target!.Value, RiskTier = input.RiskTier ?? template?.DefaultRiskTier ?? "Moderate",
                CreatedAt = now, UpdatedAt = now, LastChangedByUserId = actor.UserId, LastChangedAt = now,
                ReleaseKey = await ReleaseKeyAllocator.AllocateAsync(db, ct),
            };
            ReleaseModelService.Apply(train, fields);
            db.Set<ReleaseTrains>().Add(train);
            ReleaseModelService.AddEnvironments(db, train.Id, fields.EnvironmentIds);
            await db.SaveChangesAsync(ct);   // the parent first: every child references it

            DeploymentWindows? win = null;
            if (window is { } w)
            {
                win = new DeploymentWindows { Id = Ids.New(), ReleaseTrainId = train.Id, StartsAt = w.Start, EndsAt = w.End };
                db.Set<DeploymentWindows>().Add(win);
                Audit(db, actor, train.Id, "DeploymentWindow", win.Id, "Create", null, new { win.StartsAt, win.EndsAt });
            }
            foreach (var p in products)
            {
                var row = new BundledProducts { Id = Ids.New(), ReleaseTrainId = train.Id, ProductName = p.ProductName!, VersionTag = p.VersionTag!, ProjectCode = p.ProjectCode!, CatalogServiceId = serviceIds.GetValueOrDefault(p.ProductName!) };
                db.Set<BundledProducts>().Add(row);
                Audit(db, actor, train.Id, "BundledProduct", row.Id, "Create", null, new { row.ProductName, row.VersionTag, row.ProjectCode, row.CatalogServiceId });
            }

            int gates = 0, steps = 0, schedule = 0, comms = 0;
            if (template is not null)
            {
                var holidays = await HolidaysAsync(db, ct);
                foreach (var tg in await db.Set<TemplateGates>().AsNoTracking().Where(g => g.TemplateId == template.Id).OrderBy(g => g.SequenceOrder).ToListAsync(ct))
                {
                    // Q-080a: the template's team owns the gate; a gate with no team goes to the person creating the train (who can reassign it).
                    var g = new StageGates
                    {
                        Id = Ids.New(), ReleaseTrainId = train.Id, GateName = tg.GateName, GateClass = tg.GateClass, SequenceOrder = tg.SequenceOrder, OffsetDays = tg.OffsetDays,
                        DueOn = BusinessDays.SubtractBusinessDays(target.Value, tg.OffsetDays, holidays), RequiredBeforeStatus = tg.RequiredBeforeStatus,
                        OwnerTeamId = tg.OwnerTeamId, OwnerUserId = tg.OwnerTeamId is null ? actor.UserId : null, LastChangedByUserId = actor.UserId, LastChangedAt = now,
                    };
                    db.Set<StageGates>().Add(g); gates++;
                    Audit(db, actor, train.Id, "StageGate", g.Id, "Create", null, new { g.GateName, g.GateClass, g.SequenceOrder, g.OffsetDays, dueOn = g.DueOn.ToString("yyyy-MM-dd"), g.RequiredBeforeStatus, g.OwnerUserId, g.OwnerTeamId, fromTemplate = template.Id });
                }
                var templateSteps = await db.Set<TemplateSteps>().AsNoTracking().Where(s => s.TemplateId == template.Id).OrderBy(s => s.OffsetMinutes).ThenBy(s => s.StepCode).ToListAsync(ct);
                if (templateSteps.Count > 0 && win is null)
                    notes.Add($"The template's {Plural(templateSteps.Count, "runbook step")} were not added: steps are planned from the deployment window's start, and no window was given. Set the window, then add the steps.");
                else if (win is not null)
                    foreach (var ts in templateSteps)
                    {
                        var s = new RunbookSteps
                        {
                            Id = Ids.New(), ReleaseTrainId = train.Id, StepCode = ts.StepCode, Section = ts.Section, Title = ts.Title, PlannedStartAt = win.StartsAt.AddMinutes(ts.OffsetMinutes),
                            PlannedDurationMin = ts.PlannedDurationMin, OwnerTeamId = ts.OwnerTeamId, OwnerUserId = ts.OwnerTeamId is null ? actor.UserId : null,
                        };
                        db.Set<RunbookSteps>().Add(s); steps++;
                        Audit(db, actor, train.Id, "RunbookStep", s.Id, "Create", null, new { s.StepCode, s.Section, s.Title, s.PlannedStartAt, s.PlannedDurationMin, s.OwnerUserId, s.OwnerTeamId, fromTemplate = template.Id });
                    }
                await db.SaveChangesAsync(ct);

                var plan = await db.Set<TemplateCommSchedule>().AsNoTracking().Where(p => p.TemplateId == template.Id).ToListAsync(ct);
                if (plan.Count > 0)
                {
                    // The same seeding as POST /trains/{id}/comm-schedule:seed (Q-043e), inside this transaction.
                    var (created, copied) = await CommScheduleService.AddRowsAsync(db, train, plan, win, CommScheduling.Options.Default(clock.Zone), ct);
                    Audit(db, actor, train.Id, "CommSchedule", train.Id, "Seed", null, new { templateId = template.Id, template = template.Name, targetDate = target.Value.ToString("yyyy-MM-dd"), items = created, copiedMessages = copied });
                    schedule = created.Count; comms = copied.Count;
                }
            }

            Audit(db, actor, train.Id, "ReleaseTrain", train.Id, "Create", null, new
            {
                train.Title, targetReleaseDate = target.Value.ToString("yyyy-MM-dd"), train.RiskTier, source = template is null ? "Blank" : "Template", templateId = template?.Id, template = template?.Name, fromReleaseType = fromType && template is not null,
                release = ReleaseModelService.Snapshot(train, fields.EnvironmentIds ?? []),
            });
            await db.SaveChangesAsync(ct);
            return ServiceResult<CreatedTrain>.Ok(View(train, template is null ? "Blank" : "Template", new CreatedCounts(products.Count, gates, 0, steps, 0, comms, schedule, win is not null), notes));
        }, ct);

    public Task<ServiceResult<CreatedTrain>> CloneAsync(string sourceId, CloneTrainInput input, Actor actor, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var src = await db.Set<ReleaseTrains>().AsNoTracking().SingleOrDefaultAsync(t => t.Id == sourceId, ct);
            if (src is null) return ServiceResult<CreatedTrain>.NotFound("train");
            var f = new List<GuardFailure>();
            var title = (input.Title ?? "").Trim();
            await CheckTitleAsync(db, title, f, ct);
            var target = ParseTarget(input.TargetReleaseDate, f);
            if (input.RiskTier is not null && !Tiers.Contains(input.RiskTier)) f.Add(Bad($"Risk tier must be one of {string.Join(", ", Tiers)}"));
            if (f.Count > 0) return ServiceResult<CreatedTrain>.Fail(f);

            var now = Now;
            var newTarget = target!.Value;
            var shiftDays = newTarget.DayNumber - src.TargetReleaseDate.DayNumber;   // Q-080a: window and step times move by the same calendar days as the target
            var holidays = await HolidaysAsync(db, ct);
            var active = (await Memberships.Members(db).AsNoTracking().Where(u => u.IsActive).Select(u => u.Id).ToListAsync(ct)).ToHashSet();   // an owner who left the workspace is reassigned too
            var notes = new List<string>();
            var reassigned = 0;
            string? Owner(string? userId) { if (userId is null || active.Contains(userId)) return userId; reassigned++; return actor.UserId; }

            var train = new ReleaseTrains
            {
                Id = Ids.New(), Title = title, ClonedFromTrainId = src.Id, TargetReleaseDate = newTarget, RiskTier = input.RiskTier ?? src.RiskTier,
                CreatedAt = now, UpdatedAt = now, LastChangedByUserId = actor.UserId, LastChangedAt = now,
                ReleaseKey = await ReleaseKeyAllocator.AllocateAsync(db, ct),
            };
            // REOS-107: the copy keeps the source's team, branch, on-call, type and target environments; a person who left the workspace is dropped, with a note. The key is a new one.
            var srcEnvIds = await db.Set<TrainEnvironments>().AsNoTracking().Where(e => e.ReleaseTrainId == src.Id).Select(e => e.EnvironmentId).ToListAsync(ct);
            var liveEnvIds = (await db.Set<Environments>().AsNoTracking().Where(e => srcEnvIds.Contains(e.Id) && e.ArchivedAt == null).Select(e => e.Id).ToListAsync(ct));
                        string? KeepPerson(string? id) { if (id is null || active.Contains(id)) return id; notes.Add("The owner or on-call person of the original is no longer an active member of this workspace and was left empty."); return null; }
            var cloneFields = new NewReleaseFields(KeepPerson(src.OwnerUserId), src.OwnerTeamId, src.ReleaseBranch, KeepPerson(src.OnCallUserId), src.OnCallTeamId, src.ReleaseTypeId, liveEnvIds, src.BetaNumber);   // RT1: the copy keeps the type (even one archived since) and the Beta number; the person edits the number for the next Beta
            if (liveEnvIds.Count < srcEnvIds.Count) notes.Add("A target environment of the original is archived and was not copied.");
            ReleaseModelService.Apply(train, cloneFields);
            db.Set<ReleaseTrains>().Add(train);
            ReleaseModelService.AddEnvironments(db, train.Id, liveEnvIds);
            await db.SaveChangesAsync(ct);

            DeploymentWindows? win = null;
            if (await db.Set<DeploymentWindows>().AsNoTracking().SingleOrDefaultAsync(w => w.ReleaseTrainId == src.Id, ct) is { } sw)
            {
                win = new DeploymentWindows { Id = Ids.New(), ReleaseTrainId = train.Id, StartsAt = sw.StartsAt.AddDays(shiftDays), EndsAt = sw.EndsAt.AddDays(shiftDays) };
                db.Set<DeploymentWindows>().Add(win);
                Audit(db, actor, train.Id, "DeploymentWindow", win.Id, "Create", null, new { win.StartsAt, win.EndsAt, fromTrain = src.Id });
            }

            var productMap = new Dictionary<string, string>();
            foreach (var p in await db.Set<BundledProducts>().AsNoTracking().Where(p => p.ReleaseTrainId == src.Id).OrderBy(p => p.ProductName).ToListAsync(ct))
            {
                var row = new BundledProducts { Id = Ids.New(), ReleaseTrainId = train.Id, ProductName = p.ProductName, VersionTag = p.VersionTag, ProjectCode = p.ProjectCode, CatalogServiceId = p.CatalogServiceId };
                db.Set<BundledProducts>().Add(row); productMap[p.Id] = row.Id;
                Audit(db, actor, train.Id, "BundledProduct", row.Id, "Create", null, new { row.ProductName, row.VersionTag, row.ProjectCode, row.CatalogServiceId, fromTrain = src.Id });
            }

            var gateMap = new Dictionary<string, string>();
            var srcGates = await db.Set<StageGates>().AsNoTracking().Where(g => g.ReleaseTrainId == src.Id).OrderBy(g => g.SequenceOrder).ToListAsync(ct);
            foreach (var sg in srcGates)
            {
                var ownerUser = sg.OwnerTeamId is null ? Owner(sg.OwnerUserId) : null;
                var g = new StageGates
                {
                    Id = Ids.New(), ReleaseTrainId = train.Id, GateName = sg.GateName, GateClass = sg.GateClass, SequenceOrder = sg.SequenceOrder, OffsetDays = sg.OffsetDays,
                    DueOn = BusinessDays.SubtractBusinessDays(newTarget, sg.OffsetDays, holidays), RequiredBeforeStatus = sg.RequiredBeforeStatus,
                    OwnerUserId = ownerUser, OwnerTeamId = sg.OwnerTeamId, LastChangedByUserId = actor.UserId, LastChangedAt = now,
                };
                db.Set<StageGates>().Add(g); gateMap[sg.Id] = g.Id;
                Audit(db, actor, train.Id, "StageGate", g.Id, "Create", null, new { g.GateName, g.GateClass, g.SequenceOrder, g.OffsetDays, dueOn = g.DueOn.ToString("yyyy-MM-dd"), g.RequiredBeforeStatus, g.OwnerUserId, g.OwnerTeamId, fromGate = sg.Id });
            }

            var stepMap = new Dictionary<string, string>();
            var srcSteps = await db.Set<RunbookSteps>().AsNoTracking().Where(s => s.ReleaseTrainId == src.Id).ToListAsync(ct);
            foreach (var ss in srcSteps.OrderBy(s => s.PlannedStartAt).ThenBy(s => s.StepCode, StringComparer.Ordinal))
            {
                // The plan only: StepExecutions (actuals) belong to the source's runs and are never copied (CLAUDE.md rule 7).
                var s = new RunbookSteps
                {
                    Id = Ids.New(), ReleaseTrainId = train.Id, StepCode = ss.StepCode, Section = ss.Section, Title = ss.Title, Instructions = ss.Instructions,
                    BundledProductId = ss.BundledProductId is { } bp && productMap.TryGetValue(bp, out var np) ? np : null,
                    OwnerUserId = ss.OwnerTeamId is null ? Owner(ss.OwnerUserId) : null, OwnerTeamId = ss.OwnerTeamId,
                    PlannedStartAt = ss.PlannedStartAt.AddDays(shiftDays), PlannedDurationMin = ss.PlannedDurationMin,
                };
                db.Set<RunbookSteps>().Add(s); stepMap[ss.Id] = s.Id;
            }
            await db.SaveChangesAsync(ct);   // gates, products and steps before the rows that reference them

            var tasks = 0;
            var srcGateIds = srcGates.Select(g => g.Id).ToList();
            foreach (var st in await db.Set<ChecklistTasks>().AsNoTracking().Where(t => srcGateIds.Contains(t.StageGateId)).OrderBy(t => t.SequenceOrder).ToListAsync(ct))
            {
                // Every task starts open: no completion data is copied.
                var t = new ChecklistTasks
                {
                    Id = Ids.New(), StageGateId = gateMap[st.StageGateId], TaskDescription = st.TaskDescription, SequenceOrder = st.SequenceOrder,
                    BundledProductId = st.BundledProductId is { } bp && productMap.TryGetValue(bp, out var np) ? np : null,
                    OwnerUserId = st.OwnerTeamId is null ? Owner(st.OwnerUserId) : null, OwnerTeamId = st.OwnerTeamId, LastChangedByUserId = actor.UserId, LastChangedAt = now,
                };
                db.Set<ChecklistTasks>().Add(t); tasks++;
                Audit(db, actor, train.Id, "ChecklistTask", t.Id, "Create", null, new { gateId = t.StageGateId, description = t.TaskDescription, t.OwnerUserId, t.OwnerTeamId, fromTask = st.Id });
            }

            var srcStepIds = srcSteps.Select(s => s.Id).ToList();
            var deps = await db.Set<StepDependencies>().AsNoTracking().Where(d => srcStepIds.Contains(d.StepId)).ToListAsync(ct);
            foreach (var d in deps) db.Set<StepDependencies>().Add(new StepDependencies { StepId = stepMap[d.StepId], DependsOnStepId = stepMap[d.DependsOnStepId] });
            var depsOf = deps.ToLookup(d => d.StepId, d => srcSteps.First(s => s.Id == d.DependsOnStepId).StepCode);
            foreach (var ss in srcSteps.OrderBy(s => s.PlannedStartAt).ThenBy(s => s.StepCode, StringComparer.Ordinal))
                Audit(db, actor, train.Id, "RunbookStep", stepMap[ss.Id], "Create", null, new
                {
                    ss.StepCode, ss.Section, ss.Title, plannedStartAt = ss.PlannedStartAt.AddDays(shiftDays), ss.PlannedDurationMin, dependsOn = depsOf[ss.Id].Order(StringComparer.Ordinal).ToArray(), fromStep = ss.Id,
                });

            var comms = 0;
            foreach (var c in await db.Set<CommTemplates>().AsNoTracking().Where(c => c.ReleaseTrainId == src.Id).ToListAsync(ct))
            {
                var copy = new CommTemplates
                {
                    Id = Ids.New(), ReleaseTrainId = train.Id, LibraryTemplateId = c.LibraryTemplateId, StageGateId = c.StageGateId is { } sg && gateMap.TryGetValue(sg, out var ng) ? ng : null,
                    TemplateType = c.TemplateType, Audience = c.Audience, SubjectLine = c.SubjectLine, MarkdownBody = c.MarkdownBody,
                };
                db.Set<CommTemplates>().Add(copy); comms++;
                Audit(db, actor, train.Id, "CommTemplate", copy.Id, "Create", null, new { copy.TemplateType, copy.Audience, copy.SubjectLine, fromTemplate = c.Id });
            }

            if (reassigned > 0) notes.Add($"{Plural(reassigned, "item")} owned by a deactivated person {(reassigned == 1 ? "was" : "were")} assigned to you instead.");
            if (comms > 0) notes.Add("Messages were copied without their T-minus schedule; seed one from a template on the Schedule tab of Communicate.");
            Audit(db, actor, train.Id, "ReleaseTrain", train.Id, "Create", null, new
            {
                train.Title, targetReleaseDate = newTarget.ToString("yyyy-MM-dd"), train.RiskTier, source = "Clone", clonedFromTrainId = src.Id, clonedFrom = src.Title, shiftDays,
                release = ReleaseModelService.Snapshot(train, liveEnvIds),
            });
            await db.SaveChangesAsync(ct);
            return ServiceResult<CreatedTrain>.Ok(View(train, "Clone", new CreatedCounts(productMap.Count, gateMap.Count, tasks, stepMap.Count, deps.Count, comms, 0, win is not null), notes));
        }, ct);

    // ---- helpers
    private static CreatedTrain View(ReleaseTrains t, string source, CreatedCounts counts, List<string> notes) =>
        new(t.Id, t.Title, t.CurrentStatus, t.RiskTier, t.TargetReleaseDate.ToString("yyyy-MM-dd"), source, t.TemplateId, t.ClonedFromTrainId, t.Version, counts, notes, t.ReleaseKey);

    private static string? Blank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    /// <summary>Product name to catalog service id for the products that name a service key; an unknown or archived key is a readable failure.</summary>
    private static async Task<Dictionary<string, string>> ResolveServicesAsync(ReleaseDbContext db, List<NewProductInput> products, List<GuardFailure> f, CancellationToken ct)
    {
        var map = new Dictionary<string, string>();
        foreach (var p in products.Where(p => !string.IsNullOrWhiteSpace(p.ServiceKey)))
        {
            var s = await ServiceCatalogService.FindByKeyAsync(db, p.ServiceKey!.Trim(), ct);
            if (s is null) f.Add(new(ReleaseModelGuards.UnknownService, $"{p.ProductName}: no service with the key \"{p.ServiceKey}\" is in the catalog"));
            else if (s.ArchivedAt is not null) f.Add(new(ReleaseModelGuards.UnknownService, $"{p.ProductName}: the service \"{s.ServiceKey}\" is archived"));
            else map[p.ProductName!] = s.Id;
        }
        return map;
    }

    private static string Plural(int n, string w) => $"{n} {w}{(n == 1 ? "" : "s")}";

    private static async Task<HashSet<DateOnly>> HolidaysAsync(ReleaseDbContext db, CancellationToken ct) =>
        [.. await db.Set<Holidays>().AsNoTracking().Select(h => h.Day).ToListAsync(ct)];

    /// <summary>Titles are unique ignoring case: the CSV import finds a train by its title (Q-080a), so two trains of the same name could not be told apart there.</summary>
    private static async Task CheckTitleAsync(ReleaseDbContext db, string title, List<GuardFailure> f, CancellationToken ct)
    {
        if (title.Length == 0) f.Add(Bad("A train needs a title"));
        else if (title.Length > MaxTitle) f.Add(Bad($"The title is limited to {MaxTitle} characters"));
        else if (await db.Set<ReleaseTrains>().AnyAsync(t => t.Title.ToLower() == title.ToLower(), ct))
            f.Add(new(TrainCreationGuards.TrainTitleTaken, $"A train named \"{title}\" already exists"));
    }

    /// <summary>YYYY-MM-DD, not before today in the display zone (D24): a new plan cannot be due in the past.</summary>
    private DateOnly? ParseTarget(string? raw, List<GuardFailure> f)
    {
        if (string.IsNullOrWhiteSpace(raw)) { f.Add(Bad("A train needs a target release date")); return null; }
        if (!DateOnly.TryParseExact(raw.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) { f.Add(Bad($"Target release date \"{raw}\" is not a date (YYYY-MM-DD)")); return null; }
        var today = clock.LocalDate(Now);
        if (d < today) { f.Add(Bad($"The target release date {d:yyyy-MM-dd} is in the past; choose {today:yyyy-MM-dd} or later")); return null; }
        return d;
    }

    private static (DateTime Start, DateTime End)? ParseWindow(string? start, string? end, List<GuardFailure> f)
    {
        var hasStart = !string.IsNullOrWhiteSpace(start); var hasEnd = !string.IsNullOrWhiteSpace(end);
        if (!hasStart && !hasEnd) return null;
        if (hasStart != hasEnd) { f.Add(Bad("The deployment window needs both a start and an end, or neither")); return null; }
        static DateTime? Ts(string s) => DateTime.TryParse(s.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
            ? new DateTime(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc) : null;
        var s = Ts(start!); var e = Ts(end!);
        if (s is null) f.Add(Bad($"Window start \"{start}\" is not a date and time"));
        if (e is null) f.Add(Bad($"Window end \"{end}\" is not a date and time"));
        if (s is null || e is null) return null;
        if (e <= s) { f.Add(Bad("The deployment window must end after it starts")); return null; }
        return (s.Value, e.Value);
    }

    private static List<NewProductInput> CheckProducts(IReadOnlyList<NewProductInput> input, List<GuardFailure> f)
    {
        var result = new List<NewProductInput>();
        if (input.Count > MaxProducts) { f.Add(Bad($"A train bundles at most {MaxProducts} products")); return result; }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var n = 0; n < input.Count; n++)
        {
            var at = $"Product {n + 1}";
            var name = (input[n].ProductName ?? "").Trim(); var tag = (input[n].VersionTag ?? "").Trim(); var code = (input[n].ProjectCode ?? "").Trim();
            var ok = true;
            void Need(string v, string what, int max) { if (v.Length == 0) { f.Add(Bad($"{at} needs a {what}")); ok = false; } else if (v.Length > max) { f.Add(Bad($"{at}: the {what} is limited to {max} characters")); ok = false; } }
            Need(name, "name", MaxProductName); Need(tag, "version tag", MaxVersionTag); Need(code, "project code", MaxProjectCode);
            if (name.Length > 0 && !names.Add(name)) { f.Add(Bad($"{at}: {name} is listed twice")); ok = false; }
            if (ok) result.Add(new NewProductInput(name, tag, code, input[n].ServiceKey));
        }
        return result;
    }
}
