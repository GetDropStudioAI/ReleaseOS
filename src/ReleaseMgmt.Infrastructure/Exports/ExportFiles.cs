using System.Security.Cryptography;

namespace ReleaseMgmt.Infrastructure.Exports;

public static class ExportFiles
{
    /// <summary>SHA-256 of the file's bytes exactly as they are on disk, lower-case hex.</summary>
    public static async Task<string> Sha256HexAsync(string path, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, ct));
    }

    /// <summary>Best-effort removal of a partial or superseded file. A failure to delete is returned, not thrown, so the caller can surface it (rule 8).</summary>
    public static string? TryDelete(string? path)
    {
        try { if (path is not null && File.Exists(path)) File.Delete(path); return null; }
        catch (Exception ex) { return $"Could not remove {path}: {ex.Message}"; }
    }
}
