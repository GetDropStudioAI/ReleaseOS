using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReleaseMgmt.Domain.Ai;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Services;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>REOS-183 (D58): the AI platform over HTTP. The module guard (off means 404 and no call), the admin status (never the key), the workspace controls, and the key that reaches no log, response or database.</summary>
public class AiPlatformTests
{
    private const string Key = "sk-ant-api03-API-TEST-KEY-0123456789abcdefghij";

    private static ApiFactory Factory(Dictionary<string, string>? extra = null)
    {
        var dir = Directory.CreateTempSubdirectory("reos-ai-key-").FullName;
        var s = new Dictionary<string, string> { ["Ai:Provider"] = "fake", ["Ai:CredentialsDirectory"] = dir };
        foreach (var (k, v) in extra ?? []) s[k] = v;
        return new ApiFactory(settings: s);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private static async Task Enable(HttpClient c)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/modules/ai:enable") { Content = JsonContent.Create(new { }) };
        req.Headers.IfMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue("\"0\""));
        (await c.SendAsync(req)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task While_the_module_is_off_the_ai_endpoints_answer_404_and_the_status_says_so()
    {
        using var f = Factory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var pre = await rte.GetAsync("/api/v1/ai/preflight");
        Assert.Equal(HttpStatusCode.NotFound, pre.StatusCode);
        Assert.Equal("", await pre.Content.ReadAsStringAsync());   // exactly as a route that does not exist
        var st = await Json(await rte.GetAsync("/api/v1/ai/status"));   // the settings panel reads it while the module is off
        Assert.False(st.GetProperty("moduleEnabled").GetBoolean());
        Assert.False(st.GetProperty("canCall").GetBoolean());
        Assert.Contains("module is off", st.GetProperty("blockedBecause").GetString());
    }

    [Fact]
    public async Task With_the_module_on_the_preflight_reports_a_possible_call_and_the_model_it_would_use()
    {
        using var f = Factory(extra: new() { ["Ai:Models:Drafting"] = "bigger-model" });
        var rte = await As(f, Roles.RTE, "rte@x.com");
        await Enable(rte);
        var pre = await Json(await rte.GetAsync("/api/v1/ai/preflight?feature=release-notes-draft"));
        Assert.True(pre.GetProperty("allowed").GetBoolean());
        Assert.Equal("bigger-model", pre.GetProperty("model").GetString());
        var unknown = await Json(await rte.GetAsync("/api/v1/ai/preflight?feature=approve-the-train"));
        Assert.False(unknown.GetProperty("allowed").GetBoolean());
    }

    [Fact]
    public async Task Status_shows_module_kill_switches_models_and_whether_a_key_is_present_for_admins_only()
    {
        using var f = Factory(extra: new() { ["Ai:Provider"] = "anthropic", ["Ai:KillSwitch"] = "true", ["Ai:Models:Small"] = "tiny-one", ["Ai:Models:Drafting"] = "" });
        var viewer = await As(f, Roles.Viewer, "v@x.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v1/ai/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/v1/ai/workspace:pause", new { })).StatusCode);
        var rm = await As(f, Roles.ReleaseManager, "rm@x.com");
        var st = await Json(await rm.GetAsync("/api/v1/ai/status"));
        Assert.Equal("anthropic", st.GetProperty("provider").GetString());
        Assert.True(st.GetProperty("globalKillSwitch").GetBoolean());
        Assert.False(st.GetProperty("keyPresent").GetBoolean());
        Assert.True(st.GetProperty("keyRequired").GetBoolean());
        Assert.Equal("tiny-one", st.GetProperty("models").GetProperty("small").GetString());
        Assert.True(st.GetProperty("models").GetProperty("draftingFallsBackToSmall").GetBoolean());
        Assert.Equal(AiFeatures.All.Count, st.GetProperty("features").GetArrayLength());
        Assert.All(st.GetProperty("features").EnumerateArray(), x => Assert.Equal("tiny-one", x.GetProperty("model").GetString()));
    }

    [Fact]
    public async Task The_workspace_kill_switch_and_opt_out_are_confirmed_actions_that_block_calls_and_can_be_undone()
    {
        using var f = Factory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        await Enable(rte);
        var client = f.Services.GetRequiredService<IAiClient>();
        Assert.True((await client.CompleteAsync(new AiRequest("default", AiFeatures.RiskScore, "s", "go"))).IsCompleted);

        var paused = await Json(await rte.PostAsJsonAsync("/api/v1/ai/workspace:pause", new { }));
        Assert.True(paused.GetProperty("workspacePaused").GetBoolean());
        Assert.False(paused.GetProperty("canCall").GetBoolean());
        Assert.Equal(AiOutcome.WorkspacePaused, (await client.CompleteAsync(new AiRequest("default", AiFeatures.RiskScore, "s", "go"))).Outcome);
        Assert.Equal(HttpStatusCode.OK, (await rte.GetAsync("/api/v1/ai/preflight")).StatusCode);
        Assert.False((await Json(await rte.GetAsync("/api/v1/ai/preflight"))).GetProperty("allowed").GetBoolean());
        Assert.False((await Json(await rte.PostAsJsonAsync("/api/v1/ai/workspace:resume", new { }))).GetProperty("workspacePaused").GetBoolean());

        var out_ = await Json(await rte.PostAsJsonAsync("/api/v1/ai/workspace:opt-out", new { }));
        Assert.True(out_.GetProperty("workspaceOptedOut").GetBoolean());
        Assert.Equal(AiOutcome.OptedOut, (await client.CompleteAsync(new AiRequest("default", AiFeatures.RiskScore, "s", "go"))).Outcome);
        await rte.PostAsJsonAsync("/api/v1/ai/workspace:opt-in", new { });
        Assert.True((await client.CompleteAsync(new AiRequest("default", AiFeatures.RiskScore, "s", "go"))).IsCompleted);
    }

    [Fact]
    public async Task Turning_the_module_off_stops_calls_at_once()
    {
        using var f = Factory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var client = f.Services.GetRequiredService<IAiClient>();
        Assert.Equal(AiOutcome.ModuleOff, (await client.CompleteAsync(new AiRequest("default", AiFeatures.RiskScore, "s", "go"))).Outcome);
        await Enable(rte);
        Assert.True((await client.CompleteAsync(new AiRequest("default", AiFeatures.RiskScore, "s", "go"))).IsCompleted);
        using var off = new HttpRequestMessage(HttpMethod.Post, "/api/v1/modules/ai:disable") { Content = JsonContent.Create(new { }) };
        off.Headers.IfMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue("\"1\""));
        (await rte.SendAsync(off)).EnsureSuccessStatusCode();
        Assert.Equal(AiOutcome.ModuleOff, (await client.CompleteAsync(new AiRequest("default", AiFeatures.RiskScore, "s", "go"))).Outcome);
        Assert.Equal(HttpStatusCode.NotFound, (await rte.GetAsync("/api/v1/ai/preflight")).StatusCode);
    }

    [Fact]
    public async Task The_key_is_write_only_and_appears_in_no_response_log_config_or_database()
    {
        using var f = Factory(extra: new() { ["Ai:Provider"] = "anthropic" });
        var rte = await As(f, Roles.RTE, "rte@x.com");
        await Enable(rte);

        var put = await rte.PutAsJsonAsync("/api/v1/ai/key", new { key = Key });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await rte.PutAsJsonAsync("/api/v1/ai/key", new { key = " " })).StatusCode);

        var bodies = new List<string>();
        foreach (var url in new[] { "/api/v1/ai/status", "/api/v1/ai/preflight", "/api/v1/modules" }) bodies.Add(await (await rte.GetAsync(url)).Content.ReadAsStringAsync());
        bodies.Add(await (await rte.PostAsJsonAsync("/api/v1/ai/workspace:pause", new { })).Content.ReadAsStringAsync());
        bodies.Add(await (await rte.PostAsJsonAsync("/api/v1/ai/workspace:resume", new { })).Content.ReadAsStringAsync());
        Assert.True((await Json(await rte.GetAsync("/api/v1/ai/status"))).GetProperty("keyPresent").GetBoolean());
        Assert.All(bodies, b => Assert.DoesNotContain("API-TEST-KEY", b));

        // Not on disk in plain text, not in the database, not in configuration, not in any log line the host wrote.
        var keyDir = f.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<ReleaseMgmt.Infrastructure.Ai.AiOptions>>().CurrentValue.CredentialsDirectory;
        var files = Directory.GetFiles(keyDir);
        Assert.NotEmpty(files);
        Assert.All(files, p => Assert.DoesNotContain("API-TEST-KEY", File.ReadAllText(p)));
        var dbBytes = System.Text.Encoding.Latin1.GetString(await ReadShared(f.DbPath));
        Assert.DoesNotContain("API-TEST-KEY", dbBytes);
        var cfg = f.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
        Assert.DoesNotContain(cfg.AsEnumerable(), kv => kv.Value is not null && kv.Value.Contains("API-TEST-KEY"));
        var lines = Directory.GetFiles(Path.GetDirectoryName(f.DbPath)!, "log-*.txt").SelectMany(p => System.Text.Encoding.UTF8.GetString(ReadShared(p).Result).Split('\n')).ToArray();
        Assert.Contains(lines, l => l.Contains("provider key was set"));   // the capture works: the set was logged, without the key
        Assert.DoesNotContain(lines, l => l.Contains("API-TEST-KEY"));

        Assert.Equal(HttpStatusCode.NoContent, (await rte.DeleteAsync("/api/v1/ai/key")).StatusCode);
        Assert.False((await Json(await rte.GetAsync("/api/v1/ai/status"))).GetProperty("keyPresent").GetBoolean());
    }

    private static async Task<byte[]> ReadShared(string path)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        await fs.CopyToAsync(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task Without_a_key_the_real_provider_is_refused_before_any_network_use()
    {
        using var f = Factory(extra: new() { ["Ai:Provider"] = "anthropic" });
        var rte = await As(f, Roles.RTE, "rte@x.com");
        await Enable(rte);
        var r = await f.Services.GetRequiredService<IAiClient>().CompleteAsync(new AiRequest("default", AiFeatures.RiskScore, "s", "go"));
        Assert.Equal(AiOutcome.NoKey, r.Outcome);
        Assert.False(r.ProviderCalled);
    }

    [Fact]
    public void A_bad_provider_name_stops_the_start()
    {
        using var f = Factory(extra: new() { ["Ai:Provider"] = "carrier-pigeon" });
        Assert.ThrowsAny<Exception>(() => f.CreateClient());
    }
}
