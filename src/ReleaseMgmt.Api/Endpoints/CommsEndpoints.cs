using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Reminders;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>
/// Communications core (REOS-43/44, PROJECT_SCOPE 5.2). Everyone signed in reads and previews. The library (an admin screen) and per-train messages are
/// RTE / Release Manager work (Admin and Plan policies, same roles); marking a message sent is dispatch-level, so RTE / Release Manager too.
/// </summary>
public static class CommsEndpoints
{
    public sealed record PreviewBody(string? TemplateId, string? LibraryTemplateId, string? Subject, string? Text, string? Target);

    private static Actor ActorOf(ClaimsPrincipal u) => new(u.FindFirstValue("uid") ?? throw new InvalidOperationException("Signed-in user has no uid claim"));

    /// <summary>Registers the comm services (one line in Program.cs).</summary>
    public static IServiceCollection AddCommunications(this IServiceCollection s)
    {
        s.TryAddSingleton(sp => DisplayClock.From(sp.GetRequiredService<IConfiguration>()));
        s.AddSingleton<CommHydrationService>();
        s.AddSingleton<CommLibraryService>();
        s.AddSingleton<CommScheduleService>();
        return s;
    }

    public static void MapComms(this RouteGroupBuilder api)
    {
        // ---- token list (the editor highlights from this, so the client never carries its own copy of the allowlist)
        api.MapGet("/comm-library/tokens", () => Results.Ok(CommTokens.Allowlist.Select(n => new { name = n, description = CommTokens.Descriptions[n] })))
            .RequireAuthorization(Policies.Read);

        // ---- library
        api.MapGet("/comm-library", async (CommLibraryService s, CancellationToken ct) => Results.Ok(await s.ListLibraryAsync(ct))).RequireAuthorization(Policies.Read);
        api.MapGet("/comm-library/{id}", async (string id, CommLibraryService s, CancellationToken ct) =>
            await s.GetLibraryAsync(id, ct) is { } l ? Results.Ok(l) : Results.NotFound(new { message = "library template not found" })).RequireAuthorization(Policies.Read);
        api.MapPost("/comm-library", (LibraryTemplateInput b, ClaimsPrincipal u, CommLibraryService s, CancellationToken ct) =>
            s.CreateLibraryAsync(b, ActorOf(u), ct).ToHttpAsync()).RequireAuthorization(Policies.Admin);
        api.MapPut("/comm-library/{id}", (string id, LibraryTemplateInput b, ClaimsPrincipal u, HttpRequest r, CommLibraryService s, CancellationToken ct) =>
            s.UpdateLibraryAsync(id, b, ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.Admin);

        // ---- per-train copies
        api.MapGet("/trains/{id}/comms", async (string id, CommLibraryService s, CancellationToken ct) =>
            await s.ListForTrainAsync(id, ct) is { } l ? Results.Ok(l) : Results.NotFound(new { message = "train not found" })).RequireAuthorization(Policies.Read);
        api.MapPost("/trains/{id}/comms", (string id, CopyToTrainInput b, ClaimsPrincipal u, CommLibraryService s, CancellationToken ct) =>
            s.CopyToTrainAsync(id, b, ActorOf(u), ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);
        api.MapGet("/comm-templates/{id}", async (string id, CommLibraryService s, CancellationToken ct) =>
            await s.GetForTrainAsync(id, ct) is { } t ? Results.Ok(t) : Results.NotFound(new { message = "message template not found" })).RequireAuthorization(Policies.Read);
        api.MapPut("/comm-templates/{id}", (string id, TrainCommTemplateInput b, ClaimsPrincipal u, HttpRequest r, CommLibraryService s, CancellationToken ct) =>
            s.UpdateForTrainAsync(id, b, ActorOf(u), r.IfMatch(), ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);

        // ---- preview (token hydration; REOS-45 dispatch reuses CommHydrationService.HydrateAsync)
        api.MapPost("/trains/{id}/comms:preview", async (string id, PreviewBody b, CommHydrationService s, CancellationToken ct) =>
        {
            if (!Enum.TryParse<CommTarget>(b.Target ?? nameof(CommTarget.Markdown), ignoreCase: true, out var target) || !Enum.IsDefined(target))
                return Results.Json(Results2.GuardBody([new GuardFailure("CommPreviewTarget", $"Target must be one of {string.Join(", ", Enum.GetNames<CommTarget>())}")]), statusCode: StatusCodes.Status422UnprocessableEntity);
            var r = await s.HydrateAsync(id, new HydrateSource(b.TemplateId, b.LibraryTemplateId, b.Subject, b.Text), target, ct);
            return r.Kind == ResultKind.Ok ? Results.Ok(Shape(r.Value!)) : r.ToHttp();
        }).RequireAuthorization(Policies.Read);

        // ---- T-minus schedule
        api.MapGet("/trains/{id}/comm-schedule", async (string id, CommScheduleService s, CancellationToken ct) =>
            await s.ListAsync(id, ct) is { } l ? Results.Ok(l) : Results.NotFound(new { message = "train not found" })).RequireAuthorization(Policies.Read);
        api.MapPost("/trains/{id}/comm-schedule:seed", (string id, SeedScheduleInput b, ClaimsPrincipal u, CommScheduleService s, CancellationToken ct) =>
            s.SeedAsync(id, b, ActorOf(u), ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);
        api.MapPost("/comm-schedule/{id}:mark-sent", (string id, ClaimsPrincipal u, HttpRequest r, CommScheduleService s, CancellationToken ct) =>
            s.MarkSentAsync(id, ActorOf(u), r.IfMatch(), null, ct).ToHttpAsync()).RequireAuthorization(Policies.Plan);
    }

    private static object Shape(HydrationResult h) => new
    {
        subject = h.Subject, text = h.Text, tokenErrors = h.TokenErrors, asOf = h.AsOf, trainVersion = h.TrainVersion, canDispatch = h.CanDispatch,
        target = h.Target.ToString(), tokensUsed = h.TokensUsed, templateId = h.TemplateId, templateVersion = h.TemplateVersion,
    };
}
