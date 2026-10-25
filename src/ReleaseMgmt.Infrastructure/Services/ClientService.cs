using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

public sealed record ClientView(string Id, string Name, string Segment, NamedRef? CertEnvironment, bool Archived, int Version, string? LastChangedAt = null);
public sealed record NewClient(string? Name, string? Segment, string? CertEnvironmentId = null);
/// <summary>Null leaves a field. <c>CertEnvironmentId = ""</c> clears the environment. Making a client Standard clears its environment too.</summary>
public sealed record ClientPatch(string? Name = null, string? Segment = null, string? CertEnvironmentId = null);

/// <summary>
/// RT3 (D47): the clients of a workspace, each Standard or On-Demand. An On-Demand client names its CERT environment (one of the workspace's); a Standard client has none.
/// Planners create, edit and archive. Every write is one transaction with one <c>Client</c> audit row and a Version bump (If-Match on edits). The service gives a readable
/// <c>ClientInvalid</c> 422 for a segment and environment that do not go together; <c>trg_Client_SegmentRules_*</c> hold the same rule as the backstop. An archived client stays
/// in the list when asked for; archiving never deletes a row (RT4 will hang per-client dates on clients).
/// </summary>
public sealed class ClientService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IRealtimePublisher? realtime = null) : ServiceBase(dbf, time, realtime)
{
    private const string Entity = "Client";

    private static GuardFailure Bad(string m) => new(ReleaseModelGuards.ClientInvalid, m);

