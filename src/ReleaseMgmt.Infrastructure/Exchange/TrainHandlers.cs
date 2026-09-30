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

internal sealed record TrainPayload(string? TemplateId);

internal sealed class TrainsHandler : KindHandler
{
    public override ImportKindSpec Spec { get; } = ImportKinds.Get(ImportKinds.Trains)!;

    public override async Task<List<CurrentRow>> LoadAsync(ReleaseDbContext db, GridFilter f, CancellationToken ct)
    {
        var templates = (await db.Set<TrainTemplates>().AsNoTracking().ToListAsync(ct)).ToDictionary(t => t.Id, t => t.Name);
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
                },
                [new("#Id", t.Id), new("#Status", t.CurrentStatus), new("#Version", t.Version.ToString())]))];
    }

    protected override async Task PrepareAsync(PlanEnv env, CancellationToken ct) =>
        env.Bag["templates"] = await env.Db.Set<TrainTemplates>().AsNoTracking().ToListAsync(ct);

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
            if (t is null) { env.Err(row.Row, "Template", $"No template is named {PlanEnv.Quote(tpl)}.{PlanEnv.Did(RowParser.Suggest(tpl, templates.Select(x => x.Name)))}"); ok = false; }
            else { r.After["Template"] = t.Name; templateId = t.Id; }
        }
        var start = row.Cells.GetValueOrDefault("WindowStart", ""); var end = row.Cells.GetValueOrDefault("WindowEnd", "");
        if (row.Cells.ContainsKey("WindowStart") != row.Cells.ContainsKey("WindowEnd") && (start.Length > 0 || end.Length > 0))
        { env.Err(row.Row, row.Cells.ContainsKey("WindowStart") ? "WindowEnd" : "WindowStart", "WindowStart and WindowEnd go together: add the missing column"); ok = false; }
        else if ((start.Length == 0) != (end.Length == 0)) { env.Err(row.Row, start.Length == 0 ? "WindowStart" : "WindowEnd", "WindowStart and WindowEnd go together: give both or neither"); ok = false; }
        else if (start.Length > 0 && string.CompareOrdinal(end, start) <= 0) { env.Err(row.Row, "WindowEnd", "The window must end after it starts"); ok = false; }
        if (!ok) return null;
        r.Payload = new TrainPayload(templateId);
        r.Refs = templateId ?? "";
        return r;
    }

    protected override void Rules(PlanEnv env, ParsedRow row, Resolved r, Planned p)
    {
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
            var templateId = ((TrainPayload)p.Payload!).TemplateId;
            var start = p.After.GetValueOrDefault("WindowStart", ""); var end = p.After.GetValueOrDefault("WindowEnd", "");
            if (p.Op == RowOp.New)
            {
                var t = new ReleaseTrains
                {
                    Title = p.After["Title"], TemplateId = templateId, TargetReleaseDate = ParseDay(p.After["TargetReleaseDate"]), RiskTier = p.After["RiskTier"],
                    ChangeTicketNumber = p.After.GetValueOrDefault("ChangeTicketNumber") is { Length: > 0 } c ? c : null,
                    CreatedAt = env.Now, UpdatedAt = env.Now, LastChangedByUserId = env.Actor.UserId, LastChangedAt = env.Now,
                };
                env.Db.Set<ReleaseTrains>().Add(t);
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
        var q = db.Set<BundledProducts>().AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(f.Train)) q = q.Where(p => p.ReleaseTrainId == f.Train);
        return [.. (await q.ToListAsync(ct)).OrderBy(p => TrainCell(p.ReleaseTrainId, trains), StringComparer.Ordinal).ThenBy(p => p.ProductName, StringComparer.Ordinal)
            .Select(p => new CurrentRow(p.Id, p.Version, p.ReleaseTrainId, K(TrainCell(p.ReleaseTrainId, trains), Norm(p.ProductName)),
                new Dictionary<string, string> { ["Train"] = TrainCell(p.ReleaseTrainId, trains), ["ProductName"] = Norm(p.ProductName), ["VersionTag"] = Norm(p.VersionTag), ["ProjectCode"] = Norm(p.ProjectCode) },
                [new("#Id", p.Id), new("#Version", p.Version.ToString())]))];
    }

    protected override Resolved? Resolve(PlanEnv env, ParsedRow row)
    {
        var r = new Resolved();
        foreach (var (k, v) in row.Cells) r.After[k] = v;
        var t = TrainOf(env, row, r);
        if (t is null) return null;
        r.Key = K(t.Title, row.Cells["ProductName"]); r.Refs = TrainRef(t);
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
                var x = new BundledProducts { ReleaseTrainId = p.TrainId!, ProductName = p.After["ProductName"], VersionTag = p.After["VersionTag"], ProjectCode = p.After["ProjectCode"] };
                env.Db.Set<BundledProducts>().Add(x); id = x.Id;
            }
            else
            {
                var x = byId[p.Existing!.Id]; id = x.Id;
                if (p.Changed.Contains("VersionTag")) x.VersionTag = p.After["VersionTag"];
                if (p.Changed.Contains("ProjectCode")) x.ProjectCode = p.After["ProjectCode"];
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
