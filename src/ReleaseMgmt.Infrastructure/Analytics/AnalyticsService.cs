using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ReleaseMgmt.Infrastructure.Analytics;

/// <summary>The request's filter: <see cref="From"/>/<see cref="To"/> are calendar dates (inclusive), <see cref="Now"/> the as-of instant (UTC).</summary>
public sealed record AnalyticsWindow(DateOnly From, DateOnly To, DateTime Now)
{
    public string FromText => From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public string ToText => To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    /// <summary>ISO-8601 UTC, the format every timestamp column is stored in, so julianday(:now) and string comparison line up.</summary>
    public string NowText => DateTime.SpecifyKind(Now, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}

public sealed record MetricColumn(string Name, string Unit);

/// <summary>Catalog entry: id (M1..M15), the query name in analytics.sql, screen title, headline unit and per-column units.</summary>
public sealed record MetricInfo(string Id, string Name, string Title, string Unit, IReadOnlyList<MetricColumn> Columns);

/// <summary>
/// The 15 release-level metrics (PROJECT_SCOPE section 8), each running its statement from db/analytics.sql with bound parameters
/// (:from :to :now; nothing is ever concatenated into the SQL). Rounding is done by SQLite's round() inside the statement, as the oracle does;
/// the client only passes values through. Reads through <see cref="IAnalyticsConnectionFactory"/>, which is read-only.
/// </summary>
public sealed class AnalyticsService(IAnalyticsConnectionFactory connections)
{
    private static MetricColumn C(string n, string u) => new(n, u);
    private const string None = "";

    public static readonly IReadOnlyList<MetricInfo> Catalog =
    [
        new("M1", "on_time_rate", "On-time release rate", "%", [C("completed", "trains"), C("on_time", "trains"), C("on_time_pct", "%")]),
        new("M2", "slip_days", "Slip per train", "days", [C("Title", None), C("PlannedReleaseDate", "date"), C("actual", "date"), C("slip_days", "days")]),
        new("M3", "gate_cycle_time", "Gate cycle time", "hours", [C("GateName", None), C("samples", "gates"), C("median_h", "hours"), C("p90_h", "hours")]),
        new("M4", "gate_first_pass", "Gate first-pass rate", "%", [C("GateName", None), C("gates", "gates"), C("first_pass_pct", "%"), C("waived", "gates")]),
        new("M5", "gate_late", "Late certification", "days", [C("GateName", None), C("gates", "gates"), C("late", "gates"), C("avg_days_late", "days")]),
        new("M6", "scope_churn", "Scope churn", "products", [C("Title", None), C("at_freeze", "products"), C("added", "products"), C("removed", "products")]),
        new("M7", "runbook_variance", "Runbook step variance", "minutes", [C("Title", None), C("StepCode", None), C("Section", None), C("start_late_min", "minutes"), C("overrun_min", "minutes")]),
        new("M8", "runbook_summary", "Runbook variance per train", "minutes", [C("Title", None), C("steps", "steps"), C("total_overrun_min", "minutes"), C("worst_step", None), C("worst_overrun_min", "minutes")]),
        new("M9", "blocker_aging", "Blocker aging", "blockers", [C("Severity", None), C("lt_1d", "blockers"), C("d1_3", "blockers"), C("d3_7", "blockers"), C("ge_7d", "blockers"), C("oldest_days", "days")]),
        new("M10", "outcomes", "Rollback rate and close codes", "%", [C("completed", "trains"), C("successful", "trains"), C("with_issues", "trains"), C("unsuccessful", "trains"), C("rollback_pct", "%"), C("change_fail_pct", "%")]),
        new("M11", "freeze_exceptions", "Freeze exceptions", "overrides", [C("Name", None), C("overrides", "overrides"), C("avg_ttl_h", "hours")]),
        new("M12", "comms_timeliness", "Communication timeliness", "%", [C("scheduled", "comms"), C("on_time", "comms"), C("missed", "comms"), C("on_time_pct", "%")]),
        new("M13", "waiver_rate", "Waiver rate", "%", [C("month", "month"), C("compliance_gates", "gates"), C("waived", "gates"), C("waived_pct", "%")]),
        new("M14", "sync_health", "Sync health", "links", [C("SourceSystem", None), C("links", "links"), C("in_sync", "links"), C("mismatch", "links"), C("broken", "links"), C("stalest_min", "minutes"), C("open_alerts", "alerts")]),
        new("M15", "throughput", "Throughput and lead time", "trains", [C("month", "month"), C("completed", "trains"), C("median_lead_days", "days")]),
    ];

    /// <summary>Finds a metric by id ("M7", "m7") or query name ("runbook_variance").</summary>
    public static MetricInfo? Find(string? idOrName) =>
        Catalog.FirstOrDefault(m => string.Equals(m.Id, idOrName, StringComparison.OrdinalIgnoreCase) || string.Equals(m.Name, idOrName, StringComparison.OrdinalIgnoreCase));

