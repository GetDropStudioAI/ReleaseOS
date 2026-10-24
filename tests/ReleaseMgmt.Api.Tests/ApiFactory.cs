using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReleaseMgmt.Infrastructure.Comms;

namespace ReleaseMgmt.Api.Tests;

/// <summary>Stands in for the control webhook sender in every test host (REOS-112): records each signed call and answers as told. The real sender has its own tests.</summary>
public sealed class RecordingControlSender : IControlWebhookSender
{
    public ConcurrentQueue<(CommWebhookTarget Target, ControlDelivery Delivery)> Sent { get; } = new();
    public Func<ControlDelivery, ControlSendResult> Answer { get; set; } = _ => new(true, 200, "deploy.example.com", null);
    public Task<ControlSendResult> SendAsync(CommWebhookTarget target, ControlDelivery delivery, CancellationToken ct) { Sent.Enqueue((target, delivery)); return Task.FromResult(Answer(delivery)); }
}

public sealed class ApiFactory(string environment = "Development", bool demoData = false, string? passwordResetUrl = null, bool requireIfMatch = false, long? attachmentMaxBytes = null, IReadOnlyDictionary<string, string>? settings = null) : WebApplicationFactory<Program>
{
    private readonly string _dir = Directory.CreateTempSubdirectory("reos-api-").FullName;
    public string DbPath => Path.Combine(_dir, "app.db");
    public string AttachmentsDir => Path.Combine(_dir, "attachments");
    public const string TestHosts = "localhost;releases.example.com";
    public RecordingControlSender ControlSender { get; } = new();
    /// <summary>A fixed clock for tests whose data carries absolute dates (otherwise the real date turns "not yet due" rows overdue).</summary>
    public TimeProvider? Clock { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        var web = Path.Combine(_dir, "wwwroot");
        Directory.CreateDirectory(web);
        File.WriteAllText(Path.Combine(web, "index.html"), "<html>spa</html>");
        File.WriteAllText(Path.Combine(web, "probe.js"), "// asset");
        builder.UseWebRoot(web);
        // UseSetting, not ConfigureAppConfiguration: Program reads these while building, before late config sources apply.
        builder.UseSetting("Db:Path", DbPath);
        builder.UseSetting("Seed:Demo", demoData ? "true" : "false");
        if (passwordResetUrl is not null) builder.UseSetting("Auth:PasswordResetUrl", passwordResetUrl);
        builder.UseSetting("Api:RequireIfMatch", requireIfMatch ? "true" : "false");   // existing contract tests predate Q-004; the 428 tests opt in
        builder.UseSetting("Realtime:ServerTimeSeconds", "1");
        builder.UseSetting("Attachments:Directory", AttachmentsDir);
        if (attachmentMaxBytes is long mb) builder.UseSetting("Attachments:MaxBytes", mb.ToString());
        // REOS-111 to 114: the background workers (audit sealing and verification, telemetry housekeeping) are driven by their own tests; running them in every host would change rows under tests that compare databases.
        builder.UseSetting("Audit:Seal:Enabled", "false");
        builder.UseSetting("Telemetry:Maintenance:Enabled", "false");
        foreach (var (k, v) in settings ?? new Dictionary<string, string>()) builder.UseSetting(k, v);
        builder.UseSetting("Notifications:ScanSeconds", "3600");   // the scheduler is driven by its own tests; keep it quiet here
        builder.UseSetting("Backup:Directory", Path.Combine(_dir, "bk"));
        builder.UseSetting("Logging:File", Path.Combine(_dir, "log-.txt"));
        // REOS-68: outside Development the app refuses to start unless AllowedHosts names its host. The test server's own host is localhost;
        // releases.example.com is the deployment name the proxy tests send. Any other Host is still refused with 400.
        if (environment != "Development") builder.UseSetting("AllowedHosts", TestHosts);
        // REOS-74: outside Development an unencrypted key ring refuses to start; tests that boot Production opt in (KeyRingEncryptionTests cover the refusal).
        builder.UseSetting("DataProtection:AllowUnprotectedKeys", "true");
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IControlWebhookSender>(ControlSender);
            if (Clock is not null) { services.RemoveAll<TimeProvider>(); services.AddSingleton(Clock); }
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        KeepLogOfUnhandledException();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>
    /// CI run 90 (macOS): a request answered 500 and the cause was lost with this host's temporary directory. A host log that records an unhandled
    /// exception is copied to <c>logs/api-tests/</c> at the repository root, which CI uploads when a step fails. Tests that provoke errors on purpose
    /// log them as handled errors, not unhandled exceptions, so they are not copied.
    /// </summary>
    private void KeepLogOfUnhandledException()
    {
        try
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "db", "schema.sql"))) root = root.Parent;
            if (root is null || !Directory.Exists(_dir)) return;
            foreach (var log in Directory.GetFiles(_dir, "log-*.txt"))
            {
                if (!File.ReadAllText(log).Contains("An unhandled exception has occurred", StringComparison.Ordinal)) continue;
                var keep = Directory.CreateDirectory(Path.Combine(root.FullName, "logs", "api-tests")).FullName;
                File.Copy(log, Path.Combine(keep, $"{Path.GetFileName(_dir)}-{Path.GetFileName(log)}"), overwrite: true);
            }
        }
        catch (IOException) { }   // best effort: never turn a passing test into a failure over a diagnostic copy
    }
}
