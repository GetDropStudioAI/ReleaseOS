using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Exchange;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Exchange;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

/// <summary>One exportable grid. <see cref="ImportKind"/> is set where a file of this grid can be imported back; <see cref="Policy"/> is "Read" or "AuditRead".</summary>
public sealed record GridInfo(string Key, string Label, string Description, string? ImportKind, IReadOnlyList<string> Filters, string Policy);

/// <summary>
/// CSV and XLSX for every grid (PROJECT_SCOPE 9, REOS-49). The nine import kinds export exactly their import columns in the importer's canonical text, followed by <c>#</c>-prefixed read-only
/// columns (ids, versions, statuses, derived dates) that an import skips, so an export re-imports as "unchanged". The other grids are export-only. See Q-048d..e.
/// </summary>
public sealed class GridExportService(IDbContextFactory<ReleaseDbContext> dbf, AuditQueryService audit)
{
    public const int DefaultMaxRows = 50_000;

    /// <summary>The <c>#</c> columns of each import kind, in file order (the values come from <see cref="CurrentRow.ReadOnly"/>).</summary>
    internal static readonly IReadOnlyDictionary<string, string[]> ReadOnlyColumns = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        [ImportKinds.Users] = ["#Id", "#IsActive", "#Version"], [ImportKinds.Teams] = ["#Id", "#Version"], [ImportKinds.Holidays] = ["#Version"],
        [ImportKinds.Trains] = ["#Id", "#Status", "#Version"], [ImportKinds.Products] = ["#Id", "#Version"], [ImportKinds.Gates] = ["#Id", "#DueOn", "#Status", "#Version"],
        [ImportKinds.Tasks] = ["#Id", "#Completed", "#Version"], [ImportKinds.RunbookSteps] = ["#Id", "#Version"], [ImportKinds.ExternalLinks] = ["#Id", "#SyncState", "#LastSyncedStatus", "#Version"],
    };

    private static readonly string[] TrainOnly = ["train"];

    public static readonly IReadOnlyList<GridInfo> Grids =
    [
        .. ImportKinds.All.Select(k => new GridInfo(k.Grid, k.Label, Describe(k.Kind), k.Kind, k.Kind switch { ImportKinds.Trains => ["status"], ImportKinds.Users or ImportKinds.Teams or ImportKinds.Holidays => [], _ => TrainOnly }, "Read")),
        new("blockers", "Blockers", "open and resolved blockers with severity and owner", null, ["train", "open"], "Read"),
        new("known-issues", "Known issues", "hypercare log", null, ["train"], "Read"),
        new("freeze-windows", "Freeze windows", "freezes and chills with their product patterns", null, [], "Read"),
        new("attachments", "Attachments manifest", "evidence files with SHA-256 (the files themselves are not exported)", null, ["train"], "Read"),
        new("notifications", "My notifications", "your inbox, newest first", null, ["open"], "Read"),
        new("sync-alerts", "Sync alerts", "ITSM, backup, export and webhook failures", null, ["train"], "Read"),
        new("templates", "Train templates", "template names, status and review dates", null, [], "Read"),
        new("audit", "Audit log", "every change with before and after JSON", null, ["train", "entity", "entityId", "actor", "action", "from", "to"], "AuditRead"),
    ];

    private static string Describe(string kind) => kind switch
    {
        ImportKinds.Trains => "release trains: dates, risk tier, change ticket, window",
        ImportKinds.Products => "bundled products per train",
        ImportKinds.Gates => "stage gates with owners and due dates",
        ImportKinds.Tasks => "checklist tasks per gate",
        ImportKinds.RunbookSteps => "the runbook plan with owners and dependencies",
        ImportKinds.ExternalLinks => "Jira and ServiceNow links",
        ImportKinds.Holidays => "the business-day calendar",
        ImportKinds.Users => "people and roles",
        _ => "teams and their members",
    };

    public static GridInfo? Find(string? grid) => Grids.FirstOrDefault(g => string.Equals(g.Key, grid, StringComparison.OrdinalIgnoreCase));

    /// <summary>The grid as a table: at most <paramref name="maxRows"/> rows, with the true total so a cut is announced (as the audit export does, Q-038c).</summary>
    public async Task<ServiceResult<GridTable>> TableAsync(string grid, GridFilter filter, AuditFilter? auditFilter, int maxRows, CancellationToken ct = default)
    {
        var info = Find(grid);
        if (info is null) return ServiceResult<GridTable>.Fail(new GuardFailure(ExchangeGuards.UnknownGrid, $"{grid} is not a grid. Grids: {string.Join(", ", Grids.Select(g => g.Key))}"));
        await using var db = await dbf.CreateDbContextAsync(ct);
        if (!string.IsNullOrEmpty(filter.Train))
        {
            var byId = await db.Set<ReleaseTrains>().AsNoTracking().AnyAsync(t => t.Id == filter.Train, ct);
            if (!byId)
            {
                var hits = await db.Set<ReleaseTrains>().AsNoTracking().Where(t => t.Title == filter.Train).Select(t => t.Id).ToListAsync(ct);
                if (hits.Count != 1) return ServiceResult<GridTable>.Fail(new GuardFailure("InvalidFilter", hits.Count == 0 ? $"No train has the id or title {filter.Train}" : $"{hits.Count} trains are titled {filter.Train}; use the id"));
                filter = filter with { Train = hits[0] };
            }
        }
        if (info.ImportKind is { } kind)
        {
            var handler = CsvImportService.Handlers[kind];
            var current = await handler.LoadAsync(db, filter, ct);
            var ro = ReadOnlyColumns[kind];
            var cols = handler.Spec.Columns.Select(c => c.Name).Concat(ro).ToList();
            var rows = current.Take(maxRows).Select(r => cols.Select(c => c.StartsWith('#') ? r.ReadOnly.FirstOrDefault(x => x.Key == c).Value ?? "" : r.Cells.GetValueOrDefault(c, "")).ToArray()).ToList();
            var numeric = cols.Select(c => handler.Spec.Find(c) is { Type: ColumnType.Int } || c == "#Version").ToList();
            return ServiceResult<GridTable>.Ok(new GridTable(info.Key, info.Label, cols, numeric, rows, current.Count, current.Count > maxRows));
        }
        return ServiceResult<GridTable>.Ok(info.Key switch
        {
            "blockers" => await BlockersAsync(db, info, filter, maxRows, ct),
            "known-issues" => await KnownIssuesAsync(db, info, filter, maxRows, ct),
            "freeze-windows" => await FreezesAsync(db, info, maxRows, ct),
            "attachments" => await AttachmentsAsync(db, info, filter, maxRows, ct),
            "notifications" => await NotificationsAsync(db, info, filter, maxRows, ct),
            "sync-alerts" => await SyncAlertsAsync(db, info, filter, maxRows, ct),
            "templates" => await TemplatesAsync(db, info, maxRows, ct),
            _ => await AuditAsync(info, auditFilter ?? new AuditFilter(filter.Train), maxRows, ct),
        });
    }

    private static string Ts(DateTime? t) => t is null ? "" : t.Value.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    private static string Day(DateOnly? d) => d is null ? "" : d.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string N(string? s) => (s ?? "").Trim();
    private static readonly Func<int, string> I = n => n.ToString(CultureInfo.InvariantCulture);

    private static GridTable Table(GridInfo g, string[] cols, IEnumerable<string[]> all, int max, string[]? numeric = null)
    {
        var list = all as IList<string[]> ?? all.ToList();
        var isNum = cols.Select(c => numeric?.Contains(c) == true || c == "#Version").ToList();
        return new GridTable(g.Key, g.Label, cols, isNum, [.. list.Take(max)], list.Count, list.Count > max);
    }

    private static async Task<GridTable> BlockersAsync(ReleaseDbContext db, GridInfo g, GridFilter f, int max, CancellationToken ct)
    {
        var q = db.Set<Blockers>().AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(f.Train)) q = q.Where(b => b.ReleaseTrainId == f.Train);
        if (f.Open == true) q = q.Where(b => b.ResolvedAt == null);
        var trains = (await db.Set<ReleaseTrains>().AsNoTracking().Select(t => new { t.Id, t.Title }).ToListAsync(ct)).ToDictionary(t => t.Id, t => t.Title);
        var products = (await db.Set<BundledProducts>().AsNoTracking().Select(p => new { p.Id, p.ProductName }).ToListAsync(ct)).ToDictionary(p => p.Id, p => p.ProductName);
        var gates = (await db.Set<StageGates>().AsNoTracking().Select(x => new { x.Id, x.GateName }).ToListAsync(ct)).ToDictionary(x => x.Id, x => x.GateName);
        var users = (await db.Set<Users>().AsNoTracking().Select(u => new { u.Id, u.Email }).ToListAsync(ct)).ToDictionary(u => u.Id, u => u.Email);
        var rows = (await q.ToListAsync(ct)).OrderBy(b => trains.GetValueOrDefault(b.ReleaseTrainId, ""), StringComparer.Ordinal).ThenByDescending(b => b.RaisedAt).ThenBy(b => b.Id, StringComparer.Ordinal)
            .Select(b => new[] { trains.GetValueOrDefault(b.ReleaseTrainId, ""), N(b.Title), b.Severity, b.BundledProductId is not null ? N(products.GetValueOrDefault(b.BundledProductId)) : "",
                b.StageGateId is not null ? N(gates.GetValueOrDefault(b.StageGateId)) : "", b.OwnerUserId is not null ? users.GetValueOrDefault(b.OwnerUserId, "") : "", Ts(b.RaisedAt), Ts(b.ResolvedAt), b.Id, I(b.Version) });
        return Table(g, ["Train", "Title", "Severity", "Product", "Gate", "Owner", "RaisedAt", "ResolvedAt", "#Id", "#Version"], rows, max);
    }

    private static async Task<GridTable> KnownIssuesAsync(ReleaseDbContext db, GridInfo g, GridFilter f, int max, CancellationToken ct)
    {
        var q = db.Set<KnownIssues>().AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(f.Train)) q = q.Where(b => b.ReleaseTrainId == f.Train);
        var trains = (await db.Set<ReleaseTrains>().AsNoTracking().Select(t => new { t.Id, t.Title }).ToListAsync(ct)).ToDictionary(t => t.Id, t => t.Title);
        var rows = (await q.ToListAsync(ct)).OrderBy(b => trains.GetValueOrDefault(b.ReleaseTrainId, ""), StringComparer.Ordinal).ThenByDescending(b => b.RaisedAt).ThenBy(b => b.Id, StringComparer.Ordinal)
            .Select(b => new[] { trains.GetValueOrDefault(b.ReleaseTrainId, ""), N(b.Title), b.Severity, b.Status, N(b.Workaround), N(b.ExternalKey), Ts(b.RaisedAt), Ts(b.ResolvedAt), b.Id, I(b.Version) });
        return Table(g, ["Train", "Title", "Severity", "Status", "Workaround", "ExternalKey", "RaisedAt", "ResolvedAt", "#Id", "#Version"], rows, max);
    }

    private static async Task<GridTable> FreezesAsync(ReleaseDbContext db, GridInfo g, int max, CancellationToken ct)
    {
        var users = (await db.Set<Users>().AsNoTracking().Select(u => new { u.Id, u.Email }).ToListAsync(ct)).ToDictionary(u => u.Id, u => u.Email);
        var rows = (await db.Set<FreezeWindows>().AsNoTracking().ToListAsync(ct)).OrderBy(w => w.StartsAt).ThenBy(w => w.Id, StringComparer.Ordinal)
            .Select(w => new[] { N(w.Name), w.Kind, Ts(w.StartsAt), Ts(w.EndsAt), N(w.ProductPattern), users.GetValueOrDefault(w.CreatedByUserId, ""), w.Id, I(w.Version) });
        return Table(g, ["Name", "Kind", "StartsAt", "EndsAt", "ProductPattern", "CreatedBy", "#Id", "#Version"], rows, max);
    }

    private static async Task<GridTable> AttachmentsAsync(ReleaseDbContext db, GridInfo g, GridFilter f, int max, CancellationToken ct)
    {
        var q = db.Set<Attachments>().AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(f.Train)) q = q.Where(a => a.ReleaseTrainId == f.Train);
        var trains = (await db.Set<ReleaseTrains>().AsNoTracking().Select(t => new { t.Id, t.Title }).ToListAsync(ct)).ToDictionary(t => t.Id, t => t.Title);
        var users = (await db.Set<Users>().AsNoTracking().Select(u => new { u.Id, u.Email }).ToListAsync(ct)).ToDictionary(u => u.Id, u => u.Email);
        var rows = (await q.ToListAsync(ct)).OrderBy(a => trains.GetValueOrDefault(a.ReleaseTrainId, ""), StringComparer.Ordinal).ThenBy(a => a.UploadedAt).ThenBy(a => a.Id, StringComparer.Ordinal)
            .Select(a => new[] { trains.GetValueOrDefault(a.ReleaseTrainId, ""), a.EntityType, a.EntityId, N(a.FileName), a.ContentType, a.SizeBytes.ToString(CultureInfo.InvariantCulture), a.Sha256,
                users.GetValueOrDefault(a.UploadedByUserId, ""), Ts(a.UploadedAt), a.IsLocked ? "Yes" : "No", a.Id });
        return Table(g, ["Train", "EntityType", "EntityId", "FileName", "ContentType", "SizeBytes", "Sha256", "UploadedBy", "UploadedAt", "Locked", "#Id"], rows, max, numeric: ["SizeBytes"]);
    }

    private static async Task<GridTable> NotificationsAsync(ReleaseDbContext db, GridInfo g, GridFilter f, int max, CancellationToken ct)
    {
        var q = db.Set<Notifications>().AsNoTracking().Where(n => n.UserId == (f.UserId ?? ""));   // the inbox is personal: never anyone else's
        if (f.Open == true) q = q.Where(n => n.ReadAt == null);
        var rows = (await q.ToListAsync(ct)).OrderByDescending(n => n.CreatedAt).ThenBy(n => n.Id, StringComparer.Ordinal)
            .Select(n => new[] { n.Kind, n.EntityType, n.EntityId, I(n.EscalationLevel), N(n.Message), Ts(n.CreatedAt), Ts(n.ReadAt), n.Id, I(n.Version) });
        return Table(g, ["Kind", "EntityType", "EntityId", "EscalationLevel", "Message", "CreatedAt", "ReadAt", "#Id", "#Version"], rows, max, numeric: ["EscalationLevel"]);
    }

    private static async Task<GridTable> SyncAlertsAsync(ReleaseDbContext db, GridInfo g, GridFilter f, int max, CancellationToken ct)
    {
        var q = db.Set<SyncAlerts>().AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(f.Train)) q = q.Where(a => a.ReleaseTrainId == f.Train);
        var trains = (await db.Set<ReleaseTrains>().AsNoTracking().Select(t => new { t.Id, t.Title }).ToListAsync(ct)).ToDictionary(t => t.Id, t => t.Title);
        var rows = (await q.ToListAsync(ct)).OrderByDescending(a => a.LastOccurredAt).ThenBy(a => a.Id, StringComparer.Ordinal)
            .Select(a => new[] { a.ReleaseTrainId is not null ? trains.GetValueOrDefault(a.ReleaseTrainId, "") : "", a.SourceSystem, a.Kind, N(a.ErrorMessage), I(a.OccurrenceCount), Ts(a.FirstOccurredAt), Ts(a.LastOccurredAt), a.IsResolved ? "Yes" : "No", a.Id, I(a.Version) });
        return Table(g, ["Train", "SourceSystem", "Kind", "ErrorMessage", "OccurrenceCount", "FirstOccurredAt", "LastOccurredAt", "Resolved", "#Id", "#Version"], rows, max, numeric: ["OccurrenceCount"]);
    }

    private static async Task<GridTable> TemplatesAsync(ReleaseDbContext db, GridInfo g, int max, CancellationToken ct)
    {
        var rows = (await db.Set<TrainTemplates>().AsNoTracking().ToListAsync(ct)).OrderBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => new[] { N(t.Name), t.Status, t.DefaultRiskTier, Day(t.ReviewDueOn), t.Id, I(t.Version) });
        return Table(g, ["Name", "Status", "DefaultRiskTier", "ReviewDueOn", "#Id", "#Version"], rows, max);
    }

    private async Task<GridTable> AuditAsync(GridInfo g, AuditFilter f, int max, CancellationToken ct)
    {
        var total = await audit.CountAsync(f, ct);
        var rows = new List<string[]>();
        await foreach (var batch in audit.StreamAsync(f, max, ct: ct))
            foreach (var a in batch)
                rows.Add([a.Id.ToString(CultureInfo.InvariantCulture), Ts(a.OccurredAt), a.ActorName ?? (a.ActorUserId is null ? "System" : ""), a.ActorUserId ?? "", a.TrainTitle ?? "", a.ReleaseTrainId ?? "",
                    a.EntityType, a.EntityId, a.Action, a.BeforeJson ?? "", a.AfterJson ?? ""]);
        string[] cols = ["Id", "OccurredAt", "Actor", "ActorUserId", "Train", "ReleaseTrainId", "EntityType", "EntityId", "Action", "BeforeJson", "AfterJson"];
        return new GridTable(g.Key, g.Label, cols, cols.Select(c => c == "Id").ToList(), rows, total, total > max);
    }
}