    // ---- typed readers -----------------------------------------------------------------------------------------------------------------
    private static string S(DbDataReader r, int i) => r.GetString(i);
    private static string? Sn(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static long L(DbDataReader r, int i) => r.GetInt64(i);
    private static long? Ln(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
    private static double? Dn(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);

    public Task<IReadOnlyList<M1Row>> M1Async(AnalyticsWindow w, CancellationToken ct = default) => Run("on_time_rate", w, r => new M1Row(L(r, 0), Ln(r, 1), Dn(r, 2)), ct);
    public Task<IReadOnlyList<M2Row>> M2Async(AnalyticsWindow w, CancellationToken ct = default) => Run("slip_days", w, r => new M2Row(S(r, 0), S(r, 1), S(r, 2), L(r, 3)), ct);
    public Task<IReadOnlyList<M3Row>> M3Async(AnalyticsWindow w, CancellationToken ct = default) => Run("gate_cycle_time", w, r => new M3Row(S(r, 0), L(r, 1), Dn(r, 2), Dn(r, 3)), ct);
    public Task<IReadOnlyList<M4Row>> M4Async(AnalyticsWindow w, CancellationToken ct = default) => Run("gate_first_pass", w, r => new M4Row(S(r, 0), L(r, 1), Dn(r, 2), Ln(r, 3)), ct);
    public Task<IReadOnlyList<M5Row>> M5Async(AnalyticsWindow w, CancellationToken ct = default) => Run("gate_late", w, r => new M5Row(S(r, 0), L(r, 1), Ln(r, 2), Dn(r, 3)), ct);
    public Task<IReadOnlyList<M6Row>> M6Async(AnalyticsWindow w, CancellationToken ct = default) => Run("scope_churn", w, r => new M6Row(S(r, 0), L(r, 1), L(r, 2), L(r, 3)), ct);
    public Task<IReadOnlyList<M7Row>> M7Async(AnalyticsWindow w, CancellationToken ct = default) => Run("runbook_variance", w, r => new M7Row(S(r, 0), S(r, 1), S(r, 2), Dn(r, 3), Dn(r, 4)), ct);
    public Task<IReadOnlyList<M8Row>> M8Async(AnalyticsWindow w, CancellationToken ct = default) => Run("runbook_summary", w, r => new M8Row(S(r, 0), L(r, 1), Dn(r, 2), Sn(r, 3), Dn(r, 4)), ct);
    public Task<IReadOnlyList<M9Row>> M9Async(AnalyticsWindow w, CancellationToken ct = default) => Run("blocker_aging", w, r => new M9Row(S(r, 0), Ln(r, 1), Ln(r, 2), Ln(r, 3), Ln(r, 4), Dn(r, 5)), ct);
    public Task<IReadOnlyList<M10Row>> M10Async(AnalyticsWindow w, CancellationToken ct = default) => Run("outcomes", w, r => new M10Row(L(r, 0), Ln(r, 1), Ln(r, 2), Ln(r, 3), Dn(r, 4), Dn(r, 5)), ct);
    public Task<IReadOnlyList<M11Row>> M11Async(AnalyticsWindow w, CancellationToken ct = default) => Run("freeze_exceptions", w, r => new M11Row(S(r, 0), L(r, 1), Dn(r, 2)), ct);
    public Task<IReadOnlyList<M12Row>> M12Async(AnalyticsWindow w, CancellationToken ct = default) => Run("comms_timeliness", w, r => new M12Row(L(r, 0), Ln(r, 1), Ln(r, 2), Dn(r, 3)), ct);
    public Task<IReadOnlyList<M13Row>> M13Async(AnalyticsWindow w, CancellationToken ct = default) => Run("waiver_rate", w, r => new M13Row(Sn(r, 0), L(r, 1), Ln(r, 2), Dn(r, 3)), ct);
    public Task<IReadOnlyList<M14Row>> M14Async(AnalyticsWindow w, CancellationToken ct = default) => Run("sync_health", w, r => new M14Row(S(r, 0), L(r, 1), Ln(r, 2), Ln(r, 3), Ln(r, 4), Dn(r, 5), L(r, 6)), ct);
    public Task<IReadOnlyList<M15Row>> M15Async(AnalyticsWindow w, CancellationToken ct = default) => Run("throughput", w, r => new M15Row(Sn(r, 0), L(r, 1), Dn(r, 2)), ct);

    /// <summary>Runs a metric by catalog id and returns its typed rows as objects (for the generic endpoint).</summary>
    public async Task<IReadOnlyList<object>> RunAsync(string id, AnalyticsWindow w, CancellationToken ct = default)
    {
        static IReadOnlyList<object> Box<T>(IReadOnlyList<T> rows) where T : class => rows.Cast<object>().ToList();
        return id.ToUpperInvariant() switch
        {
            "M1" => Box(await M1Async(w, ct)), "M2" => Box(await M2Async(w, ct)), "M3" => Box(await M3Async(w, ct)), "M4" => Box(await M4Async(w, ct)),
            "M5" => Box(await M5Async(w, ct)), "M6" => Box(await M6Async(w, ct)), "M7" => Box(await M7Async(w, ct)), "M8" => Box(await M8Async(w, ct)),
            "M9" => Box(await M9Async(w, ct)), "M10" => Box(await M10Async(w, ct)), "M11" => Box(await M11Async(w, ct)), "M12" => Box(await M12Async(w, ct)),
            "M13" => Box(await M13Async(w, ct)), "M14" => Box(await M14Async(w, ct)), "M15" => Box(await M15Async(w, ct)),
            _ => throw new ArgumentException($"Unknown metric {id}", nameof(id)),
        };
    }

    private async Task<IReadOnlyList<T>> Run<T>(string name, AnalyticsWindow w, Func<DbDataReader, T> map, CancellationToken ct)
    {
        var sql = AnalyticsSql.Get(name);
        await using var conn = await connections.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        // Bind only what the statement names, as the oracle does; values are bound, never spliced into the text.
        Bind(cmd, sql, ":from", w.FromText);
        Bind(cmd, sql, ":to", w.ToText);
        Bind(cmd, sql, ":now", w.NowText);
        var rows = new List<T>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) rows.Add(map(r));
        return rows;
    }

    private static void Bind(SqliteCommand cmd, string sql, string name, string value)
    {
        if (sql.Contains(name, StringComparison.Ordinal)) cmd.Parameters.AddWithValue(name, value);
    }
}
