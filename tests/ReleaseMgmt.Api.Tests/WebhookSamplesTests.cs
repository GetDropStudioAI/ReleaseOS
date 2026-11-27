using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ReleaseMgmt.Api.Reminders;
using ReleaseMgmt.Api.Webhooks;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Webhooks;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// REOS-165: the receiver samples in docs/samples/webhooks are run against what the real sender puts on the wire. A delivery is signed by the real <see cref="WebhookSignature"/>, posted through the real
/// <see cref="OutboundWebhookSender"/> (its HTTP handler is the only stub), and the headers and body the receiver would see are handed to the C#, JavaScript and Python samples in <c>--check</c> mode.
/// The body has non-ASCII text so a sample that re-encodes or parses the body fails. Needs python3 (or python), node and dotnet on the PATH, as CI has; a missing one fails the test, it is not skipped.
/// </summary>
public class WebhookSamplesTests
{
    private sealed class Capture : HttpMessageHandler
    {
        public Dictionary<string, string> Headers = [];
        public string Body = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            foreach (var h in request.Headers) Headers[h.Key] = h.Value.Single();
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
    private sealed class Factory(HttpMessageHandler h) : IHttpClientFactory { public HttpClient CreateClient(string name) => new(h, disposeHandler: false); }

    private static string SamplesDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "docs", "samples", "webhooks"))) return Path.Combine(d.FullName, "docs", "samples", "webhooks");
        throw new DirectoryNotFoundException("docs/samples/webhooks not found above " + AppContext.BaseDirectory);
    }

    private static (int Exit, string Output) Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start " + file);
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(240_000)) { p.Kill(true); throw new TimeoutException(file + " did not finish"); }
        return (p.ExitCode, (output.Result + error.Result).Trim());
    }

    private static string Python()
    {
        foreach (var n in new[] { "python3", "python" })
        {
            try { if (Run(n, "-c", "import sys; assert sys.version_info >= (3, 10)").Exit == 0) return n; } catch (System.ComponentModel.Win32Exception) { }
        }
        throw new InvalidOperationException("python 3.10+ is required on the PATH to run docs/samples/webhooks/verify.py");
    }

    [Fact]
    public async Task The_C_sharp_JavaScript_and_Python_samples_verify_a_delivery_the_real_sender_signed_and_refuse_a_changed_one()
    {
        const string secret = "whsec_" + "abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG";
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var body = "{\"id\":\"evt_4182\",\"type\":\"gate.failed\",\"occurredAt\":\"2026-11-26T12:00:00Z\",\"data\":{\"note\":\"Zürich – 東京 ✓\"}}";
        var deliveryId = Guid.CreateVersion7().ToString();

        var capture = new Capture();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Webhooks:Out:AllowPrivateTargets"] = "true" }).Build();
        var vault = WebhookTestRows.Ephemeral();
        var sender = new OutboundWebhookSender(new Factory(capture), WebhookOutOptions.From(config, isDevelopment: true), NullLogger<OutboundWebhookSender>.Instance, vault, OutboundAddressPolicy.Default);
        const string url = "https://hooks.example.com/releaseos";
        var target = new WebhookTarget("s1", "Dashboard", new Uri(url).Authority, vault.Protect(url).ProtectedUrl);
        var sent = await sender.SendAsync(target, new WebhookRequest(deliveryId, now, "gate.failed", 1, body, WebhookSignature.Sign(secret, now, deliveryId, body)), CancellationToken.None);
        Assert.True(sent.Delivered);

        var dir = Directory.CreateTempSubdirectory("reos-samples-").FullName;
        try
        {
            string Write(string name, string withBody)
            {
                var path = Path.Combine(dir, name);
                File.WriteAllText(path, JsonSerializer.Serialize(new
                {
                    secret, timestamp = capture.Headers["X-ReleaseOS-Timestamp"], deliveryId = capture.Headers["X-ReleaseOS-Delivery"], signature = capture.Headers["X-ReleaseOS-Signature"], body = withBody, now,
                }));
                return path;
            }
            var good = Write("good.json", capture.Body);
            var changed = Write("changed.json", capture.Body.Replace("gate.failed", "gate.passed"));
            Assert.Equal(body, capture.Body);   // the sender sent the body it was given, byte for byte

            var samples = SamplesDir();
            var runners = new (string Name, Func<string, (int, string)> Check)[]
            {
                ("Python", f => Run(Python(), "-I", Path.Combine(samples, "verify.py"), "--check", f)),
                ("JavaScript", f => Run("node", Path.Combine(samples, "verify.mjs"), "--check", f)),
                ("C#", f => Run("dotnet", "run", Path.Combine(samples, "Verify.cs"), "--", "--check", f)),
            };
            foreach (var (name, check) in runners)
            {
                Assert.Equal((0, "ok"), check(good));
                var refused = check(changed);
                Assert.Equal(1, refused.Item1);
                Assert.Contains("FAILED", refused.Item2);
                _ = name;
            }
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
