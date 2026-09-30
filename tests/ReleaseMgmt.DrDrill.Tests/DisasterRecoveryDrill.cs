using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Sync;
using ReleaseMgmt.Infrastructure.Backup;
using ReleaseMgmt.Infrastructure.Sync;
using ReleaseMgmt.LoadTests;

namespace ReleaseMgmt.DrDrill;

/// <summary>Size of the instance the drill builds. Env: DR_DRILL_SCALE=small|full, or DR_TRAINS, DR_AUDIT_ROWS, DR_ATTACH_COUNT, DR_ATTACH_KB.</summary>
public sealed record DrScale(string Name, int Trains, int AuditRows, int AttachmentCount, int AttachmentKb)
{
    public static DrScale FromEnv()
    {
        var name = Environment.GetEnvironmentVariable("DR_DRILL_SCALE") ?? "small";
        var baseScale = name == "full" ? new DrScale("full", 200, 100_000, 60, 1024) : new DrScale("small", 20, 5_000, 6, 64);
        int E(string k, int d) => int.TryParse(Environment.GetEnvironmentVariable(k), out var v) ? v : d;
        var s = baseScale with { Trains = E("DR_TRAINS", baseScale.Trains), AuditRows = E("DR_AUDIT_ROWS", baseScale.AuditRows), AttachmentCount = E("DR_ATTACH_COUNT", baseScale.AttachmentCount), AttachmentKb = E("DR_ATTACH_KB", baseScale.AttachmentKb) };
        return s == baseScale ? s : s with { Name = name + "+overrides" };
    }
}

public sealed record StepTiming(string Name, double Seconds, string Detail);
public sealed record Check(string Name, bool Passed, string Detail);

public sealed class DrReport
{
    public DrScale Scale { get; set; } = null!;
    public List<StepTiming> Backup { get; } = [];
    public List<StepTiming> Restore { get; } = [];
    public List<StepTiming> Verify { get; } = [];
    public List<Check> Checks { get; } = [];
    public double RestoreSeconds => Restore.Sum(s => s.Seconds);
    public long DbBytes, AttachmentBytes, AttachmentFiles, KeyRingFiles, SecretFiles, AuditRows, TotalRows;
    public int Tables, Triggers;
    public const double BudgetSeconds = 30 * 60;
    public bool Passed => Checks.All(c => c.Passed) && RestoreSeconds < BudgetSeconds;

