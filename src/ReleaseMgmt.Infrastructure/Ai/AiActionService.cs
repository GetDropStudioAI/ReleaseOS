using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Ai;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Infrastructure.Ai;

/// <summary>
/// REOS-184 (D58): the audit trail of AI-assisted actions. One <c>AiActions</c> row per call (refusals and failures included) and one <c>AiAction</c> audit row in the same transaction; the human decision
/// is added once, with its own audit row whose before and after hold the draft and the human edit, so the audit view shows both. The audit rows carry the hashes, so the existing hash chain (REOS-114)
/// covers them: nothing here has a chain of its own. AI output lives only in <c>AiActions</c>, apart from every status row; this class touches no other domain table and calls no status-changing service
/// (an architecture test reads the source and the types to keep it so). Time comes from the TimeProvider; ids are UUIDv7; a decision carries If-Match and answers 409 with the current row.
/// </summary>
public sealed class AiActionService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IRealtimePublisher? realtime = null) : ServiceBase(dbf, time, realtime), IAiActionLog
{
    private static readonly JsonSerializerOptions Camel = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    private static string Cut(string? s, int max) => s is null ? "" : s.Length <= max ? s : s[..max];

    // ---- recording -------------------------------------------------------------------------------------------------------------------

    public async Task<string> RecordAsync(AiRequest request, AiResult result, CancellationToken ct = default)
    {
        using var _ = WorkspaceScope.Enter(request.WorkspaceId);   // the row, its audit row and its filters all belong to the request's workspace, whatever flow called
        var meta = request.Meta ?? new AiCallMeta();
        var known = AiFeatures.Find(request.Feature) is not null;
        var completed = result.Outcome == AiOutcome.Completed;
        var now = Now;
        var row = new AiActions
        {
            Id = Ids.New(), WorkspaceId = request.WorkspaceId, Feature = known ? request.Feature : "unknown", Provider = Cut(result.Provider ?? "unknown", 40), Model = result.Model, ServedModel = result.ServedModel,
            PromptTemplateVersion = Cut(string.IsNullOrWhiteSpace(meta.PromptTemplateVersion) ? AiCallMeta.Untracked : meta.PromptTemplateVersion, 60), PromptHash = result.PromptHash,
            InputRefsJson = JsonSerializer.Serialize(result.Inputs ?? [], Camel), InputHash = result.InputHash, Outcome = DbOutcome(result.Outcome),
            Detail = result.Detail is null ? null : Cut(result.Detail, 500), OutputText = completed ? result.Text ?? "" : null, OutputHash = completed ? AiHash.Sha256(result.Text ?? "") : null,
            InputTokens = Clamp(result.Usage?.InputTokens), OutputTokens = Clamp(result.Usage?.OutputTokens), CacheReadTokens = Clamp(result.Usage?.CacheReadTokens), CacheWriteTokens = Clamp(result.Usage?.CacheWriteTokens),
            Attempts = result.Attempts, Redactions = result.Redactions, Batch = result.Batch, SubjectType = Blank(meta.SubjectType), SubjectId = Blank(meta.SubjectId), ReleaseTrainId = Blank(meta.ReleaseTrainId),
            RequestedByUserId = Blank(meta.RequestedByUserId), RequestedAt = now, Decision = completed ? AiDecisions.Pending : AiDecisions.NotApplicable,
        };
        if ((row.SubjectType is null) != (row.SubjectId is null)) row.SubjectType = row.SubjectId = null;   // both or neither (the table says so)
        var outcome = await RunAsync(async db =>
        {
            db.Set<AiActions>().Add(row);
            var action = completed ? "Generated" : result.ProviderCalled ? "Failed" : "Refused";
            Audit(db, row.RequestedByUserId is { } u ? new Actor(u) : null, null, "AiAction", row.Id, action, null, GeneratedJson(row, result.Inputs ?? [], result.Metering)).ReleaseTrainId = row.ReleaseTrainId;
            if (result.Metering is { } metering) await SettleAsync(db, row, result, metering, now, ct);   // REOS-185: the allowance is settled in the same transaction as the action it paid for
            await db.SaveChangesAsync(ct);
            return ServiceResult<string>.Ok(row.Id);
        }, ct);
        return outcome.IsOk ? outcome.Value! : throw new InvalidOperationException("The AI action could not be recorded: " + string.Join("; ", outcome.Failures.Select(f => f.Message)));
    }

