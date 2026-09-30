using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using nietras.SeparatedValues;
using ReleaseMgmt.Api.Endpoints;
using ReleaseMgmt.Domain.Common;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>REOS-38: the audit viewer endpoints (roles, filters, keyset paging, CSV) and the guarantee that reading never writes.</summary>
public class AuditTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private static string[] Ids(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt64().ToString()).ToArray();

    private static string Q(string s) => s.Replace("'", "''");

    private static void Ev(ApiFactory f, string at, string? actor, string? train, string entity, string entityId, string action, string? before = null, string? after = null) =>
        Sql(f, $"INSERT INTO AuditEvents(OccurredAt,ActorUserId,ReleaseTrainId,EntityType,EntityId,Action,BeforeJson,AfterJson) VALUES('{at}',{(actor is null ? "NULL" : $"'{actor}'")},{(train is null ? "NULL" : $"'{train}'")},'{entity}','{Q(entityId)}','{Q(action)}',{(before is null ? "NULL" : $"'{Q(before)}'")},{(after is null ? "NULL" : $"'{Q(after)}'")})");

    private static string Ts(int i) => $"2026-10-01T10:{i / 60 % 60:00}:{i % 60:00}Z";

    private static async Task<(ApiFactory F, HttpClient Rte, string RteId, string OtherId)> Setup()
    {
        var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        await As(f, Roles.GovernanceOfficer, "gov@x.com");
        return (f, rte, UserId(f, "rte@x.com"), UserId(f, "gov@x.com"));
    }

    // ---- roles ------------------------------------------------------------------------------------------------------------------------
    [Theory]
    [InlineData("/api/v1/audit")]
    [InlineData("/api/v1/audit.csv")]
    [InlineData("/api/v1/audit/entity-types")]
    public async Task Viewers_are_refused_and_RTE_ReleaseManager_and_GovernanceOfficer_may_read(string url)
    {
        using var f = new ApiFactory();
        Assert.Equal(HttpStatusCode.Forbidden, (await (await As(f, Roles.Viewer, "v@x.com")).GetAsync(url)).StatusCode);
        foreach (var role in new[] { Roles.RTE, Roles.ReleaseManager, Roles.GovernanceOfficer })
            Assert.Equal(HttpStatusCode.OK, (await (await As(f, role, $"{role}@x.com")).GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().GetAsync(url)).StatusCode);
    }

    // ---- shape and joins --------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Rows_carry_the_actor_name_and_train_title_and_the_raw_json_and_a_system_event_has_a_null_actor()
    {
        var (f, rte, rteId, _) = await Setup(); using var _f = f;
        SeedTrain(f, rteId, rteId);
        Ev(f, Ts(1), rteId, "t1", "StageGate", "g1", "Certified", "{\"Status\":\"InProgress\"}", "{\"Status\":\"Certified\"}");
        Ev(f, Ts(2), null, null, "SyncAlert", "a1", "Raised");

        var page = await Json(await rte.GetAsync("/api/v1/audit?entity=StageGate"));
        var row = page.GetProperty("items")[0];
        Assert.Equal("rte", row.GetProperty("actorName").GetString());
        Assert.Equal("R26.10", row.GetProperty("trainTitle").GetString());
        Assert.Equal("{\"Status\":\"InProgress\"}", row.GetProperty("beforeJson").GetString());   // raw string, not parsed
        Assert.Equal("{\"Status\":\"Certified\"}", row.GetProperty("afterJson").GetString());
        Assert.Equal("2026-10-01T10:00:01Z", row.GetProperty("occurredAt").GetString());

        var sys = (await Json(await rte.GetAsync("/api/v1/audit?entity=SyncAlert"))).GetProperty("items")[0];
        Assert.Equal(JsonValueKind.Null, sys.GetProperty("actorUserId").ValueKind);
        Assert.Equal(JsonValueKind.Null, sys.GetProperty("actorName").ValueKind);
        Assert.Equal(JsonValueKind.Null, sys.GetProperty("trainTitle").ValueKind);
        Assert.Equal(JsonValueKind.Null, sys.GetProperty("beforeJson").ValueKind);
    }

    [Fact]
    public async Task Entity_types_lists_each_type_once_sorted()
    {
        var (f, rte, rteId, _) = await Setup(); using var _f = f;
        Ev(f, Ts(1), rteId, null, "StageGate", "g1", "A"); Ev(f, Ts(2), rteId, null, "StageGate", "g2", "A"); Ev(f, Ts(3), rteId, null, "Attachment", "x", "A");
        var types = (await Json(await rte.GetAsync("/api/v1/audit/entity-types"))).EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Equal(types.Order(StringComparer.Ordinal).ToList(), types);
        Assert.Equal(types.Distinct().Count(), types.Count);
        Assert.Contains("Attachment", types); Assert.Contains("StageGate", types);
    }

    // ---- filters ----------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Each_filter_narrows_the_result_and_filters_combine()
    {
        var (f, rte, rteId, govId) = await Setup(); using var _f = f;
        SeedTrain(f, rteId, govId);
        Sql(f, "INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CreatedAt,UpdatedAt) VALUES('t2','R26.11','2026-11-30','Moderate','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z')");
        Ev(f, Ts(1), rteId, "t1", "StageGate", "g1", "Started");
        Ev(f, Ts(2), govId, "t1", "StageGate", "g2", "Certified");
        Ev(f, Ts(3), govId, "t2", "Attachment", "a1", "Uploaded");
        Ev(f, Ts(4), null, "t2", "StageGate", "g9", "Decertified");
        var before = (await Json(await rte.GetAsync("/api/v1/audit"))).GetProperty("items").GetArrayLength();
        Assert.True(before >= 4);

        async Task<string[]> Actions(string qs) => (await Json(await rte.GetAsync("/api/v1/audit?" + qs))).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("action").GetString()!).ToArray();

        Assert.Equal(["Certified", "Started"], await Actions("train=t1"));
        Assert.Equal(["Decertified", "Uploaded"], await Actions("train=t2"));
        Assert.Equal(["Uploaded"], await Actions("entity=Attachment"));
        Assert.Equal(["Certified"], await Actions("entityId=g2"));
        Assert.Equal(["Uploaded", "Certified"], await Actions($"actor={govId}"));
        Assert.Equal(["Decertified", "Certified"], await Actions("action=CERTIF"));           // contains, case-insensitive
        Assert.Equal(["Decertified"], await Actions("action=decert&train=t2&entity=StageGate")); // combined
        Assert.Empty(await Actions("train=t1&entity=Attachment"));
        Assert.Empty(await Actions("train=nope"));
    }

    [Fact]
    public async Task The_action_filter_treats_percent_and_underscore_literally()
    {
        var (f, rte, rteId, _) = await Setup(); using var _f = f;
        Ev(f, Ts(1), rteId, null, "X", "1", "Rate 50% off"); Ev(f, Ts(2), rteId, null, "X", "2", "Rate 5000 off"); Ev(f, Ts(3), rteId, null, "X", "3", "a_b"); Ev(f, Ts(4), rteId, null, "X", "4", "axb");
        Assert.Equal(["1"], (await Json(await rte.GetAsync("/api/v1/audit?entity=X&action=50%25"))).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("entityId").GetString()!).ToArray());
        Assert.Equal(["3"], (await Json(await rte.GetAsync("/api/v1/audit?entity=X&action=a_b"))).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("entityId").GetString()!).ToArray());
    }

    [Fact]
    public async Task The_date_range_is_inclusive_and_a_date_only_to_covers_the_whole_day()
    {
        var (f, rte, rteId, _) = await Setup(); using var _f = f;
        Ev(f, "2026-09-30T23:59:59Z", rteId, null, "D", "before", "A");
        Ev(f, "2026-10-01T00:00:00Z", rteId, null, "D", "start", "A");
        Ev(f, "2026-10-01T12:00:00Z", rteId, null, "D", "noon", "A");
        Ev(f, "2026-10-01T23:59:59Z", rteId, null, "D", "end", "A");
        Ev(f, "2026-10-02T00:00:00Z", rteId, null, "D", "after", "A");
        async Task<string[]> Ids2(string qs) => (await Json(await rte.GetAsync("/api/v1/audit?entity=D&" + qs))).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("entityId").GetString()!).Reverse().ToArray();

        Assert.Equal(["start", "noon", "end"], await Ids2("from=2026-10-01&to=2026-10-01"));                 // dates only: whole UTC day, both ends included
        Assert.Equal(["start", "noon"], await Ids2("from=2026-10-01T00:00:00Z&to=2026-10-01T12:00:00Z"));       // instants: both ends included
        Assert.Equal(["noon", "end", "after"], await Ids2("from=2026-10-01T12:00:00Z"));
        Assert.Equal(["before", "start"], await Ids2("to=2026-10-01T00:00:00Z"));
        Assert.Equal(["start", "noon"], await Ids2("from=2026-10-01T02:00:00%2B02:00&to=2026-10-01T14:00:00%2B02:00"));   // an offset is honoured
    }

    [Theory]
    [InlineData("from=yesterday")]
    [InlineData("to=2026-13-45")]
    [InlineData("cursor=abc")]
    [InlineData("cursor=-1")]
    public async Task A_malformed_filter_is_a_400_with_a_guard_not_a_500(string qs)
    {
        var (f, rte, _, _) = await Setup(); using var _f = f;
        var r = await rte.GetAsync("/api/v1/audit?" + qs);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("InvalidFilter", (await Json(r)).GetProperty("guard").GetString());
        if (!qs.StartsWith("cursor")) Assert.Equal(HttpStatusCode.BadRequest, (await rte.GetAsync("/api/v1/audit.csv?" + qs)).StatusCode);
    }

    // ---- paging -----------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Keyset_paging_walks_newest_first_without_gaps_or_duplicates_even_while_rows_arrive()
    {
        var (f, rte, rteId, _) = await Setup(); using var _f = f;
        for (var i = 0; i < 25; i++) Ev(f, Ts(i), rteId, null, "P", $"e{i}", "A");
        var all = Ids(await Json(await rte.GetAsync("/api/v1/audit?entity=P&limit=500")));
        Assert.Equal(25, all.Length);
        Assert.Equal(all.OrderByDescending(long.Parse).ToArray(), all);   // newest (highest Id) first

        var seen = new List<string>(); string? cursor = null; var pages = 0;
        do
        {
            var page = await Json(await rte.GetAsync($"/api/v1/audit?entity=P&limit=10{(cursor is null ? "" : "&cursor=" + cursor)}"));
            Assert.Equal(10, page.GetProperty("limit").GetInt32());
            seen.AddRange(Ids(page));
            if (pages++ == 0) Ev(f, Ts(30), rteId, null, "P", "late", "A");   // a new row after page 1 must not shift the pages that follow
            cursor = page.GetProperty("nextCursor").ValueKind == JsonValueKind.Null ? null : page.GetProperty("nextCursor").GetString();
        } while (cursor is not null && pages < 10);

        Assert.Equal(3, pages);
        Assert.Equal(25, seen.Count);
        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.Equal(all, seen);
    }

    [Fact]
    public async Task An_exact_multiple_ends_with_a_null_cursor_and_the_limit_is_clamped()
    {
        var (f, rte, rteId, _) = await Setup(); using var _f = f;
        for (var i = 0; i < 4; i++) Ev(f, Ts(i), rteId, null, "L", $"e{i}", "A");
        var p = await Json(await rte.GetAsync("/api/v1/audit?entity=L&limit=4"));
        Assert.Equal(4, p.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, p.GetProperty("nextCursor").ValueKind);
        Assert.Equal(500, (await Json(await rte.GetAsync("/api/v1/audit?limit=100000"))).GetProperty("limit").GetInt32());
        Assert.Equal(1, (await Json(await rte.GetAsync("/api/v1/audit?entity=L&limit=0"))).GetProperty("limit").GetInt32());
        Assert.Equal(100, (await Json(await rte.GetAsync("/api/v1/audit"))).GetProperty("limit").GetInt32());
    }

    // ---- csv --------------------------------------------------------------------------------------------------------------------------
    private static List<Dictionary<string, string>> Csv(string text)
    {
        using var r = Sep.New(',').Reader(o => o with { Unescape = true }).FromText(text);
        var cols = r.Header.ColNames;
        var rows = new List<Dictionary<string, string>>();
        foreach (var row in r) { var d = new Dictionary<string, string>(); foreach (var c in cols) d[c] = row[c].ToString(); rows.Add(d); }
        return rows;
    }

    [Fact]
    public async Task The_csv_has_a_header_the_download_headers_and_the_same_rows_as_the_json_for_the_same_filter()
    {
        var (f, rte, rteId, _) = await Setup(); using var _f = f;
        SeedTrain(f, rteId, rteId);
        Ev(f, Ts(1), rteId, "t1", "StageGate", "g1", "Certified", "{\"a\":1}", "{\"a\":2}");
        Ev(f, Ts(2), null, null, "SyncAlert", "a1", "Raised");
        Ev(f, Ts(3), rteId, "t1", "Other", "o1", "Done");
        var r = await rte.GetAsync("/api/v1/audit.csv?entity=StageGate");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("text/csv; charset=utf-8", r.Content.Headers.ContentType!.ToString());
        Assert.StartsWith("attachment", r.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Matches(@"^audit-\d{8}-\d{6}\.csv$", r.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        Assert.Equal("false", r.Headers.GetValues("X-Audit-Truncated").Single());
        var text = await r.Content.ReadAsStringAsync();
        Assert.False(text.StartsWith('﻿'));
        Assert.True(text.StartsWith("Id,OccurredAt,Actor,ActorUserId,Train,ReleaseTrainId,EntityType,EntityId,Action,BeforeJson,AfterJson"), $"[{(int)r.StatusCode}] len={text.Length} {text}");

        var reader = Csv(text);
        var rows = reader.Select(row => (Id: row["Id"].ToString(), At: row["OccurredAt"].ToString(), Actor: row["Actor"].ToString(), Train: row["Train"].ToString(), Action: row["Action"].ToString(), Before: row["BeforeJson"].ToString(), After: row["AfterJson"].ToString())).ToList();
        var single = Assert.Single(rows);
        Assert.Equal("2026-10-01T10:00:01Z", single.At);
        Assert.Equal(("rte", "R26.10", "Certified", "{\"a\":1}", "{\"a\":2}"), (single.Actor, single.Train, single.Action, single.Before, single.After));

        var allCsv = Csv(await rte.GetStringAsync("/api/v1/audit.csv?train=t1")).Select(x => x["Id"].ToString()).ToArray();
        var allJson = Ids(await Json(await rte.GetAsync("/api/v1/audit?train=t1")));
        Assert.Equal(allJson, allCsv);
    }

    [Fact]
    public async Task The_csv_names_a_system_actor_and_quotes_commas_quotes_and_newlines_so_a_parser_reads_them_back()
    {
        var (f, rte, rteId, _) = await Setup(); using var _f = f;
        var tricky = "line1,\"quoted\"\r\nline2";
        Ev(f, Ts(1), null, null, "C", "1", "Sys, \"act\"", tricky, "{}");
        var text = await rte.GetStringAsync("/api/v1/audit.csv?entity=C");
        Assert.True(text.Contains("\"Sys, \"\"act\"\"\""), text);      // RFC 4180 quoting
        var reader = Csv(text);
        var row = Assert.Single(reader.Select(x => (Actor: x["Actor"].ToString(), ActorId: x["ActorUserId"].ToString(), Action: x["Action"].ToString(), Before: x["BeforeJson"].ToString())));
        Assert.Equal(("System", "", "Sys, \"act\"", tricky), row);
    }

    [Theory]
    [InlineData("=SUM(A1)")]
    [InlineData("+cmd|' /C calc'!A0")]
    [InlineData("-2+3")]
    [InlineData("@SUM(1)")]
    [InlineData("\tTAB")]
    [InlineData("\rCR")]
    public async Task Cells_a_spreadsheet_could_read_as_a_formula_get_a_leading_quote(string evil)
    {
        var (f, rte, rteId, _) = await Setup(); using var _f = f;
        Ev(f, Ts(1), rteId, null, "F", evil, evil, evil, evil);
        var reader = Csv(await rte.GetStringAsync("/api/v1/audit.csv?entity=F"));
        var row = Assert.Single(reader.Select(x => (Entity: x["EntityId"].ToString(), Action: x["Action"].ToString(), Before: x["BeforeJson"].ToString(), After: x["AfterJson"].ToString())));
        Assert.Equal(("'" + evil, "'" + evil, "'" + evil, "'" + evil), row);
        Assert.Equal("", AuditEndpoints.Safe(null));
        Assert.Equal("plain", AuditEndpoints.Safe("plain"));
        Assert.Equal("a=b", AuditEndpoints.Safe("a=b"));   // only a leading character matters
        Assert.Equal("{\"a\":-1}", AuditEndpoints.Safe("{\"a\":-1}"));
    }

    [Fact]
    public async Task Over_the_row_cap_the_csv_says_so_in_headers_and_a_final_row_and_keeps_the_newest_rows()
    {
        using var f = new ApiFactory();
        var capped = f.WithWebHostBuilder(b => b.UseSetting("Audit:CsvMaxRows", "3"));   // same database, tiny cap
        var rte = capped.CreateClient();
        (await rte.PostAsJsonAsync("/auth/dev-login", new { email = "rte@x.com", name = "rte", role = Roles.RTE })).EnsureSuccessStatusCode();
        var rteId = UserId(f, "rte@x.com");
        for (var i = 0; i < 5; i++) Ev(f, Ts(i), rteId, null, "K", $"e{i}", "A");
        var r = await rte.GetAsync("/api/v1/audit.csv?entity=K");
        Assert.Equal("true", r.Headers.GetValues("X-Audit-Truncated").Single());
        Assert.Equal("5", r.Headers.GetValues("X-Audit-Total-Rows").Single());
        var reader = Csv(await r.Content.ReadAsStringAsync());
        var ids = reader.Select(x => (Id: x["Id"].ToString(), Entity: x["EntityId"].ToString())).ToList();
        Assert.Equal(4, ids.Count);                                   // 3 events + the marker row
        Assert.Equal(["e4", "e3", "e2"], ids.Take(3).Select(x => x.Entity));
        Assert.StartsWith("# truncated: 3 of 5", ids[3].Id);

        var under = await rte.GetAsync("/api/v1/audit.csv?entity=K&action=zzz");   // nothing matches: header only, not truncated
        Assert.Equal("false", under.Headers.GetValues("X-Audit-Truncated").Single());
        var underText = await under.Content.ReadAsStringAsync();
        Assert.StartsWith("Id,OccurredAt,", underText);                // an empty export still carries its header
        Assert.Empty(Csv(underText));
    }

    [Fact]
    public async Task A_large_export_streams_in_batches_across_more_than_one_page()
    {
        var (f, rte, rteId, _) = await Setup(); using var _f = f;
        Sql(f, "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i < 1200) " +
               $"INSERT INTO AuditEvents(OccurredAt,ActorUserId,EntityType,EntityId,Action) SELECT '2026-10-01T10:00:00Z','{rteId}','Big',CAST(i AS TEXT),'A' FROM n");
        var ids = Csv(await rte.GetStringAsync("/api/v1/audit.csv?entity=Big")).Select(x => long.Parse(x["Id"].ToString())).ToList();
        Assert.Equal(1200, ids.Count);
        Assert.Equal(1200, ids.Distinct().Count());
        Assert.Equal(ids.OrderByDescending(x => x).ToList(), ids);
    }

    // ---- immutability and read-only ---------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Reading_the_audit_log_writes_nothing_there_is_no_write_route_and_the_table_stays_append_only()
    {
        var (f, rte, rteId, _) = await Setup(); using var _f = f;
        Ev(f, Ts(1), rteId, null, "R", "1", "A");
        var count = () => Scalar(f, "SELECT COUNT(*) FROM AuditEvents");
        var n = count();
        await rte.GetAsync("/api/v1/audit"); await rte.GetAsync("/api/v1/audit?entity=R"); await rte.GetAsync("/api/v1/audit.csv"); await rte.GetAsync("/api/v1/audit/entity-types");
        Assert.Equal(n, count());                                     // reads are not audited

        foreach (var m in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
        {
            var resp = await rte.SendAsync(new HttpRequestMessage(m, "/api/v1/audit") { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") });
            Assert.False(resp.IsSuccessStatusCode, $"{m} /audit answered {resp.StatusCode}");
        }
        Assert.Equal(n, count());

        Assert.Throws<SqliteException>(() => Sql(f, "UPDATE AuditEvents SET Action='x'"));
        Assert.Throws<SqliteException>(() => Sql(f, "DELETE FROM AuditEvents"));
        Assert.Equal(n, count());
    }
}
