using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Infrastructure.Sync;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// REOS-39/40/41 over HTTP: roles, write-only credentials (never in a response, never in SQLite), test connection, ExternalLinks management, "sync now",
/// and the acceptance that a mismatch never blocks Gated to Executing. The ITSM is a recorded fake behind the named HTTP client; no socket is opened.
/// </summary>
public class ConnectorTests
{

    /// <summary>Reads a file another process (the running app: SQLite, Serilog) still has open. Windows refuses File.ReadAllBytes then; Linux and macOS do not.</summary>
    private static byte[] ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    private const string Secret = "Api-Secret-Token-77aa", SnUrl = "https://acme.service-now.com";

    private sealed class Fake : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new(HttpStatusCode.OK) { Content = new StringContent("{}") };
        public int Count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Interlocked.Increment(ref Count); return Task.FromResult(Respond(request)); }
        public void Always(int status, string body = "{}") => Respond = _ => new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private static string Sn(string state, string start = "2026-10-30 02:00:00", string end = "2026-10-30 06:00:00") =>
        $$"""{"result":[{"number":"CHG0030001","state":"{{state}}","start_date":"{{start}}","end_date":"{{end}}","short_description":"card 4111111111111111"}]}""";

    private sealed record Ctx(ApiFactory F, WebApplicationFactory<Program> Host, Fake Http, HttpClient Rte, HttpClient Rm, HttpClient Viewer, HttpClient Anon) : IDisposable
    {
        public void Dispose() { Host.Dispose(); F.Dispose(); }
        public string V(string trainId = "t1") => Scalar(F, $"SELECT Version FROM ReleaseTrains WHERE Id='{trainId}'");
    }

    /// <summary>The application host with the connector HTTP client replaced by a fake and the background timers parked (tests drive cycles through :sync).</summary>
    private static async Task<Ctx> Setup(bool requireIfMatch = false)
    {
        var f = new ApiFactory(requireIfMatch: requireIfMatch);
        var fake = new Fake();
        var host = f.WithWebHostBuilder(b =>
        {
            b.UseSetting("Sync:StartDelaySeconds", "3600");
            b.UseSetting("Sync:WatchdogSeconds", "3600");
            b.ConfigureTestServices(s => s.AddHttpClient(ConnectorHttp.ClientName).ConfigurePrimaryHttpMessageHandler(() => fake));
        });
        async Task<HttpClient> As(string role, string email)
        {
            var c = host.CreateClient();
            (await c.PostAsJsonAsync("/auth/dev-login", new { email, name = email.Split('@')[0], role })).EnsureSuccessStatusCode();
            return c;
        }
        return new(f, host, fake, await As(Roles.RTE, "rte@x.com"), await As(Roles.ReleaseManager, "rm@x.com"), await As(Roles.Viewer, "v@x.com"), host.CreateClient());
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private static async Task<HttpResponseMessage> Send(HttpClient c, HttpMethod m, string url, object? body = null, string? ifMatch = null)
    {
        var req = new HttpRequestMessage(m, url) { Content = body is null ? null : JsonContent.Create(body) };
        if (ifMatch is not null) req.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return await c.SendAsync(req);
    }

    private static async Task<string> Guard(HttpResponseMessage r)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        return (await Json(r)).GetProperty("guard").GetString()!;
    }

    private static async Task Configure(Ctx c, string state = "-1")
    {
        Assert.Equal(HttpStatusCode.OK, (await Send(c.Rte, HttpMethod.Put, "/api/v1/connectors/ServiceNow", new { baseUrl = SnUrl })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(c.Rte, HttpMethod.Put, "/api/v1/connectors/ServiceNow/credentials", new { kind = "Basic", username = "svc-release", secret = Secret })).StatusCode);
        c.Http.Always(200, Sn(state));
    }

    private static void SeedTrain(Ctx c) => Sql(c.F, "INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CreatedAt,UpdatedAt) VALUES('t1','R26.11','2026-11-30','Low','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z');");

    // ---- roles ---------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Everyone_signed_in_can_read_but_only_an_RTE_or_Release_Manager_can_write_and_anonymous_is_refused()
    {
        using var c = await Setup(); SeedTrain(c);
        Assert.Equal(HttpStatusCode.OK, (await c.Viewer.GetAsync("/api/v1/connectors")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.Viewer.GetAsync("/api/v1/trains/t1/links")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.Anon.GetAsync("/api/v1/connectors")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.Anon.GetAsync("/api/v1/trains/t1/links")).StatusCode);

        var writes = new (HttpMethod M, string Url, object? Body)[]
        {
            (HttpMethod.Put, "/api/v1/connectors/Jira", new { baseUrl = "https://acme.atlassian.net" }),
            (HttpMethod.Put, "/api/v1/connectors/Jira/credentials", new { kind = "ApiToken", username = "u@x.com", secret = "s" }),
            (HttpMethod.Delete, "/api/v1/connectors/Jira/credentials", null),
            (HttpMethod.Post, "/api/v1/connectors/Jira:test", null),
            (HttpMethod.Post, "/api/v1/connectors/Jira:sync", null),
            (HttpMethod.Post, "/api/v1/trains/t1/links", new { entityType = "Train", entityId = "t1", sourceSystem = "Jira", externalKey = "PAY-1" }),
            (HttpMethod.Delete, "/api/v1/links/x", null),
        };
        foreach (var (m, url, body) in writes)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await Send(c.Viewer, m, url, body ?? new { })).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(c.Anon, m, url, body ?? new { })).StatusCode);
        }
        Assert.Equal(0, long.Parse(Scalar(c.F, "SELECT COUNT(*) FROM ConnectorState")));
        Assert.Equal(HttpStatusCode.OK, (await Send(c.Rm, HttpMethod.Put, "/api/v1/connectors/Jira", new { baseUrl = "https://acme.atlassian.net" })).StatusCode);   // RM is a superset of RTE (D32)
    }

    // ---- configuration ---------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task The_list_shows_both_connectors_and_the_status_after_setup_without_ever_returning_a_secret()
    {
        using var c = await Setup();
        var start = await Json(await c.Rte.GetAsync("/api/v1/connectors"));
        Assert.Equal(["Jira", "ServiceNow"], start.EnumerateArray().Select(x => x.GetProperty("source").GetString()!));
        Assert.All(start.EnumerateArray(), x => Assert.Equal("NotConfigured", x.GetProperty("status").GetString()));

        var put = await Send(c.Rte, HttpMethod.Put, "/api/v1/connectors/ServiceNow", new { baseUrl = SnUrl });
        var made = await Json(put);
        Assert.Equal((SnUrl, true, 1, "NoCredentials"), (made.GetProperty("baseUrl").GetString(), made.GetProperty("isEnabled").GetBoolean(), made.GetProperty("version").GetInt32(), made.GetProperty("status").GetString()));

        var cred = await Send(c.Rte, HttpMethod.Put, "/api/v1/connectors/ServiceNow/credentials", new { kind = "OAuth", username = "client-id-123", secret = Secret });
        var body = await cred.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, cred.StatusCode);
        Assert.DoesNotContain(Secret, body); Assert.DoesNotContain("client-id-123", body);
        var view = JsonDocument.Parse(body).RootElement;
        Assert.Equal(("OAuth", true, "NeverSynced"), (view.GetProperty("credentialKind").GetString(), view.GetProperty("hasCredentials").GetBoolean(), view.GetProperty("status").GetString()));

        var list = await (await c.Viewer.GetAsync("/api/v1/connectors")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(Secret, list); Assert.DoesNotContain("client-id-123", list);
        Assert.DoesNotContain("secret", list.Replace("hasCredentials", ""), StringComparison.OrdinalIgnoreCase);   // no field is even named for it
    }

    [Fact]
    public async Task Settings_edits_use_if_match_and_the_first_save_needs_none()
    {
        using var c = await Setup(requireIfMatch: true);
        Assert.Equal(HttpStatusCode.OK, (await Send(c.Rte, HttpMethod.Put, "/api/v1/connectors/Jira", new { baseUrl = "https://acme.atlassian.net" })).StatusCode);   // creates
        Assert.Equal((HttpStatusCode)428, (await Send(c.Rte, HttpMethod.Put, "/api/v1/connectors/Jira", new { isEnabled = false })).StatusCode);
        var stale = await Send(c.Rte, HttpMethod.Put, "/api/v1/connectors/Jira", new { isEnabled = false }, "9");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(1, (await Json(stale)).GetProperty("current").GetProperty("version").GetInt32());
        var ok = await Send(c.Rte, HttpMethod.Put, "/api/v1/connectors/Jira", new { isEnabled = false }, "\"1\"");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal((false, 2, "Disabled"), ((await Json(ok)).GetProperty("isEnabled").GetBoolean(), (await Json(ok)).GetProperty("version").GetInt32(), (await Json(ok)).GetProperty("status").GetString()));
    }

    [Fact]
    public async Task Unsafe_settings_and_credentials_are_422_with_the_reason_and_unknown_connectors_404()
    {
        using var c = await Setup();
        Assert.Equal("ConnectorInvalid", await Guard(await Send(c.Rte, HttpMethod.Put, "/api/v1/connectors/Jira", new { baseUrl = "https://169.254.169.254" })));   // a metadata address, even in Development
        Assert.Equal("ConnectorInvalid", await Guard(await Send(c.Rte, HttpMethod.Put, "/api/v1/connectors/Jira", new { baseUrl = "https://u:p@acme.atlassian.net" })));
        Assert.Equal("CredentialsInvalid", await Guard(await Send(c.Rte, HttpMethod.Put, "/api/v1/connectors/Jira/credentials", new { kind = "Basic", username = "u", secret = "s" })));
        Assert.Equal("CredentialsInvalid", await Guard(await Send(c.Rte, HttpMethod.Put, "/api/v1/connectors/Jira/credentials", new { kind = "ApiToken", username = "", secret = "s" })));
        Assert.Equal(HttpStatusCode.NotFound, (await Send(c.Rte, HttpMethod.Put, "/api/v1/connectors/Bitbucket", new { baseUrl = SnUrl })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(c.Rte, HttpMethod.Post, "/api/v1/connectors/Bitbucket:test")).StatusCode);
    }

    [Fact]
    public async Task Credentials_live_in_the_data_protection_store_and_never_in_the_database_file()
    {
        using var c = await Setup(); SeedTrain(c);
        await Configure(c);
        await Send(c.Rte, HttpMethod.Post, "/api/v1/trains/t1/links", new { entityType = "Train", entityId = "t1", sourceSystem = "ServiceNow", externalKey = "CHG0030001" });
        await Send(c.Rte, HttpMethod.Post, "/api/v1/connectors/ServiceNow:test");
        c.Http.Always(401);
        await Send(c.Rte, HttpMethod.Post, "/api/v1/connectors/ServiceNow:sync");

        var dir = Path.GetDirectoryName(c.F.DbPath)!;
        var cred = Path.Combine(dir, "secrets", "ServiceNow.cred");
        Assert.True(File.Exists(cred));
        Assert.False(Contains(ReadShared(cred), Secret));
        Assert.True(Directory.GetFiles(Path.Combine(dir, "keys")).Length > 0);   // the key ring is on disk beside the database, and is what has to be backed up
        foreach (var f in Directory.GetFiles(dir, "app.db*"))
        {
            var bytes = ReadShared(f);
            Assert.False(Contains(bytes, Secret), $"{Path.GetFileName(f)} holds the secret");
            Assert.False(Contains(bytes, "svc-release"), $"{Path.GetFileName(f)} holds the user name");
        }

        var cleared = await Send(c.Rte, HttpMethod.Delete, "/api/v1/connectors/ServiceNow/credentials");
        Assert.False((await Json(cleared)).GetProperty("hasCredentials").GetBoolean());
        Assert.False(File.Exists(cred));
    }

    private static bool Contains(byte[] haystack, string needle) => haystack.AsSpan().IndexOf(Encoding.UTF8.GetBytes(needle)) >= 0;

    // ---- test connection ---------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Test_connection_reports_success_or_a_readable_422()
    {
        using var c = await Setup();
        Assert.Equal("ConnectorNotConfigured", await Guard(await Send(c.Rte, HttpMethod.Post, "/api/v1/connectors/ServiceNow:test")));
        await Configure(c);

        var ok = await Send(c.Rte, HttpMethod.Post, "/api/v1/connectors/ServiceNow:test");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.True((await Json(ok)).GetProperty("ok").GetBoolean());

        c.Http.Always(401);
        var bad = await Send(c.Rte, HttpMethod.Post, "/api/v1/connectors/ServiceNow:test");
        Assert.Equal("ConnectorTestFailed", await Guard(bad));
        Assert.Contains("refused the credentials", (await Json(bad)).GetProperty("message").GetString());
        Assert.Equal("0", Scalar(c.F, "SELECT COUNT(*) FROM SyncAlerts"));   // a manual test is not a background failure
    }

    // ---- links ---------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Links_are_added_listed_versioned_and_removed_per_train()
    {
        using var c = await Setup(); SeedTrain(c);
        var add = await Send(c.Rte, HttpMethod.Post, "/api/v1/trains/t1/links", new { entityType = "Train", entityId = "t1", sourceSystem = "ServiceNow", externalKey = "chg0030001" });
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        var link = await Json(add);
        Assert.Equal(("CHG0030001", "Unsynced", false, 1), (link.GetProperty("externalKey").GetString(), link.GetProperty("syncState").GetString(), link.GetProperty("stale").GetBoolean(), link.GetProperty("version").GetInt32()));

        Assert.Equal("LinkExists", await Guard(await Send(c.Rte, HttpMethod.Post, "/api/v1/trains/t1/links", new { entityType = "Train", entityId = "t1", sourceSystem = "ServiceNow", externalKey = "CHG0030001" })));
        Assert.Equal("LinkInvalid", await Guard(await Send(c.Rte, HttpMethod.Post, "/api/v1/trains/t1/links", new { entityType = "Train", entityId = "t1", sourceSystem = "ServiceNow", externalKey = "INC1" })));
        Assert.Equal(HttpStatusCode.NotFound, (await Send(c.Rte, HttpMethod.Post, "/api/v1/trains/nope/links", new { entityType = "Train", entityId = "nope", sourceSystem = "Jira", externalKey = "PAY-1" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.Viewer.GetAsync("/api/v1/trains/nope/links")).StatusCode);

        var list = await Json(await c.Viewer.GetAsync("/api/v1/trains/t1/links"));
        Assert.Single(list.EnumerateArray());
        var id = link.GetProperty("id").GetString()!;
        var stale = await Send(c.Rte, HttpMethod.Delete, $"/api/v1/links/{id}", ifMatch: "9");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(c.Rte, HttpMethod.Delete, $"/api/v1/links/{id}", ifMatch: "1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(c.Rte, HttpMethod.Delete, $"/api/v1/links/{id}", ifMatch: "1")).StatusCode);
        Assert.Empty((await Json(await c.Viewer.GetAsync("/api/v1/trains/t1/links"))).EnumerateArray());
        Assert.Equal("2", Scalar(c.F, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='ExternalLink'"));   // Add and Remove
    }

    // ---- sync now and the engine wiring ---------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Sync_now_runs_one_cycle_and_the_link_shows_the_source_state()
    {
        using var c = await Setup(); SeedTrain(c);
        await Configure(c, "-2");
        await Send(c.Rte, HttpMethod.Post, "/api/v1/trains/t1/links", new { entityType = "Train", entityId = "t1", sourceSystem = "ServiceNow", externalKey = "CHG0030001" });

        var run = await Json(await Send(c.Rte, HttpMethod.Post, "/api/v1/connectors/ServiceNow:sync"));
        Assert.Equal(("Clean", 1), (run.GetProperty("outcome").GetString(), run.GetProperty("linksChecked").GetInt32()));
        var l = (await Json(await c.Viewer.GetAsync("/api/v1/trains/t1/links"))).EnumerateArray().Single();
        Assert.Equal(("InSync", "Scheduled"), (l.GetProperty("syncState").GetString(), l.GetProperty("lastSyncedStatus").GetString()));
        Assert.NotEqual(JsonValueKind.Null, l.GetProperty("lastSyncedAt").ValueKind);

        var conn = (await Json(await c.Viewer.GetAsync("/api/v1/connectors"))).EnumerateArray().Single(x => x.GetProperty("source").GetString() == "ServiceNow");
        Assert.Equal(("Ok", 1, 0), (conn.GetProperty("status").GetString(), conn.GetProperty("linkCount").GetInt32(), conn.GetProperty("consecutiveFailures").GetInt32()));

        c.Http.Always(401);
        await Send(c.Rte, HttpMethod.Post, "/api/v1/connectors/ServiceNow:sync");
        Assert.Equal("1", Scalar(c.F, "SELECT COUNT(*) FROM SyncAlerts WHERE Kind='AuthFailed' AND IsResolved=0 AND SourceSystem='ServiceNow'"));
        var failing = (await Json(await c.Viewer.GetAsync("/api/v1/connectors"))).EnumerateArray().Single(x => x.GetProperty("source").GetString() == "ServiceNow");
        Assert.Equal(("Failing", 1), (failing.GetProperty("status").GetString(), failing.GetProperty("openAlerts").GetInt32()));
    }

    [Fact]
    public async Task The_poller_and_watchdog_are_registered_as_hosted_services()
    {
        using var c = await Setup();
        var hosted = c.Host.Services.GetServices<IHostedService>().Select(h => h.GetType()).ToList();
        Assert.Contains(typeof(SyncPollerService), hosted);
        Assert.Contains(typeof(SyncWatchdogService), hosted);
    }

    // ---- REOS-41 acceptance: a mismatch never blocks Gated to Executing ----------------------------------------------------------------------
    [Fact]
    public async Task A_mismatch_is_shown_as_a_warning_and_never_blocks_Gated_to_Executing()
    {
        using var c = await Setup(); SeedTrain(c);
        await Configure(c, "-2");                                                  // the change is Scheduled, planned 02:00-06:00
        await Send(c.Rte, HttpMethod.Post, "/api/v1/trains/t1/links", new { entityType = "Train", entityId = "t1", sourceSystem = "ServiceNow", externalKey = "CHG0030001" });
        Sql(c.F, "INSERT INTO DeploymentWindows(Id,ReleaseTrainId,StartsAt,EndsAt) VALUES('w1','t1','2026-10-30T03:00:00Z','2026-10-30T07:00:00Z')");   // the train's window differs

        Assert.Equal(HttpStatusCode.OK, (await Send(c.Rte, HttpMethod.Post, "/api/v1/trains/t1:advance", new { to = "Gated" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(c.Rte, HttpMethod.Post, "/api/v1/trains/t1:capture-baseline")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(c.Rm, HttpMethod.Post, "/api/v1/trains/t1/gonogo", new { decision = "Go" })).StatusCode);

        await Send(c.Rte, HttpMethod.Post, "/api/v1/connectors/ServiceNow:sync");
        var link = (await Json(await c.Rte.GetAsync("/api/v1/trains/t1/links"))).EnumerateArray().Single();
        Assert.Equal("Mismatch", link.GetProperty("syncState").GetString());
        var warning = link.GetProperty("warnings").EnumerateArray().Single();
        Assert.Equal("ChgWindowDiffers", warning.GetProperty("rule").GetString());
        Assert.Equal("1", Scalar(c.F, "SELECT COUNT(*) FROM SyncAlerts WHERE Kind='Mismatch' AND IsResolved=0"));

        var readiness = await Json(await c.Rte.GetAsync("/api/v1/trains/t1/readiness"));
        Assert.True(readiness.GetProperty("ready").GetBoolean());                  // the readiness line does not list it either
        var advance = await Send(c.Rte, HttpMethod.Post, "/api/v1/trains/t1:advance", new { to = "Executing" }, c.V());
        Assert.Equal(HttpStatusCode.OK, advance.StatusCode);
        Assert.Equal("Executing", Scalar(c.F, "SELECT CurrentStatus FROM ReleaseTrains WHERE Id='t1'"));

        // now Executing with the change still Scheduled: a second warning, still no effect on the train
        await Send(c.Rte, HttpMethod.Post, "/api/v1/connectors/ServiceNow:sync");
        var rules = (await Json(await c.Rte.GetAsync("/api/v1/trains/t1/links"))).EnumerateArray().Single().GetProperty("warnings").EnumerateArray().Select(w => w.GetProperty("rule").GetString()!).Order().ToArray();
        Assert.Equal(["ChgNotImplementing", "ChgWindowDiffers"], rules);
        Assert.Equal("Executing", Scalar(c.F, "SELECT CurrentStatus FROM ReleaseTrains WHERE Id='t1'"));
    }

    [Fact]
    public async Task Even_an_unreachable_or_failing_connector_does_not_stop_a_train_advancing()
    {
        using var c = await Setup(); SeedTrain(c);
        await Configure(c);
        await Send(c.Rte, HttpMethod.Post, "/api/v1/trains/t1/links", new { entityType = "Train", entityId = "t1", sourceSystem = "ServiceNow", externalKey = "CHG0030001" });
        c.Http.Always(503);
        await Send(c.Rte, HttpMethod.Post, "/api/v1/connectors/ServiceNow:sync");
        Assert.Equal("1", Scalar(c.F, "SELECT COUNT(*) FROM SyncAlerts WHERE Kind='Unreachable'"));
        Assert.Equal(HttpStatusCode.OK, (await Send(c.Rte, HttpMethod.Post, "/api/v1/trains/t1:advance", new { to = "Gated" })).StatusCode);
    }
}
