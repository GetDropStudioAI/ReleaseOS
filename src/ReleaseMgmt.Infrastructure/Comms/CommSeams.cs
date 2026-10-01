namespace ReleaseMgmt.Infrastructure.Comms;

/// <summary>Escaping targets a message can be rendered for (PROJECT_SCOPE 5.2: Markdown; HTML for rich-text copy; JSON for webhooks).</summary>
public static class CommTargets
{
    public const string PlainText = "PlainText", Markdown = "Markdown", Html = "Html", JsonString = "JsonString";
    public static readonly string[] All = [PlainText, Markdown, Html, JsonString];
}

/// <summary>
/// One hydrated message. <paramref name="TokenErrors"/> is empty when every token is known; <paramref name="AsOf"/> is the snapshot instant and
/// <paramref name="TrainVersion"/> the train's <c>Version</c> that snapshot was read at.
/// For target <see cref="CommTargets.JsonString"/> <paramref name="Text"/> is the message already escaped for use INSIDE a JSON string literal (no surrounding quotes).
/// </summary>
public sealed record RenderedComm(string Text, IReadOnlyList<string> TokenErrors, DateTime AsOf, int TrainVersion);

/// <summary>
/// The hydration seam (REOS-45 consumes it, REOS-44 implements it). Dispatch always renders through it and refuses to dispatch when
/// <see cref="RenderedComm.TokenErrors"/> is not empty. Give either <paramref name="templateId"/> (a per-train CommTemplates id: renders its body)
/// or <paramref name="text"/> (an ad-hoc source such as the subject line).
/// </summary>
public interface ICommDispatchRenderer
{
    Task<RenderedComm> RenderAsync(string trainId, string? templateId, string? text, string target, CancellationToken ct);
}

/// <summary>
/// Registered only when nothing else is (TryAddSingleton). It fails closed: every render is a token error, so nothing can be dispatched by accident
/// before the real hydrator is wired in. It is NOT a renderer and does no substitution.
/// </summary>
public sealed class FailClosedCommRenderer(TimeProvider time) : ICommDispatchRenderer
{
    public const string Message = "renderer not configured";
    public Task<RenderedComm> RenderAsync(string trainId, string? templateId, string? text, string target, CancellationToken ct) =>
        Task.FromResult(new RenderedComm("", [Message], time.GetUtcNow().UtcDateTime, 0));
}

/// <summary>An allowlisted destination as the sender receives it: <see cref="ProtectedUrl"/> is the stored Data Protection payload, decrypted only inside the sender (Q-053e);
/// <see cref="Host"/> is what a result or alert may show.</summary>
public sealed record CommWebhookTarget(string Id, string Name, string Host, string ProtectedUrl, string Kind);

/// <summary>Outcome of one send. <see cref="Reason"/> never contains the URL; <see cref="Host"/> is the only part of it that may be shown.</summary>
public sealed record CommWebhookResult(bool Delivered, string Host, string? Reason);

/// <summary>Posts an already-rendered JSON body to an allowlisted destination. Implementations must not throw for delivery problems (return a failed result).</summary>
public interface ICommWebhookSender
{
    Task<CommWebhookResult> SendAsync(CommWebhookTarget target, string jsonBody, CancellationToken ct);
}

/// <summary>Registered when nothing else is: refuses to send. Keeps the host bootable in tests that never touch webhooks.</summary>
public sealed class NoCommWebhookSender : ICommWebhookSender
{
    public Task<CommWebhookResult> SendAsync(CommWebhookTarget target, string jsonBody, CancellationToken ct) =>
        Task.FromResult(new CommWebhookResult(false, "", "no webhook sender is configured"));
}

/// <summary>Guard names of the communication dispatch (422 bodies: <c>{guard, message, items}</c>).</summary>
public static class CommGuards
{
    public const string TokenErrors = "TokenErrors";
    public const string WebhookNotAllowed = "WebhookNotAllowed";
    public const string WebhookNotHttps = "WebhookNotHttps";
    public const string DispatchRole = "DispatchRole";
    public const string InvalidChannel = "InvalidChannel";
    public const string InvalidFormat = "InvalidFormat";
    public const string TemplateRequired = "TemplateRequired";
    public const string TemplateMismatch = "TemplateMismatch";
    public const string ScheduleAlreadySent = "ScheduleAlreadySent";
    public const string EmptyMessage = "EmptyMessage";
    public const string MessageTooLarge = "MessageTooLarge";
    public const string RenderInvalid = "RenderInvalid";
    public const string InvalidCursor = "InvalidCursor";
}
