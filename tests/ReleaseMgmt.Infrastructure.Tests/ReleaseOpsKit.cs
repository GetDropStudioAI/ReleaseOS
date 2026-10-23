using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Comms;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Reminders;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Infrastructure.Tests;

/// <summary>A control webhook sender that records what it was asked to send and answers as told.</summary>
internal sealed class FakeControlSender : IControlWebhookSender
{
    public ConcurrentQueue<(CommWebhookTarget Target, ControlDelivery Delivery)> Sent { get; } = new();
    public Func<ControlDelivery, ControlSendResult> Answer { get; set; } = _ => new(true, 200, "deploy.example.com", null);
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    public async Task<ControlSendResult> SendAsync(CommWebhookTarget target, ControlDelivery delivery, CancellationToken ct)
    {
        Sent.Enqueue((target, delivery));
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
        return Answer(delivery);
    }
}

internal sealed class FakeSecrets : IControlSigningSecrets
{
    public ConcurrentDictionary<string, string> Keys { get; } = new();
    public Task<bool> ExistsAsync(string destinationId, CancellationToken ct = default) => Task.FromResult(Keys.ContainsKey(destinationId));
    public Task<string?> GetAsync(string destinationId, CancellationToken ct = default) => Task.FromResult(Keys.TryGetValue(destinationId, out var k) ? k : null);
    public Task<string> RotateAsync(string destinationId, CancellationToken ct = default) { var k = "whs_test_" + Guid.NewGuid().ToString("N"); Keys[destinationId] = k; return Task.FromResult(k); }
    public Task<bool> ClearAsync(string destinationId, CancellationToken ct = default) => Task.FromResult(Keys.TryRemove(destinationId, out _));
}

/// <summary>
/// One migrated database (users rte, rm, gov1, gov2, dev; train t1; the default workspace), a fake clock at 2026-10-20 14:00:00Z and the services of REOS-111 to 114 wired the way the host wires them,
/// with a recording webhook sender and an in-memory signing-key store. <see cref="Seed"/> adds a Production environment with thresholds and a control webhook, a catalog service and a running canary.
/// </summary>
internal sealed class OpsKit
{
    public static readonly DateTime T0 = new(2026, 10, 20, 14, 0, 0, DateTimeKind.Utc);
    public static readonly IngestPrincipal Key = new("ik1", "CI", "default");
    public const string Hook = "wh1";

    private sealed class Factory(string path) : IDbContextFactory<ReleaseDbContext>
    {
        public ReleaseDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ReleaseDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").AddInterceptors(new SqliteConnectionInterceptor()).Options);
    }

    public string Path { get; }
    public FakeTimeProvider Time { get; }
    public IDbContextFactory<ReleaseDbContext> Db { get; }
    public IConfiguration Config { get; }
    public FakeControlSender Sender { get; } = new();
    public FakeSecrets Secrets { get; } = new();
    public SyncAlertWriter Alerts { get; }
    public DeploymentControlService Control { get; }
    public TelemetryService Telemetry { get; }
    public DeploymentService Deployments { get; }
    public EnvironmentService Environments { get; }
    public RunService Runs { get; }
    public AuditChainService Chain { get; }
    public ApprovalPolicyService Policies { get; }
    public ApprovalService Approvals { get; }
    public WaiverService Waivers { get; }
    public FreezeService Freezes { get; }
    public ExceptionService Exceptions { get; }

    public OpsKit(TriggerSuiteFixture fx, IDictionary<string, string?>? settings = null)
    {
        Path = fx.FreshPath();
        Time = new FakeTimeProvider(new DateTimeOffset(T0));
        Db = new Factory(Path);
        Config = new ConfigurationBuilder().AddInMemoryCollection(settings ?? new Dictionary<string, string?>()).Build();
        Alerts = new SyncAlertWriter(Db, Time, NullLogger<SyncAlertWriter>.Instance);
        Control = new DeploymentControlService(Db, Time, Config, Sender, Secrets, Alerts);
        Telemetry = new TelemetryService(Db, Time, Config, null, Control, Alerts);
        Deployments = new DeploymentService(Db, Time);
        Environments = new EnvironmentService(Db, Time, null, Secrets);
        Runs = new RunService(Db, Time);
        Chain = new AuditChainService(Db, Time, Config, Alerts);
        Policies = new ApprovalPolicyService(Db, Time);
        Approvals = new ApprovalService(Db, Time);
        Waivers = new WaiverService(Db, Time);
        Freezes = new FreezeService(Db, Time, null, null, null, Config);
        Exceptions = new ExceptionService(Db, Time);
    }

