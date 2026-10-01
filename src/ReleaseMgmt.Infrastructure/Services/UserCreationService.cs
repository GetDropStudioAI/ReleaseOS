using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Infrastructure.Services;

/// <summary>
/// REOS-83: an admin adds a user before their first sign-in, so they can be named as an owner straight away. The row is created active and keyed by
/// the normalised email (trimmed, lower case); the first organisation sign-in finds it by email (Users.Email is COLLATE NOCASE) and binds to it. The role
/// given here is provisional: sign-in replaces it with the role the identity provider sends (Q-003, Q-083a). Kept apart from <see cref="AdminService"/> so
/// the user-creation path has one owner (Q-083d).
/// </summary>
public sealed partial class UserCreationService(IDbContextFactory<ReleaseDbContext> dbf, TimeProvider time, IRealtimePublisher? realtime = null) : ServiceBase(dbf, time, realtime)
{
    public const string InvalidInput = AdminService.InvalidInput, UserExists = "UserExists", HandleTaken = "HandleTaken";
    public const int MaxEmailLength = 254, MaxNameLength = 200;

    // One '@', no whitespace or control characters, a dot in the domain. Deliverability is the identity provider's business; this only refuses typos.
    [GeneratedRegex(@"^[^\s@\p{C}]+@[^\s@\p{C}]+\.[^\s@\p{C}]+$")] private static partial Regex EmailShape();
    [GeneratedRegex(@"^@?[A-Za-z0-9._-]{1,40}$")] private static partial Regex HandleShape();

    public static string NormaliseEmail(string? email) => (email ?? "").Trim().ToLowerInvariant();

    public Task<ServiceResult<Users>> CreateUserAsync(string? email, string? displayName, string? role, string? handle, Actor actor, CancellationToken ct = default)
    {
        var mail = NormaliseEmail(email);
        var name = (displayName ?? "").Trim();
        var tag = string.IsNullOrWhiteSpace(handle) ? null : handle.Trim();
        if (mail.Length == 0 || mail.Length > MaxEmailLength || !EmailShape().IsMatch(mail)) return Task.FromResult(Invalid("Enter the email address the person signs in with, such as rae@example.com"));
        if (name.Length == 0 || name.Length > MaxNameLength || name.Any(char.IsControl)) return Task.FromResult(Invalid($"Enter a display name (at most {MaxNameLength} characters)"));
        if (role is null || !Roles.All.Contains(role)) return Task.FromResult(Invalid($"The role must be one of {string.Join(", ", Roles.All)}"));
        if (tag is not null && !HandleShape().IsMatch(tag)) return Task.FromResult(Invalid("A handle is letters, digits, '.', '_' or '-' (optionally starting with @)"));
        tag = tag?.TrimStart('@');

        return RunAsync(async db =>
        {
            // Email and Handle are COLLATE NOCASE in the schema, so these comparisons match the UNIQUE constraints; a race past them is a 422 DbRule.
            if (await db.Set<Users>().AnyAsync(u => u.Email == mail, ct))
                return ServiceResult<Users>.Fail(new GuardFailure(UserExists, $"{mail} is already a user"));
            if (tag is not null && await db.Set<Users>().AnyAsync(u => u.Handle == tag, ct))
                return ServiceResult<Users>.Fail(new GuardFailure(HandleTaken, $"@{tag} is already someone's handle"));
            var u = new Users { Id = Ids.New(), Email = mail, DisplayName = name, Role = role, Handle = tag, IsActive = true };
            db.Set<Users>().Add(u);
            Audit(db, actor, null, "User", u.Id, "Create", null, new { u.Email, u.DisplayName, u.Role, u.Handle, u.IsActive });
            await db.SaveChangesAsync(ct);
            return ServiceResult<Users>.Ok(u);
        }, ct);
    }

    private static ServiceResult<Users> Invalid(string message) => ServiceResult<Users>.Fail(new GuardFailure(InvalidInput, message));
}
