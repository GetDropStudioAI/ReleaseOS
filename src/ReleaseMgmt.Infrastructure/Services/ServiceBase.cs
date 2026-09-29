using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

/// <summary>
/// Shared plumbing for domain services (CLAUDE.md rules 2-4): one transaction per call, guards checked first,
/// optimistic concurrency (If-Match -> Conflict with the current row), one service audit row in the same
/// transaction, clock from TimeProvider, trigger failures mapped to a DbRule guard failure.
/// </summary>
public abstract class ServiceBase(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time)
{
    protected static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    /// <summary>Service clock truncated to whole seconds: the contract stores 'YYYY-MM-DDTHH:MM:SSZ'.</summary>
    protected DateTime Now
    {
        get { var t = time.GetUtcNow().UtcDateTime; return new DateTime(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc); }
    }

    protected async Task<ServiceResult<T>> RunAsync<T>(Func<ReleaseDbContext, Task<ServiceResult<T>>> body, CancellationToken ct)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var result = await body(db);
            if (result.IsOk) await tx.CommitAsync(ct);
            return result;
        }
        catch (DbUpdateException ex) when (DbRules.TryGetMessage(ex, out _))
        {
            DbRules.TryGetMessage(ex, out var message);
            return ServiceResult<T>.Fail(new GuardFailure(Guards.DbRule, message)); // transaction disposed -> rolled back
        }
    }

    protected static bool VersionMismatch(int? expected, int current) => expected is int v && v != current;

    /// <summary>One AuditEvents row per service write (D27).</summary>
    protected void Audit(ReleaseDbContext db, Actor actor, string? trainId, string entityType, string entityId, string action, object? before = null, object? after = null) =>
        db.Set<AuditEvents>().Add(new AuditEvents
        {
            OccurredAt = Now, ActorUserId = actor.UserId, ReleaseTrainId = trainId, EntityType = entityType, EntityId = entityId, Action = action,
            BeforeJson = before is null ? null : JsonSerializer.Serialize(before, Json),
            AfterJson = after is null ? null : JsonSerializer.Serialize(after, Json),
        });

    protected static async Task<string?> RoleOf(ReleaseDbContext db, string? userId) =>
        userId is null ? null : await db.Set<Users>().Where(u => u.Id == userId).Select(u => u.Role).SingleOrDefaultAsync();
}
