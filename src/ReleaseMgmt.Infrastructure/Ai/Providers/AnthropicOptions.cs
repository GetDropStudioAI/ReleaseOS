namespace ReleaseMgmt.Infrastructure.Ai.Providers;

/// <summary>The Anthropic adapter's wire settings (<c>Ai:Anthropic:*</c>). Kept with the adapter: nothing else may know the provider's address.</summary>
public sealed class AnthropicOptions
{
    public string BaseUrl { get; set; } = "https://api.anthropic.com";
    public string ApiVersion { get; set; } = "2023-06-01";
}
