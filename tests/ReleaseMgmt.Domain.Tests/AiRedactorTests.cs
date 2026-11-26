using ReleaseMgmt.Domain.Ai;

namespace ReleaseMgmt.Domain.Tests;

/// <summary>REOS-183 (D12): token redaction before anything is sent to a model, and the feature routing table.</summary>
public class AiRedactorTests
{
    [Theory]
    [InlineData("key sk-ant-api03-abcdefghijklmnopqrstuvwx here", "sk-ant")]
    [InlineData("Authorization: Bearer abcdef1234567890xyz", "abcdef1234567890")]
    [InlineData("token ghp_abcdefghijklmnopqrstuvwxyz0123456789", "ghp_")]
    [InlineData("slack xoxb-1234567890-abcdefghij", "xoxb-")]
    [InlineData("aws AKIAABCDEFGHIJKLMNOP end", "AKIA")]
    [InlineData("jwt eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhb", "eyJhbGci")]
    [InlineData("password = hunter2hunter2", "hunter2")]
    [InlineData("write to jane.doe+x@example.co.uk", "jane.doe")]
    public void Secrets_and_addresses_are_masked_and_counted(string input, string leak)
    {
        var o = AiRedactor.Redact(input, out var n);
        Assert.DoesNotContain(leak, o);
        Assert.Contains(AiRedactor.Mask, o);
        Assert.True(n >= 1);
    }

    [Fact]
    public void Ordinary_release_text_is_left_alone()
    {
        const string text = "Release 2026.11 targets CERT on 2026-12-01; 3 of 4 gates passed; rollback budget 45 min.";
        Assert.Equal(text, AiRedactor.Redact(text, out var n));
        Assert.Equal(0, n);
        Assert.Equal("", AiRedactor.Redact(null, out _));
    }

    [Fact]
    public void Every_feature_has_a_unique_key_and_the_small_model_is_the_default_tier()
    {
        Assert.Equal(AiFeatures.All.Count, AiFeatures.All.Select(f => f.Key).Distinct().Count());
        Assert.Equal(AiModelTier.Small, AiFeatures.Find(AiFeatures.RiskScore)!.Tier);
        Assert.Equal(AiModelTier.Small, AiFeatures.Find(AiFeatures.Chat)!.Tier);   // chat is capped first and never uses the large model
        Assert.All(new[] { AiFeatures.ReleaseNotes, AiFeatures.CommsDraft, AiFeatures.RunbookDraft, AiFeatures.PirDraft }, k => Assert.Equal(AiModelTier.Drafting, AiFeatures.Find(k)!.Tier));
        Assert.Null(AiFeatures.Find("approve-the-train"));
    }
}
