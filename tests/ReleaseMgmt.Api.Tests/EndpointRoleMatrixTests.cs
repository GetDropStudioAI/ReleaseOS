using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ReleaseMgmt.Domain.Common;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// REOS-53 / M8: the endpoint-to-role matrix of PROJECT_SCOPE section 1 as an automated test. Every endpoint the running app maps must be classified below
/// (a new endpoint fails <see cref="Every_mapped_endpoint_is_classified_and_nothing_stale_is_listed"/> until someone decides who may call it), and each
/// classification is then checked over HTTP for anonymous and each role: denied = 401 (anonymous) or 403 (signed in), allowed = anything else and never a 5xx.
/// Roles: Viewer, RTE, ReleaseManager (superset of RTE, D32), GovernanceOfficer. There is no separate Admin or Contributor role: "Admin" is the v1 policy
/// held by RTE and ReleaseManager (D14, D32), and the people who "contribute" are the owners covered by the Owned class.
/// </summary>
public class EndpointRoleMatrixTests
{
    // Class -> who is allowed past the HTTP authorization layer. Written from PROJECT_SCOPE section 1, not read from Policies.cs.
    private static readonly string[] All = [Roles.Viewer, Roles.RTE, Roles.ReleaseManager, Roles.GovernanceOfficer];
    private static readonly Dictionary<string, string[]> Allowed = new()
    {
        ["Anonymous"] = ["anonymous", .. All],
        ["Read"] = All,
        // Owned: any signed-in role passes HTTP authorization; the handler or service then decides by ownership (gate/task/step/condition/PIR-action owner, or RTE/RM).
        // The denial side is asserted with real rows in Owned_actions_refuse_people_who_are_neither_owner_nor_planner and in CloseoutTests / GovernanceTests.
        ["Owned"] = All,
        ["Plan"] = [Roles.RTE, Roles.ReleaseManager],
        ["Admin"] = [Roles.RTE, Roles.ReleaseManager],             // v1: RTE and RM (D14, D32)
        ["ReleaseManager"] = [Roles.ReleaseManager],               // Go/No-Go
        ["GovernanceOfficer"] = [Roles.GovernanceOfficer],         // waive, waiver request/approve
        ["AuditRead"] = [Roles.RTE, Roles.ReleaseManager, Roles.GovernanceOfficer],
        ["Uploaders"] = [Roles.RTE, Roles.ReleaseManager, Roles.GovernanceOfficer],
        ["Closeout"] = [Roles.RTE, Roles.ReleaseManager, Roles.GovernanceOfficer],
        ["FreezeApprove"] = [Roles.ReleaseManager, Roles.GovernanceOfficer],
        ["Approvers"] = [Roles.ReleaseManager, Roles.GovernanceOfficer],
        // Milestone done/undone (Q-0842): planners, or the owner; Viewers are refused even when they own it. Ownership is asserted in MilestoneTests.
        ["OwnedNotViewer"] = [Roles.RTE, Roles.ReleaseManager, Roles.GovernanceOfficer],
    };

    // Endpoints mapped only when the host environment is Development.
    private static readonly HashSet<string> DevOnly =
    [
        "POST /auth/dev-login", "POST /api/v1/dev/live-drill", "POST /api/v1/dev/sync/connector", "POST /api/v1/dev/sync/raise", "POST /api/v1/dev/sync/reset",
    ];

