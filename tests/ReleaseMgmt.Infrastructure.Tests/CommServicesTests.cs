using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Reminders;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Infrastructure.Tests;

/// <summary>REOS-43/44 against the migrated schema: single-snapshot hydration, library and per-train copies, schedule seeding, SentAt vs DueAt.</summary>
public sealed class CommServicesTests(TriggerSuiteFixture fx) : IClassFixture<TriggerSuiteFixture>
{
    private sealed class Factory(string path) : IDbContextFactory<ReleaseDbContext>
    {
        public ReleaseDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ReleaseDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").AddInterceptors(new SqliteConnectionInterceptor()).Options);
    }

    private sealed class Env
    {
        public required string Path { get; init; }
        public required FakeTimeProvider Time { get; init; }
        public required IDbContextFactory<ReleaseDbContext> Db { get; init; }
        public DisplayClock Clock { get; } = new(TimeZoneInfo.FindSystemTimeZoneById("America/Chicago"));
        public Func<string, Task>? Probe { get; set; }
        public CommHydrationService Hydration => new(Db, Time, Clock, s => Probe?.Invoke(s) ?? Task.CompletedTask);
        public CommLibraryService Library => new(Db, Time);
        public CommScheduleService Schedule => new(Db, Time, Clock);

        public void Sql(string sql, params object?[] p) { using var c = TriggerSuiteFixture.Open(Path); TriggerSuiteFixture.Run(c, sql, p); }
        public List<object?[]> Query(string sql, params object?[] p)
        {
            using var c = TriggerSuiteFixture.Open(Path);
            using var cmd = c.CreateCommand();
            TriggerSuiteFixture.Bind(cmd, sql, p);
            using var r = cmd.ExecuteReader();
            var rows = new List<object?[]>();
            while (r.Read()) { var row = new object?[r.FieldCount]; r.GetValues(row!); rows.Add(row.Select(v => v is DBNull ? null : v).ToArray()); }
            return rows;
        }
        public long Count(string sql, params object?[] p) => Convert.ToInt64(Query(sql, p)[0][0]);
        public string Text(string sql, params object?[] p) => Convert.ToString(Query(sql, p)[0][0])!;
    }

    private static readonly Actor Rte = new("rte");

