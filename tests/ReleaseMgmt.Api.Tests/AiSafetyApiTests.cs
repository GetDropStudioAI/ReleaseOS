using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Text.RegularExpressions;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Domain.Ai;
using ReleaseMgmt.Domain.Common;

namespace ReleaseMgmt.Api.Tests;

/// <summary>REOS-186 (D58): what AI may read follows the API's own policies, and no AI route can write a status.</summary>
public class AiSafetyApiTests
{
    private static async Task<string[]> RolesOf(IAuthorizationPolicyProvider p, string policy)
    {
        var pol = await p.GetPolicyAsync(policy) ?? throw new InvalidOperationException(policy);
        return pol.Requirements.OfType<RolesAuthorizationRequirement>().SelectMany(r => r.AllowedRoles).OrderBy(x => x).ToArray();
    }

    [Fact]
    public async Task The_AI_read_policy_is_the_same_set_of_roles_as_the_APIs_Read_and_AuditRead_policies()
    {
        using var f = new ApiFactory();
        using var _ = f.CreateClient();
        var p = f.Services.GetRequiredService<IAuthorizationPolicyProvider>();
        var read = await RolesOf(p, Policies.Read);
        var audit = await RolesOf(p, Policies.AuditRead);
        Assert.Equal(Roles.All.OrderBy(x => x), read);
        Assert.Equal(audit, AiReadPolicy.RolesFor(AiSources.AuditLog).OrderBy(x => x));
        Assert.Equal(audit, AiReadPolicy.RolesFor(AiSources.AiActions).OrderBy(x => x));   // the AI trail is read with the audit policy (/ai/actions)
        Assert.DoesNotContain(Roles.Viewer, audit);
        foreach (var s in AiReadPolicy.Sources.Except([AiSources.AuditLog, AiSources.AiActions]))
            Assert.Equal(read, AiReadPolicy.RolesFor(s).OrderBy(x => x));
    }

    private static readonly HashSet<string> KnownAiWrites = new(StringComparer.OrdinalIgnoreCase)
    {
        "POST /api/v1/ai/workspace:pause", "POST /api/v1/ai/workspace:resume", "POST /api/v1/ai/workspace:opt-out", "POST /api/v1/ai/workspace:opt-in",
        "PUT /api/v1/ai/key", "DELETE /api/v1/ai/key", "POST /api/v1/ai/actions/{id}:decide", "POST /api/v1/dev/ai/draft",
    };

    [Fact]
    public void The_only_AI_routes_that_write_are_the_controls_the_key_the_human_decision_and_the_dev_draft_and_none_names_a_status_change()
    {
        using var f = new ApiFactory();
        using var _ = f.CreateClient();
        var writes = f.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => Regex.IsMatch(e.RoutePattern.RawText ?? "", @"/ai(/|$)"))
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []).Where(m => m != "GET").Select(m => $"{m} /{(e.RoutePattern.RawText ?? "").TrimStart('/')}"))
            .ToList();
        Assert.NotEmpty(writes);
        Assert.All(writes, w => Assert.Contains(w, KnownAiWrites));   // a new AI write route must be added here on purpose, and reviewed against D58
        Assert.DoesNotContain(writes, w => Regex.IsMatch(w, "approve|certify|waive|advance|abort|complete|transition|gate|train|run", RegexOptions.IgnoreCase));
    }
}
