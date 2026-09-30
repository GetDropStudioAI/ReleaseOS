using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Exchange;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Exchange;

internal sealed class UsersHandler : KindHandler
{
    public override ImportKindSpec Spec { get; } = ImportKinds.Get(ImportKinds.Users)!;

    public override async Task<List<CurrentRow>> LoadAsync(ReleaseDbContext db, GridFilter filter, CancellationToken ct) =>
        [.. (await db.Set<Users>().AsNoTracking().ToListAsync(ct)).OrderBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(u => u.Email, StringComparer.OrdinalIgnoreCase)
            .Select(u => new CurrentRow(u.Id, u.Version, null, K(u.Email),
                new Dictionary<string, string> { ["Email"] = u.Email, ["DisplayName"] = Norm(u.DisplayName), ["Role"] = u.Role, ["Handle"] = Norm(u.Handle) },
                [new("#Id", u.Id), new("#IsActive", u.IsActive ? "Yes" : "No"), new("#Version", u.Version.ToString())]))];

    protected override Resolved? Resolve(PlanEnv env, ParsedRow row)
    {
        var r = new Resolved { Key = K(row.Cells["Email"]) };
        foreach (var (k, v) in row.Cells) r.After[k] = v;
        return r;
    }

    protected override async Task PrepareAsync(PlanEnv env, CancellationToken ct)
    {
        await env.LoadPeopleAsync(ct);
        env.Bag["handles"] = env.Users.Where(u => !string.IsNullOrEmpty(u.Handle)).GroupBy(u => u.Handle!.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First().Email.ToLowerInvariant());
    }

    protected override void Rules(PlanEnv env, ParsedRow row, Resolved r, Planned p)
    {
        // Q-003: the identity provider owns roles. A file may set the role of a NEW user (pre-provisioning) but not change an existing one.
        if (p.Existing is not null && p.Changed.Contains("Role"))
            env.Err(row.Row, "Role", $"{p.Existing.Cells["Email"]} is {p.Existing.Cells["Role"]}; roles come from the identity provider (Q-003) and cannot be changed by file");
        if (r.After.TryGetValue("Handle", out var h) && h.Length > 0)
        {
            var handles = (Dictionary<string, string>)env.Bag["handles"];
            var email = r.Key;
            if (handles.TryGetValue(h.ToLowerInvariant(), out var owner) && owner != email) env.Err(row.Row, "Handle", $"@{h} already belongs to {owner}");
            else handles[h.ToLowerInvariant()] = email;
        }
    }

    public override async Task ApplyAsync(ApplyEnv env, IReadOnlyList<Planned> rows, CancellationToken ct)
    {
        var byId = new Dictionary<string, Users>();
        foreach (var chunk in Chunks(rows.Where(r => r.Existing is not null).Select(r => r.Existing!.Id)))
            foreach (var u in await env.Db.Set<Users>().Where(u => chunk.Contains(u.Id)).ToListAsync(ct)) byId[u.Id] = u;
        foreach (var p in rows.Where(r => r.Op is RowOp.New or RowOp.Updated))
        {
            if (p.Op == RowOp.New)
            {
                var u = new Users { Email = p.After["Email"], DisplayName = p.After["DisplayName"], Role = p.After["Role"], Handle = p.After.GetValueOrDefault("Handle") is { Length: > 0 } h ? h : null, IsActive = true };
                env.Db.Set<Users>().Add(u);
                env.AuditRow(p, null, "User", u.Id);
            }
            else
            {
                var u = byId[p.Existing!.Id];
                if (p.Changed.Contains("DisplayName")) u.DisplayName = p.After["DisplayName"];
                if (p.Changed.Contains("Handle")) u.Handle = p.After["Handle"].Length == 0 ? null : p.After["Handle"];
                u.Version++;
                env.AuditRow(p, null, "User", u.Id);
            }
        }
    }
}

internal sealed class TeamsHandler : KindHandler
{
    public override ImportKindSpec Spec { get; } = ImportKinds.Get(ImportKinds.Teams)!;

    public override async Task<List<CurrentRow>> LoadAsync(ReleaseDbContext db, GridFilter filter, CancellationToken ct)
    {
        var users = (await db.Set<Users>().AsNoTracking().ToListAsync(ct)).ToDictionary(u => u.Id);
        var members = (await db.Set<TeamMembers>().AsNoTracking().ToListAsync(ct)).ToLookup(m => m.TeamId);
        return [.. (await db.Set<Teams>().AsNoTracking().ToListAsync(ct)).OrderBy(t => t.Handle, StringComparer.OrdinalIgnoreCase)
            .Select(t => new CurrentRow(t.Id, t.Version, null, K(t.Handle),
                new Dictionary<string, string> { ["Handle"] = t.Handle, ["Name"] = Norm(t.Name), ["Members"] = MemberCell(members[t.Id].Select(m => users.TryGetValue(m.UserId, out var u) ? u.Email : m.UserId)) },
                [new("#Id", t.Id), new("#Version", t.Version.ToString())]))];
    }

