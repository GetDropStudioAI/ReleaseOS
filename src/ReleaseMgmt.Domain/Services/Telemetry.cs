using System.Security.Cryptography;
using System.Text;

namespace ReleaseMgmt.Domain.Services;

/// <summary>Guard names of telemetry, deployment control and run pause (REOS-111, 112). Kept apart from <see cref="Guards"/> so parallel streams do not collide.</summary>
public static class DeploymentGuards
{
    public const string MetricInvalid = "MetricInvalid";
    public const string ControlInvalid = "ControlInvalid";
    /// <summary>A control action is a confirmed one (D41 dialogs): the request must say <c>confirm: true</c>.</summary>
    public const string ConfirmationRequired = "ConfirmationRequired";
    public const string ControlRole = "ControlRole";
    public const string NoControlWebhook = "NoControlWebhook";
    public const string NoSigningSecret = "NoSigningSecret";
    public const string IllegalControlAction = "IllegalControlAction";
    public const string ControlInFlight = "ControlInFlight";
    /// <summary>The call to the environment's webhook did not succeed. Returned as 502 with the recorded action; also a Deployment/ControlFailed alert.</summary>
    public const string ControlFailed = "ControlFailed";
    public const string RunPaused = "RunPaused";
    public const string RunAlreadyPaused = "RunAlreadyPaused";
    public const string RunNotPaused = "RunNotPaused";
    public const string PauseInvalid = "PauseInvalid";
}

public static class MetricSeries
{
    public const string Canary = "Canary", Stable = "Stable";
    public static readonly string[] All = [Canary, Stable];
}

public static class MetricNames
{
    public const string ErrorRate = "ErrorRate", LatencyP95 = "LatencyP95", Availability = "Availability";
    public static readonly string[] All = [ErrorRate, LatencyP95, Availability];
}

/// <summary>An environment's thresholds (REOS-109) with the duration a metric must stay beyond one before it is a breach (REOS-111).</summary>
public sealed record Thresholds(double? ErrorRateMaxPct, double? LatencyP95MaxMs, double? AvailabilityMinPct, int ForSeconds)
{
    public double? LimitOf(string metric) => metric switch
    {
        MetricNames.ErrorRate => ErrorRateMaxPct,
        MetricNames.LatencyP95 => LatencyP95MaxMs,
        MetricNames.Availability => AvailabilityMinPct,
        _ => null,
    };
}

public static class ThresholdRules
{
    /// <summary>Error rate and latency breach above their limit; availability below its minimum. A value exactly at the limit is inside it.</summary>
    public static bool IsBeyond(string metric, double value, double limit) => metric == MetricNames.Availability ? value < limit : value > limit;

    /// <summary>The worse of two values for a metric: the higher error rate or latency, the lower availability.</summary>
    public static double Worse(string metric, double a, double b) => metric == MetricNames.Availability ? Math.Min(a, b) : Math.Max(a, b);

    public static double? ValueOf(string metric, double? errorRatePct, double? latencyP95Ms, double? availabilityPct) => metric switch
    {
        MetricNames.ErrorRate => errorRatePct,
        MetricNames.LatencyP95 => latencyP95Ms,
        MetricNames.Availability => availabilityPct,
        _ => null,
    };
}

/// <summary>Availability against an SLO target and the error budget it leaves (REOS-111).</summary>
public sealed record ErrorBudget(double ObjectivePct, double MeasuredPct, bool Meets, double BudgetPct, double ConsumedPct, double RemainingPct, bool Exhausted);

public static class SloMath
{
    /// <summary>
    /// The budget is what the objective allows to fail: 100 - objective (99.9 % leaves 0.1 %). Consumed is what actually failed (100 - measured, never below 0).
    /// Remaining is the share of the budget still unspent, as a percentage (it goes negative once the budget is overspent). An objective of 100 % has no budget:
    /// any failure exhausts it.
    /// </summary>
    public static ErrorBudget Budget(double objectivePct, double measuredPct)
    {
        var budget = Math.Max(0, 100 - objectivePct);
        var consumed = Math.Max(0, 100 - measuredPct);
        var remaining = budget > 0 ? (budget - consumed) / budget * 100 : consumed == 0 ? 100 : 0;
        return new(objectivePct, measuredPct, measuredPct >= objectivePct, budget, consumed, remaining, consumed > budget);
    }
}

public static class CanaryMath
{
    private const double Epsilon = 1e-9;

    /// <summary>The 1-based step the rollout is on: how many steps its traffic has reached (0 before the first).</summary>
    public static int CurrentStep(IReadOnlyList<double> steps, double trafficPercent) => steps.Count(s => s <= trafficPercent + Epsilon);

    /// <summary>The traffic of the step after the current one, or null on the last.</summary>
    public static double? NextTraffic(IReadOnlyList<double> steps, double trafficPercent)
    {
        var n = CurrentStep(steps, trafficPercent);
        return n < steps.Count ? steps[n] : null;
    }
}

/// <summary>Request shape for a control call's signature, shared by the sender and the documentation: <c>v1=hex(HMAC-SHA256(secret, "{timestamp}.{deliveryId}.{body}"))</c>.</summary>
public static class ControlSignature
{
    public const string Version = "v1";
    public const string DeliveryHeader = "X-ReleaseOS-Delivery", TimestampHeader = "X-ReleaseOS-Timestamp", SignatureHeader = "X-ReleaseOS-Signature";
    /// <summary>A receiver should refuse a delivery older than this many seconds, and one whose delivery id it has seen.</summary>
    public const int ToleranceSeconds = 300;

    public static string Sign(string secret, long unixSeconds, string deliveryId, string body)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{unixSeconds}.{deliveryId}.{body}"));
        return $"{Version}={Convert.ToHexStringLower(mac)}";
    }
}
