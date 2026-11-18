using System.ComponentModel.DataAnnotations;

namespace MyApp.BuildingBlocks.Web.Security;

/// <summary>
/// Cross-origin settings, bound from <see cref="SectionName"/>. With no allowed origin CORS is off: no CORS header
/// is ever written and browsers only allow same-origin calls, so a fresh project is closed by default.
/// </summary>
public sealed class HttpCorsOptions : IValidatableObject
{
    /// <summary>The configuration section the options are bound from.</summary>
    public const string SectionName = "Http:Cors";

    /// <summary>
    /// The origins a browser may call the API from, each exactly as a browser sends it: <c>scheme://host[:port]</c>
    /// with no path, no trailing slash and no default port, for example <c>https://app.example.com</c>. Credentials
    /// (cookies) are allowed for these origins, so the wildcard <c>*</c> is rejected, and an <c>http</c> origin is only
    /// accepted for a local host (<c>localhost</c>, <c>*.localhost</c>, a loopback address) or in the Development
    /// environment. Empty by default: CORS is off.
    /// </summary>
    public IReadOnlyList<string> AllowedOrigins { get; set; } = [];

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var origin in AllowedOrigins)
        {
            if (OriginProblem(origin) is { } problem)
            {
                yield return Invalid(origin, problem);
            }
        }
    }

    /// <summary>
    /// The listed <c>http</c> origins whose host is not local. They are only accepted in the Development environment:
    /// elsewhere the credentials allowed for them would travel unencrypted.
    /// </summary>
    internal IEnumerable<string> InsecureOrigins() =>
        AllowedOrigins.Where(o => OriginProblem(o) is null
            && Uri.TryCreate(o, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttp
            && !LocalHost.Is(uri.Host));

    /// <summary>The message of an invalid origin; it names the setting.</summary>
    internal static ValidationResult Invalid(string? origin, string problem) =>
        new($"{SectionName}:{nameof(AllowedOrigins)} contains '{origin}', which {problem}.", [nameof(AllowedOrigins)]);

    /// <summary>Returns why an entry is not an origin a browser could send, or null when it is one.</summary>
    private static string? OriginProblem(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return "is empty";
        }

        if (origin.Contains('*', StringComparison.Ordinal))
        {
            return "is a wildcard; '*' and wildcard hosts are never allowed because credentials are allowed for every listed origin";
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return "is not an http or https origin such as https://app.example.com";
        }

        if (origin.EndsWith('/') || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return "has a path, a trailing slash, a query, a fragment or user information; an origin is only scheme://host[:port]";
        }

        // A browser never writes the default port, so an origin that does would silently never match.
        if (!string.Equals(origin, uri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
        {
            return "is not written as a browser sends it (it names the default port of its scheme, 443 for https or 80 for http); "
                + $"write it as {uri.GetLeftPart(UriPartial.Authority)}";
        }

        return null;
    }
}
