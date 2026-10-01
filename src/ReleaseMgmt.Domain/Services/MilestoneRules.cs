namespace ReleaseMgmt.Domain.Services;

/// <summary>Guard names for train milestones (Q-0840..). Kept out of <see cref="Guards"/> so the shared file is not edited.</summary>
public static class MilestoneGuards
{
    public const string InvalidMilestone = "InvalidMilestone";
}

/// <summary>
/// Train milestones: named key dates on a train ("Code complete", "UAT sign-off"). Informational only: a milestone is never certified and never blocks a gate or a
/// train transition (decision of 2026-10-01). The limits mirror the CHECKs on TrainMilestones in db/schema.sql so the service answers 422 before the database would.
/// </summary>
public static class MilestoneRules
{
    public const int MaxName = 200;
    public const int MaxNote = 2000;

    /// <summary>Null when the values are acceptable; otherwise the readable reason (422 InvalidMilestone).</summary>
    public static GuardFailure? Validate(string? name, string? note, string? ownerUserId, string? ownerTeamId)
    {
        var n = name?.Trim() ?? "";
        if (n.Length == 0) return new(MilestoneGuards.InvalidMilestone, "A milestone needs a name");
        if (n.Length > MaxName) return new(MilestoneGuards.InvalidMilestone, $"A milestone name is at most {MaxName} characters (this one has {n.Length})");
        if (note is not null && note.Length > MaxNote) return new(MilestoneGuards.InvalidMilestone, $"A milestone note is at most {MaxNote} characters (this one has {note.Length})");
        if (ownerUserId is not null && ownerTeamId is not null) return new(MilestoneGuards.InvalidMilestone, "A milestone is owned by a person or a team, not both");
        return null;
    }

    /// <summary>Blank notes are stored as no note.</summary>
    public static string? NormaliseNote(string? note) => string.IsNullOrWhiteSpace(note) ? null : note.Trim();
}
