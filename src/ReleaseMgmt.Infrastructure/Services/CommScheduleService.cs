using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Reminders;

namespace ReleaseMgmt.Infrastructure.Services;

public sealed record SeedScheduleInput(string? TemplateId);

/// <summary>
/// One line of a train's T-minus schedule. <see cref="TMinus"/> is business days before the target date (0 on the day, negative after), <see cref="Label"/> its text
/// ("T−7", "T0", "T+5"). <see cref="State"/> is Sent, SentLate, Overdue, Ready (due within 24 h) or Scheduled; <see cref="Late"/> is SentAt after DueAt,
/// which is exactly what the timeliness KPI counts as not on time.
/// </summary>
public sealed record CommScheduleItemView(string Id, string ReleaseTrainId, string CommTemplateId, string Name, string TemplateType, string Audience, string SubjectLine,
    DateTime DueAt, DateTime? SentAt, string? DispatchId, int TMinus, string Label, string State, bool Late, int LateMinutes, int Version);

/// <summary>
/// The T-minus schedule (REOS-43): seeded from a train template's <c>TemplateCommSchedule</c>, then SentAt recorded against DueAt.
/// DueAt is computed once at seeding from the target date, business days and holidays (D9) and the deployment window (see <see cref="CommScheduling"/>);
/// moving the target date later does not move it (Q-043e).
/// </summary>
public sealed class CommScheduleService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, DisplayClock clock, IRealtimePublisher? realtime = null) : ServiceBase(dbf, time, realtime)
{
    private CommScheduling.Options Options => CommScheduling.Options.Default(clock.Zone);

    public async Task<List<CommScheduleItemView>?> ListAsync(string trainId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        return await ViewsAsync(db, trainId, ct);
    }

