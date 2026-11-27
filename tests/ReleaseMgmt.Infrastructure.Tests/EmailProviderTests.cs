using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ReleaseMgmt.Infrastructure.Email;

namespace ReleaseMgmt.Infrastructure.Tests;

/// <summary>
/// REOS-167: the real providers, with no real mail: the SMTP provider against an in-process fake SMTP server (a loopback socket speaking just enough SMTP), the SendGrid provider against a stub HTTP handler. The outcome
/// classes are what the processor acts on: a refused recipient is a hard bounce, a 4xx or a dead connection is temporary, a refusal that is not about the recipient is permanent without suppressing anyone.
/// </summary>
public sealed class EmailProviderTests
{
    private sealed class MemoryCredentials(string? value) : IEmailCredentials
    {
        public Task<bool> HasAsync(string name, CancellationToken ct = default) => Task.FromResult(value is not null);
        public Task<string?> GetAsync(string name, CancellationToken ct = default) => Task.FromResult(value);
        public Task SetAsync(string name, string v, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ClearAsync(string name, CancellationToken ct = default) => Task.FromResult(true);
    }

    /// <summary>A minimal SMTP server: records the DATA of each message and answers RCPT, MAIL and DATA by rule.</summary>
    private sealed class FakeSmtpServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public List<string> Messages { get; } = [];
        public List<string> Commands { get; } = [];
        public Func<string, string?> RcptAnswer { get; set; } = _ => null;     // null = 250
        public Func<string, string?> MailAnswer { get; set; } = _ => null;

        public FakeSmtpServer() { _listener.Start(); _ = Task.Run(AcceptAsync); }

