using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using nietras.SeparatedValues;
using ReleaseMgmt.Domain.Common;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>REOS-107/109: the release's owner, team, branch, on-call, type and environments travel through the CSV import and export, and a product imports a catalog service key.</summary>
public class ReleaseModelExchangeTests
{
    private static string Cell(string s) => s.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    private static string Csv(params string[][] rows) => string.Join("\r\n", rows.Select(r => string.Join(',', r.Select(Cell)))) + "\r\n";
    private static Task<HttpResponseMessage> Preview(HttpClient c, string kind, string csv, string mode = "Upsert") =>
        c.PostAsync($"/api/v1/imports/{kind}:preview?mode={mode}&fileName=t.csv", new StringContent(csv, new UTF8Encoding(false), "text/csv"));
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
    private static async Task<JsonElement> Commit(HttpClient c, JsonElement preview)
    {
        var r = await c.PostAsJsonAsync($"/api/v1/imports/{preview.GetProperty("jobId").GetString()}:commit", new { acknowledgeDecertify = false });
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, text);
        return JsonDocument.Parse(text).RootElement;
    }
    private static string Date(int days = 60) => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(days).ToString("yyyy-MM-dd");
    private static string[] Row(string csv, string title, int column = 0)
    {
        using var reader = Sep.New(',').Reader(o => o with { HasHeader = false, Unescape = true, DisableColCountCheck = true }).FromText(csv.TrimStart('﻿'));
        var rows = new List<string[]>();
        foreach (var r in reader) { var cells = new string[r.ColCount]; for (var i = 0; i < cells.Length; i++) cells[i] = r[i].ToString(); rows.Add(cells); }
        var header = rows[0];
        return rows.Skip(1).First(x => x[column] == title).Select((v, i) => header[i] + "=" + v).ToArray();
    }

    private static async Task<(ApiFactory F, HttpClient Rte)> World()
    {
        var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        await As(f, Roles.Viewer, "dana@x.com");
        Sql(f, @"INSERT INTO Teams(Id,Handle,Name) VALUES('tm1','ops','Operations'),('tm2','sre','SRE')");
        Assert.Equal(HttpStatusCode.OK, (await rte.PostAsJsonAsync("/api/v1/environments", new { name = "Staging", kind = "Staging" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rte.PostAsJsonAsync("/api/v1/environments", new { name = "Production", kind = "Production" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rte.PostAsJsonAsync("/api/v1/services", new { serviceKey = "payments-api", name = "Payments API" })).StatusCode);
        return (f, rte);
    }

    [Fact]
    public async Task Trains_import_the_release_fields_export_them_with_the_key_and_re_import_unchanged()
    {
        var (f, rte) = await World(); using var _ = f;
        var header = new[] { "Title", "TargetReleaseDate", "RiskTier", "Owner", "Team", "Branch", "OnCall", "ReleaseType", "Environments" };
        var p = await Json(await Preview(rte, "Trains", Csv(header, ["R27.01", Date(), "Low", "dana@x.com", "ops", "release/27.01", "@sre", "cert", "production, Staging"])));
        Assert.Equal(0, p.GetProperty("counts").GetProperty("errors").GetInt32());
        await Commit(rte, p);

        var id = Scalar(f, "SELECT Id FROM ReleaseTrains WHERE Title='R27.01'");
        var rel = await Json(await rte.GetAsync($"/api/v1/trains/{id}/release"));
        Assert.Equal("RLS-1", rel.GetProperty("key").GetString());
        Assert.Equal("dana", rel.GetProperty("owner").GetProperty("name").GetString());
        Assert.Equal("Operations", rel.GetProperty("ownerTeam").GetProperty("name").GetString());
        Assert.Equal("SRE", rel.GetProperty("onCallTeam").GetProperty("name").GetString());
        Assert.Equal("CERT", rel.GetProperty("type").GetProperty("name").GetString());
        Assert.Equal("release/27.01", rel.GetProperty("branch").GetString());
        Assert.Equal(new[] { "Staging", "Production" }, rel.GetProperty("environments").EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToArray());
        Assert.Equal("1", Scalar(f, $"SELECT COUNT(*) FROM AuditEvents WHERE EntityId='{id}' AND Action='ImportInsert'"));

        // The export carries them, canonical, with the key as a read-only column; the file re-imports as "unchanged"
        var csv = await rte.GetStringAsync("/api/v1/exports/trains.csv");
        var row = Row(csv, "R27.01");
        Assert.Contains("Owner=dana@x.com", row); Assert.Contains("Team=ops", row); Assert.Contains("Branch=release/27.01", row); Assert.Contains("OnCall='@sre", row);   // a cell starting with @ is escaped like any other (CSV injection); the import takes the escape off
        Assert.Contains("ReleaseType=CERT", row); Assert.Contains("Environments=Staging, Production", row); Assert.Contains("#Key=RLS-1", row);
        var again = await Json(await Preview(rte, "Trains", csv));
        Assert.Equal(0, again.GetProperty("counts").GetProperty("errors").GetInt32());
        Assert.Equal(1, again.GetProperty("counts").GetProperty("unchanged").GetInt32());

        // A changed branch and environment list is an update (audited, Version bumped, the key stays); a present-and-empty owner clears it; an absent column leaves a field alone
        var v = Scalar(f, $"SELECT Version FROM ReleaseTrains WHERE Id='{id}'");
        var upd = await Json(await Preview(rte, "Trains", Csv(["Title", "TargetReleaseDate", "RiskTier", "Owner", "Branch", "Environments"], ["R27.01", Date(), "Low", "", "release/27.01-rc", "Production"])));
        Assert.Equal(1, upd.GetProperty("counts").GetProperty("updated").GetInt32());
        await Commit(rte, upd);
        var after = await Json(await rte.GetAsync($"/api/v1/trains/{id}/release"));
        Assert.Equal("release/27.01-rc", after.GetProperty("branch").GetString());
        Assert.Equal(JsonValueKind.Null, after.GetProperty("owner").ValueKind);
        Assert.Equal(1, after.GetProperty("environments").GetArrayLength());
        Assert.Equal("Operations", after.GetProperty("ownerTeam").GetProperty("name").GetString());   // Team was not in the file
        Assert.Equal("RLS-1", after.GetProperty("key").GetString());
        Assert.True(int.Parse(Scalar(f, $"SELECT Version FROM ReleaseTrains WHERE Id='{id}'")) > int.Parse(v));
        Assert.Equal("1", Scalar(f, $"SELECT COUNT(*) FROM AuditEvents WHERE EntityId='{id}' AND Action='ImportUpdate'"));
    }

    [Fact]
    public async Task A_row_naming_something_that_does_not_exist_is_reported_with_its_column_and_nothing_is_committed()
    {
        var (f, rte) = await World(); using var _ = f;
        var header = new[] { "Title", "TargetReleaseDate", "RiskTier", "Owner", "Team", "OnCall", "ReleaseType", "Environments" };
        var p = await Json(await Preview(rte, "Trains", Csv(header,
            ["R1", Date(), "Low", "nobody@x.com", "", "", "", ""], ["R2", Date(), "Low", "", "no-such-team", "", "", ""], ["R3", Date(), "Low", "", "", "", "Alpha", ""],
            ["R4", Date(), "Low", "", "", "", "", "Staging, Mars"], ["R5", Date(), "Low", "@ops", "", "", "", ""])));
        var errors = p.GetProperty("errors").EnumerateArray().Select(e => (e.GetProperty("row").GetInt32(), e.GetProperty("column").GetString())).ToList();
        Assert.Equal([(1, "Owner"), (2, "Team"), (3, "ReleaseType"), (4, "Environments"), (5, "Owner")], errors);
        var refused = await rte.PostAsJsonAsync($"/api/v1/imports/{p.GetProperty("jobId").GetString()}:commit", new { acknowledgeDecertify = false });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("0", Scalar(f, "SELECT COUNT(*) FROM ReleaseTrains"));
    }

    [Fact]
    public async Task Products_import_a_service_key_and_export_it()
    {
        var (f, rte) = await World(); using var _ = f;
        var train = (await Json(await rte.PostAsJsonAsync("/api/v1/trains", new { title = "R27.01", targetReleaseDate = Date(), riskTier = "Low" }))).GetProperty("id").GetString()!;
        var bad = await Json(await Preview(rte, "Products", Csv(["Train", "ProductName", "VersionTag", "ProjectCode", "ServiceKey"], ["R27.01", "Payments API", "4.5.0", "PAY", "payments-apx"])));
        var e = bad.GetProperty("errors").EnumerateArray().Single();
        Assert.Equal("ServiceKey", e.GetProperty("column").GetString());
        Assert.Contains("payments-api", e.GetProperty("message").GetString());   // "Did you mean"

        var ok = await Json(await Preview(rte, "Products", Csv(["Train", "ProductName", "VersionTag", "ProjectCode", "ServiceKey"], ["R27.01", "Payments API", "4.5.0", "PAY", "PAYMENTS-API"], ["R27.01", "Card Portal", "2.1.0", "CRD", ""])));
        Assert.Equal(0, ok.GetProperty("counts").GetProperty("errors").GetInt32());
        await Commit(rte, ok);
        Assert.Equal("payments-api", Scalar(f, $"SELECT s.ServiceKey FROM BundledProducts p JOIN CatalogServices s ON s.Id=p.CatalogServiceId WHERE p.ReleaseTrainId='{train}' AND p.ProductName='Payments API'"));
        Assert.Equal("", Scalar(f, $"SELECT COALESCE(CatalogServiceId,'') FROM BundledProducts WHERE ReleaseTrainId='{train}' AND ProductName='Card Portal'"));
        var csv = await rte.GetStringAsync("/api/v1/exports/products.csv");
        Assert.Contains("ServiceKey=payments-api", Row(csv, "Payments API", column: 1));
        // the export re-imports unchanged; a present-and-empty key unlinks
        var again = await Json(await Preview(rte, "Products", csv));
        Assert.Equal(2, again.GetProperty("counts").GetProperty("unchanged").GetInt32());
        var unlink = await Json(await Preview(rte, "Products", Csv(["Train", "ProductName", "VersionTag", "ProjectCode", "ServiceKey"], ["R27.01", "Payments API", "4.5.0", "PAY", ""])));
        await Commit(rte, unlink);
        Assert.Equal("", Scalar(f, $"SELECT COALESCE(CatalogServiceId,'') FROM BundledProducts WHERE ReleaseTrainId='{train}' AND ProductName='Payments API'"));
    }

    [Fact]
    public async Task The_calendar_titles_a_releases_events_with_its_key_and_the_documents_print_its_fields()
    {
        var (f, rte) = await World(); using var _ = f;
        var id = (await Json(await rte.PostAsJsonAsync("/api/v1/trains", new { title = "R27.01", targetReleaseDate = Date(), riskTier = "Low", ownerTeamId = "tm1", releaseBranch = "release/27.01" }))).GetProperty("id").GetString()!;
        var path = (await Json(await rte.PostAsJsonAsync("/api/v1/me/ics-tokens", new { scope = "all" }))).GetProperty("path").GetString()!;
        var ics = await f.CreateClient().GetStringAsync(path);
        Assert.Contains("SUMMARY:RLS-1 R27.01: target release date", ics);
        Assert.DoesNotContain("release/27.01", ics);   // titles and times only: the branch and the people are not put in a link anyone can open
        // the evidence pack and release report generate with the release line (their text is in the PDF; generation failing would fail PdfExportTests too)
        var job = await rte.PostAsJsonAsync($"/api/v1/trains/{id}/export-jobs", new { kind = "ReleaseReportPdf" });
        Assert.True(job.IsSuccessStatusCode, await job.Content.ReadAsStringAsync());
    }
}
