using System.Net;
using MyApp.Api.Tests.Support;

namespace MyApp.Api.Tests.Security;

// Criteria 13 to 16 of the HTTP security change. The limit is two requests per hour, so a client is identified by
// how many of its requests are answered 200 before the first 429.
public class ForwardedHeadersTests
{
    private const string Proxy = "10.9.9.9";
    private const string Public = "https://api.example.com";
    private const string Http = "http://api.example.com";

    private static ApiHostFactory FactoryBehindProxy(string environment = "Development") =>
        ApiHostFactory.ForEnvironment(environment).WithLowLimit().WithSetting(HttpSettingKeys.KnownProxy(0), Proxy);

    // Breaks when forwarded headers are processed without a configured proxy (including the platform default that
    // trusts loopback): a client could pick its own partition by changing the header.
    [Theory]
    [InlineData("203.0.113.50")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public async Task ForwardedForIsIgnoredWhenNoProxyIsConfigured(string remote)
    {
        using var factory = new ApiHostFactory().WithLowLimit();
        using var client = factory.CreateClient();
        await client.ExhaustAsync(remote, forwardedFor: "198.51.100.1");

        using var changed = await client.SendAsync(SecurityRequests.Version, remote, forwardedFor: "198.51.100.2");
        using var spoofed = await client.SendAsync(SecurityRequests.Version, remote, forwardedFor: "198.51.100.3, 198.51.100.4");
        using var none = await client.SendAsync(SecurityRequests.Version, remote);

        Assert.Equal(HttpStatusCode.TooManyRequests, changed.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, spoofed.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, none.StatusCode);
    }

    // Breaks when X-Forwarded-Proto is trusted from any sender: HSTS must not appear for a request that merely claims HTTPS.
    [Fact]
    public async Task ForwardedProtoIsIgnoredWhenNoProxyIsConfigured()
    {
        using var factory = ApiHostFactory.ForEnvironment("Production");
        using var client = factory.ClientFor(Http);

        using var response = await client.SendAsync(SecurityRequests.Version, "203.0.113.50", forwardedProto: "https");

        SecurityAssert.HasNoHsts(response);
    }

    // Breaks when the limiter partitions on the proxy address instead of the forwarded client address.
    [Fact]
    public async Task DifferentForwardedClientsBehindAKnownProxyAreLimitedIndependently()
    {
        using var factory = FactoryBehindProxy();
        using var client = factory.CreateClient();
        await client.ExhaustAsync(Proxy, forwardedFor: "203.0.113.1");

        using var limited = await client.SendAsync(SecurityRequests.Version, Proxy, forwardedFor: "203.0.113.1");
        using var other = await client.SendAsync(SecurityRequests.Version, Proxy, forwardedFor: "203.0.113.2");

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    // Breaks when forwarded headers are ignored although the sender is the configured proxy, so all clients share the proxy's limit.
    [Fact]
    public async Task ForwardedForIsHonoredFromAKnownProxy()
    {
        using var factory = FactoryBehindProxy();
        using var client = factory.CreateClient();
        await client.ExhaustAsync(Proxy, forwardedFor: "203.0.113.1");

        using var fresh = await client.SendAsync(SecurityRequests.Version, Proxy, forwardedFor: "203.0.113.77");

        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
    }

    // Breaks when the left-most (client controlled) address is trusted: the client prepends spoofed values and the proxy appends the real one.
    [Fact]
    public async Task AddressesPrependedByTheClientAreNotTrusted()
    {
        using var factory = FactoryBehindProxy();
        using var client = factory.CreateClient();
        await client.ExhaustAsync(Proxy, forwardedFor: "198.51.100.1, 203.0.113.1");

        using var sameRealClient = await client.SendAsync(SecurityRequests.Version, Proxy, forwardedFor: "198.51.100.2, 203.0.113.1");
        using var otherRealClient = await client.SendAsync(SecurityRequests.Version, Proxy, forwardedFor: "198.51.100.2, 203.0.113.2");

        Assert.Equal(HttpStatusCode.TooManyRequests, sameRealClient.StatusCode);
        Assert.Equal(HttpStatusCode.OK, otherRealClient.StatusCode);
    }

    // Breaks when a forwarded header from a sender that is not the proxy is honored, even though a proxy is configured.
    [Fact]
    public async Task ForwardedForFromAnUnknownSenderIsIgnoredEvenWithAProxyConfigured()
    {
        using var factory = FactoryBehindProxy();
        using var client = factory.CreateClient();
        await client.ExhaustAsync("203.0.113.200", forwardedFor: "203.0.113.1");

        using var changed = await client.SendAsync(SecurityRequests.Version, "203.0.113.200", forwardedFor: "203.0.113.2");

        Assert.Equal(HttpStatusCode.TooManyRequests, changed.StatusCode);
    }

    // Breaks when a configured network is not used to recognise the proxy.
    [Fact]
    public async Task ForwardedForIsHonoredFromAProxyInAKnownNetwork()
    {
        using var factory = new ApiHostFactory().WithLowLimit().WithSetting(HttpSettingKeys.KnownNetwork(0), "10.0.0.0/8");
        using var client = factory.CreateClient();
        await client.ExhaustAsync("10.20.30.40", forwardedFor: "203.0.113.1");

        using var limited = await client.SendAsync(SecurityRequests.Version, "10.20.30.41", forwardedFor: "203.0.113.1");
        using var other = await client.SendAsync(SecurityRequests.Version, "10.20.30.40", forwardedFor: "203.0.113.2");

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    // Breaks when a chain of several trusted hops is cut after the first one, so the inner proxy's address is the client.
    [Fact]
    public async Task ChainOfTrustedProxiesResolvesToTheFirstUntrustedAddressFromTheRight()
    {
        using var factory = new ApiHostFactory().WithLowLimit()
            .WithSetting(HttpSettingKeys.KnownNetwork(0), "10.0.0.0/8");
        using var client = factory.CreateClient();
        await client.ExhaustAsync("10.0.0.1", forwardedFor: "203.0.113.1, 10.0.0.2");

        using var same = await client.SendAsync(SecurityRequests.Version, "10.0.0.1", forwardedFor: "203.0.113.1, 10.0.0.3");
        using var other = await client.SendAsync(SecurityRequests.Version, "10.0.0.1", forwardedFor: "203.0.113.9, 10.0.0.2");

        Assert.Equal(HttpStatusCode.TooManyRequests, same.StatusCode);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    // Breaks when X-Forwarded-Proto from a known proxy is not applied, so HSTS is lost behind a TLS-terminating proxy.
    [Fact]
    public async Task ForwardedProtoHttpsFromAKnownProxyMakesTheRequestCountAsHttps()
    {
        using var factory = FactoryBehindProxy("Production");
        using var client = factory.ClientFor(Http);

        using var response = await client.SendAsync(SecurityRequests.Version, Proxy, forwardedProto: "https");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        SecurityAssert.HasHsts(response);
    }

    // Breaks when the scheme is forced to HTTPS whenever a proxy is configured, whatever it forwards.
    [Fact]
    public async Task ForwardedProtoHttpFromAKnownProxyStaysHttp()
    {
        using var factory = FactoryBehindProxy("Production");
        using var client = factory.ClientFor(Public);

        using var response = await client.SendAsync(SecurityRequests.Version, Proxy, forwardedProto: "http");

        SecurityAssert.HasNoHsts(response);
    }

    // Breaks when X-Forwarded-Proto is honored from a sender that is not a known proxy although a proxy is configured.
    [Fact]
    public async Task ForwardedProtoFromAnUnknownSenderIsIgnoredEvenWithAProxyConfigured()
    {
        using var factory = FactoryBehindProxy("Production");
        using var client = factory.ClientFor(Http);

        using var response = await client.SendAsync(SecurityRequests.Version, "203.0.113.200", forwardedProto: "https");

        SecurityAssert.HasNoHsts(response);
    }
}
