using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Reminders;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Infrastructure.Exports;

public static class ExportGuards
{
    public const string Licence = "ExportLicence", Config = "ExportConfig", Role = "ExportRole", BadKind = "ExportKind", NotReady = "ExportNotReady", Terminal = "ExportJobFinished";
}

/// <summary>What is kept in <c>ExportJobs.Parameters</c> (the schema has no columns for these; Q-050a).</summary>
public sealed record ExportJobParams(string Format = ExportKinds.Pdf, string? ContentType = null, long? SizeBytes = null, string? Error = null, int Attempts = 0, DateTime? StartedAt = null, int? Pages = null);

public sealed record ExportJobView(string Id, string Kind, string Label, string Format, string? TrainId, string? TrainTitle, string Status, string FileName, string? Sha256, long? SizeBytes,
    string? ContentType, string? Error, int Attempts, string Ref, string RequestedByUserId, string? RequestedByName, DateTime CreatedAt, DateTime? StartedAt, DateTime? CompletedAt, int Version);

public sealed record ExportFile(ExportJobs Job, ExportJobParams Params, string FullPath);

/// <summary>Result of the crash-recovery pass: jobs put back in the queue and jobs that ran out of attempts.</summary>
public sealed record RecoveryResult(IReadOnlyList<string> Requeued, IReadOnlyList<string> Failed);

