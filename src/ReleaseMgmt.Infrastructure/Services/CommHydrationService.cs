using System.Data;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;
using ReleaseMgmt.Infrastructure.Reminders;

namespace ReleaseMgmt.Infrastructure.Services;

/// <summary>What to hydrate. Exactly one source: a per-train template, a library template, or ad-hoc text (an unsaved edit in the editor).</summary>
public sealed record HydrateSource(string? TemplateId = null, string? LibraryTemplateId = null, string? Subject = null, string? Text = null)
{
    public int Sources => (TemplateId is null ? 0 : 1) + (LibraryTemplateId is null ? 0 : 1) + (Text is null ? 0 : 1);
}

/// <summary>
/// A hydrated message. <see cref="AsOf"/> is the service clock when the snapshot was read and <see cref="TrainVersion"/> the train's Version in that same
/// snapshot: a client that later sees a higher version knows the text is stale. <see cref="CanDispatch"/> is false while <see cref="TokenErrors"/> is non-empty;
/// dispatch (REOS-45) must refuse then and must store <see cref="Subject"/> and <see cref="Text"/> verbatim.
/// </summary>
public sealed record HydrationResult(string? Subject, string Text, IReadOnlyList<TokenError> TokenErrors, DateTime AsOf, int TrainVersion, bool CanDispatch,
    CommTarget Target, IReadOnlyList<string> TokensUsed, string? TemplateId, int? TemplateVersion);

/// <summary>
/// Token hydration (PROJECT_SCOPE 5.2). Everything a token can read, and the template itself, is loaded inside ONE read transaction: SQLite (WAL) gives
/// the reader the database as of its first read for the whole transaction, so a write that commits between two queries cannot produce a mixed view.
/// Rendering then runs on the frozen <see cref="HydrationSnapshot"/> with no further database access.
/// </summary>
public sealed class CommHydrationService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, DisplayClock clock, Func<string, Task>? snapshotProbe = null)
{
    public const string SourceMissing = "CommPreviewSource";
    public const string TooLarge = "CommPreviewTooLarge";

    private DateTime Now
    {
        get { var t = time.GetUtcNow().UtcDateTime; return new DateTime(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc); }
    }

