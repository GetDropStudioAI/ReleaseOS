using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ReleaseMgmt.Infrastructure.Persistence;

/// <summary>Sets WAL, foreign_keys and busy_timeout on every connection open (D2).</summary>
public sealed class SqliteConnectionInterceptor : DbConnectionInterceptor
{
    public const string Pragmas = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = Pragmas;
        cmd.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = Pragmas;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
