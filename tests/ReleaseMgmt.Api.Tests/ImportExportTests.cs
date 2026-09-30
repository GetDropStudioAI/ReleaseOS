using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
using nietras.SeparatedValues;
using ReleaseMgmt.Domain.Common;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// REOS-48/49 (M7 "Done when"): an import with one bad row commits nothing and reports row + column; every grid export re-imports unchanged.
/// Also: unknown columns, # columns, BOM and header case, limits, Append/Upsert, audited updates, plan locks, decertify acknowledgement, stale previews, roles and CSV-injection escaping in both formats.
/// </summary>
public class ImportExportTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
    private static string Q(string s) => s.Replace("'", "''");

    // ---- world ------------------------------------------------------------------------------------------------------------------------
    private sealed record World(ApiFactory F, HttpClient Rte, string RteId, string GovId);

    private static async Task<World> Build(bool requireIfMatch = false)
    {
        var f = new ApiFactory(requireIfMatch: requireIfMatch);
        var rte = await As(f, Roles.RTE, "rte@x.com");
        await As(f, Roles.GovernanceOfficer, "gov@x.com");
        var rid = UserId(f, "rte@x.com"); var gid = UserId(f, "gov@x.com");
        SeedTrain(f, rid, gid);   // t1 'R26.10' (Planning): gates g1 Code Freeze, g2 Compliance Sign-off; tasks k1, k2
        Sql(f, $"""
            INSERT INTO Users(Id,Email,DisplayName,Role,Handle,IsActive) VALUES
              ('u-dana','dana@x.com','Dana Ortiz','Viewer','dana',1),('u-off','off@x.com','Old Hand','Viewer',NULL,0),
              ('u-eq','eq@x.com','=SUM(1+1)','Viewer',NULL,1),('u-minus','minus@x.com','-Dash, "quoted"','Viewer',NULL,1),
              ('u-plus','plus@x.com','+plus','Viewer',NULL,1),('u-at','at@x.com','@at','Viewer',NULL,1),('u-apos','apos@x.com','''tis','Viewer',NULL,1);
            INSERT INTO Teams(Id,Handle,Name) VALUES ('team-desk','desk','Support desk'),('team-web','web','Web');
            INSERT INTO TeamMembers(TeamId,UserId) VALUES ('team-desk','u-dana'),('team-desk','{rid}');
            DELETE FROM Holidays;
            INSERT INTO Holidays(Day,Name) VALUES ('2026-11-26','Thanksgiving'),('2026-12-25','Christmas');
            UPDATE ReleaseTrains SET TemplateId=(SELECT Id FROM TrainTemplates WHERE Name='Standard release'), ChangeTicketNumber='CHG0030001' WHERE Id='t1';
            INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CurrentStatus,CreatedAt,UpdatedAt) VALUES
              ('t2','R26.11 Live, "quoted" train','2026-11-20','Moderate','Executing','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z');
            INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CurrentStatus,CloseCode,ActualEndAt,CreatedAt,UpdatedAt) VALUES
              ('t3','=HYPERLINK("http://evil")','2026-09-01','Low','Complete','Successful','2026-09-01T10:00:00Z','2026-08-01T00:00:00Z','2026-09-01T00:00:00Z');
            INSERT INTO DeploymentWindows(Id,ReleaseTrainId,StartsAt,EndsAt) VALUES ('w1','t1','2026-10-30T01:00:00Z','2026-10-30T05:00:00Z');
            INSERT INTO BundledProducts(Id,ReleaseTrainId,ProductName,VersionTag,ProjectCode) VALUES
              ('p1','t1','Payments API','4.5.0','PAY'),('p2','t1','Onboarding UI','007','2026-11-13'),('p3','t2','Merchant API','2.4.0','MER');
            INSERT INTO StageGates(Id,ReleaseTrainId,GateName,GateClass,SequenceOrder,OffsetDays,DueOn,RequiredBeforeStatus,OwnerTeamId,Status,LastChangedByUserId) VALUES
              ('g3','t1','CAB Approval','Compliance',3,1,'2026-10-29','Executing','team-desk','Pending','{rid}');
            INSERT INTO StageGates(Id,ReleaseTrainId,GateName,GateClass,SequenceOrder,OffsetDays,DueOn,RequiredBeforeStatus,OwnerUserId,Status,CertifiedByUserId,CertifiedAt,LastChangedByUserId) VALUES
              ('g5','t1','Sign-off','Standard',4,0,'2026-10-30','Complete','{rid}','Certified','{gid}','2026-10-01T00:00:00Z','{rid}'),
              ('g4','t2','QA Sign-off','Standard',1,3,'2026-11-17','Gated','u-dana','Certified','{gid}','2026-10-01T00:00:00Z','{rid}');
            INSERT INTO ChecklistTasks(Id,StageGateId,BundledProductId,TaskDescription,OwnerUserId,OwnerTeamId,IsCompleted,SequenceOrder,CompletedAt,CompletedByUserId) VALUES
              ('k3','g4','p3','Run regression, all suites',NULL,'team-web',1,1,'2026-10-02T00:00:00Z','{rid}'),
              ('k5','g5','p1','Sign the release note','{rid}',NULL,1,1,'2026-10-02T00:00:00Z','{rid}');
            INSERT INTO RunbookSteps(Id,ReleaseTrainId,BundledProductId,StepCode,Section,Title,Instructions,OwnerUserId,OwnerTeamId,PlannedStartAt,PlannedDurationMin) VALUES
              ('s1','t1',NULL,'R-001','PreCheck','Confirm Go',NULL,'u-dana',NULL,'2026-10-30T01:00:00Z',5),
              ('s2','t1','p1','R-002','Deploy','Deploy API','step 1
            step 2, with a comma',NULL,'team-web','2026-10-30T01:05:00Z',15),
              ('s3','t1',NULL,'R-003','Verify','Smoke test',NULL,'{rid}',NULL,'2026-10-30T01:20:00Z',20),
              ('s4','t2',NULL,'R-001','Deploy','Live step',NULL,'u-dana',NULL,'2026-11-20T01:00:00Z',10);
            INSERT INTO StepDependencies(StepId,DependsOnStepId) VALUES ('s2','s1'),('s3','s1'),('s3','s2');
            INSERT INTO ExternalLinks(Id,ReleaseTrainId,EntityType,EntityId,SourceSystem,ExternalKey) VALUES
              ('l1','t1','Product','p1','Jira','PAY/4.5.0'),('l2','t1','Train','t1','ServiceNow','CHG0030001'),('l3','t1','Gate','g1','Jira','PAY-123'),('l4','t2','RunbookStep','s4','Jira','ONB-9');
            INSERT INTO Blockers(Id,ReleaseTrainId,BundledProductId,StageGateId,Title,Severity,OwnerUserId,RaisedAt) VALUES
              ('b1','t1','p1','g1','=cmd|'' /C calc''!A0','High','{rid}','2026-10-03T00:00:00Z'),('b2','t1',NULL,NULL,'@risk','Low',NULL,'2026-10-04T00:00:00Z'),('b3','t1',NULL,NULL,'-1+1','Low',NULL,'2026-10-05T00:00:00Z'),
              ('b4','t1',NULL,NULL,'+1','Low',NULL,'2026-10-06T00:00:00Z');
            INSERT INTO KnownIssues(Id,ReleaseTrainId,Title,Severity,Workaround,Status,ExternalKey,RaisedAt) VALUES ('ki1','t3','Slow login','Medium','Retry','Open','INC1','2026-09-02T00:00:00Z');
            INSERT INTO FreezeWindows(Id,Name,Kind,StartsAt,EndsAt,ProductPattern,CreatedByUserId) VALUES ('fw1','Year-end freeze','Freeze','2026-12-20T00:00:00Z','2027-01-03T00:00:00Z','Pay*','{gid}');
            INSERT INTO Attachments(Id,ReleaseTrainId,EntityType,EntityId,FileName,ContentType,SizeBytes,Sha256,StoragePath,UploadedByUserId,UploadedAt) VALUES
              ('a1','t1','Gate','g1','=evil.pdf','application/pdf',12,'{new string('a', 64)}','stored/a1','{rid}','2026-10-02T00:00:00Z');
            INSERT INTO Notifications(Id,UserId,Kind,EntityType,EntityId,Message,CreatedAt) VALUES
              ('n1','{rid}','GateEntered','StageGate','g1','for rte','2026-10-02T00:00:00Z'),('n2','{gid}','GateEntered','StageGate','g1','for gov','2026-10-02T00:00:00Z');
            INSERT INTO SyncAlerts(Id,ReleaseTrainId,SourceSystem,Kind,Fingerprint,ErrorMessage,FirstOccurredAt,LastOccurredAt) VALUES ('sa1','t1','Jira','Stalled','fp1','Jira is stalled','2026-10-02T00:00:00Z','2026-10-02T00:00:00Z');
            """);
        return new World(f, rte, rid, gid);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------------------------
    private static Task<HttpResponseMessage> Preview(HttpClient c, string kind, string csv, string mode = "Upsert", string name = "test.csv") =>
        c.PostAsync($"/api/v1/imports/{kind}:preview?mode={mode}&fileName={Uri.EscapeDataString(name)}", new StringContent(csv, new UTF8Encoding(false), "text/csv"));

    private static Task<HttpResponseMessage> Commit(HttpClient c, string jobId, int? ifMatch = null, bool ack = false)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/imports/{jobId}:commit") { Content = JsonContent.Create(new { acknowledgeDecertify = ack }) };
        if (ifMatch is int v) req.Headers.TryAddWithoutValidation("If-Match", v.ToString());
        return c.SendAsync(req);
    }

    private static async Task<JsonElement> Ok(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    private static string Cell(string s) => s.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    private static string Csv(params string[][] rows) => string.Join("\r\n", rows.Select(r => string.Join(',', r.Select(Cell)))) + "\r\n";

    private static List<string[]> ParseCsv(string csv)
    {
        using var reader = Sep.New(',').Reader(o => o with { HasHeader = false, Unescape = true, DisableColCountCheck = true }).FromText(csv.TrimStart('﻿'));
        var rows = new List<string[]>();
        foreach (var r in reader) { var cells = new string[r.ColCount]; for (var i = 0; i < cells.Length; i++) cells[i] = r[i].ToString(); rows.Add(cells); }
        return rows;
    }

    private static string XlsxToCsv(byte[] xlsx)
    {
        using var wb = new XLWorkbook(new MemoryStream(xlsx));
        var ws = wb.Worksheet(1);
        var last = ws.LastRowUsed()?.RowNumber() ?? 1; var cols = ws.LastColumnUsed()?.ColumnNumber() ?? 1;
        var rows = new List<string[]>();
        for (var r = 1; r <= last; r++)
            rows.Add(Enumerable.Range(1, cols).Select(c =>
            {
                var cell = ws.Cell(r, c);
                return cell.IsEmpty() ? "" : cell.Value.IsNumber ? ((long)cell.Value.GetNumber()).ToString() : cell.GetString();
            }).ToArray());
        return Csv(rows.ToArray());
    }

    private static string Fingerprint(ApiFactory f, params string[] except)
    {
        using var c = new SqliteConnection($"Data Source={f.DbPath};Pooling=False");
        c.Open();
        var tables = new List<string>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) tables.Add(rd.GetString(0));
        }
        var sb = new StringBuilder();
        foreach (var t in tables.Where(t => !except.Contains(t)))
        {
            sb.Append('#').Append(t).Append('\n');
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT * FROM \"{t}\" ORDER BY rowid";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) { for (var i = 0; i < rd.FieldCount; i++) sb.Append(rd.IsDBNull(i) ? "\0" : Convert.ToString(rd.GetValue(i))).Append('|'); sb.Append('\n'); }
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static int Counts(JsonElement preview, string what) => preview.GetProperty("counts").GetProperty(what).GetInt32();

    // ---- THE acceptance: one bad row commits nothing and reports row + column -------------------------------------------------------------
    [Fact]
    public async Task An_import_with_one_bad_row_in_a_large_file_commits_nothing_and_reports_exactly_that_row_and_column()
    {
        var w = await Build(); using var _ = w.F;
        var rows = new List<string[]> { new[] { "Day", "Name" } };
        for (var i = 0; i < 3000; i++) rows.Add([new DateOnly(2030, 1, 1).AddDays(i).ToString("yyyy-MM-dd"), $"Holiday {i}"]);
        rows[1234] = ["2030-13-45", "Not a day"];   // data row 1234
        var before = Fingerprint(w.F, "ImportJobs");

        var p = await Json(await Preview(w.Rte, "Holidays", Csv([.. rows]), name: "big.csv"));
        var errors = p.GetProperty("errors").EnumerateArray().ToList();
        var e = Assert.Single(errors);
        Assert.Equal(1234, e.GetProperty("row").GetInt32());
        Assert.Equal("Day", e.GetProperty("column").GetString());
        Assert.Equal(1, Counts(p, "errors")); Assert.Equal(2999, Counts(p, "new"));
        Assert.Equal(3000, p.GetProperty("rowCount").GetInt32());

        var commit = await Commit(w.Rte, p.GetProperty("jobId").GetString()!);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, commit.StatusCode);
        Assert.Equal("ImportHasErrors", (await Json(commit)).GetProperty("guard").GetString());
        Assert.Equal(before, Fingerprint(w.F, "ImportJobs"));   // NOT ONE row changed in ANY table, and no audit row either
        Assert.Equal("2", Scalar(w.F, "SELECT COUNT(*) FROM Holidays"));

        var job = await Json(await w.Rte.GetAsync($"/api/v1/imports/{p.GetProperty("jobId").GetString()}"));
        Assert.Equal("Previewed", job.GetProperty("status").GetString());
        Assert.Equal(1, job.GetProperty("errorCount").GetInt32());
        Assert.Equal(1234, job.GetProperty("errors")[0].GetProperty("row").GetInt32());
        Assert.Equal("Day", job.GetProperty("errors")[0].GetProperty("column").GetString());
        Assert.Equal("1", Scalar(w.F, "SELECT COUNT(*) FROM ImportJobs WHERE ErrorCount=1 AND Status='Previewed' AND json_extract(ErrorsJson,'$[0].row')=1234 AND json_extract(ErrorsJson,'$[0].column')='Day'"));
    }

    [Fact]
    public async Task A_large_good_file_commits_every_row_with_its_own_audit_row_in_one_go()
    {
        var w = await Build(); using var _ = w.F;
        var rows = new List<string[]> { new[] { "Day", "Name" } };
        for (var i = 0; i < 2000; i++) rows.Add([new DateOnly(2030, 1, 1).AddDays(i).ToString("yyyy-MM-dd"), $"Holiday {i}"]);
        var p = await Ok(await Preview(w.Rte, "Holidays", Csv([.. rows]), "Append", "large.csv"));
        Assert.Equal(2000, Counts(p, "new"));
        var done = await Ok(await Commit(w.Rte, p.GetProperty("jobId").GetString()!));
        Assert.Equal(2000, done.GetProperty("inserted").GetInt32());
        Assert.Equal("2002", Scalar(w.F, "SELECT COUNT(*) FROM Holidays"));
        Assert.Equal("2000", Scalar(w.F, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='Holiday' AND Action='ImportInsert'"));
    }

    [Fact]
    public async Task One_bad_cell_in_a_multi_column_kind_names_the_column_and_blocks_the_good_rows_too()
    {
        var w = await Build(); using var _ = w.F;
        var before = Fingerprint(w.F, "ImportJobs");
        var csv = Csv(["Title", "TargetReleaseDate", "RiskTier"], ["R27.01 Good", "2027-01-15", "Low"], ["R27.02 Bad", "2027-02-15", "Sky-high"], ["R27.03 Good", "2027-03-15", "High"]);
        var p = await Json(await Preview(w.Rte, "Trains", csv, "Append"));
        var e = Assert.Single(p.GetProperty("errors").EnumerateArray());
        Assert.Equal(2, e.GetProperty("row").GetInt32()); Assert.Equal("RiskTier", e.GetProperty("column").GetString());
        Assert.Contains("Moderate", e.GetProperty("message").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Commit(w.Rte, p.GetProperty("jobId").GetString()!)).StatusCode);
        Assert.Equal(before, Fingerprint(w.F, "ImportJobs"));
        var rows = p.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("op").GetString()!).ToArray();
        Assert.Equal(["new", "error", "new"], rows);
    }

    [Fact]
    public async Task The_trigger_is_the_backstop_a_job_with_errors_cannot_be_marked_committed_even_by_hand()
    {
        var w = await Build(); using var _ = w.F;
        var p = await Json(await Preview(w.Rte, "Holidays", Csv(["Day", "Name"], ["nope", "x"])));
        var id = p.GetProperty("jobId").GetString()!;
        var ex = Assert.Throws<SqliteException>(() => Sql(w.F, $"UPDATE ImportJobs SET Status='Committed', CommittedAt='2026-10-01T00:00:00Z' WHERE Id='{id}'"));
        Assert.Contains("Import has row errors", ex.Message);
    }

    // ---- header rules -----------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task An_unknown_column_is_an_error_and_a_status_column_is_refused_with_its_own_message()
    {
        var w = await Build(); using var _ = w.F;
        var p = await Json(await Preview(w.Rte, "Holidays", Csv(["Day", "Name", "Colour"], ["2030-01-01", "New Year", "red"])));
        var e = Assert.Single(p.GetProperty("errors").EnumerateArray());
        Assert.Equal(0, e.GetProperty("row").GetInt32()); Assert.Equal("Colour", e.GetProperty("column").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Commit(w.Rte, p.GetProperty("jobId").GetString()!)).StatusCode);

        var s = await Json(await Preview(w.Rte, "Trains", Csv(["Title", "TargetReleaseDate", "RiskTier", "Status"], ["R27.01", "2027-01-15", "Low", "Complete"]), "Append"));
        var se = s.GetProperty("errors").EnumerateArray().Single(x => x.GetProperty("column").GetString() == "Status");
        Assert.Contains("workflow", se.GetProperty("message").GetString());
        Assert.Equal("0", Scalar(w.F, "SELECT COUNT(*) FROM ReleaseTrains WHERE Title='R27.01'"));
    }

    [Fact]
    public async Task Hash_columns_are_skipped_and_a_BOM_with_odd_header_case_is_fine()
    {
        var w = await Build(); using var _ = w.F;
        var csv = "﻿" + Csv(["#Version", "DAY", "name", "#anything at all"], ["9", "2030-01-01", "New Year", "x"]);
        var bytes = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.TrimStart('﻿'))).ToArray();   // a real EF BB BF prefix
        var r = await w.Rte.PostAsync("/api/v1/imports/Holidays:preview?mode=Append&fileName=bom.csv", new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue("text/csv") } });
        var p = await Ok(r);
        Assert.Equal(0, Counts(p, "errors")); Assert.Equal(1, Counts(p, "new"));
        Assert.Equal(["#Version", "#anything at all"], p.GetProperty("skippedColumns").EnumerateArray().Select(x => x.GetString()!).ToArray());
        Assert.Equal(HttpStatusCode.OK, (await Commit(w.Rte, p.GetProperty("jobId").GetString()!)).StatusCode);
        Assert.Equal("New Year", Scalar(w.F, "SELECT Name FROM Holidays WHERE Day='2030-01-01'"));
    }

    [Fact]
    public async Task A_missing_required_column_and_a_duplicate_column_are_reported_at_row_zero()
    {
        var w = await Build(); using var _ = w.F;
        var p = await Json(await Preview(w.Rte, "Users", Csv(["Email", "email", "Role"], ["a@x.com", "a@x.com", "Viewer"]), "Append"));
        var errs = p.GetProperty("errors").EnumerateArray().ToList();
        Assert.Contains(errs, e => e.GetProperty("column").GetString() == "email");
        Assert.Contains(errs, e => e.GetProperty("column").GetString() == "DisplayName");
        Assert.All(errs, e => Assert.Equal(0, e.GetProperty("row").GetInt32()));
    }

    // ---- limits -----------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Ten_thousand_rows_are_accepted_and_ten_thousand_and_one_are_refused_and_recorded_as_rejected()
    {
        var w = await Build(); using var _ = w.F;
        string Big(int n) { var sb = new StringBuilder("Day,Name\r\n"); for (var i = 0; i < n; i++) sb.Append(new DateOnly(2030, 1, 1).AddDays(i).ToString("yyyy-MM-dd")).Append(",H\r\n"); return sb.ToString(); }
        var ok = await Ok(await Preview(w.Rte, "Holidays", Big(10_000), "Append"));
        Assert.Equal(10_000, Counts(ok, "new"));

        var over = await Preview(w.Rte, "Holidays", Big(10_001), "Append", "over.csv");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, over.StatusCode);
        var body = await Json(over);
        Assert.Equal("ImportRejected", body.GetProperty("guard").GetString());
        Assert.Contains("10,000", body.GetProperty("message").GetString());
        Assert.Equal("1", Scalar(w.F, "SELECT COUNT(*) FROM ImportJobs WHERE Status='Rejected' AND FileName='over.csv' AND ErrorCount=1"));
        Assert.Equal("2", Scalar(w.F, "SELECT COUNT(*) FROM Holidays"));
    }

    [Fact]
    public async Task A_file_over_5_MB_is_refused_413_as_a_raw_body_and_as_a_multipart_upload()
    {
        var w = await Build(); using var _ = w.F;
        var big = "Day,Name\r\n2030-01-01," + new string('x', 5 * 1024 * 1024) + "\r\n";
        var raw = await Preview(w.Rte, "Holidays", big);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, raw.StatusCode);
        Assert.Equal("ImportTooLarge", (await Json(raw)).GetProperty("guard").GetString());

        var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(big)), "file", "big.csv" } };
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await w.Rte.PostAsync("/api/v1/imports/Holidays:preview?mode=Append", form)).StatusCode);
        Assert.Equal("0", Scalar(w.F, "SELECT COUNT(*) FROM ImportJobs"));
    }

    [Fact]
    public async Task A_multipart_upload_works_and_its_file_name_is_kept_without_a_path()
    {
        var w = await Build(); using var _ = w.F;
        var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(Csv(["Day", "Name"], ["2030-01-01", "New Year"]))), "file", "..\\..\\evil\\holidays.csv" } };
        var p = await Ok(await w.Rte.PostAsync("/api/v1/imports/Holidays:preview?mode=Append", form));
        Assert.Equal("holidays.csv", p.GetProperty("fileName").GetString());
        Assert.Equal(64, p.GetProperty("sha256").GetString()!.Length);
        Assert.Equal("1", Scalar(w.F, "SELECT COUNT(*) FROM ImportJobs WHERE FileName='holidays.csv' AND length(Sha256)=64"));
    }

    [Theory]
    [InlineData("xlsx")]
    [InlineData("latin1")]
    [InlineData("binary")]
    [InlineData("empty")]
    public async Task Files_that_are_not_UTF8_CSV_are_refused_with_a_reason(string what)
    {
        var w = await Build(); using var _ = w.F;
        byte[] bytes = what switch
        {
            "xlsx" => [0x50, 0x4B, 3, 4, 20, 0, 0, 0],
            "latin1" => Encoding.Latin1.GetBytes("Day,Name\r\n2030-01-01,Café\r\n"),
            "binary" => [(byte)'D', 0, (byte)'a', 0, 1, 2],
            _ => [],
        };
        var r = await w.Rte.PostAsync("/api/v1/imports/Holidays:preview?mode=Append", new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue("text/csv") } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        var msg = (await Json(r)).GetProperty("message").GetString()!;
        Assert.True(what switch { "xlsx" => msg.Contains("Excel"), "latin1" => msg.Contains("UTF-8"), "binary" => msg.Contains("binary"), _ => msg.Contains("empty") }, msg);
    }

    [Fact]
    public async Task Quoted_fields_with_commas_quotes_and_line_breaks_import_as_RFC_4180_says()
    {
        var w = await Build(); using var _ = w.F;
        var csv = "Day,Name\r\n2030-01-01,\"He said \"\"hi\"\", then left\"\r\n";
        var p = await Ok(await Preview(w.Rte, "Holidays", csv, "Append"));
        Assert.Equal(0, Counts(p, "errors"));
        Assert.Equal("He said \"hi\", then left", p.GetProperty("rows")[0].GetProperty("cells").GetProperty("Name").GetString());
        var ragged = await Json(await Preview(w.Rte, "Holidays", "Day,Name\r\n2030-01-01\r\n", "Append"));
        Assert.Equal("(row)", ragged.GetProperty("errors")[0].GetProperty("column").GetString());
    }

    // ---- Append / Upsert, audit, versions ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task Append_refuses_an_existing_key_at_its_row_and_column_and_commits_nothing()
    {
        var w = await Build(); using var _ = w.F;
        var before = Fingerprint(w.F, "ImportJobs");
        var p = await Json(await Preview(w.Rte, "Holidays", Csv(["Day", "Name"], ["2030-01-01", "New Year"], ["2026-11-26", "Thanksgiving again"]), "Append"));
        var e = Assert.Single(p.GetProperty("errors").EnumerateArray());
        Assert.Equal(2, e.GetProperty("row").GetInt32()); Assert.Equal("Day", e.GetProperty("column").GetString());
        Assert.Contains("already exists", e.GetProperty("message").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Commit(w.Rte, p.GetProperty("jobId").GetString()!)).StatusCode);
        Assert.Equal(before, Fingerprint(w.F, "ImportJobs"));
    }

    [Fact]
    public async Task A_duplicate_key_inside_the_file_is_an_error_on_the_second_row()
    {
        var w = await Build(); using var _ = w.F;
        var p = await Json(await Preview(w.Rte, "Holidays", Csv(["Day", "Name"], ["2030-01-01", "A"], ["2030-01-02", "B"], ["2030-01-01", "C"]), "Append"));
        var e = Assert.Single(p.GetProperty("errors").EnumerateArray());
        Assert.Equal(3, e.GetProperty("row").GetInt32()); Assert.Contains("row 1", e.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Upsert_updates_new_and_changed_rows_only_and_audits_each_with_before_and_after_and_the_job_with_counts()
    {
        var w = await Build(); using var _ = w.F;
        var p = await Ok(await Preview(w.Rte, "Holidays", Csv(["Day", "Name"], ["2026-11-26", "Thanksgiving Day"], ["2026-12-24", "Christmas Eve"], ["2026-12-25", "Christmas"])));
        Assert.Equal((1, 1, 1, 0), (Counts(p, "new"), Counts(p, "updated"), Counts(p, "unchanged"), Counts(p, "errors")));
        var updated = p.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("op").GetString() == "updated");
        Assert.Equal("Thanksgiving", updated.GetProperty("before").GetProperty("Name").GetString());     // the struck-through old value
        Assert.Equal(["Name"], updated.GetProperty("changed").EnumerateArray().Select(x => x.GetString()!).ToArray());
        Assert.Equal("0", Scalar(w.F, "SELECT COUNT(*) FROM AuditEvents WHERE Action LIKE 'Import%'"));  // preview writes no audit

        var done = await Ok(await Commit(w.Rte, p.GetProperty("jobId").GetString()!));
        Assert.Equal((1, 1, 1), (done.GetProperty("inserted").GetInt32(), done.GetProperty("updated").GetInt32(), done.GetProperty("unchanged").GetInt32()));
        Assert.Equal("Thanksgiving Day", Scalar(w.F, "SELECT Name FROM Holidays WHERE Day='2026-11-26'"));
        Assert.Equal("2", Scalar(w.F, "SELECT Version FROM Holidays WHERE Day='2026-11-26'"));       // Version stamped on the changed row
        Assert.Equal("1", Scalar(w.F, "SELECT Version FROM Holidays WHERE Day='2026-12-25'"));       // the unchanged one was not touched
        Assert.Equal("1", Scalar(w.F, "SELECT Version FROM Holidays WHERE Day='2026-12-24'"));

        Assert.Equal("Thanksgiving", Scalar(w.F, "SELECT json_extract(BeforeJson,'$.Name') FROM AuditEvents WHERE EntityType='Holiday' AND Action='ImportUpdate'"));
        Assert.Equal("Thanksgiving Day", Scalar(w.F, "SELECT json_extract(AfterJson,'$.Name') FROM AuditEvents WHERE EntityType='Holiday' AND Action='ImportUpdate'"));
        Assert.Equal("2026-11-26", Scalar(w.F, "SELECT EntityId FROM AuditEvents WHERE EntityType='Holiday' AND Action='ImportUpdate'"));
        Assert.Equal(w.RteId, Scalar(w.F, "SELECT ActorUserId FROM AuditEvents WHERE EntityType='Holiday' AND Action='ImportUpdate'"));
        Assert.Equal("1", Scalar(w.F, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='Holiday' AND Action='ImportInsert' AND EntityId='2026-12-24'"));
        Assert.Equal("0", Scalar(w.F, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='Holiday' AND EntityId='2026-12-25'"));          // unchanged: no audit row
        Assert.Equal("1", Scalar(w.F, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='ImportJob' AND Action='Commit' AND json_extract(AfterJson,'$.inserted')=1 AND json_extract(AfterJson,'$.updated')=1 AND json_extract(AfterJson,'$.unchanged')=1"));
        Assert.Equal("Committed", Scalar(w.F, "SELECT Status FROM ImportJobs"));
        Assert.Equal("1", Scalar(w.F, "SELECT COUNT(*) FROM ImportJobs WHERE CommittedAt IS NOT NULL AND Version=2"));

        var again = await Commit(w.Rte, p.GetProperty("jobId").GetString()!);
        Assert.Equal("ImportCommitted", (await Json(again)).GetProperty("guard").GetString());       // no double apply
    }

    [Fact]
    public async Task Upserting_a_train_changes_only_what_differs_recomputes_gate_due_dates_and_bumps_the_version()
    {
        var w = await Build(); using var _ = w.F;
        var csv = Csv(["Title", "TargetReleaseDate", "RiskTier", "Template", "ChangeTicketNumber", "WindowStart", "WindowEnd"],
                      ["R26.10", "2026-11-06", "High", "Standard release", "CHG0099999", "2026-11-06T01:00:00Z", "2026-11-06T06:00:00+00:00"]);
        var p = await Ok(await Preview(w.Rte, "Trains", csv));
        var row = p.GetProperty("rows")[0];
        Assert.Equal("updated", row.GetProperty("op").GetString());
        Assert.Equal(["TargetReleaseDate", "ChangeTicketNumber", "WindowStart", "WindowEnd"], row.GetProperty("changed").EnumerateArray().Select(x => x.GetString()!).ToArray());
        await Ok(await Commit(w.Rte, p.GetProperty("jobId").GetString()!));
        Assert.Equal("2026-11-06", Scalar(w.F, "SELECT TargetReleaseDate FROM ReleaseTrains WHERE Id='t1'"));
        Assert.Equal("CHG0099999", Scalar(w.F, "SELECT ChangeTicketNumber FROM ReleaseTrains WHERE Id='t1'"));
        Assert.Equal("2", Scalar(w.F, "SELECT Version FROM ReleaseTrains WHERE Id='t1'"));
        Assert.Equal(w.RteId, Scalar(w.F, "SELECT LastChangedByUserId FROM ReleaseTrains WHERE Id='t1'"));
        Assert.Equal("2026-10-30", Scalar(w.F, "SELECT DueOn FROM StageGates WHERE Id='g1'"));   // 5 business days before 2026-11-06 (Fri): 30 Oct
        Assert.Equal("2", Scalar(w.F, "SELECT Version FROM StageGates WHERE Id='g1'"));
        Assert.Equal("2026-11-06T06:00:00Z", Scalar(w.F, "SELECT EndsAt FROM DeploymentWindows WHERE ReleaseTrainId='t1'"));
        Assert.Equal("2", Scalar(w.F, "SELECT Version FROM DeploymentWindows WHERE ReleaseTrainId='t1'"));
        Assert.Equal("CHG0030001", Scalar(w.F, "SELECT json_extract(BeforeJson,'$.ChangeTicketNumber') FROM AuditEvents WHERE EntityType='ReleaseTrain' AND Action='ImportUpdate'"));
        Assert.Equal("CHG0099999", Scalar(w.F, "SELECT json_extract(AfterJson,'$.ChangeTicketNumber') FROM AuditEvents WHERE EntityType='ReleaseTrain' AND Action='ImportUpdate'"));
        Assert.Equal("Planning", Scalar(w.F, "SELECT CurrentStatus FROM ReleaseTrains WHERE Id='t1'"));    // an import never moves a status
    }

    [Fact]
    public async Task Append_creates_trains_in_Planning_with_their_template_and_window()
    {
        var w = await Build(); using var _ = w.F;
        var p = await Ok(await Preview(w.Rte, "Trains", Csv(["Title", "TargetReleaseDate", "RiskTier", "Template", "WindowStart", "WindowEnd"], ["R27.01 New", "2027-01-15", "veryhigh", "standard release", "2027-01-15T02:00:00Z", "2027-01-15T04:00:00Z"]), "Append"));
        await Ok(await Commit(w.Rte, p.GetProperty("jobId").GetString()!));
        Assert.Equal("Planning", Scalar(w.F, "SELECT CurrentStatus FROM ReleaseTrains WHERE Title='R27.01 New'"));
        Assert.Equal("VeryHigh", Scalar(w.F, "SELECT RiskTier FROM ReleaseTrains WHERE Title='R27.01 New'"));
        Assert.Equal(Scalar(w.F, "SELECT Id FROM TrainTemplates WHERE Name='Standard release'"), Scalar(w.F, "SELECT TemplateId FROM ReleaseTrains WHERE Title='R27.01 New'"));
        Assert.Equal("1", Scalar(w.F, "SELECT COUNT(*) FROM DeploymentWindows d JOIN ReleaseTrains t ON t.Id=d.ReleaseTrainId WHERE t.Title='R27.01 New' AND d.StartsAt='2027-01-15T02:00:00Z'"));
        Assert.Equal("1", Scalar(w.F, "SELECT COUNT(*) FROM AuditEvents a JOIN ReleaseTrains t ON t.Id=a.EntityId WHERE t.Title='R27.01 New' AND a.Action='ImportInsert' AND a.ReleaseTrainId=t.Id"));
    }

    // ---- references, locks and workflow guards -------------------------------------------------------------------------------------------
    [Fact]
    public async Task Plan_imports_are_refused_for_an_Executing_train_but_an_unchanged_row_is_fine()
    {
        var w = await Build(); using var _ = w.F;
        var csv = Csv(["Train", "ProductName", "VersionTag", "ProjectCode"], ["R26.11 Live, \"quoted\" train", "Merchant API", "2.4.0", "MER"], ["R26.11 Live, \"quoted\" train", "Extra API", "1.0", "EXT"]);
        var p = await Json(await Preview(w.Rte, "Products", csv));
        var e = Assert.Single(p.GetProperty("errors").EnumerateArray());
        Assert.Equal(2, e.GetProperty("row").GetInt32()); Assert.Equal("Train", e.GetProperty("column").GetString());
        Assert.Contains("Executing", e.GetProperty("message").GetString());
        Assert.Equal(1, Counts(p, "unchanged"));

        var changed = await Json(await Preview(w.Rte, "Products", Csv(["Train", "ProductName", "VersionTag", "ProjectCode"], ["R26.11 Live, \"quoted\" train", "Merchant API", "2.4.1", "MER"])));
        Assert.Equal("Train", changed.GetProperty("errors")[0].GetProperty("column").GetString());
    }

    [Fact]
    public async Task Unknown_train_owner_gate_and_product_are_errors_on_their_own_column_with_a_suggestion()
    {
        var w = await Build(); using var _ = w.F;
        var p = await Json(await Preview(w.Rte, "Tasks", Csv(["Train", "Gate", "Description", "Owner", "Product"],
            ["R26.1O", "Code Freeze", "a", "rte@x.com", ""], ["R26.10", "Code Frees", "b", "rte@x.com", ""], ["R26.10", "Code Freeze", "c", "@dsk", ""], ["R26.10", "Code Freeze", "d", "rte@x.com", "Payments Ap"]), "Append"));
        var cols = p.GetProperty("errors").EnumerateArray().Select(e => (e.GetProperty("row").GetInt32(), e.GetProperty("column").GetString())).ToArray();
        Assert.Equal([(1, "Train"), (2, "Gate"), (3, "Owner"), (4, "Product")], cols);
        Assert.Contains("R26.10", p.GetProperty("errors")[0].GetProperty("message").GetString());
        Assert.Contains("@desk", p.GetProperty("errors")[2].GetProperty("message").GetString());
    }

    [Fact]
    public async Task Tasks_into_a_Certified_gate_warn_and_need_an_acknowledgement_and_the_trigger_writes_its_own_audit_row()
    {
        var w = await Build(); using var _ = w.F;
        var p = await Ok(await Preview(w.Rte, "Tasks", Csv(["Train", "Gate", "Description", "Owner"], ["R26.10", "Sign-off", "One more check", "@desk"]), "Append"));
        Assert.Equal(1, p.GetProperty("warnings").GetArrayLength());
        Assert.Equal("Gate", p.GetProperty("warnings")[0].GetProperty("column").GetString());
        Assert.Contains("Sign-off", p.GetProperty("decertifiesGates")[0].GetString());
        var id = p.GetProperty("jobId").GetString()!;

        var refused = await Commit(w.Rte, id);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("DecertifyNotAcknowledged", (await Json(refused)).GetProperty("guard").GetString());
        Assert.Equal("Certified", Scalar(w.F, "SELECT Status FROM StageGates WHERE Id='g5'"));

        var done = await Ok(await Commit(w.Rte, id, ack: true));
        Assert.Equal(1, done.GetProperty("inserted").GetInt32());
        Assert.Equal("InProgress", Scalar(w.F, "SELECT Status FROM StageGates WHERE Id='g5'"));
        Assert.Equal(w.RteId, Scalar(w.F, "SELECT ActorUserId FROM AuditEvents WHERE EntityType='StageGate' AND EntityId='g5' AND Action='Decertified'"));
        Assert.Equal("desk", Scalar(w.F, "SELECT t.Handle FROM ChecklistTasks c JOIN Teams t ON t.Id=c.OwnerTeamId WHERE c.TaskDescription='One more check'"));
        Assert.Equal("2", Scalar(w.F, "SELECT SequenceOrder FROM ChecklistTasks WHERE TaskDescription='One more check'"));   // after k5
        Assert.Equal("2", Scalar(w.F, "SELECT Version FROM ReleaseTrains WHERE Id='t1'"));                                     // the train moved once
    }

    [Fact]
    public async Task Runbook_steps_check_DependsOn_within_the_file_and_the_train_and_reject_cycles()
    {
        var w = await Build(); using var _ = w.F;
        string[] head = ["Train", "StepCode", "Title", "Section", "PlannedStart", "DurationMin", "Owner", "DependsOn"];
        var unknown = await Json(await Preview(w.Rte, "RunbookSteps", Csv(head, ["R26.10", "R-009", "Reconcile", "Verify", "2026-10-30T02:00:00Z", "15", "dana@x.com", "R-013"]), "Append"));
        var e = Assert.Single(unknown.GetProperty("errors").EnumerateArray());
        Assert.Equal(1, e.GetProperty("row").GetInt32()); Assert.Equal("DependsOn", e.GetProperty("column").GetString());
        Assert.Contains("R-013 is not a step in this file or in R26.10", e.GetProperty("message").GetString());

        var self = await Json(await Preview(w.Rte, "RunbookSteps", Csv(head, ["R26.10", "R-009", "Reconcile", "Verify", "2026-10-30T02:00:00Z", "15", "dana@x.com", "R-009"]), "Append"));
        Assert.Contains("itself", self.GetProperty("errors")[0].GetProperty("message").GetString());

        // R-003 depends on R-001 already; making R-001 depend on R-003 closes a cycle
        var cycle = await Json(await Preview(w.Rte, "RunbookSteps", Csv(head, ["R26.10", "R-001", "Confirm Go", "PreCheck", "2026-10-30T01:00:00Z", "5", "dana@x.com", "R-003"])));
        var ce = Assert.Single(cycle.GetProperty("errors").EnumerateArray());
        Assert.Equal("DependsOn", ce.GetProperty("column").GetString()); Assert.Contains("cycle", ce.GetProperty("message").GetString());

        // within-file references are fine, and dependencies are written
        var good = await Ok(await Preview(w.Rte, "RunbookSteps", Csv(head,
            ["R26.10", "R-010", "Step ten", "Verify", "2026-10-30T02:00:00Z", "10", "dana@x.com", "R-011;R-003"], ["R26.10", "R-011", "Step eleven", "Verify", "2026-10-30T01:50:00Z", "10", "@web", ""]), "Append"));
        Assert.Equal(0, Counts(good, "errors"));
        await Ok(await Commit(w.Rte, good.GetProperty("jobId").GetString()!));
        Assert.Equal("R-003,R-011", Scalar(w.F, "SELECT group_concat(d2.StepCode) FROM (SELECT d.StepCode FROM StepDependencies x JOIN RunbookSteps s ON s.Id=x.StepId JOIN RunbookSteps d ON d.Id=x.DependsOnStepId WHERE s.StepCode='R-010' AND s.ReleaseTrainId='t1' ORDER BY d.StepCode) d2"));
    }

    [Fact]
    public async Task Users_take_a_role_when_new_but_an_existing_role_is_the_identity_providers_to_change()
    {
        var w = await Build(); using var _ = w.F;
        var p = await Json(await Preview(w.Rte, "Users", Csv(["Email", "DisplayName", "Role", "Handle"], ["new@x.com", "New Person", "GovernanceOfficer", "@newp"], ["dana@x.com", "Dana Ortiz", "RTE", "dana"])));
        var e = Assert.Single(p.GetProperty("errors").EnumerateArray());
        Assert.Equal(2, e.GetProperty("row").GetInt32()); Assert.Equal("Role", e.GetProperty("column").GetString());
        Assert.Contains("identity provider", e.GetProperty("message").GetString());

        var ok = await Ok(await Preview(w.Rte, "Users", Csv(["Email", "DisplayName", "Role", "Handle"], ["new@x.com", "New Person", "GovernanceOfficer", "@newp"], ["dana@x.com", "Dana O.", "Viewer", "dana"])));
        await Ok(await Commit(w.Rte, ok.GetProperty("jobId").GetString()!));
        Assert.Equal("GovernanceOfficer,newp,1", Scalar(w.F, "SELECT Role||','||Handle||','||IsActive FROM Users WHERE Email='new@x.com'"));
        Assert.Equal("Dana O.", Scalar(w.F, "SELECT DisplayName FROM Users WHERE Email='dana@x.com'"));
        Assert.Equal("Viewer", Scalar(w.F, "SELECT Role FROM Users WHERE Email='dana@x.com'"));
        var taken = await Json(await Preview(w.Rte, "Users", Csv(["Email", "DisplayName", "Role", "Handle"], ["third@x.com", "Third", "Viewer", "dana"]), "Append"));
        Assert.Equal("Handle", taken.GetProperty("errors")[0].GetProperty("column").GetString());
    }

    [Fact]
    public async Task Teams_resolve_members_by_email_and_update_the_member_set()
    {
        var w = await Build(); using var _ = w.F;
        var bad = await Json(await Preview(w.Rte, "Teams", Csv(["Handle", "Name", "Members"], ["ops", "Ops", "dana@x.com;ghost@x.com"]), "Append"));
        Assert.Equal("Members", bad.GetProperty("errors")[0].GetProperty("column").GetString());
        var p = await Ok(await Preview(w.Rte, "Teams", Csv(["Handle", "Name", "Members"], ["ops", "Ops", "dana@x.com; RTE@x.com"], ["desk", "Support desk", "dana@x.com"])));
        Assert.Equal((1, 1), (Counts(p, "new"), Counts(p, "updated")));
        await Ok(await Commit(w.Rte, p.GetProperty("jobId").GetString()!));
        Assert.Equal("dana@x.com,rte@x.com", Scalar(w.F, "SELECT group_concat(u.Email) FROM (SELECT u.Email FROM TeamMembers m JOIN Users u ON u.Id=m.UserId JOIN Teams t ON t.Id=m.TeamId WHERE t.Handle='ops' ORDER BY u.Email) u"));
        Assert.Equal("dana@x.com", Scalar(w.F, "SELECT group_concat(u.Email) FROM TeamMembers m JOIN Users u ON u.Id=m.UserId JOIN Teams t ON t.Id=m.TeamId WHERE t.Handle='desk'"));
        Assert.Equal("2", Scalar(w.F, "SELECT Version FROM Teams WHERE Handle='desk'"));
    }

    [Fact]
    public async Task Gates_and_links_validate_their_own_rules()
    {
        var w = await Build(); using var _ = w.F;
        var certified = await Json(await Preview(w.Rte, "Gates", Csv(["Train", "GateName", "SequenceOrder", "OffsetDays", "RequiredBefore", "Owner"], ["R26.10", "Renamed", "4", "0", "Complete", "rte@x.com"])));
        Assert.Contains("certified", certified.GetProperty("errors")[0].GetProperty("message").GetString());
        var gate = await Ok(await Preview(w.Rte, "Gates", Csv(["Train", "GateName", "SequenceOrder", "OffsetDays", "RequiredBefore", "Owner", "GateClass"], ["R26.10", "Security Review", "5", "4", "Gated", "@desk", "compliance"]), "Append"));
        await Ok(await Commit(w.Rte, gate.GetProperty("jobId").GetString()!));
        Assert.Equal("Pending,Compliance,2026-10-26", Scalar(w.F, "SELECT Status||','||GateClass||','||DueOn FROM StageGates WHERE GateName='Security Review'"));   // 4 business days before Fri 30 Oct

        var badKey = await Json(await Preview(w.Rte, "ExternalLinks", Csv(["Train", "EntityType", "EntityRef", "System", "Key"], ["R26.10", "Gate", "Code Freeze", "Jira", "not a key"]), "Append"));
        Assert.Equal("Key", badKey.GetProperty("errors")[0].GetProperty("column").GetString());
        var dup = await Json(await Preview(w.Rte, "ExternalLinks", Csv(["Train", "EntityType", "EntityRef", "System", "Key"], ["R26.10", "Gate", "Code Freeze", "Jira", "PAY-123"]), "Append"));
        Assert.Contains("already exists", dup.GetProperty("errors")[0].GetProperty("message").GetString());
        var link = await Ok(await Preview(w.Rte, "ExternalLinks", Csv(["Train", "EntityType", "EntityRef", "System", "Key"], ["R26.10", "Product", "Onboarding UI", "Jira", "onb-77"]), "Append"));
        await Ok(await Commit(w.Rte, link.GetProperty("jobId").GetString()!));
        Assert.Equal("Unsynced", Scalar(w.F, "SELECT SyncState FROM ExternalLinks WHERE ExternalKey='ONB-77' AND EntityId='p2'"));
    }

    // ---- jobs: If-Match, staleness, expiry, ownership, history -------------------------------------------------------------------------------
    [Fact]
    public async Task Commit_needs_If_Match_when_required_and_a_wrong_version_is_409()
    {
        var w = await Build(requireIfMatch: true); using var _ = w.F;
        var p = await Ok(await Preview(w.Rte, "Holidays", Csv(["Day", "Name"], ["2030-01-01", "New Year"])));
        var id = p.GetProperty("jobId").GetString()!;
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await Commit(w.Rte, id)).StatusCode);
        var stale = await Commit(w.Rte, id, ifMatch: 7);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(id, (await Json(stale)).GetProperty("current").GetProperty("id").GetString());
        Assert.Equal("0", Scalar(w.F, "SELECT COUNT(*) FROM Holidays WHERE Day='2030-01-01'"));
        Assert.Equal(HttpStatusCode.OK, (await Commit(w.Rte, id, ifMatch: p.GetProperty("version").GetInt32())).StatusCode);
    }

    [Fact]
    public async Task A_second_preview_of_the_same_rows_goes_stale_once_the_first_is_committed()
    {
        var w = await Build(); using var _ = w.F;
        var csv = Csv(["Day", "Name"], ["2026-11-26", "Thanksgiving Day"]);
        var a = await Ok(await Preview(w.Rte, "Holidays", csv)); var b = await Ok(await Preview(w.Rte, "Holidays", csv));
        Assert.Equal(HttpStatusCode.OK, (await Commit(w.Rte, a.GetProperty("jobId").GetString()!)).StatusCode);
        var stale = await Commit(w.Rte, b.GetProperty("jobId").GetString()!);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("2", Scalar(w.F, "SELECT Version FROM Holidays WHERE Day='2026-11-26'"));   // applied once
    }

    [Fact]
    public async Task A_train_that_moved_since_the_preview_makes_a_plan_import_stale()
    {
        var w = await Build(); using var _ = w.F;
        var p = await Ok(await Preview(w.Rte, "Products", Csv(["Train", "ProductName", "VersionTag", "ProjectCode"], ["R26.10", "Ledger", "1.0", "LED"]), "Append"));
        Sql(w.F, "UPDATE ReleaseTrains SET Version=Version+1 WHERE Id='t1'");
        Assert.Equal(HttpStatusCode.Conflict, (await Commit(w.Rte, p.GetProperty("jobId").GetString()!)).StatusCode);
        Assert.Equal("0", Scalar(w.F, "SELECT COUNT(*) FROM BundledProducts WHERE ProductName='Ledger'"));
    }

    [Fact]
    public async Task A_preview_expires_after_30_minutes_and_someone_elses_preview_is_not_visible()
    {
        var w = await Build(); using var _ = w.F;
        var p = await Ok(await Preview(w.Rte, "Holidays", Csv(["Day", "Name"], ["2030-01-01", "New Year"])));
        var id = p.GetProperty("jobId").GetString()!;
        Assert.Equal(1800, (DateTime.Parse(p.GetProperty("expiresAt").GetString()!) - DateTime.Parse(p.GetProperty("createdAt").GetString()!)).TotalSeconds);
        var other = await As(w.F, Roles.ReleaseManager, "rm@x.com");
        Assert.Equal(HttpStatusCode.NotFound, (await Commit(other, id)).StatusCode);

        Sql(w.F, $"UPDATE ImportJobs SET CreatedAt='2020-01-01T00:00:00Z' WHERE Id='{id}'");
        var late = await Commit(w.Rte, id);
        Assert.Equal("ImportExpired", (await Json(late)).GetProperty("guard").GetString());
        Assert.Equal("Expired", Scalar(w.F, $"SELECT Status FROM ImportJobs WHERE Id='{id}'"));
        Assert.Equal("0", Scalar(w.F, "SELECT COUNT(*) FROM Holidays WHERE Day='2030-01-01'"));
    }

    [Fact]
    public async Task History_lists_jobs_newest_first_and_rows_page_through_the_stored_preview()
    {
        var w = await Build(); using var _ = w.F;
        var sb = new List<string[]> { new[] { "Day", "Name" } };
        for (var i = 0; i < 250; i++) sb.Add([new DateOnly(2030, 1, 1).AddDays(i).ToString("yyyy-MM-dd"), $"H{i}"]);
        var p = await Ok(await Preview(w.Rte, "Holidays", Csv([.. sb]), "Append", "a.csv"));
        Assert.Equal(200, p.GetProperty("rows").GetArrayLength()); Assert.Equal(250, p.GetProperty("rowsTotal").GetInt32());
        var id = p.GetProperty("jobId").GetString()!;
        var rest = await Ok(await w.Rte.GetAsync($"/api/v1/imports/{id}/rows?skip=200&take=100"));
        Assert.Equal(50, rest.GetArrayLength()); Assert.Equal(201, rest[0].GetProperty("row").GetInt32());
        await Ok(await Preview(w.Rte, "Holidays", Csv(["Day", "Name"], ["2040-01-01", "Later"]), "Append", "b.csv"));

        var list = await Ok(await w.Rte.GetAsync("/api/v1/imports"));
        Assert.Equal(["b.csv", "a.csv"], list.EnumerateArray().Select(j => j.GetProperty("fileName").GetString()!).ToArray());
        Assert.Equal("rte", list[0].GetProperty("uploadedBy").GetString());
        Assert.Equal("Append", list[1].GetProperty("mode").GetString());
        Assert.Equal(250, list[1].GetProperty("counts").GetProperty("new").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await w.Rte.GetAsync("/api/v1/imports/nope")).StatusCode);
    }

    [Fact]
    public async Task An_unknown_kind_and_a_bad_mode_are_422()
    {
        var w = await Build(); using var _ = w.F;
        Assert.Equal("UnknownImportKind", (await Json(await Preview(w.Rte, "Widgets", "a\r\n1\r\n"))).GetProperty("guard").GetString());
        Assert.Equal("InvalidImportMode", (await Json(await Preview(w.Rte, "Holidays", "Day,Name\r\n", "Replace"))).GetProperty("guard").GetString());
        var nodata = await Json(await Preview(w.Rte, "Holidays", "Day,Name\r\n"));
        Assert.Contains("no data rows", nodata.GetProperty("errors")[0].GetProperty("message").GetString());
    }

    // ---- roles ------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Import_endpoints_are_for_RTE_and_ReleaseManager_only()
    {
        var w = await Build(); using var _ = w.F;
        var p = await Ok(await Preview(w.Rte, "Holidays", Csv(["Day", "Name"], ["2030-01-01", "New Year"])));
        foreach (var role in new[] { Roles.Viewer, Roles.GovernanceOfficer })
        {
            var c = await As(w.F, role, $"{role}@x.com");
            Assert.Equal(HttpStatusCode.Forbidden, (await Preview(c, "Holidays", "Day,Name\r\n")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Commit(c, p.GetProperty("jobId").GetString()!)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/v1/imports")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync($"/api/v1/imports/{p.GetProperty("jobId").GetString()}")).StatusCode);
        }
        var anon = w.F.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Preview(anon, "Holidays", "Day,Name\r\n")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/imports")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await (await As(w.F, Roles.ReleaseManager, "rm@x.com")).GetAsync("/api/v1/imports")).StatusCode);
    }

    [Fact]
    public async Task Any_signed_in_role_may_export_a_grid_but_the_audit_grid_needs_AuditRead_and_anonymous_gets_401()
    {
        var w = await Build(); using var _ = w.F;
        var viewer = await As(w.F, Roles.Viewer, "v@x.com");
        foreach (var grid in new[] { "users", "trains", "blockers" })
            foreach (var ext in new[] { "csv", "xlsx" })
                Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/api/v1/exports/{grid}.{ext}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v1/exports/audit.csv")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v1/exports/audit.xlsx")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await (await As(w.F, Roles.GovernanceOfficer, "g2@x.com")).GetAsync("/api/v1/exports/audit.csv")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await w.F.CreateClient().GetAsync("/api/v1/exports/trains.csv")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.Rte.GetAsync("/api/v1/exports/widgets.csv")).StatusCode);
    }

    // ---- exports: format, headers, escaping -------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_CSV_export_is_an_attachment_with_a_BOM_CRLF_line_ends_nosniff_and_the_hash_columns()
    {
        var w = await Build(); using var _ = w.F;
        var r = await w.Rte.GetAsync("/api/v1/exports/gates.csv");
        Assert.Equal("text/csv; charset=utf-8", r.Content.Headers.ContentType!.ToString());
        Assert.Equal("attachment", r.Content.Headers.ContentDisposition!.DispositionType);
        Assert.StartsWith("gates-", r.Content.Headers.ContentDisposition.FileName);
        Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("false", r.Headers.GetValues("X-Export-Truncated").Single());
        var bytes = await r.Content.ReadAsByteArrayAsync();
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        var header = text[..text.IndexOf("\r\n", StringComparison.Ordinal)];
        Assert.Equal("Train,GateName,SequenceOrder,OffsetDays,RequiredBefore,Owner,GateClass,#Id,#DueOn,#Status,#Version", header);
        Assert.DoesNotContain("\n", text.Replace("\r\n", ""));   // no bare LF outside quoted fields in this grid
        Assert.Equal(HttpStatusCode.OK, (await w.Rte.GetAsync("/api/v1/export/gates.csv")).StatusCode);   // the spelling PROJECT_SCOPE 6 uses
    }

    [Fact]
    public async Task An_XLSX_export_has_a_frozen_bold_header_numbers_as_numbers_and_text_that_stays_text()
    {
        var w = await Build(); using var _ = w.F;
        var r = await w.Rte.GetAsync("/api/v1/exports/products.xlsx");
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", r.Content.Headers.ContentType!.MediaType);
        Assert.EndsWith(".xlsx", r.Content.Headers.ContentDisposition!.FileName);
        Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
        using var wb = new XLWorkbook(new MemoryStream(await r.Content.ReadAsByteArrayAsync()));
        var ws = wb.Worksheet(1);
        Assert.Equal("Train", ws.Cell(1, 1).GetString()); Assert.True(ws.Cell(1, 1).Style.Font.Bold);
        Assert.Equal(1, ws.SheetView.SplitRow);
        var rows = Enumerable.Range(2, ws.LastRowUsed()!.RowNumber() - 1).ToDictionary(i => ws.Cell(i, 2).GetString(), i => i);
        Assert.Equal("007", ws.Cell(rows["Onboarding UI"], 3).GetString());             // "007" stayed text
        Assert.Equal(XLDataType.Text, ws.Cell(rows["Onboarding UI"], 3).DataType);
        Assert.Equal("2026-11-13", ws.Cell(rows["Onboarding UI"], 4).GetString());       // an ISO date stayed text, not a date serial
        Assert.Equal(XLDataType.Text, ws.Cell(rows["Onboarding UI"], 4).DataType);
        var version = ws.Cell(rows["Payments API"], 6);
        Assert.Equal("#Version", ws.Cell(1, 6).GetString()); Assert.Equal(XLDataType.Number, version.DataType); Assert.Equal(1, version.GetDouble());
    }

    [Fact]
    public async Task CSV_injection_is_neutralised_in_both_formats_on_every_grid_that_can_carry_it()
    {
        var w = await Build(); using var _ = w.F;
        // CSV: parse the export properly and check no cell can start a formula; the escaped forms are exactly those the importer undoes
        foreach (var grid in new[] { "users", "trains", "blockers", "attachments" })
        {
            var csv = Encoding.UTF8.GetString(await w.Rte.GetByteArrayAsync($"/api/v1/exports/{grid}.csv"));
            foreach (var row in ParseCsv(csv).Skip(1)) foreach (var cell in row) Assert.False(cell.Length > 0 && cell[0] is '=' or '+' or '-' or '@' or '\t' or '\r', $"{grid}: live cell {cell}");
        }
        var users = ParseCsv(Encoding.UTF8.GetString(await w.Rte.GetByteArrayAsync("/api/v1/exports/users.csv")));
        var names = users.Skip(1).Select(r => r[1]).ToArray();
        Assert.Contains("'=SUM(1+1)", names); Assert.Contains("'-Dash, \"quoted\"", names); Assert.Contains("'+plus", names); Assert.Contains("'@at", names); Assert.Contains("''tis", names);
        var blockers = ParseCsv(Encoding.UTF8.GetString(await w.Rte.GetByteArrayAsync("/api/v1/exports/blockers.csv")));
        Assert.Contains("'=cmd|' /C calc'!A0", blockers.Skip(1).Select(r => r[1]));

        // XLSX: same text, stored as text cells, never a formula
        foreach (var grid in new[] { "users", "blockers", "trains", "attachments" })
        {
            using var wb = new XLWorkbook(new MemoryStream(await w.Rte.GetByteArrayAsync($"/api/v1/exports/{grid}.xlsx")));
            var ws = wb.Worksheet(1);
            foreach (var cell in ws.CellsUsed())
            {
                Assert.False(cell.HasFormula, $"{grid} {cell.Address}: formula");
                if (cell.DataType == XLDataType.Text) Assert.False(cell.GetString() is [ '=' or '+' or '-' or '@' or '\t' or '\r', ..], $"{grid} {cell.Address}: live {cell.GetString()}");
            }
        }
        using var ub = new XLWorkbook(new MemoryStream(await w.Rte.GetByteArrayAsync("/api/v1/exports/users.xlsx")));
        var all = ub.Worksheet(1).CellsUsed().Select(c => c.GetString()).ToList();
        Assert.Contains("'=SUM(1+1)", all); Assert.Contains("'-Dash, \"quoted\"", all); Assert.Contains("''tis", all);
    }

    [Fact]
    public async Task Export_only_grids_export_with_their_headers_filters_and_own_rows()
    {
        var w = await Build(); using var _ = w.F;
        async Task<List<string[]>> Get(string url) => ParseCsv(Encoding.UTF8.GetString(await w.Rte.GetByteArrayAsync(url)));
        var expect = new Dictionary<string, string>
        {
            ["blockers"] = "Train,Title,Severity,Product,Gate,Owner,RaisedAt,ResolvedAt,#Id,#Version",
            ["known-issues"] = "Train,Title,Severity,Status,Workaround,ExternalKey,RaisedAt,ResolvedAt,#Id,#Version",
            ["freeze-windows"] = "Name,Kind,StartsAt,EndsAt,ProductPattern,CreatedBy,#Id,#Version",
            ["attachments"] = "Train,EntityType,EntityId,FileName,ContentType,SizeBytes,Sha256,UploadedBy,UploadedAt,Locked,#Id",
            ["notifications"] = "Kind,EntityType,EntityId,EscalationLevel,Message,CreatedAt,ReadAt,#Id,#Version",
            ["sync-alerts"] = "Train,SourceSystem,Kind,ErrorMessage,OccurrenceCount,FirstOccurredAt,LastOccurredAt,Resolved,#Id,#Version",
            ["templates"] = "Name,Status,DefaultRiskTier,ReviewDueOn,#Id,#Version",
            ["audit"] = "Id,OccurredAt,Actor,ActorUserId,Train,ReleaseTrainId,EntityType,EntityId,Action,BeforeJson,AfterJson",
        };
        foreach (var (grid, header) in expect)
        {
            var rows = await Get($"/api/v1/exports/{grid}.csv");
            Assert.Equal(header, string.Join(',', rows[0]));
            Assert.True(rows.Count > 1 || grid == "audit", $"{grid} has no rows");
            using var wb = new XLWorkbook(new MemoryStream(await w.Rte.GetByteArrayAsync($"/api/v1/exports/{grid}.xlsx")));
            Assert.Equal(header, string.Join(',', wb.Worksheet(1).Row(1).CellsUsed().Select(c => c.GetString())));
        }
        Assert.Equal(["for rte"], (await Get("/api/v1/exports/notifications.csv")).Skip(1).Select(r => r[4]).ToArray());          // the inbox is personal
        Assert.Equal(new[] { "'+1", "'-1+1", "'=cmd|' /C calc'!A0", "'@risk" }.Order(StringComparer.Ordinal), (await Get("/api/v1/exports/blockers.csv?train=t1")).Skip(1).Select(r => r[1]).Order(StringComparer.Ordinal));
        Assert.Empty((await Get("/api/v1/exports/blockers.csv?train=t2")).Skip(1));
        Assert.Empty((await Get("/api/v1/exports/known-issues.csv?train=R26.10")).Skip(1));                                  // a title works as a train filter too
        Assert.Equal(["Slow login"], (await Get("/api/v1/exports/known-issues.csv?train=t3")).Skip(1).Select(r => r[1]).ToArray());
        Assert.Equal(["R26.10"], (await Get("/api/v1/exports/trains.csv?status=Planning")).Skip(1).Select(r => r[0]).ToArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Rte.GetAsync("/api/v1/exports/gates.csv?train=nope")).StatusCode);
        var g = await Ok(await w.Rte.GetAsync("/api/v1/exports/grids"));
        Assert.Equal(17, g.GetArrayLength());
        Assert.Equal(9, g.EnumerateArray().Count(x => x.GetProperty("importKind").ValueKind == JsonValueKind.String));
    }

    [Fact]
    public async Task A_cut_at_Export_MaxRows_is_announced_in_headers_and_a_final_row_in_both_formats()
    {
        var w = await Build(); using var _ = w.F;
        using var small = w.F.WithWebHostBuilder(b => b.UseSetting("Export:MaxRows", "2"));
        var c = small.CreateClient();
        (await c.PostAsJsonAsync("/auth/dev-login", new { email = "rte@x.com", name = "rte", role = "RTE" })).EnsureSuccessStatusCode();
        var r = await c.GetAsync("/api/v1/exports/users.csv");
        Assert.Equal("true", r.Headers.GetValues("X-Export-Truncated").Single());
        Assert.Equal("9", r.Headers.GetValues("X-Export-Total-Rows").Single());
        var rows = ParseCsv(Encoding.UTF8.GetString(await r.Content.ReadAsByteArrayAsync()));
        Assert.Equal(4, rows.Count);                                   // header, 2 rows, the marker
        Assert.StartsWith("# truncated: 2 of 9 rows", rows[^1][0]);
        using var wb = new XLWorkbook(new MemoryStream(await c.GetByteArrayAsync("/api/v1/exports/users.xlsx")));
        Assert.StartsWith("# truncated: 2 of 9 rows", wb.Worksheet(1).Cell(4, 1).GetString());
    }

    // ---- THE round trip: every grid that has an import kind re-imports unchanged, from CSV and from XLSX ------------------------------------
    [Theory]
    [InlineData("users", "Users")]
    [InlineData("teams", "Teams")]
    [InlineData("holidays", "Holidays")]
    [InlineData("trains", "Trains")]
    [InlineData("products", "Products")]
    [InlineData("gates", "Gates")]
    [InlineData("tasks", "Tasks")]
    [InlineData("runbook-steps", "RunbookSteps")]
    [InlineData("external-links", "ExternalLinks")]
    public async Task Every_grid_export_re_imports_unchanged_from_CSV_and_XLSX_and_the_database_is_untouched(string grid, string kind)
    {
        var w = await Build(); using var _ = w.F;
        var csvBytes = await w.Rte.GetByteArrayAsync($"/api/v1/exports/{grid}.csv");
        var csv = Encoding.UTF8.GetString(csvBytes);
        var table = ParseCsv(csv);
        var dataRows = table.Count - 1;
        Assert.True(dataRows >= 1, $"{grid}: the fixture has no rows");
        Assert.Contains(table[0], h => h.StartsWith('#'));                       // read-only fields are marked

        var xlsx = XlsxToCsv(await w.Rte.GetByteArrayAsync($"/api/v1/exports/{grid}.xlsx"));
        Assert.Equal(table.Select(r => string.Join('\u001f', r)), ParseCsv(xlsx).Select(r => string.Join('\u001f', r)));   // both formats carry the same cells

        var before = Fingerprint(w.F, "ImportJobs", "AuditEvents");
        var auditBefore = int.Parse(Scalar(w.F, "SELECT COUNT(*) FROM AuditEvents"));
        foreach (var (text, label) in new[] { (csv, "CSV"), (xlsx, "XLSX") })
        {
            var p = await Ok(await Preview(w.Rte, kind, text, "Upsert", $"{grid}.{label}"));
            Assert.True(0 == Counts(p, "errors"), $"{grid} {label}: {p.GetProperty("errors")}");
            Assert.Equal((0, 0, dataRows), (Counts(p, "new"), Counts(p, "updated"), Counts(p, "unchanged")));
            Assert.All(p.GetProperty("rows").EnumerateArray(), r => Assert.Equal("unchanged", r.GetProperty("op").GetString()));
            Assert.Equal(0, p.GetProperty("warnings").GetArrayLength());
            var done = await Ok(await Commit(w.Rte, p.GetProperty("jobId").GetString()!));
            Assert.Equal((0, 0, dataRows), (done.GetProperty("inserted").GetInt32(), done.GetProperty("updated").GetInt32(), done.GetProperty("unchanged").GetInt32()));
        }
        Assert.Equal(before, Fingerprint(w.F, "ImportJobs", "AuditEvents"));       // not one row of any table changed, not even a Version
        Assert.Equal(auditBefore + 2, int.Parse(Scalar(w.F, "SELECT COUNT(*) FROM AuditEvents")));   // only the two job-level Commit rows

        // and Append of the same file is refused row by row: every key exists
        var append = await Json(await Preview(w.Rte, kind, csv, "Append"));
        Assert.Equal(dataRows, Counts(append, "errors"));
    }

    [Fact]
    public async Task An_edited_export_imports_the_edit_and_nothing_else()
    {
        var w = await Build(); using var _ = w.F;
        var table = ParseCsv(Encoding.UTF8.GetString(await w.Rte.GetByteArrayAsync("/api/v1/exports/tasks.csv")));
        var owner = Array.IndexOf(table[0], "Owner");
        var desc = Array.IndexOf(table[0], "Description");
        var target = table.Skip(1).First(r => r[desc] == "Tag repos");
        target[owner] = "@desk";
        var p = await Ok(await Preview(w.Rte, "Tasks", Csv([.. table])));
        Assert.Equal((0, 1, table.Count - 2), (Counts(p, "new"), Counts(p, "updated"), Counts(p, "unchanged")));
        await Ok(await Commit(w.Rte, p.GetProperty("jobId").GetString()!));
        Assert.Equal("desk", Scalar(w.F, "SELECT t.Handle FROM ChecklistTasks c JOIN Teams t ON t.Id=c.OwnerTeamId WHERE c.Id='k1'"));
        Assert.Equal("2", Scalar(w.F, "SELECT Version FROM ChecklistTasks WHERE Id='k1'"));
        Assert.Equal("1", Scalar(w.F, "SELECT Version FROM ChecklistTasks WHERE Id='k2'"));
        Assert.Equal("rte@x.com", Scalar(w.F, "SELECT json_extract(BeforeJson,'$.Owner') FROM AuditEvents WHERE EntityType='ChecklistTask' AND EntityId='k1' AND Action='ImportUpdate'"));
        Assert.Equal("@desk", Scalar(w.F, "SELECT json_extract(AfterJson,'$.Owner') FROM AuditEvents WHERE EntityType='ChecklistTask' AND EntityId='k1' AND Action='ImportUpdate'"));
    }
}
