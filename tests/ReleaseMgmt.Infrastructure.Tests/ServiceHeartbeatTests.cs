using Microsoft.Extensions.Time.Testing;
using ReleaseMgmt.Infrastructure.Sync;

namespace ReleaseMgmt.Infrastructure.Tests;

/// <summary>REOS-69: the liveness rule behind <c>/healthz</c> for the sync poller and watchdog loops.</summary>
public class ServiceHeartbeatTests
{
    private static readonly TimeSpan Stall = TimeSpan.FromMinutes(15), Delay = TimeSpan.FromSeconds(5);

    [Fact]
    public void A_disabled_loop_is_disabled_whatever_the_time()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-05T08:00:00Z"));
        var h = new ServiceHeartbeat(clock, enabled: false, Stall, Delay);
        clock.Advance(TimeSpan.FromDays(3));
        Assert.Equal(new HeartbeatSnapshot(ServiceHeartbeat.Disabled, null, null), h.Read());
    }

    [Fact]
    public void Running_until_three_intervals_pass_without_a_pass_counting_from_the_first_due_pass_then_stalled()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-05T08:00:00Z"));
        var h = new ServiceHeartbeat(clock, enabled: true, Stall, Delay);
        h.Started();
        clock.Advance(Stall + Delay);   // exactly the threshold after the first pass was due: not yet
        Assert.Equal(ServiceHeartbeat.Running, h.Read().State);
        Assert.Null(h.Read().LastCycleUtc);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(ServiceHeartbeat.Stalled, h.Read().State);

        h.Beat();   // a pass finished: running again, with its time
        Assert.Equal(new HeartbeatSnapshot(ServiceHeartbeat.Running, DateTime.Parse("2026-10-05T08:15:06Z").ToUniversalTime(), Stall.TotalSeconds), h.Read());
        clock.Advance(Stall);
        Assert.Equal(ServiceHeartbeat.Running, h.Read().State);
        clock.Advance(TimeSpan.FromSeconds(1));
        var s = h.Read();
        Assert.Equal(ServiceHeartbeat.Stalled, s.State);
        Assert.Equal(DateTimeKind.Utc, s.LastCycleUtc!.Value.Kind);
    }

    [Fact]
    public void A_loop_that_ended_while_the_app_runs_is_stalled_at_once()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-05T08:00:00Z"));
        var h = new ServiceHeartbeat(clock, enabled: true, Stall, TimeSpan.Zero);
        h.Started(); h.Beat();
        h.Exited();
        Assert.Equal(ServiceHeartbeat.Stalled, h.Read().State);
    }
}
