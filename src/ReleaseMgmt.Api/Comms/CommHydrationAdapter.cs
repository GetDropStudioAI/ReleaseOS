using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Comms;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Api.Comms;

/// <summary>
/// Connects dispatch (<see cref="ICommDispatchRenderer"/>, REOS-45) to the token hydrator (<see cref="CommHydrationService"/>, REOS-44), so a dispatch is
/// rendered by the same single-snapshot code the preview uses. Token errors are returned, never thrown; a missing train or template is a token error too,
/// so dispatch refuses instead of sending an empty message.
/// </summary>
public sealed class CommHydrationAdapter(CommHydrationService hydrator, TimeProvider time) : ICommDispatchRenderer
{
    public async Task<RenderedComm> RenderAsync(string trainId, string? templateId, string? text, string target, CancellationToken ct)
    {
        if (!Enum.TryParse<CommTarget>(target, ignoreCase: true, out var t))
            return Failed($"unknown render target '{target}'");

        // A template renders its body. Ad-hoc text is the subject line, so it goes through the subject path (single line, never markup).
        var source = templateId is not null ? new HydrateSource(TemplateId: templateId) : new HydrateSource(Subject: text, Text: "");
        var r = await hydrator.HydrateAsync(trainId, source, t, ct);
        if (!r.IsOk)
            return Failed(r.Failures.Count > 0 ? r.Failures[0].Message : $"{r.Missing} not found");

        var h = r.Value!;
        var rendered = templateId is not null ? h.Text : h.Subject ?? "";
        var errors = h.TokenErrors.Select(e => $"{e.Part}, line {e.Line}: {e.Message}").ToList();
        return new RenderedComm(rendered, errors, h.AsOf, h.TrainVersion);
    }

    private RenderedComm Failed(string why) => new("", [why], time.GetUtcNow().UtcDateTime, 0);
}
