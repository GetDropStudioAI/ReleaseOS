using Microsoft.AspNetCore.Authorization;
using ReleaseMgmt.Domain.Common;

namespace ReleaseMgmt.Api.Auth;

/// <summary>Named policies. Release Manager is a superset of RTE (D32).</summary>
public static class Policies
{
    public const string Read = "Read";                       // any signed-in role
    public const string Plan = "Plan";                       // RTE, ReleaseManager
    public const string Admin = "Admin";                     // RTE, ReleaseManager (v1)
    public const string ReleaseManager = "ReleaseManager";   // Go/No-Go
    public const string GovernanceOfficer = "GovernanceOfficer";
    public const string AuditRead = "AuditRead";             // RTE, ReleaseManager, GovernanceOfficer
    public const string FreezeApprove = "FreezeApprove";     // ReleaseManager, GovernanceOfficer

    public static void Configure(AuthorizationOptions o)
    {
        o.AddPolicy(Read, p => p.RequireRole(Roles.All));
        o.AddPolicy(Plan, p => p.RequireRole(Roles.RTE, Roles.ReleaseManager));
        o.AddPolicy(Admin, p => p.RequireRole(Roles.RTE, Roles.ReleaseManager));
        o.AddPolicy(ReleaseManager, p => p.RequireRole(Roles.ReleaseManager));
        o.AddPolicy(GovernanceOfficer, p => p.RequireRole(Roles.GovernanceOfficer));
        o.AddPolicy(AuditRead, p => p.RequireRole(Roles.RTE, Roles.ReleaseManager, Roles.GovernanceOfficer));
        o.AddPolicy(FreezeApprove, p => p.RequireRole(Roles.ReleaseManager, Roles.GovernanceOfficer));
        o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    }
}
