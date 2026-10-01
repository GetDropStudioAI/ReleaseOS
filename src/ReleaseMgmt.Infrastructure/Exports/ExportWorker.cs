using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuestPDF.Fluent;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Exports.Documents;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Infrastructure.Exports;

/// <summary>
/// Renders queued ExportJobs (REOS-50). A timer calls <see cref="RunOnceAsync"/> every <c>Exports:PollSeconds</c>; tests call it directly (it reads only the injected TimeProvider).
/// Per job: licence check, one consistent read of the train, (evidence pack) MetricSnapshots written and read back, QuestPDF render, file written under a temporary name
/// OUTSIDE wwwroot, SHA-256 computed over the FINAL bytes on disk, file moved into its id-named place, job marked Done with hash and size. ANY exception marks the job Failed
/// with a readable message and raises Export/ExportFailed (see <see cref="ExportService.FailAsync"/>); the partial and final files are removed, so a failure leaves no orphan.
/// A job left Running by a crash is recovered (requeued, or failed visibly once out of attempts) at startup and whenever it is older than Exports:StaleRunningSeconds.
/// Retention (REOS-72): at startup and then every <see cref="ExportOptions.PurgeInterval"/>, files older than Exports:RetentionDays are deleted and their jobs marked
/// expired (<see cref="ExportService.PurgeExpiredAsync"/>); a failing pass raises Export/ExportFailed like any worker failure.
/// </summary>
public sealed class ExportWorker(ExportService jobs, ExportModelLoader loader, MetricSnapshotService snapshots, AttachmentService attachments, ExportOptions options, TimeProvider time, ILogger<ExportWorker> log)
    : BackgroundService
{
    private DateTime Now { get { var t = time.GetUtcNow().UtcDateTime; return new DateTime(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc); } }
    private string TempDir => Path.Combine(options.Directory, ".tmp");
    private DateTime? _lastPurge;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try
        {
            // Nothing else can be rendering at startup, so every Running job is an orphan of the previous process.
            var rec = await jobs.RecoverAsync(all: true, stop);
            if (rec.Requeued.Count + rec.Failed.Count > 0) log.LogWarning("Export recovery at startup: {Requeued} requeued, {Failed} failed", rec.Requeued.Count, rec.Failed.Count);
            SweepTemp();
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { await WorkerFailedAsync("startup recovery", ex); }
        await PurgeIfDueAsync(stop);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.PollSeconds), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stop))
            {
                try { await RunOnceAsync(stop); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch (Exception ex) { await WorkerFailedAsync("export pass", ex); }
                await PurgeIfDueAsync(stop);
            }
        }
        catch (OperationCanceledException) { /* shutting down: a Running job is recovered at the next start */ }
    }

    /// <summary>One pass: recover stale Running jobs, then process the queue until it is empty. Returns how many jobs it ran (Done or Failed).</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        await jobs.RecoverAsync(all: false, ct);
        var ran = 0;
        while (!ct.IsCancellationRequested && await jobs.ClaimNextAsync(ct) is { } job)
        {
            await ProcessAsync(job, ct);
            ran++;
        }
        return ran;
    }

    /// <summary>Runs the retention purge when it has not run within <see cref="ExportOptions.PurgeInterval"/> (by the injected clock). Never throws: a failure is an alert.</summary>
    public async Task PurgeIfDueAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (_lastPurge is DateTime last && now - last < ExportOptions.PurgeInterval) return;
        _lastPurge = now;
        try { await jobs.PurgeExpiredAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { await WorkerFailedAsync("retention purge", ex); }
    }

    private async Task WorkerFailedAsync(string what, Exception ex)
    {
        log.LogError(ex, "Export worker {What} failed", what);
        await jobs.RaiseWorkerFailureAsync($"The export worker's {what} failed: {ex.GetType().Name}: {ex.Message}");
    }

    private void SweepTemp()
    {
        if (!Directory.Exists(TempDir)) return;
        foreach (var f in Directory.EnumerateFiles(TempDir))
            if (ExportFiles.TryDelete(f) is { } problem) log.LogWarning("{Problem}", problem);
    }

    private async Task ProcessAsync(ExportJobs job, CancellationToken ct)
    {
        var format = ExportService.ParamsOf(job).Format;
        var rel = ExportService.RelativePathFor(job.Id, format);
        string? temp = null, final = null;
        var done = false;
        try
        {
            options.ApplyLicense();
            if (job.ReleaseTrainId is null) throw new InvalidOperationException("The train this export was requested for no longer exists");
            var now = Now;
            var model = await loader.LoadAsync(job.ReleaseTrainId, job.Id, job.Kind, job.RequestedByUserId, now, ct)
                        ?? throw new InvalidOperationException("The train this export was requested for no longer exists");
            if (job.Kind == ExportKinds.EvidencePack)
                model = model with { Metrics = await snapshots.CaptureAsync(job.Id, model.Train.Id, model.Train.Title, now, ct) };

            var pdf = Create(job.Kind, model).GeneratePdf();

            Directory.CreateDirectory(TempDir);
            temp = Path.Combine(TempDir, $"{job.Id}.{Guid.NewGuid():N}.part");
            if (format == ExportKinds.Zip)
                await EvidenceZip.WriteAsync(temp, Path.ChangeExtension(job.FileName, ".pdf"), pdf, model.Attachments, attachments, now, ct);
            else
                await File.WriteAllBytesAsync(temp, pdf, ct);

            var sha = await ExportFiles.Sha256HexAsync(temp, ct);   // over the bytes as they are on disk: what a download will return
            var size = new FileInfo(temp).Length;
            final = jobs.Resolve(rel) ?? throw new InvalidOperationException("Export path escaped the exports directory");
            Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            File.Move(temp, final, overwrite: true);   // a retry of the same job replaces its own earlier file
            temp = null;

            await jobs.CompleteAsync(job.Id, rel, sha, size, null, ct);
            done = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // shutting down: the job stays Running and is recovered at the next start
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Export job {Job} ({Kind}) failed", job.Id, job.Kind);
            await jobs.FailAsync(job.Id, Readable(ex), CancellationToken.None);
        }
        finally
        {
            if (!done)
            {
                if (ExportFiles.TryDelete(temp) is { } p1) log.LogWarning("{Problem}", p1);
                if (ExportFiles.TryDelete(final) is { } p2) log.LogWarning("{Problem}", p2);
            }
        }
    }

    private static string Readable(Exception ex) => ex switch
    {
        ExportConfigException or ExportIntegrityException => ex.Message,
        InvalidOperationException => ex.Message,
        _ => $"{ex.GetType().Name}: {ex.Message}",
    };

    private QuestPDF.Infrastructure.IDocument Create(string kind, ExportModel m) => kind switch
    {
        ExportKinds.ReleaseReport => new ReleaseReportDocument(m, options),
        ExportKinds.RunSheet => new RunSheetDocument(m, options),
        ExportKinds.EvidencePack => new EvidencePackDocument(m, options),
        ExportKinds.Scorecard => new ScorecardDocument(m, options),
        _ => throw new InvalidOperationException($"Unknown export kind {kind}"),
    };
}
