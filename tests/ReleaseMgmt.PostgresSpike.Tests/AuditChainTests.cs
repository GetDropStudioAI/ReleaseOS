using Npgsql;
using ReleaseMgmt.Domain.Services;
using Xunit.Abstractions;

namespace ReleaseMgmt.PostgresSpike.Tests;

/// <summary>The audit hash chain (REOS-114, AuditChainService) on Postgres. The shipped sealer is raw Microsoft.Data.Sqlite and relies on two SQLite properties: BEGIN IMMEDIATE (one writer at
/// a time) and Id order = commit order. Postgres has neither: an IDENTITY value is taken at INSERT, not at COMMIT. This spike ports the sealer's critical section and measures both hazards.</summary>
public sealed class AuditChainTests(PgFixture fx, ITestOutputHelper o) : IClassFixture<PgFixture>
{
    private const string Chain = "default";
    private const string InsertAudit = "INSERT INTO \"AuditEvents\"(\"OccurredAt\",\"ActorUserId\",\"EntityType\",\"EntityId\",\"Action\",\"WorkspaceId\") VALUES('2026-10-20T14:00:00Z','rte','Probe',$1,'Write','default') RETURNING \"Id\"";

    /// <summary>The shipped algorithm (read head, take the next unsealed rows in Id order, hash, update, advance head), on Npgsql. <paramref name="lockTable"/> is the candidate fix.</summary>
    private static async Task<int> SealAsync(string cs, bool lockTable)
    {
        await using var c = new NpgsqlConnection(cs);
        await c.OpenAsync();
        await using var tx = await c.BeginTransactionAsync();
        if (lockTable)
        {
            // SHARE ROW EXCLUSIVE conflicts with the ROW EXCLUSIVE lock every INSERT holds until it commits: the sealer waits for in-flight writers, so after this line every Id
            // below the highest one it can see is committed or aborted (no gap can fill in later), and two sealers cannot run together (it conflicts with itself).
            await using var l = new NpgsqlCommand("LOCK TABLE \"AuditEvents\" IN SHARE ROW EXCLUSIVE MODE", c, tx); await l.ExecuteNonQueryAsync();
        }
        long lastId = 0; var lastHash = AuditChain.Genesis;
        await using (var h = new NpgsqlCommand("SELECT \"LastId\",\"LastHash\" FROM \"AuditChainHeads\" WHERE \"ChainId\"=$1", c, tx) { Parameters = { new() { Value = Chain } } })
        await using (var r = await h.ExecuteReaderAsync()) { if (await r.ReadAsync()) { lastId = r.GetInt64(0); lastHash = r.GetString(1); } }
        var rows = new List<(long Id, string Canonical)>();
        await using (var q = new NpgsqlCommand("SELECT \"Id\",\"OccurredAt\",\"ActorUserId\",\"ReleaseTrainId\",\"EntityType\",\"EntityId\",\"Action\",\"BeforeJson\",\"AfterJson\",\"WorkspaceId\" FROM \"AuditEvents\" " +
                                              "WHERE \"Hash\" IS NULL AND \"Id\" > $1 AND \"WorkspaceId\"=$2 ORDER BY \"Id\" LIMIT 500", c, tx) { Parameters = { new() { Value = lastId }, new() { Value = Chain } } })
        await using (var r = await q.ExecuteReaderAsync())
            while (await r.ReadAsync())
                rows.Add((r.GetInt64(0), AuditChain.Canonical(r.GetInt64(0), r.GetString(1), N(r, 2), N(r, 3), r.GetString(4), r.GetString(5), r.GetString(6), N(r, 7), N(r, 8), N(r, 9))));
        foreach (var (id, canonical) in rows)
        {
            var hash = AuditChain.Hash(lastHash, canonical);
            await using var u = new NpgsqlCommand("UPDATE \"AuditEvents\" SET \"PrevHash\"=$1,\"Hash\"=$2 WHERE \"Id\"=$3 AND \"Hash\" IS NULL", c, tx) { Parameters = { new() { Value = lastHash }, new() { Value = hash }, new() { Value = id } } };
            await u.ExecuteNonQueryAsync();
            lastHash = hash; lastId = id;
        }
        if (rows.Count > 0)
        {
            await using var up = new NpgsqlCommand("INSERT INTO \"AuditChainHeads\"(\"ChainId\",\"LastId\",\"LastHash\",\"SealedCount\",\"SealedAt\") VALUES($1,$2,$3,$4,'2026-10-20T14:00:00Z') " +
                "ON CONFLICT(\"ChainId\") DO UPDATE SET \"LastId\"=excluded.\"LastId\",\"LastHash\"=excluded.\"LastHash\",\"SealedCount\"=\"AuditChainHeads\".\"SealedCount\"+$4", c, tx)
                { Parameters = { new() { Value = Chain }, new() { Value = lastId }, new() { Value = lastHash }, new() { Value = (long)rows.Count } } };
            await up.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
        return rows.Count;
    }

    private static string? N(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    /// <summary>Recomputes the chain the way tools/verify_audit_chain.py does; returns (sealed rows, rows left unsealed behind the head, broken links).</summary>
    private static (int Sealed, int Skipped, int Broken) Verify(PgEnv e)
    {
        var rows = e.Query("SELECT \"Id\",\"OccurredAt\",\"ActorUserId\",\"ReleaseTrainId\",\"EntityType\",\"EntityId\",\"Action\",\"BeforeJson\",\"AfterJson\",\"WorkspaceId\",\"PrevHash\",\"Hash\" FROM \"AuditEvents\" WHERE \"WorkspaceId\"='default' ORDER BY \"Id\"");
        var prev = AuditChain.Genesis; int sealedRows = 0, skipped = 0, broken = 0; long headId = 0;
        foreach (var r in rows.Where(r => r[11] is not null)) headId = Math.Max(headId, (long)r[0]!);
        foreach (var r in rows)
        {
            if (r[11] is null) { if ((long)r[0]! < headId) skipped++; continue; }
            var canonical = AuditChain.Canonical((long)r[0]!, (string)r[1]!, (string?)r[2], (string?)r[3], (string)r[4]!, (string)r[5]!, (string)r[6]!, (string?)r[7], (string?)r[8], (string?)r[9]);
            if ((string?)r[10] != prev || (string)r[11]! != AuditChain.Hash(prev, canonical)) broken++;
            prev = (string)r[11]!; sealedRows++;
        }
        return (sealedRows, skipped, broken);
    }

    [PgFact]
    public async Task Hazard_a_row_that_commits_after_a_higher_Id_is_sealed_is_skipped_forever()
    {
        var e = await fx.NewEnvAsync();
        await using var a = e.Open(); await using var b = e.Open();
        await using var txA = await a.BeginTransactionAsync();
        long idA, idB;
        await using (var cmd = new NpgsqlCommand(InsertAudit, a, txA) { Parameters = { new() { Value = "A" } } }) idA = (long)(await cmd.ExecuteScalarAsync())!;   // Id n, not committed yet
        await using (var cmd = new NpgsqlCommand(InsertAudit, b) { Parameters = { new() { Value = "B" } } }) idB = (long)(await cmd.ExecuteScalarAsync())!;     // Id n+1, committed
        Assert.True(idB > idA);

        Assert.Equal(1, await SealAsync(e.ConnectionString, lockTable: false));      // the sealer sees only B and moves the head past A's Id
        await txA.CommitAsync();                                                       // A commits late
        Assert.Equal(0, await SealAsync(e.ConnectionString, lockTable: false));      // Id > head filters A out: it is never sealed
        var v = Verify(e);
        o.WriteLine($"unlocked sealer: sealed {v.Sealed}, rows left unsealed behind the head {v.Skipped}, broken links {v.Broken}");
        Assert.Equal(1, v.Skipped);
        Assert.Null(e.Scalar("SELECT \"Hash\" FROM \"AuditEvents\" WHERE \"Id\"=$1", idA));
    }

    [PgFact]
    public async Task Fix_LOCK_TABLE_SHARE_ROW_EXCLUSIVE_makes_the_sealer_wait_for_in_flight_writers_so_nothing_is_skipped()
    {
        var e = await fx.NewEnvAsync();
        await using var a = e.Open(); await using var b = e.Open();
        await using var txA = await a.BeginTransactionAsync();
        await using (var cmd = new NpgsqlCommand(InsertAudit, a, txA) { Parameters = { new() { Value = "A" } } }) await cmd.ExecuteScalarAsync();
        await using (var cmd = new NpgsqlCommand(InsertAudit, b) { Parameters = { new() { Value = "B" } } }) await cmd.ExecuteScalarAsync();

        var seal = Task.Run(() => SealAsync(e.ConnectionString, lockTable: true));
        await Task.Delay(700);
        Assert.False(seal.IsCompleted, "the sealer must wait for A");
        await txA.CommitAsync();
        Assert.Equal(2, await seal.WaitAsync(TimeSpan.FromSeconds(10)));
        var v = Verify(e);
        o.WriteLine($"locked sealer: sealed {v.Sealed}, skipped {v.Skipped}, broken {v.Broken}");
        Assert.Equal((2, 0, 0), v);
    }

    [PgFact]
    public async Task Fix_holds_under_4_writers_and_3_sealers_running_together()
    {
        var e = await fx.NewEnvAsync();
        var stop = false;
        var sealers = Enumerable.Range(0, 3).Select(_ => Task.Run(async () => { while (!Volatile.Read(ref stop)) { await SealAsync(e.ConnectionString, lockTable: true); await Task.Delay(5); } })).ToArray();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, 4).Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < 100; i++)
            {
                await using var c = e.Open(); await using var tx = await c.BeginTransactionAsync();
                await using (var cmd = new NpgsqlCommand(InsertAudit, c, tx) { Parameters = { new() { Value = $"w{w}-{i}" } } }) await cmd.ExecuteScalarAsync();
                if (i % 7 == 0) await Task.Delay(3);   // hold the transaction open a little, so Ids commit out of order
                await tx.CommitAsync();
            }
        })));
        Volatile.Write(ref stop, true);
        await Task.WhenAll(sealers);
        await SealAsync(e.ConnectionString, lockTable: true);
        var v = Verify(e);
        o.WriteLine($"400 audit rows written by 4 writers in {sw.ElapsedMilliseconds} ms beside 3 sealers: sealed {v.Sealed}, skipped {v.Skipped}, broken {v.Broken}");
        Assert.Equal((400, 0, 0), v);
    }
}