        private async Task AcceptAsync()
        {
            try { while (!_stop.IsCancellationRequested) { var c = await _listener.AcceptTcpClientAsync(_stop.Token); _ = Task.Run(() => ServeAsync(c)); } }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using var _ = client;
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            async Task Say(string s) { var b = Encoding.ASCII.GetBytes(s + "\r\n"); await stream.WriteAsync(b); }
            try
            {
                await Say("220 fake.example.com ESMTP");
                while (await reader.ReadLineAsync(_stop.Token) is { } line)
                {
                    lock (Commands) Commands.Add(line);
                    var cmd = line.Split(' ')[0].ToUpperInvariant();
                    if (cmd is "EHLO") await Say("250-fake.example.com\r\n250 8BITMIME");
                    else if (cmd == "HELO") await Say("250 fake.example.com");
                    else if (cmd == "MAIL") await Say(MailAnswer(line) ?? "250 2.1.0 OK");
                    else if (cmd == "RCPT") await Say(RcptAnswer(line) ?? "250 2.1.5 OK");
                    else if (cmd == "RSET" || cmd == "NOOP") await Say("250 OK");
                    else if (cmd == "DATA")
                    {
                        await Say("354 End data with <CR><LF>.<CR><LF>");
                        var sb = new StringBuilder();
                        while (await reader.ReadLineAsync(_stop.Token) is { } l && l != ".") sb.AppendLine(l);
                        lock (Messages) Messages.Add(sb.ToString());
                        await Say("250 2.0.0 queued as FAKE123");
                    }
                    else if (cmd == "QUIT") { await Say("221 bye"); break; }
                    else await Say("502 not implemented");
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        }

        public void Dispose() { _stop.Cancel(); _listener.Stop(); }
    }

    private static EmailOptions Options(int port, params (string, string)[] extra)
    {
        var kv = new Dictionary<string, string?>
        {
            ["Notifications:Smtp:Enabled"] = "true", ["Notifications:Email:From:Address"] = "releaseos@example.com", ["Notifications:Email:Smtp:Host"] = "127.0.0.1",
            ["Notifications:Email:Smtp:Port"] = port.ToString(), ["Notifications:Email:Smtp:Security"] = "None",
        };
        foreach (var (k, v) in extra) kv[k] = v;
        return EmailOptions.From(new ConfigurationBuilder().AddInMemoryCollection(kv).Build());
    }

    private static EmailEnvelope Mail(string to = "person@example.com") =>
        new(to, "releaseos@example.com", "ReleaseOS", "[Payments] Approval requested: RLS-7", "An approval is waiting.\nOpen: https://releaseos.example.com/approvals\n", "<p>An approval is waiting.</p>", "msg-1");

    [Fact]
    public async Task Smtp_sends_a_multipart_message_with_the_message_key_and_no_credential_when_none_is_set()
    {
        using var server = new FakeSmtpServer();
        var p = new SmtpEmailProvider(Options(server.Port), new MemoryCredentials(null), NullLogger<SmtpEmailProvider>.Instance);
        var r = await p.SendAsync(Mail(), default);
        Assert.Equal(EmailOutcome.Sent, r.Outcome);
        var data = Assert.Single(server.Messages);
        Assert.Contains("Subject: [Payments] Approval requested: RLS-7", data);
        Assert.Contains("X-ReleaseOS-Message: msg-1", data);
        Assert.Contains("Auto-Submitted: auto-generated", data);
        Assert.Contains("multipart/alternative", data);
        Assert.Contains("text/plain", data);
        Assert.Contains("text/html", data);
        Assert.Contains(server.Commands, c => c.StartsWith("RCPT TO:<person@example.com>", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(server.Commands, c => c.StartsWith("MAIL FROM:<releaseos@example.com>", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(server.Commands, c => c.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Smtp_classifies_what_the_server_says()
    {
        using var server = new FakeSmtpServer();
        var p = new SmtpEmailProvider(Options(server.Port), new MemoryCredentials(null), NullLogger<SmtpEmailProvider>.Instance);

        server.RcptAnswer = _ => "550 5.1.1 <person@example.com>: Recipient address rejected: User unknown";
        var rejected = await p.SendAsync(Mail(), default);
        Assert.Equal(EmailOutcome.Rejected, rejected.Outcome);   // a hard bounce: the address is suppressed
        Assert.Empty(server.Messages);

        server.RcptAnswer = _ => "451 4.3.0 Try again later";
        var temp = await p.SendAsync(Mail(), default);
        Assert.Equal(EmailOutcome.Temporary, temp.Outcome);

        server.RcptAnswer = _ => null;
        server.MailAnswer = _ => "554 5.7.1 Sender refused";
        var failed = await p.SendAsync(Mail(), default);
        Assert.Equal(EmailOutcome.Failed, failed.Outcome);   // permanent, but not the recipient's fault: nobody is suppressed
        Assert.Empty(server.Messages);

        server.MailAnswer = _ => "452 4.2.2 Mailbox full";
        Assert.Equal(EmailOutcome.Temporary, (await p.SendAsync(Mail(), default)).Outcome);
    }

    [Fact]
    public async Task Smtp_to_a_server_that_is_not_there_is_temporary_and_the_reason_names_no_address_or_body()
    {
        int port;
        using (var l = new TcpListener(IPAddress.Loopback, 0)) { l.Start(); port = ((IPEndPoint)l.LocalEndpoint).Port; }   // a port nothing listens on
        var p = new SmtpEmailProvider(Options(port), new MemoryCredentials(null), NullLogger<SmtpEmailProvider>.Instance);
        var r = await p.SendAsync(Mail("secret.person@example.com"), default);
        Assert.Equal(EmailOutcome.Temporary, r.Outcome);
        Assert.DoesNotContain("secret.person", r.Reason ?? "");
        Assert.DoesNotContain("approval is waiting", r.Reason ?? "");
    }

    [Fact]
    public async Task Smtp_with_a_user_name_and_no_stored_password_is_temporary_and_never_connects()
    {
        using var server = new FakeSmtpServer();
        var p = new SmtpEmailProvider(Options(server.Port, ("Notifications:Email:Smtp:Host", "localhost"), ("Notifications:Email:Smtp:UserName", "mailer")), new MemoryCredentials(null), NullLogger<SmtpEmailProvider>.Instance);
        var r = await p.SendAsync(Mail(), default);
        Assert.Equal(EmailOutcome.Temporary, r.Outcome);
        Assert.Contains("password", r.Reason);
        Assert.Empty(server.Commands);
    }

    // ---- SendGrid ----

    private sealed class StubHandler(Func<HttpRequestMessage, string, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request, body));
            return answer(request, body);
        }
    }

    private sealed class Factory(HttpMessageHandler h) : IHttpClientFactory { public HttpClient CreateClient(string name) => new(h, disposeHandler: false); }

    private static EmailOptions SendGridOptions() => EmailOptions.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Notifications:Smtp:Enabled"] = "true", ["Notifications:Email:Provider"] = "SendGrid", ["Notifications:Email:From:Address"] = "releaseos@example.com",
    }).Build());

    [Fact]
    public async Task SendGrid_posts_the_message_with_the_stored_key_and_returns_the_provider_id()
    {
        var h = new StubHandler((_, _) => { var r = new HttpResponseMessage(HttpStatusCode.Accepted); r.Headers.Add("X-Message-Id", "sg-abc123"); return r; });
        var p = new SendGridEmailProvider(new Factory(h), SendGridOptions(), new MemoryCredentials("SG.the-api-key"));
        var r = await p.SendAsync(Mail(), default);
        Assert.Equal(EmailOutcome.Sent, r.Outcome);
        Assert.Equal("sg-abc123", r.ProviderMessageId);
        var (req, body) = Assert.Single(h.Calls);
        Assert.Equal("https://api.sendgrid.com/v3/mail/send", req.RequestUri!.ToString());
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        Assert.Equal("SG.the-api-key", req.Headers.Authorization.Parameter);
        Assert.DoesNotContain("SG.the-api-key", body);   // the key travels in the header only
        var j = JsonDocument.Parse(body).RootElement;
        Assert.Equal("person@example.com", j.GetProperty("personalizations")[0].GetProperty("to")[0].GetProperty("email").GetString());
        Assert.Equal("releaseos@example.com", j.GetProperty("from").GetProperty("email").GetString());
        Assert.Equal("text/plain", j.GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("text/html", j.GetProperty("content")[1].GetProperty("type").GetString());
        Assert.Equal("msg-1", j.GetProperty("custom_args").GetProperty("releaseos_message").GetString());
    }

    [Theory]
    [InlineData(429, EmailOutcome.Temporary)]
    [InlineData(500, EmailOutcome.Temporary)]
    [InlineData(503, EmailOutcome.Temporary)]
    [InlineData(401, EmailOutcome.Temporary)]   // a bad key is configuration: kept for retry, never a suppression
    [InlineData(403, EmailOutcome.Temporary)]
    [InlineData(400, EmailOutcome.Failed)]
    [InlineData(413, EmailOutcome.Failed)]
    public async Task SendGrid_status_classes(int status, EmailOutcome expected)
    {
        var h = new StubHandler((_, _) => new HttpResponseMessage((HttpStatusCode)status));
        var r = await new SendGridEmailProvider(new Factory(h), SendGridOptions(), new MemoryCredentials("k")).SendAsync(Mail(), default);
        Assert.Equal(expected, r.Outcome);
        Assert.DoesNotContain("person@example.com", r.Reason ?? "");
    }

    [Fact]
    public async Task SendGrid_without_a_stored_key_or_with_the_network_down_is_temporary()
    {
        var h = new StubHandler((_, _) => throw new HttpRequestException("down"));
        var noKey = await new SendGridEmailProvider(new Factory(h), SendGridOptions(), new MemoryCredentials(null)).SendAsync(Mail(), default);
        Assert.Equal(EmailOutcome.Temporary, noKey.Outcome);
        Assert.Empty(h.Calls);
        var down = await new SendGridEmailProvider(new Factory(h), SendGridOptions(), new MemoryCredentials("k")).SendAsync(Mail(), default);
        Assert.Equal(EmailOutcome.Temporary, down.Outcome);
    }

    [Fact]
    public async Task The_credential_store_is_protected_per_name_and_a_changed_key_ring_reads_as_absent()
    {
        var dir = Directory.CreateTempSubdirectory("reos-email-cred-").FullName;
        try
        {
            var opts = EmailOptions.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Notifications:Email:CredentialsDirectory"] = dir }).Build());
            var ring1 = new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider();
            var store = new DataProtectionEmailCredentials(ring1, opts);
            Assert.False(await store.HasAsync(IEmailCredentials.Provider));
            await store.SetAsync(IEmailCredentials.Provider, "  s3cret-value  ");
            Assert.Equal("s3cret-value", await store.GetAsync(IEmailCredentials.Provider));
            Assert.DoesNotContain("s3cret", await File.ReadAllTextAsync(Path.Combine(dir, "provider.secret")));
            Assert.Null(await store.GetAsync(IEmailCredentials.Events));
            // the same file read as the other secret (copied over) does not unprotect: the purpose names the secret
            File.Copy(Path.Combine(dir, "provider.secret"), Path.Combine(dir, "events.secret"));
            Assert.Null(await store.GetAsync(IEmailCredentials.Events));
            // another key ring cannot read it: absent, not an exception
            Assert.Null(await new DataProtectionEmailCredentials(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(), opts).GetAsync(IEmailCredentials.Provider));
            Assert.True(await store.ClearAsync(IEmailCredentials.Provider));
            Assert.False(await store.ClearAsync(IEmailCredentials.Provider));
            await Assert.ThrowsAsync<ArgumentException>(() => store.GetAsync("../escape"));
            await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync(IEmailCredentials.Provider, " "));
        }
        finally { Directory.Delete(dir, true); }
    }
}
