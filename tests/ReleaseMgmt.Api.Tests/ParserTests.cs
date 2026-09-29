using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ReleaseMgmt.Domain.Common;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>REOS-28: :parse stores a preview, :commit inserts in one transaction; errors block, stale previews are 409, decertify needs an acknowledgement.</summary>
public class ParserTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
    private static Task<HttpResponseMessage> Parse(HttpClient c, string text, string? gate = null) => c.PostAsJsonAsync("/api/v1/trains/t1/tasks:parse", new { text, defaultGateId = gate });
    private static Task<HttpResponseMessage> Commit(HttpClient c, string preview, bool ack = false) => c.PostAsJsonAsync("/api/v1/trains/t1/tasks:commit", new { previewId = preview, acknowledgeDecertify = ack });

    private static async Task<(ApiFactory F, HttpClient Rte, string Rid, string Gid)> Setup()
    {
        var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        await As(f, Roles.GovernanceOfficer, "gov@x.com");
        var rid = UserId(f, "rte@x.com"); var gid = UserId(f, "gov@x.com");
        SeedTrain(f, rid, gid);
        Sql(f, $@"INSERT INTO Teams(Id,Handle,Name) VALUES('team1','desk','Support desk');
                  INSERT INTO BundledProducts(Id,ReleaseTrainId,ProductName,VersionTag,ProjectCode) VALUES('p1','t1','Payments API','4.5.0','PAY');");
        return (f, rte, rid, gid);
    }

    [Fact]
    public async Task Parse_then_commit_inserts_every_task_once_with_owners_products_and_order()
    {
        var (f, c, rid, gid) = await Setup(); using var _ = f;
        var p = await Json(await Parse(c, "# Code Freeze\n- Tag repos again @desk [Payments API]\n- Freeze the wiki\n# compliance sign-off\n- SOX evidence pack @rte@x.com"));
        Assert.Equal(0, p.GetProperty("errorCount").GetInt32());
        Assert.Equal(3, p.GetProperty("tasks").GetArrayLength());
        Assert.Equal(1, p.GetProperty("warningCount").GetInt32());                                   // "Freeze the wiki" has no @owner: gate owner fallback is a warning
        var version = p.GetProperty("trainVersion").GetInt32();
        Assert.Equal("0", Scalar(f, "SELECT COUNT(*) FROM ChecklistTasks WHERE TaskDescription='Freeze the wiki'"));   // parsing writes no tasks
        Assert.Equal("1", Scalar(f, $"SELECT COUNT(*) FROM ParsePreviews WHERE Id='{p.GetProperty("previewId").GetString()}' AND ErrorCount=0"));

        var done = await Commit(c, p.GetProperty("previewId").GetString()!);
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.Equal(3, (await Json(done)).GetProperty("inserted").GetInt32());

        Assert.Equal("desk", Scalar(f, "SELECT t.Handle FROM ChecklistTasks c JOIN Teams t ON t.Id=c.OwnerTeamId WHERE c.TaskDescription='Tag repos again'"));
        Assert.Equal("p1", Scalar(f, "SELECT BundledProductId FROM ChecklistTasks WHERE TaskDescription='Tag repos again'"));
        Assert.Equal(rid, Scalar(f, "SELECT OwnerUserId FROM ChecklistTasks WHERE TaskDescription='Freeze the wiki'"));       // gate owner fallback
        Assert.Equal(rid, Scalar(f, "SELECT OwnerUserId FROM ChecklistTasks WHERE TaskDescription='SOX evidence pack'"));       // @rte@x.com matched by email
        Assert.Equal("2,3", Scalar(f, "SELECT group_concat(SequenceOrder) FROM (SELECT SequenceOrder FROM ChecklistTasks WHERE StageGateId='g1' AND TaskDescription<>'Tag repos' ORDER BY SequenceOrder)"));   // continues after the existing task
        Assert.Equal((version + 1).ToString(), Scalar(f, "SELECT Version FROM ReleaseTrains WHERE Id='t1'"));                   // the train moved
        Assert.Equal("1", Scalar(f, "SELECT COUNT(*) FROM AuditEvents WHERE Action='BulkCommit' AND ReleaseTrainId='t1'"));
        Assert.NotEqual("", Scalar(f, $"SELECT CommittedAt FROM ParsePreviews WHERE Id='{p.GetProperty("previewId").GetString()}'"));
    }

    [Fact]
    public async Task Any_error_blocks_the_commit_and_the_preview_lists_line_numbers_and_closest_matches()
    {
        var (f, c, _, _) = await Setup(); using var _f = f;
        var r = await Parse(c, "# Code Freeze\n- fine @desk\n- bad owner @dsk\n- bad product [Payments Ap]\nnot a task");
        var p = await Json(r);
        Assert.Equal(3, p.GetProperty("errorCount").GetInt32());
        var issues = p.GetProperty("issues").EnumerateArray().ToList();
        Assert.Equal([3, 4, 5], issues.Select(i => i.GetProperty("line").GetInt32()).ToArray());
        Assert.Contains("@desk", issues[0].GetProperty("suggestions").EnumerateArray().Select(x => x.GetString()));
        Assert.Contains("Payments API", issues[1].GetProperty("suggestions").EnumerateArray().Select(x => x.GetString()));

        var commit = await Commit(c, p.GetProperty("previewId").GetString()!);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, commit.StatusCode);
        Assert.Equal("PreviewHasErrors", (await Json(commit)).GetProperty("guard").GetString());
        Assert.Equal("1", Scalar(f, "SELECT COUNT(*) FROM ChecklistTasks WHERE StageGateId='g1'"));                                // only the seeded task: nothing inserted, not even the good line
    }

    [Fact]
    public async Task A_preview_taken_at_an_older_train_version_is_409_and_so_is_a_second_preview_after_a_commit()
    {
        var (f, c, _, _) = await Setup(); using var _f = f;
        var a = await Json(await Parse(c, "# Code Freeze\n- from preview A @desk"));
        var b = await Json(await Parse(c, "# Code Freeze\n- from preview B @desk"));
        Assert.Equal(HttpStatusCode.OK, (await Commit(c, a.GetProperty("previewId").GetString()!)).StatusCode);
        var stale = await Commit(c, b.GetProperty("previewId").GetString()!);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.True((await Json(stale)).GetProperty("current").GetProperty("version").GetInt32() > b.GetProperty("trainVersion").GetInt32());
        Assert.Equal("0", Scalar(f, "SELECT COUNT(*) FROM ChecklistTasks WHERE TaskDescription='from preview B'"));

        // the same thing when the train is changed some other way (the target date moves)
        var c1 = await Json(await Parse(c, "# Code Freeze\n- from preview C @desk"));
        var patch = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/trains/t1") { Content = JsonContent.Create(new { targetReleaseDate = "2026-11-06" }) };
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(patch)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Commit(c, c1.GetProperty("previewId").GetString()!)).StatusCode);
    }

    [Fact]
    public async Task Expired_committed_and_foreign_previews_are_refused()
    {
        var (f, c, _, _) = await Setup(); using var _f = f;
        var other = await As(f, Roles.RTE, "other@x.com");
        var p = (await Json(await Parse(c, "# Code Freeze\n- once @desk"))).GetProperty("previewId").GetString()!;
        Assert.Equal(HttpStatusCode.NotFound, (await Commit(other, p)).StatusCode);                                              // someone else's preview is not visible
        Assert.Equal("1800", Scalar(f, $"SELECT CAST(strftime('%s',ExpiresAt) AS INTEGER) - CAST(strftime('%s',CreatedAt) AS INTEGER) FROM ParsePreviews WHERE Id='{p}'"));   // 30 minutes

        Sql(f, $"UPDATE ParsePreviews SET ExpiresAt='2020-01-01T00:00:00Z' WHERE Id='{p}'");
        var expired = await Commit(c, p);
        Assert.Equal("PreviewExpired", (await Json(expired)).GetProperty("guard").GetString());
        Sql(f, $"UPDATE ParsePreviews SET ExpiresAt='2099-01-01T00:00:00Z' WHERE Id='{p}'");

        Assert.Equal(HttpStatusCode.OK, (await Commit(c, p)).StatusCode);
        var again = await Commit(c, p);
        Assert.Equal("PreviewCommitted", (await Json(again)).GetProperty("guard").GetString());                                   // no double insert
        Assert.Equal("1", Scalar(f, "SELECT COUNT(*) FROM ChecklistTasks WHERE TaskDescription='once'"));
    }

    [Fact]
    public async Task Committing_into_a_certified_gate_needs_the_decertify_acknowledgement()
    {
        var (f, c, _, _) = await Setup(); using var _f = f;
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/v1/tasks/k1:complete", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/v1/gates/g1:start", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/v1/gates/g1:certify", new { })).StatusCode);
        Assert.Equal("Certified", Scalar(f, "SELECT Status FROM StageGates WHERE Id='g1'"));

        var p = await Json(await Parse(c, "# Code Freeze\n- one more thing @desk"));
        Assert.Equal(["Code Freeze"], p.GetProperty("decertifiesGates").EnumerateArray().Select(x => x.GetString()!).ToArray());     // the preview already says so
        var id = p.GetProperty("previewId").GetString()!;

        var refused = await Commit(c, id);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("DecertifyNotAcknowledged", (await Json(refused)).GetProperty("guard").GetString());
        Assert.Equal("Certified", Scalar(f, "SELECT Status FROM StageGates WHERE Id='g1'"));                                          // nothing happened
        Assert.Equal("0", Scalar(f, "SELECT COUNT(*) FROM ChecklistTasks WHERE TaskDescription='one more thing'"));

        Assert.Equal(HttpStatusCode.OK, (await Commit(c, id, ack: true)).StatusCode);
        Assert.NotEqual("Certified", Scalar(f, "SELECT Status FROM StageGates WHERE Id='g1'"));                                       // the trigger decertified it
        Assert.True(int.Parse(Scalar(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='StageGate' AND EntityId='g1'")) >= 3);    // start, certify and the trigger's own decertify row
    }

    [Fact]
    public async Task The_gate_chosen_in_the_drawer_takes_lines_before_any_heading_and_only_planners_can_use_it()
    {
        var (f, c, _, _) = await Setup(); using var _f = f;
        var p = await Json(await Parse(c, "- goes to the chosen gate @desk", gate: "g2"));
        Assert.Equal(0, p.GetProperty("errorCount").GetInt32());
        Assert.Equal("Compliance Sign-off", p.GetProperty("tasks")[0].GetProperty("gateName").GetString());
        var nogate = await Json(await Parse(c, "- no gate anywhere @desk"));
        Assert.Equal("NoTargetGate", nogate.GetProperty("issues")[0].GetProperty("code").GetString());

        var viewer = await As(f, Roles.Viewer, "v@x.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await Parse(viewer, "# Code Freeze\n- x @desk")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/api/v1/trains/nope/tasks:parse", new { text = "- x" })).StatusCode);
    }
}
