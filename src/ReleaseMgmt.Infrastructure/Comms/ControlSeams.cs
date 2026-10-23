namespace ReleaseMgmt.Infrastructure.Comms;

/// <summary>One signed control call (REOS-112): the exact body, the delivery id (the action's id, the receiver's replay key), the timestamp it was signed at and the signature header value.</summary>
public sealed record ControlDelivery(string DeliveryId, long UnixSeconds, string Body, string Signature);

/// <summary>Outcome of one control call. <see cref="Reason"/> never contains the URL (it is a secret); <see cref="Host"/> is the only part of it that may be shown.</summary>
public sealed record ControlSendResult(bool Delivered, int? HttpStatus, string Host, string? Reason);

/// <summary>
/// Posts a signed control call to an environment's webhook destination. The implementation applies every protection the other outbound webhooks have (https only, no credentials in
/// the URL, no redirects, private and local addresses refused at save time and at connect time, a hard timeout) and makes exactly one attempt: a pause, promote or rollback is not safe to
/// repeat blindly. Never throws for delivery problems: returns a failed result.
/// </summary>
public interface IControlWebhookSender
{
    Task<ControlSendResult> SendAsync(CommWebhookTarget target, ControlDelivery delivery, CancellationToken ct);
}

/// <summary>Registered when nothing else is: refuses to send. Keeps the host bootable in tests that never touch control calls.</summary>
public sealed class NoControlWebhookSender : IControlWebhookSender
{
    public Task<ControlSendResult> SendAsync(CommWebhookTarget target, ControlDelivery delivery, CancellationToken ct) =>
        Task.FromResult(new ControlSendResult(false, null, target.Host, "no control webhook sender is configured"));
}

/// <summary>
/// The HMAC key a control webhook is signed with, one per destination and workspace (CLAUDE.md rule 11: protected with Data Protection, kept under the credentials directory,
/// never in SQLite or configuration, never returned after it is created). The receiver is given the key once, when it is created or rotated.
/// </summary>
public interface IControlSigningSecrets
{
    Task<bool> ExistsAsync(string destinationId, CancellationToken ct = default);
    /// <summary>Null when none is set. Throws when the stored value cannot be unprotected (the key ring changed): rotate it.</summary>
    Task<string?> GetAsync(string destinationId, CancellationToken ct = default);
    /// <summary>Creates a new key (replacing any earlier one) and returns it. The only time it is ever shown.</summary>
    Task<string> RotateAsync(string destinationId, CancellationToken ct = default);
    Task<bool> ClearAsync(string destinationId, CancellationToken ct = default);
}
