using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using ReleaseMgmt.Api.Auth;

namespace ReleaseMgmt.Api.Tests;

/// <summary>REOS-163: the token bucket that serves the ingest keys, the API tokens and the workspaces, on the injected clock.</summary>
public class PublicApiLimiterTests
{
    [Fact]
    public void The_limiter_is_a_token_bucket_on_the_injected_clock()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-05T08:00:00Z"));
        var limiter = new IngestLimiter(clock, new ConfigurationBuilder().Build());
        var states = Enumerable.Range(0, 5).Select(_ => limiter.Take("pt:a", 1, 3)).ToList();
        Assert.Equal([true, true, true, false, false], states.Select(s => s.Allowed));
        Assert.Equal([2, 1, 0, 0, 0], states.Select(s => s.Remaining));
        Assert.All(states, s => Assert.Equal(3, s.Limit));
        Assert.Equal(1, states[3].RetryAfterSeconds);
        Assert.Equal(3, states[2].ResetSeconds);   // seconds until the bucket is full again
        Assert.False(limiter.Take("pt:a", 1, 3).Allowed);   // time stands still: nothing refills
        Assert.True(limiter.Take("pt:b", 1, 3).Allowed);    // buckets are by name
        clock.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(limiter.Take("pt:a", 1, 3).Allowed);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(limiter.Take("pt:a", 1, 3).Allowed);
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(2, limiter.Take("pt:a", 1, 3).Remaining);   // refilled to the burst, no further
        Assert.True(limiter.TryTake("k1", out _));   // the ingest key's own call still works on the same bucket code
    }
}
