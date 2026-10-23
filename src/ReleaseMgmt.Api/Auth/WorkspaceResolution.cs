using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Entities;
using ReleaseMgmt.Infrastructure.Persistence;

namespace ReleaseMgmt.Api.Auth;

/// <summary>
/// REOS-105 (D45): the current workspace comes from the request and is checked against membership.
/// <list type="bullet">
/// <item>A route segment, <c>/api/v1/w/{slug}/…</c> (<see cref="WorkspaceRoutePrefix"/> strips it before routing), or the <c>X-Workspace</c> header, or
/// the <c>workspace</c> query value for the live channel (a browser cannot set headers on a WebSocket). Each names the workspace by slug or id.</item>
/// <item>A request that names none uses the person's only membership, or the default workspace when they are a member of it, so the SPA works unchanged
/// (Q-105a). A person in several workspaces and not in the default one must name one: 400 <c>WorkspaceRequired</c>.</item>
/// <item>A workspace the person is not a member of, an archived one, or one that does not exist is the same 404 <c>WorkspaceNotFound</c>: the answer does
/// not say whether it exists.</item>
/// </list>
/// The person's role claims are replaced by the role held in that workspace, so every policy and inline role check is per workspace, and the rest of the
/// request runs inside <see cref="WorkspaceScope"/>. A signed-in person who is a member of no workspace holds no role (every role policy refuses) and
/// runs in a workspace that has no rows, so nothing is shown or written by mistake.
/// </summary>
public sealed class WorkspaceResolution(RequestDelegate next, IDbContextFactory<ReleaseDbContext> dbf)
{
    public const string Header = "X-Workspace";
    public const string Query = "workspace";
    /// <summary>REOS-106: the workspace the person last chose, set by the SPA. A last resort for requests that cannot carry the header (links, downloads); never an error.</summary>
    public const string Cookie = "reos.workspace";
    public const string Claim = "ws";
    public const string NotFound = "WorkspaceNotFound";
    public const string Required = "WorkspaceRequired";
    /// <summary>The workspace of a person who is a member of none: no row has it, so its queries are empty and its writes fail.</summary>
    public const string Nowhere = "~none";

    public async Task InvokeAsync(HttpContext http)
    {
        var uid = http.User.FindFirst("uid")?.Value;
        if (uid is null || http.User.Identity?.IsAuthenticated != true) { await next(http); return; }

        var asked = http.Items[WorkspaceRoutePrefix.ItemKey] as string;
        if (asked is null && http.Request.Headers.TryGetValue(Header, out var h) && !string.IsNullOrWhiteSpace(h)) asked = h.ToString().Trim();
        if (asked is null && http.Request.Path.StartsWithSegments("/hub") && http.Request.Query.TryGetValue(Query, out var q) && !string.IsNullOrWhiteSpace(q)) asked = q.ToString().Trim();

        string? remembered = null;
        if (asked is null && http.Request.Cookies.TryGetValue(Cookie, out var c) && !string.IsNullOrWhiteSpace(c)) remembered = c.Trim();

        List<(string Id, string Slug, string Role)> mine;
        await using (var db = await dbf.CreateDbContextAsync(http.RequestAborted))
            mine = [.. (await (from m in db.Set<WorkspaceMembers>().IgnoreQueryFilters().AsNoTracking()
                               join w in db.Set<Workspaces>().AsNoTracking() on m.WorkspaceId equals w.Id
                               where m.UserId == uid && w.ArchivedAt == null
                               select new { w.Id, w.Slug, m.Role }).ToListAsync(http.RequestAborted)).Select(x => (x.Id, x.Slug, x.Role))];

        (string Id, string Slug, string Role)? current;
        if (asked is not null)
        {
            current = mine.Where(m => m.Id == asked || string.Equals(m.Slug, asked, StringComparison.OrdinalIgnoreCase)).Select(m => ((string, string, string)?)m).FirstOrDefault();
            if (current is null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                await http.Response.WriteAsJsonAsync(new { guard = NotFound, message = "No workspace of yours has that name" });
                return;
            }
        }
        else if (remembered is not null && mine.Any(m => m.Id == remembered || string.Equals(m.Slug, remembered, StringComparison.OrdinalIgnoreCase)))   // a stale or foreign value falls through to the rules below
            current = mine.First(m => m.Id == remembered || string.Equals(m.Slug, remembered, StringComparison.OrdinalIgnoreCase));
        else if (mine.Count == 1) current = mine[0];
        else if (mine.Any(m => m.Id == WorkspaceIds.Default)) current = mine.Single(m => m.Id == WorkspaceIds.Default);
        else if (mine.Count == 0) current = null;
        else if (IsChooser(http.Request)) current = mine.OrderBy(m => m.Slug, StringComparer.Ordinal).First();   // REOS-106: the SPA asks who you are and where you belong before it names one
        else
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsJsonAsync(new { guard = Required, message = $"You are a member of several workspaces: name one in the {Header} header or the /api/v1/w/{{workspace}}/ path" });
            return;
        }

