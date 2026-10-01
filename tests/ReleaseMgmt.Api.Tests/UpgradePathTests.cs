using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// Q-SEC-B8m: the whole upgrade path of an existing database, as an operator runs it (docs/RUNBOOK_OPERATIONS.md section 8). A database as the first
/// release left it (baseline schema, its two migrations, a team with a plain-text webhook URL) gets every db/upgrades script, then the real app starts on
/// it. At start the app converts what SQL cannot (002: the webhook URL is encrypted with the key ring), and the result must equal a fresh database:
/// the same tables, CHECKs, indexes and triggers, with the rows kept.
/// </summary>
public class UpgradePathTests
{
    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "db", "schema.sql"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private static void Exec(string path, string sql)
    {
        using var c = new SqliteConnection($"Data Source={path};Pooling=False"); c.Open();
        using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery();
    }

    private static List<string> Rows(string path, string sql)
    {
        using var c = new SqliteConnection($"Data Source={path};Pooling=False"); c.Open();
        using var cmd = c.CreateCommand(); cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var rows = new List<string>();
        while (r.Read()) rows.Add(string.Join("|", Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "NULL" : Convert.ToString(r.GetValue(i)))));
        return rows;
    }

    /// <summary>sqlite_master with comments and spacing removed (the stored DDL keeps the text it was created with) and the quotes ALTER TABLE ... RENAME adds.</summary>
    private static List<string> Shape(string path) =>
        [.. Rows(path, "SELECT type || ' ' || name || ' ' || tbl_name || ' ' || COALESCE(sql,'') FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' AND name <> '__EFMigrationsHistory' ORDER BY type, name")
            .Select(s => Regex.Replace(Regex.Replace(Regex.Replace(s, "--[^\n]*", ""), @"\s+", " "), "CREATE TABLE \"([A-Za-z_]+)\"", "CREATE TABLE $1").Trim())];

    [Fact]
    public async Task A_first_release_database_upgraded_by_the_scripts_and_started_once_equals_a_fresh_database()
    {
        using var f = new ApiFactory();
        Exec(f.DbPath, File.ReadAllText(Path.Combine(RepoRoot(), "db", "upgrades", "baseline", "schema-20260929.sql")));
        Exec(f.DbPath, """
            CREATE TABLE __EFMigrationsHistory (MigrationId TEXT NOT NULL CONSTRAINT PK___EFMigrationsHistory PRIMARY KEY, ProductVersion TEXT NOT NULL);
            INSERT INTO __EFMigrationsHistory VALUES ('20260929110130_Schema', '10.0.12'), ('20260929110136_Triggers', '10.0.12');
            INSERT INTO WebhookDestinations (Id, Name, Url, Kind) VALUES ('wd1', 'Payments channel', 'https://hooks.example.test/services/T000/B000/secret-part', 'Teams');
            INSERT INTO Teams (Id, Handle, Name, WebhookDestinationId) VALUES ('team1', 'payments', 'Payments', 'wd1');
            """);
        foreach (var script in Directory.GetFiles(Path.Combine(RepoRoot(), "db", "upgrades"), "*.sql").Order(StringComparer.Ordinal))
            Exec(f.DbPath, File.ReadAllText(script));

        (await f.CreateClient().GetAsync("/healthz")).EnsureSuccessStatusCode();   // starts the app: migrations (nothing to do), then the start-up conversion

        var row = Assert.Single(Rows(f.DbPath, "SELECT Id, Name, Host, substr(ProtectedUrl, 1, 5), length(UrlHmac), Kind FROM WebhookDestinations"));
        Assert.Equal("wd1|Payments channel|hooks.example.test|CfDJ8|64|Teams", row);
        Assert.DoesNotContain(Rows(f.DbPath, "SELECT * FROM WebhookDestinations"), r => r.Contains("secret-part"));
        Assert.Equal(["team1|wd1"], Rows(f.DbPath, "SELECT Id, WebhookDestinationId FROM Teams"));
        Assert.Empty(Rows(f.DbPath, "PRAGMA foreign_key_check"));

        using var fresh = new ApiFactory();
        (await fresh.CreateClient().GetAsync("/healthz")).EnsureSuccessStatusCode();
        Assert.Equal(Shape(fresh.DbPath), Shape(f.DbPath));
    }
}