    /// <summary>
    /// The audit trail's outcome column keeps the list it had (REOS-184). The two REOS-185 refusals (a cap, the rate limit) are <c>Blocked</c> there, with the message as the detail; the ledger has the exact reason.
    /// </summary>
    private static string DbOutcome(AiOutcome o) => o is AiOutcome.AllowanceReached or AiOutcome.RateLimited ? nameof(AiOutcome.Blocked) : o.ToString();

    /// <summary>
    /// REOS-185 (D57): turns the call's reservation into the record. A completed call settles at its real cost (Used up, Reserved down); a failed one is released and charged nothing (a timeout or provider error
    /// bills no tokens we can see; Q-185a); a refusal by a cap or the rate limit becomes a Refused entry with its exact reason. A reservation already given back as stale (the call outlived it) is settled as a new
    /// entry so a late answer is still counted. Each ledger change has its audit row here, beside the action's.
    /// </summary>
    private async Task SettleAsync(ReleaseDbContext db, AiActions row, AiResult result, AiMetering m, DateTime now, CancellationToken ct)
    {
        var actor = row.RequestedByUserId is { } u ? new Actor(u) : null;
        var def = AiFeatures.Find(row.Feature);
        var cls = def?.CappedFirst == true ? "Chat" : "Standard";
        var period = await db.Set<AiAllowancePeriods>().SingleAsync(p => p.Id == m.PeriodId, ct);
        var u0 = result.Usage;

        AiUsageLedger Fresh(string state) => new()
        {
            WorkspaceId = row.WorkspaceId, PeriodId = period.Id, Feature = row.Feature, Class = cls, Model = row.Model, Batch = row.Batch, RequestedByUserId = row.RequestedByUserId, State = state,
            ReservedAt = now, SettledAt = now, LastChangedAt = now, AiActionId = row.Id,
        };

        if (m.Refusal is { } why)
        {
            var e = Fresh("Refused"); e.RefusalReason = why.ToString();
            db.Set<AiUsageLedger>().Add(e);
            Audit(db, actor, null, "AiUsage", e.Id, "Refused", null, new { feature = row.Feature, reason = why.ToString(), action = row.Id, period = AiAllowanceService.Day(period.PeriodStart) });
            return;
        }

        var entry = m.EntryId is null ? null : await db.Set<AiUsageLedger>().SingleOrDefaultAsync(l => l.Id == m.EntryId, ct);
        if (entry is null) throw new InvalidOperationException("The AI call's reservation is not in the ledger");
        var completed = result.Outcome == AiOutcome.Completed;
        if (entry.State != "Reserved")
        {
            // Given back as stale while the call was still running. A completed answer was still paid for: count it as its own entry.
            if (!completed) return;
            var late = Fresh("Settled"); late.CostMicroUsd = m.CostMicroUsd; Tokens(late, u0);
            db.Set<AiUsageLedger>().Add(late);
            period.UsedMicroUsd += m.CostMicroUsd; if (cls == "Chat") period.ChatUsedMicroUsd += m.CostMicroUsd; period.LastChangedAt = now; period.Version++;
            Audit(db, actor, null, "AiUsage", late.Id, "Settled", null, new { feature = row.Feature, costMicroUsd = m.CostMicroUsd, action = row.Id, late = true });
            return;
        }

        var before = new { state = entry.State, reservedMicroUsd = entry.ReservedMicroUsd };
        period.ReservedMicroUsd -= entry.ReservedMicroUsd; if (entry.Class == "Chat") period.ChatReservedMicroUsd -= entry.ReservedMicroUsd;
        if (completed)
        {
            entry.State = "Settled"; entry.CostMicroUsd = m.CostMicroUsd; Tokens(entry, u0);
            period.UsedMicroUsd += m.CostMicroUsd; if (entry.Class == "Chat") period.ChatUsedMicroUsd += m.CostMicroUsd;
        }
        else entry.State = "Released";
        entry.AiActionId = row.Id; entry.SettledAt = now; entry.LastChangedAt = now; entry.Version++;
        period.LastChangedAt = now; period.Version++;
        Audit(db, actor, null, "AiUsage", entry.Id, entry.State, before, new { state = entry.State, feature = row.Feature, model = row.Model, costMicroUsd = entry.CostMicroUsd, entry.InputTokens, entry.OutputTokens, entry.CacheReadTokens, entry.CacheWriteTokens, action = row.Id });
    }

