using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;

namespace ReleaseMgmt.Api;

/// <summary>
/// Endpoint metadata for a route that is not held to the global body limit. <c>null</c> means the endpoint enforces its own cap (the two upload routes:
/// evidence attachments, Attachments:MaxBytes; CSV import, 5 MB), exactly as before the global limit existed.
/// </summary>
public sealed record BodySizeLimit(long? MaxRequestBodySize) : IRequestSizeLimitMetadata;

/// <summary>
/// Security review SEC-D7 (OWASP API4, Q-SEC-D1): every request body is held to <c>Limits:MaxRequestBodyBytes</c> (default 1 MiB) unless its endpoint says
/// otherwise with <see cref="BodySizeLimit"/>. Kestrel's own default is about 30 MB for every route, and no JSON field has a length limit of its own, so a single
/// request could store 30 MB of text that every later list, feed and PDF then carries. A declared <c>Content-Length</c> over the limit is refused here, unread,
/// with 413 <c>{guard:"RequestTooLarge"}</c>; a chunked body is stopped by Kestrel at the same limit (the exception handler keeps that 413).
/// </summary>
public sealed class RequestBodyLimit(RequestDelegate next, IConfiguration config)
{
    public const long DefaultMaxBytes = 1_048_576;
    public const string Guard = "RequestTooLarge";
    private readonly long _default = Math.Max(1024, config.GetValue("Limits:MaxRequestBodyBytes", DefaultMaxBytes));

    public async Task InvokeAsync(HttpContext http)
    {
        var meta = http.GetEndpoint()?.Metadata.GetMetadata<IRequestSizeLimitMetadata>();
        var max = meta is null ? _default : meta.MaxRequestBodySize;
        if (max is long limit)
        {
            if (http.Request.ContentLength > limit)
            {
                http.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                await http.Response.WriteAsJsonAsync(new { guard = Guard, message = $"The request body is larger than the {limit / 1024:N0} KB this endpoint accepts" });
                return;
            }
            var feature = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = limit;
        }
        await next(http);
    }
}
