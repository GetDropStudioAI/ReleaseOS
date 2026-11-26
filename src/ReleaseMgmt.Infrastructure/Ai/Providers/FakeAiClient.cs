using ReleaseMgmt.Domain.Ai;

namespace ReleaseMgmt.Infrastructure.Ai.Providers;

/// <summary>
/// REOS-183: the deterministic provider adapter for tests and for development without a key. The same request always gives the same text and the same token counts
/// (a quarter of a character each, the usual rough rule), it needs no key and makes no network call. It records what it was sent so a test can assert what left the platform.
/// </summary>
public sealed class FakeAiClient : IAiProvider
{
    private readonly List<AiProviderRequest> _sent = [];
    private readonly object _lock = new();

    public string Name => "fake";
    public bool RequiresKey => false;

    /// <summary>Everything the adapter was asked to send, in order.</summary>
    public IReadOnlyList<AiProviderRequest> Sent { get { lock (_lock) return _sent.ToList(); } }
    public int CallCount { get { lock (_lock) return _sent.Count; } }

    /// <summary>A test sets this to make the next calls fail (e.g. transient twice, then succeed). Called with the 1-based call number.</summary>
    public Func<int, AiProviderException?>? FailWith { get; set; }

    public Task<AiProviderResponse> CompleteAsync(AiProviderRequest request, CancellationToken ct)
    {
        int n;
        lock (_lock) { _sent.Add(request); n = _sent.Count; }
        if (FailWith?.Invoke(n) is { } ex) throw ex;
        return Task.FromResult(Answer(request));
    }

    public Task<IReadOnlyList<AiProviderResponse?>> CompleteBatchAsync(IReadOnlyList<AiProviderRequest> requests, CancellationToken ct)
    {
        lock (_lock) _sent.AddRange(requests);
        return Task.FromResult<IReadOnlyList<AiProviderResponse?>>(requests.Select(r => (AiProviderResponse?)Answer(r)).ToList());
    }

    public static AiProviderResponse Answer(AiProviderRequest r)
    {
        var text = $"[fake:{r.Model}:{r.Feature}] {r.User.Length} characters received";
        var input = (r.System.Length + r.User.Length + 3) / 4;
        var cached = r.CacheSystem ? (r.System.Length + 3) / 4 : 0;
        return new AiProviderResponse(text, input - cached, (text.Length + 3) / 4, CacheReadTokens: 0, CacheWriteTokens: cached);
    }
}