    private static void Tokens(AiUsageLedger l, AiUsage? u)
    {
        l.InputTokens = Clamp(u?.InputTokens); l.OutputTokens = Clamp(u?.OutputTokens); l.CacheReadTokens = Clamp(u?.CacheReadTokens); l.CacheWriteTokens = Clamp(u?.CacheWriteTokens);
    }

    private static int Clamp(long? v) => (int)Math.Clamp(v ?? 0, 0, int.MaxValue);
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>The "Generated" audit row's after: everything the trail promises about a call except the context text (which is only hashed). The output is in it so the chain covers the draft.</summary>
    private static object GeneratedJson(AiActions r, IReadOnlyList<AiInputRef> inputs, AiMetering? metering) => new
    {
        feature = r.Feature, provider = r.Provider, model = r.Model, servedModel = r.ServedModel, promptTemplateVersion = r.PromptTemplateVersion, promptHash = r.PromptHash, inputHash = r.InputHash, inputs,
        outcome = r.Outcome, detail = r.Detail, output = r.OutputText, outputHash = r.OutputHash, inputTokens = r.InputTokens, outputTokens = r.OutputTokens, cacheReadTokens = r.CacheReadTokens,
        cacheWriteTokens = r.CacheWriteTokens, attempts = r.Attempts, redactions = r.Redactions, batch = r.Batch, subjectType = r.SubjectType, subjectId = r.SubjectId, requestedBy = r.RequestedByUserId,
        decision = r.Decision, costMicroUsd = metering?.CostMicroUsd, capRefusal = metering?.Refusal?.ToString(),
    };

    // ---- the human decision ----------------------------------------------------------------------------------------------------------