    private static readonly Dictionary<string, string> Matrix = Parse("""
        ANY /hub/trains                                          Read
        ANY /hub/trains/negotiate                                Read
        DELETE /api/v1/attachments/{id}                          Uploaders
        DELETE /api/v1/connectors/{source}/credentials           Admin
        DELETE /api/v1/export-jobs/{id}                          Read
        DELETE /api/v1/holidays/{day}                            Admin
        DELETE /api/v1/links/{id}                                Admin
        DELETE /api/v1/sync/webhook-allowlist/{id}               Admin
        DELETE /api/v1/trains/{id}/cis/{ciId}                    Plan
        GET {*path:nonfile}                                      Anonymous
        GET /api/v1/analytics                                    Read
        GET /api/v1/analytics/{metric}                           Read
        GET /api/v1/analytics/{metric}/xlsx                      Read
        GET /api/v1/attachments                                  Read
        GET /api/v1/attachments/{id}                             Read
        GET /api/v1/audit                                        AuditRead
        GET /api/v1/audit.csv                                    AuditRead
        GET /api/v1/audit/entity-types                           AuditRead
        GET /api/v1/calendar                                     Read
        GET /api/v1/comm-library                                 Read
        GET /api/v1/comm-library/{id}                            Read
        GET /api/v1/comm-library/tokens                          Read
        GET /api/v1/comm-templates/{id}                          Read
        GET /api/v1/comms/dispatches/{id}                        Read
        GET /api/v1/comms/dispatches/{id}/body                   Read
        GET /api/v1/config                                       Read
        GET /api/v1/connectors                                   Read
        GET /api/v1/export-jobs                                  Read
        GET /api/v1/export-jobs/{id}                             Read
        GET /api/v1/export-jobs/{id}/file                        Read
        GET /api/v1/export/{grid}.csv                            Read
        GET /api/v1/export/{grid}.xlsx                           Read
        GET /api/v1/exports/{grid}.csv                           Read
        GET /api/v1/exports/{grid}.xlsx                          Read
        GET /api/v1/exports/grids                                Read
        GET /api/v1/freeze-windows/ahead                         Read
        GET /api/v1/freezes                                      Read
        GET /api/v1/gates/{id}                                   Read
        GET /api/v1/gates/{id}/waivers                           Read
        GET /api/v1/holidays                                     Read
        GET /api/v1/ics/{token}.ics                              Anonymous
        GET /api/v1/ics/{token}/all.ics                          Anonymous
        GET /api/v1/ics/{token}/freezes.ics                      Anonymous
        GET /api/v1/ics/{token}/mine.ics                         Anonymous
        GET /api/v1/ics/{token}/trains/{trainId}.ics             Anonymous
        GET /api/v1/imports                                      Plan
        GET /api/v1/imports/{jobId}                              Plan
        GET /api/v1/imports/{jobId}/rows                         Plan
        GET /api/v1/imports/kinds                                Plan
        GET /api/v1/me                                           Read
        GET /api/v1/me/ics-tokens                                Read
        GET /api/v1/me/notifications                             Read
        GET /api/v1/me/notifications/count                       Read
        GET /api/v1/me/session/{clientId}                        Read
        GET /api/v1/me/work                                      Read
        GET /api/v1/owners                                       Read
        GET /api/v1/runs/{id}                                    Read
        GET /api/v1/runs/{id}/forecast                           Read
        GET /api/v1/sync/alerts                                  Read
        GET /api/v1/sync/health                                  Read
        GET /api/v1/sync/mismatches                              Read
        GET /api/v1/sync/state                                   Read
        GET /api/v1/sync/webhook-allowlist                       Read
        GET /api/v1/teams                                        Read
        GET /api/v1/templates                                    Read
        GET /api/v1/templates/{id}                               Read
        GET /api/v1/templates/library-options                    Read
        GET /api/v1/trains                                       Read
        GET /api/v1/trains/{id}                                  Read
        GET /api/v1/trains/{id}/attachments                      Read
        GET /api/v1/trains/{id}/attachments/manifest             Read
        GET /api/v1/trains/{id}/change-record                    Read
        GET /api/v1/trains/{id}/cis                              Read
        GET /api/v1/trains/{id}/comm-schedule                    Read
        GET /api/v1/trains/{id}/comms                            Read
        GET /api/v1/trains/{id}/comms/dispatch-context           Read
        GET /api/v1/trains/{id}/comms/dispatches                 Read
        GET /api/v1/trains/{id}/gonogo                           Read
        GET /api/v1/trains/{id}/known-issues                     Read
        GET /api/v1/trains/{id}/links                            Read
        GET /api/v1/trains/{id}/pir                              Read
        GET /api/v1/trains/{id}/products                         Read
        GET /api/v1/trains/{id}/readiness                        Read
        GET /api/v1/trains/{id}/rollback-attestation             Read
        GET /api/v1/trains/{id}/runs                             Read
        GET /api/v1/trains/{id}/steps                            Read
        GET /api/v1/trains/{id}/window                           Read
        GET /api/v1/users                                        Read
        GET /auth/config                                         Anonymous
        GET /healthz                                             Anonymous
        PATCH /api/v1/pir-actions/{id}                           Closeout
        PATCH /api/v1/steps/{id}                                 Plan
        PATCH /api/v1/teams/{id}                                 Admin
        PATCH /api/v1/trains/{id}                                Plan
        PATCH /api/v1/trains/{id}/known-issues/{issueId}         Closeout
        PATCH /api/v1/trains/{id}/pir                            Closeout
        PATCH /api/v1/users/{id}                                 Admin
        POST /api/v1/attachments                                 Uploaders
        POST /api/v1/comm-library                                Admin
        POST /api/v1/comm-schedule/{id}:mark-sent                Plan
        POST /api/v1/conditions/{id}:close                       Owned
        POST /api/v1/connectors/{source}:sync                    Admin
        POST /api/v1/connectors/{source}:test                    Admin
        POST /api/v1/dev/live-drill                              Plan
        POST /api/v1/dev/sync/connector                          Admin
        POST /api/v1/dev/sync/raise                              Admin
        POST /api/v1/dev/sync/reset                              Admin
        POST /api/v1/freezes                                     FreezeApprove
        POST /api/v1/freezes/{id}/override-requests              Plan
        POST /api/v1/freezes/{id}/overrides                      FreezeApprove
        POST /api/v1/gates/{id}:certify                          Owned
        POST /api/v1/gates/{id}:fail                             Owned
        POST /api/v1/gates/{id}:reopen                           Owned
        POST /api/v1/gates/{id}:start                            Owned
        POST /api/v1/gates/{id}:waive                            GovernanceOfficer
        POST /api/v1/gates/{id}/tasks                            Plan
        POST /api/v1/gates/{id}/waivers                          GovernanceOfficer
        POST /api/v1/gonogo/{id}/conditions                      ReleaseManager
        POST /api/v1/holidays                                    Admin
        POST /api/v1/imports/{jobId}:commit                      Plan
        POST /api/v1/imports/{kind}:preview                      Plan
        POST /api/v1/me/ics-tokens                               Read
        POST /api/v1/me/ics-tokens/{id}:revoke                   Read
        POST /api/v1/me/ics-tokens/{id}:rotate                   Read
        POST /api/v1/me/notifications:read-all                   Read
        POST /api/v1/notifications/{id}:read                     Read
        POST /api/v1/pir-actions/{id}:complete                   Owned
        POST /api/v1/pir-actions/{id}:reopen                     Owned
        POST /api/v1/runs/{id}:end                               Plan
        POST /api/v1/runs/{id}/steps/{stepId}:done               Owned
        POST /api/v1/runs/{id}/steps/{stepId}:fail               Owned
        POST /api/v1/runs/{id}/steps/{stepId}:skip               Owned
        POST /api/v1/runs/{id}/steps/{stepId}:start              Owned
        POST /api/v1/sync/alerts/{id}:resolve                    Admin
        POST /api/v1/sync/webhook-allowlist                      Admin
        POST /api/v1/tasks/{id}:complete                         Owned
        POST /api/v1/tasks/{id}:reopen                           Owned
        POST /api/v1/teams                                       Admin
        POST /api/v1/templates                                   Admin
        POST /api/v1/templates/{id}:approve                      Approvers
        POST /api/v1/templates/{id}:retire                       Approvers
        POST /api/v1/trains/{id}:abort                           Plan
        POST /api/v1/trains/{id}:advance                         Plan
        POST /api/v1/trains/{id}:capture-baseline                Plan
        POST /api/v1/trains/{id}:complete                        Plan
        POST /api/v1/trains/{id}:exit-hypercare                  Plan
        POST /api/v1/trains/{id}:rehearsed-rollback              Plan
        POST /api/v1/trains/{id}/cis                             Plan
        POST /api/v1/trains/{id}/comm-schedule:seed              Plan
        POST /api/v1/trains/{id}/comms                           Plan
        POST /api/v1/trains/{id}/comms:dispatch                  Plan
        POST /api/v1/trains/{id}/comms:preview                   Read
        POST /api/v1/trains/{id}/export-jobs                     Read
        POST /api/v1/trains/{id}/gonogo                          ReleaseManager
        POST /api/v1/trains/{id}/known-issues                    Closeout
        POST /api/v1/trains/{id}/known-issues/{issueId}:accept   Closeout
        POST /api/v1/trains/{id}/known-issues/{issueId}:reopen   Closeout
        POST /api/v1/trains/{id}/known-issues/{issueId}:resolve  Closeout
        POST /api/v1/trains/{id}/links                           Admin
        POST /api/v1/trains/{id}/pir                             Closeout
        POST /api/v1/trains/{id}/pir:close                       Closeout
        POST /api/v1/trains/{id}/pir:hold                        Closeout
        POST /api/v1/trains/{id}/pir:schedule                    Closeout
        POST /api/v1/trains/{id}/pir/actions                     Closeout
        POST /api/v1/trains/{id}/runs                            Plan
        POST /api/v1/trains/{id}/steps                           Plan
        POST /api/v1/trains/{id}/tasks:commit                    Plan
        POST /api/v1/trains/{id}/tasks:parse                     Plan
        POST /api/v1/waivers/{id}:approve                        GovernanceOfficer
        POST /auth/dev-login                                     Anonymous
        POST /auth/logout                                        Anonymous
        PUT /api/v1/comm-library/{id}                            Admin
        PUT /api/v1/comm-templates/{id}                          Plan
        PUT /api/v1/connectors/{source}                          Admin
        PUT /api/v1/connectors/{source}/credentials              Admin
        PUT /api/v1/me/session/{clientId}                        Read
        PUT /api/v1/steps/{id}/dependencies                      Plan
        PUT /api/v1/templates/{id}                               Admin
        PUT /api/v1/trains/{id}/change-record                    Plan
        PUT /api/v1/trains/{id}/window                           Plan
        GET /api/v1/trains/{id}/milestones                       Read
        POST /api/v1/trains/{id}/milestones                      Plan
        PATCH /api/v1/milestones/{id}                            Plan
        POST /api/v1/milestones/{id}:done                        OwnedNotViewer
        POST /api/v1/milestones/{id}:undone                      OwnedNotViewer
        DELETE /api/v1/milestones/{id}                           Plan
        """);