    public string ToMarkdown()
    {
        static string T(double s) => s.ToString("0.00", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.AppendLine($"### DR drill, scale `{Scale.Name}` ({Scale.Trains} trains, {Scale.AuditRows} audit rows, {Scale.AttachmentCount} attachments of {Scale.AttachmentKb} KB)");
        sb.AppendLine();
        sb.AppendLine($"Database {DbBytes / 1048576.0:0.0} MB ({Tables} tables, {Triggers} triggers, {TotalRows} rows, {AuditRows} audit rows), attachments {AttachmentBytes / 1048576.0:0.0} MB in {AttachmentFiles} files, key ring {KeyRingFiles} file(s), credentials {SecretFiles} file(s).");
        sb.AppendLine();
        void Table(string title, List<StepTiming> steps, bool total)
        {
            sb.AppendLine($"**{title}**").AppendLine();
            sb.AppendLine("| Step | Seconds | Detail |").AppendLine("|---|---:|---|");
            foreach (var s in steps) sb.AppendLine($"| {s.Name} | {T(s.Seconds)} | {s.Detail} |");
            if (total) sb.AppendLine($"| **Total restore (loss to healthy)** | **{T(steps.Sum(x => x.Seconds))}** | budget {BudgetSeconds / 60:0} min ({BudgetSeconds:0} s): {(RestoreSeconds < BudgetSeconds ? "met" : "MISSED")} |");
            sb.AppendLine();
        }
        Table("Backup (before the loss; not part of the restore time)", Backup, false);
        Table("Restore (after total loss)", Restore, true);
        Table("Verification (after the restore; not part of the restore time)", Verify, false);
        sb.AppendLine("**Checks**").AppendLine();
        sb.AppendLine("| Check | Result | Detail |").AppendLine("|---|---|---|");
        foreach (var c in Checks) sb.AppendLine($"| {c.Name} | {(c.Passed ? "pass" : "**FAIL**")} | {c.Detail} |");
        return sb.ToString();
    }

    public string ToJson() => JsonSerializer.Serialize(new
    {
        scale = Scale, restoreSeconds = RestoreSeconds, budgetSeconds = BudgetSeconds, passed = Passed, dbBytes = DbBytes, attachmentBytes = AttachmentBytes, attachmentFiles = AttachmentFiles,
        auditRows = AuditRows, totalRows = TotalRows, tables = Tables, triggers = Triggers, backup = Backup, restore = Restore, verify = Verify, checks = Checks, markdown = ToMarkdown(),
    }, new JsonSerializerOptions { WriteIndented = true });
}

/// <summary>Everything read from a database file to prove a restore reproduced it: row counts per table, trigger names, the audit trail as one hash.</summary>
public sealed record DbSnapshot(SortedDictionary<string, long> Counts, SortedSet<string> Triggers, long AuditRows, long MaxAuditId, string AuditDigest)
{
    public static DbSnapshot Read(string dbFile)
    {
        using var c = new SqliteConnection($"Data Source={dbFile};Mode=ReadOnly;Pooling=False");
        c.Open();
        string Scalar(string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture)!; }
        var tables = new List<string>();
        using (var cmd = c.CreateCommand()) { cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name"; using var r = cmd.ExecuteReader(); while (r.Read()) tables.Add(r.GetString(0)); }
        var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var t in tables) counts[t] = long.Parse(Scalar($"SELECT COUNT(*) FROM \"{t}\""), CultureInfo.InvariantCulture);
        var triggers = new SortedSet<string>(StringComparer.Ordinal);
        using (var cmd = c.CreateCommand()) { cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='trigger'"; using var r = cmd.ExecuteReader(); while (r.Read()) triggers.Add(r.GetString(0)); }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long max = 0;
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, OccurredAt, ActorUserId, ReleaseTrainId, EntityType, EntityId, Action, BeforeJson, AfterJson FROM AuditEvents ORDER BY Id";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                max = r.GetInt64(0);
                var line = string.Join('\u001f', Enumerable.Range(0, 9).Select(i => r.IsDBNull(i) ? "\u0000" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture))) + "\n";
                hash.AppendData(Encoding.UTF8.GetBytes(line));
            }
        }
        return new DbSnapshot(counts, triggers, counts.GetValueOrDefault("AuditEvents"), max, Convert.ToHexString(hash.GetHashAndReset()));
    }
}

/// <summary>
/// The disaster-recovery drill (REOS-52), the runbook as code. Builds a populated instance (demo data, bulk history, evidence attachments, connector credentials
/// written through the real credential store and therefore encrypted with the Data Protection key ring, an ICS token), takes a backup with the application's own
/// BackupService and copies the key ring, credentials and attachments as the runbook says, destroys the instance completely, restores it with the API's own
/// <c>restore</c> entry point plus the copies, boots the API against it and verifies. Every restore step is timed with a monotonic clock.
/// </summary>
public static class DisasterRecoveryDrill
{
    public const string JiraSecret = "jira-api-token-DRILL-3f9a1c7e2b";
    public const string ServiceNowSecret = "sn-basic-secret-DRILL-77aa10ce";
    private static readonly Dictionary<string, string?> Quiet = new()
    {
        ["Notifications__ScanSeconds"] = "3600", ["Sync__Enabled"] = "false",   // nothing else writes to the database while the drill measures it
    };

