using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Email;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>
/// Email delivery (REOS-167, D61). Everyone signed in reads and changes their own channel preferences; RTE and Release Manager see the state of the feature, the send log and the suppression list, send a test to their own
/// address, store the provider credential and make the secret that signs inbound bounce events (a Viewer or Governance Officer gets 403 on those). Credentials are write-only. The one anonymous route is the provider's
/// bounce and complaint callback: it is refused unless the body is signed with the events secret (the same HMAC shape as the outbound webhooks) and is not older than five minutes.
/// </summary>
public static class EmailEndpoints
{
    public sealed record PreferenceBody(string? Category, string? Channel, string? Mode, int? Version);
    public sealed record CredentialBody(string? Value);
    public sealed record SuppressionBody(string? Address, string? Detail);

    private static Actor ActorOf(ClaimsPrincipal u) => new(u.FindFirstValue("uid") ?? throw new InvalidOperationException("Signed-in user has no uid claim"));

    public static void MapEmail(this RouteGroupBuilder api)
    {
        api.MapGet("/me/notification-preferences", async (ClaimsPrincipal u, EmailPreferenceService s, EmailOptions o, CancellationToken ct) => Results.Ok(new
        {
            emailEnabled = o.Enabled, digestHourUtc = o.DigestHourUtc,
            categories = EmailCategories.Choosable.Select(c => new { key = c, label = EmailCategories.Label(c) }),
            channels = EmailCategories.Channels,
            modes = EmailCategories.Modes.Select(m => new { key = m, label = EmailCategories.ModeLabel(m) }),
            preferences = await s.ListAsync(ActorOf(u).UserId, ct),
        })).RequireAuthorization(Policies.Read);
        api.MapPut("/me/notification-preferences", async (PreferenceBody b, ClaimsPrincipal u, EmailPreferenceService s, CancellationToken ct) =>
            (await s.SetAsync(ActorOf(u), b.Category, b.Channel, b.Mode, b.Version, ct)).ToHttp()).RequireAuthorization(Policies.Read);

        api.MapGet("/email/status", async (EmailAdminService s, CancellationToken ct) => Results.Ok(await s.StatusAsync(ct))).RequireAuthorization(Policies.Admin);
        api.MapGet("/email/messages", async (string? status, int? take, EmailAdminService s, CancellationToken ct) => Results.Ok(new { messages = await s.ListAsync(status, take ?? 100, ct) })).RequireAuthorization(Policies.Admin);
        api.MapGet("/email/messages/{id}", async (string id, EmailAdminService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } m ? Results.Ok(m) : Results.NotFound(new { message = "Email message not found" })).RequireAuthorization(Policies.Admin);
        api.MapPost("/email/test", async (ClaimsPrincipal u, EmailAdminService s, CancellationToken ct) => (await s.SendTestAsync(ActorOf(u), ct)).ToHttp()).RequireAuthorization(Policies.Admin);
        api.MapPut("/email/credential", async (CredentialBody b, ClaimsPrincipal u, EmailAdminService s, CancellationToken ct) => (await s.SetCredentialAsync(b.Value, ActorOf(u), ct)).ToHttp()).RequireAuthorization(Policies.Admin);
        api.MapDelete("/email/credential", async (ClaimsPrincipal u, EmailAdminService s, CancellationToken ct) => (await s.ClearCredentialAsync(ActorOf(u), ct)).ToHttp()).RequireAuthorization(Policies.Admin);
        api.MapPost("/email/events-secret:rotate", async (ClaimsPrincipal u, EmailAdminService s, CancellationToken ct) =>
        {
            var r = await s.RotateEventsSecretAsync(ActorOf(u), ct);
            return r.IsOk ? Results.Ok(new { secret = r.Value, signatureHeader = ControlSignature.SignatureHeader, timestampHeader = ControlSignature.TimestampHeader, idHeader = ControlSignature.DeliveryHeader, toleranceSeconds = ControlSignature.ToleranceSeconds }) : r.ToHttp();
        }).RequireAuthorization(Policies.Admin);

        api.MapGet("/email/suppressions", async (int? take, EmailSuppressionService s, CancellationToken ct) => Results.Ok(new { suppressions = await s.ListAsync(take ?? 200, ct) })).RequireAuthorization(Policies.Admin);
        api.MapPost("/email/suppressions", async (SuppressionBody b, ClaimsPrincipal u, EmailSuppressionService s, CancellationToken ct) =>
        {
            var r = await s.AddAsync(b.Address, b.Detail, ActorOf(u), ct);
            return r.IsOk ? Results.Json(r.Value, statusCode: StatusCodes.Status201Created) : r.ToHttp();
        }).RequireAuthorization(Policies.Admin);
        api.MapDelete("/email/suppressions/{id}", async (string id, ClaimsPrincipal u, HttpRequest req, EmailSuppressionService s, CancellationToken ct) =>
            (await s.RemoveAsync(id, ActorOf(u), req.IfMatch(), ct)).ToHttp()).RequireAuthorization(Policies.Admin);

        api.MapPost("/email/events", async (HttpRequest req, EmailEventService s, IEmailCredentials creds, TimeProvider time, ILoggerFactory lf, CancellationToken ct) =>
        {
            var log = lf.CreateLogger("EmailEvents");
            var secret = await creds.GetAsync(IEmailCredentials.Events, ct);
            if (secret is null) return Results.NotFound(new { message = "Email events are not set up" });
            string body;
            using (var reader = new StreamReader(req.Body, Encoding.UTF8, false, 1024, leaveOpen: true))
            {
                var buf = new char[20_001];
                var n = await reader.ReadBlockAsync(buf, 0, buf.Length);
                if (n > 20_000) return Results.Json(new { message = "The event is too large" }, statusCode: StatusCodes.Status413PayloadTooLarge);
                body = new string(buf, 0, n);
            }
            var id = req.Headers[ControlSignature.DeliveryHeader].ToString();
            var given = req.Headers[ControlSignature.SignatureHeader].ToString();
            if (!long.TryParse(req.Headers[ControlSignature.TimestampHeader].ToString(), out var ts) || id.Length is 0 or > 100 || given.Length == 0
                || Math.Abs(time.GetUtcNow().ToUnixTimeSeconds() - ts) > ControlSignature.ToleranceSeconds
                || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(ControlSignature.Sign(secret, ts, id, body)), Encoding.ASCII.GetBytes(given)))
            {
                log.LogWarning("Refused an email event that was not signed with the events secret (or was stale)");
                return Results.Json(new { guard = "BadSignature", message = "The signature is missing, wrong or too old" }, statusCode: StatusCodes.Status401Unauthorized);
            }
            EmailProviderEvent? ev;
            try { ev = JsonSerializer.Deserialize<EmailProviderEvent>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
            catch (JsonException) { ev = null; }
            if (ev is null) return Results.Json(new { guard = EmailGuards.BadEvent, message = "The body is not a JSON event {type, address, messageId, reason}" }, statusCode: StatusCodes.Status422UnprocessableEntity);
            var r = await s.RecordAsync(ev, ct);
            return r.IsOk ? Results.Json(new { matched = r.Value }, statusCode: StatusCodes.Status202Accepted) : r.ToHttp();
        }).AllowAnonymous();
    }
}
