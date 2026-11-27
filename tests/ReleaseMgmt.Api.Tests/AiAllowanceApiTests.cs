using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ReleaseMgmt.Domain.Ai;
using ReleaseMgmt.Domain.Common;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// REOS-185 (D57, D58) over HTTP: the AI allowance and usage ledger are for administrators (RTE, Release Manager), readable while the module is off, and there is no endpoint that adds credits (a workspace
/// administrator must not be able to give their own workspace credits). The model is the deterministic fake; prices are raised so a few calls use up a small allowance.
/// </summary>
public class AiAllowanceApiTests
{
    private static ApiFactory Factory(int includedCredits = 6)
    {
        var dir = Directory.CreateTempSubdirectory("reos-ai-allowance-").FullName;
        return new ApiFactory(settings: new Dictionary<string, string>
        {
            ["Ai:Provider"] = "fake", ["Ai:CredentialsDirectory"] = dir, ["Ai:Allowance:IncludedCredits"] = includedCredits.ToString(),
            ["Ai:Allowance:UserPerMinute"] = "0", ["Ai:Allowance:UserPerHour"] = "0",
            ["Ai:Pricing:Models:claude-haiku-5-5:InputPerMTok"] = "100", ["Ai:Pricing:Models:claude-haiku-5-5:OutputPerMTok"] = "100",   // about 3,400 micro-dollars a fake call, about 55,700 held while it runs
        });
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private static async Task Enable(HttpClient c)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/modules/ai:enable") { Content = JsonContent.Create(new { }) };
        req.Headers.IfMatch.Add(new EntityTagHeaderValue("\"0\""));
        (await c.SendAsync(req)).EnsureSuccessStatusCode();
    }

    private static Task<AiResult> Ask(ApiFactory f, string feature = AiFeatures.GateSummary) =>
        f.Services.GetRequiredService<IAiClient>().CompleteAsync(new AiRequest("default", feature, "You summarise gate evidence.", "Summarise.", [new AiContextItem("Evidence", "Two blockers are open.")], 512));

