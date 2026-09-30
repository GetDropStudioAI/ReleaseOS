namespace ReleaseMgmt.Domain.Services;

/// <summary>Guard names for rollback attestation, PIR, PIR actions, known issues and hypercare exit (REOS-36, Q-036*).</summary>
public static class CloseoutGuards
{
    public const string CloseoutRole = "CloseoutRole";
    public const string InvalidRehearsalRun = "InvalidRehearsalRun";
    public const string PirAlreadyExists = "PirAlreadyExists";
    public const string PirRequiresComplete = "PirRequiresComplete";
    public const string PirClosed = "PirClosed";
    public const string IllegalPirTransition = "IllegalPirTransition";
    public const string PirSummaryRequired = "PirSummaryRequired";
    public const string PirActionsOpen = "PirActionsOpen";
    public const string InvalidPirAction = "InvalidPirAction";
    public const string PirActionDone = "PirActionDone";
    public const string PirActionNotDone = "PirActionNotDone";
    public const string PirActionRole = "PirActionRole";
    public const string InvalidKnownIssue = "InvalidKnownIssue";
    public const string IllegalKnownIssueTransition = "IllegalKnownIssueTransition";
    public const string KnownIssueResolved = "KnownIssueResolved";
    public const string KnownIssueAcceptRole = "KnownIssueAcceptRole";
    public const string WorkaroundRequired = "WorkaroundRequired";
    public const string HypercareEnded = "HypercareEnded";
    public const string HypercareRequiresComplete = "HypercareRequiresComplete";
    public const string OpenKnownIssues = "OpenKnownIssues";
}
