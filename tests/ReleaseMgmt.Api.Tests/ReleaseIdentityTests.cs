using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using ReleaseMgmt.Api.Endpoints;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Infrastructure.Analytics;
using ReleaseMgmt.Infrastructure.Exports;
using UglyToad.PdfPig;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// REOS-137 (G15): every document that leaves the app says which workspace it came from and which release it is about: key, owner, team, environments.
/// Two workspaces side by side, so a file can only pass if it names its own and not the other's.
/// </summary>
[Collection("PdfEnvironment")]
public class ReleaseIdentityTests
{
    private sealed record Ws(string Name, string Train, string Key, string Owner, string Team, string Env, HttpClient Client);

    private sealed record World(ApiFactory F, Ws A, Ws B)
    {
        public Task<int> Run() => F.Services.GetRequiredService<ExportWorker>().RunOnceAsync();
    }

    private static string Scalar(ApiFactory f, string sql)
    {
        using var c = new SqliteConnection($"Data Source={f.DbPath};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand(); cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }

    /// <summary>Workspace "Payments" (the default one, train t1) and workspace "Beta Ledger" (train tb), each with an owner, a team, an environment, a blocker and a runbook.</summary>
    private static async Task<World> Setup()
    {
        Environment.SetEnvironmentVariable("Pdf__QuestPdfLicense", "Community");
        var f = new ApiFactory();
        var rteA = await As(f, Roles.RTE, "alpha-rte@x.com");
        string a = UserId(f, "alpha-rte@x.com");
        SeedTrain(f, a, a);
        var rteB = await WorkspaceIsolationTests.InB(f, Roles.RTE, "beta-rte@x.com");
        string b = UserId(f, "beta-rte@x.com");
        var t = "2026-10-01T00:00:00Z";
        Sql(f, $@"
            UPDATE Workspaces SET Name='Payments' WHERE Id='default';
            UPDATE Workspaces SET Name='Beta Ledger', KeyPrefix='BET' WHERE Id='{WorkspaceIsolationTests.B}';
            INSERT INTO Teams(Id,Handle,Name,WorkspaceId) VALUES('tma','core','Core Platform','default'),('tmb','ledger','Ledger Crew','{WorkspaceIsolationTests.B}');
            INSERT INTO Environments(Id,Name,Kind,SortOrder,CreatedAt,WorkspaceId) VALUES('enva','Prod EU','Production',1,'{t}','default'),('envb','Prod Asia','Production',1,'{t}','{WorkspaceIsolationTests.B}');
            UPDATE ReleaseTrains SET OwnerUserId='{a}', OwnerTeamId='tma' WHERE Id='t1';
            INSERT INTO TrainEnvironments(ReleaseTrainId,EnvironmentId) VALUES('t1','enva');
            INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CreatedAt,UpdatedAt,WorkspaceId,OwnerUserId,OwnerTeamId) VALUES('tb','Ledger close','2026-11-30','High','{t}','{t}','{WorkspaceIsolationTests.B}','{b}','tmb');
            INSERT INTO TrainEnvironments(ReleaseTrainId,EnvironmentId) VALUES('tb','envb');
            INSERT INTO Blockers(Id,ReleaseTrainId,Title,Severity,OwnerUserId,RaisedAt) VALUES('bla','t1','Pen-test unsigned','Medium','{a}','{t}'),('blb','tb','Audit open','Low','{b}','{t}');
            INSERT INTO RunbookSteps(Id,ReleaseTrainId,StepCode,Section,Title,OwnerUserId,PlannedStartAt,PlannedDurationMin) VALUES
              ('sa1','t1','R-001','Deploy','Deploy it','{a}','2026-10-30T06:00:00Z',30),('sb1','tb','R-001','Deploy','Deploy ledger','{b}','2026-11-30T06:00:00Z',30);
            INSERT INTO DeploymentWindows(Id,ReleaseTrainId,StartsAt,EndsAt) VALUES('wa','t1','2026-10-30T06:00:00Z','2026-10-30T10:00:00Z');");
        string Name(string id) => Scalar(f, $"SELECT DisplayName FROM Users WHERE Id='{id}'");
        return new World(f,
            new Ws("Payments", "t1", Scalar(f, "SELECT ReleaseKey FROM ReleaseTrains WHERE Id='t1'"), Name(a), "Core Platform", "Prod EU", rteA),
            new Ws("Beta Ledger", "tb", Scalar(f, "SELECT ReleaseKey FROM ReleaseTrains WHERE Id='tb'"), Name(b), "Ledger Crew", "Prod Asia", rteB));
    }

    private static string Flat(string s) => Regex.Replace(s, @"\s+", " ");
    private static string AllText(byte[] pdf) { using var d = PdfDocument.Open(pdf); return Flat(string.Join(" ", Enumerable.Range(1, d.NumberOfPages).Select(i => d.GetPage(i).Text))); }
    private static string FirstPage(byte[] pdf) { using var d = PdfDocument.Open(pdf); return Flat(d.GetPage(1).Text); }

    private static async Task<(string Id, byte[] Bytes)> Export(World w, Ws ws, string kind, string? format = null)
    {
        var r = await ws.Client.PostAsJsonAsync($"/api/v1/trains/{ws.Train}/export-jobs", new { kind, format });
        Assert.True(r.StatusCode == HttpStatusCode.Accepted, await r.Content.ReadAsStringAsync());
        var id = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
        await w.Run();
        var file = await ws.Client.GetAsync($"/api/v1/export-jobs/{id}/file");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        return (id, await file.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("ReleaseReport")]
    [InlineData("RunSheet")]
    [InlineData("Scorecard")]
    [InlineData("EvidencePack")]
    public async Task Each_pdf_names_its_own_workspace_key_owner_team_and_environments_on_page_one_and_the_other_workspaces_not_at_all(string kind)
    {
        var w = await Setup(); using var _f = w.F;
        foreach (var (mine, other) in new[] { (w.A, w.B), (w.B, w.A) })
        {
            var (_, pdf) = await Export(w, mine, kind);
            var p1 = FirstPage(pdf);
            foreach (var expected in new[] { mine.Name, mine.Key, mine.Owner, mine.Team, mine.Env })
                Assert.True(p1.Contains(expected), $"{kind} of {mine.Name}: page 1 has no '{expected}': {p1[..Math.Min(p1.Length, 600)]}");
            Assert.Contains("Workspace " + mine.Name, p1);
            var all = AllText(pdf);
            foreach (var foreign in new[] { other.Name, other.Key, other.Team, other.Env })
                Assert.False(all.Contains(foreign), $"{kind} of {mine.Name} mentions '{foreign}' of the other workspace");
        }
    }

    [Theory]
    [InlineData("ReleaseReport")]
    [InlineData("RunSheet")]
    [InlineData("Scorecard")]
    [InlineData("EvidencePack")]
    public async Task Every_page_header_and_footer_names_the_workspace_and_the_release_and_times_are_utc_with_the_display_zone_beside(string kind)
    {
        var w = await Setup(); using var _f = w.F;
        var (_, pdf) = await Export(w, w.A, kind);
        using var d = PdfDocument.Open(pdf);
        for (var i = 1; i <= d.NumberOfPages; i++)
        {
            var page = Flat(d.GetPage(i).Text);
            // the header names the workspace; the footer (page 1 of the pack: the identity line) names the key. Checked apart: a long footer wraps differently per platform's fonts.
            Assert.True(page.Contains("Payments"), $"{kind} page {i} does not name the workspace");
            Assert.True(page.Contains(w.A.Key), $"{kind} page {i} does not name the release key {w.A.Key}");
        }
        // D51: the generated-at stamp is UTC, with the display zone (America/Chicago) beside it
        var all = AllText(pdf);
        const string Beside = @"\((?:\w{3} ?\d{1,2} \w{3} )?\d\d:\d\d(?:–\d\d:\d\d)? CT\)";   // the weekday and date appear only when the display zone is on another day than UTC; the space after the weekday is optional because PdfPig on Windows drops it ("Wed30 Sep", REOS-148)
        Assert.Matches(@"generated \w{3} ?\d{1,2} \w{3} \d{4} \d\d:\d\d UTC " + Beside, all);
        Assert.DoesNotMatch(@"\d\d:\d\d CT", Regex.Replace(all, Beside, ""));   // no time is given in CT alone
    }

    [Fact]
    public async Task The_window_is_printed_in_utc_with_the_display_zone_beside_it()
    {
        var w = await Setup(); using var _f = w.F;
        var (_, pdf) = await Export(w, w.A, "ReleaseReport");
        Assert.Contains("Fri 30 Oct 06:00–10:00 UTC (01:00–05:00 CT)", AllText(pdf));
    }

    [Fact]
    public async Task The_pdf_metadata_names_the_key_and_the_workspace()
    {
        var w = await Setup(); using var _f = w.F;
        var (_, pdf) = await Export(w, w.B, "ReleaseReport");
        using var d = PdfDocument.Open(pdf);
        Assert.Contains(w.B.Key, d.Information.Title);
        Assert.Contains("Beta Ledger", d.Information.Subject);
    }

    [Fact]
    public async Task The_evidence_zip_manifest_names_the_workspace_and_the_release_on_every_row()
    {
        var w = await Setup(); using var _f = w.F;
        foreach (var ws in new[] { w.A, w.B })
        {
            var form = new MultipartFormDataContent { { new StringContent("Train"), "entityType" }, { new StringContent(ws.Train), "entityId" } };
            var file = new ByteArrayContent(Encoding.UTF8.GetBytes("scope memo of " + ws.Name)); file.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain");
            form.Add(file, "file", "memo.txt");
            Assert.Equal(HttpStatusCode.OK, (await ws.Client.PostAsync("/api/v1/attachments", form)).StatusCode);
            var (_, zipBytes) = await Export(w, ws, "EvidencePack", "zip");
            using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
            var manifest = new StreamReader(zip.GetEntry("manifest.csv")!.Open()).ReadToEnd().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal("name,size,sha256,gate_or_task,uploader,uploaded_at,locked,zip_path,workspace,release_key,owner,team,environments", manifest[0]);
            Assert.Equal(2, manifest.Length);
            Assert.EndsWith($",{ws.Name},{ws.Key},{ws.Owner},{ws.Team},{ws.Env}", manifest[1]);
        }
    }

    [Fact]
    public async Task Every_grid_is_stamped_with_its_workspace_and_a_grid_about_releases_with_the_release_model()
    {
        var w = await Setup(); using var _f = w.F;
        foreach (var ws in new[] { w.A, w.B })
        {
            var grids = JsonDocument.Parse(await ws.Client.GetStringAsync("/api/v1/exports/grids")).RootElement.EnumerateArray().Select(g => g.GetProperty("key").GetString()!).ToList();
            Assert.True(grids.Count >= 18, string.Join(",", grids));
            foreach (var grid in grids.Where(g => g is not ("audit" or "notifications")))
            {
                var csv = Encoding.UTF8.GetString(await ws.Client.GetByteArrayAsync($"/api/v1/exports/{grid}.csv")).TrimStart('\uFEFF');
                var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                var head = lines[0].Split(',');
                Assert.True(head.Contains("#Workspace"), $"{grid}: no #Workspace column in {lines[0]}");
                Assert.Equal("#Workspace", head[Array.IndexOf(head, "#Workspace")]);
                foreach (var row in lines.Skip(1)) Assert.Contains(ws.Name, row);   // no row of another workspace, and every row says whose it is
                if (grid is "blockers" or "products" or "gates" or "runbook-steps")
                {
                    Assert.Equal(["#Workspace", "#ReleaseKey", "#ReleaseOwner", "#ReleaseTeam", "#ReleaseEnvironments"], head[^5..]);
                    if (grid == "blockers") Assert.Contains($",{ws.Name},{ws.Key},{ws.Owner},{ws.Team},{ws.Env}", lines[1]);
                }
            }
            // the xlsx carries the same columns
            using var wb = new XLWorkbook(new MemoryStream(await ws.Client.GetByteArrayAsync("/api/v1/exports/blockers.xlsx")));
            var sheet = wb.Worksheet(1);
            var heads = sheet.Row(1).CellsUsed().Select(c => c.GetString()).ToList();
            Assert.Equal(["#Workspace", "#ReleaseKey", "#ReleaseOwner", "#ReleaseTeam", "#ReleaseEnvironments"], heads[^5..]);
            Assert.Equal(ws.Name, sheet.Cell(2, heads.IndexOf("#Workspace") + 1).GetString());
            Assert.Equal(ws.Key, sheet.Cell(2, heads.IndexOf("#ReleaseKey") + 1).GetString());
            Assert.Equal(ws.Env, sheet.Cell(2, heads.IndexOf("#ReleaseEnvironments") + 1).GetString());
        }
    }

    [Fact]
    public async Task The_trains_grid_gains_the_workspace_only_and_still_re_imports_unchanged()
    {
        var w = await Setup(); using var _f = w.F;
        var csv = Encoding.UTF8.GetString(await w.A.Client.GetByteArrayAsync("/api/v1/exports/trains.csv")).TrimStart('\uFEFF');
        var head = csv.Split("\r\n")[0].Split(',');
        Assert.Equal("#Workspace", head[^1]);
        Assert.Contains("Owner", head); Assert.Contains("Team", head); Assert.Contains("Environments", head); Assert.Contains("#Key", head);
        Assert.DoesNotContain("#ReleaseKey", head);
        Assert.Contains(w.A.Key, csv);
    }

    [Fact]
    public async Task The_analytics_workbook_names_the_workspace_and_adds_the_release_model_to_per_release_metrics()
    {
        var w = await Setup(); using var _f = w.F;
        var info = AnalyticsService.Find("M2")!;
        var win = new AnalyticsWindow(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), DateTime.Parse("2026-09-28T12:00:00Z").ToUniversalTime());
        // the endpoint: About names the workspace
        foreach (var ws in new[] { w.A, w.B })
        {
            using var about = new XLWorkbook(new MemoryStream(await ws.Client.GetByteArrayAsync("/api/v1/analytics/M2/xlsx?from=2026-01-01&to=2026-12-31&now=2026-09-28T12:00:00Z")));
            Assert.Equal("Workspace", about.Worksheet("About").Cell(6, 1).GetString());
            Assert.Equal(ws.Name, about.Worksheet("About").Cell(6, 2).GetString());
            Assert.Contains("last four columns", about.Worksheet("About").Cell(7, 2).GetString());
        }
        // a per-release metric: key, owner, team and environments follow the metric's columns, matched by title
        var facts = new Dictionary<string, ReleaseFact>
        {
            ["x1"] = new("x1", "R26.10", "RLS-284", "Rae Tran", "Core Platform", ["Prod EU", "Staging"]),
            ["x2"] = new("x2", "Twin", "RLS-285", "A", "B", []), ["x3"] = new("x3", "Twin", "RLS-286", "C", "D", []),
        };
        var bytes = AnalyticsExportEndpoints.Workbook(info, win, [new M2Row("R26.10", "2026-10-30", "2026-11-02", 3), new M2Row("Twin", "2026-10-30", "2026-10-30", 0), new M2Row("Gone", "2026-10-30", "2026-10-30", 0)],
            "Payments", facts);
        using var wb = new XLWorkbook(new MemoryStream(bytes));
        var ws2 = wb.Worksheet("M2");
        Assert.Equal(["release_key", "owner", "team", "environments"], Enumerable.Range(5, 4).Select(c => ws2.Cell(1, c).GetString()).ToArray());
        Assert.Equal(["RLS-284", "Rae Tran", "Core Platform", "Prod EU, Staging"], Enumerable.Range(5, 4).Select(c => ws2.Cell(2, c).GetString()).ToArray());
        Assert.True(Enumerable.Range(5, 4).All(c => ws2.Cell(3, c).IsEmpty()), "two releases share the title: nothing is guessed");
        Assert.True(Enumerable.Range(5, 4).All(c => ws2.Cell(4, c).IsEmpty()));
        // a workspace-wide metric has no release columns and says so
        var m1 = AnalyticsExportEndpoints.Workbook(AnalyticsService.Find("M1")!, win, [new M1Row(3, 2, 66.7)], "Payments", facts);
        using var wb1 = new XLWorkbook(new MemoryStream(m1));
        Assert.True(wb1.Worksheet("M1").Cell(1, 4).IsEmpty());
        Assert.Equal("All releases in the workspace", wb1.Worksheet("About").Cell(7, 2).GetString());
    }

    [Fact]
    public async Task The_analytics_workbook_of_each_workspace_equals_its_per_metric_exports_and_holds_nothing_of_the_other()
    {
        var w = await Setup(); using var _f = w.F;
        const string Q = "from=2026-01-01&to=2026-12-31&now=2026-09-28T12:00:00Z";
        var ids = Enumerable.Range(1, 24).Select(i => $"M{i}").ToList();
        foreach (var (mine, other) in new[] { (w.A, w.B), (w.B, w.A) })
        {
            using var wb = new XLWorkbook(new MemoryStream(await mine.Client.GetByteArrayAsync($"/api/v1/analytics/xlsx?{Q}")));
            Assert.Equal(["Parameters", .. ids], wb.Worksheets.Select(s => s.Name).ToArray());
            Assert.Equal(mine.Name, wb.Worksheet("Parameters").Cell(2, 2).GetString());
            foreach (var id in ids)
            {
                using var one = new XLWorkbook(new MemoryStream(await mine.Client.GetByteArrayAsync($"/api/v1/analytics/{id}/xlsx?{Q}")));
                WorkbookAssert.SameCells(one.Worksheet(id), wb.Worksheet(id), $"{mine.Name} {id}");
            }
            var all = wb.Worksheets.SelectMany(s => s.CellsUsed()).Select(c => c.GetString()).ToList();
            foreach (var foreign in new[] { other.Name, other.Key, other.Owner, other.Team, other.Env, other.Train == "tb" ? "Ledger close" : "Payments close" })
                Assert.DoesNotContain(all, t => t.Contains(foreign));
        }
        // sanity: the metrics are not empty, so the comparison above compared something (workspace A's Medium blocker, B's Low one)
        using var a = new XLWorkbook(new MemoryStream(await w.A.Client.GetByteArrayAsync($"/api/v1/analytics/xlsx?{Q}")));
        using var b = new XLWorkbook(new MemoryStream(await w.B.Client.GetByteArrayAsync($"/api/v1/analytics/xlsx?{Q}")));
        Assert.Equal("Medium", a.Worksheet("M9").Cell(2, 1).GetString());
        Assert.Equal("Low", b.Worksheet("M9").Cell(2, 1).GetString());
        Assert.Equal(2, a.Worksheet("M9").LastRowUsed()!.RowNumber());
    }
}
