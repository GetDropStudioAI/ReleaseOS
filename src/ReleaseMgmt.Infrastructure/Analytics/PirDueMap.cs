using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Reminders;

namespace ReleaseMgmt.Infrastructure.Analytics;

/// <summary>
/// REOS-121: the due dates M18 and M24 need, worked out the way <c>PirDue</c> does for the reminders and My work: a PIR that a rollback or an abort required is due
/// <c>Pir:DueBusinessDays</c> business days after the local date of its first audit row, in <c>Display:TimeZone</c>, skipping the weekends and the holidays of the release's
/// workspace. A business-day count cannot be written in SQL, so the statements receive the answer as the JSON object <c>:pir_due</c> ({PIR id: yyyy-MM-dd}).
/// PIRs required by a close code or by hand have no due date and are not in it (Q-0870).
/// </summary>
public sealed class PirDueMap(DisplayClock clock, int businessDays)
{
    public async Task<string> JsonAsync(SqliteConnection conn, IReadOnlyList<string> workspaces, CancellationToken ct = default)
    {
        var scope = JsonSerializer.Serialize(workspaces);
        var holidays = new Dictionary<string, HashSet<DateOnly>>(StringComparer.Ordinal);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT WorkspaceId, Day FROM Holidays WHERE WorkspaceId IN (SELECT value FROM json_each($ws))";
            cmd.Parameters.AddWithValue("$ws", scope);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (!holidays.TryGetValue(r.GetString(0), out var set)) holidays[r.GetString(0)] = set = [];
                set.Add(DateOnly.ParseExact(r.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture));
            }
        }
        var due = new SortedDictionary<string, string>(StringComparer.Ordinal);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT p.Id, t.WorkspaceId, (SELECT min(a.OccurredAt) FROM AuditEvents a WHERE a.EntityType = 'PostImplementationReview' AND a.EntityId = p.Id)
                FROM PostImplementationReviews p JOIN ReleaseTrains t ON t.Id = p.ReleaseTrainId
                WHERE p.RequiredReason IN ('RollbackPerformed', 'Aborted') AND t.WorkspaceId IN (SELECT value FROM json_each($ws))
                """;
            cmd.Parameters.AddWithValue("$ws", scope);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (r.IsDBNull(2)) continue;   // a PIR with no audit row has no creation time to count from
                var created = DateTime.Parse(r.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                var days = holidays.GetValueOrDefault(r.GetString(1)) ?? [];
                due[r.GetString(0)] = BusinessDays.AddBusinessDays(clock.LocalDate(created), businessDays, days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
        }
        return JsonSerializer.Serialize(due);
    }
}
