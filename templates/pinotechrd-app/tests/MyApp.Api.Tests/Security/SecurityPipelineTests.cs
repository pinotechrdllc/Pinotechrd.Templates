using System.Net;
using MyApp.Api.Tests.Support;

namespace MyApp.Api.Tests.Security;

// The pieces hold together in the one pipeline of the real Program.
public class SecurityPipelineTests
{
    private const string Listed = "https://app.example.com";
    private const string Proxy = "10.9.9.9";

    // Breaks when the order of the pipeline loses any of: security headers or CORS before the error handling, HSTS on
    // errors, or the problem body of the error handler.
    [Fact]
    public async Task ErrorFromAListedOriginOverHttpsToAPublicHostInProductionCarriesEverything()
    {
        using var factory = ApiHostFactory.ForEnvironment("Production")
            .WithSetting(HttpSettingKeys.AllowedOrigin(0), Listed);
        using var client = factory.ClientFor("https://api.example.com");

        using var response = await client.SendAsync(SecurityRequests.Throwing, origin: Listed);

        await ProblemAssert.IsProblemAsync(response, 500, "internal_error");
        SecurityAssert.HasStandardHeaders(response);
        SecurityAssert.HasHsts(response);
        SecurityAssert.AllowsOrigin(response, Listed);
    }

    // Breaks when the rate limiter answers before the forwarded scheme, the security headers or CORS are applied.
    [Fact]
    public async Task RateLimitedRequestBehindAProxyCarriesProblemSecurityHeadersHstsAndCors()
    {
        using var factory = ApiHostFactory.ForEnvironment("Production")
            .WithLowLimit()
            .WithSetting(HttpSettingKeys.AllowedOrigin(0), Listed)
            .WithSetting(HttpSettingKeys.KnownProxy(0), Proxy);
        using var client = factory.ClientFor("http://api.example.com");
        await client.ExhaustAsync(Proxy, forwardedFor: "203.0.113.1", forwardedProto: "https");

        using var response = await client.SendAsync(
            SecurityRequests.Version, Proxy, Listed, forwardedFor: "198.51.100.5, 203.0.113.1", forwardedProto: "https");

        await ProblemAssert.IsProblemAsync(response, 429, "rate_limited");
        SecurityAssert.RetryAfterSeconds(response);
        SecurityAssert.HasStandardHeaders(response);
        SecurityAssert.HasHsts(response);
        SecurityAssert.AllowsOrigin(response, Listed);
    }

    // Breaks when an unknown route in Production answers something other than the problem 404 with all the headers.
    [Fact]
    public async Task UnknownRouteInProductionIsAProblemWithSecurityHeaders()
    {
        using var factory = ApiHostFactory.ForEnvironment("Production")
            .WithSetting(HttpSettingKeys.AllowedOrigin(0), Listed);
        using var client = factory.ClientFor("https://api.example.com");

        using var response = await client.SendAsync(SecurityRequests.Unknown, origin: Listed);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        SecurityAssert.HasStandardHeaders(response);
        SecurityAssert.HasHsts(response);
        SecurityAssert.AllowsOrigin(response, Listed);
    }
}
