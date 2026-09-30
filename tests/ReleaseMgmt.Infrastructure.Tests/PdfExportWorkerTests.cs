using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Analytics;
using ReleaseMgmt.Infrastructure.Exports;
using ReleaseMgmt.Infrastructure.Exports.Documents;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Reminders;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Infrastructure.Tests;

/// <summary>REOS-50: the export worker on a fake clock: claim, restart-safety, retry limits, fail-visibly, no orphans, deterministic output.</summary>
public sealed class PdfExportWorkerTests(TriggerSuiteFixture fx) : IClassFixture<TriggerSuiteFixture>
{
    private sealed class Factory(string path) : IDbContextFactory<ReleaseDbContext>
    {
        public ReleaseDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ReleaseDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").AddInterceptors(new SqliteConnectionInterceptor()).Options);
    }

    private sealed class Sink : IAlertSink
    {
        public List<(string Source, string Kind, string Key, string Message)> Raised { get; } = [];
        public Task RaiseAsync(string sourceSystem, string kind, string key, string message, CancellationToken ct = default) { Raised.Add((sourceSystem, kind, key, message)); return Task.CompletedTask; }
    }

    private sealed class Env
    {
        public required string Path { get; init; }
        public required string Dir { get; init; }
        public required FakeTimeProvider Time { get; init; }
        public required IDbContextFactory<ReleaseDbContext> Db { get; init; }
        public required ExportOptions Options { get; init; }
        public required ExportService Jobs { get; init; }
        public required ExportWorker Worker { get; init; }
        public required ExportModelLoader Loader { get; init; }
        public required Sink Alerts { get; init; }

        public void Sql(string sql, params object?[] p) { using var c = TriggerSuiteFixture.Open(Path); TriggerSuiteFixture.Run(c, sql, p); }
        public object? Scalar(string sql, params object?[] p)
        {
            using var c = TriggerSuiteFixture.Open(Path); using var cmd = c.CreateCommand();
            TriggerSuiteFixture.Bind(cmd, sql, p);
            var v = cmd.ExecuteScalar();
            return v is DBNull ? null : v;
        }
        public long Count(string sql, params object?[] p) => Convert.ToInt64(Scalar(sql, p));
        public string Status(string id) => (string)Scalar("SELECT Status FROM ExportJobs WHERE Id=?", id)!;
        public string Params(string id) => (string)Scalar("SELECT Parameters FROM ExportJobs WHERE Id=?", id)!;
        public int Files => Directory.Exists(Dir) ? Directory.EnumerateFiles(Dir, "*", SearchOption.AllDirectories).Count() : 0;
        public void At(string utc) => Time.SetUtcNow(DateTimeOffset.Parse(utc, null, System.Globalization.DateTimeStyles.AssumeUniversal));
    }

    private Env NewEnv(string? licence = "Community", int maxAttempts = 2, string? pdfA = null)
    {
        var path = fx.FreshPath();
        var dir = Path.Combine(fx.Dir, "exports-" + Guid.NewGuid().ToString("N")[..8]);
        var time = new FakeTimeProvider(); time.SetUtcNow(DateTimeOffset.Parse("2026-10-20T14:00:00Z"));
        var db = new Factory(path);
        var options = new ExportOptions(dir, licence, true, 5, TimeSpan.FromSeconds(300), maxAttempts, 5000, pdfA);
        var sink = new Sink();
        var alerts = new SyncAlertWriter(db, time, NullLogger<SyncAlertWriter>.Instance);
        var jobs = new ExportService(db, time, options, alerts, sink, NullLogger<ExportService>.Instance);
        var audit = new AuditQueryService(db);
        var loader = new ExportModelLoader(db, audit, new DisplayClock(TimeZoneInfo.FindSystemTimeZoneById("America/Chicago")), options);
        var store = new AttachmentService(db, time, new AttachmentOptions(Path.Combine(fx.Dir, "att-" + Guid.NewGuid().ToString("N")[..8]), 1_000_000), sink);
        var snaps = new MetricSnapshotService(db, time, new AnalyticsService(new SqliteAnalyticsConnectionFactory(path)));
        var worker = new ExportWorker(jobs, loader, snaps, store, options, time, NullLogger<ExportWorker>.Instance);
        var e = new Env { Path = path, Dir = dir, Time = time, Db = db, Options = options, Jobs = jobs, Worker = worker, Loader = loader, Alerts = sink };
        return e;
    }

