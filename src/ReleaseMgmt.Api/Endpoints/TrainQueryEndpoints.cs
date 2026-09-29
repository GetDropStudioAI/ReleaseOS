using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>Read side for the Stream and the train header (REOS-22). Reads never change state.</summary>
public static class TrainQueryEndpoints
{
    public sealed record StreamRow(string Id, string Title, string Status, string RiskTier, string TargetReleaseDate, int DaysToTarget, int Blockers, string[] Gates, int Version);
    public sealed record GateRow(string Id, string Name, string Class, string Status, string DueOn, string RequiredBeforeStatus, int TasksDone, int TasksTotal, int Version);
    public sealed record TrainDetail(string Id, string Title, string Status, string RiskTier, string TargetReleaseDate, int DaysToTarget, string? ChangeTicketNumber, string? CloseCode, bool RollbackRehearsed, int Version, GateRow[] Gates);

    private static readonly string[] Next = ["Planning", "Gated", "Executing", "Complete"];

    public static void MapTrainQueries(this RouteGroupBuilder api)
    {
        api.MapGet("/trains", async (IDbContextFactory<ReleaseDbContext> dbf, IReadinessService readiness, TimeProvider time, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
            var cutoff = time.GetUtcNow().UtcDateTime.AddDays(-30);
            var trains = await db.Set<ReleaseTrains>().AsNoTracking()
                .Where(t => t.ArchivedAt == null && t.CurrentStatus != "Aborted" && (t.CurrentStatus != "Complete" || t.ActualEndAt >= cutoff))
                .OrderBy(t => t.TargetReleaseDate).ThenBy(t => t.Title).ToListAsync(ct);
            var ids = trains.Select(t => t.Id).ToList();
            var gates = (await db.Set<StageGates>().AsNoTracking().Where(g => ids.Contains(g.ReleaseTrainId)).ToListAsync(ct))
                .ToLookup(g => g.ReleaseTrainId);
            var rows = new List<StreamRow>();
            foreach (var t in trains)
            {
                var i = Array.IndexOf(Next, t.CurrentStatus);
                var blockers = 0;
                if (i >= 0 && i < Next.Length - 1)   // blockers for the next hop: the same guards :advance runs
                    blockers = (await readiness.GetAsync(t.Id, Next[i + 1], ct)).Value?.Blockers.Count ?? 0;
                rows.Add(new StreamRow(t.Id, t.Title, t.CurrentStatus, t.RiskTier, t.TargetReleaseDate.ToString("yyyy-MM-dd"),
                    t.TargetReleaseDate.DayNumber - today.DayNumber, blockers,
                    [.. gates[t.Id].OrderBy(g => g.SequenceOrder).Select(g => g.Status)], t.Version));
            }
            return Results.Ok(rows);
        }).RequireAuthorization(Policies.Read);

        api.MapGet("/trains/{id}", async (string id, IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var t = await db.Set<ReleaseTrains>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (t is null) return Results.NotFound(new { message = "Train not found" });
            var gates = await db.Set<StageGates>().AsNoTracking().Where(g => g.ReleaseTrainId == id).OrderBy(g => g.SequenceOrder).ToListAsync(ct);
            var gateIds = gates.Select(g => g.Id).ToList();
            var tasks = (await db.Set<ChecklistTasks>().AsNoTracking().Where(k => gateIds.Contains(k.StageGateId)).Select(k => new { k.StageGateId, k.IsCompleted }).ToListAsync(ct))
                .ToLookup(k => k.StageGateId);
            var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
            return Results.Ok(new TrainDetail(t.Id, t.Title, t.CurrentStatus, t.RiskTier, t.TargetReleaseDate.ToString("yyyy-MM-dd"),
                t.TargetReleaseDate.DayNumber - today.DayNumber, t.ChangeTicketNumber, t.CloseCode, t.RollbackRehearsedAt != null, t.Version,
                [.. gates.Select(g => new GateRow(g.Id, g.GateName, g.GateClass, g.Status, g.DueOn.ToString("yyyy-MM-dd"), g.RequiredBeforeStatus,
                    tasks[g.Id].Count(k => k.IsCompleted), tasks[g.Id].Count(), g.Version))]));
        }).RequireAuthorization(Policies.Read);
    }
}
