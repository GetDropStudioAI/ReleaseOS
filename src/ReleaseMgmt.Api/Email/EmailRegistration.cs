using Microsoft.Extensions.DependencyInjection.Extensions;
using ReleaseMgmt.Infrastructure.Email;

namespace ReleaseMgmt.Api.Email;

public static class EmailRegistration
{
    /// <summary>
    /// REOS-167 (D61): email delivery: the options (a bad enabled configuration stops the start), the provider (SMTP or SendGrid; a test replaces <see cref="IEmailProvider"/> with the fake), the credential store (Data Protection),
    /// the queue the notifier uses, the preference, suppression, admin and event services, the delivery processor and its worker. Needs the DbContext factory, TimeProvider, Data Protection, <c>PublicLinks</c>, <c>DisplayClock</c> and the alert
    /// writer registered first. With <c>Notifications:Smtp:Enabled</c> off (the default) the worker does nothing and nothing is queued.
    /// </summary>
    public static IServiceCollection AddEmailDelivery(this IServiceCollection services, IConfiguration config)
    {
        var options = EmailOptions.From(config);
        services.AddSingleton(options);
        services.TryAddSingleton<IEmailCredentials, DataProtectionEmailCredentials>();
        services.AddHttpClient(SendGridEmailProvider.ClientName, c => c.Timeout = TimeSpan.FromSeconds(20)).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });
        services.TryAddSingleton<IEmailProvider>(sp => !options.Enabled ? new DisabledEmailProvider()
            : options.Provider == "SendGrid" ? ActivatorUtilities.CreateInstance<SendGridEmailProvider>(sp)
            : ActivatorUtilities.CreateInstance<SmtpEmailProvider>(sp));
        services.AddSingleton<IEmailProblemSink, EmailProblemSink>();
        services.AddSingleton<EmailQueue>();
        services.AddSingleton<EmailPreferenceService>();
        services.AddSingleton<EmailSuppressionService>();
        services.AddSingleton<EmailAdminService>();
        services.AddSingleton<EmailEventService>();
        services.AddSingleton<EmailDeliveryProcessor>();
        services.AddSingleton<EmailDigestService>();
        services.AddHostedService<EmailDeliveryWorker>();
        return services;
    }
}
