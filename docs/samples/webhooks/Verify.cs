// Verifies a ReleaseOS webhook delivery (docs/WEBHOOKS.md). .NET 10 single-file app:  dotnet run docs/samples/webhooks/Verify.cs -- --selftest
// In a real receiver, call ReleaseOsWebhook.Verify(...) with the RAW request body (the exact bytes, not a re-serialised object) and the three headers.
using System.Security.Cryptography;
using System.Text;

if (args.Contains("--selftest"))
{
    const string secret = "whsec_test_secret", delivery = "7d1c0a52-8d3e-4f5e-9d3b-0c1b6f6e2a11", body = "{\"id\":\"evt_1\",\"type\":\"gate.failed\"}";
    const long ts = 1792504800;
    const string expected = "v1=ac276cd9bebb669b711a37e14b70c1398c4d7b8f6a6ed3f563b8813fb4f43dff";
    var ok = ReleaseOsWebhook.Verify(secret, ts.ToString(), delivery, body, expected, ts)
        && !ReleaseOsWebhook.Verify(secret, ts.ToString(), delivery, body + " ", expected, ts)               // body changed
        && !ReleaseOsWebhook.Verify("whsec_other", ts.ToString(), delivery, body, expected, ts)              // wrong secret
        && !ReleaseOsWebhook.Verify(secret, ts.ToString(), delivery, body, expected, ts + 301)               // outside the replay window
        && ReleaseOsWebhook.Verify(secret, ts.ToString(), delivery, body, expected, ts + 300);               // inside it
    Console.WriteLine(ok ? "ok" : "FAILED");
    return ok ? 0 : 1;
}
Console.WriteLine("Use ReleaseOsWebhook.Verify from your own endpoint; run with --selftest to check this sample.");
return 0;

public static class ReleaseOsWebhook
{
    public const int ToleranceSeconds = 300;

    /// <param name="secret">The whsec_ secret shown once when the webhook was added or its secret rotated.</param>
    /// <param name="timestampHeader">X-ReleaseOS-Timestamp (Unix seconds).</param>
    /// <param name="deliveryId">X-ReleaseOS-Delivery.</param>
    /// <param name="rawBody">The request body exactly as received.</param>
    /// <param name="signatureHeader">X-ReleaseOS-Signature, "v1=" and 64 hex characters.</param>
    /// <param name="nowUnixSeconds">Your own clock, DateTimeOffset.UtcNow.ToUnixTimeSeconds().</param>
    public static bool Verify(string secret, string timestampHeader, string deliveryId, string rawBody, string? signatureHeader, long nowUnixSeconds)
    {
        if (signatureHeader is null || !long.TryParse(timestampHeader, out var ts)) return false;
        if (Math.Abs(nowUnixSeconds - ts) > ToleranceSeconds) return false;                    // replay window
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{ts}.{deliveryId}.{rawBody}"));
        var expected = Encoding.ASCII.GetBytes("v1=" + Convert.ToHexStringLower(mac));
        return CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(signatureHeader));   // constant time
    }
    // After Verify: store deliveryId (for a day or more) and answer 200 without acting when you have seen it: delivery is at least once.
}
