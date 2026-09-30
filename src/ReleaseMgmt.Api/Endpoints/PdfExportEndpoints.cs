using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using ReleaseMgmt.Api.Auth;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Exports;

namespace ReleaseMgmt.Api.Endpoints;

public sealed record CreateExportRequest(string? Kind, string? Format);

/// <summary>
/// PDF export jobs (REOS-50, PROJECT_SCOPE 9). Prefix /export-jobs (the grid CSV/XLSX exports of REOS-48/49 own /exports/...). Jobs are created Queued (202) and rendered by the
/// ExportWorker; nothing here sets a Status. Roles: the evidence pack (and its ZIP) needs AuditRead (RTE, Release Manager, Governance Officer, per the scope's permission table:
/// "Read audit log, evidence packs"); the release report, run sheet and scorecard are readable by any signed-in role. A finished job is evidence and cannot be deleted (Q-050c).
/// </summary>
public static class PdfExportEndpoints
{
    private static Actor ActorOf(ClaimsPrincipal u) => new(u.FindFirstValue("uid") ?? throw new InvalidOperationException("Signed-in user has no uid claim"));

    private static async Task<bool> CanAuditAsync(IAuthorizationService auth, ClaimsPrincipal u) => (await auth.AuthorizeAsync(u, Policies.AuditRead)).Succeeded;

    public static void MapPdfExports(this RouteGroupBuilder api)
    {
        api.MapPost("/trains/{id}/export-jobs", async (string id, CreateExportRequest? body, ClaimsPrincipal u, IAuthorizationService auth, ExportService svc, CancellationToken ct) =>
        {
            var kind = ExportKinds.Parse(body?.Kind);
            if (kind is null) return Results.Json(new { guard = ExportGuards.BadKind, message = "Kind must be ReleaseReport, RunSheet, EvidencePack or Scorecard" }, statusCode: StatusCodes.Status400BadRequest);
            if (ExportKinds.NeedsAuditRead(kind) && !await CanAuditAsync(auth, u)) return Results.Forbid();
            var r = await svc.EnqueueAsync(id, kind, body?.Format, ActorOf(u), ct);
            return r.IsOk ? Results.Accepted($"/api/v1/export-jobs/{r.Value!.Id}", r.Value) : r.ToHttp();
        }).RequireAuthorization(Policies.Read);

        api.MapGet("/export-jobs", async (string? trainId, string? status, int? limit, ClaimsPrincipal u, IAuthorizationService auth, ExportService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(trainId, status, await CanAuditAsync(auth, u), limit, ct))).RequireAuthorization(Policies.Read);

        api.MapGet("/export-jobs/{id}", async (string id, ClaimsPrincipal u, IAuthorizationService auth, ExportService svc, CancellationToken ct) =>
        {
            var j = await svc.GetAsync(id, ct);
            if (j is null) return Results.NotFound(new { message = "export job not found" });
            if (ExportKinds.NeedsAuditRead(j.Kind) && !await CanAuditAsync(auth, u)) return Results.Forbid();
            return Results.Ok(j);
        }).RequireAuthorization(Policies.Read);

        api.MapGet("/export-jobs/{id}/file", async (string id, HttpContext http, ClaimsPrincipal u, IAuthorizationService auth, ExportService svc, CancellationToken ct) =>
        {
            var j = await svc.GetAsync(id, ct);
            if (j is null) return Results.NotFound(new { message = "export job not found" });
            if (ExportKinds.NeedsAuditRead(j.Kind) && !await CanAuditAsync(auth, u)) return Results.Forbid();
            var r = await svc.FindFileAsync(id, ct);
            if (!r.IsOk) return r.ToHttp();
            var f = r.Value!;
            http.Response.Headers.XContentTypeOptions = "nosniff";
            http.Response.Headers.CacheControl = "private, no-store";
            http.Response.Headers["X-Content-SHA256"] = f.Job.Sha256;
            return Results.File(new FileStream(f.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan),
                f.Params.ContentType ?? "application/octet-stream", f.Job.FileName, enableRangeProcessing: false);
        }).RequireAuthorization(Policies.Read);

        // Exports (above all evidence packs) are audit records: refused explicitly rather than left to a 404/405.
        api.MapDelete("/export-jobs/{id}", (string id) =>
            Results.Json(new { guard = "ExportImmutable", message = "Export jobs are kept as a record and cannot be deleted; generate a new one if needed" }, statusCode: StatusCodes.Status405MethodNotAllowed))
            .RequireAuthorization(Policies.Read);
    }
}
