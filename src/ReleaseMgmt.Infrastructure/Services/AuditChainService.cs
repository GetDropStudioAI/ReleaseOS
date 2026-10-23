using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Reminders;

namespace ReleaseMgmt.Infrastructure.Services;

/// <summary>The first row at which a chain stops verifying, and why.</summary>
public sealed record ChainBreak(long Id, string Reason);

/// <summary>What one chain looks like. Status: Verified, or Failed with <see cref="FirstBad"/>. <see cref="PendingRows"/> are rows written but not yet sealed (never a failure on their own).</summary>
public sealed record ChainVerification(string ChainId, string Status, long SealedRows, long PendingRows, ChainBreak? FirstBad, long? HeadId, string? HeadHash);

/// <summary>The integrity of the current workspace's audit chain as the audit screen's banner and signature line read it.</summary>
public sealed record ChainIntegrity(string Status, long SealedRows, long PendingRows, ChainBreak? FirstBad, DateTime? VerifiedAt, long? HeadId, string? HeadHash);

/// <summary>
/// The hash chain of the audit log (REOS-114). Rows written by services, by triggers or by anything else are sealed in Id order, per chain (a workspace, or the host-wide rows), by
/// <see cref="SealPendingAsync"/>: it runs right after commits (<see cref="AuditSealSignal"/>), on a timer, and once at start for rows from before the chain, in its own short transaction,
/// so a write never waits for it. A verification pass (<see cref="VerifyAsync"/>, hourly by default) recomputes every hash and names the first row that does not fit: the row whose
/// content changed, or the row after a removed one, or a row inserted behind the chain; the chain's head (<c>AuditChainHeads</c>) catches rows removed from the end. A failure raises an
/// <c>Audit/IntegrityFailed</c> alert (SyncAlerts and the live push) that resolves itself when the chain verifies again. The algorithm is <see cref="AuditChain"/>; tools/verify_audit_chain.py
/// repeats it from the database file alone.
/// </summary>
public sealed class AuditChainService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IConfiguration config, SyncAlertWriter? alerts = null)
{
    private readonly SemaphoreSlim _sealGate = new(1, 1);
    private int Batch => Math.Clamp(config.GetValue("Audit:Seal:BatchSize", 1000), 10, 20_000);

