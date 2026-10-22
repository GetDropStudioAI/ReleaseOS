using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Infrastructure.Exports;
using UglyToad.PdfPig;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// REOS-54 / M8: the pilot's dress rehearsal. One High-risk release goes from the shipped "Standard release" template to an evidence pack a Governance
/// Officer can verify, through the HTTP API alone (no SQL writes, nothing kept in a spreadsheet), with If-Match required as in production and each person
/// doing only their own part: the RTE plans and runs it, a Release Manager approves the template and records Go/No-Go, a Governance Officer certifies the
/// Compliance gates, closes the Go condition, holds and closes the PIR and generates the pack, and a Viewer contributor completes the task, runbook step
/// and PIR action they own. The steps follow docs/PILOT_GUIDE.md. This proves the software path of the M8 acceptance; the acceptance itself (a real
/// release with the pilot RTE, and a Governance Officer accepting its pack) is human-led.
/// </summary>
public class PilotDressRehearsalTests
{
    private const string Title = "R26.40 Pilot dress rehearsal";
    private static readonly HttpMethod Post = HttpMethod.Post, Put = HttpMethod.Put;

    private static string Iso(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    private static async Task<HttpResponseMessage> Send(HttpClient c, HttpMethod m, string url, object? body = null, int? ifMatch = null)
    {
        var req = new HttpRequestMessage(m, url) { Content = JsonContent.Create(body ?? new { }) };
        if (ifMatch is int v) req.Headers.TryAddWithoutValidation("If-Match", $"\"{v}\"");
        return await c.SendAsync(req);
    }

    /// <summary>The step must succeed (2xx); otherwise the failure names the step, the status and the body the person would have seen.</summary>
    private static async Task<JsonElement> Ok(Task<HttpResponseMessage> call, string step)
    {
        var r = await call;
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{step}: {(int)r.StatusCode} {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static Task<JsonElement> Get(HttpClient c, string url) => Ok(c.GetAsync(url), "GET " + url);

    private static MultipartFormDataContent Evidence(string entityType, string entityId, string text, string fileName)
    {
        var form = new MultipartFormDataContent { { new StringContent(entityType), "entityType" }, { new StringContent(entityId), "entityId" } };
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
        file.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain");
        form.Add(file, "file", fileName);
        return form;
    }

    private static string Text(byte[] pdf)
    {
        using var d = PdfDocument.Open(pdf);
        return string.Join("\n", d.GetPages().Select(p => p.Text));
    }

    [Fact]
    public async Task A_high_risk_release_runs_from_the_standard_template_to_a_verified_evidence_pack_through_the_API_alone()
    {
        using var root = new ApiFactory(requireIfMatch: true);
        // The PDF licence is a host setting here, not the process environment that PdfExportTests changes while other classes run in parallel.
        using var app = root.WithWebHostBuilder(b => b.UseSetting("Pdf:QuestPdfLicense", "Community"));
        async Task<HttpClient> SignIn(string role, string email, string name)
        {
            var c = app.CreateClient();
            (await c.PostAsJsonAsync("/auth/dev-login", new { email, name, role })).EnsureSuccessStatusCode();
            return c;
        }
        var rte = await SignIn(Roles.RTE, "rae@pilot.test", "Rae Tran");
        var rm = await SignIn(Roles.ReleaseManager, "rel@pilot.test", "Rel Marsh");
        var gov = await SignIn(Roles.GovernanceOfficer, "gina@pilot.test", "Gina Ortiz");
        var qa = await SignIn(Roles.Viewer, "quinn@pilot.test", "Quinn Abbot");   // a contributor: owns one task, one step and one PIR action
        string rteId = UserId(root, "rae@pilot.test"), rmId = UserId(root, "rel@pilot.test"), govId = UserId(root, "gina@pilot.test"), qaId = UserId(root, "quinn@pilot.test");

        // ---- 1. A Release Manager approves the shipped template (it is seeded as a Draft: the seed never fabricates an approval) ----------------
        var standard = (await Get(rm, "/api/v1/templates")).EnumerateArray().Single(t => t.GetProperty("name").GetString() == "Standard release");
        var templateId = standard.GetProperty("id").GetString()!;
        await Ok(Send(rm, Post, $"/api/v1/templates/{templateId}:approve", ifMatch: standard.GetProperty("version").GetInt32()), "approve the template");

        // ---- 2. The RTE creates the train from it: products, window, the four default gates and the T-minus message plan in one step ------------
        var target = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(21);
        while (target.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) target = target.AddDays(1);
        var w0 = target.ToDateTime(new TimeOnly(6, 0), DateTimeKind.Utc);
        var created = await Ok(Send(rte, Post, "/api/v1/trains", new
        {
            title = Title, targetReleaseDate = target.ToString("yyyy-MM-dd"), riskTier = "High", templateId, windowStartsAt = Iso(w0), windowEndsAt = Iso(w0.AddHours(4)),
            products = new[] { new { productName = "Payments API", versionTag = "4.6.0", projectCode = "PAY" }, new { productName = "Card Portal", versionTag = "2.2.0", projectCode = "CRD" } },
        }), "create the train from the template");
        var id = created.GetProperty("id").GetString()!;
        var counts = created.GetProperty("created");
        Assert.Equal((2, 4, 6, true), (counts.GetProperty("products").GetInt32(), counts.GetProperty("gates").GetInt32(), counts.GetProperty("commSchedule").GetInt32(), counts.GetProperty("window").GetBoolean()));
        Assert.Equal("Planning", created.GetProperty("status").GetString());

        async Task<int> TrainVersion() => (await Get(rte, $"/api/v1/trains/{id}")).GetProperty("version").GetInt32();
        var gates = (await Get(rte, $"/api/v1/trains/{id}")).GetProperty("gates").EnumerateArray().ToDictionary(g => g.GetProperty("name").GetString()!, g => g.GetProperty("id").GetString()!);
        Assert.Equal(["CAB Approval", "Code Freeze", "Compliance Sign-off", "QA Sign-off"], gates.Keys.Order(StringComparer.Ordinal));
        var products = (await Get(rte, $"/api/v1/trains/{id}/products")).GetProperty("products").EnumerateArray().ToDictionary(p => p.GetProperty("name").GetString()!, p => p.GetProperty("id").GetString()!);

        // ---- 3. Change record and affected CI ---------------------------------------------------------------------------------------------------
        await Ok(Send(rte, Put, $"/api/v1/trains/{id}/change-record", new
        {
            justification = "Fee-schedule change required before month-end close.", implementationPlan = "Deploy Payments API, then Card Portal, then smoke test.",
            riskImpactAnalysis = "Card authorisation path; High risk.", backoutPlan = "Runbook section Rollback (R-090).", testPlan = "Regression RC2 and the card-auth smoke test.",
            communicationPlan = "T-minus messages from the train's plan.", cabDate = target.AddDays(-1).ToString("yyyy-MM-dd"),
        }, ifMatch: 0), "write the change record");
        await Ok(Send(rte, Post, $"/api/v1/trains/{id}/cis", new { ciName = "payments-prod" }), "add the affected CI");

        // ---- 4. The runbook, before Code Freeze certifies (the certification captures the baseline, steps included) ------------------------------
        async Task<JsonElement> Step(string code, string section, string title, string owner, int atMin, int min, string? product = null) =>
            await Ok(Send(rte, Post, $"/api/v1/trains/{id}/steps", new
            {
                stepCode = code, section, title, instructions = $"{title}: see the deployment notes.", ownerUserId = owner,
                plannedStartAt = w0.AddMinutes(atMin), plannedDurationMin = min, bundledProductId = product is null ? null : products[product],
            }), "add runbook step " + code);
        var pre = await Step("R-010", "PreCheck", "Pre-checks and health baseline", rteId, 0, 15);
        var pay = await Step("R-020", "Deploy", "Deploy Payments API", rteId, 15, 45, "Payments API");
        var portal = await Step("R-030", "Deploy", "Deploy Card Portal", rteId, 60, 30, "Card Portal");
        var smoke = await Step("R-040", "Verify", "Card-auth smoke test", qaId, 90, 30);
        var back = await Step("R-090", "Rollback", "Roll back Payments API", rteId, 120, 45);
        string S(JsonElement s) => s.GetProperty("id").GetString()!;
        await Ok(Send(rte, Put, $"/api/v1/steps/{S(portal)}/dependencies", new { dependsOn = new[] { S(pay) } }, portal.GetProperty("version").GetInt32()), "R-030 after R-020");
        await Ok(Send(rte, Put, $"/api/v1/steps/{S(smoke)}/dependencies", new { dependsOn = new[] { S(portal) } }, smoke.GetProperty("version").GetInt32()), "R-040 after R-030");
        // 120 min of work in a 240 min window with 45 min of rollback: 75 min spare, 62%, above the 30% minimum.
        var contingency = (await Get(rte, $"/api/v1/trains/{id}/advisories")).GetProperty("contingency");
        Assert.Equal(("ok", 62), (contingency.GetProperty("state").GetString(), contingency.GetProperty("percent").GetInt32()));

        // ---- 5. Checklists pasted in bulk, the way a team's existing list arrives -----------------------------------------------------------------
        const string paste = """
            # Code Freeze
            - Payments release branch cut and tagged @rae@pilot.test [Payments API]
            - Portal release branch cut and tagged @rae@pilot.test [Card Portal]
            # QA Sign-off
            - Regression suite RC2 green @quinn@pilot.test
            // compliance evidence is attached to the gate
            # Compliance Sign-off
            - SOX change evidence attached @rae@pilot.test
            # CAB Approval
            - CAB minutes attached @rae@pilot.test
            """;
        var preview = await Ok(Send(rte, Post, $"/api/v1/trains/{id}/tasks:parse", new { text = paste }), "parse the pasted checklist");
        Assert.True(preview.GetProperty("errorCount").GetInt32() + preview.GetProperty("warningCount").GetInt32() == 0, preview.ToString());
        var commit = await Ok(Send(rte, Post, $"/api/v1/trains/{id}/tasks:commit", new { previewId = preview.GetProperty("previewId").GetString(), acknowledgeDecertify = false }), "commit the checklist");
        Assert.Equal(5, commit.GetProperty("inserted").GetInt32());

        // ---- gate helpers: read the gate as the screen does, act with the version just read --------------------------------------------------------
        async Task<JsonElement> Gate(string name) => await Get(rte, $"/api/v1/gates/{gates[name]}");
        async Task GateAction(HttpClient who, string name, string action) =>
            await Ok(Send(who, Post, $"/api/v1/gates/{gates[name]}:{action}", ifMatch: (await Gate(name)).GetProperty("version").GetInt32()), $"{action} {name}");
        async Task CompleteTasks(HttpClient who, string name)
        {
            foreach (var t in (await Gate(name)).GetProperty("tasks").EnumerateArray())
                await Ok(Send(who, Post, $"/api/v1/tasks/{t.GetProperty("id").GetString()}:complete", ifMatch: t.GetProperty("version").GetInt32()), $"complete a {name} task");
        }
        async Task Attach(string name, string text, string file) =>
            await Ok(rte.PostAsync("/api/v1/attachments", Evidence("Gate", gates[name], text, file)), $"attach evidence to {name}");

        // ---- 6. Code Freeze and QA Sign-off, then Gated ---------------------------------------------------------------------------------------------
        await GateAction(rte, "Code Freeze", "start");
        await Attach("Code Freeze", "Branches release/4.6 and release/2.2 cut at 14:02.", "branch-cut.txt");
        await CompleteTasks(rte, "Code Freeze");
        await GateAction(rte, "Code Freeze", "certify");
        Assert.Equal("5", Scalar(root, $"SELECT json_array_length(SnapshotJson, '$.steps') FROM Baselines WHERE ReleaseTrainId = '{id}'"));   // captured by the certification

        await GateAction(rte, "QA Sign-off", "start");
        await CompleteTasks(qa, "QA Sign-off");   // the contributor ticks their own task; nothing else is open to them
        await GateAction(rte, "QA Sign-off", "certify");
        await Ok(Send(rte, Post, $"/api/v1/trains/{id}:advance", new { to = "Gated" }, await TrainVersion()), "advance to Gated");

        // ---- 7. Messages go out from the train's own plan, and each one is recorded ------------------------------------------------------------------
        async Task<JsonElement> Dispatch(string type)
        {
            var item = (await Get(rte, $"/api/v1/trains/{id}/comm-schedule")).EnumerateArray().Single(i => i.GetProperty("templateType").GetString() == type);
            var d = await Ok(Send(rte, Post, $"/api/v1/trains/{id}/comms:dispatch", new
            {
                templateId = item.GetProperty("commTemplateId").GetString(), scheduleItemId = item.GetProperty("id").GetString(), channel = "Copy", format = "Markdown",
            }, await TrainVersion()), $"dispatch the {type} message");
            Assert.Contains(Title, d.GetProperty("subject").GetString());
            return d;
        }
        await Dispatch("Tminus7");

        // ---- 8. Rehearsal (rollback included), then the attestation that cites it -----------------------------------------------------------------
        async Task<int> ExecVersion(string runId, string stepId) =>
            (await Get(rte, $"/api/v1/runs/{runId}")).GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("stepId").GetString() == stepId).GetProperty("version").GetInt32();
        async Task Execute(string runId, HttpClient who, JsonElement step)
        {
            foreach (var action in new[] { "start", "done" })
                await Ok(Send(who, Post, $"/api/v1/runs/{runId}/steps/{S(step)}:{action}", new { note = (string?)null }, await ExecVersion(runId, S(step))),
                    $"{action} {step.GetProperty("stepCode").GetString()}");
        }
        async Task EndRun(string runId) =>
            await Ok(Send(rte, Post, $"/api/v1/runs/{runId}:end", new { outcome = "Completed" }, (await Get(rte, $"/api/v1/runs/{runId}")).GetProperty("version").GetInt32()), "end the run");

        var rehearsal = (await Ok(Send(rte, Post, $"/api/v1/trains/{id}/runs", new { mode = "Rehearsal" }), "start the rehearsal")).GetProperty("id").GetString()!;
        foreach (var (who, step) in new[] { (rte, pre), (rte, pay), (rte, portal), (qa, smoke), (rte, back) }) await Execute(rehearsal, who, step);
        await EndRun(rehearsal);
        await Ok(Send(rte, Post, $"/api/v1/trains/{id}:rehearsed-rollback", new { runId = rehearsal, note = "R-090 rehearsed end to end in staging" }, await TrainVersion()),
            "attest the rollback rehearsal");

        // ---- 9. Compliance gates: the RTE completes the tasks, a Governance Officer certifies (segregation of duties) ----------------------------------
        foreach (var (name, file) in new[] { ("Compliance Sign-off", "sox-evidence.txt"), ("CAB Approval", "cab-minutes.txt") })
        {
            await GateAction(rte, name, "start");
            await Attach(name, $"{name} evidence for {Title}.", file);
            await CompleteTasks(rte, name);
            await GateAction(gov, name, "certify");
        }

        // ---- 10. Go/No-Go: only the decision is missing now; the readiness line and :advance agree ---------------------------------------------------
        var readiness = await Get(rte, $"/api/v1/trains/{id}/readiness?target=Executing");
        Assert.Equal(["ExecutingRequiresGo"], readiness.GetProperty("blockers").EnumerateArray().Select(b => b.GetProperty("failure").GetProperty("guard").GetString()!).ToArray());
        var decision = await Ok(Send(rm, Post, $"/api/v1/trains/{id}/gonogo", new
        {
            decision = "GoWithConditions", notes = "Go once the CISO signs the pen-test exceptions.",
            conditions = new[] { new { text = "Card Portal pen-test exceptions signed by the CISO", ownerUserId = govId, expiresAt = DateTime.UtcNow.AddDays(2) } },
        }, await TrainVersion()), "record Go/No-Go");
        await Dispatch("GoNoGo");
        var condition = decision.GetProperty("conditions").EnumerateArray().Single();
        await Ok(Send(gov, Post, $"/api/v1/conditions/{condition.GetProperty("id").GetString()}:close", ifMatch: condition.GetProperty("version").GetInt32()), "close the condition");
        Assert.True((await Get(rte, $"/api/v1/trains/{id}/readiness?target=Executing")).GetProperty("ready").GetBoolean());
        await Ok(Send(rte, Post, $"/api/v1/trains/{id}:advance", new { to = "Executing" }, await TrainVersion()), "advance to Executing");

        // ---- 11. The live run: actuals are recorded beside the plan, never over it ---------------------------------------------------------------------
        await Dispatch("Cutover");
        var live = (await Ok(Send(rte, Post, $"/api/v1/trains/{id}/runs", new { mode = "Live" }), "start the live run")).GetProperty("id").GetString()!;
        foreach (var (who, step) in new[] { (rte, pre), (rte, pay), (rte, portal), (qa, smoke) }) await Execute(live, who, step);
        await Get(rte, $"/api/v1/runs/{live}/forecast");
        await EndRun(live);
        Assert.Equal(Iso(w0.AddMinutes(15)), (await Get(rte, $"/api/v1/trains/{id}/steps")).EnumerateArray().Single(s => S(s) == S(pay)).GetProperty("plannedStartAt").GetString());

        // ---- 12. Complete, hypercare, PIR --------------------------------------------------------------------------------------------------------------
        await Ok(Send(rte, Post, $"/api/v1/trains/{id}:complete", new { closeCode = "Successful", notes = "All products live; smoke test green." }, await TrainVersion()), "complete the train");
        await Dispatch("Complete");
        Assert.Equal(4, (await Get(rte, $"/api/v1/trains/{id}/comm-schedule")).EnumerateArray().Count(i => i.GetProperty("sentAt").ValueKind == JsonValueKind.String));
        await Ok(Send(rte, Post, $"/api/v1/trains/{id}/known-issues", new { title = "Portal tooltip shows the old fee", severity = "Low", workaround = "None needed" }), "log a known issue");
        await Ok(Send(rte, Post, $"/api/v1/trains/{id}:exit-hypercare", ifMatch: await TrainVersion()), "exit hypercare");

        await Ok(Send(rte, Post, $"/api/v1/trains/{id}/pir"), "start the PIR");
        var action = await Ok(Send(rte, Post, $"/api/v1/trains/{id}/pir/actions", new { text = "Add the fee tooltip to the smoke test", ownerUserId = qaId, dueOn = target.AddDays(7).ToString("yyyy-MM-dd") }), "add a PIR action");
        await Ok(Send(qa, Post, $"/api/v1/pir-actions/{action.GetProperty("id").GetString()}:complete", ifMatch: action.GetProperty("version").GetInt32()), "the owner completes the PIR action");
        async Task<int> PirVersion() => (await Get(gov, $"/api/v1/trains/{id}/pir")).GetProperty("pir").GetProperty("version").GetInt32();
        await Ok(Send(gov, Post, $"/api/v1/trains/{id}/pir:hold", new { summary = "Release went to plan; one cosmetic issue logged and accepted as Low." }, await PirVersion()), "hold the PIR");
        await Ok(Send(gov, Post, $"/api/v1/trains/{id}/pir:close", ifMatch: await PirVersion()), "close the PIR");

        // ---- 13. The Governance Officer generates the evidence pack and verifies it ----------------------------------------------------------------------
        var pdfJob = (await Ok(Send(gov, Post, $"/api/v1/trains/{id}/export-jobs", new { kind = "EvidencePack" }), "request the evidence pack")).GetProperty("id").GetString()!;
        var zipJob = (await Ok(Send(gov, Post, $"/api/v1/trains/{id}/export-jobs", new { kind = "EvidencePack", format = "zip" }), "request the evidence ZIP")).GetProperty("id").GetString()!;
        var worker = app.Services.GetRequiredService<ExportWorker>();
        while (await worker.RunOnceAsync() > 0) { }
        async Task<byte[]> Verified(string job)
        {
            var j = await Get(gov, $"/api/v1/export-jobs/{job}");
            Assert.True(j.GetProperty("status").GetString() == "Done", j.ToString());
            var r = await gov.GetAsync($"/api/v1/export-jobs/{job}/file");
            var bytes = await r.Content.ReadAsByteArrayAsync();
            Assert.Equal(j.GetProperty("sha256").GetString(), Sha(bytes));                       // the stored hash verifies against the file
            Assert.Equal(j.GetProperty("sha256").GetString(), r.Headers.GetValues("X-Content-SHA256").Single());
            return bytes;
        }
        var pack = Text(await Verified(pdfJob));
        // What docs/PILOT_GUIDE.md section 5 asks the Governance Officer to check: every section, the gates, who certified and decided, and the outcome.
        foreach (var expected in new[] { Title, "1 · Change record", "2 · Approvals and certifications", "3 · Go/No-Go decision", "5 · Evidence manifest (all 3)",
                     "6 · Runbook actuals", "7 · Gate transition log", "8 · Audit events", "9 · Metric snapshot",
                     "Code Freeze", "QA Sign-off", "Compliance Sign-off", "CAB Approval", "Gina Ortiz", "Rel Marsh", "Successful", "R-090" })
            Assert.Contains(expected, pack);

        using var zip = new ZipArchive(new MemoryStream(await Verified(zipJob)), ZipArchiveMode.Read);
        var manifest = new StreamReader(zip.GetEntry("manifest.csv")!.Open(), Encoding.UTF8).ReadToEnd();
        var files = zip.Entries.Where(e => e.FullName.StartsWith("files/")).ToList();
        Assert.Equal(3, files.Count);
        foreach (var entry in files)
        {
            using var s = entry.Open(); var ms = new MemoryStream(); s.CopyTo(ms);
            Assert.Contains(Sha(ms.ToArray()), manifest);                                             // every file matches its manifest line
        }
        Assert.Equal("3", Scalar(root, $"SELECT COUNT(*) FROM Attachments WHERE ReleaseTrainId = '{id}' AND IsLocked = 1"));   // certified gates locked their evidence

        // ---- 14. Where it ended, and who did it ------------------------------------------------------------------------------------------------------------
        var train = await Get(gov, $"/api/v1/trains/{id}");
        Assert.Equal(("Complete", "Successful"), (train.GetProperty("status").GetString(), train.GetProperty("closeCode").GetString()));
        var actors = Scalar(root, $"SELECT group_concat(DISTINCT ActorUserId) FROM AuditEvents WHERE ReleaseTrainId = '{id}'").Split(',');
        Assert.Superset(new HashSet<string> { rteId, rmId, govId, qaId }, actors.ToHashSet());   // every person's part is on the record under their own name
    }
}
