using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Api.Auth;

/// <summary>
/// REOS-110: authentication of the push ingest API by a per-workspace API key (<c>Authorization: Bearer rk_…</c>, or <c>X-Api-Key</c>). The key says which workspace it writes to; it carries
/// no role and opens nothing but <c>/api/v1/ingest/*</c> (policy <see cref="Policies.Ingest"/>). A missing key is a plain 401. A key that is malformed, unknown or revoked is a 401 that is
/// logged and audited (host-wide <c>IngestKey/Rejected</c> row: the caller's address and why, never the key); the audit rows and the attempts are bounded per address, so a guessing client
/// cannot flood the log or lock the database. Beyond its rate limit a valid key gets 429 with <c>Retry-After</c>.
/// </summary>
public static class IngestKeyAuthentication
{
    public const string Scheme = "IngestKey";
    public const string KeyClaim = "ik", NameClaim = "ikname";

    public static IngestPrincipal? PrincipalOf(ClaimsPrincipal u) =>
        u.FindFirst(KeyClaim)?.Value is { } id && u.FindFirst(WorkspaceResolution.Claim)?.Value is { } ws ? new(id, u.FindFirst(NameClaim)?.Value ?? "", ws) : null;

    internal const string ThrottledItem = "reos.ingest.throttled", RetryAfterItem = "reos.ingest.retryAfter";
}

/// <summary>
/// Token bucket per key (<c>Ingest:RequestsPerSecond</c>, default 100; <c>Ingest:Burst</c>, default 200) and a failure budget per address (<c>Ingest:FailedKeysPerMinute</c>, default 30, of which
/// <c>Ingest:RejectionAuditsPerMinute</c>, default 10, are audited). In memory: a restart refills every bucket.
/// </summary>
public sealed class IngestLimiter(TimeProvider time, IConfiguration config)
{
    private sealed class Bucket { public double Tokens; public long Last; public int Failures; public int Audits; public long WindowStart; public long Touched; }
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();
    private long _calls;
    private const int PruneEvery = 1000, PruneAbove = 5000;
    private double Rate => Math.Max(1, config.GetValue("Ingest:RequestsPerSecond", 100));
    private double Burst => Math.Max(Rate, config.GetValue("Ingest:Burst", 200));
    private int FailureBudget => Math.Max(1, config.GetValue("Ingest:FailedKeysPerMinute", 30));
    private int AuditBudget => Math.Max(0, config.GetValue("Ingest:RejectionAuditsPerMinute", 10));
    private long Now => time.GetTimestamp();

    /// <summary>Addresses come from the caller: forget the idle ones now and then, so a client spreading refused keys over many addresses cannot grow this without bound.</summary>
    private void Prune()
    {
        if (Interlocked.Increment(ref _calls) % PruneEvery != 0 || _buckets.Count < PruneAbove) return;
        foreach (var (k, b) in _buckets)
            if (time.GetElapsedTime(b.Touched, Now) > TimeSpan.FromMinutes(5)) _buckets.TryRemove(k, out _);
    }

    /// <summary>Takes one request from the key's bucket; false with the seconds to wait when it is empty.</summary>
    public bool TryTake(string keyId, out int retryAfterSeconds)
    {
        Prune();
        var b = _buckets.GetOrAdd("k:" + keyId, _ => new Bucket { Tokens = Burst, Last = Now, Touched = Now });
        lock (b)
        {
            var now = Now;
            b.Touched = now;
            b.Tokens = Math.Min(Burst, b.Tokens + time.GetElapsedTime(b.Last, now).TotalSeconds * Rate);
            b.Last = now;
            if (b.Tokens >= 1) { b.Tokens -= 1; retryAfterSeconds = 0; return true; }
            retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((1 - b.Tokens) / Rate));
            return false;
        }
    }

    private Bucket Window(string address)
    {
        Prune();
        var b = _buckets.GetOrAdd("a:" + address, _ => new Bucket { WindowStart = Now, Touched = Now });
        b.Touched = Now;
        if (time.GetElapsedTime(b.WindowStart, Now) >= TimeSpan.FromMinutes(1)) { b.WindowStart = Now; b.Failures = 0; b.Audits = 0; }
        return b;
    }

    /// <summary>True while the address may still try keys.</summary>
    public bool MayTry(string address) { var b = Window(address); lock (b) return b.Failures < FailureBudget; }

    /// <summary>Counts a failed key; true when this one should also be audited.</summary>
    public bool NoteFailure(string address)
    {
        var b = Window(address);
        lock (b) { b.Failures++; if (b.Audits < AuditBudget) { b.Audits++; return true; } return false; }
    }
}

public sealed class IngestKeyAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, IngestKeyService keys, IngestLimiter limiter)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private static string? Presented(HttpRequest r)
    {
        var h = r.Headers.Authorization.ToString();
        if (h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return h[7..].Trim();
        var k = r.Headers["X-Api-Key"].ToString().Trim();
        return k.Length > 0 ? k : null;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var presented = Presented(Request);
        if (presented is null) return AuthenticateResult.NoResult();
        var address = Context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!limiter.MayTry(address))
        {
            Context.Items[IngestKeyAuthentication.ThrottledItem] = true;
            Context.Items[IngestKeyAuthentication.RetryAfterItem] = 60;
            return AuthenticateResult.Fail("too many refused keys from this address");
        }
        var who = await keys.AuthenticateAsync(presented, Context.RequestAborted);
        if (who is null)
        {
            Logger.LogWarning("Ingest key refused for {Method} {Path} from {Address}", Request.Method, Request.Path.Value, address);   // never the key
            if (limiter.NoteFailure(address)) await keys.RecordRejectedAsync(address, "malformed, unknown or revoked key", Context.RequestAborted);
            return AuthenticateResult.Fail("invalid ingest key");
        }
        if (!limiter.TryTake(who.KeyId, out var retry))
        {
            Context.Items[IngestKeyAuthentication.ThrottledItem] = true;
            Context.Items[IngestKeyAuthentication.RetryAfterItem] = retry;
            return AuthenticateResult.Fail("rate limit");
        }
        var identity = new ClaimsIdentity([new Claim(IngestKeyAuthentication.KeyClaim, who.KeyId), new Claim(IngestKeyAuthentication.NameClaim, who.KeyName), new Claim(WorkspaceResolution.Claim, who.WorkspaceId)], IngestKeyAuthentication.Scheme);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), IngestKeyAuthentication.Scheme));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (Context.Items.ContainsKey(IngestKeyAuthentication.ThrottledItem))
        {
            Response.StatusCode = StatusCodes.Status429TooManyRequests;
            Response.Headers.RetryAfter = (Context.Items[IngestKeyAuthentication.RetryAfterItem] as int? ?? 1).ToString();
            await Response.WriteAsJsonAsync(new { guard = ReleaseModelGuards.RateLimited, message = "Too many requests; wait for Retry-After seconds" });
            return;
        }
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        await Response.WriteAsJsonAsync(new { guard = ReleaseModelGuards.InvalidIngestKey, message = "A valid API key is required (Authorization: Bearer rk_…)" });
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) => HandleChallengeAsync(properties);
}
