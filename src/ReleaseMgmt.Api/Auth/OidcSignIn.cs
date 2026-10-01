using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using ReleaseMgmt.Domain.Common;

namespace ReleaseMgmt.Api.Auth;

/// <summary>
/// What the app does with a validated OpenID Connect ID token (the handler has already checked issuer, audience, lifetime, signature, nonce and PKCE):
/// app roles only from <c>Auth:RoleMap</c> (SEC-B7), no account linked through an email the IdP says is unverified (SEC-B8), and no sign-in for an identity
/// that maps to no role unless <c>Auth:DefaultRole</c> admits it (SEC-B9). The Users row is found by the token's issuer + subject, not its email
/// (<see cref="IdpIdentityBinder"/>, Q-SEC-B8), provisioned just in time (Q-003), and its id becomes "uid".
/// </summary>
public static class OidcSignIn
{
    /// <summary><c>Auth:DefaultRole</c>: empty (the default) refuses identities that map to no role; <c>Viewer</c> admits them read-only. Nothing else is allowed.</summary>
    public static string? DefaultRole(IConfiguration config)
    {
        var v = config["Auth:DefaultRole"];
        if (string.IsNullOrWhiteSpace(v)) return null;
        if (v.Trim() == Roles.Viewer) return Roles.Viewer;
        throw new InvalidOperationException($"Auth:DefaultRole must be empty or {Roles.Viewer}; '{v}' would give that role to everyone the identity provider signs in");
    }

    public static async Task OnTokenValidated(TokenValidatedContext ctx, IReadOnlyDictionary<string, string> roleMap, string? defaultRole)
    {
        if (ctx.Principal?.Identity is not ClaimsIdentity id) return;
        var log = ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(OidcSignIn));
        var emailClaim = id.FindFirst(ClaimTypes.Email) ?? id.FindFirst("email");
        var email = emailClaim?.Value ?? id.FindFirst("preferred_username")?.Value;
        if (email is null) { ctx.Fail("The identity provider returned no email claim"); return; }
        // "sub" arrives as NameIdentifier when the handler maps inbound claims (the default). The issuer is the validated token's.
        var subClaim = id.FindFirst("sub") ?? id.FindFirst(ClaimTypes.NameIdentifier);
        var subject = subClaim?.Value;
        var issuer = ctx.SecurityToken?.Issuer ?? id.FindFirst("iss")?.Value ?? (subClaim?.Issuer is { } si && si != ClaimsIdentity.DefaultIssuer ? si : null);
        if (string.IsNullOrEmpty(subject) || string.IsNullOrEmpty(issuer))
        {
            log.LogWarning("Organisation sign-in refused for {Email}: the token has no subject or issuer", email);
            ctx.Fail("The identity provider returned no subject");
            return;
        }
        if (emailClaim is not null && string.Equals(id.FindFirst("email_verified")?.Value, "false", StringComparison.OrdinalIgnoreCase))
        {
            log.LogWarning("Organisation sign-in refused for {Email}: the identity provider marks the email unverified", email);
            ctx.Fail("The identity provider says this email address is not verified");
            return;
        }
        var roles = RoleMapper.AddRoles(id, roleMap, defaultRole);
        if (roles.Count == 0)
        {
            log.LogWarning("Organisation sign-in refused for {Email}: no group or role of theirs is in Auth:RoleMap, and Auth:DefaultRole is not set", email);
            ctx.Fail("Your account has no role in this app. Ask an administrator to add you to a mapped group.");
            return;
        }
        var role = roles.OrderBy(r => Array.IndexOf(Roles.All, r)).First();
        // SEC-B8 (Q-SEC-B8): the account is the identity provider's issuer + subject; email is display data.
        var binder = ctx.HttpContext.RequestServices.GetRequiredService<IdpIdentityBinder>();
        var verified = emailClaim is not null && string.Equals(id.FindFirst("email_verified")?.Value, "true", StringComparison.OrdinalIgnoreCase);
        if (await binder.PrepareAsync(issuer, subject, email, verified) is { } refused) { ctx.Fail(refused); return; }
        var uid = await ctx.HttpContext.RequestServices.GetRequiredService<UserProvisioner>().UpsertAsync(email, id.FindFirst("name")?.Value ?? email, role);
        if (!await binder.BindAsync(uid, issuer, subject)) { ctx.Fail("This email address belongs to another identity in this app. Ask an administrator."); return; }
        id.AddClaim(new Claim("uid", uid));
    }
}