    private static string Iso(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    private DateTime Now { get { var t = time.GetUtcNow().UtcDateTime; return new DateTime(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc); } }

    // ---- sealing --------------------------------------------------------------------------------------------------------------------

    /// <summary>Seals every row that has no hash yet, chain by chain, oldest first. Returns how many rows it sealed.</summary>
    public async Task<int> SealPendingAsync(CancellationToken ct = default)
    {
        using var _ = WorkspaceScope.HostWide();   // every chain, not the workspace of whoever asked
        await _sealGate.WaitAsync(ct);
        try
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            await db.Database.OpenConnectionAsync(ct);
            var chains = new List<string>();
            await using (var cmd = db.Database.GetDbConnection().CreateCommand())
            {
                cmd.CommandText = "SELECT DISTINCT COALESCE(WorkspaceId, '*') FROM AuditEvents WHERE Hash IS NULL";
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) chains.Add(r.GetString(0));
            }
            var sealedRows = 0;
            foreach (var chain in chains) sealedRows += await SealChainAsync(db.Database.GetDbConnection(), chain, ct);
            return sealedRows;
        }
        finally { _sealGate.Release(); }
    }

    private static async Task Exec(DbConnection c, string sql, CancellationToken ct, params (string Name, object? Value)[] p)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in p) { var prm = cmd.CreateParameter(); prm.ParameterName = n; prm.Value = v ?? DBNull.Value; cmd.Parameters.Add(prm); }
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<int> SealChainAsync(DbConnection c, string chain, CancellationToken ct)
    {
        var total = 0;
        while (true)
        {
            await Exec(c, "BEGIN IMMEDIATE", ct);   // the write lock up front: no snapshot to go stale between reading the head and writing
            try
            {
                long lastId = 0; var lastHash = AuditChain.Genesis;
                await using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "SELECT LastId, LastHash FROM AuditChainHeads WHERE ChainId = $c";
                    cmd.Parameters.Add(new SqliteParameter("$c", chain));
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    if (await r.ReadAsync(ct)) { lastId = r.GetInt64(0); lastHash = r.GetString(1); }
                }
                var rows = new List<(long Id, string Canonical)>();
                await using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "SELECT Id, OccurredAt, ActorUserId, ReleaseTrainId, EntityType, EntityId, Action, BeforeJson, AfterJson, WorkspaceId FROM AuditEvents " +
                                      "WHERE Hash IS NULL AND Id > $last AND " + (chain == AuditChain.HostChain ? "WorkspaceId IS NULL" : "WorkspaceId = $c") + " ORDER BY Id LIMIT $n";
                    cmd.Parameters.Add(new SqliteParameter("$last", lastId));
                    cmd.Parameters.Add(new SqliteParameter("$c", chain));
                    cmd.Parameters.Add(new SqliteParameter("$n", Batch));
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct))
                        rows.Add((r.GetInt64(0), AuditChain.Canonical(r.GetInt64(0), r.GetString(1), Str(r, 2), Str(r, 3), r.GetString(4), r.GetString(5), r.GetString(6), Str(r, 7), Str(r, 8), Str(r, 9))));
                }
                if (rows.Count == 0) { await Exec(c, "ROLLBACK", ct); return total; }
                foreach (var (id, canonical) in rows)
                {
                    var hash = AuditChain.Hash(lastHash, canonical);
                    await Exec(c, "UPDATE AuditEvents SET PrevHash = $p, Hash = $h WHERE Id = $id AND Hash IS NULL", ct, ("$p", lastHash), ("$h", hash), ("$id", id));
                    lastHash = hash; lastId = id;
                }
                await Exec(c, "INSERT INTO AuditChainHeads (ChainId, LastId, LastHash, SealedCount, SealedAt) VALUES ($c, $id, $h, $n, $t) " +
                              "ON CONFLICT(ChainId) DO UPDATE SET LastId = excluded.LastId, LastHash = excluded.LastHash, SealedCount = SealedCount + $n, SealedAt = excluded.SealedAt",
                    ct, ("$c", chain), ("$id", lastId), ("$h", lastHash), ("$n", rows.Count), ("$t", Iso(Now)));
                await Exec(c, "COMMIT", ct);
                total += rows.Count;
                if (rows.Count < Batch) return total;
            }
            catch
            {
                try { await Exec(c, "ROLLBACK", CancellationToken.None); } catch (SqliteException) { /* the transaction already ended */ }
                throw;
            }
        }
    }

    private static string? Str(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    // ---- verification ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Recomputes one chain from its first row. Rules: a sealed row's PrevHash is the Hash of the row before it in the chain, and its Hash is the hash of that and its own content;
    /// an unsealed row may only come after the last sealed row (never before one: it would have been inserted behind the chain); and the head says where the chain ends.
    /// </summary>
    public static async Task<ChainVerification> VerifyChainAsync(DbConnection c, string chainId, CancellationToken ct = default)
    {
        var prev = AuditChain.Genesis;
        long sealedRows = 0, pending = 0; long? firstUnsealed = null; long lastSealedId = 0; string? lastHash = null;
        ChainBreak? bad = null;
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, OccurredAt, ActorUserId, ReleaseTrainId, EntityType, EntityId, Action, BeforeJson, AfterJson, WorkspaceId, PrevHash, Hash FROM AuditEvents WHERE " +
                              (chainId == AuditChain.HostChain ? "WorkspaceId IS NULL" : "WorkspaceId = $c") + " ORDER BY Id";
            if (chainId != AuditChain.HostChain) cmd.Parameters.Add(new SqliteParameter("$c", chainId));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var id = r.GetInt64(0);
                var hash = Str(r, 11);
                if (hash is null) { pending++; firstUnsealed ??= id; continue; }
                if (firstUnsealed is { } u) { bad ??= new(u, "This row was added behind the chain: rows after it were sealed first, so it is not part of the chain"); }
                var stored = Str(r, 10);
                if (stored != prev)
                    bad ??= new(id, "The previous hash does not match the row before it: a row before this one was changed, removed or inserted");
                var computed = AuditChain.Hash(prev, AuditChain.Canonical(id, r.GetString(1), Str(r, 2), Str(r, 3), r.GetString(4), r.GetString(5), r.GetString(6), Str(r, 7), Str(r, 8), Str(r, 9)));
                if (computed != hash)
                    bad ??= new(id, "The content of this row does not match its hash: the row was altered");
                prev = hash; lastHash = hash; lastSealedId = id; sealedRows++;
            }
        }
        long? headId = null; string? headHash = null;
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT LastId, LastHash, SealedCount FROM AuditChainHeads WHERE ChainId = $c";
            cmd.Parameters.Add(new SqliteParameter("$c", chainId));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                headId = r.GetInt64(0); headHash = r.GetString(1);
                if (headId > lastSealedId) bad ??= new(headId.Value, $"The chain ends at row {headId} but that row (or rows after row {lastSealedId}) is missing: rows were removed from the end");
                else if (headId == lastSealedId && headHash != lastHash) bad ??= new(lastSealedId, "The last sealed row does not match the chain's head");
                else if (headId < lastSealedId) bad ??= new(headId.Value, "Rows were sealed beyond the chain's head: the head was changed");
            }
            else if (sealedRows > 0) bad ??= new(lastSealedId, "The chain's head record is missing");
        }
        return new(chainId, bad is null ? "Verified" : "Failed", sealedRows, pending, bad, headId, headHash);
    }

    /// <summary>Verifies every chain in a database file, read-only and outside any host: for the <c>verify-audit</c> command and for a copy of a database.</summary>
    public static async Task<List<ChainVerification>> VerifyFileAsync(string path, CancellationToken ct = default)
    {
        await using var c = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        await c.OpenAsync(ct);
        var chains = new List<string>();
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT DISTINCT COALESCE(WorkspaceId, '*') FROM AuditEvents UNION SELECT ChainId FROM AuditChainHeads ORDER BY 1";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) chains.Add(r.GetString(0));
        }
        var results = new List<ChainVerification>();
        foreach (var chain in chains) results.Add(await VerifyChainAsync(c, chain, ct));
        return results;
    }

    /// <summary>Verifies one chain of this database, stores the result on its head (when there is one) and raises or resolves the alert.</summary>
    public async Task<ChainVerification> VerifyAsync(string chainId, CancellationToken ct = default)
    {
        using var _ = WorkspaceScope.HostWide();
        ChainVerification result;
        await using (var db = await dbf.CreateDbContextAsync(ct))
        {
            await db.Database.OpenConnectionAsync(ct);
            var c = db.Database.GetDbConnection();
            result = await VerifyChainAsync(c, chainId, ct);
            if (result.HeadId is not null)
                await Exec(c, "UPDATE AuditChainHeads SET VerifiedAt = $t, VerifiedRows = $n, VerifiedStatus = $s, FirstBadId = $b, FirstBadReason = $why WHERE ChainId = $c", ct,
                    ("$t", Iso(Now)), ("$n", result.SealedRows), ("$s", result.Status), ("$b", result.FirstBad?.Id), ("$why", result.FirstBad?.Reason), ("$c", chainId));
        }
        await AlertAsync(result, ct);
        return result;
    }

    private async Task AlertAsync(ChainVerification v, CancellationToken ct)
    {
        if (alerts is null) return;
        using var scope = v.ChainId == AuditChain.HostChain ? WorkspaceScope.HostWide() : WorkspaceScope.Enter(v.ChainId);
        if (v.Status == "Failed")
            await alerts.RaiseAsync("Audit", "IntegrityFailed", v.ChainId, $"The audit log does not verify from row {v.FirstBad!.Id}: {v.FirstBad.Reason}", ct: ct);
        else await alerts.ResolveAsync("Audit", "IntegrityFailed", v.ChainId, ct);
    }

    /// <summary>Verifies every chain that exists (each workspace's, and the host-wide rows') and raises or resolves the alerts. Returns the results.</summary>
    public async Task<List<ChainVerification>> VerifyAllAsync(CancellationToken ct = default)
    {
        using var _ = WorkspaceScope.HostWide();
        List<string> chains;
        await using (var db = await dbf.CreateDbContextAsync(ct))
        {
            chains = await db.Database.SqlQueryRaw<string>("SELECT DISTINCT COALESCE(WorkspaceId, '*') AS Value FROM AuditEvents UNION SELECT ChainId AS Value FROM AuditChainHeads").ToListAsync(ct);
        }
        var results = new List<ChainVerification>();
        foreach (var chain in chains.Order(StringComparer.Ordinal)) results.Add(await VerifyAsync(chain, ct));
        return results;
    }

    // ---- the endpoint's view --------------------------------------------------------------------------------------------------------

    /// <summary>The integrity of the current workspace's chain: the stored result of the last pass, or a fresh pass when there is none, it is older than <paramref name="maxAge"/>, or <paramref name="recheck"/>.</summary>
    public async Task<ChainIntegrity> IntegrityAsync(bool recheck = false, TimeSpan? maxAge = null, CancellationToken ct = default)
    {
        var chain = AuditChain.ChainOf(WorkspaceScope.Current);
        await using (var db = await dbf.CreateDbContextAsync(ct))
        {
            var head = await db.Set<AuditChainHeads>().AsNoTracking().SingleOrDefaultAsync(h => h.ChainId == chain, ct);
            var ws = WorkspaceScope.Current;
            var pendingNow = await db.Set<AuditEvents>().AsNoTracking().LongCountAsync(a => a.Hash == null && a.WorkspaceId == ws, ct);   // this chain's rows only
            var fresh = head?.VerifiedAt is { } at && (maxAge is null || Now - at <= maxAge) && !recheck;
            if (head is not null && fresh)
                return new(head.VerifiedStatus ?? "Unverified", head.VerifiedRows ?? head.SealedCount, pendingNow, head.VerifiedStatus == "Failed" && head.FirstBadId is { } id ? new(id, head.FirstBadReason ?? "") : null, head.VerifiedAt, head.LastId, head.LastHash);
            if (head is null && pendingNow == 0 && !recheck && !await db.Set<AuditEvents>().AnyAsync(ct)) return new("Verified", 0, 0, null, Now, null, null);   // nothing to chain yet
        }
        var v = await VerifyAsync(chain, ct);
        return new(v.Status, v.SealedRows, v.PendingRows, v.FirstBad, Now, v.HeadId, v.HeadHash);
    }
}
