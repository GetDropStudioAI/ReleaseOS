using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using nietras.SeparatedValues;
using ReleaseMgmt.Domain.Common;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// REOS-118 (RT3, D47): the workspace's clients. A client belongs to one workspace; only an On-Demand client has a CERT environment, and it is one of that workspace's. The service
/// answers a readable 422 and the trigger holds the same rule behind it. Every write stamps Version, honours If-Match and writes its audit row in the same transaction. The CSV import
/// and export carry the same rules and re-import unchanged.
/// </summary>
public class ClientTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
    private static long Count(ApiFactory f, string sql) => long.Parse(Scalar(f, sql));
    private static long Audits(ApiFactory f, string id, string action) => Count(f, $"SELECT COUNT(*) FROM AuditEvents WHERE EntityType='Client' AND EntityId='{id}' AND Action='{action}'");

    private static async Task<HttpResponseMessage> Send(HttpClient c, HttpMethod m, string url, object? body, int? ifMatch)
    {
        using var req = new HttpRequestMessage(m, url);
        if (body is not null) req.Content = JsonContent.Create(body);
        if (ifMatch is int v) req.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{v}\""));
        return await c.SendAsync(req);
    }

    private static async Task<(ApiFactory F, HttpClient Rte, string CertId, string ProdId)> World()
    {
        var f = new ApiFactory();
        var rte = await As(f, Roles.RTE, "rte@x.com");
        var cert = await Json(await rte.PostAsJsonAsync("/api/v1/environments", new { name = "CERT", kind = "Staging" }));
        var prod = await Json(await rte.PostAsJsonAsync("/api/v1/environments", new { name = "Production", kind = "Production" }));
        return (f, rte, cert.GetProperty("id").GetString()!, prod.GetProperty("id").GetString()!);
    }

    private static Task<HttpResponseMessage> Make(HttpClient c, string name, string segment, string? env = null) =>
        c.PostAsJsonAsync("/api/v1/clients", new { name, segment, certEnvironmentId = env });

    [Fact]
    public async Task A_standard_client_has_no_environment_and_an_on_demand_client_names_one_and_each_create_is_audited()
    {
        var (f, rte, cert, _) = await World(); using var _f = f;
        var std = await Make(rte, "Acme", "Standard");
        Assert.Equal(HttpStatusCode.OK, std.StatusCode);
        var a = await Json(std);
        Assert.Equal("Standard", a.GetProperty("segment").GetString());
        Assert.Equal(JsonValueKind.Null, a.GetProperty("certEnvironment").ValueKind);
        Assert.Equal(1, a.GetProperty("version").GetInt32());

        var od = await Json(await Make(rte, "Globex", "OnDemand", cert));
        Assert.Equal("OnDemand", od.GetProperty("segment").GetString());
        Assert.Equal("CERT", od.GetProperty("certEnvironment").GetProperty("name").GetString());
        Assert.Equal(cert, od.GetProperty("certEnvironment").GetProperty("id").GetString());

        Assert.Equal(1, Audits(f, a.GetProperty("id").GetString()!, "Create"));
        Assert.Equal(1, Audits(f, od.GetProperty("id").GetString()!, "Create"));
        Assert.Equal(new[] { "Acme", "Globex" }, (await rte.GetFromJsonAsync<JsonElement>("/api/v1/clients")).EnumerateArray().Select(x => x.GetProperty("name").GetString()).ToArray());
        Assert.Equal("1", Scalar(f, "SELECT COUNT(*) FROM Clients WHERE Name='Acme' AND LastChangedByUserId IS NOT NULL AND LastChangedAt IS NOT NULL"));
    }

    [Fact]
    public async Task The_segment_rule_gives_a_readable_422_from_the_service_and_nothing_is_written()
    {
        var (f, rte, cert, _) = await World(); using var _f = f;
        var standardWithEnv = await Make(rte, "Acme", "Standard", cert);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, standardWithEnv.StatusCode);
        var b = await Json(standardWithEnv);
        Assert.Equal("ClientInvalid", b.GetProperty("guard").GetString());
        Assert.Contains("Only an On-Demand client has a CERT environment", b.GetProperty("message").GetString());

        var onDemandWithout = await Json(await Make(rte, "Globex", "OnDemand"));
        Assert.Equal("ClientInvalid", onDemandWithout.GetProperty("guard").GetString());
        Assert.Contains("needs a CERT environment", onDemandWithout.GetProperty("message").GetString());

        Assert.Equal("ClientInvalid", (await Json(await Make(rte, "Initech", "Premium"))).GetProperty("guard").GetString());
        Assert.Equal("ClientInvalid", (await Json(await Make(rte, "   ", "Standard"))).GetProperty("guard").GetString());
        Assert.Equal("FieldTooLong", (await Json(await Make(rte, new string('x', 201), "Standard"))).GetProperty("guard").GetString());   // the API's body limit answers before the service
        var missing = await Json(await Make(rte, "Hooli", "OnDemand", "no-such-environment"));
        Assert.Equal("ClientInvalid", missing.GetProperty("guard").GetString());
        Assert.Contains("does not exist in this workspace", missing.GetProperty("message").GetString());
        Assert.Equal(0, Count(f, "SELECT COUNT(*) FROM Clients"));
        Assert.Equal(0, Count(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='Client'"));
    }

    [Fact]
    public async Task The_trigger_holds_the_same_rule_when_a_row_is_written_around_the_service()
    {
        var (f, rte, cert, _) = await World(); using var _f = f;
        var std = (await Json(await Make(rte, "Acme", "Standard"))).GetProperty("id").GetString()!;
        var od = (await Json(await Make(rte, "Globex", "OnDemand", cert))).GetProperty("id").GetString()!;
        string Fails(string sql) => Assert.Throws<SqliteException>(() => Sql(f, sql)).Message;
        Assert.Contains("Only an On-Demand client has a CERT environment", Fails($"UPDATE Clients SET CertEnvironmentId='{cert}' WHERE Id='{std}'"));
        Assert.Contains("needs a CERT environment", Fails($"UPDATE Clients SET CertEnvironmentId=NULL WHERE Id='{od}'"));
        Assert.Contains("Only an On-Demand client has a CERT environment", Fails($"UPDATE Clients SET Segment='Standard' WHERE Id='{od}'"));
        Assert.Contains("Only an On-Demand client has a CERT environment", Fails($"INSERT INTO Clients(Id,Name,Segment,CertEnvironmentId,CreatedAt) VALUES('x1','X','Standard','{cert}','2026-10-01T00:00:00Z')"));
        Assert.Contains("needs a CERT environment", Fails("INSERT INTO Clients(Id,Name,Segment,CreatedAt) VALUES('x2','Y','OnDemand','2026-10-01T00:00:00Z')"));
    }

    [Fact]
    public async Task A_client_belongs_to_one_workspace_and_its_environment_is_that_workspaces()
    {
        var (f, rte, cert, _) = await World(); using var _f = f;
        Sql(f, @"INSERT INTO Workspaces(Id,Name,Slug,CreatedAt) VALUES('w2','EU','eu','2026-10-01T00:00:00Z');
                 INSERT INTO Environments(Id,Name,Kind,CreatedAt,WorkspaceId) VALUES('envw2','CERT','Staging','2026-10-01T00:00:00Z','w2');
                 INSERT INTO Clients(Id,Name,Segment,CertEnvironmentId,CreatedAt,WorkspaceId) VALUES('cw2','Elsewhere','OnDemand','envw2','2026-10-01T00:00:00Z','w2');");
        // The API of the default workspace cannot see, name or reach the other workspace's rows
        Assert.Empty((await rte.GetFromJsonAsync<JsonElement>("/api/v1/clients")).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await rte.GetAsync("/api/v1/clients/cw2")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(rte, HttpMethod.Patch, "/api/v1/clients/cw2", new { name = "Hijack" }, null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rte.PostAsync("/api/v1/clients/cw2:archive", null)).StatusCode);
        var foreignEnv = await Make(rte, "Hooli", "OnDemand", "envw2");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, foreignEnv.StatusCode);
        Assert.Equal("ClientInvalid", (await Json(foreignEnv)).GetProperty("guard").GetString());
        Assert.Equal("Elsewhere", Scalar(f, "SELECT Name FROM Clients WHERE Id='cw2'"));   // untouched

        // The same name is free in each workspace
        Assert.Equal(HttpStatusCode.OK, (await Make(rte, "Elsewhere", "Standard")).StatusCode);

        // The workspace triggers are the backstop for a row written around the service
        var ownRow = (await Json(await Make(rte, "Local", "OnDemand", cert))).GetProperty("id").GetString()!;
        Assert.Contains("The environment belongs to another workspace", Assert.Throws<SqliteException>(() => Sql(f, $"UPDATE Clients SET CertEnvironmentId='envw2' WHERE Id='{ownRow}'")).Message);
        Assert.Contains("The environment belongs to another workspace", Assert.Throws<SqliteException>(() => Sql(f, "INSERT INTO Clients(Id,Name,Segment,CertEnvironmentId,CreatedAt) VALUES('bad','Bad','OnDemand','envw2','2026-10-01T00:00:00Z')")).Message);
        Assert.Contains("A row cannot move to another workspace", Assert.Throws<SqliteException>(() => Sql(f, $"UPDATE Clients SET WorkspaceId='w2' WHERE Id='{ownRow}'")).Message);
    }

    [Fact]
    public async Task A_name_is_unique_in_the_workspace_ignoring_case()
    {
        var (f, rte, _, _) = await World(); using var _f = f;
        await Make(rte, "Acme", "Standard");
        var dup = await Make(rte, "  ACME ", "Standard");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, dup.StatusCode);
        Assert.Equal("ClientNameTaken", (await Json(dup)).GetProperty("guard").GetString());
        Assert.Equal(1, Count(f, "SELECT COUNT(*) FROM Clients"));
    }

    [Fact]
    public async Task Edits_stamp_Version_honour_If_Match_audit_in_the_same_transaction_and_switching_segment_moves_the_environment()
    {
        var (f, rte, cert, prod) = await World(); using var _f = f;
        var made = await Json(await Make(rte, "Globex", "OnDemand", cert));
        var id = made.GetProperty("id").GetString()!;

        var renamed = await Send(rte, HttpMethod.Patch, $"/api/v1/clients/{id}", new { name = "Globex Corp" }, 1);
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var r = await Json(renamed);
        Assert.Equal(2, r.GetProperty("version").GetInt32());
        Assert.Equal("Globex Corp", r.GetProperty("name").GetString());
        Assert.Equal(1, Audits(f, id, "Update"));
        var audit = Scalar(f, $"SELECT BeforeJson || ' -> ' || AfterJson FROM AuditEvents WHERE EntityType='Client' AND EntityId='{id}' AND Action='Update'");
        Assert.Contains("\"Name\":\"Globex\"", audit); Assert.Contains("\"Name\":\"Globex Corp\"", audit);

        // A stale If-Match is a 409 with the current row, and nothing changes
        var stale = await Send(rte, HttpMethod.Patch, $"/api/v1/clients/{id}", new { name = "Stale" }, 1);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("Globex Corp", await stale.Content.ReadAsStringAsync());
        Assert.Equal(1, Audits(f, id, "Update"));

        // A different CERT environment, then Standard: the environment goes with it
        var moved = await Json(await Send(rte, HttpMethod.Patch, $"/api/v1/clients/{id}", new { certEnvironmentId = prod }, 2));
        Assert.Equal("Production", moved.GetProperty("certEnvironment").GetProperty("name").GetString());
        var standard = await Json(await Send(rte, HttpMethod.Patch, $"/api/v1/clients/{id}", new { segment = "Standard" }, 3));
        Assert.Equal("Standard", standard.GetProperty("segment").GetString());
        Assert.Equal(JsonValueKind.Null, standard.GetProperty("certEnvironment").ValueKind);
        Assert.Equal(4, standard.GetProperty("version").GetInt32());

        // Back to On-Demand needs an environment in the same edit; Standard plus an environment is refused
        var bare = await Send(rte, HttpMethod.Patch, $"/api/v1/clients/{id}", new { segment = "OnDemand" }, 4);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bare.StatusCode);
        Assert.Contains("needs a CERT environment", (await Json(bare)).GetProperty("message").GetString());
        var both = await Send(rte, HttpMethod.Patch, $"/api/v1/clients/{id}", new { certEnvironmentId = cert }, 4);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, both.StatusCode);
        Assert.Contains("Only an On-Demand client has a CERT environment", (await Json(both)).GetProperty("message").GetString());
        var back = await Json(await Send(rte, HttpMethod.Patch, $"/api/v1/clients/{id}", new { segment = "On-Demand", certEnvironmentId = cert }, 4));
        Assert.Equal("OnDemand", back.GetProperty("segment").GetString());
        Assert.Equal(5, back.GetProperty("version").GetInt32());
        Assert.Equal(4, Audits(f, id, "Update"));

        // An edit that changes nothing writes nothing
        var same = await Json(await Send(rte, HttpMethod.Patch, $"/api/v1/clients/{id}", new { name = "Globex Corp" }, 5));
        Assert.Equal(5, same.GetProperty("version").GetInt32());
        Assert.Equal(4, Audits(f, id, "Update"));
    }

    [Fact]
    public async Task Archive_and_unarchive_are_audited_idempotent_and_keep_the_row()
    {
        var (f, rte, cert, _) = await World(); using var _f = f;
        var id = (await Json(await Make(rte, "Globex", "OnDemand", cert))).GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.Conflict, (await Send(rte, HttpMethod.Post, $"/api/v1/clients/{id}:archive", null, 9)).StatusCode);
        var archived = await Json(await Send(rte, HttpMethod.Post, $"/api/v1/clients/{id}:archive", null, 1));
        Assert.True(archived.GetProperty("archived").GetBoolean());
        Assert.Equal(2, archived.GetProperty("version").GetInt32());
        Assert.Equal(1, Audits(f, id, "Archive"));
        Assert.Empty((await rte.GetFromJsonAsync<JsonElement>("/api/v1/clients")).EnumerateArray());
        Assert.Single((await rte.GetFromJsonAsync<JsonElement>("/api/v1/clients?includeArchived=true")).EnumerateArray());
        await rte.PostAsync($"/api/v1/clients/{id}:archive", null);   // a repeat is a no-op
        Assert.Equal(1, Audits(f, id, "Archive"));
        Assert.Equal("2", Scalar(f, $"SELECT Version FROM Clients WHERE Id='{id}'"));
        var back = await Json(await rte.PostAsync($"/api/v1/clients/{id}:unarchive", null));
        Assert.False(back.GetProperty("archived").GetBoolean());
        Assert.Equal(1, Audits(f, id, "Unarchive"));
        // An archived environment cannot be chosen for a new CERT client, but the client that already has it keeps it
        var env = (await Json(await rte.PostAsJsonAsync("/api/v1/environments", new { name = "Old CERT", kind = "Staging" }))).GetProperty("id").GetString()!;
        var keeper = (await Json(await Make(rte, "Keeper", "OnDemand", env))).GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await rte.PostAsync($"/api/v1/environments/{env}:archive", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(rte, HttpMethod.Patch, $"/api/v1/clients/{keeper}", new { name = "Keeper 2" }, 1)).StatusCode);
        var refused = await Make(rte, "Newcomer", "OnDemand", env);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains("is archived", (await Json(refused)).GetProperty("message").GetString());
    }

    [Fact]
    public async Task Everyone_reads_planners_write_and_a_viewer_is_refused()
    {
        var (f, rte, cert, _) = await World(); using var _f = f;
        var rm = await As(f, Roles.ReleaseManager, "rm@x.com");
        var gov = await As(f, Roles.GovernanceOfficer, "gov@x.com");
        var viewer = await As(f, Roles.Viewer, "dana@x.com");
        var id = (await Json(await Make(rm, "Globex", "OnDemand", cert))).GetProperty("id").GetString()!;
        foreach (var c in new[] { rte, rm, gov, viewer })
        {
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/clients")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/v1/clients/{id}")).StatusCode);
        }
        foreach (var c in new[] { gov, viewer })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await Make(c, "Nope", "Standard")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Send(c, HttpMethod.Patch, $"/api/v1/clients/{id}", new { name = "Nope" }, 1)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsync($"/api/v1/clients/{id}:archive", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsync($"/api/v1/clients/{id}:unarchive", null)).StatusCode);
        }
        Assert.Equal(1, Count(f, "SELECT COUNT(*) FROM Clients"));
        Assert.Equal("1", Scalar(f, $"SELECT Version FROM Clients WHERE Id='{id}'"));
    }

    // ---- CSV import and export ---------------------------------------------------------------------------------------------------------
    private static string Cell(string s) => s.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    private static string Csv(params string[][] rows) => string.Join("\r\n", rows.Select(r => string.Join(',', r.Select(Cell)))) + "\r\n";
    private static Task<HttpResponseMessage> Preview(HttpClient c, string csv, string mode = "Upsert") =>
        c.PostAsync($"/api/v1/imports/Clients:preview?mode={mode}&fileName=clients.csv", new StringContent(csv, new UTF8Encoding(false), "text/csv"));
    private static async Task<JsonElement> Commit(HttpClient c, JsonElement preview)
    {
        var r = await c.PostAsJsonAsync($"/api/v1/imports/{preview.GetProperty("jobId").GetString()}:commit", new { acknowledgeDecertify = false });
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, text);
        return JsonDocument.Parse(text).RootElement;
    }
    private static int Counts(JsonElement p, string name) => p.GetProperty("counts").GetProperty(name).GetInt32();
    private static List<string[]> Table(string csv)
    {
        using var reader = Sep.New(',').Reader(o => o with { HasHeader = false, Unescape = true, DisableColCountCheck = true }).FromText(csv.TrimStart('﻿'));
        var rows = new List<string[]>();
        foreach (var r in reader) { var cells = new string[r.ColCount]; for (var i = 0; i < cells.Length; i++) cells[i] = r[i].ToString(); rows.Add(cells); }
        return rows;
    }

    [Fact]
    public async Task A_file_creates_and_updates_clients_with_the_same_rules_each_row_audited_and_the_export_re_imports_unchanged()
    {
        var (f, rte, _, _) = await World(); using var _f = f;
        var p = await Json(await Preview(rte, Csv(["Name", "Segment", "CertEnvironment"], ["Acme", "standard", ""], ["Globex", "On-Demand", "cert"], ["Initech", "ON-DEMAND", "Production"])));
        Assert.Equal(0, Counts(p, "errors")); Assert.Equal(3, Counts(p, "new"));
        await Commit(rte, p);
        Assert.Equal("Standard|", Scalar(f, "SELECT Segment || '|' || COALESCE(CertEnvironmentId,'') FROM Clients WHERE Name='Acme'"));
        Assert.Equal("OnDemand|CERT", Scalar(f, "SELECT c.Segment || '|' || e.Name FROM Clients c JOIN Environments e ON e.Id = c.CertEnvironmentId WHERE c.Name='Globex'"));
        Assert.Equal(3, Count(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='Client' AND Action='ImportInsert'"));
        Assert.Equal(3, Count(f, "SELECT COUNT(*) FROM Clients WHERE Version=1 AND LastChangedByUserId IS NOT NULL AND LastChangedAt IS NOT NULL"));

        var csv = await rte.GetStringAsync("/api/v1/exports/clients.csv");
        var table = Table(csv);
        Assert.Equal(new[] { "Name", "Segment", "CertEnvironment", "#Id", "#Archived", "#Version" }, table[0].Take(6).ToArray());   // the grid adds its workspace columns after
        Assert.Equal(new[] { "Acme", "Standard", "", }, table.Single(r => r[0] == "Acme").Take(3).ToArray());
        Assert.Equal(new[] { "Globex", "On-Demand", "CERT" }, table.Single(r => r[0] == "Globex").Take(3).ToArray());
        var again = await Json(await Preview(rte, csv));
        Assert.Equal(0, Counts(again, "errors")); Assert.Equal(3, Counts(again, "unchanged")); Assert.Equal(0, Counts(again, "new") + Counts(again, "updated"));

        // An update: Acme becomes On-Demand with an environment, Globex becomes Standard without naming the environment (it goes), Initech is left alone with the column absent
        var up = await Json(await Preview(rte, Csv(["Name", "Segment", "CertEnvironment"], ["Acme", "On-Demand", "Production"], ["Globex", "Standard", ""])));
        Assert.Equal(0, Counts(up, "errors")); Assert.Equal(2, Counts(up, "updated"));
        await Commit(rte, up);
        Assert.Equal("OnDemand|Production", Scalar(f, "SELECT c.Segment || '|' || e.Name FROM Clients c JOIN Environments e ON e.Id = c.CertEnvironmentId WHERE c.Name='Acme'"));
        Assert.Equal("Standard|", Scalar(f, "SELECT Segment || '|' || COALESCE(CertEnvironmentId,'') FROM Clients WHERE Name='Globex'"));
        Assert.Equal("2", Scalar(f, "SELECT Version FROM Clients WHERE Name='Acme'"));
        Assert.Equal(2, Count(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='Client' AND Action='ImportUpdate'"));

        var noColumn = await Json(await Preview(rte, Csv(["Name", "Segment"], ["Initech", "On-Demand"], ["Acme", "Standard"])));
        Assert.Equal(0, Counts(noColumn, "errors")); Assert.Equal(1, Counts(noColumn, "unchanged")); Assert.Equal(1, Counts(noColumn, "updated"));   // Initech keeps its environment; Acme loses it
    }

    [Fact]
    public async Task A_file_row_that_breaks_a_rule_is_an_error_with_its_row_and_column_and_nothing_is_written()
    {
        var (f, rte, cert, _) = await World(); using var _f = f;
        Sql(f, $"UPDATE Environments SET ArchivedAt='2026-10-01T00:00:00Z', Version=2 WHERE Id='{cert}'");
        var p = await Json(await Preview(rte, Csv(["Name", "Segment", "CertEnvironment"],
            ["A", "Standard", "Production"],        // 1: Standard with an environment
            ["B", "On-Demand", ""],                 // 2: On-Demand without one
            ["C", "On-Demand", "Prodcution"],       // 3: no such environment (a suggestion)
            ["D", "Premium", ""],                   // 4: no such segment
            ["E", "On-Demand", "CERT"],             // 5: archived environment
            ["Ok", "Standard", ""],
            ["Ok", "Standard", ""])));              // 7: duplicate
        var errors = p.GetProperty("errors").EnumerateArray().Select(e => (e.GetProperty("row").GetInt32(), e.GetProperty("column").GetString())).ToList();
        Assert.Equal([(1, "CertEnvironment"), (2, "CertEnvironment"), (3, "CertEnvironment"), (4, "Segment"), (5, "CertEnvironment"), (7, "Name")], errors);
        Assert.Contains("Production", p.GetProperty("errors").EnumerateArray().First(e => e.GetProperty("row").GetInt32() == 3).GetProperty("message").GetString());   // did you mean
        var commit = await rte.PostAsJsonAsync($"/api/v1/imports/{p.GetProperty("jobId").GetString()}:commit", new { acknowledgeDecertify = false });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, commit.StatusCode);
        Assert.Equal(0, Count(f, "SELECT COUNT(*) FROM Clients"));
        Assert.Equal(0, Count(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='Client'"));
        // Append refuses a name that exists
        await Make(rte, "Existing", "Standard");
        var append = await Json(await Preview(rte, Csv(["Name", "Segment"], ["existing", "Standard"]), "Append"));
        Assert.Equal(1, Counts(append, "errors"));
    }

    [Fact]
    public async Task Import_needs_a_planner()
    {
        var (f, _, _, _) = await World(); using var _f = f;
        var viewer = await As(f, Roles.Viewer, "dana@x.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await Preview(viewer, Csv(["Name", "Segment"], ["Acme", "Standard"]))).StatusCode);
        var kinds = await (await As(f, Roles.RTE, "rte2@x.com")).GetFromJsonAsync<JsonElement>("/api/v1/imports/kinds");
        Assert.Contains(kinds.EnumerateArray(), k => k.GetProperty("kind").GetString() == "Clients");
    }
}
