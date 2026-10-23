using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using ReleaseMgmt.Domain.Common;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>REOS-110: API keys (hashed, shown once, revocable), the push ingest API (create or update a deployment, stream events), its idempotency, refusals, rate limits, workspace scope and live hints.</summary>
public class IngestApiTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
    private static async Task<string> Guard(HttpResponseMessage r, HttpStatusCode status = HttpStatusCode.UnprocessableEntity) { Assert.Equal(status, r.StatusCode); return (await Json(r)).GetProperty("guard").GetString()!; }
    private static string Target(int days = 60) => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(days).ToString("yyyy-MM-dd");
    private static long Count(ApiFactory f, string sql) => long.Parse(Scalar(f, sql));
    private static long Audits(ApiFactory f, string type, string action) => Count(f, $"SELECT COUNT(*) FROM AuditEvents WHERE EntityType='{type}' AND Action='{action}'");

    /// <summary>An RTE client, a Production environment, the payments-api service, and an API key made through the admin endpoint.</summary>
    private static async Task<(HttpClient Rte, HttpClient Ingest, string Secret)> Setup(ApiFactory f)
    {
        var rte = await As(f, Roles.RTE, "rte@x.com");
        Assert.Equal(HttpStatusCode.OK, (await rte.PostAsJsonAsync("/api/v1/environments", new { name = "Production", kind = "Production" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rte.PostAsJsonAsync("/api/v1/services", new { serviceKey = "payments-api", name = "Payments API" })).StatusCode);
        var made = await rte.PostAsJsonAsync("/api/v1/ingest-keys", new { name = "CI payments" });
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        var secret = (await Json(made)).GetProperty("secret").GetString()!;
        return (rte, IngestTestKit.Client(f, secret), secret);
    }

    private static object Dep(string ext = "ci-100", object? extra = null)
    {
        var d = new Dictionary<string, object?> { ["externalId"] = ext, ["serviceKey"] = "payments-api", ["environment"] = "Production", ["releaseVersion"] = "4.5.0" };
        if (extra is not null) foreach (var p in extra.GetType().GetProperties()) d[char.ToLowerInvariant(p.Name[0]) + p.Name[1..]] = p.GetValue(extra);
        return d;
    }

    [Fact]
    public async Task A_key_is_shown_once_and_only_its_hash_is_stored()
    {
        using var f = new ApiFactory();
        var (rte, _, secret) = await Setup(f);
        Assert.StartsWith("rk_", secret);
        Assert.True(secret.Length >= 40);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
        Assert.Equal(hash, Scalar(f, "SELECT KeySha256 FROM IngestKeys"));
        Assert.Equal(secret[..8], Scalar(f, "SELECT KeyPrefix FROM IngestKeys"));
        // not in any other column, nor in the audit trail, nor in the list the admin screen reads
        Assert.Equal(0, Count(f, $"SELECT COUNT(*) FROM IngestKeys WHERE Name='{secret}' OR KeyPrefix='{secret}'"));
        Assert.Equal(0, Count(f, $"SELECT COUNT(*) FROM AuditEvents WHERE AfterJson LIKE '%{secret}%' OR BeforeJson LIKE '%{secret}%' OR AfterJson LIKE '%{hash}%'"));
        var list = await rte.GetStringAsync("/api/v1/ingest-keys");
        Assert.DoesNotContain(secret, list); Assert.DoesNotContain(hash, list); Assert.DoesNotContain("\"secret\"", list);
        Assert.Contains("CI payments", list);
        Assert.Equal(1, Audits(f, "IngestKey", "Create"));
        // only Admin (RTE, Release Manager) creates or lists keys
        foreach (var role in new[] { Roles.Viewer, Roles.GovernanceOfficer })
        {
            var c = await As(f, role, $"{role}@x.com");
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/v1/ingest-keys", new { name = "x" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/v1/ingest-keys")).StatusCode);
        }
        Assert.Equal("IngestKeyInvalid", await Guard(await rte.PostAsJsonAsync("/api/v1/ingest-keys", new { name = "  " })));
    }

    [Fact]
    public async Task Revoking_a_key_stops_it_at_once_and_is_audited()
    {
        using var f = new ApiFactory();
        var (rte, ingest, _) = await Setup(f);
        Assert.Equal(HttpStatusCode.Created, (await ingest.PostAsJsonAsync("/api/v1/ingest/deployments", Dep())).StatusCode);
        var key = (await rte.GetFromJsonAsync<JsonElement>("/api/v1/ingest-keys")).EnumerateArray().Single();
        Assert.NotEqual(JsonValueKind.Null, key.GetProperty("lastUsedAt").ValueKind);   // first use is stamped
        var id = key.GetProperty("id").GetString()!;
        using var stale = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/ingest-keys/{id}:revoke");
        stale.Headers.IfMatch.Add(new EntityTagHeaderValue("\"9\""));
        Assert.Equal(HttpStatusCode.Conflict, (await rte.SendAsync(stale)).StatusCode);
        var revoked = await rte.PostAsync($"/api/v1/ingest-keys/{id}:revoke", null);
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        Assert.NotEqual(JsonValueKind.Null, (await Json(revoked)).GetProperty("revokedAt").ValueKind);
        Assert.Equal("InvalidIngestKey", await Guard(await ingest.PostAsJsonAsync("/api/v1/ingest/deployments", Dep("ci-101")), HttpStatusCode.Unauthorized));
        Assert.Equal(0, Count(f, "SELECT COUNT(*) FROM Deployments WHERE ExternalId='ci-101'"));
        await rte.PostAsync($"/api/v1/ingest-keys/{id}:revoke", null);   // a repeat keeps who revoked it
        Assert.Equal(1, Audits(f, "IngestKey", "Revoke"));
    }

    [Fact]
    public async Task A_scripted_job_creates_a_deployment_moves_it_through_its_states_and_streams_events()
    {
        using var f = new ApiFactory();
        var (rte, ingest, _) = await Setup(f);
        var train = (await Json(await rte.PostAsJsonAsync("/api/v1/trains", new { title = "R27.01", targetReleaseDate = Target(), riskTier = "Low",
            products = new[] { new { productName = "Payments API", versionTag = "4.5.0", projectCode = "PAY", serviceKey = "payments-api" } } }))).GetProperty("id").GetString()!;
        var key = (await Json(await rte.GetAsync($"/api/v1/trains/{train}/release"))).GetProperty("key").GetString()!;

        var created = await ingest.PostAsJsonAsync("/api/v1/ingest/deployments", Dep("ci-100", new { Strategy = "Canary", CanarySteps = new[] { 5, 25, 50, 100 }, ReleaseKey = key, DesiredPods = 12 }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var c = await Json(created);
        Assert.True(c.GetProperty("created").GetBoolean());
        Assert.Equal("Queued", c.GetProperty("state").GetString());
        var id = c.GetProperty("id").GetString()!;

        async Task<JsonElement> Move(object body) { var r = await ingest.PostAsJsonAsync("/api/v1/ingest/deployments", body); Assert.Equal(HttpStatusCode.OK, r.StatusCode); return await Json(r); }
        Assert.Equal("Running", (await Move(new { externalId = "ci-100", state = "Running", trafficPercent = 5, readyPods = 1 })).GetProperty("state").GetString());
        await Move(new { externalId = "ci-100", trafficPercent = 50, readyPods = 6 });
        var ev = await ingest.PostAsJsonAsync("/api/v1/ingest/events", new { deploymentExternalId = "ci-100", events = new object[]
        {
            new { level = "Info", message = "Canary at 5 %", externalId = "e-1" }, new { level = "Warning", message = "p95 latency up 12 %", externalId = "e-2" }, new { message = "Canary at 50 %", externalId = "e-3" },
        } });
        Assert.Equal(HttpStatusCode.OK, ev.StatusCode);
        Assert.Equal(3, (await Json(ev)).GetProperty("accepted").GetInt32());
        await Move(new { externalId = "ci-100", state = "Paused" });
        await Move(new { externalId = "ci-100", state = "Running" });
        var done = await Move(new { externalId = "ci-100", state = "Successful", readyPods = 12 });
        Assert.Equal("Successful", done.GetProperty("state").GetString());

        var d = await Json(await rte.GetAsync($"/api/v1/deployments/{id}"));
        var dep = d.GetProperty("deployment");
        Assert.Equal("Successful", dep.GetProperty("state").GetString());
        Assert.Equal(100, dep.GetProperty("trafficPercent").GetDouble());   // a deployment that succeeds without a stated traffic is at 100 %
        Assert.Equal("Canary", dep.GetProperty("strategy").GetString());
        Assert.Equal(new[] { 5.0, 25, 50, 100 }, dep.GetProperty("canarySteps").EnumerateArray().Select(x => x.GetDouble()).ToArray());
        Assert.Equal(key, dep.GetProperty("releaseKey").GetString());
        Assert.Equal(12, dep.GetProperty("readyPods").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, dep.GetProperty("startedAt").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, dep.GetProperty("finishedAt").ValueKind);
        Assert.Equal(new[] { "Canary at 5 %", "p95 latency up 12 %", "Canary at 50 %" }, d.GetProperty("events").EnumerateArray().Select(x => x.GetProperty("message").GetString()).ToArray().Take(3).ToArray());
        Assert.Contains("Warning", d.GetProperty("events").ToString());

        // The release shows the service in the environment as Successful
        var state = await Json(await rte.GetAsync($"/api/v1/trains/{train}/environments"));
        Assert.Equal("Successful", state.GetProperty("services")[0].GetProperty("cells")[0].GetProperty("state").GetString());
        Assert.Single((await rte.GetFromJsonAsync<JsonElement>($"/api/v1/trains/{train}/deployments")).EnumerateArray());
        Assert.Single((await rte.GetFromJsonAsync<JsonElement>("/api/v1/deployments?state=Successful")).EnumerateArray());
        Assert.Empty((await rte.GetFromJsonAsync<JsonElement>("/api/v1/deployments?state=Failed")).EnumerateArray());

        // Every write: one audit row in the same transaction, system actor, the key named; the Version moved with each change
        Assert.Equal(1, Audits(f, "Deployment", "Create"));
        Assert.True(Audits(f, "Deployment", "State") >= 4 && Audits(f, "DeploymentEvent", "Append") == 1);
        Assert.Contains("CI payments", Scalar(f, "SELECT AfterJson FROM AuditEvents WHERE EntityType='Deployment' AND Action='Create'"));
        Assert.Equal(0, Count(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType IN ('Deployment','DeploymentEvent') AND ActorUserId IS NOT NULL"));
        Assert.True(Count(f, $"SELECT Version FROM Deployments WHERE Id='{id}'") >= 7);
    }

    [Fact]
    public async Task A_replay_changes_nothing_and_writes_nothing()
    {
        using var f = new ApiFactory();
        var (_, ingest, _) = await Setup(f);
        var first = await ingest.PostAsJsonAsync("/api/v1/ingest/deployments", Dep("ci-1", new { State = "Running", TrafficPercent = 10 }));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var a = await Json(first);
        var audits = Count(f, "SELECT COUNT(*) FROM AuditEvents");
        var replay = await ingest.PostAsJsonAsync("/api/v1/ingest/deployments", Dep("ci-1", new { State = "Running", TrafficPercent = 10 }));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var b = await Json(replay);
        Assert.False(b.GetProperty("created").GetBoolean()); Assert.False(b.GetProperty("changed").GetBoolean());
        Assert.Equal(a.GetProperty("id").GetString(), b.GetProperty("id").GetString());
        Assert.Equal(a.GetProperty("version").GetInt32(), b.GetProperty("version").GetInt32());
        Assert.Equal(audits, Count(f, "SELECT COUNT(*) FROM AuditEvents"));
        Assert.Equal(1, Count(f, "SELECT COUNT(*) FROM Deployments"));

        var events = new { deploymentExternalId = "ci-1", events = new object[] { new { message = "one", externalId = "x-1" }, new { message = "two", externalId = "x-2" } } };
        Assert.Equal(2, (await Json(await ingest.PostAsJsonAsync("/api/v1/ingest/events", events))).GetProperty("accepted").GetInt32());
        var version = Count(f, "SELECT Version FROM Deployments"); audits = Count(f, "SELECT COUNT(*) FROM AuditEvents");
        var again = await Json(await ingest.PostAsJsonAsync("/api/v1/ingest/events", events));
        Assert.Equal(0, again.GetProperty("accepted").GetInt32()); Assert.Equal(2, again.GetProperty("duplicates").GetInt32());
        Assert.Equal(2, Count(f, "SELECT COUNT(*) FROM DeploymentEvents"));
        Assert.Equal(version, Count(f, "SELECT Version FROM Deployments")); Assert.Equal(audits, Count(f, "SELECT COUNT(*) FROM AuditEvents"));
        // a partly new batch adds only what is new
        var mixed = await Json(await ingest.PostAsJsonAsync("/api/v1/ingest/events", new { deploymentExternalId = "ci-1", events = new object[] { new { message = "two", externalId = "x-2" }, new { message = "three", externalId = "x-3" }, new { message = "three", externalId = "x-3" } } }));
        Assert.Equal(1, mixed.GetProperty("accepted").GetInt32()); Assert.Equal(2, mixed.GetProperty("duplicates").GetInt32());
    }

    [Fact]
    public async Task Bad_pushes_are_readable_refusals_that_write_nothing()
    {
        using var f = new ApiFactory();
        var (_, ingest, _) = await Setup(f);
        Assert.Equal(HttpStatusCode.Created, (await ingest.PostAsJsonAsync("/api/v1/ingest/deployments", Dep("ci-1"))).StatusCode);
        var before = Count(f, "SELECT COUNT(*) FROM AuditEvents");
        async Task<string> G(string path, object body) => await Guard(await ingest.PostAsJsonAsync(path, body));

        Assert.Equal("DeploymentInvalid", await G("/api/v1/ingest/deployments", new { }));                                                       // no externalId
        Assert.Equal("DeploymentInvalid", await G("/api/v1/ingest/deployments", new { externalId = "new-1" }));                                  // new, but no service, environment or version
        Assert.Equal("UnknownService", await G("/api/v1/ingest/deployments", Dep("new-2", new { ServiceKey = "nope" })));
        Assert.Equal("UnknownEnvironment", await G("/api/v1/ingest/deployments", Dep("new-3", new { Environment = "Nowhere" })));
        Assert.Equal("UnknownRelease", await G("/api/v1/ingest/deployments", Dep("new-4", new { ReleaseKey = "RLS-99" })));
        Assert.Equal("DeploymentInvalid", await G("/api/v1/ingest/deployments", Dep("new-5", new { State = "Exploded" })));
        Assert.Equal("DeploymentInvalid", await G("/api/v1/ingest/deployments", Dep("new-6", new { Strategy = "BlueGreen" })));
        Assert.Equal("DeploymentInvalid", await G("/api/v1/ingest/deployments", Dep("new-7", new { Strategy = "Rolling", CanarySteps = new[] { 5, 25 } })));
        Assert.Equal("DeploymentInvalid", await G("/api/v1/ingest/deployments", Dep("new-8", new { Strategy = "Canary", CanarySteps = new[] { 50, 25 } })));   // steps rise
        Assert.Equal("DeploymentInvalid", await G("/api/v1/ingest/deployments", Dep("new-9", new { TrafficPercent = 101 })));
        Assert.Equal("DeploymentInvalid", await G("/api/v1/ingest/deployments", Dep("new-10", new { ReadyPods = -1 })));
        // an existing deployment keeps its identity, and its states move only along the allowed paths
        Assert.Equal("DeploymentIdentityMismatch", await G("/api/v1/ingest/deployments", new { externalId = "ci-1", releaseVersion = "9.9.9" }));
        Assert.Equal("IllegalDeploymentTransition", await G("/api/v1/ingest/deployments", new { externalId = "ci-1", state = "Successful" }));   // Queued -> Successful
        Assert.Equal(HttpStatusCode.OK, (await ingest.PostAsJsonAsync("/api/v1/ingest/deployments", new { externalId = "ci-1", state = "Failed" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ingest.PostAsJsonAsync("/api/v1/ingest/deployments", new { externalId = "ci-1", state = "RolledBack" })).StatusCode);
        Assert.Equal("IllegalDeploymentTransition", await G("/api/v1/ingest/deployments", new { externalId = "ci-1", state = "Running" }));      // RolledBack is final
        // events
        Assert.Equal("DeploymentInvalid", await G("/api/v1/ingest/events", new { deploymentExternalId = "ci-1" }));
        Assert.Equal("DeploymentInvalid", await G("/api/v1/ingest/events", new { deploymentExternalId = "ci-1", events = Array.Empty<object>() }));
        Assert.Equal("DeploymentInvalid", await G("/api/v1/ingest/events", new { deploymentExternalId = "ci-1", events = new[] { new { level = "Debug", message = "x" } } }));
        Assert.Equal("DeploymentInvalid", await G("/api/v1/ingest/events", new { deploymentExternalId = "ci-1", events = new[] { new { message = "  " } } }));
        Assert.Equal("DeploymentInvalid", await G("/api/v1/ingest/events", new { deploymentExternalId = "ci-1", events = Enumerable.Range(0, 101).Select(i => new { message = $"m{i}" }).ToArray() }));
        Assert.Equal("FieldTooLong", await G("/api/v1/ingest/events", new { deploymentExternalId = "ci-1", events = new[] { new { message = new string('x', 2001) } } }));
        Assert.Equal(HttpStatusCode.NotFound, (await ingest.PostAsJsonAsync("/api/v1/ingest/events", new { deploymentExternalId = "unknown", events = new[] { new { message = "x" } } })).StatusCode);

        Assert.Equal(before + 2, Count(f, "SELECT COUNT(*) FROM AuditEvents"));   // only the two accepted moves wrote anything
        Assert.Equal(0, Count(f, "SELECT COUNT(*) FROM DeploymentEvents"));
        Assert.Equal(1, Count(f, "SELECT COUNT(*) FROM Deployments"));
    }

    [Fact]
    public async Task A_bad_key_gets_401_and_is_audited_without_the_key_and_a_guessing_client_is_slowed()
    {
        using var f = new ApiFactory(settings: new Dictionary<string, string> { ["Ingest:FailedKeysPerMinute"] = "4", ["Ingest:RejectionAuditsPerMinute"] = "3" });
        var (rte, ingest, _) = await Setup(f);
        var anon = f.CreateClient();
        // no key at all, and a session cookie in place of a key: 401, not audited as a bad key
        Assert.Equal("InvalidIngestKey", await Guard(await anon.PostAsJsonAsync("/api/v1/ingest/deployments", Dep()), HttpStatusCode.Unauthorized));
        Assert.Equal("InvalidIngestKey", await Guard(await rte.PostAsJsonAsync("/api/v1/ingest/deployments", Dep()), HttpStatusCode.Unauthorized));
        Assert.Equal(0, Audits(f, "IngestKey", "Rejected"));

        var wrong = "rk_" + new string('A', 43);
        using (var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/ingest/deployments") { Content = JsonContent.Create(Dep()) })
        {
            req.Headers.Authorization = new("Bearer", wrong);
            var res = await anon.SendAsync(req);
            Assert.Equal("InvalidIngestKey", await Guard(res, HttpStatusCode.Unauthorized));
            Assert.Equal("Bearer", res.Headers.WwwAuthenticate.Single().Scheme);
        }
        Assert.Equal(1, Audits(f, "IngestKey", "Rejected"));
        Assert.Equal("", Scalar(f, "SELECT COALESCE(WorkspaceId,'') FROM AuditEvents WHERE EntityType='IngestKey' AND Action='Rejected'"));   // host-wide: no workspace
        Assert.Equal(0, Count(f, $"SELECT COUNT(*) FROM AuditEvents WHERE AfterJson LIKE '%{wrong}%'"));
        Assert.Contains("remoteAddress", Scalar(f, "SELECT AfterJson FROM AuditEvents WHERE EntityType='IngestKey' AND Action='Rejected'"));

        // other shapes of bad key: too short, wrong prefix, a revoked key's secret
        foreach (var bad in new[] { "rk_short", "Bearer-less garbage", "sk_" + new string('A', 43) })
        {
            using var r = new HttpRequestMessage(HttpMethod.Post, "/api/v1/ingest/events") { Content = JsonContent.Create(new { }) };
            r.Headers.TryAddWithoutValidation("X-Api-Key", bad);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.SendAsync(r)).StatusCode);
        }
        Assert.Equal(3, Audits(f, "IngestKey", "Rejected"));   // bounded per address: 3 rows however many refusals
        // the 4 refusals used the address's budget: even the right key waits (429), and nothing was written for it
        var throttled = await ingest.PostAsJsonAsync("/api/v1/ingest/deployments", Dep("ci-9"));
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.True(throttled.Headers.RetryAfter is not null);
        Assert.Equal(3, Audits(f, "IngestKey", "Rejected"));
        Assert.Equal(0, Count(f, "SELECT COUNT(*) FROM Deployments"));
    }

    [Fact]
    public async Task A_key_beyond_its_rate_limit_gets_429_with_Retry_After_and_other_keys_are_not_slowed()
    {
        using var f = new ApiFactory(settings: new Dictionary<string, string> { ["Ingest:RequestsPerSecond"] = "1", ["Ingest:Burst"] = "3" });
        var (rte, ingest, _) = await Setup(f);
        var other = IngestTestKit.Client(f, IngestTestKit.Key(f, name: "Another job"));
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++) codes.Add((await ingest.PostAsJsonAsync("/api/v1/ingest/deployments", Dep($"ci-{i}"))).StatusCode);
        Assert.Equal(3, codes.Count(c => c == HttpStatusCode.Created));
        Assert.Equal(2, codes.Count(c => c == HttpStatusCode.TooManyRequests));
        var limited = await ingest.PostAsJsonAsync("/api/v1/ingest/deployments", Dep("ci-x"));
        Assert.Equal("RateLimited", await Guard(limited, HttpStatusCode.TooManyRequests));
        Assert.True(limited.Headers.RetryAfter!.Delta >= TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.Created, (await other.PostAsJsonAsync("/api/v1/ingest/deployments", Dep("ci-other"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rte.GetAsync("/api/v1/deployments")).StatusCode);   // people reading are not subject to it
    }

    [Fact]
    public async Task A_key_writes_only_to_its_own_workspace_and_the_other_cannot_read_it()
    {
        using var f = new ApiFactory();
        var (rteA, ingestA, _) = await Setup(f);
        var rteB = await WorkspaceIsolationTests.InB(f, Roles.RTE, "b@x.com");
        // B has its own environment and service of the same names, and its own key
        Assert.Equal(HttpStatusCode.OK, (await rteB.PostAsJsonAsync("/api/v1/environments", new { name = "Production", kind = "Production" })).StatusCode);
        var keyB = IngestTestKit.Key(f, WorkspaceIsolationTests.B, "B job");
        var ingestB = IngestTestKit.Client(f, keyB);
        // B has no payments-api yet: its key cannot use A's
        Assert.Equal("UnknownService", await Guard(await ingestB.PostAsJsonAsync("/api/v1/ingest/deployments", Dep("shared-id"))));
        Assert.Equal(HttpStatusCode.OK, (await rteB.PostAsJsonAsync("/api/v1/services", new { serviceKey = "payments-api", name = "Beta payments" })).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await ingestA.PostAsJsonAsync("/api/v1/ingest/deployments", Dep("shared-id"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await ingestB.PostAsJsonAsync("/api/v1/ingest/deployments", Dep("shared-id"))).StatusCode);   // the same external id: another workspace's deployment
        Assert.Equal(2, Count(f, "SELECT COUNT(*) FROM Deployments WHERE ExternalId='shared-id'"));
        Assert.Equal(1, Count(f, $"SELECT COUNT(*) FROM Deployments WHERE ExternalId='shared-id' AND WorkspaceId='{WorkspaceIsolationTests.B}'"));
        var idA = (await rteA.GetFromJsonAsync<JsonElement>("/api/v1/deployments")).EnumerateArray().Single().GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.NotFound, (await rteB.GetAsync($"/api/v1/deployments/{idA}")).StatusCode);
        Assert.Single((await rteB.GetFromJsonAsync<JsonElement>("/api/v1/deployments")).EnumerateArray());
        // B's key cannot append events to A's deployment: its externalId is B's
        Assert.Equal(HttpStatusCode.OK, (await ingestB.PostAsJsonAsync("/api/v1/ingest/events", new { deploymentExternalId = "shared-id", events = new[] { new { message = "from B" } } })).StatusCode);
        Assert.Equal(0, Count(f, $"SELECT COUNT(*) FROM DeploymentEvents WHERE DeploymentId='{idA}'"));
        // and a key of a workspace that was archived stops working
        Sql(f, $"UPDATE Workspaces SET ArchivedAt='2026-10-02T00:00:00Z' WHERE Id='{WorkspaceIsolationTests.B}'");
        Assert.Equal(HttpStatusCode.Unauthorized, (await ingestB.PostAsJsonAsync("/api/v1/ingest/deployments", Dep("shared-id"))).StatusCode);
    }

    [Fact]
    public async Task Changes_are_pushed_over_SignalR_to_the_workspace_group_only()
    {
        using var f = new ApiFactory();
        var (_, ingest, _) = await Setup(f);
        var (hubA, eventsA) = await Listen(f, "a@x.com", null);
        var (hubB, eventsB) = await Listen(f, "b@x.com", WorkspaceIsolationTests.B);
        try
        {
            var created = await Json(await ingest.PostAsJsonAsync("/api/v1/ingest/deployments", Dep("ci-live")));
            var id = created.GetProperty("id").GetString()!;
            Assert.Equal($"DeploymentChanged {id} 1", await Next(eventsA));
            await ingest.PostAsJsonAsync("/api/v1/ingest/events", new { deploymentExternalId = "ci-live", events = new[] { new { message = "hello" } } });
            Assert.Equal($"DeploymentChanged {id} 2", await Next(eventsA));
            await ingest.PostAsJsonAsync("/api/v1/ingest/deployments", Dep("ci-live"));   // a replay: no change, no hint
            await Task.Delay(400);
            Assert.False(eventsA.Reader.TryRead(out var extra), "a replay pushed " + extra);
            Assert.False(eventsB.Reader.TryRead(out var leaked), "workspace B was told: " + leaked);
        }
        finally { await hubA.DisposeAsync(); await hubB.DisposeAsync(); }
    }

    private static async Task<string> Next(Channel<string> ch)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await ch.Reader.ReadAsync(cts.Token);
    }

    private static async Task<(HubConnection Hub, Channel<string> Events)> Listen(ApiFactory f, string email, string? workspace)
    {
        var login = await f.CreateClient().PostAsJsonAsync("/auth/dev-login", new { email, name = email.Split('@')[0], role = Roles.Viewer });
        login.EnsureSuccessStatusCode();
        var cookie = login.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        if (workspace is not null)
        {
            var uid = UserId(f, email);
            Sql(f, $@"INSERT OR IGNORE INTO Workspaces(Id,Name,Slug,CreatedAt) VALUES('{workspace}','Beta','beta','2026-10-01T00:00:00Z');
                     INSERT INTO WorkspaceMembers(WorkspaceId,UserId,Role,AddedAt) VALUES('{workspace}','{uid}','Viewer','2026-10-01T00:00:00Z');
                     DELETE FROM WorkspaceMembers WHERE WorkspaceId='default' AND UserId='{uid}'");
        }
        var events = Channel.CreateUnbounded<string>();
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(f.Server.BaseAddress, "/hub/trains" + (workspace is null ? "" : "?workspace=" + workspace)), o =>
            {
                o.HttpMessageHandlerFactory = _ => f.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
                o.Headers["Cookie"] = cookie;
            }).Build();
        hub.On<string, int, string?>("DeploymentChanged", (id, version, _) => events.Writer.TryWrite($"DeploymentChanged {id} {version}"));
        await hub.StartAsync();
        return (hub, events);
    }

    [Fact]
    public async Task Fifty_events_a_second_are_accepted_without_slowing_the_app()
    {
        using var f = new ApiFactory();
        var (rte, ingest, _) = await Setup(f);
        Assert.Equal(HttpStatusCode.Created, (await ingest.PostAsJsonAsync("/api/v1/ingest/deployments", Dep("ci-load", new { State = "Running" }))).StatusCode);
        async Task<double> P95(Func<Task> work, int n)
        {
            var times = new List<double>();
            for (var i = 0; i < n; i++) { var sw = Stopwatch.StartNew(); await work(); times.Add(sw.Elapsed.TotalMilliseconds); await Task.Delay(20); }
            times.Sort();
            return times[(int)Math.Ceiling(times.Count * 0.95) - 1];
        }
        var baseline = await P95(async () => (await rte.GetAsync("/api/v1/trains")).EnsureSuccessStatusCode(), 40);

        var seconds = int.TryParse(Environment.GetEnvironmentVariable("REOS_INGEST_LOAD_SECONDS"), out var secs) && secs > 0 ? secs : 3; const int perSecond = 50;
        var failures = 0;
        var ingestTimes = new System.Collections.Concurrent.ConcurrentBag<double>();
        using var stop = new CancellationTokenSource();
        var reader = Task.Run(async () => await P95(async () => (await rte.GetAsync("/api/v1/trains")).EnsureSuccessStatusCode(), seconds * 20));
        var watch = Stopwatch.StartNew();
        var senders = Enumerable.Range(0, perSecond * seconds).Select(async i =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(i * 1000.0 / perSecond));   // the n-th event is due at n/50 s: 50 a second, steady
            var sw = Stopwatch.StartNew();
            var r = await ingest.PostAsJsonAsync("/api/v1/ingest/events", new { deploymentExternalId = "ci-load", events = new[] { new { level = "Info", message = $"event {i}", externalId = $"load-{i}" } } });
            ingestTimes.Add(sw.Elapsed.TotalMilliseconds);
            if (r.StatusCode != HttpStatusCode.OK) Interlocked.Increment(ref failures);
        }).ToArray();
        await Task.WhenAll(senders);
        var during = await reader;
        Assert.Equal(0, failures);
        Assert.Equal(perSecond * seconds, Count(f, "SELECT COUNT(*) FROM DeploymentEvents"));
        // Generous bounds for a shared CI runner; the 50/s figure is the sustained rate above, and docs/LOAD_TEST.md has the long run.
        Assert.True(during < Math.Max(baseline * 6, 750), $"GET /trains p95 went from {baseline:F0} ms to {during:F0} ms while 50 events/s were ingested");
        var sorted = ingestTimes.Order().ToArray();
        Assert.True(sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1] < 1000, "ingest p95 over a second");
    }
}