    public Task<ServiceResult<AiActionView>> DecideAsync(string actionId, string decision, string? finalText, string? note, Actor actor, bool isAdmin, int? expectedVersion, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var row = await db.Set<AiActions>().SingleOrDefaultAsync(a => a.Id == actionId, ct);
            if (row is null) return ServiceResult<AiActionView>.NotFound("AI action");
            if (VersionMismatch(expectedVersion, row.Version)) return ServiceResult<AiActionView>.Conflict(await ViewAsync(db, row, ct));
            var d = AiDecisions.Decidable.FirstOrDefault(x => string.Equals(x, decision?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (d is null) return Fail(AiGuards.AiActionDecision, "The decision is Accepted, Edited or Rejected");
            if (row.Outcome != nameof(AiOutcome.Completed)) return Fail(AiGuards.AiActionNotDecidable, "This call produced no draft (it was refused or failed), so there is nothing to decide");
            if (row.Decision != AiDecisions.Pending) return Fail(AiGuards.AiActionDecided, $"This draft was already {row.Decision.ToLowerInvariant()}; a human decision on an AI action is final");
            if (!isAdmin && row.RequestedByUserId != actor.UserId) return Fail(AiGuards.AiActionRole, "Only the person who asked for the draft, or an administrator, can record the decision on it");
            if (note is { Length: > 1000 }) return Fail(AiGuards.AiActionDecision, "A note is at most 1000 characters");
            var edited = d == AiDecisions.Edited;
            if (edited && string.IsNullOrWhiteSpace(finalText)) return Fail(AiGuards.AiActionDecision, "An edited decision needs the edited text");
            if (edited && finalText!.Length > 200000) return Fail(AiGuards.AiActionDecision, "The edited text is at most 200000 characters");
            if (edited && finalText == row.OutputText) return Fail(AiGuards.AiActionDecision, "The edited text is the same as the draft; record it as Accepted");
            if (!edited && !string.IsNullOrWhiteSpace(finalText)) return Fail(AiGuards.AiActionDecision, "Only an Edited decision carries text");
            var now = Now;
            var before = new { decision = row.Decision, output = row.OutputText, outputHash = row.OutputHash };
            row.Decision = d; row.FinalText = edited ? finalText : null; row.FinalHash = edited ? AiHash.Sha256(finalText!) : null; row.DecisionNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
            row.DecidedByUserId = actor.UserId; row.DecidedAt = now; row.LastChangedByUserId = actor.UserId; row.LastChangedAt = now; row.Version++;
            Audit(db, actor, null, "AiAction", row.Id, d, before, new { decision = d, final = row.FinalText, finalHash = row.FinalHash, note = row.DecisionNote, outputHash = row.OutputHash, decidedBy = actor.UserId }).ReleaseTrainId = row.ReleaseTrainId;
            await db.SaveChangesAsync(ct);
            return ServiceResult<AiActionView>.Ok(await ViewAsync(db, row, ct));
        }, ct);

    private static ServiceResult<AiActionView> Fail(string guard, string message) => ServiceResult<AiActionView>.Fail(new GuardFailure(guard, message));

    // ---- reads -----------------------------------------------------------------------------------------------------------------------

    public async Task<AiActionView?> GetAsync(string actionId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var row = await db.Set<AiActions>().AsNoTracking().SingleOrDefaultAsync(a => a.Id == actionId, ct);
        return row is null ? null : await ViewAsync(db, row, ct);
    }

    public async Task<IReadOnlyList<AiActionSummary>> ListAsync(AiActionFilter f, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var q = db.Set<AiActions>().AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(f.Feature)) q = q.Where(a => a.Feature == f.Feature);
        if (!string.IsNullOrWhiteSpace(f.Decision)) q = q.Where(a => a.Decision == f.Decision);
        if (!string.IsNullOrWhiteSpace(f.Outcome)) q = q.Where(a => a.Outcome == f.Outcome);
        if (!string.IsNullOrWhiteSpace(f.RequestedByUserId)) q = q.Where(a => a.RequestedByUserId == f.RequestedByUserId);
        if (!string.IsNullOrWhiteSpace(f.SubjectType)) q = q.Where(a => a.SubjectType == f.SubjectType);
        if (!string.IsNullOrWhiteSpace(f.SubjectId)) q = q.Where(a => a.SubjectId == f.SubjectId);
        var rows = await q.OrderByDescending(a => a.RequestedAt).ThenByDescending(a => a.Id).Take(Math.Clamp(f.Limit, 1, 500)).ToListAsync(ct);
        var names = await NamesAsync(db, rows.SelectMany(r => new[] { r.RequestedByUserId, r.DecidedByUserId }), ct);
        var blocked = rows.Where(r => r.Outcome == nameof(AiOutcome.Blocked)).Select(r => r.Id).ToList();
        var caps = blocked.Count == 0 ? [] : await db.Set<AiUsageLedger>().AsNoTracking().Where(l => l.State == "Refused" && blocked.Contains(l.AiActionId!)).ToDictionaryAsync(l => l.AiActionId!, l => l.RefusalReason, ct);
        return [.. rows.Select(r => new AiActionSummary(r.Id, r.Feature, FeatureName(r.Feature), r.Model, r.Outcome, r.Decision, r.InputTokens, r.OutputTokens, r.Decision == AiDecisions.Edited, r.SubjectType, r.SubjectId,
            Who(names, r.RequestedByUserId), r.RequestedAt, Who(names, r.DecidedByUserId), r.DecidedAt, caps.GetValueOrDefault(r.Id)))];
    }

