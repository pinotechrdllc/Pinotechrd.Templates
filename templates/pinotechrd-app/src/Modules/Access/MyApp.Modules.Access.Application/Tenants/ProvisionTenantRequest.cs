using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MyApp.Modules.Access.Application.Tenants;

/// <summary>
/// A request to provision a tenant. The slug is the tenant's identifier and the key of the request: the same slug, name
/// and subscription is the same request. Values are refused, never normalized, and no message quotes them.
/// </summary>
/// <param name="Slug">
/// The identifier: 1 to 63 characters, lower-case ASCII letters, digits and single hyphens, starting and ending with a
/// letter or digit (<see cref="SlugPattern"/>). It never names a schema or reaches SQL text.
/// </param>
/// <param name="Name">The display name: 1 to <see cref="MaxNameLength"/> characters, not blank, no control, format or
/// line/paragraph separator character and no lone surrogate.</param>
/// <param name="SubscriptionId">The subscription the tenant belongs to; not the empty GUID.</param>
public sealed partial record ProvisionTenantRequest(string Slug, string Name, Guid SubscriptionId)
{
    /// <summary>
    /// The pattern of a slug, the one <see cref="IsValidSlug"/> uses (culture-invariant). A hyphen is never doubled
    /// (<c>--</c> starts a SQL comment); <c>\z</c>, not <c>$</c>, which also matches before a final line feed.
    /// </summary>
    public const string SlugPattern = @"^(?!.*--)[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\z";

    /// <summary>The longest name, the length of the catalog column.</summary>
    public const int MaxNameLength = 200;

    /// <summary>Whether <paramref name="slug"/> matches <see cref="SlugPattern"/>.</summary>
    /// <param name="slug">The slug to check.</param>
    /// <returns><see langword="true"/> when it is a valid slug.</returns>
    public static bool IsValidSlug(string? slug) => slug is not null && SlugRegex().IsMatch(slug);

    /// <summary>Whether <paramref name="name"/> is a valid tenant name.</summary>
    /// <param name="name">The name to check.</param>
    /// <returns>
    /// <see langword="true"/> when it is not blank, has no control, format or line/paragraph separator character and no
    /// lone surrogate, and fits the catalog column.
    /// </returns>
    public static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= MaxNameLength && !HasInvisible(name);

    /// <summary>Checks every field.</summary>
    /// <returns>The messages per field; empty when the request is valid. No message quotes a value.</returns>
    public IReadOnlyDictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (!IsValidSlug(Slug))
        {
            errors[nameof(Slug)] = ["The slug must be 1 to 63 lower-case letters, digits or single hyphens, starting and ending with a letter or digit."];
        }

        if (!IsValidName(Name))
        {
            errors[nameof(Name)] = [$"The name must be 1 to {MaxNameLength} characters, not blank, without control, invisible format or separator characters."];
        }

        if (SubscriptionId == Guid.Empty)
        {
            errors[nameof(SubscriptionId)] = ["The subscription id must be a GUID other than the empty one."];
        }

        return errors;
    }

    // Culture-invariant, so no culture widens [a-z].
    [GeneratedRegex(SlugPattern, RegexOptions.CultureInvariant)]
    private static partial Regex SlugRegex();

    // Control (Cc), format (Cf: zero-width and bidirectional marks), line and paragraph separators (Zl, Zp) and lone
    // surrogates (Cs): invisible or able to change how the name is shown. A surrogate pair (an emoji) is one valid rune.
    private static bool HasInvisible(string name)
    {
        var rest = name.AsSpan();
        while (!rest.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(rest, out var rune, out var consumed) != OperationStatus.Done
                || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
                    or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Surrogate)
            {
                return true;
            }

            rest = rest[consumed..];
        }

        return false;
    }
}
