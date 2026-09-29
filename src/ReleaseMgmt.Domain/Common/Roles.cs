namespace ReleaseMgmt.Domain.Common;

public static class Roles
{
    public const string Viewer = "Viewer";
    public const string ReleaseManager = "ReleaseManager";
    public const string RTE = "RTE";
    public const string GovernanceOfficer = "GovernanceOfficer";

    public static readonly string[] All = [Viewer, ReleaseManager, RTE, GovernanceOfficer];
}
