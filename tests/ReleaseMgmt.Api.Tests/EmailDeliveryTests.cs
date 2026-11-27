using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Email;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// REOS-167 (D61): email delivery, driven through the real host with a fake provider (no mail is ever sent) and a frozen clock. Off by default (no row, no mail, no alert); templates built from structure only (D12) with UTC and local
/// times (D44); per-person preferences by kind and channel; the outbox with retries and backoff; a hard bounce suppressing the address; an undeliverable address raising ONE alert (rule 8); inbound bounce events signed with a secret
/// that Data Protection holds (rule 11); the daily digest sent once a day.
/// </summary>
public class EmailDeliveryTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-27T12:00:00Z");
    private static readonly (string, string)[] On =
    [
        ("Notifications:Smtp:Enabled", "true"), ("Notifications:Email:From:Address", "releaseos@example.com"), ("Notifications:Email:Smtp:Host", "smtp.example.com"),
        ("Notifications:Email:BackoffBaseSeconds", "60"), ("Notifications:Email:MaxAttempts", "3"), ("Notifications:Email:DigestHourUtc", "7"), ("App:PublicBaseUrl", "https://releaseos.example.com"),
    ];

    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
    private static long Count(ApiFactory f, string sql) => long.Parse(Scalar(f, sql));
    private static long Audits(ApiFactory f, string entity, string action) => Count(f, $"SELECT COUNT(*) FROM AuditEvents WHERE EntityType='{entity}' AND Action='{action}'");
    private static string Status(ApiFactory f, string to) => Scalar(f, $"SELECT Status FROM EmailOutbox WHERE ToAddress='{to}' ORDER BY CreatedAt DESC, Id DESC LIMIT 1");

    private sealed record Env(ApiFactory F, FakeTimeProvider Clock, HttpClient Rte, string RteId, string RteEmail) : IDisposable
    {
        public void Dispose() => F.Dispose();
        public EmailDeliveryProcessor Processor => F.Services.GetRequiredService<EmailDeliveryProcessor>();
        public FakeEmailProvider Provider => F.EmailProvider;
        public async Task Notify(string userId, string kind, int level = 0, string message = "Something happened", string entityType = "Approval", string entityId = "a1")
        {
            using var _ = WorkspaceScope.Enter("default");
            await F.Services.GetRequiredService<INotifier>().NotifyAsync(new NotificationRequest(userId, kind, entityType, entityId, message, level));
        }
        public async Task<int> Digest() { using var _ = WorkspaceScope.Enter("default"); return await F.Services.GetRequiredService<EmailDigestService>().QueueAsync(); }
    }

    private static async Task<Env> Start(bool enabled = true, DateTimeOffset? at = null, params (string, string)[] extra)
    {
        var clock = new FakeTimeProvider(at ?? T0);
        var settings = (enabled ? On : [("App:PublicBaseUrl", "https://releaseos.example.com")]).Concat(extra).GroupBy(s => s.Item1).ToDictionary(g => g.Key, g => g.Last().Item2);
        var f = new ApiFactory(settings: settings) { Clock = clock };
        var rte = await As(f, Roles.RTE, "rte@x.com");
        return new(f, clock, rte, UserId(f, "rte@x.com"), "rte@x.com");
    }

    private static async Task<HttpResponseMessage> Send(HttpClient c, HttpMethod m, string url, object? body = null, string? ifMatch = null)
    {
        var req = new HttpRequestMessage(m, url);
        if (body is not null) req.Content = JsonContent.Create(body);
        if (ifMatch is not null) req.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return await c.SendAsync(req);
    }

    private static async Task<HttpResponseMessage> Prefer(HttpClient c, string category, string channel, string mode, int? version = null) =>
        await c.PutAsJsonAsync("/api/v1/me/notification-preferences", new { category, channel, mode, version });

    // ---- off by default ----

    [Fact]
    public async Task Off_by_default_no_mail_no_row_no_alert_and_the_inbox_still_works()
    {
        using var e = await Start(enabled: false);
        await e.Notify(e.RteId, "ApprovalRequested");
        await e.Notify(e.RteId, "StepLate", 2, entityType: "RunbookRun", entityId: "r1");
        Assert.Equal(2, Count(e.F, "SELECT COUNT(*) FROM Notifications"));
        Assert.Equal(0, Count(e.F, "SELECT COUNT(*) FROM EmailOutbox"));
        Assert.Equal(0, await e.Processor.ProcessDueAsync());
        Assert.Empty(e.Provider.Attempts);
        Assert.Equal(0, Count(e.F, "SELECT COUNT(*) FROM SyncAlerts"));
        var s = await Json(await e.Rte.GetAsync("/api/v1/email/status"));
        Assert.False(s.GetProperty("enabled").GetBoolean());
        var test = await e.Rte.PostAsync("/api/v1/email/test", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, test.StatusCode);
        Assert.Equal("EmailOff", (await Json(test)).GetProperty("guard").GetString());
        Assert.Equal(0, Count(e.F, "SELECT COUNT(*) FROM EmailOutbox"));
    }

    [Fact]
    public void An_enabled_configuration_that_cannot_work_stops_the_start_and_the_default_is_off()
    {
        static IConfiguration C(params (string, string)[] kv) => new ConfigurationBuilder().AddInMemoryCollection(kv.ToDictionary(x => x.Item1, x => (string?)x.Item2)).Build();
        Assert.False(EmailOptions.From(C()).Enabled);
        Assert.False(EmailOptions.From(C(("Notifications:Smtp:Enabled", "false"))).Enabled);
        var ok = new[] { ("Notifications:Smtp:Enabled", "true"), ("Notifications:Email:From:Address", "a@example.com"), ("Notifications:Email:Smtp:Host", "smtp.example.com") };
        Assert.True(EmailOptions.From(C(ok)).Enabled);
        Assert.Contains("From:Address", Assert.Throws<InvalidOperationException>(() => EmailOptions.From(C(("Notifications:Smtp:Enabled", "true"), ("Notifications:Email:Smtp:Host", "h")))).Message);
        Assert.Contains("From:Address", Assert.Throws<InvalidOperationException>(() => EmailOptions.From(C(ok.Where(x => !x.Item1.EndsWith("Address")).Append(("Notifications:Email:From:Address", "A <a@example.com>")).ToArray()))).Message);
        Assert.Contains("Provider", Assert.Throws<InvalidOperationException>(() => EmailOptions.From(C(ok.Append(("Notifications:Email:Provider", "Carrier")).ToArray()))).Message);
        Assert.Contains("Smtp:Host", Assert.Throws<InvalidOperationException>(() => EmailOptions.From(C(ok.Where(x => !x.Item1.EndsWith("Host")).ToArray()))).Message);
        Assert.Contains("in clear", Assert.Throws<InvalidOperationException>(() => EmailOptions.From(C(ok.Concat([("Notifications:Email:Smtp:Security", "None"), ("Notifications:Email:Smtp:UserName", "u")]).ToArray()))).Message);
        Assert.True(EmailOptions.From(C(ok.Concat([("Notifications:Email:Smtp:Security", "None"), ("Notifications:Email:Smtp:UserName", "u"), ("Notifications:Email:Smtp:Host", "localhost")]).GroupBy(x => x.Item1).Select(g => g.Last()).ToArray())).Enabled);
        Assert.Contains("https", Assert.Throws<InvalidOperationException>(() => EmailOptions.From(C(ok.Concat([("Notifications:Email:Provider", "SendGrid"), ("Notifications:Email:ApiBase", "http://api.example.com")]).ToArray()))).Message);
    }

    // ---- what is mailed, and what is in it ----

    [Fact]
    public async Task An_approval_request_is_mailed_from_the_template_with_no_message_text_and_UTC_beside_local_time()
    {
        using var e = await Start();
        const string secretText = "Acme Corp ticket ABC-123: refund for J. Smith, card 4111";
        await e.Notify(e.RteId, "ApprovalRequested", 0, secretText);
        Assert.Equal(1, Count(e.F, "SELECT COUNT(*) FROM Notifications"));
        Assert.Equal("Pending", Status(e.F, e.RteEmail));
        Assert.Equal(1, await e.Processor.ProcessDueAsync());

        var mail = Assert.Single(e.Provider.Sent);
        Assert.Equal(e.RteEmail, mail.To);
        Assert.Equal("releaseos@example.com", mail.FromAddress);
        Assert.StartsWith("[", mail.Subject);
        Assert.Contains("Approval requested", mail.Subject);
        foreach (var body in new[] { mail.Subject, mail.Text, mail.Html })
        {
            Assert.DoesNotContain("Acme", body);
            Assert.DoesNotContain("ABC-123", body);
            Assert.DoesNotContain("4111", body);
        }
        Assert.Contains("2026-11-27 12:00 UTC (2026-11-27 06:00 America/Chicago)", mail.Text);   // D44: UTC first, the display zone beside it, both labelled
        Assert.Contains("Open: https://releaseos.example.com/approvals", mail.Text);              // D52: from App:PublicBaseUrl
        Assert.Contains("href=\"https://releaseos.example.com/approvals\"", mail.Html);
        Assert.Contains("https://releaseos.example.com/notification-settings", mail.Text);
        Assert.Equal("Sent", Status(e.F, e.RteEmail));
        Assert.Equal(1, Audits(e.F, "EmailMessage", "Sent"));
        Assert.Equal(0, Count(e.F, "SELECT COUNT(*) FROM SyncAlerts"));
        Assert.Equal("fake", Scalar(e.F, "SELECT Provider FROM EmailOutbox"));
    }

    [Fact]
    public async Task The_four_kinds_map_to_their_templates_and_other_notifications_stay_in_the_inbox()
    {
        using var e = await Start();
        await e.Notify(e.RteId, "ApprovalApproved");
        await e.Notify(e.RteId, "ApprovalChangesRequested");
        await e.Notify(e.RteId, "GateOverdue", 1, entityType: "StageGate", entityId: "g1");
        await e.Notify(e.RteId, "RollbackDeadline", 2, entityType: "RunbookRun", entityId: "r1");
        await e.Notify(e.RteId, "GateEntered", 0, entityType: "StageGate", entityId: "g1");   // level 0, not one of the four
        await e.Notify(e.RteId, "SyncAlert", 0, entityType: "SyncAlert", entityId: "x");
        Assert.Equal(6, Count(e.F, "SELECT COUNT(*) FROM Notifications"));
        Assert.Equal("DecisionMade|DecisionMade|EscalationLevel1|EscalationLevel2",
            Scalar(e.F, "SELECT group_concat(Category, '|') FROM (SELECT Category FROM EmailOutbox ORDER BY CreatedAt, Id)").Replace("EscalationLevel2|EscalationLevel1", "EscalationLevel1|EscalationLevel2"));
        Assert.Equal(4, Count(e.F, "SELECT COUNT(*) FROM EmailOutbox"));
        Assert.Equal(1, Count(e.F, "SELECT COUNT(*) FROM EmailOutbox WHERE Subject LIKE '%Escalation level 2%'"));
        Assert.Equal(1, Count(e.F, "SELECT COUNT(*) FROM EmailOutbox WHERE Subject LIKE '%Escalation level 1%'"));
    }

    [Fact]
    public async Task Nothing_is_mailed_to_a_person_who_is_inactive_and_no_link_is_invented_without_a_base_address()
    {
        using var e = await Start(true, null, ("App:PublicBaseUrl", ""));
        var other = await As(e.F, Roles.ReleaseManager, "gone@x.com");
        var goneId = UserId(e.F, "gone@x.com");
        Sql(e.F, $"UPDATE Users SET IsActive=0 WHERE Id='{goneId}'");
        await e.Notify(goneId, "ApprovalRequested");
        Assert.Equal(0, Count(e.F, "SELECT COUNT(*) FROM EmailOutbox"));
        await e.Notify(e.RteId, "ApprovalRequested");
        await e.Processor.ProcessDueAsync();
        var mail = Assert.Single(e.Provider.Sent);
        Assert.DoesNotContain("http", mail.Text.Replace("This message holds no ticket text", ""));
        Assert.DoesNotContain("href", mail.Html);
        _ = other;
    }

    [Fact]
    public void Templates_encode_everything_and_only_link_to_http_addresses_and_never_carry_a_line_break_in_the_subject()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        var c = new EmailContent(EmailCategories.ApprovalRequested, "Pay<script>alert(1)</script>\r\nBcc: x@y.com", "RLS-1", "javascript:alert(1)", T0.UtcDateTime, "https://x.example.com/notification-settings");
        var m = EmailTemplates.Render(c, zone);
        Assert.DoesNotContain('\n', m.Subject);
        Assert.DoesNotContain('\r', m.Subject);
        Assert.DoesNotContain("<script>", m.Html);
        Assert.DoesNotContain("javascript:", m.Html);
        Assert.Contains("&lt;script&gt;", m.Html);
        Assert.Contains("RLS-1", m.Subject);
        var digest = EmailTemplates.Render(new EmailContent(EmailCategories.DailyDigest, "Pay", null, "https://x.example.com/inbox", T0.UtcDateTime, null,
            [new DigestItem(EmailCategories.DecisionMade, "RLS-2", T0.UtcDateTime, "https://x.example.com/approvals")]), zone);
        Assert.Contains("Decision made · RLS-2, 2026-11-27 12:00 UTC", digest.Text);
        Assert.Contains("1 notification since", digest.Text);
        var many = Enumerable.Range(0, 25).Select(i => new DigestItem(EmailCategories.DecisionMade, $"RLS-{i}", T0.UtcDateTime, null)).ToList();
        var capped = EmailTemplates.Render(new EmailContent(EmailCategories.DailyDigest, "Pay", null, null, T0.UtcDateTime, null, many, 40), zone);
        Assert.Contains("40 notifications since", capped.Text);   // the headline counts all of them, not the lines it lists
        Assert.Contains("and 15 more", capped.Text);
        Assert.Contains("and 15 more", capped.Html);
    }

    // ---- preferences ----

    [Fact]
    public async Task Preferences_default_to_email_immediately_and_webhook_off_and_the_inbox_has_no_choice()
    {
        using var e = await Start();
        var j = await Json(await e.Rte.GetAsync("/api/v1/me/notification-preferences"));
        Assert.True(j.GetProperty("emailEnabled").GetBoolean());
        var prefs = j.GetProperty("preferences").EnumerateArray().ToList();
        Assert.Equal(8, prefs.Count);   // four kinds, two channels
        Assert.All(prefs.Where(p => p.GetProperty("channel").GetString() == "Email"), p => { Assert.Equal("Immediate", p.GetProperty("mode").GetString()); Assert.Equal(0, p.GetProperty("version").GetInt32()); });
        Assert.All(prefs.Where(p => p.GetProperty("channel").GetString() == "Webhook"), p => Assert.Equal("Off", p.GetProperty("mode").GetString()));
        Assert.DoesNotContain(prefs, p => p.GetProperty("channel").GetString() == "Inbox");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Prefer(e.Rte, "ApprovalRequested", "Inbox", "Off")).StatusCode);
    }

    [Fact]
    public async Task Off_and_daily_stop_the_immediate_mail_and_a_person_changes_only_their_own()
    {
        using var e = await Start();
        var rm = await As(e.F, Roles.ReleaseManager, "rm@x.com");
        var rmId = UserId(e.F, "rm@x.com");
        Assert.Equal(HttpStatusCode.OK, (await Prefer(e.Rte, "ApprovalRequested", "Email", "Off")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Prefer(e.Rte, "DecisionMade", "Email", "Daily")).StatusCode);
        await e.Notify(e.RteId, "ApprovalRequested");   // off
        await e.Notify(e.RteId, "ApprovalApproved");    // daily: not now
        await e.Notify(e.RteId, "GateOverdue", 1, entityType: "StageGate", entityId: "g1");   // default: immediately
        await e.Notify(rmId, "ApprovalRequested");      // another person: their own default
        Assert.Equal(2, Count(e.F, "SELECT COUNT(*) FROM EmailOutbox"));
        Assert.Equal(1, Count(e.F, $"SELECT COUNT(*) FROM EmailOutbox WHERE UserId='{e.RteId}' AND Category='EscalationLevel1'"));
        Assert.Equal(1, Count(e.F, $"SELECT COUNT(*) FROM EmailOutbox WHERE UserId='{rmId}' AND Category='ApprovalRequested'"));
        Assert.Equal(4, Count(e.F, "SELECT COUNT(*) FROM Notifications"));   // the inbox is always written
        Assert.Equal(2, Audits(e.F, "NotificationPreference", "Set"));
        // someone else's preferences are not reachable: the endpoint has no user in it
        var mine = await Json(await rm.GetAsync("/api/v1/me/notification-preferences"));
        Assert.All(mine.GetProperty("preferences").EnumerateArray().Where(p => p.GetProperty("channel").GetString() == "Email"), p => Assert.Equal("Immediate", p.GetProperty("mode").GetString()));
    }

    [Fact]
    public async Task Preference_changes_are_validated_versioned_audited_and_a_stale_screen_gets_409()
    {
        using var e = await Start();
        Assert.Equal("Immediate", (await Json(await Prefer(e.Rte, "EscalationLevel2", "Email", "Immediate", 0))).GetProperty("mode").GetString());
        var changed = await Json(await Prefer(e.Rte, "EscalationLevel2", "Email", "Daily", 1));
        Assert.Equal(2, changed.GetProperty("version").GetInt32());
        var stale = await Prefer(e.Rte, "EscalationLevel2", "Email", "Off", 1);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("Daily", (await Json(stale)).GetProperty("current").GetProperty("mode").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await Prefer(e.Rte, "DecisionMade", "Email", "Off", 5)).StatusCode);   // a row that does not exist yet has version 0
        foreach (var (cat, ch, mode) in new[] { ("StepLate", "Email", "Off"), ("DecisionMade", "Sms", "Off"), ("DecisionMade", "Email", "Sometimes"), ("DecisionMade", "Webhook", "Daily"), (null!, "Email", "Off") })
        {
            var r = await Prefer(e.Rte, cat, ch, mode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
            Assert.Equal("PreferenceInvalid", (await Json(r)).GetProperty("guard").GetString());
        }
        Assert.Equal(2, Audits(e.F, "NotificationPreference", "Set"));   // the unchanged first call wrote one row, the change another
        Assert.Equal(1, Count(e.F, "SELECT COUNT(*) FROM NotificationPreferences"));
        // setting the value it already has writes nothing
        await Prefer(e.Rte, "EscalationLevel2", "Email", "Daily");
        Assert.Equal(2, Audits(e.F, "NotificationPreference", "Set"));
        Assert.Equal(2, Count(e.F, "SELECT Version FROM NotificationPreferences"));
        // a viewer sets their own, too
        var v = await As(e.F, Roles.Viewer, "v@x.com");
        Assert.Equal(HttpStatusCode.OK, (await Prefer(v, "DecisionMade", "Email", "Off")).StatusCode);
    }

    // ---- sending: retries, dead letters, bounces ----

    [Fact]
    public async Task A_temporary_failure_retries_with_exponential_backoff_and_then_sends_without_an_alert()
    {
        using var e = await Start();
        e.Provider.FailTimes[e.RteEmail] = 2;
        await e.Notify(e.RteId, "ApprovalRequested");
        Assert.Equal(1, await e.Processor.ProcessDueAsync());
        Assert.Equal("Pending|1|2026-11-27T12:01:00Z", Scalar(e.F, "SELECT Status||'|'||AttemptCount||'|'||NextAttemptAt FROM EmailOutbox"));
        Assert.Equal(0, await e.Processor.ProcessDueAsync());   // not yet
        e.Clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(0, await e.Processor.ProcessDueAsync());
        e.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await e.Processor.ProcessDueAsync());
        Assert.Equal("Pending|2|2026-11-27T12:03:00Z", Scalar(e.F, "SELECT Status||'|'||AttemptCount||'|'||NextAttemptAt FROM EmailOutbox"));   // 60 s, then 120 s
        e.Clock.Advance(TimeSpan.FromSeconds(120));
        Assert.Equal(1, await e.Processor.ProcessDueAsync());
        Assert.Equal("Sent|3", Scalar(e.F, "SELECT Status||'|'||AttemptCount FROM EmailOutbox"));
        Assert.Single(e.Provider.Sent);
        Assert.Equal(3, e.Provider.Attempts.Count);
        Assert.Equal(0, Count(e.F, "SELECT COUNT(*) FROM SyncAlerts"));
        Assert.Equal(1, Audits(e.F, "EmailMessage", "Sent"));
    }

    [Fact]
    public async Task A_dead_letter_raises_one_alert_per_address_that_a_later_success_resolves()
    {
        using var e = await Start();
        e.Provider.FailEverything = true;
        await e.Notify(e.RteId, "ApprovalRequested");
        for (var i = 0; i < 3; i++) { await e.Processor.ProcessDueAsync(); e.Clock.Advance(TimeSpan.FromHours(1)); }
        Assert.Equal("DeadLetter|3", Scalar(e.F, "SELECT Status||'|'||AttemptCount FROM EmailOutbox"));
        Assert.Equal(1, Audits(e.F, "EmailMessage", "DeadLetter"));
        Assert.Equal(1, Count(e.F, "SELECT COUNT(*) FROM SyncAlerts WHERE SourceSystem='Notifications' AND Kind='DeliveryFailed' AND IsResolved=0"));
        Assert.Contains(e.RteEmail, Scalar(e.F, "SELECT ErrorMessage FROM SyncAlerts"));
        // a second dead letter to the same address is the same alert, counted
        await e.Notify(e.RteId, "ApprovalRequested", entityId: "a2");
        for (var i = 0; i < 3; i++) { await e.Processor.ProcessDueAsync(); e.Clock.Advance(TimeSpan.FromHours(1)); }
        Assert.Equal(1, Count(e.F, "SELECT COUNT(*) FROM SyncAlerts"));
        Assert.Equal(2, Count(e.F, "SELECT OccurrenceCount FROM SyncAlerts"));
        // the provider recovers; the next message to the address closes the alert
        e.Provider.FailEverything = false;
        await e.Notify(e.RteId, "ApprovalRequested", entityId: "a3");
        await e.Processor.ProcessDueAsync();
        Assert.Equal("Sent", Status(e.F, e.RteEmail));
        Assert.Equal(1, Count(e.F, "SELECT COUNT(*) FROM SyncAlerts WHERE IsResolved=1"));
    }

    [Fact]
    public async Task A_hard_bounce_suppresses_the_address_raises_an_alert_and_no_further_mail_is_attempted()
    {
        using var e = await Start();
        e.Provider.Rejected.Add(e.RteEmail);
        await e.Notify(e.RteId, "ApprovalRequested");
        await e.Processor.ProcessDueAsync();
        Assert.Equal("Bounced", Status(e.F, e.RteEmail));
        Assert.Equal("Bounce", Scalar(e.F, "SELECT Reason FROM EmailSuppressions"));
        Assert.Equal(1, Audits(e.F, "EmailMessage", "Bounced"));
        Assert.Equal(1, Audits(e.F, "EmailSuppression", "Add"));
        Assert.Equal(1, Count(e.F, "SELECT COUNT(*) FROM SyncAlerts WHERE SourceSystem='Notifications' AND Kind='DeliveryFailed' AND IsResolved=0"));
        Assert.Contains("undeliverable", Scalar(e.F, "SELECT ErrorMessage FROM SyncAlerts"));
        var tries = e.Provider.Attempts.Count;

        await e.Notify(e.RteId, "ApprovalRequested", entityId: "a2");   // recorded as suppressed, never sent
        Assert.Equal("Suppressed", Status(e.F, e.RteEmail));
        Assert.Equal(0, await e.Processor.ProcessDueAsync());
        Assert.Equal(tries, e.Provider.Attempts.Count);
        Assert.Equal(1, Audits(e.F, "EmailMessage", "Suppressed"));
        Assert.Equal(3, Count(e.F, "SELECT COUNT(*) FROM Notifications"));   // the inbox is unaffected: the two approvals, and the planners were told in-app about the undeliverable address (an alert is not mailed)

        var list = await Json(await e.Rte.GetAsync("/api/v1/email/suppressions"));
        var entry = Assert.Single(list.GetProperty("suppressions").EnumerateArray());
        Assert.Equal("Bounce", entry.GetProperty("reason").GetString());
        // an administrator lifts it (with the version they saw): the alert closes and mail flows again
        e.Provider.Rejected.Clear();
        var del = await Send(e.Rte, HttpMethod.Delete, "/api/v1/email/suppressions/" + entry.GetProperty("id").GetString(), ifMatch: entry.GetProperty("version").GetInt32().ToString());
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        Assert.Equal(1, Count(e.F, "SELECT COUNT(*) FROM SyncAlerts WHERE IsResolved=1"));
        Assert.Equal(1, Audits(e.F, "EmailSuppression", "Remove"));
        await e.Notify(e.RteId, "ApprovalRequested", entityId: "a3");
        await e.Processor.ProcessDueAsync();
        Assert.Equal("Sent", Status(e.F, e.RteEmail));
    }

    [Fact]
    public async Task An_address_suppressed_while_its_message_waited_is_never_sent()
    {
        using var e = await Start();
        e.Provider.FailTimes[e.RteEmail] = 1;
        await e.Notify(e.RteId, "ApprovalRequested");
        await e.Processor.ProcessDueAsync();   // first try fails, waiting for the second
        var add = await e.Rte.PostAsJsonAsync("/api/v1/email/suppressions", new { address = e.RteEmail.ToUpperInvariant(), detail = "Asked not to be mailed" });
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);
        e.Clock.Advance(TimeSpan.FromMinutes(5));
        await e.Processor.ProcessDueAsync();
        Assert.Equal("Suppressed", Status(e.F, e.RteEmail));
        Assert.Single(e.Provider.Attempts);
        Assert.Empty(e.Provider.Sent);
        var again = await e.Rte.PostAsJsonAsync("/api/v1/email/suppressions", new { address = e.RteEmail, detail = "" });
        Assert.Equal("AlreadySuppressed", (await Json(again)).GetProperty("guard").GetString());
        Assert.Equal("AddressInvalid", (await Json(await e.Rte.PostAsJsonAsync("/api/v1/email/suppressions", new { address = "not an address" }))).GetProperty("guard").GetString());
    }

    [Fact]
    public async Task A_message_the_provider_refuses_outright_is_dead_lettered_at_once_without_suppressing_the_address()
    {
        using var host = new ApiFactory(settings: On.ToDictionary(s => s.Item1, s => s.Item2)) { Clock = new FakeTimeProvider(T0), ConfigureServices = s => s.AddSingleton<IEmailProvider>(new FixedProvider(new(EmailOutcome.Failed, null, "400: bad message"))) };
        await As(host, Roles.RTE, "rte@x.com");
        using (WorkspaceScope.Enter("default")) await host.Services.GetRequiredService<INotifier>().NotifyAsync(new NotificationRequest(UserId(host, "rte@x.com"), "ApprovalRequested", "Approval", "a1", "m"));
        await host.Services.GetRequiredService<EmailDeliveryProcessor>().ProcessDueAsync();
        Assert.Equal("DeadLetter|1", Scalar(host, "SELECT Status||'|'||AttemptCount FROM EmailOutbox"));
        Assert.Equal(0, Count(host, "SELECT COUNT(*) FROM EmailSuppressions"));
        Assert.Equal(1, Count(host, "SELECT COUNT(*) FROM SyncAlerts WHERE Kind='DeliveryFailed'"));
    }

    private sealed class FixedProvider(EmailSendResult result) : IEmailProvider
    {
        public string Name => "fixed";
        public Task<EmailSendResult> SendAsync(EmailEnvelope message, CancellationToken ct) => Task.FromResult(result);
    }

    // ---- bounce and complaint events from the provider ----

    private static async Task<HttpResponseMessage> PostEvent(Env e, string secret, object body, long? ts = null, string id = "evt-1", string? signature = null)
    {
        var json = JsonSerializer.Serialize(body);
        var t = ts ?? e.Clock.GetUtcNow().ToUnixTimeSeconds();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/email/events") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        req.Headers.TryAddWithoutValidation(ControlSignature.TimestampHeader, t.ToString());
        req.Headers.TryAddWithoutValidation(ControlSignature.DeliveryHeader, id);
        req.Headers.TryAddWithoutValidation(ControlSignature.SignatureHeader, signature ?? ControlSignature.Sign(secret, t, id, json));
        return await e.F.CreateClient().SendAsync(req);   // an anonymous client: the provider has no session
    }

    [Fact]
    public async Task A_signed_bounce_event_marks_the_message_bounced_suppresses_the_address_and_raises_the_alert()
    {
        using var e = await Start();
        Assert.Equal(HttpStatusCode.NotFound, (await PostEvent(e, "x", new { type = "bounce", address = e.RteEmail })).StatusCode);   // no secret yet: the route says it is not set up
        var made = await Json(await e.Rte.PostAsync("/api/v1/email/events-secret:rotate", null));
        var secret = made.GetProperty("secret").GetString()!;
        Assert.True(secret.Length >= 40);
        Assert.Equal(1, Audits(e.F, "EmailEvents" + "Secret", "Rotated"));
        Assert.DoesNotContain(secret, Scalar(e.F, "SELECT group_concat(coalesce(AfterJson,''),' ') FROM AuditEvents"));

        await e.Notify(e.RteId, "ApprovalRequested");
        await e.Processor.ProcessDueAsync();
        Assert.Equal("Sent", Status(e.F, e.RteEmail));
        var providerId = Scalar(e.F, "SELECT ProviderMessageId FROM EmailOutbox");

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostEvent(e, "wrong-secret", new { type = "bounce", messageId = providerId })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostEvent(e, secret, new { type = "bounce", messageId = providerId }, ts: e.Clock.GetUtcNow().ToUnixTimeSeconds() - 301)).StatusCode);   // stale
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostEvent(e, secret, new { type = "bounce", messageId = providerId }, signature: "v1=00")).StatusCode);
        Assert.Equal("Sent", Status(e.F, e.RteEmail));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostEvent(e, secret, new { type = "opened", address = e.RteEmail })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostEvent(e, secret, new { type = "bounce" })).StatusCode);

        var ok = await PostEvent(e, secret, new { type = "bounce", messageId = providerId, reason = "550 5.1.1 mailbox unavailable" });
        Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
        Assert.Equal(1, (await Json(ok)).GetProperty("matched").GetInt32());
        Assert.Equal("Bounced", Status(e.F, e.RteEmail));
        Assert.Equal("Bounce", Scalar(e.F, "SELECT Reason FROM EmailSuppressions"));
        Assert.Equal(1, Audits(e.F, "EmailMessage", "Bounced"));
        Assert.Equal(1, Count(e.F, "SELECT COUNT(*) FROM SyncAlerts WHERE Kind='DeliveryFailed' AND IsResolved=0"));
        // a repeat of the same event changes nothing further
        Assert.Equal(HttpStatusCode.Accepted, (await PostEvent(e, secret, new { type = "bounce", messageId = providerId })).StatusCode);
        Assert.Equal(1, Count(e.F, "SELECT COUNT(*) FROM EmailSuppressions"));
        // a complaint by address, and an event that matches nothing
        await e.Rte.DeleteAsync("/api/v1/email/suppressions/" + Scalar(e.F, "SELECT Id FROM EmailSuppressions"));
        await e.Notify(e.RteId, "ApprovalRequested", entityId: "a2");
        await e.Processor.ProcessDueAsync();
        var c = await PostEvent(e, secret, new { type = "complaint", address = e.RteEmail.ToUpperInvariant() }, id: "evt-2");
        Assert.Equal(HttpStatusCode.Accepted, c.StatusCode);
        Assert.Equal("Complaint", Scalar(e.F, "SELECT Reason FROM EmailSuppressions"));
        var none = await PostEvent(e, secret, new { type = "bounce", address = "stranger@example.com" }, id: "evt-3");
        Assert.Equal(0, (await Json(none)).GetProperty("matched").GetInt32());
        Assert.Equal(1, Count(e.F, "SELECT COUNT(*) FROM EmailSuppressions"));
    }

    // ---- the daily digest ----

    [Fact]
    public async Task The_daily_digest_is_one_message_a_day_after_the_hour_and_only_when_there_is_something_to_say()
    {
        using var e = await Start(true, DateTimeOffset.Parse("2026-11-27T05:00:00Z"));
        Assert.Equal(HttpStatusCode.OK, (await Prefer(e.Rte, "DecisionMade", "Email", "Daily")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Prefer(e.Rte, "EscalationLevel1", "Email", "Daily")).StatusCode);
        await e.Notify(e.RteId, "ApprovalApproved", message: "Customer Globex asked for it");
        await e.Notify(e.RteId, "GateOverdue", 1, entityType: "StageGate", entityId: "g1");
        await e.Notify(e.RteId, "ApprovalRequested");   // immediate: its own mail, not in the digest
        await e.Processor.ProcessDueAsync();
        Assert.Equal(1, Count(e.F, "SELECT COUNT(*) FROM EmailOutbox"));
        e.Clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(0, await e.Digest());   // 05:30, before the digest hour (07:00 UTC)
        e.Clock.SetUtcNow(DateTimeOffset.Parse("2026-11-27T07:15:00Z"));
        Assert.Equal(1, await e.Digest());
        Assert.Equal(0, await e.Digest());   // the same day again: nothing more
        await e.Processor.ProcessDueAsync();
        var digest = Assert.Single(e.Provider.Sent, m => m.Subject.Contains("daily digest"));
        Assert.Contains("2 notifications since the last one", digest.Text);
        Assert.Contains("Decision made", digest.Text);
        Assert.Contains("Escalation, level 1", digest.Text);
        Assert.DoesNotContain("Globex", digest.Text + digest.Html);
        Assert.Contains("2026-11-27 07:15 UTC", EmailTemplates.When(e.Clock.GetUtcNow().UtcDateTime, TimeZoneInfo.Utc));
        Assert.Equal("digest:" + e.RteId + ":20261127", Scalar(e.F, "SELECT DedupKey FROM EmailOutbox WHERE Category='DailyDigest'"));
        // next day: nothing new, no mail; then one new item, a digest with only that
        e.Clock.SetUtcNow(DateTimeOffset.Parse("2026-11-28T07:15:00Z"));
        Assert.Equal(0, await e.Digest());
        await e.Notify(e.RteId, "ApprovalApproved", entityId: "a9");
        e.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, await e.Digest());
        await e.Processor.ProcessDueAsync();
        Assert.Contains("1 notification since the last one", e.Provider.Sent.Last().Text);
        Assert.Equal(2, Count(e.F, "SELECT COUNT(*) FROM EmailOutbox WHERE Category='DailyDigest'"));
    }

    // ---- the admin surface ----

    [Fact]
    public async Task A_test_email_goes_to_the_senders_own_address_only_and_only_planners_may_send_one()
    {
        using var e = await Start();
        var r = await e.Rte.PostAsync("/api/v1/email/test", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(e.RteEmail, (await Json(r)).GetProperty("to").GetString());
        await e.Processor.ProcessDueAsync();
        var mail = Assert.Single(e.Provider.Sent);
        Assert.Contains("Test email", mail.Subject);
        Assert.Equal(e.RteEmail, mail.To);
        Assert.Equal(1, Audits(e.F, "EmailMessage", "TestQueued"));
        foreach (var role in new[] { Roles.Viewer, Roles.GovernanceOfficer })
        {
            var c = await As(e.F, role, $"{role}@x.com");
            foreach (var path in new[] { "/api/v1/email/status", "/api/v1/email/messages", "/api/v1/email/suppressions" }) Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsync("/api/v1/email/test", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PutAsJsonAsync("/api/v1/email/credential", new { value = "x" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsync("/api/v1/email/events-secret:rotate", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/v1/email/suppressions", new { address = "a@example.com" })).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await e.F.CreateClient().GetAsync("/api/v1/email/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await e.F.CreateClient().GetAsync("/api/v1/me/notification-preferences")).StatusCode);
    }

    [Fact]
    public async Task The_log_and_status_show_what_was_sent_and_what_failed_without_a_credential_anywhere()
    {
        using var e = await Start(true, null, ("Notifications:Email:Smtp:UserName", "mailer"));
        await e.Notify(e.RteId, "ApprovalRequested");
        await e.Processor.ProcessDueAsync();
        var s = await Json(await e.Rte.GetAsync("/api/v1/email/status"));
        Assert.True(s.GetProperty("enabled").GetBoolean());
        Assert.Equal("Smtp", s.GetProperty("provider").GetString());
        Assert.True(s.GetProperty("needsCredential").GetBoolean());
        Assert.False(s.GetProperty("hasCredential").GetBoolean());
        Assert.Equal(1, s.GetProperty("counts").GetProperty("sentLast7Days").GetInt32());
        var list = await Json(await e.Rte.GetAsync("/api/v1/email/messages?status=Sent"));
        var m = Assert.Single(list.GetProperty("messages").EnumerateArray());
        Assert.Equal("Approval requested", m.GetProperty("categoryLabel").GetString());
        var detail = await Json(await e.Rte.GetAsync("/api/v1/email/messages/" + m.GetProperty("id").GetString()));
        Assert.Contains("An approval is waiting", detail.GetProperty("bodyText").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await e.Rte.GetAsync("/api/v1/email/messages/nope")).StatusCode);

        const string password = "hunter2-very-secret-password";
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await e.Rte.PutAsJsonAsync("/api/v1/email/credential", new { value = "  " })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await e.Rte.PutAsJsonAsync("/api/v1/email/credential", new { value = password })).StatusCode);
        Assert.True((await Json(await e.Rte.GetAsync("/api/v1/email/status"))).GetProperty("hasCredential").GetBoolean());
        // rule 11: Data Protection only. Not in SQLite (any column of any table), not in an audit row, not on disk in clear, not in any response
        var dir = Path.Combine(Path.GetDirectoryName(e.F.DbPath)!, "email-credentials");
        var file = Assert.Single(Directory.GetFiles(dir, "*.secret"));
        Assert.DoesNotContain(password, await File.ReadAllTextAsync(file));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Assert.DoesNotContain(password, Encoding.Latin1.GetString(await File.ReadAllBytesAsync(e.F.DbPath)));   // not one byte of the database file
        Assert.Equal(1, Audits(e.F, "EmailCredential", "Set"));
        Assert.DoesNotContain("hunter2", Scalar(e.F, "SELECT group_concat(coalesce(BeforeJson,'')||coalesce(AfterJson,''),' ') FROM AuditEvents"));
        Assert.DoesNotContain("hunter2", await (await e.Rte.GetAsync("/api/v1/email/status")).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await e.Rte.DeleteAsync("/api/v1/email/credential")).StatusCode);
        Assert.False((await Json(await e.Rte.GetAsync("/api/v1/email/status"))).GetProperty("hasCredential").GetBoolean());
        Assert.Equal(1, Audits(e.F, "EmailCredential", "Removed"));
    }

    [Fact]
    public async Task A_credential_is_refused_when_the_configuration_needs_none()
    {
        using var e = await Start();   // SMTP with no user name: an open relay on a trusted network
        var r = await e.Rte.PutAsJsonAsync("/api/v1/email/credential", new { value = "x" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal("CredentialNotNeeded", (await Json(r)).GetProperty("guard").GetString());
    }

    [Fact]
    public async Task Finished_messages_are_pruned_after_retention_with_one_audit_row_and_a_pending_one_is_kept()
    {
        using var e = await Start(true, null, ("Notifications:Email:RetentionDays", "30"));
        await e.Notify(e.RteId, "ApprovalRequested");
        await e.Processor.ProcessDueAsync();
        e.Provider.FailEverything = true;
        await e.Notify(e.RteId, "ApprovalRequested", entityId: "a2");
        e.Clock.Advance(TimeSpan.FromDays(31));
        using (WorkspaceScope.Enter("default")) Assert.Equal(1, await e.Processor.PruneAsync());
        Assert.Equal("Pending", Scalar(e.F, "SELECT Status FROM EmailOutbox"));
        Assert.Equal(1, Audits(e.F, "EmailMessage", "Pruned"));
    }

    [Fact]
    public async Task A_person_whose_address_is_not_an_address_keeps_their_inbox_row_and_the_problem_is_raised_once_not_swallowed()
    {
        using var e = await Start();
        Sql(e.F, $"UPDATE Users SET Email='bad address' WHERE Id='{e.RteId}'");   // the table's CHECK would refuse mail to it
        await e.Notify(e.RteId, "ApprovalRequested");
        await e.Notify(e.RteId, "ApprovalRequested", entityId: "a2");
        Assert.Equal(2, Count(e.F, "SELECT COUNT(*) FROM Notifications WHERE Kind='ApprovalRequested'"));   // the inbox is never lost to a mail problem
        Assert.Equal(0, Count(e.F, "SELECT COUNT(*) FROM EmailOutbox"));
        Assert.Equal(1, Count(e.F, "SELECT COUNT(*) FROM SyncAlerts WHERE SourceSystem='Notifications' AND Kind='DeliveryFailed' AND IsResolved=0"));   // one alert, counted
        Assert.Equal(2, Count(e.F, "SELECT OccurrenceCount FROM SyncAlerts"));
        Assert.Contains("bad address", Scalar(e.F, "SELECT ErrorMessage FROM SyncAlerts"));
        var test = await e.Rte.PostAsync("/api/v1/email/test", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, test.StatusCode);
        Assert.Equal("NoAddress", (await Json(test)).GetProperty("guard").GetString());
        // a person who chose Off is not nagged about their address
        Sql(e.F, $"INSERT INTO NotificationPreferences(Id,UserId,Category,Channel,Mode,UpdatedAt) VALUES('p1','{e.RteId}','DecisionMade','Email','Off','2026-11-27T00:00:00Z')");
        await e.Notify(e.RteId, "ApprovalApproved");
        Assert.Equal(2, Count(e.F, "SELECT OccurrenceCount FROM SyncAlerts"));
    }

    [Fact]
    public async Task Nobody_is_mailed_who_is_not_a_member_of_the_workspace_and_their_inbox_row_is_still_written()
    {
        using var e = await Start();
        Sql(e.F, "INSERT INTO Users(Id,Email,DisplayName,Role) VALUES('outsider','outsider@x.com','Outsider','Viewer')");   // no membership row
        await e.Notify("outsider", "ApprovalRequested");
        Assert.Equal(1, Count(e.F, "SELECT COUNT(*) FROM Notifications WHERE UserId='outsider'"));
        Assert.Equal(0, Count(e.F, "SELECT COUNT(*) FROM EmailOutbox"));
        Assert.Equal(0, Count(e.F, "SELECT COUNT(*) FROM SyncAlerts"));
    }
}
