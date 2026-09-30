using System.Net;
using ReleaseMgmt.Domain.Sync;
using ReleaseMgmt.Infrastructure.Sync;
using static ReleaseMgmt.Infrastructure.Tests.SyncTestKit;

namespace ReleaseMgmt.Infrastructure.Tests;

/// <summary>REOS-39: the Jira Cloud and ServiceNow read connectors against recorded HTTP responses (no live tenant, no socket).</summary>
public sealed class SyncConnectorTests
{
    private static SyncOptions Opts(int maxBytes = 1_000_000, double timeout = 0.3) => SyncOptions.From(new Cfg(new()
    {
        ["Sync:TimeoutSeconds"] = timeout.ToString(System.Globalization.CultureInfo.InvariantCulture), ["Sync:MaxResponseBytes"] = maxBytes.ToString(),
    }));

    private static ConnectorHttp H(FakeHttp h, string src, string url, SyncOptions? o = null) => new(new FakeHttpFactory(h), o ?? Opts(), TimeProvider.System, src, ConnectorUrlPolicy.Normalize(url));
    private static JiraCloudConnector Jira(FakeHttp h, SyncOptions? o = null) => new(H(h, "Jira", JiraUrl, o), new ConnectorCredentials("ApiToken", "rae@example.com", Secret));
    private static ServiceNowConnector Sn(FakeHttp h, string kind = "Basic", TimeProvider? t = null, SyncOptions? o = null) =>
        new(H(h, "ServiceNow", SnUrl, o), new ConnectorCredentials(kind, "svc-release", Secret), t ?? TimeProvider.System);

    private static async Task<ConnectorException> Fails(Task t) => await Assert.ThrowsAsync<ConnectorException>(() => t);

