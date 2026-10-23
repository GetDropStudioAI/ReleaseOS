using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

/// <summary>
/// REOS-106 (D45): the switcher's list, creating a workspace, and managing a workspace's members (add a person, change a role, remove). Every write is
/// one transaction with one audit row, stamps Version and reads the clock from <see cref="TimeProvider"/>. The membership is read on every request
/// (<c>WorkspaceResolution</c>), so a change takes effect on the person's next request. QUESTIONS Q-106a to Q-106g record the judgement calls.
/// </summary>
public sealed partial class WorkspaceMembersService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, UserCreationService users, IRealtimePublisher? realtime = null)
    : ServiceBase(dbf, time, realtime)
{
    public const string InvalidInput = AdminService.InvalidInput, SlugTaken = "SlugTaken", LastAdmin = "LastAdmin", LastGovernance = "LastGovernance", NotAllowed = "NotAllowed";
    public const int MaxSlugLength = 40;

    /// <summary>The roles that administer a workspace (the Admin policy, D14/D32).</summary>
    private static readonly string[] AdminRoles = [Roles.RTE, Roles.ReleaseManager];

    // Lower case letters, digits and hyphens, starting and ending with a letter or digit (the schema's CHECK allows a bare hyphen; a URL segment should not).
    [GeneratedRegex("^[a-z0-9]([a-z0-9-]*[a-z0-9])?$")] private static partial Regex SlugShape();

    /// <summary>One workspace as the switcher lists it: the person's own role there.</summary>
    public sealed record WorkspaceChoice(string Id, string Name, string Slug, string? Region, string Role);

    /// <summary>One member of the current workspace, with the role held here (not <c>Users.Role</c>) and the membership's Version for If-Match.</summary>
    public sealed record MemberView(string UserId, string Email, string DisplayName, string? Handle, bool IsActive, string Role, DateTime AddedAt, int Version);

    /// <summary>The workspaces the person belongs to, not archived, by name. Not filtered by the current workspace: it is the list the switcher chooses from.</summary>
    public async Task<List<WorkspaceChoice>> ListMineAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var rows = await (from m in db.Set<WorkspaceMembers>().IgnoreQueryFilters().AsNoTracking()
                          join w in db.Set<Workspaces>().AsNoTracking() on m.WorkspaceId equals w.Id
                          where m.UserId == userId && w.ArchivedAt == null
                          select new WorkspaceChoice(w.Id, w.Name, w.Slug, w.Region, m.Role)).ToListAsync(ct);
        return [.. rows.OrderBy(w => w.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(w => w.Slug, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Creates a workspace (empty: Q-105h) and makes the creator its first member, with the role they hold in the workspace they create it from (an
    /// admin: RTE or Release Manager). Audited in the new workspace. The slug is the URL segment: unique on the host, and never another workspace's id.
    /// </summary>
    public Task<ServiceResult<WorkspaceChoice>> CreateAsync(string? name, string? slug, string? region, Actor actor, CancellationToken ct = default)
    {
        name = (name ?? "").Trim();
        slug = (slug ?? "").Trim().ToLowerInvariant();
        region = string.IsNullOrWhiteSpace(region) ? null : region.Trim();
        if (name.Length is 0 or > WorkspaceService.MaxNameLength || name.Any(char.IsControl)) return Task.FromResult(Invalid<WorkspaceChoice>($"Enter a name of at most {WorkspaceService.MaxNameLength} characters"));
        if (slug.Length is 0 or > MaxSlugLength || !SlugShape().IsMatch(slug)) return Task.FromResult(Invalid<WorkspaceChoice>($"A short name for the address is lower case letters, digits and hyphens, at most {MaxSlugLength} characters, such as emea-payments"));
        if (region is { Length: > WorkspaceService.MaxRegionLength } || region?.Any(char.IsControl) == true) return Task.FromResult(Invalid<WorkspaceChoice>($"A region is at most {WorkspaceService.MaxRegionLength} characters"));
        return RunAsync(async db =>
        {
            var mine = await Memberships.RoleOf(db, actor.UserId, ct);
            if (mine is null || !AdminRoles.Contains(mine)) return ServiceResult<WorkspaceChoice>.Fail(new GuardFailure(NotAllowed, "Only an RTE or Release Manager can create a workspace"));
            if (await db.Set<Workspaces>().AnyAsync(w => w.Slug == slug || w.Id == slug, ct))
                return ServiceResult<WorkspaceChoice>.Fail(new GuardFailure(SlugTaken, $"The short name “{slug}” is already used by a workspace on this server. Choose another"));
            var now = Now;
            var w = new Workspaces { Id = Ids.New(), Name = name, Slug = slug, Region = region, CreatedAt = now };
            db.Set<Workspaces>().Add(w);
            db.Set<WorkspaceMembers>().Add(new WorkspaceMembers { WorkspaceId = w.Id, UserId = actor.UserId, Role = mine, AddedAt = now });
            Audit(db, actor, null, "Workspace", w.Id, "Create", null, new { w.Name, w.Slug, w.Region, w.Version }).WorkspaceId = w.Id;
            Audit(db, actor, null, "WorkspaceMember", actor.UserId, "Add", null, new { workspaceId = w.Id, role = mine, creator = true }).WorkspaceId = w.Id;
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true)   // a race past the check above
            {
                return ServiceResult<WorkspaceChoice>.Fail(new GuardFailure(SlugTaken, $"The short name “{slug}” is already used by a workspace on this server. Choose another"));
            }
            return ServiceResult<WorkspaceChoice>.Ok(new(w.Id, w.Name, w.Slug, w.Region, mine));
        }, ct);
    }

    /// <summary>The current workspace's members, active and inactive, by name.</summary>
    public async Task<List<MemberView>> ListMembersAsync(CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var ws = Memberships.WorkspaceOf(db);
        var rows = await (from m in db.Set<WorkspaceMembers>().IgnoreQueryFilters().AsNoTracking()
                          join u in db.Set<Users>().AsNoTracking() on m.UserId equals u.Id
                          where m.WorkspaceId == ws
                          select new MemberView(u.Id, u.Email, u.DisplayName, u.Handle, u.IsActive, m.Role, m.AddedAt, m.Version)).ToListAsync(ct);
        return [.. rows.OrderBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase).ThenBy(m => m.Email, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Adds a person to the current workspace with a role. A new email address becomes an account (as <c>POST /users</c>); an address that already has an
    /// account becomes a member, and the answer never says whether it did (Q-105e). The account keeps its own display name.
    /// </summary>
    public async Task<ServiceResult<MemberView>> AddMemberAsync(string? email, string? displayName, string? role, Actor actor, CancellationToken ct = default)
    {
        var added = await users.CreateUserAsync(email, displayName, role, null, actor, ct);
        if (!added.IsOk) return added.Kind == ResultKind.GuardFailed ? ServiceResult<MemberView>.Fail(added.Failures) : ServiceResult<MemberView>.NotFound(added.Missing ?? "user");
        return ServiceResult<MemberView>.Ok((await ListMembersAsync(ct)).Single(m => m.UserId == added.Value!.Id));
    }

    /// <summary>
    /// Sets a member's role in the current workspace (If-Match is the membership's Version). The last active RTE/Release Manager, and the last active
    /// Governance Officer, cannot be moved to a role that ends that duty. In the default workspace <c>Users.Role</c> follows (Q-105e).
    /// </summary>
    public Task<ServiceResult<MemberView>> ChangeRoleAsync(string userId, string? role, Actor actor, int? expectedVersion, CancellationToken ct = default)
    {
        if (role is null || !Roles.All.Contains(role)) return Task.FromResult(Invalid<MemberView>($"The role must be one of {string.Join(", ", Roles.All)}"));
        return RunAsync(async db =>
        {
            var ws = Memberships.WorkspaceOf(db);
            var m = await db.Set<WorkspaceMembers>().IgnoreQueryFilters().SingleOrDefaultAsync(x => x.WorkspaceId == ws && x.UserId == userId, ct);
            if (m is null) return ServiceResult<MemberView>.NotFound("member");
            if (VersionMismatch(expectedVersion, m.Version)) return ServiceResult<MemberView>.Conflict(await ViewAsync(db, m, ct));
            var u = await db.Set<Users>().SingleAsync(x => x.Id == userId, ct);
            if (m.Role != role)
            {
                if (await LastHolderAsync(db, m, u, role, ct) is { } refused) return ServiceResult<MemberView>.Fail(refused);
                var before = new { m.Role, m.Version };
                m.Role = role; m.Version++;
                var mirrored = ws == WorkspaceIds.Default && u.Role != role;
                if (mirrored) { u.Role = role; u.Version++; }
                Audit(db, actor, null, "WorkspaceMember", userId, "ChangeRole", before, new { m.Role, m.Version, workspaceId = ws, accountRoleFollows = mirrored });
                await db.SaveChangesAsync(ct);
            }
            return ServiceResult<MemberView>.Ok(await ViewAsync(db, m, ct));
        }, ct);
    }

    /// <summary>
    /// Removes a person from the current workspace (If-Match is the membership's Version) and from its teams. Their account, their rows and their other
    /// workspaces stay. The last active RTE/Release Manager and the last active Governance Officer cannot be removed.
    /// </summary>
    public Task<ServiceResult<MemberView>> RemoveMemberAsync(string userId, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var ws = Memberships.WorkspaceOf(db);
            var m = await db.Set<WorkspaceMembers>().IgnoreQueryFilters().SingleOrDefaultAsync(x => x.WorkspaceId == ws && x.UserId == userId, ct);
            if (m is null) return ServiceResult<MemberView>.NotFound("member");
            if (VersionMismatch(expectedVersion, m.Version)) return ServiceResult<MemberView>.Conflict(await ViewAsync(db, m, ct));
            var u = await db.Set<Users>().SingleAsync(x => x.Id == userId, ct);
            if (await LastHolderAsync(db, m, u, null, ct) is { } refused) return ServiceResult<MemberView>.Fail(refused);
            var view = await ViewAsync(db, m, ct);
            var teams = await db.Set<TeamMembers>().Where(t => t.UserId == userId).ToListAsync(ct);   // the query filter limits these to this workspace's teams
            db.Set<TeamMembers>().RemoveRange(teams);
            db.Set<WorkspaceMembers>().Remove(m);
            Audit(db, actor, null, "WorkspaceMember", userId, "Remove", new { m.Role, m.Version, workspaceId = ws }, new { removedFromTeams = teams.Count });
            await db.SaveChangesAsync(ct);
            return ServiceResult<MemberView>.Ok(view);
        }, ct);

    /// <summary>
    /// A workspace must keep someone who can administer it and someone who can certify Compliance gates. Refuses taking the last active holder of either
    /// duty away (<paramref name="newRole"/> null = removal). A holder who is deactivated is not counted: they cannot act.
    /// </summary>
    private static async Task<GuardFailure?> LastHolderAsync(ReleaseDbContext db, WorkspaceMembers m, Users u, string? newRole, CancellationToken ct)
    {
        if (!u.IsActive) return null;
        foreach (var (held, guard, who) in new (string[] Roles, string Guard, string Who)[]
                 { (AdminRoles, LastAdmin, "RTE or Release Manager"), ([Roles.GovernanceOfficer], LastGovernance, "Governance Officer") })
        {
            if (!held.Contains(m.Role) || (newRole is not null && held.Contains(newRole))) continue;
            if (!await Memberships.ActiveWithRole(db, held).AnyAsync(x => x.Id != m.UserId, ct))
                return new GuardFailure(guard, $"{u.DisplayName} is the last {who} in this workspace. Make someone else a {who} first");
        }
        return null;
    }

    private static async Task<MemberView> ViewAsync(ReleaseDbContext db, WorkspaceMembers m, CancellationToken ct)
    {
        var u = await db.Set<Users>().AsNoTracking().SingleAsync(x => x.Id == m.UserId, ct);
        return new(u.Id, u.Email, u.DisplayName, u.Handle, u.IsActive, m.Role, m.AddedAt, m.Version);
    }

    private static ServiceResult<T> Invalid<T>(string message) => ServiceResult<T>.Fail(new GuardFailure(InvalidInput, message));
}
