using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Domain.Sync;
using ReleaseMgmt.Infrastructure.Reminders;
using ReleaseMgmt.Infrastructure.Sync;
using static ReleaseMgmt.Infrastructure.Tests.SyncTestKit;

namespace ReleaseMgmt.Infrastructure.Tests;

/// <summary>REOS-39: Connectors admin, ExternalLinks management, credentials only via Data Protection, keys/states/dates only. REOS-41: stale links.</summary>
public sealed class SyncServiceTests(TriggerSuiteFixture fx) : IClassFixture<TriggerSuiteFixture>
{
    private static readonly Actor Rte = new("rte");
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private static string GuardOf<T>(ServiceResult<T> r) { Assert.Equal(ResultKind.GuardFailed, r.Kind); return r.Failures[0].Guard; }

    // ---- settings ---------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task The_first_save_creates_the_row_and_later_saves_need_the_current_version()
    {
        var e = NewEnv(fx, serviceNow: false);
        var made = await e.Service.SaveSettingsAsync("ServiceNow", SnUrl + "/", null, Rte, null);
        Assert.True(made.IsOk);
        Assert.Equal((SnUrl + "/", true, 1), (made.Value!.BaseUrl, made.Value.IsEnabled, made.Value.Version));   // trailing slash is kept as typed; the connector normalises

        var stale = await e.Service.SaveSettingsAsync("ServiceNow", SnUrl, false, Rte, 7);
        Assert.Equal(ResultKind.Conflict, stale.Kind);
        var ok = await e.Service.SaveSettingsAsync("ServiceNow", null, false, Rte, 1);   // disable only
        Assert.Equal((false, 2), (ok.Value!.IsEnabled, ok.Value.Version));
        Assert.Equal(SnUrl + "/", ok.Value.BaseUrl);
        Assert.Equal(["Create", "Update"], e.Query("SELECT Action FROM AuditEvents WHERE EntityType='ConnectorState' ORDER BY Id").Select(r => (string)r[0]!));
        Assert.Equal("rte", e.Query("SELECT ActorUserId FROM AuditEvents WHERE EntityType='ConnectorState' AND Action='Create'")[0][0]);
    }

    [Theory]
    [InlineData("http://acme.service-now.com", "https")]
    [InlineData("https://user:pw@acme.service-now.com", "credentials")]
    [InlineData("https://acme.service-now.com/?x=1", "query")]
    [InlineData("https://127.0.0.1", "private")]
    [InlineData("nonsense", "absolute")]
    public async Task Unsafe_base_urls_are_refused_with_a_readable_422(string url, string word)
    {
        var e = NewEnv(fx, serviceNow: false);
        var svc = new ConnectorService(e.Db, e.Time, e.Creds, new ConnectorFactory(new FakeHttpFactory(e.Http), e.Creds, e.Options, e.Time, new TestEnv(), e.Config), e.Alerts, e.Options, new TestEnv(), IPAddress.IsLoopback);
        var r = await svc.SaveSettingsAsync("ServiceNow", url, null, Rte, null);
        Assert.Equal(ConnectorGuards.ConnectorInvalid, GuardOf(r));
        Assert.Contains(word, r.Failures[0].Message);
        Assert.Equal(0, e.Count("SELECT COUNT(*) FROM ConnectorState"));
    }

    [Fact]
    public async Task An_unknown_connector_is_404_and_a_save_with_nothing_to_save_says_so()
    {
        var e = NewEnv(fx, serviceNow: false);
        Assert.Equal(ResultKind.NotFound, (await e.Service.SaveSettingsAsync("Bitbucket", SnUrl, null, Rte, null)).Kind);
        Assert.Equal(ConnectorGuards.ConnectorInvalid, GuardOf(await e.Service.SaveSettingsAsync("Jira", null, true, Rte, null)));
    }

