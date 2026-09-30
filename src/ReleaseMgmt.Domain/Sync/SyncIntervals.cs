namespace ReleaseMgmt.Domain.Sync;

/// <summary>Pure timing rules of the sync pipeline (PROJECT_SCOPE 5.3): poll cadence, stall/stale thresholds, 429 backoff.</summary>
public static class SyncIntervals
{
    public const int StallIntervals = 3;
    public const int RateLimitAlertAfter = 3;

    /// <summary>5 minutes normally, 1 minute while any deployment window is open (D11).</summary>
    public static TimeSpan PollInterval(bool windowOpen, TimeSpan slow, TimeSpan fast) => windowOpen ? fast : slow;

    /// <summary>
    /// How old a heartbeat or a link may be before it counts as stalled/stale: <see cref="StallIntervals"/> intervals.
    /// While a window has been open for at least one slow interval the poller has been on the fast cadence, so the threshold is three fast intervals;
    /// before that (the window just opened) the last cycle may legitimately be a slow interval old, so the slow cadence applies.
    /// </summary>
    public static TimeSpan StallThreshold(DateTime now, DateTime? openWindowSince, TimeSpan slow, TimeSpan fast, int intervals = StallIntervals)
    {
        var onFast = openWindowSince is DateTime since && now - since >= slow;
        return (onFast ? fast : slow) * intervals;
    }

    /// <summary>A link that has synced before but not within the threshold renders as stale, even without an error (5.3.6). Never-synced links are "not yet synced", not stale.</summary>
    public static bool IsStale(DateTime? lastSyncedAt, DateTime now, TimeSpan threshold) => lastSyncedAt is DateTime t && now - t >= threshold;

    /// <summary>The watchdog's test: no cycle has completed for <see cref="StallIntervals"/> intervals (D30). <paramref name="reference"/> is the watchdog's own start, so a fresh start is not a stall.</summary>
    public static bool IsStalled(DateTime? lastCycleCompletedAt, DateTime reference, DateTime now, TimeSpan threshold)
    {
        var last = lastCycleCompletedAt is DateTime t && t > reference ? t : reference;
        return now - last >= threshold;
    }

    /// <summary>
    /// Exponential backoff after the n-th consecutive 429: base * 2^(n-1), or the server's Retry-After when longer, never more than <paramref name="cap"/>.
    /// The cap is the poll interval, so three throttled cycles still happen within three intervals and the alert is not postponed by the backoff.
    /// </summary>
    public static TimeSpan Backoff(int consecutive429, TimeSpan baseDelay, TimeSpan? retryAfter, TimeSpan cap)
    {
        var n = Math.Clamp(consecutive429, 1, 20);
        var d = TimeSpan.FromTicks(baseDelay.Ticks * (1L << (n - 1)));
        if (retryAfter is TimeSpan ra && ra > d) d = ra;
        return d > cap ? cap : d;
    }
}
