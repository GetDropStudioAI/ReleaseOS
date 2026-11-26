using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ReleaseMgmt.Api.PublicApi;
using ReleaseMgmt.Domain.Services;

namespace ReleaseMgmt.Api.Tests;

/// <summary>
/// REOS-161: the public API's contract. The OpenAPI document is generated from the catalog; the committed copy (<c>docs/api/public-v1.openapi.json</c>) must equal it, so a change to the surface is a
/// visible change to a file a client reads. The catalog must also be exactly what is mapped: every forwarded operation is an internal endpoint that exists, takes the same method and is role-checked;
/// no other route is reachable under the prefix; nothing sets a status directly.
/// </summary>
public class PublicApiContractTests
{
    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "db", "schema.sql"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    public static string Lf(string text) => text.Replace("\r\n", "\n");

    public static string DocPath => Path.Combine(RepoRoot(), "docs", "api", "public-v1.openapi.json");

    [Fact]
    public void The_committed_OpenAPI_document_is_the_generated_one()
    {
        var generated = OpenApiBuilder.Json();
        if (Environment.GetEnvironmentVariable("UPDATE_OPENAPI") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DocPath)!);
            File.WriteAllText(DocPath, generated);
        }
        Assert.True(File.Exists(DocPath), "docs/api/public-v1.openapi.json is missing: run `UPDATE_OPENAPI=1 dotnet test --filter PublicApiContractTests`");
        Assert.True(Lf(generated) == Lf(File.ReadAllText(DocPath)),   // a Windows checkout may turn the file's LF into CRLF; the content is what is compared
            "The public API changed and docs/api/public-v1.openapi.json did not. Regenerate with `UPDATE_OPENAPI=1 dotnet test --filter PublicApiContractTests`, review the diff against docs/API_VERSIONING.md (a breaking change needs a new version), and commit it.");
    }

    [Fact]
    public void The_document_describes_every_operation_once_and_only_those()
    {
        var doc = OpenApiBuilder.Build();
        var fromDoc = doc["paths"]!.AsObject().SelectMany(p => p.Value!.AsObject().Select(m => (Method: m.Key.ToUpperInvariant(), Path: p.Key, Id: (string)m.Value!["operationId"]!, Scope: (string)m.Value["x-required-scope"]!))).ToList();
        var fromCatalog = PublicApiCatalog.All.Select(o => (o.Method, o.Path, Id: o.OperationId, o.Scope)).ToList();
        Assert.Equal(fromCatalog.Order().ToList(), fromDoc.Order().ToList());
        Assert.Equal(fromCatalog.Count, fromCatalog.Select(o => o.Id).Distinct().Count());
        Assert.Equal(fromCatalog.Count, fromCatalog.Select(o => (o.Method, o.Path)).Distinct().Count());
        Assert.Equal("3.1.0", (string)doc["openapi"]!);
        Assert.All(fromCatalog, o => Assert.Contains(o.Scope, ApiTokenScopes.All));
    }

    [Fact]
    public void Every_operation_is_a_GET_or_a_POST_and_only_a_read_is_a_GET()
    {
        foreach (var o in PublicApiCatalog.All)
        {
            Assert.True(o.Method is "GET" or "POST", $"{o.OperationId}: only GET and POST (no PATCH, PUT or DELETE: nothing sets a status column, CLAUDE.md rule 2)");
            Assert.Equal(o.Method == "GET", o.Scope == ApiTokenScopes.Read);
            Assert.DoesNotContain("status", o.Path, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static (string Method, string Route, string[] Policies, bool Anonymous, bool Authorized)[] Routes(ApiFactory f) =>
        [.. f.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().SelectMany(e =>
        {
            var methods = e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [];
            var auth = e.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).OfType<string>().ToArray();
            var anon = e.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            return methods.Select(m => (m, e.RoutePattern.RawText ?? "", auth, anon, e.Metadata.GetOrderedMetadata<IAuthorizeData>().Any()));
        })];

    [Fact]
    public void The_catalog_is_exactly_what_is_mapped_and_every_forwarded_operation_is_role_checked_internally()
    {
        using var f = new ApiFactory { Clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-05T08:00:00Z")) };
        f.CreateClient();   // starts the host
        var routes = Routes(f);

        foreach (var o in PublicApiCatalog.All)
        {
            if (o.IsNative)
            {
                var r = routes.Where(x => x.Method == o.Method && x.Route == PublicApiCatalog.Prefix + o.Path).ToList();
                Assert.True(r.Count == 1, $"{o.OperationId}: the native endpoint {o.Method} {PublicApiCatalog.Prefix}{o.Path} is not mapped exactly once");
                Assert.True(r[0].Authorized && !r[0].Anonymous, $"{o.OperationId}: a native endpoint must require authorization");
                continue;
            }
            var internalRoute = "/api/v1" + o.InternalPath;
            var m = routes.Where(x => x.Method == o.Method && string.Equals(x.Route, internalRoute, StringComparison.Ordinal)).ToList();
            Assert.True(m.Count == 1, $"{o.OperationId}: {o.Method} {internalRoute} is not an internal endpoint (or is mapped twice)");
            Assert.False(m[0].Anonymous, $"{o.OperationId}: the internal endpoint allows anonymous callers");   // a policy, or an inline role check behind the fallback policy (signed-in only), as the internal API has it
            Assert.DoesNotContain(m[0].Policies, p => p == "Ingest");
        }

        // nothing else is mapped under the public prefix (but the document and the catalog's native routes)
        var allowed = PublicApiCatalog.All.Where(o => o.IsNative).Select(o => (o.Method, PublicApiCatalog.Prefix + o.Path)).Append(("GET", PublicApiCatalog.Prefix + "/openapi.json")).ToHashSet();
        var extra = routes.Where(r => r.Route.StartsWith(PublicApiCatalog.Prefix, StringComparison.OrdinalIgnoreCase) && !allowed.Contains((r.Method, r.Route))).ToList();
        Assert.Empty(extra);
    }

    [Fact]
    public void The_policy_of_each_forwarded_operation_is_one_the_application_defines_and_a_token_needs_no_special_one()
    {
        using var f = new ApiFactory { Clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-05T08:00:00Z")) };
        f.CreateClient();
        var routes = Routes(f);
        var known = new[] { "Read", "Plan", "Admin", "ReleaseManager", "GovernanceOfficer", "AuditRead", "FreezeApprove", "PolicyManage" };
        foreach (var o in PublicApiCatalog.All.Where(o => !o.IsNative))
        {
            var r = routes.Single(x => x.Method == o.Method && x.Route == "/api/v1" + o.InternalPath);
            Assert.All(r.Policies, p => Assert.Contains(p, known));
            // a read-scope operation never needs more than reading; a write that needs a planner is the planner's policy, never weakened by the scope
            if (o.Scope == ApiTokenScopes.Read && r.Policies.Length > 0) Assert.Contains("Read", r.Policies);
        }
    }
}
