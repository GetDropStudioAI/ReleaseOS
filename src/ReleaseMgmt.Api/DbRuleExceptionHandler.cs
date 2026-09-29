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
        if (!DbRules.TryGetMessage(ex, out var message)) return false;
        ctx.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        await ctx.Response.WriteAsJsonAsync(new { guard = Guards.DbRule, message }, ct);
        return true;
    }
}
