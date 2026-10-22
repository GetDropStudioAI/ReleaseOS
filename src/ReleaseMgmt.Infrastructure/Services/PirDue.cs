using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Reminders;

namespace ReleaseMgmt.Infrastructure.Services;

/// <summary>
/// REOS-87: a review after a rollback or an abort. Ending a Live run RolledBack, or aborting an Executing train, requires the train's PIR
/// (created in the same transaction when there is none yet), and such a PIR is due <c>Pir:DueBusinessDays</c> business days (default 1,
/// "a retrospective within 24 business hours") after it was created, counted in Display:TimeZone. Created = the PIR's first audit row, so no
/// column is stored (proposal P3a). PIRs created for a close code or by hand keep no due date, as before (Q-0870).
/// </summary>
public static class PirDue
{
    public const string RollbackPerformed = "RollbackPerformed", Aborted = "Aborted";
    public const int MaxBusinessDays = 30;

    public static bool HasDueDate(string requiredReason) => requiredReason is RollbackPerformed or Aborted;

    /// <summary>Config <c>Pir:DueBusinessDays</c> (0–30, default 1). A value that is not a whole number in range is an error, never a silent default (rule 8).</summary>
    public static int BusinessDaysFrom(IConfiguration? config)
    {
        var raw = config?["Pir:DueBusinessDays"];
        if (string.IsNullOrWhiteSpace(raw)) return 1;
        if (int.TryParse(raw, out var n) && n is >= 0 and <= MaxBusinessDays) return n;
        throw new InvalidOperationException($"Pir:DueBusinessDays '{raw}' must be a whole number from 0 to {MaxBusinessDays}");
    }

    /// <summary>Adds the train's PIR with <paramref name="reason"/> unless it already has one (UNIQUE per train); the caller audits it in the same transaction.</summary>
    public static async Task<PostImplementationReviews?> CreateIfMissingAsync(ReleaseDbContext db, string trainId, string reason, CancellationToken ct)
    {
        if (db.Set<PostImplementationReviews>().Local.Any(p => p.ReleaseTrainId == trainId)) return null;
        if (await db.Set<PostImplementationReviews>().AnyAsync(p => p.ReleaseTrainId == trainId, ct)) return null;
        var p = new PostImplementationReviews { Id = Ids.New(), ReleaseTrainId = trainId, RequiredReason = reason, Status = "Required" };
        db.Set<PostImplementationReviews>().Add(p);
        return p;
    }

    /// <summary>Due dates of the given PIRs that carry one: the local date of each PIR's first audit row plus <paramref name="days"/> business days.</summary>
    public static async Task<Dictionary<string, DateOnly>> DueDatesAsync(ReleaseDbContext db, IReadOnlyCollection<PostImplementationReviews> pirs, DisplayClock clock, int days, CancellationToken ct)
    {
        var ids = pirs.Where(p => HasDueDate(p.RequiredReason)).Select(p => p.Id).ToList();
        if (ids.Count == 0) return [];
        var created = (await db.Set<AuditEvents>().AsNoTracking().Where(a => a.EntityType == "PostImplementationReview" && ids.Contains(a.EntityId))
                .Select(a => new { a.EntityId, a.OccurredAt }).ToListAsync(ct))
            .GroupBy(a => a.EntityId).ToDictionary(g => g.Key, g => g.Min(a => a.OccurredAt));
        var holidays = (await db.Set<Holidays>().AsNoTracking().Select(h => h.Day).ToListAsync(ct)).ToHashSet();
        return created.ToDictionary(c => c.Key, c => BusinessDays.AddBusinessDays(clock.LocalDate(c.Value), days, holidays));
    }

    /// <summary>Overdue = still Required or Scheduled after the due day has ended in the display zone (Scheduled does not move the date).</summary>
    public static bool IsOverdue(PostImplementationReviews p, DateOnly? dueOn, DateOnly today) =>
        dueOn is { } d && p.Status is "Required" or "Scheduled" && today > d;
}
