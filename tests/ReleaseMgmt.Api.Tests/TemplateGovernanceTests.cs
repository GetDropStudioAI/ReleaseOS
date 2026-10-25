using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReleaseMgmt.Domain.Common;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// REOS-122 (RT7): template governance. Templates carry checklist tasks and milestones; a release made from one gets them; templates have versions; the approver is not the author
/// (service 422, trigger as backstop); a Governance Officer approves a change to a Compliance gate (Q10); Compliance gates of an approved template are locked on the release (Q8);
/// milestones move with the target (Q9). Q8 to Q10 are built to the recommended defaults behind <c>Templates:*</c> keys (docs/QUESTIONS.md Q-122a).
/// </summary>
public class TemplateGovernanceTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private static async Task<HttpResponseMessage> Send(HttpClient c, HttpMethod m, string url, object? body = null, string? ifMatch = null)
    {
        var req = new HttpRequestMessage(m, url) { Content = body is null ? null : JsonContent.Create(body) };
        if (ifMatch is not null) req.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return await c.SendAsync(req);
    }

    private static async Task<string> Guard(HttpResponseMessage r)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        return (await Json(r)).GetProperty("guard").GetString()!;
    }

    private static string Target(int days = 60) => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(days).ToString("yyyy-MM-dd");

    /// <summary>Code Freeze (Standard, two tasks), CAB (Compliance, one task), three milestones, one runbook step.</summary>
    private static object Body(string name = "Quarterly", int freezeOffset = 5, int cabOffset = 1, string cabTask = "SOX evidence uploaded", string cabClass = "Compliance", object[]? milestones = null) => new
    {
        name,
        defaultRiskTier = "High",
        gates = new object[]
        {
            new { gateName = "Code Freeze", gateClass = "Standard", offsetDays = freezeOffset, requiredBeforeStatus = "Gated", tasks = new object[] { new { taskDescription = "Tag the repositories" }, new { taskDescription = "Freeze the release branch" } } },
            new { gateName = "CAB", gateClass = cabClass, offsetDays = cabOffset, requiredBeforeStatus = "Executing", tasks = new object[] { new { taskDescription = cabTask } } },
        },
        steps = new[] { new { stepCode = "PRE-1", section = "PreCheck", title = "Health check", offsetMinutes = -30, plannedDurationMin = 15 } },
        schedule = Array.Empty<object>(),
        milestones = milestones ?? [new { name = "Code complete", offsetDays = -14 }, new { name = "UAT sign-off", offsetDays = -3 }, new { name = "Hypercare exit", offsetDays = 7 }],
    };

    private sealed record Crew(ApiFactory F, HttpClient Rte, HttpClient Rm, HttpClient Rm2, HttpClient Gov);

    private static async Task<Crew> Setup(Dictionary<string, string>? settings = null)
    {
        var f = new ApiFactory(settings: settings);
        return new(f, await As(f, Roles.RTE, "rte@x.com"), await As(f, Roles.ReleaseManager, "rm@x.com"), await As(f, Roles.ReleaseManager, "rm2@x.com"), await As(f, Roles.GovernanceOfficer, "gov@x.com"));
    }

    private static async Task<JsonElement> Create(HttpClient c, object body)
    {
        var r = await Send(c, HttpMethod.Post, "/api/v1/templates", body);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await Json(r);
    }

    private static async Task<JsonElement> Approve(HttpClient c, JsonElement t)
    {
        var r = await Send(c, HttpMethod.Post, $"/api/v1/templates/{t.GetProperty("id").GetString()}:approve", null, t.GetProperty("version").GetInt32().ToString());
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await Json(r);
    }

    private static Task<HttpResponseMessage> TryApprove(HttpClient c, JsonElement t) =>
        Send(c, HttpMethod.Post, $"/api/v1/templates/{t.GetProperty("id").GetString()}:approve", null, t.GetProperty("version").GetInt32().ToString());

    private static async Task<JsonElement> NewVersion(HttpClient c, JsonElement approved)
    {
        var r = await Send(c, HttpMethod.Post, $"/api/v1/templates/{approved.GetProperty("id").GetString()}:new-version", null, approved.GetProperty("version").GetInt32().ToString());
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await Json(r);
    }

    private static async Task<JsonElement> Edit(HttpClient c, JsonElement draft, object body)
    {
        var r = await Send(c, HttpMethod.Put, $"/api/v1/templates/{draft.GetProperty("id").GetString()}", body, draft.GetProperty("version").GetInt32().ToString());
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await Json(r);
    }

    private static async Task<JsonElement> Get(HttpClient c, string url) => await Json(await c.GetAsync(url));

    private static async Task<string> MakeRelease(HttpClient c, string templateId, string title, string target, string? releaseTypeId = null)
    {
        var r = await Send(c, HttpMethod.Post, "/api/v1/trains", new { title, targetReleaseDate = target, templateId, releaseTypeId });
        Assert.True(r.StatusCode == HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        return (await Json(r)).GetProperty("id").GetString()!;
    }

    private static long Audits(ApiFactory f, string entity, string action, string? id = null) =>
        long.Parse(Scalar(f, $"SELECT COUNT(*) FROM AuditEvents WHERE EntityType='{entity}' AND Action='{action}'{(id is null ? "" : $" AND EntityId='{id}'")}"));

    // ---- tasks and milestones in templates, and what a release gets

    [Fact]
    public async Task A_template_carries_tasks_and_milestones_and_a_release_made_from_it_gets_them()
    {
        var (f, rte, rm, _, gov) = await Setup(); using var _f = f;
        var draft = await Create(rte, Body());
        Assert.Equal(3, draft.GetProperty("template").GetProperty("taskCount").GetInt32());
        Assert.Equal(3, draft.GetProperty("template").GetProperty("milestoneCount").GetInt32());
        Assert.Equal(2, draft.GetProperty("gates")[0].GetProperty("tasks").GetArrayLength());
        Assert.Equal("Tag the repositories", draft.GetProperty("gates")[0].GetProperty("tasks")[0].GetProperty("taskDescription").GetString());
        Assert.Equal(-14, draft.GetProperty("milestones")[0].GetProperty("offsetDays").GetInt32());
        var approved = await Approve(gov, draft);
        var id = approved.GetProperty("id").GetString()!;

        var target = Target();
        var trainId = await MakeRelease(rm, id, "R27.05 From template", target);
        var created = Assert.Single((await Get(rm, "/api/v1/trains")).EnumerateArray(), x => x.GetProperty("id").GetString() == trainId);
        Assert.Equal("Planning", created.GetProperty("status").GetString());

        // the tasks sit on their gates and belong to the person who made the release (the template named no team)
        Assert.Equal("2", Scalar(f, $"SELECT COUNT(*) FROM ChecklistTasks k JOIN StageGates g ON g.Id=k.StageGateId WHERE g.ReleaseTrainId='{trainId}' AND g.GateName='Code Freeze'"));
        Assert.Equal("1", Scalar(f, $"SELECT COUNT(*) FROM ChecklistTasks k JOIN StageGates g ON g.Id=k.StageGateId WHERE g.ReleaseTrainId='{trainId}' AND g.GateName='CAB' AND k.TaskDescription='SOX evidence uploaded'"));
        Assert.Equal(UserId(f, "rm@x.com"), Scalar(f, $"SELECT DISTINCT k.OwnerUserId FROM ChecklistTasks k JOIN StageGates g ON g.Id=k.StageGateId WHERE g.ReleaseTrainId='{trainId}' AND g.GateName='Code Freeze'"));
        Assert.Equal("0", Scalar(f, $"SELECT COUNT(*) FROM ChecklistTasks k JOIN StageGates g ON g.Id=k.StageGateId WHERE g.ReleaseTrainId='{trainId}' AND k.IsCompleted=1"));

        // the milestones are dated from the target and keep their offset
        var targetDay = DateOnly.Parse(target);
        var milestones = await Get(rm, $"/api/v1/trains/{trainId}/milestones");
        Assert.Equal(3, milestones.GetArrayLength());
        foreach (var m in milestones.EnumerateArray())
            Assert.Equal(targetDay.AddDays(m.GetProperty("offsetDays").GetInt32()).ToString("yyyy-MM-dd"), m.GetProperty("dueOn").GetString());
        Assert.Equal(new[] { -14, -3, 7 }, milestones.EnumerateArray().Select(m => m.GetProperty("offsetDays").GetInt32()).OrderBy(x => x).ToArray());

        // the audit rows: one per task and per milestone, in the creating transaction
        Assert.Equal("3", Scalar(f, $"SELECT COUNT(*) FROM AuditEvents WHERE ReleaseTrainId='{trainId}' AND EntityType='ChecklistTask' AND Action='Create'"));
        Assert.Equal("3", Scalar(f, $"SELECT COUNT(*) FROM AuditEvents WHERE ReleaseTrainId='{trainId}' AND EntityType='Milestone' AND Action='Add'"));
    }

    [Fact]
    public async Task The_created_counts_say_how_many_tasks_and_milestones_came_with_the_release()
    {
        var (f, rte, rm, _, gov) = await Setup(); using var _f = f;
        var approved = await Approve(gov, await Create(rte, Body()));
        var r = await Send(rm, HttpMethod.Post, "/api/v1/trains", new { title = "R27.06 Counts", targetReleaseDate = Target(), templateId = approved.GetProperty("id").GetString() });
        var created = (await Json(r)).GetProperty("created");
        Assert.Equal(2, created.GetProperty("gates").GetInt32());
        Assert.Equal(3, created.GetProperty("tasks").GetInt32());
        Assert.Equal(3, created.GetProperty("milestones").GetInt32());
    }

    [Fact]
    public async Task A_template_task_with_a_team_belongs_to_the_team_and_a_bad_task_is_refused()
    {
        var (f, rte, _, _, _) = await Setup(); using var _f = f;
        Sql(f, "INSERT INTO Teams(Id,Handle,Name) VALUES('tmq','qa','QA')");
        var ok = await Create(rte, new { name = "Owned", gates = new[] { new { gateName = "QA", gateClass = "Standard", offsetDays = 3, requiredBeforeStatus = "Gated", tasks = new[] { new { taskDescription = "Regression pass", ownerTeamId = "tmq" } } } } });
        Assert.Equal("@qa", ok.GetProperty("gates")[0].GetProperty("tasks")[0].GetProperty("ownerTeamName").GetString());
        Assert.Equal("TemplateInvalid", await Guard(await Send(rte, HttpMethod.Post, "/api/v1/templates", new { name = "Blank task", gates = new[] { new { gateName = "G", gateClass = "Standard", offsetDays = 1, requiredBeforeStatus = "Gated", tasks = new[] { new { taskDescription = "  " } } } } })));
        Assert.Equal("FieldTooLong", await Guard(await Send(rte, HttpMethod.Post, "/api/v1/templates", new { name = "Long task", gates = new[] { new { gateName = "G", gateClass = "Standard", offsetDays = 1, requiredBeforeStatus = "Gated", tasks = new[] { new { taskDescription = new string('x', 501) } } } } })));
        Assert.Equal("TemplateInvalid", await Guard(await Send(rte, HttpMethod.Post, "/api/v1/templates", new { name = "Unknown team", gates = new[] { new { gateName = "G", gateClass = "Standard", offsetDays = 1, requiredBeforeStatus = "Gated", tasks = new[] { new { taskDescription = "t", ownerTeamId = "nope" } } } } })));
        Assert.Equal("TemplateInvalid", await Guard(await Send(rte, HttpMethod.Post, "/api/v1/templates", Body("Far milestone", milestones: [new { name = "Far", offsetDays = 400 }]))));
        Assert.Equal("TemplateInvalid", await Guard(await Send(rte, HttpMethod.Post, "/api/v1/templates", Body("Nameless milestone", milestones: [new { name = " ", offsetDays = -3 }]))));
        Assert.Equal("TemplateInvalid", await Guard(await Send(rte, HttpMethod.Post, "/api/v1/templates", new { name = "Twice", gates = new[] { new { gateName = "Gate", gateClass = "Standard", offsetDays = 1, requiredBeforeStatus = "Gated" }, new { gateName = "gate", gateClass = "Standard", offsetDays = 2, requiredBeforeStatus = "Gated" } } })));
        Assert.Equal("0", Scalar(f, "SELECT COUNT(*) FROM TrainTemplates WHERE Name IN ('Blank task','Long task','Unknown team','Far milestone','Nameless milestone','Twice')"));
    }

    [Fact]
    public async Task Milestones_made_from_a_template_move_with_the_target_and_a_date_set_by_hand_stays()
    {
        var (f, rte, rm, _, gov) = await Setup(); using var _f = f;
        var approved = await Approve(gov, await Create(rte, Body()));
        var trainId = await MakeRelease(rm, approved.GetProperty("id").GetString()!, "R27.07 Moves", Target());

        var version = (await Get(rm, $"/api/v1/trains/{trainId}")).GetProperty("version").GetInt32();
        var newTarget = DateOnly.Parse(Target(75));
        var moved = await Send(rm, new HttpMethod("PATCH"), $"/api/v1/trains/{trainId}", new { targetReleaseDate = newTarget.ToString("yyyy-MM-dd") }, version.ToString());
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        var milestones = await Get(rm, $"/api/v1/trains/{trainId}/milestones");
        foreach (var m in milestones.EnumerateArray())
            Assert.Equal(newTarget.AddDays(m.GetProperty("offsetDays").GetInt32()).ToString("yyyy-MM-dd"), m.GetProperty("dueOn").GetString());
        Assert.Equal("3", Scalar(f, $"SELECT COUNT(*) FROM AuditEvents WHERE ReleaseTrainId='{trainId}' AND EntityType='Milestone' AND Action='MovedWithTarget'"));
        Assert.Contains("\"milestonesMoved\":3", Scalar(f, $"SELECT AfterJson FROM AuditEvents WHERE ReleaseTrainId='{trainId}' AND Action='ChangeTargetDate'"));
        Assert.Equal("1", Scalar(f, $"SELECT MIN(Version) - 1 FROM TrainMilestones WHERE ReleaseTrainId='{trainId}'"));   // each moved milestone's Version is bumped

        // a date set by hand is a fixed date: the milestone stops following the target
        var uat = milestones.EnumerateArray().Single(m => m.GetProperty("name").GetString() == "UAT sign-off");
        var hand = newTarget.AddDays(-20).ToString("yyyy-MM-dd");
        var edit = await Send(rm, new HttpMethod("PATCH"), $"/api/v1/milestones/{uat.GetProperty("id").GetString()}", new { dueOn = hand }, uat.GetProperty("version").GetInt32().ToString());
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.Equal("", Scalar(f, $"SELECT IFNULL(OffsetDays,'') FROM TrainMilestones WHERE Id='{uat.GetProperty("id").GetString()}'"));
        version = (await Get(rm, $"/api/v1/trains/{trainId}")).GetProperty("version").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await Send(rm, new HttpMethod("PATCH"), $"/api/v1/trains/{trainId}", new { targetReleaseDate = Target(90) }, version.ToString())).StatusCode);
        Assert.Equal(hand, Scalar(f, $"SELECT DueOn FROM TrainMilestones WHERE Id='{uat.GetProperty("id").GetString()}'"));
        var code = (await Get(rm, $"/api/v1/trains/{trainId}/milestones")).EnumerateArray().Single(m => m.GetProperty("name").GetString() == "Code complete");
        Assert.Equal(DateOnly.Parse(Target(90)).AddDays(-14).ToString("yyyy-MM-dd"), code.GetProperty("dueOn").GetString());
    }

    [Fact]
    public async Task With_MilestonesMoveWithTarget_off_a_milestone_gets_a_fixed_date()
    {
        var (f, rte, rm, _, gov) = await Setup(new() { ["Templates:MilestonesMoveWithTarget"] = "false" }); using var _f = f;
        var approved = await Approve(gov, await Create(rte, Body()));
        var target = Target();
        var trainId = await MakeRelease(rm, approved.GetProperty("id").GetString()!, "R27.08 Fixed", target);
        Assert.Equal("3", Scalar(f, $"SELECT COUNT(*) FROM TrainMilestones WHERE ReleaseTrainId='{trainId}' AND OffsetDays IS NULL"));
        Assert.Equal(DateOnly.Parse(target).AddDays(-14).ToString("yyyy-MM-dd"), Scalar(f, $"SELECT DueOn FROM TrainMilestones WHERE ReleaseTrainId='{trainId}' AND Name='Code complete'"));
        var version = (await Get(rm, $"/api/v1/trains/{trainId}")).GetProperty("version").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await Send(rm, new HttpMethod("PATCH"), $"/api/v1/trains/{trainId}", new { targetReleaseDate = Target(80) }, version.ToString())).StatusCode);
        Assert.Equal(DateOnly.Parse(target).AddDays(-14).ToString("yyyy-MM-dd"), Scalar(f, $"SELECT DueOn FROM TrainMilestones WHERE ReleaseTrainId='{trainId}' AND Name='Code complete'"));
    }

    // ---- the approver is not the author

    [Fact]
    public async Task The_author_does_not_approve_and_neither_does_the_person_who_last_changed_it()
    {
        var (f, rte, rm, rm2, _) = await Setup(); using var _f = f;
        var plain = Body("Plain", cabClass: "Standard");   // no Compliance gate: only separation of duties is in play

        var mine = await Create(rm, plain);
        Assert.Equal(UserId(f, "rm@x.com"), mine.GetProperty("template").GetProperty("createdByUserId").GetString());
        var refused = await TryApprove(rm, mine);
        Assert.Equal("TemplateApproverIsAuthor", await Guard(refused));
        Assert.Contains("someone else approves it", (await Json(refused)).GetProperty("message").GetString());
        Assert.Equal("Draft", Scalar(f, $"SELECT Status FROM TrainTemplates WHERE Id='{mine.GetProperty("id").GetString()}'"));
        Assert.Equal(0, Audits(f, "TrainTemplate", "Approve"));                                   // a refusal writes nothing
        Assert.Equal(HttpStatusCode.OK, (await TryApprove(rm2, mine)).StatusCode);                  // a different Release Manager may

        // someone else's draft that I changed last is mine too
        var theirs = await Create(rte, Body("Theirs", cabClass: "Standard"));
        var edited = await Edit(rm, theirs, Body("Theirs", freezeOffset: 4, cabClass: "Standard"));
        Assert.Equal(UserId(f, "rm@x.com"), edited.GetProperty("template").GetProperty("lastEditedByUserId").GetString());
        Assert.Equal("TemplateApproverIsAuthor", await Guard(await TryApprove(rm, edited)));
        Assert.Equal(HttpStatusCode.OK, (await TryApprove(rm2, edited)).StatusCode);
    }

    [Fact]
    public async Task The_trigger_refuses_an_author_approving_whatever_wrote_the_row()
    {
        var (f, rte, _, _, _) = await Setup(); using var _f = f;
        var draft = await Create(rte, Body("Backstop", cabClass: "Standard"));
        var id = draft.GetProperty("id").GetString()!;
        var author = UserId(f, "rte@x.com");
        var e = Assert.Throws<SqliteException>(() => Sql(f, $"UPDATE TrainTemplates SET Status='Approved', ApprovedByUserId='{author}', ApprovedAt='2026-10-01T00:00:00Z' WHERE Id='{id}'"));
        Assert.Contains("Separation of duties", e.Message);
        Assert.Equal("Draft", Scalar(f, $"SELECT Status FROM TrainTemplates WHERE Id='{id}'"));
        var other = UserId(f, "rte@x.com");   // an insert that is already approved by its author is refused as well
        var e2 = Assert.Throws<SqliteException>(() => Sql(f, $"INSERT INTO TrainTemplates(Id,Name,Status,ApprovedByUserId,ApprovedAt,CreatedByUserId) VALUES('x1','Inserted','Approved','{other}','2026-10-01T00:00:00Z','{other}')"));
        Assert.Contains("Separation of duties", e2.Message);
    }

    // ---- a Compliance change needs a Governance Officer (Q10)

    [Fact]
    public async Task A_Release_Manager_does_not_approve_a_template_that_adds_or_changes_a_Compliance_gate()
    {
        var (f, rte, rm, _, gov) = await Setup(); using var _f = f;
        var v1 = await Create(rte, Body());
        Assert.True(v1.GetProperty("needsGovernanceApproval").GetBoolean());
        var refused = await TryApprove(rm, v1);
        Assert.Equal("TemplateNeedsGovernanceApproval", await Guard(refused));
        Assert.Contains("Governance Officer", (await Json(refused)).GetProperty("message").GetString());
        Assert.Equal("Draft", Scalar(f, $"SELECT Status FROM TrainTemplates WHERE Id='{v1.GetProperty("id").GetString()}'"));
        var approved1 = await Approve(gov, v1);

        // version 2 changes a Standard gate only: a Release Manager approves
        var v2 = await NewVersion(rte, approved1);
        Assert.False(v2.GetProperty("complianceChanged").GetBoolean());
        v2 = await Edit(rte, v2, Body(freezeOffset: 4));
        Assert.False(v2.GetProperty("needsGovernanceApproval").GetBoolean());
        var approved2 = await Approve(rm, v2);

        // version 3 changes the Compliance gate's offset: a Governance Officer approves
        var v3 = await Edit(rte, await NewVersion(rte, approved2), Body(freezeOffset: 4, cabOffset: 2));
        Assert.True(v3.GetProperty("complianceChanged").GetBoolean());
        Assert.Contains(v3.GetProperty("changes").EnumerateArray(), c => c.GetProperty("area").GetString() == "Gate" && c.GetProperty("subject").GetString() == "CAB" && c.GetProperty("compliance").GetBoolean());
        Assert.Equal("TemplateNeedsGovernanceApproval", await Guard(await TryApprove(rm, v3)));
        var approved3 = await Approve(gov, v3);

        // version 4 changes a Compliance task; version 5 downgrades the gate to Standard: each needs a Governance Officer
        var v4 = await Edit(rte, await NewVersion(rte, approved3), Body(freezeOffset: 4, cabOffset: 2, cabTask: "SOX evidence and sign-off uploaded"));
        Assert.Equal("TemplateNeedsGovernanceApproval", await Guard(await TryApprove(rm, v4)));
        var approved4 = await Approve(gov, v4);
        var v5 = await Edit(rte, await NewVersion(rte, approved4), Body(freezeOffset: 4, cabOffset: 2, cabTask: "SOX evidence and sign-off uploaded", cabClass: "Standard"));
        Assert.Equal("TemplateNeedsGovernanceApproval", await Guard(await TryApprove(rm, v5)));
        Assert.Equal(HttpStatusCode.OK, (await TryApprove(gov, v5)).StatusCode);
    }

    [Fact]
    public async Task With_the_Governance_Officer_rule_off_a_Release_Manager_approves_a_Compliance_template()
    {
        var (f, rte, rm, _, _) = await Setup(new() { ["Templates:ComplianceChangeNeedsGovernanceOfficer"] = "false" }); using var _f = f;
        var v1 = await Create(rte, Body());
        Assert.False(v1.GetProperty("needsGovernanceApproval").GetBoolean());
        Assert.Equal("Approved", (await Approve(rm, v1)).GetProperty("status").GetString());
        var mine = await Create(rm, Body("Author still counts"));
        Assert.Equal("TemplateApproverIsAuthor", await Guard(await TryApprove(rm, mine)));   // the Governance Officer rule is off; separation of duties is not a setting
    }

    // ---- versions

    [Fact]
    public async Task A_new_version_is_a_draft_under_the_same_name_and_approving_it_retires_the_old_one_and_touches_no_release()
    {
        var (f, rte, rm, _, gov) = await Setup(); using var _f = f;
        var v1 = await Approve(gov, await Create(rte, Body()));
        var v1Id = v1.GetProperty("id").GetString()!;
        var type = await Json(await Send(rte, HttpMethod.Post, "/api/v1/release-types", new { name = "Quarterly type", defaultTemplateId = v1Id }));
        var typeId = type.GetProperty("id").GetString()!;
        var target = Target();
        var oldRelease = await MakeRelease(rm, v1Id, "R27.09 Made from version 1", target, typeId);
        var gatesBefore = Scalar(f, $"SELECT group_concat(GateName || ':' || OffsetDays || ':' || TemplateLocked, ',') FROM (SELECT * FROM StageGates WHERE ReleaseTrainId='{oldRelease}' ORDER BY SequenceOrder)");

        // only an Approved template gets a version
        var draftOnly = await Create(rte, Body("Draft only"));
        Assert.Equal("IllegalTemplateTransition", await Guard(await Send(rte, HttpMethod.Post, $"/api/v1/templates/{draftOnly.GetProperty("id").GetString()}:new-version", null, draftOnly.GetProperty("version").GetInt32().ToString())));

        var v2 = await NewVersion(rte, v1);
        var t2 = v2.GetProperty("template");
        Assert.Equal("Quarterly", t2.GetProperty("name").GetString());
        Assert.Equal(2, t2.GetProperty("versionNo").GetInt32());
        Assert.Equal("Draft", t2.GetProperty("status").GetString());
        Assert.Equal(v1Id, t2.GetProperty("supersedesTemplateId").GetString());
        Assert.Equal(UserId(f, "rte@x.com"), t2.GetProperty("createdByUserId").GetString());
        Assert.Equal(2, v2.GetProperty("gates").GetArrayLength());
        Assert.Equal(3, t2.GetProperty("taskCount").GetInt32());
        Assert.Equal(3, t2.GetProperty("milestoneCount").GetInt32());
        Assert.Equal(1, t2.GetProperty("stepCount").GetInt32());
        Assert.Equal(2, v2.GetProperty("versions").GetArrayLength());
        Assert.Empty(v2.GetProperty("changes").EnumerateArray());   // a copy differs from nothing
        Assert.Equal("Approved", Scalar(f, $"SELECT Status FROM TrainTemplates WHERE Id='{v1Id}'"));   // version 1 is untouched until version 2 is approved
        Assert.Equal(1, Audits(f, "TrainTemplate", "NewVersion"));

        // one more version of the same template is refused while version 2 exists; a new version keeps the name
        Assert.Equal("TemplateVersionExists", await Guard(await Send(rte, HttpMethod.Post, $"/api/v1/templates/{v1Id}:new-version", null, v1.GetProperty("version").GetInt32().ToString())));
        Assert.Equal("TemplateInvalid", await Guard(await Send(rte, HttpMethod.Put, $"/api/v1/templates/{v2.GetProperty("id").GetString()}", Body("Renamed"), v2.GetProperty("version").GetInt32().ToString())));
        Assert.Equal("TemplateNameTaken", await Guard(await Send(rte, HttpMethod.Post, "/api/v1/templates", Body("Quarterly"))));   // the name belongs to the template and its versions

        // the draft is edited, and its changes against version 1 are listed
        v2 = await Edit(rte, v2, Body(freezeOffset: 4, milestones: [new { name = "Code complete", offsetDays = -14 }, new { name = "UAT sign-off", offsetDays = -3 }, new { name = "Hypercare exit", offsetDays = 7 }, new { name = "Launch review", offsetDays = 1 }]));
        var changes = v2.GetProperty("changes").EnumerateArray().ToList();
        Assert.Contains(changes, c => c.GetProperty("area").GetString() == "Milestone" && c.GetProperty("kind").GetString() == "Added" && c.GetProperty("subject").GetString() == "Launch review");
        Assert.Contains(changes, c => c.GetProperty("area").GetString() == "Gate" && c.GetProperty("kind").GetString() == "Changed" && c.GetProperty("subject").GetString() == "Code Freeze");
        Assert.DoesNotContain(changes, c => c.GetProperty("compliance").GetBoolean());

        // approving it: version 1 retires in the same transaction, the release type starts from version 2, and both leave audit rows
        var approved2 = await Approve(rm, v2);
        var v2Id = approved2.GetProperty("id").GetString()!;
        Assert.Equal("Retired", Scalar(f, $"SELECT Status FROM TrainTemplates WHERE Id='{v1Id}'"));
        Assert.Equal("", Scalar(f, $"SELECT IFNULL(ApprovedByUserId,'') FROM TrainTemplates WHERE Id='{v1Id}'"));
        Assert.Equal(1, Audits(f, "TrainTemplate", "Retire", v1Id));
        Assert.Contains(v2Id, Scalar(f, $"SELECT AfterJson FROM AuditEvents WHERE EntityType='TrainTemplate' AND Action='Retire' AND EntityId='{v1Id}'"));
        Assert.Equal(v2Id, Scalar(f, $"SELECT DefaultTemplateId FROM ReleaseTypes WHERE Id='{typeId}'"));
        Assert.Equal(1, Audits(f, "ReleaseType", "DefaultTemplateMoved", typeId));
        Assert.Equal("2", Scalar(f, $"SELECT Version FROM ReleaseTypes WHERE Id='{typeId}'"));
        Assert.Equal(new[] { 2, 1 }, (await Get(rm, $"/api/v1/templates/{v2Id}")).GetProperty("versions").EnumerateArray().Select(v => v.GetProperty("versionNo").GetInt32()).ToArray());

        // the release made from version 1 is exactly as it was; a new one has version 2's plan; version 1 can no longer start one
        Assert.Equal(v1Id, Scalar(f, $"SELECT TemplateId FROM ReleaseTrains WHERE Id='{oldRelease}'"));
        Assert.Equal(gatesBefore, Scalar(f, $"SELECT group_concat(GateName || ':' || OffsetDays || ':' || TemplateLocked, ',') FROM (SELECT * FROM StageGates WHERE ReleaseTrainId='{oldRelease}' ORDER BY SequenceOrder)"));
        Assert.Equal("3", Scalar(f, $"SELECT COUNT(*) FROM TrainMilestones WHERE ReleaseTrainId='{oldRelease}'"));
        var newRelease = await MakeRelease(rm, v2Id, "R27.10 Made from version 2", Target(70), typeId);
        Assert.Equal("4", Scalar(f, $"SELECT COUNT(*) FROM TrainMilestones WHERE ReleaseTrainId='{newRelease}'"));
        Assert.Equal("4", Scalar(f, $"SELECT OffsetDays FROM StageGates WHERE ReleaseTrainId='{newRelease}' AND GateName='Code Freeze'"));
        Assert.Equal("TemplateNotApproved", await Guard(await Send(rm, HttpMethod.Post, "/api/v1/trains", new { title = "R27.11 From the old one", targetReleaseDate = Target(), templateId = v1Id, releaseTypeId = typeId })));

        // version 3 can be started from version 2, and the list shows every version, newest first within a name
        var list = await Get(rm, "/api/v1/templates");
        var quarterly = list.EnumerateArray().Where(t => t.GetProperty("name").GetString() == "Quarterly").Select(t => t.GetProperty("versionNo").GetInt32()).ToArray();
        Assert.Equal(new[] { 2, 1 }, quarterly);
        Assert.Equal(HttpStatusCode.OK, (await Send(rte, HttpMethod.Post, $"/api/v1/templates/{v2Id}:new-version", null, approved2.GetProperty("version").GetInt32().ToString())).StatusCode);
    }

    [Fact]
    public async Task A_new_version_follows_the_roles_and_If_Match()
    {
        var (f, rte, rm, _, gov) = await Setup(); using var _f = f;
        var viewer = await As(f, Roles.Viewer, "v@x.com");
        var v1 = await Approve(gov, await Create(rte, Body()));
        var id = v1.GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(viewer, HttpMethod.Post, $"/api/v1/templates/{id}:new-version", null, "1")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(gov, HttpMethod.Post, $"/api/v1/templates/{id}:new-version", null, "1")).StatusCode);
        var stale = await Send(rte, HttpMethod.Post, $"/api/v1/templates/{id}:new-version", null, (v1.GetProperty("version").GetInt32() - 1).ToString());
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(v1.GetProperty("version").GetInt32(), (await Json(stale)).GetProperty("current").GetProperty("version").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await Send(rm, HttpMethod.Post, "/api/v1/templates/nope:new-version", null, "1")).StatusCode);
        Assert.Equal(0, Audits(f, "TrainTemplate", "NewVersion"));
        Assert.Equal(HttpStatusCode.OK, (await Send(rm, HttpMethod.Post, $"/api/v1/templates/{id}:new-version", null, v1.GetProperty("version").GetInt32().ToString())).StatusCode);
    }

    // ---- locked Compliance gates (Q8)

    [Fact]
    public async Task A_Compliance_gate_from_an_approved_template_cannot_be_removed_or_downgraded_on_the_release()
    {
        var (f, rte, rm, _, gov) = await Setup(); using var _f = f;
        var approved = await Approve(gov, await Create(rte, Body()));
        var trainId = await MakeRelease(rm, approved.GetProperty("id").GetString()!, "R27.12 Locked", Target());
        var cab = Scalar(f, $"SELECT Id FROM StageGates WHERE ReleaseTrainId='{trainId}' AND GateName='CAB'");
        var freeze = Scalar(f, $"SELECT Id FROM StageGates WHERE ReleaseTrainId='{trainId}' AND GateName='Code Freeze'");
        Assert.Equal("1", Scalar(f, $"SELECT TemplateLocked FROM StageGates WHERE Id='{cab}'"));
        Assert.Equal("0", Scalar(f, $"SELECT TemplateLocked FROM StageGates WHERE Id='{freeze}'"));
        string Version(string gate) => Scalar(f, $"SELECT Version FROM StageGates WHERE Id='{gate}'");

        // the gate shows that it is locked
        var detail = await Get(rm, $"/api/v1/gates/{cab}");
        Assert.True(detail.GetProperty("templateLocked").GetBoolean());
        Assert.False((await Get(rm, $"/api/v1/gates/{freeze}")).GetProperty("templateLocked").GetBoolean());
        var row = (await Get(rm, $"/api/v1/trains/{trainId}")).GetProperty("gates").EnumerateArray().Single(g => g.GetProperty("id").GetString() == cab);
        Assert.True(row.GetProperty("templateLocked").GetBoolean());

        // no downgrade, no removal: the service says why
        var downgrade = await Send(rm, new HttpMethod("PATCH"), $"/api/v1/gates/{cab}", new { gateClass = "Standard" }, Version(cab));
        Assert.Equal("GateLockedByTemplate", await Guard(downgrade));
        Assert.Contains("request a waiver", (await Json(downgrade)).GetProperty("message").GetString());
        Assert.Equal("GateLockedByTemplate", await Guard(await Send(rm, HttpMethod.Delete, $"/api/v1/gates/{cab}", null, Version(cab))));
        Assert.Equal("Compliance", Scalar(f, $"SELECT GateClass FROM StageGates WHERE Id='{cab}'"));
        Assert.Equal(0, Audits(f, "StageGate", "Remove"));

        // the rest of the gate is the planner's: owner, offset, a change to the same class
        var owner = UserId(f, "rm@x.com");
        Assert.Equal(HttpStatusCode.OK, (await Send(rm, new HttpMethod("PATCH"), $"/api/v1/gates/{cab}", new { gateClass = "Compliance", offsetDays = 2, ownerUserId = owner }, Version(cab))).StatusCode);
        Assert.Equal("1", Scalar(f, $"SELECT TemplateLocked FROM StageGates WHERE Id='{cab}'"));

        // other gates of the template are not locked: they can be changed and removed as before
        Assert.Equal(HttpStatusCode.OK, (await Send(rm, new HttpMethod("PATCH"), $"/api/v1/gates/{freeze}", new { gateClass = "Compliance" }, Version(freeze))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(rm, new HttpMethod("PATCH"), $"/api/v1/gates/{freeze}", new { gateClass = "Standard" }, Version(freeze))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(rm, HttpMethod.Delete, $"/api/v1/gates/{freeze}", null, Version(freeze))).StatusCode);

        // a gate the planner adds is theirs, even a Compliance one
        var added = await Json(await Send(rm, HttpMethod.Post, $"/api/v1/trains/{trainId}/gates", new { gateName = "Extra review", gateClass = "Compliance", offsetDays = 3, requiredBeforeStatus = "Executing", ownerUserId = owner }));
        var addedId = added.GetProperty("id").GetString()!;
        Assert.Equal("0", Scalar(f, $"SELECT TemplateLocked FROM StageGates WHERE Id='{addedId}'"));
        Assert.Equal(HttpStatusCode.OK, (await Send(rm, HttpMethod.Delete, $"/api/v1/gates/{addedId}", null, Version(addedId))).StatusCode);

        // any other writer meets the trigger
        var e1 = Assert.Throws<SqliteException>(() => Sql(f, $"UPDATE StageGates SET GateClass='Standard' WHERE Id='{cab}'"));
        Assert.Contains("cannot be downgraded", e1.Message);
        var e2 = Assert.Throws<SqliteException>(() => Sql(f, $"UPDATE StageGates SET TemplateLocked=0 WHERE Id='{cab}'"));
        Assert.Contains("cannot be downgraded or unlocked", e2.Message);
        var e3 = Assert.Throws<SqliteException>(() => Sql(f, $"DELETE FROM StageGates WHERE Id='{cab}'"));
        Assert.Contains("cannot be removed", e3.Message);
    }

    [Fact]
    public async Task A_waiver_is_still_the_way_to_skip_a_locked_gate_and_the_lock_does_not_stop_a_release_being_deleted_by_cascade()
    {
        var (f, rte, rm, _, gov) = await Setup(); using var _f = f;
        var approved = await Approve(gov, await Create(rte, Body()));
        var trainId = await MakeRelease(rm, approved.GetProperty("id").GetString()!, "R27.13 Cascade", Target());
        Sql(f, $"DELETE FROM ReleaseTrains WHERE Id='{trainId}'");   // the cascade removes the locked gate with its release
        Assert.Equal("0", Scalar(f, $"SELECT COUNT(*) FROM StageGates WHERE ReleaseTrainId='{trainId}'"));
    }

    [Fact]
    public async Task With_LockComplianceGates_off_the_gates_of_a_new_release_are_not_locked()
    {
        var (f, rte, rm, _, gov) = await Setup(new() { ["Templates:LockComplianceGates"] = "false" }); using var _f = f;
        var approved = await Approve(gov, await Create(rte, Body()));
        var trainId = await MakeRelease(rm, approved.GetProperty("id").GetString()!, "R27.14 Unlocked", Target());
        Assert.Equal("0", Scalar(f, $"SELECT COUNT(*) FROM StageGates WHERE ReleaseTrainId='{trainId}' AND TemplateLocked=1"));
        var cab = Scalar(f, $"SELECT Id FROM StageGates WHERE ReleaseTrainId='{trainId}' AND GateName='CAB'");
        Assert.Equal(HttpStatusCode.OK, (await Send(rm, new HttpMethod("PATCH"), $"/api/v1/gates/{cab}", new { gateClass = "Standard" }, Scalar(f, $"SELECT Version FROM StageGates WHERE Id='{cab}'"))).StatusCode);
    }

    [Fact]
    public async Task A_copy_of_a_release_keeps_the_locks_of_the_original()
    {
        var (f, rte, rm, _, gov) = await Setup(); using var _f = f;
        var approved = await Approve(gov, await Create(rte, Body()));
        var trainId = await MakeRelease(rm, approved.GetProperty("id").GetString()!, "R27.15 Original", Target());
        var clone = await Json(await Send(rm, HttpMethod.Post, $"/api/v1/trains/{trainId}:clone", new { title = "R27.16 Copy", targetReleaseDate = Target(90) }));
        var copyId = clone.GetProperty("id").GetString()!;
        Assert.Equal("1", Scalar(f, $"SELECT COUNT(*) FROM StageGates WHERE ReleaseTrainId='{copyId}' AND TemplateLocked=1"));
        var cab = Scalar(f, $"SELECT Id FROM StageGates WHERE ReleaseTrainId='{copyId}' AND GateName='CAB'");
        Assert.Equal("GateLockedByTemplate", await Guard(await Send(rm, HttpMethod.Delete, $"/api/v1/gates/{cab}", null, Scalar(f, $"SELECT Version FROM StageGates WHERE Id='{cab}'"))));
    }

    [Fact]
    public async Task The_Gates_file_import_does_not_downgrade_a_locked_gate()
    {
        var (f, rte, rm, _, gov) = await Setup(); using var _f = f;
        var approved = await Approve(gov, await Create(rte, Body()));
        var trainId = await MakeRelease(rm, approved.GetProperty("id").GetString()!, "R27.17 Import", Target());
        Assert.Equal("1", Scalar(f, $"SELECT COUNT(*) FROM StageGates WHERE ReleaseTrainId='{trainId}' AND TemplateLocked=1"));
        var export = await rm.GetStringAsync($"/api/v1/exports/gates.csv?train={trainId}");
        var line = export.Split('\n').First(l => l.Contains("CAB"));
        var downgraded = export.Replace(line, line.Replace("Compliance", "Standard"));
        var preview = await rm.PostAsync("/api/v1/imports/Gates:preview?mode=Upsert&fileName=gates.csv", new StringContent(downgraded, new System.Text.UTF8Encoding(false), "text/csv"));
        var text = await preview.Content.ReadAsStringAsync();
        Assert.Contains("Compliance gate from the approved template and cannot be made Standard", text);
        Assert.Equal("Compliance", Scalar(f, $"SELECT GateClass FROM StageGates WHERE ReleaseTrainId='{trainId}' AND GateName='CAB'"));
    }
}
