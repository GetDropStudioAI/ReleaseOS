using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using ReleaseMgmt.Api.Endpoints;
using ReleaseMgmt.Api.Sync;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Services;
using ReleaseMgmt.Infrastructure.Sync;

namespace ReleaseMgmt.Api;

/// <summary>
/// REOS-66 (security review SEC-D7, Q-SEC-D1): the maximum length of every string field of every JSON body the API accepts, in one table, enforced by one
/// endpoint filter on the <c>/api/v1</c> group (<see cref="Filter"/>). An over-long value is 422 <c>{guard:"FieldTooLong", field, max, message}</c> before the
/// handler runs, so nothing is written. <c>field</c> is the JSON path (<c>conditions[0].text</c>, <c>memberIds[2]</c>); lengths are in UTF-16 characters, as the
/// services' own checks count them.
///
/// The schema has no length CHECKs except the UI-state JSON (256 KB, kept by <c>SessionService</c>), so each value comes from a limit the product already has:
/// the CSV import's column limits (the other way in for the same rows), the checklist parser, the comm library, the connector and webhook checks
/// (Q-SEC-D1 lists them). A new body type or string field must be added here: <c>FieldLimitTests</c> fails for any JSON string field without a limit.
/// </summary>
public static class FieldLimits
{
    public const string Guard = "FieldTooLong";

    /// <summary>Ids: UUIDv7 text is 36 characters, trigger-made ids 32 hex; room for either.</summary>
    public const int Id = 64;
    /// <summary>Enum-like values (status, kind, scope, section, severity, format...): the longest allowed value is under 20.</summary>
    public const int Code = 40;
    /// <summary>A handle with its optional <c>@</c> (the import's handle rule: 1 to 40 of <c>A-Za-z0-9._-</c>).</summary>
    public const int Handle = 41;
    /// <summary>Names and titles: import <c>Trains.Title</c>, <c>Products.ProductName</c>, <c>Gates.GateName</c>, <c>Teams.Name</c>, <c>Holidays.Name</c> (200).</summary>
    public const int Name = 200;
    /// <summary>A runbook step's title: import <c>RunbookSteps.Title</c> (300).</summary>
    public const int StepTitle = 300;
    /// <summary>A step code: import pattern, up to 20 characters.</summary>
    public const int StepCode = 20;
    /// <summary>One line of text: a task (import <c>Tasks.Description</c> and the checklist parser, 2,000), and the reasons, notes, conditions and PIR actions written like one.</summary>
    public const int Text = ChecklistParser.MaxDescription;
    /// <summary>Long-form text: import <c>RunbookSteps.Instructions</c> and a comm template body (20,000).</summary>
    public const int LongText = CommLibraryService.MaxBody;
    /// <summary>An ITSM key: import <c>ExternalLinks.Key</c> (100).</summary>
    public const int ExternalKey = 100;
    /// <summary>Product version tag and project code: import <c>Products.VersionTag</c> and <c>Products.ProjectCode</c> (100).</summary>
    public const int Tag = 100;
    /// <summary>A URL: the connector and webhook checks (2,048).</summary>
    public const int Url = ConnectorUrlPolicy.MaxUrlLength;
    /// <summary>A pasted checklist: at most 500 lines of 2,500 characters plus line ends (the parser's own whole-paste limit).</summary>
    public const int Paste = ChecklistParser.MaxLines * (ChecklistParser.MaxLineLength + 2);

