using System.Text.Json;
using ReleaseMgmt.Domain.Sync;
using ReleaseMgmt.Infrastructure.Sync;
using static ReleaseMgmt.Infrastructure.Tests.SyncTestKit;

namespace ReleaseMgmt.Infrastructure.Tests;

/// <summary>
/// Security review, OWASP API10 (unsafe consumption of APIs), docs/security/scan-ssrf.md SEC-C4 and SEC-C5: what Jira and ServiceNow send back is data from
/// another system. Only a plausible status name is kept (OI-12: keys, states and dates), and nothing they send is written into a request header unchecked.
/// </summary>
public sealed class SyncUnsafeConsumptionTests(TriggerSuiteFixture fx) : IClassFixture<TriggerSuiteFixture>
{
    private const string Marker = "4111111111111111";

    private static SyncOptions Opts() => SyncOptions.From(new Cfg(new() { ["Sync:TimeoutSeconds"] = "0.3" }));
    private static ConnectorHttp H(FakeHttp h, string src, string url) => new(new FakeHttpFactory(h), Opts(), TimeProvider.System, src, ConnectorUrlPolicy.Normalize(url));
    private static JiraCloudConnector Jira(FakeHttp h) => new(H(h, "Jira", JiraUrl), new ConnectorCredentials("ApiToken", "rae@example.com", Secret));
    private static ServiceNowConnector Sn(FakeHttp h, string kind = "Basic") => new(H(h, "ServiceNow", SnUrl), new ConnectorCredentials(kind, "svc-release", Secret), TimeProvider.System);
    private static async Task<ConnectorException> Fails(Task t) => await Assert.ThrowsAsync<ConnectorException>(() => t);

    public static TheoryData<string, string> HostileStates => new()
    {
        { "too long", "Implement " + Marker + new string('x', 5_000) },
        { "a whole ticket", "Customer John Smith, card " + Marker + ", asked for a refund; see the attached statement for the full account history" },
        { "newline", "Implement\n" + Marker },
        { "bidi override", "‮tnemelpmI " + Marker },
        { "zero width", "Imple​ment " + Marker },
        { "NUL", "Implement\0" + Marker },
        { "blank", "   " },
    };

    // ---- SEC-C4: status names ---------------------------------------------------------------------------------------------------------------------------------
    [Theory]
    [MemberData(nameof(HostileStates))]
    public async Task A_jira_status_name_that_is_not_a_plausible_status_is_a_parse_error_that_does_not_repeat_it(string _, string state)
    {
        var h = new FakeHttp(); h.Always(200, JsonSerializer.Serialize(new { key = "PAY-12", fields = new { status = new { name = state } } }));
        var ex = await Fails(Jira(h).FetchStatusAsync("PAY-12", default));
        Assert.Equal(ConnectorErrorKind.Parse, ex.Kind);
        Assert.DoesNotContain(Marker, ex.Message);
    }

    [Theory]
    [MemberData(nameof(HostileStates))]
    public async Task A_servicenow_state_that_is_not_a_plausible_state_is_a_parse_error_that_does_not_repeat_it(string _, string state)
    {
        var h = new FakeHttp(); h.Always(200, JsonSerializer.Serialize(new { result = new[] { new { number = "CHG0030001", state, start_date = "", end_date = "" } } }));
        var ex = await Fails(Sn(h).FetchStatusAsync("CHG0030001", default));
        Assert.Equal(ConnectorErrorKind.Parse, ex.Kind);
        Assert.DoesNotContain(Marker, ex.Message);
    }

    [Theory]
    [InlineData("In Progress")] [InlineData("Selected for Development")] [InlineData("Won't Do")] [InlineData("Klar zur Prüfung")] [InlineData("完了")] [InlineData("Done ✅")]
    public async Task Ordinary_status_names_still_pass(string state)
    {
        var h = new FakeHttp(); h.Always(200, JsonSerializer.Serialize(new { fields = new { status = new { name = state } } }));
        Assert.Equal(state, (await Jira(h).FetchStatusAsync("PAY-12", default)).State);
    }

