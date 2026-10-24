using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using ReleaseMgmt.Domain.Common;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>REOS-114 over HTTP and the command line: the background sealer chains the audit log, the integrity endpoint reports it, and tampering is named at its first row, raised as an alert, and found by the verify verb.</summary>
public class AuditChainApiTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private static ApiFactory Sealing() => new(settings: new Dictionary<string, string> { ["Audit:Seal:Enabled"] = "true", ["Audit:Seal:IntervalSeconds"] = "1", ["Audit:Seal:StartDelaySeconds"] = "0" });

    private static async Task<JsonElement> UntilSealed(HttpClient rte, string query = "")
    {
        for (var i = 0; i < 100; i++)
        {
            var r = await Json(await rte.GetAsync("/api/v1/audit/integrity" + query));
            if (r.GetProperty("pendingRows").GetInt64() == 0 && r.GetProperty("sealedRows").GetInt64() > 0) return r;
            await Task.Delay(100);
        }
        throw new TimeoutException("the sealer did not catch up");
    }

    [Fact]
    public async Task The_sealer_chains_new_audit_rows_and_a_tampered_row_is_named_raised_and_found_by_the_verify_verb()
    {
        using var f = Sealing();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        Assert.True((await rte.PostAsJsonAsync("/api/v1/services", new { serviceKey = "payments-api", name = "Payments API" })).IsSuccessStatusCode);
        Assert.True((await rte.PostAsJsonAsync("/api/v1/services", new { serviceKey = "ledger", name = "Ledger" })).IsSuccessStatusCode);

        var ok = await UntilSealed(rte);
        Assert.Equal(("Verified", true, 0L), (ok.GetProperty("status").GetString(), ok.GetProperty("hashChained").GetBoolean(), ok.GetProperty("pendingRows").GetInt64()));
        Assert.Equal(JsonValueKind.Null, ok.GetProperty("firstBad").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, ok.GetProperty("headHash").ValueKind);
        Assert.Equal(0, long.Parse(Scalar(f, "SELECT COUNT(*) FROM AuditEvents WHERE Hash IS NULL")));
        var run = await Json(await rte.GetAsync("/api/v1/audit/integrity?recheck=true"));
        Assert.Equal("Verified", run.GetProperty("status").GetString());

        // the verify verb agrees on a copy of the database
        var good = await Verify(f);
        Assert.Equal(0, good.Exit);
        Assert.Contains("verified", good.Output);

        // an attacker with file access removes the append-only trigger and edits a sealed row
        var victim = Scalar(f, "SELECT Id FROM AuditEvents WHERE Hash IS NOT NULL AND WorkspaceId = 'default' ORDER BY Id LIMIT 1 OFFSET 1");
        Sql(f, "DROP TRIGGER trg_Audit_NoUpdate");
        Sql(f, $"UPDATE AuditEvents SET AfterJson = '{{\"tampered\":true}}' WHERE Id = {victim}");

        var bad = await Json(await rte.GetAsync("/api/v1/audit/integrity?recheck=true"));
        Assert.Equal("Failed", bad.GetProperty("status").GetString());
        Assert.Equal(long.Parse(victim), bad.GetProperty("firstBad").GetProperty("id").GetInt64());
        Assert.False(string.IsNullOrWhiteSpace(bad.GetProperty("firstBad").GetProperty("reason").GetString()));
        var alerts = (await Json(await rte.GetAsync("/api/v1/sync/alerts"))).GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(alerts, a => a.GetProperty("source").GetString() == "Audit" && a.GetProperty("kind").GetString() == "IntegrityFailed" && !a.GetProperty("isResolved").GetBoolean());

        var cli = await Verify(f);
        Assert.Equal(1, cli.Exit);
        Assert.Contains($"FAILED at row {victim}", cli.Output);
    }

    [Fact]
    public async Task Deleting_the_newest_sealed_rows_is_found_because_the_chain_head_remembers_them()
    {
        using var f = Sealing();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        for (var i = 0; i < 3; i++) Assert.True((await rte.PostAsJsonAsync("/api/v1/services", new { serviceKey = $"svc-{i}", name = $"Svc {i}" })).IsSuccessStatusCode);
        await UntilSealed(rte);
        var newest = Scalar(f, "SELECT MAX(Id) FROM AuditEvents WHERE WorkspaceId = 'default'");
        Sql(f, "DROP TRIGGER trg_Audit_NoDelete");
        Sql(f, $"DELETE FROM AuditEvents WHERE Id = {newest}");
        var bad = await Json(await rte.GetAsync("/api/v1/audit/integrity?recheck=true"));
        Assert.Equal("Failed", bad.GetProperty("status").GetString());
        Assert.Equal(1, (await Verify(f)).Exit);
    }

    /// <summary>Runs the real command line (`ReleaseMgmt.Api verify-audit <file>`) against a copy of the host's database.</summary>
    private static async Task<(int Exit, string Output)> Verify(ApiFactory f)
    {
        var copy = Path.Combine(Path.GetTempPath(), $"verify-{Guid.NewGuid():N}.db");
        try
        {
            // The host's sealer and other background services write to this database; the online backup answers BUSY at once (it does not wait like a statement does), so try again.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    using var src = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={f.DbPath};Pooling=False"); src.Open();
                    using var dst = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={copy};Pooling=False"); dst.Open();
                    src.BackupDatabase(dst);
                    break;
                }
                catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode is 5 or 6 && attempt < 20) { await Task.Delay(250); }
            }
            var dll = Path.Combine(AppContext.BaseDirectory, "ReleaseMgmt.Api.dll");
            var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, ArgumentList = { dll, "verify-audit", copy } };
            using var p = Process.Start(psi)!;
            var output = await p.StandardOutput.ReadToEndAsync() + await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            return (p.ExitCode, output);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { File.Delete(copy); } catch (IOException) { } }
    }
}