    public async Task<List<ClientView>> ListAsync(bool includeArchived = false, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var rows = await db.Set<Clients>().AsNoTracking().Where(c => includeArchived || c.ArchivedAt == null).ToListAsync(ct);
        return await ViewsAsync(db, [.. rows.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id, StringComparer.Ordinal)], ct);
    }

    public async Task<ClientView?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var row = await db.Set<Clients>().AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct);
        return row is null ? null : (await ViewsAsync(db, [row], ct))[0];
    }

    private static async Task<List<ClientView>> ViewsAsync(ReleaseDbContext db, List<Clients> rows, CancellationToken ct)
    {
        var ids = rows.Select(r => r.CertEnvironmentId).OfType<string>().Distinct().ToList();
        var envs = await db.Set<Environments>().AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.Name, ct);
        return [.. rows.Select(c => new ClientView(c.Id, c.Name, c.Segment, c.CertEnvironmentId is not null && envs.TryGetValue(c.CertEnvironmentId, out var n) ? new(c.CertEnvironmentId, n) : null,
            c.ArchivedAt is not null, c.Version, c.LastChangedAt?.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")))];
    }

    private static object Snapshot(Clients c) => new { c.Name, c.Segment, c.CertEnvironmentId, archived = c.ArchivedAt is not null, c.Version };

    /// <summary>The rules a client has to meet, in the order a person fixes them. <paramref name="envChanged"/> is false when the environment is the one the client already has (an archived one stays).</summary>
    private static async Task<List<GuardFailure>> CheckAsync(ReleaseDbContext db, string name, string segment, string? certEnvironmentId, bool envChanged, string? exceptId, CancellationToken ct)
    {
        var f = new List<GuardFailure>();
        if (name.Length is 0 or > ReleaseModelLimits.MaxClientName) f.Add(Bad($"A client name is 1 to {ReleaseModelLimits.MaxClientName} characters"));
        else if (await db.Set<Clients>().AnyAsync(c => c.Name == name && c.Id != exceptId, ct)) f.Add(new(ReleaseModelGuards.ClientNameTaken, $"A client called \"{name}\" exists"));
        if (!ClientSegments.All.Contains(segment)) { f.Add(Bad("A client's segment is Standard or On-Demand")); return f; }
        if (segment == ClientSegments.Standard && certEnvironmentId is not null) f.Add(Bad("Only an On-Demand client has a CERT environment: remove the environment, or make the client On-Demand"));
        else if (segment == ClientSegments.OnDemand && certEnvironmentId is null) f.Add(Bad("An On-Demand client needs a CERT environment: choose one of this workspace's environments"));
        else if (certEnvironmentId is not null && envChanged)
        {
            var env = await db.Set<Environments>().AsNoTracking().SingleOrDefaultAsync(e => e.Id == certEnvironmentId, ct);
            if (env is null) f.Add(Bad("The CERT environment does not exist in this workspace"));
            else if (env.ArchivedAt is not null) f.Add(Bad($"The environment \"{env.Name}\" is archived and cannot be chosen as a CERT environment"));
        }
        return f;
    }

    public Task<ServiceResult<Clients>> CreateAsync(NewClient n, Actor actor, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var now = Now;
            var name = (n.Name ?? "").Trim();
            var segment = ClientSegments.Parse(n.Segment) ?? n.Segment ?? "";
            var env = string.IsNullOrWhiteSpace(n.CertEnvironmentId) ? null : n.CertEnvironmentId.Trim();
            var f = await CheckAsync(db, name, segment, env, true, null, ct);
            if (f.Count > 0) return ServiceResult<Clients>.Fail(f);
            var row = new Clients { Id = Ids.New(), Name = name, Segment = segment, CertEnvironmentId = env, CreatedAt = now, LastChangedByUserId = actor.UserId, LastChangedAt = now };
            db.Set<Clients>().Add(row);
            Audit(db, actor, null, Entity, row.Id, "Create", null, Snapshot(row));
            await db.SaveChangesAsync(ct);
            return ServiceResult<Clients>.Ok(row);
        }, ct);

    public Task<ServiceResult<Clients>> UpdateAsync(string id, ClientPatch p, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var row = await db.Set<Clients>().SingleOrDefaultAsync(c => c.Id == id, ct);
            if (row is null) return ServiceResult<Clients>.NotFound("client");
            if (VersionMismatch(expectedVersion, row.Version)) return ServiceResult<Clients>.Conflict(row);
            var name = p.Name is null ? row.Name : p.Name.Trim();
            var segment = p.Segment is null ? row.Segment : ClientSegments.Parse(p.Segment) ?? p.Segment;
            // An environment given (or cleared) wins; otherwise the client keeps its own, unless it is becoming Standard, which has none.
            var env = p.CertEnvironmentId is null ? row.CertEnvironmentId : p.CertEnvironmentId.Trim().Length == 0 ? null : p.CertEnvironmentId.Trim();
            if (p.CertEnvironmentId is null && segment == ClientSegments.Standard) env = null;
            var f = await CheckAsync(db, name, segment, env, env != row.CertEnvironmentId, id, ct);
            if (f.Count > 0) return ServiceResult<Clients>.Fail(f);
            if (name == row.Name && segment == row.Segment && env == row.CertEnvironmentId) return ServiceResult<Clients>.Ok(row);   // nothing changed: no version, no audit row
            var before = Snapshot(row);
            row.Name = name; row.Segment = segment; row.CertEnvironmentId = env;
            row.Version++; row.LastChangedByUserId = actor.UserId; row.LastChangedAt = Now;
            Audit(db, actor, null, Entity, row.Id, "Update", before, Snapshot(row));
            await db.SaveChangesAsync(ct);
            return ServiceResult<Clients>.Ok(row);
        }, ct);

    public Task<ServiceResult<Clients>> SetArchivedAsync(string id, bool archived, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var row = await db.Set<Clients>().SingleOrDefaultAsync(c => c.Id == id, ct);
            if (row is null) return ServiceResult<Clients>.NotFound("client");
            if (VersionMismatch(expectedVersion, row.Version)) return ServiceResult<Clients>.Conflict(row);
            if ((row.ArchivedAt is not null) == archived) return ServiceResult<Clients>.Ok(row);   // repeat is a no-op
            var before = Snapshot(row);
            var now = Now;
            row.ArchivedAt = archived ? now : null;
            row.Version++; row.LastChangedByUserId = actor.UserId; row.LastChangedAt = now;
            Audit(db, actor, null, Entity, row.Id, archived ? "Archive" : "Unarchive", before, Snapshot(row));
            await db.SaveChangesAsync(ct);
            return ServiceResult<Clients>.Ok(row);
        }, ct);
}
