using Microsoft.Data.Sqlite;

namespace ReleaseMgmt.Infrastructure.Analytics;

/// <summary>Opens the connection analytics reads from. The connection must be read-only (Q-046c).</summary>
public interface IAnalyticsConnectionFactory
{
    Task<SqliteConnection> OpenAsync(CancellationToken ct = default);
}

/// <summary>
/// Read-only connection to a SQLite file: <c>Mode=ReadOnly</c> (the file cannot be written, and a database that is not in WAL, such as the
/// unmigrated fixtures/seed.db, is left byte-for-byte alone) plus <c>PRAGMA query_only=ON</c>, <c>foreign_keys=ON</c>, <c>busy_timeout=5000</c>.
/// <c>journal_mode=WAL</c> is not issued: it is a write to the file header, and the app connection that owns the file has already set it (persistent).
/// </summary>
public sealed class SqliteAnalyticsConnectionFactory(string connectionStringOrPath) : IAnalyticsConnectionFactory
{
    private readonly string _cs = Build(connectionStringOrPath);

    private static string Build(string s)
    {
        var b = new SqliteConnectionStringBuilder(s.Contains('=') ? s : $"Data Source={s}") { Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        return b.ToString();
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var c = new SqliteConnection(_cs);
        try
        {
            await c.OpenAsync(ct);
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA query_only=ON;";
            await cmd.ExecuteNonQueryAsync(ct);
            return c;
        }
        catch { await c.DisposeAsync(); throw; }
    }
}
