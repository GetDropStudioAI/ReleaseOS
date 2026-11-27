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

/// <summary>The controls in memory for the governed-client unit tests (the database-backed ones are tested with the real services).</summary>
internal sealed class MemoryWorkspaces : IAiWorkspaceSettings
{
    private readonly Dictionary<string, AiWorkspaceState> _state = [];
    public Task<AiWorkspaceState> GetAsync(string workspaceId, CancellationToken ct = default) => Task.FromResult(_state.GetValueOrDefault(workspaceId, new AiWorkspaceState(false, false)));
    public void SetPaused(string workspaceId, bool paused) => _state[workspaceId] = _state.GetValueOrDefault(workspaceId, new AiWorkspaceState(false, false)) with { Paused = paused };
    public void SetOptedOut(string workspaceId, bool optedOut) => _state[workspaceId] = _state.GetValueOrDefault(workspaceId, new AiWorkspaceState(false, false)) with { OptedOut = optedOut };
}

/// <summary>An action log that keeps what it was given (and can be made to fail), for the governed-client unit tests.</summary>
internal sealed class RecordingActionLog : IAiActionLog
{
    public List<(AiRequest Request, AiResult Result, string Id)> Recorded { get; } = [];
    public bool Fail { get; set; }
    public Task<string> RecordAsync(AiRequest request, AiResult result, CancellationToken ct = default)
    {
        if (Fail) throw new InvalidOperationException("the audit trail is down");
        var id = "act-" + (Recorded.Count + 1);
        Recorded.Add((request, result, id));
        return Task.FromResult(id);
    }
    public Task<ServiceResult<AiActionView>> DecideAsync(string actionId, string decision, string? finalText, string? note, Actor actor, bool isAdmin, int? expectedVersion, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<AiActionView?> GetAsync(string actionId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<AiActionSummary>> ListAsync(AiActionFilter filter, CancellationToken ct = default) => throw new NotSupportedException();
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
    public MemoryWorkspaces Workspaces { get; } = new();
    public RecordingActionLog Actions { get; } = new();
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
        Client = new GovernedAiClient(provider ?? Provider, Keys, Options, Workspaces, Gate, Sink, Actions, Clock, Log);
    }

    public static AiRequest Req(string feature = AiFeatures.RiskScore, string instruction = "Assess the release", IReadOnlyList<AiContextItem>? context = null, bool cache = false, string ws = Ws) =>
        new(ws, feature, "You are an advisory assistant.", instruction, context, 512, cache);
}
