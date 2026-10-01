using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Api.Reminders;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Comms;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>
/// Communication dispatch, immutable log and drawer data (REOS-45, PROJECT_SCOPE 5.2 and section 6). RTE and Release Manager dispatch (Policies.Plan);
/// everyone signed in reads. There is deliberately no PUT, PATCH or DELETE: a dispatch is immutable (the API has no route, the database has triggers).
/// </summary>
public static class CommDispatchEndpoints
{
    public sealed record DispatchBody(string? TemplateId, string? ScheduleItemId, string? Channel, string? Format, string? WebhookDestinationId, string? WebhookUrl, bool? IsRehearsal);

    private static Actor ActorOf(ClaimsPrincipal u) => new(u.FindFirstValue("uid") ?? throw new InvalidOperationException("Signed-in user has no uid claim"));

    public static void MapCommDispatch(this RouteGroupBuilder api)
    {
        // POST /trains/{id}/comms:dispatch  If-Match: <train Version the preview showed>
        //   { templateId | scheduleItemId, channel: Copy|Mailto|Webhook, format?: RichText|Markdown (Copy), webhookDestinationId | webhookUrl (Webhook), isRehearsal? } -> DispatchView
        api.MapPost("/trains/{id}/comms:dispatch", (string id, DispatchBody b, ClaimsPrincipal u, HttpRequest r, CommDispatchService s, CancellationToken ct) =>
            s.DispatchAsync(id, new(b.TemplateId, b.ScheduleItemId, b.Channel ?? "", b.Format, b.WebhookDestinationId, b.WebhookUrl, b.IsRehearsal ?? false), ActorOf(u), r.IfMatch(), ct).ToHttpAsync())
            .RequireAuthorization(Policies.Plan);

        // GET /trains/{id}/comms/dispatches?limit&cursor -> { items[], nextCursor, limit }, newest first
        api.MapGet("/trains/{id}/comms/dispatches", async (string id, HttpRequest req, CommDispatchService s, CancellationToken ct) =>
        {
            var cursor = req.Query["cursor"].ToString();
            if (cursor.Length > 0 && !CommDispatchService.IsValidCursor(cursor))
                return Results.Json(new { guard = CommGuards.InvalidCursor, message = "cursor is the nextCursor of a previous page" }, statusCode: StatusCodes.Status400BadRequest);
            int? limit = int.TryParse(req.Query["limit"], out var l) ? l : null;
            return await s.ListAsync(id, limit, cursor.Length > 0 ? cursor : null, ct) is { } page ? Results.Ok(page) : Results.NotFound(new { message = "train not found" });
        }).RequireAuthorization(Policies.Read);

        // GET /comms/dispatches/{id} -> DispatchView with the stored body (JSON-encoded; decodes to the exact stored string)
        api.MapGet("/comms/dispatches/{id}", async (string id, CommDispatchService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } v ? Results.Ok(v) : Results.NotFound(new { message = "dispatch not found" })).RequireAuthorization(Policies.Read);

        // GET /comms/dispatches/{id}/body -> the stored body as raw UTF-8 bytes (text/plain, nosniff: a body may be HTML and is never rendered by the browser)
        api.MapGet("/comms/dispatches/{id}/body", async (string id, HttpContext http, CommDispatchService s, CancellationToken ct) =>
        {
            if (await s.GetAsync(id, ct) is not { } v) return Results.NotFound(new { message = "dispatch not found" });
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            http.Response.Headers["X-Body-Sha256"] = v.BodySha256;
            return Results.Bytes(new UTF8Encoding(false).GetBytes(v.Body), "text/plain; charset=utf-8");
        }).RequireAuthorization(Policies.Read);

        // GET /trains/{id}/comms/dispatch-context -> { asOf, trainVersion, templates[], schedule[], webhookTargets[] } (no URLs)
        api.MapGet("/trains/{id}/comms/dispatch-context", async (string id, CommDispatchService s, CancellationToken ct) =>
            await s.GetContextAsync(id, ct) is { } v ? Results.Ok(v) : Results.NotFound(new { message = "train not found" })).RequireAuthorization(Policies.Read);
    }

    /// <summary>Services for dispatch: the fail-closed default renderer (TryAdd, so an adapter over the real hydrator wins), the webhook sender and its SSRF-guarded client.</summary>
    public static IServiceCollection AddCommDispatch(this IServiceCollection services, IConfiguration config)
    {
        services.TryAddSingleton(OutboundAddressPolicy.From(config));   // REOS-77
        services.AddHttpClient(CommWebhookSender.ClientName)
            .ConfigurePrimaryHttpMessageHandler(sp => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,                       // a redirect is a second, unchecked destination
                UseProxy = false,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                ConnectCallback = async (ctx, ct) =>
                {
                    // Resolve and vet here, at connect time, so the address that is checked is the address that is used.
                    var allowPrivate = config.GetValue("Comms:Webhooks:AllowPrivateTargets", false);
                    var addrs = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
                    var policy = sp.GetRequiredService<OutboundAddressPolicy>();
                    var ok = addrs.Where(a => allowPrivate || !policy.IsBlocked(a)).ToArray();
                    if (ok.Length == 0) throw new HttpRequestException("The webhook target resolves only to blocked addresses");
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try { await socket.ConnectAsync(ok, ctx.DnsEndPoint.Port, ct); return new NetworkStream(socket, ownsSocket: true); }
                    catch { socket.Dispose(); throw; }
                },
            });
        services.AddSingleton<ICommWebhookSender, CommWebhookSender>();
        services.AddSingleton<CommDispatchService>();
        services.TryAddSingleton<ICommDispatchRenderer, FailClosedCommRenderer>();
        return services;
    }
}
