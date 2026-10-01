using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Api.Auth;

/// <summary>
/// Just-in-time provisioning (Q-003): keeps Users (read by the SoD triggers) in step with the role the user signs in with.
/// REOS-64 (rule 3): every real change is one <c>AuditEvents</c> row in the same transaction, acted by the user signing in (the identity provider vouched
/// for them; Q-SEC-F2): <c>SignInCreate</c> for a new account, <c>SignInUpdate</c> with only the changed columns before and after, and a Version bump.
/// A sign-in that changes nothing writes nothing.
/// </summary>
public sealed class UserProvisioner(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time)
{
    public const string CreateAction = "SignInCreate";
    public const string UpdateAction = "SignInUpdate";

    public async Task<string> UpsertAsync(string email, string displayName, string role, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var u = await db.Set<Users>().SingleOrDefaultAsync(x => x.Email == email, ct); // Email is COLLATE NOCASE in the schema
        if (u is null)
        {
            u = new Users { Id = Ids.New(), Email = email, DisplayName = displayName, Role = role };
            db.Set<Users>().Add(u);
            Audit(db, u.Id, CreateAction, null, new Dictionary<string, object?> { ["Email"] = email, ["DisplayName"] = displayName, ["Role"] = role, ["IsActive"] = true });
        }
        else
        {
            var before = new Dictionary<string, object?>();
            var after = new Dictionary<string, object?>();
            if (u.Role != role) { before["Role"] = u.Role; after["Role"] = role; u.Role = role; }
            if (u.DisplayName != displayName) { before["DisplayName"] = u.DisplayName; after["DisplayName"] = displayName; u.DisplayName = displayName; }
            if (!u.IsActive) { before["IsActive"] = false; after["IsActive"] = true; u.IsActive = true; }
            if (after.Count > 0)
            {
                u.Version++;
                Audit(db, u.Id, UpdateAction, before, after);
            }
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return u.Id;
    }

    private void Audit(ReleaseDbContext db, string userId, string action, object? before, object after)
    {
        var t = time.GetUtcNow().UtcDateTime;
        db.Set<AuditEvents>().Add(new AuditEvents
        {
            OccurredAt = new DateTime(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc),   // whole seconds, as ServiceBase stamps them
            ActorUserId = userId, EntityType = "User", EntityId = userId, Action = action,
            BeforeJson = before is null ? null : JsonSerializer.Serialize(before),
            AfterJson = JsonSerializer.Serialize(after),
        });
    }
}
