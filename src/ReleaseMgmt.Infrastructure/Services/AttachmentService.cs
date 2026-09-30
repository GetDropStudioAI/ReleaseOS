using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

/// <summary>Where evidence files live (outside wwwroot) and the size cap. Config keys Attachments:Directory and Attachments:MaxBytes (Q-035a).</summary>
public sealed record AttachmentOptions(string Directory, long MaxBytes)
{
    public const long HardMaxBytes = 52_428_800;   // 50 MB: the CHECK in schema.sql
    public const string DefaultDirectory = "data/attachments";
}

/// <summary>Guard names for evidence (kept here so the shared Guards file is untouched).</summary>
public static class AttachmentGuards
{
    public const string TooLarge = "AttachmentTooLarge";
    public const string Empty = "AttachmentEmpty";
    public const string Locked = "AttachmentLocked";
    public const string InvalidEntity = "AttachmentInvalidEntity";
    public const string Role = "AttachmentRole";
}

public sealed record AttachmentEntry(string Id, string ReleaseTrainId, string EntityType, string EntityId, string FileName, string ContentType, long SizeBytes, string Sha256, bool IsLocked, string UploadedByUserId, string? UploadedByName, DateTime UploadedAt);
public sealed record AttachmentFile(Attachments Row, string FullPath);

/// <summary>
/// Evidence attachments (PROJECT_SCOPE section 1/6, M4). The SHA-256 is computed here while the upload streams to disk; a client hash is never read.
/// Files are stored id-named outside wwwroot; the DB row and its audit row commit together and a failed write leaves neither an orphan file nor a row.
/// A gate's (and its tasks') attachments lock when the gate is Certified or Waived (trigger; the service checks first for a readable 422).
/// </summary>
public sealed partial class AttachmentService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, AttachmentOptions options, IAlertSink alerts, IRealtimePublisher? realtime = null) : ServiceBase(dbf, time, realtime)
{
    public static readonly string[] SupportedEntities = ["Train", "Gate", "Task"];
    public long MaxBytes => Math.Min(options.MaxBytes, AttachmentOptions.HardMaxBytes);
    private string Root => Path.GetFullPath(options.Directory);
    private string TempDir => Path.Combine(Root, ".tmp");

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9!#$&^_.+-]{0,126}/[A-Za-z0-9][A-Za-z0-9!#$&^_.+-]{0,126}$")]
    private static partial Regex MediaType();

    /// <summary>Last path segment only, control and reserved characters removed, no leading dots or trailing dots/spaces, at most 180 chars, never empty.</summary>
    public static string SanitizeFileName(string? name)
    {
        var s = (name ?? "").Replace('\\', '/');
        s = s[(s.LastIndexOf('/') + 1)..];
        var clean = new string(s.Where(c => !char.IsControl(c) && c is not ('<' or '>' or ':' or '"' or '|' or '?' or '*' or '‮' or '⁦' or '⁧' or '⁨' or '⁩' or '‎' or '‏')).ToArray()).Trim().TrimStart('.').TrimEnd('.', ' ');
        if (clean.Length > 180)
        {
            var ext = Path.GetExtension(clean);
            ext = ext.Length is > 0 and <= 20 ? ext : "";
            clean = clean[..(180 - ext.Length)] + ext;
        }
        return clean.Length == 0 ? "attachment" : clean;
    }

    public static string SafeContentType(string? ct)
    {
        var t = (ct ?? "").Split(';')[0].Trim().ToLowerInvariant();
        return MediaType().IsMatch(t) ? t : "application/octet-stream";
    }

    // ---- reads ----------------------------------------------------------------------------------------------------------------------------

    /// <summary>Name, size, SHA-256, locked, uploader, time for a train's evidence (optionally one entity's). REOS-40's evidence pack reads this.</summary>
    public async Task<List<AttachmentEntry>> ManifestAsync(string trainId, string? entityType = null, string? entityId = null, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var q = db.Set<Attachments>().Where(a => a.ReleaseTrainId == trainId);
        if (entityType is not null) q = q.Where(a => a.EntityType == entityType);
        if (entityId is not null) q = q.Where(a => a.EntityId == entityId);
        return await (from a in q
                      join u in db.Set<Users>() on a.UploadedByUserId equals u.Id into us
                      from u in us.DefaultIfEmpty()
                      orderby a.UploadedAt, a.Id
                      select new AttachmentEntry(a.Id, a.ReleaseTrainId, a.EntityType, a.EntityId, a.FileName, a.ContentType, a.SizeBytes, a.Sha256, a.IsLocked, a.UploadedByUserId, u.DisplayName, a.UploadedAt)).ToListAsync(ct);
    }

