using ReleaseMgmt.Domain.Services;

namespace ReleaseMgmt.Api.Endpoints;

/// <summary>ServiceResult -> HTTP: Ok 200, GuardFailed 422 {guard,…}, Conflict 409 with the current row, NotFound 404.</summary>
public static class Results2
{
    public static IResult ToHttp<T>(this ServiceResult<T> r) => r.Kind switch
    {
        ResultKind.Ok => Results.Ok(r.Value),
        ResultKind.GuardFailed => Results.Json(GuardBody(r.Failures), statusCode: StatusCodes.Status422UnprocessableEntity),
        ResultKind.Conflict => Results.Json(new { message = "The record changed since you loaded it", current = r.Current }, statusCode: StatusCodes.Status409Conflict),
        _ => Results.NotFound(new { message = $"{r.Missing} not found" }),
    };

    public static async Task<IResult> ToHttpAsync<T>(this Task<ServiceResult<T>> t) => (await t).ToHttp();

    public static object GuardBody(IReadOnlyList<GuardFailure> failures)
    {
        var first = failures[0];
        return new
        {
            guard = first.Guard,
            message = first.Message,
            items = first.Items,
            gates = first.Guard == Guards.GateLockout ? first.Items : null,
            failures = failures.Select(f => new { guard = f.Guard, message = f.Message, items = f.Items }),
        };
    }

    /// <summary>Parses If-Match: 3 or "3". Q-004: required by default (missing or unparseable -> 428); Api:RequireIfMatch=false relaxes it.</summary>
    public static int? IfMatch(this HttpRequest req)
    {
        if (int.TryParse(req.Headers.IfMatch.ToString().Trim('"', ' '), out var v)) return v;
        var require = req.HttpContext.RequestServices.GetRequiredService<IConfiguration>().GetValue("Api:RequireIfMatch", true);
        return require ? throw new PreconditionRequiredException() : null;
    }
}

/// <summary>Thrown by <see cref="Results2.IfMatch"/>; DbRuleExceptionHandler maps it to 428.</summary>
public sealed class PreconditionRequiredException() : Exception("This change needs an If-Match header carrying the Version you loaded");
