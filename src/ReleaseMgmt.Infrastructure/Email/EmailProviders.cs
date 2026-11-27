using System.Net;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ReleaseMgmt.Infrastructure.Email;

/// <summary>
/// SMTP through the framework's own client (no extra dependency, Q-167a): STARTTLS or none, a user name with the password from <see cref="IEmailCredentials"/>. A 5xx answer to the recipient is a hard bounce
/// (<see cref="EmailOutcome.Rejected"/>); any other 5xx is a permanent failure that is not about the person; a 4xx, a timeout, a refused connection or a missing password is temporary. The address, subject and body never go to a log.
/// </summary>
public sealed class SmtpEmailProvider(EmailOptions options, IEmailCredentials credentials, ILogger<SmtpEmailProvider> log) : IEmailProvider
{
    public string Name => "smtp";

    public async Task<EmailSendResult> SendAsync(EmailEnvelope m, CancellationToken ct)
    {
        string? password = null;
        if (options.SmtpUserName is not null)
        {
            password = await credentials.GetAsync(IEmailCredentials.Provider, ct);
            if (password is null) return new(EmailOutcome.Temporary, null, "No SMTP password is stored (set it under Email delivery)");
        }
        using var msg = new MailMessage { From = new MailAddress(m.FromAddress, m.FromName), Subject = m.Subject, SubjectEncoding = Encoding.UTF8, BodyEncoding = Encoding.UTF8 };
        msg.To.Add(new MailAddress(m.To));
        msg.Headers.Add("X-ReleaseOS-Message", m.MessageKey);
        msg.Headers.Add("Auto-Submitted", "auto-generated");
        msg.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(m.Text, Encoding.UTF8, MediaTypeNames.Text.Plain));
        msg.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(m.Html, Encoding.UTF8, MediaTypeNames.Text.Html));
        using var client = new SmtpClient(options.SmtpHost!, options.SmtpPort)
        {
            EnableSsl = options.SmtpSecurity == "StartTls",
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Timeout = 20_000,
            Credentials = options.SmtpUserName is null ? null : new NetworkCredential(options.SmtpUserName, password),
        };
        try
        {
            await client.SendMailAsync(msg, ct);
            return EmailSendResult.Sent(m.MessageKey);
        }
        catch (SmtpFailedRecipientException ex) when ((int)ex.StatusCode >= 500)
        {
            return new(EmailOutcome.Rejected, null, Short($"{(int)ex.StatusCode} {ex.StatusCode}: the recipient was refused"));
        }
        catch (SmtpException ex)
        {
            var code = (int)ex.StatusCode;
            // 5xx other than a recipient refusal (a bad sender, a refused login, a message rejected as spam) is not the person's address: do not suppress it. An authentication failure is configuration: temporary, so it is not lost.
            var auth = ex.StatusCode is SmtpStatusCode.ClientNotPermitted or SmtpStatusCode.MustIssueStartTlsFirst || ex.Message.Contains("Authentication", StringComparison.OrdinalIgnoreCase);
            log.LogWarning("SMTP attempt for message {Message} failed with {Status}", m.MessageKey, ex.StatusCode);
            return new(code >= 500 && !auth ? EmailOutcome.Failed : EmailOutcome.Temporary, null, Short($"{code} {ex.StatusCode}"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or System.Net.Sockets.SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning("SMTP attempt for message {Message} did not reach the server: {Type}", m.MessageKey, ex.GetType().Name);
            return new(EmailOutcome.Temporary, null, Short(ex.GetType().Name + ": " + ex.Message));
        }
    }

    private static string Short(string s) => s.Length > 300 ? s[..300] : s;
}

/// <summary>
/// A cloud provider over HTTPS (SendGrid v3 <c>mail/send</c>): the key from <see cref="IEmailCredentials"/> as a bearer token. 202 is accepted (the <c>X-Message-Id</c> header is the id a later bounce quotes); 429 and 5xx are
/// temporary; 401 and 403 are a bad key, which is configuration, so temporary; any other 4xx means the message itself was refused (<see cref="EmailOutcome.Failed"/>), never a suppression.
/// </summary>
public sealed class SendGridEmailProvider(IHttpClientFactory http, EmailOptions options, IEmailCredentials credentials) : IEmailProvider
{
    public const string ClientName = "email-sendgrid";
    public string Name => "sendgrid";

    public async Task<EmailSendResult> SendAsync(EmailEnvelope m, CancellationToken ct)
    {
        var key = await credentials.GetAsync(IEmailCredentials.Provider, ct);
        if (key is null) return new(EmailOutcome.Temporary, null, "No API key is stored (set it under Email delivery)");
        var body = JsonSerializer.Serialize(new
        {
            personalizations = new[] { new { to = new[] { new { email = m.To } } } },
            from = new { email = m.FromAddress, name = m.FromName },
            subject = m.Subject,
            content = new[] { new { type = "text/plain", value = m.Text }, new { type = "text/html", value = m.Html } },
            custom_args = new { releaseos_message = m.MessageKey },
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, (options.ApiBase ?? "https://api.sendgrid.com").TrimEnd('/') + "/v3/mail/send") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        try
        {
            using var res = await http.CreateClient(ClientName).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var status = (int)res.StatusCode;
            if (status is 200 or 201 or 202) return EmailSendResult.Sent(res.Headers.TryGetValues("X-Message-Id", out var ids) ? ids.FirstOrDefault() ?? m.MessageKey : m.MessageKey);
            if (status is 401 or 403) return new(EmailOutcome.Temporary, null, $"{status}: the provider refused the API key");
            if (status == 429 || status >= 500) return new(EmailOutcome.Temporary, null, $"{status}: the provider is busy or down");
            return new(EmailOutcome.Failed, null, $"{status}: the provider refused the message");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new(EmailOutcome.Temporary, null, ex.GetType().Name + ": the provider could not be reached");
        }
    }
}
