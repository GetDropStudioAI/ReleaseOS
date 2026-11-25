using System.Text.RegularExpressions;
using ReleaseMgmt.Api.Endpoints;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Api.PublicApi;

/// <summary>A query parameter of an operation, as the OpenAPI document states it.</summary>
public sealed record PublicParam(string Name, string Type, string Description, bool Required = false);

/// <summary>
/// One operation of the public API (REOS-161). The catalog below is the whole public surface: the gateway answers 404 for anything else under <c>/api/public/v1</c>, and the OpenAPI document is
/// generated from these rows, so the document cannot name an operation that does not exist or leave one out (a contract test fails on drift).
/// <para><b>Forwarded</b> operations (<see cref="InternalPath"/> set) run the internal endpoint of that path with the token's person as the caller: the same handler, the same service, the same role policy,
/// the same <c>If-Match</c> and the same audit row. <b>Native</b> operations (no internal path) are mapped under the public prefix themselves.</para>
/// </summary>
public sealed record PublicOperation(string Method, string Path, string? InternalPath, string Scope, string OperationId, string Summary, string Tag,
    IReadOnlyList<PublicParam>? Query = null, Type? Body = null, bool Paged = false, int Success = 200, string? Note = null, bool IfMatch = false)
{
    private Regex? _pattern;
    public Regex Pattern => _pattern ??= new("^" + Regex.Replace(Regex.Escape(Path).Replace(@"\{", "{").Replace(@"\}", "}"), @"\{(\w+)\}", m => $"(?<{m.Groups[1].Value}>[^/:]+)") + "$", RegexOptions.CultureInvariant);
    public bool IsNative => InternalPath is null;
    public IReadOnlyList<string> PathParameters => [.. Regex.Matches(Path, @"\{(\w+)\}").Select(m => m.Groups[1].Value)];
}

public static class PublicApiCatalog
{
    public const string Prefix = "/api/public/v1";
    public const string Version = "1.0.0";

    private static PublicParam Q(string name, string type, string description) => new(name, type, description);
    private static readonly PublicParam[] Times = [Q("from", "string", "Start, a UTC instant (2026-10-01T10:00:00Z) or a day (2026-10-01)."), Q("to", "string", "End, a UTC instant or a day.")];