        http.User = WithRole(http.User, current?.Id ?? Nowhere, current?.Role);
        using (WorkspaceScope.Enter(current?.Id ?? Nowhere))
            await next(http);
    }

    /// <summary>The two reads the SPA makes before it has chosen a workspace: who am I, and which workspaces do I belong to.</summary>
    private static bool IsChooser(HttpRequest r) =>
        HttpMethods.IsGet(r.Method) && (r.Path.Equals("/api/v1/me", StringComparison.OrdinalIgnoreCase) || r.Path.Equals("/api/v1/workspaces", StringComparison.OrdinalIgnoreCase));

    /// <summary>The principal with its role claims replaced by the workspace role (none for a non-member) and the workspace in <see cref="Claim"/>.</summary>
    private static ClaimsPrincipal WithRole(ClaimsPrincipal user, string workspaceId, string? role)
    {
        var principal = new ClaimsPrincipal();
        foreach (var identity in user.Identities)
        {
            var copy = new ClaimsIdentity(identity.Claims.Where(c => c.Type != identity.RoleClaimType && c.Type != ClaimTypes.Role && c.Type != Claim),
                identity.AuthenticationType, identity.NameClaimType, identity.RoleClaimType);
            if (identity == user.Identity)
            {
                if (role is not null) copy.AddClaim(new Claim(identity.RoleClaimType, role));
                if (role is not null && identity.RoleClaimType != ClaimTypes.Role) copy.AddClaim(new Claim(ClaimTypes.Role, role));
                copy.AddClaim(new Claim(Claim, workspaceId));
            }
            principal.AddIdentity(copy);
        }
        return principal;
    }
}

/// <summary>
/// <c>/api/v1/w/{slug}/rest</c> is <c>/api/v1/rest</c> in workspace <c>slug</c>: the segment is taken off before routing, so every endpoint is reachable both
/// ways without being mapped twice, and <see cref="WorkspaceResolution"/> checks it after sign-in.
/// </summary>
public sealed class WorkspaceRoutePrefix(RequestDelegate next)
{
    public const string ItemKey = "reos.workspace.asked";
    private static readonly PathString Prefix = "/api/v1/w";

    public Task InvokeAsync(HttpContext http)
    {
        if (http.Request.Path.StartsWithSegments(Prefix, StringComparison.OrdinalIgnoreCase, out var rest) && rest.HasValue)
        {
            var s = rest.Value!;   // "/{slug}/..."
            var end = s.IndexOf('/', 1);
            var slug = end < 0 ? s[1..] : s[1..end];
            if (slug.Length > 0)
            {
                http.Items[ItemKey] = Uri.UnescapeDataString(slug);
                http.Request.Path = "/api/v1" + (end < 0 ? "" : s[end..]);
            }
        }
        return next(http);
    }
}
