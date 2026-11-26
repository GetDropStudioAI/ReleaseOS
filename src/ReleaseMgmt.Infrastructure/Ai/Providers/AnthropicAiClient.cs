using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ReleaseMgmt.Domain.Ai;

namespace ReleaseMgmt.Infrastructure.Ai.Providers;

/// <summary>
/// REOS-183: the Anthropic provider adapter (Messages API and Message Batches API over HTTPS). It is the ONLY code that may know the provider's address, headers or wire format: an architecture test
/// fails if anything outside <c>Infrastructure/Ai/Providers</c> mentions them. The key comes from <see cref="IAiKeyStore"/> (Data Protection) at call time, is sent only in the <c>x-api-key</c> header,
/// and is never put in a log, an exception or a result. The HttpClient is built with no logging handler, so no header can be logged by the framework either.
/// Failures are classified: timeouts, 429, 5xx and network errors are transient (the governed client retries them, bounded); everything else is final.
/// </summary>
public sealed class AnthropicAiClient(HttpClient http, IAiKeyStore keys, IOptionsMonitor<AiOptions> options, TimeProvider time) : IAiProvider
{
    public string Name => "anthropic";
    public bool RequiresKey => true;

    private AnthropicOptions Cfg => options.CurrentValue.Anthropic;
    private Uri Url(string path) => new(new Uri(Cfg.BaseUrl.TrimEnd('/') + "/"), path.TrimStart('/'));

    private async Task<HttpRequestMessage> BuildAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        var key = await keys.GetKeyAsync(ct) ?? throw new AiProviderException("No provider key is stored", transient: false);
        var req = new HttpRequestMessage(method, Url(path)) { Content = content };
        req.Headers.Add("x-api-key", key);
        req.Headers.Add("anthropic-version", Cfg.ApiVersion);
        return req;
    }

    internal static JsonObject Body(AiProviderRequest r)
    {
        var system = new JsonObject { ["type"] = "text", ["text"] = r.System };
        if (r.CacheSystem) system["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
        return new JsonObject
        {
            ["model"] = r.Model,
            ["max_tokens"] = r.MaxOutputTokens,
            ["system"] = new JsonArray(system),
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = r.User }),
        };
    }

    internal static AiProviderResponse Parse(JsonElement message)
    {
        var text = new StringBuilder();
        if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            foreach (var block in content.EnumerateArray())
                if (block.TryGetProperty("type", out var t) && t.GetString() == "text" && block.TryGetProperty("text", out var tx)) text.Append(tx.GetString());
        long L(JsonElement u, string name) => u.ValueKind == JsonValueKind.Object && u.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
        message.TryGetProperty("usage", out var usage);
        return new AiProviderResponse(text.ToString(), L(usage, "input_tokens"), L(usage, "output_tokens"), L(usage, "cache_read_input_tokens"), L(usage, "cache_creation_input_tokens"));
    }

    private static AiProviderException Failure(HttpStatusCode status) =>
        new($"The provider answered {(int)status}", transient: status == HttpStatusCode.TooManyRequests || (int)status >= 500 || status == HttpStatusCode.RequestTimeout);

    public async Task<AiProviderResponse> CompleteAsync(AiProviderRequest request, CancellationToken ct)
    {
        using var req = await BuildAsync(HttpMethod.Post, "v1/messages", JsonContent.Create(Body(request)), ct);
        using var resp = await SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) throw Failure(resp.StatusCode);   // the body is not read into the message: it can echo the prompt
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return Parse(doc.RootElement);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        try { return await http.SendAsync(req, ct); }
        catch (HttpRequestException ex) { throw new AiProviderException("The provider could not be reached", transient: true, ex); }
    }

    public async Task<IReadOnlyList<AiProviderResponse?>> CompleteBatchAsync(IReadOnlyList<AiProviderRequest> requests, CancellationToken ct)
    {
        if (requests.Count == 0) return [];
        var items = new JsonArray();
        for (var i = 0; i < requests.Count; i++) items.Add(new JsonObject { ["custom_id"] = $"r{i}", ["params"] = Body(requests[i]) });
        string batchId;
        using (var req = await BuildAsync(HttpMethod.Post, "v1/messages/batches", JsonContent.Create(new JsonObject { ["requests"] = items }), ct))
        using (var resp = await SendAsync(req, ct))
        {
            if (!resp.IsSuccessStatusCode) throw Failure(resp.StatusCode);
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            batchId = doc.RootElement.GetProperty("id").GetString() ?? throw new AiProviderException("The provider returned no batch id", transient: false);
        }

        var o = options.CurrentValue;
        var deadline = time.GetUtcNow() + TimeSpan.FromMinutes(Math.Max(1, o.BatchMaxWaitMinutes));
        var poll = TimeSpan.FromSeconds(Math.Max(0, o.BatchPollSeconds));
        string? resultsUrl = null;
        while (true)
        {
            using var req = await BuildAsync(HttpMethod.Get, $"v1/messages/batches/{Uri.EscapeDataString(batchId)}", null, ct);
            using var resp = await SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) throw Failure(resp.StatusCode);
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (doc.RootElement.TryGetProperty("processing_status", out var st) && st.GetString() == "ended")
            {
                resultsUrl = doc.RootElement.TryGetProperty("results_url", out var ru) ? ru.GetString() : null;
                break;
            }
            if (time.GetUtcNow() >= deadline) throw new AiProviderException("The batch did not finish within the time allowed", transient: false);
            await Task.Delay(poll, time, ct);
        }
        if (resultsUrl is null) throw new AiProviderException("The batch ended without results", transient: false);

        var answers = new AiProviderResponse?[requests.Count];
        using (var req = await BuildAsync(HttpMethod.Get, new Uri(resultsUrl).PathAndQuery, null, ct))
        using (var resp = await SendAsync(req, ct))
        {
            if (!resp.IsSuccessStatusCode) throw Failure(resp.StatusCode);
            using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(ct));
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var row = JsonDocument.Parse(line);
                var id = row.RootElement.GetProperty("custom_id").GetString();
                if (id is null || !id.StartsWith('r') || !int.TryParse(id.AsSpan(1), out var idx) || idx < 0 || idx >= answers.Length) continue;
                var result = row.RootElement.GetProperty("result");
                if (result.GetProperty("type").GetString() == "succeeded") answers[idx] = Parse(result.GetProperty("message"));
            }
        }
        return answers;
    }
}
