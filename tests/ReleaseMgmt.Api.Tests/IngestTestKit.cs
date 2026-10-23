using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using static ReleaseMgmt.Api.Tests.LifecycleContractTests;

namespace ReleaseMgmt.Api.Tests;

/// <summary>Test helpers for the push ingest API (REOS-110): an API key written straight into the database, the way an admin's POST /ingest-keys would have (hash only).</summary>
internal static class IngestTestKit
{
    private static int _n;

    /// <summary>A usable key for <paramref name="workspaceId"/>. <c>LastUsedAt</c> is set far ahead so using it does not write (tests that compare the whole database).</summary>
    public static string Key(ApiFactory f, string workspaceId = "default", string name = "CI test", string? revokedAt = null)
    {
        var secret = "rk_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
        var id = $"ik-test-{Interlocked.Increment(ref _n)}";
        var by = revokedAt is null ? "NULL" : $"(SELECT Id FROM Users LIMIT 1)";
        Sql(f, $@"INSERT INTO IngestKeys(Id,Name,KeyPrefix,KeySha256,CreatedAt,LastUsedAt,RevokedAt,RevokedByUserId,WorkspaceId)
                  VALUES('{id}','{name}','{secret[..8]}','{hash}','2026-10-01T00:00:00Z','2999-01-01T00:00:00Z',{(revokedAt is null ? "NULL" : $"'{revokedAt}'")},{by},'{workspaceId}')");
        return secret;
    }

    public static HttpClient Client(ApiFactory f, string secret)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return c;
    }

    public static StringContent Json(object o) => new(System.Text.Json.JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
}
