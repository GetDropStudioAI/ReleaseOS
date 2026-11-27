using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Services;
using Xunit.Abstractions;

namespace ReleaseMgmt.PostgresSpike.Tests;

/// <summary>CLAUDE.md rule 1 on Postgres: the RETURNING clause versus AFTER triggers, and a trigger RAISE surfacing as a guard failure (422), not a 500.</summary>
public sealed class ReturningAndRuleTests(PgFixture fx, ITestOutputHelper o) : IClassFixture<PgFixture>
{
    private sealed class Capture : DbCommandInterceptor
    {
        public List<string> Sql { get; } = [];
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand c, CommandEventData d, InterceptionResult<DbDataReader> r) { Sql.Add(c.CommandText); return r; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c, CommandEventData d, InterceptionResult<DbDataReader> r, CancellationToken ct = default) { Sql.Add(c.CommandText); return new(r); }
    }

    /// <summary>Same model, but the RETURNING clause is switched back ON for every table (EF's default on Npgsql).</summary>
    private sealed class PgReturningContext(DbContextOptions options) : ReleaseDbContext(options)
    {
        protected override void ConfigureConventions(ModelConfigurationBuilder b)
        {
            base.ConfigureConventions(b);
            b.Properties<bool>().HaveConversion<Microsoft.EntityFrameworkCore.Storage.ValueConversion.BoolToZeroOneConverter<long>>();
        }

        protected override void OnModelCreating(ModelBuilder mb)
        {
            base.OnModelCreating(mb);
            foreach (var et in mb.Model.GetEntityTypes().Where(x => x.GetTableName() is not null))
                mb.Entity(et.ClrType).ToTable(et.GetTableName()!, t => t.UseSqlReturningClause(true));
        }
    }

    private static async Task<(string Update, string Insert)> CertifyWithAfterTriggers<TContext>(PgEnv e, Capture cap) where TContext : ReleaseDbContext
    {
        var f = new CapturingFactory<TContext>(e.ConnectionString, cap);
        // g1 InProgress -> Certified fires trg_Gate_CertifyLocksEvidence, trg_Gate_CertifyCapturesBaseline and trg_Gate_LogTransition (AFTER UPDATE): the rule-1 case
        Assert.True((await e.Tasks.CompleteAsync("k1", new Actor("rte"))).IsOk);
        Assert.True((await e.Gates.StartAsync("g1", new Actor("rte"))).IsOk);
        await using var db = f.CreateDbContext();
        var g = await db.Set<StageGates>().SingleAsync(x => x.Id == "g1");
        g.Status = "Certified"; g.CertifiedByUserId = "rte"; g.CertifiedAt = new DateTime(2026, 10, 20, 14, 5, 0, DateTimeKind.Utc); g.LastChangedByUserId = "rte"; g.LastChangedAt = new DateTime(2026, 10, 20, 14, 5, 0, DateTimeKind.Utc); g.Version++;
        cap.Sql.Clear();
        await db.SaveChangesAsync();
        var upd = cap.Sql.Single(s => s.Contains("UPDATE \"StageGates\""));
        // an insert into a table with an identity key (GateTransitions.Id bigint) exercises the generated-value read-back
        cap.Sql.Clear();
        db.Set<GateTransitions>().Add(new GateTransitions { StageGateId = "g1", FromStatus = "Certified", ToStatus = "InProgress", ActorUserId = "rte", OccurredAt = new DateTime(2026, 10, 20, 15, 0, 0, DateTimeKind.Utc) });
        string ins;
        try { await db.SaveChangesAsync(); ins = cap.Sql.Single(s => s.Contains("INSERT INTO \"GateTransitions\"")); }
        catch (Exception ex) { ins = "FAILED: " + (ex.InnerException ?? ex).Message; }
        return (upd, ins);
    }

    private sealed class CapturingFactory<TContext>(string cs, Capture cap) where TContext : ReleaseDbContext
    {
        public ReleaseDbContext CreateDbContext() =>
            (ReleaseDbContext)Activator.CreateInstance(typeof(TContext), new DbContextOptionsBuilder<TContext>().UseNpgsql(cs).AddInterceptors(cap).Options)!;
    }

    [PgFact]
    public async Task The_shipped_model_setting_UseSqlReturningClause_false_is_accepted_by_Npgsql_and_AFTER_triggers_save_fine()
    {
        var e = await fx.NewEnvAsync();
        var (upd, ins) = await CertifyWithAfterTriggers<PgReleaseDbContext>(e, new Capture());
        o.WriteLine("UseSqlReturningClause(false):\n" + upd + "\n" + ins);
        Assert.Equal("Certified", e.Scalar("SELECT \"Status\" FROM \"StageGates\" WHERE \"Id\"='g1'"));
        Assert.Equal(1L, e.Count("SELECT count(*) FROM \"Baselines\" WHERE \"ReleaseTrainId\"='t1'"));           // the AFTER trigger cascades ran
        Assert.Equal(1L, e.Count("SELECT count(*) FROM \"AuditEvents\" WHERE \"Action\"='EvidenceLocked'"));
        // Npgsql does not turn RETURNING off for a generated key: the identity value (GateTransitions.Id) is read back with RETURNING whatever the setting says
        Assert.Contains("RETURNING \"Id\"", ins);
        Assert.DoesNotContain("RETURNING", upd);   // an UPDATE with no store-generated column has none either way
    }

    [PgFact]
    public async Task Postgres_has_no_RETURNING_restriction_on_tables_with_AFTER_triggers_so_rule_1_is_SQLite_only()
    {
        var e = await fx.NewEnvAsync();
        var (upd, ins) = await CertifyWithAfterTriggers<PgReturningContext>(e, new Capture());
        o.WriteLine("UseSqlReturningClause(true):\n" + upd + "\n" + ins);
        Assert.Equal("Certified", e.Scalar("SELECT \"Status\" FROM \"StageGates\" WHERE \"Id\"='g1'"));
        Assert.Equal(1L, e.Count("SELECT count(*) FROM \"Baselines\" WHERE \"ReleaseTrainId\"='t1'"));
        Assert.DoesNotContain("FAILED", ins);
        Assert.Contains("RETURNING \"Id\"", ins);
    }

    // ---- trigger RAISE -> 422 DbRule ---------------------------------------------------------------------------------------------------

    private sealed class Probe(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider t) : ServiceBase(dbf, t)
    {
        public Task<ServiceResult<int>> IllegalGateUpdate() => RunAsync(async db =>
        {
            var g = await db.Set<StageGates>().SingleAsync(x => x.Id == "g1");
            g.Status = "Failed"; g.LastChangedByUserId = "rte";   // Pending -> Failed: the trigger is the backstop
            await db.SaveChangesAsync();
            return ServiceResult<int>.Ok(1);
        }, default);
    }

    [PgFact]
    public async Task A_trigger_RAISE_is_a_PostgresException_that_the_shipped_DbRules_mapper_does_not_recognise()
    {
        var e = await fx.NewEnvAsync();
        // ServiceBase.RunAsync catches DbUpdateException only when DbRules.TryGetMessage (SqliteException code 19) says so: on Npgsql the exception escapes, i.e. a 500 instead of a 422
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => new Probe(e.Db, e.Time).IllegalGateUpdate());
        var pg = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal("RE001", pg.SqlState);                                    // the generator's one custom SQLSTATE for every trigger RAISE
        Assert.Equal("Illegal gate status transition", pg.MessageText);
        Assert.False(DbRules.TryGetMessage(ex, out _));
        Assert.Equal("Pending", e.Scalar("SELECT \"Status\" FROM \"StageGates\" WHERE \"Id\"='g1'"));
    }

    [PgFact]
    public async Task A_Postgres_aware_mapper_gives_the_same_422_text_for_triggers_and_a_readable_text_for_constraints()
    {
        var e = await fx.NewEnvAsync();
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => new Probe(e.Db, e.Time).IllegalGateUpdate());
        Assert.True(PgDbRules.TryGetMessage(ex, out var message));
        Assert.Equal("Illegal gate status transition", message);               // identical to the SQLite service test's expectation

        // a UNIQUE failure (the version-slot index the SQLite mapper special-cases by NAME) carries the constraint name on Postgres too
        await using var db = e.Db.CreateDbContext();
        db.Set<ChecklistTasks>().Add(new ChecklistTasks { Id = "k1", StageGateId = "g1", TaskDescription = "dup", OwnerUserId = "rte", SequenceOrder = 77 });
        var dup = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.True(PgDbRules.TryGetMessage(dup, out var dupMessage));
        o.WriteLine(dupMessage);
        Assert.StartsWith("UNIQUE constraint failed: ChecklistTasks", dupMessage);
        Assert.False(PgDbRules.TryGetMessage(new DbUpdateException("x", new InvalidOperationException()), out _));
    }
}

/// <summary>The spike's candidate for the Postgres sibling of <see cref="DbRules"/>: SQLSTATE RE001 is a rule trigger (message = the rule text); class 23 is a constraint, worded like SQLite's
/// "UNIQUE constraint failed: Table.Column" so callers (and the one text special-case, UX_Trains_VersionSlot) keep working. Lives in the spike project only.</summary>
public static class PgDbRules
{
    public static bool TryGetMessage(Exception ex, out string message)
    {
        for (var e = ex; e is not null; e = e.InnerException!)
        {
            if (e is PostgresException p)
            {
                message = p.SqlState switch
                {
                    "RE001" => p.MessageText,
                    "23505" => $"UNIQUE constraint failed: {p.TableName}.{p.ConstraintName}",
                    "23503" => $"FOREIGN KEY constraint failed: {p.TableName}.{p.ConstraintName}",
                    "23502" => $"NOT NULL constraint failed: {p.TableName}.{p.ColumnName}",
                    "23514" => $"CHECK constraint failed: {p.TableName}.{p.ConstraintName}",
                    _ => "",
                };
                if (message.Length > 0) return true;
            }
            if (e.InnerException is null) break;
        }
        message = "";
        return false;
    }
}
