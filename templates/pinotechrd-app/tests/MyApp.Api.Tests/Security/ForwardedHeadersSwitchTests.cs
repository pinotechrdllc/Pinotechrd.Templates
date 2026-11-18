using System.Net;
using MyApp.Api.Tests.Support;

namespace MyApp.Api.Tests.Security;

// Criterion 23: the platform switch (ASPNETCORE_FORWARDEDHEADERS_ENABLED, configuration key ForwardedHeaders_Enabled)
// cannot widen who is trusted to set X-Forwarded-For.
public class ForwardedHeadersSwitchTests
{
    private const string SwitchKey = "ForwardedHeaders_Enabled";
    private const string Proxy = "10.9.9.9";

    // Breaks when the host starts with the switch on and nothing configured: X-Forwarded-For would be trusted from anyone.
    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("TRUE")]
    public void SwitchOnWithoutAProxyStopsTheHostNamingTheSection(string value)
    {
        using var factory = new ApiHostFactory().WithSetting(SwitchKey, value);

        StartupFailure.AssertOptionsInvalid(factory, "Http:ForwardedHeaders");
    }

    // Breaks when the message does not name the switch, so the operator cannot tell what to unset.
    [Fact]
    public void SwitchOnWithoutAProxyNamesTheSwitchInTheMessage()
    {
        using var factory = new ApiHostFactory().WithSetting(SwitchKey, "true");

        StartupFailure.AssertOptionsInvalid(factory, SwitchKey);
    }

    // Breaks when the switch on with a proxy configured trusts any sender (the platform clears the trusted lists).
    [Fact]
    public async Task SwitchOnWithAProxyTrustsOnlyThatProxy()
    {
        using var factory = new ApiHostFactory().WithLowLimit()
            .WithSetting(SwitchKey, "true")
            .WithSetting(HttpSettingKeys.KnownProxy(0), Proxy);
        using var client = factory.CreateClient();
        await client.ExhaustAsync("203.0.113.200", forwardedFor: "198.51.100.1");

        using var changed = await client.SendAsync(SecurityRequests.Version, "203.0.113.200", forwardedFor: "198.51.100.2");
        using var prepended = await client.SendAsync(SecurityRequests.Version, "203.0.113.200", forwardedFor: "198.51.100.3, 198.51.100.4");

        Assert.Equal(HttpStatusCode.TooManyRequests, changed.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, prepended.StatusCode);
    }

    // Breaks when the proxy itself is no longer trusted once the switch is on.
    [Fact]
    public async Task SwitchOnWithAProxyStillHonorsTheForwardedAddressFromThatProxy()
    {
        using var factory = new ApiHostFactory().WithLowLimit()
            .WithSetting(SwitchKey, "true")
            .WithSetting(HttpSettingKeys.KnownProxy(0), Proxy);
        using var client = factory.CreateClient();
        await client.ExhaustAsync(Proxy, forwardedFor: "203.0.113.1");

        using var same = await client.SendAsync(SecurityRequests.Version, Proxy, forwardedFor: "203.0.113.1");
        using var other = await client.SendAsync(SecurityRequests.Version, Proxy, forwardedFor: "203.0.113.2");

        Assert.Equal(HttpStatusCode.TooManyRequests, same.StatusCode);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    // Breaks when the switch on makes the platform trust a client-prepended address behind the real proxy.
    [Fact]
    public async Task SwitchOnWithAProxyDoesNotTrustAClientPrependedAddress()
    {
        using var factory = new ApiHostFactory().WithLowLimit()
            .WithSetting(SwitchKey, "true")
            .WithSetting(HttpSettingKeys.KnownProxy(0), Proxy);
        using var client = factory.CreateClient();
        await client.ExhaustAsync(Proxy, forwardedFor: "198.51.100.1, 203.0.113.1");

        using var sameReal = await client.SendAsync(SecurityRequests.Version, Proxy, forwardedFor: "198.51.100.2, 203.0.113.1");

        Assert.Equal(HttpStatusCode.TooManyRequests, sameReal.StatusCode);
    }

    // Breaks when the switch off (or an unrelated value) changes anything: same as before, nothing is trusted without a proxy.
    [Theory]
    [InlineData("false")]
    [InlineData("0")]
    public async Task SwitchOffBehavesAsBefore(string value)
    {
        using var factory = new ApiHostFactory().WithLowLimit().WithSetting(SwitchKey, value);
        using var client = factory.CreateClient();
        await client.ExhaustAsync("127.0.0.1", forwardedFor: "198.51.100.1");

        using var changed = await client.SendAsync(SecurityRequests.Version, "127.0.0.1", forwardedFor: "198.51.100.2");

        Assert.Equal(HttpStatusCode.TooManyRequests, changed.StatusCode);
    }

    // Breaks when, in any configuration that starts, changing X-Forwarded-For gives a limited client a fresh partition.
    [Theory]
    [InlineData(null, null)]
    [InlineData("false", null)]
    [InlineData("true", Proxy)]
    [InlineData(null, Proxy)]
    public async Task ChangingForwardedForNeverGivesALimitedClientAFreshPartition(string? switchValue, string? proxy)
    {
        using var factory = new ApiHostFactory().WithLowLimit();
        if (switchValue is not null)
        {
            factory.WithSetting(SwitchKey, switchValue);
        }

        if (proxy is not null)
        {
            factory.WithSetting(HttpSettingKeys.KnownProxy(0), proxy);
        }

        using var client = factory.CreateClient();
        const string Remote = "203.0.113.150";
        await client.ExhaustAsync(Remote, forwardedFor: "198.51.100.1");

        for (var i = 2; i < 6; i++)
        {
            using var response = await client.SendAsync(SecurityRequests.Version, Remote, forwardedFor: $"198.51.100.{i}");
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        }
    }
}
