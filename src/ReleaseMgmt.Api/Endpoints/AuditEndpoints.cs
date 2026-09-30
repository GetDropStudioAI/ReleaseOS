using System.Globalization;
using System.Text;
using nietras.SeparatedValues;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>
/// Audit viewer endpoints (REOS-38; PROJECT_SCOPE section 6). Read-only: RTE, Release Manager (superset of RTE, D32) and Governance Officer.
/// Reading the audit log is not itself audited, and nothing here writes to AuditEvents. See Q-038a..c.
/// </summary>
public static class AuditEndpoints
{
    /// <summary>Config key Audit:CsvMaxRows (default 50 000): the most rows one CSV export contains.</summary>
    public const int DefaultCsvMaxRows = 50_000;
    private static readonly string[] Columns = ["Id", "OccurredAt", "Actor", "ActorUserId", "Train", "ReleaseTrainId", "EntityType", "EntityId", "Action", "BeforeJson", "AfterJson"];
    public const string TruncatedHeader = "X-Audit-Truncated", TotalHeader = "X-Audit-Total-Rows";

    public static void MapAudit(this RouteGroupBuilder api)
    {
        // GET /audit?train&entity&entityId&actor&action&from&to&cursor&limit -> { items[], nextCursor, limit }
        api.MapGet("/audit", async (HttpRequest req, AuditQueryService s, CancellationToken ct) =>
        {
            if (!TryFilter(req, out var f, out var bad)) return bad!;
            long? cursor = null;
            if (req.Query["cursor"].ToString() is { Length: > 0 } c)
            {
                if (!long.TryParse(c, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return Bad("cursor is the nextCursor of a previous page");
                cursor = id;
            }
            int? limit = int.TryParse(req.Query["limit"], out var l) ? l : null;
            return Results.Ok(await s.QueryAsync(f, cursor, limit, ct));
        }).RequireAuthorization(Policies.AuditRead);

        // GET /audit/entity-types -> string[] (feeds the filter line)
        api.MapGet("/audit/entity-types", async (AuditQueryService s, CancellationToken ct) => Results.Ok(await s.EntityTypesAsync(ct))).RequireAuthorization(Policies.AuditRead);

        // GET /audit.csv?<same filter> -> RFC 4180 CSV, newest first, at most Audit:CsvMaxRows rows (Q-038c: a cut is announced in headers and a final row)
        api.MapGet("/audit.csv", async (HttpContext http, AuditQueryService s, IConfiguration cfg, TimeProvider time, CancellationToken ct) =>
        {
            if (!TryFilter(http.Request, out var f, out var bad)) { await bad!.ExecuteAsync(http); return; }
            var max = Math.Max(1, cfg.GetValue("Audit:CsvMaxRows", DefaultCsvMaxRows));
            var total = await s.CountAsync(f, ct);
            var truncated = total > max;
            var res = http.Response;
            res.ContentType = "text/csv; charset=utf-8";
            res.Headers.ContentDisposition = $"attachment; filename=\"audit-{time.GetUtcNow():yyyyMMdd-HHmmss}.csv\"";
            res.Headers.CacheControl = "no-store";
            res.Headers[TotalHeader] = total.ToString(CultureInfo.InvariantCulture);
            res.Headers[TruncatedHeader] = truncated ? "true" : "false";
            var sb = new StringBuilder();
            using var writer = Sep.New(',').Writer(o => o with { Escape = true })   // Escape: RFC 4180 quoting of separators, quotes and line breaks (off by default in Sep)
                .To(new StringWriter(sb));
            async Task Flush()
            {
                writer.Flush();
                if (sb.Length == 0) return;
                await res.WriteAsync(sb.ToString(), new UTF8Encoding(false), ct);
                sb.Clear();
            }
            var wrote = false;
            await foreach (var batch in s.StreamAsync(f, max, ct: ct))
            {
                foreach (var a in batch)
                {
                    wrote = true;
                    using var row = writer.NewRow();
                    row["Id"].Set(a.Id.ToString(CultureInfo.InvariantCulture));
                    row["OccurredAt"].Set(a.OccurredAt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
                    row["Actor"].Set(Safe(a.ActorName ?? (a.ActorUserId is null ? "System" : "")));
                    row["ActorUserId"].Set(Safe(a.ActorUserId));
                    row["Train"].Set(Safe(a.TrainTitle));
                    row["ReleaseTrainId"].Set(Safe(a.ReleaseTrainId));
                    row["EntityType"].Set(Safe(a.EntityType));
                    row["EntityId"].Set(Safe(a.EntityId));
                    row["Action"].Set(Safe(a.Action));
                    row["BeforeJson"].Set(Safe(a.BeforeJson));
                    row["AfterJson"].Set(Safe(a.AfterJson));
                }
                await Flush();
            }
            if (!wrote) await res.WriteAsync(string.Join(',', Columns) + "\n", new UTF8Encoding(false), ct);   // Sep writes the header with the first row; an empty export still has one
            if (truncated)
            {
                using var row = writer.NewRow();
                row["Id"].Set($"# truncated: {max} of {total} matching events exported (newest first); narrow the filter (from/to) to export the rest");
                foreach (var col in Columns.Skip(1)) row[col].Set("");
            }
            await Flush();
        }).RequireAuthorization(Policies.AuditRead);
    }

    /// <summary>OWASP CSV injection: a cell a spreadsheet could read as a formula (starts with = + - @ tab or CR) gets a leading single quote. Null stays empty.</summary>
    public static string Safe(string? cell) => string.IsNullOrEmpty(cell) ? "" : cell[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + cell : cell;

    private static IResult Bad(string message) => Results.Json(new { guard = "InvalidFilter", message }, statusCode: StatusCodes.Status400BadRequest);

    private static bool TryFilter(HttpRequest req, out AuditFilter filter, out IResult? bad)
    {
        var q = req.Query;
        string? Get(string k) => q[k].ToString() is { Length: > 0 } v ? v : null;
        filter = new AuditFilter(Get("train"), Get("entity"), Get("entityId"), Get("actor"), Get("action"));
        bad = null;
        if (!AuditQueryService.TryParseBound(Get("from"), false, out var from)) { bad = Bad("from is a UTC date (2026-10-01) or instant (2026-10-01T10:00:00Z)"); return false; }
        if (!AuditQueryService.TryParseBound(Get("to"), true, out var to)) { bad = Bad("to is a UTC date (2026-10-01) or instant (2026-10-01T10:00:00Z)"); return false; }
        filter = filter with { From = from, ToInclusive = to };
        return true;
    }
}