    /// <summary>Resolves the stored file for a download. A row whose file is gone is raised as an alert (rule 8) and reported as missing.</summary>
    public async Task<ServiceResult<AttachmentFile>> FindFileAsync(string id, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var a = await db.Set<Attachments>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (a is null) return ServiceResult<AttachmentFile>.NotFound("attachment");
        var path = ResolveStored(a.StoragePath);
        if (path is null || !File.Exists(path))
        {
            await alerts.RaiseAsync("Attachments", "FileMissing", a.Id, $"The stored file for attachment {a.Id} ({a.FileName}) is missing from {options.Directory}", ct);
            return ServiceResult<AttachmentFile>.NotFound("attachment file");
        }
        return ServiceResult<AttachmentFile>.Ok(new(a, path));
    }

    // ---- upload ---------------------------------------------------------------------------------------------------------------------------

    /// <summary>Cheap pre-check the endpoint runs before it reads a byte of the file (so a refused upload costs nothing).</summary>
    public async Task<ServiceResult<string>> CheckTargetAsync(string entityType, string entityId, Actor actor, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var f = await TargetGuardsAsync(db, entityType, entityId, actor, ct);
        return f.Failures.Count > 0 ? ServiceResult<string>.Fail(f.Failures) : ServiceResult<string>.Ok(f.TrainId!);
    }

