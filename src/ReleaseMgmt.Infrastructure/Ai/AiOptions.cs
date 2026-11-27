using Microsoft.Extensions.Options;
using ReleaseMgmt.Domain.Ai;
using ReleaseMgmt.Infrastructure.Ai.Providers;

namespace ReleaseMgmt.Infrastructure.Ai;

/// <summary>
/// REOS-183: <c>Ai:*</c> configuration. Every key has a default that works untouched (D56). The provider KEY is not here and never may be (CLAUDE.md rule 11): it lives in
/// <see cref="DataProtectionAiKeyStore"/>. Model names are configuration, not code, so a model upgrade is a config change.
/// </summary>
public sealed class AiOptions
{
    public const string Section = "Ai";

    /// <summary><c>anthropic</c> (default) or <c>fake</c> (deterministic, for tests and development without a key).</summary>
    public string Provider { get; set; } = "anthropic";

    /// <summary>The global kill switch: while true, no AI call is made in any workspace. Read live, so a config change takes effect without a restart.</summary>
    public bool KillSwitch { get; set; }

    public AiModelNames Models { get; set; } = new();

    /// <summary>Per attempt.</summary>
    public int TimeoutSeconds { get; set; } = 30;
    /// <summary>Retries after the first attempt, for transient failures only. Clamped to <see cref="MaxRetriesCap"/>.</summary>
    public int MaxRetries { get; set; } = 2;
    public const int MaxRetriesCap = 5;
    public int RetryBaseDelayMilliseconds { get; set; } = 500;
    /// <summary>Upper bound on a request's output tokens, whatever the caller asks.</summary>
    public int MaxOutputTokens { get; set; } = 4096;
    /// <summary>Upper bound on the characters of one prompt after redaction; a bigger one is refused, not truncated.</summary>
    public int MaxInputChars { get; set; } = 60_000;

    /// <summary>The batch path polls this often and gives up waiting after <see cref="BatchMaxWaitMinutes"/>.</summary>
    public int BatchPollSeconds { get; set; } = 30;
    public int BatchMaxWaitMinutes { get; set; } = 60;

    public AnthropicOptions Anthropic { get; set; } = new();

    /// <summary>REOS-185 (D57, D58): the included monthly allowance, the caps and the per-person rate limit. Provisional defaults (Q-185a); the plans story replaces <see cref="ConfigAiAllowancePolicy"/>.</summary>
    public AiAllowanceOptions Allowance { get; set; } = new();
    /// <summary>REOS-185: model prices for the cost model. Empty by default: the published Haiku, Sonnet and Opus prices apply by model family.</summary>
    public AiPricingOptions Pricing { get; set; } = new();

    /// <summary>Folder of the Data Protection file that holds the key (outside wwwroot, never in git).</summary>
    public string CredentialsDirectory { get; set; } = "data/ai";

    public int EffectiveRetries => Math.Clamp(MaxRetries, 0, MaxRetriesCap);
    public TimeSpan Timeout => TimeSpan.FromSeconds(Math.Clamp(TimeoutSeconds, 1, 300));
}

public sealed class AiModelNames
{
    /// <summary>The default for every feature (Haiku-class). Configurable, not tied to a version.</summary>
    public string Small { get; set; } = "claude-haiku-5-5";
    /// <summary>Optional larger model for drafting features. Empty: drafting uses the small model.</summary>
    public string Drafting { get; set; } = "";
}

/// <summary>REOS-185: <c>Ai:Allowance:*</c>. A credit is one cent (10,000 micro-dollars). Haiku-class medium use is about 2 USD per workspace per month (pricing-and-infrastructure section 5), so 300 credits (3 USD) is a small included allowance.</summary>
public sealed class AiAllowanceOptions
{
    /// <summary>Included credits per workspace per UTC month.</summary>
    public int IncludedCredits { get; set; } = 300;
    /// <summary>At this share of the allowance chat stops and administrators are told. Everything else goes on until 100%.</summary>
    public int SoftCapPercent { get; set; } = 80;
    /// <summary>The most of the allowance chat may spend.</summary>
    public int ChatSharePercent { get; set; } = 50;
    /// <summary>Calls one person may start per minute and per hour. 0 = no limit. A scheduled or batch run (no person) is never rate-limited, only capped.</summary>
    public int UserPerMinute { get; set; } = 10;
    public int UserPerHour { get; set; } = 100;
}

public sealed class AiModelPriceOptions
{
    public decimal InputPerMTok { get; set; }
    public decimal OutputPerMTok { get; set; }
}

public sealed class AiPricingOptions
{
    /// <summary>Exact model name to price (USD per million tokens), overriding the family table.</summary>
    public Dictionary<string, AiModelPriceOptions> Models { get; set; } = [];
    public decimal CacheReadMultiplier { get; set; } = 0.05m;
    public decimal CacheWriteMultiplier { get; set; } = 1.25m;
    public decimal BatchMultiplier { get; set; } = 0.5m;

    public AiCostModel ToCostModel() => new(Models.ToDictionary(kv => kv.Key, kv => new AiModelPrice(kv.Value.InputPerMTok, kv.Value.OutputPerMTok)), CacheReadMultiplier, CacheWriteMultiplier, BatchMultiplier);
}

/// <summary>The default policy: configuration, read live. The plans story swaps this for a per-plan one.</summary>
public sealed class ConfigAiAllowancePolicy(IOptionsMonitor<AiOptions> options) : IAiAllowancePolicy
{
    public Task<AiAllowancePolicy> GetAsync(string workspaceId, CancellationToken ct = default)
    {
        var a = options.CurrentValue.Allowance;
        return Task.FromResult(new AiAllowancePolicy(AiCredits.FromCredits(Math.Max(0, a.IncludedCredits)), Math.Clamp(a.SoftCapPercent, 1, 100), Math.Clamp(a.ChatSharePercent, 0, 100), Math.Max(0, a.UserPerMinute), Math.Max(0, a.UserPerHour)));
    }
}
