using Microsoft.Extensions.DependencyInjection.Extensions;
using ReleaseMgmt.Infrastructure.Exports;
using ReleaseMgmt.Infrastructure.Reminders;

namespace ReleaseMgmt.Api.Exports;

public static class PdfExportRegistration
{
    /// <summary>
    /// REOS-50: PDF exports as ExportJobs (release report, run sheet, evidence pack + ZIP, scorecard). Registers the options, the job service, the metric snapshot writer,
    /// the model loader and the hosted <see cref="ExportWorker"/>. Needs TimeProvider, IAlertSink, the DbContext factory, AuditQueryService, AnalyticsService and
    /// AttachmentService registered first. Config: Exports:Directory (default: "exports" beside the database, outside wwwroot), Exports:PollSeconds (5),
    /// Exports:StaleRunningSeconds (300), Exports:MaxAttempts (2), Exports:AuditRowLimit (5000), Pdf:QuestPdfLicense (Community | Professional | Enterprise; no default, OI-2),
    /// Pdf:PdfA (false, OI-8), Display:TimeZone.
    /// </summary>
    public static IServiceCollection AddPdfExports(this IServiceCollection services, IConfiguration config, bool isDevelopment)
    {
        var dbDir = Path.GetDirectoryName(Path.GetFullPath(config["Db:Path"] ?? "data/releasemgmt.db"))!;
        var options = ExportOptions.From(config, dbDir, isDevelopment);
        services.AddSingleton(options);
        services.TryAddSingleton<SyncAlertWriter>();
        services.TryAddSingleton(sp => DisplayClock.From(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton<MetricSnapshotService>();
        services.AddSingleton<ExportService>();
        services.AddSingleton<ExportModelLoader>();
        services.AddSingleton<ExportWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<ExportWorker>());
        return services;
    }
}
