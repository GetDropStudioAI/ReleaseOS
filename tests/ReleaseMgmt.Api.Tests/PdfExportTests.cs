using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Infrastructure.Exports;
using UglyToad.PdfPig;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// REOS-50: PDF export jobs. THE ACCEPTANCE: the stored SHA-256 of an evidence pack verifies against the downloaded file (and the X-Content-SHA256 header).
/// Every test drives the real host and calls ExportWorker.RunOnceAsync itself (the timer is set to an hour so nothing races the test).
/// </summary>
public class PdfExportTests
{
    [ModuleInitializer]
    internal static void Init() => Environment.SetEnvironmentVariable("Exports__PollSeconds", "3600");

    private const string Licence = "Pdf__QuestPdfLicense";
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    private sealed record Env(ApiFactory F, HttpClient Rte, HttpClient Rm, HttpClient Gov, HttpClient Viewer)
    {
        public ExportWorker Worker => F.Services.GetRequiredService<ExportWorker>();
        public Task<int> Run() => Worker.RunOnceAsync();
        public string ExportsDir => Path.Combine(Path.GetDirectoryName(F.DbPath)!, "exports");
        public int FilesInExports => Directory.Exists(ExportsDir) ? Directory.EnumerateFiles(ExportsDir, "*", SearchOption.AllDirectories).Count() : 0;
    }

    private static MultipartFormDataContent Form(string entityType, string entityId, byte[] bytes, string fileName, string contentType = "text/plain")
    {
        var form = new MultipartFormDataContent { { new StringContent(entityType), "entityType" }, { new StringContent(entityId), "entityId" } };
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        form.Add(file, "file", fileName);
        return form;
    }

