using Microsoft.Extensions.Configuration;

namespace ReleaseMgmt.Infrastructure.Reminders;

/// <summary>What a team channel is told (PROJECT_SCOPE 5.5). The URL never appears here: the sender resolves it from the team's WebhookDestination.</summary>
public sealed record WebhookNotice(string TeamId, string Kind, string EntityType, string EntityId, int EscalationLevel, string Message, string? TrainId);

/// <summary>Delivers a notice to a team's webhook, if it has one. Implemented in the Api host (IHttpClientFactory). Never throws for a delivery failure: it raises an alert and returns false.</summary>
public interface ITeamWebhookSender
{
    Task<bool> SendAsync(WebhookNotice notice, CancellationToken ct = default);
}

/// <summary>Display time zone (D24) helpers: gate due dates are calendar days, and "due" ends with that day in the display zone (Q-037b).</summary>
public sealed class DisplayClock(TimeZoneInfo zone)
{
    public TimeZoneInfo Zone { get; } = zone;

    /// <summary>Config <c>Display:TimeZone</c>; an unknown id is an error, not a silent UTC fallback.</summary>
    public static DisplayClock From(IConfiguration config)
    {
        var id = config["Display:TimeZone"] ?? "America/Chicago";
        try { return new DisplayClock(TimeZoneInfo.FindSystemTimeZoneById(id)); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new InvalidOperationException($"Display:TimeZone '{id}' is not a known time zone", ex);
        }
    }

    public DateOnly LocalDate(DateTime utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone));

    /// <summary>The UTC instant at which local day <paramref name="d"/> begins.</summary>
    public DateTime StartOfDayUtc(DateOnly d)
    {
        var local = d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        if (Zone.IsInvalidTime(local)) local = local.AddHours(1);   // midnight skipped by a DST change
        return TimeZoneInfo.ConvertTimeToUtc(local, Zone);
    }
}
