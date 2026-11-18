using System.Net;
using MyApp.Api.Tests.Support;

namespace MyApp.Api.Tests.Security;

// Cache-Control, and Vary: Origin. (Cross-Origin-Resource-Policy is part of the
// shared SecurityAssert.HasStandardHeaders used by SecurityHeadersTests.)
public class ResponseHeaderPolicyTests
{
    private const string Listed = "https://app.example.com";
    private const string Second = "https://admin.example.com";

    private static ApiHostFactory FactoryWithOrigin() =>
        new ApiHostFactory().WithSetting(HttpSettingKeys.AllowedOrigin(0), Listed);

    // Breaks when a success or routing miss answer lacks Cache-Control: no-store.
    [Theory]
    [InlineData(SecurityRequests.Version, 200)]
    [InlineData(SecurityRequests.Unknown, 404)]
    public async Task ApiResponsesAreNotStored(string path, int status)
    {
        using var factory = new ApiHostFactory();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(path);

        Assert.Equal(status, (int)response.StatusCode);
        SecurityAssert.HasNoStore(response);
    }

    // Breaks when the rejection written by the rate limiter lacks Cache-Control: no-store.
    [Fact]
    public async Task RateLimitedResponseIsNotStored()
    {
        using var factory = new ApiHostFactory().WithLowLimit();
        using var client = factory.CreateClient();
        await client.ExhaustAsync("10.5.0.1");

        using var response = await client.SendAsync(SecurityRequests.Version, "10.5.0.1");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        SecurityAssert.HasNoStore(response);
    }

