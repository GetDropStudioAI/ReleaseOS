using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Comms;

/// <summary>
/// REOS-73 / Q-053e: brings an existing database to the encrypted <c>WebhookDestinations</c> of db/schema.sql. Runs at every start, after the EF migrations
/// (it needs the Data Protection key ring, which a SQL migration cannot reach) and before anything reads the table. Idempotent:
/// <list type="bullet">
/// <item>Legacy shape (a plain <c>Url</c> column, the schema before 2026-09-30): every row's URL is protected and hashed, and the table is rebuilt with
/// db/schema.sql's DDL text verbatim (so the schema contract test holds for upgraded databases too), in one transaction, with one audit row per destination
/// (host only). Foreign keys from Teams and CommDispatches keep pointing at the same ids; <c>PRAGMA foreign_key_check</c> must be clean or nothing is kept.</item>
/// <item>New shape but the HMAC key was missing (restored without <c>secrets/</c>): each row's hash is recomputed from its decrypted URL. A row that cannot be
/// decrypted is left as it is and reported (the senders then fail visibly with a Webhook alert).</item>
/// <item>Otherwise nothing is done.</item>
/// </list>
/// </summary>
public static class WebhookUrlProtectionUpgrade
{
    public sealed record Result(int Protected, int Rehashed, IReadOnlyList<string> Unreadable);

    public static async Task<Result> RunAsync(string connectionString, IWebhookUrlVault vault, DateTime nowUtc, ILogger log, CancellationToken ct = default)
    {
        await using var c = new SqliteConnection(connectionString);
        await c.OpenAsync(ct);
        await ExecAsync(c, "PRAGMA busy_timeout=5000;", ct);
        var columns = new List<string>();
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM pragma_table_info('WebhookDestinations')";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) columns.Add(r.GetString(0));
        }
        if (columns.Count == 0) return new(0, 0, []);   // not migrated yet (should not happen: called after MigrateAsync)
        var now = nowUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        if (columns.Contains("Url")) return new(await ProtectLegacyAsync(c, vault, now, log, ct), 0, []);

        if (!vault.EnsureHmacKey()) return new(0, 0, []);
        var rows = new List<(string Id, string Name, string Protected, int Version)>();
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, Name, ProtectedUrl, Version FROM WebhookDestinations";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) rows.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3)));
        }
        if (rows.Count == 0) return new(0, 0, []);
        var unreadable = new List<string>();
        var rehashed = 0;
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
        foreach (var row in rows)
        {
            string url;
            try { url = vault.Unprotect(row.Protected); }
            catch (WebhookUrlUnreadableException) { unreadable.Add(row.Name); continue; }
            await ExecAsync(c, "UPDATE WebhookDestinations SET UrlHmac=$h, Version=Version+1 WHERE Id=$id", ct, tx, ("$h", vault.Hmac(url)), ("$id", row.Id));
            await AuditAsync(c, tx, now, row.Id, "RehashUrl", new { reason = "the webhook address key was missing and has been created again" }, ct);
            rehashed++;
        }
        await tx.CommitAsync(ct);
        log.LogWarning("The webhook address key was missing and was created again: {Rehashed} destination(s) re-keyed, {Unreadable} unreadable ({Names})",
            rehashed, unreadable.Count, string.Join(", ", unreadable));
        return new(0, rehashed, unreadable);
    }

    private static async Task<int> ProtectLegacyAsync(SqliteConnection c, IWebhookUrlVault vault, string now, ILogger log, CancellationToken ct)
    {
        var ddl = SchemaSql.TablesAndIndexes().Single(s => s.StartsWith("CREATE TABLE WebhookDestinations ", StringComparison.Ordinal));
        var rows = new List<(string Id, string Name, string Url, string Kind, int Version)>();
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, Name, Url, Kind, Version FROM WebhookDestinations";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) rows.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4)));
        }
        // A table rebuild (SQLite "12-step" procedure): foreign keys off and the legacy ALTER so that renaming the old table does not rewrite the
        // REFERENCES WebhookDestinations(Id) clauses of Teams and CommDispatches to the temporary name. Both pragmas are no-ops inside a transaction, so they come first.
        // secure_delete: the pages of the dropped plaintext table are overwritten with zeros, and the checkpoint below carries that into the database file.
        await ExecAsync(c, "PRAGMA foreign_keys=OFF; PRAGMA legacy_alter_table=ON; PRAGMA secure_delete=ON;", ct);
        try
        {
            await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
            await ExecAsync(c, "ALTER TABLE WebhookDestinations RENAME TO WebhookDestinations_plaintext", ct, tx);
            await ExecAsync(c, ddl, ct, tx);
            foreach (var row in rows)
            {
                if (!Uri.TryCreate(row.Url, UriKind.Absolute, out var uri))
                    throw new InvalidOperationException($"Webhook destination '{row.Name}' ({row.Id}) does not hold an absolute URL; fix or remove it before upgrading");
                var host = HostOf(uri);
                var p = vault.Protect(row.Url);
                await ExecAsync(c, "INSERT INTO WebhookDestinations(Id,Name,Host,ProtectedUrl,UrlHmac,Kind,Version) VALUES($id,$n,$h,$p,$m,$k,$v)", ct, tx,
                    ("$id", row.Id), ("$n", row.Name), ("$h", host), ("$p", p.ProtectedUrl), ("$m", p.UrlHmac), ("$k", row.Kind), ("$v", row.Version + 1));
                await AuditAsync(c, tx, now, row.Id, "ProtectUrl", new { row.Name, row.Kind, Host = host }, ct);
            }
            await ExecAsync(c, "DROP TABLE WebhookDestinations_plaintext", ct, tx);
            await using (var check = c.CreateCommand())
            {
                check.Transaction = tx;
                check.CommandText = "PRAGMA foreign_key_check";
                await using var r = await check.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct)) throw new InvalidOperationException($"Protecting webhook addresses would break a foreign key ({r.GetString(0)} -> {r.GetString(2)}); nothing was changed");
            }
            await tx.CommitAsync(ct);
            await ExecAsync(c, "PRAGMA wal_checkpoint(TRUNCATE);", ct);
        }
        finally
        {
            await ExecAsync(c, "PRAGMA legacy_alter_table=OFF; PRAGMA foreign_keys=ON; PRAGMA secure_delete=OFF;", ct);
        }
        log.LogInformation("Webhook addresses are now stored encrypted: {Count} destination(s) protected (Q-053e)", rows.Count);
        return rows.Count;
    }

    /// <summary>host[:port] as the allowlist stores it (lower-case IDN form, IPv6 in brackets, default port dropped).</summary>
    public static string HostOf(Uri uri)
    {
        var host = uri.IdnHost.ToLowerInvariant();
        if (uri.HostNameType == UriHostNameType.IPv6 && !host.StartsWith('[')) host = $"[{host}]";
        return uri.IsDefaultPort ? host : $"{host}:{uri.Port}";
    }

    private static Task AuditAsync(SqliteConnection c, SqliteTransaction tx, string now, string id, string action, object after, CancellationToken ct) =>
        ExecAsync(c, "INSERT INTO AuditEvents(OccurredAt,ActorUserId,EntityType,EntityId,Action,AfterJson) VALUES($t,NULL,'WebhookDestination',$id,$a,$j)", ct, tx,
            ("$t", now), ("$id", id), ("$a", action), ("$j", JsonSerializer.Serialize(after, new JsonSerializerOptions(JsonSerializerDefaults.Web))));

    private static async Task ExecAsync(SqliteConnection c, string sql, CancellationToken ct, SqliteTransaction? tx = null, params (string Name, object Value)[] args)
    {
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