    private static Dictionary<string, string> Parse(string table) =>
        table.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
             .Select(l => { var i = l.LastIndexOf("  ", StringComparison.Ordinal); return (Key: l[..i].Trim(), Class: l[(i + 2)..].Trim()); })
             .ToDictionary(x => x.Key, x => x.Class);

    private static string KeyOf(RouteEndpoint e) => $"{e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.FirstOrDefault() ?? "ANY"} {e.RoutePattern.RawText}";

    private static List<RouteEndpoint> Endpoints(ApiFactory f)
    {
        f.CreateClient();   // start the host
        return [.. f.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()];
    }

    [Fact]
    public void Every_mapped_endpoint_is_classified_and_nothing_stale_is_listed()
    {
        using var f = new ApiFactory();
        var mapped = Endpoints(f).Select(KeyOf).ToHashSet();
        var unclassified = mapped.Except(Matrix.Keys).Order().ToList();
        var stale = Matrix.Keys.Except(mapped).Order().ToList();
        Assert.True(unclassified.Count == 0, "Endpoints with no entry in the role matrix (classify them in EndpointRoleMatrixTests.Matrix):\n  " + string.Join("\n  ", unclassified));
        Assert.True(stale.Count == 0, "Matrix entries that no longer match a mapped endpoint:\n  " + string.Join("\n  ", stale));
        var unknownClass = Matrix.Where(m => !Allowed.ContainsKey(m.Value)).Select(m => $"{m.Key} -> {m.Value}").ToList();
        Assert.True(unknownClass.Count == 0, "Unknown class:\n  " + string.Join("\n  ", unknownClass));
        Assert.True(mapped.Count >= 150, $"Only {mapped.Count} endpoints were found; the enumeration is broken");
    }

