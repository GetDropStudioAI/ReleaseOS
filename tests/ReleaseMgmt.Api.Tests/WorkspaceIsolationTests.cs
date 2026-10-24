using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Infrastructure.Reminders;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// REOS-105 (D45) acceptance: a member of workspace B cannot read, change or be notified about anything in workspace A, through any endpoint. Every
/// endpoint the app maps is called (enumerated from the route table, as the role matrix is) by a member of B in each of the four roles, with the ids of
/// A's rows in the route: none succeeds, none fails with a 5xx, and no answer carries A's data. Endpoints that take no id of A's (lists, grids, metrics,
/// the inbox) may answer, but nothing of A's is in them. Afterwards A's rows are exactly as they were. The live channel and the inbox tell B's members
/// nothing about A, and A's calendar link shows A only.
/// </summary>
public class WorkspaceIsolationTests
{
    public const string B = "w-beta";
    private const string Marker = "ZQALPHA";   // in every text of A's rows: no answer to a member of B may contain it

    /// <summary>Workspace B, and a member of it in <paramref name="role"/> who is in no other workspace.</summary>
    internal static async Task<HttpClient> InB(ApiFactory f, string role, string email, string? defaultRole = null)
    {
        var c = await As(f, role, email);
        var id = UserId(f, email);
        Sql(f, $@"INSERT OR IGNORE INTO Workspaces(Id,Name,Slug,CreatedAt) VALUES('{B}','Beta','beta','2026-10-01T00:00:00Z');
                 INSERT INTO WorkspaceMembers(WorkspaceId,UserId,Role,AddedAt) VALUES('{B}','{id}','{role}','2026-10-01T00:00:00Z');");
        if (defaultRole is null) Sql(f, $"DELETE FROM WorkspaceMembers WHERE WorkspaceId='default' AND UserId='{id}'");
        else Sql(f, $"UPDATE WorkspaceMembers SET Role='{defaultRole}' WHERE WorkspaceId='default' AND UserId='{id}'");
        return c;
    }

    /// <summary>Workspace A (the default one): a train with a row in every table an id in a route can name, all carrying <see cref="Marker"/>.</summary>
    internal static Dictionary<string, string> SeedAlpha(ApiFactory f, string rteId, string govId)
    {
        const string t = "2026-10-01T00:00:00Z";
        Sql(f, $@"
        INSERT INTO Users(Id,Email,DisplayName,Role) VALUES('ua','zqalpha-owner@x.com','{Marker} Owner','RTE');
        INSERT INTO WorkspaceMembers(WorkspaceId,UserId,Role,AddedAt) VALUES('default','ua','RTE','{t}');
        INSERT INTO Teams(Id,Handle,Name) VALUES('tma','zqalpha-team','{Marker} Team');
        INSERT INTO TeamMembers(TeamId,UserId) VALUES('tma','ua');
        INSERT INTO Holidays(Day,Name) VALUES('2026-12-24','{Marker} Eve');
        INSERT INTO TrainTemplates(Id,Name) VALUES('tpa','{Marker} Template');
        INSERT INTO CommTemplateLibrary(Id,TemplateType,Name,Audience,SubjectLine,MarkdownBody) VALUES('la','Tminus28','{Marker} Library','All','{Marker} subject','{Marker} body');
        INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CreatedAt,UpdatedAt) VALUES('ta','{Marker} Train','2026-11-30','High','{t}','{t}');
        INSERT INTO ReleaseTypes(Id,Name,SortOrder) VALUES('rta','{Marker} Type',10);
        INSERT INTO ReleaseVersions(Id,Name,CreatedAt) VALUES('vera','{Marker} Version','{t}');
        INSERT INTO Environments(Id,Name,Region,Kind,SortOrder,CreatedAt) VALUES('enva','{Marker} Env','{Marker}-region','Production',10,'{t}');
        INSERT INTO CatalogServices(Id,ServiceKey,Name,OwnerTeamId,Repository,CreatedAt) VALUES('svca','zqalpha-svc','{Marker} Service','tma','https://git.example/{Marker}','{t}');
        INSERT INTO IngestKeys(Id,Name,KeyPrefix,KeySha256,CreatedAt,LastUsedAt) VALUES('ika','{Marker} Key','rk_zqalp',printf('%064d',3),'{t}','2999-01-01T00:00:00Z');
        UPDATE ReleaseTrains SET OwnerUserId='ua', OwnerTeamId='tma', ReleaseBranch='{Marker}-branch', ReleaseTypeId='rta', ReleaseVersionId='vera' WHERE Id='ta';
        INSERT INTO TrainEnvironments(ReleaseTrainId,EnvironmentId) VALUES('ta','enva');
        INSERT INTO Deployments(Id,ExternalId,CatalogServiceId,EnvironmentId,ReleaseTrainId,ReleaseVersion,CreatedAt,UpdatedAt,IngestKeyId) VALUES('depa','{Marker}-dep-1','svca','enva','ta','9.9.9','{t}','{t}','ika');
        INSERT INTO DeploymentEvents(DeploymentId,OccurredAt,ReceivedAt,Level,Message,ExternalId) VALUES('depa','{t}','{t}','Info','{Marker} event','{Marker}-ev-1');
        INSERT INTO MetricSamples(DeploymentId,Series,SampledAt,ReceivedAt,ErrorRatePct,LatencyP95Ms,AvailabilityPct,RequestCount) VALUES('depa','Canary','{t}','{t}',0.9,410,99.1,1200);
        INSERT INTO MetricRollups(DeploymentId,Series,BucketAt,Samples,ErrorRateAvgPct,ErrorRateMaxPct,RequestCount) VALUES('depa','Canary','2026-09-01T10:00:00Z',120,0.2,0.9,5000);
        INSERT INTO ThresholdBreaches(Id,DeploymentId,Series,Metric,ThresholdValue,ForSeconds,StartedAt,PeakValue,LastValue) VALUES('bra','depa','Canary','ErrorRate',0.5,120,'{t}',0.9,0.9);
        INSERT INTO DeploymentControlActions(Id,DeploymentId,Action,Source,RequestedByUserId,RequestedAt,Reason) VALUES('cnta','depa','Pause','Manual','{rteId}','{t}','{Marker} pause');
        INSERT INTO ApprovalPolicies(Id,Name,EnvironmentKind,CreatedAt) VALUES('pola','{Marker} Policy','Production','{t}');
        INSERT INTO ApprovalPolicyRequirements(Id,PolicyId,Label,EligibleKind,EligibleValue) VALUES('rqa','pola','{Marker} Seat','Team','tma');
        INSERT INTO Approvals(Id,ReleaseTrainId,EnvironmentId,RequestedByUserId,RequestedAt,DueAt) VALUES('apa','ta','enva','{rteId}','{t}','2026-10-02T00:00:00Z');
        INSERT INTO ApprovalDecisions(Id,ApprovalId,Label,EligibleKind,EligibleValue,DueAt) VALUES('dca','apa','{Marker} Seat','Team','tma','2026-10-02T00:00:00Z');
        INSERT INTO ChangeRecords(ReleaseTrainId,Justification) VALUES('ta','{Marker} justification');
        INSERT INTO AffectedCIs(Id,ReleaseTrainId,CiName) VALUES('cia','ta','{Marker} CI');
        INSERT INTO DeploymentWindows(Id,ReleaseTrainId,StartsAt,EndsAt) VALUES('dwa','ta','2026-11-30T02:00:00Z','2026-11-30T06:00:00Z');
        INSERT INTO BundledProducts(Id,ReleaseTrainId,ProductName,VersionTag,ProjectCode) VALUES('pa','ta','{Marker} Product','1.0.0','ZQA');
        INSERT INTO StageGates(Id,ReleaseTrainId,GateName,GateClass,SequenceOrder,OffsetDays,DueOn,RequiredBeforeStatus,OwnerUserId,Status,LastChangedByUserId) VALUES
          ('ga','ta','{Marker} Gate','Standard',1,5,'2026-11-23','Gated','{rteId}','Pending','{rteId}'),
          ('gca','ta','{Marker} Compliance','Compliance',2,2,'2026-11-28','Executing','{govId}','InProgress','{govId}');
        INSERT INTO ChecklistTasks(Id,StageGateId,TaskDescription,OwnerUserId,SequenceOrder) VALUES('ka','ga','{Marker} Task','{rteId}',1);
        INSERT INTO GateWaivers(Id,StageGateId,Reason,RequestedByUserId,RequestedAt) VALUES('wva','gca','{Marker} waiver reason, long enough','{rteId}','{t}');
        INSERT INTO Attachments(Id,ReleaseTrainId,EntityType,EntityId,FileName,ContentType,SizeBytes,Sha256,StoragePath,UploadedByUserId,UploadedAt)
          VALUES('aa','ta','Gate','ga','{Marker}.pdf','application/pdf',10,printf('%064d',0),'x/aa.bin','{rteId}','{t}');
        INSERT INTO RunbookSteps(Id,ReleaseTrainId,StepCode,Section,Title,OwnerUserId,PlannedStartAt,PlannedDurationMin) VALUES
          ('sa','ta','R-001','Deploy','{Marker} Step','{rteId}','2026-11-30T02:00:00Z',30),('sa2','ta','R-002','Deploy','{Marker} Step 2','{rteId}','2026-11-30T02:30:00Z',30);
        INSERT INTO StepDependencies(StepId,DependsOnStepId) VALUES('sa2','sa');
        INSERT INTO RunbookRuns(Id,ReleaseTrainId,Mode,StartedAt,StartedByUserId) VALUES('ra','ta','Rehearsal','{t}','{rteId}');
        INSERT INTO StepExecutions(Id,RunId,StepId) VALUES('ea','ra','sa');
        UPDATE RunbookRuns SET PausedAt='{t}', PausedByUserId='{rteId}', PauseReason='{Marker} pause' WHERE Id='ra';
        INSERT INTO Blockers(Id,ReleaseTrainId,Title,Severity,RaisedAt) VALUES('ba','ta','{Marker} Blocker','High','{t}');
        INSERT INTO KnownIssues(Id,ReleaseTrainId,Title,Severity,RaisedAt) VALUES('kia','ta','{Marker} Issue','Low','{t}');
        INSERT INTO GoNoGoDecisions(Id,ReleaseTrainId,Decision,DecidedByUserId,DecidedAt,GateSnapshotJson,Notes) VALUES('da','ta','GoWithConditions','{rteId}','{t}','{{}}','{Marker} notes');
        INSERT INTO GoNoGoConditions(Id,DecisionId,Text,OwnerUserId,ExpiresAt) VALUES('ca','da','{Marker} Condition','{rteId}','2026-11-29T00:00:00Z');
        INSERT INTO PostImplementationReviews(Id,ReleaseTrainId,RequiredReason,Summary) VALUES('pira','ta','Manual','{Marker} summary');
        INSERT INTO PirActions(Id,PirId,Text,OwnerUserId,DueOn) VALUES('paa','pira','{Marker} Action','{rteId}','2026-12-10');
        INSERT INTO TrainMilestones(Id,ReleaseTrainId,Name,DueOn,OwnerUserId) VALUES('ma','ta','{Marker} Milestone','2026-11-20','{rteId}');
        INSERT INTO FreezeWindows(Id,Name,StartsAt,EndsAt,CreatedByUserId) VALUES('fa','{Marker} Freeze','2026-12-20T00:00:00Z','2027-01-02T00:00:00Z','{govId}');
        INSERT INTO ExternalLinks(Id,ReleaseTrainId,EntityType,EntityId,SourceSystem,ExternalKey) VALUES('lka','ta','Train','ta','Jira','ZQALPHA-1');
        INSERT INTO SyncAlerts(Id,ReleaseTrainId,SourceSystem,Kind,Fingerprint,ErrorMessage,FirstOccurredAt,LastOccurredAt) VALUES('sya','ta','Jira','NotFound','fp-alpha','{Marker} alert','{t}','{t}');
        INSERT INTO WebhookDestinations(Id,Name,Host,ProtectedUrl,UrlHmac,Kind) VALUES('wha','{Marker} Channel','zqalpha.example.com','CfDJ8alpha',printf('%064d',1),'Generic');
        INSERT INTO CommTemplates(Id,ReleaseTrainId,LibraryTemplateId,TemplateType,Audience,SubjectLine,MarkdownBody) VALUES('cta','ta','la','Tminus28','All','{Marker} subject','{Marker} body');
        INSERT INTO CommDispatches(Id,CommTemplateId,Channel,HydratedSubject,HydratedBody,DispatchedByUserId,DispatchedAt,Outcome) VALUES('dsa','cta','Copy','{Marker} sent','{Marker} sent body','{rteId}','{t}','Handed');
        INSERT INTO CommSchedule(Id,ReleaseTrainId,CommTemplateId,DueAt) VALUES('csa','ta','cta','2026-11-02T00:00:00Z');
        INSERT INTO Notifications(Id,UserId,Kind,EntityType,EntityId,Message,CreatedAt) VALUES('na','ua','Reminder','Gate','ga','{Marker} reminder','{t}');
        INSERT INTO ImportJobs(Id,Kind,ReleaseTrainId,FileName,Sha256,RowCount,Status,UploadedByUserId,CreatedAt) VALUES('ija','Tasks','ta','{Marker}.csv','x',1,'Previewed','{rteId}','{t}');
        INSERT INTO ExportJobs(Id,Kind,ReleaseTrainId,FileName,RequestedByUserId,CreatedAt) VALUES('xa','ReleaseReportPdf','ta','{Marker}.pdf','{rteId}','{t}');
        INSERT INTO IcsTokens(Id,UserId,TokenSha256,CreatedAt) VALUES('ica','ua',printf('%064d',2),'{t}');
        INSERT INTO ConnectorState(SourceSystem,BaseUrl,IsEnabled) VALUES('Jira','https://zqalpha.atlassian.net',0);   -- disabled: A's own poller must not change A's rows during the comparison
        INSERT INTO AuditEvents(OccurredAt,ActorUserId,ReleaseTrainId,EntityType,EntityId,Action,AfterJson) VALUES('{t}','{rteId}','ta','Train','ta','Create','{{""title"":""{Marker} Train""}}');");
        // Route segment -> the id of A's row of that kind
        return new()
        {
            ["trains"] = "ta", ["gates"] = "ga", ["tasks"] = "ka", ["steps"] = "sa", ["products"] = "pa", ["runs"] = "ra", ["conditions"] = "ca", ["gonogo"] = "da",
            ["pir-actions"] = "paa", ["milestones"] = "ma", ["attachments"] = "aa", ["comm-library"] = "la", ["comm-templates"] = "cta", ["comm-schedule"] = "csa",
            ["dispatches"] = "dsa", ["export-jobs"] = "xa", ["freezes"] = "fa", ["links"] = "lka", ["notifications"] = "na", ["alerts"] = "sya", ["webhook-allowlist"] = "wha",
            ["teams"] = "tma", ["templates"] = "tpa", ["users"] = "ua", ["userId"] = "ua", ["waivers"] = "wva", ["ics-tokens"] = "ica", ["imports"] = "ija",
            ["release-types"] = "rta", ["versions"] = "vera", ["environments"] = "enva", ["services"] = "svca", ["deployments"] = "depa", ["ingest-keys"] = "ika", ["approval-requests"] = "apa", ["approval-policies"] = "pola",
            ["ciId"] = "cia", ["issueId"] = "kia", ["stepId"] = "sa", ["jobId"] = "ija", ["trainId"] = "ta", ["day"] = "2026-12-24",
            ["audit"] = Scalar(f, "SELECT Id FROM AuditEvents WHERE ReleaseTrainId='ta' AND EntityType='Train'"),   // GET /audit/{id}: one of A's events
        };
    }

    /// <summary>A's rows, every table, as text: compared before and after the members of B have called every endpoint.</summary>
    private static List<string> AlphaRows(ApiFactory f)
    {
        const string trains = "(SELECT Id FROM ReleaseTrains WHERE WorkspaceId='default')";
        const string gates = $"(SELECT Id FROM StageGates WHERE ReleaseTrainId IN {trains})";
        var queries = new[]
        {
            "SELECT * FROM Users WHERE Id='ua'", "SELECT * FROM WorkspaceMembers WHERE WorkspaceId='default'", "SELECT * FROM Workspaces WHERE Id='default'",
            "SELECT * FROM TeamMembers WHERE TeamId IN (SELECT Id FROM Teams WHERE WorkspaceId='default')",
            $"SELECT * FROM GateTransitions WHERE StageGateId IN {gates}", $"SELECT * FROM GateWaivers WHERE StageGateId IN {gates}", $"SELECT * FROM ChecklistTasks WHERE StageGateId IN {gates}",
            $"SELECT * FROM StepDependencies WHERE StepId IN (SELECT Id FROM RunbookSteps WHERE ReleaseTrainId IN {trains})",
            $"SELECT * FROM StepExecutions WHERE RunId IN (SELECT Id FROM RunbookRuns WHERE ReleaseTrainId IN {trains})",
            $"SELECT * FROM GoNoGoConditions WHERE DecisionId IN (SELECT Id FROM GoNoGoDecisions WHERE ReleaseTrainId IN {trains})",
            $"SELECT * FROM PirActions WHERE PirId IN (SELECT Id FROM PostImplementationReviews WHERE ReleaseTrainId IN {trains})",
            $"SELECT * FROM CommDispatches WHERE CommTemplateId IN (SELECT Id FROM CommTemplates WHERE ReleaseTrainId IN {trains})",
        }.ToList();
        foreach (var root in new[] { "Teams", "Holidays", "TrainTemplates", "CommTemplateLibrary", "ReleaseTrains", "FreezeWindows", "SyncAlerts", "WebhookDestinations",
                     "Notifications", "ImportJobs", "ExportJobs", "IcsTokens", "ConnectorState", "AuditEvents", "ReleaseTypes", "ReleaseVersions", "Environments", "CatalogServices", "IngestKeys", "Deployments",
                     "MetricSamples", "MetricRollups", "ThresholdBreaches", "DeploymentControlActions", "ApprovalPolicies", "Approvals" })
            queries.Add($"SELECT * FROM {root} WHERE WorkspaceId='default'");
        foreach (var child in new[] { "ChangeRecords", "AffectedCIs", "DeploymentWindows", "BundledProducts", "StageGates", "Attachments", "RunbookSteps", "RunbookRuns", "Blockers",
                     "KnownIssues", "GoNoGoDecisions", "PostImplementationReviews", "FreezeOverrides", "Baselines", "ExternalLinks", "CommTemplates", "CommSchedule", "TrainMilestones", "TrainEnvironments" })
            queries.Add($"SELECT * FROM {child} WHERE ReleaseTrainId IN {trains}");
        queries.Add("SELECT * FROM DeploymentEvents WHERE DeploymentId IN (SELECT Id FROM Deployments WHERE WorkspaceId='default')");
        queries.Add("SELECT * FROM ApprovalPolicyRequirements WHERE PolicyId IN (SELECT Id FROM ApprovalPolicies WHERE WorkspaceId='default')");
        queries.Add("SELECT * FROM ApprovalDecisions WHERE ApprovalId IN (SELECT Id FROM Approvals WHERE WorkspaceId='default')");
        var rows = new List<string>();
        using var c = new SqliteConnection($"Data Source={f.DbPath};Pooling=False");
        c.Open();
        foreach (var q in queries)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = q;
            using var r = cmd.ExecuteReader();
            while (r.Read()) rows.Add(q[..Math.Min(40, q.Length)] + ": " + string.Join("|", Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "∅" : Convert.ToString(r.GetValue(i)))));
        }
        rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    // Route values that never name a row of A's: the member's own session, catalog names, the SPA's own path.
    private static readonly Dictionary<string, string> Own = new()
    {
        ["source"] = "Jira", ["metric"] = "m1", ["grid"] = "gates", ["kind"] = "gates", ["clientId"] = "tab-12345678", ["path"] = "some/spa/route", ["token"] = "x-1",
    };

