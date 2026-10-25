using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

/// <summary>One release in a version's chain. <see cref="Position"/> is 1-based; <see cref="Target"/> is the release's target date.</summary>
public sealed record ChainItem(string TrainId, string? Key, string Title, string Label, string TypeId, string TypeName, int? BetaNumber, string Status, string Target, int Position);

/// <summary>A version and its chain, in order, with the advisories (a later release planned too soon after the one before it).</summary>
public sealed record VersionView(string Id, string Name, bool Archived, int Version, string CreatedAt, IReadOnlyList<ChainItem> Chain, IReadOnlyList<ChainWarning> Warnings);

/// <summary>What the release page shows: the version this release is in (null when none), where the chain can go on from here, and the release's own Version (the If-Match of linking it).</summary>
public sealed record TrainVersionView(string TrainId, VersionView? Version, IReadOnlyList<VersionChain.NextOption> Next, int TrainVersion, string DefaultTargetDate);

/// <summary>Null leaves a field. Everything optional: the type and number default to the first option after this release, the title to "version name + label", the date to this release's date plus <c>Versions:NextOffsetDays</c>.</summary>
public sealed record NextReleaseInput(string? ReleaseTypeId = null, int? BetaNumber = null, string? Title = null, string? TargetReleaseDate = null);

/// <summary>
/// RT2 (D48): versions. A version groups the releases of one product version in a chain (Beta 1 to 3, CERT, PenTest Cert, Production: <see cref="VersionChain"/>). Each release keeps its own
/// gates, dates and runbook; the version only links them. Create, rename, archive, a release joining or leaving a version, and "create the next release" from the one before it
/// (its type's default template, via <see cref="TrainCreationService"/>). Every write is one transaction with its audit row, stamps Version and honours If-Match.
/// A slip shows as an advisory against the later release (<see cref="VersionChain.Warnings"/>, <c>Versions:MinGapDays</c>, default 1): it never blocks a move.
/// </summary>
public sealed class ReleaseVersionService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, TrainCreationService creation, IConfiguration? config = null, IRealtimePublisher? realtime = null)
    : ServiceBase(dbf, time, realtime)
{
    private const string Entity = "ReleaseVersion";
    public int MinGapDays { get; } = AdvisoryService.Setting(config, "Versions:MinGapDays", VersionChain.DefaultMinGapDays, 0, 365);
    public int NextOffsetDays { get; } = AdvisoryService.Setting(config, "Versions:NextOffsetDays", 7, 0, 365);

    private static GuardFailure Bad(string m) => new(ReleaseModelGuards.VersionInvalid, m);

    // ---- reads
    public async Task<List<VersionView>> ListAsync(bool includeArchived = false, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var rows = await db.Set<ReleaseVersions>().AsNoTracking().Where(x => includeArchived || x.ArchivedAt == null).OrderBy(x => x.Name).ToListAsync(ct);
        return await ViewsAsync(db, rows, MinGapDays, ct);
    }

    public async Task<VersionView?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var row = await db.Set<ReleaseVersions>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        return row is null ? null : (await ViewsAsync(db, [row], MinGapDays, ct))[0];
    }

    /// <summary>Null for an unknown train.</summary>
    public async Task<TrainVersionView?> ForTrainAsync(string trainId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        return await ForTrainAsync(db, trainId, ct);
    }

    private async Task<TrainVersionView?> ForTrainAsync(ReleaseDbContext db, string trainId, CancellationToken ct)
    {
        var t = await db.Set<ReleaseTrains>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == trainId, ct);
        if (t is null) return null;
        var defaultDate = t.TargetReleaseDate.AddDays(NextOffsetDays).ToString("yyyy-MM-dd");
        if (t.ReleaseVersionId is null) return new(t.Id, null, [], t.Version, defaultDate);
        var row = await db.Set<ReleaseVersions>().AsNoTracking().SingleAsync(x => x.Id == t.ReleaseVersionId, ct);
        var view = (await ViewsAsync(db, [row], MinGapDays, ct))[0];
        var chain = (await VersionChains.LoadAsync(db, [row.Id], ct)).GetValueOrDefault(row.Id) ?? [];
        var types = await db.Set<ReleaseTypes>().AsNoTracking().Select(x => new VersionChain.TypeInfo(x.Id, x.Name, x.SortOrder, x.IsBeta, x.ArchivedAt != null)).ToListAsync(ct);
        var me = chain.SingleOrDefault(r => r.TrainId == trainId);
        // An archived version takes no new release; an aborted or typeless release has no place to go on from.
        var next = row.ArchivedAt is not null || me is null || me.Status == "Aborted" ? [] : VersionChain.NextOptions(me, chain, types);
        return new(t.Id, view, next, t.Version, defaultDate);
    }

    internal static async Task<List<VersionView>> ViewsAsync(ReleaseDbContext db, List<ReleaseVersions> rows, int minGapDays, CancellationToken ct)
    {
        var chains = await VersionChains.LoadAsync(db, rows.Select(r => r.Id).ToList(), ct);
        return [.. rows.Select(r =>
        {
            var chain = chains.GetValueOrDefault(r.Id) ?? [];
            var ordered = VersionChain.Order(chain);
            return new VersionView(r.Id, r.Name, r.ArchivedAt is not null, r.Version, Stamp(r.CreatedAt),
                [.. ordered.Select((c, i) => new ChainItem(c.TrainId, c.Key, c.Title, VersionChain.Label(c), c.TypeId, c.TypeName, c.BetaNumber, c.Status, c.Target.ToString("yyyy-MM-dd"), i + 1))],
                VersionChain.Warnings(chain, minGapDays));
        })];
    }

    private static string Stamp(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    // ---- writes
    private static object Snapshot(ReleaseVersions x) => new { x.Name, archived = x.ArchivedAt is not null, x.Version };

    private static async Task<List<GuardFailure>> CheckNameAsync(ReleaseDbContext db, string name, string? exceptId, CancellationToken ct)
    {
        var f = new List<GuardFailure>();
        if (name.Length is 0 or > ReleaseModelLimits.MaxVersionName) f.Add(Bad($"A version name is 1 to {ReleaseModelLimits.MaxVersionName} characters"));
        else if (await db.Set<ReleaseVersions>().AnyAsync(x => x.Name == name && x.Id != exceptId, ct)) f.Add(new(ReleaseModelGuards.VersionNameTaken, $"A version called \"{name}\" exists"));   // Name is NOCASE
        return f;
    }

    public Task<ServiceResult<ReleaseVersions>> CreateAsync(string? name, Actor actor, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var n = (name ?? "").Trim();
            var f = await CheckNameAsync(db, n, null, ct);
            if (f.Count > 0) return ServiceResult<ReleaseVersions>.Fail(f);
            var row = new ReleaseVersions { Id = Ids.New(), Name = n, CreatedAt = Now };
            db.Set<ReleaseVersions>().Add(row);
            Audit(db, actor, null, Entity, row.Id, "Create", null, Snapshot(row));
            await db.SaveChangesAsync(ct);
            return ServiceResult<ReleaseVersions>.Ok(row);
        }, ct);

    public Task<ServiceResult<ReleaseVersions>> RenameAsync(string id, string? name, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var row = await db.Set<ReleaseVersions>().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (row is null) return ServiceResult<ReleaseVersions>.NotFound("version");
            if (VersionMismatch(expectedVersion, row.Version)) return ServiceResult<ReleaseVersions>.Conflict(row);
            var n = (name ?? "").Trim();
            if (n == row.Name) return ServiceResult<ReleaseVersions>.Ok(row);
            var f = await CheckNameAsync(db, n, id, ct);
            if (f.Count > 0) return ServiceResult<ReleaseVersions>.Fail(f);
            var before = Snapshot(row);
            row.Name = n; row.Version++;
            Audit(db, actor, null, Entity, row.Id, "Update", before, Snapshot(row));
            await TouchChainAsync(db, id, ct);
            await db.SaveChangesAsync(ct);
            return ServiceResult<ReleaseVersions>.Ok(row);
        }, ct);

    public Task<ServiceResult<ReleaseVersions>> SetArchivedAsync(string id, bool archived, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var row = await db.Set<ReleaseVersions>().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (row is null) return ServiceResult<ReleaseVersions>.NotFound("version");
            if (VersionMismatch(expectedVersion, row.Version)) return ServiceResult<ReleaseVersions>.Conflict(row);
            if ((row.ArchivedAt is not null) == archived) return ServiceResult<ReleaseVersions>.Ok(row);   // repeat is a no-op
            var before = Snapshot(row);
            row.ArchivedAt = archived ? Now : null; row.Version++;
            Audit(db, actor, null, Entity, row.Id, archived ? "Archive" : "Unarchive", before, Snapshot(row));
            await db.SaveChangesAsync(ct);
            return ServiceResult<ReleaseVersions>.Ok(row);
        }, ct);

    /// <summary>A release's own row is not what changes when a sibling moves; this hints live clients to refetch the other releases of the chain.</summary>
    internal static async Task TouchChainAsync(ReleaseDbContext db, string versionId, CancellationToken ct)
    {
        foreach (var id in await db.Set<ReleaseTrains>().AsNoTracking().Where(t => t.ReleaseVersionId == versionId).Select(t => t.Id).ToListAsync(ct)) Touch(id);
    }

    /// <summary>Puts a release into a version, moves it to another, or (null) takes it out. The release must have a type, its place in the version must be free, and an archived version takes no release.</summary>
    public Task<ServiceResult<TrainVersionView>> SetTrainVersionAsync(string trainId, string? versionId, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var t = await db.Set<ReleaseTrains>().SingleOrDefaultAsync(x => x.Id == trainId, ct);
            if (t is null) return ServiceResult<TrainVersionView>.NotFound("train");
            if (VersionMismatch(expectedVersion, t.Version)) return ServiceResult<TrainVersionView>.Conflict(t);
            versionId = string.IsNullOrWhiteSpace(versionId) ? null : versionId.Trim();
            if (versionId == t.ReleaseVersionId) return ServiceResult<TrainVersionView>.Ok((await ForTrainAsync(db, trainId, ct))!);
            ReleaseVersions? target = null;
            if (versionId is not null)
            {
                target = await db.Set<ReleaseVersions>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == versionId, ct);
                var f = await CheckJoinAsync(db, t, target, ct);
                if (f.Count > 0) return ServiceResult<TrainVersionView>.Fail(f);
            }
            var old = t.ReleaseVersionId;
            var oldName = old is null ? null : await db.Set<ReleaseVersions>().AsNoTracking().Where(x => x.Id == old).Select(x => x.Name).SingleOrDefaultAsync(ct);
            t.ReleaseVersionId = versionId;
            var now = Now;
            t.Version++; t.UpdatedAt = now; t.LastChangedByUserId = actor.UserId; t.LastChangedAt = now;
            Audit(db, actor, trainId, "ReleaseTrain", trainId, versionId is null ? "VersionUnlink" : "VersionLink",
                new { releaseVersionId = old, version = oldName }, new { releaseVersionId = versionId, version = target?.Name });
            if (old is not null) await TouchChainAsync(db, old, ct);
            await db.SaveChangesAsync(ct);   // the release first: the chain below reads it
            if (versionId is not null) await TouchChainAsync(db, versionId, ct);
            return ServiceResult<TrainVersionView>.Ok((await ForTrainAsync(db, trainId, ct))!);
        }, ct);

    /// <summary>The rules for a release to take a place in a version (join, create-next). Empty when it may.</summary>
    internal static async Task<List<GuardFailure>> CheckJoinAsync(ReleaseDbContext db, ReleaseTrains t, ReleaseVersions? version, CancellationToken ct)
    {
        var f = new List<GuardFailure>();
        if (version is null) { f.Add(Bad("The version does not exist in this workspace")); return f; }
        if (version.ArchivedAt is not null) f.Add(Bad($"The version \"{version.Name}\" is archived"));
        if (t.ReleaseTypeId is null) f.Add(Bad("Give the release a type before it joins a version: a version orders its releases by type"));
        else if (t.CurrentStatus == "Aborted") f.Add(Bad("An aborted release cannot join a version"));
        else f.AddRange(await CheckSlotAsync(db, version.Id, t.ReleaseTypeId, t.BetaNumber, t.Id, ct));
        return f;
    }

    /// <summary>A version has one live release for each type (and Beta number); the schema's UX_Trains_VersionSlot holds the same rule.</summary>
    internal static async Task<List<GuardFailure>> CheckSlotAsync(ReleaseDbContext db, string versionId, string typeId, int? betaNumber, string? exceptTrainId, CancellationToken ct)
    {
        var taken = await (from t in db.Set<ReleaseTrains>().AsNoTracking()
                           join ty in db.Set<ReleaseTypes>().AsNoTracking() on t.ReleaseTypeId equals ty.Id
                           where t.ReleaseVersionId == versionId && t.ReleaseTypeId == typeId && t.BetaNumber == betaNumber && t.Id != exceptTrainId && t.CurrentStatus != "Aborted"
                           select new { t.Title, ty.Name }).FirstOrDefaultAsync(ct);
        return taken is null ? [] : [new(ReleaseModelGuards.VersionSlotTaken, $"The version already has a {VersionChain.Label(taken.Name, betaNumber)} release (\"{taken.Title}\"); a version has one release for each type")];
    }

    /// <summary>
    /// Creates the next release of the chain from <paramref name="trainId"/>: in the same version, of the type (and Beta number) chosen or the first one after this release, starting from that
    /// type's default template (<see cref="TrainCreationService"/>: gates, runbook steps when a window is given, the T-minus plan). It carries over from this release what a version's releases
    /// share: risk tier, owner, team, branch, on-call, target environments and bundled products. Dates, window, gates and runbook are the new release's own.
    /// </summary>
    public async Task<ServiceResult<CreatedTrain>> CreateNextAsync(string trainId, NextReleaseInput input, Actor actor, CancellationToken ct = default)
    {
        NewTrainInput create;
        await using (var db = await OpenAsync(ct))
        {
            var t = await db.Set<ReleaseTrains>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == trainId, ct);
            if (t is null) return ServiceResult<CreatedTrain>.NotFound("train");
            if (t.ReleaseVersionId is null) return ServiceResult<CreatedTrain>.Fail(Bad("This release is not in a version: put it in one first"));
            var v = await ForTrainAsync(db, trainId, ct);
            var version = v!.Version!;
            var typeId = input.ReleaseTypeId;
            int? beta = input.BetaNumber;
            if (string.IsNullOrWhiteSpace(typeId))
            {
                if (v.Next.Count == 0) return ServiceResult<CreatedTrain>.Fail(Bad(version.Archived ? $"The version \"{version.Name}\" is archived" : "The chain has no place left after this release"));
                typeId = v.Next[0].TypeId; beta = input.BetaNumber ?? v.Next[0].BetaNumber;
            }
            var type = await db.Set<ReleaseTypes>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == typeId, ct);
            if (type is null) return ServiceResult<CreatedTrain>.Fail(new GuardFailure(ReleaseModelGuards.ReleaseTypeInvalid, "The release type does not exist in this workspace"));
            var label = VersionChain.Label(type.Name, beta);
            var envIds = await db.Set<TrainEnvironments>().AsNoTracking().Where(e => e.ReleaseTrainId == trainId).Select(e => e.EnvironmentId).ToListAsync(ct);
            var bundled = await db.Set<BundledProducts>().AsNoTracking().Where(p => p.ReleaseTrainId == trainId).OrderBy(p => p.ProductName).ToListAsync(ct);
            var serviceKeys = await db.Set<CatalogServices>().AsNoTracking().Where(c => bundled.Select(p => p.CatalogServiceId).Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.ServiceKey, ct);
            var products = bundled.Select(p => new NewProductInput(p.ProductName, p.VersionTag, p.ProjectCode, p.CatalogServiceId is { } sid ? serviceKeys.GetValueOrDefault(sid) : null)).ToList();
            create = new NewTrainInput(
                Title: string.IsNullOrWhiteSpace(input.Title) ? $"{version.Name} {label}" : input.Title, TargetReleaseDate: string.IsNullOrWhiteSpace(input.TargetReleaseDate) ? v.DefaultTargetDate : input.TargetReleaseDate,
                RiskTier: t.RiskTier, Products: products, OwnerUserId: t.OwnerUserId, OwnerTeamId: t.OwnerTeamId, ReleaseBranch: t.ReleaseBranch, OnCallUserId: t.OnCallUserId, OnCallTeamId: t.OnCallTeamId,
                ReleaseTypeId: typeId, EnvironmentIds: envIds, BetaNumber: beta, ReleaseVersionId: t.ReleaseVersionId);
        }
        return await creation.CreateAsync(create, actor, ct);
    }
}

