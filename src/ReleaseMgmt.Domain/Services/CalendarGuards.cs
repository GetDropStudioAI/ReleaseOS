namespace ReleaseMgmt.Domain.Services;

/// <summary>Guard names for the calendar and ICS feed tokens (REOS-51, Q-051*). Kept out of <see cref="Guards"/> so the shared file is not edited.</summary>
public static class CalendarGuards
{
    public const string InvalidFilter = "InvalidFilter";
    public const string InvalidIcsScope = "InvalidIcsScope";
    public const string IcsTokenExists = "IcsTokenExists";
    public const string IcsTokenRevoked = "IcsTokenRevoked";
}
