using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;

namespace ReleaseMgmt.Infrastructure.Email;

/// <summary>
/// REOS-167 (D61): the kinds of email, one template each. A notification becomes mail only when its kind maps to one of the four user-facing categories; everything else stays in the inbox (and in the daily digest only
/// if the person chose the digest for a category). <c>DailyDigest</c> and <c>Test</c> are templates, not choices a person makes.
/// </summary>
public static class EmailCategories
{
    public const string ApprovalRequested = "ApprovalRequested", DecisionMade = "DecisionMade", EscalationLevel1 = "EscalationLevel1", EscalationLevel2 = "EscalationLevel2", DailyDigest = "DailyDigest", Test = "Test";

    /// <summary>The four kinds a person can set a preference for, in the order screens list them.</summary>
    public static readonly IReadOnlyList<string> Choosable = [ApprovalRequested, DecisionMade, EscalationLevel1, EscalationLevel2];

    public static readonly IReadOnlyList<string> Channels = ["Email", "Webhook"];
    public static readonly IReadOnlyList<string> Modes = ["Off", "Immediate", "Daily"];

    /// <summary>Email is immediate unless the person chose otherwise; the team webhook is off.</summary>
    public static string DefaultMode(string channel) => channel == "Email" ? "Immediate" : "Off";

    /// <summary>The category of an in-app notification, or null when it is not one that is mailed. A decision kind wins over its escalation level (an override declined is a decision).</summary>
    public static string? Of(string kind, int escalationLevel)
    {
        if (kind == "ApprovalRequested") return ApprovalRequested;
        if (kind.StartsWith("Approval", StringComparison.Ordinal) || kind == "FreezeOverrideDeclined") return DecisionMade;
        return escalationLevel switch { >= 2 => EscalationLevel2, 1 => EscalationLevel1, _ => null };
    }

    public static string Label(string category) => category switch
    {
        ApprovalRequested => "Approval requested",
        DecisionMade => "Decision made",
        EscalationLevel1 => "Escalation, level 1",
        EscalationLevel2 => "Escalation, level 2",
        DailyDigest => "Daily digest",
        _ => "Test email",
    };

    public static string ModeLabel(string mode) => mode switch { "Immediate" => "Immediately", "Daily" => "In the daily digest", _ => "Off" };
}

/// <summary>What the provider is given: the whole message, with the sender already decided. Never logged.</summary>
public sealed record EmailEnvelope(string To, string FromAddress, string? FromName, string Subject, string Text, string Html, string MessageKey);

/// <summary>
/// How an attempt ended. <see cref="Rejected"/> means the provider says this recipient can never be reached (a hard bounce): the address is suppressed. <see cref="Failed"/> is a permanent refusal that is not about the
/// recipient (a malformed message): no retry, no suppression. <see cref="Temporary"/> is everything else that can pass, including a missing or refused credential: retried with backoff, then dead-lettered and alerted.
/// </summary>
public enum EmailOutcome { Sent, Temporary, Rejected, Failed }

public sealed record EmailSendResult(EmailOutcome Outcome, string? ProviderMessageId = null, string? Reason = null)
{
    public static EmailSendResult Sent(string? id = null) => new(EmailOutcome.Sent, id);
}

/// <summary>One attempt, never retried here (the processor owns retries), never throws for a delivery problem. Implementations: SMTP, a cloud API, and a fake for tests.</summary>
public interface IEmailProvider
{
    string Name { get; }
    Task<EmailSendResult> SendAsync(EmailEnvelope message, CancellationToken ct);
}

/// <summary>Stands in while email is off: nothing calls it (no row is ever queued), and a call says so.</summary>
public sealed class DisabledEmailProvider : IEmailProvider
{
    public string Name => "disabled";
    public Task<EmailSendResult> SendAsync(EmailEnvelope message, CancellationToken ct) => Task.FromResult(new EmailSendResult(EmailOutcome.Temporary, null, "Email delivery is off (Notifications:Smtp:Enabled)"));
}

