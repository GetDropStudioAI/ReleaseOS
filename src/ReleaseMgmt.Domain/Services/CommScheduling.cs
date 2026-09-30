namespace ReleaseMgmt.Domain.Services;

/// <summary>
/// The T-minus plan (PROJECT_SCOPE 5.2): a template lists (library message, OffsetDays) pairs, negative before the target date and positive after.
/// This turns an offset into the instant the message is due, and a due time back into a "T-n" label. Pure, so it is unit-tested with a fixed calendar.
/// </summary>
public static class CommScheduling
{
    /// <summary>Library TemplateType that is due when the deployment window opens (minus a lead).</summary>
    public const string CutoverType = "Cutover";
    /// <summary>Library TemplateType that is due when the deployment window closes.</summary>
    public const string CompleteType = "Complete";

    public sealed record Options(TimeZoneInfo Zone, TimeOnly SendTime, int CutoverLeadMinutes)
    {
        public static Options Default(TimeZoneInfo zone) => new(zone, new TimeOnly(9, 0), 15);
    }

    /// <summary>
    /// Business-day offset from the target date (D9, same calendar as gates). Offset 0 is the target day itself. Time of day, all in the display zone:
    /// a "Cutover" message on the target day is due <see cref="Options.CutoverLeadMinutes"/> before the window opens; a "Complete" message when it closes;
    /// with no window both fall back to the send time; every other message is due at the send time (default 09:00).
    /// </summary>
    public static DateTime DueAtUtc(DateOnly target, int offsetDays, string templateType, DateTime? windowStartUtc, DateTime? windowEndUtc, IReadOnlySet<DateOnly> holidays, Options o)
    {
        if (offsetDays == 0)
        {
            if (templateType == CutoverType && windowStartUtc is { } ws) return Utc(ws.AddMinutes(-o.CutoverLeadMinutes));
            if (templateType == CompleteType && windowEndUtc is { } we) return Utc(we);
        }
        var day = offsetDays < 0 ? BusinessDays.SubtractBusinessDays(target, -offsetDays, holidays)
            : offsetDays == 0 ? target
            : BusinessDays.AddBusinessDays(target, offsetDays, holidays);
        return LocalToUtc(day, o.SendTime, o.Zone);
    }

    private static DateTime Utc(DateTime t) => DateTime.SpecifyKind(t, DateTimeKind.Utc);

    /// <summary>Wall-clock time in <paramref name="zone"/> to UTC; a time skipped by a DST change moves forward an hour.</summary>
    public static DateTime LocalToUtc(DateOnly day, TimeOnly time, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(time, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local)) local = local.AddHours(1);
        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(local, zone), DateTimeKind.Utc);
    }

    /// <summary>Business days before the target (positive), 0 on the target day, negative after it: what the schedule calls "T-n" / "T0" / "T+n".</summary>
    public static int TMinus(DateTime dueAtUtc, DateOnly target, IReadOnlySet<DateOnly> holidays, TimeZoneInfo zone) =>
        BusinessDays.Between(DateOnly.FromDateTime(CommFormat.ToLocal(dueAtUtc, zone)), target, holidays);

    public static string Label(int tMinus) => tMinus > 0 ? $"T−{tMinus}" : tMinus == 0 ? "T0" : $"T+{-tMinus}";

    public const string Sent = "Sent", SentLate = "SentLate", Overdue = "Overdue", Ready = "Ready", Scheduled = "Scheduled";

    /// <summary>Sent (on time), SentLate (SentAt after DueAt), Overdue (unsent and past due), Ready (unsent and due within 24 hours), Scheduled.</summary>
    public static string State(DateTime dueAt, DateTime? sentAt, DateTime now) =>
        sentAt is { } s ? (s > dueAt ? SentLate : Sent)
        : dueAt < now ? Overdue
        : dueAt - now <= TimeSpan.FromHours(24) ? Ready
        : Scheduled;

    /// <summary>Whole minutes a sent message was late (0 when on time or unsent). The timeliness KPI counts SentAt &lt;= DueAt; this is for display.</summary>
    public static int LateMinutes(DateTime dueAt, DateTime? sentAt) =>
        sentAt is { } s && s > dueAt ? (int)Math.Ceiling((s - dueAt).TotalMinutes) : 0;
}