    /// <summary>Creates the train's schedule from the template's plan. Reuses the train's existing copy of a library message, else copies it in.</summary>
    public Task<ServiceResult<List<CommScheduleItemView>>> SeedAsync(string trainId, SeedScheduleInput input, Actor actor, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var train = await db.Set<ReleaseTrains>().SingleOrDefaultAsync(t => t.Id == trainId, ct);
            if (train is null) return ServiceResult<List<CommScheduleItemView>>.NotFound("train");
            if (string.IsNullOrWhiteSpace(input.TemplateId)) return ServiceResult<List<CommScheduleItemView>>.Fail(new GuardFailure(CommGuards.CommTemplateInvalid, "Choose the train template whose T-minus plan to use"));
            var template = await db.Set<TrainTemplates>().AsNoTracking().SingleOrDefaultAsync(t => t.Id == input.TemplateId, ct);
            if (template is null) return ServiceResult<List<CommScheduleItemView>>.NotFound("train template");

            var f = new List<GuardFailure>();
            if (train.CurrentStatus is "Complete" or "Aborted") f.Add(new(Guards.TrainClosed, $"The train is {train.CurrentStatus}; a schedule cannot be added"));
            if (template.Status == "Retired") f.Add(new(CommGuards.CommTemplateRetired, $"Template \"{template.Name}\" is retired; use a current template"));
            if (await db.Set<CommSchedule>().AnyAsync(s => s.ReleaseTrainId == trainId, ct))
                f.Add(new(CommGuards.CommScheduleExists, "This train already has a T-minus schedule"));
            var plan = await db.Set<TemplateCommSchedule>().AsNoTracking().Where(p => p.TemplateId == template.Id).ToListAsync(ct);
            if (plan.Count == 0) f.Add(new(CommGuards.CommPlanEmpty, $"Template \"{template.Name}\" has no T-minus plan to seed from"));
            if (f.Count > 0) return ServiceResult<List<CommScheduleItemView>>.Fail(f);

            var window = await db.Set<DeploymentWindows>().AsNoTracking().SingleOrDefaultAsync(w => w.ReleaseTrainId == trainId, ct);
            var (created, copied) = await AddRowsAsync(db, train, plan, window, Options, ct);
            Audit(db, actor, trainId, "CommSchedule", trainId, "Seed", null, new { templateId = template.Id, template = template.Name, targetDate = train.TargetReleaseDate.ToString("yyyy-MM-dd"), items = created, copiedMessages = copied });
            await db.SaveChangesAsync(ct);
            return ServiceResult<List<CommScheduleItemView>>.Ok(await ViewsAsync(db, trainId, ct) ?? []);
        }, ct);

    /// <summary>
    /// Records that the message went out: SentAt from the service clock, compared with DueAt. <paramref name="dispatchId"/> links the CommDispatches row when the
    /// caller (REOS-45 dispatch) has one; a manual "mark as sent" passes none. Refused when already sent (SentAt is history, not a field to overwrite).
    /// </summary>
    public Task<ServiceResult<CommScheduleItemView>> MarkSentAsync(string scheduleId, Actor actor, int? expectedVersion, string? dispatchId = null, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var row = await db.Set<CommSchedule>().SingleOrDefaultAsync(s => s.Id == scheduleId, ct);
            if (row is null) return ServiceResult<CommScheduleItemView>.NotFound("schedule item");
            if (VersionMismatch(expectedVersion, row.Version)) return ServiceResult<CommScheduleItemView>.Conflict((await ViewsAsync(db, row.ReleaseTrainId, ct))!.Single(v => v.Id == scheduleId));
            if (row.SentAt is not null)
                return ServiceResult<CommScheduleItemView>.Fail(new GuardFailure(CommGuards.CommAlreadySent, $"This message was already sent at {row.SentAt:yyyy-MM-dd HH:mm}Z"));
            var now = Now;
            row.SentAt = now; row.DispatchId = dispatchId; row.Version++;
            Audit(db, actor, row.ReleaseTrainId, "CommSchedule", row.Id, "MarkSent",
                new { row.DueAt, sentAt = (DateTime?)null }, new { row.DueAt, row.SentAt, lateMinutes = CommScheduling.LateMinutes(row.DueAt, now), row.DispatchId });
            await db.SaveChangesAsync(ct);
            return ServiceResult<CommScheduleItemView>.Ok((await ViewsAsync(db, row.ReleaseTrainId, ct))!.Single(v => v.Id == scheduleId));
        }, ct);

    /// <summary>
    /// Adds one <c>CommSchedule</c> row per plan line (and the train's copy of each library message it needs, reusing an existing copy) to the caller's
    /// transaction. Shared by <see cref="SeedAsync"/> and train creation from a template (REOS-80), so both date the schedule the same way. The caller audits.
    /// </summary>
    internal static async Task<(List<object> Created, List<object> Copied)> AddRowsAsync(ReleaseDbContext db, ReleaseTrains train, IReadOnlyList<TemplateCommSchedule> plan,
        DeploymentWindows? window, CommScheduling.Options options, CancellationToken ct)
    {
        var trainId = train.Id;
        var library = await db.Set<CommTemplateLibrary>().AsNoTracking().ToDictionaryAsync(l => l.Id, ct);
        var holidays = (await db.Set<Holidays>().AsNoTracking().Select(h => h.Day).ToListAsync(ct)).ToHashSet();
        var copies = await db.Set<CommTemplates>().Where(c => c.ReleaseTrainId == trainId && c.LibraryTemplateId != null).ToListAsync(ct);

        var created = new List<object>(); var copied = new List<object>();
        foreach (var p in plan.OrderBy(p => p.OffsetDays).ThenBy(p => library[p.LibraryTemplateId].Name, StringComparer.OrdinalIgnoreCase))
        {
            var lib = library[p.LibraryTemplateId];
            var copy = copies.FirstOrDefault(c => c.LibraryTemplateId == lib.Id);
            if (copy is null)
            {
                copy = CommLibraryService.CopyOf(trainId, lib);
                db.Set<CommTemplates>().Add(copy); copies.Add(copy);
                copied.Add(new { copy.Id, fromLibrary = lib.Id, lib.Name });
                await db.SaveChangesAsync(ct);   // the copy first: the schedule row references it
            }
            var due = CommScheduling.DueAtUtc(train.TargetReleaseDate, p.OffsetDays, lib.TemplateType, window?.StartsAt, window?.EndsAt, holidays, options);
            var row = new CommSchedule { Id = Ids.New(), ReleaseTrainId = trainId, CommTemplateId = copy.Id, DueAt = Truncate(due) };
            db.Set<CommSchedule>().Add(row);
            created.Add(new { row.Id, offsetDays = p.OffsetDays, message = lib.Name, dueAt = row.DueAt });
        }
        return (created, copied);
    }

    private static DateTime Truncate(DateTime t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

    private async Task<List<CommScheduleItemView>?> ViewsAsync(ReleaseDbContext db, string trainId, CancellationToken ct)
    {
        var train = await db.Set<ReleaseTrains>().AsNoTracking().SingleOrDefaultAsync(t => t.Id == trainId, ct);
        if (train is null) return null;
        var holidays = (await db.Set<Holidays>().AsNoTracking().Select(h => h.Day).ToListAsync(ct)).ToHashSet();
        var rows = await db.Set<CommSchedule>().AsNoTracking().Where(s => s.ReleaseTrainId == trainId).ToListAsync(ct);
        var ids = rows.Select(r => r.CommTemplateId).Distinct().ToList();
        var templates = await db.Set<CommTemplates>().AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var names = await db.Set<CommTemplateLibrary>().AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var now = Now;
        return [.. rows.OrderBy(r => r.DueAt).ThenBy(r => r.Id).Select(r =>
        {
            var t = templates[r.CommTemplateId];
            var tm = CommScheduling.TMinus(r.DueAt, train.TargetReleaseDate, holidays, clock.Zone);
            var late = r.SentAt is { } s && s > r.DueAt;
            return new CommScheduleItemView(r.Id, r.ReleaseTrainId, r.CommTemplateId, t.LibraryTemplateId is not null && names.TryGetValue(t.LibraryTemplateId, out var n) ? n : t.TemplateType,
                t.TemplateType, t.Audience, t.SubjectLine, r.DueAt, r.SentAt, r.DispatchId, tm, CommScheduling.Label(tm), CommScheduling.State(r.DueAt, r.SentAt, now), late, CommScheduling.LateMinutes(r.DueAt, r.SentAt), r.Version);
        })];
    }
}
