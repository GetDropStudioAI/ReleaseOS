using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using ReleaseMgmt.Domain.Common;

namespace ReleaseMgmt.Infrastructure.Analytics;

/// <summary>The request's filter: <see cref="From"/>/<see cref="To"/> are calendar dates (inclusive), <see cref="Now"/> the as-of instant (UTC).</summary>
public sealed record AnalyticsWindow(DateOnly From, DateOnly To, DateTime Now)
{
    /// <summary>The workspace whose records the metrics read (:ws, REOS-105): the current one, or the default workspace outside any.</summary>
    public string WorkspaceId { get; init; } = WorkspaceScope.Current ?? WorkspaceIds.Default;
    /// <summary>REOS-121 (D49): the workspaces the metrics read, when more than the current one. The caller (the endpoint) has already limited this to the person's own memberships; empty means <see cref="WorkspaceId"/> alone.</summary>
    public IReadOnlyList<string> Workspaces { get; init; } = [];
    /// <summary>The workspaces read: <see cref="Workspaces"/>, or the one <see cref="WorkspaceId"/>.</summary>
    public IReadOnlyList<string> Scope => Workspaces.Count > 0 ? Workspaces : [WorkspaceId];
    /// <summary>REOS-121: how M16..M24 group their rows, a subset of <see cref="AnalyticsSql.Dimensions"/>; empty = one row per measure (the portfolio total).</summary>
    public IReadOnlyList<string> GroupBy { get; init; } = [];
    /// <summary>REOS-121 (Q-121a): the month a fiscal year starts, 1 to 12. 1 = calendar quarters (2026-Q3); anything else names quarters FY2027-Q1.</summary>
    public int FiscalYearStartMonth { get; init; } = 1;
    /// <summary>REOS-121: today in the display zone, for "overdue" (:localday); the UTC date of <see cref="Now"/> unless the caller says otherwise.</summary>
    public DateOnly? LocalDay { get; init; }
    /// <summary>REOS-121: PIR due dates by PIR id as a JSON object (:pir_due). Null = the service works them out from <see cref="PirDueMap"/> (business days in the display zone, as the reminders do); a test may give them.</summary>
    public string? PirDueJson { get; init; }
    public string ScopeJson => System.Text.Json.JsonSerializer.Serialize(Scope);
    public string ByText => GroupBy.Count == 0 ? "," : "," + string.Join(',', GroupBy) + ",";
    public string LocalDayText => (LocalDay ?? DateOnly.FromDateTime(Now)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    /// <summary>REOS-116 (:type): the release type the metrics are limited to, or null for every type.</summary>
    public string? ReleaseTypeId { get; init; }
    public string FromText => From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public string ToText => To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    /// <summary>ISO-8601 UTC, the format every timestamp column is stored in, so julianday(:now) and string comparison line up.</summary>
    public string NowText => DateTime.SpecifyKind(Now, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}

public sealed record MetricColumn(string Name, string Unit);

/// <summary>Catalog entry: id (M1..M24), the query name in analytics.sql, screen title, headline unit and per-column units.</summary>
/// <param name="Grouped">REOS-121: M16..M24 start with the six group columns and take <c>groupBy</c>.</param>
/// <param name="Snapshot">The metric is as of now and ignores the period (M6, M9, M14, M21).</param>
public sealed record MetricInfo(string Id, string Name, string Title, string Unit, IReadOnlyList<MetricColumn> Columns, bool Grouped = false, bool Snapshot = false);

/// <summary>
/// The 24 release-level metrics (PROJECT_SCOPE section 8; M16..M24 are the executive set of REOS-121), each running its statement from db/analytics.sql with bound parameters
/// (:from :to :now :ws :type, and for M16..M24 :by :fy :localday :pir_due; nothing is ever concatenated into the SQL). Rounding is done by SQLite's round() inside the statement, as the oracle does;
/// the client only passes values through. Reads through <see cref="IAnalyticsConnectionFactory"/>, which is read-only.
/// </summary>
public sealed class AnalyticsService(IAnalyticsConnectionFactory connections, PirDueMap? pirDue = null)
{
    private static MetricColumn C(string n, string u) => new(n, u);
    private const string None = "";
    /// <summary>The six group columns every M16..M24 row starts with ('' when the request does not group by it).</summary>
    private static readonly MetricColumn[] Keys = [C("workspace", None), C("team", None), C("release_type", None), C("version", None), C("segment", None), C("quarter", None)];

    public static readonly IReadOnlyList<MetricInfo> Catalog =
    [
        new("M1", "on_time_rate", "On-time release rate", "%", [C("completed", "trains"), C("on_time", "trains"), C("on_time_pct", "%")]),
        new("M2", "slip_days", "Slip per train", "days", [C("Title", None), C("PlannedReleaseDate", "date"), C("actual", "date"), C("slip_days", "days")]),
        new("M3", "gate_cycle_time", "Gate cycle time", "hours", [C("GateName", None), C("samples", "gates"), C("median_h", "hours"), C("p90_h", "hours")]),
        new("M4", "gate_first_pass", "Gate first-pass rate", "%", [C("GateName", None), C("gates", "gates"), C("first_pass_pct", "%"), C("waived", "gates")]),
        new("M5", "gate_late", "Late certification", "days", [C("GateName", None), C("gates", "gates"), C("late", "gates"), C("avg_days_late", "days")]),
        new("M6", "scope_churn", "Scope churn", "products", [C("Title", None), C("at_freeze", "products"), C("added", "products"), C("removed", "products")], Snapshot: true),
        new("M7", "runbook_variance", "Runbook step variance", "minutes", [C("Title", None), C("StepCode", None), C("Section", None), C("start_late_min", "minutes"), C("overrun_min", "minutes")]),
        new("M8", "runbook_summary", "Runbook variance per train", "minutes", [C("Title", None), C("steps", "steps"), C("total_overrun_min", "minutes"), C("worst_step", None), C("worst_overrun_min", "minutes")]),
        new("M9", "blocker_aging", "Blocker aging", "blockers", [C("Severity", None), C("lt_1d", "blockers"), C("d1_3", "blockers"), C("d3_7", "blockers"), C("ge_7d", "blockers"), C("oldest_days", "days")], Snapshot: true),
        new("M10", "outcomes", "Rollback rate and close codes", "%", [C("completed", "trains"), C("successful", "trains"), C("with_issues", "trains"), C("unsuccessful", "trains"), C("rollback_pct", "%"), C("change_fail_pct", "%")]),
        new("M11", "freeze_exceptions", "Freeze exceptions", "overrides", [C("Name", None), C("overrides", "overrides"), C("avg_ttl_h", "hours")]),
        new("M12", "comms_timeliness", "Communication timeliness", "%", [C("scheduled", "comms"), C("on_time", "comms"), C("missed", "comms"), C("on_time_pct", "%")]),
        new("M13", "waiver_rate", "Waiver rate", "%", [C("month", "month"), C("compliance_gates", "gates"), C("waived", "gates"), C("waived_pct", "%")]),
        new("M14", "sync_health", "Sync health", "links", [C("SourceSystem", None), C("links", "links"), C("in_sync", "links"), C("mismatch", "links"), C("broken", "links"), C("stalest_min", "minutes"), C("open_alerts", "alerts")], Snapshot: true),
        new("M15", "throughput", "Throughput and lead time", "trains", [C("month", "month"), C("completed", "trains"), C("median_lead_days", "days")]),
        new("M16", "time_to_restore", "Time to restore", "minutes", [.. Keys, C("restores", "runs"), C("median_min", "minutes"), C("max_min", "minutes")], Grouped: true),
        new("M17", "predictability", "Predictability and change failure", "%", [.. Keys, C("track", None), C("completed", "releases"), C("measured", "releases"), C("on_time", "releases"), C("on_time_pct", "%"),
            C("median_slip_days", "days"), C("max_slip_days", "days"), C("change_failed", "releases"), C("change_fail_pct", "%")], Grouped: true),
        new("M18", "exceptions_summary", "Risk and compliance exceptions", "events", [.. Keys, C("exception", None), C("events", "events"), C("releases", "releases")], Grouped: true),
        new("M19", "approval_latency", "Approval latency", "hours", [.. Keys, C("step", None), C("samples", "approvals"), C("median_h", "hours"), C("p90_h", "hours")], Grouped: true),
        new("M20", "slip_reasons", "Slip reasons", "days", [.. Keys, C("reason", None), C("moves", "moves"), C("releases", "releases"), C("days_later", "days"), C("days_earlier", "days")], Grouped: true),
        new("M21", "risk_exposure", "Risk exposure of open releases", "releases", [.. Keys, C("risk_tier", None), C("open_releases", "releases"), C("nearest_days", "days"), C("past_target", "releases"), C("with_blockers", "releases"),
            C("open_blockers", "blockers"), C("no_rehearsal", "releases")], Grouped: true, Snapshot: true),
        new("M22", "throughput_by_group", "Throughput and lead time by group", "releases", [.. Keys, C("completed", "releases"), C("median_lead_days", "days"), C("p90_lead_days", "days")], Grouped: true),
        new("M23", "deployment_success", "Deployment success and availability", "deployments", [.. Keys, C("deployments", "deployments"), C("successful", "deployments"), C("failed", "deployments"), C("rolled_back", "deployments"),
            C("success_pct", "%"), C("breaches", "breaches"), C("availability_breaches", "breaches")], Grouped: true),
        new("M24", "pir_follow_through", "Post-implementation review follow-through", "reviews", [.. Keys, C("reviews", "reviews"), C("with_due_date", "reviews"), C("held", "reviews"), C("held_by_due", "reviews"), C("overdue", "reviews"),
            C("actions", "actions"), C("actions_done", "actions"), C("actions_overdue", "actions")], Grouped: true),
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


    // REOS-121: the executive metrics. Each row starts with the six group columns (G), then its measures.
    private static (string, string, string, string, string, string) G(DbDataReader r) => (S(r, 0), S(r, 1), S(r, 2), S(r, 3), S(r, 4), S(r, 5));
    public Task<IReadOnlyList<M16Row>> M16Async(AnalyticsWindow w, CancellationToken ct = default) => Run("time_to_restore", w, r => { var g = G(r); return new M16Row(g.Item1, g.Item2, g.Item3, g.Item4, g.Item5, g.Item6, L(r, 6), Dn(r, 7), Dn(r, 8)); }, ct);
    public Task<IReadOnlyList<M17Row>> M17Async(AnalyticsWindow w, CancellationToken ct = default) => Run("predictability", w, r => { var g = G(r); return new M17Row(g.Item1, g.Item2, g.Item3, g.Item4, g.Item5, g.Item6, S(r, 6), L(r, 7), L(r, 8), Ln(r, 9), Dn(r, 10), Dn(r, 11), Ln(r, 12), Ln(r, 13), Dn(r, 14)); }, ct);
    public Task<IReadOnlyList<M18Row>> M18Async(AnalyticsWindow w, CancellationToken ct = default) => Run("exceptions_summary", w, r => { var g = G(r); return new M18Row(g.Item1, g.Item2, g.Item3, g.Item4, g.Item5, g.Item6, S(r, 6), L(r, 7), L(r, 8)); }, ct);
    public Task<IReadOnlyList<M19Row>> M19Async(AnalyticsWindow w, CancellationToken ct = default) => Run("approval_latency", w, r => { var g = G(r); return new M19Row(g.Item1, g.Item2, g.Item3, g.Item4, g.Item5, g.Item6, S(r, 6), L(r, 7), Dn(r, 8), Dn(r, 9)); }, ct);
    public Task<IReadOnlyList<M20Row>> M20Async(AnalyticsWindow w, CancellationToken ct = default) => Run("slip_reasons", w, r => { var g = G(r); return new M20Row(g.Item1, g.Item2, g.Item3, g.Item4, g.Item5, g.Item6, S(r, 6), L(r, 7), L(r, 8), Ln(r, 9), Ln(r, 10)); }, ct);
    public Task<IReadOnlyList<M21Row>> M21Async(AnalyticsWindow w, CancellationToken ct = default) => Run("risk_exposure", w, r => { var g = G(r); return new M21Row(g.Item1, g.Item2, g.Item3, g.Item4, g.Item5, g.Item6, S(r, 6), L(r, 7), Ln(r, 8), Ln(r, 9), Ln(r, 10), Ln(r, 11), Ln(r, 12)); }, ct);
    public Task<IReadOnlyList<M22Row>> M22Async(AnalyticsWindow w, CancellationToken ct = default) => Run("throughput_by_group", w, r => { var g = G(r); return new M22Row(g.Item1, g.Item2, g.Item3, g.Item4, g.Item5, g.Item6, L(r, 6), Dn(r, 7), Dn(r, 8)); }, ct);
    public Task<IReadOnlyList<M23Row>> M23Async(AnalyticsWindow w, CancellationToken ct = default) => Run("deployment_success", w, r => { var g = G(r); return new M23Row(g.Item1, g.Item2, g.Item3, g.Item4, g.Item5, g.Item6, L(r, 6), Ln(r, 7), Ln(r, 8), Ln(r, 9), Dn(r, 10), Ln(r, 11), Ln(r, 12)); }, ct);
    public Task<IReadOnlyList<M24Row>> M24Async(AnalyticsWindow w, CancellationToken ct = default) => Run("pir_follow_through", w, r => { var g = G(r); return new M24Row(g.Item1, g.Item2, g.Item3, g.Item4, g.Item5, g.Item6, L(r, 6), Ln(r, 7), Ln(r, 8), Ln(r, 9), Ln(r, 10), Ln(r, 11), Ln(r, 12), Ln(r, 13)); }, ct);

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
            "M16" => Box(await M16Async(w, ct)), "M17" => Box(await M17Async(w, ct)), "M18" => Box(await M18Async(w, ct)), "M19" => Box(await M19Async(w, ct)),
            "M20" => Box(await M20Async(w, ct)), "M21" => Box(await M21Async(w, ct)), "M22" => Box(await M22Async(w, ct)), "M23" => Box(await M23Async(w, ct)), "M24" => Box(await M24Async(w, ct)),
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
        Bind(cmd, sql, ":ws", w.ScopeJson);
        Bind(cmd, sql, ":type", w.ReleaseTypeId);
        Bind(cmd, sql, ":by", w.ByText);
        Bind(cmd, sql, ":fy", w.FiscalYearStartMonth);
        Bind(cmd, sql, ":localday", w.LocalDayText);
        if (sql.Contains(":pir_due", StringComparison.Ordinal)) Bind(cmd, sql, ":pir_due", w.PirDueJson ?? (pirDue is null ? "{}" : await pirDue.JsonAsync(conn, w.Scope, ct)));
        var rows = new List<T>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) rows.Add(map(r));
        return rows;
    }

    private static void Bind(SqliteCommand cmd, string sql, string name, object? value)
    {
        if (sql.Contains(name, StringComparison.Ordinal)) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }
}
