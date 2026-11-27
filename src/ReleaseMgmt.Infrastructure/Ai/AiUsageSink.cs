using ReleaseMgmt.Domain.Ai;

namespace ReleaseMgmt.Infrastructure.Ai;

/// <summary>The metering story's seam (REOS-183): every result goes here. The audit trail is <see cref="IAiActionLog"/>; this one is for cost.</summary>
public sealed class NoopAiUsageSink : IAiUsageSink
{
    public void Record(AiResult result) { }
}
