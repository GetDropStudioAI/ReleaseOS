using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Tests;

/// <summary>
/// Q-SEC-B8m: an existing database is brought forward by the manual scripts in db/upgrades/. tests/reference/test_upgrades.py proves the result equals
/// db/schema.sql; this proves the application then starts on it (the startup migration has nothing to do) and that a database not yet upgraded stops
/// with the script to run instead of EF's "table already exists".
/// </summary>
public sealed class UpgradeScriptTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("reos-upgrade-").FullName;
    public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "db", "schema.sql"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private static ReleaseDbContext Open(string path) => new(new DbContextOptionsBuilder<ReleaseDbContext>()
        .UseSqlite($"Data Source={path};Pooling=False").AddInterceptors(new SqliteConnectionInterceptor()).Options);

    private static void Exec(string path, string sql)
    {
        using var c = new SqliteConnection($"Data Source={path};Pooling=False"); c.Open();
        using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery();
    }

    private static string Scalar(string path, string sql)
    {
        using var c = new SqliteConnection($"Data Source={path};Pooling=False"); c.Open();
        using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToString(cmd.ExecuteScalar())!;
    }

    /// <summary>A database as the build before REOS-61/62 left it: baseline schema, its two migrations recorded, a user in it.</summary>
    private string BaselineDatabase()
    {
        var path = Path.Combine(_dir, "releasemgmt.db");
        Exec(path, File.ReadAllText(Path.Combine(RepoRoot(), "db", "upgrades", "baseline", "schema-20260929.sql")));
        Exec(path, """
            CREATE TABLE __EFMigrationsHistory (MigrationId TEXT NOT NULL CONSTRAINT PK___EFMigrationsHistory PRIMARY KEY, ProductVersion TEXT NOT NULL);
            INSERT INTO __EFMigrationsHistory VALUES ('20260929110130_Schema', '10.0.12'), ('20260929110136_Triggers', '10.0.12');
            INSERT INTO Users (Id, Email, DisplayName, Role) VALUES ('u1', 'gov@corp.example', 'Gov', 'GovernanceOfficer');
            """);
        return path;
    }

    [Fact]
    public async Task A_database_not_yet_upgraded_stops_start_up_and_names_the_scripts_instead_of_failing_inside_EF()
    {
        var path = BaselineDatabase();
        await using var db = Open(path);
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => UpgradeGuard.EnsureNoManualUpgradePendingAsync(db));
        Assert.Contains("20260929110130_Schema", e.Message);
        Assert.Contains("db/upgrades/", e.Message);
        Assert.Equal("gov@corp.example", Scalar(path, "SELECT Email FROM Users"));   // nothing touched
    }

    [Fact]
    public async Task After_the_upgrade_scripts_the_application_migrates_with_nothing_to_do_and_uses_the_new_columns()
    {
        var path = BaselineDatabase();
        foreach (var script in Directory.GetFiles(Path.Combine(RepoRoot(), "db", "upgrades"), "*.sql").Order(StringComparer.Ordinal))
            Exec(path, File.ReadAllText(script));

        await using (var db = Open(path))
        {
            await UpgradeGuard.EnsureNoManualUpgradePendingAsync(db);
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            await db.Database.MigrateAsync();
            var user = await db.Set<ReleaseMgmt.Domain.Entities.Users>().SingleAsync(u => u.Id == "u1");
            user.IdpIssuer = "https://idp.example.test/"; user.IdpSubject = "sub-gov"; user.Version++;
            await db.SaveChangesAsync();
        }
        Assert.Equal("https://idp.example.test/|sub-gov", Scalar(path, "SELECT IdpIssuer || '|' || IdpSubject FROM Users WHERE Id='u1'"));
        Assert.Equal("0", Scalar(path, "SELECT COUNT(*) FROM SessionRevocations"));

        var expected = Path.Combine(_dir, "fresh.db");
        await using (var fresh = Open(expected)) await fresh.Database.MigrateAsync();
        const string Names = "SELECT group_concat(type || ':' || name, ',') FROM (SELECT type, name FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name)";
        Assert.Equal(Scalar(expected, Names), Scalar(path, Names));
    }
}
