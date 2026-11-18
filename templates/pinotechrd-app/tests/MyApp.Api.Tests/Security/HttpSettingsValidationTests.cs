using System.Net;
using MyApp.Api.Tests.Support;

namespace MyApp.Api.Tests.Security;

// Invalid settings stop the host with a message that names the setting.
public class HttpSettingsValidationTests
{
    // Breaks when a permit limit below one or above 10,000 is accepted (the window is the longest allowed, so only the limit is at fault).
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("-300")]
    [InlineData("10001")]
    [InlineData("100000")]
    public void PermitLimitOutsideTheDocumentedRangeStopsTheHost(string value)
    {
        using var factory = new ApiHostFactory()
            .WithSetting(HttpSettingKeys.PermitLimit, value)
            .WithSetting(HttpSettingKeys.Window, "01:00:00");

        StartupFailure.AssertOptionsInvalid(factory, HttpSettingKeys.PermitLimit);
    }

    // Breaks when a window outside the documented range of one second to one hour is accepted.
    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-00:00:30")]
    [InlineData("00:00:00.500")]
    [InlineData("01:00:01")]
    [InlineData("1.00:00:00")]
    public void WindowOutsideTheDocumentedRangeStopsTheHost(string value)
    {
        using var factory = new ApiHostFactory().WithSetting(HttpSettingKeys.Window, value);

        StartupFailure.AssertOptionsInvalid(factory, HttpSettingKeys.Window);
    }

    // Breaks when a wildcard, a path, a trailing slash, a non-http scheme or a non-origin is accepted as a CORS origin.
    [Theory]
    [InlineData("*")]
    [InlineData("https://*.example.com")]
    [InlineData("https://app.example.com/")]
    [InlineData("https://app.example.com/app")]
    [InlineData("ftp://app.example.com")]
    [InlineData("app.example.com")]
    public void InvalidCorsOriginStopsTheHost(string origin)
    {
        using var factory = new ApiHostFactory().WithSetting(HttpSettingKeys.AllowedOrigin(0), origin);

        StartupFailure.AssertOptionsInvalid(factory, HttpSettingKeys.AllowedOriginsName);
    }

    // Breaks when only the first origin of the list is validated.
    [Fact]
    public void InvalidCorsOriginAfterAValidOneStopsTheHost()
    {
        using var factory = new ApiHostFactory()
            .WithSetting(HttpSettingKeys.AllowedOrigin(0), "https://app.example.com")
            .WithSetting(HttpSettingKeys.AllowedOrigin(1), "*");

        StartupFailure.AssertOptionsInvalid(factory, HttpSettingKeys.AllowedOriginsName);
    }

    // Breaks when a known proxy that is not a complete IP address is accepted.
    [Theory]
    [InlineData("not-an-address")]
    [InlineData("10.0.0.256")]
    [InlineData("10.0.0")]
    [InlineData("10.0.0.0/8")]
    public void KnownProxyThatIsNotAnIpAddressStopsTheHost(string proxy)
    {
        using var factory = new ApiHostFactory().WithSetting(HttpSettingKeys.KnownProxy(0), proxy);

        StartupFailure.AssertOptionsInvalid(factory, HttpSettingKeys.KnownProxiesName);
    }

    // Breaks when a network that is not valid CIDR (no prefix, bad prefix, host bits set, not an address) is accepted.
    [Theory]
    [InlineData("10.0.0.0")]
    [InlineData("10.0.0.0/33")]
    [InlineData("10.0.0.1/8")]
    [InlineData("abc/8")]
    [InlineData("10.0.0.0/-1")]
    public void KnownNetworkThatIsNotValidCidrStopsTheHost(string network)
    {
        using var factory = new ApiHostFactory().WithSetting(HttpSettingKeys.KnownNetwork(0), network);

        StartupFailure.AssertOptionsInvalid(factory, HttpSettingKeys.KnownNetworksName);
    }

    // Breaks when a documented valid value is rejected (the validation is stricter than the README says).
    [Theory]
    [InlineData("Http:Cors:AllowedOrigins:0", "https://app.example.com")]
    [InlineData("Http:Cors:AllowedOrigins:0", "https://app.example.com:8443")]
    [InlineData("Http:Cors:AllowedOrigins:0", "http://localhost:5173")]
    [InlineData("Http:ForwardedHeaders:KnownProxies:0", "10.0.0.5")]
    [InlineData("Http:ForwardedHeaders:KnownProxies:0", "2001:db8::1")]
    [InlineData("Http:ForwardedHeaders:KnownNetworks:0", "10.0.0.0/8")]
    [InlineData("Http:ForwardedHeaders:KnownNetworks:0", "10.1.2.0/24")]
    [InlineData("Http:ForwardedHeaders:KnownNetworks:0", "2001:db8::/32")]
    public async Task ValidSettingStartsTheHost(string key, string value)
    {
        using var factory = new ApiHostFactory().WithSetting(key, value);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(SecurityRequests.Version);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Breaks when a documented rate-limit boundary is rejected: 10,000 per hour, the minimum window with a limit of 100,
    // and exactly 100 per second on average are all allowed.
    [Theory]
    [InlineData("1", "00:01:00")]
    [InlineData("1", "01:00:00")]
    [InlineData("300", "00:01:00")]
    [InlineData("10000", "01:00:00")]
    [InlineData("100", "00:00:01")]
    [InlineData("1000", "00:00:10")]
    public async Task RateLimitBoundariesStartTheHost(string permitLimit, string window)
    {
        using var factory = new ApiHostFactory()
            .WithSetting(HttpSettingKeys.PermitLimit, permitLimit)
            .WithSetting(HttpSettingKeys.Window, window);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(SecurityRequests.Version);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Breaks when the average rate (limit divided by window) is not capped at 100 per second: such a limit stops nobody.
    [Theory]
    [InlineData("5000", "00:00:10")]
    [InlineData("101", "00:00:01")]
    [InlineData("10000", "00:01:00")]
    public void AverageRateAboveOneHundredPerSecondStopsTheHost(string permitLimit, string window)
    {
        using var factory = new ApiHostFactory()
            .WithSetting(HttpSettingKeys.PermitLimit, permitLimit)
            .WithSetting(HttpSettingKeys.Window, window);

        StartupFailure.AssertOptionsInvalid(factory, HttpSettingKeys.PermitLimit);
    }

    // Breaks when the default limit of 300 combined with a one second window is accepted (each value alone is valid).
    [Theory]
    [InlineData(HttpSettingKeys.PermitLimit)]
    [InlineData(HttpSettingKeys.Window)]
    public void DefaultLimitWithAOneSecondWindowStopsTheHostNamingTheSetting(string setting)
    {
        using var factory = new ApiHostFactory().WithSetting(HttpSettingKeys.Window, "00:00:01");

        StartupFailure.AssertOptionsInvalid(factory, setting);
    }

    // Breaks when a network that covers too much (IPv4 wider than /8, IPv6 wider than /32, IPv4-mapped IPv6) is trusted as a proxy network.
    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("10.0.0.0/7")]
    [InlineData("::/0")]
    [InlineData("fd00::/8")]
    [InlineData("::ffff:0:0/96")]
    public void KnownNetworkThatCoversTooMuchStopsTheHost(string network)
    {
        using var factory = new ApiHostFactory().WithSetting(HttpSettingKeys.KnownNetwork(0), network);

        StartupFailure.AssertOptionsInvalid(factory, HttpSettingKeys.KnownNetworksName);
    }

    // Breaks when an http origin for a host that is not local is accepted outside Development.
    [Theory]
    [InlineData("http://app.example.com")]
    [InlineData("http://203.0.113.5:3000")]
    [InlineData("http://localhost.example.com")]
    public void InsecureOriginOfAPublicHostStopsTheHostInProduction(string origin)
    {
        using var factory = ApiHostFactory.ForEnvironment("Production").WithSetting(HttpSettingKeys.AllowedOrigin(0), origin);

        StartupFailure.AssertOptionsInvalid(factory, HttpSettingKeys.AllowedOriginsName);
    }

    // Breaks when the environment is ignored: Development may list an http origin of any host.
    [Fact]
    public async Task InsecureOriginOfAPublicHostStartsTheHostInDevelopment()
    {
        using var factory = new ApiHostFactory().WithSetting(HttpSettingKeys.AllowedOrigin(0), "http://app.example.com");
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(SecurityRequests.Version, origin: "http://app.example.com");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        SecurityAssert.AllowsOrigin(response, "http://app.example.com");
    }

    // Breaks when a local http origin is refused in Production (development servers of the front end).
    [Theory]
    [InlineData("http://localhost:5173")]
    [InlineData("http://app.localhost:5173")]
    [InlineData("http://127.0.0.1:5173")]
    [InlineData("http://[::1]:5173")]
    public async Task LocalHttpOriginStartsTheHostInProduction(string origin)
    {
        using var factory = ApiHostFactory.ForEnvironment("Production").WithSetting(HttpSettingKeys.AllowedOrigin(0), origin);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(SecurityRequests.Version, origin: origin);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        SecurityAssert.AllowsOrigin(response, origin);
    }

    // Breaks when an origin that names the default port of its scheme is accepted: a browser never sends it, so it never matches.
    [Theory]
    [InlineData("https://app.example.com:443")]
    [InlineData("http://localhost:80")]
    public void OriginWithTheDefaultPortStopsTheHost(string origin)
    {
        using var factory = new ApiHostFactory().WithSetting(HttpSettingKeys.AllowedOrigin(0), origin);

        StartupFailure.AssertOptionsInvalid(factory, HttpSettingKeys.AllowedOriginsName);
    }

    // Breaks when the settings are not bound together: all sections valid at once must start.
    [Fact]
    public async Task AllValidSettingsTogetherStartTheHost()
    {
        using var factory = new ApiHostFactory()
            .WithSetting(HttpSettingKeys.PermitLimit, "50")
            .WithSetting(HttpSettingKeys.Window, "00:00:30")
            .WithSetting(HttpSettingKeys.AllowedOrigin(0), "https://app.example.com")
            .WithSetting(HttpSettingKeys.KnownProxy(0), "10.0.0.5")
            .WithSetting(HttpSettingKeys.KnownNetwork(0), "192.168.0.0/16");
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(SecurityRequests.Version, origin: "https://app.example.com");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Breaks when the defaults are not valid or no longer match the README (300 per minute, CORS off, no proxy).
    [Fact]
    public async Task DefaultsStartTheHostWithCorsOff()
    {
        using var factory = new ApiHostFactory();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(SecurityRequests.Version, origin: "https://app.example.com");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        SecurityAssert.HasNoCorsHeaders(response);
    }
}