/// <summary>
/// ExportJobs lifecycle (REOS-50). The ONLY place a job's Status changes: Queued -> Running -> Done | Failed, plus Running -> Queued when a crash left a job behind.
/// Every change bumps Version and writes one AuditEvents row in the same transaction. A failure is never swallowed: <see cref="FailAsync"/> marks the job Failed
/// and raises SyncAlerts Export/ExportFailed (which also pushes SignalR) in one call. Done jobs are evidence: nothing here deletes or edits them.
/// </summary>
public sealed class ExportService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, ExportOptions options, SyncAlertWriter alerts, IAlertSink alertSink, ILogger<ExportService> log, IRealtimePublisher? realtime = null)
    : ServiceBase(dbf, time, realtime)
{
    private static readonly JsonSerializerOptions PJson = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    public const int MaxListed = 200;

    public static ExportJobParams ParamsOf(ExportJobs j)
    {
        try { return JsonSerializer.Deserialize<ExportJobParams>(j.Parameters, PJson) ?? new(); }
        catch (JsonException) { return new(); }   // a hand-edited row prints as a default rather than breaking the list; the raw text is still in the row
    }

    private static string Pack(ExportJobParams p) => JsonSerializer.Serialize(p, PJson);

    // ---- create ---------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Queues a job. An identical job (same train, kind, format, requester) that is still Queued/Running is returned instead of a duplicate (idempotent double click);
    /// a Done job is never reused, because evidence packs are regenerated on demand (PROJECT_SCOPE 9).
    /// </summary>
    public Task<ServiceResult<ExportJobView>> EnqueueAsync(string trainId, string kind, string? format, Actor actor, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            if (ExportKinds.Parse(kind) is not { } k) return ServiceResult<ExportJobView>.Fail(new GuardFailure(ExportGuards.BadKind, "Kind must be ReleaseReport, RunSheet, EvidencePack or Scorecard"));
            if (ExportKinds.ParseFormat(k, format) is not { } fmt) return ServiceResult<ExportJobView>.Fail(new GuardFailure(ExportGuards.BadKind, "Format must be pdf (or zip for the evidence pack)"));
            var train = await db.Set<ReleaseTrains>().AsNoTracking().Where(t => t.Id == trainId).Select(t => new { t.Id, t.Title }).SingleOrDefaultAsync(ct);
            if (train is null) return ServiceResult<ExportJobView>.NotFound("train");
            var role = await RoleOf(db, actor.UserId);
            if (role is null || role == Roles.Viewer && ExportKinds.NeedsAuditRead(k))
                return ServiceResult<ExportJobView>.Fail(new GuardFailure(ExportGuards.Role, "The evidence pack is for the RTE, Release Manager and Governance Officer roles"));
            if (options.LicenseProblem is { } lic) return ServiceResult<ExportJobView>.Fail(new GuardFailure(ExportGuards.Licence, lic));
            if (options.PdfAProblem is { } pa) return ServiceResult<ExportJobView>.Fail(new GuardFailure(ExportGuards.Config, pa));

            var open = await db.Set<ExportJobs>().Where(j => j.ReleaseTrainId == trainId && j.Kind == k && j.RequestedByUserId == actor.UserId && (j.Status == "Queued" || j.Status == "Running")).ToListAsync(ct);
            var dup = open.FirstOrDefault(j => ParamsOf(j).Format == fmt);
            if (dup is not null) return ServiceResult<ExportJobView>.Ok(await ViewAsync(db, dup, ct));

            var now = Now;
            var id = Ids.New();
            var ext = fmt == ExportKinds.Zip ? "zip" : "pdf";
            var job = new ExportJobs
            {
                Id = id, Kind = k, ReleaseTrainId = trainId, RequestedByUserId = actor.UserId, CreatedAt = now, Status = "Queued",
                FileName = $"{ExportKinds.Slug(k)}-{ExportSupport.SafeFileStem(train.Title)}-{ExportSupport.JobRef(id)[3..].ToLowerInvariant()}.{ext}",
                Parameters = Pack(new ExportJobParams(fmt, fmt == ExportKinds.Zip ? "application/zip" : "application/pdf")),
            };
            db.Set<ExportJobs>().Add(job);
            Audit(db, actor, trainId, "ExportJob", id, "Enqueue", null, new { kind = k, format = fmt, job.FileName });
            await db.SaveChangesAsync(ct);
            return ServiceResult<ExportJobView>.Ok(await ViewAsync(db, job, ct));
        }, ct);

    // ---- reads ----------------------------------------------------------------------------------------------------------------------------

    public async Task<ExportJobView?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var j = await db.Set<ExportJobs>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        return j is null ? null : await ViewAsync(db, j, ct);
    }

    /// <summary>Newest first. <paramref name="includeEvidencePacks"/> is false for callers without audit-read rights.</summary>
    public async Task<List<ExportJobView>> ListAsync(string? trainId, string? status, bool includeEvidencePacks, int? limit, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var q = db.Set<ExportJobs>().AsNoTracking().Where(j => ExportKinds.All.Contains(j.Kind));
        if (!string.IsNullOrEmpty(trainId)) q = q.Where(j => j.ReleaseTrainId == trainId);
        if (!string.IsNullOrEmpty(status)) q = q.Where(j => j.Status == status);
        if (!includeEvidencePacks) q = q.Where(j => j.Kind != ExportKinds.EvidencePack);
        var rows = await q.OrderByDescending(j => j.CreatedAt).ThenByDescending(j => j.Id).Take(Math.Clamp(limit ?? 50, 1, MaxListed)).ToListAsync(ct);
        var views = new List<ExportJobView>(rows.Count);
        foreach (var j in rows) views.Add(await ViewAsync(db, j, ct));
        return views;
    }

    private static async Task<ExportJobView> ViewAsync(ReleaseDbContext db, ExportJobs j, CancellationToken ct)
    {
        var p = ParamsOf(j);
        var title = j.ReleaseTrainId is null ? null : await db.Set<ReleaseTrains>().Where(t => t.Id == j.ReleaseTrainId).Select(t => t.Title).SingleOrDefaultAsync(ct);
        var by = await db.Set<Users>().Where(u => u.Id == j.RequestedByUserId).Select(u => u.DisplayName).SingleOrDefaultAsync(ct);
        return new ExportJobView(j.Id, j.Kind, ExportKinds.Label(j.Kind), p.Format, j.ReleaseTrainId, title, j.Status, j.FileName, j.Sha256, p.SizeBytes, p.ContentType, p.Error, p.Attempts,
            ExportSupport.JobRef(j.Id), j.RequestedByUserId, by, j.CreatedAt, p.StartedAt, j.CompletedAt, j.Version);
    }

    /// <summary>
    /// Resolves the file of a Done job for download. Not Done: 422 ExportNotReady. Missing on disk: 404 and an ExportFailed alert (rule 8).
    /// A file whose size (and, up to 32 MB, whole-file SHA-256) no longer matches the stored values is refused the same way: a corrupt pack is never handed out.
    /// </summary>
    public async Task<ServiceResult<ExportFile>> FindFileAsync(string id, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var j = await db.Set<ExportJobs>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (j is null) return ServiceResult<ExportFile>.NotFound("export job");
        if (j.Status != "Done") return ServiceResult<ExportFile>.Fail(new GuardFailure(ExportGuards.NotReady, j.Status == "Failed" ? "This export failed; there is no file. Generate it again" : "This export is not finished yet"));
        var p = ParamsOf(j);
        var path = j.StoragePath is null ? null : Resolve(j.StoragePath);
        if (path is null || !File.Exists(path))
        {
            await RaiseAsync(j, "file-missing", $"The stored file for export {ExportSupport.JobRef(j.Id)} ({j.FileName}) is missing from the exports directory; generate it again", ct);
            return ServiceResult<ExportFile>.NotFound("export file");
        }
        var len = new FileInfo(path).Length;
        string? problem = null;
        if (p.SizeBytes is long expected && len != expected) problem = $"is {len} bytes but {expected} were stored";
        else if (len <= 32 * 1024 * 1024 && j.Sha256 is not null && await ExportFiles.Sha256HexAsync(path, ct) != j.Sha256) problem = "no longer matches its stored SHA-256";
        if (problem is not null)
        {
            await RaiseAsync(j, "file-corrupt", $"The stored file for export {ExportSupport.JobRef(j.Id)} ({j.FileName}) {problem}; it was not served. Generate it again", ct);
            return ServiceResult<ExportFile>.Fail(new GuardFailure(ExportGuards.NotReady, "The stored file failed its integrity check and was not served; an alert was raised. Generate the export again"));
        }
        return ServiceResult<ExportFile>.Ok(new(j, p, path));
    }

    /// <summary>Stored paths are relative and must stay inside the exports directory (no rooted paths, no "..").</summary>
    public string? Resolve(string relative)
    {
        if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative)) return null;
        var root = options.Directory;
        var full = Path.GetFullPath(Path.Combine(root, relative));
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : null;
    }

    public static string RelativePathFor(string jobId, string format) => $"{jobId[^2..]}/{jobId}.{(format == ExportKinds.Zip ? "zip" : "pdf")}";

    // ---- worker-facing state changes -------------------------------------------------------------------------------------------------------

    /// <summary>Moves the oldest Queued job to Running (attempt counted, start time from the clock) and returns it; null when the queue is empty. Runs under the write lock, so two workers cannot claim one job.</summary>
    public async Task<ExportJobs?> ClaimNextAsync(CancellationToken ct = default)
    {
        var r = await RunAsync<ExportJobs?>(async db =>
        {
            var job = await db.Set<ExportJobs>().Where(j => j.Status == "Queued").OrderBy(j => j.CreatedAt).ThenBy(j => j.Id).FirstOrDefaultAsync(ct);
            if (job is null) return ServiceResult<ExportJobs?>.Ok(null);
            var p = ParamsOf(job);
            job.Status = "Running"; job.Version++;
            job.Parameters = Pack(p with { Attempts = p.Attempts + 1, StartedAt = Now, Error = null });
            Audit(db, null, job.ReleaseTrainId, "ExportJob", job.Id, "Start", null, new { attempt = p.Attempts + 1 });
            await db.SaveChangesAsync(ct);
            return ServiceResult<ExportJobs?>.Ok(job);
        }, ct);
        return r.IsOk ? r.Value : throw new InvalidOperationException("Could not claim an export job: " + Describe(r));
    }

    public async Task CompleteAsync(string jobId, string storagePath, string sha256, long sizeBytes, int? pages, CancellationToken ct = default)
    {
        var r = await RunAsync<ExportJobs>(async db =>
        {
            var job = await db.Set<ExportJobs>().SingleOrDefaultAsync(j => j.Id == jobId, ct);
            if (job is null) return ServiceResult<ExportJobs>.NotFound("export job");
            if (job.Status != "Running") return ServiceResult<ExportJobs>.Fail(new GuardFailure(ExportGuards.Terminal, $"Export job {jobId} is {job.Status}, not Running"));
            var p = ParamsOf(job);
            job.Status = "Done"; job.Version++; job.Sha256 = sha256; job.StoragePath = storagePath; job.CompletedAt = Now;
            job.Parameters = Pack(p with { SizeBytes = sizeBytes, Pages = pages, Error = null });
            Audit(db, null, job.ReleaseTrainId, "ExportJob", job.Id, "Complete", null, new { job.FileName, sha256, sizeBytes, pages });
            await db.SaveChangesAsync(ct);
            return ServiceResult<ExportJobs>.Ok(job);
        }, ct);
        if (!r.IsOk) throw new InvalidOperationException("Could not complete the export job: " + Describe(r));
    }

    /// <summary>Marks a Queued/Running job Failed with a readable reason, then raises the Export/ExportFailed alert (SyncAlerts row + SignalR push). Never throws for the alert path: a failing alert store is logged loudly.</summary>
    public async Task FailAsync(string jobId, string message, CancellationToken ct = default)
    {
        message = message.Length > 480 ? message[..479] + "…" : message;
        var r = await RunAsync<ExportJobs>(async db =>
        {
            var job = await db.Set<ExportJobs>().SingleOrDefaultAsync(j => j.Id == jobId, ct);
            if (job is null) return ServiceResult<ExportJobs>.NotFound("export job");
            if (job.Status is "Done" or "Failed") return ServiceResult<ExportJobs>.Ok(job);   // already final: nothing to change (Done evidence is never rewritten)
            var p = ParamsOf(job);
            job.Status = "Failed"; job.Version++; job.CompletedAt = Now;
            job.Parameters = Pack(p with { Error = message });
            Audit(db, null, job.ReleaseTrainId, "ExportJob", job.Id, "Fail", null, new { error = message });
            await db.SaveChangesAsync(ct);
            return ServiceResult<ExportJobs>.Ok(job);
        }, CancellationToken.None);
        if (!r.IsOk) { log.LogError("Export job {Job} failed ({Message}) and could not be marked Failed: {Why}", jobId, message, Describe(r)); }
        var job2 = r.Value;
        await RaiseAsync(job2 ?? new ExportJobs { Id = jobId, FileName = jobId }, "job", $"Export {ExportSupport.JobRef(jobId)} failed: {message}", ct);
    }

    /// <summary>
    /// Crash recovery. A job still Running after a restart (<paramref name="all"/>) or for longer than <see cref="ExportOptions.StaleAfter"/> is put back in the queue,
    /// or, once it has used <see cref="ExportOptions.MaxAttempts"/> attempts, failed visibly (with the alert). Rendering is deterministic and the file is written
    /// under a temporary name and moved into place, so a retry starts clean.
    /// </summary>
    public async Task<RecoveryResult> RecoverAsync(bool all, CancellationToken ct = default)
    {
        var requeued = new List<string>(); var failed = new List<(string Id, string Message)>();
        var r = await RunAsync<bool>(async db =>
        {
            var cutoff = Now - options.StaleAfter;
            var running = await db.Set<ExportJobs>().Where(j => j.Status == "Running").ToListAsync(ct);
            foreach (var j in running)
            {
                var p = ParamsOf(j);
                if (!all && p.StartedAt is DateTime s && s > cutoff) continue;
                if (p.Attempts < options.MaxAttempts)
                {
                    j.Status = "Queued"; j.Version++;
                    Audit(db, null, j.ReleaseTrainId, "ExportJob", j.Id, "Requeue", null, new { attempts = p.Attempts, reason = all ? "server restarted while running" : "no progress within the stale window" });
                    requeued.Add(j.Id);
                }
                else
                {
                    var msg = $"The export was interrupted {p.Attempts} time(s) (server stopped or the job stalled) and is out of attempts";
                    j.Status = "Failed"; j.Version++; j.CompletedAt = Now; j.Parameters = Pack(p with { Error = msg });
                    Audit(db, null, j.ReleaseTrainId, "ExportJob", j.Id, "Fail", null, new { error = msg });
                    failed.Add((j.Id, msg));
                }
            }
            if (running.Count > 0) await db.SaveChangesAsync(ct);
            return ServiceResult<bool>.Ok(true);
        }, ct);
        if (!r.IsOk) throw new InvalidOperationException("Export recovery failed: " + Describe(r));
        foreach (var (id, msg) in failed)
        {
            var j = await GetRawAsync(id, ct);
            await RaiseAsync(j ?? new ExportJobs { Id = id, FileName = id }, "job", $"Export {ExportSupport.JobRef(id)} failed: {msg}", ct);
        }
        return new RecoveryResult(requeued, failed.Select(f => f.Id).ToList());
    }

    /// <summary>The worker itself failed (not one job): recorded as an Export/ExportFailed alert with key "worker" so it reaches the banner and SignalR.</summary>
    public Task RaiseWorkerFailureAsync(string message) => RaiseAsync(new ExportJobs { Id = "worker", FileName = "worker" }, "worker", message, CancellationToken.None);

    private async Task<ExportJobs?> GetRawAsync(string id, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        return await db.Set<ExportJobs>().AsNoTracking().SingleOrDefaultAsync(j => j.Id == id, ct);
    }

    private async Task RaiseAsync(ExportJobs job, string key, string message, CancellationToken ct)
    {
        var fp = key == "job" ? job.Id : $"{key}:{job.Id}";
        try
        {
            await alertSink.RaiseAsync("Export", "ExportFailed", fp, message, CancellationToken.None);
            await alerts.RaiseAsync("Export", "ExportFailed", fp, message, job.ReleaseTrainId, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Export failure could not be recorded as an alert: {Message}", message);   // the alert store itself is failing; the log is all that is left
        }
    }

    private static string Describe<T>(ServiceResult<T> r) => r.Failures.Count > 0 ? r.Failures[0].Message : r.Missing ?? r.Kind.ToString();
}
