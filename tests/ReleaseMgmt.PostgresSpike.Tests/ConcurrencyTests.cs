using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;
using Xunit.Abstractions;

namespace ReleaseMgmt.PostgresSpike.Tests;

/// <summary>Optimistic concurrency (CLAUDE.md rule 3) and the audit chain, under Postgres's default READ COMMITTED isolation. SQLite serialises writers, which hides both hazards.</summary>
public sealed class ConcurrencyTests(PgFixture fx, ITestOutputHelper o) : IClassFixture<PgFixture>
{
    /// <summary>The same model with <c>Version</c> declared a concurrency token on every table that has one: UPDATE … WHERE Id=@id AND Version=@original.</summary>
    private sealed class PgTokenContext(DbContextOptions options) : ReleaseDbContext(options)
    {
        protected override void ConfigureConventions(ModelConfigurationBuilder b)
        {
            base.ConfigureConventions(b);
            b.Properties<bool>().HaveConversion<Microsoft.EntityFrameworkCore.Storage.ValueConversion.BoolToZeroOneConverter<long>>();
        }

        protected override void OnModelCreating(ModelBuilder mb)
        {
            base.OnModelCreating(mb);
            foreach (var et in mb.Model.GetEntityTypes().Where(x => x.FindProperty("Version") is { ClrType: var t } && t == typeof(int)))
                mb.Entity(et.ClrType).Property("Version").IsConcurrencyToken();
        }
    }

