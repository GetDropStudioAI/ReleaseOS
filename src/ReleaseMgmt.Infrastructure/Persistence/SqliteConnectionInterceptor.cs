using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ReleaseMgmt.Infrastructure.Persistence;

/// <summary>
/// Sets WAL, foreign_keys and busy_timeout on every connection open (D2). EF Core's SQLite <c>Database.Exists()</c> (called first by <c>Migrate()</c>) opens a
/// <b>read-only</b> connection, and a read-only connection cannot switch a database to WAL. A database restored from a backup is a single self-contained
/// file in the default journal mode (BackupRunner writes it that way), so the switch is needed once, and on a read-only connection it failed with "attempt to
/// write a readonly database": the API could not start on a restored database (found by the REOS-52 DR drill). Read-only connections therefore skip the journal
/// mode; the first read-write connection makes the switch.
/// </summary>
public sealed class SqliteConnectionInterceptor : DbConnectionInterceptor
{
    public const string Pragmas = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
    public const string ReadOnlyPragmas = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";

    private static string PragmasFor(DbConnection connection) =>
        new SqliteConnectionStringBuilder(connection.ConnectionString).Mode == SqliteOpenMode.ReadOnly ? ReadOnlyPragmas : Pragmas;

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = PragmasFor(connection);
        cmd.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = PragmasFor(connection);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