    public async Task<ServiceResult<AttachmentEntry>> UploadAsync(string entityType, string entityId, string? fileName, string? contentType, Stream content, Actor actor, CancellationToken ct = default)
    {
        var early = await CheckTargetAsync(entityType, entityId, actor, ct);
        if (!early.IsOk) return ServiceResult<AttachmentEntry>.Fail(early.Failures);

        Directory.CreateDirectory(TempDir);
        var temp = Path.Combine(TempDir, Guid.NewGuid().ToString("N"));
        string? final = null;
        var committed = false;
        try
        {
            // Stream to a temp file and hash as it goes; abort the moment the cap is crossed.
            long size = 0;
            string sha;
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            await using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                var buf = new byte[81920];
                int n;
                while ((n = await content.ReadAsync(buf, ct)) > 0)
                {
                    size += n;
                    if (size > MaxBytes) return ServiceResult<AttachmentEntry>.Fail(new GuardFailure(AttachmentGuards.TooLarge, $"The file is larger than the {MaxBytes / 1048576} MB limit for evidence attachments"));
                    hash.AppendData(buf, 0, n);
                    await fs.WriteAsync(buf.AsMemory(0, n), ct);
                }
                sha = Convert.ToHexStringLower(hash.GetHashAndReset());
            }
            if (size == 0) return ServiceResult<AttachmentEntry>.Fail(new GuardFailure(AttachmentGuards.Empty, "The file is empty; nothing to attach"));

            var id = Ids.New();
            var relative = $"{id[^2..]}/{id}";   // shard by the (random) tail of the UUIDv7; the id alone is the file name
            final = ResolveStored(relative) ?? throw new InvalidOperationException("Storage path escaped the attachments directory");
            var row = new Attachments { Id = id, EntityType = entityType, EntityId = entityId, FileName = SanitizeFileName(fileName), ContentType = SafeContentType(contentType), SizeBytes = size, Sha256 = sha, StoragePath = relative, UploadedByUserId = actor.UserId };

            var result = await RunAsync(async db =>
            {
                var t = await TargetGuardsAsync(db, entityType, entityId, actor, ct);   // again inside the transaction: a certify may have landed while the bytes streamed
                if (t.Failures.Count > 0) return ServiceResult<AttachmentEntry>.Fail(t.Failures);
                row.ReleaseTrainId = t.TrainId!; row.UploadedAt = Now;
                db.Set<Attachments>().Add(row);
                Audit(db, actor, row.ReleaseTrainId, "Attachment", row.Id, "Upload", null, new { row.FileName, row.SizeBytes, row.Sha256, row.EntityType, row.EntityId });
                await db.SaveChangesAsync(ct);
                Directory.CreateDirectory(Path.GetDirectoryName(final)!);
                File.Move(temp, final);   // last step before commit: if the commit fails the file is removed below
                var name = await db.Set<Users>().Where(u => u.Id == actor.UserId).Select(u => u.DisplayName).SingleOrDefaultAsync(ct);
                return ServiceResult<AttachmentEntry>.Ok(ToEntry(row, name));
            }, ct);
            committed = result.IsOk;
            return result;
        }
        finally
        {
            if (!committed)
            {
                await RemoveFileAsync(temp, "UploadCleanup");
                if (final is not null) await RemoveFileAsync(final, "UploadCleanup");
            }
        }
    }

    // ---- delete ---------------------------------------------------------------------------------------------------------------------------

    public async Task<ServiceResult<AttachmentEntry>> DeleteAsync(string id, Actor actor, CancellationToken ct = default)
    {
        string? toRemove = null;
        var result = await RunAsync(async db =>
        {
            var a = await db.Set<Attachments>().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (a is null) return ServiceResult<AttachmentEntry>.NotFound("attachment");
            var role = await RoleOf(db, actor.UserId);
            var f = new List<GuardFailure>();
            if (a.UploadedByUserId != actor.UserId && role is not (Roles.RTE or Roles.ReleaseManager))
                f.Add(new(AttachmentGuards.Role, "Only the uploader, an RTE or a Release Manager can delete evidence"));
            if (a.IsLocked) f.Add(new(AttachmentGuards.Locked, "This evidence is locked because its gate was certified; it can no longer be deleted"));
            if (f.Count > 0) return ServiceResult<AttachmentEntry>.Fail(f);
            var entry = ToEntry(a, null);
            db.Set<Attachments>().Remove(a);
            Audit(db, actor, a.ReleaseTrainId, "Attachment", a.Id, "Delete", new { a.FileName, a.SizeBytes, a.Sha256, a.EntityType, a.EntityId }, null);
            await db.SaveChangesAsync(ct);
            toRemove = ResolveStored(a.StoragePath);
            return ServiceResult<AttachmentEntry>.Ok(entry);
        }, ct);
        if (result.IsOk && toRemove is not null) await RemoveFileAsync(toRemove, "DeleteCleanup");   // row is gone; a stuck file is an alert, never silent
        return result;
    }

    // ---- helpers --------------------------------------------------------------------------------------------------------------------------

    private static AttachmentEntry ToEntry(Attachments a, string? uploader) =>
        new(a.Id, a.ReleaseTrainId, a.EntityType, a.EntityId, a.FileName, a.ContentType, a.SizeBytes, a.Sha256, a.IsLocked, a.UploadedByUserId, uploader, a.UploadedAt);

    /// <summary>Stored paths are always relative and must stay inside the attachments directory.</summary>
    private string? ResolveStored(string relative)
    {
        if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative)) return null;
        var full = Path.GetFullPath(Path.Combine(Root, relative));
        return full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : null;
    }

    private async Task RemoveFileAsync(string path, string kind)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex)
        {
            await alerts.RaiseAsync("Attachments", kind, path, $"Could not remove attachment file {path}: {ex.Message}", CancellationToken.None);
        }
    }

    private async Task<(IReadOnlyList<GuardFailure> Failures, string? TrainId)> TargetGuardsAsync(ReleaseDbContext db, string entityType, string entityId, Actor actor, CancellationToken ct)
    {
        var role = await RoleOf(db, actor.UserId);
        if (role is null or Roles.Viewer) return ([new(AttachmentGuards.Role, "Your role cannot attach evidence")], null);
        switch (entityType)
        {
            case "Train":
            {
                var t = await db.Set<ReleaseTrains>().Where(x => x.Id == entityId).Select(x => x.Id).SingleOrDefaultAsync(ct);
                if (t is null) return ([new(AttachmentGuards.InvalidEntity, "That train does not exist")], null);
                return ([], t);
            }
            case "Gate":
            case "Task":
            {
                string? trainId, gateStatus, gateName;
                if (entityType == "Gate")
                {
                    var g = await db.Set<StageGates>().Where(x => x.Id == entityId).Select(x => new { x.ReleaseTrainId, x.Status, x.GateName }).SingleOrDefaultAsync(ct);
                    (trainId, gateStatus, gateName) = (g?.ReleaseTrainId, g?.Status, g?.GateName);
                }
                else
                {
                    var g = await (from tk in db.Set<ChecklistTasks>() join gt in db.Set<StageGates>() on tk.StageGateId equals gt.Id where tk.Id == entityId
                                   select new { gt.ReleaseTrainId, gt.Status, gt.GateName }).SingleOrDefaultAsync(ct);
                    (trainId, gateStatus, gateName) = (g?.ReleaseTrainId, g?.Status, g?.GateName);
                }
                if (trainId is null) return ([new(AttachmentGuards.InvalidEntity, $"That {entityType.ToLowerInvariant()} does not exist")], null);
                if (gateStatus is "Certified" or "Waived")
                    return ([new(AttachmentGuards.Locked, $"Gate \"{gateName}\" is {gateStatus}; its evidence is locked and nothing can be added. Reopen the gate first")], null);
                return ([], trainId);
            }
            default:
                return ([new(AttachmentGuards.InvalidEntity, $"Evidence can be attached to a {string.Join(", ", SupportedEntities.Select(e => e.ToLowerInvariant()))}")], null);
        }
    }
}
