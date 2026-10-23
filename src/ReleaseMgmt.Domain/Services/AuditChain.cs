using System.Security.Cryptography;
using System.Text;

namespace ReleaseMgmt.Domain.Services;

/// <summary>
/// The hash chain of the audit log (REOS-114). Each workspace's rows (and, apart, the host-wide rows whose WorkspaceId is NULL) form a chain in Id order:
/// <c>Hash = SHA-256( PrevHash + "\n" + Canonical(row) )</c>, the first row's PrevHash being 64 zeros. The canonical form is a version line, then the ten columns
/// of the row, each as <c>{utf8 byte length}:{value}</c> or <c>-</c> for NULL, joined by line feeds. It is fixed so that anything can recompute it:
/// tools/verify_audit_chain.py does, from the database file alone. Changing it would break every sealed row, so it carries a version.
/// </summary>
public static class AuditChain
{
    public const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";
    public const string FormatVersion = "ReleaseOS.Audit.v1";
    /// <summary>The chain id of the rows that belong to no workspace (accounts, backups).</summary>
    public const string HostChain = "*";

    public static string ChainOf(string? workspaceId) => workspaceId ?? HostChain;

    private static string Field(string? v) => v is null ? "-" : $"{Encoding.UTF8.GetByteCount(v)}:{v}";

    /// <param name="occurredAt">The stored text of the column ('yyyy-MM-ddTHH:mm:ssZ'), not a re-formatted time.</param>
    public static string Canonical(long id, string occurredAt, string? actorUserId, string? releaseTrainId, string entityType, string entityId, string action,
        string? beforeJson, string? afterJson, string? workspaceId) =>
        string.Join('\n', FormatVersion, Field(id.ToString(System.Globalization.CultureInfo.InvariantCulture)), Field(occurredAt), Field(actorUserId), Field(releaseTrainId),
            Field(entityType), Field(entityId), Field(action), Field(beforeJson), Field(afterJson), Field(workspaceId));

    public static string Hash(string prevHash, string canonical) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(prevHash + "\n" + canonical)));
}

/// <summary>Wakes the audit sealer after a commit that wrote audit rows, without the writer waiting for it (sealing adds no latency to a write).</summary>
public static class AuditSealSignal
{
    private static readonly SemaphoreSlim Gate = new(0, 1);

    /// <summary>Called after a commit. Never blocks and never throws; a signal already pending absorbs this one.</summary>
    public static void Notify()
    {
        try { Gate.Release(); } catch (SemaphoreFullException) { /* one is already pending */ }
    }

    /// <summary>True when signalled, false when the interval passed (the sealer then sweeps anyway: rows written by triggers or outside the services have no signal).</summary>
    public static Task<bool> WaitAsync(TimeSpan interval, CancellationToken ct) => Gate.WaitAsync(interval, ct);
}
