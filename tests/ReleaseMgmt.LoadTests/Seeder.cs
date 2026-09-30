using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ReleaseMgmt.LoadTests;

/// <summary>What the load scenarios need to know about the seeded data (ids only; the rows live in SQLite).</summary>
public sealed class SeedResult
{
    public List<string> StreamTrains { get; } = [];                 // trains shown in the Stream (Planning, Gated, Executing, recent Complete)
    public List<string> Gates { get; } = [];                        // any gate, for gate-detail reads
    public Dictionary<string, string> GateTrain { get; } = [];
    public List<string> WriteTasks { get; } = [];                   // open tasks in non-certified gates: each is owned by exactly one virtual user
    public List<string> HotTasks { get; } = [];                     // deliberately contended: every user toggles these
    public Dictionary<string, string> TaskGate { get; } = [];
    public List<(string TrainId, string RunId)> ReadRuns { get; } = [];   // Live drills with a partly finished 30-step runbook (reads only)
    public Dictionary<string, List<string>> ReadRunSteps { get; } = [];
    public List<(string RunId, string StepId)> WriteSteps { get; } = [];  // Scheduled steps of Live runs, each used once: start then done
    public List<string> TrainsWithBlockers { get; } = [];
    public string[] EntityIds = [];
    public long AuditRows, Trains, GateRows, Tasks, Steps, Products, Blockers, Notifications;
}

