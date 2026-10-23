using System.Diagnostics;
using Microsoft.Data.Sqlite;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Services;
using static ReleaseMgmt.Infrastructure.Tests.OpsKit;

namespace ReleaseMgmt.Infrastructure.Tests;

/// <summary>REOS-114: the audit log is a hash chain per workspace; rows are sealed after commit, existing rows once, and a changed, removed or inserted row is found and named.</summary>
public sealed class AuditChainTests(TriggerSuiteFixture fx) : IClassFixture<TriggerSuiteFixture>
{
    private OpsKit Kit(IDictionary<string, string?>? settings = null) => new(fx, settings);

    private static void Event(OpsKit k, string action, string? workspace = "default", string? actor = "rte", string? train = "t1", string? after = null) =>
        k.Sql($"INSERT INTO AuditEvents(OccurredAt,ActorUserId,ReleaseTrainId,EntityType,EntityId,Action,AfterJson,WorkspaceId) VALUES(?,?,?,'Probe','p1',?,?,?)", Iso(T0), actor, workspace == "default" ? train : null, action, after, workspace);   // a train belongs to its own workspace

    /// <summary>The hash of every row of a chain, recomputed here from the stored columns, independently of the service.</summary>
    private static List<(long Id, string Prev, string Hash)> Recompute(OpsKit k, string? workspace)
    {
        var prev = AuditChain.Genesis;
        var list = new List<(long, string, string)>();
        foreach (var r in k.Query($"SELECT Id,OccurredAt,ActorUserId,ReleaseTrainId,EntityType,EntityId,Action,BeforeJson,AfterJson,WorkspaceId FROM AuditEvents WHERE {(workspace is null ? "WorkspaceId IS NULL" : "WorkspaceId=?")} ORDER BY Id", workspace is null ? [] : [workspace]))
        {
            var h = AuditChain.Hash(prev, AuditChain.Canonical(Convert.ToInt64(r[0]), (string)r[1]!, (string?)r[2], (string?)r[3], (string)r[4]!, (string)r[5]!, (string)r[6]!, (string?)r[7], (string?)r[8], (string?)r[9]));
            list.Add((Convert.ToInt64(r[0]), prev, h));
            prev = h;
        }
        return list;
    }

    private static string Copy(OpsKit k)
    {
        var copy = k.Path + ".copy";
        using (var src = new SqliteConnection($"Data Source={k.Path};Pooling=False")) { src.Open(); using var dst = new SqliteConnection($"Data Source={copy};Pooling=False"); dst.Open(); src.BackupDatabase(dst); }
        SqliteConnection.ClearAllPools();
        return copy;
    }

