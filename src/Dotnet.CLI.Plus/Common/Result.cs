namespace Dotnet.CLI.Plus.Common;

public abstract record Result<TValue, TError>
    where TValue : notnull
    where TError : notnull
{
    private Result() { }

    public sealed record Success(TValue Value) : Result<TValue, TError>;

    public sealed record Failure(TError Error) : Result<TValue, TError>;

    public TResult Match<TResult>(
        Func<TValue, TResult> onSuccess,
        Func<TError, TResult> onFailure
    ) =>
        this switch
        {
            Success success => onSuccess(success.Value),
            Failure failure => onFailure(failure.Error),
            _ => throw new InvalidOperationException("Unknown result type."),
        };
}

public readonly record struct Unit
{
    public static Unit Value => default;
}