    internal static string MemberCell(IEnumerable<string> emails) =>
        string.Join(';', emails.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ThenBy(x => x, StringComparer.Ordinal));

    protected override Task PrepareAsync(PlanEnv env, CancellationToken ct) => env.LoadPeopleAsync(ct);

    protected override Resolved? Resolve(PlanEnv env, ParsedRow row)
    {
        var r = new Resolved { Key = K(row.Cells["Handle"]) };
        foreach (var (k, v) in row.Cells) r.After[k] = v;
        if (row.Cells.TryGetValue("Members", out var list))
        {
            var ids = new List<string>(); var emails = new List<string>(); var ok = true;
            foreach (var e in RowParser.Split(list))
            {
                var u = env.Users.FirstOrDefault(x => string.Equals(x.Email, e, StringComparison.OrdinalIgnoreCase));
                if (u is null) { env.Err(row.Row, "Members", $"No user has the email {PlanEnv.Quote(e)}.{PlanEnv.Did(env.Suggest(e, env.Users.Select(x => x.Email)))}"); ok = false; }
                else { ids.Add(u.Id); emails.Add(u.Email); }
            }
            if (!ok) return null;
            r.After["Members"] = MemberCell(emails);
            r.Payload = ids.Distinct().ToArray();
            r.Refs = string.Join(',', ids.Order());
        }
        return r;
    }

    public override async Task ApplyAsync(ApplyEnv env, IReadOnlyList<Planned> rows, CancellationToken ct)
    {
        var byId = new Dictionary<string, Teams>();
        foreach (var chunk in Chunks(rows.Where(r => r.Existing is not null).Select(r => r.Existing!.Id)))
            foreach (var t in await env.Db.Set<Teams>().Where(t => chunk.Contains(t.Id)).ToListAsync(ct)) byId[t.Id] = t;
        foreach (var p in rows.Where(r => r.Op is RowOp.New or RowOp.Updated))
        {
            var wanted = (p.Payload as string[])?.ToHashSet() ?? [];
            if (p.Op == RowOp.New)
            {
                var t = new Teams { Handle = p.After["Handle"], Name = p.After["Name"] };
                env.Db.Set<Teams>().Add(t);
                foreach (var m in wanted) env.Db.Set<TeamMembers>().Add(new TeamMembers { TeamId = t.Id, UserId = m });
                env.AuditRow(p, null, "Team", t.Id);
            }
            else
            {
                var t = byId[p.Existing!.Id];
                if (p.Changed.Contains("Name")) t.Name = p.After["Name"];
                if (p.Changed.Contains("Members"))
                {
                    var current = await env.Db.Set<TeamMembers>().Where(m => m.TeamId == t.Id).ToListAsync(ct);
                    env.Db.Set<TeamMembers>().RemoveRange(current.Where(m => !wanted.Contains(m.UserId)));
                    foreach (var add in wanted.Except(current.Select(m => m.UserId))) env.Db.Set<TeamMembers>().Add(new TeamMembers { TeamId = t.Id, UserId = add });
                }
                t.Version++;
                env.AuditRow(p, null, "Team", t.Id);
            }
        }
    }
}

internal sealed class HolidaysHandler : KindHandler
{
    public override ImportKindSpec Spec { get; } = ImportKinds.Get(ImportKinds.Holidays)!;

    public override async Task<List<CurrentRow>> LoadAsync(ReleaseDbContext db, GridFilter filter, CancellationToken ct) =>
        [.. (await db.Set<Holidays>().AsNoTracking().OrderBy(h => h.Day).ToListAsync(ct))
            .Select(h => new CurrentRow(Day(h.Day), h.Version, null, K(Day(h.Day)),
                new Dictionary<string, string> { ["Day"] = Day(h.Day), ["Name"] = Norm(h.Name) }, [new("#Version", h.Version.ToString())]))];

    protected override Resolved? Resolve(PlanEnv env, ParsedRow row)
    {
        var r = new Resolved { Key = K(row.Cells["Day"]) };
        foreach (var (k, v) in row.Cells) r.After[k] = v;
        return r;
    }

    public override async Task ApplyAsync(ApplyEnv env, IReadOnlyList<Planned> rows, CancellationToken ct)
    {
        var existing = (await env.Db.Set<Holidays>().ToListAsync(ct)).ToDictionary(h => Day(h.Day));
        foreach (var p in rows.Where(r => r.Op is RowOp.New or RowOp.Updated))
        {
            var day = p.After["Day"];
            if (p.Op == RowOp.New) env.Db.Set<Holidays>().Add(new Holidays { Day = ParseDay(day), Name = p.After["Name"] });
            else { var h = existing[day]; h.Name = p.After["Name"]; h.Version++; }
            env.AuditRow(p, null, "Holiday", day);
        }
    }
}
