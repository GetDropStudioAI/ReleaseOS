using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReleaseMgmt.Domain.Common;

namespace ReleaseMgmt.Infrastructure.Backup;

public sealed record BackupOptions(string ConnectionString, string Directory, TimeSpan Interval)
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(15);
}

/// <summary>Backup every 15 min; the first backup of each UTC day is also written as a nightly (kept 30 days; the rest 2 days). Failures raise an alert (D30).</summary>
public sealed class BackupService(BackupOptions options, TimeProvider time, IAlertSink alerts, ILogger<BackupService> log)
    : BackgroundService
{
    public DateTime? LastSuccessUtc { get; private set; }

    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        try
        {
            var file = BackupRunner.Backup(options.ConnectionString, options.Directory, now);
            var nightlyToday = System.IO.Directory.Exists(options.Directory) && System.IO.Directory.EnumerateFiles(options.Directory,
                $"{BackupRunner.NightlyPrefix}{now:yyyyMMdd}T*{BackupRunner.Extension}").Any();
            if (!nightlyToday) BackupRunner.Backup(options.ConnectionString, options.Directory, now, nightly: true);
            BackupRunner.Prune(options.Directory, now);
            LastSuccessUtc = now;
            log.LogInformation("Backup written to {File}", file);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Backup failed");
            await alerts.RaiseAsync("Backup", "BackupFailed", "backup", ex.Message, ct);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Interval, time);
        await RunOnceAsync(stoppingToken);
        while (await timer.WaitForNextTickAsync(stoppingToken)) await RunOnceAsync(stoppingToken);
    }
}
