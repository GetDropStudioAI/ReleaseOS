using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Exchange;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Domain.Sync;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Exchange;

internal sealed record TaskPayload(string GateId, string GateStatus, string GateName, string? UserId, string? TeamId, string? ProductId, Users? User);

internal sealed class TasksHandler : TrainScopedHandler
{
    public override ImportKindSpec Spec { get; } = ImportKinds.Get(ImportKinds.Tasks)!;

    private static string TaskKey(string train, string gate, string description) => string.Join('\u001f', train.ToLowerInvariant(), gate.ToLowerInvariant(), description);

    public override async Task<List<CurrentRow>> LoadAsync(ReleaseDbContext db, GridFilter f, CancellationToken ct)
    {
        var (users, teams, trains) = await RefsAsync(db, ct);
        var gates = (await db.Set<StageGates>().AsNoTracking().ToListAsync(ct)).ToDictionary(g => g.Id);
        var products = (await db.Set<BundledProducts>().AsNoTracking().ToListAsync(ct)).ToDictionary(p => p.Id, p => p.ProductName);
        var tasks = await db.Set<ChecklistTasks>().AsNoTracking().ToListAsync(ct);
        return [.. tasks.Where(t => gates.ContainsKey(t.StageGateId) && (string.IsNullOrEmpty(f.Train) || gates[t.StageGateId].ReleaseTrainId == f.Train))
            .Select(t => (t, g: gates[t.StageGateId], train: TrainCell(gates[t.StageGateId].ReleaseTrainId, trains)))
            .OrderBy(x => x.train, StringComparer.Ordinal).ThenBy(x => x.g.SequenceOrder).ThenBy(x => x.t.SequenceOrder).ThenBy(x => x.t.Id, StringComparer.Ordinal)
            .Select(x => new CurrentRow(x.t.Id, x.t.Version, x.g.ReleaseTrainId, TaskKey(x.train, Norm(x.g.GateName), Norm(x.t.TaskDescription)),
                new Dictionary<string, string>
                {
                    ["Train"] = x.train, ["Gate"] = Norm(x.g.GateName), ["Description"] = Norm(x.t.TaskDescription), ["Owner"] = OwnerCell(x.t.OwnerUserId, x.t.OwnerTeamId, users, teams),
                    ["Product"] = x.t.BundledProductId is not null && products.TryGetValue(x.t.BundledProductId, out var pn) ? Norm(pn) : "", ["Order"] = x.t.SequenceOrder.ToString(),
                },
                [new("#Id", x.t.Id), new("#Completed", x.t.IsCompleted ? "Yes" : "No"), new("#Version", x.t.Version.ToString())]))];
    }

    protected override async Task PrepareAsync(PlanEnv env, CancellationToken ct)
    {
        await env.LoadPeopleAsync(ct);
        env.Bag["gates"] = (await env.Db.Set<StageGates>().AsNoTracking().ToListAsync(ct)).ToLookup(g => g.ReleaseTrainId);
        env.Bag["products"] = (await env.Db.Set<BundledProducts>().AsNoTracking().ToListAsync(ct)).ToLookup(p => p.ReleaseTrainId);
        env.Bag["next"] = (await env.Db.Set<ChecklistTasks>().AsNoTracking().GroupBy(t => t.StageGateId).Select(g => new { g.Key, Max = g.Max(x => x.SequenceOrder) }).ToListAsync(ct)).ToDictionary(x => x.Key, x => x.Max);
    }

