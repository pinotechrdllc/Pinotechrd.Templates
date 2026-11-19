using System.Net;
using MyApp.Api.Tests.Support;

namespace MyApp.Api.Tests.Security;

public class CorsTests
{
    private const string Listed = "https://app.example.com";
    private const string Other = "https://evil.example.com";

    private const string AllRequestHeaders = "Accept, Accept-Language, Authorization, Content-Type, If-Match, If-None-Match";

    private static ApiHostFactory FactoryWithOrigin(string origin = Listed) =>
        new ApiHostFactory().WithSetting(HttpSettingKeys.AllowedOrigin(0), origin);

    // Breaks when CORS is on without a configured origin: a request carrying an Origin gets a CORS header.
    [Theory]
    [InlineData(SecurityRequests.Version)]
    [InlineData(SecurityRequests.Unknown)]
    [InlineData(SecurityRequests.Throwing)]
    public async Task NoCorsHeaderIsWrittenWhenNoOriginIsConfigured(string path)
    {
        using var factory = new ApiHostFactory();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(path, origin: Listed);

        SecurityAssert.HasNoCorsHeaders(response);
    }

    // Breaks when a preflight is answered with CORS headers while no origin is configured.
    [Fact]
    public async Task PreflightGetsNoCorsHeaderWhenNoOriginIsConfigured()
    {
        using var factory = new ApiHostFactory();
        using var client = factory.CreateClient();

        using var response = await client.PreflightAsync(Listed, "GET", AllRequestHeaders);

        SecurityAssert.HasNoCorsHeaders(response);
    }

    // Breaks when the listed origin is not echoed, or credentials are not allowed for it.
    [Theory]
    [InlineData("https://app.example.com")]
    [InlineData("http://localhost:5173")]
    public async Task ListedOriginGetsItsOwnOriginAndCredentials(string origin)
    {
        using var factory = FactoryWithOrigin(origin);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(SecurityRequests.Version, origin: origin);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        SecurityAssert.AllowsOrigin(response, origin);
    }

    // Breaks when an origin that is not listed, or only differs in scheme or port, is allowed.
    [Theory]
    [InlineData("https://evil.example.com")]
    [InlineData("http://app.example.com")]
    [InlineData("https://app.example.com:8443")]
    [InlineData("https://app.example.com.evil.test")]
    [InlineData("null")]
    public async Task UnlistedOriginGetsNoAllowOrigin(string origin)
    {
        using var factory = FactoryWithOrigin();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(SecurityRequests.Version, origin: origin);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        SecurityAssert.DoesNotAllowOrigin(response);
    }

    // Breaks when a request without an Origin header gets CORS headers or the wildcard.
    [Fact]
    public async Task RequestWithoutOriginGetsNoAllowOrigin()
    {
        using var factory = FactoryWithOrigin();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(SecurityRequests.Version);

        SecurityAssert.DoesNotAllowOrigin(response);
    }

    // Breaks when a method of the documented list is not allowed by the preflight, or the answer is not a success.
    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task PreflightFromAListedOriginAllowsTheDocumentedMethodsAndHeaders(string method)
    {
        using var factory = FactoryWithOrigin();
        using var client = factory.CreateClient();

        using var response = await client.PreflightAsync(Listed, method, AllRequestHeaders);

        Assert.True(response.IsSuccessStatusCode, $"Preflight answered {(int)response.StatusCode}.");
        SecurityAssert.AllowsOrigin(response, Listed);
        var methods = Split(response.Value("Access-Control-Allow-Methods"));
        Assert.Contains(method, methods, StringComparer.OrdinalIgnoreCase);
        var headers = Split(response.Value("Access-Control-Allow-Headers"));
        foreach (var header in Split(AllRequestHeaders))
        {
            Assert.Contains(header, headers, StringComparer.OrdinalIgnoreCase);
        }
    }

    // Breaks when X-Tenant is missing from the allowed request headers: a browser could not send the tenant before login.
    [Fact]
    public async Task PreflightFromAListedOriginAllowsTheTenantHeader()
    {
        using var factory = FactoryWithOrigin();
        using var client = factory.CreateClient();

        using var response = await client.PreflightAsync(Listed, "POST", "Content-Type, X-Tenant");

        Assert.True(response.IsSuccessStatusCode, $"Preflight answered {(int)response.StatusCode}.");
        SecurityAssert.AllowsOrigin(response, Listed);
        Assert.Contains("X-Tenant", Split(response.Value("Access-Control-Allow-Headers")), StringComparer.OrdinalIgnoreCase);
    }

