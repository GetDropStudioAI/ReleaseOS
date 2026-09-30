using Microsoft.AspNetCore.Diagnostics;
using ReleaseMgmt.Domain.Services;
using ReleaseMgmt.Infrastructure.Services;

namespace ReleaseMgmt.Api;

/// <summary>Safety net (CLAUDE.md rule 1): a trigger abort or constraint failure that escapes a service becomes 422 DbRule, never a 500.</summary>
public sealed class DbRuleExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception ex, CancellationToken ct)
    {
        if (ex is Endpoints.PreconditionRequiredException)
        {
            ctx.Response.StatusCode = StatusCodes.Status428PreconditionRequired;
            await ctx.Response.WriteAsJsonAsync(new { guard = "PreconditionRequired", message = ex.Message }, ct);
            return true;
        }
        if (ex is BadHttpRequestException bad)   // a missing query value or malformed body is the client's mistake: keep Kestrel's 4xx instead of the handler's 500 (REOS-53)
        {
            ctx.Response.StatusCode = bad.StatusCode;
            await ctx.Response.WriteAsJsonAsync(new { guard = "BadRequest", message = "The request could not be read: a required value is missing or the body is not valid" }, ct);
            return true;
        }
        if (!DbRules.TryGetMessage(ex, out var message)) return false;
        ctx.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        await ctx.Response.WriteAsJsonAsync(new { guard = Guards.DbRule, message }, ct);
        return true;
    }
}