    protected override Resolved? Resolve(PlanEnv env, ParsedRow row)
    {
        var r = new Resolved();
        foreach (var (k, v) in row.Cells) r.After[k] = v;
        var t = TrainOf(env, row, r);
        if (t is null) return null;
        var ok = true;
        var gates = ((ILookup<string, StageGates>)env.Bag["gates"])[t.Id].Where(g => string.Equals(g.GateName.Trim(), row.Cells["Gate"], StringComparison.OrdinalIgnoreCase)).ToList();
        StageGates? gate = null;
        if (gates.Count == 0) { env.Err(row.Row, "Gate", $"{t.Title} has no gate named {PlanEnv.Quote(row.Cells["Gate"])}.{PlanEnv.Did(RowParser.Suggest(row.Cells["Gate"], ((ILookup<string, StageGates>)env.Bag["gates"])[t.Id].Select(g => g.GateName)))}"); ok = false; }
        else if (gates.Count > 1) { env.Err(row.Row, "Gate", $"{t.Title} has {gates.Count} gates named {PlanEnv.Quote(row.Cells["Gate"])}; rename one so the file can name it"); ok = false; }
        else { gate = gates[0]; r.After["Gate"] = gate.GateName.Trim(); }

        var ownerOk = env.ResolveOwner(row.Row, "Owner", row.Cells["Owner"], out var uid, out var tid, out var canonical, out var user);
        if (ownerOk) r.After["Owner"] = canonical; else ok = false;

        string? productId = null;
        if (row.Cells.TryGetValue("Product", out var product) && product.Length > 0)
        {
            var all = ((ILookup<string, BundledProducts>)env.Bag["products"])[t.Id].ToList();
            var hits = all.Where(x => string.Equals(x.ProductName.Trim(), product, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 1) { productId = hits[0].Id; r.After["Product"] = hits[0].ProductName.Trim(); }
            else { env.Err(row.Row, "Product", hits.Count == 0 ? $"{t.Title} has no product named {PlanEnv.Quote(product)}.{PlanEnv.Did(RowParser.Suggest(product, all.Select(x => x.ProductName)))}" : $"{t.Title} has {hits.Count} products named {PlanEnv.Quote(product)}"); ok = false; }
        }
        if (!ok) return null;
        r.Key = TaskKey(t.Title, gate!.GateName.Trim(), row.Cells["Description"]);
        r.Refs = $"{TrainRef(t)}|{gate.Id}:{gate.Status}|{uid ?? tid}|{productId}";
        r.Payload = new TaskPayload(gate.Id, gate.Status, gate.GateName.Trim(), uid, tid, productId, user);
        return r;
    }

    protected override void Finish(PlanEnv env, Resolved r, CurrentRow? existing)
    {
        var order = r.After.GetValueOrDefault("Order", "");
        if (order.Length > 0) return;
        if (existing is not null) { if (r.After.ContainsKey("Order")) r.After["Order"] = existing.Cells["Order"]; return; }   // empty on an existing task: keep its place
        var next = (Dictionary<string, int>)env.Bag["next"];
        var gateId = ((TaskPayload)r.Payload!).GateId;
        next[gateId] = next.GetValueOrDefault(gateId) + 1;
        r.After["Order"] = next[gateId].ToString();
    }

    protected override void Rules(PlanEnv env, ParsedRow row, Resolved r, Planned p)
    {
        PlanLock(env, row, r, p);
        var t = (TaskPayload)r.Payload!;
        OwnerActive(env, row, p, t.User);
        if (p.Op == RowOp.New && t.GateStatus == "Certified")
        {
            env.Warn(row.Row, "Gate", $"Adding this task decertifies gate {t.GateName} of {r.Train!.Title}: its certification is removed and it goes back to In progress");
            env.Decertifies.Add($"{r.Train.Title}: {t.GateName}");
        }
    }

    public override async Task ApplyAsync(ApplyEnv env, IReadOnlyList<Planned> rows, CancellationToken ct)
    {
        var byId = new Dictionary<string, ChecklistTasks>();
        foreach (var chunk in Chunks(rows.Where(r => r.Existing is not null).Select(r => r.Existing!.Id)))
            foreach (var x in await env.Db.Set<ChecklistTasks>().Where(x => chunk.Contains(x.Id)).ToListAsync(ct)) byId[x.Id] = x;
        foreach (var p in rows.Where(r => r.Op is RowOp.New or RowOp.Updated).OrderBy(r => r.Row))
        {
            var pl = (TaskPayload)p.Payload!;
            string id;
            if (p.Op == RowOp.New)
            {
                // the AFTER INSERT trigger decertifies a Certified gate and writes its own audit row; it reads the actor and time from these two columns (D27)
                var x = new ChecklistTasks
                {
                    StageGateId = pl.GateId, BundledProductId = pl.ProductId, TaskDescription = p.After["Description"], OwnerUserId = pl.UserId, OwnerTeamId = pl.TeamId,
                    SequenceOrder = ParseInt(p.After["Order"]), LastChangedByUserId = env.Actor.UserId, LastChangedAt = env.Now,
                };
                env.Db.Set<ChecklistTasks>().Add(x); id = x.Id;
            }
            else
            {
                var x = byId[p.Existing!.Id]; id = x.Id;
                if (p.Changed.Contains("Owner")) { x.OwnerUserId = pl.UserId; x.OwnerTeamId = pl.TeamId; }
                if (p.Changed.Contains("Product")) x.BundledProductId = pl.ProductId;
                if (p.Changed.Contains("Order")) x.SequenceOrder = ParseInt(p.After["Order"]);
                x.LastChangedByUserId = env.Actor.UserId; x.LastChangedAt = env.Now; x.Version++;
            }
            env.Bump(p.TrainId);
            env.AuditRow(p, p.TrainId, "ChecklistTask", id);
        }
    }
}

internal sealed record StepPayload(string? UserId, string? TeamId, string? ProductId, Users? User, string[]? Deps);

internal sealed class StepsHandler : TrainScopedHandler
{
    public override ImportKindSpec Spec { get; } = ImportKinds.Get(ImportKinds.RunbookSteps)!;

