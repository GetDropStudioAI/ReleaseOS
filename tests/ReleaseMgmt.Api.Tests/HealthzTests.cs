using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using ReleaseMgmt.Domain.Sync;
using ReleaseMgmt.Infrastructure.Sync;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// REOS-69: <c>/healthz</c> reported the poller and the watchdog as a hard-coded "notConfigured". It now reports each loop as <c>disabled</c>,
/// <c>running</c> with the UTC time of its last pass, or <c>stalled</c>, and answers 503 when an enabled loop has stalled.
/// </summary>
public class HealthzTests
{
    private static async Task<(HttpStatusCode Status, JsonElement Body)> Health(HttpClient c)
    {
        var r = await c.GetAsync("/healthz");
        return (r.StatusCode, JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone());
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> Until(HttpClient c, Func<(HttpStatusCode Status, JsonElement Body), bool> done, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            var h = await Health(c);
            if (done(h)) return h;
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"/healthz never showed {what}: {(int)h.Status} {h.Body}");
            await Task.Delay(25);
        }
    }

    private static string State(JsonElement body, string loop) => body.GetProperty(loop).GetProperty("state").GetString()!;
    private static bool HasCycle(JsonElement body, string loop) => body.GetProperty(loop).GetProperty("lastCycleUtc").ValueKind == JsonValueKind.String;

    [Fact]
    public async Task With_sync_switched_off_both_loops_are_reported_disabled_and_the_app_is_healthy()
    {
        using var root = new ApiFactory();
        using var f = root.WithWebHostBuilder(b => b.UseSetting("Sync:Enabled", "false"));
        var (status, body) = await Health(f.CreateClient());
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Healthy", body.GetProperty("status").GetString());
        Assert.Equal("disabled", State(body, "poller"));
        Assert.Equal("disabled", State(body, "watchdog"));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("poller").GetProperty("lastCycleUtc").ValueKind);
        Assert.DoesNotContain("notConfigured", body.ToString());
    }

    [Fact]
    public async Task Running_loops_report_the_UTC_time_of_their_last_pass()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);   // before the host starts: the poller's first pass runs as it starts
        using var root = new ApiFactory();
        using var f = root.WithWebHostBuilder(b => { b.UseSetting("Sync:StartDelaySeconds", "0"); b.UseSetting("Sync:WatchdogSeconds", "0.05"); });
        var c = f.CreateClient();
        var (status, body) = await Until(c, h => HasCycle(h.Body, "poller") && HasCycle(h.Body, "watchdog"), "a pass of both loops");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Healthy", body.GetProperty("status").GetString());
        foreach (var loop in new[] { "poller", "watchdog" })
        {
            Assert.Equal("running", State(body, loop));
            var text = body.GetProperty(loop).GetProperty("lastCycleUtc").GetString()!;
            Assert.EndsWith("Z", text);
            var at = DateTime.Parse(text, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
            Assert.InRange(at, before, DateTime.UtcNow.AddSeconds(2));
        }
        Assert.Equal(900, body.GetProperty("poller").GetProperty("stalledAfterSeconds").GetDouble());   // 3 poll intervals of 300 s
    }

    /// <summary>A connector whose creation never returns until the test lets it: the poller's cycle hangs, as a stuck ITSM call or a deadlock would.</summary>
    private sealed class HangingConnectors : IConnectorFactory
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IConnector> CreateAsync(string source, string baseUrl, CancellationToken ct)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            throw new ConnectorException(ConnectorErrorKind.Network, "released by the test");
        }
    }

    [Fact]
    public async Task A_poller_whose_cycle_hangs_is_reported_stalled_with_503_and_recovers_when_the_cycle_ends()
    {
        using var root = new ApiFactory();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-05T08:00:00Z"));
        var connectors = new HangingConnectors();
        using var f = root.WithWebHostBuilder(b =>
        {
            b.UseSetting("Sync:StartDelaySeconds", "0");
            b.UseSetting("Sync:WatchdogSeconds", "3600");   // the watchdog's timer does not fire in the time this test advances
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<TimeProvider>(); s.AddSingleton<TimeProvider>(clock);
                s.RemoveAll<IConnectorFactory>(); s.AddSingleton<IConnectorFactory>(connectors);
            });
        });
        var c = f.CreateClient();
        await Until(c, h => HasCycle(h.Body, "poller"), "the poller's first pass");   // no connector yet: an empty pass

        Sql(root, """
            INSERT INTO ReleaseTrains(Id,Title,TargetReleaseDate,RiskTier,CreatedAt,UpdatedAt) VALUES('t1','R26.11','2026-11-30','Low','2026-10-01T00:00:00Z','2026-10-01T00:00:00Z');
            INSERT INTO ConnectorState(SourceSystem,BaseUrl,IsEnabled) VALUES('Jira','https://acme.atlassian.net',1);
            INSERT INTO ExternalLinks(Id,ReleaseTrainId,EntityType,EntityId,SourceSystem,ExternalKey) VALUES('l1','t1','Train','t1','Jira','PAY-1');
            """);
        // The next pass is due one poll interval (300 s) later; step the clock until the poller is inside it (it may still be scheduling its wait).
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!connectors.Entered.Task.IsCompleted)
        {
            Assert.True(DateTime.UtcNow < deadline, "the poller never started its second cycle");
            clock.Advance(TimeSpan.FromSeconds(30));
            await Task.WhenAny(connectors.Entered.Task, Task.Delay(50));
        }
        var (fresh, _) = await Health(c);
        Assert.Equal(HttpStatusCode.OK, fresh);   // hanging for a moment is not a stall

        clock.Advance(TimeSpan.FromSeconds(901));   // more than 3 intervals since its last finished pass
        var (status, body) = await Health(c);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("Unhealthy", body.GetProperty("status").GetString());
        Assert.Equal("ok", body.GetProperty("db").GetString());
        Assert.Equal("stalled", State(body, "poller"));
        Assert.True(HasCycle(body, "poller"), "a stalled loop still says when it last finished a pass");
        Assert.Equal("running", State(body, "watchdog"));

        connectors.Release.SetResult();   // the cycle ends (as a connector failure, which is an alert, not a stall) and the loop beats again
        var (after, afterBody) = await Until(c, h => h.Status == HttpStatusCode.OK, "recovery");
        Assert.Equal("running", State(afterBody, "poller"));
    }
}
