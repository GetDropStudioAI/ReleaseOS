using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using ReleaseMgmt.Domain.Ai;
using ReleaseMgmt.Infrastructure.Ai;
using ReleaseMgmt.Infrastructure.Ai.Providers;

namespace ReleaseMgmt.Infrastructure.Tests.Ai;

/// <summary>REOS-183: the Anthropic adapter against a stub HTTP handler (no network, no real model), and the key store.</summary>
public class AnthropicAiClientTests
{
    private const string Key = "sk-ant-api03-THIS-IS-A-TEST-KEY-0123456789abcdef";

    private sealed class Stub(Func<HttpRequestMessage, string?, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Req, string? Body)> Seen { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Seen.Add((request, body));
            return answer(request, body);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (AnthropicAiClient Client, Stub Handler, MemoryKeys Keys) Make(Func<HttpRequestMessage, string?, HttpResponseMessage> answer, string? key = Key, Action<AiOptions>? cfg = null)
    {
        var handler = new Stub(answer);
        var o = new AiOptions { BatchPollSeconds = 0 };
        cfg?.Invoke(o);
        var keys = new MemoryKeys(key);
        return (new AnthropicAiClient(new HttpClient(handler), keys, new TestMonitor<AiOptions>(o), new Microsoft.Extensions.Time.Testing.FakeTimeProvider()), handler, keys);
    }

    private static AiProviderRequest Request(bool cache = false) => new("some-small-model", "risk-score", "system text", "user text", 256, cache);

