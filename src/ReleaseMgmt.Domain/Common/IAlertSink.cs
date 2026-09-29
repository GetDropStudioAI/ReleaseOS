namespace ReleaseMgmt.Domain.Common;

/// <summary>Every background failure ends up here (D30). Persisted to SyncAlerts once the schema lands in M1.</summary>
public interface IAlertSink
{
    Task RaiseAsync(string sourceSystem, string kind, string key, string message, CancellationToken ct = default);
}
