using System.Buffers;

namespace MyApp.BuildingBlocks.Application.Security;

/// <summary>
/// The rule for permission names: <c>{Module}.{Resource}.{Action}</c>, three segments separated by dots, each in
/// PascalCase: an ASCII upper-case letter followed by ASCII letters and digits, such as <c>Access.Users.Read</c> or
/// <c>Sales.Orders2.Export</c>. Lower-case starts (<c>access.users.read</c>), two or four segments, empty segments,
/// spaces, underscores, hyphens and letters outside ASCII are rejected.
/// </summary>
public static class PermissionName
{
    private const int SegmentCount = 3;

    private static readonly SearchValues<char> AsciiLettersAndDigits =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789");

    /// <summary>Returns whether <paramref name="permission"/> follows the rule.</summary>
    /// <param name="permission">The name to check.</param>
    /// <returns><see langword="true"/> when the name is valid.</returns>
    public static bool IsValid(string? permission)
    {
        if (string.IsNullOrEmpty(permission))
        {
            return false;
        }

        var segments = permission.Split('.');
        return segments.Length == SegmentCount && Array.TrueForAll(segments, IsSegment);
    }

    /// <summary>
    /// Returns whether <paramref name="module"/> is a valid first segment of a permission: a PascalCase name of ASCII
    /// letters and digits, such as <c>Access</c>.
    /// </summary>
    /// <param name="module">The module name to check.</param>
    /// <returns><see langword="true"/> when the name is valid.</returns>
    public static bool IsValidModule(string? module) => module is not null && IsSegment(module);

    /// <summary>Throws when <paramref name="permission"/> does not follow the rule.</summary>
    /// <param name="permission">The name to check.</param>
    /// <param name="paramName">The name of the argument, for the exception.</param>
    /// <exception cref="ArgumentException">The name is not valid; the message quotes it and states the rule.</exception>
    public static void Validate(string? permission, string? paramName = null)
    {
        if (!IsValid(permission))
        {
            throw new ArgumentException(
                $"The permission '{permission}' is not valid: a permission is named {{Module}}.{{Resource}}.{{Action}}, three PascalCase segments of ASCII letters and digits, such as 'Access.Users.Read'.",
                paramName ?? nameof(permission));
        }
    }

    private static bool IsSegment(string segment) =>
        segment.Length > 0
        && char.IsAsciiLetterUpper(segment[0])
        && !segment.AsSpan(1).ContainsAnyExcept(AsciiLettersAndDigits);
}
