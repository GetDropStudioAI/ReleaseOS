using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Infrastructure.Reminders;

/// <summary>
/// Persists a background failure as a SyncAlerts row (D30), deduplicated by fingerprint while unresolved (repeats bump OccurrenceCount),
/// audited in the same transaction. The first occurrence also tells the RTEs and Release Managers in-app (PROJECT_SCOPE 5.3), which is
/// also the live push (NotificationCreated). Source and kind must be values the schema's CHECKs allow.
/// </summary>
public sealed class SyncAlertWriter(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, ILogger<SyncAlertWriter> log, INotifier? notifier = null, IRealtimePublisher? realtime = null)
    : ServiceBase(dbf, time)
{
    private readonly IDbContextFactory<ReleaseDbContext> _dbf = dbf;

    public static string Fingerprint(string source, string kind, string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{source}|{kind}|{key}")));

    public async Task<string> RaiseAsync(string source, string kind, string key, string message, string? trainId = null, CancellationToken ct = default)
    {
        message = message.Length > 500 ? message[..500] : message;
        (string Id, bool Created) r;
        try { r = await WriteAsync(source, kind, key, message, trainId, ct); }
        catch (DbUpdateException) { r = await WriteAsync(source, kind, key, message, trainId, ct); }   // lost the race for the open-fingerprint unique index: the row exists now, so bump it

        if (r.Created && notifier is not null)
        {
            try
            {
                await using var db = await _dbf.CreateDbContextAsync(ct);
                var who = await db.Set<Users>().Where(u => u.IsActive && (u.Role == Roles.RTE || u.Role == Roles.ReleaseManager)).Select(u => u.Id).ToListAsync(ct);
                foreach (var u in who)
                    await notifier.NotifyAsync(new NotificationRequest(u, "SyncAlert", "SyncAlert", r.Id, $"{source} {kind}: {message}", 0), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The alert row is saved and visible; only its in-app copy failed. Nothing further can carry this, so it is logged loudly.
                log.LogError(ex, "Alert {AlertId} ({Source}/{Kind}) was saved but its in-app notification failed", r.Id, source, kind);
            }
        }
        if (realtime is not null)
        {
            try { await realtime.SyncAlertRaisedAsync(r.Id, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Alert {AlertId} was saved but the live push failed", r.Id); }
        }
        return r.Id;
    }

    /// <summary>Auto-resolve (PROJECT_SCOPE 5.3.8, REOS-40): one clean cycle marks the open alert for this (source, kind, key) resolved. The row stays as history
    /// (IsResolved, ResolvedAt); a later failure opens a new row. Returns whether an open alert was resolved.</summary>
    public async Task<bool> ResolveAsync(string source, string kind, string key, CancellationToken ct = default) =>
        await ResolveAsync([(source, kind, key)], ct) > 0;

    /// <summary>Resolves every open alert among the given (source, kind, key) fingerprints in one transaction, then pushes each. Returns how many were resolved.</summary>
    public async Task<int> ResolveAsync(IReadOnlyCollection<(string Source, string Kind, string Key)> keys, CancellationToken ct = default)
    {
        if (keys.Count == 0) return 0;
        var fps = keys.Select(k => Fingerprint(k.Source, k.Kind, k.Key)).Distinct().ToList();
        var result = await RunAsync<List<string>>(async db =>
        {
            var now = Now;
            var open = await db.Set<SyncAlerts>().Where(a => fps.Contains(a.Fingerprint) && !a.IsResolved).ToListAsync(ct);
            foreach (var a in open)
            {
                a.IsResolved = true; a.ResolvedAt = now; a.Version++;
                Audit(db, null, a.ReleaseTrainId, "SyncAlert", a.Id, "AutoResolved", after: new { a.SourceSystem, a.Kind, a.OccurrenceCount });
            }
            if (open.Count > 0) await db.SaveChangesAsync(ct);
            return ServiceResult<List<string>>.Ok(open.Select(a => a.Id).ToList());
        }, ct);
        if (!result.IsOk) throw new InvalidOperationException("Could not resolve the alerts: " + (result.Failures.Count > 0 ? result.Failures[0].Message : result.Missing));
        if (realtime is not null)
            foreach (var id in result.Value!)
            {
                try { await realtime.SyncAlertRaisedAsync(id, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Alert {AlertId} was resolved but the live push failed", id); }
            }
        return result.Value!.Count;
    }

    private async Task<(string Id, bool Created)> WriteAsync(string source, string kind, string key, string message, string? trainId, CancellationToken ct)
    {
        var fp = Fingerprint(source, kind, key);
        var result = await RunAsync<(string, bool)>(async db =>
        {
            var now = Now;
            var open = await db.Set<SyncAlerts>().SingleOrDefaultAsync(a => a.Fingerprint == fp && !a.IsResolved, ct);
            if (open is not null)
            {
                open.OccurrenceCount++; open.LastOccurredAt = now; open.ErrorMessage = message; open.Version++;
                Audit(db, null, open.ReleaseTrainId, "SyncAlert", open.Id, "Repeated", after: new { open.OccurrenceCount });
                await db.SaveChangesAsync(ct);
                return ServiceResult<(string, bool)>.Ok((open.Id, false));
            }
            var a = new SyncAlerts { ReleaseTrainId = trainId, SourceSystem = source, Kind = kind, Fingerprint = fp, ErrorMessage = message, FirstOccurredAt = now, LastOccurredAt = now };
            db.Set<SyncAlerts>().Add(a);
            Audit(db, null, trainId, "SyncAlert", a.Id, "Raised", after: new { source, kind, key });
            await db.SaveChangesAsync(ct);
            return ServiceResult<(string, bool)>.Ok((a.Id, true));
        }, ct);
        if (!result.IsOk) throw new InvalidOperationException("Could not record the alert: " + (result.Failures.Count > 0 ? result.Failures[0].Message : result.Missing));
        return result.Value;
    }
}
