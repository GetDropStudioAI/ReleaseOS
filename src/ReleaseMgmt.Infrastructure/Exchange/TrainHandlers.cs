using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Exchange;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Exchange;

/// <summary>Shared by every kind whose rows belong to a train: the Train column, the plan lock (PROJECT_SCOPE 9) and the "owner is inactive" rule.</summary>
internal abstract class TrainScopedHandler : KindHandler
{
    protected static bool Locked(string status) => status is "Executing" or "Complete" or "Aborted";

    /// <summary>Plan imports are refused once the train is Executing or Complete (Aborted is closed too, as in the runbook rules). Unchanged rows are fine: an export of a running train must still re-import.</summary>
    protected static void PlanLock(PlanEnv env, ParsedRow row, Resolved r, Planned p)
    {
        if (p.Op is RowOp.New or RowOp.Updated && r.Train is { } t && Locked(t.CurrentStatus))
            env.Err(row.Row, "Train", $"{t.Title} is {t.CurrentStatus}; plan imports are refused once a train is Executing or Complete");
    }

    protected static void OwnerActive(PlanEnv env, ParsedRow row, Planned p, Users? user)
    {
        if (user is { IsActive: false } && (p.Op == RowOp.New || p.Changed.Contains("Owner")))
            env.Err(row.Row, "Owner", $"{user.Email} is inactive and cannot be given new work");
    }

    protected static ReleaseTrains? TrainOf(PlanEnv env, ParsedRow row, Resolved r)
    {
        var t = env.ResolveTrain(row.Row, row.Cells["Train"]);
        if (t is null) return null;
        r.Train = t; r.TrainId = t.Id; r.After["Train"] = t.Title;
        return t;
    }

    protected static string TrainRef(ReleaseTrains t) => $"{t.Id}@{t.Version}";
}

internal sealed record TrainPayload(string? TemplateId, string? OwnerUserId = null, string? OwnerTeamId = null, string? OnCallUserId = null, string? OnCallTeamId = null, string? ReleaseTypeId = null, List<string>? EnvironmentIds = null, int? BetaNumber = null);

internal sealed class TrainsHandler : KindHandler
{
    public override ImportKindSpec Spec { get; } = ImportKinds.Get(ImportKinds.Trains)!;