    // Breaks when no-store overwrites the Cache-Control an endpoint chose for itself.
    [Fact]
    public async Task CacheControlSetByTheEndpointIsKept()
    {
        using var factory = new ApiHostFactory();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(ProblemFixtureEndpoint.Cached);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ProblemFixtureEndpoint.CachedControl, response.Value("Cache-Control"));
    }

    // Breaks when the error handler's 500 or a health probe can be cached.
    [Theory]
    [InlineData(SecurityRequests.Throwing)]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task ServerErrorAndHealthResponsesCarryNoStore(string path)
    {
        using var factory = new ApiHostFactory();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(path);

        Assert.Contains("no-store", response.Value("Cache-Control"), StringComparison.Ordinal);
    }

    // Breaks when the OpenAPI document of Development can be cached (it would be stale after a change).
    [Fact]
    public async Task OpenApiDocumentInDevelopmentIsNotStored()
    {
        using var factory = new ApiHostFactory();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Value("Cache-Control"), StringComparison.Ordinal);
    }

    // Breaks when Vary: Origin is only written for a request from a listed origin, or is lost on a path or status.
    [Theory]
    [InlineData(SecurityRequests.Version, Listed)]
    [InlineData(SecurityRequests.Version, "https://evil.example.com")]
    [InlineData(SecurityRequests.Version, null)]
    [InlineData(SecurityRequests.Unknown, Listed)]
    [InlineData(SecurityRequests.Unknown, null)]
    [InlineData(SecurityRequests.Throwing, Listed)]
    [InlineData(SecurityRequests.Throwing, null)]
    [InlineData("/health/live", null)]
    public async Task VaryOriginIsOnEveryResponseWhileCorsIsConfigured(string path, string? origin)
    {
        using var factory = FactoryWithOrigin();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(path, origin: origin);

        Assert.Equal(1, SecurityAssert.VaryOriginCount(response));
    }

    // Breaks when the 429 answer lacks Vary: Origin.
    [Fact]
    public async Task VaryOriginIsOnTheRateLimitedResponse()
    {
        using var factory = FactoryWithOrigin().WithLowLimit();
        using var client = factory.CreateClient();
        await client.ExhaustAsync("10.5.0.2");

        using var response = await client.SendAsync(SecurityRequests.Version, "10.5.0.2", Listed);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(1, SecurityAssert.VaryOriginCount(response));
    }

    // Breaks when Vary: Origin is only written for a preflight, or is lost on its answer.
    [Fact]
    public async Task VaryOriginIsOnAPreflightResponse()
    {
        using var factory = FactoryWithOrigin();
        using var client = factory.CreateClient();

        using var response = await client.PreflightAsync(Listed, "GET");

        Assert.Equal(1, SecurityAssert.VaryOriginCount(response));
    }

    // Breaks when Vary: Origin is written although CORS is off.
    [Theory]
    [InlineData(SecurityRequests.Version, Listed)]
    [InlineData(SecurityRequests.Unknown, null)]
    [InlineData(SecurityRequests.Throwing, Listed)]
    public async Task VaryOriginIsAbsentWhenCorsIsOff(string path, string? origin)
    {
        using var factory = new ApiHostFactory();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(path, origin: origin);

        Assert.Equal(0, SecurityAssert.VaryOriginCount(response));
    }

    // Breaks when Origin is added next to an Origin the response already varies by.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResponseThatAlreadyVariesByOriginGetsNoSecondValue(bool twoOrigins)
    {
        using var factory = FactoryWithOrigin();
        if (twoOrigins)
        {
            factory.WithSetting(HttpSettingKeys.AllowedOrigin(1), Second);
        }

        using var client = factory.CreateClient();

        using var response = await client.SendAsync(ProblemFixtureEndpoint.VariesByOrigin, origin: Listed);

        Assert.Equal(1, SecurityAssert.VaryOriginCount(response));
    }

    // Breaks when the Vary an endpoint wrote is replaced instead of extended with Origin.
    [Fact]
    public async Task OriginIsAddedNextToAnotherVaryValue()
    {
        using var factory = FactoryWithOrigin();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(ProblemFixtureEndpoint.VariesByEncoding, origin: Listed);

        var tokens = response.Values("Vary").SelectMany(v => v.Split(',', StringSplitOptions.TrimEntries)).ToArray();
        Assert.Contains("Accept-Encoding", tokens, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(1, SecurityAssert.VaryOriginCount(response));
    }

    // Breaks when more than one listed origin makes the platform and the pipeline both write Origin.
    [Fact]
    public async Task VaryOriginIsNotDuplicatedWithSeveralListedOrigins()
    {
        using var factory = FactoryWithOrigin().WithSetting(HttpSettingKeys.AllowedOrigin(1), Second);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(SecurityRequests.Version, origin: Second);

        SecurityAssert.AllowsOrigin(response, Second);
        Assert.Equal(1, SecurityAssert.VaryOriginCount(response));
    }

    public static TheoryData<int, string, string?> VaryCombinations()
    {
        var data = new TheoryData<int, string, string?>();
        foreach (var origins in new[] { 1, 2 })
        {
            foreach (var path in new[] { SecurityRequests.Version, ProblemFixtureEndpoint.VariesByOrigin, ProblemFixtureEndpoint.VariesByEncoding })
            {
                foreach (var origin in new string?[] { null, Listed, "https://evil.example.com" })
                {
                    data.Add(origins, path, origin);
                }
            }
        }

        return data;
    }

    // Breaks when, in any mix of listed origins, endpoint Vary and request Origin, Origin is missing, repeated, or
    // replaces a value the endpoint wrote.
    [Theory]
    [MemberData(nameof(VaryCombinations))]
    public async Task VaryCarriesOriginExactlyOnceAndKeepsOtherValues(int listedOrigins, string path, string? origin)
    {
        using var factory = FactoryWithOrigin();
        if (listedOrigins == 2)
        {
            factory.WithSetting(HttpSettingKeys.AllowedOrigin(1), Second);
        }

        using var client = factory.CreateClient();

        using var response = await client.SendAsync(path, origin: origin);

        Assert.Equal(1, SecurityAssert.VaryOriginCount(response));
        if (path == ProblemFixtureEndpoint.VariesByEncoding)
        {
            var tokens = response.Values("Vary").SelectMany(v => v.Split(',', StringSplitOptions.TrimEntries));
            Assert.Contains("Accept-Encoding", tokens, StringComparer.OrdinalIgnoreCase);
        }
    }
}
