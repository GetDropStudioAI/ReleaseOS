using System.Globalization;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ReleaseMgmt.Infrastructure.Persistence;

/// <summary>UTC DateTime stored as ISO-8601 TEXT 'YYYY-MM-DDTHH:MM:SSZ' (whole seconds, per db/schema.sql; D5).
/// Whole seconds matter: triggers compare these strings, and "…05.123Z" sorts before "…05Z".</summary>
public sealed class UtcTextConverter() : ValueConverter<DateTime, string>(
    v => v.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture),
    v => DateTime.Parse(v, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal))
{
    public const string Format = "yyyy-MM-dd'T'HH:mm:ss'Z'";
}

/// <summary>Calendar date stored as 'YYYY-MM-DD'.</summary>
public sealed class DateTextConverter() : ValueConverter<DateOnly, string>(
    v => v.ToString(Format, CultureInfo.InvariantCulture),
    v => DateOnly.ParseExact(v, Format, CultureInfo.InvariantCulture))
{
    public const string Format = "yyyy-MM-dd";
}
