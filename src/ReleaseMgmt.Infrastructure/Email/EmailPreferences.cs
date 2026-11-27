using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Infrastructure.Email;

public sealed record PreferenceView(string Category, string Label, string Channel, string Mode, string DefaultMode, bool IsDefault, int Version);

/// <summary>
/// REOS-167: a person's own channel choices, per kind of notification (approval requested, decision made, escalation level 1 and 2): email (off, immediately, in the daily digest) and the team webhook (off, immediately).
/// The in-app inbox is always on and has no preference: escalation and My work depend on it. A person changes only their own; each change is one transaction with a Version bump (If-Match) and one audit row (rule 3).
/// No row means the default (email immediately, webhook off), so a new person is mailed from the first approval.
/// </summary>
public sealed class EmailPreferenceService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IRealtimePublisher? realtime = null) : ServiceBase(dbf, time, realtime)
{
    private const string Entity = "NotificationPreference";

    public async Task<List<PreferenceView>> ListAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var rows = await db.Set<NotificationPreferences>().AsNoTracking().Where(p => p.UserId == userId).ToListAsync(ct);
        var all = new List<PreferenceView>();
        foreach (var cat in EmailCategories.Choosable)
            foreach (var ch in EmailCategories.Channels)
                all.Add(View(cat, ch, rows.SingleOrDefault(r => r.Category == cat && r.Channel == ch)));
        return all;
    }

    private static PreferenceView View(string category, string channel, NotificationPreferences? row) =>
        new(category, EmailCategories.Label(category), channel, row?.Mode ?? EmailCategories.DefaultMode(channel), EmailCategories.DefaultMode(channel), row is null, row?.Version ?? 0);

    /// <param name="expectedVersion">The version the person's screen showed (0 = no row yet). A different one is a 409 with the current choice; null skips the check.</param>
    public Task<ServiceResult<PreferenceView>> SetAsync(Actor actor, string? category, string? channel, string? mode, int? expectedVersion, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            if (category is null || !EmailCategories.Choosable.Contains(category)) return ServiceResult<PreferenceView>.Fail(new GuardFailure(EmailGuards.PreferenceInvalid, "Choose one of the four kinds of notification", [.. EmailCategories.Choosable]));
            if (channel is null || !EmailCategories.Channels.Contains(channel)) return ServiceResult<PreferenceView>.Fail(new GuardFailure(EmailGuards.PreferenceInvalid, "The channel is Email or Webhook (the inbox is always on)", [.. EmailCategories.Channels]));
            if (mode is null || !EmailCategories.Modes.Contains(mode)) return ServiceResult<PreferenceView>.Fail(new GuardFailure(EmailGuards.PreferenceInvalid, "The choice is Off, Immediate or Daily", [.. EmailCategories.Modes]));
            if (channel == "Webhook" && mode == "Daily") return ServiceResult<PreferenceView>.Fail(new GuardFailure(EmailGuards.PreferenceInvalid, "The team webhook has no daily digest: choose Off or Immediate"));
            if (!await db.Set<Users>().AnyAsync(u => u.Id == actor.UserId && u.IsActive, ct)) return ServiceResult<PreferenceView>.NotFound("User");
            var row = await db.Set<NotificationPreferences>().SingleOrDefaultAsync(p => p.UserId == actor.UserId && p.Category == category && p.Channel == channel, ct);
            if (VersionMismatch(expectedVersion, row?.Version ?? 0)) return ServiceResult<PreferenceView>.Conflict(View(category, channel, row));
            var now = Now;
            if (row is null)
            {
                row = new NotificationPreferences { UserId = actor.UserId, Category = category, Channel = channel, Mode = mode, UpdatedAt = now, WorkspaceId = Memberships.WorkspaceOf(db) };
                db.Set<NotificationPreferences>().Add(row);
                Audit(db, actor, null, Entity, row.Id, "Set", new { mode = EmailCategories.DefaultMode(channel) }, new { actor.UserId, category, channel, mode });
            }
            else if (row.Mode != mode)
            {
                var before = new { mode = row.Mode };
                row.Mode = mode; row.UpdatedAt = now; row.Version++;
                Audit(db, actor, null, Entity, row.Id, "Set", before, new { actor.UserId, category, channel, mode });
            }
            await db.SaveChangesAsync(ct);
            return ServiceResult<PreferenceView>.Ok(View(category, channel, row));
        }, ct);
}
