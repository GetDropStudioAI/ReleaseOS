using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace ReleaseMgmt.LoadTests;

public enum Outcome { Ok, ExpectedConflict, Error }

/// <summary>Every measured request, kept individually so percentiles are exact (nearest-rank), not histogram estimates.</summary>
public sealed class Recorder
{
    public sealed record Sample(string Name, bool Write, double Ms, int Status, Outcome Outcome, string? Detail);
    private readonly ConcurrentQueue<Sample> _samples = new();
    public volatile bool Measuring = true;
    public long BusyHits;   // response bodies that mention a SQLite lock/busy error

    public void Add(Sample s) { if (Measuring) _samples.Enqueue(s); }
    public IReadOnlyList<Sample> All => [.. _samples];
}

public sealed record Row(string Name, bool Write, int Count, double P50, double P95, double P99, double Max, double Mean, int Errors, int Expected409);

public static class Stats
{
    public static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return double.NaN;
        var rank = (int)Math.Ceiling(p / 100.0 * sorted.Count);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }

    private static Row Summarise(string name, bool write, IReadOnlyCollection<Recorder.Sample> s)
    {
        var lat = s.Select(x => x.Ms).OrderBy(x => x).ToList();
        return new Row(name, write, s.Count, Percentile(lat, 50), Percentile(lat, 95), Percentile(lat, 99), lat.Count == 0 ? double.NaN : lat[^1], lat.Count == 0 ? double.NaN : lat.Average(),
            s.Count(x => x.Outcome == Outcome.Error), s.Count(x => x.Outcome == Outcome.ExpectedConflict));
    }

    public static (List<Row> PerRequest, Row Overall, Row Reads, Row Writes) Compute(IReadOnlyList<Recorder.Sample> all)
    {
        var rows = all.GroupBy(s => s.Name).Select(g => Summarise(g.Key, g.First().Write, g.ToList())).OrderBy(r => r.Write).ThenBy(r => r.Name, StringComparer.Ordinal).ToList();
        return (rows, Summarise("ALL REQUESTS", false, all), Summarise("ALL READS", false, all.Where(x => !x.Write).ToList()), Summarise("ALL WRITES", true, all.Where(x => x.Write).ToList()));
    }

    private static string F(double v) => double.IsNaN(v) ? "n/a" : v.ToString("0.0", CultureInfo.InvariantCulture);

    public static string Table(IEnumerable<Row> rows, double budgetMs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| Request | Kind | Count | p50 ms | p95 ms | p99 ms | max ms | mean ms | Errors | Expected 409 | p95 < budget |");
        sb.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---|");
        foreach (var r in rows)
            sb.AppendLine($"| {r.Name} | {(r.Name == "ALL REQUESTS" ? "mixed" : r.Write ? "write" : "read")} | {r.Count} | {F(r.P50)} | {F(r.P95)} | {F(r.P99)} | {F(r.Max)} | {F(r.Mean)} | {r.Errors} | {r.Expected409} | {(r.P95 < budgetMs ? "yes" : "**NO**")} |");
        return sb.ToString();
    }
}
