namespace MyApp.BuildingBlocks.Domain;

/// <summary>
/// The outcome of an operation that returns a <typeparamref name="T"/> and can fail in an expected way.
/// </summary>
/// <typeparam name="T">
/// The type of the value on success. It must not be <see cref="Error"/> or <see cref="object"/>: the two implicit
/// conversions would then compete, and an error could be taken for a successful value.
/// </typeparam>
/// <remarks>
/// C# does not apply user-defined conversions when the source expression has an interface type, so a value typed
/// as an interface, such as <c>IReadOnlyList&lt;T&gt;</c>, must be wrapped with <see cref="Result.Success{T}(T)"/>.
/// </remarks>
public sealed class Result<T> : Result
{
    private readonly T _value;

    internal Result(T value)
        : base(null) => _value = value;

    internal Result(Error error)
        : base(error) => _value = default!;

    /// <summary>Gets the value of a successful result.</summary>
    /// <exception cref="InvalidOperationException">The result is a failure.</exception>
    public T Value => IsSuccess ? _value : throw new InvalidOperationException("A failed result has no value.");

    /// <summary>
    /// Converts a value to a successful result. Not applied when the value is typed as an interface; use
    /// <see cref="Result.Success{T}(T)"/> then.
    /// </summary>
    /// <param name="value">The value.</param>
    public static implicit operator Result<T>(T value) => Success(value);

    /// <summary>Converts an error to a failed result.</summary>
    /// <param name="error">The error.</param>
    /// <exception cref="ArgumentNullException"><paramref name="error"/> is <see langword="null"/>.</exception>
    public static implicit operator Result<T>(Error error) => Failure<T>(error);
}