    public override async Task<List<CurrentRow>> LoadAsync(ReleaseDbContext db, GridFilter f, CancellationToken ct)
    {
        var (users, teams, trains) = await RefsAsync(db, ct);
        var products = (await db.Set<BundledProducts>().AsNoTracking().ToListAsync(ct)).ToDictionary(p => p.Id, p => p.ProductName);
        var q = db.Set<RunbookSteps>().AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(f.Train)) q = q.Where(s => s.ReleaseTrainId == f.Train);
        var steps = await q.ToListAsync(ct);
        var codes = (await db.Set<RunbookSteps>().AsNoTracking().Select(s => new { s.Id, s.StepCode }).ToListAsync(ct)).ToDictionary(s => s.Id, s => s.StepCode);
        var deps = (await db.Set<StepDependencies>().AsNoTracking().ToListAsync(ct)).GroupBy(d => d.StepId).ToDictionary(g => g.Key, g => g.Select(d => codes.GetValueOrDefault(d.DependsOnStepId, "?").ToUpperInvariant()));
        return [.. steps.OrderBy(s => TrainCell(s.ReleaseTrainId, trains), StringComparer.Ordinal).ThenBy(s => s.PlannedStartAt).ThenBy(s => s.StepCode, StringComparer.Ordinal)
            .Select(s => new CurrentRow(s.Id, s.Version, s.ReleaseTrainId, K(TrainCell(s.ReleaseTrainId, trains), s.StepCode.ToUpperInvariant()),
                new Dictionary<string, string>
                {
                    ["Train"] = TrainCell(s.ReleaseTrainId, trains), ["StepCode"] = s.StepCode.ToUpperInvariant(), ["Title"] = Norm(s.Title), ["Section"] = s.Section, ["PlannedStart"] = Ts(s.PlannedStartAt),
                    ["DurationMin"] = s.PlannedDurationMin.ToString(), ["Owner"] = OwnerCell(s.OwnerUserId, s.OwnerTeamId, users, teams),
                    ["Product"] = s.BundledProductId is not null && products.TryGetValue(s.BundledProductId, out var pn) ? Norm(pn) : "",
                    ["DependsOn"] = deps.TryGetValue(s.Id, out var d) ? string.Join(';', d.Distinct().Order(StringComparer.Ordinal)) : "", ["Instructions"] = Norm(s.Instructions),
                },
                [new("#Id", s.Id), new("#Version", s.Version.ToString())]))];
    }

