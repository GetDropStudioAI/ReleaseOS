using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Api.PublicApi;

/// <summary>
/// The front door of the public API (REOS-161 to REOS-163), <c>/api/public/v1/*</c>. It runs before everything else that reads the request and does, in this order:
/// <list type="number">
/// <item>authenticates the bearer token (<see cref="ApiTokenService"/>; looked up on every request, so a revoked token is refused at once) and bounds failed attempts per address;</item>
/// <item>takes one request from the token's bucket and one from its workspace's (<see cref="IngestLimiter"/>, injected clock), sets <c>X-RateLimit-*</c>, answers 429 with <c>Retry-After</c> when either is empty;</item>
/// <item>finds the operation in <see cref="PublicApiCatalog"/>: anything else is 404 (or 405), so nothing outside the documented subset is reachable;</item>
/// <item>checks the scope: 403 naming it, and an audit row for the denial;</item>
/// <item>for a POST with <c>Idempotency-Key</c>, replays the stored result of a repeat without running anything;</item>
/// <item>runs the request as the person the token acts as (cookies and workspace headers are dropped; the token's workspace is the workspace): a forwarded operation is the internal endpoint with the same policies and services;</item>
/// <item>turns the answer into RFC 9457 problem details (the <c>guard</c>, <c>message</c>, <c>current</c> and other members of the internal body are kept inside it) and, for a list, into a cursor page.</item>
/// </list>
/// It sits outside the exception handler, so an unhandled exception or a database rule reaches it as a response and gets the same treatment.
/// </summary>
public sealed class PublicApiGateway(RequestDelegate next, ApiTokenService tokens, IdempotencyService idempotency, IngestLimiter limiter, PublicApiOptions options, ILogger<PublicApiGateway> log)
{
    public const string TokenClaim = "apitoken", TokenNameClaim = "apitokenname", AuthScheme = "ApiToken";
    private const long MaxBufferedBody = 2 * 1024 * 1024;
    private const string ProblemJson = "application/problem+json";
    private static readonly JsonSerializerOptions Out = new() { WriteIndented = false };

    public async Task InvokeAsync(HttpContext http)
    {
        if (!http.Request.Path.StartsWithSegments(PublicApiCatalog.Prefix, out var restPath)) { await next(http); return; }
        var rest = restPath.Value ?? "";
        if (HttpMethods.IsGet(http.Request.Method) && rest == "/openapi.json") { await next(http); return; }   // the document needs no token: it names no data

        var original = http.Response.Body;
        await using var buffer = new MemoryStream();
        http.Response.Body = buffer;
        var publicPath = http.Request.Path.Value ?? "";
        Finish? finish = null;
        try { finish = await HandleAsync(http, rest, publicPath); }
        catch (Exception ex) when (!http.RequestAborted.IsCancellationRequested)
        {
            log.LogError(ex, "Public API request {Method} {Path} failed in the gateway", http.Request.Method, publicPath);
            buffer.SetLength(0);
            WriteProblem(http, StatusCodes.Status500InternalServerError, "InternalError", "The request could not be completed", publicPath);
        }
        finally { http.Response.Body = original; }

        var bytes = buffer.ToArray();
        if (finish is not null) bytes = await finish(http, bytes, publicPath);
        if (bytes.Length > 0 || http.Response.StatusCode is not (StatusCodes.Status204NoContent or StatusCodes.Status304NotModified))
        {
            http.Response.ContentLength = bytes.Length;
            if (bytes.Length > 0) await original.WriteAsync(bytes, http.RequestAborted);
        }
    }

    /// <summary>Runs after the answer exists: turns it into problem details or a page and settles the idempotency key. Returns the bytes to send.</summary>
    private delegate Task<byte[]> Finish(HttpContext http, byte[] body, string publicPath);

    private static string? Bearer(HttpRequest r) => r.Headers.Authorization.ToString() is { } h && h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h[7..].Trim() : null;

