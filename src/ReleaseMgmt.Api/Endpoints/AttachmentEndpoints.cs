using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Domain.Common;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>Evidence attachments (PROJECT_SCOPE section 6 Evidence). Upload is multipart and streamed; the hash is computed by the server (Q-035).</summary>
public static class AttachmentEndpoints
{
    private const long MultipartOverhead = 1_048_576;   // form fields + boundaries on top of the file itself
    private static readonly AuthorizeAttribute Uploaders = new() { Roles = $"{Roles.RTE},{Roles.ReleaseManager},{Roles.GovernanceOfficer}" };

    private static Actor ActorOf(ClaimsPrincipal u) => new(u.FindFirstValue("uid") ?? throw new InvalidOperationException("Signed-in user has no uid claim"));

    private static IResult Refuse(string guard, string message, int status = StatusCodes.Status422UnprocessableEntity) =>
        Results.Json(new { guard, message, failures = new[] { new { guard, message } } }, statusCode: status);

    private static IResult Http<T>(ServiceResult<T> r) =>
        r.Kind == ResultKind.GuardFailed && r.Failures[0].Guard == AttachmentGuards.TooLarge
            ? Refuse(r.Failures[0].Guard, r.Failures[0].Message, StatusCodes.Status413PayloadTooLarge)
            : r.ToHttp();

    public static void MapAttachments(this RouteGroupBuilder api)
    {
        // POST /attachments (multipart/form-data): fields entityType and entityId, then the file part. Fields must come before the file so a refused upload is refused unread.
        api.MapPost("/attachments", async (HttpContext http, ClaimsPrincipal u, AttachmentService s, CancellationToken ct) =>
        {
            var max = s.MaxBytes;
            if (http.Request.ContentLength > max + MultipartOverhead)
                return Refuse(AttachmentGuards.TooLarge, $"The file is larger than the {max / 1048576} MB limit for evidence attachments", StatusCodes.Status413PayloadTooLarge);
            var limit = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = max + MultipartOverhead;

            if (!MediaTypeHeaderValue.TryParse(http.Request.ContentType, out var mt) || !mt.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
                || HeaderUtilities.RemoveQuotes(mt.Boundary).Value is not { Length: > 0 } boundary)
                return Refuse("InvalidUpload", "Send the file as multipart/form-data with entityType, entityId and file parts", StatusCodes.Status415UnsupportedMediaType);

            var actor = ActorOf(u);
            string? entityType = null, entityId = null;
            var reader = new MultipartReader(boundary, http.Request.Body) { HeadersLengthLimit = 16 * 1024, BodyLengthLimit = max + MultipartOverhead };
            try
            {
                while (await reader.ReadNextSectionAsync(ct) is { } section)
                {
                    var cd = section.GetContentDispositionHeader();
                    if (cd is null || !cd.DispositionType.Equals("form-data")) continue;
                    var name = HeaderUtilities.RemoveQuotes(cd.Name).Value;
                    if (cd.IsFileDisposition() && name == "file")
                    {
                        if (string.IsNullOrEmpty(entityType) || string.IsNullOrEmpty(entityId))
                            return Refuse("InvalidUpload", "Send entityType and entityId before the file part");
                        var fileName = HeaderUtilities.RemoveQuotes(cd.FileName).Value ?? HeaderUtilities.RemoveQuotes(cd.FileNameStar).Value;
                        return Http(await s.UploadAsync(entityType, entityId, fileName, section.ContentType, section.Body, actor, ct));
                    }
                    // A client-sent sha256 field is read past and ignored: only the server's hash counts.
                    var value = name is "entityType" or "entityId" ? await new StreamReader(section.Body).ReadToEndAsync(ct) : null;
                    if (name == "entityType") entityType = value?.Trim();
                    else if (name == "entityId") entityId = value?.Trim();
                }
            }
            catch (InvalidDataException ex)   // multipart length limit crossed: too large, or malformed
            {
                return Refuse(AttachmentGuards.TooLarge, $"The upload is larger than the {max / 1048576} MB limit or is malformed ({ex.Message})", StatusCodes.Status413PayloadTooLarge);
            }
            catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                return Refuse(AttachmentGuards.TooLarge, $"The file is larger than the {max / 1048576} MB limit for evidence attachments", StatusCodes.Status413PayloadTooLarge);
            }
            return Refuse("InvalidUpload", "The upload has no file part");
        }).RequireAuthorization(Uploaders).DisableAntiforgery().WithMetadata(new BodySizeLimit(null));   // enforces Attachments:MaxBytes itself, not the global request limit

        api.MapGet("/attachments", async (string trainId, string? entityType, string? entityId, AttachmentService s, CancellationToken ct) =>
            Results.Ok(await s.ManifestAsync(trainId, entityType, entityId, ct))).RequireAuthorization(Policies.Read);
        api.MapGet("/trains/{id}/attachments", async (string id, string? entityType, string? entityId, AttachmentService s, CancellationToken ct) =>
            Results.Ok(await s.ManifestAsync(id, entityType, entityId, ct))).RequireAuthorization(Policies.Read);
        api.MapGet("/trains/{id}/attachments/manifest", async (string id, AttachmentService s, CancellationToken ct) =>
            Results.Ok(await s.ManifestAsync(id, null, null, ct))).RequireAuthorization(Policies.Read);

        api.MapGet("/attachments/{id}", async (string id, HttpContext http, AttachmentService s, CancellationToken ct) =>
        {
            var r = await s.FindFileAsync(id, ct);
            if (!r.IsOk) return r.ToHttp();
            var a = r.Value!.Row;
            http.Response.Headers.XContentTypeOptions = "nosniff";
            http.Response.Headers.CacheControl = "private, no-store";
            http.Response.Headers["X-Content-SHA256"] = a.Sha256;
            http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";   // REOS-53: if a browser ever renders it (uploaded HTML or SVG), it runs nothing and has no origin
            // fileDownloadName -> Content-Disposition: attachment with an RFC 6266 encoded name; the name was sanitised at upload.
            return Results.File(new FileStream(r.Value.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan),
                AttachmentService.SafeContentType(a.ContentType), AttachmentService.SanitizeFileName(a.FileName), enableRangeProcessing: false);
        }).RequireAuthorization(Policies.Read);

        api.MapDelete("/attachments/{id}", async (string id, ClaimsPrincipal u, AttachmentService s, CancellationToken ct) =>
            Http(await s.DeleteAsync(id, ActorOf(u), ct))).RequireAuthorization(Uploaders);
    }
}
