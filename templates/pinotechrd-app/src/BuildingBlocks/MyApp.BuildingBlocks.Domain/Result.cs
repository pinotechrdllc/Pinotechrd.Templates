namespace MyApp.BuildingBlocks.Domain;

/// <summary>
/// The outcome of an operation that can fail in an expected way: either a success or a failure with an
/// <see cref="Domain.Error"/>. Defects and infrastructure faults are still reported with exceptions.
/// </summary>
public class Result
{
    private static readonly Result SuccessResult = new(null);

    private readonly Error? _error;

    private protected Result(Error? error) => _error = error;

    /// <summary>Gets a value indicating whether the operation succeeded.</summary>
    public bool IsSuccess => _error is null;

    /// <summary>Gets a value indicating whether the operation failed.</summary>
    public bool IsFailure => _error is not null;

    /// <summary>Gets the error of a failed result.</summary>
    /// <exception cref="InvalidOperationException">The result is a success.</exception>
    public Error Error => _error ?? throw new InvalidOperationException("A successful result has no error.");

    /// <summary>Converts an error to a failed result.</summary>
    /// <param name="error">The error.</param>
    /// <exception cref="ArgumentNullException"><paramref name="error"/> is <see langword="null"/>.</exception>
    public static implicit operator Result(Error error) => Failure(error);

    /// <summary>Returns a successful result.</summary>
    /// <returns>The successful result.</returns>
    public static Result Success() => SuccessResult;

    /// <summary>
    /// Returns a successful result that carries <paramref name="value"/>. A <see langword="null"/> value is
    /// accepted; <see cref="Result{T}.Value"/> then returns <see langword="null"/>.
    /// </summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="value">The value.</param>
    /// <returns>The successful result.</returns>
    public static Result<T> Success<T>(T value) => new(value);

    /// <summary>Returns a failed result.</summary>
    /// <param name="error">The error.</param>
    /// <returns>The failed result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="error"/> is <see langword="null"/>.</exception>
    public static Result Failure(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result(error);
    }

    /// <summary>Returns a failed result of an operation that would return a <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The type of the value on success.</typeparam>
    /// <param name="error">The error.</param>
    /// <returns>The failed result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="error"/> is <see langword="null"/>.</exception>
    public static Result<T> Failure<T>(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result<T>(error);
    }
}
