namespace ReleaseMgmt.Domain.Services;

/// <summary>A failed guard. Maps to HTTP 422 <c>{guard, message, items}</c>.</summary>
public sealed record GuardFailure(string Guard, string Message, IReadOnlyList<string>? Items = null);

public enum ResultKind { Ok, GuardFailed, Conflict, NotFound }

/// <summary>Outcome of a service call: Ok(value) -> 200, GuardFailed -> 422, Conflict(current row) -> 409, NotFound -> 404.</summary>
public sealed record ServiceResult<T>(ResultKind Kind, T? Value, IReadOnlyList<GuardFailure> Failures, object? Current = null, string? Missing = null)
{
    public bool IsOk => Kind == ResultKind.Ok;
    public static ServiceResult<T> Ok(T value) => new(ResultKind.Ok, value, []);
    public static ServiceResult<T> Fail(params GuardFailure[] failures) => new(ResultKind.GuardFailed, default, failures);
    public static ServiceResult<T> Fail(IEnumerable<GuardFailure> failures) => new(ResultKind.GuardFailed, default, [.. failures]);
    public static ServiceResult<T> Conflict(object current) => new(ResultKind.Conflict, default, [], current);
    public static ServiceResult<T> NotFound(string what) => new(ResultKind.NotFound, default, [], Missing: what);
}

/// <summary>Who is acting. The user's role is read from Users, the same source the database triggers use.</summary>
public sealed record Actor(string UserId);
