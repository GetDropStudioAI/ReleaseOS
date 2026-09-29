namespace ReleaseMgmt.Domain.Common;

public static class Ids
{
    /// <summary>UUIDv7 as lowercase hyphenated TEXT (CLAUDE.md rule 6).</summary>
    public static string New() => Guid.CreateVersion7().ToString();
}