    [Fact]
    public void Outside_Development_the_dev_only_endpoints_do_not_exist()
    {
        using var f = new ApiFactory("Production");
        var mapped = Endpoints(f).Select(KeyOf).ToHashSet();
        Assert.Empty(mapped.Intersect(DevOnly));
        Assert.Equal(Matrix.Keys.Except(DevOnly).Order(), mapped.Order());
    }

    [Fact]
    public async Task Each_endpoint_allows_and_denies_exactly_the_roles_its_class_lists()
    {
        using var f = new ApiFactory();
        var endpoints = Endpoints(f);
        var clients = new Dictionary<string, HttpClient> { ["anonymous"] = f.CreateClient() };
        foreach (var r in All) clients[r] = await As(f, r, $"{r.ToLowerInvariant()}@matrix.test");

        var wrong = new List<string>();
        var checks = 0;
        foreach (var e in endpoints.OrderBy(x => KeyOf(x) == "POST /auth/logout").ThenBy(KeyOf, StringComparer.Ordinal))   // logout last: it ends the session of every client that calls it
        {
            var key = KeyOf(e);
            var cls = Matrix[key];
            foreach (var (who, client) in clients)
            {
                var allow = Allowed[cls].Contains(who);
                var (method, url) = Request(e);
                using var req = new HttpRequestMessage(new HttpMethod(method), url);
                if (method is "POST" or "PUT" or "PATCH" or "DELETE") req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                using var res = await client.SendAsync(req);
                var status = res.StatusCode;
                checks++;
                var denied = status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
                if (allow && (denied || (int)status >= 500)) wrong.Add($"{key} [{cls}] as {who}: expected allowed, got {(int)status} {(status >= HttpStatusCode.InternalServerError ? (await res.Content.ReadAsStringAsync())[..Math.Min(300, (await res.Content.ReadAsStringAsync()).Length)] : "")}");
                else if (!allow && who == "anonymous" && status != HttpStatusCode.Unauthorized) wrong.Add($"{key} [{cls}] as {who}: expected 401, got {(int)status}");
                else if (!allow && who != "anonymous" && status != HttpStatusCode.Forbidden) wrong.Add($"{key} [{cls}] as {who}: expected 403, got {(int)status}");
            }
        }
        Assert.True(wrong.Count == 0, $"{wrong.Count} of {checks} role checks failed:\n  " + string.Join("\n  ", wrong));
    }

