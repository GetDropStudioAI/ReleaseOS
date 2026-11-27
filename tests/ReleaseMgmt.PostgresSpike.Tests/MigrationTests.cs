using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit.Abstractions;

namespace ReleaseMgmt.PostgresSpike.Tests;

/// <summary>The SQLite build's migrations execute db/schema.sql verbatim (docs/QUESTIONS.md Q-001). This is the same pattern on Postgres: one hand-written EF migration whose Up runs the
/// generated Postgres contract (tables, CHECKs, indexes, then the triggers in file order). It lives in the spike assembly, so the shipped Migrations/ folder and its snapshot are untouched.</summary>
[DbContext(typeof(PgReleaseDbContext))]
[Migration("20261127000001_PgContractSchema")]
public sealed class PgContractSchema : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(PgFixture.GenerateSchema());   // one operation: Npgsql runs the multi-statement script, dollar-quoted function bodies included
    }

    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException("forward-only, like the SQLite migrations");
}

public sealed class MigrationTests(ITestOutputHelper o)   // builds its own empty databases: no template fixture
{
    private static async Task<string> EmptyDbAsync(string name)
    {
        await using var admin = new NpgsqlConnection(PgFixture.Dsn);
        await admin.OpenAsync();
        await PgFixture.ExecAsync(admin, $"CREATE DATABASE \"{name}\"");
        return PgFixture.For(name).ConnectionString;
    }

    [PgFact]
    public async Task A_verbatim_SQL_migration_applies_through_EF_Migrate_with_all_tables_triggers_and_checks()
    {
        var name = "reos_spike_mig_" + Guid.NewGuid().ToString("N")[..8];
        var cs = await EmptyDbAsync(name);
        try
        {
            await using var db = new PgReleaseDbContext(new DbContextOptionsBuilder<PgReleaseDbContext>().UseNpgsql(cs).Options);
            // No ModelSnapshot class exists for this context: does EF 10's Migrate() refuse (PendingModelChangesWarning is an error since EF 9)?
            Exception? refused = null;
            try { await db.Database.MigrateAsync(); } catch (Exception ex) { refused = ex; }
            o.WriteLine("Migrate() with no model snapshot: " + (refused is null ? "applied" : refused.GetType().Name + ": " + refused.Message[..Math.Min(160, refused.Message.Length)]));
            if (refused is not null)
            {
                await using var db2 = new PgReleaseDbContext(new DbContextOptionsBuilder<PgReleaseDbContext>().UseNpgsql(cs)
                    .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)).Options);
                await db2.Database.MigrateAsync();
            }
            await using var c = new NpgsqlConnection(cs); await c.OpenAsync();
            long Scalar(string sql) { using var cmd = new NpgsqlCommand(sql, c); return Convert.ToInt64(cmd.ExecuteScalar()); }
            Assert.Equal(85, Scalar("SELECT count(*) FROM information_schema.tables WHERE table_schema='public' AND table_name <> '__EFMigrationsHistory'"));
            Assert.Equal(188, Scalar("SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal"));
            Assert.Equal(1, Scalar("SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\"='20261127000001_PgContractSchema'"));
            // the contract's own CI test (SchemaContractTests) compares trigger names with db/schema.sql; on Postgres they carry the t{n}_ ordering prefix
            Assert.Equal(188, Scalar("SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal AND tgname ~ '^t[0-9]{3,4}_'"));
        }
        finally
        {
            await using var admin = new NpgsqlConnection(PgFixture.Dsn); await admin.OpenAsync();
            await PgFixture.ExecAsync(admin, $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");
        }
    }

    [PgFact]
    public async Task Npgsql_runs_the_generated_script_as_ONE_multi_statement_command_including_dollar_quoted_bodies()
    {
        var name = "reos_spike_one_" + Guid.NewGuid().ToString("N")[..8];
        var cs = await EmptyDbAsync(name);
        try
        {
            await using var c = new NpgsqlConnection(cs); await c.OpenAsync();
            string? failure = null;
            try { await PgFixture.ExecAsync(c, PgFixture.GenerateSchema()); } catch (Exception ex) { failure = ex.GetType().Name + ": " + ex.Message.ReplaceLineEndings(" ")[..Math.Min(200, ex.Message.Length)]; }
            o.WriteLine("whole script in one NpgsqlCommand: " + (failure ?? "ok"));
            await using var cmd = new NpgsqlCommand("SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal", c);
            o.WriteLine("triggers created: " + await cmd.ExecuteScalarAsync());
        }
        finally
        {
            await using var admin = new NpgsqlConnection(PgFixture.Dsn); await admin.OpenAsync();
            await PgFixture.ExecAsync(admin, $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");
        }
    }
}