    protected override async Task PrepareAsync(PlanEnv env, CancellationToken ct)
    {
        await env.LoadPeopleAsync(ct);
        env.Bag["products"] = (await env.Db.Set<BundledProducts>().AsNoTracking().ToListAsync(ct)).ToLookup(p => p.ReleaseTrainId);
        var steps = await env.Db.Set<RunbookSteps>().AsNoTracking().Select(s => new { s.Id, s.ReleaseTrainId, s.StepCode }).ToListAsync(ct);
        var codes = steps.ToDictionary(s => s.Id, s => s.StepCode.ToUpperInvariant());
        var deps = (await env.Db.Set<StepDependencies>().AsNoTracking().ToListAsync(ct)).GroupBy(d => d.StepId).ToDictionary(g => g.Key, g => g.Select(d => codes.GetValueOrDefault(d.DependsOnStepId, "?")).ToList());
        // per train: code -> the codes it depends on now
        env.Bag["graph"] = steps.GroupBy(s => s.ReleaseTrainId).ToDictionary(g => g.Key, g => g.ToDictionary(s => s.StepCode.ToUpperInvariant(), s => deps.GetValueOrDefault(s.Id) ?? [], StringComparer.Ordinal));
    }

    protected override Resolved? Resolve(PlanEnv env, ParsedRow row)
    {
        var r = new Resolved();
        foreach (var (k, v) in row.Cells) r.After[k] = v;
        var t = TrainOf(env, row, r);
        if (t is null) return null;
        var ok = env.ResolveOwner(row.Row, "Owner", row.Cells["Owner"], out var uid, out var tid, out var canonical, out var user);
        if (ok) r.After["Owner"] = canonical;
        string? productId = null;
        if (row.Cells.TryGetValue("Product", out var product) && product.Length > 0)
        {
            var all = ((ILookup<string, BundledProducts>)env.Bag["products"])[t.Id].ToList();
            var hits = all.Where(x => string.Equals(x.ProductName.Trim(), product, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 1) { productId = hits[0].Id; r.After["Product"] = hits[0].ProductName.Trim(); }
            else { env.Err(row.Row, "Product", hits.Count == 0 ? $"{t.Title} has no product named {PlanEnv.Quote(product)}.{PlanEnv.Did(RowParser.Suggest(product, all.Select(x => x.ProductName)))}" : $"{t.Title} has {hits.Count} products named {PlanEnv.Quote(product)}"); ok = false; }
        }
        if (!ok) return null;
        r.Key = K(t.Title, row.Cells["StepCode"]);
        r.Refs = $"{TrainRef(t)}|{uid ?? tid}|{productId}";
        r.Payload = new StepPayload(uid, tid, productId, user, row.Cells.TryGetValue("DependsOn", out var d) ? (d.Length == 0 ? [] : d.Split(';')) : null);
        return r;
    }

    protected override void Rules(PlanEnv env, ParsedRow row, Resolved r, Planned p)
    {
        PlanLock(env, row, r, p);
        OwnerActive(env, row, p, ((StepPayload)r.Payload!).User);
    }

    /// <summary>DependsOn = step codes resolved within this file and the train; a self-reference, an unknown code or a cycle is an error on the DependsOn cell.</summary>
    protected override void Final(PlanEnv env, List<Planned> planned)
    {
        var graphs = (Dictionary<string, Dictionary<string, List<string>>>)env.Bag["graph"];
        foreach (var byTrain in planned.Where(p => p.Op != RowOp.Error && p.Payload is StepPayload).GroupBy(p => p.TrainId!))
        {
            var current = graphs.GetValueOrDefault(byTrain.Key) ?? [];
            var fileCodes = byTrain.ToDictionary(p => p.After["StepCode"], p => p, StringComparer.Ordinal);
            var universe = current.Keys.Concat(fileCodes.Keys).ToHashSet(StringComparer.Ordinal);
            var title = byTrain.First().After["Train"];
            var graph = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);
            foreach (var (code, deps) in current) graph[code] = deps;
            var clean = true;
            foreach (var p in byTrain.OrderBy(p => p.Row))
            {
                var code = p.After["StepCode"];
                var deps = ((StepPayload)p.Payload!).Deps;
                if (deps is null) { if (!graph.ContainsKey(code)) graph[code] = []; continue; }
                var good = true;
                foreach (var d in deps)
                {
                    if (d == code) { env.Err(p.Row, "DependsOn", $"{code} cannot depend on itself"); good = false; }
                    else if (!universe.Contains(d))
                    {
                        env.Err(p.Row, "DependsOn", $"{d} is not a step in this file or in {title}.{PlanEnv.Did(RowParser.Suggest(d, universe))}"); good = false;
                    }
                }
                if (good) graph[code] = deps; else clean = false;
            }
            if (!clean) continue;
            var cycle = DependencyGraph.FindCycle(graph);
            if (cycle is null) continue;
            var row = cycle.Select(c => fileCodes.TryGetValue(c, out var fp) ? fp : null).FirstOrDefault(fp => fp is not null && ((StepPayload)fp.Payload!).Deps is not null);
            if (row is not null) env.Err(row.Row, "DependsOn", $"That would make a dependency cycle: {string.Join(" -> ", cycle)}");
        }
    }