    public override async Task<List<CurrentRow>> LoadAsync(ReleaseDbContext db, GridFilter f, CancellationToken ct)
    {
        var templates = (await db.Set<TrainTemplates>().AsNoTracking().ToListAsync(ct)).ToDictionary(t => t.Id, t => t.Name);
        var users = (await db.Set<Users>().AsNoTracking().ToListAsync(ct)).ToDictionary(u => u.Id);
        var teams = (await db.Set<Teams>().AsNoTracking().ToListAsync(ct)).ToDictionary(t => t.Id);
        var types = (await db.Set<ReleaseTypes>().AsNoTracking().ToListAsync(ct)).ToDictionary(t => t.Id, t => t.Name);
        var envNames = (await (from te in db.Set<TrainEnvironments>().AsNoTracking() join e in db.Set<Environments>().AsNoTracking() on te.EnvironmentId equals e.Id
                               orderby e.SortOrder, e.Name select new { te.ReleaseTrainId, e.Name }).ToListAsync(ct)).ToLookup(x => x.ReleaseTrainId, x => x.Name);
        var windows = (await db.Set<DeploymentWindows>().AsNoTracking().ToListAsync(ct)).ToDictionary(w => w.ReleaseTrainId);
        var q = db.Set<ReleaseTrains>().AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(f.Train)) q = q.Where(t => t.Id == f.Train);
        if (!string.IsNullOrEmpty(f.Status)) q = q.Where(t => t.CurrentStatus == f.Status);
        return [.. (await q.ToListAsync(ct)).OrderBy(t => t.TargetReleaseDate).ThenBy(t => t.Title, StringComparer.Ordinal)
            .Select(t => new CurrentRow(t.Id, t.Version, t.Id, K(t.Title),
                new Dictionary<string, string>
                {
                    ["Title"] = Norm(t.Title), ["TargetReleaseDate"] = Day(t.TargetReleaseDate), ["RiskTier"] = t.RiskTier,
                    ["Template"] = t.TemplateId is not null && templates.TryGetValue(t.TemplateId, out var tn) ? tn : "",
                    ["ChangeTicketNumber"] = Norm(t.ChangeTicketNumber),
                    ["WindowStart"] = windows.TryGetValue(t.Id, out var w) ? Ts(w.StartsAt) : "", ["WindowEnd"] = windows.TryGetValue(t.Id, out var w2) ? Ts(w2.EndsAt) : "",
                    ["Owner"] = t.OwnerUserId is not null && users.TryGetValue(t.OwnerUserId, out var ou) ? ou.Email : "",
                    ["Team"] = t.OwnerTeamId is not null && teams.TryGetValue(t.OwnerTeamId, out var ot) ? ot.Handle : "",
                    ["Branch"] = Norm(t.ReleaseBranch), ["OnCall"] = OwnerCell(t.OnCallUserId, t.OnCallTeamId, users, teams),
                    ["ReleaseType"] = t.ReleaseTypeId is not null && types.TryGetValue(t.ReleaseTypeId, out var tn2) ? tn2 : "",
                    ["BetaNumber"] = t.BetaNumber?.ToString() ?? "",
                    ["Environments"] = string.Join(", ", envNames[t.Id]),
                },
                [new("#Id", t.Id), new("#Key", t.ReleaseKey ?? ""), new("#Status", t.CurrentStatus), new("#Version", t.Version.ToString())]))];
    }

    protected override async Task PrepareAsync(PlanEnv env, CancellationToken ct)
    {
        env.Bag["templates"] = await env.Db.Set<TrainTemplates>().AsNoTracking().ToListAsync(ct);
        env.Bag["types"] = await env.Db.Set<ReleaseTypes>().AsNoTracking().ToListAsync(ct);
        env.Bag["environments"] = await env.Db.Set<Environments>().AsNoTracking().ToListAsync(ct);
        await env.LoadPeopleAsync(ct);
    }

    protected override Resolved? Resolve(PlanEnv env, ParsedRow row)
    {
        var r = new Resolved { Key = K(row.Cells["Title"]) };
        foreach (var (k, v) in row.Cells) r.After[k] = v;
        var ok = true;
        string? templateId = null;
        if (row.Cells.TryGetValue("Template", out var tpl) && tpl.Length > 0)
        {
            var templates = (List<TrainTemplates>)env.Bag["templates"];
            var t = templates.FirstOrDefault(x => string.Equals(x.Name, tpl, StringComparison.OrdinalIgnoreCase));
            if (t is null) { env.Err(row.Row, "Template", $"No template is named {PlanEnv.Quote(tpl)}.{PlanEnv.Did(env.Suggest(tpl, templates.Select(x => x.Name)))}"); ok = false; }
            else { r.After["Template"] = t.Name; templateId = t.Id; }
        }
        var start = row.Cells.GetValueOrDefault("WindowStart", ""); var end = row.Cells.GetValueOrDefault("WindowEnd", "");
        if (row.Cells.ContainsKey("WindowStart") != row.Cells.ContainsKey("WindowEnd") && (start.Length > 0 || end.Length > 0))
        { env.Err(row.Row, row.Cells.ContainsKey("WindowStart") ? "WindowEnd" : "WindowStart", "WindowStart and WindowEnd go together: add the missing column"); ok = false; }
        else if ((start.Length == 0) != (end.Length == 0)) { env.Err(row.Row, start.Length == 0 ? "WindowStart" : "WindowEnd", "WindowStart and WindowEnd go together: give both or neither"); ok = false; }
        else if (start.Length > 0 && string.CompareOrdinal(end, start) <= 0) { env.Err(row.Row, "WindowEnd", "The window must end after it starts"); ok = false; }
        // REOS-107: owner, team, branch, on-call, type and target environments. A column that is present and empty clears the field; an absent one leaves it alone.
        string? ownerUser = null, ownerTeam = null, onCallUser = null, onCallTeam = null, typeId = null; List<string>? envIds = null;
        if (row.Cells.TryGetValue("Owner", out var owner) && owner.Length > 0)
        {
            if (!env.ResolveOwner(row.Row, "Owner", owner, out ownerUser, out var asTeam, out var canon, out var ownerRow)) ok = false;
            else if (asTeam is not null) { env.Err(row.Row, "Owner", "The owner is a person (an email); give the owning team in the Team column"); ok = false; }
            else { r.After["Owner"] = canon; if (ownerRow is { IsActive: false }) { env.Err(row.Row, "Owner", $"{ownerRow.Email} is inactive and cannot be given new work"); ok = false; } }
        }
        if (row.Cells.TryGetValue("Team", out var team) && team.Length > 0)
        {
            var h = team.TrimStart('@');
            var hit = env.Teams.FirstOrDefault(x => string.Equals(x.Handle, h, StringComparison.OrdinalIgnoreCase));
            if (hit is null) { env.Err(row.Row, "Team", $"No team has the handle {PlanEnv.Quote(team)}.{PlanEnv.Did(env.Suggest(h, env.Teams.Select(x => x.Handle)))}"); ok = false; }
            else { ownerTeam = hit.Id; r.After["Team"] = hit.Handle; }
        }
        if (row.Cells.TryGetValue("OnCall", out var onCall) && onCall.Length > 0)
        {
            if (!env.ResolveOwner(row.Row, "OnCall", onCall, out onCallUser, out onCallTeam, out var canon, out var onRow)) ok = false;
            else { r.After["OnCall"] = canon; if (onRow is { IsActive: false }) { env.Err(row.Row, "OnCall", $"{onRow.Email} is inactive and cannot be on call"); ok = false; } }
        }
        if (row.Cells.TryGetValue("ReleaseType", out var typeName) && typeName.Length > 0)
        {
            var types = (List<ReleaseTypes>)env.Bag["types"];
            var tp = types.FirstOrDefault(x => string.Equals(x.Name, typeName, StringComparison.OrdinalIgnoreCase));
            if (tp is null) { env.Err(row.Row, "ReleaseType", $"No release type is named {PlanEnv.Quote(typeName)}.{PlanEnv.Did(env.Suggest(typeName, types.Select(x => x.Name)))}"); ok = false; }
            else { typeId = tp.Id; r.After["ReleaseType"] = tp.Name; }
        }
        int? betaNumber = row.Cells.TryGetValue("BetaNumber", out var bn) && int.TryParse(bn, out var bnv) ? bnv : null;
        if (row.Cells.TryGetValue("Environments", out var envCell) && envCell.Length > 0)
        {
            var all = (List<Environments>)env.Bag["environments"];
            var picked = new List<Environments>();
            foreach (var name in envCell.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var e = all.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                if (e is null) { env.Err(row.Row, "Environments", $"No environment is named {PlanEnv.Quote(name)}.{PlanEnv.Did(env.Suggest(name, all.Select(x => x.Name)))}"); ok = false; }
                else if (!picked.Contains(e)) picked.Add(e);
            }
            if (ok) { picked = [.. picked.OrderBy(x => x.SortOrder).ThenBy(x => x.Name, StringComparer.Ordinal)]; envIds = [.. picked.Select(x => x.Id)]; r.After["Environments"] = string.Join(", ", picked.Select(x => x.Name)); }
        }
        if (!ok) return null;
        r.Payload = new TrainPayload(templateId, ownerUser, ownerTeam, onCallUser, onCallTeam, typeId, envIds, betaNumber);
        r.Refs = templateId ?? "";
        return r;
    }

    protected override void Rules(PlanEnv env, ParsedRow row, Resolved r, Planned p)
    {
        // REOS-116 (D46, D48): a release has a type once the workspace has types, and a Beta type's release has its number (1 to 3); no other release has one.
        var types = (List<ReleaseTypes>)env.Bag["types"];
        if (types.Count > 0)
        {
            var typeCell = row.Cells.TryGetValue("ReleaseType", out var tc) ? tc : null;
            string? effective = typeCell ?? (p.Op == RowOp.Updated ? p.Existing!.Cells.GetValueOrDefault("ReleaseType", "") : "");
            if (p.Op == RowOp.New && string.IsNullOrEmpty(typeCell)) env.Err(row.Row, "ReleaseType", $"A release needs a release type: {string.Join(", ", types.Where(x => x.ArchivedAt == null).Select(x => x.Name))}");
            else if (p.Op == RowOp.Updated && typeCell is { Length: 0 }) env.Err(row.Row, "ReleaseType", "A release cannot lose its release type");
            else if (types.FirstOrDefault(x => string.Equals(x.Name, effective, StringComparison.OrdinalIgnoreCase)) is { } tp)
            {
                var numberCell = row.Cells.TryGetValue("BetaNumber", out var nc) ? nc : p.Op == RowOp.Updated ? p.Existing!.Cells.GetValueOrDefault("BetaNumber", "") : "";
                var typeChangedOrNumberGiven = p.Op == RowOp.New || p.Changed.Contains("ReleaseType") || p.Changed.Contains("BetaNumber");
                if (typeChangedOrNumberGiven && tp.IsBeta && numberCell.Length == 0) env.Err(row.Row, "BetaNumber", $"A {tp.Name} release needs its number, 1 to 3");
                else if (typeChangedOrNumberGiven && !tp.IsBeta && numberCell.Length > 0) env.Err(row.Row, "BetaNumber", $"Only a Beta release carries a number; \"{tp.Name}\" does not");
            }
        }
        if (p.Op == RowOp.Updated && p.Existing!.ReadOnly.FirstOrDefault(x => x.Key == "#Status").Value is { } s && s is "Executing" or "Complete" or "Aborted")
            env.Err(row.Row, p.Changed[0], $"{r.After["Title"]} is {s}; plan imports are refused once a train is Executing or Complete");
    }

    public override async Task ApplyAsync(ApplyEnv env, IReadOnlyList<Planned> rows, CancellationToken ct)
    {
        var byId = new Dictionary<string, ReleaseTrains>();
        var ids = rows.Where(r => r.Existing is not null).Select(r => r.Existing!.Id).ToList();
        foreach (var chunk in Chunks(ids))
            foreach (var t in await env.Db.Set<ReleaseTrains>().Where(t => chunk.Contains(t.Id)).ToListAsync(ct)) byId[t.Id] = t;
        var windows = new Dictionary<string, DeploymentWindows>();
        foreach (var chunk in Chunks(ids))
            foreach (var w in await env.Db.Set<DeploymentWindows>().Where(w => chunk.Contains(w.ReleaseTrainId)).ToListAsync(ct)) windows[w.ReleaseTrainId] = w;

        foreach (var p in rows.Where(r => r.Op is RowOp.New or RowOp.Updated))
        {
            var pay = (TrainPayload)p.Payload!;
            var templateId = pay.TemplateId;
            var start = p.After.GetValueOrDefault("WindowStart", ""); var end = p.After.GetValueOrDefault("WindowEnd", "");
            if (p.Op == RowOp.New)
            {
                var t = new ReleaseTrains
                {
                    Title = p.After["Title"], TemplateId = templateId, TargetReleaseDate = ParseDay(p.After["TargetReleaseDate"]), RiskTier = p.After["RiskTier"],
                    ChangeTicketNumber = p.After.GetValueOrDefault("ChangeTicketNumber") is { Length: > 0 } c ? c : null,
                    CreatedAt = env.Now, UpdatedAt = env.Now, LastChangedByUserId = env.Actor.UserId, LastChangedAt = env.Now,
                    ReleaseKey = await Services.ReleaseKeyAllocator.AllocateAsync(env.Db, ct),
                    OwnerUserId = pay.OwnerUserId, OwnerTeamId = pay.OwnerTeamId, OnCallUserId = pay.OnCallUserId, OnCallTeamId = pay.OnCallTeamId, ReleaseTypeId = pay.ReleaseTypeId, BetaNumber = pay.BetaNumber,
                    ReleaseBranch = p.After.GetValueOrDefault("Branch") is { Length: > 0 } br ? br : null,
                };
                env.Db.Set<ReleaseTrains>().Add(t);
                Services.ReleaseModelService.AddEnvironments(env.Db, t.Id, pay.EnvironmentIds);
                if (start.Length > 0) env.Db.Set<DeploymentWindows>().Add(new DeploymentWindows { ReleaseTrainId = t.Id, StartsAt = ParseTs(start), EndsAt = ParseTs(end) });
                env.AuditRow(p, t.Id, "ReleaseTrain", t.Id);
            }
            else
            {
                var t = byId[p.Existing!.Id];
                if (p.Changed.Contains("TargetReleaseDate"))
                {
                    t.TargetReleaseDate = ParseDay(p.After["TargetReleaseDate"]);
                    // as ScheduleService: a new target date moves every gate's DueOn (business days, D9)
                    foreach (var g in await env.Db.Set<StageGates>().Where(g => g.ReleaseTrainId == t.Id).ToListAsync(ct))
                    { g.DueOn = BusinessDays.SubtractBusinessDays(t.TargetReleaseDate, g.OffsetDays, env.Holidays); g.Version++; }
                }
                if (p.Changed.Contains("RiskTier")) t.RiskTier = p.After["RiskTier"];
                if (p.Changed.Contains("Template")) t.TemplateId = templateId;
                if (p.Changed.Contains("ChangeTicketNumber")) t.ChangeTicketNumber = p.After["ChangeTicketNumber"].Length == 0 ? null : p.After["ChangeTicketNumber"];
                if (p.Changed.Contains("Owner")) t.OwnerUserId = pay.OwnerUserId;
                if (p.Changed.Contains("Team")) t.OwnerTeamId = pay.OwnerTeamId;
                if (p.Changed.Contains("Branch")) t.ReleaseBranch = p.After["Branch"].Length == 0 ? null : p.After["Branch"];
                if (p.Changed.Contains("OnCall")) { t.OnCallUserId = pay.OnCallUserId; t.OnCallTeamId = pay.OnCallTeamId; }
                if (p.Changed.Contains("ReleaseType")) t.ReleaseTypeId = pay.ReleaseTypeId;
                if (p.Changed.Contains("ReleaseType") || p.Changed.Contains("BetaNumber")) t.BetaNumber = pay.BetaNumber;
                if (p.Changed.Contains("Environments"))
                {
                    env.Db.Set<TrainEnvironments>().RemoveRange(await env.Db.Set<TrainEnvironments>().Where(x => x.ReleaseTrainId == t.Id).ToListAsync(ct));
                    Services.ReleaseModelService.AddEnvironments(env.Db, t.Id, pay.EnvironmentIds);
                }
                if (p.Changed.Contains("WindowStart") || p.Changed.Contains("WindowEnd"))
                {
                    windows.TryGetValue(t.Id, out var w);
                    if (start.Length == 0) { if (w is not null) env.Db.Set<DeploymentWindows>().Remove(w); }
                    else if (w is null) env.Db.Set<DeploymentWindows>().Add(new DeploymentWindows { ReleaseTrainId = t.Id, StartsAt = ParseTs(start), EndsAt = ParseTs(end) });
                    else { w.StartsAt = ParseTs(start); w.EndsAt = ParseTs(end); w.Version++; }
                }
                t.LastChangedByUserId = env.Actor.UserId; t.LastChangedAt = env.Now; t.UpdatedAt = env.Now; t.Version++;
                env.AuditRow(p, t.Id, "ReleaseTrain", t.Id);
            }
        }
    }
}

