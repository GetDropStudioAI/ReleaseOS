using Microsoft.Extensions.Configuration;

namespace ReleaseMgmt.Infrastructure.Services;

/// <summary>
/// Deep links back into the app from notifications, team webhooks and communications (D52). The base is <c>App:PublicBaseUrl</c>, the address people open the app at;
/// it is never guessed from a request or a machine name. Unset means no link, and nothing else changes. A value that is not a plain http(s) address fails at start-up, not
/// at the first message: it must not carry credentials, a query or a fragment, because it is copied into messages that leave the app.
/// </summary>
public sealed class PublicLinks
{
    public const string ConfigKey = "App:PublicBaseUrl";

    /// <summary>No scheme-and-host-and-path base, without a trailing slash; null when not configured.</summary>
    public string? BaseUrl { get; }

    public PublicLinks(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return;
        var raw = baseUrl.Trim();
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host))
            throw new InvalidOperationException($"{ConfigKey} must be an absolute http or https address such as https://releaseos.example.com (it is the address people open the app at)");
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException($"{ConfigKey} must be a plain address: no user name or password, no query and no fragment (it is copied into messages that leave the app)");
        BaseUrl = raw.TrimEnd('/');
    }

    public static PublicLinks From(IConfiguration config) => new(config[ConfigKey]);

    public static PublicLinks None { get; } = new(null);

    /// <summary>The link for an app path such as <c>/trains/{id}</c>; null when no base is configured.</summary>
    public string? To(string path) => BaseUrl is null ? null : BaseUrl + path;

    public string? Train(string trainId) => To("/trains/" + Uri.EscapeDataString(trainId));
}