    [Fact]
    public async Task A_hostile_state_never_reaches_the_link_the_audit_trail_the_alerts_or_the_inbox()
    {
        var e = NewEnv(fx);
        // an Executing train, so the ChgNotImplementing message would quote the state if it were accepted
        e.Sql("INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CurrentStatus,CreatedAt,UpdatedAt) VALUES('t2','Running','2026-10-30','Low','Executing','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z')");
        e.AddSnLink("l1", train: "t2");
        var n = 0;
        e.Http.Respond = (_, _) => Task.FromResult(Json(200, JsonSerializer.Serialize(new { result = new[] { new { number = "CHG0030001", state = $"Implement {++n} " + Marker + new string('x', 50_000), start_date = "", end_date = "" } } })));

        await e.Poller.RunOnceAsync();
        e.Time.Advance(TimeSpan.FromMinutes(5));
        await e.Poller.RunOnceAsync();   // a value that changes every cycle would add an audit row (both copies of it) per link per cycle

        Assert.Null(e.Query("SELECT LastSyncedStatus FROM ExternalLinks WHERE Id='l1'")[0][0]);
        var everything = string.Join("\n",
            e.Query("SELECT COALESCE(BeforeJson,'')||COALESCE(AfterJson,'') FROM AuditEvents").Select(r => (string)r[0]!)
            .Concat(e.Query("SELECT ErrorMessage FROM SyncAlerts").Select(r => (string)r[0]!))
            .Concat(e.Query("SELECT Message FROM Notifications").Select(r => (string)r[0]!)));
        Assert.DoesNotContain(Marker, everything);
        Assert.Equal(1, e.OpenAlerts("ParseError"));   // visible, never silent (rule 8)
        Assert.Equal(0, e.OpenAlerts("Mismatch"));
    }

    // ---- SEC-C5: the OAuth token goes into a header --------------------------------------------------------------------------------------------------------
    [Theory]
    [InlineData("Zq9\r\nX-Injected: 1")]   // .NET writes a header added without validation to the wire as is (verified against a raw socket in the review)
    [InlineData("Zq9 value")]
    [InlineData("")]
    [InlineData("Zq9é")]
    public async Task An_oauth_answer_whose_access_token_is_not_a_bearer_token_is_an_auth_failure_and_nothing_is_sent_with_it(string token)
    {
        var h = new FakeHttp();
        h.Respond = (req, _) => Task.FromResult(req.Method == HttpMethod.Post ? Json(200, JsonSerializer.Serialize(new { access_token = token, expires_in = 1800 })) : Json(200, SnChange("-1")));
        var ex = await Fails(Sn(h, "OAuth").FetchStatusAsync("CHG0030001", default));
        Assert.Equal(ConnectorErrorKind.Auth, ex.Kind);
        Assert.DoesNotContain(h.Requests, r => r.StartsWith("GET "));
        Assert.DoesNotContain("Zq9", ex.Message);
    }

    [Fact]
    public async Task An_oversized_oauth_token_is_refused_and_a_jwt_shaped_one_is_accepted()
    {
        var h = new FakeHttp();
        h.Respond = (req, _) => Task.FromResult(req.Method == HttpMethod.Post ? Json(200, JsonSerializer.Serialize(new { access_token = new string('a', 20_000), expires_in = 1800 })) : Json(200, SnChange("-1")));
        Assert.Equal(ConnectorErrorKind.Auth, (await Fails(Sn(h, "OAuth").FetchStatusAsync("CHG0030001", default))).Kind);

        const string jwt = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJzdmMifQ.c2lnbmF0dXJl-_~+/==";
        var h2 = new FakeHttp();
        h2.Respond = (req, _) => Task.FromResult(req.Method == HttpMethod.Post ? Json(200, JsonSerializer.Serialize(new { access_token = jwt, expires_in = 1800 })) : Json(200, SnChange("-1")));
        Assert.Equal("Implement", (await Sn(h2, "OAuth").FetchStatusAsync("CHG0030001", default)).State);
        Assert.Contains("Bearer " + jwt, h2.Auth);
    }
}
