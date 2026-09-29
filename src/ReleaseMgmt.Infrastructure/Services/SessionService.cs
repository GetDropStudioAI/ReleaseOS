using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

public sealed record SessionState(int SchemaVersion, string? ActiveTrainId, string UiJson, string LastActivityAt, bool Inherited);

/// <summary>
/// Per-tab UI state (PROJECT_SCOPE 5.4): a reopened tab restores its own state, a new tab starts from the user's most recent one.
/// UI state is not a domain record, so autosaves carry no Version and write no AuditEvents row (docs/QUESTIONS.md Q-009).
/// </summary>
public sealed class SessionService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time) : ServiceBase(dbf, time)
{
    public const int MaxBytes = 262144;   // the CHECK in schema.sql; refused here first so the client gets a readable 422, not a DbRule

    private readonly IDbContextFactory<ReleaseDbContext> _dbf = dbf;

    public async Task<SessionState?> GetAsync(string userId, string clientId, CancellationToken ct = default)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var own = await db.Set<UserSessionState>().AsNoTracking().SingleOrDefaultAsync(s => s.UserId == userId && s.ClientId == clientId, ct);
        if (own is not null) return Map(own, inherited: false);
        var latest = (await db.Set<UserSessionState>().AsNoTracking().Where(s => s.UserId == userId).ToListAsync(ct)).OrderByDescending(s => s.LastActivityAt).FirstOrDefault();
        return latest is null ? null : Map(latest, inherited: true);
    }

    public Task<ServiceResult<SessionState>> PutAsync(string userId, string clientId, int schemaVersion, string? activeTrainId, string uiJson, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            if (Encoding.UTF8.GetByteCount(uiJson) > MaxBytes)
                return ServiceResult<SessionState>.Fail(new GuardFailure(Guards.SessionStateTooLarge, $"UI state is over the {MaxBytes / 1024} KB limit; shorten the longest draft"));
            try { using var _ = JsonDocument.Parse(uiJson); }
            catch (JsonException) { return ServiceResult<SessionState>.Fail(new GuardFailure(Guards.InvalidSessionState, "UI state is not valid JSON")); }

            // A train that no longer exists (deleted since the tab last saved) is dropped rather than failing the autosave.
            if (activeTrainId is not null && !await db.Set<ReleaseTrains>().AnyAsync(t => t.Id == activeTrainId, ct)) activeTrainId = null;
            var row = await db.Set<UserSessionState>().SingleOrDefaultAsync(s => s.UserId == userId && s.ClientId == clientId, ct);
            if (row is null) { row = new UserSessionState { UserId = userId, ClientId = clientId }; db.Set<UserSessionState>().Add(row); }
            row.ActiveTrainId = activeTrainId; row.SchemaVersion = schemaVersion; row.UIStateJson = uiJson; row.LastActivityAt = Now;
            await db.SaveChangesAsync(ct);
            return ServiceResult<SessionState>.Ok(Map(row, false));
        }, ct);

    /// <summary>SessionStateJanitor: drop client states idle longer than <paramref name="idle"/> (30 days by default in the host).</summary>
    public async Task<int> PurgeIdleAsync(TimeSpan idle, CancellationToken ct = default)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var cutoff = Now - idle;
        var stale = (await db.Set<UserSessionState>().ToListAsync(ct)).Where(s => s.LastActivityAt < cutoff).ToList();
        db.Set<UserSessionState>().RemoveRange(stale);
        await db.SaveChangesAsync(ct);
        return stale.Count;
    }

    private static SessionState Map(UserSessionState s, bool inherited) =>
        new(s.SchemaVersion, s.ActiveTrainId, s.UIStateJson, s.LastActivityAt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), inherited);
}