/// <summary>
/// Bulk-seeds a migrated, reference-seeded database through plain SQL inside one transaction: N trains with gates, tasks, products, deployment windows,
/// blockers, 30-step runbooks with Live/Rehearsal runs, notifications, and 100 000 audit rows (INSERT into the append-only AuditEvents is allowed;
/// only UPDATE/DELETE are trigger-blocked). Rows are built to satisfy every CHECK and to avoid every AFTER INSERT trigger side effect.
/// </summary>
public static class Seeder
{
    private const string Iso = "yyyy-MM-dd'T'HH:mm:ss'Z'";
    private static string T(DateTime d) => d.ToString(Iso, CultureInfo.InvariantCulture);
    private static string D(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string Id() => Guid.CreateVersion7().ToString();

    public static SeedResult Seed(string dbPath, IReadOnlyList<string> userIds, int trainCount, int auditRows, int seed = 52)
    {
        var rnd = new Random(seed);
        var r = new SeedResult();
        var now = DateTime.UtcNow; now = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        var today = DateOnly.FromDateTime(now);
        using var c = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        c.Open();
        Exec(c, "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
        using var tx = c.BeginTransaction();

        string U() => userIds[rnd.Next(userIds.Count)];
        var risk = new[] { "Low", "Moderate", "High", "VeryHigh" };
        var gateNames = new (string Name, string Class, int Off, string Req)[]
        {
            ("Code Freeze", "Standard", 5, "Gated"), ("Security Review", "Standard", 4, "Gated"), ("Compliance Sign-off", "Compliance", 3, "Executing"),
            ("Performance Test", "Standard", 2, "Executing"), ("CAB Approval", "Standard", 1, "Executing"),
        };
        var sections = new[] { "PreCheck", "Deploy", "Deploy", "Deploy", "Verify", "Verify", "Hypercare" };
        var productNames = new[] { "Payments API", "Card Portal", "Ledger Service", "Risk Engine", "Notification Hub", "Reporting", "Identity", "Mobile BFF" };

        using var insTrain = Cmd(c, tx, "INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CurrentStatus,ChangeTicketNumber,ActualStartAt,ActualEndAt,CloseCode,RollbackRehearsedAt,RollbackRehearsedByUserId,LastChangedByUserId,LastChangedAt,Version,CreatedAt,UpdatedAt) VALUES($id,$title,$target,$risk,$status,$chg,$start,$end,$close,$rb,$rbu,$u,$at,1,$created,$created)");
        using var insWin = Cmd(c, tx, "INSERT INTO DeploymentWindows(Id,ReleaseTrainId,StartsAt,EndsAt) VALUES($id,$t,$s,$e)");
        using var insProd = Cmd(c, tx, "INSERT INTO BundledProducts(Id,ReleaseTrainId,ProductName,VersionTag,ProjectCode) VALUES($id,$t,$n,$v,$p)");
        using var insGate = Cmd(c, tx, "INSERT INTO StageGates(Id,ReleaseTrainId,GateName,GateClass,SequenceOrder,OffsetDays,DueOn,RequiredBeforeStatus,OwnerUserId,Status,CertifiedByUserId,CertifiedAt,LastChangedByUserId,LastChangedAt) VALUES($id,$t,$n,$c,$o,$off,$due,$req,$own,$st,$cb,$ca,$own,$at)");
        using var insTask = Cmd(c, tx, "INSERT INTO ChecklistTasks(Id,StageGateId,BundledProductId,TaskDescription,OwnerUserId,IsCompleted,SequenceOrder,CompletedAt,CompletedByUserId,LastChangedByUserId,LastChangedAt) VALUES($id,$g,$p,$d,$own,$done,$o,$ca,$cb,$own,$at)");
        using var insTrans = Cmd(c, tx, "INSERT INTO GateTransitions(StageGateId,FromStatus,ToStatus,ActorUserId,OccurredAt) VALUES($g,$f,$t,$a,$at)");
        using var insBlock = Cmd(c, tx, "INSERT INTO Blockers(Id,ReleaseTrainId,BundledProductId,StageGateId,Title,Severity,OwnerUserId,RaisedAt,ResolvedAt) VALUES($id,$t,$p,$g,$title,$sev,$own,$raised,$res)");
        using var insStep = Cmd(c, tx, "INSERT INTO RunbookSteps(Id,ReleaseTrainId,BundledProductId,StepCode,Section,Title,Instructions,OwnerUserId,PlannedStartAt,PlannedDurationMin) VALUES($id,$t,$p,$code,$sec,$title,$ins,$own,$at,$dur)");
        using var insDep = Cmd(c, tx, "INSERT INTO StepDependencies(StepId,DependsOnStepId) VALUES($s,$d)");
        using var insRun = Cmd(c, tx, "INSERT INTO RunbookRuns(Id,ReleaseTrainId,Mode,StartedAt,StartedByUserId,EndedAt,Outcome) VALUES($id,$t,$m,$s,$u,$e,$o)");
        using var insExec = Cmd(c, tx, "INSERT INTO StepExecutions(Id,RunId,StepId,Status,ActualStartAt,ActualEndAt,ActorUserId) VALUES($id,$r,$s,$st,$a,$e,$u)");
        using var insBase = Cmd(c, tx, "INSERT INTO Baselines(Id,ReleaseTrainId,CapturedAt,PlannedReleaseDate,SnapshotJson,CapturedByUserId) VALUES($id,$t,$at,$d,'{\"products\":[],\"gates\":[],\"steps\":[]}',$u)");

        for (var i = 0; i < trainCount; i++)
        {
            var tid = Id();
            // 40% Planning, 20% Gated, 20% Executing, 12% Complete, 8% Aborted: the Stream (everything but Aborted and old Complete) lists ~85% of the trains.
            var bucket = i % 25;
            var status = bucket < 10 ? "Planning" : bucket < 15 ? "Gated" : bucket < 20 ? "Executing" : bucket < 23 ? "Complete" : "Aborted";
            var target = today.AddDays(status switch { "Complete" => -rnd.Next(1, 20), "Aborted" => -rnd.Next(1, 60), "Executing" => rnd.Next(0, 3), "Gated" => rnd.Next(3, 20), _ => rnd.Next(10, 120) });
            var rk = risk[i % 4];
            var created = now.AddDays(-rnd.Next(30, 150));
            var ended = status == "Complete" ? (DateTime?)now.AddDays(-rnd.Next(1, 25)) : null;   // all inside the Stream's 30-day Complete window except a few older ones below
            if (status == "Complete" && i % 3 == 0) ended = now.AddDays(-rnd.Next(35, 90));
            var owner = U();
            var rbAt = rk is "High" or "VeryHigh" ? T(created.AddDays(20)) : null;
            Set(insTrain, ("$id", tid), ("$title", $"R26.{i + 1:000} {productNames[i % productNames.Length]} release"), ("$target", D(target)), ("$risk", rk), ("$status", status),
                ("$chg", $"CHG{100000 + i}"), ("$start", ended is null ? null : T(ended.Value.AddHours(-3))), ("$end", ended is null ? null : T(ended.Value)),
                ("$close", status == "Complete" ? "Successful" : null), ("$rb", rbAt), ("$rbu", rbAt is null ? null : owner), ("$u", owner), ("$at", T(now.AddDays(-rnd.Next(0, 10)))), ("$created", T(created)));
            insTrain.ExecuteNonQuery();
            if (status is not "Aborted" and not "Complete" || ended > now.AddDays(-30)) r.StreamTrains.Add(tid);
            r.Trains++;

            var wStart = new DateTime(target.Year, target.Month, target.Day, 22, 0, 0, DateTimeKind.Utc);
            Set(insWin, ("$id", Id()), ("$t", tid), ("$s", T(wStart)), ("$e", T(wStart.AddHours(4)))); insWin.ExecuteNonQuery();

            var products = new List<string>();
            var np = 3 + i % 3;
            for (var p = 0; p < np; p++)
            {
                var pid = Id(); products.Add(pid);
                Set(insProd, ("$id", pid), ("$t", tid), ("$n", productNames[(i + p) % productNames.Length]), ("$v", $"{2 + p}.{i % 10}.{p}"), ("$p", $"PRJ{(i + p) % 20:00}")); insProd.ExecuteNonQuery();
                r.Products++;
            }

            var gateIds = new List<string>();
            for (var g = 0; g < gateNames.Length; g++)
            {
                var (name, cls, off, req) = gateNames[g];
                var gid = Id(); gateIds.Add(gid);
                var gStatus = status switch
                {
                    "Complete" or "Executing" => "Certified",
                    "Gated" => g < 2 ? "Certified" : g == 2 ? "InProgress" : "Pending",
                    "Aborted" => g == 0 ? "Certified" : "Pending",
                    _ => g == 0 ? (i % 2 == 0 ? "InProgress" : "Pending") : "Pending",
                };
                var certAt = gStatus == "Certified" ? T(created.AddDays(10 + g)) : null;
                var gOwner = U();
                Set(insGate, ("$id", gid), ("$t", tid), ("$n", name), ("$c", cls), ("$o", g + 1), ("$off", off), ("$due", D(target.AddDays(-off))), ("$req", req), ("$own", gOwner), ("$st", gStatus),
                    ("$cb", gStatus == "Certified" ? gOwner : null), ("$ca", certAt), ("$at", T(now.AddDays(-rnd.Next(0, 10)))));
                insGate.ExecuteNonQuery();
                r.Gates.Add(gid); r.GateTrain[gid] = tid; r.GateRows++;
                if (gStatus != "Pending")
                {
                    Set(insTrans, ("$g", gid), ("$f", "Pending"), ("$t", "InProgress"), ("$a", gOwner), ("$at", T(created.AddDays(8 + g)))); insTrans.ExecuteNonQuery();
                    if (gStatus == "Certified") { Set(insTrans, ("$g", gid), ("$f", "InProgress"), ("$t", "Certified"), ("$a", gOwner), ("$at", certAt)); insTrans.ExecuteNonQuery(); }
                }
                for (var k = 0; k < 8; k++)
                {
                    var kid = Id();
                    // Tasks in a Certified gate are inserted complete (an incomplete insert would decertify the gate by trigger).
                    var done = gStatus == "Certified" || (gStatus == "InProgress" && k < 4);
                    var tOwner = U();
                    Set(insTask, ("$id", kid), ("$g", gid), ("$p", products[k % products.Count]), ("$d", $"{name}: verify item {k + 1} for {productNames[(i + k) % productNames.Length]}"), ("$own", tOwner),
                        ("$done", done ? 1 : 0), ("$o", k + 1), ("$ca", done ? T(created.AddDays(9 + g)) : null), ("$cb", done ? tOwner : null), ("$at", T(now.AddDays(-rnd.Next(0, 10)))));
                    insTask.ExecuteNonQuery();
                    r.Tasks++;
                    if (!done && status is "Planning" or "Gated") { r.WriteTasks.Add(kid); r.TaskGate[kid] = gid; }
                }
            }

            if (status is "Planning" or "Gated" or "Executing" && i % 4 == 0)
            {
                var b = 1 + i % 2;
                for (var k = 0; k < b; k++)
                {
                    Set(insBlock, ("$id", Id()), ("$t", tid), ("$p", products[k % products.Count]), ("$g", gateIds[k]), ("$title", $"Dependency not ready for {productNames[(i + k) % productNames.Length]}"),
                        ("$sev", k == 0 ? "High" : "Medium"), ("$own", U()), ("$raised", T(now.AddDays(-rnd.Next(1, 10)))), ("$res", null));
                    insBlock.ExecuteNonQuery(); r.Blockers++;
                }
                r.TrainsWithBlockers.Add(tid);
            }

            // ~30-step runbook on Executing trains (Live run) and every 5th Planning/Gated train (Rehearsal run history).
            var wantsRunbook = status == "Executing" || (status is "Planning" or "Gated" && i % 5 == 0) || status == "Complete" && i % 4 == 0;
            if (!wantsRunbook) continue;
            var readDrill = status == "Executing" && r.ReadRuns.Count < 4;        // the first four Executing trains: chained steps, partly done: read-only in the load
            var runId = Id();
            var runStarted = now.AddMinutes(-40);
            var mode = status == "Executing" ? "Live" : "Rehearsal";
            var ends = status == "Executing" ? null : T(now.AddDays(-5));
            Set(insRun, ("$id", runId), ("$t", tid), ("$m", mode), ("$s", T(runStarted)), ("$u", owner), ("$e", ends), ("$o", ends is null ? null : "Completed")); insRun.ExecuteNonQuery();
            var stepIds = new List<string>();
            for (var s = 0; s < 30; s++)
            {
                var sid = Id(); stepIds.Add(sid);
                var planned = readDrill || status != "Executing" ? runStarted.AddMinutes(s * 3) : now.AddMinutes(s);   // write-pool steps are never "late" (no escalation notifications)
                Set(insStep, ("$id", sid), ("$t", tid), ("$p", products[s % products.Count]), ("$code", $"R-{s + 1:000}"), ("$sec", s == 29 ? "Rollback" : sections[s % sections.Length]), ("$title", $"Step {s + 1}: {productNames[(i + s) % productNames.Length]}"),
                    ("$ins", "Run the promoted pipeline\nWait for health checks\nRecord the result"), ("$own", U()), ("$at", T(planned)), ("$dur", 3 + s % 7));
                insStep.ExecuteNonQuery();
                if (readDrill && s > 0) { Set(insDep, ("$s", sid), ("$d", stepIds[s - 1])); insDep.ExecuteNonQuery(); }
                r.Steps++;
                var st = mode == "Live" ? (readDrill ? (s < 12 ? "Done" : s == 12 ? "Running" : "Scheduled") : "Scheduled") : "Done";
                var a = st is "Done" or "Running" ? T(runStarted.AddMinutes(s * 3)) : null;
                var e = st == "Done" ? T(runStarted.AddMinutes(s * 3 + 2)) : null;
                Set(insExec, ("$id", Id()), ("$r", runId), ("$s", sid), ("$st", st), ("$a", a), ("$e", e), ("$u", st == "Scheduled" ? null : owner)); insExec.ExecuteNonQuery();
                if (mode == "Live" && !readDrill && st == "Scheduled") r.WriteSteps.Add((runId, sid));
            }
            if (mode == "Live" && readDrill) { r.ReadRuns.Add((tid, runId)); r.ReadRunSteps[runId] = stepIds; }
            if (status is "Executing" or "Complete") { Set(insBase, ("$id", Id()), ("$t", tid), ("$at", T(created.AddDays(12))), ("$d", D(target)), ("$u", owner)); insBase.ExecuteNonQuery(); }
        }

        // Hot tasks: five open tasks that every user tries to toggle (intentional contention, 409 expected).
        foreach (var t in r.WriteTasks.Take(5).ToList()) { r.HotTasks.Add(t); }
        r.WriteTasks.RemoveAll(r.HotTasks.Contains);

        // Notifications: ~40 per user (a third unread).
        using (var ins = Cmd(c, tx, "INSERT INTO Notifications(Id,UserId,Kind,EntityType,EntityId,EscalationLevel,Message,CreatedAt,ReadAt) VALUES($id,$u,$k,$et,$ei,$lvl,$m,$at,$read)"))
        {
            foreach (var u in userIds)
                for (var n = 0; n < 40; n++)
                {
                    var at = now.AddHours(-n * 5 - rnd.Next(0, 4));
                    Set(ins, ("$id", Id()), ("$u", u), ("$k", n % 3 == 0 ? "GateReminder" : n % 3 == 1 ? "StepLate" : "GateEntered"), ("$et", "StageGate"), ("$ei", r.Gates[rnd.Next(r.Gates.Count)]), ("$lvl", n % 7 == 0 ? 1 : 0),
                        ("$m", $"Gate {gateNames[n % 5].Name} needs attention"), ("$at", T(at)), ("$read", n % 3 == 0 ? null : T(at.AddMinutes(20)))); ins.ExecuteNonQuery(); r.Notifications++;
                }
        }
        using (var ins = Cmd(c, tx, "INSERT INTO FreezeWindows(Id,Name,Kind,StartsAt,EndsAt,ProductPattern,CreatedByUserId) VALUES($id,$n,$k,$s,$e,NULL,$u)"))
        {
            Set(ins, ("$id", Id()), ("$n", "Quarter-end freeze"), ("$k", "Freeze"), ("$s", T(now.AddDays(20))), ("$e", T(now.AddDays(27))), ("$u", userIds[0])); ins.ExecuteNonQuery();
            Set(ins, ("$id", Id()), ("$n", "Holiday chill"), ("$k", "Chill"), ("$s", T(now.AddDays(40))), ("$e", T(now.AddDays(47))), ("$u", userIds[0])); ins.ExecuteNonQuery();
        }

        // 100 000 audit rows, oldest first (Id rises with time, like the real table). Real entity ids for the entity types the viewer filters on.
        var trainIds = r.StreamTrains;
        var kinds = new (string Type, string[] Actions, int Weight)[]
        {
            ("ChecklistTask", ["Complete", "Reopen", "Create"], 34), ("StepExecution", ["Start", "Done", "Fail"], 16), ("StageGate", ["Start", "Certify", "Decertified", "Waive"], 10),
            ("ReleaseTrain", ["Advance", "Update", "ChangeTarget", "Create"], 8), ("Blocker", ["Raise", "Resolve"], 6), ("RunbookRun", ["Start", "End"], 3), ("Attachment", ["Upload", "Delete"], 4),
            ("Notification", ["Read", "Create"], 9), ("GoNoGo", ["Decide"], 2), ("Connector", ["SetCredentials", "Sync"], 2), ("FreezeWindow", ["Create", "Override"], 2), ("User", ["Update", "Create"], 4),
        };
        var totalW = kinds.Sum(k => k.Weight);
        using (var ins = Cmd(c, tx, "INSERT INTO AuditEvents(OccurredAt,ActorUserId,ReleaseTrainId,EntityType,EntityId,Action,BeforeJson,AfterJson) VALUES($at,$a,$t,$et,$ei,$ac,$b,$af)"))
        {
            var start = now.AddDays(-120); var span = (now - start).TotalSeconds;
            for (var n = 0; n < auditRows; n++)
            {
                var pick = rnd.Next(totalW); var k = kinds[0];
                foreach (var kk in kinds) { if (pick < kk.Weight) { k = kk; break; } pick -= kk.Weight; }
                var action = k.Actions[rnd.Next(k.Actions.Length)];
                var at = start.AddSeconds(span * n / auditRows + rnd.NextDouble() * 5);
                var entityId = k.Type switch
                {
                    "ChecklistTask" => r.WriteTasks.Count > 0 ? r.WriteTasks[rnd.Next(r.WriteTasks.Count)] : Id(),
                    "StageGate" => r.Gates[rnd.Next(r.Gates.Count)],
                    _ => Id(),
                };
                var train = rnd.Next(100) < 4 ? null : trainIds[rnd.Next(trainIds.Count)];
                var before = action is "Complete" or "Reopen" or "Start" or "Done" or "Certify" ? JsonSerializer.Serialize(new { status = "Open", version = rnd.Next(1, 8) }) : null;
                var after = JsonSerializer.Serialize(new { status = action, version = rnd.Next(2, 9), note = "load-test seed" });
                Set(ins, ("$at", T(at)), ("$a", rnd.Next(100) < 3 ? null : U()), ("$t", train), ("$et", k.Type), ("$ei", entityId), ("$ac", action), ("$b", before), ("$af", after));
                ins.ExecuteNonQuery();
            }
            r.AuditRows = auditRows;
        }
        r.EntityIds = [.. r.WriteTasks.Take(200)];
        tx.Commit();
        if (Environment.GetEnvironmentVariable("LOAD_ANALYZE") == "1") Exec(c, "ANALYZE;");   // experiment switch only
        Exec(c, "PRAGMA wal_checkpoint(TRUNCATE);");   // no ANALYZE on purpose: the application never runs it, so the planner has no statistics in production either
        return r;
    }

    private static SqliteCommand Cmd(SqliteConnection c, SqliteTransaction tx, string sql)
    {
        var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(sql, @"\$\w+")) if (!cmd.Parameters.Contains(m.Value)) cmd.Parameters.Add(new SqliteParameter(m.Value, null));
        return cmd;
    }

    private static void Set(SqliteCommand cmd, params (string Name, object? Value)[] values)
    {
        foreach (var (n, v) in values) cmd.Parameters[n].Value = v ?? DBNull.Value;
    }

    private static void Exec(SqliteConnection c, string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
}
