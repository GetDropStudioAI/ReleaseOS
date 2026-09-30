using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Analytics;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Infrastructure.Exports;

/// <summary>
/// Writes the metric values an evidence pack prints to MetricSnapshots (D16, PROJECT_SCOPE 8) so the pack can be reproduced and audited later.
/// The values come from <see cref="AnalyticsService"/> (the same db/analytics.sql statements as the Analytics screen) for the 180 days up to the export's as-of time.
/// The pack prints the rows read BACK from the table, never a second query, so what is printed is what was stored.
/// Release-wide metrics M1, M3-M5, M9-M15 are captured whole; M2, M6 and M8 are per train and only this train's rows are kept (matched by title, Q-050e).
/// Key format: <c>M5.late[Code Freeze]</c> (metric, column, and the row's label when it has one).
/// </summary>
public sealed class MetricSnapshotService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, AnalyticsService analytics) : ServiceBase(dbf, time)
{
    public const int WindowDays = 180;
    private static readonly string[] ReleaseWide = ["M1", "M3", "M4", "M5", "M9", "M10", "M11", "M12", "M13", "M14", "M15"];
    private static readonly string[] PerTrain = ["M2", "M6", "M8"];

    /// <summary>Computes and stores the snapshot for a job (replacing any rows a previous attempt of the same job wrote) and returns the stored rows.</summary>
    public async Task<IReadOnlyList<MetricRow>> CaptureAsync(string jobId, string trainId, string trainTitle, DateTime asOf, CancellationToken ct = default)
    {
        var to = DateOnly.FromDateTime(asOf);
        var w = new AnalyticsWindow(to.AddDays(-WindowDays), to, asOf);
        var values = new List<(string Key, double Value)>();
        foreach (var id in ReleaseWide.Concat(PerTrain))
        {
            var info = AnalyticsService.Find(id)!;
            foreach (var row in await analytics.RunAsync(id, w, ct))
            {
                var props = row.GetType().GetProperties();
                var label = props.Select(p => p.GetValue(row)).OfType<string>().FirstOrDefault() ?? "";
                if (PerTrain.Contains(id) && label != trainTitle) continue;
                for (var i = 0; i < props.Length && i < info.Columns.Count; i++)
                {
                    double? v = props[i].GetValue(row) switch { long l => l, double d => d, _ => null };
                    if (v is not double x || double.IsNaN(x) || double.IsInfinity(x)) continue;
                    values.Add(($"{id}.{info.Columns[i].Name}" + (label.Length > 0 ? $"[{label}]" : ""), x));
                }
            }
        }

        var r = await RunAsync<bool>(async db =>
        {
            var old = await db.Set<MetricSnapshots>().Where(m => m.ExportJobId == jobId).ToListAsync(ct);
            db.Set<MetricSnapshots>().RemoveRange(old);   // a retried job replaces its own rows; other jobs' snapshots are never touched
            var now = Now;
            foreach (var (key, value) in values.DistinctBy(v => v.Key))
                db.Set<MetricSnapshots>().Add(new MetricSnapshots { ExportJobId = jobId, ReleaseTrainId = trainId, MetricKey = key, Value = value, PeriodStart = w.From, PeriodEnd = w.To, CapturedAt = now });
            Audit(db, null, trainId, "MetricSnapshot", jobId, "Capture", null, new { rows = values.Count, from = w.FromText, to = w.ToText });
            await db.SaveChangesAsync(ct);
            return ServiceResult<bool>.Ok(true);
        }, ct);
        if (!r.IsOk) throw new InvalidOperationException("Could not store the metric snapshot: " + (r.Failures.Count > 0 ? r.Failures[0].Message : r.Missing));
        return await ReadAsync(jobId, ct);
    }

    public async Task<IReadOnlyList<MetricRow>> ReadAsync(string jobId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var rows = await db.Set<MetricSnapshots>().AsNoTracking().Where(m => m.ExportJobId == jobId).OrderBy(m => m.Id).ToListAsync(ct);
        return rows.Select(m => ToRow(m)).OrderBy(m => MetricOrder(m.Key)).ThenBy(m => m.Label, StringComparer.Ordinal).ThenBy(m => m.Key, StringComparer.Ordinal).ToList();
    }

    private static int MetricOrder(string key) => int.TryParse(key.Split('.')[0].TrimStart('M'), out var n) ? n : 99;

    public static MetricRow ToRow(MetricSnapshots m)
    {
        var dot = m.MetricKey.IndexOf('.');
        var id = dot > 0 ? m.MetricKey[..dot] : m.MetricKey;
        var rest = dot > 0 ? m.MetricKey[(dot + 1)..] : "";
        var br = rest.IndexOf('[');
        var col = br >= 0 ? rest[..br] : rest;
        var label = br >= 0 && rest.EndsWith(']') ? rest[(br + 1)..^1] : "";
        var info = AnalyticsService.Find(id);
        var unit = info?.Columns.FirstOrDefault(c => c.Name == col)?.Unit ?? "";
        return new MetricRow(m.MetricKey, info?.Title ?? id, label.Length > 0 ? $"{label} · {col}" : col, m.Value, unit, m.PeriodStart, m.PeriodEnd, m.CapturedAt);
    }
}
