using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Support;

namespace MyApp.Api.Tests.Security;

// Criteria 32 to 38 of the HTTP security change: strict validation of trusted proxies and networks, what that means for
// requests, and the health probes under a fallback authorization policy.
public class ProxyNetworkValidationTests
{
    // Breaks when an IPv6 network that overlaps ::/96 or the IPv4-mapped range ::ffff:0:0/96 is trusted, whatever its width.
    [Theory]
    [InlineData("::/0")]
    [InlineData("::/32")]
    [InlineData("::/64")]
    [InlineData("::/80")]
    [InlineData("::/96")]
    [InlineData("::/127")]
    [InlineData("::1/128")]
    [InlineData("::fffe:0:0/95")]
    [InlineData("::ffff:0:0/96")]
    [InlineData("::ffff:10.0.0.0/104")]
    public void NetworkOverlappingTheCompatibleOrMappedRangeStopsTheHost(string network)
    {
        using var factory = new ApiHostFactory().WithSetting(HttpSettingKeys.KnownNetwork(0), network);

        StartupFailure.AssertOptionsInvalid(factory, HttpSettingKeys.KnownNetworksName, network);
    }

    // Breaks when a network that is inside, or contains, the Teredo or 6to4 ranges is trusted.
    [Theory]
    [InlineData("2001::/32")]
    [InlineData("2001:0:abcd::/48")]
    [InlineData("2001::/16")]
    [InlineData("2000::/3")]
    [InlineData("2002::/16")]
    [InlineData("2002::/15")]
    [InlineData("2002:c000:0204::/48")]
    public void NetworkOverlappingTeredoOr6To4StopsTheHost(string network)
    {
        using var factory = new ApiHostFactory().WithSetting(HttpSettingKeys.KnownNetwork(0), network);

        StartupFailure.AssertOptionsInvalid(factory, HttpSettingKeys.KnownNetworksName, network);
    }

    // Breaks when IPv4 text that the platform parser reads differently (leading zeros, hex, short form) is accepted in a network.
    [Theory]
    [InlineData("010.0.0.0/8")]
    [InlineData("0x0A.0.0.0/8")]
    [InlineData("10.0.0.05/32")]
    [InlineData("10.0.0/8")]
    public void NetworkWithNonCanonicalIpv4TextStopsTheHost(string network)
    {
        using var factory = new ApiHostFactory().WithSetting(HttpSettingKeys.KnownNetwork(0), network);

        StartupFailure.AssertOptionsInvalid(factory, HttpSettingKeys.KnownNetworksName, network);
    }

    // Breaks when IPv4 text that the platform parser reads differently (010.0.0.5 is 8.0.0.5) is accepted as a proxy.
    [Theory]
    [InlineData("010.0.0.5")]
    [InlineData("0x0A.0.0.5")]
    [InlineData("10.0.0.05")]
    [InlineData("0010.0.0.1")]
    [InlineData("10.0.0")]
    [InlineData("256.0.0.1")]
    public void ProxyWithNonCanonicalIpv4TextStopsTheHost(string proxy)
    {
        using var factory = new ApiHostFactory().WithSetting(HttpSettingKeys.KnownProxy(0), proxy);

        StartupFailure.AssertOptionsInvalid(factory, HttpSettingKeys.KnownProxiesName, proxy);
    }

