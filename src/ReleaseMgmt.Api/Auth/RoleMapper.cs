using System.Security.Claims;
using ReleaseMgmt.Domain.Common;

namespace ReleaseMgmt.Api.Auth;

/// <summary>
/// Maps identity-provider groups and roles to app roles via <c>Auth:RoleMap</c> (IdP value -> app role). <see cref="Map(IEnumerable{string}, IReadOnlyDictionary{string, string})"/>
/// falls back to Viewer; the sign-in itself falls back to <c>Auth:DefaultRole</c>, which is unset by default, so an unmapped identity is refused (SEC-B9).
/// </summary>
public static class RoleMapper
{
    /// <summary>Claim types an identity provider's own roles arrive in (the handler maps "roles"/"role" to <see cref="ClaimTypes.Role"/> unless told not to).</summary>
    public static readonly string[] IdpRoleClaimTypes = [ClaimTypes.Role, "roles", "role"];

    public static IEnumerable<string> Map(IEnumerable<string> groups, IReadOnlyDictionary<string, string> roleMap) => Map(groups, roleMap, Roles.Viewer);

    /// <summary>The app roles the map gives for <paramref name="keys"/>; otherwise <paramref name="defaultRole"/>, or nothing when that is null.</summary>
    public static IReadOnlyList<string> Map(IEnumerable<string> keys, IReadOnlyDictionary<string, string> roleMap, string? defaultRole)
    {
        var roles = keys.Where(roleMap.ContainsKey).Select(g => roleMap[g]).Where(r => Roles.All.Contains(r)).Distinct().ToList();
        return roles.Count > 0 ? roles : defaultRole is null ? [] : [defaultRole];
    }

    /// <summary>
    /// SEC-B7: replaces every role claim the identity provider sent with the app roles <c>Auth:RoleMap</c> gives for its groups and its roles. An IdP role spelled
    /// like an app role is a map key like any other, never an app role by itself. Returns the app roles added (empty: the identity has no role here).
    /// </summary>
    public static IReadOnlyList<string> AddRoles(ClaimsIdentity identity, IReadOnlyDictionary<string, string> roleMap, string? defaultRole, string groupClaim = "groups")
    {
        var idpRoles = identity.Claims.Where(c => IdpRoleClaimTypes.Contains(c.Type) || c.Type == identity.RoleClaimType).ToList();
        foreach (var c in idpRoles) identity.RemoveClaim(c);
        var roles = Map(identity.FindAll(groupClaim).Select(c => c.Value).Concat(idpRoles.Select(c => c.Value)), roleMap, defaultRole);
        foreach (var r in roles)
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, r));   // what the app reads (/me, policies with the default role type)
            if (identity.RoleClaimType != ClaimTypes.Role) identity.AddClaim(new Claim(identity.RoleClaimType, r));
        }
        return roles;
    }
}