    private static void Run(string path, string sql)
    {
        using var c = new SqliteConnection($"Data Source={path};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task Rows_are_sealed_in_order_per_chain_and_each_hash_is_the_hash_of_the_previous_one_and_its_own_content()
    {
        var k = Kit();
        k.Sql($"INSERT INTO Workspaces(Id,Name,Slug,CreatedAt) VALUES('w2','EU','eu','{Iso(T0)}')");
        foreach (var a in new[] { "One", "Two", "Three" }) Event(k, a);
        Event(k, "Other workspace", "w2");
        Event(k, "Host-wide", null, null, null);
        Event(k, "Four", after: "{\"title\":\"Zürich ✓ — multi-byte\"}");
        var baseline = k.Count("SELECT count(*) FROM AuditEvents WHERE Hash IS NOT NULL");
        Assert.Equal(0, baseline);

        var sealedCount = await k.Chain.SealPendingAsync();
        Assert.True(sealedCount >= 6);
        Assert.Equal(0, k.Count("SELECT count(*) FROM AuditEvents WHERE Hash IS NULL"));
        foreach (var chain in new string?[] { "default", "w2", null })
        {
            var expected = Recompute(k, chain);
            var stored = k.Query($"SELECT Id,PrevHash,Hash FROM AuditEvents WHERE {(chain is null ? "WorkspaceId IS NULL" : "WorkspaceId=?")} ORDER BY Id", chain is null ? [] : [chain]).Select(r => (Convert.ToInt64(r[0]), (string)r[1]!, (string)r[2]!)).ToList();
            Assert.Equal(expected, stored);
            Assert.Equal(AuditChain.Genesis, stored[0].Item2);   // each chain starts at the genesis hash
            var head = k.Query("SELECT LastId, LastHash, SealedCount FROM AuditChainHeads WHERE ChainId=?", chain ?? "*").Single();
            Assert.Equal((stored[^1].Item1, stored[^1].Item3, (long)stored.Count), (Convert.ToInt64(head[0]), (string)head[1]!, Convert.ToInt64(head[2])));
        }
        Assert.Equal(0, await k.Chain.SealPendingAsync());   // sealing again finds nothing

        // rows written afterwards continue the chain
        Event(k, "Five");
        Assert.Equal(1, await k.Chain.SealPendingAsync());
        var five = Recompute(k, "default");
        Assert.Equal(five[^1].Hash, k.Text("SELECT Hash FROM AuditEvents WHERE Action='Five'"));
        Assert.Equal(five[^2].Hash, k.Text("SELECT PrevHash FROM AuditEvents WHERE Action='Five'"));
        var v = await k.Chain.VerifyAsync("default");
        Assert.Equal(("Verified", null), (v.Status, v.FirstBad));
    }

    [Fact]
    public async Task Existing_rows_are_sealed_once_in_batches_and_the_chain_then_verifies()
    {
        var k = Kit(new Dictionary<string, string?> { ["Audit:Seal:BatchSize"] = "50" });
        k.Sql("INSERT INTO AuditEvents(OccurredAt,ActorUserId,ReleaseTrainId,EntityType,EntityId,Action,WorkspaceId) " +
              "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<420) SELECT '2026-09-01T00:00:00Z','rte','t1','Probe','p'||i,'Legacy','default' FROM n");
        var before = k.Count("SELECT count(*) FROM AuditEvents");
        Assert.Equal(before, await k.Chain.SealPendingAsync());   // all of them, in several transactions
        Assert.Equal(0, k.Count("SELECT count(*) FROM AuditEvents WHERE Hash IS NULL"));
        Assert.Equal(before, k.Count("SELECT SealedCount FROM AuditChainHeads WHERE ChainId='default'"));
        var v = await k.Chain.VerifyAsync("default");
        Assert.Equal(("Verified", before), (v.Status, v.SealedRows));
        Assert.Equal(Recompute(k, "default").Select(r => r.Hash), k.Query("SELECT Hash FROM AuditEvents WHERE WorkspaceId='default' ORDER BY Id").Select(r => (string)r[0]!));
    }

    [Fact]
    public async Task Sealing_leaves_the_log_append_only_a_sealed_row_cannot_change_or_go()
    {
        var k = Kit();
        Event(k, "One");
        await k.Chain.SealPendingAsync();
        var sealedRow = "WHERE Action='One'";
        Assert.Contains("append-only", Assert.Throws<SqliteException>(() => k.Sql($"UPDATE AuditEvents SET Action='Edited' {sealedRow}")).Message);
        Assert.Contains("append-only", Assert.Throws<SqliteException>(() => k.Sql($"UPDATE AuditEvents SET Hash=printf('%064d',5) {sealedRow}")).Message);   // not even the hash again
        Assert.Contains("append-only", Assert.Throws<SqliteException>(() => k.Sql($"UPDATE AuditEvents SET PrevHash=NULL, Hash=NULL {sealedRow}")).Message);
        Assert.Contains("append-only", Assert.Throws<SqliteException>(() => k.Sql($"DELETE FROM AuditEvents {sealedRow}")).Message);
        // an unsealed row may be sealed, and only that: the content stays
        Event(k, "Two");
        Assert.Contains("append-only", Assert.Throws<SqliteException>(() => k.Sql("UPDATE AuditEvents SET Action='Edited', PrevHash=printf('%064d',0), Hash=printf('%064d',1) WHERE Action='Two'")).Message);
        Assert.Equal(1, k.Count("SELECT count(*) FROM AuditEvents WHERE Action='One' AND Hash IS NOT NULL"));
    }

    [Fact]
    public async Task A_row_changed_in_a_copy_of_the_database_is_found_and_named()
    {
        var k = Kit();
        foreach (var a in new[] { "A1", "A2", "A3", "A4", "A5" }) Event(k, a, after: $"{{\"n\":\"{a}\"}}");
        await k.Chain.SealPendingAsync();
        var ids = k.Query("SELECT Id FROM AuditEvents ORDER BY Id").Select(r => Convert.ToInt64(r[0])).ToList();
        var copy = Copy(k);
        Assert.All(await AuditChainService.VerifyFileAsync(copy), c => Assert.Equal("Verified", c.Status));

        // someone with the file takes the triggers off and edits the third row's content
        Run(copy, "DROP TRIGGER trg_Audit_NoUpdate; DROP TRIGGER trg_Audit_NoDelete;");
        Run(copy, "UPDATE AuditEvents SET AfterJson='{\"n\":\"forged\"}' WHERE Action='A3'");
        var bad = (await AuditChainService.VerifyFileAsync(copy)).Single(c => c.ChainId == "default");
        Assert.Equal("Failed", bad.Status);
        Assert.Equal(ids[2], bad.FirstBad!.Id);   // the first row that does not verify, named
        Assert.Contains("altered", bad.FirstBad.Reason);

        // and if the forger also recomputes that row's hash, the next row no longer follows it
        var forged = k.Query("SELECT Id,OccurredAt,ActorUserId,ReleaseTrainId,EntityType,EntityId,Action,BeforeJson,AfterJson,WorkspaceId FROM AuditEvents WHERE Action='A3'").Single();
        var prev = k.Text("SELECT PrevHash FROM AuditEvents WHERE Action='A3'");
        var rehash = AuditChain.Hash(prev, AuditChain.Canonical(Convert.ToInt64(forged[0]), (string)forged[1]!, (string?)forged[2], (string?)forged[3], (string)forged[4]!, (string)forged[5]!, (string)forged[6]!, null, "{\"n\":\"forged\"}", (string?)forged[9]));
        Run(copy, $"UPDATE AuditEvents SET Hash='{rehash}' WHERE Action='A3'");
        var next = (await AuditChainService.VerifyFileAsync(copy)).Single(c => c.ChainId == "default");
        Assert.Equal((ids[3], true), (next.FirstBad!.Id, next.FirstBad.Reason.Contains("previous hash")));

        // the original is untouched and still verifies
        Assert.Equal("Verified", (await k.Chain.VerifyAsync("default")).Status);
    }

    [Fact]
    public async Task A_row_removed_or_inserted_behind_the_chain_or_cut_from_its_end_is_found_and_named()
    {
        var k = Kit();
        foreach (var a in new[] { "A1", "A2", "A3", "A4", "A5" }) Event(k, a);
        await k.Chain.SealPendingAsync();
        var ids = k.Query("SELECT Id FROM AuditEvents ORDER BY Id").Select(r => Convert.ToInt64(r[0])).ToList();
        string Drop(string path) { Run(path, "DROP TRIGGER trg_Audit_NoUpdate; DROP TRIGGER trg_Audit_NoDelete;"); return path; }

        // a row removed from the middle: the row after it names the break
        var middle = Drop(Copy(k));
        Run(middle, "DELETE FROM AuditEvents WHERE Action='A3'");
        var m = (await AuditChainService.VerifyFileAsync(middle)).Single(c => c.ChainId == "default");
        Assert.Equal((ids[3], true), (m.FirstBad!.Id, m.FirstBad.Reason.Contains("removed")));
        // the first row removed
        var first = Drop(Copy(k));
        Run(first, "DELETE FROM AuditEvents WHERE Action='A1'");
        Assert.Equal(ids[1], (await AuditChainService.VerifyFileAsync(first)).Single(c => c.ChainId == "default").FirstBad!.Id);
        // the last row removed: nothing follows it, so the chain's head gives it away
        var last = Drop(Copy(k));
        Run(last, "DELETE FROM AuditEvents WHERE Action='A5'");
        var l = (await AuditChainService.VerifyFileAsync(last)).Single(c => c.ChainId == "default");
        Assert.Equal((ids[4], true), (l.FirstBad!.Id, l.FirstBad.Reason.Contains("end")));
        // a row put back behind the chain in a place that was emptied
        var behind = Drop(Copy(k));
        Run(behind, $"DELETE FROM AuditEvents WHERE Action='A3'; INSERT INTO AuditEvents(Id,OccurredAt,ActorUserId,ReleaseTrainId,EntityType,EntityId,Action,WorkspaceId) VALUES({ids[2]},'2026-10-20T14:00:00Z','rte','t1','Probe','p1','Back-dated','default')");
        var b = (await AuditChainService.VerifyFileAsync(behind)).Single(c => c.ChainId == "default");
        Assert.Equal((ids[2], true), (b.FirstBad!.Id, b.FirstBad.Reason.Contains("behind the chain")));
        // the head record removed
        var headless = Drop(Copy(k));
        Run(headless, "DELETE FROM AuditChainHeads");
        Assert.Equal(ids[4], (await AuditChainService.VerifyFileAsync(headless)).Single(c => c.ChainId == "default").FirstBad!.Id);
        // a last row whose hash was changed
        var tail = Drop(Copy(k));
        Run(tail, "UPDATE AuditEvents SET Hash=printf('%064d',9) WHERE Action='A5'");
        Assert.Equal(ids[4], (await AuditChainService.VerifyFileAsync(tail)).Single(c => c.ChainId == "default").FirstBad!.Id);
    }

    [Fact]
    public async Task Rows_waiting_to_be_sealed_are_pending_not_a_failure_and_other_chains_are_not_affected()
    {
        var k = Kit();
        k.Sql($"INSERT INTO Workspaces(Id,Name,Slug,CreatedAt) VALUES('w2','EU','eu','{Iso(T0)}')");
        Event(k, "A1"); Event(k, "B1", "w2");
        await k.Chain.SealPendingAsync();
        Event(k, "A2"); Event(k, "A3");
        var v = await k.Chain.VerifyAsync("default");
        Assert.Equal(("Verified", 1L, 2L), (v.Status, v.SealedRows, v.PendingRows));
        // a break in one workspace's chain does not fail the other's
        var copy = Copy(k);
        Run(copy, "DROP TRIGGER trg_Audit_NoUpdate; UPDATE AuditEvents SET Action='x' WHERE Action='B1'");
        var all = await AuditChainService.VerifyFileAsync(copy);
        Assert.Equal(("Failed", "Verified"), (all.Single(c => c.ChainId == "w2").Status, all.Single(c => c.ChainId == "default").Status));
        Assert.Empty(await AuditChainService.VerifyFileAsync(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".db")).ContinueWith(t => t.IsFaulted ? new List<ChainVerification>() : t.Result));
    }

    [Fact]
    public async Task A_failed_verification_raises_an_alert_naming_the_row_and_a_clean_one_clears_it()
    {
        var k = Kit();
        using var scope = ReleaseMgmt.Domain.Common.WorkspaceScope.Enter("default");   // as in a request
        foreach (var a in new[] { "A1", "A2", "A3" }) Event(k, a);
        await k.Chain.SealPendingAsync();
        Assert.Empty(k.Query("SELECT * FROM SyncAlerts"));
        var id = Convert.ToInt64(k.Query("SELECT Id FROM AuditEvents WHERE Action='A2'").Single()[0]);
        k.Sql("DROP TRIGGER trg_Audit_NoUpdate");
        k.Sql("UPDATE AuditEvents SET AfterJson='{\"x\":1}' WHERE Action='A2'");
        var all = await k.Chain.VerifyAllAsync();
        Assert.Equal(id, all.Single(c => c.ChainId == "default").FirstBad!.Id);
        var alert = k.Query("SELECT SourceSystem, Kind, ErrorMessage, IsResolved, WorkspaceId FROM SyncAlerts").Single();
        Assert.Equal(("Audit", "IntegrityFailed", 0L, "default"), ((string)alert[0]!, (string)alert[1]!, Convert.ToInt64(alert[3]), (string)alert[4]!));
        Assert.Contains($"row {id}", (string)alert[2]!);
        // the same failure again is the same alert
        await k.Chain.VerifyAllAsync();
        Assert.Equal(1, k.Count("SELECT count(*) FROM SyncAlerts"));
        // the stored result is what the screen reads
        var i = await k.Chain.IntegrityAsync();
        Assert.Equal(("Failed", id), (i.Status, i.FirstBad!.Id));
        // put the row back as it was: verified again, alert resolved
        k.Sql("UPDATE AuditEvents SET AfterJson=NULL WHERE Action='A2'");
        Assert.Equal("Verified", (await k.Chain.VerifyAsync("default")).Status);
        Assert.Equal(1, k.Count("SELECT count(*) FROM SyncAlerts WHERE IsResolved=1"));
        Assert.Equal("Verified", (await k.Chain.IntegrityAsync(recheck: true)).Status);
    }

    [Fact]
    public async Task The_integrity_view_reports_the_count_the_last_check_and_the_first_bad_row()
    {
        var k = Kit();
        using var scope = ReleaseMgmt.Domain.Common.WorkspaceScope.Enter("default");   // as in a request
        var empty = await k.Chain.IntegrityAsync();
        Assert.Equal(("Verified", 0L), (empty.Status, empty.SealedRows));
        Event(k, "A1"); Event(k, "A2");
        await k.Chain.SealPendingAsync();
        var i = await k.Chain.IntegrityAsync();
        Assert.Equal(("Verified", 2L, 0L, null), (i.Status, i.SealedRows, i.PendingRows, i.FirstBad));
        Assert.Equal(T0, i.VerifiedAt);
        Assert.NotNull(i.HeadHash);
        Event(k, "A3");
        k.Time.Advance(TimeSpan.FromMinutes(5));
        var again = await k.Chain.IntegrityAsync(maxAge: TimeSpan.FromMinutes(1));   // older than the allowed age: checked again
        Assert.Equal((2L, 1L, T0.AddMinutes(5)), (again.SealedRows, again.PendingRows, again.VerifiedAt));
    }

    [Fact]
    public async Task Writers_are_not_held_up_and_the_chain_is_whole_when_sealing_runs_beside_them()
    {
        var k = Kit();
        var stop = false;
        var sealer = Task.Run(async () => { while (!stop) { await k.Chain.SealPendingAsync(); await Task.Delay(5); } });
        var writers = Enumerable.Range(0, 4).Select(w => Task.Run(() => { for (var i = 0; i < 60; i++) Event(k, $"W{w}-{i}", w % 2 == 0 ? "default" : null); })).ToArray();
        await Task.WhenAll(writers);
        stop = true; await sealer;
        await k.Chain.SealPendingAsync();
        Assert.Equal(0, k.Count("SELECT count(*) FROM AuditEvents WHERE Hash IS NULL"));
        foreach (var c in await k.Chain.VerifyAllAsync()) Assert.Equal("Verified", c.Status);
        Assert.Equal(120, k.Count("SELECT SealedCount FROM AuditChainHeads WHERE ChainId='default'"));
        Assert.Equal(120, k.Count("SELECT SealedCount FROM AuditChainHeads WHERE ChainId='*'"));
    }

    [Fact]
    public async Task The_python_tool_agrees_with_the_service_on_a_good_chain_and_names_the_same_bad_row()
    {
        var script = Path.Combine(RepoRoot(), "tools", "verify_audit_chain.py");
        var python = Python();
        if (python is null) return;   // no interpreter on this machine: the CI jobs that run the tools have one
        var k = Kit();
        k.Sql($"INSERT INTO Workspaces(Id,Name,Slug,CreatedAt) VALUES('w2','EU','eu','{Iso(T0)}')");
        foreach (var a in new[] { "A1", "A2", "A3" }) Event(k, a, after: $"{{\"v\":\"naïve ✓ {a}\"}}");
        Event(k, "B1", "w2"); Event(k, "H1", null, null, null);
        await k.Chain.SealPendingAsync();
        var copy = Copy(k);
        var good = Python(python, script, copy);
        Assert.Equal(0, good.Exit);
        Assert.Contains("default: verified, 3 sealed rows", good.Output);
        Assert.Contains("host-wide rows: verified, 1 sealed rows", good.Output);

        Run(copy, "DROP TRIGGER trg_Audit_NoUpdate; UPDATE AuditEvents SET Action='forged' WHERE Action='A2'");
        var id = Convert.ToInt64(k.Query("SELECT Id FROM AuditEvents WHERE Action='A2'").Single()[0]);
        var bad = Python(python, script, copy);
        Assert.Equal(1, bad.Exit);
        Assert.Contains($"default: FAILED at row {id}", bad.Output);
        Assert.Equal(id, (await AuditChainService.VerifyFileAsync(copy)).Single(c => c.ChainId == "default").FirstBad!.Id);   // the service names the same row
        Assert.Equal(2, Python(python, script, Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N") + ".db")).Exit);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "db", "schema.sql"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    private static string? Python()
    {
        foreach (var name in new[] { "python3", "python" })
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(name, "--version") { RedirectStandardOutput = true, RedirectStandardError = true });
                p!.WaitForExit(10_000);
                if (p.ExitCode == 0) return name;
            }
            catch (System.ComponentModel.Win32Exception) { /* not on the path */ }
        }
        return null;
    }

    private static (int Exit, string Output) Python(string python, string script, string db)
    {
        var psi = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("-I"); psi.ArgumentList.Add(script); psi.ArgumentList.Add(db);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);
        return (p.ExitCode, output);
    }
}