    private static Actor Rte => new("rte");

    private static async Task<string> Enqueue(Env e, string kind = "ReleaseReport", string? format = null)
    {
        var r = await e.Jobs.EnqueueAsync("t1", kind, format, Rte);
        Assert.True(r.IsOk, r.Failures.FirstOrDefault()?.Message);
        return r.Value!.Id;
    }

    [Fact]
    public async Task Queued_job_is_rendered_stored_hashed_and_marked_done_with_audit_and_versions()
    {
        var e = NewEnv();
        var id = await Enqueue(e, "EvidencePack");
        Assert.Equal("Queued", e.Status(id));
        Assert.Equal(1, await e.Worker.RunOnceAsync());
        Assert.Equal("Done", e.Status(id));
        var path = (string)e.Scalar("SELECT StoragePath FROM ExportJobs WHERE Id=?", id)!;
        var full = Path.Combine(e.Dir, path);
        Assert.Equal(await ExportFiles.Sha256HexAsync(full), (string)e.Scalar("SELECT Sha256 FROM ExportJobs WHERE Id=?", id)!);
        Assert.Equal(3L, e.Count("SELECT Version FROM ExportJobs WHERE Id=?", id));
        Assert.Equal("2026-10-20T14:00:00Z", e.Scalar("SELECT CompletedAt FROM ExportJobs WHERE Id=?", id));   // the injected clock, not the wall clock
        Assert.Equal(1L, e.Count("SELECT COUNT(*) FROM AuditEvents WHERE EntityType='ExportJob' AND EntityId=? AND Action='Complete'", id));
        Assert.True(e.Count("SELECT COUNT(*) FROM MetricSnapshots WHERE ExportJobId=?", id) > 0);
        Assert.Equal(0, await e.Worker.RunOnceAsync());   // nothing left: idempotent
        Assert.Equal(1, e.Files);
    }

    [Fact]
    public async Task A_job_left_running_by_a_crash_is_requeued_once_stale_and_completes_on_the_next_pass()
    {
        var e = NewEnv();
        var id = await Enqueue(e);
        var claimed = await e.Jobs.ClaimNextAsync();   // "the process died here": Running, attempt 1, started at 14:00
        Assert.Equal(id, claimed!.Id);
        Assert.Equal("Running", e.Status(id));

        e.Time.Advance(TimeSpan.FromSeconds(120));   // inside the stale window: it might still be running, so it is left alone
        Assert.Equal(0, await e.Worker.RunOnceAsync());
        Assert.Equal("Running", e.Status(id));

        e.Time.Advance(TimeSpan.FromSeconds(200));   // 320 s > 300 s
        Assert.Equal(1, await e.Worker.RunOnceAsync());
        Assert.Equal("Done", e.Status(id));
        Assert.Contains("\"attempts\":2", e.Params(id));
        Assert.Equal(1L, e.Count("SELECT COUNT(*) FROM AuditEvents WHERE EntityType='ExportJob' AND EntityId=? AND Action='Requeue'", id));
        Assert.Equal(1, e.Files);
        Assert.Equal(0L, e.Count("SELECT COUNT(*) FROM SyncAlerts"));
    }

