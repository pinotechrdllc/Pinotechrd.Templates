using System.Net;
using MyApp.Api.Tests.Support;

namespace MyApp.Api.Tests.Security;

// The rate limit, plus the grouping of IPv6 clients documented in the README.
public class RateLimitingTests
{
    // Breaks when the limit is not applied, is applied one request late, or the answer is not the documented problem.
    [Fact]
    public async Task ThirdRequestOfAClientIsRejectedWithRateLimitedProblemAndRetryAfter()
    {
        using var factory = new ApiHostFactory().WithLowLimit();
        using var client = factory.CreateClient();

        using var first = await client.SendAsync(SecurityRequests.Version, "10.3.0.1");
        using var second = await client.SendAsync(SecurityRequests.Version, "10.3.0.1");
        using var third = await client.SendAsync(SecurityRequests.Version, "10.3.0.1");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        await ProblemAssert.IsProblemAsync(third, 429, "rate_limited");
        var seconds = SecurityAssert.RetryAfterSeconds(third);
        Assert.InRange(seconds, 1, 3600);
    }

    // Breaks when a client that is limited keeps being limited only for some paths, or the limit applies to one route only.
    [Fact]
    public async Task LimitIsSharedByAllApiRoutesOfAClient()
    {
        using var factory = new ApiHostFactory().WithLowLimit();
        using var client = factory.CreateClient();

        using var first = await client.SendAsync(SecurityRequests.Version, "10.3.0.2");
        using var second = await client.SendAsync(SecurityRequests.Unknown, "10.3.0.2");
        using var third = await client.SendAsync(SecurityRequests.Throwing, "10.3.0.2");

        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
        await ProblemAssert.IsProblemAsync(third, 429, "rate_limited");
    }

    // Breaks when all clients share one counter (a global rather than per-client partition).
    [Fact]
    public async Task ClientsAreLimitedIndependently()
    {
        using var factory = new ApiHostFactory().WithLowLimit();
        using var client = factory.CreateClient();
        await client.ExhaustAsync("10.3.1.1");

        using var limited = await client.SendAsync(SecurityRequests.Version, "10.3.1.1");
        using var other = await client.SendAsync(SecurityRequests.Version, "10.3.1.2");

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    // Breaks when rejected requests of one client affect the counters of another client.
    [Fact]
    public async Task RejectionsOfOneClientDoNotConsumeThePermitsOfAnother()
    {
        using var factory = new ApiHostFactory().WithLowLimit();
        using var client = factory.CreateClient();
        await client.ExhaustAsync("10.3.2.1");
        for (var i = 0; i < 5; i++)
        {
            using var rejected = await client.SendAsync(SecurityRequests.Version, "10.3.2.1");
            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        }

        await client.ExhaustAsync("10.3.2.2");
    }

    // Breaks when the health probes fall under the limit, for a client that is fresh or one that is already limited.
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task HealthEndpointsAreNeverLimited(string path)
    {
        using var factory = new ApiHostFactory().WithLowLimit();
        using var client = factory.CreateClient();

        for (var i = 0; i < 8; i++)
        {
            using var fresh = await client.SendAsync(path, "10.3.3.1");
            Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        }

        await client.ExhaustAsync("10.3.3.2");
        using var limited = await client.SendAsync(SecurityRequests.Version, "10.3.3.2");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        for (var i = 0; i < 8; i++)
        {
            using var health = await client.SendAsync(path, "10.3.3.2");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }
    }

    // Breaks when the health probes spend permits of the client, so a prober would exhaust its API calls.
    [Fact]
    public async Task HealthRequestsDoNotUseUpThePermitsOfTheClient()
    {
        using var factory = new ApiHostFactory().WithLowLimit();
        using var client = factory.CreateClient();
        for (var i = 0; i < 5; i++)
        {
            using var health = await client.SendAsync("/health/live", "10.3.4.1");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }

        await client.ExhaustAsync("10.3.4.1");
    }

    // Breaks when the limit is applied off by one: a limit of one allows exactly one request.
    [Fact]
    public async Task LimitOfOneAllowsExactlyOneRequest()
    {
        using var factory = new ApiHostFactory()
            .WithSetting(HttpSettingKeys.PermitLimit, "1")
            .WithSetting(HttpSettingKeys.Window, "01:00:00");
        using var client = factory.CreateClient();

        using var first = await client.SendAsync(SecurityRequests.Version, "10.3.5.1");
        using var second = await client.SendAsync(SecurityRequests.Version, "10.3.5.1");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
    }

    // Breaks when two factories share limiter state: each host starts with fresh counters.
    [Fact]
    public async Task EachHostStartsWithFreshCounters()
    {
        using (var first = new ApiHostFactory().WithLowLimit())
        {
            using var client = first.CreateClient();
            await client.ExhaustAsync("10.3.6.1");
            using var limited = await client.SendAsync(SecurityRequests.Version, "10.3.6.1");
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        }

        using var second = new ApiHostFactory().WithLowLimit();
        using var secondClient = second.CreateClient();
        await secondClient.ExhaustAsync("10.3.6.1");
    }

    // Breaks when IPv6 clients are counted by full address, so one client rotating inside its /64 gets fresh limits.
    [Fact]
    public async Task Ipv6ClientsOfTheSame64NetworkShareOneLimit()
    {
        using var factory = new ApiHostFactory().WithLowLimit();
        using var client = factory.CreateClient();
        using var first = await client.SendAsync(SecurityRequests.Version, "2001:db8:1:2::1");
        using var second = await client.SendAsync(SecurityRequests.Version, "2001:db8:1:2:aaaa::2");

        using var third = await client.SendAsync(SecurityRequests.Version, "2001:db8:1:2:ffff:ffff:ffff:ffff");

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
    }

    // Breaks when the /64 grouping is too coarse, so unrelated IPv6 networks share a limit.
    [Fact]
    public async Task Ipv6ClientsOfDifferent64NetworksAreLimitedIndependently()
    {
        using var factory = new ApiHostFactory().WithLowLimit();
        using var client = factory.CreateClient();
        await client.ExhaustAsync("2001:db8:1:2::1");

        using var other = await client.SendAsync(SecurityRequests.Version, "2001:db8:1:3::1");

        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    // Breaks when DisableRateLimiting() on an endpoint is ignored: it must answer 200 past the limit, also for a limited client.
    [Fact]
    public async Task EndpointMarkedWithDisableRateLimitingIsNeverLimited()
    {
        using var factory = new ApiHostFactory().WithLowLimit();
        using var client = factory.CreateClient();
        await client.ExhaustAsync("10.3.7.1");

        using var limited = await client.SendAsync(SecurityRequests.Version, "10.3.7.1");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        for (var i = 0; i < 6; i++)
        {
            using var response = await client.SendAsync(ProblemFixtureEndpoint.Unlimited, "10.3.7.1");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }
}
