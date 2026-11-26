using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ReleaseMgmt.Domain.Ai;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Ai;
using ReleaseMgmt.Infrastructure.Ai.Providers;

namespace ReleaseMgmt.Infrastructure.Tests.Ai;

internal sealed class TestMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; set; } = value;
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

internal sealed class StubGate(params string[] enabledWorkspaces) : IModuleGate
{
    public HashSet<string> Enabled { get; } = [.. enabledWorkspaces];
    public Task<bool> IsEnabledAsync(string workspaceId, string moduleKey, CancellationToken ct = default) => Task.FromResult(moduleKey == ModuleKeys.Ai && Enabled.Contains(workspaceId));
}

internal sealed class MemoryKeys(string? key = null) : IAiKeyStore
{
    public string? Key { get; private set; } = key;
    public Task<bool> HasKeyAsync(CancellationToken ct = default) => Task.FromResult(Key is not null);
    public Task<string?> GetKeyAsync(CancellationToken ct = default) => Task.FromResult(Key);
    public Task SetKeyAsync(string key, CancellationToken ct = default) { Key = key; return Task.CompletedTask; }
    public Task<bool> ClearKeyAsync(CancellationToken ct = default) { var had = Key is not null; Key = null; return Task.FromResult(had); }
}

internal sealed class RecordingSink : IAiUsageSink
{
    public List<AiResult> Results { get; } = [];
    public void Record(AiResult result) => Results.Add(result);
}

/// <summary>Collects every log line so a test can assert what was (not) written.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<string> Lines { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Lines.Add(formatter(state, exception) + (exception is null ? "" : " | " + exception));
}

/// <summary>Everything a governed-client test needs, built with the fake clock and the fake provider (no network, no key).</summary>
internal sealed class AiRig
{
    public const string Ws = "ws-1";
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 26, 9, 0, 0, TimeSpan.Zero));
    public FakeAiClient Provider { get; }
    public StubGate Gate { get; } = new(Ws);
    public MemoryKeys Keys { get; } = new();
    public InMemoryAiWorkspaceSettings Workspaces { get; } = new();
    public RecordingSink Sink { get; } = new();
    public CapturingLogger<GovernedAiClient> Log { get; } = new();
    public TestMonitor<AiOptions> Options { get; }
    public GovernedAiClient Client { get; }

    public AiRig(Action<AiOptions>? configure = null, IAiProvider? provider = null)
    {
        var o = new AiOptions { Provider = "fake", RetryBaseDelayMilliseconds = 0 };
        o.Models.Small = "small-model"; o.Models.Drafting = "drafting-model";
        configure?.Invoke(o);
        Options = new TestMonitor<AiOptions>(o);
        Provider = provider as FakeAiClient ?? new FakeAiClient();
        Client = new GovernedAiClient(provider ?? Provider, Keys, Options, Workspaces, Gate, Sink, Clock, Log);
    }

    public static AiRequest Req(string feature = AiFeatures.RiskScore, string instruction = "Assess the release", IReadOnlyList<AiContextItem>? context = null, bool cache = false, string ws = Ws) =>
        new(ws, feature, "You are an advisory assistant.", instruction, context, 512, cache);
}
