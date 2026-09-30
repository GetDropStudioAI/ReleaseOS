using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Domain.Exchange;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>
/// CSV import (PROJECT_SCOPE 9, REOS-48): <c>POST /imports/{kind}:preview?mode=Append|Upsert</c> (multipart file, or the CSV as the raw body), <c>POST /imports/{jobId}:commit</c>,
/// <c>GET /imports</c> (history), <c>GET /imports/{jobId}</c> (row and column errors), <c>GET /imports/{jobId}/rows</c>, <c>GET /imports/kinds</c>.
/// Plan kinds need the Plan policy, admin data (users, teams, holidays) the Admin policy; both are RTE and Release Manager in v1 (D32). See Q-048.
/// </summary>
public static class ImportEndpoints
{
    public sealed record CommitBody(bool AcknowledgeDecertify);

    /// <summary>Registers the import and export services. Preview files live in <c>Imports:Directory</c> (default: <c>imports</c> beside the database).</summary>
    public static IServiceCollection AddExchange(this IServiceCollection services, IConfiguration config, string dbPath)
    {
        services.AddSingleton(new ImportOptions(config["Imports:Directory"] ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath))!, "imports")));
        services.AddSingleton<CsvImportService>();
        services.AddSingleton<GridExportService>();
        return services;
    }

    private static Actor ActorOf(ClaimsPrincipal u) => new(u.FindFirstValue("uid") ?? throw new InvalidOperationException("Signed-in user has no uid claim"));

    private static IResult TooLarge() => Results.Json(new { guard = ExchangeGuards.ImportTooLarge, message = $"The file is larger than {ImportLimits.MaxBytes / (1024 * 1024)} MB" }, statusCode: StatusCodes.Status413PayloadTooLarge);

    private static async Task<bool> MayImportAsync(ClaimsPrincipal u, IAuthorizationService authz, ImportKindSpec? spec) =>
        (await authz.AuthorizeAsync(u, spec?.Admin == true ? Policies.Admin : Policies.Plan)).Succeeded;

    public static void MapImports(this RouteGroupBuilder api)
    {
        api.MapGet("/imports/kinds", () => Results.Ok(ImportKinds.All.Select(k => new
        {
            k.Kind, k.Label, k.Grid, k.Plan, k.Admin, key = k.Key,
            columns = k.Columns.Select(c => new { c.Name, c.Required, type = c.Type.ToString(), allowed = c.Allowed }),
        }))).RequireAuthorization(Policies.Plan);

        api.MapPost("/imports/{kind}:preview", async (string kind, HttpRequest req, ClaimsPrincipal u, IAuthorizationService authz, CsvImportService s, CancellationToken ct) =>
        {
            if (!await MayImportAsync(u, authz, ImportKinds.Get(kind))) return Results.Forbid();
            var (bytes, name, tooLarge) = await ReadUploadAsync(req, ct);
            if (tooLarge) return TooLarge();
            if (bytes is null) return Results.Json(new { guard = ExchangeGuards.ImportRejected, message = "Send the CSV as a multipart file or as the request body" }, statusCode: StatusCodes.Status422UnprocessableEntity);
            var mode = req.Query["mode"].ToString() is { Length: > 0 } m ? m : "Append";
            var r = await s.PreviewAsync(kind, mode, name, bytes, ActorOf(u), ct);
            if (!r.IsOk && r.Failures.Any(f => f.Guard == ExchangeGuards.ImportTooLarge)) return TooLarge();
            return r.ToHttp();
        }).RequireAuthorization(Policies.Plan);

        api.MapPost("/imports/{jobId}:commit", async (string jobId, HttpRequest req, ClaimsPrincipal u, IAuthorizationService authz, CsvImportService s, CancellationToken ct) =>
        {
            var job = await s.GetAsync(jobId, ct);
            if (job is null) return Results.NotFound(new { message = "import not found" });
            if (!await MayImportAsync(u, authz, ImportKinds.Get(job.Kind))) return Results.Forbid();
            var ack = string.Equals(req.Query["acknowledgeDecertify"], "true", StringComparison.OrdinalIgnoreCase);
            if (req.HasJsonContentType() && req.ContentLength is > 0) ack |= (await req.ReadFromJsonAsync<CommitBody>(ct))?.AcknowledgeDecertify == true;
            var ifMatch = req.IfMatch();   // the job's Version from the preview; missing -> 428 (Q-004)
            return (await s.CommitAsync(jobId, ack, ActorOf(u), ifMatch, ct)).ToHttp();
        }).RequireAuthorization(Policies.Plan);

        api.MapGet("/imports", async (CsvImportService s, int? limit, CancellationToken ct) => Results.Ok(await s.ListAsync(limit ?? 50, ct))).RequireAuthorization(Policies.Plan);

        api.MapGet("/imports/{jobId}", async (string jobId, CsvImportService s, CancellationToken ct) =>
            await s.GetAsync(jobId, ct) is { } j ? Results.Ok(j) : Results.NotFound(new { message = "import not found" })).RequireAuthorization(Policies.Plan);

        api.MapGet("/imports/{jobId}/rows", async (string jobId, int? skip, int? take, CsvImportService s, CancellationToken ct) =>
            (await s.RowsAsync(jobId, skip ?? 0, take ?? 200, ct)).ToHttp()).RequireAuthorization(Policies.Plan);
    }

    /// <summary>The upload: a multipart file part, or the raw body (text/csv). Read with a hard cap, never buffered past it. The bool is "too large".</summary>
    private static async Task<(byte[]? Bytes, string? Name, bool TooLarge)> ReadUploadAsync(HttpRequest req, CancellationToken ct)
    {
        const int overhead = 1024 * 1024;   // multipart boundaries and headers
        var max = ImportLimits.MaxBytes;
        if (req.ContentLength > max + overhead) return (null, null, true);
        if (req.ContentType?.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase) == true)
        {
            var boundary = HeaderUtilities.RemoveQuotes(MediaTypeHeaderValue.Parse(req.ContentType).Boundary).Value;
            if (string.IsNullOrEmpty(boundary)) return (null, null, false);
            var reader = new MultipartReader(boundary, req.Body);
            while (await reader.ReadNextSectionAsync(ct) is { } section)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var cd) || !cd.IsFileDisposition()) continue;
                var (bytes, big) = await ReadCappedAsync(section.Body, max, ct);
                return big ? (null, null, true) : (bytes, HeaderUtilities.RemoveQuotes(cd.FileName).Value ?? HeaderUtilities.RemoveQuotes(cd.FileNameStar).Value, false);
            }
            return (null, null, false);
        }
        var (raw, tooBig) = await ReadCappedAsync(req.Body, max, ct);
        var name = req.Query["fileName"].ToString() is { Length: > 0 } q ? q : req.Headers["X-File-Name"].ToString();
        return tooBig ? (null, null, true) : (raw, name, false);
    }

    private static async Task<(byte[] Bytes, bool TooLarge)> ReadCappedAsync(Stream body, int max, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = await body.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + n > max) return ([], true);
            ms.Write(buffer, 0, n);
        }
        return (ms.ToArray(), false);
    }
}
