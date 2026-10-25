using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

public sealed record PersonRef(string Id, string Name);
public sealed record TeamRef(string Id, string Handle, string Name);
public sealed record NamedRef(string Id, string Name);
public sealed record EnvironmentRef(string Id, string Name, string Kind, string? Region);

/// <summary>A release's key, owner, team, branch, on-call, type and target environments (REOS-107). <see cref="Version"/> is the train's: the If-Match of an edit.</summary>
public sealed record ReleaseModelView(string TrainId, string? Key, PersonRef? Owner, TeamRef? OwnerTeam, string? Branch, PersonRef? OnCallUser, TeamRef? OnCallTeam,
    NamedRef? Type, IReadOnlyList<EnvironmentRef> Environments, int Version, int? BetaNumber = null);

/// <summary>An edit. Null leaves a field as it is; an empty string clears an id or the branch; an empty list clears the environments. Setting the on-call person clears the on-call
/// team and the other way round; naming both is refused.</summary>
public sealed record ReleasePatch(string? OwnerUserId = null, string? OwnerTeamId = null, string? ReleaseBranch = null, string? OnCallUserId = null, string? OnCallTeamId = null,
    string? ReleaseTypeId = null, IReadOnlyList<string>? EnvironmentIds = null, int? BetaNumber = null);

/// <summary>The fields a new release can carry from creation (optional everywhere, so the existing create flows work unchanged).</summary>
public sealed record NewReleaseFields(string? OwnerUserId = null, string? OwnerTeamId = null, string? ReleaseBranch = null, string? OnCallUserId = null, string? OnCallTeamId = null,
    string? ReleaseTypeId = null, IReadOnlyList<string>? EnvironmentIds = null, int? BetaNumber = null);

/// <summary>Allocates release keys from the workspace's counter (REOS-107). The counter only goes up, so a key is never reused, even after an archive.</summary>
public static class ReleaseKeyAllocator
{
    /// <summary>The next key of the context's workspace. Run inside the write transaction: the counter update takes the write lock first.</summary>
    public static async Task<string> AllocateAsync(ReleaseDbContext db, CancellationToken ct = default)
    {
        var ws = Memberships.WorkspaceOf(db);
        var n = await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Workspaces SET NextKeyNumber = NextKeyNumber + 1 WHERE Id = {ws}", ct);
        if (n != 1) throw new InvalidOperationException($"Workspace {ws} not found while allocating a release key");
        return await db.Database.SqlQuery<string>($"SELECT KeyPrefix || '-' || (NextKeyNumber - 1) AS Value FROM Workspaces WHERE Id = {ws}").SingleAsync(ct);
    }
}

/// <summary>
/// The release model (REOS-107, B3): owner, owning team, release branch, on-call, release type and target environments on a train, plus the train's
/// key. Every edit is one transaction with one <c>ReleaseTrain</c>/<c>ReleaseFields</c> audit row, stamps the train's Version, LastChangedBy and LastChangedAt, and
/// checks If-Match (409 with the current row). The key never changes (trg_Train_KeyImmutable); the key prefix is the workspace's setting (<see cref="WorkspaceService"/>).
/// </summary>
public sealed class ReleaseModelService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IRealtimePublisher? realtime = null) : ServiceBase(dbf, time, realtime)
{
    private static GuardFailure Bad(string m) => new(ReleaseModelGuards.ReleaseInvalid, m);

