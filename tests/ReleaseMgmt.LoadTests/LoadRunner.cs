using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using NBomber.CSharp;
using NBomber.Contracts;
using NBomber.Contracts.Stats;
using ReleaseMgmt.DrDrill;
using ReleaseMgmt.Infrastructure.Backup;

namespace ReleaseMgmt.LoadTests;

/// <summary>
/// REOS-52 load test. Starts the real API (the built ReleaseMgmt.Api.dll, Development, over real loopback Kestrel, in its own process), seeds 200 trains
/// and 100 000 audit rows through SQL, then drives 50 signed-in virtual users through a weighted mix of screens with NBomber, and asserts p95 &lt; 300 ms.
/// Configuration (environment): LOAD_USERS (50), LOAD_DURATION_S (60), LOAD_WARMUP_S (10), LOAD_TRAINS (200), LOAD_AUDIT_ROWS (100000),
/// LOAD_THINK_MIN_MS / LOAD_THINK_MAX_MS (0 = no think time: every user fires the next action as soon as the last one returns), LOAD_PROFILE (label),
/// LOAD_BUDGET_MS (300), LOAD_REPORT (markdown path), LOAD_BACKUP_EVERY_S (20; 0 = no online backups during the run).
/// Exit code 0 = every read p95 and the overall p95 under budget, zero unexpected errors, zero SQLite busy/locked. Not run by dotnet test.
/// </summary>
public static class LoadRunner
{
    private static int Env(string k, int d) => int.TryParse(Environment.GetEnvironmentVariable(k), out var v) ? v : d;

    public static async Task<int> Main()
    {
        var users = Env("LOAD_USERS", 50); var duration = Env("LOAD_DURATION_S", 60); var warm = Env("LOAD_WARMUP_S", 10);
        var trains = Env("LOAD_TRAINS", 200); var auditRows = Env("LOAD_AUDIT_ROWS", 100_000); var budget = Env("LOAD_BUDGET_MS", 300);
        var thinkMin = Env("LOAD_THINK_MIN_MS", 0); var thinkMax = Math.Max(thinkMin, Env("LOAD_THINK_MAX_MS", thinkMin));
        var backupEvery = Env("LOAD_BACKUP_EVERY_S", 20);
        var profile = Environment.GetEnvironmentVariable("LOAD_PROFILE") ?? (thinkMax == 0 ? "stress" : "typical");
        var root = Path.Combine(Path.GetTempPath(), "reos-load-" + Guid.NewGuid().ToString("N")[..8]);
        var reportPath = Environment.GetEnvironmentVariable("LOAD_REPORT") ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "test-results", $"load-{profile}.md");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        Console.WriteLine($"[load] profile={profile} users={users} duration={duration}s warmup={warm}s trains={trains} auditRows={auditRows} think={thinkMin}-{thinkMax}ms root={root}");

