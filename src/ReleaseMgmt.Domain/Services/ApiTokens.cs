namespace ReleaseMgmt.Domain.Services;

/// <summary>
/// REOS-162: what a public-API token may do, by area. A scope narrows the token; it never widens it: the person the token acts as still needs the role the internal endpoint asks for.
/// The four scopes are independent (a token with only <c>write</c> cannot read); the Admin screen offers combinations that include <c>read</c>.
/// </summary>
public static class ApiTokenScopes
{
    public const string Read = "read", Write = "write", Approvals = "approvals", Deployments = "deployments";
    public static readonly IReadOnlyList<string> All = [Read, Write, Approvals, Deployments];

    /// <summary>The scopes in <paramref name="names"/> in the canonical order, or null when one is unknown or none is named.</summary>
    public static IReadOnlyList<string>? Parse(IEnumerable<string>? names)
    {
        if (names is null) return null;
        var set = names.Select(n => (n ?? "").Trim()).ToHashSet(StringComparer.Ordinal);
        if (set.Count == 0 || set.Any(n => !All.Contains(n))) return null;
        return [.. All.Where(set.Contains)];
    }

    public static IReadOnlyList<string> Split(string csv) => csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public static string Join(IEnumerable<string> scopes) => string.Join(',', scopes);
}

/// <summary>Guard names of the public API (REOS-161 to REOS-163). They are the <c>guard</c> member of the problem details body.</summary>
public static class ApiGuards
{
    public const string TokenInvalid = "ApiTokenInvalid";          // 401: missing, malformed, unknown, revoked, expired, or its owner can no longer act
    public const string ScopeDenied = "ScopeDenied";               // 403: the token lacks the scope the endpoint needs (the body names it)
    public const string RoleDenied = "RoleDenied";                 // 403: the person the token acts as lacks the role the endpoint needs
    public const string NotInPublicApi = "NotInPublicApi";         // 404: not part of the public surface, whatever the internal API has
    public const string RateLimited = "RateLimited";               // 429
    public const string IdempotencyKeyInvalid = "IdempotencyKeyInvalid";     // 400
    public const string IdempotencyKeyReused = "IdempotencyKeyReused";       // 422: the same key with a different request
    public const string IdempotencyInProgress = "IdempotencyInProgress";     // 409: the first request with this key has not finished
    public const string InvalidCursor = "InvalidCursor";           // 400
    public const string InvalidLimit = "InvalidLimit";             // 400
    public const string ApiTokenInvalid = "ApiTokenInvalid";
    public const string ApiTokenRevoked = "ApiTokenRevoked";
    public const string ApiTokenExpired = "ApiTokenExpired";
    public const string OwnerNotMember = "ApiTokenOwnerNotMember";
}
