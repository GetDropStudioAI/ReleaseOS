using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ReleaseMgmt.Api.Reminders;
using ReleaseMgmt.Api.Webhooks;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Webhooks;

namespace ReleaseMgmt.Api.Tests;

/// <summary>REOS-164: the outbound sender posts the signed request once with the five headers and the protections of every outbound webhook; the address is never shown or logged.</summary>
public class OutboundWebhookSenderTests
{
    private sealed class Stub(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls; public HttpRequestMessage? Seen; public string? SeenBody;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls); Seen = request;
            SeenBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return await respond(request).WaitAsync(ct);
        }
    }
    private sealed class Factory(HttpMessageHandler h) : IHttpClientFactory { public HttpClient CreateClient(string name) => new(h, disposeHandler: false); }
    private sealed class Logs : ILogger<OutboundWebhookSender>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception) + exception);
    }

    private static (OutboundWebhookSender Sender, Stub Handler, WebhookTarget Target, Logs Log) Make(string url, Func<HttpRequestMessage, Task<HttpResponseMessage>>? respond = null, params (string Key, string Value)[] settings)
    {
        var handler = new Stub(respond ?? (_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value)).Build();
        var vault = WebhookTestRows.Ephemeral();
        var log = new Logs();
        var sender = new OutboundWebhookSender(new Factory(handler), WebhookOutOptions.From(config, isDevelopment: true), log, vault, OutboundAddressPolicy.Default);
        var uri = new Uri(url);
        return (sender, handler, new WebhookTarget("s1", "Dashboard", uri.Authority, vault.Protect(url).ProtectedUrl), log);
    }

    private static WebhookRequest Req() => new("del-1", 1792504800, "gate.failed", 3, "{\"id\":\"evt_1\"}", WebhookSignature.Sign("whsec_k", 1792504800, "del-1", "{\"id\":\"evt_1\"}"));
    private static readonly (string, string) AllowPrivate = ("Webhooks:Out:AllowPrivateTargets", "true");   // skips the DNS lookup; the address rules have their own tests below

    [Fact]
    public async Task The_request_carries_the_body_and_the_five_headers()
    {
        var (sender, h, target, _) = Make("https://hooks.example.com/in/tok3n", settings: AllowPrivate);
        var rq = Req();
        var r = await sender.SendAsync(target, rq, CancellationToken.None);
        Assert.Equal((true, 200, null), (r.Delivered, r.HttpStatus, r.Reason));
        Assert.Equal(HttpMethod.Post, h.Seen!.Method);
        Assert.Equal("https://hooks.example.com/in/tok3n", h.Seen.RequestUri!.ToString());
        Assert.Equal(rq.Body, h.SeenBody);
        Assert.Equal("application/json", h.Seen.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("del-1", h.Seen.Headers.GetValues("X-ReleaseOS-Delivery").Single());
        Assert.Equal("1792504800", h.Seen.Headers.GetValues("X-ReleaseOS-Timestamp").Single());
        Assert.Equal(rq.Signature, h.Seen.Headers.GetValues("X-ReleaseOS-Signature").Single());
        Assert.Equal("gate.failed", h.Seen.Headers.GetValues("X-ReleaseOS-Event").Single());
        Assert.Equal("3", h.Seen.Headers.GetValues("X-ReleaseOS-Secret-Version").Single());
        Assert.StartsWith("v1=", rq.Signature);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "HTTP 400")]
    [InlineData(HttpStatusCode.NotFound, "HTTP 404")]
    [InlineData(HttpStatusCode.TooManyRequests, "HTTP 429")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "HTTP 503")]
    public async Task A_refusal_is_a_failed_result_with_the_status_made_once_the_processor_owns_retries(HttpStatusCode status, string reason)
    {
        var (sender, h, target, _) = Make("https://hooks.example.com/in/tok3n", _ => Task.FromResult(new HttpResponseMessage(status)), AllowPrivate);
        var r = await sender.SendAsync(target, Req(), CancellationToken.None);
        Assert.Equal((false, (int)status, reason), (r.Delivered, r.HttpStatus, r.Reason));
        Assert.Equal(1, h.Calls);
        Assert.DoesNotContain("tok3n", r.Reason);
    }

    [Fact]
    public async Task A_redirect_is_not_followed_a_slow_answer_times_out_and_a_transport_error_names_only_its_type_and_nothing_logs_the_address()
    {
        var redirect = Make("https://hooks.example.com/in/tok3n", _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://elsewhere.example/") } }), AllowPrivate);
        var r = await redirect.Sender.SendAsync(redirect.Target, Req(), CancellationToken.None);
        Assert.Equal((false, 302), (r.Delivered, r.HttpStatus));
        Assert.Contains("redirects are not followed", r.Reason);

        var slow = Make("https://hooks.example.com/in/tok3n", async _ => { await Task.Delay(TimeSpan.FromSeconds(20)); return new HttpResponseMessage(HttpStatusCode.OK); }, AllowPrivate, ("Webhooks:Out:TimeoutSeconds", "0.2"));
        var t = await slow.Sender.SendAsync(slow.Target, Req(), CancellationToken.None);
        Assert.False(t.Delivered);
        Assert.Contains("no answer within", t.Reason);

        var broken = Make("https://hooks.example.com/in/tok3n", _ => throw new HttpRequestException("connection refused to https://hooks.example.com/in/tok3n"), AllowPrivate);
        var b = await broken.Sender.SendAsync(broken.Target, Req(), CancellationToken.None);
        Assert.Equal((false, null), (b.Delivered, b.HttpStatus));
        Assert.DoesNotContain("tok3n", b.Reason);
        Assert.Contains("HttpRequestException", b.Reason);

        var exploding = Make("https://hooks.example.com/in/tok3n", _ => throw new InvalidOperationException("boom at https://hooks.example.com/in/tok3n"), AllowPrivate);
        var x = await exploding.Sender.SendAsync(exploding.Target, Req(), CancellationToken.None);
        Assert.False(x.Delivered);
        Assert.DoesNotContain("tok3n", x.Reason);
        Assert.All(exploding.Log.Lines, l => Assert.DoesNotContain("tok3n", l));   // the address is the secret: only the type is logged

        var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slow.Sender.SendAsync(slow.Target, Req(), cancelled.Token));
    }

    [Theory]
    [InlineData("http://hooks.example.com/in", "https")]
    [InlineData("https://u:p@hooks.example.com/in", "credentials")]
    [InlineData("https://127.0.0.1/in", "private")]
    [InlineData("https://10.0.0.5/in", "private")]
    [InlineData("https://[::1]/in", "private")]
    [InlineData("https://169.254.169.254/in", "private")]
    public async Task The_address_rules_are_applied_again_at_send_time_and_nothing_is_called(string url, string word)
    {
        var (sender, h, target, _) = Make(url);
        var r = await sender.SendAsync(target, Req(), CancellationToken.None);
        Assert.False(r.Delivered);
        Assert.Contains(word, r.Reason);
        Assert.Equal(0, h.Calls);
    }

    [Fact]
    public async Task A_host_removed_from_the_allow_list_stops_receiving_at_once_and_a_closed_list_sends_nothing()
    {
        var (sender, h, target, _) = Make("https://hooks.example.com/in/tok3n", settings: [AllowPrivate, ("Webhooks:Out:AllowedHosts", "other.example.com")]);
        var r = await sender.SendAsync(target, Req(), CancellationToken.None);
        Assert.False(r.Delivered);
        Assert.Contains("no longer on the outbound allow-list", r.Reason);
        Assert.Equal(0, h.Calls);

        var allowed = Make("https://hooks.example.com/in/tok3n", settings: [AllowPrivate, ("Webhooks:Out:AllowedHosts", "*.example.com")]);
        Assert.True((await allowed.Sender.SendAsync(allowed.Target, Req(), CancellationToken.None)).Delivered);

        var vault = WebhookTestRows.Ephemeral();
        var closed = new OutboundWebhookSender(new Factory(new Stub(_ => Task.FromResult(new HttpResponseMessage()))), WebhookOutOptions.From(new ConfigurationBuilder().Build(), isDevelopment: false), new Logs(), vault, OutboundAddressPolicy.Default);
        var c = await closed.SendAsync(new("s1", "x", "hooks.example.com", vault.Protect("https://hooks.example.com/in").ProtectedUrl), Req(), CancellationToken.None);
        Assert.Contains("no host is on the outbound allow-list", c.Reason);
    }

    [Fact]
    public async Task The_response_body_is_never_read()
    {
        var read = false;
        var (sender, _, target, _) = Make("https://hooks.example.com/in/tok3n", _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ThrowingStream(() => read = true)) }), AllowPrivate);
        Assert.True((await sender.SendAsync(target, Req(), CancellationToken.None)).Delivered);
        Assert.False(read);
    }

    private sealed class ThrowingStream(Action onRead) : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false; public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { onRead(); return 0; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
