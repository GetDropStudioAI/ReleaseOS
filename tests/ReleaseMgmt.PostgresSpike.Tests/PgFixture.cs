using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.PostgresSpike.Tests;

/// <summary>[Fact] that skips itself when no Postgres server is configured (REOS_PG_DSN, the variable the REOS-195 oracle workflow already sets).</summary>
public sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute() { if (PgFixture.Dsn is null) Skip = "REOS_PG_DSN is not set: this spike needs a Postgres 16 server (see docs/proposals/postgres-ef-spike-findings.md)"; }
}

/// <summary>The spike's Npgsql context. Everything the SQLite build gets for free from the provider is spelled out here, so the list of conventions IS the finding:
/// <list type="bullet">
/// <item><c>bool</c>: schema.sql (and the generated Postgres schema) keep booleans as 0/1 integers; Npgsql would send <c>boolean</c>.</item>
/// </list>
/// <c>int</c> needs nothing: the generated schema types every INTEGER as <c>bigint</c>, and Npgsql reads int8 into the entities' <c>int</c> properties and writes int4 parameters into them (probed, see ProviderTests).
/// Timestamps and dates need nothing new: <see cref="AppDbContext"/>'s UtcTextConverter / DateTextConverter produce text, and the column is text.</summary>
public sealed class PgReleaseDbContext(DbContextOptions options) : ReleaseDbContext(options)
{
    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        base.ConfigureConventions(b);
        b.Properties<bool>().HaveConversion<BoolToZeroOneConverter<long>>();
    }
}

/// <summary>The same context with NO Postgres conventions: what you get by swapping UseSqlite for UseNpgsql and changing nothing else.</summary>
public sealed class PgNaiveContext(DbContextOptions options) : ReleaseDbContext(options);

public sealed class PgFixture : IAsyncLifetime
{
    /// <summary>Npgsql connection string for the admin database, from REOS_PG_DSN (a postgresql:// URL, as the Python oracle takes it, or an Npgsql key=value string).</summary>
    public static string? Dsn { get; } = Parse(Environment.GetEnvironmentVariable("REOS_PG_DSN"));

