using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Exchange;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Exchange;

/// <summary>Optional filters a grid export understands. Unused members are ignored by grids that do not have that filter.</summary>
public sealed record GridFilter(string? Train = null, string? Status = null, bool? Open = null, string? UserId = null);

/// <summary>
/// One row as it is in the database, in canonical cell text (the same text the importer produces from a file), so an export re-imports as "unchanged"
/// by construction. <see cref="ReadOnly"/> are the <c>#</c> columns: shown in exports, skipped by imports.
/// </summary>
public sealed record CurrentRow(string Id, int Version, string? TrainId, string Key, IReadOnlyDictionary<string, string> Cells, IReadOnlyList<KeyValuePair<string, string>> ReadOnly);

/// <summary>A row of the preview, serialisable (stored beside the file and paged to the screen).</summary>
public sealed record PlanRow(int Row, string Op, string Key, IReadOnlyDictionary<string, string> Cells, IReadOnlyDictionary<string, string>? Before, IReadOnlyList<string> Changed);

internal enum RowOp { New, Updated, Unchanged, Error }

internal sealed class Planned
{
    public int Row;
    public RowOp Op;
    public string Key = "";
    public Dictionary<string, string> After = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string>? Before;
    public List<string> Changed = [];
    public CurrentRow? Existing;
    public string Refs = "";
    public string? TrainId;
    public object? Payload;
    public IReadOnlyDictionary<string, string> Shown = new Dictionary<string, string>();
}

internal sealed class Resolved
{
    public string Key = "";
    public Dictionary<string, string> After = new(StringComparer.OrdinalIgnoreCase);
    public string Refs = "";
    public string? TrainId;
    public ReleaseTrains? Train;
    public object? Payload;
}

internal sealed record PlanOutcome(IReadOnlyList<Planned> Rows, IReadOnlyList<ImportError> Errors, IReadOnlyList<ImportError> Warnings, IReadOnlyList<string> Decertifies)
{
    public int Count(RowOp op) => Rows.Count(r => r.Op == op);

    /// <summary>What a preview promised. Commit re-plans against the database as it is now and refuses (409) when this differs: a target row moved, a reference changed, or the train moved.</summary>
    public string Signature()
    {
        var sb = new StringBuilder();
        foreach (var r in Rows) sb.Append(r.Row).Append('|').Append(r.Op).Append('|').Append(r.Key).Append('|').Append(r.Existing?.Id).Append('|').Append(r.Existing?.Version).Append('|').Append(r.Refs).Append('\n');
        sb.Append("errors:").Append(Errors.Count);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }
}

/// <summary>Everything a handler needs while planning: the parsed rows, the mode, and shared lookups loaded once.</summary>
internal sealed class PlanEnv(ReleaseDbContext db, ImportKindSpec spec, string mode, HeaderResult header, IReadOnlyList<ParsedRow> rows, DateTime now)
{
    public ReleaseDbContext Db => db;
    public ImportKindSpec Spec => spec;
    public bool Append => mode == "Append";
    public HeaderResult Header => header;
    public IReadOnlyList<ParsedRow> Rows => rows;
    public DateTime Now => now;
    public List<ImportError> Errors { get; } = [];
    public List<ImportError> Warnings { get; } = [];
    public SortedSet<string> Decertifies { get; } = new(StringComparer.Ordinal);
    /// <summary>Scratch space for one handler during one plan (handlers themselves are stateless).</summary>
    public Dictionary<string, object> Bag { get; } = [];

    public bool Has(string column) => header.Columns.ContainsKey(column);
    public void Err(int row, string column, string message) => Errors.Add(new(row, column, message));

    /// <summary>"Did you mean" work shared by every row of this plan (SEC-D2): a file of near-miss names cannot make a preview run for minutes.</summary>
    public SuggestBudget SuggestBudget { get; } = new();
    public IReadOnlyList<string> Suggest(string input, IEnumerable<string> pool, int max = 1) => RowParser.Suggest(input, pool, max, SuggestBudget);
    public void Warn(int row, string column, string message) => Warnings.Add(new(row, column, message));