    /// <summary>Train t1 with a change record, window, products, runbook (with rollback), blocker, a Go-with-conditions decision, a freeze override, a waiver request, two attachments and both gates certified.</summary>
    private static async Task<Env> Setup(string? licence = "Community")
    {
        Environment.SetEnvironmentVariable(Licence, licence);
        var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var rm = await As(f, Roles.ReleaseManager, "rm@x.com");
        var gov = await As(f, Roles.GovernanceOfficer, "gov@x.com");
        var viewer = await As(f, Roles.Viewer, "v@x.com");
        string rteId = UserId(f, "rte@x.com"), rmId = UserId(f, "rm@x.com"), govId = UserId(f, "gov@x.com");
        SeedTrain(f, rteId, govId);
        Sql(f, $@"
            INSERT OR REPLACE INTO ChangeRecords(ReleaseTrainId,Justification,ImplementationPlan,RiskImpactAnalysis,BackoutPlan,TestPlan,CommunicationPlan)
              VALUES('t1','Regulatory fee-schedule change must be live before month-end close.','Deploy payments then portal.','Low blast radius.','Roll back the release via the rollback steps.','QA regression RC2 and card-auth smoke test.','Notify support and finance.');
            INSERT INTO DeploymentWindows(Id,ReleaseTrainId,StartsAt,EndsAt) VALUES('w1','t1','2026-10-30T06:00:00Z','2026-10-30T10:00:00Z');
            INSERT INTO BundledProducts(Id,ReleaseTrainId,ProductName,VersionTag,ProjectCode) VALUES('p1','t1','Payments API','4.5.0','PAY'),('p2','t1','Card Portal','2.1.0','CRD');
            INSERT INTO AffectedCIs(Id,ReleaseTrainId,CiName) VALUES('c1','t1','payments-prod'),('c2','t1','portal-prod');
            INSERT INTO RunbookSteps(Id,ReleaseTrainId,BundledProductId,StepCode,Section,Title,Instructions,OwnerUserId,PlannedStartAt,PlannedDurationMin) VALUES
              ('s1','t1','p1','R-001','Deploy','Deploy Payments API','Run deploy-payments.sh and watch the health page.','{rteId}','2026-10-30T06:00:00Z',60),
              ('s2','t1','p2','R-002','Deploy','Deploy Card Portal','Run deploy-portal.sh.','{rteId}','2026-10-30T07:00:00Z',30),
              ('s3','t1',NULL,'R-003','Verify','Smoke test','Run the card-auth smoke test R-009.','{rteId}','2026-10-30T07:30:00Z',20),
              ('s4','t1',NULL,'R-090','Rollback','Roll back Payments API','Run rollback-payments.sh.','{rteId}','2026-10-30T08:00:00Z',30);
            INSERT INTO StepDependencies(StepId,DependsOnStepId) VALUES('s2','s1'),('s3','s2');
            INSERT INTO Blockers(Id,ReleaseTrainId,Title,Severity,OwnerUserId,RaisedAt) VALUES('b1','t1','Pen-test exceptions unsigned','Medium','{rteId}','2026-10-10T10:00:00Z');
            INSERT INTO GoNoGoDecisions(Id,ReleaseTrainId,Decision,DecidedByUserId,DecidedAt,GateSnapshotJson,Notes)
              VALUES('d1','t1','GoWithConditions','{rmId}','2026-10-29T15:00:00Z','{{""trainStatus"":""Gated"",""gates"":[{{""name"":""Code Freeze"",""status"":""Certified""}},{{""name"":""Compliance Sign-off"",""status"":""InProgress""}}]}}','Proceed once the pen-test exceptions are signed.');
            INSERT INTO GoNoGoConditions(Id,DecisionId,Text,OwnerUserId,ExpiresAt) VALUES('gc1','d1','Card Portal pen-test exceptions signed by CISO','{govId}','2099-10-30T00:30:00Z');
            INSERT INTO FreezeWindows(Id,Name,Kind,StartsAt,EndsAt,CreatedByUserId) VALUES('fw1','Q4 close freeze','Freeze','2026-10-25T00:00:00Z','2026-11-03T00:00:00Z','{rmId}');
            INSERT INTO FreezeOverrides(Id,FreezeWindowId,ReleaseTrainId,Reason,RequestedByUserId,ApprovedByUserId,ApprovedAt,ExpiresAt)
              VALUES('fo1','fw1','t1','Regulatory fee-schedule change must be live before month-end close.','{rteId}','{govId}','2026-10-28T10:02:00Z','2026-10-30T06:00:00Z');
            INSERT INTO GateWaivers(Id,StageGateId,Reason,RequestedByUserId,RequestedAt) VALUES('wv1','g1','Code freeze evidence is held in the paper archive.','{rteId}','2026-10-21T09:00:00Z');");

        // Evidence before certifying (the certify trigger then locks the gate's files).
        Assert.Equal(HttpStatusCode.OK, (await rte.PostAsync("/api/v1/attachments", Form("Train", "t1", Encoding.UTF8.GetBytes("train scope memo"), "scope-memo.txt"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rte.PostAsync("/api/v1/attachments", Form("Gate", "g1", Encoding.UTF8.GetBytes("=cmd|' /C calc'!A0 code freeze evidence, with comma"), "=freeze,evidence.csv", "text/csv"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rte.PostAsJsonAsync("/api/v1/gates/g1:start", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rte.PostAsJsonAsync("/api/v1/tasks/k1:complete", new { })).StatusCode);
        var c1 = await rte.PostAsJsonAsync("/api/v1/gates/g1:certify", new { });
        Assert.True(c1.StatusCode == HttpStatusCode.OK, await c1.Content.ReadAsStringAsync());
        var k2 = await rte.PostAsJsonAsync("/api/v1/tasks/k2:complete", new { });
        Assert.True(k2.StatusCode == HttpStatusCode.OK, await k2.Content.ReadAsStringAsync());
        var c2 = await gov.PostAsJsonAsync("/api/v1/gates/g2:certify", new { });
        Assert.True(c2.StatusCode == HttpStatusCode.OK, await c2.Content.ReadAsStringAsync());
        return new Env(f, rte, rm, gov, viewer);
    }

    private static async Task<JsonElement> Enqueue(HttpClient c, string kind, string? format = null, string train = "t1")
    {
        var r = await c.PostAsJsonAsync($"/api/v1/trains/{train}/export-jobs", new { kind, format });
        Assert.True(r.StatusCode == HttpStatusCode.Accepted, await r.Content.ReadAsStringAsync());
        return await Json(r);
    }

    private static async Task<(byte[] Bytes, HttpResponseMessage Res)> Download(HttpClient c, string id)
    {
        var r = await c.GetAsync($"/api/v1/export-jobs/{id}/file");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Content.ReadAsByteArrayAsync(), r);
    }

    private static string PageText(byte[] pdf, int page) { using var d = PdfDocument.Open(pdf); return d.GetPage(page).Text; }

    // ---- THE ACCEPTANCE ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Evidence_pack_stored_sha256_verifies_against_the_downloaded_file_and_the_header()
    {
        var e = await Setup(); using var _f = e.F;
        var job = await Enqueue(e.Rte, "EvidencePack");
        Assert.Equal("Queued", job.GetProperty("status").GetString());
        Assert.Equal("EvidencePackPdf", job.GetProperty("kind").GetString());
        var id = job.GetProperty("id").GetString()!;

        Assert.Equal(1, await e.Run());
        var done = await Json(await e.Rte.GetAsync($"/api/v1/export-jobs/{id}"));
        Assert.Equal("Done", done.GetProperty("status").GetString());
        var stored = done.GetProperty("sha256").GetString()!;
        Assert.Equal(64, stored.Length);

        var (bytes, res) = await Download(e.Rte, id);
        Assert.Equal(stored, Sha(bytes));
        Assert.Equal(stored, res.Headers.GetValues("X-Content-SHA256").Single());
        Assert.Equal(stored, Scalar(e.F, $"SELECT Sha256 FROM ExportJobs WHERE Id='{id}'"));
        Assert.Equal(done.GetProperty("sizeBytes").GetInt64(), bytes.Length);
        Assert.Equal("application/pdf", res.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", res.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("no-store", res.Headers.CacheControl!.ToString());
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(bytes, 0, 5));

        // File is outside wwwroot, id-named, and the audit trail has the lifecycle in one place.
        var path = Scalar(e.F, $"SELECT StoragePath FROM ExportJobs WHERE Id='{id}'");
        Assert.False(Path.IsPathRooted(path));
        Assert.True(File.Exists(Path.Combine(e.ExportsDir, path)));
        Assert.Equal("3", Scalar(e.F, $"SELECT COUNT(*) FROM AuditEvents WHERE EntityType='ExportJob' AND EntityId='{id}' AND Action IN ('Enqueue','Start','Complete')"));
        Assert.Equal("3", Scalar(e.F, $"SELECT Version FROM ExportJobs WHERE Id='{id}'"));   // 1 created, +1 Running, +1 Done
    }

    [Fact]
    public async Task Evidence_pack_page_1_carries_the_mockup_sections_and_a_footer_naming_sections_6_to_9()
    {
        var e = await Setup(); using var _f = e.F;
        var id = (await Enqueue(e.Rte, "EvidencePack")).GetProperty("id").GetString()!;
        await e.Run();
        var (bytes, _) = await Download(e.Rte, id);
        using var doc = PdfDocument.Open(bytes);
        Assert.True(doc.NumberOfPages >= 2);
        Assert.Equal(612, Math.Round(doc.GetPage(1).Width));   // Letter, points
        Assert.Equal(792, Math.Round(doc.GetPage(1).Height));
        var p1 = doc.GetPage(1).Text;
        foreach (var marker in new[]
                 {
                     "Audit evidence pack", "R26.10", "Risk High", "train version", "records are append-only",
                     "1 · Change record", "Justification", "Backout plan", "Test plan",
                     "2 · Approvals and certifications", "Separation of duties", "Code Freeze", "Compliance Sign-off", "completed none of 1 task", "not required",
                     "3 · Go/No-Go decision", "Go with conditions", "Card Portal pen-test exceptions signed by CISO",
                     "4 · Exceptions", "Freeze override FO-1", "Q4 close freeze", "Gate waiver",
                     "5 · Evidence manifest (all 2)", "sha256", "Files are in the accompanying ZIP with manifest.csv",
                     "Sections 6–9: runbook actuals · gate transition log · audit events · metric snapshot", "Page 1 of",
                 })
            Assert.Contains(marker, p1);
        Assert.Contains("EX-", p1);
        Assert.Contains("…", p1);   // short sha256 like 9f3c1a…77e1a4
        var later = string.Join("\n", Enumerable.Range(2, doc.NumberOfPages - 1).Select(i => doc.GetPage(i).Text));
        foreach (var marker in new[] { "6 · Runbook actuals", "7 · Gate transition log", "8 · Audit events", "9 · Metric snapshot", "Communication plan", "R-090", "Rollback" })
            Assert.Contains(marker, later);
        Assert.DoesNotContain("Sections 6–9", later);   // the mockup footer is page 1 only
    }

    [Fact]
    public async Task Evidence_pack_writes_metric_snapshots_and_prints_them()
    {
        var e = await Setup(); using var _f = e.F;
        var id = (await Enqueue(e.Rte, "EvidencePack")).GetProperty("id").GetString()!;
        await e.Run();
        var rows = int.Parse(Scalar(e.F, $"SELECT COUNT(*) FROM MetricSnapshots WHERE ExportJobId='{id}'"));
        Assert.True(rows > 0, "no MetricSnapshots rows were written");
        Assert.Equal("t1", Scalar(e.F, $"SELECT DISTINCT ReleaseTrainId FROM MetricSnapshots WHERE ExportJobId='{id}'"));
        Assert.Equal("1", Scalar(e.F, $"SELECT COUNT(DISTINCT CapturedAt) FROM MetricSnapshots WHERE ExportJobId='{id}'"));
        Assert.NotEqual("", Scalar(e.F, $"SELECT PeriodStart FROM MetricSnapshots WHERE ExportJobId='{id}' LIMIT 1"));
        var (bytes, _) = await Download(e.Rte, id);
        using var doc = PdfDocument.Open(bytes);
        var all = string.Join("\n", Enumerable.Range(1, doc.NumberOfPages).Select(i => doc.GetPage(i).Text));
        Assert.Contains("9 · Metric snapshot", all);
        Assert.Contains("Stored in MetricSnapshots", all);
        // a second pack writes its own rows and leaves the first pack's untouched
        var id2 = (await Enqueue(e.Rte, "EvidencePack")).GetProperty("id").GetString()!;
        await e.Run();
        Assert.Equal(rows.ToString(), Scalar(e.F, $"SELECT COUNT(*) FROM MetricSnapshots WHERE ExportJobId='{id}'"));
        Assert.NotEqual("0", Scalar(e.F, $"SELECT COUNT(*) FROM MetricSnapshots WHERE ExportJobId='{id2}'"));
    }

    [Theory]
    [InlineData("ReleaseReport", "Release report", "Gate timeline")]
    [InlineData("RunSheet", "Run sheet", "Rollback: only if the rollback decision has been made")]
    [InlineData("Scorecard", "Post-release scorecard", "Planned vs actual")]
    [InlineData("EvidencePack", "Audit evidence pack", "9 · Metric snapshot")]
    public async Task Each_kind_renders_a_valid_pdf_with_pages_and_its_own_content(string kind, string title, string marker)
    {
        var e = await Setup(); using var _f = e.F;
        var id = (await Enqueue(e.Rte, kind)).GetProperty("id").GetString()!;
        Assert.Equal(1, await e.Run());
        var (bytes, res) = await Download(e.Rte, id);
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(bytes, 0, 5));
        using var doc = PdfDocument.Open(bytes);
        Assert.True(doc.NumberOfPages > 0);
        var all = string.Join("\n", Enumerable.Range(1, doc.NumberOfPages).Select(i => doc.GetPage(i).Text));
        Assert.Contains(title, all);
        Assert.Contains(marker, all);
        Assert.Contains("R26.10", all);
        if (kind != "EvidencePack") Assert.Contains("Page 1 of", all);   // footer: page n of m
        Assert.Contains("version", all);   // footer carries the train Version
        Assert.Equal(Sha(bytes), res.Headers.GetValues("X-Content-SHA256").Single());
    }

    [Fact]
    public async Task Run_sheet_lists_steps_in_order_with_owner_dependencies_instructions_and_a_separate_rollback_page()
    {
        var e = await Setup(); using var _f = e.F;
        var id = (await Enqueue(e.Rte, "RunSheet")).GetProperty("id").GetString()!;
        await e.Run();
        var (bytes, _) = await Download(e.Rte, id);
        using var doc = PdfDocument.Open(bytes);
        Assert.True(doc.NumberOfPages >= 2);
        var p1 = doc.GetPage(1).Text;
        Assert.True(p1.IndexOf("R-001", StringComparison.Ordinal) < p1.IndexOf("R-002", StringComparison.Ordinal));
        Assert.True(p1.IndexOf("R-002", StringComparison.Ordinal) < p1.IndexOf("R-003", StringComparison.Ordinal));
        Assert.Contains("Run deploy-payments.sh", p1);
        Assert.Contains("R-001", p1);           // R-002 needs R-001
        Assert.Contains("Initials", p1);
        Assert.DoesNotContain("R-090", p1);     // rollback is on its own page
        Assert.Contains("R-090", doc.GetPage(doc.NumberOfPages).Text);
    }

    // ---- ZIP --------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Evidence_zip_holds_the_pdf_every_attachment_and_manifest_csv_and_every_hash_matches()
    {
        var e = await Setup(); using var _f = e.F;
        var job = await Enqueue(e.Rte, "EvidencePack", "zip");
        Assert.Equal("zip", job.GetProperty("format").GetString());
        var id = job.GetProperty("id").GetString()!;
        await e.Run();
        var (bytes, res) = await Download(e.Rte, id);
        Assert.Equal("application/zip", res.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Sha(bytes), res.Headers.GetValues("X-Content-SHA256").Single());   // acceptance for the ZIP form too
        Assert.Equal(Scalar(e.F, $"SELECT Sha256 FROM ExportJobs WHERE Id='{id}'"), Sha(bytes));

        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var names = zip.Entries.Select(x => x.FullName).ToList();
        Assert.Single(names, n => n.EndsWith(".pdf"));
        Assert.Contains("manifest.csv", names);
        Assert.Equal(2, names.Count(n => n.StartsWith("files/")));

        var pdfEntry = zip.Entries.Single(x => x.FullName.EndsWith(".pdf"));
        using (var s = pdfEntry.Open()) { var ms = new MemoryStream(); s.CopyTo(ms); Assert.StartsWith("%PDF", Encoding.ASCII.GetString(ms.ToArray(), 0, 5)); }

        var manifest = new StreamReader(zip.GetEntry("manifest.csv")!.Open(), Encoding.UTF8).ReadToEnd();
        var lines = manifest.TrimEnd().Split("\r\n");
        Assert.Equal("name,size,sha256,gate_or_task,uploader,uploaded_at,locked,zip_path", lines[0]);
        Assert.Equal(3, lines.Length);
        Assert.Contains("'=freeze", manifest);                // OWASP: leading = is neutralised with an apostrophe
        Assert.DoesNotContain(",=freeze", manifest);
        Assert.Contains("\"'=freeze,evidence.csv\"", manifest);   // and the comma forces RFC 4180 quoting
        Assert.Contains("true", manifest);                     // the gate-1 file was locked by the certify trigger
        Assert.Contains("Gate: Code Freeze", manifest);
        Assert.Contains("Train: Train", manifest);

        var stored = new (string Id, string Sha, long Size)[]
        {
            (Scalar(e.F, "SELECT Id FROM Attachments WHERE FileName='scope-memo.txt'"), Scalar(e.F, "SELECT Sha256 FROM Attachments WHERE FileName='scope-memo.txt'"), 16),
        };
        foreach (var entry in zip.Entries.Where(x => x.FullName.StartsWith("files/")))
        {
            using var s = entry.Open(); var ms = new MemoryStream(); s.CopyTo(ms);
            var actual = Sha(ms.ToArray());
            Assert.Equal("1", Scalar(e.F, $"SELECT COUNT(*) FROM Attachments WHERE Sha256='{actual}' AND SizeBytes={ms.Length}"));   // each file's bytes hash to a stored attachment hash
            Assert.Contains(actual, manifest);
        }
        Assert.Equal(16, stored[0].Size);
    }

    // ---- integrity + failures ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_tampered_attachment_fails_the_zip_job_loudly_raises_ExportFailed_and_offers_no_pack()
    {
        var e = await Setup(); using var _f = e.F;
        var path = Path.Combine(e.F.AttachmentsDir, Scalar(e.F, "SELECT StoragePath FROM Attachments WHERE FileName='scope-memo.txt'"));
        var bytes = await File.ReadAllBytesAsync(path); bytes[0] ^= 0x01;
        await File.WriteAllBytesAsync(path, bytes);   // one flipped bit on disk, same size

        var id = (await Enqueue(e.Rte, "EvidencePack", "zip")).GetProperty("id").GetString()!;
        Assert.Equal(1, await e.Run());
        var job = await Json(await e.Rte.GetAsync($"/api/v1/export-jobs/{id}"));
        Assert.Equal("Failed", job.GetProperty("status").GetString());
        var error = job.GetProperty("error").GetString()!;
        Assert.Contains("scope-memo.txt", error);
        Assert.Contains("no longer matches", error);
        Assert.Equal(JsonValueKind.Null, job.GetProperty("sha256").ValueKind);

        Assert.Equal("1", Scalar(e.F, "SELECT COUNT(*) FROM SyncAlerts WHERE SourceSystem='Export' AND Kind='ExportFailed' AND IsResolved=0"));
        Assert.Contains("scope-memo.txt", Scalar(e.F, "SELECT ErrorMessage FROM SyncAlerts WHERE SourceSystem='Export' AND Kind='ExportFailed'"));
        Assert.Equal("t1", Scalar(e.F, "SELECT ReleaseTrainId FROM SyncAlerts WHERE SourceSystem='Export'"));
        Assert.Equal("1", Scalar(e.F, $"SELECT COUNT(*) FROM AuditEvents WHERE EntityType='ExportJob' AND EntityId='{id}' AND Action='Fail'"));
        Assert.Equal("", Scalar(e.F, $"SELECT IFNULL(StoragePath,'') FROM ExportJobs WHERE Id='{id}'"));
        Assert.Equal(0, e.FilesInExports);   // no pack, no partial file, no orphan

        var dl = await e.Rte.GetAsync($"/api/v1/export-jobs/{id}/file");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, dl.StatusCode);
        Assert.Equal("ExportNotReady", (await Json(dl)).GetProperty("guard").GetString());
    }

    [Fact]
    public async Task A_missing_attachment_file_also_fails_the_zip_and_a_pdf_only_pack_still_works()
    {
        var e = await Setup(); using var _f = e.F;
        File.Delete(Path.Combine(e.F.AttachmentsDir, Scalar(e.F, "SELECT StoragePath FROM Attachments WHERE FileName='scope-memo.txt'")));
        var zipId = (await Enqueue(e.Rte, "EvidencePack", "zip")).GetProperty("id").GetString()!;
        await e.Run();
        Assert.Equal("Failed", Scalar(e.F, $"SELECT Status FROM ExportJobs WHERE Id='{zipId}'"));
        Assert.Equal("1", Scalar(e.F, "SELECT COUNT(*) FROM SyncAlerts WHERE SourceSystem='Export' AND Kind='ExportFailed'"));
        Assert.Equal(0, e.FilesInExports);
        var pdfId = (await Enqueue(e.Rte, "EvidencePack")).GetProperty("id").GetString()!;
        await e.Run();
        Assert.Equal("Done", Scalar(e.F, $"SELECT Status FROM ExportJobs WHERE Id='{pdfId}'"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Freeware")]
    public async Task Missing_or_invalid_licence_is_refused_readably_up_front(string? licence)
    {
        var e = await Setup(licence); using var _f = e.F;
        var r = await e.Rte.PostAsJsonAsync("/api/v1/trains/t1/export-jobs", new { kind = "ReleaseReport" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        var body = await Json(r);
        Assert.Equal("ExportLicence", body.GetProperty("guard").GetString());
        Assert.Contains("Pdf:QuestPdfLicense", body.GetProperty("message").GetString());
        Assert.Equal("0", Scalar(e.F, "SELECT COUNT(*) FROM ExportJobs"));
    }

    [Fact]
    public async Task A_licence_lost_between_queueing_and_rendering_fails_the_job_readably_and_raises_the_alert()
    {
        var e = await Setup(); using var _f = e.F;
        var id = (await Enqueue(e.Rte, "ReleaseReport")).GetProperty("id").GetString()!;
        // a job that was queued while a licence was configured and is rendered by a host that no longer has one
        Sql(e.F, "UPDATE ExportJobs SET Kind='ReleaseReportPdf' WHERE Id='" + id + "'");
        var opts = e.F.Services.GetRequiredService<ExportOptions>();
        var noLicence = new ExportWorker(e.F.Services.GetRequiredService<ExportService>(), e.F.Services.GetRequiredService<ExportModelLoader>(), e.F.Services.GetRequiredService<MetricSnapshotService>(),
            e.F.Services.GetRequiredService<ReleaseMgmt.Infrastructure.Services.AttachmentService>(), opts with { License = null }, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<ExportWorker>.Instance);
        Assert.Equal(1, await noLicence.RunOnceAsync());
        var job = await Json(await e.Rte.GetAsync($"/api/v1/export-jobs/{id}"));
        Assert.Equal("Failed", job.GetProperty("status").GetString());
        Assert.Contains("licence is not configured", job.GetProperty("error").GetString());
        Assert.Equal("1", Scalar(e.F, "SELECT COUNT(*) FROM SyncAlerts WHERE SourceSystem='Export' AND Kind='ExportFailed'"));
        Assert.Equal(0, e.FilesInExports);
    }

    [Fact]
    public async Task A_deleted_export_file_is_404_with_an_alert_and_a_corrupted_one_is_never_served()
    {
        var e = await Setup(); using var _f = e.F;
        var id = (await Enqueue(e.Rte, "ReleaseReport")).GetProperty("id").GetString()!;
        await e.Run();
        var file = Path.Combine(e.ExportsDir, Scalar(e.F, $"SELECT StoragePath FROM ExportJobs WHERE Id='{id}'"));

        var good = await File.ReadAllBytesAsync(file);
        var bad = (byte[])good.Clone(); bad[bad.Length / 2] ^= 0xFF;
        await File.WriteAllBytesAsync(file, bad);
        var corrupt = await e.Rte.GetAsync($"/api/v1/export-jobs/{id}/file");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, corrupt.StatusCode);
        Assert.Equal("1", Scalar(e.F, "SELECT COUNT(*) FROM SyncAlerts WHERE SourceSystem='Export' AND Kind='ExportFailed' AND ErrorMessage LIKE '%integrity%' OR ErrorMessage LIKE '%no longer matches%'"));

        File.Delete(file);
        var gone = await e.Rte.GetAsync($"/api/v1/export-jobs/{id}/file");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Equal("2", Scalar(e.F, "SELECT COUNT(*) FROM SyncAlerts WHERE SourceSystem='Export' AND Kind='ExportFailed'"));
    }

    // ---- roles, listing, idempotency --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Endpoints_need_a_session_and_the_evidence_pack_needs_audit_rights()
    {
        var e = await Setup(); using var _f = e.F;
        using var anon = e.F.CreateClient(new() { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/v1/trains/t1/export-jobs", new { kind = "ReleaseReport" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/export-jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/export-jobs/x/file")).StatusCode);

        // Viewer: may ask for the three ordinary documents, not for the evidence pack
        Assert.Equal(HttpStatusCode.Forbidden, (await e.Viewer.PostAsJsonAsync("/api/v1/trains/t1/export-jobs", new { kind = "EvidencePack" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await e.Viewer.PostAsJsonAsync("/api/v1/trains/t1/export-jobs", new { kind = "EvidencePack", format = "zip" })).StatusCode);
        foreach (var k in new[] { "ReleaseReport", "RunSheet", "Scorecard" })
            Assert.Equal(HttpStatusCode.Accepted, (await e.Viewer.PostAsJsonAsync("/api/v1/trains/t1/export-jobs", new { kind = k })).StatusCode);
        // RTE, Release Manager and Governance Officer may
        foreach (var c in new[] { e.Rte, e.Rm, e.Gov })
            Assert.Equal(HttpStatusCode.Accepted, (await c.PostAsJsonAsync("/api/v1/trains/t1/export-jobs", new { kind = "EvidencePack" })).StatusCode);

        await e.Run();
        var pack = Scalar(e.F, "SELECT Id FROM ExportJobs WHERE Kind='EvidencePackPdf' LIMIT 1");
        Assert.Equal(HttpStatusCode.Forbidden, (await e.Viewer.GetAsync($"/api/v1/export-jobs/{pack}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await e.Viewer.GetAsync($"/api/v1/export-jobs/{pack}/file")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await e.Gov.GetAsync($"/api/v1/export-jobs/{pack}/file")).StatusCode);
        var viewerList = await Json(await e.Viewer.GetAsync("/api/v1/export-jobs?trainId=t1"));
        Assert.DoesNotContain(viewerList.EnumerateArray(), j => j.GetProperty("kind").GetString() == "EvidencePackPdf");
        Assert.Equal(3, viewerList.GetArrayLength());
        var rteList = await Json(await e.Rte.GetAsync("/api/v1/export-jobs?trainId=t1"));
        Assert.Equal(6, rteList.GetArrayLength());
        var viewerRelease = Scalar(e.F, "SELECT Id FROM ExportJobs WHERE Kind='ReleaseReportPdf' LIMIT 1");
        Assert.Equal(HttpStatusCode.OK, (await e.Viewer.GetAsync($"/api/v1/export-jobs/{viewerRelease}/file")).StatusCode);
    }

    [Fact]
    public async Task Unknown_kind_format_and_train_are_refused_and_jobs_cannot_be_deleted()
    {
        var e = await Setup(); using var _f = e.F;
        Assert.Equal(HttpStatusCode.BadRequest, (await e.Rte.PostAsJsonAsync("/api/v1/trains/t1/export-jobs", new { kind = "Nope" })).StatusCode);
        var fmt = await e.Rte.PostAsJsonAsync("/api/v1/trains/t1/export-jobs", new { kind = "ReleaseReport", format = "zip" });   // only the evidence pack has a ZIP form
        Assert.Equal(HttpStatusCode.UnprocessableEntity, fmt.StatusCode);
        Assert.Equal("ExportKind", (await Json(fmt)).GetProperty("guard").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await e.Rte.PostAsJsonAsync("/api/v1/trains/nope/export-jobs", new { kind = "ReleaseReport" })).StatusCode);

        var id = (await Enqueue(e.Rte, "EvidencePack")).GetProperty("id").GetString()!;
        await e.Run();
        var del = await e.Rte.DeleteAsync($"/api/v1/export-jobs/{id}");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, del.StatusCode);
        Assert.Equal("ExportImmutable", (await Json(del)).GetProperty("guard").GetString());
        Assert.Equal("Done", Scalar(e.F, $"SELECT Status FROM ExportJobs WHERE Id='{id}'"));
        Assert.Equal(HttpStatusCode.NotFound, (await e.Rte.GetAsync("/api/v1/export-jobs/does-not-exist")).StatusCode);
    }

    [Fact]
    public async Task Repeating_a_request_while_queued_returns_the_same_job_and_listing_filters_by_train_and_status()
    {
        var e = await Setup(); using var _f = e.F;
        var a = await Enqueue(e.Rte, "ReleaseReport");
        var b = await Enqueue(e.Rte, "ReleaseReport");
        Assert.Equal(a.GetProperty("id").GetString(), b.GetProperty("id").GetString());
        Assert.Equal("1", Scalar(e.F, "SELECT COUNT(*) FROM ExportJobs"));
        var other = await Enqueue(e.Rm, "ReleaseReport");   // another requester gets their own job
        Assert.NotEqual(a.GetProperty("id").GetString(), other.GetProperty("id").GetString());

        Assert.Equal(2, (await Json(await e.Rte.GetAsync("/api/v1/export-jobs?status=Queued"))).GetArrayLength());
        Assert.Equal(0, (await Json(await e.Rte.GetAsync("/api/v1/export-jobs?trainId=other"))).GetArrayLength());
        Assert.Equal(2, await e.Run());
        Assert.Equal(0, (await Json(await e.Rte.GetAsync("/api/v1/export-jobs?status=Queued"))).GetArrayLength());
        Assert.Equal(2, (await Json(await e.Rte.GetAsync("/api/v1/export-jobs?trainId=t1&status=Done"))).GetArrayLength());
        var done = await Enqueue(e.Rte, "ReleaseReport");   // a Done job is never reused: a fresh one is created
        Assert.NotEqual(a.GetProperty("id").GetString(), done.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Job_status_only_changes_through_the_service_no_endpoint_patches_it()
    {
        var e = await Setup(); using var _f = e.F;
        var id = (await Enqueue(e.Rte, "ReleaseReport")).GetProperty("id").GetString()!;
        foreach (var m in new[] { HttpMethod.Put, HttpMethod.Patch })
        {
            var r = await e.Rte.SendAsync(new HttpRequestMessage(m, $"/api/v1/export-jobs/{id}") { Content = JsonContent.Create(new { status = "Done" }) });
            Assert.True(r.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound, r.StatusCode.ToString());
        }
        Assert.Equal("Queued", Scalar(e.F, $"SELECT Status FROM ExportJobs WHERE Id='{id}'"));
    }

    [Fact]
    public async Task Times_print_in_the_display_zone_not_utc()
    {
        var e = await Setup(); using var _f = e.F;
        var id = (await Enqueue(e.Rte, "ReleaseReport")).GetProperty("id").GetString()!;
        await e.Run();
        var (bytes, _) = await Download(e.Rte, id);
        using var doc = PdfDocument.Open(bytes);
        var all = string.Join("\n", Enumerable.Range(1, doc.NumberOfPages).Select(i => doc.GetPage(i).Text));
        Assert.Contains(" CT", all);   // window 06:00Z is 01:00 CT (default Display:TimeZone America/Chicago)
        Assert.Contains("01:00–05:00 CT", all.Replace(" ", " "));
    }

    [Fact]
    public async Task The_pdfa_option_writes_the_pdfa_marking_and_a_bad_value_is_refused_readably()
    {
        Environment.SetEnvironmentVariable("Pdf__PdfA", "2b");
        try
        {
            var e = await Setup(); using var _f = e.F;
            var id = (await Enqueue(e.Rte, "ReleaseReport")).GetProperty("id").GetString()!;
            await e.Run();
            var (bytes, _) = await Download(e.Rte, id);
            var text = Encoding.Latin1.GetString(bytes);
            Assert.Contains("<pdfaid:part>2</pdfaid:part>", text);   // marking only: conformance itself was checked with veraPDF, see docs/PDFA_SPIKE.md
            Assert.Contains("<pdfaid:conformance>B</pdfaid:conformance>", text);
            Assert.Contains("GTS_PDFA1", text);
        }
        finally { Environment.SetEnvironmentVariable("Pdf__PdfA", null); }

        Environment.SetEnvironmentVariable("Pdf__PdfA", "4z");
        try
        {
            var e = await Setup(); using var _f = e.F;
            var r = await e.Rte.PostAsJsonAsync("/api/v1/trains/t1/export-jobs", new { kind = "ReleaseReport" });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
            var body = await Json(r);
            Assert.Equal("ExportConfig", body.GetProperty("guard").GetString());
            Assert.Contains("Pdf:PdfA", body.GetProperty("message").GetString());
        }
        finally { Environment.SetEnvironmentVariable("Pdf__PdfA", null); }
    }
}
