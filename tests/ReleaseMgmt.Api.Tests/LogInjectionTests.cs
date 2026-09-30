using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using ReleaseMgmt.Api.Endpoints;
using ReleaseMgmt.Domain.Common;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// Tests that read the Serilog log file. <c>UseSerilog</c> (Program.cs) routes every in-process host through the static <c>Log.Logger</c>, which the last host
/// built replaces and a disposed host resets, so while other test hosts start and stop in parallel an event can land in another host's file. These tests
/// therefore run alone, after the parallel ones.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LogFileCollection { public const string Name = "Log file (not parallel)"; }

/// <summary>Security review SEC-D5 (docs/security/scan-resources-injection.md): a logged value cannot forge a log entry.</summary>
[Collection(LogFileCollection.Name)]
public class LogInjectionTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private sealed class Http500 : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
    }

    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        return sr.ReadToEnd();
    }

    private static string LogText(ApiFactory f) => string.Concat(Directory.GetFiles(Path.GetDirectoryName(f.DbPath)!, "log-*.txt").Select(ReadShared));

    [Fact]
    public async Task A_name_typed_into_the_app_cannot_forge_a_line_in_the_log_file()
    {
        using var root = new ApiFactory();
        using var web = root.WithWebHostBuilder(b =>
        {
            b.UseSetting("Comms:Webhooks:AllowPrivateTargets", "true");
            b.ConfigureServices(s => s.AddHttpClient(CommWebhookSender.ClientName).ConfigurePrimaryHttpMessageHandler(() => new Http500()));
        });
        var rte = web.CreateClient();
        (await rte.PostAsJsonAsync("/auth/dev-login", new { email = "rte@x.com", name = "rte", role = Roles.RTE })).EnsureSuccessStatusCode();
        SeedTrain(root, UserId(root, "rte@x.com"), UserId(root, "rte@x.com"));
        Sql(root, "INSERT INTO CommTemplates(Id,ReleaseTrainId,TemplateType,Audience,SubjectLine,MarkdownBody) VALUES('c1','t1','GoNoGo','All','Subject','Body');");

        // An administrator names a channel (1 to 80 characters, line breaks accepted); a failed delivery logs the name.
        const string forged = "2020-01-01 00:00:00.000 +00:00 [INF] FORGED-ENTRY backup ok";
        var hook = await rte.PostAsJsonAsync("/api/v1/sync/webhook-allowlist", new { name = "ops\r\n" + forged, url = "https://hooks.example.test/services/T/B/X", kind = "Teams" });
        Assert.True(hook.IsSuccessStatusCode, await hook.Content.ReadAsStringAsync());
        var id = (await Json(hook)).GetProperty("id").GetString();
        var sent = await rte.PostAsJsonAsync("/api/v1/trains/t1/comms:dispatch", new { templateId = "c1", channel = "Webhook", webhookDestinationId = id });
        Assert.Equal("Failed", (await Json(sent)).GetProperty("outcome").GetString());

        var text = LogText(root);
        Assert.Contains("FORGED-ENTRY", text);   // the failure was logged, with the name ...
        Assert.DoesNotContain(text.Split('\n'), l => l.TrimEnd('\r').StartsWith(forged, StringComparison.Ordinal));   // ... inside its own entry, not as a new one
        Assert.Contains("ops\\r\\n" + forged, text);
    }

    [Fact]
    public async Task A_request_path_or_header_cannot_forge_a_line_in_the_log_file()   // checked, no issue: pinned
    {
        using var f = new ApiFactory();
        var c = f.CreateClient();
        const string forged = "2020-01-01 00:00:00.000 +00:00 [ERR] FORGED-PATH admin signed in";
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/x%0D%0A" + Uri.EscapeDataString(forged)) { Content = JsonContent.Create(new { }) };
        req.Headers.TryAddWithoutValidation("Origin", "https://evil.example");   // refused and logged by the cross-site guard, before any sign-in
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(req)).StatusCode);

        var text = LogText(f);
        Assert.Contains("FORGED-PATH", text);
        Assert.DoesNotContain(text.Split('\n'), l => l.TrimEnd('\r').StartsWith(forged, StringComparison.Ordinal));   // PathString logs its escaped form
    }
}
