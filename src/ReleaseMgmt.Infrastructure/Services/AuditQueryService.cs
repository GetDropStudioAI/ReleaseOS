using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

/// <summary>Filter for the audit log. Every member is optional; <see cref="From"/>/<see cref="ToInclusive"/> are UTC instants (see <see cref="AuditQueryService.TryParseBound"/>).</summary>
public sealed record AuditFilter(string? Train = null, string? Entity = null, string? EntityId = null, string? Actor = null, string? Action = null, DateTime? From = null, DateTime? ToInclusive = null);

public sealed record AuditRow(long Id, DateTime OccurredAt, string? ActorUserId, string? ActorName, string? ReleaseTrainId, string? TrainTitle,
    string EntityType, string EntityId, string Action, string? BeforeJson, string? AfterJson);

public sealed record AuditPage(IReadOnlyList<AuditRow> Items, string? NextCursor, int Limit);

/// <summary>
/// Read-only view of AuditEvents (REOS-38, Q-038a..c). Deliberately has no write path: the table is append-only by trigger and reading it is not itself audited.
/// Newest first by Id (keyset paging: stable while new rows arrive, no gaps or duplicates, unlike OFFSET).
/// </summary>
public sealed class AuditQueryService(IDbContextFactory<ReleaseDbContext> dbf)
{
    public const int DefaultLimit = 100, MaxLimit = 500;

    /// <summary>Reads a UTC ISO instant (2026-10-01T10:00:00Z; an explicit offset is honoured) or a date-only value (2026-10-01, a UTC day).
    /// A date-only <c>to</c> covers that whole day: it becomes the last second of the day.</summary>
    public static bool TryParseBound(string? text, bool isTo, out DateTime? utc)
    {
        utc = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        text = text.Trim();
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            var start = DateTime.SpecifyKind(d.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
            utc = isTo ? start.AddDays(1).AddSeconds(-1) : start;
            return true;
        }
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var o)) { utc = o.UtcDateTime; return true; }
        return false;
    }

    public static int ClampLimit(int? limit) => Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

    private static IQueryable<AuditEvents> Apply(IQueryable<AuditEvents> q, AuditFilter f)
    {
        if (!string.IsNullOrEmpty(f.Train)) q = q.Where(a => a.ReleaseTrainId == f.Train);
        if (!string.IsNullOrEmpty(f.Entity)) q = q.Where(a => a.EntityType == f.Entity);
        if (!string.IsNullOrEmpty(f.EntityId)) q = q.Where(a => a.EntityId == f.EntityId);
        if (!string.IsNullOrEmpty(f.Actor)) q = q.Where(a => a.ActorUserId == f.Actor);
        if (!string.IsNullOrWhiteSpace(f.Action))
        {
            // Contains, case-insensitive (SQLite LIKE); % _ and \ typed by the user are matched literally.
            var like = "%" + f.Action.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            q = q.Where(a => EF.Functions.Like(a.Action, like, "\\"));
        }
        if (f.From is { } from) q = q.Where(a => a.OccurredAt >= from);
        if (f.ToInclusive is { } to) q = q.Where(a => a.OccurredAt <= to);
        return q;
    }

    private static IQueryable<AuditRow> Project(ReleaseDbContext db, IQueryable<AuditEvents> q) =>
        from a in q
        join u in db.Set<Users>().AsNoTracking() on a.ActorUserId equals u.Id into us
        from u in us.DefaultIfEmpty()
        join t in db.Set<ReleaseTrains>().AsNoTracking() on a.ReleaseTrainId equals t.Id into ts
        from t in ts.DefaultIfEmpty()
        select new AuditRow(a.Id, a.OccurredAt, a.ActorUserId, u.DisplayName, a.ReleaseTrainId, t.Title, a.EntityType, a.EntityId, a.Action, a.BeforeJson, a.AfterJson);

    /// <summary>One page, newest first. <paramref name="cursor"/> is the <c>NextCursor</c> of the previous page (the Id of its last row).</summary>
    public async Task<AuditPage> QueryAsync(AuditFilter f, long? cursor, int? limit, CancellationToken ct = default)
    {
        var n = ClampLimit(limit);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var q = Apply(db.Set<AuditEvents>().AsNoTracking(), f);
        if (cursor is { } c) q = q.Where(a => a.Id < c);
        var rows = await Project(db, q.OrderByDescending(a => a.Id)).Take(n + 1).ToListAsync(ct);
        var more = rows.Count > n;
        if (more) rows.RemoveAt(n);
        return new AuditPage(rows, more ? rows[^1].Id.ToString(CultureInfo.InvariantCulture) : null, n);
    }

    public async Task<long> CountAsync(AuditFilter f, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        return await Apply(db.Set<AuditEvents>().AsNoTracking(), f).LongCountAsync(ct);
    }

    /// <summary>Newest-first batches for the CSV export, at most <paramref name="max"/> rows in total. Never holds more than one batch.</summary>
    public async IAsyncEnumerable<IReadOnlyList<AuditRow>> StreamAsync(AuditFilter f, int max, int batch = 500, [EnumeratorCancellation] CancellationToken ct = default)
    {
        long? cursor = null; var sent = 0;
        while (sent < max)
        {
            var page = await QueryAsync(f, cursor, Math.Min(batch, max - sent), ct);
            if (page.Items.Count == 0) yield break;
            sent += page.Items.Count;
            yield return page.Items;
            if (page.NextCursor is null) yield break;
            cursor = long.Parse(page.NextCursor, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>The entity types present in the log, for the filter line.</summary>
    public async Task<IReadOnlyList<string>> EntityTypesAsync(CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        return await db.Set<AuditEvents>().AsNoTracking().Select(a => a.EntityType).Distinct().OrderBy(x => x).ToListAsync(ct);
    }
}
