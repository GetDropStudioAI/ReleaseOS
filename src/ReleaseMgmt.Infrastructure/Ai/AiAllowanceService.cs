using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReleaseMgmt.Domain.Ai;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Infrastructure.Ai;

/// <summary>
/// REOS-185 (D57, D58): the AI allowance, so AI cost can never surprise the customer or us. A workspace has one <c>AiAllowancePeriods</c> row per UTC month (opened at the first call of the month with the policy of
/// that moment, which is how the allowance resets). A call <b>reserves</b> its worst-case cost before the model is called: the reservation is one write transaction that holds SQLite's write lock from its first statement (EF Core's SQLite
/// BeginTransaction is BEGIN IMMEDIATE, and the period is inserted with ON CONFLICT DO NOTHING before anything is read), then checks the caps and the per-person rate limit against the row it now has exclusively, then holds the money in a Reserved
/// <c>AiUsageLedger</c> row and in the period's counters. Two calls therefore can never both see the same free money; the database trigger <c>trg_AiPeriod_ReservationWithinAllowance</c> is the backstop for any other
/// writer. The reservation is settled with the real cost in the transaction that records the AI action (<see cref="AiActionService"/>), or released if the call failed. Caps: at the soft cap chat stops and
/// administrators are told; chat also stops at its own share; at the hard cap (included plus credit packs) nothing runs. Each stop is a refusal with a message that says what happens next, recorded in the audit
/// trail and the ledger (never a silent failure, rule 8). Every write here has its audit row in the same transaction (rule 3); money is whole micro-dollars (rule 5); time is the injected clock (rule 4).
/// </summary>
public sealed class AiAllowanceService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IAiAllowancePolicy policy, IOptionsMonitor<AiOptions> options,
    INotifier? notifier = null, ILogger<AiAllowanceService>? log = null, IRealtimePublisher? realtime = null) : ServiceBase(dbf, time, realtime), IAiAllowance
{
    private static string Iso(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    public static string Day(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static (DateOnly Start, DateOnly End) PeriodOf(DateTime now) { var s = new DateOnly(now.Year, now.Month, 1); return (s, s.AddMonths(1)); }
    private static bool IsChat(string feature) => AiFeatures.Find(feature)?.CappedFirst == true;
    private static long Total(AiAllowancePeriods p) => p.IncludedMicroUsd + p.PackMicroUsd;
    private static long Soft(AiAllowancePeriods p) => Total(p) * p.SoftCapPercent / 100;          // integer division, exactly as the trigger computes it
    private static long ChatLimit(AiAllowancePeriods p) => Total(p) * p.ChatSharePercent / 100;

    /// <summary>A reservation still open after this long belongs to a call that died (a crash between reserving and recording). It is longer than the longest legitimate call: all retries, or a batch's full wait.</summary>
    private TimeSpan StaleAfter
    {
        get
        {
            var o = options.CurrentValue;
            var callMinutes = (int)Math.Ceiling(o.Timeout.TotalSeconds * (o.EffectiveRetries + 1) / 60.0) + 1;
            return TimeSpan.FromMinutes(Math.Max(o.BatchMaxWaitMinutes, callMinutes) + 5);
        }
    }

    private sealed record Notice(string Kind, string PeriodId, string Message);

    // ---- reserve ---------------------------------------------------------------------------------------------------------------------

    public async Task<AiReservation> ReserveAsync(string workspaceId, string feature, string? userId, string? model, long estimateMicroUsd, bool batch, CancellationToken ct = default)
    {
        using var _ = WorkspaceScope.Enter(workspaceId);
        var pol = await policy.GetAsync(workspaceId, ct);
        var now = Now;
        var (start, end) = PeriodOf(now);
        var chat = IsChat(feature);
        var estimate = Math.Max(1, estimateMicroUsd);
        var notices = new List<Notice>();
        var stale = 0;

        var result = await RunAsync(async db =>
        {
            var period = await EnsurePeriodAsync(db, workspaceId, start, end, pol, now, ct);   // BEGIN IMMEDIATE already holds the write lock; the first statement is a write too, so nothing is read before it
            // A reservation left by a call that died is given back before anyone is counted against it.
            var cutoff = now - StaleAfter;
            var dead = await db.Set<AiUsageLedger>().Where(l => l.WorkspaceId == workspaceId && l.State == "Reserved" && l.ReservedAt < cutoff).ToListAsync(ct);
            foreach (var l in dead)
            {
                await FreeAsync(db, l, now, "Released", ct);
                Audit(db, null, null, "AiUsage", l.Id, "Released", new { state = "Reserved", reservedMicroUsd = l.ReservedMicroUsd }, new { state = "Released", reason = "The call never reached the audit trail (stale reservation)" });
                stale++;
            }

            async Task<ServiceResult<AiReservation>> Refused(AiCapReason why, string message)
            {
                // The period may have just opened or had stale entries released: those writes stay. The refusal itself is recorded by the audit trail (the ledger row is written with the action).
                NoteCaps(period, 0, notices, now, db);
                await db.SaveChangesAsync(ct);
                return ServiceResult<AiReservation>.Ok(new AiReservation(false, null, period.Id, 0, why, message));
            }

            var ps = Day(period.PeriodStart); var pe = Day(period.PeriodEnd);
            if (userId is not null)
            {
                if (pol.UserPerMinute > 0 && await Started(db, workspaceId, userId, now.AddMinutes(-1), ct) >= pol.UserPerMinute)
                    return await Refused(AiCapReason.RateLimit, AiAllowanceMessages.RateLimit(pol.UserPerMinute, pol.UserPerHour, minuteWindow: true));
                if (pol.UserPerHour > 0 && await Started(db, workspaceId, userId, now.AddHours(-1), ct) >= pol.UserPerHour)
                    return await Refused(AiCapReason.RateLimit, AiAllowanceMessages.RateLimit(pol.UserPerMinute, pol.UserPerHour, minuteWindow: false));
            }

            var total = Total(period);
            var committed = period.UsedMicroUsd + period.ReservedMicroUsd;
            if (committed + estimate > total)
                return await Refused(AiCapReason.HardCap, committed >= total ? AiAllowanceMessages.HardCap(ps, pe) : AiAllowanceMessages.NotEnough(ps, pe, AiCredits.ToCredits(total - committed)));
            if (chat)
            {
                var overSoft = committed + estimate > Soft(period);
                var overShare = period.ChatUsedMicroUsd + period.ChatReservedMicroUsd + estimate > ChatLimit(period);
                if (overSoft || overShare) return await Refused(AiCapReason.ChatCap, AiAllowanceMessages.ChatCap(ps, pe, softCap: overSoft));
            }

            var entry = new AiUsageLedger
            {
                WorkspaceId = workspaceId, PeriodId = period.Id, Feature = feature, Class = chat ? "Chat" : "Standard", Model = model is { Length: > 0 and <= 100 } ? model : null, Batch = batch,
                RequestedByUserId = userId, State = "Reserved", ReservedMicroUsd = estimate, ReservedAt = now,
            };
            db.Set<AiUsageLedger>().Add(entry);
            period.ReservedMicroUsd += estimate; if (chat) period.ChatReservedMicroUsd += estimate;
            period.LastChangedAt = now; period.Version++;
            Audit(db, userId is null ? null : new Actor(userId), null, "AiUsage", entry.Id, "Reserved", null, new { feature, entry.Class, model = entry.Model, batch, reservedMicroUsd = estimate, period = ps });
            NoteCaps(period, 0, notices, now, db);
            await db.SaveChangesAsync(ct);
            return ServiceResult<AiReservation>.Ok(new AiReservation(true, entry.Id, period.Id, estimate, null, null));
        }, ct);

        if (!result.IsOk) throw new InvalidOperationException("The AI allowance could not be reserved: " + string.Join("; ", result.Failures.Select(f => f.Message)));
        if (stale > 0) log?.LogWarning("Released {Count} stale AI reservation(s) in workspace {Workspace}: a call died between reserving and recording", stale, workspaceId);
        foreach (var n in notices) await NotifyAdminsAsync(workspaceId, n, ct);
        return result.Value!;
    }

    private static Task<int> Started(ReleaseDbContext db, string workspaceId, string userId, DateTime since, CancellationToken ct) =>
        db.Set<AiUsageLedger>().CountAsync(l => l.WorkspaceId == workspaceId && l.RequestedByUserId == userId && (l.State == "Reserved" || l.State == "Settled") && l.ReservedAt > since, ct);

    /// <summary>Opens the month if it is new (one audit row) and returns the tracked row. The INSERT is first so nothing is read before the first write.</summary>
    private async Task<AiAllowancePeriods> EnsurePeriodAsync(ReleaseDbContext db, string workspaceId, DateOnly start, DateOnly end, AiAllowancePolicy pol, DateTime now, CancellationToken ct)
    {
        var id = Ids.New();
        var created = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO AiAllowancePeriods (Id, WorkspaceId, PeriodStart, PeriodEnd, IncludedMicroUsd, SoftCapPercent, ChatSharePercent, OpenedAt, LastChangedAt)
             VALUES ({id}, {workspaceId}, {Day(start)}, {Day(end)}, {pol.IncludedMicroUsd}, {pol.SoftCapPercent}, {pol.ChatSharePercent}, {Iso(now)}, {Iso(now)})
             ON CONFLICT(WorkspaceId, PeriodStart) DO NOTHING
             """, ct) == 1;
        var period = await db.Set<AiAllowancePeriods>().SingleAsync(p => p.WorkspaceId == workspaceId && p.PeriodStart == start, ct);
        if (created)
            Audit(db, null, null, "AiAllowance", period.Id, "PeriodOpened", null, new { periodStart = Day(start), periodEnd = Day(end), includedMicroUsd = pol.IncludedMicroUsd, pol.SoftCapPercent, pol.ChatSharePercent });
        return period;
    }

    /// <summary>Gives a Reserved entry's money back to its period and closes it (Released). The caller writes the audit row.</summary>
    private static async Task FreeAsync(ReleaseDbContext db, AiUsageLedger l, DateTime now, string state, CancellationToken ct)
    {
        var p = await db.Set<AiAllowancePeriods>().SingleAsync(x => x.Id == l.PeriodId, ct);
        p.ReservedMicroUsd -= l.ReservedMicroUsd; if (l.Class == "Chat") p.ChatReservedMicroUsd -= l.ReservedMicroUsd;
        p.LastChangedAt = now; p.Version++;
        l.State = state; l.SettledAt = now; l.LastChangedAt = now; l.Version++;
    }

    /// <summary>Marks the soft and hard caps as announced, once a month each, and queues the notice for administrators after the commit. <paramref name="heldNow"/> is money this call adds.</summary>
    private void NoteCaps(AiAllowancePeriods p, long heldNow, List<Notice> notices, DateTime now, ReleaseDbContext db)
    {
        var total = Total(p);
        var committed = p.UsedMicroUsd + p.ReservedMicroUsd + heldNow;
        var ps = Day(p.PeriodStart); var pe = Day(p.PeriodEnd);
        if (p.HardCapNotifiedAt is null && p.UsedMicroUsd >= total)
        {
            p.HardCapNotifiedAt = now; p.LastChangedAt = now; p.Version++;
            Audit(db, null, null, "AiAllowance", p.Id, "HardCapReached", null, new { usedMicroUsd = p.UsedMicroUsd, totalMicroUsd = total, period = ps });
            notices.Add(new Notice("HardCap", p.Id, AiAllowanceMessages.HardCapNotice(ps, pe)));
        }
        if (p.SoftCapNotifiedAt is null && total > 0 && committed * 100 >= total * p.SoftCapPercent)
        {
            p.SoftCapNotifiedAt = now; p.LastChangedAt = now; p.Version++;
            Audit(db, null, null, "AiAllowance", p.Id, "SoftCapReached", null, new { committedMicroUsd = committed, totalMicroUsd = total, p.SoftCapPercent, period = ps });
            notices.Add(new Notice("SoftCap", p.Id, AiAllowanceMessages.SoftCapNotice(p.SoftCapPercent, ps, pe)));
        }
    }

    private async Task NotifyAdminsAsync(string workspaceId, Notice n, CancellationToken ct)
    {
        log?.LogWarning("AI allowance {Kind} in workspace {Workspace}: {Message}", n.Kind, workspaceId, n.Message);   // logged whatever happens to the in-app copy
        if (notifier is null) return;
        try
        {
            using var _ = WorkspaceScope.Enter(workspaceId);
            await using var db = await OpenAsync(ct);
            var who = await Memberships.ActiveWithRole(db, Roles.RTE, Roles.ReleaseManager).Select(u => u.Id).ToListAsync(ct);
            foreach (var u in who) await notifier.NotifyAsync(new NotificationRequest(u, "AiAllowance", "AiAllowance", n.PeriodId, n.Message, 0), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.LogError(ex, "The AI allowance notice for workspace {Workspace} ({Kind}) was recorded in the audit trail, but its in-app copy failed", workspaceId, n.Kind);   // the audit row and the log carry it
        }
    }

    // ---- release ---------------------------------------------------------------------------------------------------------------------

    public async Task ReleaseAsync(string workspaceId, string entryId, string reason, CancellationToken ct = default)
    {
        using var _ = WorkspaceScope.Enter(workspaceId);
        var result = await RunAsync(async db =>
        {
            var l = await db.Set<AiUsageLedger>().SingleOrDefaultAsync(x => x.Id == entryId && x.WorkspaceId == workspaceId, ct);
            if (l is null || l.State != "Reserved") return ServiceResult<bool>.Ok(false);   // already settled or released: nothing to give back
            await FreeAsync(db, l, Now, "Released", ct);
            Audit(db, null, null, "AiUsage", l.Id, "Released", new { state = "Reserved", reservedMicroUsd = l.ReservedMicroUsd }, new { state = "Released", reason = reason.Length > 200 ? reason[..200] : reason });
            await db.SaveChangesAsync(ct);
            return ServiceResult<bool>.Ok(true);
        }, ct);
        if (!result.IsOk) throw new InvalidOperationException("The AI reservation could not be released: " + string.Join("; ", result.Failures.Select(f => f.Message)));
    }

    // ---- reads -----------------------------------------------------------------------------------------------------------------------

    public async Task<AiCapCheck> CheckAsync(string workspaceId, string feature, CancellationToken ct = default)
    {
        using var _ = WorkspaceScope.Enter(workspaceId);
        var pol = await policy.GetAsync(workspaceId, ct);
        var (start, end) = PeriodOf(Now);
        await using var db = await OpenAsync(ct);
        var p = await db.Set<AiAllowancePeriods>().AsNoTracking().SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.PeriodStart == start, ct)
            ?? new AiAllowancePeriods { IncludedMicroUsd = pol.IncludedMicroUsd, SoftCapPercent = pol.SoftCapPercent, ChatSharePercent = pol.ChatSharePercent, PeriodStart = start, PeriodEnd = end };
        var ps = Day(start); var pe = Day(end);
        var committed = p.UsedMicroUsd + p.ReservedMicroUsd;
        if (committed >= Total(p)) return new AiCapCheck(false, AiCapReason.HardCap, AiAllowanceMessages.HardCap(ps, pe));
        if (IsChat(feature))
        {
            var overSoft = committed >= Soft(p);
            if (overSoft || p.ChatUsedMicroUsd + p.ChatReservedMicroUsd >= ChatLimit(p)) return new AiCapCheck(false, AiCapReason.ChatCap, AiAllowanceMessages.ChatCap(ps, pe, overSoft));
        }
        return new AiCapCheck(true, null, null);
    }

    public async Task<AiAllowanceView> GetAsync(string workspaceId, CancellationToken ct = default)
    {
        using var _ = WorkspaceScope.Enter(workspaceId);
        var pol = await policy.GetAsync(workspaceId, ct);
        var (start, end) = PeriodOf(Now);
        await using var db = await OpenAsync(ct);
        var row = await db.Set<AiAllowancePeriods>().AsNoTracking().SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.PeriodStart == start, ct);
        var p = row ?? new AiAllowancePeriods { IncludedMicroUsd = pol.IncludedMicroUsd, SoftCapPercent = pol.SoftCapPercent, ChatSharePercent = pol.ChatSharePercent, PeriodStart = start, PeriodEnd = end };
        var ps = Day(start); var pe = Day(end);
        var total = Total(p);
        var committed = p.UsedMicroUsd + p.ReservedMicroUsd;
        var hard = committed >= total;
        var soft = !hard && committed >= Soft(p);
        var chatLimit = ChatLimit(p);
        var chatOk = !hard && !soft && p.ChatUsedMicroUsd + p.ChatReservedMicroUsd < chatLimit;
        var level = hard ? "Hard" : !chatOk ? "Soft" : "Ok";
        var message = hard ? AiAllowanceMessages.HardCap(ps, pe) : soft ? AiAllowanceMessages.ChatCap(ps, pe, softCap: true) : !chatOk ? AiAllowanceMessages.ChatCap(ps, pe, softCap: false) : null;
        var percent = total <= 0 ? 100 : (int)Math.Min(100, committed * 100 / total);

        var features = new List<AiAllowanceFeatureUsage>();
        var users = new List<AiAllowanceUserUsage>();
        var packs = new List<AiCreditPackView>();
        if (row is not null)
        {
            var byFeature = await db.Set<AiUsageLedger>().AsNoTracking().Where(l => l.PeriodId == row.Id).GroupBy(l => l.Feature)
                .Select(g => new { Feature = g.Key, Calls = g.Count(l => l.State == "Settled"), Refused = g.Count(l => l.State == "Refused"), Cost = g.Sum(l => l.CostMicroUsd), In = g.Sum(l => (long)l.InputTokens), Out = g.Sum(l => (long)l.OutputTokens) })
                .ToListAsync(ct);
            foreach (var f in AiFeatures.All)
            {
                var u = byFeature.FirstOrDefault(x => x.Feature == f.Key);
                features.Add(new AiAllowanceFeatureUsage(f.Key, f.Name, f.CappedFirst, u?.Calls ?? 0, u?.Refused ?? 0, u?.Cost ?? 0, u?.In ?? 0, u?.Out ?? 0));
            }
            var byUser = await db.Set<AiUsageLedger>().AsNoTracking().Where(l => l.PeriodId == row.Id && l.RequestedByUserId != null && l.State == "Settled").GroupBy(l => l.RequestedByUserId!)
                .Select(g => new { UserId = g.Key, Calls = g.Count(), Cost = g.Sum(l => l.CostMicroUsd) }).OrderByDescending(x => x.Cost).Take(10).ToListAsync(ct);
            var names = await UserNamesAsync(db, byUser.Select(x => x.UserId), ct);
            users = [.. byUser.Select(x => new AiAllowanceUserUsage(x.UserId, names.GetValueOrDefault(x.UserId, x.UserId), x.Calls, x.Cost))];
            var packRows = await db.Set<AiCreditPacks>().AsNoTracking().Where(k => k.PeriodId == row.Id).OrderBy(k => k.GrantedAt).ThenBy(k => k.Id).ToListAsync(ct);
            var pn = await UserNamesAsync(db, packRows.Select(k => k.GrantedByUserId), ct);
            packs = [.. packRows.Select(k => PackView(k, k.GrantedByUserId is null ? null : pn.GetValueOrDefault(k.GrantedByUserId, k.GrantedByUserId)))];
        }
        else
            features = [.. AiFeatures.All.Select(f => new AiAllowanceFeatureUsage(f.Key, f.Name, f.CappedFirst, 0, 0, 0, 0, 0))];

        return new AiAllowanceView(ps, pe, AiAllowanceMessages.Date(pe), p.IncludedMicroUsd, p.PackMicroUsd, total, p.UsedMicroUsd, p.ReservedMicroUsd, Math.Max(0, total - committed), percent, p.SoftCapPercent, p.ChatSharePercent,
            p.ChatUsedMicroUsd, chatLimit, chatOk, pol.UserPerMinute, pol.UserPerHour, level, message, AiCredits.MicroUsdPerCredit, features, users, packs, row is not null);
    }

    public async Task<IReadOnlyList<AiLedgerEntry>> LedgerAsync(string workspaceId, int limit = 100, CancellationToken ct = default)
    {
        using var _ = WorkspaceScope.Enter(workspaceId);
        await using var db = await OpenAsync(ct);
        var rows = await db.Set<AiUsageLedger>().AsNoTracking().Where(l => l.WorkspaceId == workspaceId).OrderByDescending(l => l.ReservedAt).ThenByDescending(l => l.Id).Take(Math.Clamp(limit, 1, 500)).ToListAsync(ct);
        var names = await UserNamesAsync(db, rows.Select(r => r.RequestedByUserId), ct);
        return [.. rows.Select(r => new AiLedgerEntry(r.Id, r.Feature, AiFeatures.Find(r.Feature)?.Name ?? r.Feature, r.Class, r.Model, r.Batch, r.State, r.RefusalReason, r.ReservedMicroUsd, r.CostMicroUsd, r.InputTokens, r.OutputTokens,
            r.CacheReadTokens, r.CacheWriteTokens, r.RequestedByUserId is null ? null : new AiPersonRef(r.RequestedByUserId, names.GetValueOrDefault(r.RequestedByUserId, r.RequestedByUserId)), r.ReservedAt, r.SettledAt, r.AiActionId))];
    }

    private static async Task<Dictionary<string, string>> UserNamesAsync(ReleaseDbContext db, IEnumerable<string?> ids, CancellationToken ct)
    {
        var list = ids.Where(i => i is not null).Select(i => i!).Distinct().ToList();
        return list.Count == 0 ? [] : await db.Set<Users>().AsNoTracking().Where(u => list.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
    }

    private static AiCreditPackView PackView(AiCreditPacks k, string? by) => new(k.Id, k.MicroUsd, AiCredits.ToCredits(k.MicroUsd), k.Source, k.Reference, k.Note, by, k.GrantedAt);

    // ---- credit packs ----------------------------------------------------------------------------------------------------------------

    public async Task<ServiceResult<AiCreditPackView>> GrantPackAsync(string workspaceId, long microUsd, string source, string? reference, string? note, Actor? actor, CancellationToken ct = default)
    {
        static ServiceResult<AiCreditPackView> Fail(string message) => ServiceResult<AiCreditPackView>.Fail(new GuardFailure(AiGuards.AiCreditPack, message));
        if (microUsd <= 0) return Fail("A credit pack must add at least one credit");
        if (source is not ("Manual" or "Billing")) return Fail("The source of a credit pack is Manual or Billing");
        if (source == "Billing" && string.IsNullOrWhiteSpace(reference)) return Fail("A pack granted by billing needs the billing reference, so the same purchase is never granted twice");
        if (source == "Manual" && actor is null) return Fail("A manual credit pack needs the person who granted it");
        if (reference is { Length: > 100 }) return Fail("The reference is at most 100 characters");
        if (note is { Length: > 500 }) return Fail("The note is at most 500 characters");
        reference = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim();

        using var _ = WorkspaceScope.Enter(workspaceId);
        var pol = await policy.GetAsync(workspaceId, ct);
        var now = Now;
        var (start, end) = PeriodOf(now);
        return await RunAsync(async db =>
        {
            var period = await EnsurePeriodAsync(db, workspaceId, start, end, pol, now, ct);
            if (reference is not null)
            {
                var existing = await db.Set<AiCreditPacks>().AsNoTracking().SingleOrDefaultAsync(k => k.WorkspaceId == workspaceId && k.Source == source && k.Reference == reference, ct);
                if (existing is not null) { await db.SaveChangesAsync(ct); return ServiceResult<AiCreditPackView>.Ok(PackView(existing, null)); }   // the same purchase again: it is the grant it was
            }
            var pack = new AiCreditPacks { WorkspaceId = workspaceId, PeriodId = period.Id, MicroUsd = microUsd, Source = source, Reference = reference, Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(), GrantedByUserId = actor?.UserId, GrantedAt = now };
            db.Set<AiCreditPacks>().Add(pack);
            period.PackMicroUsd += microUsd; period.LastChangedAt = now; period.Version++;
            var total = Total(period); var committed = period.UsedMicroUsd + period.ReservedMicroUsd;
            if (committed < total) period.HardCapNotifiedAt = null;                       // a month that has room again may announce running out again
            if (committed * 100 < total * period.SoftCapPercent) period.SoftCapNotifiedAt = null;
            Audit(db, actor, null, "AiCreditPack", pack.Id, "Granted", null, new { microUsd, credits = AiCredits.ToCredits(microUsd), source, reference, period = Day(start), note = pack.Note });
            await db.SaveChangesAsync(ct);
            return ServiceResult<AiCreditPackView>.Ok(PackView(pack, null));
        }, ct);
    }
}
