using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>
/// Calendar read + ICS feeds + feed-token management (REOS-51, Q-051*).
/// The feed endpoints are anonymous by design (a calendar app cannot sign in): the secret in the path is the credential. An unknown, revoked or malformed
/// token, and a feed for a train that does not exist, are all the same bare 404. Neither the token nor the feed URL is ever logged (Serilog request logging is
/// not enabled and this file logs nothing about the path).
/// </summary>
public static class CalendarEndpoints
{
    public sealed record IcsCreateBody(string? Scope, string? TrainId);

    private static Actor ActorOf(ClaimsPrincipal u) => new(u.FindFirstValue("uid") ?? throw new InvalidOperationException("Signed-in user has no uid claim"));
    private static IResult Bad(string message) => Results.Json(new { guard = CalendarGuards.InvalidFilter, message }, statusCode: StatusCodes.Status400BadRequest);

    private static bool Throttled(HttpContext http, IcsTokenService tokens, IConfiguration cfg, bool failed) =>
        tokens.Throttle(http.Connection.RemoteIpAddress?.ToString() ?? "unknown", cfg.GetValue("Ics:MaxFailuresPerMinute", 60), failed);

    public static void MapCalendar(this RouteGroupBuilder api)
    {
        api.MapGet("/calendar", async (string? from, string? to, CalendarService cal, CancellationToken ct) =>
            CalendarService.Validate(from, to, out var f, out var t) is { } why ? Bad(why) : Results.Ok(await cal.GetAsync(f, t, ct))).RequireAuthorization(Policies.Read);

        api.MapGet("/me/ics-tokens", async (ClaimsPrincipal u, IcsTokenService s, CancellationToken ct) => Results.Ok(await s.ListAsync(ActorOf(u).UserId, ct))).RequireAuthorization(Policies.Read);
        api.MapPost("/me/ics-tokens", (IcsCreateBody b, ClaimsPrincipal u, IcsTokenService s, CancellationToken ct) =>
            s.CreateAsync(b.Scope ?? IcsScopes.All, b.TrainId, ActorOf(u), ct).ToHttpAsync()).RequireAuthorization(Policies.Read);
        api.MapPost("/me/ics-tokens/{id}:rotate", (string id, IcsCreateBody? b, ClaimsPrincipal u, IcsTokenService s, CancellationToken ct) =>
            s.RotateAsync(id, b?.Scope ?? IcsScopes.All, b?.TrainId, ActorOf(u), ct).ToHttpAsync()).RequireAuthorization(Policies.Read);
        api.MapPost("/me/ics-tokens/{id}:revoke", (string id, ClaimsPrincipal u, IcsTokenService s, CancellationToken ct) =>
            s.RevokeAsync(id, ActorOf(u), ct).ToHttpAsync()).RequireAuthorization(Policies.Read);

        api.MapGet("/ics/{token}.ics", (string token, HttpContext http, IcsTokenService t, IcsFeedService f, IConfiguration cfg, CancellationToken ct) => Feed(token, IcsScopes.All, null, http, t, f, cfg, ct)).AllowAnonymous();
        api.MapGet("/ics/{token}/all.ics", (string token, HttpContext http, IcsTokenService t, IcsFeedService f, IConfiguration cfg, CancellationToken ct) => Feed(token, IcsScopes.All, null, http, t, f, cfg, ct)).AllowAnonymous();
        api.MapGet("/ics/{token}/mine.ics", (string token, HttpContext http, IcsTokenService t, IcsFeedService f, IConfiguration cfg, CancellationToken ct) => Feed(token, IcsScopes.Mine, null, http, t, f, cfg, ct)).AllowAnonymous();
        api.MapGet("/ics/{token}/freezes.ics", (string token, HttpContext http, IcsTokenService t, IcsFeedService f, IConfiguration cfg, CancellationToken ct) => Feed(token, IcsScopes.Freezes, null, http, t, f, cfg, ct)).AllowAnonymous();
        api.MapGet("/ics/{token}/trains/{trainId}.ics", (string token, string trainId, HttpContext http, IcsTokenService t, IcsFeedService f, IConfiguration cfg, CancellationToken ct) => Feed(token, IcsScopes.Train, trainId, http, t, f, cfg, ct)).AllowAnonymous();
    }

    private static async Task<IResult> Feed(string token, string scope, string? trainId, HttpContext http, IcsTokenService tokens, IcsFeedService feeds, IConfiguration cfg, CancellationToken ct)
    {
        // SEC-D10 (Q-SEC-D5, option b): a valid token is always served. The brake sees only failed lookups: once a client address has failed more than
        // Ics:MaxFailuresPerMinute times this minute its further failures are 429 instead of 404. Behind a proxy that does not forward the client address
        // every caller shares one address, so a brake in front of the lookup let strangers lock every calendar out; tokens are 256-bit, so the brake was
        // never what stops guessing.
        var userId = await tokens.ResolveAsync(token, ct);
        var body = userId is null ? null : await feeds.BuildAsync(scope, trainId, userId, ct);
        if (body is null)
        {
            if (Throttled(http, tokens, cfg, failed: false)) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            Throttled(http, tokens, cfg, failed: true);
            return Results.NotFound();
        }
        var bytes = Encoding.UTF8.GetBytes(body);
        var etag = $"\"{Convert.ToHexStringLower(SHA256.HashData(bytes))[..32]}\"";
        http.Response.Headers.CacheControl = "private, no-cache";
        http.Response.Headers.ETag = etag;
        http.Response.Headers["Referrer-Policy"] = "no-referrer";
        http.Response.Headers["X-Robots-Tag"] = "noindex";
        if (http.Request.Headers.IfNoneMatch.ToString().Split(',').Select(s => s.Trim()).Contains(etag)) return Results.StatusCode(StatusCodes.Status304NotModified);
        return Results.File(bytes, "text/calendar; charset=utf-8");
    }
}