    /// <summary>The single entry point for preview and (REOS-45) dispatch. Not found: the train, or the named template.</summary>
    public async Task<ServiceResult<HydrationResult>> HydrateAsync(string trainId, HydrateSource source, CommTarget target, CancellationToken ct = default)
    {
        if (source.Sources != 1)
            return ServiceResult<HydrationResult>.Fail(new GuardFailure(SourceMissing, "Give exactly one of templateId, libraryTemplateId or text to preview"));
        // Ad-hoc text (any signed-in role may preview) is held to the limits a saved template has, so a preview never parses more than a template could hold (SEC-D1).
        if (source.Text is { Length: > CommLibraryService.MaxBody } || (source.Subject?.Trim().Length ?? 0) > CommLibraryService.MaxSubject)
            return ServiceResult<HydrationResult>.Fail(new GuardFailure(TooLarge,
                $"A message is limited to a {CommLibraryService.MaxSubject}-character subject and a {CommLibraryService.MaxBody:N0}-character body, as a saved template is"));
        await using var db = await dbf.CreateDbContextAsync(ct);
        await using var tx = await BeginReadAsync(db, ct);

        string? subject = source.Subject, body = source.Text, templateId = null;
        int? templateVersion = null;
        if (source.TemplateId is not null)
        {
            var t = await db.Set<CommTemplates>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == source.TemplateId && x.ReleaseTrainId == trainId, ct);
            if (t is null) return ServiceResult<HydrationResult>.NotFound("message template");
            (subject, body, templateId, templateVersion) = (t.SubjectLine, t.MarkdownBody, t.Id, t.Version);
            await ProbeAsync("template");
        }
        else if (source.LibraryTemplateId is not null)
        {
            var l = await db.Set<CommTemplateLibrary>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == source.LibraryTemplateId, ct);
            if (l is null) return ServiceResult<HydrationResult>.NotFound("library template");
            (subject, body, templateId, templateVersion) = (l.SubjectLine, l.MarkdownBody, l.Id, l.Version);
            await ProbeAsync("template");
        }

        var snap = await LoadAsync(db, trainId, ct);
        if (snap is null) return ServiceResult<HydrationResult>.NotFound("train");
        await tx.CommitAsync(ct);   // read-only: nothing to write, the commit just ends the snapshot

        var m = CommHydrator.Hydrate(snap, clock.Zone, subject, body, target);
        return ServiceResult<HydrationResult>.Ok(new HydrationResult(m.Subject, m.Text, m.TokenErrors, snap.AsOf, snap.TrainVersion, m.CanDispatch, target, m.TokensUsed, templateId, templateVersion));
    }

    /// <summary>The frozen token inputs for a train, read in one transaction. Null when the train does not exist.</summary>
    public async Task<HydrationSnapshot?> LoadSnapshotAsync(string trainId, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        await using var tx = await BeginReadAsync(db, ct);
        var snap = await LoadAsync(db, trainId, ct);
        await tx.CommitAsync(ct);
        return snap;
    }

    /// <summary>Pure re-render of an already loaded snapshot (no database), for callers that hold one.</summary>
    public HydratedMessage Render(HydrationSnapshot snapshot, string? subject, string? body, CommTarget target) =>
        CommHydrator.Hydrate(snapshot, clock.Zone, subject, body, target);

    /// <summary>
    /// A DEFERRED read transaction (plain BEGIN): it takes no write lock, so writers are never blocked, and in WAL mode the snapshot is fixed by the first read.
    /// EF Core's own BeginTransaction would issue BEGIN IMMEDIATE (a write lock held for the whole hydration), which is why this opens the SQLite transaction itself.
    /// </summary>
    private static async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction> BeginReadAsync(ReleaseDbContext db, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        var conn = (Microsoft.Data.Sqlite.SqliteConnection)db.Database.GetDbConnection();
        var tx = conn.BeginTransaction(IsolationLevel.ReadCommitted, deferred: true);
        return await db.Database.UseTransactionAsync(tx, ct) ?? throw new InvalidOperationException("The read transaction could not be attached");
    }

    private Task ProbeAsync(string step) => snapshotProbe is null ? Task.CompletedTask : snapshotProbe(step);

    private async Task<HydrationSnapshot?> LoadAsync(ReleaseDbContext db, string trainId, CancellationToken ct)
    {
        var train = await db.Set<ReleaseTrains>().AsNoTracking().SingleOrDefaultAsync(t => t.Id == trainId, ct);
        if (train is null) return null;
        var asOf = Now;
        await ProbeAsync("train");

        var window = await db.Set<DeploymentWindows>().AsNoTracking().SingleOrDefaultAsync(w => w.ReleaseTrainId == trainId, ct);
        var products = await db.Set<BundledProducts>().AsNoTracking().Where(p => p.ReleaseTrainId == trainId).ToListAsync(ct);
        var gates = await db.Set<StageGates>().AsNoTracking().Where(g => g.ReleaseTrainId == trainId).OrderBy(g => g.SequenceOrder).ToListAsync(ct);
        var gateIds = gates.Select(g => g.Id).ToList();
        var tasksTotal = await db.Set<ChecklistTasks>().CountAsync(t => gateIds.Contains(t.StageGateId), ct);
        var tasksDone = await db.Set<ChecklistTasks>().CountAsync(t => gateIds.Contains(t.StageGateId) && t.IsCompleted, ct);
        await ProbeAsync("tasks");

        var blockers = await db.Set<Blockers>().AsNoTracking().Where(b => b.ReleaseTrainId == trainId && b.ResolvedAt == null).ToListAsync(ct);
        var decision = await db.Set<GoNoGoDecisions>().AsNoTracking().Where(d => d.ReleaseTrainId == trainId).OrderByDescending(d => d.DecidedAt).ThenByDescending(d => d.Id).FirstOrDefaultAsync(ct);
        var conditions = decision is null ? [] : await db.Set<GoNoGoConditions>().AsNoTracking().Where(c => c.DecisionId == decision.Id && c.ClosedAt == null).ToListAsync(ct);
        var issues = await db.Set<KnownIssues>().AsNoTracking().Where(k => k.ReleaseTrainId == trainId).ToListAsync(ct);
        var holidays = (await db.Set<Holidays>().AsNoTracking().Select(h => h.Day).ToListAsync(ct)).ToHashSet();
        await ProbeAsync("lists");

        var userIds = gates.Select(g => g.OwnerUserId).Concat(blockers.Select(b => b.OwnerUserId)).Concat(conditions.Select(c => c.OwnerUserId))
            .Append(decision?.DecidedByUserId).Where(x => x is not null).Distinct().ToList();
        var users = await db.Set<Users>().AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var teamIds = gates.Select(g => g.OwnerTeamId).Where(x => x is not null).Distinct().ToList();
        var teams = await db.Set<Teams>().AsNoTracking().Where(t => teamIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        string? User(string? id) => id is not null && users.TryGetValue(id, out var n) ? n : null;

        // Owners: who to ask. Gate owners (person, else team) in gate order, then open-blocker owners, then whoever recorded the Go/No-Go. Distinct, first seen wins.
        var owners = gates.Select(g => User(g.OwnerUserId) ?? (g.OwnerTeamId is not null && teams.TryGetValue(g.OwnerTeamId, out var tn) ? tn : null))
            .Concat(blockers.Select(b => User(b.OwnerUserId))).Append(User(decision?.DecidedByUserId))
            .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).Distinct().ToList();

        return new HydrationSnapshot(
            train.Id, train.Version, asOf, train.Title, train.TargetReleaseDate, train.CurrentStatus, train.ChangeTicketNumber, train.CloseCode,
            window?.StartsAt, window?.EndsAt,
            [.. products.Select(p => new SnapProduct(p.ProductName, p.VersionTag))],
            [.. gates.Select(g => new SnapGate(g.GateName, g.Status, g.SequenceOrder, g.DueOn, g.CertifiedAt))],
            tasksDone, tasksTotal,
            [.. blockers.Select(b => new SnapBlocker(b.Severity, b.Title, User(b.OwnerUserId), b.RaisedAt))],
            decision is null ? null : new SnapDecision(decision.Decision, User(decision.DecidedByUserId) ?? "Unknown", decision.DecidedAt,
                [.. conditions.Select(c => new SnapCondition(c.Text, User(c.OwnerUserId) ?? "Unknown", c.ExpiresAt))]),
            [.. issues.Select(k => new SnapKnownIssue(k.Severity, k.Title, k.Workaround, k.Status, k.ExternalKey))],
            owners, holidays);
    }
}