/// <summary>
/// The test double: records what it was given and answers by recipient, so a test can make an address bounce, fail for a while or succeed. No network, no mail.
/// </summary>
public sealed class FakeEmailProvider : IEmailProvider
{
    private readonly object _gate = new();
    private readonly List<EmailEnvelope> _sent = [];
    private readonly List<string> _attempts = [];
    public string Name => "fake";
    /// <summary>Addresses that answer with a hard bounce.</summary>
    public HashSet<string> Rejected { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Addresses whose next N attempts fail temporarily.</summary>
    public Dictionary<string, int> FailTimes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool FailEverything { get; set; }
    public IReadOnlyList<EmailEnvelope> Sent { get { lock (_gate) return [.. _sent]; } }
    public IReadOnlyList<string> Attempts { get { lock (_gate) return [.. _attempts]; } }

    public Task<EmailSendResult> SendAsync(EmailEnvelope message, CancellationToken ct)
    {
        lock (_gate)
        {
            _attempts.Add(message.To);
            if (Rejected.Contains(message.To)) return Task.FromResult(new EmailSendResult(EmailOutcome.Rejected, null, "550 5.1.1 No such user"));
            if (FailEverything) return Task.FromResult(new EmailSendResult(EmailOutcome.Temporary, null, "421 Service not available"));
            if (FailTimes.TryGetValue(message.To, out var n) && n > 0) { FailTimes[message.To] = n - 1; return Task.FromResult(new EmailSendResult(EmailOutcome.Temporary, null, "451 Try again later")); }
            _sent.Add(message);
            return Task.FromResult(EmailSendResult.Sent("fake-" + _sent.Count));
        }
    }
}

/// <summary>
/// The provider's password or API key, and the secret that signs inbound bounce events (CLAUDE.md rule 11, D12): ASP.NET Data Protection, one file per secret under <c>Notifications:Email:CredentialsDirectory</c>;
/// never in SQLite, appsettings or a log, and never returned by an endpoint (the events secret is shown once, when it is made). If the key ring changed the secret reads as absent and the status says so.
/// </summary>
public interface IEmailCredentials
{
    public const string Provider = "provider", Events = "events";
    Task<bool> HasAsync(string name, CancellationToken ct = default);
    Task<string?> GetAsync(string name, CancellationToken ct = default);
    Task SetAsync(string name, string value, CancellationToken ct = default);
    Task<bool> ClearAsync(string name, CancellationToken ct = default);
}

public sealed class DataProtectionEmailCredentials(IDataProtectionProvider provider, EmailOptions options) : IEmailCredentials
{
    public const string Purpose = "ReleaseMgmt.Email.Credentials.v1";
    private readonly IDataProtectionProvider _provider = provider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string PathFor(string name)
    {
        if (name is not (IEmailCredentials.Provider or IEmailCredentials.Events)) throw new ArgumentException("Not an email credential", nameof(name));
        return Path.Combine(options.CredentialsDirectory, name + ".secret");
    }

    private IDataProtector ProtectorFor(string name) => _provider.CreateProtector(Purpose).CreateProtector(name);   // a file copied to another name cannot be read as it

    public async Task<bool> HasAsync(string name, CancellationToken ct = default) => await GetAsync(name, ct) is not null;

    public async Task<string?> GetAsync(string name, CancellationToken ct = default)
    {
        var path = PathFor(name);
        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(path)) return null;
            var blob = await File.ReadAllTextAsync(path, ct);
            try { return ProtectorFor(name).Unprotect(blob); }
            catch (CryptographicException) { return null; }
        }
        finally { _gate.Release(); }
    }

    public async Task SetAsync(string name, string value, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("The value is empty", nameof(value));
        var path = PathFor(name);
        var blob = ProtectorFor(name).Protect(value.Trim());
        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(options.CredentialsDirectory);
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

    public async Task<bool> ClearAsync(string name, CancellationToken ct = default)
    {
        var path = PathFor(name);
        await _gate.WaitAsync(ct);
        try { var had = File.Exists(path); if (had) File.Delete(path); return had; }
        finally { _gate.Release(); }
    }
}