    public override async Task ApplyAsync(ApplyEnv env, IReadOnlyList<Planned> rows, CancellationToken ct)
    {
        var byId = new Dictionary<string, RunbookSteps>();
        var changed = rows.Where(r => r.Op is RowOp.New or RowOp.Updated).OrderBy(r => r.Row).ToList();
        foreach (var chunk in Chunks(rows.Where(r => r.Existing is not null).Select(r => r.Existing!.Id)))
            foreach (var x in await env.Db.Set<RunbookSteps>().Where(x => chunk.Contains(x.Id)).ToListAsync(ct)) byId[x.Id] = x;
        var trainIds = changed.Select(p => p.TrainId!).Distinct().ToList();
        var idOf = new Dictionary<(string, string), string>();
        foreach (var s in await env.Db.Set<RunbookSteps>().AsNoTracking().Where(s => trainIds.Contains(s.ReleaseTrainId)).Select(s => new { s.Id, s.ReleaseTrainId, s.StepCode }).ToListAsync(ct))
            idOf[(s.ReleaseTrainId, s.StepCode.ToUpperInvariant())] = s.Id;

        var stepIds = new Dictionary<int, string>();
        foreach (var p in changed)
        {
            var pl = (StepPayload)p.Payload!;
            string id;
            if (p.Op == RowOp.New)
            {
                var x = new RunbookSteps
                {
                    ReleaseTrainId = p.TrainId!, BundledProductId = pl.ProductId, StepCode = p.After["StepCode"], Section = p.After["Section"], Title = p.After["Title"],
                    Instructions = p.After.GetValueOrDefault("Instructions") is { Length: > 0 } ins ? ins : null, OwnerUserId = pl.UserId, OwnerTeamId = pl.TeamId,
                    PlannedStartAt = ParseTs(p.After["PlannedStart"]), PlannedDurationMin = ParseInt(p.After["DurationMin"]),
                };
                env.Db.Set<RunbookSteps>().Add(x); id = x.Id;
                idOf[(p.TrainId!, x.StepCode)] = id;
            }
            else
            {
                var x = byId[p.Existing!.Id]; id = x.Id;
                if (p.Changed.Contains("Title")) x.Title = p.After["Title"];
                if (p.Changed.Contains("Section")) x.Section = p.After["Section"];
                if (p.Changed.Contains("PlannedStart")) x.PlannedStartAt = ParseTs(p.After["PlannedStart"]);
                if (p.Changed.Contains("DurationMin")) x.PlannedDurationMin = ParseInt(p.After["DurationMin"]);
                if (p.Changed.Contains("Owner")) { x.OwnerUserId = pl.UserId; x.OwnerTeamId = pl.TeamId; }
                if (p.Changed.Contains("Product")) x.BundledProductId = pl.ProductId;
                if (p.Changed.Contains("Instructions")) x.Instructions = p.After["Instructions"].Length == 0 ? null : p.After["Instructions"];
                x.Version++;
            }
            stepIds[p.Row] = id;
            env.Bump(p.TrainId);
            env.AuditRow(p, p.TrainId, "RunbookStep", id);
        }
        // dependencies last, once every step of the file has its id
        var replace = changed.Where(p => ((StepPayload)p.Payload!).Deps is not null && (p.Op == RowOp.New ? ((StepPayload)p.Payload!).Deps!.Length > 0 : p.Changed.Contains("DependsOn"))).ToList();
        var replaceIds = replace.Where(p => p.Op == RowOp.Updated).Select(p => stepIds[p.Row]).ToList();
        foreach (var chunk in Chunks(replaceIds))
            env.Db.Set<StepDependencies>().RemoveRange(await env.Db.Set<StepDependencies>().Where(d => chunk.Contains(d.StepId)).ToListAsync(ct));
        foreach (var p in replace)
            foreach (var code in ((StepPayload)p.Payload!).Deps!.Distinct())
                env.Db.Set<StepDependencies>().Add(new StepDependencies { StepId = stepIds[p.Row], DependsOnStepId = idOf[(p.TrainId!, code)] });
    }
}

