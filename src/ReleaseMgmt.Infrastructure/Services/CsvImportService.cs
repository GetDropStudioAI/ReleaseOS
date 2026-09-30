using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Exchange;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Exchange;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

/// <summary>Where preview files live (outside SQLite and outside wwwroot). Config key <c>Imports:Directory</c>; default: <c>imports</c> next to the database file.</summary>
public sealed record ImportOptions(string Directory);

public sealed record ImportCounts(int New, int Updated, int Unchanged, int Errors);

public sealed record ImportPreview(string JobId, string Kind, string Mode, string FileName, string Sha256, int ByteCount, int RowCount, IReadOnlyList<string> Columns, IReadOnlyList<string> SkippedColumns,
    ImportCounts Counts, IReadOnlyList<ImportError> Errors, IReadOnlyList<ImportError> Warnings, IReadOnlyList<string> DecertifiesGates, IReadOnlyList<PlanRow> Rows, int RowsTotal,
    string Status, string CreatedAt, string ExpiresAt, int Version);

public sealed record ImportJobView(string Id, string Kind, string? Mode, string? TrainId, string FileName, string Sha256, int RowCount, int ErrorCount, IReadOnlyList<ImportError> Errors,
    ImportCounts? Counts, string Status, string UploadedByUserId, string? UploadedBy, string CreatedAt, string? CommittedAt, int Version);

public sealed record ImportCommitResult(string JobId, string Kind, string Mode, int Inserted, int Updated, int Unchanged, IReadOnlyList<string> DecertifiedGates, int Version);

internal sealed record StoredPlan(string Mode, string Signature, ImportCounts Counts, IReadOnlyList<string> Columns, IReadOnlyList<string> Skipped, IReadOnlyList<string> Decertifies,
    IReadOnlyList<ImportError> Warnings, IReadOnlyList<PlanRow> Rows, int ByteCount);