/// <summary>
/// REOS-167 (D61): configuration. <c>Notifications:Smtp:Enabled</c> is the switch and is <b>off by default</b>: with it off nothing is queued, nothing is sent and nothing is reported. Everything else is
/// <c>Notifications:Email:*</c>: <c>Provider</c> (<c>Smtp</c> or <c>SendGrid</c>), <c>From:Address</c>, <c>From:Name</c>, <c>Smtp:Host</c>/<c>Port</c>/<c>Security</c> (<c>StartTls</c> or <c>None</c>)/<c>UserName</c>,
/// <c>ApiBase</c> (SendGrid), <c>MaxAttempts</c> 6, <c>BackoffBaseSeconds</c> 60 doubling to <c>BackoffCapSeconds</c> 21600, <c>PollSeconds</c> 5, <c>Batch</c> 20, <c>RetentionDays</c> 90 (finished messages),
/// <c>DigestHourUtc</c> 7, <c>Worker:Enabled</c> true, <c>CredentialsDirectory</c> data/email. A secret is never here: the password or key is stored through the API (Data Protection).
/// An enabled configuration that cannot work stops the start (a bad sender, an unknown provider, no SMTP host, a password over a plain connection).
/// </summary>
public sealed record EmailOptions(bool Enabled, string Provider, string FromAddress, string? FromName, string? SmtpHost, int SmtpPort, string SmtpSecurity, string? SmtpUserName, string? ApiBase,
    int MaxAttempts, TimeSpan BackoffBase, TimeSpan BackoffCap, TimeSpan Poll, int Batch, int RetentionDays, int DigestHourUtc, bool WorkerEnabled, string CredentialsDirectory)
{
    public const string EnabledKey = "Notifications:Smtp:Enabled";
    public static readonly string[] Providers = ["Smtp", "SendGrid"];

    public static EmailOptions From(IConfiguration c)
    {
        static double D(IConfiguration c, string key, double def, double min, double max) =>
            Math.Clamp(double.TryParse(c[key], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : def, min, max);
        var enabled = string.Equals(c[EnabledKey], "true", StringComparison.OrdinalIgnoreCase);
        var o = new EmailOptions(enabled, (c["Notifications:Email:Provider"] ?? "Smtp").Trim(), (c["Notifications:Email:From:Address"] ?? "").Trim(), c["Notifications:Email:From:Name"]?.Trim() is { Length: > 0 } n ? n : "ReleaseOS",
            c["Notifications:Email:Smtp:Host"]?.Trim(), (int)D(c, "Notifications:Email:Smtp:Port", 587, 1, 65535), (c["Notifications:Email:Smtp:Security"] ?? "StartTls").Trim(), c["Notifications:Email:Smtp:UserName"]?.Trim() is { Length: > 0 } u ? u : null,
            c["Notifications:Email:ApiBase"]?.Trim() is { Length: > 0 } a ? a : null,
            (int)D(c, "Notifications:Email:MaxAttempts", 6, 1, 30), TimeSpan.FromSeconds(D(c, "Notifications:Email:BackoffBaseSeconds", 60, 0, 3_600)), TimeSpan.FromSeconds(D(c, "Notifications:Email:BackoffCapSeconds", 21_600, 0, 86_400)),
            TimeSpan.FromSeconds(D(c, "Notifications:Email:PollSeconds", 5, 0.05, 3_600)), (int)D(c, "Notifications:Email:Batch", 20, 1, 500), (int)D(c, "Notifications:Email:RetentionDays", 90, 1, 3_650),
            (int)D(c, "Notifications:Email:DigestHourUtc", 7, 0, 23), !string.Equals(c["Notifications:Email:Worker:Enabled"], "false", StringComparison.OrdinalIgnoreCase),
            c["Notifications:Email:CredentialsDirectory"] is { Length: > 0 } d ? d : "data/email");
        if (enabled) o.Validate();
        return o;
    }

    private void Validate()
    {
        if (!MailAddress.TryCreate(FromAddress, out var from) || from.Address != FromAddress) throw new InvalidOperationException("Notifications:Email:From:Address must be one plain email address such as releaseos@example.com (email is enabled)");
        if (!Providers.Contains(Provider)) throw new InvalidOperationException($"Notifications:Email:Provider '{Provider}' is not one of {string.Join(", ", Providers)}");
        if (Provider == "Smtp")
        {
            if (string.IsNullOrWhiteSpace(SmtpHost)) throw new InvalidOperationException("Notifications:Email:Smtp:Host is required while email is enabled with the Smtp provider");
            if (SmtpSecurity is not ("StartTls" or "None")) throw new InvalidOperationException("Notifications:Email:Smtp:Security must be StartTls or None (implicit TLS on port 465 is not supported: use 587 with StartTls)");
            if (SmtpSecurity == "None" && SmtpUserName is not null && !IsLoopback(SmtpHost!))
                throw new InvalidOperationException("Notifications:Email:Smtp:Security None would send the password in clear: use StartTls (None with a user name is accepted only for a local relay)");
        }
        if (Provider == "SendGrid" && ApiBase is not null && (!Uri.TryCreate(ApiBase, UriKind.Absolute, out var b) || b.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("Notifications:Email:ApiBase must be an https address");
    }

    private static bool IsLoopback(string host) => host is "localhost" || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));

    /// <summary>Delay before attempt <paramref name="attemptsMade"/> + 1: the base doubled per attempt, capped.</summary>
    public TimeSpan Delay(int attemptsMade)
    {
        var secs = BackoffBase.TotalSeconds * Math.Pow(2, Math.Max(0, attemptsMade - 1));
        return TimeSpan.FromSeconds(Math.Min(secs, BackoffCap.TotalSeconds));
    }
}

/// <summary>Wakes the sender after a commit that queued mail, so a person does not wait for the next poll. Same idea as the webhook signal.</summary>
public static class EmailSignal
{
    private static readonly SemaphoreSlim Gate = new(0, 1);
    public static void Notify() { try { Gate.Release(); } catch (SemaphoreFullException) { /* one is already pending */ } }
    public static Task<bool> WaitAsync(TimeSpan interval, CancellationToken ct) => Gate.WaitAsync(interval, ct);
}