    // ---- Jira -----------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Jira_issue_returns_the_status_name_only_and_asks_for_no_other_field()
    {
        var h = new FakeHttp(); h.Always(200, JiraIssue);
        var s = await Jira(h).FetchStatusAsync("PAY-12", default);
        Assert.Equal(new ExternalStatus("PAY-12", "In Progress"), s);   // the summary in the recorded body is never read
        Assert.Equal("GET /rest/api/3/issue/PAY-12?fields=status", h.Requests.Single());
        Assert.Equal("Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("rae@example.com:" + Secret)), h.Auth.Single());
    }

    [Fact]
    public async Task Jira_fix_version_is_found_by_project_and_name_with_released_flag_and_release_date()
    {
        var h = new FakeHttp(); h.Always(200, JiraVersions(released: false));
        var s = await Jira(h).FetchStatusAsync("PAY/4.5.0", default);
        Assert.Equal("Unreleased", s.State);
        Assert.False(s.Released);
        Assert.Equal(new DateOnly(2026, 10, 30), s.ReleaseDate);
        Assert.StartsWith("GET /rest/api/3/project/PAY/version?", h.Requests.Single());

        h.Always(200, JiraVersions(released: true));
        Assert.Equal("Released", (await Jira(h).FetchStatusAsync("PAY/4.5.0", default)).State);
        h.Always(200, """[{"name":"4.5.0","released":true,"releaseDate":"2026-10-30"}]""");   // the older non-paged shape
        Assert.True((await Jira(h).FetchStatusAsync("PAY/4.5.0", default)).Released);
    }

    [Fact]
    public async Task Jira_unknown_version_is_NotFound_and_a_malformed_answer_is_ParseError()
    {
        var h = new FakeHttp(); h.Always(200, JiraVersions(false));
        Assert.Equal(ConnectorErrorKind.NotFound, (await Fails(Jira(h).FetchStatusAsync("PAY/9.9.9", default))).Kind);
        h.Always(200, """{"fields":{}}""");
        Assert.Equal(ConnectorErrorKind.Parse, (await Fails(Jira(h).FetchStatusAsync("PAY-12", default))).Kind);
        h.Always(200, "<html>not json</html>");
        Assert.Equal(ConnectorErrorKind.Parse, (await Fails(Jira(h).FetchStatusAsync("PAY-12", default))).Kind);
    }

    [Fact]
    public async Task A_key_that_is_not_a_jira_key_never_reaches_the_network()
    {
        var h = new FakeHttp();
        Assert.Equal(ConnectorErrorKind.NotFound, (await Fails(Jira(h).FetchStatusAsync("../../secret", default))).Kind);
        Assert.Equal(0, h.Count);
    }

    // ---- ServiceNow -------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task ServiceNow_change_maps_state_code_and_planned_window_and_requests_only_key_state_dates()
    {
        var h = new FakeHttp(); h.Always(200, SnChange("-1"));
        var s = await Sn(h).FetchStatusAsync("CHG0030001", default);
        Assert.Equal("Implement", s.State);
        Assert.Equal(new DateTime(2026, 10, 30, 2, 0, 0, DateTimeKind.Utc), s.PlannedStart);
        Assert.Equal(new DateTime(2026, 10, 30, 6, 0, 0, DateTimeKind.Utc), s.PlannedEnd);
        var req = Uri.UnescapeDataString(h.Requests.Single());
        Assert.Contains("/api/now/table/change_request", req);
        Assert.Contains("sysparm_query=number=CHG0030001", req);
        Assert.Contains("sysparm_fields=number,state,start_date,end_date", req);   // no short_description, no assignee
        Assert.StartsWith("Basic ", h.Auth.Single());
    }

    [Fact]
    public async Task ServiceNow_change_task_uses_its_own_table_and_states()
    {
        var h = new FakeHttp(); h.Always(200, """{"result":[{"number":"CTASK0010001","state":"3"}]}""");
        var s = await Sn(h).FetchStatusAsync("CTASK0010001", default);
        Assert.Equal("Closed Complete", s.State);
        Assert.Contains("change_task", h.Requests.Single());
    }

    [Fact]
    public async Task ServiceNow_missing_change_is_NotFound()
    {
        var h = new FakeHttp(); h.Always(200, SnEmpty);
        var ex = await Fails(Sn(h).FetchStatusAsync("CHG0030001", default));
        Assert.Equal(ConnectorErrorKind.NotFound, ex.Kind);
    }

    [Fact]
    public async Task ServiceNow_oauth_fetches_one_token_per_connector_and_sends_it_as_bearer()
    {
        var h = new FakeHttp();
        h.Respond = (req, _) => Task.FromResult(req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/oauth_token.do")
            ? Json(200, """{"access_token":"tok-1","expires_in":1800,"token_type":"Bearer"}""") : Json(200, SnChange("-2")));
        var c = Sn(h, "OAuth");
        await c.FetchStatusAsync("CHG0030001", default);
        await c.FetchStatusAsync("CHG0030001", default);
        Assert.Equal(1, h.Requests.Count(r => r.StartsWith("POST ")));
        Assert.Equal(2, h.Requests.Count(r => r.StartsWith("GET ")));
        Assert.All(h.Auth.Where(a => a is not null), a => Assert.Equal("Bearer tok-1", a));
    }

    [Fact]
    public async Task ServiceNow_oauth_refusal_is_an_auth_failure_and_the_token_is_refetched_after_expiry()
    {
        var h = new FakeHttp(); h.Always(401, """{"error":"invalid_client"}""");
        Assert.Equal(ConnectorErrorKind.Auth, (await Fails(Sn(h, "OAuth").FetchStatusAsync("CHG0030001", default))).Kind);

        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(); time.SetUtcNow(DateTimeOffset.Parse("2026-10-30T00:00:00Z"));
        var h2 = new FakeHttp();
        h2.Respond = (req, _) => Task.FromResult(req.Method == HttpMethod.Post ? Json(200, """{"access_token":"t","expires_in":60}""") : Json(200, SnChange("-2")));
        var c = Sn(h2, "OAuth", time);
        await c.FetchStatusAsync("CHG0030001", default);
        time.Advance(TimeSpan.FromMinutes(2));
        await c.FetchStatusAsync("CHG0030001", default);
        Assert.Equal(2, h2.Requests.Count(r => r.StartsWith("POST ")));
    }

    // ---- errors, bounds -----------------------------------------------------------------------------------------------------------------
    [Theory]
    [InlineData(401, ConnectorErrorKind.Auth)] [InlineData(403, ConnectorErrorKind.Auth)] [InlineData(404, ConnectorErrorKind.NotFound)]
    [InlineData(429, ConnectorErrorKind.RateLimited)] [InlineData(500, ConnectorErrorKind.Server)] [InlineData(503, ConnectorErrorKind.Server)]
    [InlineData(302, ConnectorErrorKind.Server)]
    public async Task Http_failures_are_classified_and_their_messages_carry_no_url_or_body(int status, ConnectorErrorKind kind)
    {
        var h = new FakeHttp(); h.Always(status, """{"error":"customer 4111111111111111 not allowed"}""");
        var ex = await Fails(Sn(h).FetchStatusAsync("CHG0030001", default));
        Assert.Equal(kind, ex.Kind);
        Assert.Equal(status, ex.HttpStatus);
        Assert.DoesNotContain("service-now", ex.Message);
        Assert.DoesNotContain("4111", ex.Message);
        Assert.DoesNotContain(Secret, ex.Message);
    }

    [Fact]
    public async Task A_429_carries_the_retry_after_header()
    {
        var h = new FakeHttp(); h.Always(() => Json(429, "{}", r => r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(42))));
        Assert.Equal(TimeSpan.FromSeconds(42), (await Fails(Sn(h).FetchStatusAsync("CHG0030001", default))).RetryAfter);
    }

