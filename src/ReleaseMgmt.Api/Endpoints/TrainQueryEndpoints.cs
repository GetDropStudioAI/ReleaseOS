using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Infrastructure.Services;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>Read side for the Stream and the train header (REOS-22). Reads never change state.</summary>
public static class TrainQueryEndpoints
{
    public sealed record StreamRow(string Id, string Title, string Status, string RiskTier, string TargetReleaseDate, int DaysToTarget, int Blockers, string[] Gates, string? CloseCode, string? EndedOn, int Version);
    public sealed record GateRow(string Id, string Name, string Class, string Status, string DueOn, int TMinus, string RequiredBeforeStatus, string? OwnerName, string? CertifiedBy, string? CertifiedOn, int TasksDone, int TasksTotal, int Version);
    public sealed record TrainDetail(string Id, string Title, string Status, string RiskTier, string TargetReleaseDate, int DaysToTarget, string? ChangeTicketNumber, string? CloseCode, bool RollbackRehearsed, string? NextStatus, int Version, GateRow[] Gates);

    public sealed record FreezeAhead(string Id, string Name, string Kind, string StartsAt, string EndsAt, string Scope, bool Active, int OverridesGranted);
    public sealed record ProductRow(string Id, string Name, string VersionTag, string ProjectCode, int TasksDone, int TasksTotal, string[] OpenBlockers, string Health);
    public sealed record ProductsResponse(int TasksDone, int TasksTotal, ProductRow[] Products);
    public sealed record TaskRow(string Id, string Description, string? Owner, string? Product, bool Done, string? CompletedAt, string? CompletedBy, int Version);
    public sealed record Certify(bool Eligible, string[] Reasons);
    public sealed record GateDetail(string Id, string TrainId, string TrainStatus, string Name, string Class, string Status, string DueOn, int TMinus, string? OwnerName, int Version, TaskRow[] Tasks, Certify Certify);
    public sealed record WindowBody(DateTime StartsAt, DateTime EndsAt);
    public sealed record WindowRow(string StartsAt, string EndsAt, int Version);

    private static readonly string[] Next = ["Planning", "Gated", "Executing", "Complete"];

