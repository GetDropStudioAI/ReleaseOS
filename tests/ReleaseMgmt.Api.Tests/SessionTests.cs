using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Services;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>REOS-26: per-tab UI state with the "new tab starts from the most recent state" rule (PROJECT_SCOPE 5.4).</summary>
public class SessionTests
{
    private const string TabA = "tab-aaaaaaaa", TabB = "tab-bbbbbbbb";
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task A_reopened_tab_gets_its_own_state_and_a_new_tab_gets_the_most_recent()
    {
        using var f = new ApiFactory();
        var c = await As(f, Roles.RTE, "a@x.com");
        SeedTrain(f, UserId(f, "a@x.com"), UserId(f, "a@x.com"));

        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/v1/me/session/{TabA}")).StatusCode);          // nothing saved yet
        Assert.Equal(HttpStatusCode.NoContent, (await c.PutAsJsonAsync($"/api/v1/me/session/{TabA}", new { schemaVersion = 1, activeTrainId = "t1", ui = new { drafts = new { note = "first" } } })).StatusCode);
        await Task.Delay(1100);   // LastActivityAt has whole-second resolution
        Assert.Equal(HttpStatusCode.NoContent, (await c.PutAsJsonAsync($"/api/v1/me/session/{TabB}", new { schemaVersion = 1, activeTrainId = (string?)null, ui = new { drafts = new { note = "second" } } })).StatusCode);

        var own = await Json(await c.GetAsync($"/api/v1/me/session/{TabA}"));
        Assert.False(own.GetProperty("inherited").GetBoolean());
        Assert.Equal("first", own.GetProperty("ui").GetProperty("drafts").GetProperty("note").GetString());
        Assert.Equal("t1", own.GetProperty("activeTrainId").GetString());

        var fresh = await Json(await c.GetAsync("/api/v1/me/session/tab-cccccccc"));                                  // a brand-new tab
        Assert.True(fresh.GetProperty("inherited").GetBoolean());
        Assert.Equal("second", fresh.GetProperty("ui").GetProperty("drafts").GetProperty("note").GetString());       // the most recent save wins

        await c.PutAsJsonAsync($"/api/v1/me/session/{TabA}", new { schemaVersion = 1, activeTrainId = "t1", ui = new { drafts = new { note = "first, edited" } } });   // an update replaces, not appends
        Assert.Equal("1", Scalar(f, $"SELECT COUNT(*) FROM UserSessionState WHERE ClientId='{TabA}'"));
    }

    [Fact]
    public async Task Users_cannot_see_each_others_state_and_anonymous_is_refused()
    {
        using var f = new ApiFactory();
        var a = await As(f, Roles.RTE, "a@x.com");
        var b = await As(f, Roles.RTE, "b@x.com");
        await a.PutAsJsonAsync($"/api/v1/me/session/{TabA}", new { schemaVersion = 1, activeTrainId = (string?)null, ui = new { secret = "a-only" } });
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/v1/me/session/{TabA}")).StatusCode);          // same clientId, other user: nothing, not A's state
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync("/api/v1/me/session/tab-zzzzzzzz")).StatusCode);      // and no inheritance across users
        var anon = f.CreateClient(new() { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync($"/api/v1/me/session/{TabA}")).StatusCode);
    }

    [Fact]
    public async Task Oversized_state_is_422_and_bad_client_ids_are_400()
    {
        using var f = new ApiFactory();
        var c = await As(f, Roles.RTE, "a@x.com");
        var big = new string('x', 262144 + 10);
        var r = await c.PutAsJsonAsync($"/api/v1/me/session/{TabA}", new { schemaVersion = 1, activeTrainId = (string?)null, ui = new { draft = big } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal("SessionStateTooLarge", (await Json(r)).GetProperty("guard").GetString());
        Assert.Equal("0", Scalar(f, "SELECT COUNT(*) FROM UserSessionState"));                                            // nothing half-saved

        var ok = new string('x', 200000);                                                                                    // under the cap is fine
        Assert.Equal(HttpStatusCode.NoContent, (await c.PutAsJsonAsync($"/api/v1/me/session/{TabA}", new { schemaVersion = 1, activeTrainId = (string?)null, ui = new { draft = ok } })).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/v1/me/session/short")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/api/v1/me/session/has spaces and $$$", new { schemaVersion = 1, activeTrainId = (string?)null, ui = new { } })).StatusCode);
    }

    [Fact]
    public async Task A_train_that_no_longer_exists_is_dropped_instead_of_failing_the_autosave()
    {
        using var f = new ApiFactory();
        var c = await As(f, Roles.RTE, "a@x.com");
        Assert.Equal(HttpStatusCode.NoContent, (await c.PutAsJsonAsync($"/api/v1/me/session/{TabA}", new { schemaVersion = 1, activeTrainId = "gone", ui = new { } })).StatusCode);
        var got = await Json(await c.GetAsync($"/api/v1/me/session/{TabA}"));
        Assert.Equal(JsonValueKind.Null, got.GetProperty("activeTrainId").ValueKind);
    }

    [Fact]
    public async Task Janitor_removes_only_states_idle_for_more_than_thirty_days()
    {
        using var f = new ApiFactory();
        var c = await As(f, Roles.RTE, "a@x.com");
        var uid = UserId(f, "a@x.com");
        Sql(f, $@"INSERT INTO UserSessionState(UserId,ClientId,SchemaVersion,UIStateJson,LastActivityAt) VALUES
                  ('{uid}','old-old-old-1',1,'{{}}','2020-01-01T00:00:00Z'), ('{uid}','edge-edge-e1',1,'{{}}','{DateTime.UtcNow.AddDays(-29):yyyy-MM-dd'T'HH:mm:ss'Z'}');");
        await c.PutAsJsonAsync($"/api/v1/me/session/{TabA}", new { schemaVersion = 1, activeTrainId = (string?)null, ui = new { } });     // fresh
        var removed = await f.Services.GetRequiredService<SessionService>().PurgeIdleAsync(TimeSpan.FromDays(30));
        Assert.Equal(1, removed);
        Assert.Equal("0", Scalar(f, "SELECT COUNT(*) FROM UserSessionState WHERE ClientId='old-old-old-1'"));
        Assert.Equal("2", Scalar(f, "SELECT COUNT(*) FROM UserSessionState"));
    }

    [Fact]
    public async Task The_service_refuses_invalid_json_by_itself()
    {
        using var f = new ApiFactory();
        await As(f, Roles.RTE, "a@x.com");
        var r = await f.Services.GetRequiredService<SessionService>().PutAsync(UserId(f, "a@x.com"), TabA, 1, null, "{not json");
        Assert.Equal(ResultKind.GuardFailed, r.Kind);
        Assert.Equal(Guards.InvalidSessionState, r.Failures[0].Guard);
    }
}