internal sealed record LinkPayload(string EntityId);

internal sealed class LinksHandler : KindHandler
{
    public override ImportKindSpec Spec { get; } = ImportKinds.Get(ImportKinds.ExternalLinks)!;

    private static string LinkKey(string system, string entityType, string entityId, string key) => string.Join('\u001f', system.ToLowerInvariant(), entityType.ToLowerInvariant(), entityId, key);

    /// <summary>Every item a link can point at, by id and by (type, train): products, gates and steps by name/code, blockers and known issues by title, the train by title.</summary>
    internal sealed class Targets
    {
        public Dictionary<string, (string Type, string Name)> ById { get; } = [];
        public ILookup<(string Type, string Train), (string Id, string Name)> ByTrain { get; private set; } = null!;

        public static async Task<Targets> LoadAsync(ReleaseDbContext db, CancellationToken ct)
        {
            var t = new Targets();
            var list = new List<(string Type, string Train, string Id, string Name)>();
            foreach (var x in await db.Set<ReleaseTrains>().AsNoTracking().Select(x => new { x.Id, x.Title }).ToListAsync(ct)) list.Add(("Train", x.Id, x.Id, x.Title.Trim()));
            foreach (var x in await db.Set<BundledProducts>().AsNoTracking().Select(x => new { x.Id, x.ReleaseTrainId, x.ProductName }).ToListAsync(ct)) list.Add(("Product", x.ReleaseTrainId, x.Id, x.ProductName.Trim()));
            foreach (var x in await db.Set<StageGates>().AsNoTracking().Select(x => new { x.Id, x.ReleaseTrainId, x.GateName }).ToListAsync(ct)) list.Add(("Gate", x.ReleaseTrainId, x.Id, x.GateName.Trim()));
            foreach (var x in await db.Set<RunbookSteps>().AsNoTracking().Select(x => new { x.Id, x.ReleaseTrainId, x.StepCode }).ToListAsync(ct)) list.Add(("RunbookStep", x.ReleaseTrainId, x.Id, x.StepCode.ToUpperInvariant()));
            foreach (var x in await db.Set<Blockers>().AsNoTracking().Select(x => new { x.Id, x.ReleaseTrainId, x.Title }).ToListAsync(ct)) list.Add(("Blocker", x.ReleaseTrainId, x.Id, x.Title.Trim()));
            foreach (var x in await db.Set<KnownIssues>().AsNoTracking().Select(x => new { x.Id, x.ReleaseTrainId, x.Title }).ToListAsync(ct)) list.Add(("KnownIssue", x.ReleaseTrainId, x.Id, x.Title.Trim()));
            foreach (var x in list) t.ById[x.Id] = (x.Type, x.Name);
            t.ByTrain = list.ToLookup(x => (x.Type, x.Train), x => (x.Id, x.Name));
            return t;
        }
    }

