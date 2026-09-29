using System.Net;
using System.Net.Http.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Services;

namespace ReleaseMgmt.Api.Tests;

/// <summary>REOS-25: two signed-in clients see each other's changes without a reload; notifications reach only their recipient.</summary>
public class RealtimeTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>Signs in through the API and connects a SignalR client carrying that session cookie.</summary>
    private static async Task<(HubConnection Hub, Channel<object[]> Events)> Connect(ApiFactory f, string role, string email)
    {
        var login = await f.CreateClient().PostAsJsonAsync("/auth/dev-login", new { email, name = email.Split('@')[0], role });
        login.EnsureSuccessStatusCode();
        var cookie = login.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        var events = Channel.CreateUnbounded<object[]>();
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(f.Server.BaseAddress, "/hub/trains"), o =>
            {
                o.HttpMessageHandlerFactory = _ => f.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;    // the in-memory test server has no real socket
                o.Headers["Cookie"] = cookie;
            }).Build();
        hub.On<string, int>("TrainChanged", (id, v) => events.Writer.TryWrite(["TrainChanged", id, v]));
        hub.On<string>("NotificationCreated", id => events.Writer.TryWrite(["NotificationCreated", id]));
        hub.On<string>("ServerTime", t => events.Writer.TryWrite(["ServerTime", t]));
        await hub.StartAsync();
        return (hub, events);
    }

    private static async Task<object[]> Next(Channel<object[]> ch, string kind)
    {
        using var cts = new CancellationTokenSource(Wait);
        while (await ch.Reader.WaitToReadAsync(cts.Token))
            while (ch.Reader.TryRead(out var e))
                if ((string)e[0] == kind) return e;
        throw new TimeoutException($"No {kind} received");
    }

    private static async Task<bool> Quiet(Channel<object[]> ch, string kind, TimeSpan window)
    {
        using var cts = new CancellationTokenSource(window);
        try
        {
            while (await ch.Reader.WaitToReadAsync(cts.Token))
                while (ch.Reader.TryRead(out var e))
                    if ((string)e[0] == kind) return false;
        }
        catch (OperationCanceledException) { }
        return true;
    }

    [Fact]
    public async Task Second_client_sees_the_first_clients_change_without_reload()
    {
        using var f = new ApiFactory();
        var rteA = await LifecycleContractTests.As(f, Roles.RTE, "a@x.com");
        await LifecycleContractTests.As(f, Roles.RTE, "b@x.com");
        LifecycleContractTests.SeedTrain(f, LifecycleContractTests.UserId(f, "a@x.com"), LifecycleContractTests.UserId(f, "a@x.com"));

        var (hubB, eventsB) = await Connect(f, Roles.RTE, "b@x.com");
        await using (hubB)
        {
            var r = await rteA.PostAsJsonAsync("/api/v1/gates/g1:start", new { });          // A changes something
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var e = await Next(eventsB, "TrainChanged");                                     // B is told, no reload
            Assert.Equal("t1", e[1]);
            Assert.True((int)e[2] >= 1);
        }
    }

    [Fact]
    public async Task Anonymous_clients_cannot_connect()
    {
        using var f = new ApiFactory();
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(f.Server.BaseAddress, "/hub/trains"), o => { o.HttpMessageHandlerFactory = _ => f.Server.CreateHandler(); o.Transports = HttpTransportType.LongPolling; })
            .Build();
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => hub.StartAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
    }

    [Fact]
    public async Task Server_time_is_pushed_on_a_timer()
    {
        using var f = new ApiFactory();     // the factory sets Realtime:ServerTimeSeconds = 1
        var (hub, events) = await Connect(f, Roles.RTE, "a@x.com");
        await using (hub)
        {
            var e = await Next(events, "ServerTime");
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$", (string)e[1]);
        }
    }

    [Fact]
    public async Task Notification_is_stored_audited_and_pushed_only_to_its_recipient()
    {
        using var f = new ApiFactory();
        await LifecycleContractTests.As(f, Roles.RTE, "a@x.com");
        await LifecycleContractTests.As(f, Roles.RTE, "b@x.com");
        var a = LifecycleContractTests.UserId(f, "a@x.com");
        var (hubA, eventsA) = await Connect(f, Roles.RTE, "a@x.com");
        var (hubB, eventsB) = await Connect(f, Roles.RTE, "b@x.com");
        await using (hubA) await using (hubB)
        {
            var id = await f.Services.GetRequiredService<INotifier>().NotifyAsync(new NotificationRequest(a, "GateEntered", "StageGate", "g1", "Code Freeze is yours"));
            var e = await Next(eventsA, "NotificationCreated");
            Assert.Equal(id, e[1]);
            Assert.True(await Quiet(eventsB, "NotificationCreated", TimeSpan.FromSeconds(1.5)), "another user received someone else's notification");
        }
        Assert.Equal("1", LifecycleContractTests.Scalar(f, "SELECT COUNT(*) FROM Notifications WHERE Message='Code Freeze is yours'"));
        Assert.Equal("1", LifecycleContractTests.Scalar(f, "SELECT COUNT(*) FROM AuditEvents WHERE EntityType='Notification' AND Action='Created' AND ActorUserId IS NULL"));
    }

    [Fact]
    public async Task Notifying_an_unknown_user_fails_loudly_and_writes_nothing()
    {
        using var f = new ApiFactory();
        var notifier = f.Services.GetRequiredService<INotifier>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => notifier.NotifyAsync(new NotificationRequest("nobody", "GateEntered", "StageGate", "g1", "x")));
        Assert.Equal("0", LifecycleContractTests.Scalar(f, "SELECT COUNT(*) FROM Notifications"));
    }
}
