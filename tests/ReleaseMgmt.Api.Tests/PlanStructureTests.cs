using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ReleaseMgmt.Domain.Common;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// REOS-81: products and gates added, edited and removed on a train. Each action and each refusal: train state, references, owner xor,
/// stale Version (409), Viewer (403), a PATCH never touching Status, one audit row per write, and the train's Version bumped.
/// SeedTrain: t1 Planning, target Fri 2026-10-30; g1 Code Freeze (seq 1, Pending, task k1) and g2 Compliance Sign-off (seq 2, InProgress, task k2).
/// </summary>
public class PlanStructureTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private static async Task<(ApiFactory F, HttpClient C, string Rte)> Setup()
    {
        var f = new ApiFactory();
        var c = await As(f, Roles.RTE, "rte@x.com");
        var id = UserId(f, "rte@x.com");
        SeedTrain(f, id, id);
        return (f, c, id);
    }

    private static Task<HttpResponseMessage> Send(HttpClient c, HttpMethod m, string url, object? body = null, int? ifMatch = null)
    {
        var req = new HttpRequestMessage(m, url);
        if (body is not null) req.Content = JsonContent.Create(body);
        if (ifMatch is int v) req.Headers.TryAddWithoutValidation("If-Match", v.ToString());
        return c.SendAsync(req);
    }

    private static async Task<(HttpStatusCode Status, string? Guard, string? Message)> Refusal(Task<HttpResponseMessage> call)
    {
        var r = await call;
        var text = await r.Content.ReadAsStringAsync();
        if (text.Length == 0) return (r.StatusCode, null, null);
        var j = JsonDocument.Parse(text).RootElement;
        return (r.StatusCode, j.TryGetProperty("guard", out var g) ? g.GetString() : null, j.TryGetProperty("message", out var m) ? m.GetString() : null);
    }

    private static string TrainVersion(ApiFactory f, string id = "t1") => Scalar(f, $"SELECT Version FROM ReleaseTrains WHERE Id='{id}'");
    private static string Audits(ApiFactory f, string entityId, string action) => Scalar(f, $"SELECT COUNT(*) FROM AuditEvents WHERE EntityId='{entityId}' AND Action='{action}'");

    /// <summary>t2 Executing, t3 Complete, t4 Aborted, t5 Gated (inserted directly: no transition trigger fires on INSERT), each with one Pending gate and one product.</summary>
    private static void SeedOtherStates(ApiFactory f, string owner) => Sql(f, $@"
        INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CurrentStatus,CloseCode,CreatedAt,UpdatedAt) VALUES
          ('t2','Executing','2026-10-30','Low','Executing',NULL,'2026-10-01T00:00:00Z','2026-10-01T00:00:00Z'),
          ('t3','Complete','2026-10-30','Low','Complete','Successful','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z'),
          ('t4','Aborted','2026-10-30','Low','Aborted',NULL,'2026-10-01T00:00:00Z','2026-10-01T00:00:00Z'),
          ('t5','Gated','2026-10-30','Low','Gated',NULL,'2026-10-01T00:00:00Z','2026-10-01T00:00:00Z');
        INSERT INTO StageGates(Id,ReleaseTrainId,GateName,SequenceOrder,OffsetDays,DueOn,RequiredBeforeStatus,OwnerUserId,Status) VALUES
          ('g2x','t2','Hypercare exit',1,0,'2026-10-30','Complete','{owner}','Pending'),
          ('g3x','t3','Hypercare exit',1,0,'2026-10-30','Complete','{owner}','Pending'),
          ('g4x','t4','Hypercare exit',1,0,'2026-10-30','Complete','{owner}','Pending'),
          ('g5x','t5','CAB Approval',1,1,'2026-10-29','Executing','{owner}','Pending');
        INSERT INTO BundledProducts(Id,ReleaseTrainId,ProductName,VersionTag,ProjectCode) VALUES
          ('p2','t2','Payments','1.0','PAY'),('p3','t3','Payments','1.0','PAY'),('p4','t4','Payments','1.0','PAY'),('p5','t5','Payments','1.0','PAY');");

    // ---- products ------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_product_is_added_edited_and_removed_with_one_audit_row_each_and_the_train_version_moves()
    {
        var (f, c, _) = await Setup(); using var _f = f;
        var v0 = int.Parse(TrainVersion(f));

        var add = await c.PostAsJsonAsync("/api/v1/trains/t1/products", new { name = "  Payments API ", versionTag = "4.2.0" });
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        var p = await Json(add);
        var id = p.GetProperty("id").GetString()!;
        Assert.Equal("Payments API", p.GetProperty("productName").GetString());
        Assert.Equal("", p.GetProperty("projectCode").GetString());   // optional (Q-081a)
        Assert.Equal(1, p.GetProperty("version").GetInt32());
        Assert.Equal("1", Audits(f, id, "Add"));
        Assert.Equal((v0 + 1).ToString(), TrainVersion(f));
        Assert.Equal("Payments API", (await Json(await c.GetAsync("/api/v1/trains/t1/products"))).GetProperty("products")[0].GetProperty("name").GetString());

        var edit = await Send(c, HttpMethod.Patch, $"/api/v1/products/{id}", new { versionTag = "4.2.1", projectCode = "PAY" }, ifMatch: 1);
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var e = await Json(edit);
        Assert.Equal(("4.2.1", "PAY", 2), (e.GetProperty("versionTag").GetString(), e.GetProperty("projectCode").GetString(), e.GetProperty("version").GetInt32()));
        Assert.Equal("1", Audits(f, id, "Update"));
        Assert.Contains("4.2.0", Scalar(f, $"SELECT BeforeJson FROM AuditEvents WHERE EntityId='{id}' AND Action='Update'"));

        // The same values again change nothing: no version bump, no audit row.
        Assert.Equal(HttpStatusCode.OK, (await Send(c, HttpMethod.Patch, $"/api/v1/products/{id}", new { versionTag = "4.2.1" }, ifMatch: 2)).StatusCode);
        Assert.Equal("1", Audits(f, id, "Update"));

        var del = await Send(c, HttpMethod.Delete, $"/api/v1/products/{id}", ifMatch: 2);
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        Assert.Equal("0", Scalar(f, $"SELECT COUNT(*) FROM BundledProducts WHERE Id='{id}'"));
        Assert.Equal("1", Audits(f, id, "Remove"));
        Assert.Equal((v0 + 3).ToString(), TrainVersion(f));
        Assert.Equal(HttpStatusCode.NotFound, (await Send(c, HttpMethod.Delete, $"/api/v1/products/{id}", ifMatch: 2)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/api/v1/trains/nope/products", new { name = "x", versionTag = "1" })).StatusCode);
    }

    [Fact]
    public async Task Invalid_and_duplicate_products_are_refused_with_a_readable_422()
    {
        var (f, c, _) = await Setup(); using var _f = f;
        async Task<(HttpStatusCode, string?, string?)> Add(object body) => await Refusal(c.PostAsJsonAsync("/api/v1/trains/t1/products", body));

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "InvalidProduct"), Drop(await Add(new { name = " ", versionTag = "1.0" })));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "InvalidProduct"), Drop(await Add(new { name = "Ledger" })));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "FieldTooLong"), Drop(await Add(new { name = new string('x', 201), versionTag = "1.0" })));   // REOS-66's filter answers before the service (which still checks)
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/v1/trains/t1/products", new { name = "Ledger", versionTag = "1.0" })).StatusCode);
        var dup = await Add(new { name = "LEDGER", versionTag = "2.0" });   // case-insensitive: the parser's [Product] lookup is
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "DuplicateProduct"), Drop(dup));
        Assert.Contains("LEDGER", dup.Item3);
        Assert.Equal("1", Scalar(f, "SELECT COUNT(*) FROM BundledProducts WHERE ReleaseTrainId='t1'"));
    }

    [Fact]
    public async Task A_product_still_referenced_by_tasks_steps_blockers_or_links_cannot_be_removed_and_the_refusal_names_them()
    {
        var (f, c, rte) = await Setup(); using var _f = f;
        Sql(f, $@"
            INSERT INTO BundledProducts(Id,ReleaseTrainId,ProductName,VersionTag,ProjectCode) VALUES('p1','t1','Ledger','1.0','LED');
            UPDATE ChecklistTasks SET BundledProductId='p1' WHERE Id IN ('k1','k2');
            INSERT INTO ExternalLinks(Id,ReleaseTrainId,EntityType,EntityId,SourceSystem,ExternalKey) VALUES('l1','t1','Product','p1','Jira','LED-1');
            INSERT INTO RunbookSteps(Id,ReleaseTrainId,BundledProductId,StepCode,Section,Title,OwnerUserId,PlannedStartAt,PlannedDurationMin) VALUES('s1','t1','p1','R-001','Deploy','Deploy','{rte}','2026-10-30T06:00:00Z',10);");
        var (status, guard, message) = await Refusal(Send(c, HttpMethod.Delete, "/api/v1/products/p1", ifMatch: 1));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "ProductInUse"), (status, guard));
        Assert.Equal("Ledger is still used by 2 checklist tasks, 1 runbook step and 1 external link; move or remove those first", message);
        Assert.Equal("1", Scalar(f, "SELECT COUNT(*) FROM BundledProducts WHERE Id='p1'"));
        Assert.Equal("0", Audits(f, "p1", "Remove"));

        Sql(f, "DELETE FROM ExternalLinks; DELETE FROM RunbookSteps; UPDATE ChecklistTasks SET BundledProductId=NULL;" +
               "INSERT INTO Blockers(Id,ReleaseTrainId,BundledProductId,Title,Severity,RaisedAt) VALUES('b1','t1','p1','Broken build','High','2026-10-02T00:00:00Z');");
        Assert.Equal("Ledger is still used by 1 blocker; move or remove those first", (await Refusal(Send(c, HttpMethod.Delete, "/api/v1/products/p1", ifMatch: 1))).Message);
        Sql(f, "DELETE FROM Blockers;");
        Assert.Equal(HttpStatusCode.OK, (await Send(c, HttpMethod.Delete, "/api/v1/products/p1", ifMatch: 1)).StatusCode);
    }

    [Fact]
    public async Task Products_are_locked_once_the_train_is_executing_or_closed()
    {
        var (f, c, rte) = await Setup(); using var _f = f;
        SeedOtherStates(f, rte);
        foreach (var (train, product, guard) in new[] { ("t2", "p2", "PlanLocked"), ("t3", "p3", "TrainClosed"), ("t4", "p4", "TrainClosed") })
        {
            Assert.Equal((HttpStatusCode.UnprocessableEntity, guard), Drop(await Refusal(c.PostAsJsonAsync($"/api/v1/trains/{train}/products", new { name = "New", versionTag = "1" }))));
            Assert.Equal((HttpStatusCode.UnprocessableEntity, guard), Drop(await Refusal(Send(c, HttpMethod.Patch, $"/api/v1/products/{product}", new { versionTag = "9" }, 1))));
            Assert.Equal((HttpStatusCode.UnprocessableEntity, guard), Drop(await Refusal(Send(c, HttpMethod.Delete, $"/api/v1/products/{product}", ifMatch: 1))));
        }
        Assert.Equal("The train is Executing; its products are locked once deployment has started",
            (await Refusal(c.PostAsJsonAsync("/api/v1/trains/t2/products", new { name = "New", versionTag = "1" }))).Message);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/v1/trains/t5/products", new { name = "New", versionTag = "1" })).StatusCode);   // Gated: still open
        Sql(f, "UPDATE ReleaseTrains SET ArchivedAt='2026-10-02T00:00:00Z' WHERE Id='t5'");
        Assert.Equal("The train is archived; its products can no longer be changed",
            (await Refusal(c.PostAsJsonAsync("/api/v1/trains/t5/products", new { name = "Other", versionTag = "1" }))).Message);
    }

    [Fact]
    public async Task A_stale_product_version_is_a_409_with_the_current_row_and_a_viewer_is_refused()
    {
        var (f, c, _) = await Setup(); using var _f = f;
        Sql(f, "INSERT INTO BundledProducts(Id,ReleaseTrainId,ProductName,VersionTag,ProjectCode,Version) VALUES('p1','t1','Ledger','1.0','LED',3);");
        var r = await Send(c, HttpMethod.Patch, "/api/v1/products/p1", new { versionTag = "2.0" }, ifMatch: 2);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        var cur = (await Json(r)).GetProperty("current");
        Assert.Equal((3, "1.0"), (cur.GetProperty("version").GetInt32(), cur.GetProperty("versionTag").GetString()));
        Assert.Equal(HttpStatusCode.Conflict, (await Send(c, HttpMethod.Delete, "/api/v1/products/p1", ifMatch: 2)).StatusCode);
        Assert.Equal("1", Scalar(f, "SELECT COUNT(*) FROM BundledProducts WHERE Id='p1'"));

        var viewer = await As(f, Roles.Viewer, "v@x.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/v1/trains/t1/products", new { name = "X", versionTag = "1" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(viewer, HttpMethod.Patch, "/api/v1/products/p1", new { versionTag = "2" }, 3)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(viewer, HttpMethod.Delete, "/api/v1/products/p1", ifMatch: 3)).StatusCode);
        var gov = await As(f, Roles.GovernanceOfficer, "gov@x.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await gov.PostAsJsonAsync("/api/v1/trains/t1/products", new { name = "X", versionTag = "1" })).StatusCode);
        var rm = await As(f, Roles.ReleaseManager, "rm@x.com");
        Assert.Equal(HttpStatusCode.OK, (await rm.PostAsJsonAsync("/api/v1/trains/t1/products", new { name = "X", versionTag = "1" })).StatusCode);
    }

    [Fact]
    public async Task With_If_Match_required_a_product_delete_without_it_is_428()
    {
        using var f = new ApiFactory(requireIfMatch: true);
        var c = await As(f, Roles.RTE, "rte@x.com");
        SeedTrain(f, UserId(f, "rte@x.com"), UserId(f, "rte@x.com"));
        Sql(f, "INSERT INTO BundledProducts(Id,ReleaseTrainId,ProductName,VersionTag,ProjectCode) VALUES('p1','t1','Ledger','1.0','LED');");
        Assert.Equal((HttpStatusCode)428, (await Send(c, HttpMethod.Delete, "/api/v1/products/p1")).StatusCode);
        Assert.Equal((HttpStatusCode)428, (await Send(c, HttpMethod.Patch, "/api/v1/gates/g1", new { gateName = "x" })).StatusCode);
        Assert.Equal((HttpStatusCode)428, (await Send(c, HttpMethod.Delete, "/api/v1/gates/g1")).StatusCode);
    }

    // ---- gates ---------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_gate_added_by_offset_gets_its_due_date_in_business_days_skipping_holidays()
    {
        var (f, c, rte) = await Setup(); using var _f = f;
        Sql(f, "INSERT OR IGNORE INTO Holidays(Day,Name) VALUES('2026-10-27','Test holiday');");
        var v0 = int.Parse(TrainVersion(f));
        var r = await c.PostAsJsonAsync("/api/v1/trains/t1/gates", new { gateName = "QA Sign-off", gateClass = "Standard", offsetDays = 3, requiredBeforeStatus = "Gated", ownerUserId = rte });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var g = await Json(r);
        // Fri 30 Oct minus 3 business days: Thu 29, Wed 28, (Tue 27 holiday), Mon 26.
        Assert.Equal(("2026-10-26", 3, 3, "Pending"), (g.GetProperty("dueOn").GetString(), g.GetProperty("offsetDays").GetInt32(), g.GetProperty("sequenceOrder").GetInt32(), g.GetProperty("status").GetString()));
        var id = g.GetProperty("id").GetString()!;
        Assert.Equal("1", Audits(f, id, "Add"));
        Assert.Equal((v0 + 1).ToString(), TrainVersion(f));
        Assert.Equal(rte, Scalar(f, $"SELECT LastChangedByUserId FROM StageGates WHERE Id='{id}'"));
        Assert.Equal("0", Scalar(f, $"SELECT COUNT(*) FROM GateTransitions WHERE StageGateId='{id}'"));
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/api/v1/trains/nope/gates", new { gateName = "x", offsetDays = 1, ownerUserId = rte })).StatusCode);
    }

    [Fact]
    public async Task A_gate_added_by_due_date_stores_the_matching_offset_and_a_weekend_date_is_refused_with_the_nearest_dates()
    {
        var (f, c, rte) = await Setup(); using var _f = f;
        var g = await Json(await c.PostAsJsonAsync("/api/v1/trains/t1/gates", new { gateName = "CAB Approval", gateClass = "Compliance", dueOn = "2026-10-29", requiredBeforeStatus = "Executing", ownerUserId = rte }));
        Assert.Equal((1, "2026-10-29", "Compliance"), (g.GetProperty("offsetDays").GetInt32(), g.GetProperty("dueOn").GetString(), g.GetProperty("gateClass").GetString()));

        var weekend = await Refusal(c.PostAsJsonAsync("/api/v1/trains/t1/gates", new { gateName = "Docs", dueOn = "2026-10-25", ownerUserId = rte }));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "InvalidGate"), Drop(weekend));
        Assert.Equal("2026-10-25 is not a business day; the nearest due dates are 2026-10-23 and 2026-10-26", weekend.Message);
        Assert.Equal("InvalidGate", (await Refusal(c.PostAsJsonAsync("/api/v1/trains/t1/gates", new { gateName = "Docs", dueOn = "2026-11-02", ownerUserId = rte }))).Guard);   // after target
        Assert.Equal("InvalidGate", (await Refusal(c.PostAsJsonAsync("/api/v1/trains/t1/gates", new { gateName = "Docs", offsetDays = -1, ownerUserId = rte }))).Guard);
        Assert.Equal("InvalidGate", (await Refusal(c.PostAsJsonAsync("/api/v1/trains/t1/gates", new { gateName = "Docs", offsetDays = 366, ownerUserId = rte }))).Guard);
        var both = await Refusal(c.PostAsJsonAsync("/api/v1/trains/t1/gates", new { gateName = "Docs", offsetDays = 2, dueOn = "2026-10-28", ownerUserId = rte }));
        Assert.Equal("Give the gate either a business-day offset before the target or a due date, not both", both.Message);
        Assert.Equal("InvalidGate", (await Refusal(c.PostAsJsonAsync("/api/v1/trains/t1/gates", new { gateName = "Docs", ownerUserId = rte }))).Guard);   // neither
    }

    [Fact]
    public async Task A_gate_needs_exactly_one_existing_active_owner_a_name_unique_in_the_train_and_known_class_and_status()
    {
        var (f, c, rte) = await Setup(); using var _f = f;
        Sql(f, "INSERT INTO Teams(Id,Handle,Name) VALUES('tm1','platform','Platform'); INSERT INTO Users(Id,Email,DisplayName,Role,IsActive) VALUES('u-off','off@x.com','Off Duty','RTE',0);");
        async Task<(HttpStatusCode, string?, string?)> Add(object body) => await Refusal(c.PostAsJsonAsync("/api/v1/trains/t1/gates", body));

        var two = await Add(new { gateName = "Docs", offsetDays = 2, ownerUserId = rte, ownerTeamId = "tm1" });
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "InvalidGate", "A gate needs exactly one owner: a person or a team"), two);
        Assert.Equal("A gate needs exactly one owner: a person or a team", (await Add(new { gateName = "Docs", offsetDays = 2 })).Item3);
        Assert.Equal("The owner does not exist", (await Add(new { gateName = "Docs", offsetDays = 2, ownerUserId = "nobody" })).Item3);
        Assert.Equal("The owning team does not exist", (await Add(new { gateName = "Docs", offsetDays = 2, ownerTeamId = "nope" })).Item3);
        Assert.Equal("Off Duty is inactive and cannot be given new work", (await Add(new { gateName = "Docs", offsetDays = 2, ownerUserId = "u-off" })).Item3);
        Assert.Equal("InvalidGate", (await Add(new { gateName = "Docs", gateClass = "Bespoke", offsetDays = 2, ownerTeamId = "tm1" })).Item2);
        Assert.Equal("InvalidGate", (await Add(new { gateName = "Docs", requiredBeforeStatus = "Planning", offsetDays = 2, ownerTeamId = "tm1" })).Item2);
        Assert.Equal("InvalidGate", (await Add(new { gateName = "  ", offsetDays = 2, ownerTeamId = "tm1" })).Item2);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "DuplicateGateName", "This train already has a gate named code freeze"), await Add(new { gateName = "code freeze", offsetDays = 2, ownerTeamId = "tm1" }));

        var ok = await Json(await c.PostAsJsonAsync("/api/v1/trains/t1/gates", new { gateName = "Docs", offsetDays = 2, ownerTeamId = "tm1" }));
        Assert.Equal(("tm1", JsonValueKind.Null), (ok.GetProperty("ownerTeamId").GetString(), ok.GetProperty("ownerUserId").ValueKind));
        Assert.Equal("3", Scalar(f, "SELECT COUNT(*) FROM StageGates WHERE ReleaseTrainId='t1'"));   // only the valid one was added
    }

    [Fact]
    public async Task A_gate_inserted_at_a_position_moves_later_gates_up()
    {
        var (f, c, rte) = await Setup(); using var _f = f;
        var r = await c.PostAsJsonAsync("/api/v1/trains/t1/gates", new { gateName = "Kick-off", offsetDays = 9, ownerUserId = rte, sequenceOrder = 1 });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("Kick-off,Code Freeze,Compliance Sign-off", Scalar(f, "SELECT group_concat(GateName) FROM (SELECT GateName FROM StageGates WHERE ReleaseTrainId='t1' ORDER BY SequenceOrder)"));
        Assert.Equal("1,2,3", Scalar(f, "SELECT group_concat(SequenceOrder) FROM (SELECT SequenceOrder FROM StageGates WHERE ReleaseTrainId='t1' ORDER BY SequenceOrder)"));
        Assert.Equal("2", Scalar(f, "SELECT Version FROM StageGates WHERE Id='g1'"));   // moved rows are versioned
        Assert.Contains("Code Freeze", Scalar(f, $"SELECT AfterJson FROM AuditEvents WHERE EntityId='{(await Json(r)).GetProperty("id").GetString()}' AND Action='Add'"));
        Assert.Equal("4", (await Json(await c.PostAsJsonAsync("/api/v1/trains/t1/gates", new { gateName = "Last", offsetDays = 0, ownerUserId = rte, sequenceOrder = 99 }))).GetProperty("sequenceOrder").GetInt32().ToString());

    }

    [Fact]
    public async Task No_gate_may_be_placed_before_a_certified_or_waived_gate()
    {
        var (f, c, rte) = await Setup(); using var _f = f;
        Sql(f, $@"
            INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CreatedAt,UpdatedAt) VALUES('t9','R9','2026-10-30','Low','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z');
            INSERT INTO StageGates(Id,ReleaseTrainId,GateName,SequenceOrder,OffsetDays,DueOn,OwnerUserId,Status,CertifiedByUserId,CertifiedAt) VALUES
              ('gc','t9','Code Freeze',1,5,'2026-10-23','{rte}','Certified','{rte}','2026-10-02T00:00:00Z'),
              ('gp','t9','QA Sign-off',2,3,'2026-10-27','{rte}','Pending',NULL,NULL);");
        var (status, guard, message) = await Refusal(c.PostAsJsonAsync("/api/v1/trains/t9/gates", new { gateName = "Early", offsetDays = 9, ownerUserId = rte, sequenceOrder = 1 }));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "SequenceBeforeCertified"), (status, guard));
        Assert.Contains("Code Freeze", message);
        var ok = await Json(await c.PostAsJsonAsync("/api/v1/trains/t9/gates", new { gateName = "Between", offsetDays = 4, ownerUserId = rte, sequenceOrder = 2 }));
        Assert.Equal(2, ok.GetProperty("sequenceOrder").GetInt32());
        Assert.Equal("3", Scalar(f, "SELECT SequenceOrder FROM StageGates WHERE Id='gp'"));
    }

    [Fact]
    public async Task A_gate_is_edited_by_name_offset_owner_and_required_before_with_one_audit_row_and_never_its_status()
    {
        var (f, c, rte) = await Setup(); using var _f = f;
        Sql(f, "INSERT INTO Teams(Id,Handle,Name) VALUES('tm1','platform','Platform');");
        var v0 = int.Parse(TrainVersion(f));
        var r = await Send(c, HttpMethod.Patch, "/api/v1/gates/g1", new { gateName = "Code Freeze (all repos)", offsetDays = 6, ownerTeamId = "tm1", requiredBeforeStatus = "Executing" }, ifMatch: 1);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var g = await Json(r);
        Assert.Equal(("Code Freeze (all repos)", 6, "2026-10-22", "tm1", "Executing", 2),
            (g.GetProperty("gateName").GetString(), g.GetProperty("offsetDays").GetInt32(), g.GetProperty("dueOn").GetString(), g.GetProperty("ownerTeamId").GetString(), g.GetProperty("requiredBeforeStatus").GetString(), g.GetProperty("version").GetInt32()));
        Assert.Equal(JsonValueKind.Null, g.GetProperty("ownerUserId").ValueKind);   // setting the team replaced the person
        Assert.Equal("Pending", g.GetProperty("status").GetString());
        Assert.Equal("1", Audits(f, "g1", "Update"));
        Assert.Equal((v0 + 1).ToString(), TrainVersion(f));

        var due = await Json(await Send(c, HttpMethod.Patch, "/api/v1/gates/g1", new { dueOn = "2026-10-26" }, ifMatch: 2));
        Assert.Equal((4, "2026-10-26"), (due.GetProperty("offsetDays").GetInt32(), due.GetProperty("dueOn").GetString()));

        // Rule 2: a PATCH naming a status is refused, and the status does not move.
        var (status, guard, _) = await Refusal(Send(c, HttpMethod.Patch, "/api/v1/gates/g1", new { status = "Certified" }, ifMatch: 3));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "StatusNotEditable"), (status, guard));
        Assert.Equal("Pending", Scalar(f, "SELECT Status FROM StageGates WHERE Id='g1'"));
        Assert.Equal("InvalidGate", (await Refusal(Send(c, HttpMethod.Patch, "/api/v1/gates/g1", new { sequenceOrder = 5 }, ifMatch: 3))).Guard);
        Assert.Equal("InvalidGate", (await Refusal(Send(c, HttpMethod.Patch, "/api/v1/gates/g1", new { ownerUserId = rte, ownerTeamId = "tm1" }, ifMatch: 3))).Guard);
        Assert.Equal("InvalidGate", (await Refusal(Send(c, HttpMethod.Patch, "/api/v1/gates/g1", new { offsetDays = 1, dueOn = "2026-10-29" }, ifMatch: 3))).Guard);
        Assert.Equal("DuplicateGateName", (await Refusal(Send(c, HttpMethod.Patch, "/api/v1/gates/g1", new { gateName = "COMPLIANCE SIGN-OFF" }, ifMatch: 3))).Guard);
        Assert.Equal("3", Scalar(f, "SELECT Version FROM StageGates WHERE Id='g1'"));   // refusals change nothing

        // An InProgress gate is still editable (owner change), and keeps its status.
        var g2 = await Json(await Send(c, HttpMethod.Patch, "/api/v1/gates/g2", new { ownerUserId = rte }, ifMatch: 1));
        Assert.Equal(("InProgress", rte), (g2.GetProperty("status").GetString(), g2.GetProperty("ownerUserId").GetString()));
        Assert.Equal(HttpStatusCode.NotFound, (await Send(c, HttpMethod.Patch, "/api/v1/gates/nope", new { gateName = "x" }, ifMatch: 1)).StatusCode);
    }

    [Fact]
    public async Task Certified_and_waived_gates_cannot_be_redefined()
    {
        var (f, c, rte) = await Setup(); using var _f = f;
        Sql(f, $@"
            INSERT INTO StageGates(Id,ReleaseTrainId,GateName,SequenceOrder,OffsetDays,DueOn,OwnerUserId,Status,CertifiedByUserId,CertifiedAt) VALUES
              ('gc','t1','Certified one',8,1,'2026-10-29','{rte}','Certified','{rte}','2026-10-02T00:00:00Z'),
              ('gw','t1','Waived one',9,1,'2026-10-29','{rte}','Waived','{rte}','2026-10-02T00:00:00Z');");
        var cert = await Refusal(Send(c, HttpMethod.Patch, "/api/v1/gates/gc", new { gateName = "Renamed" }, ifMatch: 1));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "GateNotEditable", "Certified one is certified; reopen it before changing it"), cert);
        Assert.Equal("Waived one is waived, which is final; add a new gate instead", (await Refusal(Send(c, HttpMethod.Patch, "/api/v1/gates/gw", new { offsetDays = 2 }, ifMatch: 1))).Message);
    }

    [Fact]
    public async Task On_a_gated_train_a_gate_cannot_be_made_required_before_gated()
    {
        var (f, c, rte) = await Setup(); using var _f = f;
        SeedOtherStates(f, rte);
        var add = await Refusal(c.PostAsJsonAsync("/api/v1/trains/t5/gates", new { gateName = "Late QA", offsetDays = 2, requiredBeforeStatus = "Gated", ownerUserId = rte }));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "RequiredBeforePassed"), Drop(add));
        Assert.StartsWith("The train is already Gated", add.Item3);
        Assert.Equal("RequiredBeforePassed", (await Refusal(Send(c, HttpMethod.Patch, "/api/v1/gates/g5x", new { requiredBeforeStatus = "Gated" }, ifMatch: 1))).Guard);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/v1/trains/t5/gates", new { gateName = "Late QA", offsetDays = 2, requiredBeforeStatus = "Executing", ownerUserId = rte })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(c, HttpMethod.Patch, "/api/v1/gates/g5x", new { gateName = "CAB" }, ifMatch: 1)).StatusCode);
    }

    [Fact]
    public async Task Gates_are_locked_once_the_train_is_executing_or_closed()
    {
        var (f, c, rte) = await Setup(); using var _f = f;
        SeedOtherStates(f, rte);
        foreach (var (train, gate, guard) in new[] { ("t2", "g2x", "PlanLocked"), ("t3", "g3x", "TrainClosed"), ("t4", "g4x", "TrainClosed") })
        {
            Assert.Equal((HttpStatusCode.UnprocessableEntity, guard), Drop(await Refusal(c.PostAsJsonAsync($"/api/v1/trains/{train}/gates", new { gateName = "New", offsetDays = 0, requiredBeforeStatus = "Complete", ownerUserId = rte }))));
            Assert.Equal((HttpStatusCode.UnprocessableEntity, guard), Drop(await Refusal(Send(c, HttpMethod.Patch, $"/api/v1/gates/{gate}", new { gateName = "x" }, 1))));
            Assert.Equal((HttpStatusCode.UnprocessableEntity, guard), Drop(await Refusal(Send(c, HttpMethod.Delete, $"/api/v1/gates/{gate}", ifMatch: 1))));
        }
        Assert.Equal("The train is Complete; its gates can no longer be changed", (await Refusal(Send(c, HttpMethod.Delete, "/api/v1/gates/g3x", ifMatch: 1))).Message);
    }

    [Fact]
    public async Task A_pending_gate_is_removed_with_its_tasks_and_one_audit_row()
    {
        var (f, c, _) = await Setup(); using var _f = f;
        var v0 = int.Parse(TrainVersion(f));
        var r = await Send(c, HttpMethod.Delete, "/api/v1/gates/g1", ifMatch: 1);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("0", Scalar(f, "SELECT COUNT(*) FROM StageGates WHERE Id='g1'"));
        Assert.Equal("0", Scalar(f, "SELECT COUNT(*) FROM ChecklistTasks WHERE Id='k1'"));
        Assert.Equal("1", Audits(f, "g1", "Remove"));
        Assert.Contains("\"tasksRemoved\":1", Scalar(f, "SELECT AfterJson FROM AuditEvents WHERE EntityId='g1' AND Action='Remove'"));
        Assert.Equal((v0 + 1).ToString(), TrainVersion(f));
        Assert.Equal(HttpStatusCode.NotFound, (await Send(c, HttpMethod.Delete, "/api/v1/gates/g1", ifMatch: 1)).StatusCode);
    }

    [Fact]
    public async Task Only_a_pending_gate_without_history_evidence_or_references_can_be_removed()
    {
        var (f, c, rte) = await Setup(); using var _f = f;
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "GateNotRemovable", "Compliance Sign-off is in progress; only a pending gate can be removed"),
            await Refusal(Send(c, HttpMethod.Delete, "/api/v1/gates/g2", ifMatch: 1)));

        Sql(f, $@"INSERT INTO Attachments(Id,ReleaseTrainId,EntityType,EntityId,FileName,ContentType,SizeBytes,Sha256,StoragePath,UploadedByUserId,UploadedAt)
                  VALUES('a1','t1','Task','k1','tag.txt','text/plain',10,'{new string('a', 64)}','x/a1','{rte}','2026-10-02T00:00:00Z');
                  INSERT INTO ExternalLinks(Id,ReleaseTrainId,EntityType,EntityId,SourceSystem,ExternalKey) VALUES('l1','t1','Gate','g1','Jira','REL-1');");
        var refused = await Refusal(Send(c, HttpMethod.Delete, "/api/v1/gates/g1", ifMatch: 1));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "GateInUse", "Code Freeze still has 1 evidence attachment and 1 external link; remove or move those first"), refused);

        Sql(f, "DELETE FROM Attachments; DELETE FROM ExternalLinks; INSERT INTO Blockers(Id,ReleaseTrainId,StageGateId,Title,Severity,RaisedAt) VALUES('b1','t1','g1','Late','Low','2026-10-02T00:00:00Z');");
        Assert.Equal("Code Freeze still has 1 blocker; remove or move those first", (await Refusal(Send(c, HttpMethod.Delete, "/api/v1/gates/g1", ifMatch: 1))).Item3);
        Sql(f, "DELETE FROM Blockers;");

        // History: a gate that moved and came back is not Pending, but the transition log alone also blocks (the log is append-only).
        Sql(f, $"INSERT INTO GateTransitions(StageGateId,FromStatus,ToStatus,ActorUserId,OccurredAt) VALUES('g1','Pending','InProgress','{rte}','2026-10-02T00:00:00Z');");
        Assert.Equal("Code Freeze has a transition history; it cannot be removed", (await Refusal(Send(c, HttpMethod.Delete, "/api/v1/gates/g1", ifMatch: 1))).Item3);
        Assert.Equal("1", Scalar(f, "SELECT COUNT(*) FROM StageGates WHERE Id='g1'"));
        Assert.Equal("0", Audits(f, "g1", "Remove"));
    }

    [Fact]
    public async Task A_stale_gate_version_is_a_409_with_the_current_row_and_a_viewer_or_owner_who_is_not_a_planner_is_refused()
    {
        var (f, c, rte) = await Setup(); using var _f = f;
        var r = await Send(c, HttpMethod.Patch, "/api/v1/gates/g1", new { gateName = "Freeze" }, ifMatch: 7);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        var cur = (await Json(r)).GetProperty("current");
        Assert.Equal((1, "Code Freeze", "Pending"), (cur.GetProperty("version").GetInt32(), cur.GetProperty("gateName").GetString(), cur.GetProperty("status").GetString()));
        Assert.Equal(HttpStatusCode.Conflict, (await Send(c, HttpMethod.Delete, "/api/v1/gates/g1", ifMatch: 7)).StatusCode);
        Assert.Equal("1", Scalar(f, "SELECT COUNT(*) FROM StageGates WHERE Id='g1'"));

        var viewer = await As(f, Roles.Viewer, "v@x.com");
        Sql(f, $"UPDATE StageGates SET OwnerUserId='{UserId(f, "v@x.com")}' WHERE Id='g1'");   // owning a gate lets you work it, not redefine it
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/v1/trains/t1/gates", new { gateName = "x", offsetDays = 1, ownerUserId = rte })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(viewer, HttpMethod.Patch, "/api/v1/gates/g1", new { gateName = "x" }, 1)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(viewer, HttpMethod.Delete, "/api/v1/gates/g1", ifMatch: 1)).StatusCode);
        var gov = await As(f, Roles.GovernanceOfficer, "gov@x.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(gov, HttpMethod.Delete, "/api/v1/gates/g1", ifMatch: 1)).StatusCode);
        Assert.Equal("Code Freeze", Scalar(f, "SELECT GateName FROM StageGates WHERE Id='g1'"));
    }

    [Fact]
    public async Task The_gate_detail_carries_the_definition_the_inspector_edits()
    {
        var (f, c, rte) = await Setup(); using var _f = f;
        var g = await Json(await c.GetAsync("/api/v1/gates/g1"));
        Assert.Equal((1, 5, "Gated", rte, "2026-10-30"), (g.GetProperty("sequenceOrder").GetInt32(), g.GetProperty("offsetDays").GetInt32(), g.GetProperty("requiredBeforeStatus").GetString(),
            g.GetProperty("ownerUserId").GetString(), g.GetProperty("targetReleaseDate").GetString()));
    }

    private static (HttpStatusCode, string?) Drop((HttpStatusCode S, string? G, string? M) r) => (r.S, r.G);
}
