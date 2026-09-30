using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

public sealed record ParsePreview(string PreviewId, int TrainVersion, string ExpiresAt, int ErrorCount, int WarningCount,
                                  IReadOnlyList<ParsedTask> Tasks, IReadOnlyList<ParseIssue> Issues, IReadOnlyList<string> DecertifiesGates);
public sealed record BulkCommitResult(int Inserted, IReadOnlyList<string> DecertifiedGates, int TrainVersion);
internal sealed record StoredPreview(IReadOnlyList<ParsedTask> Tasks, IReadOnlyList<ParseIssue> Issues, IReadOnlyList<string> DecertifiesGates);

/// <summary>
/// Bulk checklist parser service (PROJECT_SCOPE 5.1). <c>ParseAsync</c> stores a preview for 30 minutes; <c>CommitAsync</c> inserts every task in one
/// transaction. Any error blocks commit; a train that moved since the preview was taken (Version) is a 409; committing into a Certified gate needs the
/// caller to acknowledge that it will be decertified. Previews are scratch data like session state: no audit row; the commit is audited.
/// </summary>
public sealed class TaskParserService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IRealtimePublisher? realtime = null) : ServiceBase(dbf, time, realtime)
{
    public static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(30);
    private static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web);

    public Task<ServiceResult<ParsePreview>> ParseAsync(string trainId, string? text, string? defaultGateId, Actor actor, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var train = await db.Set<ReleaseTrains>().AsNoTracking().SingleOrDefaultAsync(t => t.Id == trainId, ct);
            if (train is null) return ServiceResult<ParsePreview>.NotFound("train");

            var gates = await db.Set<StageGates>().AsNoTracking().Where(g => g.ReleaseTrainId == trainId).OrderBy(g => g.SequenceOrder).ToListAsync(ct);
            var gateIds = gates.Select(g => g.Id).ToList();
            var existing = (await db.Set<ChecklistTasks>().AsNoTracking().Where(t => gateIds.Contains(t.StageGateId)).Select(t => new { t.StageGateId, t.TaskDescription }).ToListAsync(ct)).ToLookup(t => t.StageGateId, t => t.TaskDescription);
            var people = await db.Set<Users>().AsNoTracking().ToListAsync(ct);
            var teams = await db.Set<Teams>().AsNoTracking().ToListAsync(ct);
            var products = await db.Set<BundledProducts>().AsNoTracking().Where(p => p.ReleaseTrainId == trainId).ToListAsync(ct);
            string? OwnerName(StageGates g) => g.OwnerUserId is not null ? people.FirstOrDefault(u => u.Id == g.OwnerUserId)?.DisplayName : teams.FirstOrDefault(t => t.Id == g.OwnerTeamId) is { } tm ? "@" + tm.Handle : null;

            var ctx = new ParseContext(
                gates.FirstOrDefault(g => g.Id == defaultGateId)?.GateName,
                [.. gates.Select(g => new ParseGate(g.Id, g.GateName, g.OwnerUserId is not null || g.OwnerTeamId is not null, OwnerName(g), [.. existing[g.Id]]))],
                [.. teams.Select(t => new ParseTeam(t.Id, t.Handle))],
                [.. people.Where(u => u.IsActive).Select(u => new ParseUser(u.Id, u.Handle, u.Email, u.DisplayName))],
                [.. products.Select(p => new ParseProduct(p.Id, p.ProductName))]);
            var result = ChecklistParser.Parse(text, ctx);

            var certified = gates.Where(g => g.Status == "Certified" && result.Tasks.Any(t => t.GateId == g.Id)).Select(g => g.GateName).ToList();
            var now = Now;
            // Previews are scratch data (no audit row). Uncommitted ones that expired more than a day ago are dropped here, so they cannot pile up in the
            // database and in every backup (SEC-D9); the day's grace keeps the "expired" answer for a late commit. Committed ones stay: the commit's audit row names them.
            var stale = now - PreviewLifetime - TimeSpan.FromDays(1);
            await db.Set<ParsePreviews>().Where(x => x.CommittedAt == null && x.CreatedAt < stale).ExecuteDeleteAsync(ct);   // same transaction; rows are not loaded
            var stored = new StoredPreview(result.Tasks, result.Issues, certified);
            var row = new ParsePreviews
            {
                ReleaseTrainId = trainId, UserId = actor.UserId, TrainVersion = train.Version, ResultJson = JsonSerializer.Serialize(stored, J),
                ErrorCount = result.ErrorCount, CreatedAt = now, ExpiresAt = now + PreviewLifetime,
            };
            db.Set<ParsePreviews>().Add(row);
            await db.SaveChangesAsync(ct);
            return ServiceResult<ParsePreview>.Ok(new ParsePreview(row.Id, row.TrainVersion, Iso(row.ExpiresAt), result.ErrorCount, result.WarningCount, result.Tasks, result.Issues, certified));
        }, ct);

    public Task<ServiceResult<BulkCommitResult>> CommitAsync(string trainId, string previewId, bool acknowledgeDecertify, Actor actor, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var p = await db.Set<ParsePreviews>().SingleOrDefaultAsync(x => x.Id == previewId && x.ReleaseTrainId == trainId && x.UserId == actor.UserId, ct);
            if (p is null) return ServiceResult<BulkCommitResult>.NotFound("preview");
            if (p.CommittedAt is not null) return ServiceResult<BulkCommitResult>.Fail(new GuardFailure(Guards.PreviewCommitted, "This preview was already committed"));
            if (p.ExpiresAt < Now) return ServiceResult<BulkCommitResult>.Fail(new GuardFailure(Guards.PreviewExpired, "This preview expired after 30 minutes; parse the text again"));
            if (p.ErrorCount > 0) return ServiceResult<BulkCommitResult>.Fail(new GuardFailure(Guards.PreviewHasErrors, $"The preview has {p.ErrorCount} error(s); fix them and parse again"));

            var train = await db.Set<ReleaseTrains>().SingleAsync(t => t.Id == trainId, ct);
            if (train.Version != p.TrainVersion) return ServiceResult<BulkCommitResult>.Conflict(train);   // the train moved since the preview: parse again against what it is now

            var stored = JsonSerializer.Deserialize<StoredPreview>(p.ResultJson, J)!;
            var gateIds = stored.Tasks.Select(t => t.GateId).Distinct().ToList();
            var gates = await db.Set<StageGates>().Where(g => g.ReleaseTrainId == trainId && gateIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, ct);
            if (gates.Count != gateIds.Count) return ServiceResult<BulkCommitResult>.Conflict(train);       // a gate vanished since the preview
            var certified = gates.Values.Where(g => g.Status == "Certified").Select(g => g.GateName).Order().ToList();
            if (certified.Count > 0 && !acknowledgeDecertify)
                return ServiceResult<BulkCommitResult>.Fail(new GuardFailure(Guards.DecertifyNotAcknowledged, $"Adding tasks decertifies: {string.Join(", ", certified)}. Confirm to continue", certified));

            var now = Now;
            var next = new Dictionary<string, int>();
            foreach (var g in gates.Values)
                next[g.Id] = await db.Set<ChecklistTasks>().Where(t => t.StageGateId == g.Id).MaxAsync(t => (int?)t.SequenceOrder, ct) ?? 0;
            foreach (var t in stored.Tasks.OrderBy(t => t.Line))
            {
                var g = gates[t.GateId];
                var (ownerUser, ownerTeam) = t.OwnerKind switch { "User" => (t.OwnerId, (string?)null), "Team" => ((string?)null, t.OwnerId), _ => (g.OwnerUserId, g.OwnerTeamId) };
                db.Set<ChecklistTasks>().Add(new ChecklistTasks
                {
                    StageGateId = g.Id, BundledProductId = t.ProductId, TaskDescription = t.Description, OwnerUserId = ownerUser, OwnerTeamId = ownerTeam,
                    SequenceOrder = ++next[g.Id], LastChangedByUserId = actor.UserId, LastChangedAt = now,
                });
            }
            foreach (var g in gates.Values.Where(g => g.Status == "Certified")) { g.LastChangedByUserId = actor.UserId; g.LastChangedAt = now; }   // the decertify trigger reads the actor from the gate

            p.CommittedAt = now;
            var before = new { train.Version };
            train.Version++; train.UpdatedAt = now; train.LastChangedByUserId = actor.UserId; train.LastChangedAt = now;   // the train moved: other open previews are now stale (409)
            Audit(db, actor, trainId, "ChecklistTasks", p.Id, "BulkCommit", before, new { inserted = stored.Tasks.Count, gates = gates.Values.Select(g => g.GateName).Order(), decertified = certified, train.Version });
            await db.SaveChangesAsync(ct);
            return ServiceResult<BulkCommitResult>.Ok(new BulkCommitResult(stored.Tasks.Count, certified, train.Version));
        }, ct);

    private static string Iso(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
}