    // ---- credentials -----------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Credentials_are_validated_per_connector_and_audited_without_their_values()
    {
        var e = NewEnv(fx, serviceNow: false, jira: false);
        Assert.Equal(ConnectorGuards.CredentialsInvalid, GuardOf(await e.Service.SetCredentialsAsync("Jira", "Basic", "u", "p", Rte)));            // Jira is an API token
        Assert.Equal(ConnectorGuards.CredentialsInvalid, GuardOf(await e.Service.SetCredentialsAsync("ServiceNow", "ApiToken", "u", "p", Rte)));
        Assert.Equal(ConnectorGuards.CredentialsInvalid, GuardOf(await e.Service.SetCredentialsAsync("Jira", "ApiToken", "", Secret, Rte)));
        Assert.Equal(ConnectorGuards.CredentialsInvalid, GuardOf(await e.Service.SetCredentialsAsync("Jira", "ApiToken", "rae@example.com", "", Rte)));
        Assert.Empty(e.Creds.Items);

        var r = await e.Service.SetCredentialsAsync("Jira", "ApiToken", " rae@example.com ", Secret, Rte);
        Assert.True(r.IsOk);
        Assert.Equal((true, "ApiToken"), (r.Value!.HasCredentials, r.Value.CredentialKind));
        Assert.Equal("rae@example.com", e.Creds.Items["Jira"].Username);
        var audit = e.Query("SELECT Action,AfterJson FROM AuditEvents WHERE EntityType='ConnectorCredentials'")[0];
        Assert.Equal("Set", audit[0]);
        Assert.Contains("ApiToken", (string)audit[1]!);
        Assert.DoesNotContain(Secret, (string)audit[1]!);
        Assert.DoesNotContain("rae@example.com", (string)audit[1]!);

        var cleared = await e.Service.ClearCredentialsAsync("Jira", Rte);
        Assert.False(cleared.Value!.HasCredentials);
        Assert.Empty(e.Creds.Items);
    }

    [Fact]
    public async Task Nothing_the_service_returns_contains_a_credential()
    {
        var e = NewEnv(fx);
        await e.Service.SetCredentialsAsync("ServiceNow", "OAuth", "client-id-123", Secret, Rte);
        e.AddSnLink(); e.SnAnswers();
        await e.Poller.RunOnceAsync();
        var json = JsonSerializer.Serialize(new { list = await e.Service.ListAsync(), links = (await e.Service.ListLinksAsync("t1")).Value });
        Assert.DoesNotContain(Secret, json);
        Assert.DoesNotContain("client-id-123", json);
        Assert.DoesNotContain("svc-release", json);
        Assert.Contains("\"HasCredentials\":true", json);
    }

    // ---- Data Protection: credentials never in SQLite -------------------------------------------------------------------------------------
    private static string Scan(string path) => Convert.ToHexStringLower(File.ReadAllBytes(path));
    private static bool Contains(byte[] haystack, string needle) => haystack.AsSpan().IndexOf(Encoding.UTF8.GetBytes(needle)) >= 0;

