using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ReleaseMgmt.Domain.Common;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>REOS-80: POST /trains (blank or from an Approved template) and POST /trains/{id}:clone over HTTP: roles, refusals, what is created, audit.</summary>
public class TrainCreationTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private static async Task<string> Guard(HttpResponseMessage r)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        return (await Json(r)).GetProperty("guard").GetString()!;
    }

    // Far enough ahead that every gate offset lands after today, whatever day the suite runs.
    private static string Target(int days = 60) => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(days).ToString("yyyy-MM-dd");

    private static async Task<string> ApprovedTemplate(HttpClient rte, HttpClient rm, string name = "Quarterly")
    {
        var r = await rte.PostAsJsonAsync("/api/v1/templates", new
        {
            name, defaultRiskTier = "High",
            gates = new[] { new { gateName = "Code Freeze", gateClass = "Standard", offsetDays = 5, requiredBeforeStatus = "Gated" }, new { gateName = "CAB", gateClass = "Compliance", offsetDays = 1, requiredBeforeStatus = "Executing" } },
            steps = new[] { new { stepCode = "PRE-1", section = "PreCheck", title = "Health check", offsetMinutes = -30, plannedDurationMin = 15 } },
            schedule = Array.Empty<object>(),
        });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var id = (await Json(r)).GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await rm.PostAsync($"/api/v1/templates/{id}:approve", null)).StatusCode);
        return id;
    }

    [Fact]
    public async Task Blank_train_is_created_by_an_RTE_and_shows_in_the_Stream()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var r = await rte.PostAsJsonAsync("/api/v1/trains", new { title = "R27.01 New year", targetReleaseDate = Target(), riskTier = "Low", products = new[] { new { productName = "Payments API", versionTag = "6.0.0", projectCode = "PAY" } } });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await Json(r);
        var id = j.GetProperty("id").GetString()!;
        Assert.Equal("Planning", j.GetProperty("status").GetString());
        Assert.Equal("Blank", j.GetProperty("source").GetString());
        Assert.Equal(1, j.GetProperty("created").GetProperty("products").GetInt32());
        Assert.Equal(UserId(f, "rte@x.com"), Scalar(f, $"SELECT LastChangedByUserId FROM ReleaseTrains WHERE Id='{id}'"));
        Assert.Equal("2", Scalar(f, $"SELECT COUNT(*) FROM AuditEvents WHERE ReleaseTrainId='{id}' AND Action='Create'"));
        var stream = await rte.GetFromJsonAsync<JsonElement>("/api/v1/trains");
        Assert.Contains(stream.EnumerateArray(), x => x.GetProperty("id").GetString() == id && x.GetProperty("status").GetString() == "Planning");
        Assert.Equal(HttpStatusCode.OK, (await rte.GetAsync($"/api/v1/trains/{id}")).StatusCode);
    }

    [Fact]
    public async Task From_an_Approved_template_the_gates_come_with_due_dates_and_a_Release_Manager_may_create()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var rm = await As(f, Roles.ReleaseManager, "rm@x.com");
        var tpl = await ApprovedTemplate(rte, rm);
        var target = Target();
        var start = DateOnly.Parse(target).ToString("yyyy-MM-dd") + "T08:00:00Z";
        var end = DateOnly.Parse(target).ToString("yyyy-MM-dd") + "T12:00:00Z";
        var r = await rm.PostAsJsonAsync("/api/v1/trains", new { title = "R27.02 From template", targetReleaseDate = target, templateId = tpl, windowStartsAt = start, windowEndsAt = end });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var id = (await Json(r)).GetProperty("id").GetString()!;
        Assert.Equal(tpl, Scalar(f, $"SELECT TemplateId FROM ReleaseTrains WHERE Id='{id}'"));
        Assert.Equal("High", Scalar(f, $"SELECT RiskTier FROM ReleaseTrains WHERE Id='{id}'"));
        var train = await rm.GetFromJsonAsync<JsonElement>($"/api/v1/trains/{id}");
        var gates = train.GetProperty("gates").EnumerateArray().ToList();
        Assert.Equal(["Code Freeze", "CAB"], gates.Select(g => g.GetProperty("name").GetString()));
        Assert.All(gates, g => Assert.Equal("Pending", g.GetProperty("status").GetString()));
        Assert.Equal("rm", gates[0].GetProperty("ownerName").GetString());   // no team on the template gate: the creator (Q-080a)
        Assert.Equal(DateOnly.Parse(target).ToString("yyyy-MM-dd"), Scalar(f, $"SELECT substr(PlannedStartAt,1,10) FROM RunbookSteps WHERE ReleaseTrainId='{id}'"));
        Assert.Equal("07:30:00", Scalar(f, $"SELECT substr(PlannedStartAt,12,8) FROM RunbookSteps WHERE ReleaseTrainId='{id}'"));
    }

    [Fact]
    public async Task Refusals_name_their_guard_and_write_nothing()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var rm = await As(f, Roles.ReleaseManager, "rm@x.com");
        var draft = (await Json(await rte.PostAsJsonAsync("/api/v1/templates", new { name = "Draft one", gates = new[] { new { gateName = "G", offsetDays = 1, requiredBeforeStatus = "Gated" } } }))).GetProperty("id").GetString()!;
        Assert.Equal("TemplateNotApproved", await Guard(await rte.PostAsJsonAsync("/api/v1/trains", new { title = "R1", targetReleaseDate = Target(), templateId = draft })));

        Assert.Equal(HttpStatusCode.OK, (await rte.PostAsJsonAsync("/api/v1/trains", new { title = "R27.03 Taken", targetReleaseDate = Target() })).StatusCode);
        Assert.Equal("TrainTitleTaken", await Guard(await rte.PostAsJsonAsync("/api/v1/trains", new { title = "r27.03 taken", targetReleaseDate = Target() })));
        Assert.Equal("TrainInvalid", await Guard(await rte.PostAsJsonAsync("/api/v1/trains", new { title = "R2", targetReleaseDate = "not a date" })));
        Assert.Equal("TrainInvalid", await Guard(await rte.PostAsJsonAsync("/api/v1/trains", new { title = "R2", targetReleaseDate = Target(-10) })));   // in the past
        Assert.Equal("TrainInvalid", await Guard(await rte.PostAsJsonAsync("/api/v1/trains", new { title = "R2", targetReleaseDate = Target(), windowStartsAt = "2027-01-01T10:00:00Z", windowEndsAt = "2027-01-01T09:00:00Z" })));
        Assert.Equal("TrainInvalid", await Guard(await rte.PostAsync("/api/v1/trains", JsonContent.Create(new { }))));
        Assert.Equal(HttpStatusCode.NotFound, (await rte.PostAsJsonAsync("/api/v1/trains", new { title = "R2", targetReleaseDate = Target(), templateId = "nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rte.PostAsJsonAsync("/api/v1/trains/nope:clone", new { title = "R2", targetReleaseDate = Target() })).StatusCode);
        Assert.Equal("1", Scalar(f, "SELECT COUNT(*) FROM ReleaseTrains"));
        Assert.Equal("1", Scalar(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='ReleaseTrain'"));
    }

    [Fact]
    public async Task Viewer_and_Governance_Officer_are_refused_and_anonymous_is_401()
    {
        using var f = new ApiFactory();
        var viewer = await As(f, Roles.Viewer, "v@x.com");
        var gov = await As(f, Roles.GovernanceOfficer, "gov@x.com");
        var body = new { title = "R27.04", targetReleaseDate = Target() };
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/v1/trains", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await gov.PostAsJsonAsync("/api/v1/trains", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/v1/trains/x:clone", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().PostAsJsonAsync("/api/v1/trains", body)).StatusCode);
        Assert.Equal("0", Scalar(f, "SELECT COUNT(*) FROM ReleaseTrains"));
    }

    [Fact]
    public async Task Clone_copies_the_plan_with_open_tasks_and_stamps_the_source()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var gov = await As(f, Roles.GovernanceOfficer, "gov@x.com");
        SeedTrain(f, UserId(f, "rte@x.com"), UserId(f, "gov@x.com"));
        Sql(f, $"UPDATE ChecklistTasks SET IsCompleted=1, CompletedAt='2026-10-01T00:00:00Z', CompletedByUserId='{UserId(f, "rte@x.com")}' WHERE Id='k1';");
        var r = await rte.PostAsJsonAsync("/api/v1/trains/t1:clone", new { title = "R26.10 again", targetReleaseDate = Target() });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await Json(r);
        var id = j.GetProperty("id").GetString()!;
        Assert.Equal("Clone", j.GetProperty("source").GetString());
        Assert.Equal("t1", Scalar(f, $"SELECT ClonedFromTrainId FROM ReleaseTrains WHERE Id='{id}'"));
        Assert.Equal(Scalar(f, "SELECT COUNT(*) FROM StageGates WHERE ReleaseTrainId='t1'"), Scalar(f, $"SELECT COUNT(*) FROM StageGates WHERE ReleaseTrainId='{id}'"));
        Assert.Equal("0", Scalar(f, $"SELECT COUNT(*) FROM ChecklistTasks t JOIN StageGates g ON g.Id=t.StageGateId WHERE g.ReleaseTrainId='{id}' AND t.IsCompleted=1"));
        Assert.Equal("0", Scalar(f, $"SELECT COUNT(*) FROM StageGates WHERE ReleaseTrainId='{id}' AND Status<>'Pending'"));
        Assert.Equal("Clone", Scalar(f, $"SELECT json_extract(AfterJson,'$.source') FROM AuditEvents WHERE EntityType='ReleaseTrain' AND EntityId='{id}' AND Action='Create'"));
        Assert.Equal(HttpStatusCode.Forbidden, (await gov.PostAsJsonAsync("/api/v1/trains/t1:clone", new { title = "R26.10 gov", targetReleaseDate = Target() })).StatusCode);
    }
}
