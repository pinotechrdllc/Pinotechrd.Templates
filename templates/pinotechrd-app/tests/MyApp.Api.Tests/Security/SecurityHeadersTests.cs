using MyApp.Api.Tests.Support;

namespace MyApp.Api.Tests.Security;

// Criteria 1 to 3 of the HTTP security change.
public class SecurityHeadersTests
{
    private const string Public = "https://api.example.com";

    // Breaks when a status path (success, routing miss, handled exception, health) is not covered by the headers middleware.
    [Theory]
    [InlineData(SecurityRequests.Version, 200)]
    [InlineData(SecurityRequests.Unknown, 404)]
    [InlineData("/no-such-route-at-all", 404)]
    [InlineData(SecurityRequests.Throwing, 500)]
    [InlineData("/health/live", 200)]
    [InlineData("/health/ready", 200)]
    public async Task StandardSecurityHeadersAreOnEveryKindOfResponse(string path, int status)
    {
        using var factory = new ApiHostFactory();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(path);

        Assert.Equal(status, (int)response.StatusCode);
        SecurityAssert.HasStandardHeaders(response);
    }

    // Breaks when the rate limiter's own 429 answer is written without the security headers.
    [Fact]
    public async Task StandardSecurityHeadersAreOnARateLimitedResponse()
    {
        using var factory = new ApiHostFactory().WithLowLimit();
        using var client = factory.CreateClient();
        await client.ExhaustAsync("10.1.1.1");

        using var response = await client.SendAsync(SecurityRequests.Version, "10.1.1.1");

        Assert.Equal(429, (int)response.StatusCode);
        SecurityAssert.HasStandardHeaders(response);
    }

    // Breaks when HSTS is dropped for a public HTTPS host in a non-Development environment, or is not sent on a fixed value.
    [Theory]
    [InlineData("Production", "https://api.example.com")]
    [InlineData("Production", "https://api.example.com:8443")]
    [InlineData("Production", "https://localhost.example.com")]
    [InlineData("Production", "https://notlocalhost.example.com")]
    [InlineData("Production", "https://203.0.113.10")]
    [InlineData("Staging", "https://api.example.com")]
    public async Task HstsIsSentForHttpsToAPublicHostOutsideDevelopment(string environment, string baseAddress)
    {
        using var factory = ApiHostFactory.ForEnvironment(environment);
        using var client = factory.ClientFor(baseAddress);

        using var response = await client.SendAsync(SecurityRequests.Version);

        Assert.Equal(200, (int)response.StatusCode);
        SecurityAssert.HasHsts(response);
    }

    // Breaks when HSTS is sent over plain HTTP, for a local host name or loopback address, or in Development.
    [Theory]
    [InlineData("Production", "http://api.example.com")]
    [InlineData("Production", "https://localhost")]
    [InlineData("Production", "https://app.localhost")]
    [InlineData("Production", "https://127.0.0.1")]
    [InlineData("Production", "https://127.0.0.2")]
    [InlineData("Production", "https://[::1]")]
    [InlineData("Development", "https://api.example.com")]
    public async Task HstsIsNotSentOverHttpToALocalHostOrInDevelopment(string environment, string baseAddress)
    {
        using var factory = ApiHostFactory.ForEnvironment(environment);
        using var client = factory.ClientFor(baseAddress);

        using var response = await client.SendAsync(SecurityRequests.Version);

        Assert.Equal(200, (int)response.StatusCode);
        SecurityAssert.HasNoHsts(response);
        SecurityAssert.HasStandardHeaders(response);
    }

    // Breaks when HSTS is written on error responses of a public HTTPS host only by the success path.
    [Theory]
    [InlineData(SecurityRequests.Unknown)]
    [InlineData(SecurityRequests.Throwing)]
    public async Task HstsIsAlsoSentOnErrorResponses(string path)
    {
        using var factory = ApiHostFactory.ForEnvironment("Production");
        using var client = factory.ClientFor(Public);

        using var response = await client.SendAsync(path);

        Assert.False(response.IsSuccessStatusCode);
        SecurityAssert.HasHsts(response);
    }

    // Breaks when a Server or X-Powered-By header is added. Vacuous for Server and X-Powered-By on the in-memory
    // test server, which never writes either; it only guards against the application setting them itself.
    [Fact]
    public async Task NoServerOrPoweredByHeaderIsSent()
    {
        using var factory = new ApiHostFactory();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(SecurityRequests.Version);

        Assert.Empty(response.Values("Server"));
        Assert.Empty(response.Values("X-Powered-By"));
    }
}
