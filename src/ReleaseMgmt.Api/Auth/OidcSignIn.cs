using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using ReleaseMgmt.Domain.Common;

namespace ReleaseMgmt.Api.Auth;

/// <summary>
/// What the app does with a validated OpenID Connect ID token (the handler has already checked issuer, audience, lifetime, signature, nonce and PKCE):
/// app roles only from <c>Auth:RoleMap</c> (SEC-B7), no account linked through an email the IdP says is unverified (SEC-B8), and no sign-in for an identity
/// that maps to no role unless <c>Auth:DefaultRole</c> admits it (SEC-B9). Then the Users row is provisioned just in time (Q-003) and its id becomes "uid".
/// REOS-65: a refused or failed sign-in never ends on the framework's 500. <see cref="OnRemoteFailure"/> logs it and sends the browser to the sign-in page
/// with <c>?signin=&lt;reason&gt;</c>, a fixed code from <see cref="Reasons"/> (never exception text) that the SPA turns into words.
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
        if (email is null)
        {
            log.LogWarning("Organisation sign-in refused: the identity provider returned no email claim");
            ctx.Fail(new SignInRefused(Reasons.NoEmail, "The identity provider returned no email claim"));
            return;
        }
        if (emailClaim is not null && string.Equals(id.FindFirst("email_verified")?.Value, "false", StringComparison.OrdinalIgnoreCase))
        {
            log.LogWarning("Organisation sign-in refused for {Email}: the identity provider marks the email unverified", email);
            ctx.Fail(new SignInRefused(Reasons.EmailUnverified, "The identity provider says this email address is not verified"));
            return;
        }
        var roles = RoleMapper.AddRoles(id, roleMap, defaultRole);
        if (roles.Count == 0)
        {
            log.LogWarning("Organisation sign-in refused for {Email}: no group or role of theirs is in Auth:RoleMap, and Auth:DefaultRole is not set", email);
            ctx.Fail(new SignInRefused(Reasons.NoRole, "Your account has no role in this app. Ask an administrator to add you to a mapped group."));
            return;
        }
        var role = roles.OrderBy(r => Array.IndexOf(Roles.All, r)).First();
        var uid = await ctx.HttpContext.RequestServices.GetRequiredService<UserProvisioner>().UpsertAsync(email, id.FindFirst("name")?.Value ?? email, role);
        id.AddClaim(new Claim("uid", uid));
    }

    /// <summary>The reason codes the sign-in page understands (<c>src/ReleaseMgmt.Web/src/signinReason.ts</c>). Keep the two lists in step.</summary>
    public static class Reasons
    {
        public const string NoRole = "no-role";
        public const string EmailUnverified = "email-unverified";
        public const string NoEmail = "no-email";
        public const string Denied = "denied";     // the identity provider answered error=access_denied (the user cancelled, or is not assigned to the app)
        public const string Expired = "expired";   // state or correlation cookie missing or unreadable: an old tab, a second tab, or a replayed callback
        public const string Failed = "failed";     // anything else: the identity provider's error, an invalid token, an unreachable provider
    }

    /// <summary>The query key the sign-in page reads.</summary>
    public const string QueryKey = "signin";

    /// <summary>A sign-in this app refused on purpose (as opposed to a protocol failure); carries the reason code.</summary>
    public sealed class SignInRefused(string reason, string message) : Exception(message)
    {
        public string Reason { get; } = reason;
    }

    /// <summary>The fixed reason code for a remote sign-in failure. Only codes, never the exception's text, leave the server.</summary>
    public static string ReasonFor(Exception? failure) => failure switch
    {
        SignInRefused r => r.Reason,
        OpenIdConnectProtocolException p when string.Equals(p.Data["error"] as string, "access_denied", StringComparison.Ordinal) => Reasons.Denied,
        not null when failure.Message.Contains("Correlation failed", StringComparison.Ordinal)
            || failure.Message.Contains("message.State", StringComparison.Ordinal) => Reasons.Expired,
        _ => Reasons.Failed,
    };

    /// <summary>
    /// OIDC <c>OnRemoteFailure</c>: covers every <c>ctx.Fail</c> in <see cref="OnTokenValidated"/> and every failure the handler itself reports at
    /// <c>/signin-oidc</c>. Logged at Warning (an operator needs to see refusals), then a redirect to the SPA's sign-in page; the response is handled, so the
    /// framework does not rethrow the failure as a 500.
    /// </summary>
    public static Task OnRemoteFailure(RemoteFailureContext ctx)
    {
        var reason = ReasonFor(ctx.Failure);
        var log = ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(OidcSignIn));
        if (ctx.Failure is SignInRefused) log.LogInformation("Organisation sign-in ended on the sign-in page ({Reason})", reason);   // already logged with its detail
        else log.LogWarning(ctx.Failure, "Organisation sign-in failed ({Reason})", reason);
        ctx.Response.Redirect($"{ctx.Request.PathBase}/?{QueryKey}={Uri.EscapeDataString(reason)}");
        ctx.HandleResponse();
        return Task.CompletedTask;
    }
}