    [Fact]
    public async Task A_job_that_keeps_dying_fails_visibly_after_max_attempts_with_an_alert_and_no_file()
    {
        var e = NewEnv(maxAttempts: 2);
        var id = await Enqueue(e);
        await e.Jobs.ClaimNextAsync();                       // attempt 1 dies
        e.Time.Advance(TimeSpan.FromSeconds(301));
        await e.Jobs.RecoverAsync(all: false);               // back to Queued
        Assert.Equal("Queued", e.Status(id));
        await e.Jobs.ClaimNextAsync();                       // attempt 2 dies
        e.Time.Advance(TimeSpan.FromSeconds(301));
        var rec = await e.Jobs.RecoverAsync(all: false);     // out of attempts
        Assert.Equal([id], rec.Failed);
        Assert.Equal("Failed", e.Status(id));
        Assert.Contains("interrupted 2 time", e.Params(id));
        Assert.Equal(1L, e.Count("SELECT COUNT(*) FROM SyncAlerts WHERE SourceSystem='Export' AND Kind='ExportFailed' AND IsResolved=0"));
        Assert.Equal(0, await e.Worker.RunOnceAsync());      // a Failed job is not retried behind anyone's back
        Assert.Equal(0, e.Files);
    }

    [Fact]
    public async Task Startup_recovery_requeues_every_running_job_even_a_fresh_one()
    {
        var e = NewEnv();
        var id = await Enqueue(e);
        await e.Jobs.ClaimNextAsync();
        var rec = await e.Jobs.RecoverAsync(all: true);
        Assert.Equal([id], rec.Requeued);
        Assert.Equal("Queued", e.Status(id));
        Assert.Equal(1, await e.Worker.RunOnceAsync());
        Assert.Equal("Done", e.Status(id));
    }

    [Fact]
    public async Task Two_workers_cannot_claim_the_same_job()
    {
        var e = NewEnv();
        var id = await Enqueue(e);
        var claims = await Task.WhenAll(e.Jobs.ClaimNextAsync(), e.Jobs.ClaimNextAsync());
        Assert.Single(claims, c => c is not null);
        Assert.Equal(id, claims.Single(c => c is not null)!.Id);
        Assert.Contains("\"attempts\":1", e.Params(id));
    }

    [Fact]
    public async Task A_finished_job_is_never_rewritten_by_a_late_failure()
    {
        var e = NewEnv();
        var id = await Enqueue(e);
        await e.Worker.RunOnceAsync();
        var sha = (string)e.Scalar("SELECT Sha256 FROM ExportJobs WHERE Id=?", id)!;
        await e.Jobs.FailAsync(id, "late failure from a stuck retry");
        Assert.Equal("Done", e.Status(id));
        Assert.Equal(sha, (string)e.Scalar("SELECT Sha256 FROM ExportJobs WHERE Id=?", id)!);
        var again = await e.Jobs.RecoverAsync(all: true);
        Assert.Empty(again.Requeued);
        Assert.Equal("Done", e.Status(id));
    }

    [Fact]
    public async Task A_train_deleted_while_queued_fails_the_job_readably_with_an_alert()
    {
        var e = NewEnv();
        var id = await Enqueue(e);
        e.Sql("UPDATE ExportJobs SET ReleaseTrainId=NULL WHERE Id=?", id);   // what ON DELETE SET NULL leaves behind
        await e.Worker.RunOnceAsync();
        Assert.Equal("Failed", e.Status(id));
        Assert.Contains("no longer exists", e.Params(id));
        Assert.Equal(1L, e.Count("SELECT COUNT(*) FROM SyncAlerts WHERE SourceSystem='Export' AND Kind='ExportFailed'"));
        Assert.Equal(0, e.Files);
    }

    [Fact]
    public async Task A_repeat_failure_bumps_the_open_alert_instead_of_adding_rows_and_each_job_has_its_own_key()
    {
        var e = NewEnv();
        var a = await Enqueue(e, "ReleaseReport");
        var b = await Enqueue(e, "Scorecard");
        e.Sql("UPDATE ExportJobs SET ReleaseTrainId=NULL");
        await e.Worker.RunOnceAsync();
        Assert.Equal("Failed", e.Status(a)); Assert.Equal("Failed", e.Status(b));
        Assert.Equal(2L, e.Count("SELECT COUNT(*) FROM SyncAlerts WHERE SourceSystem='Export' AND IsResolved=0"));
        await e.Jobs.FailAsync(a, "still failing");   // Failed already: no state change, but the alert is repeated (count bumps, no new row)
        Assert.Equal(2L, e.Count("SELECT COUNT(*) FROM SyncAlerts WHERE SourceSystem='Export'"));
        Assert.Equal(2L, e.Count("SELECT MAX(OccurrenceCount) FROM SyncAlerts WHERE SourceSystem='Export'"));
    }

