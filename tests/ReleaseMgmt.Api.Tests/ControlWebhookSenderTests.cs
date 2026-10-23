using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReleaseMgmt.Api.Control;
using ReleaseMgmt.Api.Reminders;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Comms;

namespace ReleaseMgmt.Api.Tests;

/// <summary>REOS-112: the control webhook sender posts the signed call with the three headers, once, with the protections of every outbound webhook; the address is never shown.</summary>
public class ControlWebhookSenderTests
{
    private sealed class Stub(Func<HttpRequestMessage, string, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls;
        public HttpRequestMessage? Seen;
        public string? SeenBody;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            Seen = request;
            SeenBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return await respond(request, SeenBody ?? "").WaitAsync(ct);
        }
    }

    private sealed class Factory(HttpMessageHandler h) : IHttpClientFactory { public HttpClient CreateClient(string name) => new(h, disposeHandler: false); }

    private static (ControlWebhookSender Sender, Stub Handler, CommWebhookTarget Target) Make(string url, Func<HttpRequestMessage, string, Task<HttpResponseMessage>>? respond = null, params (string Key, string Value)[] settings)
    {
        var handler = new Stub(respond ?? ((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value)).Build();
        var vault = WebhookTestRows.Ephemeral();
        var sender = new ControlWebhookSender(new Factory(handler), config, NullLogger<ControlWebhookSender>.Instance, vault, OutboundAddressPolicy.Default);
        var uri = new Uri(url);
        return (sender, handler, new CommWebhookTarget("wh1", "Deploy tool", uri.Authority, vault.Protect(url).ProtectedUrl, "Generic"));
    }

    private static ControlDelivery Delivery() => new("act-1", 1792504800, "{\"id\":\"act-1\",\"action\":\"rollback\"}", ControlSignature.Sign("whs_k", 1792504800, "act-1", "{\"id\":\"act-1\",\"action\":\"rollback\"}"));

    private static readonly (string, string) AllowPrivate = ("Control:Webhooks:AllowPrivateTargets", "true");   // skips the DNS lookup; the address rules have their own tests below

    [Fact]
    public async Task The_call_carries_the_body_the_delivery_id_the_timestamp_and_the_signature()
    {
        var (sender, h, target) = Make("https://deploy.example.com/hooks/abc123", settings: AllowPrivate);
        var d = Delivery();
        var r = await sender.SendAsync(target, d, CancellationToken.None);
        Assert.Equal((true, 200, "deploy.example.com", null), (r.Delivered, r.HttpStatus, r.Host, r.Reason));
        Assert.Equal(HttpMethod.Post, h.Seen!.Method);
        Assert.Equal("https://deploy.example.com/hooks/abc123", h.Seen.RequestUri!.ToString());
        Assert.Equal(d.Body, h.SeenBody);
        Assert.Equal("application/json", h.Seen.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("act-1", h.Seen.Headers.GetValues("X-ReleaseOS-Delivery").Single());
        Assert.Equal("1792504800", h.Seen.Headers.GetValues("X-ReleaseOS-Timestamp").Single());
        Assert.Equal(d.Signature, h.Seen.Headers.GetValues("X-ReleaseOS-Signature").Single());
        Assert.StartsWith("v1=", d.Signature);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "HTTP 404")]
    [InlineData(HttpStatusCode.TooManyRequests, "HTTP 429")]
    [InlineData(HttpStatusCode.InternalServerError, "HTTP 500")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "HTTP 503")]
    public async Task A_refusal_is_a_failed_result_with_the_status_and_the_call_is_made_once_never_retried(HttpStatusCode status, string reason)
    {
        var (sender, h, target) = Make("https://deploy.example.com/hooks/abc123", (_, _) => Task.FromResult(new HttpResponseMessage(status)), AllowPrivate);
        var r = await sender.SendAsync(target, Delivery(), CancellationToken.None);
        Assert.Equal((false, (int)status, reason), (r.Delivered, r.HttpStatus, r.Reason));
        Assert.Equal(1, h.Calls);   // a rollback is not repeated blindly
        Assert.DoesNotContain("abc123", r.Reason);
    }

    [Fact]
    public async Task A_redirect_is_not_followed_a_slow_answer_times_out_and_a_transport_error_names_only_its_type()
    {
        var redirect = Make("https://deploy.example.com/hooks/abc123", (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://elsewhere.example/") } }), AllowPrivate);
        var r = await redirect.Sender.SendAsync(redirect.Target, Delivery(), CancellationToken.None);
        Assert.Equal((false, 302), (r.Delivered, r.HttpStatus));
        Assert.Contains("redirects are not followed", r.Reason);

        var slow = Make("https://deploy.example.com/hooks/abc123", async (_, _) => { await Task.Delay(TimeSpan.FromSeconds(20)); return new HttpResponseMessage(HttpStatusCode.OK); }, AllowPrivate, ("Control:Webhooks:TimeoutSeconds", "0.2"));
        var t = await slow.Sender.SendAsync(slow.Target, Delivery(), CancellationToken.None);
        Assert.False(t.Delivered);
        Assert.Contains("no answer within", t.Reason);

        var broken = Make("https://deploy.example.com/hooks/abc123", (_, _) => throw new HttpRequestException("connection refused to https://deploy.example.com/hooks/abc123 with token abc123"), AllowPrivate);
        var b = await broken.Sender.SendAsync(broken.Target, Delivery(), CancellationToken.None);
        Assert.False(b.Delivered);
        Assert.DoesNotContain("abc123", b.Reason);   // the address is the secret
        Assert.Contains("HttpRequestException", b.Reason);

        var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slow.Sender.SendAsync(slow.Target, Delivery(), cancelled.Token));   // shutting down is not a delivery result
    }

    [Fact]
    public async Task The_response_body_is_never_read()
    {
        var read = false;
        var (sender, _, target) = Make("https://deploy.example.com/hooks/abc123", (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ThrowingStream(() => read = true)) }), AllowPrivate);
        Assert.True((await sender.SendAsync(target, Delivery(), CancellationToken.None)).Delivered);
        Assert.False(read);
    }

    private sealed class ThrowingStream(Action onRead) : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false; public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { onRead(); throw new IOException("a hostile endpoint streams forever"); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData("http://deploy.example.com/hooks/abc123", "only https")]
    [InlineData("https://user:pass@deploy.example.com/hooks/abc123", "credentials in the URL")]
    [InlineData("https://127.0.0.1:8443/hooks/abc123", "private or local")]
    [InlineData("https://10.0.0.5/hooks/abc123", "private or local")]
    [InlineData("https://192.168.1.20/hooks/abc123", "private or local")]
    [InlineData("https://169.254.169.254/latest/meta-data", "private or local")]
    [InlineData("https://100.64.0.9/hooks/abc123", "private or local")]
    [InlineData("https://[::1]/hooks/abc123", "private or local")]
    [InlineData("https://[::ffff:10.0.0.5]/hooks/abc123", "private or local")]
    public async Task An_address_that_is_not_https_carries_credentials_or_is_private_is_refused_before_any_connection(string url, string why)
    {
        var (sender, h, target) = Make(url);
        var r = await sender.SendAsync(target, Delivery(), CancellationToken.None);
        Assert.False(r.Delivered);
        Assert.Contains(why, r.Reason);
        Assert.Equal(0, h.Calls);
        Assert.DoesNotContain("abc123", r.Reason);
        Assert.DoesNotContain("pass", r.Reason);
    }

    [Fact]
    public async Task Allowing_private_targets_is_a_setting_and_an_unreadable_address_is_a_failed_result()
    {
        var (sender, h, target) = Make("https://10.0.0.5/hooks/abc123", settings: AllowPrivate);
        Assert.True((await sender.SendAsync(target, Delivery(), CancellationToken.None)).Delivered);
        Assert.Equal(1, h.Calls);
        var broken = target with { ProtectedUrl = "CfDJ8-not-a-payload" };
        var r = await sender.SendAsync(broken, Delivery(), CancellationToken.None);
        Assert.False(r.Delivered);
        Assert.Equal(1, h.Calls);
    }

    [Fact]
    public async Task The_real_client_checks_the_address_again_at_connect_time_so_a_name_that_resolves_to_a_private_address_never_connects()
    {
        using var f = new ApiFactory();
        f.CreateClient();
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start();
        var accepted = 0;
        _ = Task.Run(async () => { try { while (true) { using var c = await l.AcceptTcpClientAsync(); Interlocked.Increment(ref accepted); } } catch (Exception ex) when (ex is ObjectDisposedException or SocketException or InvalidOperationException) { } });
        try
        {
            var client = f.Services.GetRequiredService<IHttpClientFactory>().CreateClient(ControlWebhookSender.ClientName);
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            await Assert.ThrowsAsync<HttpRequestException>(() => client.PostAsync($"http://127.0.0.1:{port}/hooks/x", new StringContent("{}", Encoding.UTF8, "application/json")));
            await Task.Delay(200);
            Assert.Equal(0, Volatile.Read(ref accepted));
        }
        finally { l.Stop(); }
    }
}