    /// <summary>What every service does: read the row, compare the caller's If-Match version with the version it read, change, Version++, save, commit.</summary>
    private static async Task<bool> ServiceStyleWriteAsync(ReleaseDbContext db, int expectedVersion, string title, Func<Task>? afterRead = null)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        var t = await db.Set<ReleaseTrains>().SingleAsync(x => x.Id == "t1");
        if (afterRead is not null) await afterRead();
        if (t.Version != expectedVersion) return false;   // the 409 path
        t.Title = title; t.Version++; t.LastChangedAt = new DateTime(2026, 10, 20, 14, 0, 0, DateTimeKind.Utc);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return true;
    }

    [PgFact]
    public async Task Service_style_read_check_write_loses_an_update_on_Postgres_READ_COMMITTED()
    {
        var e = await fx.NewEnvAsync();
        await using var a = e.Db.CreateDbContext();
        await using var b = e.Db.CreateDbContext();
        var bReadDone = new TaskCompletionSource();
        var aDone = new TaskCompletionSource();
        // B reads (Version 1) and waits; A reads, writes and commits; then B passes its stale check and writes
        var tb = ServiceStyleWriteAsync(b, 1, "B", async () => { bReadDone.SetResult(); await aDone.Task; });
        await bReadDone.Task;
        Assert.True(await ServiceStyleWriteAsync(a, 1, "A"));
        aDone.SetResult();
        var bOk = await tb;
        o.WriteLine($"both callers sent If-Match: 1. A ok, B ok={bOk}; final Version={e.Scalar("SELECT \"Version\" FROM \"ReleaseTrains\" WHERE \"Id\"='t1'")}, Title={e.Scalar("SELECT \"Title\" FROM \"ReleaseTrains\" WHERE \"Id\"='t1'")}");
        Assert.True(bOk);                                                                 // B should have got a 409: it did not
        Assert.Equal(2, e.Scalar("SELECT \"Version\" FROM \"ReleaseTrains\" WHERE \"Id\"='t1'") is long v ? (int)v : -1);   // two writes, Version moved once: A's update is gone
        Assert.Equal("B", e.Scalar("SELECT \"Title\" FROM \"ReleaseTrains\" WHERE \"Id\"='t1'"));
    }

    [PgFact]
    public async Task Fix_1_Version_as_an_EF_concurrency_token_turns_the_lost_update_into_DbUpdateConcurrencyException()
    {
        var e = await fx.NewEnvAsync();
        var f = new PgFactory<PgTokenContext>(e.ConnectionString);
        await using var a = f.CreateDbContext();
        await using var b = f.CreateDbContext();
        var bReadDone = new TaskCompletionSource(); var aDone = new TaskCompletionSource();
        var tb = ServiceStyleWriteAsync(b, 1, "B", async () => { bReadDone.SetResult(); await aDone.Task; });
        await bReadDone.Task;
        Assert.True(await ServiceStyleWriteAsync(a, 1, "A"));
        aDone.SetResult();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => tb);                // UPDATE … WHERE "Version" = 1 touched 0 rows
        Assert.Equal("A", e.Scalar("SELECT \"Title\" FROM \"ReleaseTrains\" WHERE \"Id\"='t1'"));
    }

    [PgFact]
    public async Task Fix_2_SELECT_FOR_UPDATE_makes_the_second_writer_wait_and_then_see_the_new_Version()
    {
        var e = await fx.NewEnvAsync();
        await using var a = e.Db.CreateDbContext();
        await using var b = e.Db.CreateDbContext();
        await using var txA = await a.Database.BeginTransactionAsync();
        var ta = await a.Set<ReleaseTrains>().FromSqlRaw("SELECT * FROM \"ReleaseTrains\" WHERE \"Id\" = {0} FOR UPDATE", "t1").SingleAsync();

        var bTask = Task.Run(async () =>
        {
            await using var txB = await b.Database.BeginTransactionAsync();
            var tb = await b.Set<ReleaseTrains>().FromSqlRaw("SELECT * FROM \"ReleaseTrains\" WHERE \"Id\" = {0} FOR UPDATE", "t1").SingleAsync();   // blocks here
            return tb.Version;
        });
        await Task.Delay(500);
        Assert.False(bTask.IsCompleted, "B must block on A's row lock");
        ta.Title = "A"; ta.Version++; await a.SaveChangesAsync(); await txA.CommitAsync();
        Assert.Equal(2, await bTask.WaitAsync(TimeSpan.FromSeconds(10)));                // B's check now compares against 2, so its If-Match: 1 is a 409
    }

    [PgFact]
    public async Task Fix_3_REPEATABLE_READ_or_SERIALIZABLE_fails_the_second_writer_with_40001_which_the_caller_must_retry()
    {
        foreach (var level in new[] { IsolationLevel.RepeatableRead, IsolationLevel.Serializable })
        {
            var e = await fx.NewEnvAsync();
            await using var a = e.Db.CreateDbContext(); await using var b = e.Db.CreateDbContext();
            await using var txA = await a.Database.BeginTransactionAsync(level);
            await using var txB = await b.Database.BeginTransactionAsync(level);
            var ta = await a.Set<ReleaseTrains>().SingleAsync(x => x.Id == "t1");
            var tb = await b.Set<ReleaseTrains>().SingleAsync(x => x.Id == "t1");
            ta.Title = "A"; ta.Version++; await a.SaveChangesAsync(); await txA.CommitAsync();
            tb.Title = "B"; tb.Version++;
            // Npgsql's default execution strategy wraps a transient failure in an InvalidOperationException ("likely due to a transient failure"): the 40001 is two levels down
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => b.SaveChangesAsync());
            var pg = Assert.IsType<PostgresException>(Chain(ex).OfType<PostgresException>().Single());
            Assert.Equal("40001", pg.SqlState);   // could not serialize access due to concurrent update
            o.WriteLine($"{level}: second writer got 40001, surfaced as {ex.GetType().Name} > {ex.InnerException?.GetType().Name} > {pg.GetType().Name}");
        }
    }

    // ---- cross-row rules (plan 4.8): a trigger's EXISTS check cannot see a concurrent, uncommitted write --------------------------------

    private const string CertifySql = "UPDATE \"StageGates\" SET \"Status\"='Certified',\"CertifiedByUserId\"='rte',\"CertifiedAt\"='2026-10-20T14:05:00Z',\"LastChangedByUserId\"='rte',\"LastChangedAt\"='2026-10-20T14:05:00Z' WHERE \"Id\"='g1'";
    private const string AddTaskSql = "INSERT INTO \"ChecklistTasks\"(\"Id\",\"StageGateId\",\"TaskDescription\",\"OwnerUserId\",\"SequenceOrder\") VALUES('k-late','g1','added while certifying','rte',9)";

    private static async Task<PgEnv> GateReadyToCertifyAsync(PgFixture fx)
    {
        var e = await fx.NewEnvAsync();
        Assert.True((await e.Tasks.CompleteAsync("k1", new Actor("rte"))).IsOk);
        Assert.True((await e.Gates.StartAsync("g1", new Actor("rte"))).IsOk);
        return e;
    }

    private static async Task ExecAsync(NpgsqlConnection c, NpgsqlTransaction tx, string sql) { await using var cmd = new NpgsqlCommand(sql, c, tx); await cmd.ExecuteNonQueryAsync(); }

    [PgFact]
    public async Task Hazard_a_gate_is_certified_while_another_session_adds_an_open_task_and_both_commit()
    {
        var e = await GateReadyToCertifyAsync(fx);
        await using var a = e.Open(); await using var b = e.Open();
        await using var txA = await a.BeginTransactionAsync(); await using var txB = await b.BeginTransactionAsync();
        await ExecAsync(a, txA, AddTaskSql);            // A: a new open task on g1, not committed
        await ExecAsync(b, txB, CertifySql);            // B: trg_Gate_CertifyRequiresTasks looks for open tasks, cannot see A's, and lets the certification through
        await txB.CommitAsync(); await txA.CommitAsync();
        o.WriteLine($"gate g1: {e.Scalar("SELECT \"Status\" FROM \"StageGates\" WHERE \"Id\"='g1'")}, open tasks on it: {e.Count("SELECT count(*) FROM \"ChecklistTasks\" WHERE \"StageGateId\"='g1' AND \"IsCompleted\"=0")}");
        Assert.Equal("Certified", e.Scalar("SELECT \"Status\" FROM \"StageGates\" WHERE \"Id\"='g1'"));
        Assert.Equal(1L, e.Count("SELECT count(*) FROM \"ChecklistTasks\" WHERE \"StageGateId\"='g1' AND \"IsCompleted\"=0"));   // the rule "no certification with an open task" is violated
    }

    [PgFact]
    public async Task Fix_the_child_write_locks_the_parent_row_so_the_certifier_waits_and_then_the_rule_refuses_it()
    {
        var e = await GateReadyToCertifyAsync(fx);
        // spike-only trigger standing in for what batch 1 would generate for every child table a parent rule reads
        e.Sql("CREATE FUNCTION spike_lock_gate() RETURNS trigger LANGUAGE plpgsql AS $f$ BEGIN PERFORM 1 FROM \"StageGates\" WHERE \"Id\"=NEW.\"StageGateId\" FOR UPDATE; RETURN NEW; END $f$");
        e.Sql("CREATE TRIGGER spike_lock_gate BEFORE INSERT OR UPDATE ON \"ChecklistTasks\" FOR EACH ROW EXECUTE FUNCTION spike_lock_gate()");
        await using var a = e.Open(); await using var b = e.Open();
        await using var txA = await a.BeginTransactionAsync(); await using var txB = await b.BeginTransactionAsync();
        await ExecAsync(a, txA, AddTaskSql);
        var certifier = Task.Run(() => ExecAsync(b, txB, CertifySql));
        await Task.Delay(700);
        Assert.False(certifier.IsCompleted, "the certifier must wait for the gate row");
        await txA.CommitAsync();
        var ex = await Assert.ThrowsAsync<PostgresException>(() => certifier.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("RE001", ex.SqlState);             // now it sees the open task and refuses with the rule's own message
        o.WriteLine("certifier refused after waiting: " + ex.MessageText);
        Assert.Equal("InProgress", e.Scalar("SELECT \"Status\" FROM \"StageGates\" WHERE \"Id\"='g1'"));
    }

    private static IEnumerable<Exception> Chain(Exception e) { for (var x = e; x is not null; x = x.InnerException!) { yield return x; if (x.InnerException is null) yield break; } }

    [PgFact]
    public async Task A_retrying_execution_strategy_cannot_be_combined_with_the_services_user_started_transactions()
    {
        var e = await fx.NewEnvAsync();
        await using var db = new PgReleaseDbContext(new DbContextOptionsBuilder<PgReleaseDbContext>().UseNpgsql(e.ConnectionString, n => n.EnableRetryOnFailure()).Options);
        // ServiceBase.RunAsync does exactly this (BeginTransactionAsync in the service). Retrying 40001/40P01 therefore means moving RunAsync under strategy.ExecuteAsync(...)
        await using var tx = await db.Database.BeginTransactionAsync();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => db.Set<ReleaseTrains>().SingleAsync(x => x.Id == "t1"));   // refused at the first command inside it
        Assert.Contains("does not support user-initiated transactions", ex.Message);
    }

    [PgFact]
    public async Task Real_services_under_16_parallel_callers_with_the_same_If_Match_let_more_than_one_through()
    {
        // not deterministic (a race), so it reports instead of asserting a count; with the token or the row lock the answer must be exactly one
        var wins = new List<int>();
        for (var round = 0; round < 5; round++)
        {
            var e = await fx.NewEnvAsync();
            var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => e.Trains.RecordRollbackRehearsedAsync("t1", new Actor("rte"), 1))));
            wins.Add(results.Count(r => r.IsOk));
        }
        o.WriteLine($"callers with If-Match: 1 that got 200, per round of 16: [{string.Join(", ", wins)}] (a correct implementation: always 1)");
        Assert.All(wins, w => Assert.InRange(w, 1, 16));
    }
}