    [Fact]
    public async Task Missing_licence_is_refused_at_enqueue_and_fails_a_queued_job_readably()
    {
        var e = NewEnv(licence: null);
        var r = await e.Jobs.EnqueueAsync("t1", "ReleaseReport", null, Rte);
        Assert.Equal(ResultKind.GuardFailed, r.Kind);
        Assert.Equal(ExportGuards.Licence, r.Failures[0].Guard);
        Assert.Contains("Pdf:QuestPdfLicense", r.Failures[0].Message);

        // a queued job whose host has lost its licence
        var ok = NewEnv();
        var id = await Enqueue(ok);
        var worker = new ExportWorker(ok.Jobs, ok.Loader, new MetricSnapshotService(ok.Db, ok.Time, new AnalyticsService(new SqliteAnalyticsConnectionFactory(ok.Path))),
            new AttachmentService(ok.Db, ok.Time, new AttachmentOptions(fx.Dir, 1000), ok.Alerts), ok.Options with { License = "Freeware" }, ok.Time, NullLogger<ExportWorker>.Instance);
        await worker.RunOnceAsync();
        Assert.Equal("Failed", ok.Status(id));
        Assert.Contains("not a QuestPDF licence tier", ok.Params(id));
        Assert.Equal(0, ok.Files);
    }

    [Fact]
    public void Licence_and_pdfa_options_are_validated_and_nothing_is_defaulted()
    {
        ExportOptions O(string? lic, bool dev = false, string? pdfa = null) => new("d", lic, dev, 5, TimeSpan.FromSeconds(300), 2, 5000, pdfa);
        Assert.Contains("not configured", O(null).LicenseProblem);
        Assert.Contains("not configured", O("  ").LicenseProblem);
        Assert.Contains("not a QuestPDF licence tier", O("Gratis").LicenseProblem);
        Assert.Contains("not permitted outside Development", O("Evaluation").LicenseProblem);
        Assert.Null(O("Evaluation", dev: true).LicenseProblem);
        foreach (var ok in new[] { "Community", "community", "Professional", "Enterprise" }) Assert.Null(O(ok).LicenseProblem);
        Assert.Throws<ExportConfigException>(() => O(null).ApplyLicense());

        Assert.Null(O("Community", pdfa: null).PdfAProblem);
        Assert.Null(O("Community", pdfa: "false").PdfAProblem);
        Assert.Null(O("Community", pdfa: "true").TryPdfA(out var l1)); Assert.Equal("2b", l1);
        Assert.Null(O("Community", pdfa: "3b").TryPdfA(out var l2)); Assert.Equal("3b", l2);
        Assert.Contains("Pdf:PdfA", O("Community", pdfa: "4z").PdfAProblem);
        Assert.Throws<ExportConfigException>(() => O("Community", pdfa: "4z").ApplyLicense());
    }

