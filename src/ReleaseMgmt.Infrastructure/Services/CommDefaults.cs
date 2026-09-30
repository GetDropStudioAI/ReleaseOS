using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

/// <summary>
/// The default comm library and T-minus plan (PROJECT_SCOPE 5.2: T-7, T-3, T-1, T0 start, T0 complete, hypercare exit), created by the reference seed the first
/// time the library is empty, and attached to the seeded default train template while that template has no plan. It never touches a library that has entries,
/// so an admin who deleted or rewrote them is not overruled at the next start.
/// </summary>
public static class CommDefaults
{
    public sealed record Entry(string Name, string Type, string Audience, int OffsetDays, string Subject, string Body);

    public static readonly IReadOnlyList<Entry> Entries =
    [
        new("T-7 Readiness notice", "Tminus7", "All", -7, "[{ReleaseTitle}] Readiness notice: target {TargetDate}",
            "**{ReleaseTitle}** targets **{TargetDate}** ({DaysToTarget} business days). Status: {Status}.\nWindow: {Window}\nChange: {ChangeTicket}\n\n**Scope** ({ProductCount} products)\n{ProductList}\n\n**Progress**\n{TasksDone} of {TasksTotal} tasks done ({PercentComplete}). Next gate: {NextGate}, due {NextGateDue}.\n\n**Open blockers** ({BlockerCount})\n{BlockerList}\n\nQuestions: {Owners}"),
        new("T-3 Checkpoint", "Tminus3", "Ops", -3, "[{ReleaseTitle}] Checkpoint: {DaysToTarget} business days to go",
            "**{ReleaseTitle}** checkpoint. Status: {Status}. Window: {Window}.\n\n**Gates**\n{GateTable}\n\n**Critical blockers**\n{CriticalBlockers}\n\n**Open blockers** ({BlockerCount})\n{BlockerList}\n\nQuestions: {Owners}"),
        new("T-1 Go/No-Go outcome", "GoNoGo", "All", -1, "[{ReleaseTitle}] Go/No-Go: {GoNoGoDecision}",
            "**{ReleaseTitle}** is {GoNoGoDecision}.\nWindow: {Window}\nChange: {ChangeTicket}\n\n**Conditions**\n{Conditions}\n\n**Scope** ({ProductCount} products)\n{ProductList}\n\n**Gates**\n{GateTable}\n\n**Open blockers** ({BlockerCount})\n{BlockerList}\n\n**Known issues**\n{KnownIssues}\n\nQuestions: {Owners}"),
        new("T0 Cutover start", "Cutover", "Ops", 0, "[{ReleaseTitle}] Cutover starting: {Window}",
            "Cutover for **{ReleaseTitle}** is starting. Window: {Window}. Change: {ChangeTicket}.\n\n**Scope** ({ProductCount} products)\n{ProductList}\n\n**Open blockers** ({BlockerCount})\n{BlockerList}\n\nQuestions: {Owners}"),
        new("T0 Release complete", "Complete", "All", 0, "[{ReleaseTitle}] Release complete: {CloseCode}",
            "**{ReleaseTitle}** is {Status}. Close code: {CloseCode}.\nChange: {ChangeTicket}\n\n**Scope** ({ProductCount} products)\n{ProductList}\n\n**Known issues**\n{KnownIssues}\n\nQuestions: {Owners}"),
        new("Hypercare exit", "HypercareExit", "All", 5, "[{ReleaseTitle}] Hypercare ended",
            "Hypercare for **{ReleaseTitle}** has ended. Close code: {CloseCode}.\n\n**Known issues still open**\n{KnownIssues}\n\nQuestions: {Owners}"),
    ];

    /// <summary>Idempotent. Returns true when the library was empty and the defaults were added.</summary>
    public static async Task<bool> EnsureAsync(IDbContextFactory<ReleaseDbContext> dbf, string defaultTemplateName, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        if (await db.Set<CommTemplateLibrary>().AnyAsync(ct)) return false;
        var ids = new Dictionary<string, string>();
        foreach (var e in Entries)
        {
            var l = new CommTemplateLibrary { Id = Ids.New(), Name = e.Name, TemplateType = e.Type, Audience = e.Audience, SubjectLine = e.Subject, MarkdownBody = e.Body };
            db.Set<CommTemplateLibrary>().Add(l);
            ids[e.Name] = l.Id;
        }
        var t = await db.Set<TrainTemplates>().SingleOrDefaultAsync(x => x.Name == defaultTemplateName, ct);
        if (t is not null && !await db.Set<TemplateCommSchedule>().AnyAsync(p => p.TemplateId == t.Id, ct))
            foreach (var e in Entries)
                db.Set<TemplateCommSchedule>().Add(new TemplateCommSchedule { Id = Ids.New(), TemplateId = t.Id, LibraryTemplateId = ids[e.Name], OffsetDays = e.OffsetDays });
        await db.SaveChangesAsync(ct);
        return true;
    }
}