    [Fact]
    public async Task Usage_and_the_ledger_are_for_administrators_only()
    {
        using var f = Factory();
        foreach (var url in new[] { "/api/v1/ai/usage", "/api/v1/ai/usage/ledger" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().GetAsync(url)).StatusCode);
            foreach (var (role, want) in new[] { (Roles.Viewer, HttpStatusCode.Forbidden), (Roles.GovernanceOfficer, HttpStatusCode.Forbidden), (Roles.RTE, HttpStatusCode.OK), (Roles.ReleaseManager, HttpStatusCode.OK) })
                Assert.Equal(want, (await (await As(f, role, $"{role.ToLowerInvariant()}@x.com")).GetAsync(url)).StatusCode);
        }
    }

    [Fact]
    public async Task Usage_answers_while_the_module_is_off_with_the_whole_allowance_unused()
    {
        using var f = Factory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var u = await Json(await rte.GetAsync("/api/v1/ai/usage"));
        Assert.False(u.GetProperty("opened").GetBoolean());
        Assert.Equal((60_000, 0, 60_000, "Ok"), (u.GetProperty("totalMicroUsd").GetInt32(), u.GetProperty("usedMicroUsd").GetInt32(), u.GetProperty("remainingMicroUsd").GetInt32(), u.GetProperty("level").GetString()));
        Assert.Equal(10_000, u.GetProperty("microUsdPerCredit").GetInt32());
        Assert.Equal(AiFeatures.All.Count, u.GetProperty("features").GetArrayLength());
        Assert.Equal(0, (await Json(await rte.GetAsync("/api/v1/ai/usage/ledger"))).GetArrayLength());
    }

    [Fact]
    public async Task There_is_no_endpoint_that_adds_credits()
    {
        using var f = Factory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        foreach (var url in new[] { "/api/v1/ai/usage/packs", "/api/v1/ai/packs", "/api/v1/ai/credits", "/api/v1/ai/usage" })
            foreach (var m in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch })
            {
                using var req = new HttpRequestMessage(m, url) { Content = JsonContent.Create(new { credits = 1000 }) };
                var status = (await rte.SendAsync(req)).StatusCode;
                Assert.True(status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, $"{m} {url} answered {status}");
            }
        Assert.Equal(0, (await Json(await rte.GetAsync("/api/v1/ai/usage"))).GetProperty("packMicroUsd").GetInt32());
    }

    [Fact]
    public async Task Calls_appear_in_the_usage_and_the_ledger_and_a_request_the_allowance_cannot_cover_is_refused_with_its_reason()
    {
        using var f = Factory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        await Enable(rte);
        for (var i = 0; i < 2; i++) { var ok = await Ask(f); Assert.True(ok.IsCompleted, i + ": " + ok.Outcome + " " + ok.Detail); }
        var refused = await Ask(f);                                                    // 6,800 used and about 55,700 held would pass the 60,000
        Assert.Equal(AiOutcome.AllowanceReached, refused.Outcome);
        Assert.Contains("not enough for this request", refused.Detail);

        var u = await Json(await rte.GetAsync("/api/v1/ai/usage"));
        Assert.True(u.GetProperty("opened").GetBoolean());
        Assert.InRange(u.GetProperty("usedMicroUsd").GetInt32(), 1, 59_999);
        Assert.Equal(0, u.GetProperty("reservedMicroUsd").GetInt32());
        var gate = u.GetProperty("features").EnumerateArray().Single(x => x.GetProperty("feature").GetString() == "gate-summary");
        Assert.Equal((2, 1), (gate.GetProperty("calls").GetInt32(), gate.GetProperty("refused").GetInt32()));
        Assert.Equal("Ok", u.GetProperty("level").GetString());

        var ledger = await Json(await rte.GetAsync("/api/v1/ai/usage/ledger"));
        Assert.Equal(3, ledger.GetArrayLength());
        Assert.Equal(2, ledger.EnumerateArray().Count(e => e.GetProperty("state").GetString() == "Settled"));
        var r = ledger.EnumerateArray().Single(e => e.GetProperty("state").GetString() == "Refused");
        Assert.Equal(("HardCap", 0), (r.GetProperty("refusalReason").GetString(), r.GetProperty("costMicroUsd").GetInt32()));
        Assert.Equal(refused.ActionId, r.GetProperty("aiActionId").GetString());

        var action = await Json(await rte.GetAsync($"/api/v1/ai/actions/{refused.ActionId}"));   // the audit trail has the same refusal, as Blocked
        Assert.Equal(("Blocked", "NotApplicable", "HardCap"), (action.GetProperty("outcome").GetString(), action.GetProperty("decision").GetString(), action.GetProperty("capReason").GetString()));
    }

    [Fact]
    public async Task When_the_allowance_is_used_up_the_status_and_preflight_say_so_in_words()
    {
        using var f = Factory(includedCredits: 0);
        var rte = await As(f, Roles.RTE, "rte@x.com");
        await Enable(rte);
        var status = await Json(await rte.GetAsync("/api/v1/ai/status"));
        Assert.False(status.GetProperty("canCall").GetBoolean());
        Assert.Contains("allowance", status.GetProperty("blockedBecause").GetString());
        Assert.Contains("renews on", status.GetProperty("blockedBecause").GetString());
        var pre = await Json(await rte.GetAsync("/api/v1/ai/preflight?feature=chat"));
        Assert.False(pre.GetProperty("allowed").GetBoolean());
        var u = await Json(await rte.GetAsync("/api/v1/ai/usage"));
        Assert.Equal(("Hard", 100), (u.GetProperty("level").GetString(), u.GetProperty("percentUsed").GetInt32()));
        Assert.Contains("used up", u.GetProperty("message").GetString());
    }
}