    private async Task<Finish?> HandleAsync(HttpContext http, string rest, string publicPath)
    {
        var req = http.Request;
        var address = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // 1. who
        var presented = Bearer(req);
        if (presented is null) { http.Response.Headers.WWWAuthenticate = "Bearer"; WriteProblem(http, 401, ApiGuards.TokenInvalid, "Send the token as Authorization: Bearer rt_…", publicPath); return null; }
        if (!limiter.MayTry(address))
        {
            http.Response.Headers.RetryAfter = "60";
            WriteProblem(http, 429, ApiGuards.RateLimited, "Too many refused tokens from this address; wait for Retry-After seconds", publicPath);
            return null;
        }
        var who = await tokens.AuthenticateAsync(presented, http.RequestAborted);
        if (!who.Ok)
        {
            var reason = ApiTokenService.Reason(who.Denial);
            log.LogWarning("API token refused for {Method} {Path} from {Address}: {Reason}", req.Method, publicPath, address, reason);   // never the token
            if (who.TokenId is null) { if (limiter.NoteFailure(address)) await tokens.RecordRejectedAsync(address, reason, http.RequestAborted); }
            else if (limiter.NoteFailure("t:" + who.TokenId)) await tokens.RecordDeniedAsync(who, reason, req.Method, publicPath, null, http.RequestAborted);
            http.Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";
            WriteProblem(http, 401, ApiGuards.TokenInvalid, "This token is not valid: it is missing, unknown, revoked or expired, or the person it acts as can no longer use this workspace", publicPath);
            return null;
        }
        var me = who.Principal!;

        // 2. rate limit: the token, then its workspace
        var byToken = limiter.Take("pt:" + me.TokenId, options.TokenRequestsPerSecond, options.TokenBurst);
        var byWorkspace = byToken.Allowed ? limiter.Take("pw:" + me.WorkspaceId, options.WorkspaceRequestsPerSecond, options.WorkspaceBurst) : default;
        var shown = !byToken.Allowed || (byWorkspace.Allowed && byToken.Remaining <= byWorkspace.Remaining) ? byToken : byWorkspace;
        var h = http.Response.Headers;
        h["X-RateLimit-Limit"] = shown.Limit.ToString();
        h["X-RateLimit-Remaining"] = shown.Remaining.ToString();
        h["X-RateLimit-Reset"] = shown.ResetSeconds.ToString();
        if (!byToken.Allowed || !byWorkspace.Allowed)
        {
            var which = !byToken.Allowed ? "token" : "workspace";
            h.RetryAfter = shown.RetryAfterSeconds.ToString();
            WriteProblem(http, 429, ApiGuards.RateLimited, $"Too many requests for this {which}; wait {shown.RetryAfterSeconds} seconds (Retry-After)", publicPath, ("limit", which));
            return null;
        }

        // 3. the operation
        var (found, others) = PublicApiCatalog.Find(req.Method, rest);
        if (found is null)
        {
            if (others.Count > 0)
            {
                h.Allow = string.Join(", ", others.Distinct());
                WriteProblem(http, 405, "MethodNotAllowed", $"This path accepts {string.Join(", ", others.Distinct())}", publicPath);
            }
            else WriteProblem(http, 404, ApiGuards.NotInPublicApi, "This is not part of the public API; GET /api/public/v1/openapi.json lists what is", publicPath);
            return null;
        }
        var op = found.Operation;

        // 4. scope
        if (!me.Scopes.Contains(op.Scope))
        {
            if (limiter.NoteFailure("t:" + me.TokenId))
                await tokens.RecordDeniedAsync(who, $"missing scope {op.Scope}", req.Method, publicPath, op.Scope, http.RequestAborted);
            WriteProblem(http, 403, ApiGuards.ScopeDenied, $"This token lacks the '{op.Scope}' scope that {op.OperationId} needs; it has: {string.Join(", ", me.Scopes.Order())}", publicPath,
                ("requiredScope", op.Scope), ("scopes", new JsonArray([.. me.Scopes.Order().Select(s => (JsonNode?)s)])));
            return null;
        }

        // 5. paging parameters
        PageRequest? page = null;
        if (op.Paged)
        {
            var parsed = ParsePage(req, publicPath);
            if (parsed.Error is { } e) { WriteProblem(http, 400, e.Guard, e.Message, publicPath); return null; }
            page = parsed.Page;
        }

        // 6. idempotency
        string? keyId = null;
        if (HttpMethods.IsPost(req.Method) && req.Headers.TryGetValue("Idempotency-Key", out StringValues keys))
        {
            var key = keys.ToString().Trim();
            if (keys.Count != 1 || key.Length is 0 or > 255 || key.Any(c => c < 0x21 || c > 0x7E))
            { WriteProblem(http, 400, ApiGuards.IdempotencyKeyInvalid, "Idempotency-Key is 1 to 255 printable characters, once", publicPath); return null; }
            var (fingerprint, tooBig) = await FingerprintAsync(req);
            if (tooBig) { WriteProblem(http, 413, "PayloadTooLarge", "The request body is larger than the public API accepts with an Idempotency-Key (2 MB)", publicPath); return null; }
            var begin = await idempotency.BeginAsync(me.WorkspaceId, me.TokenId, key, fingerprint, http.RequestAborted);
            switch (begin.Outcome)
            {
                case IdempotencyOutcome.Replay:
                    http.Response.StatusCode = begin.StatusCode;
                    http.Response.ContentType = begin.ContentType;
                    h["Idempotent-Replayed"] = "true";
                    await http.Response.WriteAsync(begin.Body ?? "", http.RequestAborted);
                    return null;
                case IdempotencyOutcome.Mismatch:
                    WriteProblem(http, 422, ApiGuards.IdempotencyKeyReused, "This Idempotency-Key was used for a different request; use a new key for a new request", publicPath);
                    return null;
                case IdempotencyOutcome.InProgress:
                    h.RetryAfter = "1";
                    WriteProblem(http, 409, ApiGuards.IdempotencyInProgress, "The first request with this Idempotency-Key has not finished; try again shortly", publicPath);
                    return null;
                default: keyId = begin.Id; break;
            }
        }

        // 7. run as the person the token acts as
        req.Headers.Remove("Cookie");
        req.Headers.Remove("Authorization");
        req.Headers.Remove(WorkspaceResolution.Header);
        http.Items[WorkspaceRoutePrefix.ItemKey] = me.WorkspaceId;   // WorkspaceResolution checks the owner's membership and takes the role from it
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("uid", me.OwnerUserId), new Claim(ClaimTypes.Email, me.OwnerEmail), new Claim(ClaimTypes.Name, me.OwnerName),
            new Claim(TokenClaim, me.TokenId), new Claim(TokenNameClaim, me.Name),
        ], AuthScheme));
        if (!op.IsNative) req.Path = PublicApiCatalog.InternalPathOf(found);
        if (page is not null || op.Paged) req.QueryString = RewriteQuery(req, op);

        using var via = ApiCallContext.Enter(me.TokenId, me.Name, me.OwnerUserId);
        await next(http);   // the answer is in the buffer; Finish turns it into the public shape

        return async (ctx, body, path) =>
        {
            var status = ctx.Response.StatusCode;
            var type = ctx.Response.ContentType ?? "";
            if (status >= 400) body = Problem(ctx, status, type, body, path);
            else if (page is not null && status == 200) body = Page(ctx, body, path, page, req);
            if (status >= 400 && !ctx.Response.ContentType!.StartsWith(ProblemJson, StringComparison.Ordinal)) ctx.Response.ContentType = ProblemJson;
            if (status == 403 && (ctx.Items.ContainsKey("reos.problem.role")) && limiter.NoteFailure("t:" + me.TokenId))
                await tokens.RecordDeniedAsync(who, "the person the token acts as lacks the role this operation needs", req.Method, path, null, ctx.RequestAborted);
            if (keyId is not null)
            {
                if (status is >= 200 and < 300 && body.Length <= 4 * 1024 * 1024)
                    await idempotency.CompleteAsync(me.WorkspaceId, keyId, status, ctx.Response.ContentType, Encoding.UTF8.GetString(body), ctx.RequestAborted);
                else await idempotency.AbandonAsync(me.WorkspaceId, keyId, ctx.RequestAborted);   // only a success is replayed; a failure changed nothing, so the retry runs again
            }
            return body;
        };
    }

    // ---- idempotency ----------------------------------------------------------------------------------------------------------

    private static async Task<(string Hash, bool TooBig)> FingerprintAsync(HttpRequest req)
    {
        req.EnableBuffering(MaxBufferedBody + 1);
        using var ms = new MemoryStream();
        await req.Body.CopyToAsync(ms, req.HttpContext.RequestAborted);
        req.Body.Position = 0;
        if (ms.Length > MaxBufferedBody) return ("", true);
        var query = string.Join('&', req.Query.SelectMany(q => q.Value.Select(v => $"{q.Key}={v}")).Order(StringComparer.Ordinal));
        using var sha = SHA256.Create();
        sha.TransformBlock(Encoding.UTF8.GetBytes($"{req.Method}\n{req.Path.Value}\n{query}\n"), 0, Encoding.UTF8.GetByteCount($"{req.Method}\n{req.Path.Value}\n{query}\n"), null, 0);
        sha.TransformFinalBlock(ms.GetBuffer(), 0, (int)ms.Length);
        return (Convert.ToHexStringLower(sha.Hash!), false);
    }

    // ---- paging ---------------------------------------------------------------------------------------------------------------

    private sealed record PageRequest(int Limit, string? AfterId, int? Offset, string Fingerprint);
    private sealed record PageParse(PageRequest? Page, (string Guard, string Message)? Error);

    private static string QueryFingerprint(HttpRequest r, string path)
    {
        var q = string.Join('&', r.Query.Where(x => x.Key is not ("cursor" or "limit")).SelectMany(x => x.Value.Select(v => $"{x.Key}={v}")).Order(StringComparer.Ordinal));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path + "?" + q)))[..12];
    }

    private PageParse ParsePage(HttpRequest r, string path)
    {
        var limit = options.DefaultPageSize;
        if (r.Query.TryGetValue("limit", out var l) && (!int.TryParse(l.ToString(), out limit) || limit < 1 || limit > options.MaxPageSize))
            return new(null, (ApiGuards.InvalidLimit, $"limit is a whole number from 1 to {options.MaxPageSize} (default {options.DefaultPageSize})"));
        var fp = QueryFingerprint(r, path);
        if (!r.Query.TryGetValue("cursor", out var c) || c.ToString().Length == 0) return new(new(limit, null, null, fp), null);
        try
        {
            var node = JsonNode.Parse(Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(c.ToString())))!.AsObject();
            if ((string?)node["q"] != fp) return new(null, (ApiGuards.InvalidCursor, "This cursor belongs to a different request; pass cursor only with the same filters it came from"));
            return new(new(limit, (string?)node["a"], (int?)node["o"], fp), null);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException or NullReferenceException)
        {
            return new(null, (ApiGuards.InvalidCursor, "The cursor is not one this API gave out"));
        }
    }

    /// <summary>Takes <c>cursor</c> and <c>limit</c> off the query (the internal list does not know them) and asks a list that has a <c>take</c> for as many as it will give.</summary>
    private static QueryString RewriteQuery(HttpRequest r, PublicOperation op)
    {
        var q = QueryHelpers.ParseQuery(r.QueryString.Value).Where(x => x.Key is not ("cursor" or "limit")).ToDictionary(x => x.Key, x => x.Value);
        if (op.Path is "/deployments" or "/approval-requests") q["take"] = "500";
        return QueryString.Create(q.SelectMany(x => x.Value.Select(v => KeyValuePair.Create(x.Key, (string?)v))));
    }

    private byte[] Page(HttpContext http, byte[] body, string path, PageRequest page, HttpRequest req)
    {
        JsonArray all;
        try { all = JsonNode.Parse(body) as JsonArray ?? throw new JsonException("not an array"); }
        catch (JsonException ex)
        {
            log.LogError(ex, "A list endpoint answered something that is not an array for {Path}", path);
            http.Response.StatusCode = 500;
            return Problem(http, 500, "application/json", [], path);
        }
        var start = 0;
        if (page.AfterId is { } after)
        {
            var idx = all.ToList().FindIndex(n => (string?)n?["id"] == after);
            if (idx < 0)
            {
                http.Response.StatusCode = 400;
                return Problem(http, 400, ProblemJson, Encoding.UTF8.GetBytes(new JsonObject { ["guard"] = ApiGuards.InvalidCursor, ["message"] = "The record this cursor points at is no longer in the list; start again without a cursor" }.ToJsonString()), path);
            }
            start = idx + 1;
        }
        else if (page.Offset is { } o) start = Math.Min(o, all.Count);
        var items = all.Skip(start).Take(page.Limit).Select(n => n?.DeepClone()).ToList();
        var more = start + items.Count < all.Count;
        string? next = null;
        if (more)
        {
            var last = items[^1];
            var cursor = new JsonObject { ["q"] = page.Fingerprint };
            if ((string?)last?["id"] is { } id) cursor["a"] = id; else cursor["o"] = start + items.Count;
            next = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(cursor.ToJsonString()));
        }
        http.Response.ContentType = "application/json; charset=utf-8";
        return JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["data"] = new JsonArray([.. items]), ["page"] = new JsonObject { ["limit"] = page.Limit, ["hasMore"] = more, ["nextCursor"] = next } }, Out);
    }

    // ---- problem details (RFC 9457) -----------------------------------------------------------------------------------------------

    /// <summary>The internal error body inside problem details: <c>type, title, status, detail, instance</c>, then every member the internal body had (<c>guard</c>, <c>message</c>, <c>current</c>, <c>failures</c>, …).</summary>
    private static byte[] Problem(HttpContext http, int status, string contentType, byte[] body, string path)
    {
        JsonObject? inner = null;
        if (body.Length > 0 && contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        { try { inner = JsonNode.Parse(body) as JsonObject; } catch (JsonException) { inner = null; } }
        var guard = (string?)inner?["guard"];
        var message = (string?)inner?["message"] ?? (string?)inner?["detail"];
        if (status >= 500)   // never an exception's text
        { inner = null; guard = "InternalError"; message = "The request could not be completed; if it keeps failing, report the time and the path"; }
        guard ??= status switch
        {
            401 => ApiGuards.TokenInvalid,
            403 => ApiGuards.RoleDenied,
            404 => "NotFound",
            405 => "MethodNotAllowed",
            409 => "VersionConflict",
            413 => "PayloadTooLarge",
            428 => "PreconditionRequired",
            _ => "BadRequest",
        };
        if (status == 403 && guard == ApiGuards.RoleDenied) { message ??= "The person this token acts as does not have the role this operation needs"; http.Items["reos.problem.role"] = true; }
        message ??= Microsoft.AspNetCore.WebUtilities.ReasonPhrases.GetReasonPhrase(status);
        var problem = new JsonObject
        {
            ["type"] = "urn:releaseos:problem:" + guard,
            ["title"] = Microsoft.AspNetCore.WebUtilities.ReasonPhrases.GetReasonPhrase(status),
            ["status"] = status,
            ["detail"] = message,
            ["instance"] = path,
        };
        if (inner is not null)
            foreach (var (k, v) in inner)
                if (k is not ("type" or "title" or "status" or "detail" or "instance" or "traceId")) problem[k] = v?.DeepClone();
        problem["guard"] = guard;
        problem["message"] ??= message;
        http.Response.ContentType = ProblemJson;
        return JsonSerializer.SerializeToUtf8Bytes(problem, Out);
    }

    private static void WriteProblem(HttpContext http, int status, string guard, string detail, string path, params (string Name, object Value)[] extra)
    {
        var problem = new JsonObject
        {
            ["type"] = "urn:releaseos:problem:" + guard, ["title"] = Microsoft.AspNetCore.WebUtilities.ReasonPhrases.GetReasonPhrase(status), ["status"] = status, ["detail"] = detail, ["instance"] = path, ["guard"] = guard, ["message"] = detail,
        };
        foreach (var (n, v) in extra) problem[n] = v is JsonNode node ? node : JsonValue.Create(v)!.AsValue();
        http.Response.StatusCode = status;
        http.Response.ContentType = ProblemJson;
        http.Response.Body.Write(JsonSerializer.SerializeToUtf8Bytes(problem, Out));
    }
}