internal sealed class ProductsHandler : TrainScopedHandler
{
    public override ImportKindSpec Spec { get; } = ImportKinds.Get(ImportKinds.Products)!;

    public override async Task<List<CurrentRow>> LoadAsync(ReleaseDbContext db, GridFilter f, CancellationToken ct)
    {
        var (_, _, trains) = await RefsAsync(db, ct);
        var serviceKeys = (await db.Set<CatalogServices>().AsNoTracking().ToListAsync(ct)).ToDictionary(x => x.Id, x => x.ServiceKey);
        var q = db.Set<BundledProducts>().AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(f.Train)) q = q.Where(p => p.ReleaseTrainId == f.Train);
        return [.. (await q.ToListAsync(ct)).OrderBy(p => TrainCell(p.ReleaseTrainId, trains), StringComparer.Ordinal).ThenBy(p => p.ProductName, StringComparer.Ordinal)
            .Select(p => new CurrentRow(p.Id, p.Version, p.ReleaseTrainId, K(TrainCell(p.ReleaseTrainId, trains), Norm(p.ProductName)),
                new Dictionary<string, string> { ["Train"] = TrainCell(p.ReleaseTrainId, trains), ["ProductName"] = Norm(p.ProductName), ["VersionTag"] = Norm(p.VersionTag), ["ProjectCode"] = Norm(p.ProjectCode),
                    ["ServiceKey"] = p.CatalogServiceId is not null && serviceKeys.TryGetValue(p.CatalogServiceId, out var sk) ? sk : "" },
                [new("#Id", p.Id), new("#Version", p.Version.ToString())]))];
    }

    protected override async Task PrepareAsync(PlanEnv env, CancellationToken ct) =>
        env.Bag["services"] = await env.Db.Set<CatalogServices>().AsNoTracking().ToListAsync(ct);

    protected override Resolved? Resolve(PlanEnv env, ParsedRow row)
    {
        var r = new Resolved();
        foreach (var (k, v) in row.Cells) r.After[k] = v;
        var t = TrainOf(env, row, r);
        if (t is null) return null;
        r.Key = K(t.Title, row.Cells["ProductName"]); r.Refs = TrainRef(t);
        // REOS-109: a service key from the workspace's catalog links the product to it; present and empty unlinks, absent leaves the link alone
        if (row.Cells.TryGetValue("ServiceKey", out var key) && key.Length > 0)
        {
            var services = (List<CatalogServices>)env.Bag["services"];
            var s = services.FirstOrDefault(x => string.Equals(x.ServiceKey, key, StringComparison.OrdinalIgnoreCase));
            if (s is null) { env.Err(row.Row, "ServiceKey", $"No service in the catalog has the key {PlanEnv.Quote(key)}.{PlanEnv.Did(env.Suggest(key, services.Select(x => x.ServiceKey)))}"); return null; }
            r.After["ServiceKey"] = s.ServiceKey; r.Payload = s.Id;
        }
        return r;
    }

    protected override void Rules(PlanEnv env, ParsedRow row, Resolved r, Planned p) => PlanLock(env, row, r, p);

    public override async Task ApplyAsync(ApplyEnv env, IReadOnlyList<Planned> rows, CancellationToken ct)
    {
        var byId = new Dictionary<string, BundledProducts>();
        foreach (var chunk in Chunks(rows.Where(r => r.Existing is not null).Select(r => r.Existing!.Id)))
            foreach (var x in await env.Db.Set<BundledProducts>().Where(x => chunk.Contains(x.Id)).ToListAsync(ct)) byId[x.Id] = x;
        foreach (var p in rows.Where(r => r.Op is RowOp.New or RowOp.Updated))
        {
            string id;
            if (p.Op == RowOp.New)
            {
                var x = new BundledProducts { ReleaseTrainId = p.TrainId!, ProductName = p.After["ProductName"], VersionTag = p.After["VersionTag"], ProjectCode = p.After["ProjectCode"], CatalogServiceId = p.Payload as string };
                env.Db.Set<BundledProducts>().Add(x); id = x.Id;
            }
            else
            {
                var x = byId[p.Existing!.Id]; id = x.Id;
                if (p.Changed.Contains("VersionTag")) x.VersionTag = p.After["VersionTag"];
                if (p.Changed.Contains("ProjectCode")) x.ProjectCode = p.After["ProjectCode"];
                if (p.Changed.Contains("ServiceKey")) x.CatalogServiceId = p.Payload as string;
                x.Version++;
            }
            env.Bump(p.TrainId);
            env.AuditRow(p, p.TrainId, "BundledProduct", id);
        }
    }
}

