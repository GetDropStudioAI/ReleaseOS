using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using ReleaseMgmt.Api.Endpoints;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Infrastructure.Comms;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>REOS-45: dispatch (copy, mailto, allowlisted webhook), the immutable log and the drawer's read model. Hydration is faked: the seam is the contract.</summary>
public class CommDispatchTests
{
    private const string Secret = "SECRET-TOKEN-123";
    private const string HookUrl = "https://hooks.example.test/services/T0/B0/" + Secret;

    /// <summary>Stands in for the real hydrator: replaces the known tokens, reports the rest, escapes values per target (JSON: the whole text).</summary>
    private sealed class FakeRenderer(string dbPath) : ICommDispatchRenderer
    {
        public Dictionary<string, string> Bodies { get; } = new() { ["c1"] = "**{ReleaseTitle}** is {GoNoGoDecision}.", ["c2"] = "x" };
        public List<(string? TemplateId, string? Text, string Target)> Calls { get; } = [];
        private static readonly Dictionary<string, string> Values = new() { ["ReleaseTitle"] = "R26.24 <Q4> & \"Payments\"", ["GoNoGoDecision"] = "Go with conditions" };

        public Task<RenderedComm> RenderAsync(string trainId, string? templateId, string? text, string target, CancellationToken ct)
        {
            lock (Calls) Calls.Add((templateId, text, target));
            var src = text ?? Bodies[templateId!];
            var errors = new List<string>();
            var s = Regex.Replace(src, @"\{(\w+)\}", m =>
            {
                if (!Values.TryGetValue(m.Groups[1].Value, out var v)) { errors.Add($"Unknown token {m.Value}"); return m.Value; }
                return target == CommTargets.Html ? WebUtility.HtmlEncode(v) : v;
            });
            if (target == CommTargets.JsonString) s = JsonEncodedText.Encode(s, System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString();
            using var c = new SqliteConnection($"Data Source={dbPath};Pooling=False");
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT Version FROM ReleaseTrains WHERE Id=$t";
            cmd.Parameters.AddWithValue("$t", trainId);
            return Task.FromResult(new RenderedComm(s, errors, new DateTime(2026, 10, 29, 15, 12, 0, DateTimeKind.Utc), Convert.ToInt32(cmd.ExecuteScalar())));
        }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        public List<(Uri Url, string Body, string? ContentType)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            lock (Requests) Requests.Add((request.RequestUri!, body, request.Content?.Headers.ContentType?.MediaType));
            return await Respond(request, ct);
        }
    }

    private sealed class Env : IDisposable
    {
        public required ApiFactory Root;
        public required WebApplicationFactory<Program> Web;
        public required FakeRenderer Renderer;
        public required FakeHandler Handler;
        public required FakeTimeProvider Clock;
        public required HttpClient Rte, Rm, Viewer, Gov;
        public void Dispose() { Web.Dispose(); Root.Dispose(); }
        public string TrainVersion => Scalar(Root, "SELECT Version FROM ReleaseTrains WHERE Id='t1'");
        public long Count(string table, string where = "1=1") => long.Parse(Scalar(Root, $"SELECT COUNT(*) FROM {table} WHERE {where}"));
    }