    public void Sql(string sql, params object?[] p)
    {
        using var c = TriggerSuiteFixture.Open(Path);
        TriggerSuiteFixture.Run(c, sql, p);
    }

    public List<object?[]> Query(string sql, params object?[] p)
    {
        using var c = TriggerSuiteFixture.Open(Path);
        using var cmd = c.CreateCommand();
        TriggerSuiteFixture.Bind(cmd, sql, p);
        using var r = cmd.ExecuteReader();
        var rows = new List<object?[]>();
        while (r.Read()) { var row = new object?[r.FieldCount]; r.GetValues(row!); rows.Add(row.Select(v => v is DBNull ? null : v).ToArray()); }
        return rows;
    }

    public long Count(string sql, params object?[] p) => Convert.ToInt64(Query(sql, p)[0][0]);
    public string Text(string sql, params object?[] p) => Convert.ToString(Query(sql, p)[0][0])!;
    public static string Iso(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
    public long Audits(string entity, string action) => Count("SELECT count(*) FROM AuditEvents WHERE EntityType=? AND Action=?", entity, action);

    /// <summary>
    /// Production environment e1 (error rate above 0.5 %, p95 above 300 ms or availability below 99.5 % for 120 s; SLO 99.9 %; canary steps watched 600 s) with a control webhook and a signing key,
    /// service sv1, and running canary deployment d1 (steps 5, 25, 50, 100; at 5 %; started now) for train t1.
    /// </summary>
    public OpsKit Seed(string strategy = "Canary", bool webhook = true, bool signingKey = true)
    {
        var now = Iso(T0);
        Sql($@"
            INSERT INTO WebhookDestinations(Id,Name,Host,ProtectedUrl,UrlHmac,Kind) VALUES('{Hook}','Deploy tool','deploy.example.com','CfDJ8x',printf('%064d',9),'Generic');
            INSERT INTO Environments(Id,Name,Kind,CreatedAt,ErrorRateMaxPct,LatencyP95MaxMs,AvailabilityMinPct,SloAvailabilityPct,SloLatencyP95Ms,SloErrorRatePct,ThresholdForSeconds,CanaryObservationSeconds{(webhook ? ",ControlWebhookDestinationId" : "")})
              VALUES('e1','Production','Production','{now}',0.5,300,99.5,99.9,250,0.4,120,600{(webhook ? $",'{Hook}'" : "")});
            INSERT INTO CatalogServices(Id,ServiceKey,Name,CreatedAt) VALUES('sv1','payments-api','Payments API','{now}');
            INSERT INTO IngestKeys(Id,Name,KeyPrefix,KeySha256,CreatedAt) VALUES('ik1','CI','rk_ab12',printf('%064d',1),'{now}');
            INSERT INTO Deployments(Id,ExternalId,CatalogServiceId,EnvironmentId,ReleaseTrainId,ReleaseVersion,Strategy,CanarySteps,State,TrafficPercent,StartedAt,StepStartedAt,CreatedAt,UpdatedAt,IngestKeyId)
              VALUES('d1','ci-1','sv1','e1','t1','4.5.0','{strategy}',{(strategy == "Canary" ? "'[5,25,50,100]'" : "NULL")},'Running',5,'{now}','{now}','{now}','{now}','ik1');");
        if (webhook && signingKey) Secrets.Keys[Hook] = "whs_test_secret";
        return this;
    }

    public static IngestSample S(string series, DateTime at, double? er = null, double? lat = null, double? av = null, long? req = null) => new(series, at, er, lat, av, req);

    /// <summary>Samples every 30 s from <paramref name="from"/>, <paramref name="count"/> of them, with the given error rate.</summary>
    public static List<IngestSample> Run(string series, DateTime from, int count, double? er = null, double? lat = null, double? av = null, int stepSeconds = 30) =>
        [.. Enumerable.Range(0, count).Select(i => S(series, from.AddSeconds(i * stepSeconds), er, lat, av))];

    public static string[] GuardsOf<T>(ServiceResult<T> r) => [.. r.Failures.Select(f => f.Guard)];
}
