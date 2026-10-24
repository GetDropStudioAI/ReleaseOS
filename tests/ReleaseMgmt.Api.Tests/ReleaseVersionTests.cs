using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ReleaseMgmt.Domain.Common;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// REOS-117 (RT2, D48): a version links one product version's releases in a chain (Beta 1 to 3, CERT, PenTest Cert, Production). They show in order, moving one release's target date
/// flags the later releases that now come too soon (an advisory, never a block), and the next release is created from the previous one with its type's default template.
/// Each service write stamps Version and writes its audit row in the same transaction; a version belongs to its workspace.
/// </summary>
public class ReleaseVersionTests
{
    private static readonly Dictionary<string, string> Seeded = new() { ["ReleaseTypes:SeedAtStart"] = "true" };
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
    private static string Day(int days) => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(days).ToString("yyyy-MM-dd");
    private static long Count(ApiFactory f, string sql) => long.Parse(Scalar(f, sql));
    private static string Id(JsonElement e) => e.GetProperty("id").GetString()!;

    private static async Task<HttpResponseMessage> Send(HttpClient c, HttpMethod m, string url, object? body, int? ifMatch)
    {
        using var req = new HttpRequestMessage(m, url) { Content = body is null ? null : JsonContent.Create(body) };
        if (ifMatch is int v) req.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{v}\""));
        return await c.SendAsync(req);
    }

    private static async Task<string> Guard(HttpResponseMessage r) { Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode); return (await Json(r)).GetProperty("guard").GetString()!; }

    private static async Task<Dictionary<string, string>> TypeIds(HttpClient c) =>
        (await c.GetFromJsonAsync<JsonElement>("/api/v1/release-types")).EnumerateArray().ToDictionary(x => x.GetProperty("name").GetString()!, x => Id(x));

    private static async Task<JsonElement> NewRelease(HttpClient c, string title, string typeId, int days, int? beta = null, string? versionId = null)
    {
        var r = await c.PostAsJsonAsync("/api/v1/trains", new { title, targetReleaseDate = Day(days), riskTier = "Low", releaseTypeId = typeId, betaNumber = beta, releaseVersionId = versionId });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await Json(r);
    }

    private static async Task<JsonElement> NewVersion(HttpClient c, string name = "Orion 5.2")
    {
        var r = await c.PostAsJsonAsync("/api/v1/versions", new { name });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await Json(r);
    }

    private static async Task<string> Approved(HttpClient planner, HttpClient approver, string name, params string[] gates)
    {
        var made = await planner.PostAsJsonAsync("/api/v1/templates", new
        {
            name, defaultRiskTier = "High",
            gates = gates.Select((g, i) => new { gateName = g, gateClass = "Standard", offsetDays = 5 - i, requiredBeforeStatus = "Gated" }).ToArray(),
            steps = Array.Empty<object>(), schedule = Array.Empty<object>(),
        });
        var id = Id(await Json(made));
        Assert.Equal(HttpStatusCode.OK, (await approver.PostAsync($"/api/v1/templates/{id}:approve", null)).StatusCode);
        return id;
    }

    private static string[] Labels(JsonElement version) => [.. version.GetProperty("chain").EnumerateArray().Select(c => c.GetProperty("label").GetString()!)];

    [Fact]
    public async Task A_versions_releases_show_in_chain_order_whatever_order_they_were_linked_in()
    {
        using var f = new ApiFactory(settings: Seeded);
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var t = await TypeIds(rte);
        var v = await NewVersion(rte);
        var vid = Id(v);
        Assert.Empty(v.GetProperty("chain").EnumerateArray());
        await NewRelease(rte, "Orion Prod", t["Production"], 90, versionId: vid);
        await NewRelease(rte, "Orion Beta 2", t["Beta"], 20, beta: 2, versionId: vid);
        await NewRelease(rte, "Orion CERT", t["CERT"], 60, versionId: vid);
        var beta1 = await NewRelease(rte, "Orion Beta 1", t["Beta"], 10, beta: 1);   // made on its own, linked afterwards
        var linked = await Send(rte, HttpMethod.Put, $"/api/v1/trains/{Id(beta1)}/version", new { versionId = vid }, beta1.GetProperty("version").GetInt32());
        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);

        var chain = await rte.GetFromJsonAsync<JsonElement>($"/api/v1/versions/{vid}");
        Assert.Equal(["Beta 1", "Beta 2", "CERT", "Production"], Labels(chain));
        Assert.Equal([1, 2, 3, 4], chain.GetProperty("chain").EnumerateArray().Select(c => c.GetProperty("position").GetInt32()).ToArray());
        Assert.Empty(chain.GetProperty("warnings").EnumerateArray());

        var list = await rte.GetFromJsonAsync<JsonElement>("/api/v1/versions");
        Assert.Equal(4, list.EnumerateArray().Single().GetProperty("chain").GetArrayLength());
        var forTrain = await rte.GetFromJsonAsync<JsonElement>($"/api/v1/trains/{Id(beta1)}/version");   // the release page's read
        Assert.Equal("Orion 5.2", forTrain.GetProperty("version").GetProperty("name").GetString());
        Assert.Equal(["PenTest Cert"], forTrain.GetProperty("next").EnumerateArray().Select(n => n.GetProperty("label").GetString()!).ToArray());   // Beta 2, CERT and Production are taken
    }

    [Fact]
    public async Task Moving_a_target_date_flags_the_later_releases_that_now_come_too_soon_as_an_advisory_not_a_block()
    {
        using var f = new ApiFactory(settings: Seeded);
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var t = await TypeIds(rte);
        var vid = Id(await NewVersion(rte));
        var b1 = await NewRelease(rte, "Orion Beta 1", t["Beta"], 10, beta: 1, versionId: vid);
        var b2 = await NewRelease(rte, "Orion Beta 2", t["Beta"], 20, beta: 2, versionId: vid);
        var cert = await NewRelease(rte, "Orion CERT", t["CERT"], 30, versionId: vid);
        async Task<JsonElement> Adv(JsonElement r) => await rte.GetFromJsonAsync<JsonElement>($"/api/v1/trains/{Id(r)}/advisories");
        Assert.Equal(JsonValueKind.Null, (await Adv(b2)).GetProperty("version").ValueKind);

        // Beta 1 slips to day 25: Beta 2 (day 20) is flagged; CERT (day 30) is not, it is after Beta 2
        var moved = await Send(rte, HttpMethod.Patch, $"/api/v1/trains/{Id(b1)}", new { targetReleaseDate = Day(25) }, b1.GetProperty("version").GetInt32());
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);   // never a block
        var flagged = (await Adv(b2)).GetProperty("version");
        Assert.Equal("Beta 2", flagged.GetProperty("label").GetString());
        Assert.Equal("Beta 1", flagged.GetProperty("afterLabel").GetString());
        Assert.Equal(6, flagged.GetProperty("shortByDays").GetInt32());   // earliest is day 26 (the gap is 1 day)
        Assert.Contains("Beta 2 is planned for", flagged.GetProperty("message").GetString());
        Assert.Equal(JsonValueKind.Null, (await Adv(cert)).GetProperty("version").ValueKind);
        Assert.Equal(JsonValueKind.Null, (await Adv(b1)).GetProperty("version").ValueKind);   // it is the one that moved
        var chain = await rte.GetFromJsonAsync<JsonElement>($"/api/v1/versions/{vid}");
        Assert.Equal(["Beta 2"], chain.GetProperty("warnings").EnumerateArray().Select(w => w.GetProperty("label").GetString()!).ToArray());

        // Moving Beta 2 on past it clears the flag and flags CERT instead
        var m2 = await Send(rte, HttpMethod.Patch, $"/api/v1/trains/{Id(b2)}", new { targetReleaseDate = Day(40) }, b2.GetProperty("version").GetInt32());
        Assert.Equal(HttpStatusCode.OK, m2.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await Adv(b2)).GetProperty("version").ValueKind);
        Assert.Equal("CERT", (await Adv(cert)).GetProperty("version").GetProperty("label").GetString());
    }

    [Fact]
    public async Task A_version_is_managed_by_planners_with_If_Match_and_every_write_is_audited()
    {
        using var f = new ApiFactory(settings: Seeded);
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var viewer = await As(f, Roles.Viewer, "v@x.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/v1/versions", new { name = "Nope" })).StatusCode);
        Assert.Equal("VersionInvalid", await Guard(await rte.PostAsJsonAsync("/api/v1/versions", new { name = "   " })));
        Assert.Equal("FieldTooLong", await Guard(await rte.PostAsJsonAsync("/api/v1/versions", new { name = new string('x', 101) })));   // the API's field limit answers first, in words

        var made = await NewVersion(rte, " Orion 5.2 ");
        var id = Id(made);
        Assert.Equal("Orion 5.2", made.GetProperty("name").GetString());
        Assert.Equal(1, made.GetProperty("version").GetInt32());
        Assert.Equal("VersionNameTaken", await Guard(await rte.PostAsJsonAsync("/api/v1/versions", new { name = "ORION 5.2" })));   // any case

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(viewer, HttpMethod.Patch, $"/api/v1/versions/{id}", new { name = "x" }, 1)).StatusCode);
        var stale = await Send(rte, HttpMethod.Patch, $"/api/v1/versions/{id}", new { name = "Orion 5.3" }, 7);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("Orion 5.2", (await Json(stale)).GetProperty("current").GetProperty("name").GetString());   // the current row
        var renamed = await Send(rte, HttpMethod.Patch, $"/api/v1/versions/{id}", new { name = "Orion 5.3" }, 1);
        Assert.Equal(2, (await Json(renamed)).GetProperty("version").GetInt32());

        var archived = await Send(rte, HttpMethod.Post, $"/api/v1/versions/{id}:archive", null, 2);
        Assert.True((await Json(archived)).GetProperty("archived").GetBoolean());
        Assert.Empty((await rte.GetFromJsonAsync<JsonElement>("/api/v1/versions")).EnumerateArray());
        Assert.Single((await rte.GetFromJsonAsync<JsonElement>("/api/v1/versions?includeArchived=true")).EnumerateArray());
        Assert.Equal(HttpStatusCode.OK, (await Send(rte, HttpMethod.Post, $"/api/v1/versions/{id}:unarchive", null, 3)).StatusCode);

        foreach (var action in new[] { "Create", "Update", "Archive", "Unarchive" })
            Assert.Equal(1, Count(f, $"SELECT COUNT(*) FROM AuditEvents WHERE EntityType='ReleaseVersion' AND EntityId='{id}' AND Action='{action}'"));
        Assert.Equal(HttpStatusCode.NotFound, (await rte.GetAsync("/api/v1/versions/nope")).StatusCode);
    }

    [Fact]
    public async Task Linking_a_release_checks_its_place_stamps_Version_audits_and_unlinking_works()
    {
        using var f = new ApiFactory(settings: Seeded);
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var t = await TypeIds(rte);
        var vid = Id(await NewVersion(rte));
        var cert1 = await NewRelease(rte, "Orion CERT", t["CERT"], 30, versionId: vid);
        var cert2 = await NewRelease(rte, "Orion CERT again", t["CERT"], 35);
        var url = $"/api/v1/trains/{Id(cert2)}/version";

        Assert.Equal("VersionSlotTaken", await Guard(await Send(rte, HttpMethod.Put, url, new { versionId = vid }, cert2.GetProperty("version").GetInt32())));   // one CERT per version
        Assert.Equal("VersionSlotTaken", await Guard(await NewReleaseResponse(rte, "Orion CERT third", t["CERT"], 40, vid)));
        Assert.Equal("VersionInvalid", await Guard(await Send(rte, HttpMethod.Put, url, new { versionId = "nope" }, null)));
        Assert.Equal(HttpStatusCode.Conflict, (await Send(rte, HttpMethod.Put, url, new { versionId = vid }, 9)).StatusCode);
        Assert.Equal(1, Count(f, $"SELECT COUNT(*) FROM ReleaseTrains WHERE ReleaseVersionId='{vid}'"));

        // an aborted release gives its place back
        var abort = await rte.PostAsJsonAsync($"/api/v1/trains/{Id(cert1)}:abort", new { reason = "Superseded" });
        Assert.Equal(HttpStatusCode.OK, abort.StatusCode);
        var again = await Send(rte, HttpMethod.Put, url, new { versionId = vid }, cert2.GetProperty("version").GetInt32() + 0);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var both = (await rte.GetFromJsonAsync<JsonElement>($"/api/v1/versions/{vid}")).GetProperty("chain").EnumerateArray().Select(c => c.GetProperty("status").GetString()!).ToArray();
        Assert.Equal(["Aborted", "Planning"], both.Order().ToArray());   // the aborted one stays in the chain, the new one takes its place

        // the release in a version: a type or number change into a taken place is refused
        var beta1 = await NewRelease(rte, "Orion Beta 1", t["Beta"], 10, beta: 1, versionId: vid);
        var beta2 = await NewRelease(rte, "Orion Beta 2", t["Beta"], 20, beta: 2, versionId: vid);
        var renumber = await Send(rte, HttpMethod.Patch, $"/api/v1/trains/{Id(beta2)}/release", new { betaNumber = 1 }, beta2.GetProperty("version").GetInt32());
        Assert.Equal("VersionSlotTaken", await Guard(renumber));
        var free = await Send(rte, HttpMethod.Patch, $"/api/v1/trains/{Id(beta2)}/release", new { betaNumber = 3 }, beta2.GetProperty("version").GetInt32());
        Assert.Equal(HttpStatusCode.OK, free.StatusCode);

        // unlink: audited both ways, Version stamped, and the chain loses it
        var cur = (await rte.GetFromJsonAsync<JsonElement>($"/api/v1/trains/{Id(beta1)}/version")).GetProperty("trainVersion").GetInt32();
        var gone = await Send(rte, HttpMethod.Put, $"/api/v1/trains/{Id(beta1)}/version", new { versionId = (string?)null }, cur);
        Assert.Equal(HttpStatusCode.OK, gone.StatusCode);
        var body = await Json(gone);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("version").ValueKind);
        Assert.Equal(cur + 1, body.GetProperty("trainVersion").GetInt32());
        Assert.Equal(1, Count(f, $"SELECT COUNT(*) FROM AuditEvents WHERE EntityType='ReleaseTrain' AND EntityId='{Id(beta1)}' AND Action='VersionUnlink'"));
        Assert.Contains(vid, Scalar(f, $"SELECT BeforeJson FROM AuditEvents WHERE EntityId='{Id(beta1)}' AND Action='VersionUnlink'"));
        Assert.Equal(Scalar(f, $"SELECT LastChangedByUserId FROM ReleaseTrains WHERE Id='{Id(beta1)}'"), Scalar(f, "SELECT Id FROM Users WHERE Email='rte@x.com'"));
        Assert.DoesNotContain("Orion Beta 1", string.Join(",", (await rte.GetFromJsonAsync<JsonElement>($"/api/v1/versions/{vid}")).GetProperty("chain").EnumerateArray().Select(c => c.GetProperty("title").GetString())));
        var relink = await Send(rte, HttpMethod.Put, $"/api/v1/trains/{Id(beta1)}/version", new { versionId = vid }, cur + 1);
        Assert.Equal(HttpStatusCode.OK, relink.StatusCode);
        Assert.Equal(1, Count(f, $"SELECT COUNT(*) FROM AuditEvents WHERE EntityType='ReleaseTrain' AND EntityId='{Id(beta1)}' AND Action='VersionLink' AND AfterJson LIKE '%Orion 5.2%'"));
    }

    private static Task<HttpResponseMessage> NewReleaseResponse(HttpClient c, string title, string typeId, int days, string versionId) =>
        c.PostAsJsonAsync("/api/v1/trains", new { title, targetReleaseDate = Day(days), riskTier = "Low", releaseTypeId = typeId, releaseVersionId = versionId });

    [Fact]
    public async Task A_typeless_release_cannot_join_and_an_archived_version_takes_no_release()
    {
        using var f = new ApiFactory();   // no types seeded: a typeless release can exist
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var vid = Id(await NewVersion(rte));
        var made = await rte.PostAsJsonAsync("/api/v1/trains", new { title = "Typeless", targetReleaseDate = Day(30), riskTier = "Low" });
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        var train = await Json(made);
        var join = await Send(rte, HttpMethod.Put, $"/api/v1/trains/{Id(train)}/version", new { versionId = vid }, null);
        Assert.Equal("VersionInvalid", await Guard(join));
        Assert.Contains("type", (await Json(join)).GetProperty("message").GetString());
        var link = $"UPDATE ReleaseTrains SET ReleaseVersionId='{vid}' WHERE Id='{Id(train)}'";
        var ex = Assert.ThrowsAny<Microsoft.Data.Sqlite.SqliteException>(new Action(() => Sql(f, link)));   // the trigger behind it
        Assert.Contains("A release in a version needs a release type", ex.Message);

        Assert.Equal(HttpStatusCode.OK, (await rte.PostAsJsonAsync("/api/v1/release-types", new { name = "Production" })).StatusCode);
        var t = await TypeIds(rte);
        await Send(rte, HttpMethod.Post, $"/api/v1/versions/{vid}:archive", null, 1);
        Assert.Equal("VersionInvalid", await Guard(await NewReleaseResponse(rte, "Into archived", t["Production"], 30, vid)));
    }

    [Fact]
    public async Task The_next_release_is_made_from_the_previous_one_in_the_same_version_with_its_types_default_template()
    {
        using var f = new ApiFactory(settings: Seeded);
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var rm = await As(f, Roles.ReleaseManager, "rm@x.com");
        var t = await TypeIds(rte);
        var certTpl = await Approved(rte, rm, "CERT release", "Code Freeze", "CAB");
        var certType = (await rte.GetFromJsonAsync<JsonElement>("/api/v1/release-types")).EnumerateArray().Single(x => Id(x) == t["CERT"]);
        Assert.Equal(HttpStatusCode.OK, (await Send(rte, HttpMethod.Patch, $"/api/v1/release-types/{t["CERT"]}", new { defaultTemplateId = certTpl }, certType.GetProperty("version").GetInt32())).StatusCode);

        var vid = Id(await NewVersion(rte));
        var team = Id(await Json(await rte.PostAsJsonAsync("/api/v1/teams", new { handle = "platform", name = "Platform" })));
        var b1 = await NewRelease(rte, "Orion 5.2 Beta 1", t["Beta"], 10, beta: 1, versionId: vid);
        Assert.Equal(HttpStatusCode.OK, (await Send(rte, HttpMethod.Patch, $"/api/v1/trains/{Id(b1)}/release", new { ownerTeamId = team, releaseBranch = "release/5.2" }, b1.GetProperty("version").GetInt32())).StatusCode);

        // The first option after Beta 1 is Beta 2: dates default to this release's date plus 7 days, the title to the version and label
        var nextB = await rte.PostAsync($"/api/v1/trains/{Id(b1)}:create-next", JsonContent.Create(new { }));
        Assert.Equal(HttpStatusCode.OK, nextB.StatusCode);
        var b2 = await Json(nextB);
        Assert.Equal("Orion 5.2 Beta 2", b2.GetProperty("title").GetString());
        Assert.Equal(Day(17), b2.GetProperty("targetReleaseDate").GetString());
        Assert.Equal("Low", b2.GetProperty("riskTier").GetString());
        Assert.Equal(vid, Scalar(f, $"SELECT ReleaseVersionId FROM ReleaseTrains WHERE Id='{Id(b2)}'"));
        Assert.Equal("2", Scalar(f, $"SELECT BetaNumber FROM ReleaseTrains WHERE Id='{Id(b2)}'"));
        Assert.Equal(team, Scalar(f, $"SELECT OwnerTeamId FROM ReleaseTrains WHERE Id='{Id(b2)}'"));   // carried over
        Assert.Equal("release/5.2", Scalar(f, $"SELECT ReleaseBranch FROM ReleaseTrains WHERE Id='{Id(b2)}'"));
        Assert.Contains("\"releaseVersionId\"", Scalar(f, $"SELECT AfterJson FROM AuditEvents WHERE EntityId='{Id(b2)}' AND EntityType='ReleaseTrain' AND Action='Create'"));

        // CERT picked explicitly: starts from the CERT type's default template (2 gates), on the date given
        var nextC = await rte.PostAsJsonAsync($"/api/v1/trains/{Id(b2)}:create-next", new { releaseTypeId = t["CERT"], targetReleaseDate = Day(40), title = "Orion 5.2 CERT" });
        Assert.Equal(HttpStatusCode.OK, nextC.StatusCode);
        var cert = await Json(nextC);
        Assert.Equal(certTpl, cert.GetProperty("templateId").GetString());
        Assert.Equal(2, cert.GetProperty("created").GetProperty("gates").GetInt32());
        Assert.Equal(Day(40), cert.GetProperty("targetReleaseDate").GetString());

        var chain = await rte.GetFromJsonAsync<JsonElement>($"/api/v1/versions/{vid}");
        Assert.Equal(["Beta 1", "Beta 2", "CERT"], Labels(chain));

        // CERT is taken: asking for it again is refused; a release outside any version has no chain to go on
        Assert.Equal("VersionSlotTaken", await Guard(await rte.PostAsJsonAsync($"/api/v1/trains/{Id(b1)}:create-next", new { releaseTypeId = t["CERT"], title = "Another CERT", targetReleaseDate = Day(50) })));
        var loner = await NewRelease(rte, "Loner", t["Production"], 30);
        Assert.Equal("VersionInvalid", await Guard(await rte.PostAsJsonAsync($"/api/v1/trains/{Id(loner)}:create-next", new { })));
        Assert.Equal(HttpStatusCode.Forbidden, (await (await As(f, Roles.Viewer, "v@x.com")).PostAsJsonAsync($"/api/v1/trains/{Id(b1)}:create-next", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rte.PostAsJsonAsync("/api/v1/trains/nope:create-next", new { })).StatusCode);
    }

    [Fact]
    public async Task The_end_of_the_chain_has_nowhere_to_go_on_and_says_so()
    {
        using var f = new ApiFactory(settings: Seeded);
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var t = await TypeIds(rte);
        var vid = Id(await NewVersion(rte));
        var prod = await NewRelease(rte, "Orion Prod", t["Production"], 30, versionId: vid);
        Assert.Empty((await rte.GetFromJsonAsync<JsonElement>($"/api/v1/trains/{Id(prod)}/version")).GetProperty("next").EnumerateArray());
        var r = await rte.PostAsJsonAsync($"/api/v1/trains/{Id(prod)}:create-next", new { });
        Assert.Equal("VersionInvalid", await Guard(r));
        Assert.Contains("no place left", (await Json(r)).GetProperty("message").GetString());
        Assert.Equal(1, Count(f, "SELECT COUNT(*) FROM ReleaseTrains"));
    }

    [Fact]
    public async Task The_calendar_and_the_overview_carry_each_releases_chain_compactly()
    {
        using var f = new ApiFactory(settings: Seeded);
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var t = await TypeIds(rte);
        var vid = Id(await NewVersion(rte));
        await NewRelease(rte, "Orion Beta 1", t["Beta"], 10, beta: 1, versionId: vid);
        var b2 = await NewRelease(rte, "Orion Beta 2", t["Beta"], 5, beta: 2, versionId: vid);   // earlier than Beta 1: flagged
        await NewRelease(rte, "Solo", t["Production"], 12);

        var cal = await rte.GetFromJsonAsync<JsonElement>($"/api/v1/calendar?from={Day(0)}&to={Day(30)}");
        var trains = cal.GetProperty("trains").EnumerateArray().ToDictionary(x => x.GetProperty("title").GetString()!);
        var chain = trains["Orion Beta 2"].GetProperty("chain");
        Assert.Equal(vid, chain.GetProperty("versionId").GetString());
        Assert.Equal("Beta 2", chain.GetProperty("label").GetString());
        Assert.Equal(2, chain.GetProperty("position").GetInt32());
        Assert.Equal(["Beta 1", "Beta 2"], chain.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("label").GetString()!).ToArray());
        Assert.Contains("Beta 2 is planned for", chain.GetProperty("warning").GetString());
        Assert.Equal(JsonValueKind.Null, trains["Orion Beta 1"].GetProperty("chain").GetProperty("warning").ValueKind);
        Assert.Equal(JsonValueKind.Null, trains["Solo"].GetProperty("chain").ValueKind);
        // a type filter limits the releases listed, not the chain they sit in
        var filtered = await rte.GetFromJsonAsync<JsonElement>($"/api/v1/calendar?from={Day(0)}&to={Day(30)}&releaseTypeId={t["Beta"]}");
        Assert.Equal(2, filtered.GetProperty("trains").EnumerateArray().Count());

        var overview = await rte.GetFromJsonAsync<JsonElement>($"/api/v1/overview?from={Day(-7)}&to={Day(0)}");
        var rows = overview.GetProperty("activity").EnumerateArray().ToDictionary(x => x.GetProperty("title").GetString()!);
        Assert.Equal(2, rows["Orion Beta 2"].GetProperty("chain").GetProperty("count").GetInt32());
        Assert.Equal(JsonValueKind.Null, rows["Solo"].GetProperty("chain").ValueKind);
        Assert.Equal(Id(b2), rows["Orion Beta 2"].GetProperty("trainId").GetString());
    }

    [Fact]
    public async Task A_version_belongs_to_its_workspace()
    {
        using var f = new ApiFactory(settings: Seeded);
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var vid = Id(await NewVersion(rte));
        Sql(f, "INSERT INTO Workspaces(Id,Name,Slug,CreatedAt) VALUES('w2','EU','eu','2026-10-01T00:00:00Z')");
        Sql(f, "INSERT INTO ReleaseVersions(Id,Name,CreatedAt,WorkspaceId) VALUES('vx','Elsewhere','2026-10-01T00:00:00Z','w2')");
        Sql(f, "INSERT INTO ReleaseTypes(Id,Name,WorkspaceId) VALUES('tx','Production','w2')");
        Sql(f, "INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,CreatedAt,UpdatedAt,WorkspaceId,ReleaseTypeId,ReleaseVersionId) VALUES('rx','EU release','2026-12-01','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z','w2','tx','vx')");
        Assert.Equal(HttpStatusCode.NotFound, (await rte.GetAsync("/api/v1/versions/vx")).StatusCode);
        Assert.Equal(vid, Id((await rte.GetFromJsonAsync<JsonElement>("/api/v1/versions")).EnumerateArray().Single()));
        Assert.Equal(HttpStatusCode.NotFound, (await Send(rte, HttpMethod.Patch, "/api/v1/versions/vx", new { name = "Mine now" }, 1)).StatusCode);
        var t = await TypeIds(rte);
        Assert.Equal("VersionInvalid", await Guard(await NewReleaseResponse(rte, "Into theirs", t["Production"], 30, "vx")));
        var ex = Assert.ThrowsAny<Microsoft.Data.Sqlite.SqliteException>(() => Sql(f, "INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,CreatedAt,UpdatedAt,ReleaseTypeId,ReleaseVersionId) SELECT 'ry','x','2026-12-01','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z',Id,'vx' FROM ReleaseTypes WHERE Name='Production' AND WorkspaceId='default'"));
        Assert.Contains("version belongs to another workspace", ex.Message);
    }
}
