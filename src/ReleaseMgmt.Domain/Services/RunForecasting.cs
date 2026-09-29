namespace ReleaseMgmt.Domain.Services;

/// <summary>One runbook step as the forecast sees it: its (effective) plan, its dependencies and what has actually happened in this run.</summary>
public sealed record ForecastStep(string Code, string Section, DateTime PlannedStart, int PlannedDurationMin, IReadOnlyCollection<string> DependsOn,
                                  string Status, DateTime? ActualStart, DateTime? ActualEnd);

public sealed record ForecastRow(string Step, string State, DateTime? Start, DateTime? End, int? EndVarianceMin);

public sealed record RunForecastResult(
    DateTime? ForecastFinish, DateTime PlannedFinish, int RollbackPlannedMin, DateTime? RollbackDeadline, int? CrossesDeadlineByMin, bool AlertRaised,
    int? WindowClosesInSec, IReadOnlyList<string> BlockedByFailed, IReadOnlyList<ForecastRow> Steps);

/// <summary>
/// Server-side forecast (PROJECT_SCOPE section 3). For every step outside the Rollback section:
/// Done = actuals; Running = actual start + planned duration, or now once that has passed; Scheduled = starts at the latest of its planned start,
/// the forecast end of every dependency, and now (a step that has not started cannot start in the past), and lasts its planned duration.
/// A Skipped step is transparent (it ends when its dependencies end). A Failed step stops the forecast: everything depending on it, and the
/// finish itself, are unknown and reported as blocked. Rollback deadline = window end minus the total planned duration of the Rollback steps;
/// the alert is raised when the forecast finish is after it.
/// </summary>
public static class RunForecasting
{
    public static RunForecastResult Compute(DateTime now, DateTime? windowEnd, IReadOnlyList<ForecastStep> steps)
    {
        var byCode = steps.ToDictionary(s => s.Code);
        var ends = new Dictionary<string, DateTime?>();
        var starts = new Dictionary<string, DateTime?>();
        var visiting = new HashSet<string>();

        DateTime? EndOf(string code)
        {
            if (ends.TryGetValue(code, out var known)) return known;
            if (!byCode.TryGetValue(code, out var s)) return null;
            if (!visiting.Add(code)) return null;   // a cycle cannot exist (the runbook rejects them); do not loop if data says otherwise
            DateTime? start = null, end = null; var blocked = false;
            var depEnds = new List<DateTime>();
            foreach (var d in s.DependsOn)
            {
                if (!byCode.TryGetValue(d, out var dep)) continue;
                var e = EndOf(d);
                if (e is null) blocked = true; else depEnds.Add(e.Value);
            }
            var depMax = depEnds.Count == 0 ? (DateTime?)null : depEnds.Max();
            switch (s.Status)
            {
                case "Done":
                    start = s.ActualStart; end = s.ActualEnd ?? s.ActualStart; break;
                case "Failed":
                    start = s.ActualStart; end = null; break;   // blocked: nothing after it can be forecast
                case "Skipped":
                    end = blocked ? null : depMax ?? s.PlannedStart; start = end; break;
                case "Running":
                    start = s.ActualStart ?? now;
                    var byPlan = start.Value.AddMinutes(s.PlannedDurationMin);
                    end = byPlan > now ? byPlan : now; break;   // overdue: it cannot end before now
                default:
                    if (blocked) { end = null; break; }
                    start = Max(s.PlannedStart, depMax, now);
                    end = start.Value.AddMinutes(s.PlannedDurationMin); break;
            }
            visiting.Remove(code);
            starts[code] = start; ends[code] = end;
            return end;
        }

        var main = steps.Where(s => s.Section != "Rollback").OrderBy(s => s.PlannedStart).ThenBy(s => s.Code, StringComparer.Ordinal).ToList();
        foreach (var s in steps) EndOf(s.Code);

        var rows = main.Select(s => new ForecastRow(s.Code, s.Status, starts.GetValueOrDefault(s.Code), ends.GetValueOrDefault(s.Code),
            ends.GetValueOrDefault(s.Code) is { } e ? (int)Math.Round((e - s.PlannedStart.AddMinutes(s.PlannedDurationMin)).TotalMinutes, MidpointRounding.AwayFromZero) : null)).ToList();
        var blockedBy = main.Where(s => s.Status == "Failed").Select(s => s.Code).Order(StringComparer.Ordinal).ToList();
        var anyUnknown = rows.Any(r => r.End is null);
        DateTime? finish = anyUnknown || main.Count == 0 ? null : rows.Max(r => r.End);
        var plannedFinish = main.Count == 0 ? now : main.Max(s => s.PlannedStart.AddMinutes(s.PlannedDurationMin));
        var rollbackMin = steps.Where(s => s.Section == "Rollback").Sum(s => s.PlannedDurationMin);
        DateTime? deadline = windowEnd?.AddMinutes(-rollbackMin);
        int? crosses = finish is not null && deadline is not null && finish > deadline ? (int)Math.Ceiling((finish.Value - deadline.Value).TotalMinutes) : null;
        return new RunForecastResult(finish, plannedFinish, rollbackMin, deadline, crosses, crosses is not null,
            windowEnd is null ? null : (int)Math.Floor((windowEnd.Value - now).TotalSeconds), blockedBy, rows);
    }

    private static DateTime Max(DateTime a, DateTime? b, DateTime c) { var m = a > c ? a : c; return b is { } x && x > m ? x : m; }
}