    // Breaks when an IPv4-mapped IPv6 proxy is accepted, or the message does not tell the operator the IPv4 form to write.
    [Theory]
    [InlineData("::ffff:10.0.0.5")]
    [InlineData("::ffff:a00:5")]
    public void IPv4MappedProxyStopsTheHostAndNamesTheIpv4Form(string proxy)
    {
        using var factory = new ApiHostFactory().WithSetting(HttpSettingKeys.KnownProxy(0), proxy);

        var message = StartupFailure.AssertOptionsInvalid(factory, HttpSettingKeys.KnownProxiesName, proxy);

        Assert.Contains("10.0.0.5", message.Replace(proxy, string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    // Breaks when a legitimate network is refused by the stricter rules (ranges next to the forbidden ones included).
    [Theory]
    [InlineData("10.0.0.0/8")]
    [InlineData("192.168.1.0/24")]
    [InlineData("2001:db8::/32")]
    [InlineData("fd00::/32")]
    [InlineData("fd12:3456:789a::/48")]
    [InlineData("64:ff9b::/96")]
    [InlineData("::fffe:0:0/96")]
    [InlineData("0:0:0:1::/64")]
    [InlineData("2001:1::/32")]
    [InlineData("fe80::/64")]
    public async Task AcceptedNetworkStartsTheHost(string network)
    {
        using var factory = new ApiHostFactory().WithSetting(HttpSettingKeys.KnownNetwork(0), network);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(SecurityRequests.Version);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Breaks when a legitimate proxy address is refused by the stricter rules.
    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("2001:db8::1")]
    [InlineData("::1")]
    [InlineData("fd00::5")]
    public async Task AcceptedProxyStartsTheHost(string proxy)
    {
        using var factory = new ApiHostFactory().WithSetting(HttpSettingKeys.KnownProxy(0), proxy);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(SecurityRequests.Version);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Breaks when an IPv4 client, reported plain or IPv4-mapped, is matched by an IPv6 trusted network, so its
    // X-Forwarded-For would be believed and it could pick its own rate-limit partition.
    [Theory]
    [InlineData("203.0.113.7")]
    [InlineData("::ffff:203.0.113.7")]
    public async Task Ipv4ClientIsNotTrustedByAnIpv6NetworkForForwardedFor(string remote)
    {
        using var factory = new ApiHostFactory().WithLowLimit().WithSetting(HttpSettingKeys.KnownNetwork(0), "2001:db8::/32");
        using var client = factory.CreateClient();
        await client.ExhaustAsync(remote, forwardedFor: "198.51.100.1");

        using var changed = await client.SendAsync(SecurityRequests.Version, remote, forwardedFor: "198.51.100.2");

        Assert.Equal(HttpStatusCode.TooManyRequests, changed.StatusCode);
    }

    // Breaks when the same client can make the request count as HTTPS, which would add HSTS.
    [Theory]
    [InlineData("203.0.113.7")]
    [InlineData("::ffff:203.0.113.7")]
    public async Task Ipv4ClientIsNotTrustedByAnIpv6NetworkForForwardedProto(string remote)
    {
        using var factory = ApiHostFactory.ForEnvironment("Production").WithSetting(HttpSettingKeys.KnownNetwork(0), "2001:db8::/32");
        using var client = factory.ClientFor("http://api.example.com");

        using var response = await client.SendAsync(SecurityRequests.Version, remote, forwardedProto: "https");

        SecurityAssert.HasNoHsts(response);
    }

    // Positive control: a proxy inside the configured IPv6 network is trusted, so the two checks above are not
    // passing only because forwarded headers never work.
    [Fact]
    public async Task ProxyInsideTheIpv6NetworkIsTrustedForForwardedForAndProto()
    {
        using var factory = ApiHostFactory.ForEnvironment("Production").WithLowLimit().WithSetting(HttpSettingKeys.KnownNetwork(0), "2001:db8::/32");
        using var client = factory.ClientFor("http://api.example.com");
        await client.ExhaustAsync("2001:db8::1", forwardedFor: "198.51.100.1");

        using var limited = await client.SendAsync(SecurityRequests.Version, "2001:db8::1", forwardedFor: "198.51.100.1");
        using var other = await client.SendAsync(SecurityRequests.Version, "2001:db8::1", forwardedFor: "198.51.100.2", forwardedProto: "https");

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        SecurityAssert.HasHsts(other);
    }

    // Breaks when a fallback authorization policy also blocks the probes: an orchestrator could never see the API as alive.
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task HealthEndpointsStayReachableUnderAFallbackPolicy(string path)
    {
        using var factory = WithFallbackPolicy();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Breaks when the fallback policy is not applied to API endpoints, which would make the test above prove nothing.
    [Fact]
    public async Task ApiEndpointWithoutAllowAnonymousAnswers401UnderAFallbackPolicy()
    {
        using var factory = WithFallbackPolicy();
        using var client = factory.CreateClient();

        using var anonymous = await client.SendAsync(SecurityRequests.Version);
        using var authenticated = await client.SendAsync(SecurityRequests.Version, user: "alice");

        await ProblemAssert.IsProblemAsync(anonymous, 401, "unauthorized");
        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
    }

    private static ApiHostFactory WithFallbackPolicy() =>
        new ApiHostFactory().WithServices(services =>
        {
            TestAuthenticationHandler.Register(services);
            services.AddAuthorizationBuilder().SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        });
}