        // LOAD_SERVER_ENV="Key=Value;Key2=Value2": extra environment for the API process (for example an EF command log level while profiling).
        var serverEnv = (Environment.GetEnvironmentVariable("LOAD_SERVER_ENV") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(kv => kv.Split('=', 2)).ToDictionary(kv => kv[0], kv => (string?)kv[1]);
        await using var app = await AppProcess.StartAsync(root, serverEnv);
        var dbPath = Path.Combine(root, "data", "releasemgmt.db");
        Console.WriteLine($"[load] API pid {app.Pid} listening on {app.BaseUri}");

        // Sign in 50 users through the Development dev-login endpoint: 10 RTE, 5 Release Manager, 5 Governance Officer, 30 Viewer.
        var vusers = new List<VUser>();
        var roles = new (string Role, int Count)[] { ("RTE", 10), ("ReleaseManager", 5), ("GovernanceOfficer", 5), ("Viewer", Math.Max(0, users - 20)) };
        var logins = new List<(int I, string Email, string Role, HttpClient Http)>();
        var idx = 0;
        foreach (var (role, count) in roles)
            for (var n = 0; n < count && idx < users; n++, idx++)
            {
                var http = app.NewClient(); var email = $"load{idx:00}@example.com";
                (await http.PostAsJsonAsync("/auth/dev-login", new { email, name = $"Load User {idx:00}", role })).EnsureSuccessStatusCode();
                logins.Add((idx, email, role, http));
            }
        var ids = new Dictionary<string, string>();
        using (var c = new SqliteConnection($"Data Source={dbPath};Pooling=False")) { c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT Email, Id FROM Users"; using var rd = cmd.ExecuteReader(); while (rd.Read()) ids[rd.GetString(0)] = rd.GetString(1); }
        foreach (var l in logins) vusers.Add(new VUser(l.I, l.Email, l.Role, ids[l.Email], l.Http, 5200 + l.I));

        var seedWatch = Stopwatch.StartNew();
        var seed = Seeder.Seed(dbPath, vusers.Select(v => v.UserId).ToList(), trains, auditRows);
        Console.WriteLine($"[load] seeded in {seedWatch.Elapsed.TotalSeconds:0.0}s: {seed.Trains} trains, {seed.GateRows} gates, {seed.Tasks} tasks, {seed.Products} products, {seed.Blockers} blockers, {seed.Steps} steps, {seed.AuditRows} audit rows; DB {new FileInfo(dbPath).Length / 1048576.0:0.0} MB");

        var rec = new Recorder();
        var unloaded = await Preflight(app, vusers, seed);
        if (Environment.GetEnvironmentVariable("LOAD_PROBE_ONLY") == "1") return 0;

        var workload = new Workload(seed, rec, vusers);
        var cpu = new CpuSampler(app);
        var backups = new List<(double Seconds, long Bytes)>();
        using var stop = new CancellationTokenSource();
        var backupTask = backupEvery > 0 ? Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(warm + 5), stop.Token);   // first online backup lands 5 s into the measured window
                while (!stop.IsCancellationRequested)
                {
                    var sw = Stopwatch.StartNew();
                    var file = BackupRunner.Backup($"Data Source={dbPath};Pooling=False", Path.Combine(root, "data", "backups"), DateTime.UtcNow);   // online backup API + integrity_check on the copy (throws if not "ok")
                    backups.Add((sw.Elapsed.TotalSeconds, new FileInfo(file).Length));
                    File.Delete(file);
                    await Task.Delay(TimeSpan.FromSeconds(backupEvery), stop.Token);
                }
            }
            catch (OperationCanceledException) { }
        }) : Task.CompletedTask;

        var scenario = Scenario.Create("release_mgmt_mix", async ctx =>
        {
            var u = vusers[ctx.ScenarioInfo.InstanceNumber % vusers.Count];
            try { await workload.IterationAsync(ctx, u); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (ctx.ScenarioInfo.ScenarioOperation == ScenarioOperation.Bombing) rec.Add(new Recorder.Sample("HARNESS EXCEPTION", false, 0, 0, Outcome.Error, ex.GetType().Name + ": " + ex.Message));
                return Response.Fail<object>(message: ex.Message);
            }
            if (thinkMax > 0) await Task.Delay(u.Rnd.Next(thinkMin, thinkMax + 1));
            return Response.Ok();
        })
        .WithWarmUpDuration(TimeSpan.FromSeconds(warm))
        .WithLoadSimulations(Simulation.KeepConstant(vusers.Count, TimeSpan.FromSeconds(duration)));

        cpu.Start();
        var wall = Stopwatch.StartNew();
        NBomberRunner.RegisterScenarios(scenario)
            .WithReportFolder(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!, "nbomber-" + profile))
            .WithReportFormats(ReportFormat.Md, ReportFormat.Csv)
            .Run();
        wall.Stop();
        stop.Cancel(); await backupTask;
        var cpuResult = cpu.Stop();

        // ------------------------------------------------------------------------------------------------------------------------------- verdict
        var all = rec.All;
        var (rows, overall, reads, writes) = Stats.Compute(all);
        var logText = ReadServerLogs(root) + "\n" + app.Output;
        var lockHits = Regex.Matches(logText, @"database is locked|SQLITE_BUSY|SQLite Error 5\b|SQLite Error 6\b", RegexOptions.IgnoreCase).Count + rec.BusyHits;
        var serverErrors = Regex.Matches(logText, @"\[ERR\]|\bfail: ").Count;
        var errors = all.Where(s => s.Outcome == Outcome.Error).ToList();
        var readBreaches = rows.Where(r => !r.Write && r.P95 >= budget).ToList();
        var writeBreaches = rows.Where(r => r.Write && r.P95 >= budget).ToList();
        var failed = overall.P95 >= budget || readBreaches.Count > 0 || errors.Count > 0 || lockHits > 0 || all.Count == 0;
        var measuredSeconds = duration;

        var md = new StringBuilder();
        md.AppendLine($"# Load test report: profile `{profile}`");
        md.AppendLine();
        md.AppendLine($"Run at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z. Result: **{(failed ? "FAIL" : "PASS")}** (budget: p95 < {budget} ms overall and for every read request; zero unexpected errors; zero SQLite busy/locked).");
        md.AppendLine();
        md.AppendLine("## Machine and configuration");
        md.AppendLine($"- Host: {Machine()}");
        md.AppendLine($"- Server: the built `ReleaseMgmt.Api.dll` in its own process (Development, real loopback Kestrel), SQLite WAL on local disk; load generator (NBomber {typeof(NBomberRunner).Assembly.GetName().Version}) in a second process on the same machine, so the two compete for the same cores.");
        md.AppendLine($"- Virtual users: {vusers.Count} signed in through dev-login ({string.Join(", ", roles.Select(r => $"{r.Count} {r.Role}"))}), each with its own HTTP client and cookie; think time between actions {(thinkMax == 0 ? "none (closed loop: the next action starts the moment the last returned)" : $"{thinkMin}-{thinkMax} ms uniform")}.");
        md.AppendLine($"- Duration: {warm} s warm-up (not measured) + {duration} s measured; {all.Count} requests measured, {all.Count / (double)measuredSeconds:0.0} requests/s.");
        md.AppendLine($"- Data: {seed.Trains} trains, {seed.GateRows} gates, {seed.Tasks} checklist tasks, {seed.Products} products, {seed.Blockers} open blockers, {seed.Steps} runbook steps, {seed.Notifications} notifications, {seed.AuditRows} audit rows; database {new FileInfo(dbPath).Length / 1048576.0:0.0} MB.");
        md.AppendLine($"- Server CPU during the measured window: {cpuResult.ServerPct:0}% of one core ({cpuResult.ServerPct / Environment.ProcessorCount:0}% of the machine); load generator {cpuResult.ClientPct:0}% of one core; server working set {cpuResult.ServerWorkingSetMb:0} MB.");
        md.AppendLine($"- Step writes: {workload.StepPairsDone} start+done pairs completed; the one-shot step pool ({seed.WriteSteps.Count} steps) was found empty {workload.PoolExhausted} times (those iterations read the live run instead).");
        md.AppendLine($"- Online backups taken during the run (BackupRunner.Backup on the live database, integrity_check on each copy): {backups.Count}{(backups.Count > 0 ? $", each {string.Join(" / ", backups.Select(b => b.Seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s"))} for {backups[0].Bytes / 1048576.0:0.0} MB" : "")}.");
        md.AppendLine();
        md.AppendLine("## Summary");
        md.AppendLine(Stats.Table([overall, reads, writes], budget));
        md.AppendLine("## Per request");
        md.AppendLine(Stats.Table(rows.Where(r => !r.Write), budget));
        md.AppendLine(Stats.Table(rows.Where(r => r.Write), budget));
        md.AppendLine("## Unloaded latency (one user, sequential, before the load starts)");
        md.AppendLine(unloaded);
        md.AppendLine("## Integrity");
        md.AppendLine($"- Unexpected errors: **{errors.Count}** (409 on the deliberately contended tasks is counted separately as expected: {all.Count(s => s.Outcome == Outcome.ExpectedConflict)}).");
        md.AppendLine($"- SQLite busy/locked occurrences (response bodies, server log, server output): **{lockHits}** with busy_timeout=5000.");
        md.AppendLine($"- Server log lines at error level: {serverErrors}.");
        if (readBreaches.Count > 0) md.AppendLine($"- READ p95 over budget: {string.Join("; ", readBreaches.Select(r => $"{r.Name} {r.P95:0.0} ms"))}");
        if (writeBreaches.Count > 0) md.AppendLine($"- WRITE p95 over budget (reported, not part of the read gate): {string.Join("; ", writeBreaches.Select(r => $"{r.Name} {r.P95:0.0} ms"))}");
        foreach (var g in errors.GroupBy(e => e.Name + " -> " + (e.Detail ?? "")[..Math.Min(160, (e.Detail ?? "").Length)]).Take(15)) md.AppendLine($"- error x{g.Count()}: {g.Key}");
        File.WriteAllText(reportPath, md.ToString());
        Console.WriteLine(md.ToString());
        Console.WriteLine($"[load] report: {Path.GetFullPath(reportPath)}");
        Console.WriteLine($"[load] RESULT {(failed ? "FAIL" : "PASS")}: overall p95 {overall.P95:0.0} ms, reads p95 {reads.P95:0.0} ms, writes p95 {writes.P95:0.0} ms, errors {errors.Count}, locked {lockHits}");

        try { Directory.Delete(root, true); } catch (IOException) { /* the report is what matters */ }
        return failed ? 1 : 0;
    }

    /// <summary>One deterministic pass over every read endpoint the mix uses: a setup mistake must stop the run, not hide as a zero-latency request.</summary>
    private static async Task<string> Preflight(AppProcess app, List<VUser> users, SeedResult seed)
    {
        var rte = users.First(u => u.Role == "RTE");
        var t = seed.StreamTrains[0]; var g = seed.Gates[0]; var (train, run) = seed.ReadRuns[0];
        var urls = new List<string> { "/api/v1/trains", "/api/v1/freeze-windows/ahead", $"/api/v1/trains/{t}", $"/api/v1/trains/{t}/readiness", $"/api/v1/trains/{t}/products", $"/api/v1/trains/{t}/window", $"/api/v1/gates/{g}",
            $"/api/v1/runs/{run}", $"/api/v1/runs/{run}/forecast", $"/api/v1/trains/{train}/steps", "/api/v1/audit?limit=100", "/api/v1/audit?entity=ChecklistTask&limit=100", "/api/v1/audit?action=Certif&limit=100", $"/api/v1/audit?train={t}&limit=100", $"/api/v1/audit?actor={rte.UserId}&limit=100", "/api/v1/audit?from=2026-08-01&to=2026-08-08&limit=100", "/api/v1/audit/entity-types", "/api/v1/me/work", "/api/v1/me/notifications/count", "/api/v1/me/notifications?limit=50",
            $"/api/v1/calendar?from={DateTime.UtcNow:yyyy-MM}-01&to={DateTime.UtcNow:yyyy-MM}-28" };
        for (var m = 1; m <= 15; m++) urls.Add($"/api/v1/analytics/M{m}");
        var sb = new StringBuilder("| Request (single user, nothing else running) | p50 ms | max ms |\n|---|---:|---:|\n");
        foreach (var url in urls)
        {
            var lat = new List<double>();
            for (var n = 0; n < 11; n++)   // the first call is the JIT/cache warm-up and is dropped
            {
                var t0 = Stopwatch.GetTimestamp();
                using var r = await rte.Http.GetAsync(url);
                var body = await r.Content.ReadAsStringAsync();
                if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"Preflight: GET {url} returned {(int)r.StatusCode}: {body}");
                if (n > 0) lat.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            }
            lat.Sort();
            sb.AppendLine($"| GET {url.Replace(rte.UserId, "{user}").Replace(t, "{train}").Replace(g, "{gate}").Replace(run, "{run}").Replace(train, "{train}")} | {Stats.Percentile(lat, 50):0.0} | {lat[^1]:0.0} |");
        }
        Console.WriteLine($"[load] preflight ok ({urls.Count} endpoints)\n{sb}");
        return sb.ToString();
    }

    private static string ReadServerLogs(string root)
    {
        var dir = Path.Combine(root, "logs");
        return Directory.Exists(dir) ? string.Join("\n", Directory.GetFiles(dir, "*.log").Select(f => { using var s = new StreamReader(new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)); return s.ReadToEnd(); })) : "";
    }

    private static string Machine()
    {
        var model = "unknown CPU";
        try { if (File.Exists("/proc/cpuinfo")) model = File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal))?.Split(':', 2)[1].Trim() ?? model; } catch (IOException) { }
        var ram = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0;
        return $"{Environment.ProcessorCount} logical cores ({model}), {ram:0.0} GB RAM available to the process, {RuntimeInformation.OSDescription}, {RuntimeInformation.FrameworkDescription}. This is a shared development container, not the production host.";
    }

    private sealed class CpuSampler(AppProcess app)
    {
        private TimeSpan _s0, _c0; private DateTime _t0;
        public void Start() { _s0 = app.CpuTime; _c0 = Process.GetCurrentProcess().TotalProcessorTime; _t0 = DateTime.UtcNow; }
        public (double ServerPct, double ClientPct, double ServerWorkingSetMb) Stop()
        {
            var wall = (DateTime.UtcNow - _t0).TotalSeconds;
            return ((app.CpuTime - _s0).TotalSeconds / wall * 100, (Process.GetCurrentProcess().TotalProcessorTime - _c0).TotalSeconds / wall * 100, app.WorkingSetBytes / 1048576.0);
        }
    }
}
