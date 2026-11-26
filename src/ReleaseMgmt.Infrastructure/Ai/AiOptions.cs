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
