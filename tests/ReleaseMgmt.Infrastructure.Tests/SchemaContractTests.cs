using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Tests;

/// <summary>db/schema.sql is the contract (CLAUDE.md rule 1): the migrated database must equal it.</summary>
public sealed class SchemaContractTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("reos-schema-").FullName;
    private readonly string _schemaText = File.ReadAllText(Path.Combine(RepoRoot(), "db", "schema.sql"));

    public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "db", "schema.sql"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private string Migrated()
    {
        var path = Path.Combine(_dir, "migrated.db");
        using var db = new ReleaseDbContext(new DbContextOptionsBuilder<ReleaseDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").AddInterceptors(new SqliteConnectionInterceptor()).Options);
        db.Database.Migrate();
        return path;
    }

    private string FromSchemaSql()
    {
        var path = Path.Combine(_dir, "reference.db");
        using var c = new SqliteConnection($"Data Source={path};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = _schemaText;
        cmd.ExecuteNonQuery();
        return path;
    }

    private static List<(string Type, string Name, string Sql)> Master(string db)
    {
        using var c = new SqliteConnection($"Data Source={db};Mode=ReadOnly;Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT type, name, COALESCE(sql,'') FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' AND name NOT LIKE '__EFMigrations%' ORDER BY type, name";
        using var r = cmd.ExecuteReader();
        var rows = new List<(string, string, string)>();
        while (r.Read()) rows.Add((r.GetString(0), r.GetString(1), r.GetString(2)));
        return rows;
    }

    [Fact]
    public void Migrated_database_has_exactly_the_trigger_names_in_schema_sql()
    {
        var expected = Regex.Matches(_schemaText, @"^CREATE TRIGGER (\w+)", RegexOptions.Multiline).Select(m => m.Groups[1].Value).Order().ToList();
        var actual = Master(Migrated()).Where(r => r.Type == "trigger").Select(r => r.Name).Order().ToList();
        Assert.Equal(152, expected.Count);   // 42 + 49 workspace-boundary triggers (REOS-104) + 16 release-model, deployment and boundary triggers (REOS-107/109/110) + 23 for telemetry, control, approvals and the audit chain (REOS-111 to 114) + 3 for release types (REOS-116) + 4 for clients (REOS-118) + 1 for versions (REOS-117) + 4 for client dates (REOS-119) + 10 for template governance (REOS-122)
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Migrated_database_matches_schema_sql_tables_indexes_and_triggers_verbatim()
    {
        var migrated = Master(Migrated());
        var reference = Master(FromSchemaSql());
        Assert.Equal(72, reference.Count(r => r.Type == "table"));   // 49 + Workspaces, WorkspaceMembers (REOS-104) + 7 (REOS-107/109/110) + 9 (REOS-111 to 114) + Clients (REOS-118) + ReleaseVersions (REOS-117) + ReleaseClientDates (REOS-119) + TemplateTasks, TemplateMilestones (REOS-122)
        Assert.Equal(41, reference.Count(r => r.Type == "index"));   // 23 + IX_WorkspaceMembers_User, IX_Trains_Workspace, IX_Audit_Workspace + 5 (UX_Trains_Key, IX_Deployments_*, IX_DeploymentEvents_Deployment) + 9 (REOS-111 to 114) + UX_Trains_VersionSlot (REOS-117)
        Assert.Equal(reference.Select(r => (r.Type, r.Name)), migrated.Select(r => (r.Type, r.Name)));
        foreach (var (a, b) in reference.Zip(migrated)) Assert.Equal(a.Sql, b.Sql); // DDL text identical, incl. CHECKs
    }

    [Fact]
    public void Migrated_database_starts_with_the_default_workspace_and_raw_rows_land_in_it()
    {
        using var c = new SqliteConnection($"Data Source={Migrated()};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Id || '|' || Slug FROM Workspaces";
        Assert.Equal("default|default", cmd.ExecuteScalar());
        cmd.CommandText = "INSERT INTO Holidays(Day, Name) VALUES ('2026-12-25', 'Christmas'); SELECT WorkspaceId FROM Holidays";
        Assert.Equal(ReleaseMgmt.Domain.Common.WorkspaceIds.Default, cmd.ExecuteScalar());
        Assert.Equal(ReleaseMgmt.Domain.Common.WorkspaceIds.Default, new ReleaseMgmt.Domain.Entities.ReleaseTrains().WorkspaceId);   // EF writes it too
    }

    [Fact]
    public void Trigger_creation_order_matches_file_order()
    {
        var expected = Regex.Matches(_schemaText, @"^CREATE TRIGGER (\w+)", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        using var c = new SqliteConnection($"Data Source={Migrated()};Mode=ReadOnly;Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='trigger' ORDER BY rowid";
        using var r = cmd.ExecuteReader();
        var actual = new List<string>();
        while (r.Read()) actual.Add(r.GetString(0));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void EF_model_maps_every_table_and_column_and_nothing_else()
    {
        var path = Migrated();
        using var db = new ReleaseDbContext(new DbContextOptionsBuilder<ReleaseDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);
        var model = db.Model.GetEntityTypes().ToDictionary(e => e.GetTableName()!,
            e => e.GetProperties().Select(p => (Name: p.GetColumnName(), p.IsNullable)).OrderBy(p => p.Name, StringComparer.Ordinal).ToList());   // SQLite orders ordinally

        using var c = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        c.Open();
        var tables = new List<string>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name NOT LIKE '__EFMigrations%'";
            using var r = cmd.ExecuteReader(); while (r.Read()) tables.Add(r.GetString(0));
        }
        Assert.Equal(tables.Order(), model.Keys.Order());
        foreach (var t in tables)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT name, \"notnull\", pk FROM pragma_table_info('{t}') ORDER BY name";
            using var r = cmd.ExecuteReader();
            var cols = new List<(string, bool)>();
            while (r.Read()) cols.Add((r.GetString(0), r.GetInt32(1) == 0 && r.GetInt32(2) == 0));
            Assert.True(cols.SequenceEqual(model[t].Select(p => (p.Name!, p.IsNullable))), $"Model/table mismatch for {t}");
        }
    }

    [Fact]
    public void Every_entity_uses_the_no_RETURNING_save_path()
    {
        using var db = new ReleaseDbContext(new DbContextOptionsBuilder<ReleaseDbContext>().UseSqlite("Data Source=:memory:").Options);
        foreach (var e in db.Model.GetEntityTypes())
        {
            var table = Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table(e.GetTableName()!, e.GetSchema());
            Assert.False(e.IsSqlReturningClauseUsed(table), $"{e.Name} still uses RETURNING");
        }
    }
}
