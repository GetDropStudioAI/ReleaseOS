using System.Security.Claims;
using ReleaseMgmt.Domain.Common;

namespace ReleaseMgmt.Api.Auth;

/// <summary>Maps IdP group claims to app roles via <c>Auth:RoleMap</c> (group -> role). Unmapped users get Viewer.</summary>
public static class RoleMapper
{
    public static IEnumerable<string> Map(IEnumerable<string> groups, IReadOnlyDictionary<string, string> roleMap)
    {
        var roles = groups.Where(roleMap.ContainsKey).Select(g => roleMap[g]).Where(r => Roles.All.Contains(r)).Distinct().ToList();
        return roles.Count > 0 ? roles : [Roles.Viewer];
    }

    public static void AddRoles(ClaimsIdentity identity, IReadOnlyDictionary<string, string> roleMap, string groupClaim = "groups")
    {
        var groups = identity.FindAll(groupClaim).Select(c => c.Value);
        foreach (var r in Map(groups, roleMap)) identity.AddClaim(new Claim(ClaimTypes.Role, r));
    }
}
