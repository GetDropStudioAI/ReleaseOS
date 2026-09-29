using ReleaseMgmt.Domain.Common;

namespace ReleaseMgmt.Api;

/// <summary>M0 stand-in: logs at Error. Replaced by the SyncAlerts writer in M1 (REOS-16), where the table lands.</summary>
public sealed class LoggingAlertSink(ILogger<LoggingAlertSink> log) : IAlertSink
{
    public Task RaiseAsync(string sourceSystem, string kind, string key, string message, CancellationToken ct = default)
    {
        log.LogError("ALERT {Source}/{Kind} [{Key}]: {Message}", sourceSystem, kind, key, message);
        return Task.CompletedTask;
    }
}