    private static readonly Dictionary<Type, Dictionary<string, int>> Table = new()
    {
        // Admin (REOS-36)
        [typeof(AdminEndpoints.PatchUser)] = new() { ["Handle"] = Handle },
        [typeof(AdminEndpoints.TeamBody)] = new() { ["Handle"] = Handle, ["Name"] = Name, ["MemberIds"] = Id },
        [typeof(AdminEndpoints.TeamPatch)] = new() { ["Name"] = Name, ["MemberIds"] = Id },
        [typeof(AdminEndpoints.HolidayBody)] = new() { ["Name"] = Name },
        // Calendar feed tokens
        [typeof(CalendarEndpoints.IcsCreateBody)] = new() { ["Scope"] = Code, ["TrainId"] = Id },
        // Close-out: PIR, actions, known issues
        [typeof(CloseoutEndpoints.SummaryBody)] = new() { ["Summary"] = LongText },
        [typeof(CloseoutEndpoints.PirActionBody)] = new() { ["Text"] = Text, ["OwnerUserId"] = Id },
        [typeof(CloseoutEndpoints.PirActionPatchBody)] = new() { ["Text"] = Text, ["OwnerUserId"] = Id },
        [typeof(CloseoutEndpoints.KnownIssueBody)] = new() { ["Title"] = Name, ["Severity"] = Code, ["Workaround"] = LongText, ["ExternalKey"] = ExternalKey },
        [typeof(CloseoutEndpoints.KnownIssuePatchBody)] = new() { ["Title"] = Name, ["Severity"] = Code, ["Workaround"] = LongText, ["ExternalKey"] = ExternalKey },
        // Communications (REOS-43..45)
        [typeof(CommDispatchEndpoints.DispatchBody)] = new() { ["TemplateId"] = Id, ["ScheduleItemId"] = Id, ["Channel"] = Code, ["Format"] = Code, ["WebhookDestinationId"] = Id, ["WebhookUrl"] = WebhookUrlPolicy.MaxUrlLength },
        [typeof(CommsEndpoints.PreviewBody)] = new() { ["TemplateId"] = Id, ["LibraryTemplateId"] = Id, ["Subject"] = CommLibraryService.MaxSubject, ["Text"] = CommLibraryService.MaxBody, ["Target"] = Code },
        [typeof(LibraryTemplateInput)] = new() { ["Name"] = Name, ["TemplateType"] = CommLibraryService.MaxType, ["Audience"] = Code, ["SubjectLine"] = CommLibraryService.MaxSubject, ["MarkdownBody"] = CommLibraryService.MaxBody },
        [typeof(TrainCommTemplateInput)] = new() { ["Audience"] = Code, ["SubjectLine"] = CommLibraryService.MaxSubject, ["MarkdownBody"] = CommLibraryService.MaxBody },
        [typeof(CopyToTrainInput)] = new() { ["LibraryTemplateId"] = Id },
        [typeof(SeedScheduleInput)] = new() { ["TemplateId"] = Id },
        // Connectors and sync (REOS-39..42)
        [typeof(ConnectorEndpoints.SettingsBody)] = new() { ["BaseUrl"] = Url },
        [typeof(ConnectorEndpoints.CredentialsBody)] = new() { ["Kind"] = Code, ["Username"] = 256, ["Secret"] = 2048 },   // ConnectorService's own limits
        [typeof(ConnectorEndpoints.LinkBody)] = new() { ["EntityType"] = Code, ["EntityId"] = Id, ["SourceSystem"] = Code, ["ExternalKey"] = ExternalKey },
        [typeof(SyncEndpoints.WebhookBody)] = new() { ["Name"] = 80, ["Url"] = WebhookUrlPolicy.MaxUrlLength, ["Kind"] = Code },   // SyncHealthService: a channel name is 1 to 80
        [typeof(SyncDevEndpoints.RaiseBody)] = new() { ["Source"] = Code, ["Kind"] = Code, ["Key"] = Name, ["Message"] = 500, ["TrainId"] = Id },   // SyncAlertWriter keeps 500
        [typeof(SyncDevEndpoints.ConnectorBody)] = new() { ["Source"] = Code, ["BaseUrl"] = Url },
        // Freezes (REOS-33)
        [typeof(FreezeEndpoints.FreezeBody)] = new() { ["Name"] = Name, ["Kind"] = Code, ["ProductPattern"] = Name },   // a glob over product names (200)
        [typeof(FreezeEndpoints.OverrideBody)] = new() { ["TrainId"] = Id, ["RequestedByUserId"] = Id, ["Reason"] = Text },
        [typeof(FreezeEndpoints.OverrideRequestBody)] = new() { ["TrainId"] = Id, ["Reason"] = Text },
        // Governance: change record, CIs, Go/No-Go
        [typeof(GovernanceEndpoints.ChangeRecordBody)] = new()
        {
            ["Justification"] = LongText, ["ImplementationPlan"] = LongText, ["RiskImpactAnalysis"] = LongText, ["BackoutPlan"] = LongText, ["TestPlan"] = LongText, ["CommunicationPlan"] = LongText,
        },
        [typeof(GovernanceEndpoints.CiBody)] = new() { ["CiName"] = Name, ["CiExternalId"] = ExternalKey },
        [typeof(GovernanceEndpoints.ConditionBody)] = new() { ["Text"] = Text, ["OwnerUserId"] = Id },
        [typeof(GovernanceEndpoints.GoNoGoBody)] = new() { ["Decision"] = Code, ["Notes"] = Text },   // Conditions: ConditionBody above
        // Lifecycle, gates, waivers, tasks
        [typeof(LifecycleEndpoints.AdvanceRequest)] = new() { ["To"] = Code },
        [typeof(LifecycleEndpoints.CompleteRequest)] = new() { ["CloseCode"] = Code, ["Notes"] = Text },
        [typeof(LifecycleEndpoints.RehearsedRequest)] = new() { ["RunId"] = Id, ["Note"] = Text },
        [typeof(LifecycleEndpoints.WaiverRequestBody)] = new() { ["Reason"] = Text },
        [typeof(LifecycleEndpoints.AddTaskRequest)] = new() { ["Description"] = Text, ["OwnerUserId"] = Id, ["OwnerTeamId"] = Id },
        // Checklist paste (REOS-31)
        [typeof(ParserEndpoints.ParseBody)] = new() { ["Text"] = Paste, ["DefaultGateId"] = Id },
        [typeof(ParserEndpoints.CommitBody)] = new() { ["PreviewId"] = Id },
        // Exports (REOS-50)
        [typeof(CreateExportRequest)] = new() { ["Kind"] = Code, ["Format"] = Code },
        // Runs and runbook
        [typeof(RunEndpoints.StartRunBody)] = new() { ["Mode"] = Code },
        [typeof(RunEndpoints.StepActionBody)] = new() { ["Note"] = Text },
        [typeof(RunEndpoints.EndRunBody)] = new() { ["Outcome"] = Code },
        [typeof(RunbookEndpoints.CreateStepBody)] = new()
        {
            ["StepCode"] = StepCode, ["Section"] = Code, ["Title"] = StepTitle, ["Instructions"] = LongText, ["OwnerUserId"] = Id, ["OwnerTeamId"] = Id, ["BundledProductId"] = Id,
        },
        [typeof(RunbookEndpoints.PatchStepBody)] = new()
        {
            ["StepCode"] = StepCode, ["Section"] = Code, ["Title"] = StepTitle, ["Instructions"] = LongText, ["OwnerUserId"] = Id, ["OwnerTeamId"] = Id, ["BundledProductId"] = Id,
        },
        [typeof(RunbookEndpoints.DependenciesBody)] = new() { ["DependsOn"] = Id },
        // UI session state (the Ui JSON itself is held to 256 KB by the schema CHECK and SessionService)
        [typeof(SessionEndpoints.PutSessionBody)] = new() { ["ActiveTrainId"] = Id },
        // Train templates (REOS-38)
        [typeof(TemplateInput)] = new() { ["Name"] = Name, ["DefaultRiskTier"] = Code },   // Gates, Steps, Schedule: their rows below
        [typeof(TemplateGateInput)] = new() { ["GateName"] = Name, ["GateClass"] = Code, ["RequiredBeforeStatus"] = Code, ["OwnerTeamId"] = Id },
        // REOS-84: train milestones (the service's own limits, which the schema's CHECKs also hold)
        [typeof(MilestoneEndpoints.AddMilestoneBody)] = new() { ["Name"] = MilestoneRules.MaxName, ["OwnerUserId"] = Id, ["OwnerTeamId"] = Id, ["Note"] = MilestoneRules.MaxNote },
        [typeof(MilestoneEndpoints.PatchMilestoneBody)] = new() { ["Name"] = MilestoneRules.MaxName, ["OwnerUserId"] = Id, ["OwnerTeamId"] = Id, ["Note"] = MilestoneRules.MaxNote },
        // REOS-83: add a user ahead of first sign-in (email as Users.Email, import limit)
        [typeof(UserCreationEndpoints.NewUser)] = new() { ["Email"] = UserCreationService.MaxEmailLength, ["DisplayName"] = UserCreationService.MaxNameLength, ["Role"] = Code, ["Handle"] = Handle },
        // REOS-81: a train's products and gates
        [typeof(PlanStructureEndpoints.ProductBody)] = new() { ["Name"] = Name, ["VersionTag"] = Tag, ["ProjectCode"] = Tag },
        [typeof(PlanStructureEndpoints.NewGateBody)] = new() { ["GateName"] = Name, ["GateClass"] = Code, ["RequiredBeforeStatus"] = Code, ["OwnerUserId"] = Id, ["OwnerTeamId"] = Id },
        [typeof(PlanStructureEndpoints.PatchGateBody)] = new() { ["GateName"] = Name, ["GateClass"] = Code, ["RequiredBeforeStatus"] = Code, ["OwnerUserId"] = Id, ["OwnerTeamId"] = Id, ["Status"] = Code },
        // REOS-80: new train (blank, from template, copy). Dates and timestamps are ISO strings, well under Code.
        [typeof(NewTrainInput)] = new() { ["Title"] = Name, ["TargetReleaseDate"] = Code, ["RiskTier"] = Code, ["TemplateId"] = Id, ["WindowStartsAt"] = Code, ["WindowEndsAt"] = Code },
        [typeof(NewProductInput)] = new() { ["ProductName"] = Name, ["VersionTag"] = Tag, ["ProjectCode"] = Tag },
        [typeof(CloneTrainInput)] = new() { ["Title"] = Name, ["TargetReleaseDate"] = Code, ["RiskTier"] = Code },
        [typeof(TemplateStepInput)] = new() { ["StepCode"] = StepCode, ["Section"] = Code, ["Title"] = StepTitle, ["OwnerTeamId"] = Id },
        [typeof(TemplateScheduleInput)] = new() { ["LibraryTemplateId"] = Id },
    };

