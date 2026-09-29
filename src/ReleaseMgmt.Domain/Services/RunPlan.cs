namespace ReleaseMgmt.Domain.Services;

/// <summary>Run timing rules that need no database.</summary>
public static class RunPlan
{
    /// <summary>
    /// D28: a Rehearsal shifts every planned time by (run start - earliest planned start of the non-Rollback steps), so lateness is
    /// measured from when the rehearsal actually began. A Live run uses absolute planned times (shift zero). No steps outside Rollback: zero.
    /// </summary>
    public static TimeSpan RehearsalShift(DateTime runStartedAt, IEnumerable<(string Section, DateTime PlannedStartAt)> steps)
    {
        DateTime? earliest = null;
        foreach (var (section, start) in steps)
            if (section != "Rollback" && (earliest is null || start < earliest)) earliest = start;
        return earliest is null ? TimeSpan.Zero : runStartedAt - earliest.Value;
    }
}
