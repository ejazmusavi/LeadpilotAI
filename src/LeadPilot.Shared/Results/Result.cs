namespace LeadPilot.Shared.Results;

/// <summary>Stable, machine-readable error returned by use cases.</summary>
public sealed record Error(string Code, string Message)
{
    public static readonly Error None = new(string.Empty, string.Empty);
    public static Error NotFound(string what) => new("not_found", $"{what} was not found.");
    public static Error Validation(string message) => new("validation", message);
    public static Error Forbidden(string message = "You do not have permission to perform this action.") => new("forbidden", message);
    public static Error Conflict(string message) => new("conflict", message);
    /// <summary>The request would create a record that probably already exists (HTTP 409). The caller may resend with an explicit override.</summary>
    public static Error Duplicate(string message) => new("duplicate", message);
    public static Error TooLarge(string message) => new("too_large", message);
}

public class Result
{
    protected Result(bool isSuccess, Error error)
    {
        if (isSuccess && error != Error.None) throw new ArgumentException("A successful result cannot carry an error.", nameof(error));
        if (!isSuccess && error == Error.None) throw new ArgumentException("A failed result must carry an error.", nameof(error));
        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public Error Error { get; }

    public static Result Success() => new(true, Error.None);
    public static Result Failure(Error error) => new(false, error);
    public static Result<T> Success<T>(T value) => new(value, true, Error.None);
    public static Result<T> Failure<T>(Error error) => new(default, false, error);
}

public sealed class Result<T> : Result
{
    private readonly T? _value;
    internal Result(T? value, bool isSuccess, Error error) : base(isSuccess, error) => _value = value;

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("Cannot read the value of a failed result.");
}
