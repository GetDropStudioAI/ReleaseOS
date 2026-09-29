using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using ReleaseMgmt.Api;
using ReleaseMgmt.Domain.Common;

namespace ReleaseMgmt.Api.Tests;

/// <summary>HTTP contract for state changes: 422 names the guard, 409 returns the current row, roles are enforced (M1).</summary>
public class LifecycleContractTests
{
    private static async Task<HttpClient> As(ApiFactory f, string role, string email)
    {
        var c = f.CreateClient();
        (await c.PostAsJsonAsync("/auth/dev-login", new { email, name = email.Split('@')[0], role })).EnsureSuccessStatusCode();
        return c;
    }

    private static string UserId(ApiFactory f, string email)
    {
        using var c = new SqliteConnection($"Data Source={f.DbPath};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Id FROM Users WHERE Email = $e";
        cmd.Parameters.AddWithValue("$e", email);
        return (string)cmd.ExecuteScalar()!;
    }

    private static void Sql(ApiFactory f, string sql)
    {
        using var c = new SqliteConnection($"Data Source={f.DbPath};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Train t1 (High risk, target 2026-10-30) with g1 Code Freeze (Standard, owned by rteId) and g2 Compliance Sign-off (owned by govId), one open task each.</summary>
    private static void SeedTrain(ApiFactory f, string rteId, string govId) => Sql(f, $@"
        INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CreatedAt,UpdatedAt) VALUES('t1','R26.10','2026-10-30','High','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z');
        INSERT INTO StageGates(Id,ReleaseTrainId,GateName,GateClass,SequenceOrder,OffsetDays,DueOn,RequiredBeforeStatus,OwnerUserId,Status,LastChangedByUserId) VALUES
          ('g1','t1','Code Freeze','Standard',1,5,'2026-10-23','Gated','{rteId}','Pending','{rteId}'),
          ('g2','t1','Compliance Sign-off','Compliance',2,2,'2026-10-28','Executing','{govId}','InProgress','{govId}');
        INSERT INTO ChecklistTasks(Id,StageGateId,TaskDescription,OwnerUserId,SequenceOrder) VALUES('k1','g1','Tag repos','{rteId}',1),('k2','g2','SOX evidence','{govId}',1);");

    private static async Task<(HttpStatusCode Status, JsonElement Body)> Post(HttpClient c, string url, object? body = null, string? ifMatch = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body ?? new { }) };
        if (ifMatch is not null) req.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        var r = await c.SendAsync(req);
        var text = await r.Content.ReadAsStringAsync();
        return (r.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement);
    }

    [Fact]
    public async Task Illegal_train_transition_returns_422_naming_the_guard()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        SeedTrain(f, UserId(f, "rte@x.com"), UserId(f, "rte@x.com"));
        var (status, body) = await Post(rte, "/api/v1/trains/t1:advance", new { to = "Executing" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("IllegalTransition", body.GetProperty("guard").GetString());
    }

    [Fact]
    public async Task Gate_lockout_422_lists_the_blocking_gates()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        SeedTrain(f, UserId(f, "rte@x.com"), UserId(f, "rte@x.com"));
        var (status, body) = await Post(rte, "/api/v1/trains/t1:advance", new { to = "Gated" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("GateLockout", body.GetProperty("guard").GetString());
        Assert.Equal(["Code Freeze"], body.GetProperty("gates").EnumerateArray().Select(x => x.GetString()!).ToArray());
    }

    [Fact]
    public async Task Illegal_gate_transition_returns_422_naming_the_guard()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        SeedTrain(f, UserId(f, "rte@x.com"), UserId(f, "rte@x.com"));
        var (status, body) = await Post(rte, "/api/v1/gates/g1:certify"); // Pending -> Certified
        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("IllegalGateTransition", body.GetProperty("guard").GetString());
    }

    [Fact]
    public async Task Stale_If_Match_returns_409_with_the_current_row()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        SeedTrain(f, UserId(f, "rte@x.com"), UserId(f, "rte@x.com"));
        Assert.Equal(HttpStatusCode.OK, (await Post(rte, "/api/v1/gates/g1:start", ifMatch: "1")).Status);
        var (status, body) = await Post(rte, "/api/v1/gates/g1:fail", ifMatch: "\"1\"");
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal(2, body.GetProperty("current").GetProperty("version").GetInt32());
        Assert.Equal("InProgress", body.GetProperty("current").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Successful_write_returns_the_new_row_with_bumped_version()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        SeedTrain(f, UserId(f, "rte@x.com"), UserId(f, "rte@x.com"));
        var (status, body) = await Post(rte, "/api/v1/gates/g1:start");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("InProgress", body.GetProperty("status").GetString());
        Assert.Equal(2, body.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Missing_records_return_404()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        Assert.Equal(HttpStatusCode.NotFound, (await Post(rte, "/api/v1/trains/nope:advance", new { to = "Gated" })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(rte, "/api/v1/gates/nope:start")).Status);
    }

    [Fact]
    public async Task Viewer_cannot_change_state_and_Release_Manager_can_do_what_an_RTE_can()
    {
        using var f = new ApiFactory();
        var viewer = await As(f, Roles.Viewer, "v@x.com");
        var rm = await As(f, Roles.ReleaseManager, "rm@x.com");
        await As(f, Roles.RTE, "rte@x.com");
        SeedTrain(f, UserId(f, "rte@x.com"), UserId(f, "rte@x.com"));
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(viewer, "/api/v1/trains/t1:advance", new { to = "Gated" })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(viewer, "/api/v1/gates/g1:start")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Post(rm, "/api/v1/gates/g1:start")).Status);                 // RM acts like an RTE (D32)
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Post(rm, "/api/v1/trains/t1:advance", new { to = "Gated" })).Status); // allowed to try; guard refuses
    }

    [Fact]
    public async Task Only_a_Governance_Officer_certifies_a_Compliance_gate()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var gov = await As(f, Roles.GovernanceOfficer, "gov@x.com");
        SeedTrain(f, UserId(f, "rte@x.com"), UserId(f, "gov@x.com"));
        var (status, _) = await Post(rte, "/api/v1/gates/g2:certify");
        Assert.Equal(HttpStatusCode.Forbidden, status);
        // a Governance Officer is let through to the service, which then names the real blockers
        var (s2, body) = await Post(gov, "/api/v1/gates/g2:certify");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, s2);
        var guards = body.GetProperty("failures").EnumerateArray().Select(x => x.GetProperty("guard").GetString()).ToHashSet();
        Assert.Contains("GateOpenTasks", guards);
        Assert.Contains("EarlierGateOpen", guards);
    }

    [Fact]
    public async Task Gate_owner_who_is_not_a_planner_can_start_their_own_gate_only()
    {
        using var f = new ApiFactory();
        var owner = await As(f, Roles.Viewer, "owner@x.com"); // Viewer role, but the gate owner
        await As(f, Roles.RTE, "rte@x.com");
        var ownerId = UserId(f, "owner@x.com");
        SeedTrain(f, ownerId, UserId(f, "rte@x.com"));
        Assert.Equal(HttpStatusCode.OK, (await Post(owner, "/api/v1/gates/g1:start")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(owner, "/api/v1/gates/g2:fail")).Status); // owned by someone else
    }

    [Fact]
    public async Task Changing_the_target_date_recomputes_gate_due_dates()
    {
        using var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        SeedTrain(f, UserId(f, "rte@x.com"), UserId(f, "rte@x.com"));
        var r = await rte.PatchAsJsonAsync("/api/v1/trains/t1", new { targetReleaseDate = "2026-11-06" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using var c = new SqliteConnection($"Data Source={f.DbPath};Pooling=False"); c.Open();
        using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT DueOn FROM StageGates WHERE Id='g1'";
        Assert.Equal("2026-10-30", cmd.ExecuteScalar()); // Fri 11-06 minus 5 business days
    }

    // ---- the DbRule safety net --------------------------------------------------------------------------------------
    [Fact]
    public async Task Escaped_trigger_abort_is_mapped_to_422_DbRule()
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        var ex = new Microsoft.EntityFrameworkCore.DbUpdateException("save failed", new SqliteException("SQLite Error 19: 'Illegal gate status transition'.", 19));
        var handled = await new DbRuleExceptionHandler().TryHandleAsync(ctx, ex, default);
        Assert.True(handled);
        Assert.Equal(422, ctx.Response.StatusCode);
        ctx.Response.Body.Position = 0;
        var body = JsonDocument.Parse(ctx.Response.Body).RootElement;
        Assert.Equal("DbRule", body.GetProperty("guard").GetString());
        Assert.Equal("Illegal gate status transition", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Unrelated_exceptions_are_not_swallowed_by_the_DbRule_handler()
    {
        var handled = await new DbRuleExceptionHandler().TryHandleAsync(new DefaultHttpContext(), new InvalidOperationException("boom"), default);
        Assert.False(handled);
    }
}