    private static string FeatureName(string key) => AiFeatures.Find(key)?.Name ?? key;
    private static AiPersonRef? Who(Dictionary<string, string> names, string? id) => id is null ? null : new(id, names.GetValueOrDefault(id, id));
    private static async Task<Dictionary<string, string>> NamesAsync(ReleaseDbContext db, IEnumerable<string?> ids, CancellationToken ct)
    {
        var list = ids.Where(i => i is not null).Select(i => i!).Distinct().ToList();
        return list.Count == 0 ? [] : await db.Set<Users>().AsNoTracking().Where(u => list.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
    }

    private static async Task<AiActionView> ViewAsync(ReleaseDbContext db, AiActions r, CancellationToken ct)
    {
        var names = await NamesAsync(db, [r.RequestedByUserId, r.DecidedByUserId], ct);
        var audit = await db.Set<AuditEvents>().AsNoTracking().Where(a => a.EntityType == "AiAction" && a.EntityId == r.Id).OrderBy(a => a.Id).Select(a => new { a.Action, a.AfterJson }).ToListAsync(ct);
        var inputs = JsonSerializer.Deserialize<List<AiInputRef>>(r.InputRefsJson, Camel) ?? [];
        static string? Prop(string? json, string name)
        {
            if (json is null) return null;
            try { using var d = JsonDocument.Parse(json); return d.RootElement.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null; }
            catch (JsonException) { return null; }
        }
        var generated = audit.FirstOrDefault(a => a.Action is "Generated" or "Refused" or "Failed");
        var decided = audit.FirstOrDefault(a => a.Action is "Accepted" or "Edited" or "Rejected");
        var cap = r.Outcome == nameof(AiOutcome.Blocked) ? await db.Set<AiUsageLedger>().AsNoTracking().Where(l => l.AiActionId == r.Id && l.State == "Refused").Select(l => l.RefusalReason).SingleOrDefaultAsync(ct) : null;
        var outputOk = r.OutputText is null ? r.OutputHash is null : AiHash.Sha256(r.OutputText) == r.OutputHash;
        var finalOk = r.FinalText is null ? r.FinalHash is null : AiHash.Sha256(r.FinalText) == r.FinalHash;
        var generatedOk = generated is not null && Prop(generated.AfterJson, "outputHash") == r.OutputHash;
        var decisionOk = r.Decision is AiDecisions.Pending or AiDecisions.NotApplicable ? decided is null : decided is not null && decided.Action == r.Decision && Prop(decided.AfterJson, "finalHash") == r.FinalHash;
        return new AiActionView(r.Id, r.Feature, FeatureName(r.Feature), r.Provider, r.Model, r.ServedModel, r.PromptTemplateVersion, r.PromptHash, inputs, r.InputHash, r.Outcome, r.Detail, r.OutputText, r.OutputHash,
            r.InputTokens, r.OutputTokens, r.CacheReadTokens, r.CacheWriteTokens, r.Attempts, r.Redactions, r.Batch, r.SubjectType, r.SubjectId, r.ReleaseTrainId, Who(names, r.RequestedByUserId), r.RequestedAt,
            r.Decision, r.FinalText, r.FinalHash, r.DecisionNote, Who(names, r.DecidedByUserId), r.DecidedAt, r.Version, new AiActionIntegrity(outputOk, finalOk, generatedOk, decisionOk, outputOk && finalOk && generatedOk && decisionOk), cap);
    }
}