    public override async Task<List<CurrentRow>> LoadAsync(ReleaseDbContext db, GridFilter f, CancellationToken ct)
    {
        var (_, _, trains) = await RefsAsync(db, ct);
        var targets = await Targets.LoadAsync(db, ct);
        var q = db.Set<ExternalLinks>().AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(f.Train)) q = q.Where(l => l.ReleaseTrainId == f.Train);
        return [.. (await q.ToListAsync(ct)).OrderBy(l => TrainCell(l.ReleaseTrainId, trains), StringComparer.Ordinal).ThenBy(l => l.SourceSystem).ThenBy(l => l.ExternalKey, StringComparer.Ordinal).ThenBy(l => l.EntityType)
            .Select(l => new CurrentRow(l.Id, l.Version, l.ReleaseTrainId, LinkKey(l.SourceSystem, l.EntityType, l.EntityId, l.ExternalKey),
                new Dictionary<string, string>
                {
                    ["Train"] = TrainCell(l.ReleaseTrainId, trains), ["EntityType"] = l.EntityType, ["EntityRef"] = targets.ById.TryGetValue(l.EntityId, out var e) ? e.Name : "",
                    ["System"] = l.SourceSystem, ["Key"] = l.ExternalKey,
                },
                [new("#Id", l.Id), new("#SyncState", l.SyncState), new("#LastSyncedStatus", Norm(l.LastSyncedStatus)), new("#Version", l.Version.ToString())]))];
    }

    protected override async Task PrepareAsync(PlanEnv env, CancellationToken ct) => env.Bag["targets"] = await Targets.LoadAsync(env.Db, ct);

    protected override Resolved? Resolve(PlanEnv env, ParsedRow row)
    {
        var r = new Resolved();
        foreach (var (k, v) in row.Cells) r.After[k] = v;
        var t = env.ResolveTrain(row.Row, row.Cells["Train"]);
        if (t is null) return null;
        r.Train = t; r.TrainId = t.Id; r.After["Train"] = t.Title;
        var ok = true;
        var system = row.Cells["System"]; var type = row.Cells["EntityType"];
        var key = ExternalKeys.Normalize(system, row.Cells["Key"]);
        if (ExternalKeys.Validate(system, key) is string bad) { env.Err(row.Row, "Key", bad); ok = false; } else r.After["Key"] = key;

        var targets = (Targets)env.Bag["targets"];
        var all = targets.ByTrain[(type, t.Id)].ToList();
        var reference = row.Cells["EntityRef"];
        var hits = all.Where(x => string.Equals(x.Name, reference, StringComparison.OrdinalIgnoreCase)).ToList();
        string? entityId = null;
        if (hits.Count == 1) { entityId = hits[0].Id; r.After["EntityRef"] = hits[0].Name; }
        else
        {
            var what = type switch { "Train" => "train title", "Product" => "product name", "Gate" => "gate name", "RunbookStep" => "step code", _ => "title" };
            env.Err(row.Row, "EntityRef", hits.Count == 0 ? $"{t.Title} has no {type} with {what} {PlanEnv.Quote(reference)}.{PlanEnv.Did(RowParser.Suggest(reference, all.Select(x => x.Name)))}"
                                                          : $"{t.Title} has {hits.Count} items of type {type} named {PlanEnv.Quote(reference)}; rename one so the file can name it");
            ok = false;
        }
        if (!ok) return null;
        r.Key = LinkKey(system, type, entityId!, key);
        r.Refs = $"{t.Id}|{entityId}";
        r.Payload = new LinkPayload(entityId!);
        return r;
    }

    public override Task ApplyAsync(ApplyEnv env, IReadOnlyList<Planned> rows, CancellationToken ct)
    {
        foreach (var p in rows.Where(r => r.Op == RowOp.New))
        {
            var l = new ExternalLinks { ReleaseTrainId = p.TrainId!, EntityType = p.After["EntityType"], EntityId = ((LinkPayload)p.Payload!).EntityId, SourceSystem = p.After["System"], ExternalKey = p.After["Key"] };
            env.Db.Set<ExternalLinks>().Add(l);
            env.AuditRow(p, p.TrainId, "ExternalLink", l.Id);   // links do not bump the train (ConnectorService.AddLink does not either)
        }
        return Task.CompletedTask;
    }
}