    private static WindowRow ToRow(DeploymentWindows w) => new(w.StartsAt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), w.EndsAt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), w.Version);

    private static async Task<HashSet<DateOnly>> HolidaysAsync(ReleaseDbContext db, CancellationToken ct) =>
        [.. await db.Set<Holidays>().AsNoTracking().Select(h => h.Day).ToListAsync(ct)];

    public static void MapTrainQueries(this RouteGroupBuilder api)
    {
        api.MapGet("/trains", async (IDbContextFactory<ReleaseDbContext> dbf, TrainLifecycleService lifecycle, TimeProvider time, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
            var holidays = await HolidaysAsync(db, ct);
            var cutoff = time.GetUtcNow().UtcDateTime.AddDays(-30);
            var trains = await db.Set<ReleaseTrains>().AsNoTracking()
                .Where(t => t.ArchivedAt == null && t.CurrentStatus != "Aborted" && (t.CurrentStatus != "Complete" || t.ActualEndAt >= cutoff))
                .OrderBy(t => t.TargetReleaseDate).ThenBy(t => t.Title).ToListAsync(ct);
            var ids = trains.Select(t => t.Id).ToList();
            var gates = (await db.Set<StageGates>().AsNoTracking().Where(g => ids.Contains(g.ReleaseTrainId)).ToListAsync(ct))
                .ToLookup(g => g.ReleaseTrainId);
            var rows = new List<StreamRow>();
            // Blockers for the next hop: the same guards :advance runs, counted for every train at once (a per-train readiness call took ~1 ms each: 230 ms for 200 trains; REOS-52).
            var blockerCounts = await lifecycle.NextHopBlockerCountsAsync(db, trains, ct);
            foreach (var t in trains)
            {
                var blockers = blockerCounts[t.Id];
                rows.Add(new StreamRow(t.Id, t.Title, t.CurrentStatus, t.RiskTier, t.TargetReleaseDate.ToString("yyyy-MM-dd"),
                    BusinessDays.Between(today, t.TargetReleaseDate, holidays), blockers,
                    [.. gates[t.Id].OrderBy(g => g.SequenceOrder).Select(g => g.Status)], t.CloseCode, t.ActualEndAt?.ToString("yyyy-MM-dd"), t.Version));
            }
            return Results.Ok(rows);
        }).RequireAuthorization(Policies.Read);

        // The Stream footer: freezes and chills that are running now or start within the next 45 days, nearest first, with how many unexpired overrides each has.
        api.MapGet("/freeze-windows/ahead", async (IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var now = time.GetUtcNow().UtcDateTime;
            var horizon = now.AddDays(45);
            var windows = (await db.Set<FreezeWindows>().AsNoTracking().ToListAsync(ct)).Where(w => w.EndsAt > now && w.StartsAt < horizon).OrderBy(w => w.StartsAt).Take(3).ToList();
            var ids = windows.Select(w => w.Id).ToList();
            var overrides = (await db.Set<FreezeOverrides>().AsNoTracking().Where(o => ids.Contains(o.FreezeWindowId)).ToListAsync(ct)).Where(o => o.ExpiresAt > now).ToLookup(o => o.FreezeWindowId);
            const string iso = "yyyy-MM-dd'T'HH:mm:ss'Z'";
            return Results.Ok(windows.Select(w => new FreezeAhead(w.Id, w.Name, w.Kind, w.StartsAt.ToString(iso), w.EndsAt.ToString(iso),
                w.ProductPattern is null ? "All products" : $"Products matching {w.ProductPattern}", w.StartsAt <= now, overrides[w.Id].Count())));
        }).RequireAuthorization(Policies.Read);

        // Who a task can be assigned to: active users and teams (user xor team, D-level rule in the schema).
        api.MapGet("/owners", async (IDbContextFactory<ReleaseDbContext> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var users = await db.Set<Users>().AsNoTracking().Where(u => u.IsActive).OrderBy(u => u.DisplayName).Select(u => new { id = u.Id, name = u.DisplayName, kind = "user" }).ToListAsync(ct);
            var teams = await db.Set<Teams>().AsNoTracking().OrderBy(t => t.Handle).Select(t => new { id = t.Id, name = "@" + t.Handle, kind = "team" }).ToListAsync(ct);
            return Results.Ok(users.Concat(teams));
        }).RequireAuthorization(Policies.Read);

        // Product health (docs/QUESTIONS.md Q-007, provisional): Ready = every task done and no open blocker; At risk = any open blocker; otherwise On track.
        api.MapGet("/trains/{id}/products", async (string id, IDbContextFactory<ReleaseDbContext> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            if (!await db.Set<ReleaseTrains>().AnyAsync(t => t.Id == id, ct)) return Results.NotFound(new { message = "Train not found" });
            var products = await db.Set<BundledProducts>().AsNoTracking().Where(p => p.ReleaseTrainId == id).OrderBy(p => p.ProductName).ToListAsync(ct);
            var gateIds = await db.Set<StageGates>().Where(g => g.ReleaseTrainId == id).Select(g => g.Id).ToListAsync(ct);
            var tasks = (await db.Set<ChecklistTasks>().AsNoTracking().Where(k => gateIds.Contains(k.StageGateId) && k.BundledProductId != null)
                .Select(k => new { k.BundledProductId, k.IsCompleted }).ToListAsync(ct)).ToLookup(k => k.BundledProductId!);
            var blockers = (await db.Set<Blockers>().AsNoTracking().Where(b => b.ReleaseTrainId == id && b.ResolvedAt == null && b.BundledProductId != null)
                .Select(b => new { b.BundledProductId, b.Severity }).ToListAsync(ct)).ToLookup(b => b.BundledProductId!);
            var rows = products.Select(p =>
            {
                var done = tasks[p.Id].Count(k => k.IsCompleted); var total = tasks[p.Id].Count();
                var open = blockers[p.Id].Select(b => b.Severity).ToArray();
                var health = open.Length > 0 ? "At risk" : total > 0 && done == total ? "Ready" : "On track";
                return new ProductRow(p.Id, p.ProductName, p.VersionTag, p.ProjectCode, done, total, open, health);
            }).ToArray();
            return Results.Ok(new ProductsResponse(rows.Sum(r => r.TasksDone), rows.Sum(r => r.TasksTotal), rows));
        }).RequireAuthorization(Policies.Read);

        // Gate + checklist + whether *this caller* may certify it now, and if not, every reason (the same guards CertifyAsync runs).
        api.MapGet("/gates/{id}", async (string id, ClaimsPrincipal u, IDbContextFactory<ReleaseDbContext> dbf, GateService gates, TimeProvider time, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var g = await db.Set<StageGates>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (g is null) return Results.NotFound(new { message = "Gate not found" });
            var train = await db.Set<ReleaseTrains>().AsNoTracking().SingleAsync(t => t.Id == g.ReleaseTrainId, ct);
            var holidays = await HolidaysAsync(db, ct);
            var people = await db.Set<Users>().AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.DisplayName, ct);
            var teamNames = await db.Set<Teams>().AsNoTracking().ToDictionaryAsync(x => x.Id, x => "@" + x.Handle, ct);
            var products = await db.Set<BundledProducts>().AsNoTracking().Where(p => p.ReleaseTrainId == g.ReleaseTrainId).ToDictionaryAsync(p => p.Id, p => p.ProductName, ct);
            var tasks = await db.Set<ChecklistTasks>().AsNoTracking().Where(k => k.StageGateId == id).OrderBy(k => k.SequenceOrder).ToListAsync(ct);

            var reasons = new List<string>();
            var (allowed, denial) = await LifecycleEndpoints.GateAccessAsync(db, g, u, complianceRestricted: true, ct);
            // A Compliance gate's role rule is already one of the certify guards below, so only the ownership denial is added here (no duplicate line).
            if (!allowed && denial is not null && g.GateClass != "Compliance") reasons.Add(denial);
            var uid = u.FindFirstValue("uid") ?? "";
            reasons.AddRange((await gates.EvaluateCertifyAsync(db, g, uid)).Select(f => f.Items is { Count: > 0 } ? $"{f.Message}: {string.Join(", ", f.Items)}" : f.Message));
            reasons = [.. reasons.Distinct()];

            return Results.Ok(new GateDetail(g.Id, g.ReleaseTrainId, train.CurrentStatus, g.GateName, g.GateClass, g.Status, g.DueOn.ToString("yyyy-MM-dd"),
                BusinessDays.Between(g.DueOn, train.TargetReleaseDate, holidays), g.OwnerUserId is null ? teamNames.GetValueOrDefault(g.OwnerTeamId ?? "") : people.GetValueOrDefault(g.OwnerUserId), g.Version,
                [.. tasks.Select(k => new TaskRow(k.Id, k.TaskDescription, k.OwnerUserId is null ? teamNames.GetValueOrDefault(k.OwnerTeamId ?? "") : people.GetValueOrDefault(k.OwnerUserId),
                    k.BundledProductId is null ? null : products.GetValueOrDefault(k.BundledProductId), k.IsCompleted, k.CompletedAt?.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                    k.CompletedByUserId is null ? null : people.GetValueOrDefault(k.CompletedByUserId), k.Version))],
                new Certify(reasons.Count == 0, [.. reasons])));
        }).RequireAuthorization(Policies.Read);

        api.MapGet("/trains/{id}/window", async (string id, IDbContextFactory<ReleaseDbContext> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var w = await db.Set<DeploymentWindows>().AsNoTracking().SingleOrDefaultAsync(x => x.ReleaseTrainId == id, ct);
            return w is null ? Results.NotFound(new { message = "No deployment window set" }) : Results.Ok(ToRow(w));
        }).RequireAuthorization(Policies.Read);

        api.MapPut("/trains/{id}/window", async (string id, WindowBody body, ClaimsPrincipal u, HttpRequest req, WindowService s, CancellationToken ct) =>
        {
            var uid = u.FindFirstValue("uid") ?? throw new InvalidOperationException("Signed-in user has no uid claim");
            var r = await s.SetAsync(id, body.StartsAt, body.EndsAt, new Actor(uid), req.IfMatch(), ct);
            return r.Kind == ResultKind.Ok ? Results.Ok(ToRow(r.Value!)) : Results2.ToHttp(r);
        }).RequireAuthorization(Policies.Plan);

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
            var holidays = await HolidaysAsync(db, ct);
            var people = await db.Set<Users>().AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.DisplayName, ct);
            var teamNames = await db.Set<Teams>().AsNoTracking().ToDictionaryAsync(x => x.Id, x => "@" + x.Handle, ct);
            var i = Array.IndexOf(Next, t.CurrentStatus);
            return Results.Ok(new TrainDetail(t.Id, t.Title, t.CurrentStatus, t.RiskTier, t.TargetReleaseDate.ToString("yyyy-MM-dd"),
                BusinessDays.Between(today, t.TargetReleaseDate, holidays), t.ChangeTicketNumber, t.CloseCode, t.RollbackRehearsedAt != null,
                i >= 0 && i < Next.Length - 1 ? Next[i + 1] : null, t.Version,
                [.. gates.Select(g => new GateRow(g.Id, g.GateName, g.GateClass, g.Status, g.DueOn.ToString("yyyy-MM-dd"), BusinessDays.Between(g.DueOn, t.TargetReleaseDate, holidays),
                    g.RequiredBeforeStatus, g.OwnerUserId is null ? teamNames.GetValueOrDefault(g.OwnerTeamId ?? "") : people.GetValueOrDefault(g.OwnerUserId),
                    g.CertifiedByUserId is null ? null : people.GetValueOrDefault(g.CertifiedByUserId), g.CertifiedAt?.ToString("yyyy-MM-dd"),
                    tasks[g.Id].Count(k => k.IsCompleted), tasks[g.Id].Count(), g.Version))]));
        }).RequireAuthorization(Policies.Read);
    }
}
