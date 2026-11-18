using System.Globalization;

namespace MyApp.Api.Tests.Support;

/// <summary>Assertions on the security-related headers of a response, shared by the security tests.</summary>
internal static class SecurityAssert
{
    public const string ExpectedHsts = "max-age=31536000; includeSubDomains";

    /// <summary>The headers every response carries, whatever its status, and the ones no response carries.</summary>
    public static void HasStandardHeaders(HttpResponseMessage response)
    {
        Assert.Equal("nosniff", response.Value("X-Content-Type-Options"));
        Assert.Equal("DENY", response.Value("X-Frame-Options"));
        Assert.Equal("no-referrer", response.Value("Referrer-Policy"));
        var csp = response.Value("Content-Security-Policy");
        Assert.NotNull(csp);
        Assert.Contains("default-src 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
        Assert.Equal("same-origin", response.Value("Cross-Origin-Resource-Policy"));
        Assert.Empty(response.Values("Server"));
        Assert.Empty(response.Values("X-Powered-By"));
    }

    /// <summary>The response carries <c>Cache-Control: no-store</c> exactly (it set none of its own).</summary>
    public static void HasNoStore(HttpResponseMessage response) =>
        Assert.Equal("no-store", response.Value("Cache-Control"));

    /// <summary>Number of times <c>Origin</c> is listed in <c>Vary</c>, over all values and comma separated tokens.</summary>
    public static int VaryOriginCount(HttpResponseMessage response) =>
        response.Values("Vary").SelectMany(v => v.Split(',', StringSplitOptions.TrimEntries)).Count(t => t.Equals("Origin", StringComparison.OrdinalIgnoreCase));

    public static void HasHsts(HttpResponseMessage response) =>
        Assert.Equal(ExpectedHsts, response.Value("Strict-Transport-Security"));

    public static void HasNoHsts(HttpResponseMessage response) =>
        Assert.Empty(response.Values("Strict-Transport-Security"));

    /// <summary>No header of the CORS family at all.</summary>
    public static void HasNoCorsHeaders(HttpResponseMessage response)
    {
        var cors = AllHeaderNames(response).Where(n => n.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.True(cors.Length == 0, "Unexpected CORS headers: " + string.Join(", ", cors));
    }

    /// <summary>The origin is echoed, credentials are allowed, and the wildcard is never used.</summary>
    public static void AllowsOrigin(HttpResponseMessage response, string origin)
    {
        Assert.Equal(origin, response.Value("Access-Control-Allow-Origin"));
        Assert.Equal("true", response.Value("Access-Control-Allow-Credentials"));
        HasNoWildcard(response);
    }

    public static void DoesNotAllowOrigin(HttpResponseMessage response)
    {
        Assert.Empty(response.Values("Access-Control-Allow-Origin"));
        HasNoWildcard(response);
    }

    public static void HasNoWildcard(HttpResponseMessage response)
    {
        foreach (var name in AllHeaderNames(response).Where(n => n.StartsWith("Access-Control-Allow-", StringComparison.OrdinalIgnoreCase)))
        {
            var values = response.Values(name).SelectMany(v => v.Split(',', StringSplitOptions.TrimEntries));
            Assert.DoesNotContain("*", values);
        }
    }

    /// <summary>The value of <c>Retry-After</c> as whole seconds, which must be a positive integer.</summary>
    public static int RetryAfterSeconds(HttpResponseMessage response)
    {
        var text = response.Value("Retry-After");
        Assert.NotNull(text);
        Assert.True(int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds), $"Retry-After '{text}' is not an integer.");
        Assert.True(seconds > 0, "Retry-After must be positive.");
        return seconds;
    }

    private static IEnumerable<string> AllHeaderNames(HttpResponseMessage response) =>
        response.Headers.Select(h => h.Key).Concat(response.Content.Headers.Select(h => h.Key));
}
