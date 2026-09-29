namespace ReleaseMgmt.Domain.Services;

/// <summary>Legal status moves (PROJECT_SCOPE §3, D26). The triggers are the backstop; these give readable 422s first.</summary>
public static class Transitions
{
    public static bool TrainLegal(string from, string to) => (from, to) switch
    {
        ("Planning", "Gated" or "Aborted") => true,
        ("Gated", "Planning" or "Executing" or "Aborted") => true,
        ("Executing", "Complete" or "Aborted") => true,
        _ => false,
    };

    public static bool GateLegal(string from, string to) => (from, to) switch
    {
        ("Pending", "InProgress") => true,
        ("InProgress", "Certified" or "Failed" or "Waived") => true,
        ("Failed", "InProgress") => true,
        ("Certified", "InProgress") => true,
        _ => false,
    };

    /// <summary>Order of the checkpoints a gate can be required before (Gated &lt; Executing &lt; Complete).</summary>
    public static int Rank(string status) => status switch { "Gated" => 1, "Executing" => 2, _ => 3 };

    public static bool IsResolved(string gateStatus) => gateStatus is "Certified" or "Waived";
}

public static class Guards
{
    public const string IllegalTransition = "IllegalTransition";
    public const string IllegalGateTransition = "IllegalGateTransition";
    public const string GateLockout = "GateLockout";
    public const string ExecutingRequiresGo = "ExecutingRequiresGo";
    public const string ConditionExpired = "ConditionExpired";
    public const string ExecutingRequiresBaseline = "ExecutingRequiresBaseline";
    public const string RollbackNotRehearsed = "RollbackNotRehearsed";
    public const string CloseCodeRequired = "CloseCodeRequired";
    public const string GateOpenTasks = "GateOpenTasks";
    public const string ComplianceNeedsTask = "ComplianceNeedsTask";
    public const string EarlierGateOpen = "EarlierGateOpen";
    public const string ComplianceCertifierRole = "ComplianceCertifierRole";
    public const string SegregationOfDuties = "SegregationOfDuties";
    public const string WaiverRequired = "WaiverRequired";
    public const string WaiverReason = "WaiverReason";
    public const string WaiverApproverRole = "WaiverApproverRole";
    public const string WaiverSelfApproval = "WaiverSelfApproval";
    public const string WaiverAlreadyDecided = "WaiverAlreadyDecided";
    public const string BaselineExists = "BaselineExists";
    public const string DbRule = "DbRule";
    public const string InvalidWindow = "InvalidWindow";
    public const string InvalidSessionState = "InvalidSessionState";
    public const string SessionStateTooLarge = "SessionStateTooLarge";
}