    [Fact]
    public async Task Credentials_are_stored_protected_outside_SQLite_and_the_database_file_never_holds_the_secret()
    {
        var e = NewEnv(fx, serviceNow: false);
        var dir = Directory.CreateTempSubdirectory("reos-cred-").FullName;
        var store = new DataProtectionCredentialStore(Microsoft.AspNetCore.DataProtection.DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir, "keys"))), SyncOptions.From(new Cfg(new() { ["Sync:Credentials:Directory"] = Path.Combine(dir, "secrets") })));
        var factory = new ConnectorFactory(new FakeHttpFactory(e.Http), store, e.Options, e.Time, new TestEnv(), e.Config);
        var svc = new ConnectorService(e.Db, e.Time, store, factory, e.Alerts, e.Options, new TestEnv(), null, e.Realtime);
        var poller = new SyncPollerService(e.Db, e.Time, factory, e.Alerts, e.Sink, new SyncCycleWriter(e.Db, e.Time), e.Options, e.Config, NullLogger<SyncPollerService>.Instance);

        await svc.SaveSettingsAsync("ServiceNow", SnUrl, null, Rte, null);
        await svc.SetCredentialsAsync("ServiceNow", "Basic", "svc-release", Secret, Rte);
        e.AddSnLink();
        e.Http.Always(401);                                             // the failure path writes alerts and audit rows too
        await poller.RunOnceAsync();
        e.SnAnswers(); e.Time.Advance(Interval);
        await poller.RunOnceAsync();                                    // and the success path
        Assert.True((await svc.TestAsync("ServiceNow", Rte)).IsOk);

        // the request used the stored secret (proving it was read back) ...
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes("svc-release:" + Secret));
        Assert.Contains("Basic " + basic, e.Http.Auth);

        // ... and no byte of the database (main, WAL, SHM) holds the secret, the user name, or the Basic header form
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(e.Path)!, Path.GetFileName(e.Path) + "*"))
        {
            var bytes = File.ReadAllBytes(f);
            Assert.False(Contains(bytes, Secret), $"{Path.GetFileName(f)} holds the secret");
            Assert.False(Contains(bytes, basic), $"{Path.GetFileName(f)} holds the Basic header");
            Assert.False(Contains(bytes, "svc-release"), $"{Path.GetFileName(f)} holds the user name");
        }

        // the credential file is opaque, the key ring does not contain the secret, and the file is the only place it lives
        var cred = Path.Combine(dir, "secrets", "ServiceNow.cred");
        Assert.True(File.Exists(cred));
        var raw = File.ReadAllBytes(cred);
        Assert.False(Contains(raw, Secret)); Assert.False(Contains(raw, "svc-release"));
        Assert.All(Directory.GetFiles(Path.Combine(dir, "keys")), k => Assert.False(Contains(File.ReadAllBytes(k), Secret)));
        var back = await store.GetAsync("ServiceNow");
        Assert.Equal(("Basic", "svc-release", Secret), (back!.Kind, back.Username, back.Secret));
        Assert.Equal("Basic", await store.KindAsync("ServiceNow"));
        Assert.DoesNotContain(Secret, back.ToString());
    }

    [Fact]
    public async Task A_lost_key_ring_makes_the_credentials_unreadable_and_reports_it_as_an_auth_failure_not_a_crash()
    {
        var dir = Directory.CreateTempSubdirectory("reos-cred-").FullName;
        var o = SyncOptions.From(new Cfg(new() { ["Sync:Credentials:Directory"] = Path.Combine(dir, "secrets") }));
        await new DataProtectionCredentialStore(Microsoft.AspNetCore.DataProtection.DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir, "keys-a"))), o).SetAsync("Jira", new("ApiToken", "u@x.com", Secret));

        var other = new DataProtectionCredentialStore(Microsoft.AspNetCore.DataProtection.DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir, "keys-b"))), o);
        var ex = await Assert.ThrowsAsync<ConnectorException>(() => other.GetAsync("Jira"));
        Assert.Equal(ConnectorErrorKind.Auth, ex.Kind);
        Assert.Contains("key ring", ex.Message);
        Assert.DoesNotContain(Secret, ex.Message);
        Assert.Null(await other.KindAsync("Jira"));                     // listing does not fail
    }

    [Fact]
    public async Task The_store_refuses_unknown_sources_and_can_clear()
    {
        var dir = Directory.CreateTempSubdirectory("reos-cred-").FullName;
        var s = new DataProtectionCredentialStore(Microsoft.AspNetCore.DataProtection.DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir, "k"))), SyncOptions.From(new Cfg(new() { ["Sync:Credentials:Directory"] = Path.Combine(dir, "s") })));
        await Assert.ThrowsAsync<ArgumentException>(() => s.SetAsync("../../etc/passwd", new("Basic", "u", "p")));
        await s.SetAsync("Jira", new("ApiToken", "u", "p"));
        Assert.True(await s.ClearAsync("Jira"));
        Assert.False(await s.ClearAsync("Jira"));
        Assert.Null(await s.GetAsync("Jira"));
    }

    // ---- test connection --------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Test_connection_makes_one_bounded_read_and_reports_success()
    {
        var e = NewEnv(fx, jira: true, serviceNow: false); e.Http.Always(200, """{"accountId":"abc","emailAddress":"rae@example.com"}""");
        var r = await e.Service.TestAsync("Jira", Rte);
        Assert.True(r.IsOk);
        Assert.Equal(("Jira", true), (r.Value!.Source, r.Value.Ok));
        Assert.Equal("GET /rest/api/3/myself", e.Http.Requests.Single());
        Assert.Equal("Ok", JsonDocument.Parse(e.Query("SELECT AfterJson FROM AuditEvents WHERE Action='Tested'")[0][0]!.ToString()!).RootElement.GetProperty("outcome").GetString());
    }

    [Theory]
    [InlineData(401, "refused the credentials")]
    [InlineData(503, "answered with HTTP 503")]
    public async Task A_failed_test_is_a_readable_422_and_raises_no_background_alert(int status, string words)
    {
        var e = NewEnv(fx); e.Http.Always(status);
        var r = await e.Service.TestAsync("ServiceNow", Rte);
        Assert.Equal(ConnectorGuards.ConnectorTestFailed, GuardOf(r));
        Assert.Contains(words, r.Failures[0].Message);
        Assert.DoesNotContain("service-now.com", r.Failures[0].Message);
        Assert.Equal(0, e.Count("SELECT COUNT(*) FROM SyncAlerts"));
        Assert.Equal(1, e.Count("SELECT COUNT(*) FROM AuditEvents WHERE Action='Tested'"));
    }

    [Fact]
    public async Task Test_without_a_base_url_or_credentials_says_what_is_missing()
    {
        var e = NewEnv(fx, serviceNow: false);
        Assert.Equal(ConnectorGuards.ConnectorNotConfigured, GuardOf(await e.Service.TestAsync("Jira", Rte)));
        await e.Service.SaveSettingsAsync("Jira", JiraUrl, null, Rte, null);
        var r = await e.Service.TestAsync("Jira", Rte);
        Assert.Equal(ConnectorGuards.ConnectorTestFailed, GuardOf(r));
        Assert.Contains("No Jira credentials", r.Failures[0].Message);
        Assert.Equal(0, e.Http.Count);
    }

    // ---- status words -----------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task The_list_reports_a_status_word_and_the_bookkeeping_for_both_connectors()
    {
        var e = NewEnv(fx, serviceNow: false);
        var l = await e.Service.ListAsync();
        Assert.Equal(["Jira", "ServiceNow"], l.Select(c => c.Source));
        Assert.All(l, c => Assert.Equal("NotConfigured", c.Status));

        await e.Service.SaveSettingsAsync("ServiceNow", SnUrl, null, Rte, null);
        Assert.Equal("NoCredentials", (await e.Service.ListAsync()).Single(c => c.Source == "ServiceNow").Status);
        await e.Service.SetCredentialsAsync("ServiceNow", "Basic", "u", "p", Rte);
        Assert.Equal("NeverSynced", (await e.Service.ListAsync()).Single(c => c.Source == "ServiceNow").Status);

        e.AddSnLink(); e.SnAnswers();
        await e.Poller.RunOnceAsync();
        var ok = (await e.Service.ListAsync()).Single(c => c.Source == "ServiceNow");
        Assert.Equal(("Ok", 0, 1, 0), (ok.Status, ok.ConsecutiveFailures, ok.LinkCount, ok.OpenAlerts));
        Assert.NotNull(ok.LastSuccessAt);

        e.Time.Advance(Interval); e.Http.Always(500);
        await e.Poller.RunOnceAsync();
        var bad = (await e.Service.ListAsync()).Single(c => c.Source == "ServiceNow");
        Assert.Equal(("Failing", 1, 1), (bad.Status, bad.ConsecutiveFailures, bad.OpenAlerts));

        var dog = e.NewWatchdog();
        e.Time.Advance(TimeSpan.FromMinutes(20));
        await dog.CheckOnceAsync();
        Assert.Equal("Stalled", (await e.Service.ListAsync()).Single(c => c.Source == "ServiceNow").Status);

        await e.Service.SaveSettingsAsync("ServiceNow", null, false, Rte, (await e.Service.ListAsync()).Single(c => c.Source == "ServiceNow").Version);
        Assert.Equal("Disabled", (await e.Service.ListAsync()).Single(c => c.Source == "ServiceNow").Status);
    }

    // ---- links -------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Links_are_added_normalised_audited_and_listed_per_train()
    {
        var e = NewEnv(fx);
        var r = await e.Service.AddLinkAsync("t1", "Train", "t1", "ServiceNow", " chg0030001 ", Rte);
        Assert.True(r.IsOk);
        Assert.Equal(("CHG0030001", "Unsynced", false, 1), (r.Value!.ExternalKey, r.Value.SyncState, r.Value.Stale, r.Value.Version));
        var p = await e.Service.AddLinkAsync("t1", "Product", "p1", "Jira", "pay/4.5.0", Rte);
        Assert.Equal("PAY/4.5.0", p.Value!.ExternalKey);

        var list = (await e.Service.ListLinksAsync("t1")).Value!;
        Assert.Equal(["Jira", "ServiceNow"], list.Select(l => l.SourceSystem));
        var a = e.Query("SELECT ActorUserId,ReleaseTrainId,Action FROM AuditEvents WHERE EntityType='ExternalLink'");
        Assert.Equal(2, a.Count);
        Assert.All(a, x => Assert.Equal(["rte", "t1", "Add"], x));
    }

    [Fact]
    public async Task Bad_links_are_refused_with_the_reason()
    {
        var e = NewEnv(fx);
        Assert.Equal("LinkInvalid", GuardOf(await e.Service.AddLinkAsync("t1", "Train", "t1", "ServiceNow", "INC0001", Rte)));
        Assert.Equal("LinkInvalid", GuardOf(await e.Service.AddLinkAsync("t1", "Train", "t1", "ServiceNow", "CHG0030001^ORactive=true", Rte)));
        Assert.Equal("LinkInvalid", GuardOf(await e.Service.AddLinkAsync("t1", "Train", "t1", "Jira", "https://evil/x", Rte)));
        Assert.Equal("LinkInvalid", GuardOf(await e.Service.AddLinkAsync("t1", "Train", "t1", "GitHub", "PAY-1", Rte)));
        Assert.Equal("LinkInvalid", GuardOf(await e.Service.AddLinkAsync("t1", "Widget", "t1", "Jira", "PAY-1", Rte)));
        Assert.Equal("LinkInvalid", GuardOf(await e.Service.AddLinkAsync("t1", "Train", "other", "Jira", "PAY-1", Rte)));       // a train link is to that train
        Assert.Equal("LinkInvalid", GuardOf(await e.Service.AddLinkAsync("t1", "Product", "nope", "Jira", "PAY-1", Rte)));      // not one of its products
        Assert.Equal(ResultKind.NotFound, (await e.Service.AddLinkAsync("zzz", "Train", "zzz", "Jira", "PAY-1", Rte)).Kind);
        Assert.Equal(0, e.Count("SELECT COUNT(*) FROM ExternalLinks"));

        Assert.True((await e.Service.AddLinkAsync("t1", "Train", "t1", "Jira", "PAY-1", Rte)).IsOk);
        Assert.Equal("LinkExists", GuardOf(await e.Service.AddLinkAsync("t1", "Train", "t1", "Jira", "pay-1", Rte)));
    }

    [Fact]
    public async Task Removing_a_link_is_audited_versioned_and_closes_its_open_alerts_keeping_them_as_history()
    {
        var e = NewEnv(fx); e.AddSnLink(); e.Http.Always(200, SnEmpty);
        await e.Poller.RunOnceAsync();                                   // NotFound alert for l1
        Assert.Equal(1, e.OpenAlerts("NotFound"));

        Assert.Equal(ResultKind.Conflict, (await e.Service.RemoveLinkAsync("l1", Rte, 99)).Kind);
        Assert.Equal(ResultKind.NotFound, (await e.Service.RemoveLinkAsync("nope", Rte, null)).Kind);
        var current = (int)e.Count("SELECT Version FROM ExternalLinks WHERE Id='l1'");
        Assert.True((await e.Service.RemoveLinkAsync("l1", Rte, current)).IsOk);
        Assert.Equal(0, e.Count("SELECT COUNT(*) FROM ExternalLinks"));
        Assert.Equal(0, e.OpenAlerts("NotFound"));
        Assert.Equal(1, e.Count("SELECT COUNT(*) FROM SyncAlerts WHERE Kind='NotFound' AND IsResolved=1"));   // history kept
        Assert.Equal(1, e.Count("SELECT COUNT(*) FROM AuditEvents WHERE EntityType='ExternalLink' AND Action='Remove'"));
    }

    [Fact]
    public async Task A_link_not_refreshed_for_three_intervals_is_stale_and_a_never_synced_one_is_not()
    {
        var e = NewEnv(fx); e.AddSnLink(); e.SnAnswers();
        Assert.False((await e.Service.ListLinksAsync("t1")).Value!.Single().Stale);      // never synced: "not yet synced"
        await e.Poller.RunOnceAsync();
        Assert.False((await e.Service.ListLinksAsync("t1")).Value!.Single().Stale);
        e.Time.Advance(TimeSpan.FromMinutes(14));
        Assert.False((await e.Service.ListLinksAsync("t1")).Value!.Single().Stale);
        e.Time.Advance(TimeSpan.FromMinutes(1));
        var l = (await e.Service.ListLinksAsync("t1")).Value!.Single();
        Assert.True(l.Stale);                                                            // 15 min = three intervals, with no error at all
        Assert.Equal("InSync", l.SyncState);

        await e.Poller.RunOnceAsync();
        Assert.False((await e.Service.ListLinksAsync("t1")).Value!.Single().Stale);
    }

    [Fact]
    public async Task While_a_window_has_been_open_the_stale_threshold_is_three_minutes()
    {
        var e = NewEnv(fx); e.AddSnLink(); e.SnAnswers();
        await e.Poller.RunOnceAsync();
        e.Sql("INSERT INTO DeploymentWindows(Id,ReleaseTrainId,StartsAt,EndsAt) VALUES('w','t1','2026-10-30T02:00:00Z','2026-10-30T09:00:00Z')");
        e.Time.Advance(TimeSpan.FromMinutes(3));
        Assert.True((await e.Service.ListLinksAsync("t1")).Value!.Single().Stale);
    }

    [Fact]
    public async Task Mismatch_warnings_are_listed_on_the_link_with_rule_and_since_and_clear_with_the_mismatch()
    {
        var e = NewEnv(fx);
        e.Sql("INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CurrentStatus,CreatedAt,UpdatedAt) VALUES('t2','Running','2026-10-30','Low','Executing','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z')");
        e.AddSnLink("m1", "CHG0030001", "t2"); e.SnAnswers("-2");
        await e.Poller.RunOnceAsync();
        e.Time.Advance(Interval);
        await e.Poller.RunOnceAsync();

        var l = (await e.Service.ListLinksAsync("t2")).Value!.Single();
        Assert.Equal("Mismatch", l.SyncState);
        var w = Assert.Single(l.Warnings);
        Assert.Equal(MismatchRules.ChgNotImplementing, w.Rule);
        Assert.Equal(new DateTime(2026, 10, 30, 2, 30, 0, DateTimeKind.Utc), w.Since);   // since the first cycle that saw it, not the latest
        Assert.Contains("should be in Implement", w.Message);
        Assert.Empty((await e.Service.ListLinksAsync("t1")).Value!);                     // per train

        e.SnAnswers("-1"); e.Time.Advance(Interval);
        await e.Poller.RunOnceAsync();
        Assert.Empty((await e.Service.ListLinksAsync("t2")).Value!.Single().Warnings);
    }
}