    private static string? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (!s.StartsWith("postgres", StringComparison.OrdinalIgnoreCase)) return s;
        var u = new Uri(s);
        var b = new NpgsqlConnectionStringBuilder { Host = u.Host, Port = u.Port > 0 ? u.Port : 5432, Database = u.AbsolutePath.Trim('/'), Pooling = false };
        if (!string.IsNullOrEmpty(u.UserInfo)) { var p = u.UserInfo.Split(':', 2); b.Username = Uri.UnescapeDataString(p[0]); if (p.Length > 1) b.Password = Uri.UnescapeDataString(p[1]); }
        return b.ConnectionString;
    }

    public const string Now = "2026-10-20T14:00:00Z";
    private readonly string _template = "reos_spike_tpl_" + Guid.NewGuid().ToString("N")[..8];
    private readonly List<string> _created = [];
    private int _n;

    public static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "ReleaseMgmt.sln"))) return d.FullName;
        throw new InvalidOperationException("repository root not found");
    }

    /// <summary>The Postgres schema as REOS-195's generator derives it from db/schema.sql (never committed; db/schema.sql is untouched).</summary>
    public static string GenerateSchema()
    {
        var psi = new ProcessStartInfo("python3", $"\"{Path.Combine(RepoRoot(), "tools", "gen_pg_schema.py")}\"") { RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var sql = p.StandardOutput.ReadToEnd();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException("gen_pg_schema.py failed: " + err);
        return sql;
    }

    public static NpgsqlConnectionStringBuilder For(string db) => new(Dsn!) { Database = db, Pooling = false };

    public async Task InitializeAsync()
    {
        if (Dsn is null) return;
        await using var admin = new NpgsqlConnection(Dsn);
        await admin.OpenAsync();
        await ExecAsync(admin, $"CREATE DATABASE \"{_template}\"");
        _created.Add(_template);
        await using var c = new NpgsqlConnection(For(_template).ConnectionString);
        await c.OpenAsync();
        await ExecAsync(c, GenerateSchema());   // Npgsql runs the whole multi-statement script, $f$ / $fn$ bodies included (MigrationTests proves it)
        await ExecAsync(c, Seed);
    }

    public async Task DisposeAsync()
    {
        if (Dsn is null) return;
        await using var admin = new NpgsqlConnection(Dsn);
        await admin.OpenAsync();
        foreach (var db in _created) await ExecAsync(admin, $"DROP DATABASE IF EXISTS \"{db}\" WITH (FORCE)");
    }

    public static async Task ExecAsync(NpgsqlConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>The trigger suite's seed (rte/rm/gov1/gov2/dev, train t1, gates g1 Code Freeze + g2 Compliance), written with quoted identifiers.</summary>
    public const string Seed = $@"
        INSERT INTO ""Users""(""Id"",""Email"",""DisplayName"",""Role"") VALUES
          ('rte','rte@x.com','Rae T.','RTE'),('rm','rm@x.com','Rel Mgr','ReleaseManager'),('gov1','g1@x.com','Gov One','GovernanceOfficer'),
          ('gov2','g2@x.com','Gov Two','GovernanceOfficer'),('dev','dev@x.com','Dev','Viewer');
        INSERT INTO ""ReleaseTrains""(""Id"",""Title"",""TargetReleaseDate"",""RiskTier"",""CreatedAt"",""UpdatedAt"") VALUES('t1','R26.10','2026-10-30','High','{Now}','{Now}');
        INSERT INTO ""BundledProducts""(""Id"",""ReleaseTrainId"",""ProductName"",""VersionTag"",""ProjectCode"") VALUES('p1','t1','Payments API','4.5.0','PAY'),('p2','t1','Card Portal','2.1.0','CRD');
        INSERT INTO ""StageGates""(""Id"",""ReleaseTrainId"",""GateName"",""GateClass"",""SequenceOrder"",""OffsetDays"",""DueOn"",""RequiredBeforeStatus"",""OwnerUserId"",""Status"",""LastChangedByUserId"") VALUES
          ('g1','t1','Code Freeze','Standard',1,5,'2026-10-23','Gated','rte','Pending','rte'),
          ('g2','t1','Compliance Sign-off','Compliance',2,2,'2026-10-28','Executing','gov1','InProgress','gov1');
        INSERT INTO ""ChecklistTasks""(""Id"",""StageGateId"",""TaskDescription"",""OwnerUserId"",""SequenceOrder"") VALUES('k1','g1','Tag repos','rte',1),('k2','g2','SOX evidence uploaded','gov1',1);
        INSERT INTO ""RunbookSteps""(""Id"",""ReleaseTrainId"",""BundledProductId"",""StepCode"",""Section"",""Title"",""OwnerUserId"",""PlannedStartAt"",""PlannedDurationMin"") VALUES
          ('s1','t1','p1','R-001','PreCheck','Health check','rte','2026-10-30T02:00:00Z',10),('s2','t1','p1','R-002','Deploy','Deploy Payments API','rte','2026-10-30T02:10:00Z',30);
        INSERT INTO ""StepDependencies""(""StepId"",""DependsOnStepId"") VALUES('s2','s1');
        INSERT INTO ""WorkspaceMembers""(""WorkspaceId"",""UserId"",""Role"",""AddedAt"") SELECT 'default', ""Id"", ""Role"", '{Now}' FROM ""Users"";";

    /// <summary>A fresh database cloned from the template (CREATE DATABASE … TEMPLATE: milliseconds, and every test is isolated).</summary>
    public async Task<PgEnv> NewEnvAsync(string? connectionOptions = null)
    {
        var name = $"{_template}_{Interlocked.Increment(ref _n)}";
        await using (var admin = new NpgsqlConnection(Dsn))
        {
            await admin.OpenAsync();
            await ExecAsync(admin, $"CREATE DATABASE \"{name}\" TEMPLATE \"{_template}\"");
        }
        lock (_created) _created.Add(name);
        return new PgEnv(For(name).ConnectionString + (connectionOptions is null ? "" : ";" + connectionOptions));
    }
}

public sealed class PgFactory<TContext>(string cs) : IDbContextFactory<ReleaseDbContext> where TContext : ReleaseDbContext
{
    public ReleaseDbContext CreateDbContext() =>
        (ReleaseDbContext)Activator.CreateInstance(typeof(TContext), new DbContextOptionsBuilder<TContext>().UseNpgsql(cs).Options)!;
}

public sealed class PgEnv(string cs)
{
    public string ConnectionString { get; } = cs;
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 20, 14, 0, 0, TimeSpan.Zero));
    public IDbContextFactory<ReleaseDbContext> Db { get; } = new PgFactory<PgReleaseDbContext>(cs);
    public TrainLifecycleService Trains => new(Db, Time);
    public GateService Gates => new(Db, Time);
    public WaiverService Waivers => new(Db, Time);
    public TaskService Tasks => new(Db, Time);

    public NpgsqlConnection Open() { var c = new NpgsqlConnection(ConnectionString); c.Open(); return c; }

    public void Sql(string sql, params object?[] p) { using var c = Open(); using var cmd = Cmd(c, sql, p); cmd.ExecuteNonQuery(); }

    public List<object?[]> Query(string sql, params object?[] p)
    {
        using var c = Open();
        using var cmd = Cmd(c, sql, p);
        using var r = cmd.ExecuteReader();
        var rows = new List<object?[]>();
        while (r.Read()) { var row = new object?[r.FieldCount]; r.GetValues(row!); rows.Add(row.Select(v => v is DBNull ? null : v).ToArray()); }
        return rows;
    }

    public long Count(string sql, params object?[] p) => Convert.ToInt64(Query(sql, p)[0][0]);
    public object? Scalar(string sql, params object?[] p) => Query(sql, p).Single()[0];

    private static NpgsqlCommand Cmd(NpgsqlConnection c, string sql, object?[] p)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var v in p) cmd.Parameters.Add(new NpgsqlParameter { Value = v ?? DBNull.Value });
        return cmd;
    }
}
