using System.Net;
using System.Text.Json;
using ReleaseMgmt.Domain.Common;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>REOS-88 contingency and REOS-85 window collisions through GET /trains/{id}/advisories and the Calendar payload.</summary>
public class AdvisoryTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    /// <summary>t1 and t2 Gated, windows 06:00–10:00 and 09:30–11:00 on 6 Nov sharing CI ledger-db; t3 Complete in the same slot (ignored); t4 overlaps but shares nothing.</summary>
    private static async Task<(ApiFactory F, HttpClient Rte)> Setup()
    {
        var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@adv.test");
        var uid = UserId(f, "rte@adv.test");
        Sql(f, $@"
            INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CurrentStatus,CloseCode,CreatedAt,UpdatedAt) VALUES
              ('t1','R26.12 Card portal','2026-11-06','Low','Gated',NULL,'2026-10-01T00:00:00Z','2026-10-01T00:00:00Z'),
              ('t2','R26.25 Ledger','2026-11-06','Low','Gated',NULL,'2026-10-01T00:00:00Z','2026-10-01T00:00:00Z'),
              ('t3','R26.20 Done','2026-11-06','Low','Complete','Successful','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z'),
              ('t4','R26.30 Other','2026-11-06','Low','Planning',NULL,'2026-10-01T00:00:00Z','2026-10-01T00:00:00Z');
            INSERT INTO DeploymentWindows(Id,ReleaseTrainId,StartsAt,EndsAt) VALUES
              ('w1','t1','2026-11-06T06:00:00Z','2026-11-06T10:00:00Z'),('w2','t2','2026-11-06T09:30:00Z','2026-11-06T11:00:00Z'),
              ('w3','t3','2026-11-06T06:00:00Z','2026-11-06T10:00:00Z'),('w4','t4','2026-11-06T06:00:00Z','2026-11-06T10:00:00Z');
            INSERT INTO AffectedCIs(Id,ReleaseTrainId,CiName) VALUES('c1','t1','ledger-db'),('c2','t2','Ledger-DB'),('c3','t3','ledger-db'),('c4','t4','cards-db');
            INSERT INTO RunbookSteps(Id,ReleaseTrainId,StepCode,Section,Title,OwnerUserId,PlannedStartAt,PlannedDurationMin) VALUES
              ('s1','t1','R-001','Deploy','Deploy','{uid}','2026-11-06T06:00:00Z',150),
              ('s2','t1','R-901','Rollback','Roll back','{uid}','2026-11-06T09:00:00Z',60);");
        return (f, rte);
    }

    [Fact]
    public async Task Advisories_give_the_contingency_and_the_trains_whose_windows_collide()
    {
        var (f, rte) = await Setup(); using var _ = f;
        var a = await Json(await rte.GetAsync("/api/v1/trains/t1/advisories"));
        var c = a.GetProperty("contingency");
        Assert.Equal(("low", 150, 60, 30, 20, 30), (c.GetProperty("state").GetString(), c.GetProperty("plannedMin").GetInt32(), c.GetProperty("rollbackMin").GetInt32(),
            c.GetProperty("spareMin").GetInt32(), c.GetProperty("percent").GetInt32(), c.GetProperty("minPercent").GetInt32()));
        Assert.Equal("2026-11-06T09:00:00Z", c.GetProperty("rollbackDeadline").GetString());

        var hit = Assert.Single(a.GetProperty("collisions").EnumerateArray());   // t2 shares the CI; t3 is Complete; t4 shares nothing
        Assert.Equal(("t2", "R26.25 Ledger"), (hit.GetProperty("otherTrainId").GetString(), hit.GetProperty("otherTitle").GetString()));
        Assert.Equal(["ledger-db"], hit.GetProperty("sharedCis").EnumerateArray().Select(x => x.GetString()!).ToArray());

        var none = await Json(await rte.GetAsync("/api/v1/trains/t3/advisories"));   // a closed train has nothing to warn about
        Assert.Equal(JsonValueKind.Null, none.GetProperty("contingency").ValueKind);
        Assert.Empty(none.GetProperty("collisions").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await rte.GetAsync("/api/v1/trains/nope/advisories")).StatusCode);
    }

    [Fact]
    public async Task The_calendar_lists_each_trains_collisions_and_a_train_without_a_window_has_an_unknown_contingency()
    {
        var (f, rte) = await Setup(); using var _ = f;
        var cal = await Json(await rte.GetAsync("/api/v1/calendar?from=2026-11-01&to=2026-11-30"));
        var trains = cal.GetProperty("trains").EnumerateArray().ToDictionary(t => t.GetProperty("id").GetString()!);
        var t1 = Assert.Single(trains["t1"].GetProperty("collisions").EnumerateArray());
        Assert.Equal(("t2", "CI ledger-db"), (t1.GetProperty("trainId").GetString(), t1.GetProperty("shared")[0].GetString()));
        Assert.Equal("t1", Assert.Single(trains["t2"].GetProperty("collisions").EnumerateArray()).GetProperty("trainId").GetString());
        Assert.Empty(trains["t3"].GetProperty("collisions").EnumerateArray());

        Sql(f, "DELETE FROM DeploymentWindows WHERE ReleaseTrainId='t1'");
        var a = await Json(await rte.GetAsync("/api/v1/trains/t1/advisories"));
        Assert.Equal(("unknown", "no deployment window"), (a.GetProperty("contingency").GetProperty("state").GetString(), a.GetProperty("contingency").GetProperty("reason").GetString()));
        Assert.Empty(a.GetProperty("collisions").EnumerateArray());
    }
}
