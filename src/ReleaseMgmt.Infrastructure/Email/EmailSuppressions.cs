using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Reminders;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Infrastructure.Email;

public sealed record SuppressionView(string Id, string Address, string Reason, string? Detail, string CreatedAt, string? CreatedBy, int Version);

/// <summary>
/// REOS-167: the suppression list. An address lands here when the provider says it can never be reached (a hard bounce) or that the person complained, or when an administrator adds it by hand; nothing is mailed to it
/// again from the workspace until an administrator removes the entry (a confirmation dialog on the screen, one audit row). Every add and remove is one transaction with its audit row (rule 3). Removing an entry also resolves the
/// "undeliverable address" alert raised for it.
/// </summary>
public sealed class EmailSuppressionService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, SyncAlertWriter alerts, IRealtimePublisher? realtime = null) : ServiceBase(dbf, time, realtime)
{
    private const string Entity = "EmailSuppression";

    public static string AlertKey(string address) => "email:" + address.ToLowerInvariant();

    private static string Iso(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    public async Task<List<SuppressionView>> ListAsync(int take = 200, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var rows = await db.Set<EmailSuppressions>().AsNoTracking().OrderByDescending(s => s.CreatedAt).ThenBy(s => s.Address).Take(Math.Clamp(take, 1, 1000)).ToListAsync(ct);
        var ids = rows.Where(r => r.CreatedByUserId != null).Select(r => r.CreatedByUserId!).Distinct().ToList();
        var names = await db.Set<Users>().AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        return [.. rows.Select(r => new SuppressionView(r.Id, r.Address, r.Reason, r.Detail, Iso(r.CreatedAt), r.CreatedByUserId is { } u ? names.GetValueOrDefault(u) : null, r.Version))];
    }

    public Task<ServiceResult<SuppressionView>> AddAsync(string? address, string? detail, Actor actor, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            address = address?.Trim();
            if (!EmailGuards.IsAddress(address)) return ServiceResult<SuppressionView>.Fail(new GuardFailure(EmailGuards.AddressInvalid, "Enter one email address such as name@example.com"));
            if (await db.Set<EmailSuppressions>().AnyAsync(s => s.Address == address, ct)) return ServiceResult<SuppressionView>.Fail(new GuardFailure(EmailGuards.AlreadySuppressed, "This address is already on the suppression list"));
            var row = Add(db, address!, "Manual", detail?.Trim() is { Length: > 0 } d ? (d.Length > 500 ? d[..500] : d) : null, null, actor.UserId, Now);
            Audit(db, actor, null, Entity, row.Id, "Add", null, new { reason = row.Reason });
            await db.SaveChangesAsync(ct);
            return ServiceResult<SuppressionView>.Ok(new(row.Id, row.Address, row.Reason, row.Detail, Iso(row.CreatedAt), null, row.Version));
        }, ct);

    public async Task<ServiceResult<SuppressionView>> RemoveAsync(string id, Actor actor, int? ifMatch, CancellationToken ct = default)
    {
        string? address = null;
        var r = await RunAsync(async db =>
        {
            var row = await db.Set<EmailSuppressions>().SingleOrDefaultAsync(s => s.Id == id, ct);
            if (row is null) return ServiceResult<SuppressionView>.NotFound("Suppressed address");
            if (VersionMismatch(ifMatch, row.Version)) return ServiceResult<SuppressionView>.Conflict(new { row.Id, row.Address, row.Reason, row.Version });
            address = row.Address;
            var view = new SuppressionView(row.Id, row.Address, row.Reason, row.Detail, Iso(row.CreatedAt), null, row.Version);
            db.Set<EmailSuppressions>().Remove(row);
            Audit(db, actor, null, Entity, row.Id, "Remove", new { row.Reason }, null);
            await db.SaveChangesAsync(ct);
            return ServiceResult<SuppressionView>.Ok(view);
        }, ct);
        if (r.IsOk && address is not null) await alerts.ResolveAsync("Notifications", "DeliveryFailed", AlertKey(address), ct);   // the address may be tried again, so its alert is closed
        return r;
    }

    /// <summary>Adds the row in the caller's transaction (the caller audits and saves). The address is stored as given; the column compares without regard to capitals.</summary>
    public static EmailSuppressions Add(ReleaseDbContext db, string address, string reason, string? detail, string? sourceMessageId, string? byUserId, DateTime at)
    {
        var row = new EmailSuppressions { Address = address, Reason = reason, Detail = detail, SourceMessageId = sourceMessageId, CreatedByUserId = byUserId, CreatedAt = at, WorkspaceId = Memberships.WorkspaceOf(db) };
        db.Set<EmailSuppressions>().Add(row);
        return row;
    }
}

/// <summary>
/// The sink behind <see cref="IEmailProblemSink"/>: one alert per address (<c>Notifications/DeliveryFailed</c>, the same fingerprint as a bounce), raised after the commit that could not queue mail for it.
/// It resolves <see cref="SyncAlertWriter"/> when it is needed, not at construction: the writer depends on the notifier, which depends on this. A failure to raise is logged at error level and
/// swallowed here only because the caller has already committed the inbox row and must not lose it; the log line is the trace (rule 8).
/// </summary>
public sealed class EmailProblemSink(IServiceProvider services, ILogger<EmailProblemSink> log) : IEmailProblemSink
{
    public async Task UndeliverableAsync(string address, string why, CancellationToken ct)
    {
        try
        {
            var alerts = services.GetRequiredService<SyncAlertWriter>();
            await alerts.RaiseAsync("Notifications", "DeliveryFailed", EmailSuppressionService.AlertKey(address),
                $"A person's email address \"{(address.Length > 120 ? address[..120] : address)}\" cannot be mailed: {why}. Their notifications reach the inbox only until the address is corrected under Teams & settings.", ct: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "The undeliverable-address alert could not be raised");
        }
    }
}
