using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ReleaseMgmt.Domain.Common;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// Security review, resource consumption and injection (docs/security/scan-resources-injection.md). Each test reproduces one finding (SEC-D numbers in the
/// report) over HTTP against the real host, and then pins the fix.
/// </summary>
public class ResourceInjectionTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private static async Task<(ApiFactory F, HttpClient Rte, HttpClient Viewer)> World()
    {
        var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var viewer = await As(f, Roles.Viewer, "viewer@x.com");
        SeedTrain(f, UserId(f, "rte@x.com"), UserId(f, "rte@x.com"));   // t1 'R26.10' with gates Code Freeze and Compliance Sign-off
        return (f, rte, viewer);
    }

    private static Task<HttpResponseMessage> Preview(HttpClient c, string kind, string csv, string mode = "Append") =>
        c.PostAsync($"/api/v1/imports/{kind}:preview?mode={mode}&fileName=t.csv", new StringContent(csv, new UTF8Encoding(false), "text/csv"));

    // ---- SEC-D1: comm preview of ad-hoc text ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_viewer_cannot_preview_ad_hoc_text_longer_than_a_template_may_be()
    {
        var (f, _, viewer) = await World(); using var _f = f;
        var r = await viewer.PostAsJsonAsync("/api/v1/trains/t1/comms:preview", new { text = new string('{', 50_000) });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal("CommPreviewTooLarge", (await Json(r)).GetProperty("guard").GetString());

        var subject = await viewer.PostAsJsonAsync("/api/v1/trains/t1/comms:preview", new { subject = new string('s', 201), text = "x" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, subject.StatusCode);

        var ok = await viewer.PostAsJsonAsync("/api/v1/trains/t1/comms:preview", new { subject = "[{ReleaseTitle}]", text = new string('x', 20_000) });   // the library's own limits pass
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    // ---- SEC-D2: import suggestions ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_owner_cell_longer_than_an_email_is_a_cell_error_not_a_search()
    {
        var (f, rte, _) = await World(); using var _f = f;
        var csv = "Train,Gate,Description,Owner\r\nR26.10,Code Freeze,Tag repos,a@" + new string('b', 100_000) + "\r\n";
        var p = await Json(await Preview(rte, "Tasks", csv));
        var e = Assert.Single(p.GetProperty("errors").EnumerateArray());
        Assert.Equal("Owner", e.GetProperty("column").GetString());
        Assert.Equal("Owner is longer than 254 characters", e.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Ten_thousand_near_miss_rows_against_long_titles_preview_in_seconds_not_minutes()
    {
        var (f, rte, _) = await World(); using var _f = f;
        // 200 trains whose titles are as long as a title may be, and a Products file whose every row names a title that is one character off each of them
        var titles = Enumerable.Range(0, 200).Select(i => $"{i:D3}-" + new string('t', 196)).ToList();
        Sql(f, "INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CreatedAt,UpdatedAt) VALUES " +
               string.Join(',', titles.Select((t, i) => $"('lt{i}','{t}','2026-12-01','Low','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z')")) + ";");
        var sb = new StringBuilder("Train,ProductName,VersionTag,ProjectCode\r\n");
        for (var i = 0; i < 10_000; i++) sb.Append("999-").Append(new string('t', 195)).Append('x').Append($",P{i},1.0,PC\r\n");
        var sw = Stopwatch.StartNew();
        var r = await Preview(rte, "Products", sb.ToString());
        sw.Stop();
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(10_000, (await Json(r)).GetProperty("counts").GetProperty("errors").GetInt32());
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"the preview took {sw.Elapsed}");
    }

    // ---- SEC-D3: import width and the size of the preview response ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_file_wider_than_the_column_limit_is_rejected_unread()
    {
        var (f, rte, _) = await World(); using var _f = f;
        var csv = "Day,Name," + string.Join(',', Enumerable.Range(0, 5_000).Select(i => $"c{i}")) + "\r\n2030-01-01,New Year\r\n";
        var r = await Preview(rte, "Holidays", csv);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        var body = await Json(r);
        Assert.Equal("ImportRejected", body.GetProperty("guard").GetString());
        Assert.Contains("columns", body.GetProperty("message").GetString());
        Assert.Equal("Rejected", Scalar(f, "SELECT Status FROM ImportJobs"));
    }

    [Fact]
    public async Task The_preview_lists_at_most_1000_errors_and_counts_all_of_them()
    {
        var (f, rte, _) = await World(); using var _f = f;
        var sb = new StringBuilder("Train,Gate,Description,Owner\r\n");
        for (var i = 0; i < 1_500; i++) sb.Append($"R26.10,Code Freeze,Task {i},nobody{i}@x.com\r\n");
        var p = await Json(await Preview(rte, "Tasks", sb.ToString()));
        Assert.Equal(1_500, p.GetProperty("counts").GetProperty("errors").GetInt32());
        var errors = p.GetProperty("errors").EnumerateArray().ToList();
        Assert.Equal(1_001, errors.Count);
        Assert.Equal("500 more errors are not listed", errors[^1].GetProperty("message").GetString());
    }

    // ---- SEC-D9: stored previews ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task One_person_keeps_at_most_five_open_import_previews_on_disk()
    {
        var (f, rte, _) = await World(); using var _f = f;
        var ids = new List<string>();
        for (var i = 0; i < 7; i++)
            ids.Add((await Json(await Preview(rte, "Holidays", $"Day,Name\r\n2030-01-0{i + 1},H{i}\r\n"))).GetProperty("jobId").GetString()!);
        Assert.Equal("Expired", Scalar(f, $"SELECT Status FROM ImportJobs WHERE Id='{ids[0]}'"));
        Assert.Equal("Expired", Scalar(f, $"SELECT Status FROM ImportJobs WHERE Id='{ids[1]}'"));
        Assert.Equal("5", Scalar(f, "SELECT COUNT(*) FROM ImportJobs WHERE Status='Previewed'"));
        var dir = Path.Combine(Path.GetDirectoryName(f.DbPath)!, "imports");
        Assert.False(File.Exists(Path.Combine(dir, ids[0] + ".csv")));
        Assert.Equal(10, Directory.GetFiles(dir).Length);   // five previews, a file and a plan each
        Assert.Equal("ImportExpired", (await Json(await rte.PostAsJsonAsync($"/api/v1/imports/{ids[0]}:commit", new { }))).GetProperty("guard").GetString());
        Assert.Equal(HttpStatusCode.OK, (await rte.PostAsJsonAsync($"/api/v1/imports/{ids[6]}:commit", new { })).StatusCode);   // the newest still commits
    }

    [Fact]
    public async Task Expired_checklist_previews_do_not_pile_up_in_the_database()
    {
        var (f, rte, _) = await World(); using var _f = f;
        var rid = UserId(f, "rte@x.com");
        Sql(f, $"INSERT INTO ParsePreviews(Id,ReleaseTrainId,UserId,TrainVersion,ResultJson,ErrorCount,CreatedAt,ExpiresAt) VALUES('old','t1','{rid}',1,'{{}}',0,'2020-01-01T00:00:00Z','2020-01-01T00:30:00Z')");
        var r = await rte.PostAsJsonAsync("/api/v1/trains/t1/tasks:parse", new { text = "# Code Freeze\n- Tag release @rte@x.com" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("0", Scalar(f, "SELECT COUNT(*) FROM ParsePreviews WHERE Id='old'"));
        Assert.Equal("1", Scalar(f, "SELECT COUNT(*) FROM ParsePreviews"));
    }

    // ---- SEC-D10 (not fixed, Q-SEC-D5): the anonymous feed brake is keyed by client address -------------------------------------------------------

    [Fact(Skip = "SEC-D10 reproduced, not fixed: the brake's design is Q-051e and the remedy depends on the deployment's proxy (Q-SEC-D5). Unskip to reproduce: fails with 429.")]
    public async Task Strangers_guessing_feed_tokens_behind_the_same_proxy_address_do_not_lock_out_a_valid_calendar()
    {
        var (f, _, viewer) = await World(); using var _f = f;
        var path = (await Json(await viewer.PostAsJsonAsync("/api/v1/me/ics-tokens", new { scope = "all" }))).GetProperty("path").GetString()!;
        var anon = f.CreateClient();   // the test server gives every client the same (empty) address, as a reverse proxy without forwarded headers does
        for (var i = 0; i < 61; i++) Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/v1/ics/{new string('A', 43)}.ics")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync(path)).StatusCode);   // today: 429 for everyone behind that address for the rest of the minute
    }

    // ---- checked, no issue: ICS property injection (pinned) ----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_name_with_line_breaks_cannot_add_a_property_or_an_event_to_an_ICS_feed()
    {
        var (f, _, viewer) = await World(); using var _f = f;
        const string evil = "Q4\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:forged@evil\r\nSUMMARY:Forged;X=1,2";
        Sql(f, $"UPDATE ReleaseTrains SET Title='R26.10' || char(13,10) || 'X-WR-CALNAME:forged' WHERE Id='t1';" +
               $"INSERT INTO FreezeWindows(Id,Name,Kind,StartsAt,EndsAt,CreatedByUserId) VALUES('fw1',{string.Join(" || char(13,10) || ", evil.Split("\r\n").Select(s => "'" + s + "'"))},'Freeze','2026-12-20T00:00:00Z','2027-01-03T00:00:00Z','{UserId(f, "viewer@x.com")}');");
        var t = await Json(await viewer.PostAsJsonAsync("/api/v1/me/ics-tokens", new { scope = "train", trainId = "t1" }));
        var ics = await (await f.CreateClient().GetAsync($"/api/v1/ics/{t.GetProperty("token").GetString()}/trains/t1.ics")).Content.ReadAsStringAsync();
        var lines = ics.Split("\r\n");
        Assert.DoesNotContain(lines, l => l.StartsWith("UID:forged", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.StartsWith("X-WR-CALNAME:forged", StringComparison.Ordinal));
        Assert.Equal(lines.Count(l => l == "BEGIN:VEVENT"), lines.Count(l => l.StartsWith("UID:", StringComparison.Ordinal)));
        Assert.Single(lines, l => l.StartsWith("X-WR-CALNAME:", StringComparison.Ordinal));
        var unfolded = ics.Replace("\r\n ", "");   // RFC 5545 line folding
        Assert.Contains("Q4\\nEND:VEVENT\\nBEGIN:VEVENT\\nUID:forged@evil\\nSUMMARY:Forged\\;X=1\\,2", unfolded);   // the value is there, as escaped TEXT
        Assert.Contains("X-WR-CALNAME:R26.10\\nX-WR-CALNAME:forged", unfolded);
    }

    // ---- SEC-D7: request bodies ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_json_body_over_the_request_limit_is_refused_413_before_it_is_read()
    {
        var (f, rte, viewer) = await World(); using var _f = f;
        var r = await viewer.PutAsJsonAsync("/api/v1/me/session/tab-aaaa-0001", new { schemaVersion = 1, activeTrainId = (string?)null, ui = new { draft = new string('x', 2 * 1024 * 1024) } });
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, r.StatusCode);
        Assert.Equal("RequestTooLarge", (await Json(r)).GetProperty("guard").GetString());

        // the two upload routes keep their own, larger limits
        var csv = "Day,Name\r\n2030-01-01," + new string('x', 2 * 1024 * 1024) + "\r\n";
        var p = await Preview(rte, "Holidays", csv);
        Assert.Equal(HttpStatusCode.OK, p.StatusCode);
        Assert.Contains("longer than 200", (await Json(p)).GetProperty("errors")[0].GetProperty("message").GetString());
    }
}
