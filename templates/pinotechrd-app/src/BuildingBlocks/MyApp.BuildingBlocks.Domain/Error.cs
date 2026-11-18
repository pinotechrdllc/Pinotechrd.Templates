using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace MyApp.BuildingBlocks.Domain;

/// <summary>
/// Describes an expected failure. Errors are immutable, returned in a <see cref="Result"/> and never thrown.
/// They are compared by reference: compare <see cref="Code"/> to test for a specific error.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1716:Identifiers should not match keywords",
    Justification = "Error is the domain term; the generated project is C# only, where it is not a keyword.")]
public sealed class Error
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> NoDetails =
        new Dictionary<string, IReadOnlyList<string>>(0, StringComparer.Ordinal).AsReadOnly();

    private Error(ErrorKind kind, string code, string message, IReadOnlyDictionary<string, IReadOnlyList<string>> details)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Kind = kind;
        Code = code;
        Message = message;
        Details = details;
    }

    /// <summary>Gets the kind of failure.</summary>
    public ErrorKind Kind { get; }

    /// <summary>Gets the stable, machine-readable code that clients use to identify and translate the error.</summary>
    public string Code { get; }

    /// <summary>Gets a description for developers and logs. Clients translate <see cref="Code"/> instead of showing it.</summary>
    public string Message { get; }

    /// <summary>Gets the validation messages per field. Empty unless the error is a validation error built with details.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Details { get; }

    /// <summary>Creates an error of kind <see cref="ErrorKind.Failure"/>.</summary>
    /// <param name="code">The stable error code.</param>
    /// <param name="message">The description for developers.</param>
    /// <returns>The error.</returns>
    public static Error Failure(string code, string message) => new(ErrorKind.Failure, code, message, NoDetails);

    /// <summary>Creates an error of kind <see cref="ErrorKind.Validation"/>.</summary>
    /// <param name="code">The stable error code.</param>
    /// <param name="message">The description for developers.</param>
    /// <param name="details">The messages per field, or <see langword="null"/> for none. The values are copied.</param>
    /// <returns>The error.</returns>
    public static Error Validation(string code, string message, IReadOnlyDictionary<string, string[]>? details = null) =>
        new(ErrorKind.Validation, code, message, Copy(details));

    /// <summary>Creates an error of kind <see cref="ErrorKind.NotFound"/>.</summary>
    /// <param name="code">The stable error code.</param>
    /// <param name="message">The description for developers.</param>
    /// <returns>The error.</returns>
    public static Error NotFound(string code, string message) => new(ErrorKind.NotFound, code, message, NoDetails);

    /// <summary>Creates an error of kind <see cref="ErrorKind.Conflict"/>.</summary>
    /// <param name="code">The stable error code.</param>
    /// <param name="message">The description for developers.</param>
    /// <returns>The error.</returns>
    public static Error Conflict(string code, string message) => new(ErrorKind.Conflict, code, message, NoDetails);

    /// <summary>Creates an error of kind <see cref="ErrorKind.Unauthorized"/>.</summary>
    /// <param name="code">The stable error code.</param>
    /// <param name="message">The description for developers.</param>
    /// <returns>The error.</returns>
    public static Error Unauthorized(string code, string message) => new(ErrorKind.Unauthorized, code, message, NoDetails);

    /// <summary>Creates an error of kind <see cref="ErrorKind.Forbidden"/>.</summary>
    /// <param name="code">The stable error code.</param>
    /// <param name="message">The description for developers.</param>
    /// <returns>The error.</returns>
    public static Error Forbidden(string code, string message) => new(ErrorKind.Forbidden, code, message, NoDetails);

    /// <inheritdoc />
    public override string ToString() => $"{Kind} {Code}: {Message}";

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Copy(IReadOnlyDictionary<string, string[]>? details)
    {
        if (details is null || details.Count == 0)
        {
            return NoDetails;
        }

        var copy = new Dictionary<string, IReadOnlyList<string>>(details.Count, StringComparer.Ordinal);
        foreach (var (field, messages) in details)
        {
            ArgumentNullException.ThrowIfNull(messages, nameof(details));
            copy[field] = new ReadOnlyCollection<string>([.. messages]);
        }

        return copy.AsReadOnly();
    }
}