    [Fact]
    public async Task A_hung_server_times_out_and_a_transport_error_is_a_network_failure()
    {
        var h = new FakeHttp(); h.Respond = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Json(200, "{}"); };
        var ex = await Fails(Sn(h, o: Opts(timeout: 0.1)).FetchStatusAsync("CHG0030001", default));
        Assert.Equal(ConnectorErrorKind.Timeout, ex.Kind);

        h.Respond = (_, _) => throw new TaskCanceledException("HttpClient.Timeout elapsed");
        Assert.Equal(ConnectorErrorKind.Timeout, (await Fails(Sn(h).FetchStatusAsync("CHG0030001", default))).Kind);

        h.Respond = (_, _) => throw new HttpRequestException("Name or service not known (acme.service-now.com:443)");
        var net = await Fails(Sn(h).FetchStatusAsync("CHG0030001", default));
        Assert.Equal(ConnectorErrorKind.Network, net.Kind);
        Assert.DoesNotContain("service-now", net.Message);   // only the exception type is kept: a transport message can carry the URI
    }

    [Fact]
    public async Task A_caller_cancellation_is_not_mistaken_for_a_timeout()
    {
        var h = new FakeHttp(); h.Respond = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Json(200, "{}"); };
        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Sn(h, o: Opts(timeout: 30)).FetchStatusAsync("CHG0030001", cts.Token));
    }

    [Fact]
    public async Task An_oversized_answer_is_refused_before_it_is_parsed()
    {
        var h = new FakeHttp(); h.Always(200, "{\"result\":[{\"state\":\"-1\",\"pad\":\"" + new string('x', 5_000) + "\"}]}");
        var ex = await Fails(Sn(h, o: Opts(maxBytes: 1024)).FetchStatusAsync("CHG0030001", default));
        Assert.Equal(ConnectorErrorKind.Parse, ex.Kind);
        Assert.Contains("1024", ex.Message);
    }

    [Fact]
    public async Task Requests_stay_on_the_configured_host()
    {
        var h = new FakeHttp(); h.Always(200, SnChange("-1"));
        var http = H(h, "ServiceNow", SnUrl);
        var ex = await Fails(http.GetJsonAsync("https://evil.example/steal", null, default));   // an absolute URL in place of a path
        Assert.Equal(ConnectorErrorKind.Network, ex.Kind);
        Assert.Equal(0, h.Count);
    }

    // ---- base URL policy (SSRF) -------------------------------------------------------------------------------------------------------------
    [Theory]
    [InlineData("https://acme.atlassian.net", false, null)]
    [InlineData("https://acme.atlassian.net/", false, null)]
    [InlineData("http://acme.atlassian.net", false, "https")]
    [InlineData("http://localhost:6081", true, null)]
    [InlineData("https://user:pw@acme.atlassian.net", false, "credentials")]
    [InlineData("https://acme.atlassian.net/?next=http://evil", false, "query")]
    [InlineData("ftp://acme.atlassian.net", false, "https")]
    [InlineData("acme.atlassian.net", false, "absolute")]
    [InlineData("", false, "absolute")]
    [InlineData("https://127.0.0.1", false, "private")]
    [InlineData("https://169.254.169.254", false, "private")]
    [InlineData("https://[::1]", false, "private")]
    [InlineData("https://10.1.2.3", false, "private")]
    public void Base_urls_are_vetted(string url, bool dev, string? expectedProblemWord)
    {
        var p = ConnectorUrlPolicy.Validate(url, dev, SyncOptions.From(new Cfg(new())), ip => IPAddress.IsLoopback(ip) || ip.ToString().StartsWith("10.") || ip.ToString().StartsWith("169.254."));
        if (expectedProblemWord is null) Assert.Null(p); else Assert.Contains(expectedProblemWord, p);
    }

    [Fact]
    public void The_allowed_host_list_restricts_base_urls_and_private_targets_can_be_allowed()
    {
        var o = SyncOptions.From(new Cfg(new() { ["Sync:AllowedHosts"] = "*.atlassian.net, acme.service-now.com" }));
        Assert.Null(ConnectorUrlPolicy.Validate("https://acme.atlassian.net", false, o));
        Assert.Null(ConnectorUrlPolicy.Validate("https://acme.service-now.com", false, o));
        Assert.Contains("allowed list", ConnectorUrlPolicy.Validate("https://evil.example", false, o));
        Assert.Contains("allowed list", ConnectorUrlPolicy.Validate("https://atlassian.net.evil.example", false, o));
        Assert.Contains("allowed list", ConnectorUrlPolicy.Validate("https://atlassian.net", false, o));   // "*.x" means a subdomain
        var priv = SyncOptions.From(new Cfg(new() { ["Sync:AllowPrivateTargets"] = "true" }));
        Assert.Null(ConnectorUrlPolicy.Validate("https://10.1.2.3", false, priv, _ => true));
    }
}