    private static async Task<Env> Setup(bool allowPrivate = true, double? timeoutSeconds = null, bool fakeRenderer = true, bool requireIfMatch = false, bool failClosed = false)
    {
        var root = new ApiFactory(requireIfMatch: requireIfMatch);
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-29T15:12:07.789Z"));
        var handler = new FakeHandler();
        var renderer = new FakeRenderer(root.DbPath);
        var web = root.WithWebHostBuilder(b =>
        {
            b.UseSetting("Comms:Webhooks:AllowPrivateTargets", allowPrivate ? "true" : "false");   // true: the fake handler has no DNS, so the resolver check is skipped
            if (timeoutSeconds is double t) b.UseSetting("Comms:Webhooks:TimeoutSeconds", t.ToString(System.Globalization.CultureInfo.InvariantCulture));
            b.UseSetting("Auth:Session:IdleMinutes", "10080");   // the fake clock jumps days while these clients stay signed in (SEC-B4 limits follow the same clock)
            b.UseSetting("Auth:Session:AbsoluteHours", "168");
            b.ConfigureServices(s =>
            {
                s.RemoveAll<TimeProvider>();
                s.AddSingleton<TimeProvider>(clock);
                if (fakeRenderer) s.AddSingleton<ICommDispatchRenderer>(renderer);   // registered after the default, as the real adapter is
                if (failClosed) s.AddSingleton<ICommDispatchRenderer>(new FailClosedCommRenderer(clock));   // what a host with no hydrator registered would use
                s.AddHttpClient(CommWebhookSender.ClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
            });
        });
        async Task<HttpClient> As(string role, string email)
        {
            var c = web.CreateClient();
            (await c.PostAsJsonAsync("/auth/dev-login", new { email, name = email.Split('@')[0], role })).EnsureSuccessStatusCode();
            return c;
        }
        var rte = await As(Roles.RTE, "rte@x.com");
        var rm = await As(Roles.ReleaseManager, "rm@x.com");
        var viewer = await As(Roles.Viewer, "v@x.com");
        var gov = await As(Roles.GovernanceOfficer, "gov@x.com");
        Sql(root, @"
            INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CreatedAt,UpdatedAt) VALUES('t1','R26.24 Q4 Payments','2026-10-30','Low','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z');
            INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CreatedAt,UpdatedAt) VALUES('t2','R26.25 Other','2026-11-13','Low','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z');
            INSERT INTO CommTemplates(Id,ReleaseTrainId,TemplateType,Audience,SubjectLine,MarkdownBody) VALUES('c1','t1','GoNoGo','All stakeholders','[{ReleaseTitle}] Go/No-Go: {GoNoGoDecision}','**{ReleaseTitle}** is {GoNoGoDecision}.');
            INSERT INTO CommTemplates(Id,ReleaseTrainId,TemplateType,Audience,SubjectLine,MarkdownBody) VALUES('c2','t2','Readiness','All','Readiness','x');
            INSERT INTO CommSchedule(Id,ReleaseTrainId,CommTemplateId,DueAt) VALUES('s1','t1','c1','2026-10-29T17:00:00Z');
            INSERT INTO CommSchedule(Id,ReleaseTrainId,CommTemplateId,DueAt) VALUES('s0','t1','c1','2026-10-27T09:00:00Z');
            INSERT INTO WebhookDestinations(Id,Name,Url,Kind) VALUES('w1','release-ops','" + HookUrl + @"','Teams');
            INSERT INTO WebhookDestinations(Id,Name,Url,Kind) VALUES('w2','private','https://127.0.0.1/hook','Generic');
            INSERT INTO WebhookDestinations(Id,Name,Url,Kind) VALUES('w3','generic','https://generic.example.test/in/" + Secret + @"','Generic');");
        return new Env { Root = root, Web = web, Renderer = renderer, Handler = handler, Clock = clock, Rte = rte, Rm = rm, Viewer = viewer, Gov = gov };
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private static async Task<HttpResponseMessage> Send(HttpClient c, HttpMethod m, string url, object? body = null, string? ifMatch = null)
    {
        var req = new HttpRequestMessage(m, url) { Content = body is null ? null : JsonContent.Create(body) };
        if (ifMatch is not null) req.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return await c.SendAsync(req);
    }

    private static Task<HttpResponseMessage> Dispatch(Env e, object body, HttpClient? who = null, string? ifMatch = null, string train = "t1") =>
        Send(who ?? e.Rte, HttpMethod.Post, $"/api/v1/trains/{train}/comms:dispatch", body, ifMatch ?? (train == "t1" ? e.TrainVersion : null));

    private static async Task<JsonElement> Ok(HttpResponseMessage r)
    {
        Assert.True(r.StatusCode == HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        return await Json(r);
    }

    private static async Task<JsonElement> Refused(HttpResponseMessage r, string guard)
    {
        var body = await Json(r);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal(guard, body.GetProperty("guard").GetString());
        return body;
    }

    private static string HexOf(string s) => Convert.ToHexString(Encoding.UTF8.GetBytes(s));

    // ---- the hydration seam -----------------------------------------------------------------------------------------
    [Fact]
    public async Task An_unknown_token_blocks_dispatch_lists_the_errors_and_writes_nothing()
    {
        using var e = await Setup();
        e.Renderer.Bodies["c1"] = "Hello {ReleaseTitel} and {Nope} and {ReleaseTitle}";
        var body = await Refused(await Dispatch(e, new { templateId = "c1", scheduleItemId = "s1", channel = "Copy" }), "TokenErrors");
        var items = body.GetProperty("items").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Equal(["Unknown token {ReleaseTitel}", "Unknown token {Nope}"], items);
        Assert.Contains("{Nope}", body.GetProperty("message").GetString());
        Assert.Equal(0, e.Count("CommDispatches"));
        Assert.Equal(0, e.Count("AuditEvents", "EntityType='CommDispatch'"));
        Assert.Equal("", Scalar(e.Root, "SELECT COALESCE(SentAt,'') FROM CommSchedule WHERE Id='s1'"));
        Assert.Empty(e.Handler.Requests);
    }

    [Fact]
    public async Task An_unknown_token_in_the_subject_blocks_dispatch_too()
    {
        using var e = await Setup();
        Sql(e.Root, "UPDATE CommTemplates SET SubjectLine='[{ReleaseTitle}] {Typo}' WHERE Id='c1'");
        await Refused(await Dispatch(e, new { templateId = "c1", channel = "Mailto" }), "TokenErrors");
        Assert.Equal(0, e.Count("CommDispatches"));
    }

    [Fact]
    public async Task With_no_renderer_registered_the_fail_closed_default_refuses_everything()
    {
        using var e = await Setup(fakeRenderer: false, failClosed: true);
        var body = await Refused(await Dispatch(e, new { templateId = "c1", channel = "Copy" }, ifMatch: "1"), "TokenErrors");
        Assert.Equal("renderer not configured", body.GetProperty("items")[0].GetString());
        Assert.Equal(0, e.Count("CommDispatches"));
    }

    [Fact]
    public async Task The_real_host_renders_through_the_token_hydrator_and_an_unknown_token_writes_nothing()
    {
        using var e = await Setup(fakeRenderer: false);   // the host's own registration: CommHydrationAdapter over the REOS-44 hydrator
        var md = await Ok(await Dispatch(e, new { templateId = "c1", channel = "Copy", format = "Markdown" }));
        Assert.Equal("**R26.24 Q4 Payments** is Not yet recorded.", md.GetProperty("body").GetString());
        Assert.Equal("[R26.24 Q4 Payments] Go/No-Go: Not yet recorded", md.GetProperty("subject").GetString());
        var rich = await Ok(await Dispatch(e, new { templateId = "c1", channel = "Copy", format = "RichText" }));
        Assert.Equal("<strong>R26.24 Q4 Payments</strong> is Not yet recorded.", rich.GetProperty("body").GetString());

        var hook = await Ok(await Dispatch(e, new { templateId = "c1", channel = "Webhook", webhookDestinationId = "w1" }));
        Assert.Equal("Delivered", hook.GetProperty("outcome").GetString());
        using (var payload = JsonDocument.Parse(hook.GetProperty("body").GetString()!))   // the stored body is the exact JSON that was posted, and it parses
            Assert.Contains("Not yet recorded", payload.RootElement.EnumerateObject().Select(p => p.Value.ToString()).First(v => v.Contains("R26.24")));

        Sql(e.Root, "UPDATE CommTemplates SET MarkdownBody='Status {Statuz}' WHERE Id='c1'");
        var before = e.Count("CommDispatches");
        var bad = await Refused(await Dispatch(e, new { templateId = "c1", channel = "Copy", format = "Markdown" }), "TokenErrors");
        Assert.Contains("Statuz", bad.GetProperty("items")[0].GetString() + bad.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, e.Count("CommDispatches"));   // an unknown token blocks dispatch: nothing written, nothing sent
    }

    [Fact]
    public async Task Each_channel_renders_for_its_own_target()
    {
        using var e = await Setup();
        var rich = await Ok(await Dispatch(e, new { templateId = "c1", channel = "Copy", format = "RichText" }));
        var md = await Ok(await Dispatch(e, new { templateId = "c1", channel = "Copy", format = "Markdown" }));
        await Ok(await Dispatch(e, new { templateId = "c1", channel = "Copy" }));   // default format is rich text
        await Ok(await Dispatch(e, new { templateId = "c1", channel = "Mailto" }));
        await Ok(await Dispatch(e, new { templateId = "c1", channel = "Webhook", webhookDestinationId = "w1" }));
        var bodyTargets = e.Renderer.Calls.Where(c => c.TemplateId == "c1").Select(c => c.Target).ToList();
        Assert.Equal([CommTargets.Html, CommTargets.Markdown, CommTargets.Html, CommTargets.PlainText, CommTargets.JsonString], bodyTargets);
        var subjectTargets = e.Renderer.Calls.Where(c => c.TemplateId is null).Select(c => c.Target).ToList();
        Assert.Equal([CommTargets.PlainText, CommTargets.PlainText, CommTargets.PlainText, CommTargets.PlainText, CommTargets.JsonString], subjectTargets);
        Assert.Equal("**R26.24 &lt;Q4&gt; &amp; &quot;Payments&quot;** is Go with conditions.", rich.GetProperty("body").GetString());   // Html: values escaped
        Assert.Equal("**R26.24 <Q4> & \"Payments\"** is Go with conditions.", md.GetProperty("body").GetString());
    }

    // ---- exact bytes ------------------------------------------------------------------------------------------------
    public static IEnumerable<object[]> Edge() =>
    [
        ["multi-byte", "Grüße, 日本語 ✓ 😀 — “quotes” café\u00A0nbsp"],
        ["crlf", "line one\r\nline two\r\n\r\nline four\rlone CR\nlone LF"],
        ["trailing whitespace", "ends with spaces   \t \n  indented\ttab   "],
        ["leading and trailing blank lines", "\n\n  body \n\n\n"],
        ["binary-ish controls", "\u0001\u0002 bell\u0007 esc\u001b [0m \u007f del \u2028 line-sep \uFEFF bom"],
    ];

    [Theory, MemberData(nameof(Edge))]
    public async Task A_dispatched_message_is_retrievable_byte_for_byte(string _, string text)
    {
        using var e = await Setup();
        e.Renderer.Bodies["c1"] = text;
        var sent = await Ok(await Dispatch(e, new { templateId = "c1", channel = "Copy", format = "Markdown" }));
        var id = sent.GetProperty("id").GetString()!;
        Assert.Equal(text, sent.GetProperty("body").GetString());

        Assert.Equal(text, (await Json(await e.Viewer.GetAsync($"/api/v1/comms/dispatches/{id}"))).GetProperty("body").GetString());
        var raw = await e.Viewer.GetAsync($"/api/v1/comms/dispatches/{id}/body");
        Assert.Equal(Encoding.UTF8.GetBytes(text), await raw.Content.ReadAsByteArrayAsync());   // bytes in == bytes out, no BOM, no newline translation
        Assert.Equal("text/plain; charset=utf-8", raw.Content.Headers.ContentType!.ToString());
        Assert.Equal("nosniff", raw.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(HexOf(text), Scalar(e.Root, $"SELECT hex(CAST(HydratedBody AS BLOB)) FROM CommDispatches WHERE Id='{id}'"));   // the stored bytes themselves

        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        Assert.Equal(sha, raw.Headers.GetValues("X-Body-Sha256").Single());
        Assert.Contains($"\"bodySha256\":\"{sha}\"", Scalar(e.Root, "SELECT AfterJson FROM AuditEvents WHERE EntityType='CommDispatch'"));   // the hash taken at dispatch time
    }

    [Fact]
    public async Task Copy_records_the_rich_text_and_the_markdown_body_as_two_immutable_rows()
    {
        using var e = await Setup();
        e.Renderer.Bodies["c1"] = "{ReleaseTitle}";
        var rich = await Ok(await Dispatch(e, new { templateId = "c1", channel = "Copy", format = "RichText" }));
        var md = await Ok(await Dispatch(e, new { templateId = "c1", channel = "Copy", format = "Markdown" }));
        Assert.Equal("R26.24 &lt;Q4&gt; &amp; &quot;Payments&quot;", rich.GetProperty("body").GetString());
        Assert.Equal("R26.24 <Q4> & \"Payments\"", md.GetProperty("body").GetString());
        Assert.Equal("RichText", rich.GetProperty("format").GetString());
        Assert.Equal("Handed", md.GetProperty("outcome").GetString());
        Assert.Equal(2, e.Count("CommDispatches", "Channel='Copy' AND Outcome='Handed'"));
        Assert.Contains("\"format\":\"Markdown\"", Scalar(e.Root, $"SELECT AfterJson FROM AuditEvents WHERE EntityId='{md.GetProperty("id").GetString()}'"));
    }

    [Theory]
    [InlineData("nul", "NUL")]
    [InlineData("lone surrogate", "surrogate")]
    public async Task Text_that_cannot_be_stored_exactly_is_refused_not_altered(string kind, string word)
    {
        using var e = await Setup();
        e.Renderer.Bodies["c1"] = kind == "nul" ? "before\0after" : "bad " + '\ud800' + " surrogate";   // built here: a lone surrogate does not survive xunit's data serialization
        var body = await Refused(await Dispatch(e, new { templateId = "c1", channel = "Copy" }), "RenderInvalid");
        Assert.Contains(word, body.GetProperty("message").GetString());
        Assert.Equal(0, e.Count("CommDispatches"));
    }

    [Fact]
    public async Task An_empty_message_is_refused()
    {
        using var e = await Setup();
        e.Renderer.Bodies["c1"] = " \r\n ";
        await Refused(await Dispatch(e, new { templateId = "c1", channel = "Copy" }), "EmptyMessage");
    }

    // ---- schedule ---------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Fulfilling_a_schedule_item_stamps_SentAt_from_the_clock_and_records_it_against_DueAt()
    {
        using var e = await Setup();
        var sent = await Ok(await Dispatch(e, new { scheduleItemId = "s1", channel = "Copy" }));   // template comes from the item
        Assert.Equal("c1", sent.GetProperty("templateId").GetString());
        Assert.Equal("2026-10-29T15:12:07Z", Scalar(e.Root, "SELECT SentAt FROM CommSchedule WHERE Id='s1'"));   // whole seconds of the injected clock
        Assert.Equal(sent.GetProperty("id").GetString(), Scalar(e.Root, "SELECT DispatchId FROM CommSchedule WHERE Id='s1'"));
        Assert.Equal("2", Scalar(e.Root, "SELECT Version FROM CommSchedule WHERE Id='s1'"));
        Assert.False(sent.GetProperty("late").GetBoolean());   // due 17:00, sent 15:12
        Assert.Equal("2026-10-29T17:00:00Z", sent.GetProperty("dueAt").GetString());
        Assert.Equal("2026-10-29T15:12:07Z", sent.GetProperty("sentAt").GetString());

        e.Clock.Advance(TimeSpan.FromHours(3));
        var late = await Ok(await Dispatch(e, new { scheduleItemId = "s0", templateId = "c1", channel = "Mailto" }));   // due 27th, sent 29th
        Assert.True(late.GetProperty("late").GetBoolean());
        Assert.Equal("2026-10-29T18:12:07Z", Scalar(e.Root, "SELECT SentAt FROM CommSchedule WHERE Id='s0'"));
        var audit = Scalar(e.Root, $"SELECT AfterJson FROM AuditEvents WHERE EntityId='{late.GetProperty("id").GetString()}'");
        Assert.Contains("\"dueAt\":\"2026-10-27T09:00:00Z\"", audit);
        Assert.Contains("\"sentAt\":\"2026-10-29T18:12:07Z\"", audit);
        Assert.Contains("\"late\":true", audit);
    }

    [Fact]
    public async Task A_schedule_item_is_sent_once_and_a_rehearsal_does_not_send_it()
    {
        using var e = await Setup();
        await Ok(await Dispatch(e, new { scheduleItemId = "s1", channel = "Copy", isRehearsal = true }));
        Assert.Equal("", Scalar(e.Root, "SELECT COALESCE(SentAt,'') FROM CommSchedule WHERE Id='s1'"));   // a dry run is not a send: M12 must not count it
        Assert.Equal(1, e.Count("CommDispatches", "IsRehearsal=1"));
        await Ok(await Dispatch(e, new { scheduleItemId = "s1", channel = "Copy" }));
        await Refused(await Dispatch(e, new { scheduleItemId = "s1", channel = "Copy" }), "ScheduleAlreadySent");
        Assert.Equal(2, e.Count("CommDispatches"));
        await Ok(await Dispatch(e, new { templateId = "c1", channel = "Copy" }));   // sending the template again without the item is allowed
    }

    [Fact]
    public async Task Template_and_schedule_item_must_belong_to_the_train_and_agree()
    {
        using var e = await Setup();
        Assert.Equal(HttpStatusCode.NotFound, (await Dispatch(e, new { templateId = "c2", channel = "Copy" })).StatusCode);   // another train's template
        Assert.Equal(HttpStatusCode.NotFound, (await Dispatch(e, new { scheduleItemId = "nope", channel = "Copy" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Dispatch(e, new { templateId = "c1", channel = "Copy" }, train: "nope")).StatusCode);
        await Refused(await Dispatch(e, new { channel = "Copy" }), "TemplateRequired");
        await Refused(await Dispatch(e, new { scheduleItemId = "s1", templateId = "c2", channel = "Copy" }), "TemplateMismatch");
        await Refused(await Dispatch(e, new { templateId = "c1", channel = "Pigeon" }), "InvalidChannel");
        await Refused(await Dispatch(e, new { templateId = "c1", channel = "Copy", format = "Pdf" }), "InvalidFormat");
        Assert.Equal(0, e.Count("CommDispatches"));
    }

    // ---- webhooks ---------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_webhook_target_that_is_not_https_is_refused_before_anything_is_sent()
    {
        using var e = await Setup();
        var body = await Refused(await Dispatch(e, new { templateId = "c1", channel = "Webhook", webhookUrl = "http://hooks.example.test/x" }), "WebhookNotHttps");
        Assert.Contains("https", body.GetProperty("message").GetString());
        await Refused(await Dispatch(e, new { templateId = "c1", channel = "Webhook", webhookUrl = "ftp://hooks.example.test/x" }), "WebhookNotHttps");
        await Refused(await Dispatch(e, new { templateId = "c1", channel = "Webhook", webhookUrl = "not a url" }), "WebhookNotHttps");
        Assert.Empty(e.Handler.Requests);
        Assert.Equal(0, e.Count("CommDispatches"));
    }

    [Fact]
    public async Task A_webhook_target_that_is_not_on_the_allowlist_is_refused()
    {
        using var e = await Setup();
        await Refused(await Dispatch(e, new { templateId = "c1", channel = "Webhook", webhookUrl = "https://evil.example.test/steal" }), "WebhookNotAllowed");
        await Refused(await Dispatch(e, new { templateId = "c1", channel = "Webhook", webhookDestinationId = "nope" }), "WebhookNotAllowed");
        await Refused(await Dispatch(e, new { templateId = "c1", channel = "Webhook" }), "WebhookNotAllowed");
        await Refused(await Dispatch(e, new { templateId = "c1", channel = "Webhook", webhookDestinationId = "w1", webhookUrl = HookUrl }), "WebhookNotAllowed");   // exactly one of the two
        Assert.Empty(e.Handler.Requests);
        Assert.Equal(0, e.Count("CommDispatches"));
    }

    [Fact]
    public async Task An_allowlisted_https_webhook_delivers_the_exact_stored_json_and_records_Delivered()
    {
        using var e = await Setup();
        e.Renderer.Bodies["c1"] = "Line 1\nLine \"2\" \\ ✓ {GoNoGoDecision}";
        var sent = await Ok(await Dispatch(e, new { templateId = "c1", scheduleItemId = "s1", channel = "Webhook", webhookDestinationId = "w1" }));
        Assert.Equal("Delivered", sent.GetProperty("outcome").GetString());
        Assert.Equal("release-ops", sent.GetProperty("webhookName").GetString());
        var req = Assert.Single(e.Handler.Requests);
        Assert.Equal(HookUrl, req.Url.ToString());
        Assert.Equal("application/json", req.ContentType);
        Assert.Equal(sent.GetProperty("body").GetString(), req.Body);   // what was posted is what is stored
        var posted = JsonDocument.Parse(req.Body).RootElement.GetProperty("text").GetString();   // Teams/Slack {"text": ...}
        Assert.Equal("[R26.24 <Q4> & \"Payments\"] Go/No-Go: Go with conditions\n\nLine 1\nLine \"2\" \\ ✓ Go with conditions", posted);
        Assert.Equal("[R26.24 <Q4> & \"Payments\"] Go/No-Go: Go with conditions", sent.GetProperty("subject").GetString());   // the stored subject is the readable one
        Assert.Equal("Delivered", Scalar(e.Root, "SELECT Outcome FROM CommDispatches"));
        Assert.Equal("w1", Scalar(e.Root, "SELECT WebhookDestinationId FROM CommDispatches"));
        Assert.Equal("2026-10-29T15:12:07Z", Scalar(e.Root, "SELECT SentAt FROM CommSchedule WHERE Id='s1'"));
        Assert.Equal(0, e.Count("SyncAlerts"));
        Assert.DoesNotContain(Secret, Scalar(e.Root, "SELECT AfterJson FROM AuditEvents WHERE EntityType='CommDispatch'"));
    }

    [Fact]
    public async Task A_generic_destination_gets_subject_and_text_separately_and_can_be_picked_by_its_URL()
    {
        using var e = await Setup();
        var sent = await Ok(await Dispatch(e, new { templateId = "c1", channel = "Webhook", webhookUrl = "https://generic.example.test/in/" + Secret }));
        var posted = JsonDocument.Parse(Assert.Single(e.Handler.Requests).Body).RootElement;
        Assert.Equal("[R26.24 <Q4> & \"Payments\"] Go/No-Go: Go with conditions", posted.GetProperty("subject").GetString());
        Assert.Equal("**R26.24 <Q4> & \"Payments\"** is Go with conditions.", posted.GetProperty("text").GetString());
        Assert.Equal("t1", posted.GetProperty("trainId").GetString());
        Assert.Equal("w3", sent.GetProperty("webhookDestinationId").GetString());
    }

    [Fact]
    public async Task A_renderer_that_does_not_escape_for_JSON_is_refused_before_sending()
    {
        using var e = await Setup();
        var raw = new RawJsonRenderer();
        using var web = e.Root.WithWebHostBuilder(b => b.ConfigureServices(s => { s.AddSingleton<ICommDispatchRenderer>(raw); }));
        var c = web.CreateClient();
        (await c.PostAsJsonAsync("/auth/dev-login", new { email = "rte@x.com", name = "rte", role = "RTE" })).EnsureSuccessStatusCode();
        var r = await Send(c, HttpMethod.Post, "/api/v1/trains/t1/comms:dispatch", new { templateId = "c1", channel = "Webhook", webhookDestinationId = "w1" }, "1");
        await Refused(r, "RenderInvalid");
        Assert.Empty(e.Handler.Requests);
        Assert.Equal(0, e.Count("CommDispatches"));
    }

    private sealed class RawJsonRenderer : ICommDispatchRenderer
    {
        public Task<RenderedComm> RenderAsync(string trainId, string? templateId, string? text, string target, CancellationToken ct) =>
            Task.FromResult(new RenderedComm("has a \" quote and\na raw newline", [], DateTime.UtcNow, 1));
    }

    [Fact]
    public async Task A_failed_delivery_is_recorded_alerted_and_visible_and_never_hides_the_secret()
    {
        using var e = await Setup();
        e.Handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var sent = await Ok(await Dispatch(e, new { scheduleItemId = "s1", channel = "Webhook", webhookDestinationId = "w1" }));   // the attempt is recorded: 200 with outcome Failed
        Assert.Equal("Failed", sent.GetProperty("outcome").GetString());
        Assert.Equal("HTTP 500", sent.GetProperty("failureReason").GetString());
        Assert.Equal("Failed", Scalar(e.Root, "SELECT Outcome FROM CommDispatches"));
        Assert.Equal("", Scalar(e.Root, "SELECT COALESCE(SentAt,'') FROM CommSchedule WHERE Id='s1'"));   // not sent: the item stays due
        Assert.Equal("Webhook|DeliveryFailed|1", Scalar(e.Root, "SELECT SourceSystem||'|'||Kind||'|'||OccurrenceCount FROM SyncAlerts"));
        Assert.Equal("t1", Scalar(e.Root, "SELECT ReleaseTrainId FROM SyncAlerts"));
        var msg = Scalar(e.Root, "SELECT ErrorMessage FROM SyncAlerts");
        Assert.Contains("release-ops", msg); Assert.Contains("hooks.example.test", msg); Assert.Contains("HTTP 500", msg);
        Assert.DoesNotContain(Secret, msg);
        Assert.DoesNotContain(Secret, Scalar(e.Root, "SELECT group_concat(COALESCE(AfterJson,''),'') FROM AuditEvents"));
        Assert.DoesNotContain(Secret, Scalar(e.Root, "SELECT group_concat(Message,'') FROM Notifications"));
        Assert.Equal("Failed", (await Json(await e.Viewer.GetAsync("/api/v1/trains/t1/comms/dispatches"))).GetProperty("items")[0].GetProperty("outcome").GetString());

        await Ok(await Dispatch(e, new { scheduleItemId = "s1", channel = "Webhook", webhookDestinationId = "w1" }));   // a retry is a new dispatch; the alert counts the repeat
        Assert.Equal("2", Scalar(e.Root, "SELECT OccurrenceCount FROM SyncAlerts"));
        Assert.Equal(2, e.Count("CommDispatches", "Outcome='Failed'"));
    }

    [Fact]
    public async Task A_webhook_that_does_not_answer_within_the_timeout_fails_and_says_so()
    {
        using var e = await Setup(timeoutSeconds: 0.3);
        e.Handler.Respond = async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return new HttpResponseMessage(HttpStatusCode.OK); };
        var sw = Stopwatch.StartNew();
        var sent = await Ok(await Dispatch(e, new { templateId = "c1", channel = "Webhook", webhookDestinationId = "w1" }));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
        Assert.Equal("Failed", sent.GetProperty("outcome").GetString());
        Assert.Contains("no answer within 0.3s", sent.GetProperty("failureReason").GetString());
        Assert.Equal("Webhook|DeliveryFailed", Scalar(e.Root, "SELECT SourceSystem||'|'||Kind FROM SyncAlerts"));
    }

    [Fact]
    public void The_default_timeout_is_ten_seconds() => Assert.Equal(10.0, CommWebhookSender.DefaultTimeoutSeconds);

    [Fact]
    public async Task A_redirect_is_a_failure_not_a_second_destination()
    {
        using var e = await Setup();
        e.Handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://169.254.169.254/latest/meta-data") } });
        var sent = await Ok(await Dispatch(e, new { templateId = "c1", channel = "Webhook", webhookDestinationId = "w1" }));
        Assert.Equal("Failed", sent.GetProperty("outcome").GetString());
        Assert.Contains("redirects are not followed", sent.GetProperty("failureReason").GetString());
        Assert.Single(e.Handler.Requests);
    }

    [Fact]
    public async Task A_client_error_is_not_retried_and_a_transport_error_names_only_its_type()
    {
        using var e = await Setup();
        e.Handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Equal("HTTP 404", (await Ok(await Dispatch(e, new { templateId = "c1", channel = "Webhook", webhookDestinationId = "w1" }))).GetProperty("failureReason").GetString());
        e.Handler.Respond = (_, _) => throw new HttpRequestException("connect to " + HookUrl + " refused");
        var reason = (await Ok(await Dispatch(e, new { templateId = "c1", channel = "Webhook", webhookDestinationId = "w1" }))).GetProperty("failureReason").GetString()!;
        Assert.Equal("HttpRequestException", reason);
        Assert.DoesNotContain(Secret, Scalar(e.Root, "SELECT group_concat(ErrorMessage,'') FROM SyncAlerts"));
        Assert.Equal(2, e.Handler.Requests.Count);
    }

    [Fact]
    public async Task Private_and_loopback_targets_are_refused_by_the_sender_unless_explicitly_allowed()
    {
        using var e = await Setup(allowPrivate: false);
        var sent = await Ok(await Dispatch(e, new { templateId = "c1", channel = "Webhook", webhookDestinationId = "w2" }));   // https://127.0.0.1/hook is on the allowlist but must never be reached
        Assert.Equal("Failed", sent.GetProperty("outcome").GetString());
        Assert.Contains("private or local", sent.GetProperty("failureReason").GetString());
        Assert.Empty(e.Handler.Requests);
        Assert.Equal("Webhook|DeliveryFailed", Scalar(e.Root, "SELECT SourceSystem||'|'||Kind FROM SyncAlerts"));
    }

    // ---- immutability -----------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_dispatch_cannot_be_edited_or_deleted_over_HTTP_or_in_SQL()
    {
        using var e = await Setup();
        var id = (await Ok(await Dispatch(e, new { templateId = "c1", channel = "Copy" }))).GetProperty("id").GetString()!;
        foreach (var m in new[] { HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
        {
            foreach (var url in new[] { $"/api/v1/comms/dispatches/{id}", $"/api/v1/trains/t1/comms/dispatches", $"/api/v1/comms/dispatches/{id}/body", $"/api/v1/trains/t1/comms/dispatches/{id}" })
            {
                var r = await Send(e.Rm, m, url, m == HttpMethod.Delete ? null : new { body = "tampered" }, "1");
                Assert.True(r.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, $"{m} {url} -> {(int)r.StatusCode}");
            }
        }
        var before = Scalar(e.Root, $"SELECT hex(CAST(HydratedBody AS BLOB)) FROM CommDispatches WHERE Id='{id}'");
        var upd = Assert.Throws<SqliteException>(() => Sql(e.Root, $"UPDATE CommDispatches SET HydratedBody='tampered' WHERE Id='{id}'"));
        Assert.Contains("Dispatches are immutable", upd.Message);
        var del = Assert.Throws<SqliteException>(() => Sql(e.Root, $"DELETE FROM CommDispatches WHERE Id='{id}'"));
        Assert.Contains("Dispatches are immutable", del.Message);
        Assert.Throws<SqliteException>(() => Sql(e.Root, "UPDATE CommDispatches SET Outcome='Failed'"));
        Assert.Equal(before, Scalar(e.Root, $"SELECT hex(CAST(HydratedBody AS BLOB)) FROM CommDispatches WHERE Id='{id}'"));
        Assert.Equal("Handed", Scalar(e.Root, $"SELECT Outcome FROM CommDispatches WHERE Id='{id}'"));
    }

    [Fact]
    public async Task The_database_is_the_backstop_for_webhook_rules_when_the_service_is_bypassed()
    {
        using var e = await Setup();
        var uid = UserId(e.Root, "rte@x.com");
        string Row(string channel, string? dest) => $"INSERT INTO CommDispatches(Id,CommTemplateId,Channel,WebhookDestinationId,HydratedSubject,HydratedBody,DispatchedByUserId,DispatchedAt,Outcome) VALUES('x{Guid.NewGuid():N}','c1','{channel}',{(dest is null ? "NULL" : $"'{dest}'")},'s','b','{uid}','2026-10-29T15:00:00Z','Handed')";
        Assert.Contains("CHECK", Assert.Throws<SqliteException>(() => Sql(e.Root, Row("Webhook", null))).Message);          // a webhook needs a destination
        Assert.Contains("CHECK", Assert.Throws<SqliteException>(() => Sql(e.Root, Row("Copy", "w1"))).Message);             // and only a webhook has one
        Assert.Contains("FOREIGN KEY", Assert.Throws<SqliteException>(() => Sql(e.Root, "PRAGMA foreign_keys=ON; " + Row("Webhook", "not-on-allowlist"))).Message);   // the allowlist is a foreign key
        Assert.Contains("CHECK", Assert.Throws<SqliteException>(() => Sql(e.Root, "INSERT INTO WebhookDestinations(Id,Name,Url,Kind) VALUES('wh','plain','http://hooks.example.test/x','Generic')")).Message);   // https only
        Assert.Contains("CHECK", Assert.Throws<SqliteException>(() => Sql(e.Root, Row("Copy", null).Replace("'Handed'", "'Sent'"))).Message);
        Assert.Equal(0, e.Count("CommDispatches"));
        Sql(e.Root, Row("Webhook", "w1"));   // a valid row goes in (the triggers guard update and delete, not insert)
        Assert.Equal(1, e.Count("CommDispatches"));
    }

    // ---- audit, roles, concurrency ------------------------------------------------------------------------------------
    [Fact]
    public async Task One_audit_row_is_written_in_the_same_transaction_with_actor_and_clock()
    {
        using var e = await Setup();
        var sent = await Ok(await Dispatch(e, new { templateId = "c1", scheduleItemId = "s1", channel = "Mailto" }));
        var id = sent.GetProperty("id").GetString();
        Assert.Equal(1, e.Count("AuditEvents", "EntityType='CommDispatch'"));
        Assert.Equal($"CommDispatch|{id}|Dispatch|t1|{UserId(e.Root, "rte@x.com")}|2026-10-29T15:12:07Z",
            Scalar(e.Root, "SELECT EntityType||'|'||EntityId||'|'||Action||'|'||ReleaseTrainId||'|'||ActorUserId||'|'||OccurredAt FROM AuditEvents WHERE EntityType='CommDispatch'"));
        var after = JsonDocument.Parse(Scalar(e.Root, "SELECT AfterJson FROM AuditEvents WHERE EntityType='CommDispatch'")).RootElement;
        Assert.Equal("Mailto", after.GetProperty("channel").GetString());
        Assert.Equal("Handed", after.GetProperty("outcome").GetString());
        Assert.Equal("s1", after.GetProperty("scheduleItemId").GetString());
        Assert.Equal(64, after.GetProperty("bodySha256").GetString()!.Length);
        Assert.Equal("2026-10-29T15:12:07Z", sent.GetProperty("dispatchedAt").GetString());
        Assert.Equal("rte", sent.GetProperty("dispatchedByName").GetString());
    }

    [Fact]
    public async Task Only_RTE_and_ReleaseManager_dispatch_and_everyone_signed_in_reads()
    {
        using var e = await Setup();
        var body = new { templateId = "c1", channel = "Copy" };
        Assert.Equal(HttpStatusCode.Forbidden, (await Dispatch(e, body, e.Viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Dispatch(e, body, e.Gov)).StatusCode);
        using var anon = e.Web.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(anon, HttpMethod.Post, "/api/v1/trains/t1/comms:dispatch", body, "1")).StatusCode);
        Assert.Equal(0, e.Count("CommDispatches"));
        var id = (await Ok(await Dispatch(e, body, e.Rm))).GetProperty("id").GetString();
        await Ok(await Dispatch(e, body, e.Rte));
        foreach (var c in new[] { e.Viewer, e.Gov, e.Rte, e.Rm })
        {
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/trains/t1/comms/dispatches")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/v1/comms/dispatches/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/v1/comms/dispatches/{id}/body")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/trains/t1/comms/dispatch-context")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/trains/t1/comms/dispatches")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await e.Viewer.GetAsync("/api/v1/comms/dispatches/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await e.Viewer.GetAsync("/api/v1/trains/nope/comms/dispatches")).StatusCode);
    }

    [Fact]
    public async Task A_train_that_moved_since_the_preview_is_a_409_and_a_missing_If_Match_is_a_428_when_required()
    {
        using var e = await Setup();
        var stale = await Dispatch(e, new { templateId = "c1", channel = "Copy" }, ifMatch: "99");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(1, (await Json(stale)).GetProperty("current").GetProperty("version").GetInt32());
        Assert.Equal(0, e.Count("CommDispatches"));
        await Ok(await Dispatch(e, new { templateId = "c1", channel = "Copy" }, ifMatch: "1"));

        using var strict = await Setup(requireIfMatch: true);
        var c = strict.Rte;
        Assert.Equal((HttpStatusCode)428, (await Send(c, HttpMethod.Post, "/api/v1/trains/t1/comms:dispatch", new { templateId = "c1", channel = "Copy" })).StatusCode);
        Assert.Equal(0, strict.Count("CommDispatches"));
    }

    // ---- the log and the drawer's read model -------------------------------------------------------------------------
    [Fact]
    public async Task The_log_pages_newest_first_without_gaps_or_duplicates_and_stays_within_its_train()
    {
        using var e = await Setup();
        var ids = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add((await Ok(await Dispatch(e, new { templateId = "c1", channel = i % 2 == 0 ? "Copy" : "Mailto" }))).GetProperty("id").GetString()!);
            if (i != 2) e.Clock.Advance(TimeSpan.FromMinutes(1));   // dispatches 2 and 3 share a second: the Id breaks the tie
        }
        await Ok(await Dispatch(e, new { templateId = "c2", channel = "Copy" }, train: "t2"));
        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await Json(await e.Viewer.GetAsync($"/api/v1/trains/t1/comms/dispatches?limit=2" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor))));
            Assert.Equal(2, page.GetProperty("limit").GetInt32());
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetString()!));
            cursor = page.GetProperty("nextCursor").GetString();
            pages++;
        } while (cursor is not null && pages < 10);
        Assert.Equal(3, pages);
        Assert.Equal(5, seen.Distinct().Count());
        Assert.Equal(5, seen.Intersect(ids).Count());
        var times = seen.Select(id => Scalar(e.Root, $"SELECT DispatchedAt FROM CommDispatches WHERE Id='{id}'")).ToList();
        Assert.Equal(times.OrderByDescending(x => x, StringComparer.Ordinal), times);
        var first = (await Json(await e.Viewer.GetAsync("/api/v1/trains/t1/comms/dispatches"))).GetProperty("items")[0];
        Assert.Equal("Copy", first.GetProperty("channel").GetString());
        Assert.Equal("rte", first.GetProperty("dispatchedByName").GetString());
        Assert.Equal("[R26.24 <Q4> & \"Payments\"] Go/No-Go: Go with conditions", first.GetProperty("subject").GetString());
        Assert.False(first.TryGetProperty("body", out _));   // the list carries no bodies; one dispatch does
        Assert.Equal(1, (await Json(await e.Viewer.GetAsync("/api/v1/trains/t2/comms/dispatches"))).GetProperty("items").GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, (await e.Viewer.GetAsync("/api/v1/trains/t1/comms/dispatches?cursor=garbage")).StatusCode);
        var big = await Json(await e.Viewer.GetAsync("/api/v1/trains/t1/comms/dispatches?limit=100000"));
        Assert.Equal(200, big.GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task The_drawer_context_lists_templates_schedule_states_and_allowlisted_targets_without_URLs()
    {
        using var e = await Setup();
        await Ok(await Dispatch(e, new { scheduleItemId = "s0", channel = "Copy" }));   // sent 29th 15:12 for a 27th due date: late
        var res = await e.Viewer.GetAsync("/api/v1/trains/t1/comms/dispatch-context");
        var text = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Secret, text);
        Assert.DoesNotContain("https://", text);
        var ctx = JsonDocument.Parse(text).RootElement;
        Assert.Equal("2026-10-29T15:12:07Z", ctx.GetProperty("asOf").GetString());
        Assert.Equal("c1", Assert.Single(ctx.GetProperty("templates").EnumerateArray()).GetProperty("id").GetString());
        Assert.Equal("**{ReleaseTitle}** is {GoNoGoDecision}.", ctx.GetProperty("templates")[0].GetProperty("markdownBody").GetString());
        var states = ctx.GetProperty("schedule").EnumerateArray().ToDictionary(x => x.GetProperty("id").GetString()!, x => x.GetProperty("state").GetString());
        Assert.Equal("sentLate", states["s0"]);
        Assert.Equal("ready", states["s1"]);   // due in under 24 h, not sent
        var targets = ctx.GetProperty("webhookTargets").EnumerateArray().ToDictionary(x => x.GetProperty("name").GetString()!, x => x.GetProperty("host").GetString());
        Assert.Equal("hooks.example.test", targets["release-ops"]);
        Assert.Equal(3, targets.Count);
        e.Clock.Advance(TimeSpan.FromDays(3));
        var later = (await Json(await e.Viewer.GetAsync("/api/v1/trains/t1/comms/dispatch-context"))).GetProperty("schedule").EnumerateArray().ToDictionary(x => x.GetProperty("id").GetString()!, x => x.GetProperty("state").GetString());
        Assert.Equal("overdue", later["s1"]);
        Sql(e.Root, "INSERT INTO CommSchedule(Id,ReleaseTrainId,CommTemplateId,DueAt) VALUES('s9','t1','c1','2026-12-01T09:00:00Z')");
        var far = (await Json(await e.Viewer.GetAsync("/api/v1/trains/t1/comms/dispatch-context"))).GetProperty("schedule").EnumerateArray().ToDictionary(x => x.GetProperty("id").GetString()!, x => x.GetProperty("state").GetString());
        Assert.Equal("scheduled", far["s9"]);
    }
}