    private sealed class RecordingAlerts : IAlertSink
    {
        public List<string> Raised { get; } = [];
        public Task RaiseAsync(string s, string k, string key, string m, CancellationToken ct = default) { Raised.Add($"{s}/{k}: {m}"); return Task.CompletedTask; }
    }

    private static async Task<T> Timed<T>(List<StepTiming> into, string name, Func<Task<(T Value, string Detail)>> body)
    {
        var t0 = Stopwatch.GetTimestamp();
        var (value, detail) = await body();
        into.Add(new StepTiming(name, Stopwatch.GetElapsedTime(t0).TotalSeconds, detail));
        return value;
    }

    public static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "ReleaseMgmt.sln"))) return d.FullName;
        throw new DirectoryNotFoundException("ReleaseMgmt.sln not found above " + AppContext.BaseDirectory);
    }

    public static (long Files, long Bytes) CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        long files = 0, bytes = 0;
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest, overwrite: true);
            files++; bytes += new FileInfo(f).Length;
        }
        return (files, bytes);
    }

    public static void DeleteWithRetry(string dir)
    {
        for (var i = 0; i < 50; i++)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); return; }
            catch (IOException) { Thread.Sleep(200); }   // Windows: the killed process may still hold a handle for a moment
            catch (UnauthorizedAccessException) { Thread.Sleep(200); }
        }
        if (Directory.Exists(dir)) throw new IOException($"Could not delete {dir}");
    }

    /// <summary>The store exactly as the API builds it: application name ReleaseMgmt, keys in a directory, one protected file per connector.</summary>
    public static DataProtectionCredentialStore OpenCredentialStore(string keysDir, string secretsDir)
    {
        var services = new ServiceCollection();
        services.AddDataProtection().SetApplicationName("ReleaseMgmt").PersistKeysToFileSystem(new DirectoryInfo(keysDir));
        var provider = services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();
        var options = SyncOptions.From(new ConfigurationBuilder().Build(), secretsDir);
        return new DataProtectionCredentialStore(provider, options);
    }

    public static async Task<DrReport> RunAsync(DrScale scale, string scratch)
    {
        var report = new DrReport { Scale = scale };
        var prod = Path.Combine(scratch, "prod");                 // the instance root: the API runs with this as its working directory, so every path is the shipped default
        var offsite = Path.Combine(scratch, "offsite");           // what an operator ships off the box: the backup file plus the three directories
        var data = Path.Combine(prod, "data");
        var db = Path.Combine(data, "releasemgmt.db");
        void Check(string name, bool ok, string detail) => report.Checks.Add(new Check(name, ok, detail));

        // ------------------------------------------------------------------------------------------------------------------------------ build the instance
        string backupFile; DbSnapshot truth;
        List<(string Id, string Path, string Sha, long Size)> attachments = [];
        string icsToken;
        long trainsInApi;
        await using (var source = await AppProcess.StartAsync(prod, new Dictionary<string, string?>(Quiet) { ["Seed__Demo"] = "true" }))
        {
            using var rte = source.NewClient();
            var userIds = new List<string>();
            foreach (var (email, role) in new[] { ("rte@drill.example", "RTE"), ("rm@drill.example", "ReleaseManager"), ("gov@drill.example", "GovernanceOfficer"), ("viewer@drill.example", "Viewer"), ("rte2@drill.example", "RTE") })
            {
                using var login = await (email == "rte@drill.example" ? rte : source.NewClient()).PostAsJsonAsync("/auth/dev-login", new { email, name = email.Split('@')[0], role });
                login.EnsureSuccessStatusCode();
            }
            using (var c = new SqliteConnection($"Data Source={db};Pooling=False")) { c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT Id FROM Users"; using var r = cmd.ExecuteReader(); while (r.Read()) userIds.Add(r.GetString(0)); }

            var seed = Seeder.Seed(db, userIds, scale.Trains, scale.AuditRows);   // bulk history through SQL, like the load test

            // Connector credentials through the real API: the real credential store encrypts them with the Data Protection key ring.
            foreach (var (src, url, kind, user, secret) in new[] { ("Jira", "https://drill.atlassian.net", "ApiToken", "drill@example.com", JiraSecret), ("ServiceNow", "https://drill.service-now.com", "Basic", "svc-release", ServiceNowSecret) })
            {
                (await rte.PutAsJsonAsync($"/api/v1/connectors/{src}", new { baseUrl = url })).EnsureSuccessStatusCode();
                (await rte.PutAsJsonAsync($"/api/v1/connectors/{src}/credentials", new { kind, username = user, secret })).EnsureSuccessStatusCode();
            }
            using (var ics = await rte.PostAsJsonAsync("/api/v1/me/ics-tokens", new { scope = "all" }))
            {
                ics.EnsureSuccessStatusCode();
                icsToken = JsonDocument.Parse(await ics.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
            }
            var rng = new Random(52);
            var train = seed.StreamTrains[0];
            for (var i = 0; i < scale.AttachmentCount; i++)
            {
                var bytes = new byte[scale.AttachmentKb * 1024]; rng.NextBytes(bytes);
                using var form = new MultipartFormDataContent { { new StringContent("Train"), "entityType" }, { new StringContent(train), "entityId" } };
                var file = new ByteArrayContent(bytes); file.Headers.ContentType = new("application/octet-stream");
                form.Add(file, "file", $"evidence-{i:000}.bin");
                using var up = await rte.PostAsync("/api/v1/attachments", form);
                if (!up.IsSuccessStatusCode) throw new InvalidOperationException($"Attachment upload {i} failed: {(int)up.StatusCode} {await up.Content.ReadAsStringAsync()}");
            }
            using (var t = await rte.GetAsync("/api/v1/trains")) trainsInApi = JsonDocument.Parse(await t.Content.ReadAsStringAsync()).RootElement.GetArrayLength();

            // -------------------------------------------------------------------------------------------------------------------------- back up (the runbook)
            var alerts = new RecordingAlerts();
            var backupDir = Path.Combine(offsite, "backups");
            var svc = new BackupService(new BackupOptions($"Data Source={db};Pooling=False", backupDir, BackupOptions.DefaultInterval), TimeProvider.System, alerts, NullLogger<BackupService>.Instance);
            await Timed(report.Backup, "Online backup of the live database (BackupService: SQLite online backup API + integrity_check)", async () =>
            {
                await svc.RunOnceAsync();
                if (alerts.Raised.Count > 0) throw new InvalidOperationException("The backup raised an alert: " + string.Join("; ", alerts.Raised));
                var f = Directory.GetFiles(backupDir, "releasemgmt-*.db").Where(x => !Path.GetFileName(x).Contains("nightly")).OrderBy(x => x).Last();
                return (f, $"{new FileInfo(f).Length / 1048576.0:0.0} MB, journal_mode=DELETE, single file");
            });
            backupFile = Directory.GetFiles(backupDir, "releasemgmt-*.db").Where(x => !Path.GetFileName(x).Contains("nightly")).OrderBy(x => x).Last();
            foreach (var dir in new[] { "keys", "secrets", "attachments" })
            {
                var (files, bytes) = await Timed(report.Backup, $"Copy data/{dir} off the box", () => Task.FromResult((CopyDirectory(Path.Combine(data, dir), Path.Combine(offsite, dir)), "")));
                report.Backup[^1] = report.Backup[^1] with { Detail = $"{files} file(s), {bytes / 1048576.0:0.00} MB" };
                if (dir == "keys") report.KeyRingFiles = files; else if (dir == "secrets") report.SecretFiles = files; else { report.AttachmentFiles = files; report.AttachmentBytes = bytes; }
            }

            // The truth to compare with: the live database at this quiet moment, which must equal the backup taken from it.
            truth = DbSnapshot.Read(db);
            var backupSnap = DbSnapshot.Read(backupFile);
            Check("Backup equals the live database it was taken from", truth.Counts.SequenceEqual(backupSnap.Counts) && truth.AuditDigest == backupSnap.AuditDigest && truth.Triggers.SetEquals(backupSnap.Triggers),
                $"{truth.Counts.Values.Sum()} rows, audit digest {truth.AuditDigest[..12]}");
            using (var c = new SqliteConnection($"Data Source={db};Mode=ReadOnly;Pooling=False"))
            {
                c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT Id, StoragePath, Sha256, SizeBytes FROM Attachments"; using var r = cmd.ExecuteReader();
                while (r.Read()) attachments.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3)));
            }
            report.DbBytes = new FileInfo(backupFile).Length; report.AuditRows = truth.AuditRows; report.TotalRows = truth.Counts.Values.Sum(); report.Tables = truth.Counts.Count; report.Triggers = truth.Triggers.Count;
        }

        // ------------------------------------------------------------------------------------------------------------------------------ total loss
        DeleteWithRetry(prod);
        Check("Total loss simulated", !Directory.Exists(prod) && Directory.Exists(offsite), "database, WAL, key ring, credentials, attachments, logs and local backups deleted; only the off-box copy remains");

        // ------------------------------------------------------------------------------------------------------------------------------ restore (timed)
        Directory.CreateDirectory(prod);   // a fresh host: an empty working directory
        var restoreOutput = await Timed(report.Restore, "1. Restore the database: ReleaseMgmt.Api restore <backup> data/releasemgmt.db", async () =>
        {
            var (code, output) = await AppProcess.RunCliAsync(prod, null, "restore", backupFile, Path.Combine("data", "releasemgmt.db"));
            if (code != 0) throw new InvalidOperationException($"restore exited {code}: {output}");
            return (output, $"{new FileInfo(db).Length / 1048576.0:0.0} MB copied, integrity_check run by the CLI");
        });
        foreach (var dir in new[] { "keys", "secrets", "attachments" })
        {
            var t0 = Stopwatch.GetTimestamp();
            var (files, bytes) = CopyDirectory(Path.Combine(offsite, dir), Path.Combine(data, dir));
            report.Restore.Add(new StepTiming($"{report.Restore.Count + 1}. Copy {dir} back to data/{dir}", Stopwatch.GetElapsedTime(t0).TotalSeconds, $"{files} file(s), {bytes / 1048576.0:0.00} MB"));
        }
        Check("The restore CLI reports a healthy database", restoreOutput.Contains("integrity_check: ok"), restoreOutput.Trim());

        // Database-level verification before the application starts (it writes on start: reference-data check, its own first backup).
        var t1 = Stopwatch.GetTimestamp();
        var integrity = BackupRunner.IntegrityCheck(db);
        Check("SQLite integrity_check", integrity == "ok", integrity);
        var restored = DbSnapshot.Read(db);
        var schemaTriggers = Regex.Matches(File.ReadAllText(Path.Combine(RepoRoot(), "db", "schema.sql")), @"^CREATE TRIGGER (\w+)", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToHashSet();
        Check("Trigger set equals db/schema.sql", restored.Triggers.SetEquals(schemaTriggers) && schemaTriggers.Count == 42,
            $"{restored.Triggers.Count} triggers in the database, {schemaTriggers.Count} in schema.sql; missing [{string.Join(",", schemaTriggers.Except(restored.Triggers))}], extra [{string.Join(",", restored.Triggers.Except(schemaTriggers))}]");
        var diffs = truth.Counts.Where(kv => restored.Counts.GetValueOrDefault(kv.Key, -1) != kv.Value).Select(kv => $"{kv.Key}: {kv.Value} -> {restored.Counts.GetValueOrDefault(kv.Key, -1)}").ToList();
        Check("Row counts match in every table", diffs.Count == 0 && restored.Counts.Count == truth.Counts.Count, diffs.Count == 0 ? $"{restored.Counts.Count} tables, {restored.Counts.Values.Sum()} rows" : string.Join("; ", diffs));
        Check("Audit trail intact", restored.AuditDigest == truth.AuditDigest && restored.MaxAuditId == truth.MaxAuditId && restored.AuditRows == truth.AuditRows,
            $"{restored.AuditRows} rows, highest Id {restored.MaxAuditId}, SHA-256 over every audit row {restored.AuditDigest[..16]}... identical");
        using (var c = new SqliteConnection($"Data Source={db};Pooling=False"))
        {
            c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "UPDATE AuditEvents SET Action = 'tampered' WHERE Id = (SELECT MIN(Id) FROM AuditEvents)";
            string outcome; try { cmd.ExecuteNonQuery(); outcome = "the UPDATE succeeded"; } catch (SqliteException ex) { outcome = ex.Message; }
            Check("Append-only audit trigger still fires after the restore", outcome.Contains("append-only"), outcome);
        }
        report.Verify.Add(new StepTiming("Database checks (integrity, triggers, counts, audit digest)", Stopwatch.GetElapsedTime(t1).TotalSeconds, ""));

        // ------------------------------------------------------------------------------------------------------------------------------ boot against the restored data
        AppProcess app = await Timed(report.Restore, $"{report.Restore.Count + 1}. Start the API and wait for /healthz", async () =>
        {
            var a = await AppProcess.StartAsync(prod, Quiet);   // Seed__Demo=false: nothing is seeded over the restored data
            return (a, "process start, migrations check, hosted services, first healthy /healthz");
        });
        await using (app)
        {
            using var http = app.NewClient(cookies: false);
            using var health = await http.GetAsync("/healthz");
            var healthBody = await health.Content.ReadAsStringAsync();
            Check("/healthz is healthy on the restored instance", health.StatusCode == HttpStatusCode.OK && healthBody.Contains("Healthy"), healthBody);

            var t2 = Stopwatch.GetTimestamp();
            using var rte = app.NewClient();
            (await rte.PostAsJsonAsync("/auth/dev-login", new { email = "rte@drill.example", name = "rte", role = "RTE" })).EnsureSuccessStatusCode();
            using var trains = await rte.GetAsync("/api/v1/trains");
            var n = JsonDocument.Parse(await trains.Content.ReadAsStringAsync()).RootElement.GetArrayLength();
            Check("The API serves the restored data", trains.IsSuccessStatusCode && n == trainsInApi && n > 0, $"GET /trains returns {n} trains (before the loss: {trainsInApi})");
            using var conns = await rte.GetAsync("/api/v1/connectors");
            var connectors = JsonDocument.Parse(await conns.Content.ReadAsStringAsync()).RootElement.EnumerateArray().ToDictionary(e => e.GetProperty("source").GetString()!, e => e.TryGetProperty("credentialKind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null);
            Check("The running restored API can read the connector credentials (key ring restored)", connectors.GetValueOrDefault("Jira") == "ApiToken" && connectors.GetValueOrDefault("ServiceNow") == "Basic",
                $"GET /connectors: Jira kind={connectors.GetValueOrDefault("Jira")}, ServiceNow kind={connectors.GetValueOrDefault("ServiceNow")} (a kind is only listed when the stored file decrypts)");
            using var anon = app.NewClient(cookies: false);
            using var ics = await anon.GetAsync($"/api/v1/ics/{icsToken}.ics");
            Check("The ICS feed token issued before the loss still works", ics.StatusCode == HttpStatusCode.OK, $"GET /ics/<token>.ics -> {(int)ics.StatusCode}");
            report.Verify.Add(new StepTiming("API checks (login, trains, connectors, ICS)", Stopwatch.GetElapsedTime(t2).TotalSeconds, ""));
        }
        SqliteConnection.ClearAllPools();

        // ------------------------------------------------------------------------------------------------------------------------------ credentials and the key ring
        var t3 = Stopwatch.GetTimestamp();
        var keys = Path.Combine(data, "keys"); var secrets = Path.Combine(data, "secrets");
        var withRing = OpenCredentialStore(keys, secrets);
        var jira = await withRing.GetAsync("Jira"); var sn = await withRing.GetAsync("ServiceNow");
        Check("Stored connector credentials decrypt with the restored key ring", jira is { Secret: JiraSecret, Username: "drill@example.com" } && sn is { Secret: ServiceNowSecret, Username: "svc-release" },
            "Jira API token and ServiceNow basic secret read back exactly (through DataProtectionCredentialStore, the API's own class)");

        var noRing = Path.Combine(scratch, "restore-without-key-ring"); var noRingSecrets = Path.Combine(noRing, "secrets");
        CopyDirectory(Path.Combine(offsite, "secrets"), noRingSecrets);   // the restore that forgot the key ring: same secrets directory, a brand-new empty key ring
        var withoutRing = OpenCredentialStore(Path.Combine(noRing, "keys"), noRingSecrets);
        string failure;
        try { await withoutRing.GetAsync("Jira"); failure = "NO ERROR: the credentials were readable without the original key ring"; }
        catch (ConnectorException ex) { failure = ex.Message; }
        Check("Without the key ring the credentials cannot be read, with a clear error", failure.Contains("key ring changed") && failure.Contains("enter them again"), failure);
        Check("A restore without the key ring lists the connector as having no credentials (tolerant listing)", await withoutRing.KindAsync("Jira") is null, "KindAsync returns null, so the admin screen shows NoCredentials and the poller reports AuthFailed until the token is re-entered");

        var blob = File.ReadAllText(Path.Combine(secrets, "Jira.cred"));
        var dbBytes = File.ReadAllBytes(db);
        Check("The secrets are encrypted at rest and never in SQLite", !blob.Contains(JiraSecret) && !blob.Contains("drill@example.com") && Encoding.UTF8.GetString(dbBytes).IndexOf(JiraSecret, StringComparison.Ordinal) < 0 && Encoding.UTF8.GetString(dbBytes).IndexOf(ServiceNowSecret, StringComparison.Ordinal) < 0,
            $"Jira.cred is {blob.Length} characters of protected payload; neither secret appears in it or anywhere in the {dbBytes.Length / 1048576.0:0.0} MB database file");
        report.Verify.Add(new StepTiming("Credential and key ring checks", Stopwatch.GetElapsedTime(t3).TotalSeconds, ""));

        // ------------------------------------------------------------------------------------------------------------------------------ attachments
        var t4 = Stopwatch.GetTimestamp();
        var bad = new List<string>();
        foreach (var a in attachments)
        {
            var path = Path.Combine(data, "attachments", a.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) { bad.Add($"{a.Id}: file missing"); continue; }
            using var fs = File.OpenRead(path);
            var sha = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
            if (!string.Equals(sha, a.Sha, StringComparison.OrdinalIgnoreCase) || new FileInfo(path).Length != a.Size) bad.Add($"{a.Id}: hash or size differs");
        }
        Check("Every attachment's SHA-256 and size match the stored values", bad.Count == 0 && attachments.Count == scale.AttachmentCount, bad.Count == 0 ? $"{attachments.Count} files verified" : string.Join("; ", bad));
        report.Verify.Add(new StepTiming("Attachment hashes", Stopwatch.GetElapsedTime(t4).TotalSeconds, $"{attachments.Count} files"));

        Check("Restore time is within the 30 minute budget", report.RestoreSeconds < DrReport.BudgetSeconds, $"{report.RestoreSeconds:0.00} s measured (budget {DrReport.BudgetSeconds:0} s)");
        return report;
    }
}