    // ---- shared lookups (loaded on demand, read-only) ---------------------------------------------------------------------------------
    public List<Users> Users { get; private set; } = [];
    public List<Teams> Teams { get; private set; } = [];
    public List<ReleaseTrains> Trains { get; private set; } = [];
    public HashSet<DateOnly> Holidays { get; private set; } = [];
    private bool _people, _trains, _holidays;

    public async Task LoadPeopleAsync(CancellationToken ct)
    {
        if (_people) return;
        Users = await db.Set<Users>().AsNoTracking().ToListAsync(ct);
        Teams = await db.Set<Teams>().AsNoTracking().ToListAsync(ct);
        _people = true;
    }

    public async Task LoadTrainsAsync(CancellationToken ct)
    {
        if (_trains) return;
        Trains = await db.Set<ReleaseTrains>().AsNoTracking().ToListAsync(ct);
        _trains = true;
    }

    public async Task LoadHolidaysAsync(CancellationToken ct)
    {
        if (_holidays) return;
        Holidays = [.. await db.Set<Holidays>().AsNoTracking().Select(h => h.Day).ToListAsync(ct)];
        _holidays = true;
    }

    /// <summary>The train a Train cell names (title, case-insensitive). Adds a row error and returns null when there is none or more than one.</summary>
    public ReleaseTrains? ResolveTrain(int row, string cell, string column = "Train")
    {
        var hits = Trains.Where(t => string.Equals(t.Title, cell, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hits.Count == 1) return hits[0];
        if (hits.Count > 1) { Err(row, column, $"{Quote(cell)} matches {hits.Count} trains with the same title; rename one so the file can name it"); return null; }
        var near = Suggest(cell, Trains.Select(t => t.Title));
        Err(row, column, $"No train is titled {Quote(cell)}.{Did(near)}");
        return null;
    }

    /// <summary>An owner cell: an email, or an @handle that names a team first and then a user (as the bulk parser does). Canonical text is the user's email or "@teamhandle".</summary>
    public bool ResolveOwner(int row, string column, string cell, out string? userId, out string? teamId, out string canonical, out Users? user)
    {
        userId = null; teamId = null; canonical = cell; user = null;
        if (cell.StartsWith('@'))
        {
            var h = cell[1..];
            var team = Teams.FirstOrDefault(t => string.Equals(t.Handle, h, StringComparison.OrdinalIgnoreCase));
            if (team is not null) { teamId = team.Id; canonical = "@" + team.Handle; return true; }
            user = Users.FirstOrDefault(u => u.Handle is not null && string.Equals(u.Handle, h, StringComparison.OrdinalIgnoreCase));
        }
        else user = Users.FirstOrDefault(u => string.Equals(u.Email, cell, StringComparison.OrdinalIgnoreCase));
        if (user is not null) { userId = user.Id; canonical = user.Email; return true; }
        var pool = Teams.Select(t => "@" + t.Handle).Concat(Users.Where(u => u.Handle != null).Select(u => "@" + u.Handle!)).Concat(Users.Select(u => u.Email));
        Err(row, column, $"No team or user matches {Quote(cell)}.{Did(Suggest(cell, pool, 2))}");
        return false;
    }

    public static string Quote(string s) => $"\"{s}\"";
    public static string Did(IReadOnlyList<string> near) => near.Count == 0 ? "" : $" Did you mean {string.Join(" or ", near.Select(Quote))}?";
}

/// <summary>What a handler needs to write: the context (inside the service transaction), the actor, the service clock, and the audit/touch hooks of <c>ServiceBase</c>.</summary>
internal sealed class ApplyEnv(ReleaseDbContext db, Actor actor, DateTime now, Action<string?, string, string, string, object?, object?> audit, string mode)
{
    public ReleaseDbContext Db => db;
    public Actor Actor => actor;
    public DateTime Now => now;
    public string Mode => mode;
    public HashSet<string> TrainsToBump { get; } = [];
    public HashSet<DateOnly> Holidays { get; set; } = [];

