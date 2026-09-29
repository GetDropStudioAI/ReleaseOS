using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>The runbook plan (PROJECT_SCOPE section 6): GET/POST /trains/{id}/steps, PATCH /steps/{id}, PUT /steps/{id}/dependencies.</summary>
public static class RunbookEndpoints
{
    public sealed record CreateStepBody(string? StepCode, string? Section, string Title, string? Instructions, string? OwnerUserId, string? OwnerTeamId,
                                        DateTime PlannedStartAt, int PlannedDurationMin, string? BundledProductId);
    public sealed record PatchStepBody(string? StepCode, string? Section, string? Title, string? Instructions, string? OwnerUserId, string? OwnerTeamId,
                                       DateTime? PlannedStartAt, int? PlannedDurationMin, string? BundledProductId);
    public sealed record DependenciesBody(string[] DependsOn);
    public sealed record DependencyRef(string Id, string Code);
    public sealed record StepRow(string Id, string StepCode, string Section, string Title, string? Instructions, string? OwnerUserId, string? OwnerTeamId, string? OwnerName,
                                 string? ProductId, string? ProductName, string PlannedStartAt, string PlannedEndAt, int PlannedDurationMin, DependencyRef[] DependsOn, int Version);

    private const string Iso = "yyyy-MM-dd'T'HH:mm:ss'Z'";
    private static Actor ActorOf(ClaimsPrincipal u) => new(u.FindFirstValue("uid") ?? throw new InvalidOperationException("Signed-in user has no uid claim"));

    public static void MapRunbook(this RouteGroupBuilder api)
    {
        api.MapGet("/trains/{id}/steps", async (string id, IDbContextFactory<ReleaseDbContext> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            if (!await db.Set<ReleaseTrains>().AnyAsync(t => t.Id == id, ct)) return Results.NotFound(new { message = "Train not found" });
            return Results.Ok(await RowsAsync(db, id, ct));
        }).RequireAuthorization(Policies.Read);

        api.MapPost("/trains/{id}/steps", async (string id, CreateStepBody b, ClaimsPrincipal u, RunbookService s, IDbContextFactory<ReleaseDbContext> dbf, CancellationToken ct) =>
        {
            var r = await s.CreateAsync(id, new NewStep(b.StepCode, b.Section ?? "Deploy", b.Title, b.Instructions, b.OwnerUserId, b.OwnerTeamId, b.PlannedStartAt, b.PlannedDurationMin, b.BundledProductId), ActorOf(u), ct);
            return r.IsOk ? Results.Ok(await OneAsync(dbf, r.Value!.Id, ct)) : Results2.ToHttp(r);
        }).RequireAuthorization(Policies.Plan);

        api.MapPatch("/steps/{id}", async (string id, PatchStepBody b, ClaimsPrincipal u, HttpRequest req, RunbookService s, IDbContextFactory<ReleaseDbContext> dbf, CancellationToken ct) =>
        {
            var r = await s.UpdateAsync(id, new StepPatch(b.StepCode, b.Section, b.Title, b.Instructions, b.OwnerUserId, b.OwnerTeamId, b.PlannedStartAt, b.PlannedDurationMin, b.BundledProductId), ActorOf(u), req.IfMatch(), ct);
            return r.IsOk ? Results.Ok(await OneAsync(dbf, id, ct)) : Results2.ToHttp(r);
        }).RequireAuthorization(Policies.Plan);

        api.MapPut("/steps/{id}/dependencies", async (string id, DependenciesBody b, ClaimsPrincipal u, HttpRequest req, RunbookService s, IDbContextFactory<ReleaseDbContext> dbf, CancellationToken ct) =>
        {
            var r = await s.SetDependenciesAsync(id, b.DependsOn ?? [], ActorOf(u), req.IfMatch(), ct);
            return r.IsOk ? Results.Ok(await OneAsync(dbf, id, ct)) : Results2.ToHttp(r);
        }).RequireAuthorization(Policies.Plan);
    }

    private static async Task<StepRow> OneAsync(IDbContextFactory<ReleaseDbContext> dbf, string stepId, CancellationToken ct)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var trainId = await db.Set<RunbookSteps>().Where(x => x.Id == stepId).Select(x => x.ReleaseTrainId).SingleAsync(ct);
        return (await RowsAsync(db, trainId, ct)).Single(x => x.Id == stepId);
    }

    private static async Task<StepRow[]> RowsAsync(ReleaseDbContext db, string trainId, CancellationToken ct)
    {
        var steps = (await db.Set<RunbookSteps>().AsNoTracking().Where(s => s.ReleaseTrainId == trainId).ToListAsync(ct))
            .OrderBy(s => s.PlannedStartAt).ThenBy(s => s.StepCode, StringComparer.Ordinal).ToList();
        var ids = steps.Select(s => s.Id).ToList();
        var deps = (await db.Set<StepDependencies>().AsNoTracking().Where(d => ids.Contains(d.StepId)).ToListAsync(ct)).ToLookup(d => d.StepId);
        var code = steps.ToDictionary(s => s.Id, s => s.StepCode);
        var people = await db.Set<Users>().AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.DisplayName, ct);
        var teams = await db.Set<Teams>().AsNoTracking().ToDictionaryAsync(x => x.Id, x => "@" + x.Handle, ct);
        var products = await db.Set<BundledProducts>().AsNoTracking().Where(p => p.ReleaseTrainId == trainId).ToDictionaryAsync(p => p.Id, p => p.ProductName, ct);
        return [.. steps.Select(s => new StepRow(s.Id, s.StepCode, s.Section, s.Title, s.Instructions, s.OwnerUserId, s.OwnerTeamId,
            s.OwnerUserId is not null ? people.GetValueOrDefault(s.OwnerUserId) : teams.GetValueOrDefault(s.OwnerTeamId ?? ""),
            s.BundledProductId, s.BundledProductId is null ? null : products.GetValueOrDefault(s.BundledProductId),
            s.PlannedStartAt.ToString(Iso), s.PlannedStartAt.AddMinutes(s.PlannedDurationMin).ToString(Iso), s.PlannedDurationMin,
            [.. deps[s.Id].Select(d => new DependencyRef(d.DependsOnStepId, code[d.DependsOnStepId])).OrderBy(d => d.Code, StringComparer.Ordinal)], s.Version))];
    }
}
