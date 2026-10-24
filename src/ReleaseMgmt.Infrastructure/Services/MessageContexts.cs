using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Exports;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

/// <summary>Finds the workspace, the release and the deep link a message is about, from the entity the notice names (REOS-139).</summary>
public static class MessageContexts
{
    /// <param name="entityType">The notice's entity (StageGate, RunbookRun, GoNoGoCondition, PostImplementationReview, Approval, FreezeWindow, SyncAlert, ...).</param>
    /// <param name="trainId">The release when the caller knows it; otherwise it is read from the entity.</param>
    public static async Task<MessageSubject> ForEntityAsync(ReleaseDbContext db, PublicLinks? links, string entityType, string entityId, string? trainId, CancellationToken ct)
    {
        links ??= PublicLinks.None;
        trainId ??= await TrainOfAsync(db, entityType, entityId, ct);
        var workspace = await ReleaseFacts.WorkspaceNameAsync(db, ct);
        if (trainId is null) return new MessageSubject(workspace, null, null, entityType == "SyncAlert" ? links.To("/sync") : entityType == "Approval" ? links.To("/approvals") : links.To("/"));

        var t = await db.Set<ReleaseTrains>().AsNoTracking().Where(x => x.Id == trainId).Select(x => new { x.ReleaseKey, x.Title }).SingleOrDefaultAsync(ct);
        var e = Uri.EscapeDataString(trainId);
        var link = entityType switch
        {
            "StageGate" => links.To($"/trains/{e}/gates/{Uri.EscapeDataString(entityId)}"),
            "RunbookRun" => links.To($"/orchestration/{e}/live"),
            "Approval" => links.To("/approvals"),
            _ => links.To($"/trains/{e}"),
        };
        return new MessageSubject(workspace, t?.ReleaseKey, t?.Title, link);
    }

    private static async Task<string?> TrainOfAsync(ReleaseDbContext db, string entityType, string entityId, CancellationToken ct) => entityType switch
    {
        "ReleaseTrain" => entityId,
        "StageGate" => await db.Set<StageGates>().AsNoTracking().Where(x => x.Id == entityId).Select(x => x.ReleaseTrainId).SingleOrDefaultAsync(ct),
        "RunbookRun" => await db.Set<RunbookRuns>().AsNoTracking().Where(x => x.Id == entityId).Select(x => x.ReleaseTrainId).SingleOrDefaultAsync(ct),
        "StepExecution" => await (from e in db.Set<StepExecutions>().AsNoTracking() join r in db.Set<RunbookRuns>().AsNoTracking() on e.RunId equals r.Id where e.Id == entityId select r.ReleaseTrainId).SingleOrDefaultAsync(ct),
        "GoNoGoCondition" => await (from c in db.Set<GoNoGoConditions>().AsNoTracking() join d in db.Set<GoNoGoDecisions>().AsNoTracking() on c.DecisionId equals d.Id where c.Id == entityId select d.ReleaseTrainId).SingleOrDefaultAsync(ct),
        "PostImplementationReview" => await db.Set<PostImplementationReviews>().AsNoTracking().Where(x => x.Id == entityId).Select(x => x.ReleaseTrainId).SingleOrDefaultAsync(ct),
        "Approval" => await db.Set<Approvals>().AsNoTracking().Where(x => x.Id == entityId).Select(x => x.ReleaseTrainId).SingleOrDefaultAsync(ct),
        "SyncAlert" => await db.Set<SyncAlerts>().AsNoTracking().Where(x => x.Id == entityId).Select(x => x.ReleaseTrainId).SingleOrDefaultAsync(ct),
        _ => null,
    };
}