    public async Task<ReleaseModelView?> GetAsync(string trainId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var t = await db.Set<ReleaseTrains>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == trainId, ct);
        return t is null ? null : await ViewAsync(db, t, ct);
    }

    public static async Task<ReleaseModelView> ViewAsync(ReleaseDbContext db, ReleaseTrains t, CancellationToken ct)
    {
        var userIds = new[] { t.OwnerUserId, t.OnCallUserId }.OfType<string>().Distinct().ToList();
        var teamIds = new[] { t.OwnerTeamId, t.OnCallTeamId }.OfType<string>().Distinct().ToList();
        var people = await db.Set<Users>().AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var teams = await db.Set<Teams>().AsNoTracking().Where(x => teamIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x, ct);
        var type = t.ReleaseTypeId is null ? null : await db.Set<ReleaseTypes>().AsNoTracking().Where(x => x.Id == t.ReleaseTypeId).Select(x => new NamedRef(x.Id, x.Name)).SingleOrDefaultAsync(ct);
        var envs = await (from te in db.Set<TrainEnvironments>().AsNoTracking()
                          join e in db.Set<Environments>().AsNoTracking() on te.EnvironmentId equals e.Id
                          where te.ReleaseTrainId == t.Id
                          orderby e.SortOrder, e.Name
                          select new EnvironmentRef(e.Id, e.Name, e.Kind, e.Region)).ToListAsync(ct);
        PersonRef? P(string? id) => id is not null && people.TryGetValue(id, out var n) ? new(id, n) : null;
        TeamRef? T(string? id) => id is not null && teams.TryGetValue(id, out var x) ? new(x.Id, x.Handle, x.Name) : null;
        return new(t.Id, t.ReleaseKey, P(t.OwnerUserId), T(t.OwnerTeamId), t.ReleaseBranch, P(t.OnCallUserId), T(t.OnCallTeamId), type, envs, t.Version, t.BetaNumber);
    }

    /// <summary>Checks a set of release fields against the workspace (shared by creation, cloning and edits). Returns the failures, empty when fine.
    /// RT1 (D46, D48): <paramref name="requireType"/> asks for a type whenever the workspace has any (creation, a clone of a typed release, an edit that clears it);
    /// <paramref name="checkBeta"/> checks the Beta number against the type (creation, and an edit of the type or the number).</summary>
    public static async Task<List<GuardFailure>> ValidateAsync(ReleaseDbContext db, string? ownerUserId, string? ownerTeamId, string? branch, string? onCallUserId, string? onCallTeamId,
        string? releaseTypeId, IReadOnlyList<string>? environmentIds, CancellationToken ct, string? keepType = null, IReadOnlyCollection<string>? keepEnvironments = null,
        int? betaNumber = null, bool requireType = false, bool checkBeta = false)
    {
        var f = new List<GuardFailure>();
        if (ownerUserId is not null && !await Memberships.Members(db).AnyAsync(u => u.Id == ownerUserId && u.IsActive, ct)) f.Add(Bad("The owner must be an active member of this workspace"));
        if (onCallUserId is not null && !await Memberships.Members(db).AnyAsync(u => u.Id == onCallUserId && u.IsActive, ct)) f.Add(Bad("The on-call person must be an active member of this workspace"));
        if (ownerTeamId is not null && !await db.Set<Teams>().AnyAsync(x => x.Id == ownerTeamId, ct)) f.Add(Bad("The owning team does not exist in this workspace"));
        if (onCallTeamId is not null && !await db.Set<Teams>().AnyAsync(x => x.Id == onCallTeamId, ct)) f.Add(Bad("The on-call team does not exist in this workspace"));
        if (onCallUserId is not null && onCallTeamId is not null) f.Add(Bad("On call is a person or a team, not both"));
        if (branch is not null && (branch.Trim().Length is 0 or > ReleaseModelLimits.MaxBranch)) f.Add(Bad($"The release branch is 1 to {ReleaseModelLimits.MaxBranch} characters"));
        ReleaseTypes? type = null;
        if (releaseTypeId is null)
        {
            if (requireType && await db.Set<ReleaseTypes>().AnyAsync(ct))
                f.Add(new(ReleaseModelGuards.ReleaseTypeRequired, "Choose a release type: every release has one (Beta, CERT, PenTest Cert, Production, or a type added under Teams & settings)"));
        }
        else
        {
            type = await db.Set<ReleaseTypes>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == releaseTypeId, ct);
            if (type is null) f.Add(new(ReleaseModelGuards.ReleaseTypeInvalid, "The release type does not exist in this workspace"));
            else if (type.ArchivedAt is not null && releaseTypeId != keepType) f.Add(new(ReleaseModelGuards.ReleaseTypeInvalid, $"The release type \"{type.Name}\" is archived"));
        }
        if (checkBeta && (type is not null || releaseTypeId is null)) f.AddRange(CheckBetaNumber(type, betaNumber));
        if (environmentIds is not null)
        {
            if (environmentIds.Count != environmentIds.Distinct().Count()) f.Add(Bad("An environment is listed twice"));
            var found = await db.Set<Environments>().AsNoTracking().Where(e => environmentIds.Contains(e.Id)).ToListAsync(ct);
            foreach (var id in environmentIds.Distinct())
            {
                var e = found.SingleOrDefault(x => x.Id == id);
                if (e is null) f.Add(new(ReleaseModelGuards.EnvironmentInvalid, "A target environment does not exist in this workspace", [id]));
                else if (e.ArchivedAt is not null && keepEnvironments?.Contains(id) != true) f.Add(new(ReleaseModelGuards.EnvironmentInvalid, $"The environment \"{e.Name}\" is archived", [id]));
            }
        }
        return f;
    }

    /// <summary>RT1 (D48): a Beta type needs a number from 1 to 3; any other type, or none, takes no number.</summary>
    public static IEnumerable<GuardFailure> CheckBetaNumber(ReleaseTypes? type, int? number)
    {
        if (type is { IsBeta: true })
        {
            if (number is null) yield return new(ReleaseModelGuards.BetaNumberInvalid, $"A {type.Name} release needs its number, {ReleaseModelLimits.MinBetaNumber} to {ReleaseModelLimits.MaxBetaNumber}");
            else if (number is < ReleaseModelLimits.MinBetaNumber or > ReleaseModelLimits.MaxBetaNumber) yield return new(ReleaseModelGuards.BetaNumberInvalid, $"The number is {ReleaseModelLimits.MinBetaNumber} to {ReleaseModelLimits.MaxBetaNumber}");
        }
        else if (number is not null) yield return new(ReleaseModelGuards.BetaNumberInvalid, type is null ? "A number goes with a Beta release type" : $"Only a Beta release carries a number; \"{type.Name}\" does not");
    }

    /// <summary>Sets the fields on a train being created (creation and clone). The caller validated them with <see cref="ValidateAsync"/>.</summary>
    public static void Apply(ReleaseTrains train, NewReleaseFields n)
    {
        train.OwnerUserId = n.OwnerUserId; train.OwnerTeamId = n.OwnerTeamId; train.ReleaseBranch = n.ReleaseBranch?.Trim(); train.ReleaseTypeId = n.ReleaseTypeId; train.BetaNumber = n.BetaNumber;
        train.OnCallUserId = n.OnCallUserId; train.OnCallTeamId = n.OnCallTeamId;
    }

    public static void AddEnvironments(ReleaseDbContext db, string trainId, IEnumerable<string>? environmentIds)
    {
        foreach (var id in (environmentIds ?? []).Distinct()) db.Set<TrainEnvironments>().Add(new TrainEnvironments { ReleaseTrainId = trainId, EnvironmentId = id });
    }

    public static object Snapshot(ReleaseTrains t, IEnumerable<string> environmentIds) => new
    {
        key = t.ReleaseKey, ownerUserId = t.OwnerUserId, ownerTeamId = t.OwnerTeamId, branch = t.ReleaseBranch, onCallUserId = t.OnCallUserId, onCallTeamId = t.OnCallTeamId,
        releaseTypeId = t.ReleaseTypeId, betaNumber = t.BetaNumber, releaseVersionId = t.ReleaseVersionId, environmentIds = environmentIds.Order(StringComparer.Ordinal).ToArray(),
    };

    public Task<ServiceResult<ReleaseModelView>> UpdateAsync(string trainId, ReleasePatch p, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var t = await db.Set<ReleaseTrains>().SingleOrDefaultAsync(x => x.Id == trainId, ct);
            if (t is null) return ServiceResult<ReleaseModelView>.NotFound("train");
            if (VersionMismatch(expectedVersion, t.Version)) return ServiceResult<ReleaseModelView>.Conflict(t);
            if (p.OnCallUserId is { Length: > 0 } && p.OnCallTeamId is { Length: > 0 }) return ServiceResult<ReleaseModelView>.Fail(Bad("On call is a person or a team, not both"));

            var currentEnvs = await db.Set<TrainEnvironments>().Where(e => e.ReleaseTrainId == trainId).ToListAsync(ct);
            var before = Snapshot(t, currentEnvs.Select(e => e.EnvironmentId));

            static string? Pick(string? patch, string? current) => patch is null ? current : patch.Length == 0 ? null : patch;
            var owner = Pick(p.OwnerUserId, t.OwnerUserId); var ownerTeam = Pick(p.OwnerTeamId, t.OwnerTeamId);
            var branch = p.ReleaseBranch is null ? t.ReleaseBranch : p.ReleaseBranch.Trim().Length == 0 ? null : p.ReleaseBranch.Trim();
            var type = Pick(p.ReleaseTypeId, t.ReleaseTypeId);
            string? onUser = t.OnCallUserId, onTeam = t.OnCallTeamId;
            if (p.OnCallUserId is not null) { onUser = Pick(p.OnCallUserId, null); if (onUser is not null) onTeam = null; }
            if (p.OnCallTeamId is not null) { onTeam = Pick(p.OnCallTeamId, null); if (onTeam is not null) onUser = null; }
            var wantEnvs = p.EnvironmentIds is null ? null : p.EnvironmentIds.Distinct().ToList();

            // RT1 (D48): changing to a type that is not a Beta type drops the number; changing to a Beta type needs one in this patch.
            var typeChanged = type != t.ReleaseTypeId;
            var newType = type is null ? null : await db.Set<ReleaseTypes>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == type, ct);
            var number = typeChanged ? (newType is { IsBeta: true } ? p.BetaNumber : null) : p.BetaNumber ?? t.BetaNumber;
            var numberChanged = number != t.BetaNumber;

            // Only what changed is checked: a person who left the workspace, or an environment archived since, does not block an unrelated edit.
            var f = await ValidateAsync(db, owner == t.OwnerUserId ? null : owner, ownerTeam == t.OwnerTeamId ? null : ownerTeam, branch == t.ReleaseBranch ? null : branch,
                onUser == t.OnCallUserId ? null : onUser, onTeam == t.OnCallTeamId ? null : onTeam, type, wantEnvs, ct, keepType: t.ReleaseTypeId,
                keepEnvironments: currentEnvs.Select(e => e.EnvironmentId).ToHashSet(), betaNumber: number, requireType: p.ReleaseTypeId is { Length: 0 }, checkBeta: typeChanged || numberChanged || p.BetaNumber is not null);
            if (f.Count > 0) return ServiceResult<ReleaseModelView>.Fail(f);

            if (t.ReleaseVersionId is not null && type is not null && (typeChanged || numberChanged) && t.CurrentStatus != "Aborted")
            {
                var slot = await ReleaseVersionService.CheckSlotAsync(db, t.ReleaseVersionId, type, number, t.Id, ct);   // RT2: a release in a version keeps a free place of its own
                if (slot.Count > 0) return ServiceResult<ReleaseModelView>.Fail(slot);
            }

            var envChanged = wantEnvs is not null && !wantEnvs.ToHashSet().SetEquals(currentEnvs.Select(e => e.EnvironmentId));
            if (owner == t.OwnerUserId && ownerTeam == t.OwnerTeamId && branch == t.ReleaseBranch && type == t.ReleaseTypeId && !numberChanged && onUser == t.OnCallUserId && onTeam == t.OnCallTeamId && !envChanged)
                return ServiceResult<ReleaseModelView>.Ok(await ViewAsync(db, t, ct));   // nothing to change: no Version bump, no audit row

            t.OwnerUserId = owner; t.OwnerTeamId = ownerTeam; t.ReleaseBranch = branch; t.ReleaseTypeId = type; t.BetaNumber = number; t.OnCallUserId = onUser; t.OnCallTeamId = onTeam;
            if (envChanged)
            {
                var want = wantEnvs!.ToHashSet();
                db.Set<TrainEnvironments>().RemoveRange(currentEnvs.Where(e => !want.Contains(e.EnvironmentId)));
                AddEnvironments(db, trainId, want.Except(currentEnvs.Select(e => e.EnvironmentId)));
            }
            var now = Now;
            t.Version++; t.UpdatedAt = now; t.LastChangedByUserId = actor.UserId; t.LastChangedAt = now;
            Audit(db, actor, trainId, "ReleaseTrain", trainId, "ReleaseFields", before, Snapshot(t, wantEnvs ?? currentEnvs.Select(e => e.EnvironmentId).ToList()));
            await db.SaveChangesAsync(ct);
            return ServiceResult<ReleaseModelView>.Ok(await ViewAsync(db, t, ct));
        }, ct);
}