    [Fact]
    public void Stored_paths_cannot_leave_the_exports_directory()
    {
        var e = NewEnv();
        Assert.Null(e.Jobs.Resolve("../outside.pdf"));
        Assert.Null(e.Jobs.Resolve("ab/../../outside.pdf"));
        Assert.Null(e.Jobs.Resolve("/etc/passwd"));
        Assert.Null(e.Jobs.Resolve(""));
        Assert.StartsWith(e.Dir, e.Jobs.Resolve(ExportService.RelativePathFor("0192a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b", "pdf")));
        Assert.EndsWith(".zip", ExportService.RelativePathFor("0192a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b", "zip"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("2b")]
    [InlineData("3b")]
    public async Task The_same_data_renders_byte_identical_pdfs_so_a_regenerated_pack_can_be_compared(string? pdfA)
    {
        var e = NewEnv(pdfA: pdfA);
        e.Options.ApplyLicense();
        foreach (var kind in ExportKinds.All)
        {
            var now = DateTime.SpecifyKind(new DateTime(2026, 10, 20, 14, 0, 0), DateTimeKind.Utc);
            var m1 = (await e.Loader.LoadAsync("t1", "0192a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b", kind, "rte", now))!;
            var m2 = (await e.Loader.LoadAsync("t1", "0192a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b", kind, "rte", now))!;
            static QuestPDF.Infrastructure.IDocument Doc(string k, ExportModel m, ExportOptions o) => k switch
            {
                ExportKinds.ReleaseReport => new ReleaseReportDocument(m, o), ExportKinds.RunSheet => new RunSheetDocument(m, o),
                ExportKinds.EvidencePack => new EvidencePackDocument(m, o), _ => new ScorecardDocument(m, o),
            };
            var a = QuestPDF.Fluent.GenerateExtensions.GeneratePdf(Doc(kind, m1, e.Options));
            var b = QuestPDF.Fluent.GenerateExtensions.GeneratePdf(Doc(kind, m2, e.Options));
            // PDF/A writes random xmpMM DocumentID/InstanceID uuids and derives the trailer /ID from them (see docs/PDFA_SPIKE.md): everything else must match byte for byte.
            static string Mask(byte[] x) => System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(System.Text.Encoding.Latin1.GetString(x), "uuid:[0-9a-fA-F-]{36}", "uuid:X"), @"/ID \[.*?\]>>", "/ID [X]>>", System.Text.RegularExpressions.RegexOptions.Singleline);   // the two random ids are written as hex <..> or as a binary literal (..), depending on the bytes
            if (pdfA is null) Assert.True(a.AsSpan().SequenceEqual(b), $"{kind} is not deterministic");
            else
            {
                string ma = Mask(a), mb = Mask(b);
                var at = ma == mb ? -1 : Enumerable.Range(0, Math.Min(ma.Length, mb.Length)).FirstOrDefault(i => ma[i] != mb[i], Math.Min(ma.Length, mb.Length));
                Assert.True(at < 0, $"{kind} (PDF/A {pdfA}) differs by more than the XMP uuids and the trailer /ID; lengths {ma.Length}/{mb.Length}, first difference at {at}: [{(at < 0 ? "" : ma.Substring(Math.Max(0, at - 60), Math.Min(140, ma.Length - Math.Max(0, at - 60))).Replace("\n", "\\n"))}] vs [{(at < 0 ? "" : mb.Substring(Math.Max(0, at - 60), Math.Min(140, mb.Length - Math.Max(0, at - 60))).Replace("\n", "\\n"))}]");
            }
        }
    }

    [Fact]
    public async Task Snapshot_rows_are_replaced_for_a_retried_job_and_never_touch_other_jobs()
    {
        var e = NewEnv();
        var snaps = new MetricSnapshotService(e.Db, e.Time, new AnalyticsService(new SqliteAnalyticsConnectionFactory(e.Path)));
        var a = await Enqueue(e, "EvidencePack"); var b = await Enqueue(e, "ReleaseReport");
        var asOf = DateTime.SpecifyKind(new DateTime(2026, 10, 20, 14, 0, 0), DateTimeKind.Utc);
        var first = await snaps.CaptureAsync(a, "t1", "R26.10", asOf);
        var other = await snaps.CaptureAsync(b, "t1", "R26.10", asOf);
        var again = await snaps.CaptureAsync(a, "t1", "R26.10", asOf);
        Assert.Equal(first.Select(x => (x.Key, x.Value)), again.Select(x => (x.Key, x.Value)));   // reproducible
        Assert.Equal(first.Count, e.Count("SELECT COUNT(*) FROM MetricSnapshots WHERE ExportJobId=?", a));
        Assert.Equal(other.Count, e.Count("SELECT COUNT(*) FROM MetricSnapshots WHERE ExportJobId=?", b));
        Assert.All(first, m => Assert.Equal(asOf, m.CapturedAt));
    }
}