    private static (string Method, string Url) Request(RouteEndpoint e)
    {
        var method = e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.FirstOrDefault() ?? "ANY";
        var raw = e.RoutePattern.RawText!;
        if (raw.StartsWith("/hub/trains")) return raw.EndsWith("/negotiate") ? ("POST", raw + "?negotiateVersion=1") : ("GET", raw);
        var url = Regex.Replace(raw, @"\{\*?(\w+)(:[^}]*)?\}", m => m.Groups[1].Value switch
        {
            "day" => "2026-01-01", "source" => "Jira", "metric" => "m1", "grid" => "gates", "kind" => "gates", "clientId" => "tab-12345678", "path" => "some/spa/route",
            _ => "x-1",
        });
        return (method, url);
    }

    [Fact]
    public async Task Owned_actions_refuse_people_who_are_neither_owner_nor_planner()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var rm = await As(f, Roles.ReleaseManager, "rm@x.com");
        var gov = await As(f, Roles.GovernanceOfficer, "gov@x.com");
        var viewer = await As(f, Roles.Viewer, "v@x.com");
        var owner = await As(f, Roles.Viewer, "owner@x.com");   // a Viewer who owns a task: owners act on what they own, whatever their role
        var (rteId, govId, ownerId) = (UserId(f, "rte@x.com"), UserId(f, "gov@x.com"), UserId(f, "owner@x.com"));
        SeedTrain(f, rteId, govId);
        Sql(f, $@"
            INSERT INTO ChecklistTasks(Id,StageGateId,TaskDescription,OwnerUserId,SequenceOrder) VALUES('k9','g1','Owned by a viewer','{ownerId}',2);
            INSERT INTO RunbookSteps(Id,ReleaseTrainId,StepCode,Section,Title,OwnerUserId,PlannedStartAt,PlannedDurationMin) VALUES('s1','t1','R-001','Deploy','Deploy','{rteId}','2026-10-30T06:10:00Z',30);");

        async Task<HttpStatusCode> Post(HttpClient c, string url) => (await c.PostAsync(url, new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode;

        // Tasks: a Viewer and a Governance Officer who own neither the task nor its gate are refused; the owner and the planners are not.
        foreach (var url in new[] { "/api/v1/tasks/k1:complete", "/api/v1/tasks/k1:reopen" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, await Post(viewer, url));
            Assert.Equal(HttpStatusCode.Forbidden, await Post(gov, url));
            Assert.Equal(HttpStatusCode.Forbidden, await Post(owner, url));   // owns k9, not k1
        }
        Assert.Equal(HttpStatusCode.Forbidden, await Post(viewer, "/api/v1/tasks/k9:complete"));
        Assert.Equal(HttpStatusCode.OK, await Post(owner, "/api/v1/tasks/k9:complete"));   // its own owner
        Assert.Equal(HttpStatusCode.OK, await Post(rm, "/api/v1/tasks/k1:complete"));      // planner
        Assert.Equal(HttpStatusCode.OK, await Post(rte, "/api/v1/tasks/k1:reopen"));

        // Standard gate g1 (owned by the RTE): Viewer, Governance Officer and a Viewer who owns a task are refused.
        foreach (var a in new[] { "start", "certify", "fail", "reopen" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, await Post(viewer, $"/api/v1/gates/g1:{a}"));
            Assert.Equal(HttpStatusCode.Forbidden, await Post(gov, $"/api/v1/gates/g1:{a}"));
            Assert.Equal(HttpStatusCode.Forbidden, await Post(owner, $"/api/v1/gates/g1:{a}"));
        }
        Assert.NotEqual(HttpStatusCode.Forbidden, await Post(rm, "/api/v1/gates/g1:start"));

        // Compliance gate g2: neither RTE nor RM certifies it (only a Governance Officer); the SoD trigger and service then apply to the officer.
        Assert.Equal(HttpStatusCode.Forbidden, await Post(rte, "/api/v1/gates/g2:certify"));
        Assert.Equal(HttpStatusCode.Forbidden, await Post(rm, "/api/v1/gates/g2:certify"));
        Assert.Equal(HttpStatusCode.Forbidden, await Post(viewer, "/api/v1/gates/g2:certify"));
        Assert.NotEqual(HttpStatusCode.Forbidden, await Post(gov, "/api/v1/gates/g2:certify"));

        // Run steps (s1 owned by the RTE): the step actions check ownership before the run is looked up.
        foreach (var a in new[] { "start", "done", "fail", "skip" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, await Post(viewer, $"/api/v1/runs/r1/steps/s1:{a}"));
            Assert.Equal(HttpStatusCode.Forbidden, await Post(gov, $"/api/v1/runs/r1/steps/s1:{a}"));
            Assert.NotEqual(HttpStatusCode.Forbidden, await Post(rte, $"/api/v1/runs/r1/steps/s1:{a}"));
            Assert.NotEqual(HttpStatusCode.Forbidden, await Post(rm, $"/api/v1/runs/r1/steps/s1:{a}"));
        }
    }

    [Fact]
    public async Task Audit_and_evidence_reads_that_the_route_table_calls_Read_are_still_role_checked_inline()
    {
        using var f = new ApiFactory();
        var viewer = await As(f, Roles.Viewer, "v@x.com");
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var gov = await As(f, Roles.GovernanceOfficer, "gov@x.com");
        // The audit grid export and the evidence pack sit on Read routes but need AuditRead (scope: "Read audit log, evidence packs").
        foreach (var ext in new[] { "csv", "xlsx" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync($"/api/v1/exports/audit.{ext}")).StatusCode);
            Assert.NotEqual(HttpStatusCode.Forbidden, (await rte.GetAsync($"/api/v1/exports/audit.{ext}")).StatusCode);
            Assert.NotEqual(HttpStatusCode.Forbidden, (await gov.GetAsync($"/api/v1/exports/audit.{ext}")).StatusCode);
        }
        StringContent Kind(string k) => new($"{{\"kind\":\"{k}\"}}", Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync("/api/v1/trains/nope/export-jobs", Kind("EvidencePack"))).StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, (await rte.PostAsync("/api/v1/trains/nope/export-jobs", Kind("EvidencePack"))).StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, (await gov.PostAsync("/api/v1/trains/nope/export-jobs", Kind("EvidencePack"))).StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, (await viewer.PostAsync("/api/v1/trains/nope/export-jobs", Kind("ReleaseReport"))).StatusCode);   // the scope leaves the report to every role
    }
}