    private Env NewEnv()
    {
        var path = fx.FreshPath();
        return new Env { Path = path, Time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 14, 0, 0, TimeSpan.Zero)), Db = new Factory(path) };
    }

    private static DateTime U(string s) => DateTime.SpecifyKind(DateTime.Parse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal), DateTimeKind.Utc);

    // ---- single read-transaction snapshot -----------------------------------------------------------------------
    [Fact]
    public async Task A_write_committed_between_the_reads_cannot_produce_a_mixed_view()
    {
        var e = NewEnv();
        var wrote = false;
        e.Probe = step =>
        {
            if (step != "train" || wrote) return Task.CompletedTask;
            wrote = true;   // another connection commits right after the train row was read, before the lists are
            e.Sql("UPDATE ReleaseTrains SET Title='CHANGED', Version=Version+1 WHERE Id='t1'");
            e.Sql("INSERT INTO Blockers(Id,ReleaseTrainId,Title,Severity,RaisedAt) VALUES('b1','t1','New critical','Critical','2026-10-20T14:00:00Z')");
            e.Sql("INSERT INTO BundledProducts(Id,ReleaseTrainId,ProductName,VersionTag,ProjectCode) VALUES('p9','t1','Late Product','9.9','LAT')");
            e.Sql("UPDATE ChecklistTasks SET IsCompleted=1, CompletedAt='2026-10-20T14:00:00Z', CompletedByUserId='rte', LastChangedByUserId='rte', LastChangedAt='2026-10-20T14:00:00Z' WHERE Id='k1'");
            return Task.CompletedTask;
        };
        var r = await e.Hydration.HydrateAsync("t1", new(Text: "{ReleaseTitle}|{BlockerCount}|{ProductCount}|{TasksDone}"), CommTarget.PlainText);
        Assert.True(wrote);
        Assert.Equal("R26.10|0|2|0", r.Value!.Text);   // all four from before the write
        Assert.Equal(1, r.Value.TrainVersion);           // and the version that goes with them

        e.Probe = null;
        var after = await e.Hydration.HydrateAsync("t1", new(Text: "{ReleaseTitle}|{BlockerCount}|{ProductCount}|{TasksDone}"), CommTarget.PlainText);
        Assert.Equal("CHANGED|1|3|1", after.Value!.Text);
        Assert.Equal(2, after.Value.TrainVersion);
    }

    [Fact]
    public async Task The_template_is_part_of_the_same_snapshot()
    {
        var e = NewEnv();
        e.Sql("INSERT INTO CommTemplateLibrary(Id,TemplateType,Name,Audience,SubjectLine,MarkdownBody) VALUES('l1','GoNoGo','L','All','S {Status}','B {ReleaseTitle}')");
        e.Probe = step => { if (step == "template") e.Sql("UPDATE CommTemplateLibrary SET MarkdownBody='CHANGED {ReleaseTitle}', Version=2 WHERE Id='l1'"); return Task.CompletedTask; };
        var r = (await e.Hydration.HydrateAsync("t1", new(LibraryTemplateId: "l1"), CommTarget.PlainText)).Value!;
        Assert.Equal("B R26.10", r.Text); Assert.Equal(1, r.TemplateVersion);   // the read of the template already happened; a later write is not seen
    }

    [Fact]
    public async Task Hydration_reports_asOf_from_the_service_clock_and_the_train_version()
    {
        var e = NewEnv();
        e.Time.Advance(TimeSpan.FromMinutes(7) + TimeSpan.FromMilliseconds(400));
        var r = (await e.Hydration.HydrateAsync("t1", new(Text: "x"), CommTarget.Markdown)).Value!;
        Assert.Equal(U("2026-10-20T14:07:00Z"), r.AsOf);   // whole seconds, like every stored timestamp
        Assert.Equal(1, r.TrainVersion);
        Assert.True(r.CanDispatch); Assert.Empty(r.TokenErrors);
    }

    [Fact]
    public async Task Hydration_reads_the_train_the_way_the_tokens_say()
    {
        var e = NewEnv();
        var r = (await e.Hydration.HydrateAsync("t1", new(Text: "{ReleaseTitle}|{Status}|{ProductList}|{ProductCount}|{NextGate}|{NextGateDue}|{TasksDone}/{TasksTotal}|{PercentComplete}|{Owners}|{Window}|{DaysToTarget}|{GoNoGoDecision}"), CommTarget.PlainText)).Value!;
        Assert.Equal("R26.10|Planning|Card Portal 2.1.0 · Payments API 4.5.0|2|Code Freeze|Fri 23 Oct|0/2|0%|Rae T. · Gov One|Not scheduled|8|Not yet recorded", r.Text);
    }

    [Fact]
    public async Task An_unknown_token_gives_tokenErrors_and_CanDispatch_false()
    {
        var e = NewEnv();
        var r = (await e.Hydration.HydrateAsync("t1", new(Subject: "{Nope}", Text: "Hi {ReleaseTitel}"), CommTarget.Markdown)).Value!;
        Assert.False(r.CanDispatch);
        Assert.Equal(["subject", "body"], r.TokenErrors.Select(t => t.Part));
    }

    [Fact]
    public async Task Hydrating_a_per_train_template_uses_its_own_text_and_a_template_of_another_train_is_not_found()
    {
        var e = NewEnv();
        e.Sql("INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CreatedAt,UpdatedAt) VALUES('t2','Other','2026-11-13','Low','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z')");
        e.Sql("INSERT INTO CommTemplates(Id,ReleaseTrainId,TemplateType,Audience,SubjectLine,MarkdownBody) VALUES('c1','t1','GoNoGo','All','[{ReleaseTitle}]','Body {Status}')");
        var ok = (await e.Hydration.HydrateAsync("t1", new(TemplateId: "c1"), CommTarget.PlainText)).Value!;
        Assert.Equal(("[R26.10]", "Body Planning", "c1"), (ok.Subject, ok.Text, ok.TemplateId));
        Assert.Equal(ResultKind.NotFound, (await e.Hydration.HydrateAsync("t2", new(TemplateId: "c1"), CommTarget.PlainText)).Kind);
        Assert.Equal(ResultKind.NotFound, (await e.Hydration.HydrateAsync("nope", new(Text: "x"), CommTarget.PlainText)).Kind);
        Assert.Equal(ResultKind.NotFound, (await e.Hydration.HydrateAsync("t1", new(LibraryTemplateId: "nope"), CommTarget.PlainText)).Kind);
    }

    [Fact]
    public async Task Exactly_one_source_is_required()
    {
        var e = NewEnv();
        Assert.Equal(CommGuards.CommPreviewSource, (await e.Hydration.HydrateAsync("t1", new(), CommTarget.PlainText)).Failures.Single().Guard);
        Assert.Equal(CommGuards.CommPreviewSource, (await e.Hydration.HydrateAsync("t1", new(TemplateId: "a", Text: "b"), CommTarget.PlainText)).Failures.Single().Guard);
    }

    // ---- library and per-train copies ------------------------------------------------------------------------------
    private static LibraryTemplateInput Lib(string name = "T-7 Readiness", string type = "Tminus7", string body = "**{ReleaseTitle}** is {Status}") => new(name, type, "All", "[{ReleaseTitle}] notice", body);

    [Fact]
    public async Task Library_create_and_update_stamp_Version_and_write_one_audit_row_each()
    {
        var e = NewEnv();
        var created = (await e.Library.CreateLibraryAsync(Lib(), Rte)).Value!;
        Assert.Equal(1, created.Version); Assert.True(created.Valid);
        var upd = await e.Library.UpdateLibraryAsync(created.Id, Lib(body: "Changed {Status}"), Rte, 1);
        Assert.Equal(2, upd.Value!.Version);
        Assert.Equal("2", e.Text("SELECT Version FROM CommTemplateLibrary WHERE Id=?", created.Id));
        Assert.Equal(1, e.Count("SELECT count(*) FROM AuditEvents WHERE EntityType='CommLibraryTemplate' AND Action='Create' AND EntityId=? AND ActorUserId='rte'", created.Id));
        Assert.Equal(1, e.Count("SELECT count(*) FROM AuditEvents WHERE EntityType='CommLibraryTemplate' AND Action='Update' AND EntityId=? AND BeforeJson LIKE '%{ReleaseTitle}** is%' AND AfterJson LIKE '%Changed%'", created.Id));

        var stale = await e.Library.UpdateLibraryAsync(created.Id, Lib(body: "again"), Rte, 1);
        Assert.Equal(ResultKind.Conflict, stale.Kind);
        Assert.Equal(2, ((LibraryTemplateView)stale.Current!).Version);
        Assert.Equal(1, e.Count("SELECT count(*) FROM AuditEvents WHERE EntityType='CommLibraryTemplate' AND Action='Update'"));   // a refused write leaves no audit row
    }

    [Fact]
    public async Task Library_validation_names_its_guard()
    {
        var e = NewEnv();
        await e.Library.CreateLibraryAsync(Lib(), Rte);
        Assert.Equal(CommGuards.CommTemplateNameTaken, (await e.Library.CreateLibraryAsync(Lib(name: "t-7 READINESS", type: "x"), Rte)).Failures.Single().Guard);
        var bad = await e.Library.CreateLibraryAsync(new("", "", "Bosses", "line1\nline2", ""), Rte);
        Assert.All(bad.Failures, f => Assert.Equal(CommGuards.CommTemplateInvalid, f.Guard));
        Assert.True(bad.Failures.Count >= 4);
        Assert.Equal(1, e.Count("SELECT count(*) FROM CommTemplateLibrary"));
    }

    [Fact]
    public async Task A_library_entry_with_an_unknown_token_saves_but_reports_it()
    {
        var e = NewEnv();
        var v = (await e.Library.CreateLibraryAsync(Lib(body: "Hi {Nope}"), Rte)).Value!;
        Assert.False(v.Valid); Assert.Equal(TokenErrorKinds.UnknownToken, v.TokenErrors.Single().Kind);
        Assert.Equal(["ReleaseTitle"], v.TokensUsed);
    }

    [Fact]
    public async Task Copying_into_a_train_makes_an_editable_copy_that_locks_once_dispatched()
    {
        var e = NewEnv();
        var lib = (await e.Library.CreateLibraryAsync(Lib(), Rte)).Value!;
        var copy = (await e.Library.CopyToTrainAsync("t1", new(lib.Id), Rte)).Value!;
        Assert.Equal((lib.Name, lib.MarkdownBody, "t1", false), (copy.LibraryName, copy.MarkdownBody, copy.ReleaseTrainId, copy.Dispatched));
        Assert.Equal(1, e.Count("SELECT count(*) FROM AuditEvents WHERE EntityType='CommTemplate' AND Action='Copy' AND ReleaseTrainId='t1' AND EntityId=?", copy.Id));

        var edit = (await e.Library.UpdateForTrainAsync(copy.Id, new("Ops", "New subject", "New {Status}"), Rte, 1)).Value!;
        Assert.Equal((2, "Ops", "New subject"), (edit.Version, edit.Audience, edit.SubjectLine));
        Assert.Equal(lib.MarkdownBody, (await e.Library.GetLibraryAsync(lib.Id))!.MarkdownBody);   // the library is untouched
        Assert.Equal(ResultKind.Conflict, (await e.Library.UpdateForTrainAsync(copy.Id, new("Ops", "x", "y"), Rte, 1)).Kind);

        e.Sql("INSERT INTO CommDispatches(Id,CommTemplateId,Channel,HydratedSubject,HydratedBody,DispatchedByUserId,DispatchedAt,Outcome) VALUES('d1',?,'Copy','s','b','rte','2026-10-20T14:00:00Z','Handed')", copy.Id);
        var locked = await e.Library.UpdateForTrainAsync(copy.Id, new("Ops", "x", "y"), Rte, 2);
        Assert.Equal(CommGuards.CommTemplateDispatched, locked.Failures.Single().Guard);
        Assert.True((await e.Library.GetForTrainAsync(copy.Id))!.Dispatched);
        Assert.Equal(1, e.Count("SELECT count(*) FROM AuditEvents WHERE EntityType='CommTemplate' AND Action='Update'"));
    }

    [Fact]
    public async Task Copy_needs_a_real_train_and_a_real_library_entry()
    {
        var e = NewEnv();
        var lib = (await e.Library.CreateLibraryAsync(Lib(), Rte)).Value!;
        Assert.Equal(ResultKind.NotFound, (await e.Library.CopyToTrainAsync("nope", new(lib.Id), Rte)).Kind);
        Assert.Equal(ResultKind.NotFound, (await e.Library.CopyToTrainAsync("t1", new("nope"), Rte)).Kind);
        Assert.Equal(CommGuards.CommTemplateInvalid, (await e.Library.CopyToTrainAsync("t1", new(null), Rte)).Failures.Single().Guard);
    }

    // ---- schedule seeding ----------------------------------------------------------------------------------------------
    private static async Task<string> SeedDefaultTemplate(Env e, string status = "Draft")
    {
        e.Sql("INSERT INTO TrainTemplates(Id,Name,Status,DefaultRiskTier) VALUES('tt1','Std',?,'Moderate')", status);
        Assert.True(await CommDefaults.EnsureAsync(e.Db, "Std"));
        return "tt1";
    }

    [Fact]
    public async Task Seeding_computes_DueAt_from_the_target_with_business_days_holidays_and_the_window()
    {
        var e = NewEnv();
        e.Sql("INSERT INTO Holidays(Day,Name) VALUES('2026-10-28','Test holiday')");
        e.Sql("INSERT INTO DeploymentWindows(Id,ReleaseTrainId,StartsAt,EndsAt) VALUES('w1','t1','2026-10-30T06:00:00Z','2026-10-30T10:00:00Z')");   // 01:00-05:00 CDT
        var tt = await SeedDefaultTemplate(e);

        var items = (await e.Schedule.SeedAsync("t1", new(tt), Rte)).Value!;
        Assert.Equal(6, items.Count);
        Assert.Equal(
            [
                ("T−7", "T-7 Readiness notice", U("2026-10-20T14:00:00Z")),   // 09:00 CDT; Wed 28 is a holiday so 7 business days back from Fri 30 is Tue 20
                ("T−3", "T-3 Checkpoint", U("2026-10-26T14:00:00Z")),
                ("T−1", "T-1 Go/No-Go outcome", U("2026-10-29T14:00:00Z")),
                ("T0", "T0 Cutover start", U("2026-10-30T05:45:00Z")),         // 15 minutes before the window opens
                ("T0", "T0 Release complete", U("2026-10-30T10:00:00Z")),      // when the window closes
                ("T+5", "Hypercare exit", U("2026-11-06T15:00:00Z")),          // 09:00 CST after DST ended on Sun 1 Nov
            ],
            items.Select(i => (i.Label, i.Name, i.DueAt)));
        Assert.All(items, i => { Assert.Null(i.SentAt); Assert.False(i.Late); Assert.Equal(1, i.Version); });
        Assert.Equal(6, e.Count("SELECT count(*) FROM CommSchedule WHERE ReleaseTrainId='t1' AND SentAt IS NULL"));
        Assert.Equal(6, e.Count("SELECT count(*) FROM CommTemplates WHERE ReleaseTrainId='t1' AND LibraryTemplateId IS NOT NULL"));
        Assert.Equal("2026-10-20T14:00:00Z", e.Text("SELECT MIN(DueAt) FROM CommSchedule"));   // stored as UTC ISO text
        Assert.Equal(1, e.Count("SELECT count(*) FROM AuditEvents WHERE EntityType='CommSchedule' AND Action='Seed' AND ReleaseTrainId='t1' AND ActorUserId='rte'"));
        Assert.Equal(1, e.Count("SELECT count(*) FROM AuditEvents WHERE Action='Seed'"));   // one audit row for the whole write
    }

    [Fact]
    public async Task Seeding_without_a_window_or_holidays_uses_the_send_time_and_reuses_a_train_copy()
    {
        var e = NewEnv();
        var tt = await SeedDefaultTemplate(e);
        var libId = e.Text("SELECT Id FROM CommTemplateLibrary WHERE Name='T-1 Go/No-Go outcome'");
        var existing = (await e.Library.CopyToTrainAsync("t1", new(libId), Rte)).Value!;   // the RTE already tailored this one

        var items = (await e.Schedule.SeedAsync("t1", new(tt), Rte)).Value!;
        Assert.Equal(U("2026-10-30T14:00:00Z"), items.First(i => i.Name == "T0 Cutover start").DueAt);   // no window: 09:00 on the target day
        Assert.Equal(existing.Id, items.Single(i => i.Name == "T-1 Go/No-Go outcome").CommTemplateId);   // not duplicated
        Assert.Equal(6, e.Count("SELECT count(*) FROM CommTemplates WHERE ReleaseTrainId='t1'"));
    }

    [Fact]
    public async Task Seeding_guards()
    {
        var e = NewEnv();
        var tt = await SeedDefaultTemplate(e);
        Assert.Equal(ResultKind.NotFound, (await e.Schedule.SeedAsync("nope", new(tt), Rte)).Kind);
        Assert.Equal(ResultKind.NotFound, (await e.Schedule.SeedAsync("t1", new("nope"), Rte)).Kind);
        Assert.Equal(CommGuards.CommTemplateInvalid, (await e.Schedule.SeedAsync("t1", new(null), Rte)).Failures.Single().Guard);

        e.Sql("INSERT INTO TrainTemplates(Id,Name,Status,DefaultRiskTier) VALUES('tt0','Empty','Draft','Moderate')");
        Assert.Equal(CommGuards.CommPlanEmpty, (await e.Schedule.SeedAsync("t1", new("tt0"), Rte)).Failures.Single().Guard);
        Assert.Equal(0, e.Count("SELECT count(*) FROM CommSchedule"));   // a refusal writes nothing

        Assert.True((await e.Schedule.SeedAsync("t1", new(tt), Rte)).IsOk);
        Assert.Equal(CommGuards.CommScheduleExists, (await e.Schedule.SeedAsync("t1", new(tt), Rte)).Failures.Single().Guard);
        Assert.Equal(6, e.Count("SELECT count(*) FROM CommSchedule"));
        Assert.Equal(1, e.Count("SELECT count(*) FROM AuditEvents WHERE Action='Seed'"));
    }

    [Fact]
    public async Task A_retired_template_or_a_closed_train_cannot_seed()
    {
        var e = NewEnv();
        var tt = await SeedDefaultTemplate(e);
        e.Sql("UPDATE TrainTemplates SET Status='Retired' WHERE Id='tt1'");
        Assert.Equal(CommGuards.CommTemplateRetired, (await e.Schedule.SeedAsync("t1", new(tt), Rte)).Failures.Single().Guard);
        e.Sql("UPDATE TrainTemplates SET Status='Draft' WHERE Id='tt1'");
        e.Sql("UPDATE ReleaseTrains SET CurrentStatus='Aborted' WHERE Id='t1'");
        Assert.Equal(Guards.TrainClosed, (await e.Schedule.SeedAsync("t1", new(tt), Rte)).Failures.Single().Guard);
    }

    [Fact]
    public async Task The_seeded_default_plan_lists_the_six_standard_messages_and_every_token_in_them_is_allowlisted()
    {
        Assert.Equal(["T-7", "T-3", "T-1", "T0 Cutover", "T0 Release", "Hypercare"], CommDefaults.Entries.Select(x => x.Name.Split(' ')[0] + (x.Name.StartsWith("T0") ? " " + x.Name.Split(' ')[1] : "")).ToList());
        Assert.Equal([-7, -3, -1, 0, 0, 5], CommDefaults.Entries.Select(x => x.OffsetDays));
        foreach (var x in CommDefaults.Entries)
        {
            Assert.True(TokenParser.Parse(x.Subject, "subject").IsValid, x.Name);
            Assert.True(TokenParser.Parse(x.Body).IsValid, x.Name);
            Assert.Contains(x.Audience, CommLibraryService.Audiences);
        }
        var e = NewEnv();
        await SeedDefaultTemplate(e);
        Assert.False(await CommDefaults.EnsureAsync(e.Db, "Std"));   // idempotent: a non-empty library is never touched
        Assert.Equal(6, e.Count("SELECT count(*) FROM CommTemplateLibrary"));
        Assert.Equal(6, e.Count("SELECT count(*) FROM TemplateCommSchedule WHERE TemplateId='tt1'"));
    }

    // ---- SentAt vs DueAt -------------------------------------------------------------------------------------------------
    [Fact]
    public async Task MarkSent_records_SentAt_from_the_clock_and_lateness_against_DueAt()
    {
        var e = NewEnv();
        var tt = await SeedDefaultTemplate(e);
        var items = (await e.Schedule.SeedAsync("t1", new(tt), Rte)).Value!;
        var t7 = items.Single(i => i.Label == "T−7");   // due Wed 2026-10-21T14:00Z (no holidays in this database)
        var t1 = items.Single(i => i.Label == "T−1");   // due 2026-10-29T14:00Z

        var early = (await e.Schedule.MarkSentAsync(t1.Id, Rte, 1)).Value!;
        Assert.Equal((U("2026-10-20T14:00:00Z"), false, 0, CommScheduling.Sent, 2), (early.SentAt!.Value, early.Late, early.LateMinutes, early.State, early.Version));

        e.Time.Advance(TimeSpan.FromHours(24) + TimeSpan.FromMinutes(42) + TimeSpan.FromSeconds(5));   // now Wed 14:42:05Z, 42 min 5 s after T-7 was due
        var late = (await e.Schedule.MarkSentAsync(t7.Id, Rte, 1)).Value!;
        Assert.Equal((U("2026-10-21T14:42:05Z"), true, 43, CommScheduling.SentLate), (late.SentAt!.Value, late.Late, late.LateMinutes, late.State));
        Assert.Equal("2026-10-21T14:42:05Z", e.Text("SELECT SentAt FROM CommSchedule WHERE Id=?", t7.Id));

        Assert.Equal(1, e.Count("SELECT count(*) FROM AuditEvents WHERE EntityType='CommSchedule' AND Action='MarkSent' AND EntityId=? AND ReleaseTrainId='t1' AND AfterJson LIKE '%\"lateMinutes\":43%'", t7.Id));
        Assert.Equal(2, e.Count("SELECT count(*) FROM AuditEvents WHERE Action='MarkSent'"));
        var listed = (await e.Schedule.ListAsync("t1"))!;
        Assert.Equal(2, listed.Count(i => i.SentAt is not null));
        Assert.Equal(CommScheduling.Scheduled, listed.Single(i => i.Label == "T−3").State);   // due Mon 26 Oct, five days ahead
        Assert.Equal(CommScheduling.SentLate, listed.Single(i => i.Label == "T−7").State);
    }

    [Fact]
    public async Task MarkSent_is_history_it_cannot_be_repeated_and_honours_If_Match()
    {
        var e = NewEnv();
        var tt = await SeedDefaultTemplate(e);
        var item = (await e.Schedule.SeedAsync("t1", new(tt), Rte)).Value!.First();

        var stale = await e.Schedule.MarkSentAsync(item.Id, Rte, 7);
        Assert.Equal(ResultKind.Conflict, stale.Kind);
        Assert.Equal(item.Id, ((CommScheduleItemView)stale.Current!).Id);
        Assert.Equal(0, e.Count("SELECT count(*) FROM CommSchedule WHERE SentAt IS NOT NULL"));

        Assert.True((await e.Schedule.MarkSentAsync(item.Id, Rte, 1)).IsOk);
        Assert.Equal(CommGuards.CommAlreadySent, (await e.Schedule.MarkSentAsync(item.Id, Rte, 2)).Failures.Single().Guard);
        Assert.Equal(ResultKind.NotFound, (await e.Schedule.MarkSentAsync("nope", Rte, 1)).Kind);
        Assert.Equal(1, e.Count("SELECT count(*) FROM AuditEvents WHERE Action='MarkSent'"));
    }

    [Fact]
    public async Task MarkSent_can_link_the_dispatch_row_when_dispatch_supplies_one()
    {
        var e = NewEnv();
        var tt = await SeedDefaultTemplate(e);
        var item = (await e.Schedule.SeedAsync("t1", new(tt), Rte)).Value!.First();
        e.Sql("INSERT INTO CommDispatches(Id,CommTemplateId,Channel,HydratedSubject,HydratedBody,DispatchedByUserId,DispatchedAt,Outcome) VALUES('d1',?,'Copy','s','b','rte','2026-10-20T14:00:00Z','Handed')", item.CommTemplateId);
        var r = (await e.Schedule.MarkSentAsync(item.Id, Rte, 1, dispatchId: "d1")).Value!;
        Assert.Equal("d1", r.DispatchId);
    }

    [Fact]
    public async Task List_of_an_unknown_train_is_null()
    {
        var e = NewEnv();
        Assert.Null(await e.Schedule.ListAsync("nope"));
        Assert.Empty((await e.Schedule.ListAsync("t1"))!);
    }
}