/// <summary>
/// CSV import (PROJECT_SCOPE 9, D18; REOS-48). <c>PreviewAsync</c> parses and validates every row with Sep, stores an <c>ImportJobs</c> row (Previewed) and the file beside it;
/// <c>CommitAsync</c> re-plans the stored file against the database as it is now inside one transaction, and refuses when the plan moved (409), when there are errors (422; the
/// <c>trg_Import_NoCommitWithErrors</c> trigger is the backstop), or when a certified gate would be decertified without acknowledgement.
/// <para><b>Audit (Q-048c, D27)</b>: every created or changed row gets its own AuditEvents row with only the changed columns as before/after, actor and service clock;
/// the job gets one more row (<c>ImportJob</c> / <c>Commit</c>) with the counts. Unchanged rows write nothing. Version is stamped on every changed row; a train whose plan rows
/// changed is bumped once (as BulkCommit does), so open previews of the same train go stale.</para>
/// </summary>
public sealed class CsvImportService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, ImportOptions options, IRealtimePublisher? realtime = null) : ServiceBase(dbf, time, realtime)
{
    private static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web);
    public const int PreviewRows = 200, MaxStoredErrors = 1000;
    public static readonly string[] Modes = ["Append", "Upsert"];

    internal static readonly IReadOnlyDictionary<string, KindHandler> Handlers = new KindHandler[]
    {
        new TrainsHandler(), new ProductsHandler(), new GatesHandler(), new TasksHandler(), new StepsHandler(), new LinksHandler(), new HolidaysHandler(), new UsersHandler(), new TeamsHandler(),
    }.ToDictionary(h => h.Spec.Kind, StringComparer.OrdinalIgnoreCase);

    private string FilePath(string jobId) => Path.Combine(options.Directory, jobId + ".csv");
    private string PlanPath(string jobId) => Path.Combine(options.Directory, jobId + ".plan.json");
    private static string Iso(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    // ---- preview ---------------------------------------------------------------------------------------------------------------------
    public async Task<ServiceResult<ImportPreview>> PreviewAsync(string kind, string mode, string? fileName, byte[] bytes, Actor actor, CancellationToken ct = default)
    {
        var spec = ImportKinds.Get(kind);
        if (spec is null) return Fail<ImportPreview>(ExchangeGuards.UnknownImportKind, $"{kind} is not an import kind. Kinds: {string.Join(", ", ImportKinds.All.Select(k => k.Kind))}");
        var m = Modes.FirstOrDefault(x => string.Equals(x, mode, StringComparison.OrdinalIgnoreCase));
        if (m is null) return Fail<ImportPreview>(ExchangeGuards.InvalidImportMode, "Mode is Append (an existing key is an error) or Upsert (an existing key is updated)");
        if (bytes.Length > ImportLimits.MaxBytes) return Fail<ImportPreview>(ExchangeGuards.ImportTooLarge, $"The file is larger than {ImportLimits.MaxBytes / (1024 * 1024)} MB");
        var name = CleanName(fileName);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        var (content, fatal) = CsvFile.Read(bytes);
        if (content is null)
        {
            await RecordRejectedAsync(spec.Kind, name, sha, fatal!, actor, ct);
            return Fail<ImportPreview>(ExchangeGuards.ImportRejected, fatal!);
        }

        var handler = Handlers[spec.Kind];
        return await RunAsync(async db =>
        {
            var now = Now;
            await ExpireOldAsync(db, now, ct);
            var header = RowParser.ResolveHeader(spec, content.Header);
            var parsed = header.Errors.Any(e => e.Message.StartsWith("Required column", StringComparison.Ordinal)) ? [] : content.Rows.Select(r => RowParser.Parse(spec, header, r.Row, r.Cells)).ToList();
            var env = new PlanEnv(db, spec, m, header, parsed, now);
            env.Errors.AddRange(header.Errors);
            if (content.Rows.Count == 0) env.Err(0, "(file)", "The file has a header but no data rows");
            var plan = parsed.Count == 0 ? new PlanOutcome([], env.Errors, [], []) : await handler.PlanAsync(env, new GridFilter(), ct);
            var errors = SortErrors(plan.Errors);

            var trains = plan.Rows.Select(r => r.TrainId).Where(t => t is not null).Distinct().ToList();
            var job = new ImportJobs
            {
                Kind = spec.Kind, ReleaseTrainId = trains.Count == 1 ? trains[0] : null, FileName = name, Sha256 = sha, RowCount = content.Rows.Count, ErrorCount = errors.Count,
                ErrorsJson = JsonSerializer.Serialize(Capped(errors), J), Status = "Previewed", UploadedByUserId = actor.UserId, CreatedAt = now,
            };
            db.Set<ImportJobs>().Add(job);
            await db.SaveChangesAsync(ct);

            var columns = spec.Columns.Where(c => header.Columns.ContainsKey(c.Name)).Select(c => c.Name).ToList();
            var rows = plan.Rows.Select(p => ToRow(spec, p)).ToList();
            var counts = new ImportCounts(plan.Count(RowOp.New), plan.Count(RowOp.Updated), plan.Count(RowOp.Unchanged), errors.Count);
            var stored = new StoredPlan(m, plan.Signature(), counts, columns, header.Skipped, plan.Decertifies, plan.Warnings, rows, bytes.Length);
            Directory.CreateDirectory(options.Directory);
            await File.WriteAllBytesAsync(FilePath(job.Id), bytes, ct);
            await File.WriteAllTextAsync(PlanPath(job.Id), JsonSerializer.Serialize(stored, J), ct);

            return ServiceResult<ImportPreview>.Ok(new ImportPreview(job.Id, spec.Kind, m, name, sha, bytes.Length, content.Rows.Count, columns, header.Skipped, counts, errors, plan.Warnings, plan.Decertifies,
                [.. rows.Take(PreviewRows)], rows.Count, job.Status, Iso(job.CreatedAt), Iso(job.CreatedAt + ImportLimits.PreviewLifetime), job.Version));
        }, ct);
    }

    private async Task RecordRejectedAsync(string kind, string name, string sha, string reason, Actor actor, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        db.Set<ImportJobs>().Add(new ImportJobs
        {
            Kind = kind, FileName = name, Sha256 = sha, RowCount = 0, ErrorCount = 1, ErrorsJson = JsonSerializer.Serialize(new[] { new ImportError(0, "(file)", reason) }, J),
            Status = "Rejected", UploadedByUserId = actor.UserId, CreatedAt = Now,
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Previews left uncommitted for 30 minutes expire; their files go with them.</summary>
    private async Task ExpireOldAsync(ReleaseDbContext db, DateTime now, CancellationToken ct)
    {
        var cutoff = now - ImportLimits.PreviewLifetime;
        var old = await db.Set<ImportJobs>().Where(j => j.Status == "Previewed" && j.CreatedAt < cutoff).ToListAsync(ct);
        foreach (var j in old) { j.Status = "Expired"; j.Version++; DeleteFiles(j.Id, keepPlan: false); }
    }

    private void DeleteFiles(string jobId, bool keepPlan)
    {
        try { File.Delete(FilePath(jobId)); if (!keepPlan) File.Delete(PlanPath(jobId)); }
        catch (IOException ex) { Console.Error.WriteLine($"import {jobId}: could not delete a stored file: {ex.Message}"); }   // stored files are scratch data; a stuck file is visible in the log, never silent
    }

    // ---- commit ----------------------------------------------------------------------------------------------------------------------
    public async Task<ServiceResult<ImportCommitResult>> CommitAsync(string jobId, bool acknowledgeDecertify, Actor actor, int? expectedVersion, CancellationToken ct = default)
    {
        var expired = false;
        var result = await RunAsync(async db =>
        {
            var job = await db.Set<ImportJobs>().SingleOrDefaultAsync(j => j.Id == jobId && j.UploadedByUserId == actor.UserId, ct);
            if (job is null) return ServiceResult<ImportCommitResult>.NotFound("import");
            var spec = ImportKinds.Get(job.Kind)!;
            if (job.Status == "Committed") return Fail<ImportCommitResult>(ExchangeGuards.ImportCommitted, "This import was already committed");
            if (job.Status == "Expired") return Fail<ImportCommitResult>(ExchangeGuards.ImportExpired, "This preview expired after 30 minutes; upload the file again");
            if (job.Status == "Rejected") return Fail<ImportCommitResult>(ExchangeGuards.ImportRejected, "This file was rejected; it cannot be committed");
            if (VersionMismatch(expectedVersion, job.Version)) return ServiceResult<ImportCommitResult>.Conflict(View(job, null));
            var now = Now;
            if (job.CreatedAt + ImportLimits.PreviewLifetime < now) { expired = true; return Fail<ImportCommitResult>(ExchangeGuards.ImportExpired, "This preview expired after 30 minutes; upload the file again"); }
            if (job.ErrorCount > 0) return Fail<ImportCommitResult>(ExchangeGuards.ImportHasErrors, $"The preview has {job.ErrorCount} error(s); fix the file and preview it again. Nothing was imported");

            if (!File.Exists(FilePath(job.Id)) || !File.Exists(PlanPath(job.Id))) return Fail<ImportCommitResult>(ExchangeGuards.ImportExpired, "The stored preview is gone; upload the file again");
            var bytes = await File.ReadAllBytesAsync(FilePath(job.Id), ct);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), job.Sha256, StringComparison.Ordinal))
                return Fail<ImportCommitResult>(ExchangeGuards.ImportTampered, "The stored file no longer matches its SHA-256; upload the file again");
            var stored = JsonSerializer.Deserialize<StoredPlan>(await File.ReadAllTextAsync(PlanPath(job.Id), ct), J)!;

            var (content, fatal) = CsvFile.Read(bytes);
            if (content is null) return Fail<ImportCommitResult>(ExchangeGuards.ImportRejected, fatal!);
            var header = RowParser.ResolveHeader(spec, content.Header);
            var parsed = content.Rows.Select(r => RowParser.Parse(spec, header, r.Row, r.Cells)).ToList();
            var env = new PlanEnv(db, spec, stored.Mode, header, parsed, now);
            env.Errors.AddRange(header.Errors);
            var handler = Handlers[spec.Kind];
            var plan = await handler.PlanAsync(env, new GridFilter(), ct);
            if (plan.Errors.Count > 0)
                return ServiceResult<ImportCommitResult>.Fail(new GuardFailure(ExchangeGuards.ImportHasErrors,
                    $"The data changed since the preview and the file now has {plan.Errors.Count} error(s). Nothing was imported; preview it again",
                    [.. SortErrors(plan.Errors).Take(10).Select(e => $"row {e.Row} {e.Column}: {e.Message}")]));
            if (plan.Signature() != stored.Signature)
                return ServiceResult<ImportCommitResult>.Conflict(new { message = "The data changed since the preview (a row it would change moved, or a train did). Preview the file again", previewed = stored.Counts,
                    now = new ImportCounts(plan.Count(RowOp.New), plan.Count(RowOp.Updated), plan.Count(RowOp.Unchanged), 0) });
            if (plan.Decertifies.Count > 0 && !acknowledgeDecertify)
                return ServiceResult<ImportCommitResult>.Fail(new GuardFailure(ExchangeGuards.DecertifyNotAcknowledged, $"Adding tasks decertifies: {string.Join(", ", plan.Decertifies)}. Confirm to continue", plan.Decertifies));

            await env.LoadHolidaysAsync(ct);
            var apply = new ApplyEnv(db, actor, now, (train, type, id, action, before, after) => Audit(db, actor, train, type, id, action, before, after), stored.Mode) { Holidays = env.Holidays };
            await handler.ApplyAsync(apply, plan.Rows, ct);
            if (apply.TrainsToBump.Count > 0)
                foreach (var t in await db.Set<ReleaseTrains>().Where(t => apply.TrainsToBump.Contains(t.Id)).ToListAsync(ct))
                { t.Version++; t.UpdatedAt = now; t.LastChangedByUserId = actor.UserId; t.LastChangedAt = now; }

            var (added, changed, same) = (plan.Count(RowOp.New), plan.Count(RowOp.Updated), plan.Count(RowOp.Unchanged));
            job.Status = "Committed"; job.CommittedAt = now; job.Version++;
            Audit(db, actor, job.ReleaseTrainId, "ImportJob", job.Id, "Commit", null,
                new { kind = job.Kind, mode = stored.Mode, file = job.FileName, sha256 = job.Sha256, rows = job.RowCount, inserted = added, updated = changed, unchanged = same, decertified = plan.Decertifies });
            await db.SaveChangesAsync(ct);
            return ServiceResult<ImportCommitResult>.Ok(new ImportCommitResult(job.Id, job.Kind, stored.Mode, added, changed, same, plan.Decertifies, job.Version));
        }, ct);

        if (expired) await MarkAsync(jobId, "Expired", ct);
        if (result.IsOk)
        {
            DeleteFiles(jobId, keepPlan: true);
            try { await ShrinkPlanAsync(jobId, ct); } catch (IOException ex) { Console.Error.WriteLine($"import {jobId}: could not shrink the stored plan: {ex.Message}"); }
        }
        return result;
    }

    private async Task MarkAsync(string jobId, string status, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        if (await db.Set<ImportJobs>().SingleOrDefaultAsync(j => j.Id == jobId && j.Status == "Previewed", ct) is { } j) { j.Status = status; j.Version++; await db.SaveChangesAsync(ct); }
        DeleteFiles(jobId, keepPlan: false);
    }

    private async Task ShrinkPlanAsync(string jobId, CancellationToken ct)
    {
        if (!File.Exists(PlanPath(jobId))) return;
        var stored = JsonSerializer.Deserialize<StoredPlan>(await File.ReadAllTextAsync(PlanPath(jobId), ct), J)!;
        await File.WriteAllTextAsync(PlanPath(jobId), JsonSerializer.Serialize(stored with { Rows = [] }, J), ct);
    }

    // ---- reads -----------------------------------------------------------------------------------------------------------------------
    public async Task<IReadOnlyList<ImportJobView>> ListAsync(int limit = 50, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var jobs = await db.Set<ImportJobs>().AsNoTracking().OrderByDescending(j => j.CreatedAt).ThenByDescending(j => j.Id).Take(Math.Clamp(limit, 1, 200)).ToListAsync(ct);
        var names = await UserNamesAsync(db, jobs, ct);
        return [.. jobs.Select(j => View(j, names.GetValueOrDefault(j.UploadedByUserId), stored: ReadPlan(j.Id)))];
    }

    public async Task<ImportJobView?> GetAsync(string jobId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var j = await db.Set<ImportJobs>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == jobId, ct);
        if (j is null) return null;
        var names = await UserNamesAsync(db, [j], ct);
        return View(j, names.GetValueOrDefault(j.UploadedByUserId), stored: ReadPlan(j.Id));
    }

    /// <summary>A page of the preview rows (Previewed jobs only: committed and expired jobs keep their counts and errors, not their rows).</summary>
    public async Task<ServiceResult<IReadOnlyList<PlanRow>>> RowsAsync(string jobId, int skip, int take, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var j = await db.Set<ImportJobs>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == jobId, ct);
        if (j is null) return ServiceResult<IReadOnlyList<PlanRow>>.NotFound("import");
        var stored = ReadPlan(jobId);
        if (j.Status != "Previewed" || stored is null || stored.Rows.Count == 0 && j.RowCount > 0) return Fail<IReadOnlyList<PlanRow>>(ExchangeGuards.ImportExpired, "The rows of this import are no longer kept");
        return ServiceResult<IReadOnlyList<PlanRow>>.Ok([.. stored.Rows.Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 1000))]);
    }

    private StoredPlan? ReadPlan(string jobId)
    {
        try { return File.Exists(PlanPath(jobId)) ? JsonSerializer.Deserialize<StoredPlan>(File.ReadAllText(PlanPath(jobId)), J) : null; }
        catch (Exception ex) when (ex is IOException or JsonException) { Console.Error.WriteLine($"import {jobId}: stored plan unreadable: {ex.Message}"); return null; }
    }

    private static async Task<Dictionary<string, string>> UserNamesAsync(ReleaseDbContext db, IEnumerable<ImportJobs> jobs, CancellationToken ct)
    {
        var ids = jobs.Select(j => j.UploadedByUserId).Distinct().ToList();
        return await db.Set<Users>().AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
    }

    private static ImportJobView View(ImportJobs j, string? uploader, StoredPlan? stored = null) =>
        new(j.Id, j.Kind, stored?.Mode, j.ReleaseTrainId, j.FileName, j.Sha256, j.RowCount, j.ErrorCount, ParseErrors(j.ErrorsJson), stored?.Counts, j.Status, j.UploadedByUserId, uploader,
            Iso(j.CreatedAt), j.CommittedAt is { } c ? Iso(c) : null, j.Version);

    private static IReadOnlyList<ImportError> ParseErrors(string json) => JsonSerializer.Deserialize<List<ImportError>>(json, J) ?? [];

    // ---- helpers ---------------------------------------------------------------------------------------------------------------------
    private static ServiceResult<T> Fail<T>(string guard, string message) => ServiceResult<T>.Fail(new GuardFailure(guard, message));

    private static List<ImportError> SortErrors(IEnumerable<ImportError> e) => [.. e.OrderBy(x => x.Row).ThenBy(x => x.Column, StringComparer.Ordinal)];

    /// <summary>ErrorsJson keeps the first 1,000; ErrorCount keeps the true total, and a last entry says how many were left out.</summary>
    private static List<ImportError> Capped(List<ImportError> errors) =>
        errors.Count <= MaxStoredErrors ? errors : [.. errors.Take(MaxStoredErrors), new(0, "(file)", $"{errors.Count - MaxStoredErrors:N0} more errors are not listed")];

    private static PlanRow ToRow(ImportKindSpec spec, Planned p) =>
        new(p.Row, p.Op.ToString().ToLowerInvariant(), string.Join(" + ", spec.Key.Select(k => p.Shown.GetValueOrDefault(k) ?? "")), p.Shown,
            p.Op == RowOp.Updated ? p.Changed.ToDictionary(c => c, c => p.Before![c]) : null, p.Changed);

    private static string CleanName(string? name)
    {
        var n = Path.GetFileName((name ?? "").Replace('\\', '/'));
        n = new string([.. n.Where(c => !char.IsControl(c))]).Trim();
        if (n.Length == 0) n = "import.csv";
        return n.Length > 200 ? n[..200] : n;
    }
}
