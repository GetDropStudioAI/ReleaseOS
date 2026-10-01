namespace ReleaseMgmt.Infrastructure.Sync;

/// <summary>What <c>/healthz</c> reports for one background loop (REOS-69).</summary>
/// <param name="State"><see cref="ServiceHeartbeat.Disabled"/>, <see cref="ServiceHeartbeat.Running"/> or <see cref="ServiceHeartbeat.Stalled"/>.</param>
/// <param name="LastCycleUtc">When the loop last finished a pass (null: none yet, or disabled).</param>
/// <param name="StalledAfterSeconds">How long without a pass counts as stalled (null when disabled).</param>
public sealed record HeartbeatSnapshot(string State, DateTime? LastCycleUtc, double? StalledAfterSeconds);

/// <summary>
/// The liveness of one background loop (REOS-69: the sync poller and the watchdog), read by <c>/healthz</c>. The loop calls <see cref="Started"/> when it begins,
/// <see cref="Beat"/> after every pass (a pass that failed still counts: the loop is alive, and the failure is an alert of its own) and <see cref="Exited"/> if
/// it ends while the app is not stopping. The loop is <b>stalled</b> when it has exited, or when no pass has finished for <see cref="StallAfter"/> since the
/// later of its last pass and its first due pass (start + <c>firstDueAfter</c>, so a start delay is not a stall). Time is the injected clock.
/// </summary>
public sealed class ServiceHeartbeat(TimeProvider time, bool enabled, TimeSpan stallAfter, TimeSpan firstDueAfter)
{
    public const string Disabled = "disabled", Running = "running", Stalled = "stalled";

    private readonly DateTime _created = time.GetUtcNow().UtcDateTime;
    private long _startedTicks, _lastTicks;   // 0 = not yet
    private volatile bool _exited;

    public bool Enabled => enabled;
    public TimeSpan StallAfter => stallAfter;

    public void Started() => Interlocked.Exchange(ref _startedTicks, time.GetUtcNow().UtcDateTime.Ticks);
    public void Beat() => Interlocked.Exchange(ref _lastTicks, time.GetUtcNow().UtcDateTime.Ticks);
    public void Exited() => _exited = true;

    public HeartbeatSnapshot Read()
    {
        if (!enabled) return new(Disabled, null, null);
        var last = Interlocked.Read(ref _lastTicks);
        var started = Interlocked.Read(ref _startedTicks);
        DateTime? lastUtc = last == 0 ? null : new DateTime(last, DateTimeKind.Utc);
        var reference = lastUtc ?? (started == 0 ? _created : new DateTime(started, DateTimeKind.Utc)) + firstDueAfter;
        var stalled = _exited || time.GetUtcNow().UtcDateTime - reference > stallAfter;
        return new(stalled ? Stalled : Running, lastUtc, stallAfter.TotalSeconds);
    }
}
