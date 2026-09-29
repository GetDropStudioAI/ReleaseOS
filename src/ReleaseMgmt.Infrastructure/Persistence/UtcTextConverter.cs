using System.Globalization;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ReleaseMgmt.Infrastructure.Persistence;

/// <summary>UTC DateTime stored as ISO-8601 TEXT ending in Z (D5). DateTimeOffset is banned in entities.</summary>
public sealed class UtcTextConverter() : ValueConverter<DateTime, string>(
    v => v.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture),
    v => DateTime.Parse(v, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal))
{
    public const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
}
