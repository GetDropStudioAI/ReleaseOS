namespace ReleaseMgmt.Domain.Services;

/// <summary>
/// REOS-175 and REOS-177 (D56, D63): the CAB module's vocabulary and its pure rules. The services call these first so a person gets a readable 422; the triggers in
/// <c>db/schema.sql</c> (block "REOS-175 + REOS-177") repeat the same rules as the backstop, and use the same message text.
/// </summary>
public static class ChangeTypes
{
    public const string Standard = "Standard", Normal = "Normal", Emergency = "Emergency";
    public static readonly string[] All = [Standard, Normal, Emergency];
}

public static class ChangeStatuses
{
    public const string Draft = "Draft", Submitted = "Submitted", Approved = "Approved", Rejected = "Rejected", Implemented = "Implemented", Closed = "Closed", Cancelled = "Cancelled";
    public static readonly string[] All = [Draft, Submitted, Approved, Rejected, Implemented, Closed, Cancelled];
}

public static class ChangeRiskLevels
{
    public const string Low = "Low", Medium = "Medium", High = "High", Critical = "Critical";
    public static readonly string[] All = [Low, Medium, High, Critical];
}

public static class CabMeetingStatuses
{
    public const string Planned = "Planned", InProgress = "InProgress", Completed = "Completed", Cancelled = "Cancelled";
}

public static class CabDecisionKinds
{
    public const string Approved = "Approved", Rejected = "Rejected", Deferred = "Deferred";
    public static readonly string[] All = [Approved, Rejected, Deferred];
    /// <summary>A verdict moves a change; Deferred only records that the CAB looked and wants more.</summary>
    public static bool IsVerdict(string decision) => decision is Approved or Rejected;
}

public static class CabAttendeeRoles
{
    public const string Member = "Member", Guest = "Guest";
    public static readonly string[] All = [Member, Guest];
}

public static class CabAttendance
{
    public const string Invited = "Invited", Attended = "Attended", Absent = "Absent";
    public static readonly string[] All = [Invited, Attended, Absent];
}

/// <summary>Service guard names of the CAB module (422 <c>{guard, message}</c>).</summary>
public static class CabGuards
{
    public const string ChangeInvalid = "ChangeInvalid";
    public const string IllegalChangeTransition = "IllegalChangeTransition";
    public const string ChangeNotEditable = "ChangeNotEditable";
    public const string ChangeNeedsRollbackNote = "ChangeNeedsRollbackNote";
    public const string ChangeNeedsWindow = "ChangeNeedsWindow";
    public const string ChangeNotAwaitingDecision = "ChangeNotAwaitingDecision";
    public const string OnlyEmergencyBeforeApproval = "OnlyEmergencyBeforeApproval";
    /// <summary>REOS-177 rule 1: a requester cannot approve or reject their own change.</summary>
    public const string ChangeSelfApproval = "ChangeSelfApproval";
    /// <summary>REOS-177 rule 2: the CAB chair cannot be the only approver of a change they raised.</summary>
    public const string ChangeChairSoleApprover = "ChangeChairSoleApprover";
    /// <summary>REOS-177 rule 3: an Emergency change needs a second person's review before it is closed.</summary>
    public const string ChangeEmergencyNeedsReview = "ChangeEmergencyNeedsReview";
    public const string CabMeetingInvalid = "CabMeetingInvalid";
    public const string IllegalMeetingTransition = "IllegalMeetingTransition";
    public const string CabMeetingClosed = "CabMeetingClosed";
    public const string CabChangeNotSubmitted = "CabChangeNotSubmitted";
    public const string CabDuplicate = "CabDuplicate";
    public const string CabNotMember = "CabNotMember";
}

/// <summary>The exact texts of the three separation-of-duties rules. The trigger raises the same words (a test compares them).</summary>
public static class CabRuleText
{
    public const string SelfApproval = "Separation of duties: you cannot approve or reject a change you requested";
    public const string ChairSoleApprover = "Separation of duties: the CAB chair cannot be the only approver of a change they raised; a second approver is needed";
    public const string EmergencyNeedsReview = "Separation of duties: an Emergency change needs a review by a second person before it can be closed";
}

public static class CabRules
{
    /// <summary>Draft &gt; Submitted &gt; Approved | Rejected | Cancelled; Approved &gt; Implemented | Cancelled; Implemented &gt; Closed; Draft &gt; Cancelled; Submitted &gt; Implemented (Emergency only, checked apart). Rejected, Closed and Cancelled are final.</summary>
    public static bool IsLegalChangeMove(string from, string to) => (from, to) switch
    {
        (ChangeStatuses.Draft, ChangeStatuses.Submitted or ChangeStatuses.Cancelled) => true,
        (ChangeStatuses.Submitted, ChangeStatuses.Approved or ChangeStatuses.Rejected or ChangeStatuses.Cancelled or ChangeStatuses.Implemented) => true,
        (ChangeStatuses.Approved, ChangeStatuses.Implemented or ChangeStatuses.Cancelled) => true,
        (ChangeStatuses.Implemented, ChangeStatuses.Closed) => true,
        _ => false,
    };

    public static bool IsLegalMeetingMove(string from, string to) => (from, to) switch
    {
        (CabMeetingStatuses.Planned, CabMeetingStatuses.InProgress or CabMeetingStatuses.Cancelled) => true,
        (CabMeetingStatuses.InProgress, CabMeetingStatuses.Completed or CabMeetingStatuses.Cancelled) => true,
        _ => false,
    };

    /// <summary>Rule 1: the person who requested a change cannot approve or reject it (deferring is not a verdict).</summary>
    public static bool IsSelfApproval(string requestedBy, string decidedBy, string decision) => CabDecisionKinds.IsVerdict(decision) && requestedBy == decidedBy;

    /// <summary>
    /// Rule 2: <paramref name="approver"/> chairs the meeting that has the change on its agenda (or recorded the decision) and raised the change (requested it, or keyed it in), and no one
    /// else who did not raise it has approved. <paramref name="otherApprovers"/> are the people with an Approved decision on the change, not counting <paramref name="approver"/>.
    /// </summary>
    public static bool ChairIsSoleApprover(string approver, bool approverChairs, string requestedBy, string createdBy, IEnumerable<string> otherApprovers) =>
        approverChairs && (approver == requestedBy || approver == createdBy) && !otherApprovers.Any(p => p != requestedBy && p != createdBy);
}