    /// <summary>Every operation. Order is the order of the document.</summary>
    public static readonly IReadOnlyList<PublicOperation> All =
    [
        // ---- read ------------------------------------------------------------------------------------------------------------------
        new("GET", "/me", "/me", ApiTokenScopes.Read, "getMe", "Who the token acts as: the person, their role in the workspace and the workspace", "Identity"),
        new("GET", "/releases", "/trains", ApiTokenScopes.Read, "listReleases", "List releases (those open, and those closed in the last 30 days)", "Releases", [Q("releaseTypeId", "string", "Only releases of this type.")], Paged: true),
        new("GET", "/releases/{id}", "/trains/{id}", ApiTokenScopes.Read, "getRelease", "One release with its gates, products and status", "Releases"),
        new("GET", "/releases/{id}/readiness", "/trains/{id}/readiness", ApiTokenScopes.Read, "getReleaseReadiness", "What stands between the release and a status", "Releases", [Q("target", "string", "The status to reach; default Executing.")]),
        new("GET", "/releases/{id}/deployments", "/trains/{id}/deployments", ApiTokenScopes.Read, "listReleaseDeployments", "Deployments of one release", "Deployments"),
        new("GET", "/releases/{id}/approval-requests", "/trains/{id}/approval-requests", ApiTokenScopes.Read, "listReleaseApprovalRequests", "Approval requests of one release", "Approvals"),
        new("GET", "/gates/{id}", "/gates/{id}", ApiTokenScopes.Read, "getGate", "One gate with its checklist, evidence and who may certify it", "Gates"),
        new("GET", "/approval-requests", "/approval-requests", ApiTokenScopes.Read, "listApprovalRequests", "List approval requests", "Approvals",
            [Q("trainId", "string", "Only requests for this release."), Q("environmentId", "string", "Only requests for this environment."), Q("status", "string", "Pending, ChangesRequested, Approved, Declined or Cancelled."),
             Q("mine", "boolean", "Only requests waiting for the person the token acts as.")], Paged: true),
        new("GET", "/approval-requests/{id}", "/approval-requests/{id}", ApiTokenScopes.Read, "getApprovalRequest", "One approval request with its seats and decisions", "Approvals"),
        new("GET", "/calendar", "/calendar", ApiTokenScopes.Read, "getCalendar", "Releases, gate dates, milestones, client dates and freeze windows between two dates", "Calendar",
            [.. Times, Q("releaseTypeId", "string", "Only releases of this type."), Q("segment", "string", "Client segment: Standard or OnDemand.")]),
        new("GET", "/deployments", "/deployments", ApiTokenScopes.Read, "listDeployments", "List deployments, newest first", "Deployments",
            [Q("environmentId", "string", "Only this environment."), Q("serviceId", "string", "Only this service."), Q("trainId", "string", "Only this release."), Q("state", "string", "Only this state.")], Paged: true),
        new("GET", "/deployments/{id}", "/deployments/{id}", ApiTokenScopes.Read, "getDeployment", "One deployment with its events", "Deployments", [Q("events", "integer", "How many events to return (newest first); default 200.")]),
        new("GET", "/analytics", "/analytics", ApiTokenScopes.Read, "listAnalyticsMetrics", "The analytics metrics (M1 to M24) and their columns", "Analytics"),
        new("GET", "/analytics/{metric}", "/analytics/{metric}", ApiTokenScopes.Read, "getAnalyticsMetric", "One metric's rows over a period", "Analytics",
            [.. Times, Q("releaseTypeId", "string", "Only releases of this type."), Q("workspaces", "string", "Workspaces to read across, comma separated; only those the token's person belongs to."), Q("groupBy", "string", "Grouping, for M16 to M24.")]),

        // ---- write: existing service actions on releases, gates and tasks ----------------------------------------------------------
        new("POST", "/releases/{id}:advance", "/trains/{id}:advance", ApiTokenScopes.Write, "advanceRelease", "Move a release to its next status (the service checks every rule and the database enforces them)", "Releases", Body: typeof(LifecycleEndpoints.AdvanceRequest), IfMatch: true),
        new("POST", "/releases/{id}:complete", "/trains/{id}:complete", ApiTokenScopes.Write, "completeRelease", "Complete a release with a close code", "Releases", Body: typeof(LifecycleEndpoints.CompleteRequest), IfMatch: true),
        new("POST", "/releases/{id}:abort", "/trains/{id}:abort", ApiTokenScopes.Write, "abortRelease", "Abort a release", "Releases", Body: typeof(LifecycleEndpoints.AbortRequest), IfMatch: true),
        new("POST", "/gates/{id}:start", "/gates/{id}:start", ApiTokenScopes.Write, "startGate", "Start a gate", "Gates", IfMatch: true),
        new("POST", "/gates/{id}:fail", "/gates/{id}:fail", ApiTokenScopes.Write, "failGate", "Mark a gate failed", "Gates", IfMatch: true),
        new("POST", "/gates/{id}:reopen", "/gates/{id}:reopen", ApiTokenScopes.Write, "reopenGate", "Reopen a gate", "Gates", IfMatch: true),
        new("POST", "/tasks/{id}:complete", "/tasks/{id}:complete", ApiTokenScopes.Write, "completeTask", "Complete a checklist task", "Gates", IfMatch: true),
        new("POST", "/tasks/{id}:reopen", "/tasks/{id}:reopen", ApiTokenScopes.Write, "reopenTask", "Reopen a checklist task", "Gates", IfMatch: true),

        // ---- approvals: certification, waivers and approval requests ---------------------------------------------------------------
        new("POST", "/gates/{id}:certify", "/gates/{id}:certify", ApiTokenScopes.Approvals, "certifyGate", "Certify a gate (the person the token acts as must be eligible, and not the one who did the work)", "Gates", IfMatch: true),
        new("POST", "/gates/{id}:waive", "/gates/{id}:waive", ApiTokenScopes.Approvals, "waiveGate", "Waive a gate on an approved waiver", "Gates", IfMatch: true),
        new("POST", "/gates/{id}/waivers", "/gates/{id}/waivers", ApiTokenScopes.Approvals, "requestWaiver", "Ask for a waiver of a gate", "Approvals", Body: typeof(LifecycleEndpoints.WaiverRequestBody)),
        new("POST", "/waivers/{id}:approve", "/waivers/{id}:approve", ApiTokenScopes.Approvals, "approveWaiver", "Approve a waiver", "Approvals", IfMatch: true),
        new("POST", "/waivers/{id}:decline", "/waivers/{id}:decline", ApiTokenScopes.Approvals, "declineWaiver", "Decline a waiver", "Approvals", Body: typeof(ApprovalEndpoints.DeclineBody), IfMatch: true),
        new("POST", "/approval-requests", "/approval-requests", ApiTokenScopes.Approvals, "createApprovalRequest", "Ask for an approval to take a release to an environment", "Approvals", Body: typeof(NewApproval), Success: 201),
        new("POST", "/approval-requests/{id}:decide", "/approval-requests/{id}:decide", ApiTokenScopes.Approvals, "decideApprovalRequest", "Approve, decline or ask for changes on a seat of an approval request", "Approvals", Body: typeof(DecideRequest), IfMatch: true),
        new("POST", "/approval-requests/{id}:resubmit", "/approval-requests/{id}:resubmit", ApiTokenScopes.Approvals, "resubmitApprovalRequest", "Resubmit an approval request after changes", "Approvals", IfMatch: true),
        new("POST", "/approval-requests/{id}:cancel", "/approval-requests/{id}:cancel", ApiTokenScopes.Approvals, "cancelApprovalRequest", "Cancel an approval request", "Approvals", IfMatch: true),

        // ---- deployments: record what a deployment tool did, and control it -----------------------------------------------------------
        new("POST", "/deployments", null, ApiTokenScopes.Deployments, "recordDeployment", "Record a deployment, or its new state, by the tool's own id (a replay of the same id changes nothing)", "Deployments", Body: typeof(IngestDeployment), Success: 200,
            Note: "201 when the deployment is new, 200 when it already existed."),
        new("POST", "/deployments/events", null, ApiTokenScopes.Deployments, "recordDeploymentEvents", "Append events to a deployment (an event with an externalId is recorded once)", "Deployments", Body: typeof(DeploymentsNative.EventsBody)),
        new("POST", "/deployments/{id}:pause", "/deployments/{id}:pause", ApiTokenScopes.Deployments, "pauseDeployment", "Ask the environment's deployment tool to pause a deployment", "Deployments", Body: typeof(ControlRequest), IfMatch: true),
        new("POST", "/deployments/{id}:resume", "/deployments/{id}:resume", ApiTokenScopes.Deployments, "resumeDeployment", "Ask the deployment tool to resume a deployment", "Deployments", Body: typeof(ControlRequest), IfMatch: true),
        new("POST", "/deployments/{id}:promote", "/deployments/{id}:promote", ApiTokenScopes.Deployments, "promoteDeployment", "Ask the deployment tool to promote a canary", "Deployments", Body: typeof(ControlRequest), IfMatch: true),
        new("POST", "/deployments/{id}:rollback", "/deployments/{id}:rollback", ApiTokenScopes.Deployments, "rollbackDeployment", "Ask the deployment tool to roll a deployment back", "Deployments", Body: typeof(ControlRequest), IfMatch: true),
        new("POST", "/deployments/{id}:arm-rollback", "/deployments/{id}:arm-rollback", ApiTokenScopes.Deployments, "armDeploymentRollback", "Arm automatic rollback on a confirmed threshold breach", "Deployments", IfMatch: true),
        new("POST", "/deployments/{id}:disarm-rollback", "/deployments/{id}:disarm-rollback", ApiTokenScopes.Deployments, "disarmDeploymentRollback", "Disarm automatic rollback", "Deployments", IfMatch: true),
    ];

    public sealed record Match(PublicOperation Operation, IReadOnlyDictionary<string, string> Values);

    /// <summary>The operation for a method and a path below the prefix, or the methods the path does have (for a 405), or neither (404).</summary>
    public static (Match? Found, IReadOnlyList<string> OtherMethods) Find(string method, string rest)
    {
        List<string> others = [];
        foreach (var op in All)
        {
            var m = op.Pattern.Match(rest);
            if (!m.Success) continue;
            if (string.Equals(op.Method, method, StringComparison.OrdinalIgnoreCase))
                return (new Match(op, op.PathParameters.ToDictionary(p => p, p => Uri.UnescapeDataString(m.Groups[p].Value))), []);
            others.Add(op.Method);
        }
        return (null, others);
    }

    /// <summary>The internal path of a forwarded operation with its parameters filled in (each segment escaped).</summary>
    public static string InternalPathOf(Match m) =>
        "/api/v1" + Regex.Replace(m.Operation.InternalPath!, @"\{(\w+)\}", x => Uri.EscapeDataString(m.Values[x.Groups[1].Value]));
}
