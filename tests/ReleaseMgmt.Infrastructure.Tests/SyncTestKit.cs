using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Domain.Sync;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Reminders;
using ReleaseMgmt.Infrastructure.Services;
using ReleaseMgmt.Infrastructure.Sync;

namespace ReleaseMgmt.Infrastructure.Tests;

/// <summary>Recorded HTTP responses and the wiring shared by the REOS-39/40/41 tests. No test here opens a socket.</summary>
internal static class SyncTestKit
{
    public const string Secret = "S3cr3t-Token-Value-9f1c";
    public const string SnUrl = "https://acme.service-now.com", JiraUrl = "https://acme.atlassian.net";

    public sealed class FakeHttp : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } = (_, _) => Task.FromResult(Json(200, "{}"));
        public List<string> Requests { get; } = [];
        public List<string?> Auth { get; } = [];
        public int Count => Requests.Count;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Requests) { Requests.Add($"{request.Method} {request.RequestUri!.PathAndQuery}"); Auth.Add(request.Headers.Authorization?.ToString()); }
            return await Respond(request, ct);
        }
        public void Always(Func<HttpResponseMessage> make) => Respond = (_, _) => Task.FromResult(make());
        public void Always(int status, string body = "{}") => Respond = (_, _) => Task.FromResult(Json(status, body));
    }

    public sealed class FakeHttpFactory(HttpMessageHandler h) : IHttpClientFactory { public HttpClient CreateClient(string name) => new(h, disposeHandler: false); }

    public static HttpResponseMessage Json(int status, string body, Action<HttpResponseMessage>? tweak = null)
    {
        var r = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        tweak?.Invoke(r);
        return r;
    }

    // ---- recorded bodies (keys, states and dates only, plus the fields a real tenant adds and we must ignore) ---------------------------
    public static string SnChange(string state, string? start = "2026-10-30 02:00:00", string? end = "2026-10-30 06:00:00") =>
        $$"""{"result":[{"number":"CHG0030001","state":"{{state}}","start_date":"{{start}}","end_date":"{{end}}","short_description":"Card PAN 4111111111111111 rollout"}]}""";
    public const string SnEmpty = """{"result":[]}""";
    public const string JiraIssue = """{"id":"10001","key":"PAY-12","fields":{"status":{"name":"In Progress","statusCategory":{"key":"indeterminate"}},"summary":"Customer John Smith card issue"}}""";
    public static string JiraVersions(bool released, string date = "2026-10-30") =>
        $$"""{"isLast":true,"values":[{"id":"1","name":"3.9.0","released":true,"releaseDate":"2026-09-01"},{"id":"2","name":"4.5.0","released":{{(released ? "true" : "false")}},"archived":false,"releaseDate":"{{date}}","description":"secret text"}]}""";

    public sealed class MemCreds : ICredentialStore
    {
        public Dictionary<string, ConnectorCredentials> Items { get; } = [];
        public Task SetAsync(string s, ConnectorCredentials c, CancellationToken ct = default) { Items[s] = c; return Task.CompletedTask; }
        public Task<ConnectorCredentials?> GetAsync(string s, CancellationToken ct = default) => Task.FromResult(Items.GetValueOrDefault(s));
        public Task<string?> KindAsync(string s, CancellationToken ct = default) => Task.FromResult(Items.GetValueOrDefault(s)?.Kind);
        public Task<bool> ClearAsync(string s, CancellationToken ct = default) => Task.FromResult(Items.Remove(s));
    }

    public sealed class Cfg(Dictionary<string, string?> v) : IConfiguration
    {
        public string? this[string key] { get => v.GetValueOrDefault(key); set => v[key] = value; }
        public IConfigurationSection GetSection(string key) => throw new NotSupportedException();
        public IEnumerable<IConfigurationSection> GetChildren() => [];
        public Microsoft.Extensions.Primitives.IChangeToken GetReloadToken() => throw new NotSupportedException();
    }

    public sealed class TestEnv(string name = "Production") : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    public sealed class RecordingSink : IAlertSink
    {
        public List<(string Source, string Kind, string Key, string Message)> Raised { get; } = [];
        public Task RaiseAsync(string sourceSystem, string kind, string key, string message, CancellationToken ct = default) { Raised.Add((sourceSystem, kind, key, message)); return Task.CompletedTask; }
    }

    public sealed class RecordingRealtime : IRealtimePublisher
    {
        public List<string> Alerts { get; } = [];
        public Task TrainChangedAsync(string trainId, int version, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotificationCreatedAsync(string userId, string notificationId, CancellationToken ct = default) => Task.CompletedTask;
        public Task ForecastChangedAsync(string trainId, string runId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SyncAlertRaisedAsync(string alertId, CancellationToken ct = default) { lock (Alerts) Alerts.Add(alertId); return Task.CompletedTask; }
    }

    public sealed class DbFactory(string path) : IDbContextFactory<ReleaseDbContext>
    {
        public ReleaseDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ReleaseDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").AddInterceptors(new SqliteConnectionInterceptor()).Options);
    }

    /// <summary>A migrated database (users rte/rm/gov1/gov2/dev, train t1 Planning target 2026-10-30) with the whole sync engine wired to fakes.</summary>
    public sealed class Env
    {
        public required string Path { get; init; }
        public required FakeTimeProvider Time { get; init; }
        public required DbFactory Db { get; init; }
        public required FakeHttp Http { get; init; }
        public required MemCreds Creds { get; init; }
        public required RecordingSink Sink { get; init; }
        public required RecordingRealtime Realtime { get; init; }
        public required SyncOptions Options { get; init; }
        public required SyncAlertWriter Alerts { get; init; }
        public required SyncPollerService Poller { get; init; }
        public required ConnectorService Service { get; init; }
        public required Cfg Config { get; init; }

        public SyncWatchdogService NewWatchdog() => new(Db, Time, Alerts, Sink, Options, NullLogger<SyncWatchdogService>.Instance);

        public void Sql(string sql, params object?[] p) { using var c = TriggerSuiteFixture.Open(Path); TriggerSuiteFixture.Run(c, sql, p); }
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
        public void At(string utc) => Time.SetUtcNow(DateTimeOffset.Parse(utc, null, System.Globalization.DateTimeStyles.AssumeUniversal));
        public string Link(string id, string col) => (string)Query($"SELECT {col} FROM ExternalLinks WHERE Id=?", id)[0][0]!;
        public long OpenAlerts(string? kind = null) => kind is null ? Count("SELECT COUNT(*) FROM SyncAlerts WHERE IsResolved=0") : Count("SELECT COUNT(*) FROM SyncAlerts WHERE IsResolved=0 AND Kind=?", kind);

        public void AddSnLink(string id = "l1", string key = "CHG0030001", string train = "t1") =>
            Sql("INSERT INTO ExternalLinks(Id,ReleaseTrainId,EntityType,EntityId,SourceSystem,ExternalKey) VALUES(?,?,'Train',?,'ServiceNow',?)", id, train, train, key);
        public void AddJiraLink(string id, string key, string entityType = "Product", string entityId = "p1", string train = "t1") =>
            Sql("INSERT INTO ExternalLinks(Id,ReleaseTrainId,EntityType,EntityId,SourceSystem,ExternalKey) VALUES(?,?,?,?,'Jira',?)", id, train, entityType, entityId, key);

        /// <summary>The first call of each cycle answers with <paramref name="body"/> for CHG keys; tests override <see cref="Http"/> for anything fancier.</summary>
        public void SnAnswers(string state = "-1") => Http.Always(200, SnChange(state));
    }

    public static Env NewEnv(TriggerSuiteFixture fx, string now = "2026-10-30T02:30:00Z", Dictionary<string, string?>? cfg = null, bool serviceNow = true, bool jira = false)
    {
        var path = fx.FreshPath();
        var time = new FakeTimeProvider();
        time.SetUtcNow(DateTimeOffset.Parse(now, null, System.Globalization.DateTimeStyles.AssumeUniversal));
        var values = new Dictionary<string, string?> { ["Sync:PollSeconds"] = "300", ["Sync:WindowPollSeconds"] = "60", ["Sync:TimeoutSeconds"] = "0.3", ["Sync:BackoffBaseSeconds"] = "30" };
        if (cfg is not null) foreach (var (k, v) in cfg) values[k] = v;
        var config = new Cfg(values);
        var options = SyncOptions.From(config);
        var db = new DbFactory(path);
        var http = new FakeHttp();
        var creds = new MemCreds();
        var sink = new RecordingSink();
        var rt = new RecordingRealtime();
        var alerts = new SyncAlertWriter(db, time, NullLogger<SyncAlertWriter>.Instance, new Notifier(db, time), rt);
        var factory = new ConnectorFactory(new FakeHttpFactory(http), creds, options, time, new TestEnv(), config);
        var writer = new SyncCycleWriter(db, time, rt);
        var poller = new SyncPollerService(db, time, factory, alerts, sink, writer, options, config, NullLogger<SyncPollerService>.Instance);
        var service = new ConnectorService(db, time, creds, factory, alerts, options, new TestEnv(), null, rt);
        var e = new Env { Path = path, Time = time, Db = db, Http = http, Creds = creds, Sink = sink, Realtime = rt, Options = options, Alerts = alerts, Poller = poller, Service = service, Config = config };
        if (serviceNow)
        {
            creds.Items["ServiceNow"] = new ConnectorCredentials(ConnectorCredentials.Basic, "svc-release", Secret);
            e.Sql("INSERT INTO ConnectorState(SourceSystem,BaseUrl) VALUES('ServiceNow',?)", SnUrl);
        }
        if (jira)
        {
            creds.Items["Jira"] = new ConnectorCredentials(ConnectorCredentials.ApiToken, "rae@example.com", Secret);
            e.Sql("INSERT INTO ConnectorState(SourceSystem,BaseUrl) VALUES('Jira',?)", JiraUrl);
        }
        return e;
    }
}
