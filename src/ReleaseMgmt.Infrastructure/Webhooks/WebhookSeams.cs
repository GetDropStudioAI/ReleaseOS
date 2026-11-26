using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Sync;

namespace ReleaseMgmt.Infrastructure.Webhooks;

/// <summary>Where one delivery goes. The address stays protected until the HTTP call (Q-053e); <see cref="Host"/> is the only part of it a message may show.</summary>
public sealed record WebhookTarget(string SubscriptionId, string Name, string Host, string ProtectedUrl);

/// <summary>One signed request: the exact body, the delivery id (stable across retries), this attempt's timestamp, the event type, the secret version and the signature header value.</summary>
public sealed record WebhookRequest(string DeliveryId, long UnixSeconds, string EventType, int SecretVersion, string Body, string Signature);

/// <summary>Outcome of one attempt. <see cref="Reason"/> never holds the address; <see cref="HttpStatus"/> is null when there was no answer.</summary>
public sealed record WebhookSendResult(bool Delivered, int? HttpStatus, string? Reason);

/// <summary>
/// Posts one signed request. The implementation applies every protection of the other outbound webhooks (https only, no credentials in the URL, no redirects, private and local addresses refused before sending and again
/// when the socket connects, a hard timeout, the response body never read) and makes exactly one attempt: the processor owns retries. Never throws for delivery problems.
/// </summary>
public interface IWebhookSender
{
    Task<WebhookSendResult> SendAsync(WebhookTarget target, WebhookRequest request, CancellationToken ct);
}

/// <summary>The address rule the senders use, from the Api's one <c>OutboundAddressPolicy</c> (the services here do not reference the Api).</summary>
public interface IOutboundAddressGuard
{
    bool IsBlocked(IPAddress address);
}

/// <summary>
/// The HMAC signing secret of a subscription (CLAUDE.md rule 11): one file per subscription under <c>{Sync:Credentials:Directory}/outbound-webhooks/{workspace}/</c>, protected with ASP.NET Data Protection and a purpose that names
/// the workspace and the subscription, so a file copied elsewhere cannot be read there. It is never in SQLite, never in configuration, never logged, and only the answer to create or rotate ever shows it.
/// </summary>
public interface IWebhookSigningSecrets
{
    Task<bool> ExistsAsync(string subscriptionId, CancellationToken ct = default);
    /// <summary>Null when none is set. Throws <see cref="InvalidOperationException"/> when the stored value cannot be unprotected (the key ring changed): rotate it.</summary>
    Task<string?> GetAsync(string subscriptionId, CancellationToken ct = default);
    /// <summary>Makes a new secret (replacing any earlier one) and returns it. The only time it is shown.</summary>
    Task<string> RotateAsync(string subscriptionId, CancellationToken ct = default);
    /// <summary>Puts a secret back (undoing a rotation whose transaction failed).</summary>
    Task SetAsync(string subscriptionId, string secret, CancellationToken ct = default);
    Task<bool> ClearAsync(string subscriptionId, CancellationToken ct = default);
}

public sealed class DataProtectionWebhookSecrets(IDataProtectionProvider provider, SyncOptions options) : IWebhookSigningSecrets
{
    public const string Purpose = "ReleaseMgmt.OutboundWebhook.Signing.v1";
    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static string Workspace => WorkspaceScope.Current ?? WorkspaceIds.Default;

    private string PathFor(string id)
    {
        if (id.Length is 0 or > 64 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) throw new ArgumentException("Not a subscription id", nameof(id));
        if (!Workspace.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) throw new InvalidOperationException("The workspace id cannot name a folder");
        return Path.Combine(options.CredentialsDirectory, "outbound-webhooks", Workspace, id + ".secret");
    }

    private IDataProtector ProtectorFor(string id) => _protector.CreateProtector("workspace:" + Workspace).CreateProtector("subscription:" + id);

    public Task<bool> ExistsAsync(string subscriptionId, CancellationToken ct = default) => Task.FromResult(File.Exists(PathFor(subscriptionId)));

    public async Task<string?> GetAsync(string subscriptionId, CancellationToken ct = default)
    {
        var path = PathFor(subscriptionId);
        string blob;
        await _gate.WaitAsync(ct);
        try { if (!File.Exists(path)) return null; blob = await File.ReadAllTextAsync(path, ct); }
        finally { _gate.Release(); }
        try { return ProtectorFor(subscriptionId).Unprotect(blob); }
        catch (CryptographicException) { throw new InvalidOperationException("The signing secret cannot be read (the Data Protection key ring changed); rotate the secret"); }
    }

    public async Task<string> RotateAsync(string subscriptionId, CancellationToken ct = default)
    {
        var secret = WebhookSignature.NewSecret();
        await SetAsync(subscriptionId, secret, ct);
        return secret;
    }

    public async Task SetAsync(string subscriptionId, string secret, CancellationToken ct = default)
    {
        var path = PathFor(subscriptionId);
        var blob = ProtectorFor(subscriptionId).Protect(secret);
        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            await File.WriteAllTextAsync(tmp, blob, ct);
            File.Move(tmp, path, overwrite: true);
            if (!OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { /* best effort: the content is encrypted anyway */ }
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> ClearAsync(string subscriptionId, CancellationToken ct = default)
    {
        var path = PathFor(subscriptionId);
        await _gate.WaitAsync(ct);
        try { var had = File.Exists(path); if (had) File.Delete(path); return had; }
        finally { _gate.Release(); }
    }
}
