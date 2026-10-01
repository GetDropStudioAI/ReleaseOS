using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Api.Auth;

/// <summary>
/// SEC-B8 / REOS-61 (Q-SEC-B8): an organisation sign-in is matched to its Users row by the identity provider's issuer and subject
/// (<c>Users.IdpIssuer</c>, <c>Users.IdpSubject</c>), never by email. Email is contact and display data that follows the identity provider.
/// <list type="bullet">
/// <item>A bound identity signs in as its user even after its email changed; the row's Email is updated (refused if another account holds that email).</item>
/// <item>A user with no bound identity yet (made by dev-login, an admin, or before this change) is bound on first sign-in only when the token says
/// <c>email_verified=true</c>.</item>
/// <item>A different identity carrying the email of a user already bound to another subject is refused and logged at Warning.</item>
/// </list>
/// Each change to a Users row bumps Version and writes one AuditEvents row in the same transaction (CLAUDE.md rule 3), with the user as actor.
/// Kept apart from <see cref="UserProvisioner"/>, which still owns creating the row and keeping role and display name in step (Q-003).
/// </summary>
public sealed class IdpIdentityBinder(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, ILogger<IdpIdentityBinder> log)
{
    /// <summary>Before provisioning: resolves the identity. Returns null when sign-in may continue, else the refusal with its reason code (already logged).</summary>
    public async Task<OidcSignIn.SignInRefused?> PrepareAsync(string issuer, string subject, string email, bool emailVerified, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var bound = await db.Set<Users>().SingleOrDefaultAsync(u => u.IdpIssuer == issuer && u.IdpSubject == subject, ct);
        if (bound is not null)
        {
            if (bound.Email == email) return null;
            if (await db.Set<Users>().AnyAsync(u => u.Email == email && u.Id != bound.Id, ct))   // Email is COLLATE NOCASE in the schema
            {
                log.LogWarning("Organisation sign-in refused for user {UserId}: the identity provider now gives them the email of another account", bound.Id);
                return new(OidcSignIn.Reasons.IdentityConflict, "Your email address at the identity provider belongs to another account in this app. Ask an administrator.");
            }
            var before = bound.Email;
            bound.Email = email; bound.Version++;
            Audit(db, bound.Id, "EmailChanged", new { email = before }, new { email });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return null;
        }

        var byEmail = await db.Set<Users>().SingleOrDefaultAsync(u => u.Email == email, ct);
        if (byEmail is null) return null;   // a new user: UserProvisioner creates the row, BindAsync binds it
        if (byEmail.IdpSubject is not null)
        {
            log.LogWarning("Organisation sign-in refused for {Email}: user {UserId} is bound to a different identity-provider subject", email, byEmail.Id);
            return new(OidcSignIn.Reasons.IdentityConflict, "This email address belongs to another identity in this app. Ask an administrator.");
        }
        if (!emailVerified)
        {
            log.LogWarning("Organisation sign-in refused for {Email}: user {UserId} has no identity bound yet and the identity provider does not say the email is verified", email, byEmail.Id);
            return new(OidcSignIn.Reasons.EmailUnverified, "Your identity provider does not confirm this email address, so it cannot be linked to the existing account. Ask an administrator.");
        }
        Bind(db, byEmail, issuer, subject);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return null;
    }

    /// <summary>After provisioning: binds a user created by this sign-in. False (logged) if someone else's identity was bound to it in the meantime.</summary>
    public async Task<bool> BindAsync(string userId, string issuer, string subject, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var u = await db.Set<Users>().SingleAsync(x => x.Id == userId, ct);
        if (u.IdpSubject is not null)
        {
            if (u.IdpIssuer == issuer && u.IdpSubject == subject) return true;
            log.LogWarning("Organisation sign-in refused for user {UserId}: a different identity-provider subject was bound to it concurrently", userId);
            return false;
        }
        Bind(db, u, issuer, subject);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    private void Bind(ReleaseDbContext db, Users u, string issuer, string subject)
    {
        u.IdpIssuer = issuer; u.IdpSubject = subject; u.Version++;
        Audit(db, u.Id, "IdentityBound", null, new { issuer, subject });
    }

    private void Audit(ReleaseDbContext db, string userId, string action, object? before, object? after)
    {
        var t = time.GetUtcNow().UtcDateTime;
        db.Set<AuditEvents>().Add(new AuditEvents
        {
            OccurredAt = new DateTime(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc), ActorUserId = userId, EntityType = "User", EntityId = userId,
            Action = action, BeforeJson = before is null ? null : JsonSerializer.Serialize(before), AfterJson = after is null ? null : JsonSerializer.Serialize(after),
        });
    }
}
