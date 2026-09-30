using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Reminders;
using ReleaseMgmt.Domain.Services;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>Development-only test support for the Sync health screen (REOS-42), mapped next to <c>MapDev</c>. Never exists in Production.</summary>
public static class SyncDevEndpoints
{
    public sealed record RaiseBody(string Source, string Kind, string? Key, string? Message, string? TrainId, int? Repeat);
    public sealed record ConnectorBody(string Source, string? BaseUrl, bool? IsEnabled, int? ConsecutiveFailures, int? LastSuccessMinutesAgo);

    public static void MapSyncDev(this RouteGroupBuilder api)
    {
        // Raises (or repeats) an alert through the real SyncAlertWriter, so the SyncAlertRaised push reaches every open client. -> { id }
        api.MapPost("/dev/sync/raise", async (RaiseBody b, SyncAlertWriter w, CancellationToken ct) =>
        {
            string id = "";
            for (var i = 0; i < Math.Clamp(b.Repeat ?? 1, 1, 500); i++)
                id = await w.RaiseAsync(b.Source, b.Kind, b.Key ?? "dev", b.Message ?? $"{b.Source} {b.Kind} (dev)", b.TrainId, ct);
            return Results.Ok(new { id });
        }).RequireAuthorization(Policies.Admin);

        // Upserts a ConnectorState row (what the poller would write). Times are relative to the server clock.
        api.MapPost("/dev/sync/connector", async (ConnectorBody b, IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, CancellationToken ct) =>
        {
            if (b.Source is not ("Jira" or "ServiceNow")) return Results.BadRequest(new { message = "Source is Jira or ServiceNow" });
            await using var db = await dbf.CreateDbContextAsync(ct);
            var now = time.GetUtcNow().UtcDateTime; now = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
            var c = await db.Set<ConnectorState>().SingleOrDefaultAsync(x => x.SourceSystem == b.Source, ct);
            if (c is null) { c = new ConnectorState { SourceSystem = b.Source, BaseUrl = b.BaseUrl ?? $"https://{b.Source.ToLowerInvariant()}.example.com" }; db.Set<ConnectorState>().Add(c); }
            else { if (b.BaseUrl is not null) c.BaseUrl = b.BaseUrl; c.Version++; }
            if (b.IsEnabled is bool e) c.IsEnabled = e;
            c.ConsecutiveFailures = b.ConsecutiveFailures ?? c.ConsecutiveFailures;
            c.LastCycleStartedAt = now.AddSeconds(-30); c.LastCycleCompletedAt = now;
            c.LastSuccessAt = b.LastSuccessMinutesAgo is int m ? now.AddMinutes(-m) : c.LastSuccessAt ?? now;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { c.SourceSystem, c.ConsecutiveFailures, c.Version });
        }).RequireAuthorization(Policies.Admin);

        // Puts the screen back to clean: every open alert resolved (history kept) and the failure counters zeroed; then one push so open clients refetch.
        api.MapPost("/dev/sync/reset", async (IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IRealtimePublisher rt, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var now = time.GetUtcNow().UtcDateTime; now = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
            var open = await db.Set<SyncAlerts>().Where(a => !a.IsResolved).ToListAsync(ct);
            foreach (var a in open) { a.IsResolved = true; a.ResolvedAt = now; a.Version++; }
            foreach (var c in await db.Set<ConnectorState>().ToListAsync(ct)) { c.ConsecutiveFailures = 0; c.Version++; }
            await db.SaveChangesAsync(ct);
            await rt.SyncAlertRaisedAsync("reset", ct);
            return Results.Ok(new { resolved = open.Count });
        }).RequireAuthorization(Policies.Admin);
    }
}
