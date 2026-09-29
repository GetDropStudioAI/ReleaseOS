using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

/// <summary>Users, teams and holidays admin (RTE/RM in v1, D14/D32). Roles come from sign-in (Q-003), so users are edited for handle and active state only.</summary>
public sealed class AdminService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time) : ServiceBase(dbf, time)
{
    private readonly IDbContextFactory<ReleaseDbContext> _dbf = dbf;
    public const string InvalidInput = "InvalidInput";

    public async Task<List<Users>> ListUsersAsync(CancellationToken ct = default)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        return await db.Set<Users>().AsNoTracking().OrderBy(u => u.DisplayName).ToListAsync(ct);
    }

    public async Task<List<Holidays>> ListHolidaysAsync(CancellationToken ct = default)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        return await db.Set<Holidays>().AsNoTracking().OrderBy(h => h.Day).ToListAsync(ct);
    }

    public sealed record TeamView(string Id, string Handle, string Name, int Version, string[] MemberIds);

    public async Task<List<TeamView>> ListTeamsAsync(CancellationToken ct = default)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var teams = await db.Set<Teams>().AsNoTracking().OrderBy(t => t.Handle).ToListAsync(ct);
        var members = await db.Set<TeamMembers>().AsNoTracking().ToListAsync(ct);
        return [.. teams.Select(t => new TeamView(t.Id, t.Handle, t.Name, t.Version, [.. members.Where(m => m.TeamId == t.Id).Select(m => m.UserId)]))];
    }

    public Task<ServiceResult<Users>> PatchUserAsync(string userId, string? handle, bool? isActive, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var u = await db.Set<Users>().SingleOrDefaultAsync(x => x.Id == userId, ct);
            if (u is null) return ServiceResult<Users>.NotFound("user");
            if (VersionMismatch(expectedVersion, u.Version)) return ServiceResult<Users>.Conflict(u);
            if (handle is not null && !ValidHandle(handle)) return Invalid<Users>("A handle is letters, digits, '.', '_' or '-' (optionally starting with @)");
            var before = new { u.Handle, u.IsActive };
            if (handle is not null) u.Handle = handle.Length == 0 ? null : handle.TrimStart('@');
            if (isActive is bool a) u.IsActive = a;
            u.Version++;
            Audit(db, actor, null, "User", u.Id, "Update", before, new { u.Handle, u.IsActive });
            await db.SaveChangesAsync(ct);
            return ServiceResult<Users>.Ok(u);
        }, ct);

    public Task<ServiceResult<Teams>> CreateTeamAsync(string handle, string name, IEnumerable<string> memberUserIds, Actor actor, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            if (!ValidHandle(handle) || string.IsNullOrWhiteSpace(name)) return Invalid<Teams>("A team needs a name and a handle (letters, digits, '.', '_' or '-')");
            var t = new Teams { Id = Ids.New(), Handle = handle.TrimStart('@'), Name = name.Trim() };
            db.Set<Teams>().Add(t);
            foreach (var m in memberUserIds.Distinct()) db.Set<TeamMembers>().Add(new TeamMembers { TeamId = t.Id, UserId = m });
            Audit(db, actor, null, "Team", t.Id, "Create", null, new { t.Handle, t.Name });
            await db.SaveChangesAsync(ct);
            return ServiceResult<Teams>.Ok(t);
        }, ct);

    public Task<ServiceResult<Teams>> UpdateTeamAsync(string teamId, string? name, IEnumerable<string>? memberUserIds, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var t = await db.Set<Teams>().SingleOrDefaultAsync(x => x.Id == teamId, ct);
            if (t is null) return ServiceResult<Teams>.NotFound("team");
            if (VersionMismatch(expectedVersion, t.Version)) return ServiceResult<Teams>.Conflict(t);
            if (name is not null) { if (string.IsNullOrWhiteSpace(name)) return Invalid<Teams>("A team needs a name"); t.Name = name.Trim(); }
            if (memberUserIds is not null)
            {
                var want = memberUserIds.Distinct().ToHashSet();
                var current = await db.Set<TeamMembers>().Where(m => m.TeamId == teamId).ToListAsync(ct);
                db.Set<TeamMembers>().RemoveRange(current.Where(m => !want.Contains(m.UserId)));
                foreach (var add in want.Except(current.Select(m => m.UserId))) db.Set<TeamMembers>().Add(new TeamMembers { TeamId = teamId, UserId = add });
            }
            t.Version++;
            Audit(db, actor, null, "Team", t.Id, "Update", null, new { t.Name, members = memberUserIds });
            await db.SaveChangesAsync(ct);
            return ServiceResult<Teams>.Ok(t);
        }, ct);

    public Task<ServiceResult<Holidays>> AddHolidayAsync(DateOnly day, string name, Actor actor, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            if (string.IsNullOrWhiteSpace(name)) return Invalid<Holidays>("A holiday needs a name");
            if (await db.Set<Holidays>().AnyAsync(h => h.Day == day, ct)) return Invalid<Holidays>($"{day:yyyy-MM-dd} is already a holiday");
            var h = new Holidays { Day = day, Name = name.Trim() };
            db.Set<Holidays>().Add(h);
            Audit(db, actor, null, "Holiday", day.ToString("yyyy-MM-dd"), "Add", null, new { h.Name });
            await db.SaveChangesAsync(ct);
            return ServiceResult<Holidays>.Ok(h);
        }, ct);

    public Task<ServiceResult<Holidays>> RemoveHolidayAsync(DateOnly day, Actor actor, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var h = await db.Set<Holidays>().SingleOrDefaultAsync(x => x.Day == day, ct);
            if (h is null) return ServiceResult<Holidays>.NotFound("holiday");
            db.Set<Holidays>().Remove(h);
            Audit(db, actor, null, "Holiday", day.ToString("yyyy-MM-dd"), "Remove", new { h.Name });
            await db.SaveChangesAsync(ct);
            return ServiceResult<Holidays>.Ok(h);
        }, ct);

    private static bool ValidHandle(string handle) =>
        handle.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(handle, @"^@?[A-Za-z0-9._-]{1,40}$");

    private static ServiceResult<T> Invalid<T>(string message) => ServiceResult<T>.Fail(new GuardFailure(InvalidInput, message));
}
