using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

/// <summary>Guard names of the communications stories (REOS-43/44). Kept apart from <see cref="Guards"/> so parallel streams do not collide.</summary>
public static class CommGuards
{
    public const string CommTemplateInvalid = "CommTemplateInvalid";
    public const string CommTemplateNameTaken = "CommTemplateNameTaken";
    public const string CommTemplateDispatched = "CommTemplateDispatched";
    public const string CommScheduleExists = "CommScheduleExists";
    public const string CommPlanEmpty = "CommPlanEmpty";
    public const string CommTemplateRetired = "CommTemplateRetired";
    public const string CommAlreadySent = "CommAlreadySent";
    public const string CommPreviewSource = CommHydrationService.SourceMissing;
}

public sealed record LibraryTemplateInput(string? Name, string? TemplateType, string? Audience, string? SubjectLine, string? MarkdownBody);
public sealed record TrainCommTemplateInput(string? Audience, string? SubjectLine, string? MarkdownBody);
public sealed record CopyToTrainInput(string? LibraryTemplateId);

/// <summary>A library entry. <see cref="TokenErrors"/> is empty when every token is on the allowlist; saving with errors is allowed (dispatch is the gate).</summary>
public sealed record LibraryTemplateView(string Id, string TemplateType, string Name, string Audience, string SubjectLine, string MarkdownBody, int Version,
    IReadOnlyList<string> TokensUsed, IReadOnlyList<TokenError> TokenErrors, int TrainCopies)
{
    public bool Valid => TokenErrors.Count == 0;
}

/// <summary>A per-train copy. <see cref="Dispatched"/> is true once any send was logged; the copy is then read-only.</summary>
public sealed record TrainCommTemplateView(string Id, string ReleaseTrainId, string? LibraryTemplateId, string? LibraryName, string TemplateType, string Audience,
    string SubjectLine, string MarkdownBody, int Version, bool Dispatched, int DispatchCount, IReadOnlyList<string> TokensUsed, IReadOnlyList<TokenError> TokenErrors)
{
    public bool Valid => TokenErrors.Count == 0;
}

/// <summary>
/// The comm template library and the per-train copies (REOS-43). Library entries are shared source text; a train gets its own editable copy
/// (<c>CommTemplates</c>) that stops being editable once anything was dispatched from it. Every write bumps Version and writes one AuditEvents row
/// in the same transaction. Token errors never block a save (the editor shows them, and dispatch refuses while they exist).
/// </summary>
public sealed class CommLibraryService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IRealtimePublisher? realtime = null) : ServiceBase(dbf, time, realtime)
{
    public static readonly string[] Audiences = ["Exec", "Ops", "SupportDesk", "Clients", "All"];
    public const int MaxSubject = 200, MaxBody = 20000, MaxType = 40;

    // ---- library reads
    public async Task<List<LibraryTemplateView>> ListLibraryAsync(CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var copies = await db.Set<CommTemplates>().AsNoTracking().Where(c => c.LibraryTemplateId != null).GroupBy(c => c.LibraryTemplateId!).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        return (await db.Set<CommTemplateLibrary>().AsNoTracking().OrderBy(l => l.Name).ToListAsync(ct)).Select(l => Show(l, copies.GetValueOrDefault(l.Id))).ToList();
    }

    public async Task<LibraryTemplateView?> GetLibraryAsync(string id, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var l = await db.Set<CommTemplateLibrary>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        return l is null ? null : Show(l, await db.Set<CommTemplates>().CountAsync(c => c.LibraryTemplateId == id, ct));
    }