    private static (string Method, string Url, bool NamesAlpha) Request(RouteEndpoint e, Dictionary<string, string> alpha)
    {
        var method = e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.FirstOrDefault() ?? "ANY";
        var raw = e.RoutePattern.RawText!;
        var names = false;
        var segments = raw.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var m = Regex.Match(segments[i], @"^\{\*?(\w+)(:[^}]*)?\}(.*)$");
            if (!m.Success) continue;
            var name = m.Groups[1].Value;
            string value;
            if (Own.TryGetValue(name, out var own)) value = own;
            else if (name == "id") { value = alpha[segments[i - 1]]; names = true; }
            else { value = alpha[name]; names = true; }
            segments[i] = value + m.Groups[3].Value;
        }
        return (method, string.Join('/', segments), names);
    }

    private static async Task<string> Text(HttpResponseMessage res)
    {
        var bytes = await res.Content.ReadAsByteArrayAsync();
        if (bytes.Length > 4 && bytes[0] == (byte)'P' && bytes[1] == (byte)'K')   // XLSX: the text is inside the zip
        {
            using var zip = new ZipArchive(new MemoryStream(bytes));
            var sb = new StringBuilder();
            foreach (var entry in zip.Entries) { using var s = new StreamReader(entry.Open()); sb.Append(await s.ReadToEndAsync()); }
            return sb.ToString();
        }
        return Encoding.UTF8.GetString(bytes);
    }

    [Fact]
    public async Task A_member_of_another_workspace_can_neither_read_nor_change_anything_through_any_endpoint()
    {
        using var f = new ApiFactory();
        f.CreateClient();
        var endpoints = f.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText is { } r && r.StartsWith("/api/v1/") && !r.StartsWith("/api/v1/ics/") && !r.StartsWith("/api/v1/dev/"))
            .OrderBy(e => e.RoutePattern.RawText, StringComparer.Ordinal).ToList();
        await As(f, Roles.RTE, "rte@alpha.test");
        await As(f, Roles.GovernanceOfficer, "gov@alpha.test");
        var alpha = SeedAlpha(f, UserId(f, "rte@alpha.test"), UserId(f, "gov@alpha.test"));
        var members = new Dictionary<string, HttpClient>();
        foreach (var r in new[] { Roles.Viewer, Roles.RTE, Roles.ReleaseManager, Roles.GovernanceOfficer }) members[r] = await InB(f, r, $"{r.ToLowerInvariant()}@beta.test");
        Sql(f, $"INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,CreatedAt,UpdatedAt,WorkspaceId) VALUES('tb','ZQBETA Train','2026-11-30','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z','{B}')");
        var before = AlphaRows(f);

        var wrong = new List<string>();
        var checks = 0;
        foreach (var e in endpoints)
        {
            var (method, url, namesAlpha) = Request(e, alpha);
            foreach (var (role, client) in members)
            {
                using var req = new HttpRequestMessage(new HttpMethod(method), url);
                if (method is "POST" or "PUT" or "PATCH" or "DELETE") req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                using var res = await client.SendAsync(req);
                var body = await Text(res);
                checks++;
                var key = $"{method} {url} as {role} in B";
                if ((int)res.StatusCode >= 500) wrong.Add($"{key}: {(int)res.StatusCode} {body[..Math.Min(200, body.Length)]}");
                else if (namesAlpha && res.IsSuccessStatusCode) wrong.Add($"{key}: {(int)res.StatusCode}, expected a refusal for a row of another workspace");
                else if (body.Contains(Marker, StringComparison.OrdinalIgnoreCase)) wrong.Add($"{key}: {(int)res.StatusCode} and the answer shows workspace A's data");
            }
        }
        Assert.True(wrong.Count == 0, $"{wrong.Count} of {checks} isolation checks failed:\n  " + string.Join("\n  ", wrong));
        Assert.True(checks >= 600, $"Only {checks} requests were made; the enumeration is broken");

        var after = AlphaRows(f);
        var changed = after.Except(before).Select(r => "+ " + r).Concat(before.Except(after).Select(r => "- " + r)).ToList();
        Assert.True(changed.Count == 0, "Workspace A's rows changed:\n  " + string.Join("\n  ", changed));   // nothing of A's changed
        // and B's own train is there for B: the filters hide the other workspace, not everything
        Assert.Contains("ZQBETA Train", await members[Roles.Viewer].GetStringAsync("/api/v1/trains"));
        Assert.DoesNotContain("ZQBETA", await (await As(f, Roles.RTE, "rte@alpha.test")).GetStringAsync("/api/v1/trains"));
    }

    [Fact]
    public async Task The_workspace_comes_from_the_header_or_the_route_and_must_be_one_of_the_persons()
    {
        using var f = new ApiFactory();
        var both = await InB(f, Roles.RTE, "both@x.test", defaultRole: Roles.Viewer);   // a member of the default workspace and of B
        var onlyA = await As(f, Roles.RTE, "a@x.test");
        Sql(f, $"INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,CreatedAt,UpdatedAt,WorkspaceId) VALUES('tb','ZQBETA Train','2026-11-30','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z','{B}')");
        Sql(f, "INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,CreatedAt,UpdatedAt) VALUES('ta','ZQALPHA Train','2026-11-30','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z')");

        // no workspace named: a member of the default workspace works in it, with the role held there
        var me = await both.GetFromJsonAsync<Dictionary<string, object>>("/api/v1/me");
        Assert.Equal("default", me!["workspaceId"].ToString());
        Assert.Contains("Viewer", me["roles"].ToString());
        Assert.Contains("ZQALPHA", await both.GetStringAsync("/api/v1/trains"));

        // the header, by slug or id, and the route segment choose B, with B's role
        foreach (var (url, header) in new[] { ("/api/v1/me", "beta"), ("/api/v1/me", B), ("/api/v1/w/beta/me", null) })
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (header is not null) req.Headers.Add("X-Workspace", header);
            var body = await (await both.SendAsync(req)).Content.ReadFromJsonAsync<Dictionary<string, object>>();
            Assert.Equal(B, body!["workspaceId"].ToString());
            Assert.Contains("RTE", body["roles"].ToString());
        }
        var inB = await both.GetStringAsync("/api/v1/w/beta/trains");
        Assert.Contains("ZQBETA", inB);
        Assert.DoesNotContain("ZQALPHA", inB);
        // the role is the workspace's: a Viewer in the default workspace cannot plan there, an RTE in B can
        Assert.Equal(HttpStatusCode.Forbidden, (await both.PostAsJsonAsync("/api/v1/trains/ta:advance", new { to = "Gated" })).StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, (await both.PostAsJsonAsync("/api/v1/w/beta/trains/tb:advance", new { to = "Gated" })).StatusCode);

        // someone else's workspace, or one that does not exist: the same 404, whichever way it is named
        foreach (var name in new[] { "beta", B, "nope" })
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/trains");
            req.Headers.Add("X-Workspace", name);
            var res = await onlyA.SendAsync(req);
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
            Assert.Contains("WorkspaceNotFound", await res.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.NotFound, (await onlyA.GetAsync($"/api/v1/w/{name}/trains")).StatusCode);
        }

        // a member of several workspaces, none of them the default one, must name one
        var two = await InB(f, Roles.Viewer, "two@x.test");
        Sql(f, $@"INSERT INTO Workspaces(Id,Name,Slug,CreatedAt) VALUES('w-gamma','Gamma','gamma','2026-10-01T00:00:00Z');
                 INSERT INTO WorkspaceMembers(WorkspaceId,UserId,Role,AddedAt) VALUES('w-gamma','{UserId(f, "two@x.test")}','Viewer','2026-10-01T00:00:00Z');");
        var unnamed = await two.GetAsync("/api/v1/trains");
        Assert.Equal(HttpStatusCode.BadRequest, unnamed.StatusCode);
        Assert.Contains("WorkspaceRequired", await unnamed.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await two.GetAsync("/api/v1/w/gamma/trains")).StatusCode);

        // an archived workspace is closed, and a person in no workspace at all holds no role
        Sql(f, "UPDATE Workspaces SET ArchivedAt='2026-10-02T00:00:00Z' WHERE Id='w-gamma'");
        Assert.Equal(HttpStatusCode.NotFound, (await two.GetAsync("/api/v1/w/gamma/trains")).StatusCode);
        Sql(f, $"DELETE FROM WorkspaceMembers WHERE UserId='{UserId(f, "two@x.test")}'");
        Assert.Equal(HttpStatusCode.Forbidden, (await two.GetAsync("/api/v1/trains")).StatusCode);
    }

    private static async Task<(HubConnection Hub, Channel<string> Events)> Listen(ApiFactory f, string email, string role, string? workspace)
    {
        var login = await f.CreateClient().PostAsJsonAsync("/auth/dev-login", new { email, name = email.Split('@')[0], role });
        login.EnsureSuccessStatusCode();
        var cookie = login.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        var events = Channel.CreateUnbounded<string>();
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(f.Server.BaseAddress, "/hub/trains" + (workspace is null ? "" : "?workspace=" + workspace)), o =>
            {
                o.HttpMessageHandlerFactory = _ => f.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
                o.Headers["Cookie"] = cookie;
            }).Build();
        hub.On<string, int>("TrainChanged", (id, _) => events.Writer.TryWrite("TrainChanged " + id));
        hub.On<string>("NotificationCreated", id => events.Writer.TryWrite("NotificationCreated " + id));
        hub.On<string>("SyncAlertRaised", id => events.Writer.TryWrite("SyncAlertRaised " + id));
        await hub.StartAsync();
        return (hub, events);
    }

    private static async Task<List<string>> Drain(Channel<string> ch, TimeSpan window)
    {
        var seen = new List<string>();
        using var cts = new CancellationTokenSource(window);
        try { while (await ch.Reader.WaitToReadAsync(cts.Token)) while (ch.Reader.TryRead(out var e)) seen.Add(e); }
        catch (OperationCanceledException) { }
        return seen;
    }

    [Fact]
    public async Task Live_updates_alerts_and_notifications_reach_only_the_workspace_they_belong_to()
    {
        using var f = new ApiFactory();
        var rteA = await As(f, Roles.RTE, "rte@alpha.test");
        await InB(f, Roles.RTE, "rte@beta.test");
        SeedTrain(f, UserId(f, "rte@alpha.test"), UserId(f, "rte@alpha.test"));
        var (hubA, eventsA) = await Listen(f, "watcher@alpha.test", Roles.RTE, null);
        await InB(f, Roles.ReleaseManager, "watcher@beta.test");
        var (hubB, eventsB) = await Listen(f, "watcher@beta.test", Roles.ReleaseManager, "beta");
        // dev-login above put the B watcher back into the default workspace with its role; make it B's only again
        Sql(f, $"DELETE FROM WorkspaceMembers WHERE WorkspaceId='default' AND UserId='{UserId(f, "watcher@beta.test")}'");
        await using (hubA)
        await using (hubB)
        {
            Assert.Equal(HttpStatusCode.OK, (await rteA.PostAsJsonAsync("/api/v1/gates/g1:start", new { })).StatusCode);
            using (WorkspaceScope.Enter(WorkspaceIds.Default))   // a background failure in workspace A: an alert, and in-app notices to A's planners
                await f.Services.GetRequiredService<SyncAlertWriter>().RaiseAsync("Jira", "AuthFailed", "jira", "ZQALPHA token expired", "t1");

            var a = await Drain(eventsA, TimeSpan.FromSeconds(3));
            Assert.Contains("TrainChanged t1", a);
            Assert.Contains(a, e => e.StartsWith("SyncAlertRaised "));
            var b = await Drain(eventsB, TimeSpan.FromSeconds(1));
            Assert.Empty(b);
        }
        Assert.Equal("0", Scalar(f, $"SELECT COUNT(*) FROM Notifications WHERE UserId IN ('{UserId(f, "rte@beta.test")}','{UserId(f, "watcher@beta.test")}')"));
        Assert.NotEqual("0", Scalar(f, $"SELECT COUNT(*) FROM Notifications WHERE UserId = '{UserId(f, "rte@alpha.test")}' AND WorkspaceId = 'default'"));
    }

    [Fact]
    public async Task A_calendar_link_shows_its_own_workspace_and_stops_when_its_owner_leaves_it()
    {
        using var f = new ApiFactory();
        var b = await InB(f, Roles.RTE, "rte@beta.test");
        Sql(f, "INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,CreatedAt,UpdatedAt) VALUES('ta','ZQALPHA Train','2026-11-30','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z')");
        Sql(f, $"INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,CreatedAt,UpdatedAt,WorkspaceId) VALUES('tb','ZQBETA Train','2026-11-30','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z','{B}')");
        var minted = await (await b.PostAsJsonAsync("/api/v1/me/ics-tokens", new { scope = "all" })).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var path = minted.GetProperty("path").GetString()!;
        Assert.Equal(B, Scalar(f, "SELECT WorkspaceId FROM IcsTokens"));
        var anon = f.CreateClient();
        var feed = await anon.GetStringAsync(path);
        Assert.Contains("ZQBETA", feed);
        Assert.DoesNotContain("ZQALPHA", feed);
        Sql(f, $"DELETE FROM WorkspaceMembers WHERE WorkspaceId='{B}'");
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Audit_rows_are_read_in_their_own_workspace_and_account_rows_from_the_default_one()
    {
        using var f = new ApiFactory();
        var govB = await InB(f, Roles.GovernanceOfficer, "gov@beta.test");
        var govA = await As(f, Roles.GovernanceOfficer, "gov@alpha.test");
        Sql(f, $@"INSERT INTO AuditEvents(OccurredAt,EntityType,EntityId,Action,WorkspaceId) VALUES('2026-10-01T00:00:00Z','Team','ZQALPHA-team','Create','default'),
                   ('2026-10-01T00:00:00Z','Team','ZQBETA-team','Create','{B}'),('2026-10-01T00:00:00Z','User','ZQHOST-user','SignInCreate',NULL);");
        var inB = await govB.GetStringAsync("/api/v1/audit?limit=500");
        var inA = await govA.GetStringAsync("/api/v1/audit?limit=500");
        Assert.Contains("ZQBETA", inB);
        Assert.DoesNotContain("ZQALPHA", inB);
        Assert.DoesNotContain("ZQHOST", inB);
        Assert.Contains("ZQALPHA", inA);
        Assert.Contains("ZQHOST", inA);
        Assert.DoesNotContain("ZQBETA", inA);
        // sign-in writes account rows host-wide
        Assert.Equal("0", Scalar(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='User' AND Action LIKE 'SignIn%' AND WorkspaceId IS NOT NULL"));
    }

    [Fact]
    public async Task New_rows_take_the_current_workspace_and_an_admin_adds_people_to_it()
    {
        using var f = new ApiFactory();
        var rmB = await InB(f, Roles.ReleaseManager, "rm@beta.test");
        await As(f, Roles.RTE, "a@alpha.test");
        Assert.Equal(HttpStatusCode.OK, (await rmB.PostAsJsonAsync("/api/v1/teams", new { handle = "ops", name = "Ops", memberIds = Array.Empty<string>() })).StatusCode);
        Assert.Equal(B, Scalar(f, "SELECT WorkspaceId FROM Teams WHERE Handle='ops'"));
        Assert.Equal(B, Scalar(f, "SELECT WorkspaceId FROM AuditEvents WHERE EntityType='Team'"));
        // a team cannot take someone who is not a member of the workspace
        var outsider = UserId(f, "a@alpha.test");
        var refused = await rmB.PostAsJsonAsync("/api/v1/teams", new { handle = "dev", name = "Dev", memberIds = new[] { outsider } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);

        // adding a person who already has an account (elsewhere) makes them a member, without saying where they are
        var add = await rmB.PostAsJsonAsync("/api/v1/users", new { email = "a@alpha.test", displayName = "A", role = Roles.Viewer });
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        Assert.Equal("Viewer", Scalar(f, $"SELECT Role FROM WorkspaceMembers WHERE WorkspaceId='{B}' AND UserId='{outsider}'"));
        Assert.Equal("RTE", Scalar(f, $"SELECT Role FROM WorkspaceMembers WHERE WorkspaceId='default' AND UserId='{outsider}'"));
        var again = await rmB.PostAsJsonAsync("/api/v1/users", new { email = "a@alpha.test", displayName = "A", role = Roles.Viewer });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
        // a new person joins B only, and the account row is host-wide
        Assert.Equal(HttpStatusCode.OK, (await rmB.PostAsJsonAsync("/api/v1/users", new { email = "new@beta.test", displayName = "New", role = Roles.RTE })).StatusCode);
        var created = UserId(f, "new@beta.test");
        Assert.Equal($"{B}:RTE", Scalar(f, $"SELECT group_concat(WorkspaceId || ':' || Role) FROM WorkspaceMembers WHERE UserId='{created}'"));
        Assert.Equal("∅", Scalar(f, $"SELECT COALESCE(WorkspaceId, '∅') FROM AuditEvents WHERE EntityType='User' AND EntityId='{created}'"));
    }

    [Fact]
    public async Task The_same_webhook_address_in_another_workspace_is_refused_without_naming_the_workspace()
    {
        using var f = new ApiFactory();
        var rmA = await As(f, Roles.ReleaseManager, "rm@alpha.test");
        var rmB = await InB(f, Roles.ReleaseManager, "rm@beta.test");
        var hook = new { name = "#ops", url = "https://hooks.slack.com/services/T000/B000/XXXXXXXXXXXXXXXXXXXXXXXX" };
        Assert.Equal(HttpStatusCode.OK, (await rmA.PostAsJsonAsync("/api/v1/sync/webhook-allowlist", hook)).StatusCode);
        var inA = await rmA.PostAsJsonAsync("/api/v1/sync/webhook-allowlist", hook);
        var inB = await rmB.PostAsJsonAsync("/api/v1/sync/webhook-allowlist", hook);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, inB.StatusCode);
        var a = await inA.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        var b = await inB.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("WebhookDuplicate", b!["guard"].ToString());
        Assert.Equal(a!["message"].ToString(), b["message"].ToString());   // the same words whichever workspace holds it
        Assert.DoesNotContain("workspace", b["message"].ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Default", b["message"].ToString());
    }

    [Fact]
    public async Task The_shell_shows_the_current_workspace_and_queues_only_its_approvals_by_the_role_held_there()
    {
        using var f = new ApiFactory();
        await As(f, Roles.RTE, "rte@alpha.test");
        var gov = await InB(f, Roles.GovernanceOfficer, "gov@both.test", defaultRole: Roles.Viewer);   // Governance Officer in B, Viewer in the default workspace
        var rte = UserId(f, "rte@alpha.test");
        SeedAlpha(f, rte, rte);   // A has an unapproved waiver (wva) that a Governance Officer of A would see
        Sql(f, $@"INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,CreatedAt,UpdatedAt,WorkspaceId) VALUES('tb','ZQBETA Train','2026-11-30','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z','{B}');
                 INSERT INTO StageGates(Id,ReleaseTrainId,GateName,GateClass,SequenceOrder,OffsetDays,DueOn,OwnerUserId,Status,LastChangedByUserId) VALUES('gb','tb','ZQBETA Compliance','Compliance',1,2,'2026-11-28','{rte}','InProgress','{rte}');
                 INSERT INTO GateWaivers(Id,StageGateId,Reason,RequestedByUserId,RequestedAt) VALUES('wvb','gb','ZQBETA waiver reason, long enough','{rte}','2026-10-01T00:00:00Z');");

        using var inB = new HttpRequestMessage(HttpMethod.Get, "/api/v1/approvals");
        inB.Headers.Add("X-Workspace", "beta");
        var queue = await (await gov.SendAsync(inB)).Content.ReadAsStringAsync();
        Assert.Contains("wvb", queue);
        Assert.DoesNotContain("ZQALPHA", queue);
        var inDefault = await gov.GetStringAsync("/api/v1/approvals");   // a Viewer there: nothing to approve
        Assert.DoesNotContain("wva", inDefault);
        Assert.DoesNotContain("wvb", inDefault);
        Assert.Contains("\"slug\":\"beta\"", await gov.GetStringAsync("/api/v1/w/beta/workspace"));
        Assert.Contains("\"slug\":\"default\"", await gov.GetStringAsync("/api/v1/workspace"));
    }

    [Fact]
    public async Task The_same_failure_in_two_workspaces_is_two_open_alerts()
    {
        using var f = new ApiFactory();
        f.CreateClient();
        Sql(f, $"INSERT INTO Workspaces(Id,Name,Slug,CreatedAt) VALUES('{B}','Beta','beta','2026-10-01T00:00:00Z')");
        var writer = f.Services.GetRequiredService<SyncAlertWriter>();
        foreach (var ws in new[] { WorkspaceIds.Default, B, B })
            using (WorkspaceScope.Enter(ws)) await writer.RaiseAsync("Jira", "Unreachable", "jira", "down");
        Assert.Equal($"default:1,{B}:2", Scalar(f, "SELECT group_concat(WorkspaceId || ':' || OccurrenceCount, ',') FROM (SELECT * FROM SyncAlerts ORDER BY WorkspaceId)"));
        await writer.RaiseAsync("Backup", "BackupFailed", "backup", "disk full");   // outside any workspace: host-wide
        Assert.Equal("1", Scalar(f, "SELECT COUNT(*) FROM SyncAlerts WHERE WorkspaceId IS NULL AND SourceSystem='Backup'"));
    }
}