/// <summary>One step of a chain as the calendar and the overview draw it.</summary>
public sealed record ChainStep(string TrainId, string Label, string Status, string Target);

/// <summary>A release's place in its version, compact: the version, this release's label and position, the whole chain in order, and the advisory against this release (null when none).</summary>
public sealed record ChainBrief(string VersionId, string VersionName, string Label, int Position, int Count, IReadOnlyList<ChainStep> Steps, string? Warning);

/// <summary>Loads the releases of versions as <see cref="ChainRelease"/>s (shared by the version service, the advisories, the calendar and the overview).</summary>
public static class VersionChains
{
    /// <summary>The brief of every release that is in a version, keyed by train id. One query for the chains, one for the version names.</summary>
    public static async Task<Dictionary<string, ChainBrief>> BriefsAsync(ReleaseDbContext db, int minGapDays, CancellationToken ct = default)
    {
        var chains = await LoadAsync(db, null, ct);
        if (chains.Count == 0) return [];
        var names = await db.Set<ReleaseVersions>().AsNoTracking().ToDictionaryAsync(v => v.Id, v => v.Name, ct);
        var result = new Dictionary<string, ChainBrief>();
        foreach (var (versionId, releases) in chains)
        {
            if (!names.TryGetValue(versionId, out var name)) continue;
            var ordered = VersionChain.Order(releases);
            var steps = ordered.Select(r => new ChainStep(r.TrainId, VersionChain.Label(r), r.Status, r.Target.ToString("yyyy-MM-dd"))).ToList();
            var warnings = VersionChain.Warnings(releases, minGapDays).ToDictionary(w => w.TrainId, w => w.Message);
            for (var i = 0; i < ordered.Count; i++)
                result[ordered[i].TrainId] = new ChainBrief(versionId, name, steps[i].Label, i + 1, ordered.Count, steps, warnings.GetValueOrDefault(ordered[i].TrainId));
        }
        return result;
    }

    public static async Task<Dictionary<string, List<ChainRelease>>> LoadAsync(ReleaseDbContext db, IReadOnlyCollection<string>? versionIds, CancellationToken ct = default)
    {
        var rows = await (from t in db.Set<ReleaseTrains>().AsNoTracking()
                          join ty in db.Set<ReleaseTypes>().AsNoTracking() on t.ReleaseTypeId equals ty.Id
                          where t.ReleaseVersionId != null && (versionIds == null || versionIds.Contains(t.ReleaseVersionId))
                          select new { VersionId = t.ReleaseVersionId!, t.Id, t.Title, t.ReleaseKey, TypeId = ty.Id, TypeName = ty.Name, ty.SortOrder, t.BetaNumber, t.CurrentStatus, t.TargetReleaseDate }).ToListAsync(ct);
        return rows.GroupBy(r => r.VersionId).ToDictionary(g => g.Key,
            g => g.Select(r => new ChainRelease(r.Id, r.Title, r.ReleaseKey, r.TypeId, r.TypeName, r.SortOrder, r.BetaNumber, r.CurrentStatus, r.TargetReleaseDate)).ToList());
    }
}
