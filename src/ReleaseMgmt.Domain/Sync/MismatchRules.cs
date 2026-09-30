namespace ReleaseMgmt.Domain.Sync;

/// <summary>One violated 5.3.7 rule. <see cref="Rule"/> is a stable code (part of the alert fingerprint); the message holds keys, states and dates only.</summary>
public sealed record MismatchFinding(string Rule, string Message);

/// <summary>What the mismatch rules need to know about one link, its train and what the source system reported.</summary>
public sealed record MismatchInput(
    string SourceSystem, string ExternalKey, ExternalStatus Status,
    string TrainStatus, DateOnly TrainTarget, DateTime? TrainEndedAt,
    DateTime? WindowStart, DateTime? WindowEnd, DateTime Now);

/// <summary>
/// PROJECT_SCOPE 5.3.7, mismatch rules v1. Warnings only (OI-9, <c>Governance:BlockOnChgMismatch=false</c>): nothing here is ever consulted by a
/// lifecycle guard. A missing date on either side is "no data", not a mismatch.
/// </summary>
public static class MismatchRules
{
    public const string ChgNotImplementing = "ChgNotImplementing", ChgNotClosed = "ChgNotClosed", FixVersionNotReleased = "FixVersionNotReleased",
        ChgWindowDiffers = "ChgWindowDiffers", FixVersionDateDiffers = "FixVersionDateDiffers";
    public static readonly string[] All = [ChgNotImplementing, ChgNotClosed, FixVersionNotReleased, ChgWindowDiffers, FixVersionDateDiffers];

    public static readonly TimeSpan Grace = TimeSpan.FromHours(24);

    public static bool IsChange(string key) => key.StartsWith("CHG", StringComparison.Ordinal);
    public static bool IsFixVersion(string key) => key.Contains('/');

    /// <summary>What this app's state implies for the linked item, or null when it implies nothing (stored in ExternalLinks.ExpectedStatus).</summary>
    public static string? ExpectedStatus(string sourceSystem, string key, string trainStatus) =>
        sourceSystem == "ServiceNow" && IsChange(key) ? trainStatus switch { "Executing" => "Implement", "Complete" => "Closed", _ => null }
        : sourceSystem == "Jira" && IsFixVersion(key) ? trainStatus == "Complete" ? "Released" : null
        : null;

    public static IReadOnlyList<MismatchFinding> Evaluate(MismatchInput i, TimeSpan? windowTolerance = null)
    {
        var f = new List<MismatchFinding>();
        var tol = windowTolerance ?? TimeSpan.Zero;
        var ended = i.TrainEndedAt is DateTime e && i.Now - e >= Grace;

        if (i.SourceSystem == "ServiceNow" && IsChange(i.ExternalKey))
        {
            var state = i.Status.State;
            if (i.TrainStatus == "Executing" && !Eq(state, "Implement"))
                f.Add(new(ChgNotImplementing, $"{i.ExternalKey} is {state}; the train is Executing, so the change should be in Implement"));
            if (i.TrainStatus == "Complete" && ended && !Eq(state, "Closed"))
                f.Add(new(ChgNotClosed, $"{i.ExternalKey} is {state} more than 24 h after the train completed; it should be Closed"));
            if (i.Status.PlannedStart is DateTime ps && i.Status.PlannedEnd is DateTime pe && i.WindowStart is DateTime ws && i.WindowEnd is DateTime we
                && (Diff(ps, ws) > tol || Diff(pe, we) > tol))
                f.Add(new(ChgWindowDiffers, $"{i.ExternalKey} plans {Iso(ps)} to {Iso(pe)}; the train's deployment window is {Iso(ws)} to {Iso(we)}"));
        }
        if (i.SourceSystem == "Jira" && IsFixVersion(i.ExternalKey))
        {
            if (i.TrainStatus == "Complete" && ended && i.Status.Released != true)
                f.Add(new(FixVersionNotReleased, $"Fix version {i.ExternalKey} is {i.Status.State} more than 24 h after the train completed; it should be Released"));
            if (i.Status.ReleaseDate is DateOnly rd && rd != i.TrainTarget)
                f.Add(new(FixVersionDateDiffers, $"Fix version {i.ExternalKey} releases on {rd:yyyy-MM-dd}; the train's target is {i.TrainTarget:yyyy-MM-dd}"));
        }
        return f;
    }

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static TimeSpan Diff(DateTime a, DateTime b) => (a - b).Duration();
    private static string Iso(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm'Z'");
}
