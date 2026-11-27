using Microsoft.Extensions.Configuration;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Infrastructure.Tests;

/// <summary>REOS-176: the <c>Cab:Risk:*</c> thresholds have defaults that work untouched, and a bad value stops the start instead of being guessed.</summary>
public class ChangeRiskConfigTests
{
    private static IConfiguration Cfg(params (string Key, string? Value)[] v) => new ConfigurationBuilder().AddInMemoryCollection(v.ToDictionary(x => x.Key, x => x.Value)).Build();

    [Fact]
    public void Nothing_set_means_the_defaults() => Assert.Equal(new ChangeRiskOptions(), ChangeRiskConfig.From(Cfg()));

    [Fact]
    public void Each_key_overrides_its_own_threshold_only()
    {
        var o = ChangeRiskConfig.From(Cfg(("Cab:Risk:NearFreezeHours", "24"), ("Cab:Risk:LargeScopeProducts", "3")));
        Assert.Equal((24, 3, 90), (o.NearFreezeHours, o.LargeScopeProducts, o.PriorFailureLookbackDays));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("many")]
    [InlineData("2.5")]
    public void A_value_that_is_not_a_positive_whole_number_stops_the_start(string bad)
    {
        var e = Assert.Throws<InvalidOperationException>(() => ChangeRiskConfig.From(Cfg(("Cab:Risk:PriorFailureLookbackDays", bad))));
        Assert.Contains("Cab:Risk:PriorFailureLookbackDays", e.Message);
    }
}