    // Breaks when the preflight cache lifetime documented as 10 minutes is lost.
    [Fact]
    public async Task PreflightMayBeCachedForTenMinutes()
    {
        using var factory = FactoryWithOrigin();
        using var client = factory.CreateClient();

        using var response = await client.PreflightAsync(Listed, "POST", "Content-Type");

        Assert.Equal("600", response.Value("Access-Control-Max-Age"));
    }

    // Breaks when a method or a request header outside the documented lists is announced as allowed (the platform still
    // echoes the origin on such a preflight; the browser refuses the request because the lists lack the value).
    [Theory]
    [InlineData("TRACE", null)]
    [InlineData("GET", "X-Not-Documented")]
    public async Task PreflightForAnUndocumentedMethodOrHeaderDoesNotListIt(string method, string? requestHeaders)
    {
        using var factory = FactoryWithOrigin();
        using var client = factory.CreateClient();

        using var response = await client.PreflightAsync(Listed, method, requestHeaders);

        SecurityAssert.HasNoWildcard(response);
        Assert.DoesNotContain("TRACE", Split(response.Value("Access-Control-Allow-Methods")), StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("X-Not-Documented", Split(response.Value("Access-Control-Allow-Headers")), StringComparer.OrdinalIgnoreCase);
    }

    // Breaks when the preflight of an origin that is not listed is answered with an allowed origin.
    [Theory]
    [InlineData("https://evil.example.com")]
    [InlineData("http://app.example.com")]
    public async Task PreflightFromAnUnlistedOriginGetsNoAllowOrigin(string origin)
    {
        using var factory = FactoryWithOrigin();
        using var client = factory.CreateClient();

        using var response = await client.PreflightAsync(origin, "GET", AllRequestHeaders);

        SecurityAssert.DoesNotAllowOrigin(response);
    }

    // Breaks when the CORS middleware sits after the error handling or routing, so error responses lose the headers.
    [Theory]
    [InlineData(SecurityRequests.Throwing, 500)]
    [InlineData(SecurityRequests.Unknown, 404)]
    public async Task ErrorResponsesKeepTheCorsHeadersOfAListedOrigin(string path, int status)
    {
        using var factory = FactoryWithOrigin();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(path, origin: Listed);

        Assert.Equal(status, (int)response.StatusCode);
        SecurityAssert.AllowsOrigin(response, Listed);
    }

    // Breaks when the CORS middleware sits after the rate limiter, so the 429 answer has no CORS header.
    [Fact]
    public async Task RateLimitedResponseKeepsTheCorsHeadersOfAListedOrigin()
    {
        using var factory = FactoryWithOrigin().WithLowLimit();
        using var client = factory.CreateClient();
        await client.ExhaustAsync("10.2.2.2");

        using var response = await client.SendAsync(SecurityRequests.Version, "10.2.2.2", Listed);

        Assert.Equal(429, (int)response.StatusCode);
        SecurityAssert.AllowsOrigin(response, Listed);
    }

    // Breaks when an unlisted origin gets CORS headers on an error, or any answer ever carries the wildcard.
    [Theory]
    [InlineData(SecurityRequests.Throwing)]
    [InlineData(SecurityRequests.Unknown)]
    [InlineData("/health/live")]
    public async Task UnlistedOriginGetsNoAllowOriginOnErrorsAndHealth(string path)
    {
        using var factory = FactoryWithOrigin();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(path, origin: Other);

        SecurityAssert.DoesNotAllowOrigin(response);
    }

    // Breaks when a header of the documented exposed list is not exposed to the browser client.
    [Fact]
    public async Task ResponseExposesRetryAfterETagAndLocationToAListedOrigin()
    {
        using var factory = FactoryWithOrigin();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(SecurityRequests.Version, origin: Listed);

        var exposed = Split(response.Value("Access-Control-Expose-Headers"));
        foreach (var header in new[] { "Retry-After", "ETag", "Location" })
        {
            Assert.Contains(header, exposed, StringComparer.OrdinalIgnoreCase);
        }
    }

    // Breaks when the 429 answer does not expose Retry-After, the one header a client needs from it.
    [Fact]
    public async Task RateLimitedResponseExposesRetryAfterToAListedOrigin()
    {
        using var factory = FactoryWithOrigin().WithLowLimit();
        using var client = factory.CreateClient();
        await client.ExhaustAsync("10.2.2.3");

        using var response = await client.SendAsync(SecurityRequests.Version, "10.2.2.3", Listed);

        Assert.Contains("Retry-After", Split(response.Value("Access-Control-Expose-Headers")), StringComparer.OrdinalIgnoreCase);
        SecurityAssert.RetryAfterSeconds(response);
    }

    private static string[] Split(string? value) =>
        value is null ? [] : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
