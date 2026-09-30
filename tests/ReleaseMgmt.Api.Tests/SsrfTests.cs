using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ReleaseMgmt.Api.Endpoints;
using ReleaseMgmt.Api.Reminders;
using ReleaseMgmt.Api.Sync;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Comms;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Reminders;
using ReleaseMgmt.Infrastructure.Sync;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// Security review, OWASP API7 (SSRF) and API10 (unsafe consumption): docs/security/scan-ssrf.md. Unlike the connector, notification and dispatch tests,
/// several tests here use the REAL named HTTP clients (the SocketsHttpHandler with the connect-time address check) against a TCP listener on 127.0.0.1,
/// so the check that actually stands between an admin-entered URL and the host's own network is exercised, not replaced by a fake.
/// </summary>
public class SsrfTests
{
    private static readonly string[] OutboundClients = [ConnectorHttp.ClientName, TeamWebhookSender.ClientName, CommWebhookSender.ClientName];

    /// <summary>A plain TCP listener on 127.0.0.1 that counts connections and, if given one, writes a canned HTTP response after reading the request head.</summary>
    private sealed class Listener : IDisposable
    {
        private readonly TcpListener _l = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private int _accepted;
        public int Port { get; }
        public int Accepted => Volatile.Read(ref _accepted);

        public Listener(string? response = null)
        {
            _l.Start();
            Port = ((IPEndPoint)_l.LocalEndpoint).Port;
            _ = LoopAsync(response);
        }

        private async Task LoopAsync(string? response)
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var c = await _l.AcceptTcpClientAsync(_stop.Token);
                    Interlocked.Increment(ref _accepted);
                    _ = ServeAsync(c, response);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { /* stopped */ }
        }

        private static async Task ServeAsync(TcpClient c, string? response)
        {
            using (c)
            {
                if (response is null) return;
                var s = c.GetStream();
                var head = new StringBuilder();
                var buf = new byte[8192];
                while (!head.ToString().Contains("\r\n\r\n"))
                {
                    var n = await s.ReadAsync(buf);
                    if (n == 0) return;
                    head.Append(Encoding.ASCII.GetString(buf, 0, n));
                }
                await s.WriteAsync(Encoding.ASCII.GetBytes(response));
                await s.FlushAsync();
            }
        }