    /// <summary>Bump each train at most once per job (the trains moved: other open previews and parser previews are now stale, like BulkCommit).</summary>
    public void Bump(string? trainId) { if (trainId is not null) TrainsToBump.Add(trainId); }

    /// <summary>One AuditEvents row per changed or created row: Before/After hold only the columns that changed (all columns for an insert).</summary>
    public void AuditRow(Planned p, string? trainId, string entityType, string entityId)
    {
        object? before = p.Op == RowOp.Updated ? p.Changed.ToDictionary(c => c, c => p.Before![c]) : null;
        object? after = p.Op == RowOp.Updated ? p.Changed.ToDictionary(c => c, c => p.After[c]) : new Dictionary<string, string>(p.After);
        audit(trainId, entityType, entityId, p.Op == RowOp.New ? "ImportInsert" : "ImportUpdate", before, after);
    }
}

/// <summary>One import kind: how its rows look in the database, how a file row is resolved and compared, and how a plan is written.</summary>
internal abstract class KindHandler
{
    public abstract ImportKindSpec Spec { get; }

    /// <summary>Every row of this kind now, in canonical cell text, ordered for export.</summary>
    public abstract Task<List<CurrentRow>> LoadAsync(ReleaseDbContext db, GridFilter filter, CancellationToken ct);

    protected virtual Task PrepareAsync(PlanEnv env, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Resolves references and canonicalises the file cells. Adds row errors to <paramref name="env"/> and returns null when the row cannot be resolved.</summary>
    protected abstract Resolved? Resolve(PlanEnv env, ParsedRow row);

    /// <summary>Called once the existing row (if any) is known, before comparing. Handlers use it for "empty means keep" columns.</summary>
    protected virtual void Finish(PlanEnv env, Resolved r, CurrentRow? existing) { }

    /// <summary>Business rules that need to know whether the row is new, changed or unchanged (plan locks, certified gates...). Add errors to <paramref name="env"/>.</summary>
    protected virtual void Rules(PlanEnv env, ParsedRow row, Resolved r, Planned p) { }

    /// <summary>After every row was planned (cross-row checks such as dependency cycles).</summary>
    protected virtual void Final(PlanEnv env, List<Planned> planned) { }

    public abstract Task ApplyAsync(ApplyEnv env, IReadOnlyList<Planned> rows, CancellationToken ct);

    private string KeyText(Resolved r) => string.Join(" + ", Spec.Key.Select(k => r.After.TryGetValue(k, out var v) ? v : "?"));

    public async Task<PlanOutcome> PlanAsync(PlanEnv env, GridFilter all, CancellationToken ct)
    {
        await env.LoadTrainsAsync(ct);
        await PrepareAsync(env, ct);
        var current = (await LoadAsync(env.Db, all, ct)).ToLookup(c => c.Key, StringComparer.Ordinal);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var planned = new List<Planned>();
        var keyColumn = Spec.Key[^1];
        foreach (var parsed in env.Rows)
        {
            var errorsBefore = env.Errors.Count;
            var p = new Planned { Row = parsed.Row, Shown = parsed.Cells };
            planned.Add(p);
            if (parsed.Errors.Count > 0) { env.Errors.AddRange(parsed.Errors); p.Op = RowOp.Error; continue; }
            var r = Resolve(env, parsed);
            if (r is null) { p.Op = RowOp.Error; continue; }
            p.Key = r.Key; p.After = r.After; p.Refs = r.Refs; p.TrainId = r.TrainId; p.Payload = r.Payload;
            p.Shown = r.After;

            if (seen.TryGetValue(r.Key, out var first))
            {
                env.Err(parsed.Row, keyColumn, $"Duplicate of row {first}: {KeyText(r)} appears twice in this file");
                p.Op = RowOp.Error; continue;
            }
            seen[r.Key] = parsed.Row;

            var matches = current[r.Key].ToList();
            if (matches.Count > 1)
            {
                env.Err(parsed.Row, keyColumn, $"{KeyText(r)} matches {matches.Count} existing rows, so the file cannot say which to update");
                p.Op = RowOp.Error; continue;
            }
            var existing = matches.SingleOrDefault();
            Finish(env, r, existing);
            if (existing is null) p.Op = RowOp.New;
            else if (env.Append)
            {
                env.Err(parsed.Row, keyColumn, $"{KeyText(r)} already exists. Append refuses existing keys; use Upsert to update it");
                p.Op = RowOp.Error; continue;
            }
            else
            {
                p.Existing = existing;
                p.Before = new Dictionary<string, string>(existing.Cells, StringComparer.OrdinalIgnoreCase);
                var keys = Spec.Key.ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var (col, value) in r.After)
                    if (!keys.Contains(col) && !string.Equals(existing.Cells.GetValueOrDefault(col, ""), value, StringComparison.Ordinal)) p.Changed.Add(col);
                p.Op = p.Changed.Count > 0 ? RowOp.Updated : RowOp.Unchanged;
            }
            Rules(env, parsed, r, p);
            if (env.Errors.Count > errorsBefore) p.Op = RowOp.Error;
        }
        Final(env, planned);
        var byRow = planned.ToLookup(x => x.Row);   // one lookup per error, not a scan of every row (10,000 rows x 100,000 errors was 10^9 comparisons)
        foreach (var e in env.Errors.Where(e => e.Row > 0)) { var p = byRow[e.Row].FirstOrDefault(); if (p is not null) p.Op = RowOp.Error; }
        return new PlanOutcome(planned, env.Errors, env.Warnings, [.. env.Decertifies]);
    }

