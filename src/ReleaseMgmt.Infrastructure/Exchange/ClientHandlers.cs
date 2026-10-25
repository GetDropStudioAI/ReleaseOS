using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Exchange;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Exchange;

/// <summary>
/// RT3 (D47): the workspace's clients. A file row is Name, Segment (Standard or On-Demand) and, for an On-Demand client, the name of its CERT environment. A column that is absent leaves
/// the environment alone; a Standard row always ends with none (the same as the screen: making a client Standard clears the environment). An archived client is listed with <c>#Archived</c>
/// and stays archived when a file updates it.
/// </summary>
internal sealed class ClientsHandler : KindHandler
{
    public override ImportKindSpec Spec { get; } = ImportKinds.Get(ImportKinds.Clients)!;

    public override async Task<List<CurrentRow>> LoadAsync(ReleaseDbContext db, GridFilter filter, CancellationToken ct)
    {
        var envs = await db.Set<Environments>().AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.Name, ct);
        return [.. (await db.Set<Clients>().AsNoTracking().ToListAsync(ct)).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id, StringComparer.Ordinal)
            .Select(c => new CurrentRow(c.Id, c.Version, null, K(Norm(c.Name)),
                new Dictionary<string, string> { ["Name"] = Norm(c.Name), ["Segment"] = ClientSegments.Label(c.Segment), ["CertEnvironment"] = c.CertEnvironmentId is not null && envs.TryGetValue(c.CertEnvironmentId, out var n) ? n : "" },
                [new("#Id", c.Id), new("#Archived", c.ArchivedAt is null ? "No" : "Yes"), new("#Version", c.Version.ToString())]))];
    }

    protected override async Task PrepareAsync(PlanEnv env, CancellationToken ct) =>
        env.Bag["environments"] = await env.Db.Set<Environments>().AsNoTracking().ToListAsync(ct);

    protected override Resolved? Resolve(PlanEnv env, ParsedRow row)
    {
        var r = new Resolved { Key = K(row.Cells["Name"]) };
        foreach (var (k, v) in row.Cells) r.After[k] = v;
        if (row.Cells.TryGetValue("CertEnvironment", out var name) && name.Length > 0)
        {
            var all = (List<Environments>)env.Bag["environments"];
            var hit = all.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (hit is null) { env.Err(row.Row, "CertEnvironment", $"No environment is named {PlanEnv.Quote(name)}.{PlanEnv.Did(env.Suggest(name, all.Select(x => x.Name)))}"); return null; }
            r.After["CertEnvironment"] = hit.Name;
        }
        return r;
    }

    protected override void Finish(PlanEnv env, Resolved r, CurrentRow? existing)
    {
        if (r.After.ContainsKey("CertEnvironment")) return;
        // Column absent: a Standard client has none; an On-Demand client keeps the one it has.
        if (r.After["Segment"] == ClientSegments.Standard) r.After["CertEnvironment"] = "";
        else if (existing is not null) r.After["CertEnvironment"] = existing.Cells.GetValueOrDefault("CertEnvironment", "");
    }

    protected override void Rules(PlanEnv env, ParsedRow row, Resolved r, Planned p)
    {
        var segment = r.After["Segment"];
        var envName = r.After.GetValueOrDefault("CertEnvironment", "");
        if (segment == ClientSegments.Standard && envName.Length > 0)
            env.Err(row.Row, "CertEnvironment", "Only an On-Demand client has a CERT environment: leave it empty for a Standard client, or make the client On-Demand");
        else if (segment != ClientSegments.Standard && envName.Length == 0)
            env.Err(row.Row, "CertEnvironment", "An On-Demand client needs a CERT environment: give the name of one of this workspace's environments");
        else if (envName.Length > 0 && (p.Op == RowOp.New || p.Changed.Contains("CertEnvironment"))
                 && ((List<Environments>)env.Bag["environments"]).FirstOrDefault(x => string.Equals(x.Name, envName, StringComparison.OrdinalIgnoreCase)) is { ArchivedAt: not null })
            env.Err(row.Row, "CertEnvironment", $"The environment {PlanEnv.Quote(envName)} is archived and cannot be chosen as a CERT environment");
    }

    public override async Task ApplyAsync(ApplyEnv env, IReadOnlyList<Planned> rows, CancellationToken ct)
    {
        var environments = await env.Db.Set<Environments>().AsNoTracking().ToListAsync(ct);
        string? EnvId(string name) => name.Length == 0 ? null : environments.First(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)).Id;
        var byId = new Dictionary<string, Clients>();
        foreach (var chunk in Chunks(rows.Where(r => r.Existing is not null).Select(r => r.Existing!.Id)))
            foreach (var c in await env.Db.Set<Clients>().Where(c => chunk.Contains(c.Id)).ToListAsync(ct)) byId[c.Id] = c;
        foreach (var p in rows.Where(r => r.Op is RowOp.New or RowOp.Updated))
        {
            if (p.Op == RowOp.New)
            {
                var c = new Clients
                {
                    Name = p.After["Name"], Segment = p.After["Segment"] == ClientSegments.Standard ? ClientSegments.Standard : ClientSegments.OnDemand, CertEnvironmentId = EnvId(p.After.GetValueOrDefault("CertEnvironment", "")),
                    CreatedAt = env.Now, LastChangedByUserId = env.Actor.UserId, LastChangedAt = env.Now,
                };
                env.Db.Set<Clients>().Add(c);
                env.AuditRow(p, null, "Client", c.Id);
            }
            else
            {
                var c = byId[p.Existing!.Id];
                if (p.Changed.Contains("Segment")) c.Segment = p.After["Segment"] == ClientSegments.Standard ? ClientSegments.Standard : ClientSegments.OnDemand;
                if (p.Changed.Contains("Segment") || p.Changed.Contains("CertEnvironment")) c.CertEnvironmentId = EnvId(p.After.GetValueOrDefault("CertEnvironment", ""));
                c.Version++; c.LastChangedByUserId = env.Actor.UserId; c.LastChangedAt = env.Now;
                env.AuditRow(p, null, "Client", c.Id);
            }
        }
    }
}