    // ---- library writes
    public Task<ServiceResult<LibraryTemplateView>> CreateLibraryAsync(LibraryTemplateInput i, Actor actor, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var bad = await ValidateLibraryAsync(db, i, null, ct);
            if (bad.Count > 0) return ServiceResult<LibraryTemplateView>.Fail(bad);
            var l = new CommTemplateLibrary { Id = Ids.New(), Name = i.Name!.Trim(), TemplateType = i.TemplateType!.Trim(), Audience = i.Audience!, SubjectLine = i.SubjectLine!.Trim(), MarkdownBody = i.MarkdownBody! };
            db.Set<CommTemplateLibrary>().Add(l);
            Audit(db, actor, null, "CommLibraryTemplate", l.Id, "Create", null, Shape(l));
            await db.SaveChangesAsync(ct);
            return ServiceResult<LibraryTemplateView>.Ok(Show(l, 0));
        }, ct);

    public Task<ServiceResult<LibraryTemplateView>> UpdateLibraryAsync(string id, LibraryTemplateInput i, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var l = await db.Set<CommTemplateLibrary>().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (l is null) return ServiceResult<LibraryTemplateView>.NotFound("library template");
            var copies = await db.Set<CommTemplates>().CountAsync(c => c.LibraryTemplateId == id, ct);
            if (VersionMismatch(expectedVersion, l.Version)) return ServiceResult<LibraryTemplateView>.Conflict(Show(l, copies));
            var bad = await ValidateLibraryAsync(db, i, id, ct);
            if (bad.Count > 0) return ServiceResult<LibraryTemplateView>.Fail(bad);
            var before = Shape(l);
            l.Name = i.Name!.Trim(); l.TemplateType = i.TemplateType!.Trim(); l.Audience = i.Audience!; l.SubjectLine = i.SubjectLine!.Trim(); l.MarkdownBody = i.MarkdownBody!;
            l.Version++;
            // Existing per-train copies are deliberately untouched: they are the train's own text.
            Audit(db, actor, null, "CommLibraryTemplate", id, "Update", before, Shape(l));
            await db.SaveChangesAsync(ct);
            return ServiceResult<LibraryTemplateView>.Ok(Show(l, copies));
        }, ct);

    // ---- per-train copies
    public async Task<List<TrainCommTemplateView>?> ListForTrainAsync(string trainId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        if (!await db.Set<ReleaseTrains>().AnyAsync(t => t.Id == trainId, ct)) return null;
        var rows = await db.Set<CommTemplates>().AsNoTracking().Where(c => c.ReleaseTrainId == trainId).ToListAsync(ct);
        var names = await LibraryNamesAsync(db, ct);
        var result = new List<TrainCommTemplateView>();
        foreach (var r in rows) result.Add(await ShowTrainAsync(db, r, names, ct));
        return [.. result.OrderBy(v => v.TemplateType).ThenBy(v => v.LibraryName)];
    }

    public async Task<TrainCommTemplateView?> GetForTrainAsync(string id, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var r = await db.Set<CommTemplates>().AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct);
        return r is null ? null : await ShowTrainAsync(db, r, await LibraryNamesAsync(db, ct), ct);
    }

    /// <summary>Copies a library entry into a train as its own editable message. A train may hold several copies of one entry.</summary>
    public Task<ServiceResult<TrainCommTemplateView>> CopyToTrainAsync(string trainId, CopyToTrainInput i, Actor actor, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            if (!await db.Set<ReleaseTrains>().AnyAsync(t => t.Id == trainId, ct)) return ServiceResult<TrainCommTemplateView>.NotFound("train");
            if (string.IsNullOrWhiteSpace(i.LibraryTemplateId)) return ServiceResult<TrainCommTemplateView>.Fail(new GuardFailure(CommGuards.CommTemplateInvalid, "Choose the library template to copy"));
            var l = await db.Set<CommTemplateLibrary>().SingleOrDefaultAsync(x => x.Id == i.LibraryTemplateId, ct);
            if (l is null) return ServiceResult<TrainCommTemplateView>.NotFound("library template");
            var c = CopyOf(trainId, l);
            db.Set<CommTemplates>().Add(c);
            Audit(db, actor, trainId, "CommTemplate", c.Id, "Copy", null, new { fromLibrary = l.Id, libraryVersion = l.Version, c.TemplateType, c.Audience, c.SubjectLine });
            await db.SaveChangesAsync(ct);
            return ServiceResult<TrainCommTemplateView>.Ok(await ShowTrainAsync(db, c, await LibraryNamesAsync(db, ct), ct));
        }, ct);

    /// <summary>Edits the train's own copy. Refused (CommTemplateDispatched) once any send was logged from it: the log points at this row.</summary>
    public Task<ServiceResult<TrainCommTemplateView>> UpdateForTrainAsync(string id, TrainCommTemplateInput i, Actor actor, int? expectedVersion, CancellationToken ct = default) =>
        RunAsync(async db =>
        {
            var c = await db.Set<CommTemplates>().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (c is null) return ServiceResult<TrainCommTemplateView>.NotFound("message template");
            var names = await LibraryNamesAsync(db, ct);
            if (VersionMismatch(expectedVersion, c.Version)) return ServiceResult<TrainCommTemplateView>.Conflict(await ShowTrainAsync(db, c, names, ct));
            if (await db.Set<CommDispatches>().AnyAsync(d => d.CommTemplateId == id, ct))
                return ServiceResult<TrainCommTemplateView>.Fail(new GuardFailure(CommGuards.CommTemplateDispatched, "This message was already sent, so it can no longer be edited. Copy the library template again for a new message"));
            var bad = ValidateText(i.Audience, i.SubjectLine, i.MarkdownBody);
            if (bad.Count > 0) return ServiceResult<TrainCommTemplateView>.Fail(bad);
            var before = new { c.Audience, c.SubjectLine, c.MarkdownBody };
            c.Audience = i.Audience!; c.SubjectLine = i.SubjectLine!.Trim(); c.MarkdownBody = i.MarkdownBody!;
            c.Version++;
            Audit(db, actor, c.ReleaseTrainId, "CommTemplate", id, "Update", before, new { c.Audience, c.SubjectLine, c.MarkdownBody });
            await db.SaveChangesAsync(ct);
            return ServiceResult<TrainCommTemplateView>.Ok(await ShowTrainAsync(db, c, names, ct));
        }, ct);

    internal static CommTemplates CopyOf(string trainId, CommTemplateLibrary l) => new()
    {
        Id = Ids.New(), ReleaseTrainId = trainId, LibraryTemplateId = l.Id, TemplateType = l.TemplateType, Audience = l.Audience, SubjectLine = l.SubjectLine, MarkdownBody = l.MarkdownBody,
    };

    // ---- helpers
    private static async Task<Dictionary<string, string>> LibraryNamesAsync(ReleaseDbContext db, CancellationToken ct) =>
        await db.Set<CommTemplateLibrary>().AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Name, ct);

    private static List<GuardFailure> ValidateText(string? audience, string? subject, string? body)
    {
        var f = new List<GuardFailure>();
        GuardFailure Bad(string m) => new(CommGuards.CommTemplateInvalid, m);
        if (audience is null || !Audiences.Contains(audience)) f.Add(Bad($"Audience must be one of {string.Join(", ", Audiences)}"));
        if (string.IsNullOrWhiteSpace(subject)) f.Add(Bad("A message needs a subject line"));
        else if (subject.Trim().Length > MaxSubject) f.Add(Bad($"The subject line is limited to {MaxSubject} characters"));
        else if (subject.Contains('\n') || subject.Contains('\r')) f.Add(Bad("The subject line must be a single line"));
        if (string.IsNullOrWhiteSpace(body)) f.Add(Bad("A message needs a body"));
        else if (body.Length > MaxBody) f.Add(Bad($"The body is limited to {MaxBody} characters"));
        return f;
    }

    private static async Task<List<GuardFailure>> ValidateLibraryAsync(ReleaseDbContext db, LibraryTemplateInput i, string? selfId, CancellationToken ct)
    {
        var f = ValidateText(i.Audience, i.SubjectLine, i.MarkdownBody);
        var name = (i.Name ?? "").Trim();
        if (name.Length == 0) f.Add(new(CommGuards.CommTemplateInvalid, "A library template needs a name"));
        else if (await db.Set<CommTemplateLibrary>().AnyAsync(x => x.Id != selfId && x.Name.ToLower() == name.ToLower(), ct))
            f.Add(new(CommGuards.CommTemplateNameTaken, $"A library template named \"{name}\" already exists"));
        var type = (i.TemplateType ?? "").Trim();
        if (type.Length == 0) f.Add(new(CommGuards.CommTemplateInvalid, "A library template needs a type, for example GoNoGo or Cutover"));
        else if (type.Length > MaxType) f.Add(new(CommGuards.CommTemplateInvalid, $"The type is limited to {MaxType} characters"));
        return f;
    }

    private static (IReadOnlyList<string> Used, IReadOnlyList<TokenError> Errors) Check(string subject, string body)
    {
        var s = TokenParser.Parse(subject, "subject"); var b = TokenParser.Parse(body, "body");
        return ([.. s.Tokens.Concat(b.Tokens).Distinct()], [.. s.Errors, .. b.Errors]);
    }

    private static LibraryTemplateView Show(CommTemplateLibrary l, int copies)
    {
        var (used, errors) = Check(l.SubjectLine, l.MarkdownBody);
        return new(l.Id, l.TemplateType, l.Name, l.Audience, l.SubjectLine, l.MarkdownBody, l.Version, used, errors, copies);
    }

    private static async Task<TrainCommTemplateView> ShowTrainAsync(ReleaseDbContext db, CommTemplates c, Dictionary<string, string> names, CancellationToken ct)
    {
        var n = await db.Set<CommDispatches>().CountAsync(d => d.CommTemplateId == c.Id, ct);
        var (used, errors) = Check(c.SubjectLine, c.MarkdownBody);
        return new(c.Id, c.ReleaseTrainId, c.LibraryTemplateId, c.LibraryTemplateId is not null && names.TryGetValue(c.LibraryTemplateId, out var name) ? name : null,
            c.TemplateType, c.Audience, c.SubjectLine, c.MarkdownBody, c.Version, n > 0, n, used, errors);
    }

    private static object Shape(CommTemplateLibrary l) => new { l.Name, l.TemplateType, l.Audience, l.SubjectLine, l.MarkdownBody };
}
