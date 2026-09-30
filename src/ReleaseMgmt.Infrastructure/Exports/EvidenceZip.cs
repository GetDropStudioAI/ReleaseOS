using System.IO.Compression;
using System.Security.Cryptography;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Infrastructure.Exports;

/// <summary>
/// The evidence pack ZIP (PROJECT_SCOPE 9): the PDF, every attachment file and manifest.csv. Each attachment is read from its stored location and hashed
/// while it is copied into the archive; a missing file, a size that differs or a SHA-256 that differs from the value stored at upload throws
/// <see cref="ExportIntegrityException"/> and the caller discards the half-written archive: a corrupt pack is never offered (rule 8).
/// Entry times are the export's clock, so the archive is reproducible.
/// </summary>
public static class EvidenceZip
{
    public const string ManifestName = "manifest.csv";
    public static readonly string[] ManifestHeader = ["name", "size", "sha256", "gate_or_task", "uploader", "uploaded_at", "locked", "zip_path"];

    public static string EntryPath(AttachmentRow a) => $"files/{a.No:D3}-{AttachmentService.SanitizeFileName(a.FileName)}";

    /// <summary>manifest.csv: RFC 4180 with OWASP CSV-injection escaping (a cell starting = + - @, tab or CR gets a leading apostrophe). CRLF line ends, UTF-8 without BOM.</summary>
    public static string ManifestCsv(IEnumerable<AttachmentRow> rows)
    {
        var lines = new List<string> { ExportSupport.CsvRow(ManifestHeader) };
        foreach (var a in rows)
            lines.Add(ExportSupport.CsvRow([a.FileName, a.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), a.Sha256, $"{a.EntityType}: {a.EntityLabel}", a.UploaderName,
                DateTime.SpecifyKind(a.UploadedAt, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture), a.Locked ? "true" : "false", EntryPath(a)]));
        return string.Join("\r\n", lines) + "\r\n";
    }

    public static async Task WriteAsync(string zipPath, string pdfEntryName, byte[] pdf, IReadOnlyList<AttachmentRow> attachments, AttachmentService store, DateTime stamp, CancellationToken ct)
    {
        var when = new DateTimeOffset(DateTime.SpecifyKind(stamp, DateTimeKind.Utc));
        await using var fs = new FileStream(zipPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false);

        var pdfEntry = zip.CreateEntry(pdfEntryName, CompressionLevel.Optimal); pdfEntry.LastWriteTime = when;
        await using (var s = pdfEntry.Open()) await s.WriteAsync(pdf, ct);

        var manifest = zip.CreateEntry(ManifestName, CompressionLevel.Optimal); manifest.LastWriteTime = when;
        await using (var s = manifest.Open()) await s.WriteAsync(new System.Text.UTF8Encoding(false).GetBytes(ManifestCsv(attachments)), ct);

        foreach (var a in attachments)
        {
            var found = await store.FindFileAsync(a.Id, ct);   // raises its own FileMissing alert when the stored file is gone
            if (!found.IsOk) throw new ExportIntegrityException($"Evidence file {a.No} '{a.FileName}' is missing from storage; the evidence pack was not produced. Restore the file or investigate before exporting again.");
            var entry = zip.CreateEntry(EntryPath(a), CompressionLevel.Optimal); entry.LastWriteTime = when;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long size = 0;
            await using (var src = new FileStream(found.Value!.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var dst = entry.Open())
            {
                var buf = new byte[81920];
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0) { hash.AppendData(buf, 0, n); size += n; await dst.WriteAsync(buf.AsMemory(0, n), ct); }
            }
            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (actual != a.Sha256 || size != a.SizeBytes)
                throw new ExportIntegrityException($"Evidence file {a.No} '{a.FileName}' no longer matches the record made at upload (stored SHA-256 {a.Sha256}, on disk {actual}; stored size {a.SizeBytes}, on disk {size}). The evidence pack was not produced: the file has been changed or damaged since it was attached.");
        }
    }
}
