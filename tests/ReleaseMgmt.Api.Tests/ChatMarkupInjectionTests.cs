using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ReleaseMgmt.Api.Endpoints;
using ReleaseMgmt.Api.Reminders;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Reminders;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// Security review SEC-D6 (OWASP A05 injection into a downstream interpreter): text people type (train titles, gate names, reasons) reaches Slack and Teams
/// channels, which render markup. Slack's control sequences (<c>&lt;!channel&gt;</c>, <c>&lt;https://x|label&gt;</c>) are neutralised only by HTML entities,
/// not by Markdown backslashes; Teams renders Markdown links. Values must arrive as text, and template text the author wrote must be left alone.
/// </summary>
public class ChatMarkupInjectionTests
{
    private const string Evil = "R26.10 <!channel> <https://evil.example|Reset your password> [Sign in](https://evil.example)";

    private sealed class Capture : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            lock (Bodies) Bodies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task A_dispatched_value_cannot_ping_a_Slack_channel_or_post_a_Slack_link_and_Teams_keeps_its_Markdown_escaping()
    {
        using var root = new ApiFactory();
        var capture = new Capture();
        using var web = root.WithWebHostBuilder(b =>
        {
            b.UseSetting("Comms:Webhooks:AllowPrivateTargets", "true");   // the capture handler has no DNS
            b.ConfigureServices(s => s.AddHttpClient(CommWebhookSender.ClientName).ConfigurePrimaryHttpMessageHandler(() => capture));
        });
        var rte = web.CreateClient();
        (await rte.PostAsJsonAsync("/auth/dev-login", new { email = "rte@x.com", name = "rte", role = Roles.RTE })).EnsureSuccessStatusCode();
        Sql(root, $@"
            INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CreatedAt,UpdatedAt) VALUES('t1','{Evil}','2026-10-30','Low','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z');
            INSERT INTO CommTemplates(Id,ReleaseTrainId,TemplateType,Audience,SubjectLine,MarkdownBody) VALUES('c1','t1','GoNoGo','All','{{ReleaseTitle}}','Update for {{ReleaseTitle}}, see <https://wiki.example/runbook|the runbook>');
            INSERT INTO WebhookDestinations(Id,Name,Url,Kind) VALUES('ws','slack','https://hooks.example.test/services/T/B/S','Slack'),('wt','teams','https://teams.example.test/webhook/T','Teams');");

        foreach (var dest in new[] { "ws", "wt" })
        {
            var r = await rte.PostAsJsonAsync("/api/v1/trains/t1/comms:dispatch", new { templateId = "c1", channel = "Webhook", webhookDestinationId = dest });
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        }
        Assert.Equal(2, capture.Bodies.Count);
        var slack = JsonDocument.Parse(capture.Bodies[0]).RootElement.GetProperty("text").GetString()!;
        var teams = JsonDocument.Parse(capture.Bodies[1]).RootElement.GetProperty("text").GetString()!;

        Assert.DoesNotContain("<!channel", slack);
        Assert.DoesNotContain("<https://evil", slack);
        Assert.Contains("&lt;!channel&gt;", slack);
        Assert.Contains("<https://wiki.example/runbook|the runbook>", slack);   // the author's own Slack link, from the template text, still works

        Assert.Contains("\\<!channel\\>", teams);                                   // unchanged for Teams: Markdown escaping, which Teams honours
        Assert.Contains("\\[Sign in\\](https://evil.example)", teams);
    }

    // ---- team notifications -----------------------------------------------------------------------------------------------------------------------

    private sealed class Factory(HttpMessageHandler h) : IHttpClientFactory { public HttpClient CreateClient(string name) => new(h, disposeHandler: false); }
    private sealed class Env : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    [Theory]
    [InlineData("Slack", "Step &lt;!channel&gt; &lt;https://evil.example|Reset&gt; [Sign in](https://evil.example) &amp; co is late")]
    [InlineData("Teams", "Step \\<!channel\\> \\<https://evil.example\\|Reset\\> \\[Sign in\\](https://evil.example) & co is late")]
    public async Task A_team_notice_carries_names_as_text_not_as_chat_markup(string kind, string expected)
    {
        using var f = new ApiFactory();
        _ = f.Server;
        Sql(f, $@"INSERT INTO WebhookDestinations(Id,Name,Url,Kind) VALUES('w1','channel','https://93.184.216.34/services/T/B/X','{kind}');
                  INSERT INTO Teams(Id,Handle,Name,WebhookDestinationId) VALUES('tm1','platform','Platform','w1');");
        var dbf = f.Services.GetRequiredService<IDbContextFactory<ReleaseDbContext>>();
        var capture = new Capture();
        var writer = new SyncAlertWriter(dbf, TimeProvider.System, NullLogger<SyncAlertWriter>.Instance, f.Services.GetRequiredService<INotifier>());
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Notifications:Webhooks:AllowPrivateTargets"] = "true" }).Build();
        var sender = new TeamWebhookSender(dbf, TimeProvider.System, new Factory(capture), writer, new NoAlerts(), config, new Env(), NullLogger<TeamWebhookSender>.Instance);

        Assert.True(await sender.SendAsync(new WebhookNotice("tm1", "StepLate", "RunbookStep", "s1", 1, "Step <!channel> <https://evil.example|Reset> [Sign in](https://evil.example) & co is late", null)));
        Assert.Equal(expected, JsonDocument.Parse(Assert.Single(capture.Bodies)).RootElement.GetProperty("text").GetString());
    }

    private sealed class NoAlerts : IAlertSink { public Task RaiseAsync(string s, string k, string key, string m, CancellationToken ct = default) => Task.CompletedTask; }
}