    // ---- helpers shared by handlers ---------------------------------------------------------------------------------------------------
    protected static string Ts(DateTime? t) => t is null ? "" : t.Value.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
    protected static string Day(DateOnly? d) => d is null ? "" : d.Value.ToString("yyyy-MM-dd");
    protected static string Norm(string? s) => (s ?? "").Trim();
    protected static string K(params string[] parts) => string.Join('\u001f', parts.Select(p => p.ToLowerInvariant()));
    protected static string KExact(string lowerPart, params string[] exact) => string.Join('\u001f', new[] { lowerPart.ToLowerInvariant() }.Concat(exact));

    protected static DateTime ParseTs(string canonical) => DateTime.SpecifyKind(DateTime.ParseExact(canonical, "yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal), DateTimeKind.Utc);
    protected static DateOnly ParseDay(string canonical) => DateOnly.ParseExact(canonical, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    protected static int ParseInt(string canonical) => int.Parse(canonical, System.Globalization.CultureInfo.InvariantCulture);

    public static string OwnerCell(string? userId, string? teamId, IReadOnlyDictionary<string, Users> users, IReadOnlyDictionary<string, Teams> teams) =>
        userId is not null && users.TryGetValue(userId, out var u) ? u.Email : teamId is not null && teams.TryGetValue(teamId, out var t) ? "@" + t.Handle : "";

    public static string TrainCell(string? trainId, IReadOnlyDictionary<string, ReleaseTrains> trains) => trainId is not null && trains.TryGetValue(trainId, out var t) ? t.Title : "";

    /// <summary>Owner columns of a row and the two lookups every train-scoped loader needs.</summary>
    protected static async Task<(Dictionary<string, Users> Users, Dictionary<string, Teams> Teams, Dictionary<string, ReleaseTrains> Trains)> RefsAsync(ReleaseDbContext db, CancellationToken ct) =>
        ((await db.Set<Users>().AsNoTracking().ToListAsync(ct)).ToDictionary(u => u.Id),
         (await db.Set<Teams>().AsNoTracking().ToListAsync(ct)).ToDictionary(t => t.Id),
         (await db.Set<ReleaseTrains>().AsNoTracking().ToListAsync(ct)).ToDictionary(t => t.Id));

    protected static IEnumerable<T[]> Chunks<T>(IEnumerable<T> items, int size = 500) => items.Chunk(size);
}
