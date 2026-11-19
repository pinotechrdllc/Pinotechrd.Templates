using Microsoft.AspNetCore.Http;

namespace MyApp.Modules.Access.Endpoints.Auth;

/// <summary>
/// The cookie that carries the refresh token, the only place the token travels: never in a response body, never readable
/// by script. <c>__Host-</c> makes the browser refuse it unless it is <c>Secure</c>, has <c>Path=/</c> and no
/// <c>Domain</c>, so no other host or subdomain can set or read it; <c>SameSite=Strict</c> keeps it off requests from
/// other sites. A browser client therefore calls the API from the same site (a proxy in front of both).
/// </summary>
public static class RefreshCookie
{
    /// <summary>The cookie name.</summary>
    public const string Name = "__Host-refresh";

    /// <summary>Reads the refresh token the request carries, or <see langword="null"/>.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The token, or <see langword="null"/> when there is no cookie or it is empty.</returns>
    public static string? Read(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Cookies.TryGetValue(Name, out var value) && !string.IsNullOrEmpty(value) ? value : null;
    }

    /// <summary>Sets the cookie to <paramref name="refreshToken"/> for <paramref name="lifetime"/>.</summary>
    /// <param name="response">The response.</param>
    /// <param name="refreshToken">The refresh token.</param>
    /// <param name="lifetime">How long the browser keeps it, the token's sliding lifetime.</param>
    public static void Write(HttpResponse response, string refreshToken, TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(response);
        var options = Options();
        options.MaxAge = lifetime;
        response.Cookies.Append(Name, refreshToken, options);
    }

    /// <summary>Tells the browser to drop the cookie.</summary>
    /// <param name="response">The response.</param>
    public static void Clear(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        // The same attributes as when set: a browser ignores a __Host- cookie without them, deletion included.
        response.Cookies.Delete(Name, Options());
    }

    private static CookieOptions Options() => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        IsEssential = true,
    };
}
