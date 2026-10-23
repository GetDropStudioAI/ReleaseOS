using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

/// <summary>Filter for the audit log. Every member is optional; <see cref="From"/>/<see cref="ToInclusive"/> are UTC instants (see <see cref="AuditQueryService.TryParseBound"/>).</summary>
public sealed record AuditFilter(string? Train = null, string? Entity = null, string? EntityId = null, string? Actor = null, string? Action = null, DateTime? From = null, DateTime? ToInclusive = null);

public sealed record AuditRow(long Id, DateTime OccurredAt, string? ActorUserId, string? ActorName, string? ReleaseTrainId, string? TrainTitle,
    string EntityType, string EntityId, string Action, string? BeforeJson, string? AfterJson);

/// <summary><paramref name="Total"/> is the number of events matching the filter, set only when the caller asked for it (the audit screen's "Showing 1–100 of 412").</summary>
public sealed record AuditPage(IReadOnlyList<AuditRow> Items, string? NextCursor, int Limit, long? Total = null);

/// <summary>
/// What the audit screen's integrity banner says (REOS-101, REOS-114): the protection of the log (the two triggers that make AuditEvents append-only, checked in the live
/// database), the extent of the log in this workspace, and its hash chain: <see cref="Status"/> is Verified, Failed (with <see cref="FirstBad"/>, the first row that does not
/// verify, and why) or Unverified; <see cref="SealedRows"/> are chained, <see cref="PendingRows"/> are written and waiting to be sealed; <see cref="VerifiedAt"/> is the last full check.
/// </summary>
public sealed record AuditIntegrity(bool AppendOnly, IReadOnlyList<string> MissingTriggers, bool HashChained, long RetainedEvents, DateTime? OldestAt, DateTime? NewestAt, DateTime CheckedAt,
    string Status = "Unverified", long SealedRows = 0, long PendingRows = 0, ChainBreak? FirstBad = null, DateTime? VerifiedAt = null, long? HeadId = null, string? HeadHash = null);

/// <summary>
/// Read-only view of AuditEvents (REOS-38, Q-038a..c). Deliberately has no write path: the table is append-only by trigger and reading it is not itself audited.
/// Newest first by Id (keyset paging: stable while new rows arrive, no gaps or duplicates, unlike OFFSET).
/// </summary>
public sealed class AuditQueryService(IDbContextFactory<ReleaseDbContext> dbf, Microsoft.Extensions.Configuration.IConfiguration? config = null)
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

    /// <param name="newestFirstPage">
    /// True for the paged, newest-first query. An entity-type filter with no entity id is then written so that IX_Audit_Entity cannot be used
    /// (<c>EntityType || ''</c>). The entity types are a dozen values that each own a few percent to a third of the log, so the index finds tens of thousands
    /// of matches and SQLite then sorts them all by Id (~80 ms for 100 000 rows, growing with the log); walking the primary key downwards stops after
    /// one page (~0.5 ms; found by the REOS-52 load test). With an entity id the index is the right choice and is left alone. A plain ANALYZE does not
    /// change this plan in the SQLite build we ship, which is why the query itself is steered.
    /// </param>
    private static IQueryable<AuditEvents> Apply(IQueryable<AuditEvents> q, AuditFilter f, bool newestFirstPage = false)
    {
        if (!string.IsNullOrEmpty(f.Train)) q = q.Where(a => a.ReleaseTrainId == f.Train);
        if (!string.IsNullOrEmpty(f.Entity))
            q = newestFirstPage && string.IsNullOrEmpty(f.EntityId) ? q.Where(a => a.EntityType + "" == f.Entity) : q.Where(a => a.EntityType == f.Entity);
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
    public async Task<AuditPage> QueryAsync(AuditFilter f, long? cursor, int? limit, CancellationToken ct = default, bool includeTotal = false)
    {
        var n = ClampLimit(limit);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var q = Apply(db.Set<AuditEvents>().AsNoTracking(), f, newestFirstPage: true);
        if (cursor is { } c) q = q.Where(a => a.Id < c);
        var rows = await Project(db, q.OrderByDescending(a => a.Id)).Take(n + 1).ToListAsync(ct);
        var more = rows.Count > n;
        if (more) rows.RemoveAt(n);
        var total = includeTotal ? await CountAsync(f, ct) : (long?)null;
        return new AuditPage(rows, more ? rows[^1].Id.ToString(CultureInfo.InvariantCulture) : null, n, total);
    }

    /// <summary>One event by its id (the audit screen reopens a shared link to an event that is not on the page it loaded). Null when it does not exist in this workspace.</summary>
    public async Task<AuditRow?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        return await Project(db, db.Set<AuditEvents>().AsNoTracking().Where(a => a.Id == id)).SingleOrDefaultAsync(ct);
    }

    /// <summary>Names of the triggers that keep AuditEvents append-only (db/schema.sql); the integrity check fails when the live database lacks one.</summary>
    public static readonly string[] AppendOnlyTriggers = ["trg_Audit_NoUpdate", "trg_Audit_NoDelete"];

    public async Task<AuditIntegrity> IntegrityAsync(TimeProvider time, CancellationToken ct = default, AuditChainService? chain = null, bool recheck = false)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var present = await db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'trigger' AND tbl_name = 'AuditEvents'").ToListAsync(ct);
        var missing = AppendOnlyTriggers.Where(t => !present.Contains(t)).ToList();
        var events = db.Set<AuditEvents>().AsNoTracking();
        var count = await events.LongCountAsync(ct);
        var oldest = count == 0 ? (DateTime?)null : await events.MinAsync(a => a.OccurredAt, ct);
        var newest = count == 0 ? (DateTime?)null : await events.MaxAsync(a => a.OccurredAt, ct);
        if (chain is null) return new AuditIntegrity(missing.Count == 0, missing, HashChained: false, count, oldest, newest, time.GetUtcNow().UtcDateTime);
        var c = await chain.IntegrityAsync(recheck, TimeSpan.FromMinutes(Math.Max(1, config?.GetValue("Audit:Verify:MaxAgeMinutes", 70) ?? 70)), ct);
        return new AuditIntegrity(missing.Count == 0, missing, HashChained: true, count, oldest, newest, time.GetUtcNow().UtcDateTime, c.Status, c.SealedRows, c.PendingRows, c.FirstBad, c.VerifiedAt, c.HeadId, c.HeadHash);
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