    /// <summary>Every body type with a row in the table (for the completeness test and the documentation table).</summary>
    public static IReadOnlyCollection<Type> Types => Table.Keys;

    /// <summary>The limit of one property of a body type, or null when it has none.</summary>
    public static int? Max(Type type, string property) => Table.TryGetValue(type, out var t) && t.TryGetValue(property, out var m) ? m : null;

    public static bool Covers(Type type) => Table.ContainsKey(type);

    public sealed record TooLong(string Field, int Max, int Length);

    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> Props = new();

    /// <summary>The first string field of <paramref name="body"/> (walked in declaration order, into nested bodies and lists) that is longer than its limit.</summary>
    public static TooLong? FirstTooLong(object? body, string path = "")
    {
        if (body is null || !Table.TryGetValue(body.GetType(), out var limits)) return null;
        foreach (var p in Props.GetOrAdd(body.GetType(), t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(x => x.CanRead && x.GetIndexParameters().Length == 0).ToArray()))
        {
            var value = p.GetValue(body);
            if (value is null) continue;
            var field = path + JsonNamingPolicy.CamelCase.ConvertName(p.Name);
            limits.TryGetValue(p.Name, out var max);
            switch (value)
            {
                case string s:
                    if (max > 0 && s.Length > max) return new(field, max, s.Length);
                    break;
                case IEnumerable<string> items:
                    var i = 0;
                    foreach (var s in items)
                    {
                        if (max > 0 && s is not null && s.Length > max) return new($"{field}[{i}]", max, s.Length);
                        i++;
                    }
                    break;
                case IEnumerable list when value is not JsonElement:
                    var j = 0;
                    foreach (var item in list)
                    {
                        if (FirstTooLong(item, $"{field}[{j}].") is { } bad) return bad;
                        j++;
                    }
                    break;
                default:
                    if (FirstTooLong(value, field + ".") is { } nested) return nested;
                    break;
            }
        }
        return null;
    }

    public static IResult Refuse(TooLong t) => Results.Json(new
    {
        guard = Guard,
        field = t.Field,
        max = t.Max,
        message = $"{t.Field} is {t.Length:N0} characters long; at most {t.Max:N0} are allowed",
    }, statusCode: StatusCodes.Status422UnprocessableEntity);

    /// <summary>
    /// The endpoint filter (added to the <c>/api/v1</c> group in Program.cs). At build time it finds the handler's parameters whose type has a row in the table
    /// (the bound JSON body); an endpoint without one gets no filter at all.
    /// </summary>
    public static EndpointFilterDelegate Filter(EndpointFilterFactoryContext ctx, EndpointFilterDelegate next)
    {
        var bodies = ctx.MethodInfo.GetParameters().Select((p, i) => (p, i)).Where(x => Covers(x.p.ParameterType)).Select(x => x.i).ToArray();
        if (bodies.Length == 0) return next;
        return async inv =>
        {
            foreach (var i in bodies)
                if (FirstTooLong(inv.Arguments[i]) is { } bad) return Refuse(bad);
            return await next(inv);
        };
    }
}