    [Fact]
    public async Task A_call_sends_the_key_only_in_the_x_api_key_header_and_parses_text_and_all_four_token_counts()
    {
        var (c, h, _) = Make((_, _) => Json("""{"content":[{"type":"text","text":"Hello "},{"type":"text","text":"there"}],"usage":{"input_tokens":120,"output_tokens":30,"cache_read_input_tokens":100,"cache_creation_input_tokens":20}}"""));
        var r = await c.CompleteAsync(Request(cache: true), default);
        Assert.Equal("Hello there", r.Text);
        Assert.Equal((120, 30, 100, 20), (r.InputTokens, r.OutputTokens, r.CacheReadTokens, r.CacheWriteTokens));
        var (req, body) = Assert.Single(h.Seen);
        Assert.Equal("/v1/messages", req.RequestUri!.AbsolutePath);
        Assert.Equal(Key, req.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", req.Headers.GetValues("anthropic-version").Single());
        Assert.DoesNotContain(Key, body);   // never in the body
        Assert.DoesNotContain(Key, req.RequestUri.ToString());   // never in the URL
        using var doc = JsonDocument.Parse(body!);
        Assert.Equal("some-small-model", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal(256, doc.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal("ephemeral", doc.RootElement.GetProperty("system")[0].GetProperty("cache_control").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Without_the_cache_option_the_system_prompt_carries_no_cache_control()
    {
        var (c, h, _) = Make((_, _) => Json("""{"content":[{"type":"text","text":"x"}],"usage":{"input_tokens":1,"output_tokens":1}}"""));
        await c.CompleteAsync(Request(cache: false), default);
        Assert.DoesNotContain("cache_control", h.Seen[0].Body);
    }

    [Theory]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(529, true)]
    [InlineData(408, true)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(404, false)]
    public async Task Failures_are_classified_and_the_message_never_carries_the_key_or_the_body(int status, bool transient)
    {
        var (c, _, _) = Make((_, _) => Json("{\"error\":{\"message\":\"echo: system text " + Key + "\"}}", (HttpStatusCode)status));
        var ex = await Assert.ThrowsAsync<AiProviderException>(() => c.CompleteAsync(Request(), default));
        Assert.Equal(transient, ex.Transient);
        Assert.DoesNotContain(Key, ex.ToString());
        Assert.DoesNotContain("system text", ex.ToString());
    }

    [Fact]
    public async Task A_network_error_is_transient_and_without_a_stored_key_nothing_is_sent()
    {
        var (c, _, _) = Make((_, _) => throw new HttpRequestException("boom"));
        Assert.True((await Assert.ThrowsAsync<AiProviderException>(() => c.CompleteAsync(Request(), default))).Transient);

        var (c2, h2, _) = Make((_, _) => Json("{}"), key: null);
        var ex = await Assert.ThrowsAsync<AiProviderException>(() => c2.CompleteAsync(Request(), default));
        Assert.False(ex.Transient);
        Assert.Empty(h2.Seen);
    }

    [Fact]
    public async Task The_batch_path_submits_polls_until_ended_and_maps_results_by_custom_id_with_a_failed_item_left_null()
    {
        var polls = 0;
        var (c, h, _) = Make((req, _) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Post) return Json("""{"id":"msgbatch_1","processing_status":"in_progress"}""");
            if (path.EndsWith("/results")) return Json(
                "{\"custom_id\":\"r1\",\"result\":{\"type\":\"succeeded\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"B\"}],\"usage\":{\"input_tokens\":5,\"output_tokens\":2}}}}\n" +
                "{\"custom_id\":\"r0\",\"result\":{\"type\":\"succeeded\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"A\"}],\"usage\":{\"input_tokens\":4,\"output_tokens\":1}}}}\n" +
                "{\"custom_id\":\"r2\",\"result\":{\"type\":\"errored\"}}\n");
            return ++polls < 3 ? Json("""{"id":"msgbatch_1","processing_status":"in_progress"}""")
                : Json("""{"id":"msgbatch_1","processing_status":"ended","results_url":"https://api.anthropic.com/v1/messages/batches/msgbatch_1/results"}""");
        });
        var answers = await c.CompleteBatchAsync([Request(), Request(), Request()], default);
        Assert.Equal(["A", "B", null], answers.Select(a => a?.Text));
        Assert.Equal(3, polls);
        Assert.Equal("/v1/messages/batches", h.Seen[0].Req.RequestUri!.AbsolutePath);
        Assert.All(h.Seen, s => Assert.Equal(Key, s.Req.Headers.GetValues("x-api-key").Single()));
        using var doc = JsonDocument.Parse(h.Seen[0].Body!);
        Assert.Equal(3, doc.RootElement.GetProperty("requests").GetArrayLength());
    }

    [Fact]
    public async Task A_batch_that_does_not_finish_in_time_fails_with_a_final_error()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var handler = new Stub((req, _) => Json(req.Method == HttpMethod.Post ? """{"id":"b"}""" : """{"id":"b","processing_status":"in_progress"}"""));
        var o = new AiOptions { BatchPollSeconds = 30, BatchMaxWaitMinutes = 1 };
        var c = new AnthropicAiClient(new HttpClient(handler), new MemoryKeys(Key), new TestMonitor<AiOptions>(o), clock);
        var task = c.CompleteBatchAsync([Request()], default);
        for (var i = 0; i < 20 && !task.IsCompleted; i++) { await Task.Delay(20); clock.Advance(TimeSpan.FromSeconds(30)); }
        var ex = await Assert.ThrowsAsync<AiProviderException>(() => task);
        Assert.False(ex.Transient);
    }

    [Fact]
    public async Task Through_the_governed_client_the_key_never_reaches_a_log_a_result_or_an_error()
    {
        // The whole path: governed client -> Anthropic adapter -> stub. One call that works, one that fails with the key echoed back by the provider.
        var fail = false;
        var handler = new Stub((_, _) => fail ? Json("{\"error\":\"bad key " + Key + "\"}", HttpStatusCode.Unauthorized)
            : Json("""{"content":[{"type":"text","text":"ok"}],"usage":{"input_tokens":3,"output_tokens":1}}"""));
        var rig = new AiRig();
        var o = rig.Options.CurrentValue; o.Provider = "anthropic";
        await rig.Keys.SetKeyAsync(Key);
        var provider = new AnthropicAiClient(new HttpClient(handler), rig.Keys, rig.Options, rig.Clock);
        var client = new GovernedAiClient(provider, rig.Keys, rig.Options, rig.Workspaces, rig.Gate, rig.Sink, rig.Clock, rig.Log);
        var ok = await client.CompleteAsync(AiRig.Req(instruction: $"my key is {Key}"));
        fail = true;
        var bad = await client.CompleteAsync(AiRig.Req());
        Assert.True(ok.IsCompleted);
        Assert.Equal(AiOutcome.ProviderError, bad.Outcome);
        var everything = string.Join('\n', rig.Log.Lines) + JsonSerializer.Serialize(ok) + JsonSerializer.Serialize(bad) + string.Join('\n', handler.Seen.Select(s => s.Body));
        Assert.DoesNotContain(Key, everything);   // the prompt that held the key was redacted before sending, and nothing wrote it anywhere
        Assert.DoesNotContain("THIS-IS-A-TEST-KEY", everything);
    }

    [Fact]
    public async Task The_key_store_protects_the_key_on_disk_and_never_stores_it_in_plain_text()
    {
        var dir = Directory.CreateTempSubdirectory("reos-ai-").FullName;
        try
        {
            var opts = new TestMonitor<AiOptions>(new AiOptions { CredentialsDirectory = dir });
            var store = new DataProtectionAiKeyStore(new EphemeralDataProtectionProvider(), opts);
            Assert.False(await store.HasKeyAsync());
            await store.SetKeyAsync("  " + Key + "  ");
            Assert.True(await store.HasKeyAsync());
            Assert.Equal(Key, await store.GetKeyAsync());
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) Assert.DoesNotContain("THIS-IS-A-TEST-KEY", await File.ReadAllTextAsync(f));
            // A different key ring cannot read it: reads as absent, never throws, never leaks.
            var other = new DataProtectionAiKeyStore(new EphemeralDataProtectionProvider(), opts);
            Assert.False(await other.HasKeyAsync());
            Assert.True(await store.ClearKeyAsync());
            Assert.False(await store.HasKeyAsync());
            await Assert.ThrowsAsync<ArgumentException>(() => store.SetKeyAsync(" "));
        }
        finally { Directory.Delete(dir, true); }
    }
}
