using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Tests.Persistence;

public class Widget
{
    public string Id { get; set; } = Ids.New();
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
}

public class TestDb(DbContextOptions<TestDb> options) : AppDbContext(options)
{
    public DbSet<Widget> Widgets => Set<Widget>();
}

public sealed class DbFixture : IDisposable
{
    public string Dir { get; } = Directory.CreateTempSubdirectory("reos-test-").FullName;
    public string DbFile => Path.Combine(Dir, "test.db");
    public string ConnectionString => $"Data Source={DbFile};Pooling=False";

    public TestDb Create()
    {
        var o = new DbContextOptionsBuilder<TestDb>().UseSqlite(ConnectionString)
            .AddInterceptors(new SqliteConnectionInterceptor()).Options;
        var db = new TestDb(o);
        db.Database.OpenConnection();
        db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS Widgets (Id TEXT PRIMARY KEY, Name TEXT NOT NULL, CreatedAt TEXT NOT NULL, ClosedAt TEXT)");
        return db;
    }

    public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(Dir, true); } catch (IOException) { } }
}

public class PersistenceConventionTests : IDisposable
{
    private readonly DbFixture _fx = new();
    public void Dispose() => _fx.Dispose();

    [Fact]
    public void Pragmas_are_set_on_every_connection()
    {
        using var db = _fx.Create();
        Assert.Equal("wal", db.Database.SqlQueryRaw<string>("SELECT journal_mode AS Value FROM pragma_journal_mode").Single());
        Assert.Equal("1", db.Database.SqlQueryRaw<string>("SELECT CAST(foreign_keys AS TEXT) AS Value FROM pragma_foreign_keys").Single());
        Assert.Equal("5000", db.Database.SqlQueryRaw<string>("SELECT CAST(timeout AS TEXT) AS Value FROM pragma_busy_timeout").Single());
    }

    [Fact]
    public void Timestamps_round_trip_as_utc_iso8601_text_in_whole_seconds()
    {
        var t = new DateTime(2026, 9, 29, 9, 29, 5, DateTimeKind.Utc);
        using (var db = _fx.Create()) { db.Widgets.Add(new Widget { Name = "a", CreatedAt = t, ClosedAt = null }); db.SaveChanges(); }
        using var db2 = _fx.Create();
        Assert.Equal("2026-09-29T09:29:05Z", db2.Database.SqlQueryRaw<string>("SELECT CreatedAt AS Value FROM Widgets").Single());
        var w = db2.Widgets.Single();
        Assert.Equal(t, w.CreatedAt);
        Assert.Equal(DateTimeKind.Utc, w.CreatedAt.Kind);
        Assert.Null(w.ClosedAt);
    }

    [Fact]
    public void Ids_are_uuid_v7()
    {
        var id = Guid.Parse(Ids.New());
        Assert.Equal(7, id.Version);
    }

    [Fact]
    public void Saves_do_not_use_returning_so_tables_with_after_triggers_work()
    {
        using var db = _fx.Create();
        db.Database.ExecuteSqlRaw("CREATE TABLE WidgetLog (Note TEXT)");
        db.Database.ExecuteSqlRaw("CREATE TRIGGER trg_Widget_Ai AFTER INSERT ON Widgets BEGIN INSERT INTO WidgetLog VALUES (NEW.Id); END");
        db.Widgets.Add(new Widget { Name = "x", CreatedAt = DateTime.UtcNow });
        db.SaveChanges(); // would throw "cannot use RETURNING with triggers" if the RETURNING path were used
        Assert.Equal(1, db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM WidgetLog").Single());
    }
}
