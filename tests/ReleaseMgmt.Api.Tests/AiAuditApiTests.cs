using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ReleaseMgmt.Domain.Ai;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Services;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// REOS-184 (D58) over HTTP: the AI inventory (RTE, Release Manager and Governance Officer; readable while the module is off), the audit trail of AI actions, the human decision (once, by the requester or
/// an administrator, If-Match), and what the audit view and the integrity check say afterwards. The model is the deterministic fake, so no network is used.
/// </summary>
public class AiAuditApiTests
{
    private static ApiFactory Factory()
    {
        var dir = Directory.CreateTempSubdirectory("reos-ai-audit-").FullName;
        return new ApiFactory(settings: new Dictionary<string, string> { ["Ai:Provider"] = "fake", ["Ai:CredentialsDirectory"] = dir, ["Ai:Models:Drafting"] = "bigger-model" });
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private static async Task<HttpResponseMessage> Send(HttpClient c, HttpMethod m, string url, object? body = null, int? ifMatch = null)
    {
        using var req = new HttpRequestMessage(m, url);
        if (body is not null) req.Content = JsonContent.Create(body);
        if (ifMatch is int v) req.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{v}\""));
        return await c.SendAsync(req);
    }

    private static async Task Enable(HttpClient c) => (await Send(c, HttpMethod.Post, "/api/v1/modules/ai:enable", new { }, 0)).EnsureSuccessStatusCode();

    /// <summary>A draft asked for by <paramref name="email"/>, made through the governed client exactly as a feature will.</summary>
    private static async Task<string> DraftFor(ApiFactory f, string email, string context = "Gate evidence: two blockers are open.")
    {
        var r = await f.Services.GetRequiredService<IAiClient>().CompleteAsync(new AiRequest("default", AiFeatures.ReleaseNotes, "You draft release notes.", "Write the notes.",
            [new AiContextItem("Gate evidence", context)], 512, false, new AiCallMeta(UserId(f, email), "Train", "t-1", null, "release-notes/1")));
        Assert.True(r.IsCompleted, r.Detail);
        return r.ActionId!;
    }

    [Fact]
    public async Task The_inventory_is_for_administrators_and_governance_officers_only()
    {
        using var f = Factory();
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().GetAsync("/api/v1/ai/inventory")).StatusCode);
        foreach (var (role, want) in new[] { (Roles.Viewer, HttpStatusCode.Forbidden), (Roles.RTE, HttpStatusCode.OK), (Roles.ReleaseManager, HttpStatusCode.OK), (Roles.GovernanceOfficer, HttpStatusCode.OK) })
        {
            var c = await As(f, role, $"{role.ToLowerInvariant()}@x.com");
            Assert.Equal(want, (await c.GetAsync("/api/v1/ai/inventory")).StatusCode);
            Assert.Equal(want, (await c.GetAsync("/api/v1/ai/actions")).StatusCode);
        }
    }

    [Fact]
    public async Task The_inventory_answers_while_the_module_is_off_and_says_so()
    {
        using var f = Factory();
        var gov = await As(f, Roles.GovernanceOfficer, "gov@x.com");
        var inv = await Json(await gov.GetAsync("/api/v1/ai/inventory"));
        var c = inv.GetProperty("controls");
        Assert.False(c.GetProperty("moduleEnabled").GetBoolean());
        Assert.False(c.GetProperty("canCall").GetBoolean());
        Assert.Contains("module is off", c.GetProperty("blockedBecause").GetString());
        Assert.Equal(AiFeatures.All.Count, inv.GetProperty("features").GetArrayLength());
        Assert.All(inv.GetProperty("features").EnumerateArray(), x => Assert.False(x.GetProperty("enabled").GetBoolean()));
        Assert.Contains("CustomerData", string.Join(' ', inv.GetProperty("data").GetProperty("neverSent").EnumerateArray().Select(e => e.GetString())));
        Assert.True(inv.GetProperty("data").GetProperty("hashesOnly").GetBoolean());
    }

    [Fact]
    public async Task A_draft_is_listed_without_its_text_and_read_in_full_by_id()
    {
        using var f = Factory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var viewer = await As(f, Roles.Viewer, "dev@x.com");
        await Enable(rte);
        var id = await DraftFor(f, "dev@x.com");
        var inv = await Json(await rte.GetAsync("/api/v1/ai/inventory"));
        var notes = inv.GetProperty("features").EnumerateArray().Single(x => x.GetProperty("key").GetString() == AiFeatures.ReleaseNotes);
        Assert.True(notes.GetProperty("enabled").GetBoolean());
        Assert.Equal("bigger-model", notes.GetProperty("model").GetString());
        Assert.Equal(1, notes.GetProperty("pending").GetInt32());
        Assert.Equal("Pending", (await Json(await rte.GetAsync("/api/v1/ai/actions?decision=Pending")))[0].GetProperty("decision").GetString());
        var list = await Json(await rte.GetAsync("/api/v1/ai/actions"));
        Assert.False(list[0].TryGetProperty("outputText", out _));   // a list line never carries a text
        var full = await Json(await viewer.GetAsync($"/api/v1/ai/actions/{id}"));
        Assert.Contains("fake:bigger-model", full.GetProperty("outputText").GetString());
        Assert.True(full.GetProperty("integrity").GetProperty("ok").GetBoolean());
        Assert.DoesNotContain("two blockers", await (await viewer.GetAsync($"/api/v1/ai/actions/{id}")).Content.ReadAsStringAsync());   // the context text is never in the trail
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync("/api/v1/ai/actions/nope")).StatusCode);
    }

    [Fact]
    public async Task The_requester_edits_the_draft_once_and_the_audit_view_shows_the_draft_and_the_edit_and_the_chain_verifies()
    {
        using var f = Factory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var viewer = await As(f, Roles.Viewer, "dev@x.com");
        var other = await As(f, Roles.Viewer, "other@x.com");
        await Enable(rte);
        var id = await DraftFor(f, "dev@x.com");
        var draft = (await Json(await viewer.GetAsync($"/api/v1/ai/actions/{id}"))).GetProperty("outputText").GetString();

        Assert.Equal(HttpStatusCode.PreconditionRequired, (await Send(viewer, HttpMethod.Post, $"/api/v1/ai/actions/{id}:decide", new { decision = "Accepted" })).StatusCode);   // If-Match is required
        var stranger = await Send(other, HttpMethod.Post, $"/api/v1/ai/actions/{id}:decide", new { decision = "Accepted" }, 1);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, stranger.StatusCode);
        Assert.Equal("AiActionRole", (await Json(stranger)).GetProperty("guard").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await Send(viewer, HttpMethod.Post, $"/api/v1/ai/actions/{id}:decide", new { decision = "Accepted" }, 9)).StatusCode);
        var same = await Send(viewer, HttpMethod.Post, $"/api/v1/ai/actions/{id}:decide", new { decision = "Edited", finalText = draft }, 1);
        Assert.Equal("AiActionDecision", (await Json(same)).GetProperty("guard").GetString());

        var edited = await Json(await Send(viewer, HttpMethod.Post, $"/api/v1/ai/actions/{id}:decide", new { decision = "Edited", finalText = "Hand-written notes for R26.12." }, 1));
        Assert.Equal(("Edited", 2), (edited.GetProperty("decision").GetString(), edited.GetProperty("version").GetInt32()));
        Assert.Equal(draft, edited.GetProperty("outputText").GetString());
        Assert.Equal("Hand-written notes for R26.12.", edited.GetProperty("finalText").GetString());
        var again = await Send(viewer, HttpMethod.Post, $"/api/v1/ai/actions/{id}:decide", new { decision = "Rejected" }, 2);
        Assert.Equal("AiActionDecided", (await Json(again)).GetProperty("guard").GetString());

        // the audit view: the Generated row and the Edited row, the second holding the draft (before) and the edit (after)
        var audit = await Json(await rte.GetAsync($"/api/v1/audit?entity=AiAction&entityId={id}"));
        var items = audit.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["Edited", "Generated"], items.Select(i => i.GetProperty("action").GetString()!).ToArray());
        var row = items[0];
        Assert.Equal(draft, JsonDocument.Parse(row.GetProperty("beforeJson").GetString()!).RootElement.GetProperty("output").GetString());
        Assert.Equal("Hand-written notes for R26.12.", JsonDocument.Parse(row.GetProperty("afterJson").GetString()!).RootElement.GetProperty("final").GetString());

        // and the chain verifies, with the AI rows in it
        await f.Services.GetRequiredService<AuditChainService>().SealPendingAsync();
        var integrity = await Json(await rte.GetAsync("/api/v1/audit/integrity?recheck=true"));
        Assert.Equal("Verified", integrity.GetProperty("status").GetString());
        Assert.True(integrity.GetProperty("sealedRows").GetInt64() >= 3);
    }

    [Fact]
    public async Task A_rejection_and_an_administrators_decision()
    {
        using var f = Factory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        await As(f, Roles.Viewer, "dev@x.com");
        await Enable(rte);
        var id = await DraftFor(f, "dev@x.com");
        var rejected = await Json(await Send(rte, HttpMethod.Post, $"/api/v1/ai/actions/{id}:decide", new { decision = "Rejected", note = "Wrong release" }, 1));   // an administrator may decide another person's draft
        Assert.Equal(("Rejected", "Wrong release"), (rejected.GetProperty("decision").GetString(), rejected.GetProperty("decisionNote").GetString()));
        Assert.Equal("rte", rejected.GetProperty("decidedBy").GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Send(rte, HttpMethod.Post, $"/api/v1/ai/actions/{id}:decide", new { decision = "bogus" }, 2)).StatusCode);
    }

    [Fact]
    public async Task A_refusal_is_on_the_trail_and_cannot_be_decided()
    {
        using var f = Factory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var r = await f.Services.GetRequiredService<IAiClient>().CompleteAsync(new AiRequest("default", AiFeatures.RiskScore, "s", "go"));   // the module is off
        Assert.Equal(AiOutcome.ModuleOff, r.Outcome);
        var a = await Json(await rte.GetAsync($"/api/v1/ai/actions/{r.ActionId}"));
        Assert.Equal(("ModuleOff", "NotApplicable"), (a.GetProperty("outcome").GetString(), a.GetProperty("decision").GetString()));
        var decide = await Send(rte, HttpMethod.Post, $"/api/v1/ai/actions/{r.ActionId}:decide", new { decision = "Accepted" }, 1);
        Assert.Equal("AiActionNotDecidable", (await Json(decide)).GetProperty("guard").GetString());
    }

    [Fact]
    public async Task The_controls_and_the_key_are_audited_and_the_key_is_never_in_the_trail()
    {
        using var f = Factory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        await Enable(rte);
        (await rte.PostAsJsonAsync("/api/v1/ai/workspace:pause", new { })).EnsureSuccessStatusCode();
        (await rte.PostAsJsonAsync("/api/v1/ai/workspace:resume", new { })).EnsureSuccessStatusCode();
        (await rte.PostAsJsonAsync("/api/v1/ai/workspace:opt-out", new { })).EnsureSuccessStatusCode();
        const string key = "sk-ant-api03-AUDIT-TEST-KEY-0123456789abcdef";
        Assert.Equal(HttpStatusCode.NoContent, (await rte.PutAsJsonAsync("/api/v1/ai/key", new { key })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await rte.DeleteAsync("/api/v1/ai/key")).StatusCode);
        var ws = await Json(await rte.GetAsync("/api/v1/audit?entity=AiWorkspace"));
        Assert.Equal(["OptedOut", "Resumed", "Paused"], ws.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("action").GetString()!).ToArray());
        var keyRows = await rte.GetAsync("/api/v1/audit?entity=AiKey");
        var text = await keyRows.Content.ReadAsStringAsync();
        Assert.DoesNotContain("AUDIT-TEST-KEY", text);
        Assert.Equal(["Removed", "Set"], JsonDocument.Parse(text).RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("action").GetString()!).ToArray());
        Assert.True((await Json(await rte.GetAsync("/api/v1/ai/status"))).GetProperty("workspaceOptedOut").GetBoolean());
    }
}
