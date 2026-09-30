namespace ReleaseMgmt.Domain.Sync;

/// <summary>What went wrong talking to an ITSM system (PROJECT_SCOPE 5.3.3). Pure classification, no I/O.</summary>
public enum ConnectorErrorKind { Auth, NotFound, Timeout, RateLimited, Server, Network, Parse }

/// <summary>A connector failure with its class. The message is safe to store and show: it never carries a URL, a response body or a credential.</summary>
public sealed class ConnectorException(ConnectorErrorKind kind, string message, int? httpStatus = null, TimeSpan? retryAfter = null, Exception? inner = null)
    : Exception(message, inner)
{
    public ConnectorErrorKind Kind { get; } = kind;
    public int? HttpStatus { get; } = httpStatus;
    /// <summary>The server's Retry-After on a 429, when it sent one.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>What the poller does with a classified error: the SyncAlerts kind, whether it is connector-wide, and the link state it implies.</summary>
public static class ConnectorErrorClassifier
{
    /// <summary>HTTP status -> class. 2xx is not an error (null). Redirects are not followed, so a 3xx counts as a server-side problem.</summary>
    public static ConnectorErrorKind? FromHttpStatus(int status) => status switch
    {
        >= 200 and < 300 => null,
        401 or 403 => ConnectorErrorKind.Auth,
        404 or 410 => ConnectorErrorKind.NotFound,
        408 or 504 => ConnectorErrorKind.Timeout,
        429 => ConnectorErrorKind.RateLimited,
        _ => ConnectorErrorKind.Server,   // 3xx, other 4xx, 5xx
    };

    /// <summary>The SyncAlerts.Kind the schema allows: AuthFailed, NotFound, RateLimited, ParseError; everything that means "cannot reach it" is Unreachable.</summary>
    public static string AlertKind(ConnectorErrorKind k) => k switch
    {
        ConnectorErrorKind.Auth => "AuthFailed",
        ConnectorErrorKind.NotFound => "NotFound",
        ConnectorErrorKind.RateLimited => "RateLimited",
        ConnectorErrorKind.Parse => "ParseError",
        _ => "Unreachable",
    };

    /// <summary>
    /// A connector-wide failure affects every link (bad credentials, host down, throttled), so the cycle stops and one alert covers it.
    /// NotFound and ParseError concern one link; the rest of the cycle carries on.
    /// </summary>
    public static bool IsConnectorWide(ConnectorErrorKind k) => k is not (ConnectorErrorKind.NotFound or ConnectorErrorKind.Parse);

    /// <summary>The ExternalLinks.SyncState this error implies, or null to leave the state alone (unreachable and throttled links simply age into "stale").</summary>
    public static string? LinkState(ConnectorErrorKind k) => k switch
    {
        ConnectorErrorKind.Auth => "AuthFailed",
        ConnectorErrorKind.NotFound => "NotFound",
        _ => null,
    };

    /// <summary>Alerts raised by a connector-wide failure use one key per connector; link-level ones use the link id.</summary>
    public const string ConnectorKey = "connector";
}
