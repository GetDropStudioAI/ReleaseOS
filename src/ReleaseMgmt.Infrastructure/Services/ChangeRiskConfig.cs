using Microsoft.Extensions.Configuration;
using ReleaseMgmt.Domain.Services;

namespace ReleaseMgmt.Infrastructure.Services;

/// <summary>REOS-176: reads the <c>Cab:Risk:*</c> keys. A value that is not a whole number of at least 1 stops the start rather than being guessed.</summary>
public static class ChangeRiskConfig
{
    public static ChangeRiskOptions From(IConfiguration c)
    {
        var d = new ChangeRiskOptions();
        int Read(string name, int fallback)
        {
            var text = c[$"{ChangeRiskOptions.Section}:{name}"];
            if (string.IsNullOrWhiteSpace(text)) return fallback;
            return int.TryParse(text, out var v) && v is > 0 and <= 100000 ? v : throw new InvalidOperationException($"{ChangeRiskOptions.Section}:{name} must be a whole number of at least 1, not \"{text}\"");
        }
        return new() { NearFreezeHours = Read(nameof(ChangeRiskOptions.NearFreezeHours), d.NearFreezeHours), LargeScopeProducts = Read(nameof(ChangeRiskOptions.LargeScopeProducts), d.LargeScopeProducts),
            PriorFailureLookbackDays = Read(nameof(ChangeRiskOptions.PriorFailureLookbackDays), d.PriorFailureLookbackDays) };
    }
}
