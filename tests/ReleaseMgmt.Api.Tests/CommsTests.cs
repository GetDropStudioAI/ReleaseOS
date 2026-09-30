using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ReleaseMgmt.Domain.Common;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>REOS-43/44: comm library, per-train copies, T-minus schedule, mark-sent and the preview endpoint (roles, shapes, If-Match, audit).</summary>
public class CommsTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private static async Task<HttpResponseMessage> Send(HttpClient c, HttpMethod m, string url, object? body = null, string? ifMatch = null)
    {
        var req = new HttpRequestMessage(m, url) { Content = body is null ? (m == HttpMethod.Get ? null : JsonContent.Create(new { })) : JsonContent.Create(body) };
        if (ifMatch is not null) req.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return await c.SendAsync(req);
    }

    private static async Task<string> Guard(HttpResponseMessage r)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        return (await Json(r)).GetProperty("guard").GetString()!;
    }

    private sealed record Ctx(ApiFactory F, HttpClient Rte, HttpClient Rm, HttpClient Gov, HttpClient Viewer, HttpClient Anon) : IDisposable
    {
        public void Dispose() { F.Dispose(); }
    }

    private static async Task<Ctx> Setup(bool requireIfMatch = false)
    {
        var f = new ApiFactory(requireIfMatch: requireIfMatch);
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var rm = await As(f, Roles.ReleaseManager, "rm@x.com");
        var gov = await As(f, Roles.GovernanceOfficer, "gov@x.com");
        var viewer = await As(f, Roles.Viewer, "v@x.com");
        SeedTrain(f, UserId(f, "rte@x.com"), UserId(f, "gov@x.com"));   // t1, target 2026-10-30, gates Code Freeze and Compliance Sign-off
        return new Ctx(f, rte, rm, gov, viewer, f.CreateClient());
    }

    private static object Lib(string name = "Custom notice", string body = "**{ReleaseTitle}** is {Status}") => new { name, templateType = "Custom", audience = "All", subjectLine = "[{ReleaseTitle}] notice", markdownBody = body };

    private static async Task<string> DefaultTemplateId(HttpClient c) =>
        (await c.GetFromJsonAsync<JsonElement>("/api/v1/templates")).EnumerateArray().Single(t => t.GetProperty("name").GetString() == "Standard release").GetProperty("id").GetString()!;

    private static long Audits(ApiFactory f, string type, string action) => long.Parse(Scalar(f, $"SELECT COUNT(*) FROM AuditEvents WHERE EntityType='{type}' AND Action='{action}'"));

    // ---- seeded defaults and the templates screen's picker
    [Fact]
    public async Task The_reference_seed_provides_the_six_standard_messages_and_the_default_template_uses_them()
    {
        using var c = await Setup();
        var lib = (await c.Viewer.GetFromJsonAsync<JsonElement>("/api/v1/comm-library")).EnumerateArray().ToList();
        Assert.Equal(6, lib.Count);
        Assert.All(lib, l => { Assert.True(l.GetProperty("valid").GetBoolean()); Assert.Empty(l.GetProperty("tokenErrors").EnumerateArray()); });
        var options = (await c.Rte.GetFromJsonAsync<JsonElement>("/api/v1/templates/library-options")).EnumerateArray().Select(o => o.GetProperty("name").GetString()).ToList();
        Assert.Contains("T-1 Go/No-Go outcome", options);
        var t = await c.Rte.GetFromJsonAsync<JsonElement>($"/api/v1/templates/{await DefaultTemplateId(c.Rte)}");
        Assert.Equal([-7, -3, -1, 0, 0, 5], t.GetProperty("schedule").EnumerateArray().Select(s => s.GetProperty("offsetDays").GetInt32()).Order().ToList());
    }

    [Fact]
    public async Task Token_list_is_the_allowlist_with_descriptions()
    {
        using var c = await Setup();
        var toks = (await c.Viewer.GetFromJsonAsync<JsonElement>("/api/v1/comm-library/tokens")).EnumerateArray().ToList();
        Assert.Equal(22, toks.Count);
        Assert.Contains(toks, t => t.GetProperty("name").GetString() == "GoNoGoDecision" && t.GetProperty("description").GetString()!.Length > 5);
    }

    // ---- roles
    [Fact]
    public async Task Everyone_signed_in_reads_but_only_RTE_and_Release_Manager_edit()
    {
        using var c = await Setup();
        var libId = (await c.Rte.GetFromJsonAsync<JsonElement>("/api/v1/comm-library")).EnumerateArray().First().GetProperty("id").GetString()!;
        var tt = await DefaultTemplateId(c.Rte);
        foreach (var reader in new[] { c.Viewer, c.Gov, c.Rte, c.Rm })
            foreach (var url in new[] { "/api/v1/comm-library", $"/api/v1/comm-library/{libId}", "/api/v1/comm-library/tokens", "/api/v1/trains/t1/comms", "/api/v1/trains/t1/comm-schedule" })
                Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.Anon.GetAsync("/api/v1/comm-library")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(c.Anon, HttpMethod.Post, "/api/v1/trains/t1/comms:preview", new { text = "x" })).StatusCode);

        foreach (var denied in new[] { c.Viewer, c.Gov })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await Send(denied, HttpMethod.Post, "/api/v1/comm-library", Lib("A"))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Send(denied, HttpMethod.Put, $"/api/v1/comm-library/{libId}", Lib("A"), "1")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Send(denied, HttpMethod.Post, "/api/v1/trains/t1/comms", new { libraryTemplateId = libId })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Send(denied, HttpMethod.Post, "/api/v1/trains/t1/comm-schedule:seed", new { templateId = tt })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Send(denied, HttpMethod.Post, "/api/v1/comm-schedule/x:mark-sent", null, "1")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Send(denied, HttpMethod.Post, "/api/v1/trains/t1/comms:preview", new { text = "x" })).StatusCode);   // preview only reads
        }
        Assert.Equal(HttpStatusCode.OK, (await Send(c.Rte, HttpMethod.Post, "/api/v1/comm-library", Lib("By RTE"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(c.Rm, HttpMethod.Post, "/api/v1/comm-library", Lib("By RM"))).StatusCode);
    }

    // ---- library: contract, If-Match, audit
    [Fact]
    public async Task Library_create_update_with_If_Match_409_current_and_audit()
    {
        using var c = await Setup(requireIfMatch: true);
        var created = await Send(c.Rte, HttpMethod.Post, "/api/v1/comm-library", Lib());
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var j = await Json(created); var id = j.GetProperty("id").GetString()!;
        Assert.Equal(1, j.GetProperty("version").GetInt32());
        Assert.Equal(["ReleaseTitle", "Status"], j.GetProperty("tokensUsed").EnumerateArray().Select(x => x.GetString()).ToList());

        Assert.Equal(HttpStatusCode.PreconditionRequired, (await Send(c.Rte, HttpMethod.Put, $"/api/v1/comm-library/{id}", Lib(body: "b"))).StatusCode);   // no If-Match -> 428
        var ok = await Send(c.Rm, HttpMethod.Put, $"/api/v1/comm-library/{id}", Lib(body: "Changed {Status}"), "1");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(2, (await Json(ok)).GetProperty("version").GetInt32());

        var stale = await Send(c.Rte, HttpMethod.Put, $"/api/v1/comm-library/{id}", Lib(body: "again"), "1");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(2, (await Json(stale)).GetProperty("current").GetProperty("version").GetInt32());   // 409 carries the current row

        Assert.Equal(1, Audits(c.F, "CommLibraryTemplate", "Create")); Assert.Equal(1, Audits(c.F, "CommLibraryTemplate", "Update"));
        Assert.Equal("1", Scalar(c.F, $"SELECT COUNT(*) FROM AuditEvents WHERE EntityId='{id}' AND Action='Update' AND ActorUserId='{UserId(c.F, "rm@x.com")}'"));
        Assert.Equal("Changed {Status}", Scalar(c.F, $"SELECT MarkdownBody FROM CommTemplateLibrary WHERE Id='{id}'"));
        Assert.Equal(HttpStatusCode.NotFound, (await Send(c.Rte, HttpMethod.Put, "/api/v1/comm-library/none", Lib(), "1")).StatusCode);
    }

    [Fact]
    public async Task Library_validation_returns_422_with_the_guard_and_a_saved_template_may_carry_token_errors()
    {
        using var c = await Setup();
        Assert.Equal("CommTemplateNameTaken", await Guard(await Send(c.Rte, HttpMethod.Post, "/api/v1/comm-library", Lib("t-7 readiness NOTICE"))));
        Assert.Equal("CommTemplateInvalid", await Guard(await Send(c.Rte, HttpMethod.Post, "/api/v1/comm-library", new { name = "X", templateType = "T", audience = "Nobody", subjectLine = "s", markdownBody = "b" })));
        var typo = await Send(c.Rte, HttpMethod.Post, "/api/v1/comm-library", Lib("Has a typo", "Hi {ReleaseTitel}"));
        Assert.Equal(HttpStatusCode.OK, typo.StatusCode);   // saved; dispatch is the gate
        var j = await Json(typo);
        Assert.False(j.GetProperty("valid").GetBoolean());
        var err = j.GetProperty("tokenErrors")[0];
        Assert.Equal("UnknownToken", err.GetProperty("kind").GetString()); Assert.Equal("{ReleaseTitle}", err.GetProperty("suggestion").GetString());
    }

    // ---- preview
    [Fact]
    public async Task Preview_returns_text_tokenErrors_asOf_and_trainVersion()
    {
        using var c = await Setup();
        var r = await Send(c.Viewer, HttpMethod.Post, "/api/v1/trains/t1/comms:preview", new { subject = "[{ReleaseTitle}]", text = "{ReleaseTitle} is {Status}. Blockers: {BlockerList}", target = "PlainText" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await Json(r);
        Assert.Equal("R26.10 is Planning. Blockers: None", j.GetProperty("text").GetString());   // empty list prints None
        Assert.Equal("[R26.10]", j.GetProperty("subject").GetString());
        Assert.Empty(j.GetProperty("tokenErrors").EnumerateArray());
        Assert.True(j.GetProperty("canDispatch").GetBoolean());
        Assert.Equal(1, j.GetProperty("trainVersion").GetInt32());
        Assert.True(DateTime.Parse(j.GetProperty("asOf").GetString()!).ToUniversalTime() > DateTime.UtcNow.AddMinutes(-2));
        Assert.Equal("PlainText", j.GetProperty("target").GetString());
        Assert.Equal(["ReleaseTitle", "Status", "BlockerList"], j.GetProperty("tokensUsed").EnumerateArray().Select(x => x.GetString()).ToList());
    }

    [Fact]
    public async Task Preview_of_an_unknown_token_reports_it_and_blocks_dispatch()
    {
        using var c = await Setup();
        var j = await Json(await Send(c.Rte, HttpMethod.Post, "/api/v1/trains/t1/comms:preview", new { text = "Hello {ReleaseTitel} and {Status", target = "Markdown" }));
        Assert.False(j.GetProperty("canDispatch").GetBoolean());
        var errs = j.GetProperty("tokenErrors").EnumerateArray().ToList();
        Assert.Equal(2, errs.Count);
        Assert.Equal("UnknownToken", errs[0].GetProperty("kind").GetString());
        Assert.Equal("{ReleaseTitel}", errs[0].GetProperty("token").GetString());
        Assert.Equal((1, 7), (errs[0].GetProperty("line").GetInt32(), errs[0].GetProperty("column").GetInt32()));
        Assert.Contains("Did you mean {ReleaseTitle}", errs[0].GetProperty("message").GetString());
        Assert.Equal("UnclosedBrace", errs[1].GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Preview_escapes_per_target()
    {
        using var c = await Setup();
        Sql(c.F, "UPDATE ReleaseTrains SET Title='<b>R26</b> & \"x\" *y*' WHERE Id='t1'");
        async Task<string> Text(string target) => (await Json(await Send(c.Rte, HttpMethod.Post, "/api/v1/trains/t1/comms:preview", new { text = "{ReleaseTitle}", target }))).GetProperty("text").GetString()!;
        Assert.Equal("<b>R26</b> & \"x\" *y*", await Text("PlainText"));
        Assert.Equal("\\<b\\>R26\\</b\\> & \"x\" \\*y\\*", await Text("Markdown"));
        Assert.Equal("&lt;b&gt;R26&lt;/b&gt; &amp; &quot;x&quot; *y*", await Text("Html"));
        var json = await Text("jsonstring");   // target is case-insensitive: Markdown escaping, then JSON string escaping of the whole text
        Assert.Equal("\\\\<b\\\\>R26\\\\</b\\\\> & \\\"x\\\" \\\\*y\\\\*", json);
        Assert.DoesNotContain("\"x\"", json);   // quotes are escaped for a JSON string
    }

    [Fact]
    public async Task Preview_sources_targets_and_not_found()
    {
        using var c = await Setup();
        var libId = (await c.Rte.GetFromJsonAsync<JsonElement>("/api/v1/comm-library")).EnumerateArray().First(l => l.GetProperty("name").GetString() == "T-1 Go/No-Go outcome").GetProperty("id").GetString()!;
        var lib = await Json(await Send(c.Rte, HttpMethod.Post, "/api/v1/trains/t1/comms:preview", new { libraryTemplateId = libId, target = "Markdown" }));
        Assert.StartsWith("[R26.10] Go/No-Go: Not yet recorded", lib.GetProperty("subject").GetString());
        Assert.True(lib.GetProperty("canDispatch").GetBoolean());
        Assert.Equal(libId, lib.GetProperty("templateId").GetString());

        var copy = await Json(await Send(c.Rte, HttpMethod.Post, "/api/v1/trains/t1/comms", new { libraryTemplateId = libId }));
        var byCopy = await Json(await Send(c.Rte, HttpMethod.Post, "/api/v1/trains/t1/comms:preview", new { templateId = copy.GetProperty("id").GetString() }));   // target defaults to Markdown
        Assert.Equal("Markdown", byCopy.GetProperty("target").GetString());
        Assert.Equal(lib.GetProperty("text").GetString(), byCopy.GetProperty("text").GetString());

        Assert.Equal("CommPreviewSource", await Guard(await Send(c.Rte, HttpMethod.Post, "/api/v1/trains/t1/comms:preview", new { })));
        Assert.Equal("CommPreviewSource", await Guard(await Send(c.Rte, HttpMethod.Post, "/api/v1/trains/t1/comms:preview", new { text = "x", libraryTemplateId = libId })));
        Assert.Equal("CommPreviewTarget", await Guard(await Send(c.Rte, HttpMethod.Post, "/api/v1/trains/t1/comms:preview", new { text = "x", target = "Pdf" })));
        Assert.Equal(HttpStatusCode.NotFound, (await Send(c.Rte, HttpMethod.Post, "/api/v1/trains/nope/comms:preview", new { text = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(c.Rte, HttpMethod.Post, "/api/v1/trains/t1/comms:preview", new { libraryTemplateId = "nope" })).StatusCode);
    }

    [Fact]
    public async Task Preview_trainVersion_moves_with_the_train()
    {
        using var c = await Setup();
        Sql(c.F, "UPDATE ReleaseTrains SET Version=7 WHERE Id='t1'");
        var j = await Json(await Send(c.Rte, HttpMethod.Post, "/api/v1/trains/t1/comms:preview", new { text = "x" }));
        Assert.Equal(7, j.GetProperty("trainVersion").GetInt32());
    }

    // ---- per-train copies
    [Fact]
    public async Task Per_train_copy_edit_If_Match_and_lock_after_dispatch()
    {
        using var c = await Setup(requireIfMatch: true);
        var libId = (await c.Rte.GetFromJsonAsync<JsonElement>("/api/v1/comm-library")).EnumerateArray().First().GetProperty("id").GetString()!;
        var copy = await Json(await Send(c.Rte, HttpMethod.Post, "/api/v1/trains/t1/comms", new { libraryTemplateId = libId }));
        var id = copy.GetProperty("id").GetString()!;
        Assert.Equal(1, copy.GetProperty("version").GetInt32()); Assert.False(copy.GetProperty("dispatched").GetBoolean());
        Assert.Equal(1, (await c.Viewer.GetFromJsonAsync<JsonElement>("/api/v1/trains/t1/comms")).GetArrayLength());

        var body = new { audience = "Ops", subjectLine = "Edited {Status}", markdownBody = "Edited {ReleaseTitle}" };
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await Send(c.Rte, HttpMethod.Put, $"/api/v1/comm-templates/{id}", body)).StatusCode);
        var ok = await Json(await Send(c.Rm, HttpMethod.Put, $"/api/v1/comm-templates/{id}", body, "1"));
        Assert.Equal((2, "Ops"), (ok.GetProperty("version").GetInt32(), ok.GetProperty("audience").GetString()));
        var stale = await Send(c.Rte, HttpMethod.Put, $"/api/v1/comm-templates/{id}", body, "1");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(2, (await Json(stale)).GetProperty("current").GetProperty("version").GetInt32());
        Assert.Equal(1, Audits(c.F, "CommTemplate", "Copy")); Assert.Equal(1, Audits(c.F, "CommTemplate", "Update"));

        Sql(c.F, $"INSERT INTO CommDispatches(Id,CommTemplateId,Channel,HydratedSubject,HydratedBody,DispatchedByUserId,DispatchedAt,Outcome) VALUES('d1','{id}','Copy','s','b','{UserId(c.F, "rte@x.com")}','2026-10-20T14:00:00Z','Handed')");
        Assert.Equal("CommTemplateDispatched", await Guard(await Send(c.Rte, HttpMethod.Put, $"/api/v1/comm-templates/{id}", body, "2")));
        Assert.True((await c.Viewer.GetFromJsonAsync<JsonElement>($"/api/v1/comm-templates/{id}")).GetProperty("dispatched").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await c.Viewer.GetAsync("/api/v1/comm-templates/none")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.Viewer.GetAsync("/api/v1/trains/none/comms")).StatusCode);
    }

    // ---- schedule
    [Fact]
    public async Task Seeding_the_schedule_from_the_default_template_then_recording_SentAt_against_DueAt()
    {
        using var c = await Setup(requireIfMatch: true);
        var tt = await DefaultTemplateId(c.Rte);
        Assert.Empty((await c.Viewer.GetFromJsonAsync<JsonElement>("/api/v1/trains/t1/comm-schedule")).EnumerateArray());

        var seeded = await Send(c.Rte, HttpMethod.Post, "/api/v1/trains/t1/comm-schedule:seed", new { templateId = tt });   // no If-Match: it creates
        Assert.Equal(HttpStatusCode.OK, seeded.StatusCode);
        var items = (await Json(seeded)).EnumerateArray().ToList();
        Assert.Equal(["T−7", "T−3", "T−1", "T0", "T0", "T+5"], items.Select(i => i.GetProperty("label").GetString()).ToList());
        Assert.All(items, i => Assert.Equal(JsonValueKind.Null, i.GetProperty("sentAt").ValueKind));
        Assert.Equal("T-7 Readiness notice", items[0].GetProperty("name").GetString());
        var due = items.Select(i => DateTime.Parse(i.GetProperty("dueAt").GetString()!).ToUniversalTime()).ToList();
        Assert.Equal(due.Order().ToList(), due);   // sorted by DueAt
        Assert.Equal(6, (await c.Gov.GetFromJsonAsync<JsonElement>("/api/v1/trains/t1/comm-schedule")).GetArrayLength());
        Assert.Equal(1, Audits(c.F, "CommSchedule", "Seed"));
        Assert.Equal("6", Scalar(c.F, "SELECT COUNT(*) FROM CommTemplates WHERE ReleaseTrainId='t1'"));

        Assert.Equal("CommScheduleExists", await Guard(await Send(c.Rte, HttpMethod.Post, "/api/v1/trains/t1/comm-schedule:seed", new { templateId = tt })));
        Assert.Equal(HttpStatusCode.NotFound, (await Send(c.Rte, HttpMethod.Post, "/api/v1/trains/t1/comm-schedule:seed", new { templateId = "none" })).StatusCode);

        var first = items[0]; var sid = first.GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await Send(c.Rte, HttpMethod.Post, $"/api/v1/comm-schedule/{sid}:mark-sent")).StatusCode);
        var stale = await Send(c.Rte, HttpMethod.Post, $"/api/v1/comm-schedule/{sid}:mark-sent", null, "9");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(sid, (await Json(stale)).GetProperty("current").GetProperty("id").GetString());

        var sent = await Send(c.Rm, HttpMethod.Post, $"/api/v1/comm-schedule/{sid}:mark-sent", null, "1");
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        var s = await Json(sent);
        var sentAt = DateTime.Parse(s.GetProperty("sentAt").GetString()!).ToUniversalTime(); var dueAt = DateTime.Parse(s.GetProperty("dueAt").GetString()!).ToUniversalTime();
        Assert.True(sentAt > DateTime.UtcNow.AddMinutes(-2));   // from the service clock
        Assert.Equal(sentAt > dueAt, s.GetProperty("late").GetBoolean());
        Assert.Equal(sentAt > dueAt ? "SentLate" : "Sent", s.GetProperty("state").GetString());
        Assert.Equal(2, s.GetProperty("version").GetInt32());
        Assert.Equal(1, Audits(c.F, "CommSchedule", "MarkSent"));
        Assert.Equal("1", Scalar(c.F, $"SELECT COUNT(*) FROM CommSchedule WHERE Id='{sid}' AND SentAt IS NOT NULL AND Version=2"));

        Assert.Equal("CommAlreadySent", await Guard(await Send(c.Rte, HttpMethod.Post, $"/api/v1/comm-schedule/{sid}:mark-sent", null, "2")));
        Assert.Equal(HttpStatusCode.NotFound, (await Send(c.Rte, HttpMethod.Post, "/api/v1/comm-schedule/none:mark-sent", null, "1")).StatusCode);
        Assert.Equal(1, Audits(c.F, "CommSchedule", "MarkSent"));
    }

    [Fact]
    public async Task Seeding_an_unknown_train_is_404_and_an_empty_plan_is_422()
    {
        using var c = await Setup();
        var tt = await DefaultTemplateId(c.Rte);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(c.Rte, HttpMethod.Post, "/api/v1/trains/none/comm-schedule:seed", new { templateId = tt })).StatusCode);
        var empty = await Json(await Send(c.Rte, HttpMethod.Post, "/api/v1/templates", new { name = "No plan", gates = new[] { new { gateName = "G", gateClass = "Standard", offsetDays = 2, requiredBeforeStatus = "Gated" } } }));
        Assert.Equal("CommPlanEmpty", await Guard(await Send(c.Rte, HttpMethod.Post, "/api/v1/trains/t1/comm-schedule:seed", new { templateId = empty.GetProperty("id").GetString() })));
        Assert.Equal("0", Scalar(c.F, "SELECT COUNT(*) FROM CommSchedule"));
    }
}