        public void Dispose() { _stop.Cancel(); _l.Stop(); }
    }

    private const string Ok200 = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}";

    // ---- the connect-time guard, through the real handlers (regression pins; these passed before the review) ---------------------------------------
    public static TheoryData<string> LoopbackSpellings => new()
    {
        "127.0.0.1", "localhost", "2130706433", "0x7f000001", "017700000001", "0177.0.0.1", "127.1", "0x7f.1", "0.0.0.0", "0", "[::ffff:127.0.0.1]", "[::1]",
    };

    [Theory]
    [MemberData(nameof(LoopbackSpellings))]
    public async Task No_outbound_client_ever_connects_to_a_loopback_target_however_it_is_spelled(string host)
    {
        using var f = new ApiFactory();   // Development: http is allowed for connectors, so the connect-time check is the only thing in the way
        using var l = new Listener(Ok200);
        var http = f.Services.GetRequiredService<IHttpClientFactory>();
        foreach (var name in OutboundClients)
        {
            using var c = http.CreateClient(name);
            c.Timeout = TimeSpan.FromSeconds(15);
            await Assert.ThrowsAsync<HttpRequestException>(() => c.GetAsync($"http://{host}:{l.Port}/latest/meta-data"));
        }
        Assert.Equal(0, l.Accepted);
    }

    [Fact]
    public async Task Test_connection_to_a_host_name_that_resolves_to_loopback_is_refused_when_the_socket_connects()
    {
        // The DNS-rebinding shape: the name is not an address when it is saved, and only resolves to a private one later. It is judged at connect time.
        using var f = new ApiFactory();
        using var l = new Listener(Ok200);
        var rte = await As(f, Roles.RTE, "rte@x.com");
        Assert.Equal(HttpStatusCode.OK, (await rte.PutAsJsonAsync("/api/v1/connectors/Jira", new { baseUrl = $"http://localhost:{l.Port}" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rte.PutAsJsonAsync("/api/v1/connectors/Jira/credentials", new { kind = "ApiToken", username = "rae@example.com", secret = "tok-123" })).StatusCode);

        var r = await rte.PostAsync("/api/v1/connectors/Jira:test", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        var body = await r.Content.ReadAsStringAsync();
        Assert.Contains("ConnectorTestFailed", body);
        Assert.DoesNotContain("localhost", body);   // the message names the class of failure, not the target
        Assert.Equal(0, l.Accepted);
    }

    [Fact]
    public async Task A_redirect_is_handed_back_and_never_followed_by_any_outbound_client()
    {
        using var target = new Listener(Ok200);
        using var hop = new Listener($"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{target.Port}/latest/meta-data\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        using var f0 = new ApiFactory();
        // private targets allowed so the first hop (on 127.0.0.1) is reachable at all; the second hop must still never be requested
        using var f = f0.WithWebHostBuilder(b =>
        {
            b.UseSetting("Sync:AllowPrivateTargets", "true");
            b.UseSetting("Notifications:Webhooks:AllowPrivateTargets", "true");
            b.UseSetting("Comms:Webhooks:AllowPrivateTargets", "true");
        });
        var http = f.Services.GetRequiredService<IHttpClientFactory>();
        foreach (var name in OutboundClients)
        {
            using var c = http.CreateClient(name);
            c.Timeout = TimeSpan.FromSeconds(15);
            using var res = await c.GetAsync($"http://127.0.0.1:{hop.Port}/hook");
            Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        }
        Assert.True(hop.Accepted >= 1);
        Assert.Equal(0, target.Accepted);
    }

    // ---- SEC-C1: IPv6 forms that carry an IPv4 address ------------------------------------------------------------------------------------------------
    [Theory]
    [InlineData("64:ff9b::a9fe:a9fe")]    // NAT64 well-known prefix + 169.254.169.254 (cloud metadata)
    [InlineData("64:ff9b::a00:5")]        // NAT64 + 10.0.0.5
    [InlineData("64:ff9b::c0a8:101")]     // NAT64 + 192.168.1.1
    [InlineData("64:ff9b::7f00:1")]       // NAT64 + 127.0.0.1
    [InlineData("64:ff9b:1::a00:5")]      // NAT64 local-use prefix (RFC 8215): translated to whatever the operator chose
    [InlineData("::7f00:1")]              // IPv4-compatible ::127.0.0.1 (deprecated form)
    [InlineData("::a9fe:a9fe")]           // IPv4-compatible ::169.254.169.254
    [InlineData("::ffff:0:a00:5")]        // SIIT IPv4-translated ::ffff:0:10.0.0.5
    [InlineData("2002:a9fe:a9fe::1")]     // 6to4 wrapping 169.254.169.254
    [InlineData("2002:c0a8:101::1")]      // 6to4 wrapping 192.168.1.1
    [InlineData("2002:7f00:1::1")]        // 6to4 wrapping 127.0.0.1
    [InlineData("100::1")]                // discard-only prefix
    [InlineData("2001:db8::1")]           // documentation prefix
    public void IPv6_forms_that_carry_a_blocked_IPv4_address_are_blocked(string address) =>
        Assert.True(WebhookAddressPolicy.IsBlocked(IPAddress.Parse(address)), address);

    [Theory]
    [InlineData("64:ff9b::5db8:d822")]    // NAT64 to 93.184.216.34: an IPv6-only host reaching a public service keeps working
    [InlineData("2002:5db8:d822::1")]     // 6to4 of the same public address
    [InlineData("2606:4700:4700::1111")]
    [InlineData("2a00:1450:4001:80b::200e")]
    public void IPv6_forms_that_carry_a_public_IPv4_address_stay_open(string address) =>
        Assert.False(WebhookAddressPolicy.IsBlocked(IPAddress.Parse(address)), address);

    [Fact]
    public async Task A_NAT64_spelling_of_the_metadata_address_is_refused_as_a_connector_base_url_and_as_a_webhook_address()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var c = await rte.PutAsJsonAsync("/api/v1/connectors/Jira", new { baseUrl = "https://[64:ff9b::a9fe:a9fe]" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, c.StatusCode);
        Assert.Contains("ConnectorInvalid", await c.Content.ReadAsStringAsync());
        var w = await rte.PostAsJsonAsync("/api/v1/sync/webhook-allowlist", new { name = "meta", url = "https://[64:ff9b::a9fe:a9fe]/latest/meta-data" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, w.StatusCode);
        Assert.Contains(SyncGuards.WebhookPrivateTarget, await w.Content.ReadAsStringAsync());
        Assert.Equal("0", Scalar(f, "SELECT COUNT(*) FROM ConnectorState WHERE SourceSystem='Jira'"));
        Assert.Equal("0", Scalar(f, "SELECT COUNT(*) FROM WebhookDestinations"));
    }

    // ---- SEC-C2: the save-time checks judge the host that will be connected to ------------------------------------------------------------------------------
    [Theory]
    [InlineData("https://１２７.０.０.１/hook")]                  // fullwidth digits and dots: Uri calls this a DNS name; its IDN form is 127.0.0.1
    [InlineData("https://①②⑦.0.0.1/hook")]                         // enclosed alphanumerics
    [InlineData("https://１６９.２５４.１６９.２５４/latest")]     // the metadata address, fullwidth
    [InlineData("https://１０.０.０.５/hook")]
    public void An_address_spelled_with_unicode_digits_is_judged_by_the_address_it_becomes(string url)
    {
        var r = WebhookUrlPolicy.Check(url, allowPrivate: false);
        Assert.False(r.IsOk, url);
        Assert.Equal(SyncGuards.WebhookPrivateTarget, r.Failures[0].Guard);

        var baseUrl = url[..url.IndexOf('/', "https://".Length)];
        var problem = ConnectorUrlPolicy.Validate(baseUrl, false, SyncOptions.From(new ConfigurationBuilder().Build()), WebhookAddressPolicy.IsBlocked);
        Assert.NotNull(problem);
        Assert.Contains("private", problem);
    }

    [Fact]
    public void A_connector_base_url_is_bounded_like_a_webhook_address()
    {
        var o = SyncOptions.From(new ConfigurationBuilder().Build());
        Assert.Null(ConnectorUrlPolicy.Validate("https://acme.atlassian.net/jira", false, o, WebhookAddressPolicy.IsBlocked));
        var tooLong = ConnectorUrlPolicy.Validate("https://acme.atlassian.net/" + new string('a', WebhookUrlPolicy.MaxUrlLength), false, o, WebhookAddressPolicy.IsBlocked);
        Assert.NotNull(tooLong);
        Assert.Contains("longer", tooLong);
    }

    // ---- SEC-C3: webhook senders never read (or wait for) the response body ------------------------------------------------------------------------------
    /// <summary>A response body that serves <paramref name="quickBytes"/> at once, then (if asked) never ends. Counts what was read, across instances.</summary>
    private sealed class HostileBody(long quickBytes, bool hang, Action<int> counted) : Stream
    {
        private long _served;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _served; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_served < quickBytes)
            {
                var n = (int)Math.Min(buffer.Length, quickBytes - _served);
                buffer.Span[..n].Clear();
                _served += n;
                counted(n);
                return n;
            }
            if (hang) await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class HostileWebhook : HttpMessageHandler
    {
        private long _bytesRead;
        public int Calls;
        public long BytesRead => Interlocked.Read(ref _bytesRead);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            // "200 OK" at once, then 8 MB of body, then a body that never ends: a hostile, compromised or broken endpoint
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new HostileBody(8 << 20, hang: true, n => Interlocked.Add(ref _bytesRead, n))) });
        }
    }

    private sealed class StubFactory(HttpMessageHandler h) : IHttpClientFactory { public HttpClient CreateClient(string name) => new(h, disposeHandler: false); }
    private sealed class NoSink : IAlertSink { public Task RaiseAsync(string s, string k, string key, string m, CancellationToken ct = default) => Task.CompletedTask; }

    [Fact]
    public async Task A_team_webhook_that_answers_200_and_then_streams_forever_is_delivered_once_and_its_body_is_never_buffered()
    {
        using var f = new ApiFactory();
        _ = f.Server;
        Sql(f, @"INSERT INTO WebhookDestinations(Id,Name,Url,Kind) VALUES('w1','Platform channel','https://93.184.216.34/services/T0/B0/SECRET','Teams');
                 INSERT INTO Teams(Id,Handle,Name,WebhookDestinationId) VALUES('tm1','platform','Platform','w1');
                 INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CreatedAt,UpdatedAt) VALUES('t1','R26.10','2026-10-30','Low','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z')");
        var dbf = f.Services.GetRequiredService<IDbContextFactory<ReleaseDbContext>>();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Notifications:Webhooks:TimeoutSeconds"] = "1", ["Notifications:Webhooks:MaxAttempts"] = "3", ["Notifications:Webhooks:RetryDelayMs"] = "0",
        }).Build();
        var hook = new HostileWebhook();
        var writer = new SyncAlertWriter(dbf, TimeProvider.System, NullLogger<SyncAlertWriter>.Instance, f.Services.GetRequiredService<INotifier>());
        var sender = new TeamWebhookSender(dbf, TimeProvider.System, new StubFactory(hook), writer, new NoSink(), config, f.Services.GetRequiredService<IHostEnvironment>(), NullLogger<TeamWebhookSender>.Instance);

        var delivered = await sender.SendAsync(new WebhookNotice("tm1", "GateOverdue", "StageGate", "g1", 1, "Gate 'Code Freeze' is overdue", "t1"));

        Assert.True(delivered, "a 200 is a delivery; waiting for the body turned it into a timeout");
        Assert.Equal(1, hook.Calls);                                  // no duplicate posts to the channel
        Assert.True(hook.BytesRead < 1 << 20, $"{hook.BytesRead} bytes of the answer were read into memory");
        Assert.Equal("0", Scalar(f, "SELECT COUNT(*) FROM SyncAlerts"));
    }

    [Fact]
    public async Task A_comm_webhook_that_answers_200_and_then_streams_forever_is_delivered_and_its_body_is_never_buffered()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Comms:Webhooks:TimeoutSeconds"] = "1" }).Build();
        var hook = new HostileWebhook();
        var sender = new CommWebhookSender(new StubFactory(hook), config, NullLogger<CommWebhookSender>.Instance);

        var r = await sender.SendAsync(new CommWebhookTarget("w1", "release-ops", "https://93.184.216.34/services/T0/B0/SECRET", "Teams"), """{"text":"hello"}""", default);

        Assert.True(r.Delivered, r.Reason);
        Assert.Equal(1, hook.Calls);
        Assert.True(hook.BytesRead < 1 << 20, $"{hook.BytesRead} bytes of the answer were read into memory");
    }
}