internal sealed record OwnerPayload(string? UserId, string? TeamId, DateOnly Target, Users? User = null);

internal sealed class GatesHandler : TrainScopedHandler
{
    public override ImportKindSpec Spec { get; } = ImportKinds.Get(ImportKinds.Gates)!;

    public override async Task<List<CurrentRow>> LoadAsync(ReleaseDbContext db, GridFilter f, CancellationToken ct)
    {
        var (users, teams, trains) = await RefsAsync(db, ct);
        var q = db.Set<StageGates>().AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(f.Train)) q = q.Where(g => g.ReleaseTrainId == f.Train);
        return [.. (await q.ToListAsync(ct)).OrderBy(g => TrainCell(g.ReleaseTrainId, trains), StringComparer.Ordinal).ThenBy(g => g.SequenceOrder)
            .Select(g => new CurrentRow(g.Id, g.Version, g.ReleaseTrainId, K(TrainCell(g.ReleaseTrainId, trains), g.SequenceOrder.ToString()),
                new Dictionary<string, string>
                {
                    ["Train"] = TrainCell(g.ReleaseTrainId, trains), ["GateName"] = Norm(g.GateName), ["SequenceOrder"] = g.SequenceOrder.ToString(), ["OffsetDays"] = g.OffsetDays.ToString(),
                    ["RequiredBefore"] = g.RequiredBeforeStatus, ["Owner"] = OwnerCell(g.OwnerUserId, g.OwnerTeamId, users, teams), ["GateClass"] = g.GateClass,
                },
                [new("#Id", g.Id), new("#DueOn", Day(g.DueOn)), new("#Status", g.Status), new("#Version", g.Version.ToString())]))];
    }

    protected override Task PrepareAsync(PlanEnv env, CancellationToken ct) => env.LoadPeopleAsync(ct);

    protected override Resolved? Resolve(PlanEnv env, ParsedRow row)
    {
        var r = new Resolved();
        foreach (var (k, v) in row.Cells) r.After[k] = v;
        var t = TrainOf(env, row, r);
        if (t is null) return null;
        if (!env.ResolveOwner(row.Row, "Owner", row.Cells["Owner"], out var uid, out var tid, out var canonical, out var user)) return null;
        r.After["Owner"] = canonical;
        r.Key = K(t.Title, row.Cells["SequenceOrder"]);
        r.Refs = $"{TrainRef(t)}|{uid ?? tid}";
        r.Payload = new OwnerPayload(uid, tid, t.TargetReleaseDate, user);
        return r;
    }

    protected override void Rules(PlanEnv env, ParsedRow row, Resolved r, Planned p)
    {
        PlanLock(env, row, r, p);
        OwnerActive(env, row, p, ((OwnerPayload)r.Payload!).User);
        if (p.Op == RowOp.Updated && p.Existing!.ReadOnly.FirstOrDefault(x => x.Key == "#Status").Value is "Certified" or "Waived" && p.Changed.Count > 0)
            env.Err(row.Row, p.Changed[0], $"Gate {p.Existing.Cells["GateName"]} is {p.Existing.ReadOnly.First(x => x.Key == "#Status").Value}; a certified or waived gate cannot be redefined by file");
    }

    public override async Task ApplyAsync(ApplyEnv env, IReadOnlyList<Planned> rows, CancellationToken ct)
    {
        var byId = new Dictionary<string, StageGates>();
        foreach (var chunk in Chunks(rows.Where(r => r.Existing is not null).Select(r => r.Existing!.Id)))
            foreach (var x in await env.Db.Set<StageGates>().Where(x => chunk.Contains(x.Id)).ToListAsync(ct)) byId[x.Id] = x;
        var holidays = env.Holidays;
        foreach (var p in rows.Where(r => r.Op is RowOp.New or RowOp.Updated))
        {
            var o = (OwnerPayload)p.Payload!;
            string id;
            if (p.Op == RowOp.New)
            {
                var offset = ParseInt(p.After["OffsetDays"]);
                var g = new StageGates
                {
                    ReleaseTrainId = p.TrainId!, GateName = p.After["GateName"], GateClass = p.After.GetValueOrDefault("GateClass") is { Length: > 0 } gc ? gc : "Standard",
                    SequenceOrder = ParseInt(p.After["SequenceOrder"]), OffsetDays = offset, DueOn = BusinessDays.SubtractBusinessDays(o.Target, offset, holidays),
                    RequiredBeforeStatus = p.After["RequiredBefore"], OwnerUserId = o.UserId, OwnerTeamId = o.TeamId, Status = "Pending",
                    LastChangedByUserId = env.Actor.UserId, LastChangedAt = env.Now,
                };
                env.Db.Set<StageGates>().Add(g); id = g.Id;
            }
            else
            {
                var g = byId[p.Existing!.Id]; id = g.Id;
                if (p.Changed.Contains("GateName")) g.GateName = p.After["GateName"];
                if (p.Changed.Contains("GateClass")) g.GateClass = p.After["GateClass"].Length == 0 ? "Standard" : p.After["GateClass"];
                if (p.Changed.Contains("RequiredBefore")) g.RequiredBeforeStatus = p.After["RequiredBefore"];
                if (p.Changed.Contains("OffsetDays")) { g.OffsetDays = ParseInt(p.After["OffsetDays"]); g.DueOn = BusinessDays.SubtractBusinessDays(o.Target, g.OffsetDays, holidays); }
                if (p.Changed.Contains("Owner")) { g.OwnerUserId = o.UserId; g.OwnerTeamId = o.TeamId; }
                g.LastChangedByUserId = env.Actor.UserId; g.LastChangedAt = env.Now; g.Version++;
            }
            env.Bump(p.TrainId);
            env.AuditRow(p, p.TrainId, "StageGate", id);
        }
    }
}
