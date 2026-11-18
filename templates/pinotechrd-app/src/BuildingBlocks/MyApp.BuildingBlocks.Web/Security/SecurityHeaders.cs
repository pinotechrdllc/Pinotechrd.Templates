using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace MyApp.BuildingBlocks.Web.Security;

/// <summary>
/// Adds the response headers that harden a JSON API, when the response starts, so error responses written after the
/// exception handler clears the response get them too. Response-start callbacks run in reverse order of registration
/// and this one is registered before every other middleware's, so it runs last and sees the headers they wrote.
/// </summary>
internal static class SecurityHeaders
{
    /// <summary>Content security policy of an API that serves no HTML: nothing may load and nothing may frame it.</summary>
    internal const string ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";

    /// <summary>One year, the minimum the HSTS preload list asks for and what OWASP recommends.</summary>
    internal const string StrictTransportSecurity = "max-age=31536000; includeSubDomains";

    /// <summary>
    /// The cache policy of a response that sets none: API responses carry user data (and later tokens), so no browser
    /// or shared cache may keep them (OWASP REST Security Cheat Sheet). An endpoint that sets its own keeps it.
    /// </summary>
    internal const string CacheControl = "no-store";

    /// <summary>
    /// Only same-origin pages may embed a response with a no-cors request (an image or script tag). It does not
    /// affect CORS requests (<c>fetch</c> in cors mode), which the CORS policy governs.
    /// </summary>
    internal const string CrossOriginResourcePolicy = "same-origin";

    private const string OriginToken = "Origin";

    /// <summary>Registers the headers on the response of <paramref name="context"/>.</summary>
    /// <param name="context">The request.</param>
    /// <param name="hstsEnabled">Whether HSTS may be sent at all, that is, the environment is not Development.</param>
    /// <param name="varyByOrigin">
    /// Whether CORS is on, so responses differ by the <c>Origin</c> request header and must say so to caches. The
    /// platform only writes <c>Vary: Origin</c> when more than one origin is listed.
    /// </param>
    public static void Register(HttpContext context, bool hstsEnabled, bool varyByOrigin)
    {
        var sendHsts = hstsEnabled && context.Request.IsHttps && !LocalHost.Is(context.Request.Host.Host);
        context.Response.OnStarting(
            static state =>
            {
                var (response, hsts, vary) = ((HttpResponse, bool, bool))state;
                var headers = response.Headers;
                headers.XContentTypeOptions = "nosniff";
                headers.XFrameOptions = "DENY";
                headers["Referrer-Policy"] = "no-referrer";
                headers.ContentSecurityPolicy = ContentSecurityPolicy;
                headers["Cross-Origin-Resource-Policy"] = CrossOriginResourcePolicy;
                if (!headers.ContainsKey(HeaderNames.CacheControl))
                {
                    headers.CacheControl = CacheControl;
                }

                if (hsts)
                {
                    headers.StrictTransportSecurity = StrictTransportSecurity;
                }

                if (vary)
                {
                    headers.Vary = VaryWithOrigin(headers.Vary);
                }

                return Task.CompletedTask;
            },
            (context.Response, sendHsts, varyByOrigin));
    }

    /// <summary>
    /// Returns the <c>Vary</c> value with <c>Origin</c> exactly once. This callback runs after every other one, so it
    /// sees the values an endpoint wrote and the one the platform CORS middleware appends when several origins are
    /// listed; each name is kept once, in its first position, and <c>Origin</c> is added when missing. <c>*</c> already
    /// varies by everything and is left as it is.
    /// </summary>
    private static string VaryWithOrigin(Microsoft.Extensions.Primitives.StringValues current)
    {
        var tokens = current
            .SelectMany(v => (v ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (tokens.Contains("*", StringComparer.Ordinal))
        {
            return "*";
        }

        if (!tokens.Contains(OriginToken, StringComparer.OrdinalIgnoreCase))
        {
            tokens.Add(OriginToken);
        }

        return string.Join(", ", tokens);
    }
}
