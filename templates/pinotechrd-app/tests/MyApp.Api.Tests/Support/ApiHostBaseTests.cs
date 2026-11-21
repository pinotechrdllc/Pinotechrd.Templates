using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MyApp.Api.Tests.Contract;
using MyApp.Modules.Access.Infrastructure.Security;
using Pinotechrd.Security.Options;

namespace MyApp.Api.Tests.Support;

// Every test host built on the real Program shares the defaults of ApiHostBase: the fake JWT settings and the purge turned
// off. A new factory that derives from WebApplicationFactory<Program> directly would silently lose them.
public class ApiHostBaseTests
{
    public static TheoryData<string> Factories => [nameof(ApiHostFactory), nameof(ContractIsCurrentTests.RealApiFactory)];

    private static WebApplicationFactory<Program> Create(string name) => name switch
    {
        nameof(ApiHostFactory) => new ApiHostFactory(),
        nameof(ContractIsCurrentTests.RealApiFactory) => new ContractIsCurrentTests.RealApiFactory(),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    private static List<Type> ConcreteProgramFactories() =>
        [.. typeof(ApiHostBaseTests).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(WebApplicationFactory<Program>).IsAssignableFrom(t))];

    // Breaks when a test factory derives from WebApplicationFactory<Program> without ApiHostBase, so its host starts without
    // the fake JWT settings or with the purge service running.
    [Fact]
    public void EveryConcreteProgramFactoryOfTheTestAssemblyDerivesFromApiHostBase()
    {
        var outsiders = ConcreteProgramFactories().Where(t => !typeof(ApiHostBase).IsAssignableFrom(t)).Select(t => t.FullName);

        Assert.Empty(outsiders);
    }

    // Breaks when the reflection above finds nothing (a wrong base type or assembly), so the check before it passes vacuously;
    // the nested RealApiFactory proves nested types are seen.
    [Fact]
    public void TheFactoryScanFindsTheKnownFactoriesIncludingNestedOnes()
    {
        var found = ConcreteProgramFactories();

        Assert.Contains(typeof(ApiHostFactory), found);
        Assert.Contains(typeof(ContractIsCurrentTests.RealApiFactory), found);
    }

    // Breaks when a factory's host does not get the fake AccessJwt values or the disabled purge from ApiHostBase, or when the
    // purge service still runs a round in a test host.
    [Theory]
    [MemberData(nameof(Factories))]
    public void AHostFromEachFactoryGetsTheFakeJwtSettingsAndNoPurge(string name)
    {
        using var factory = Create(name);

        var jwt = factory.Services.GetRequiredService<IOptions<PinotechrdSecurityOptions>>().Value.Jwt;
        var purge = factory.Services.GetRequiredService<IOptions<RefreshTokenPurgeOptions>>().Value;
        var service = Assert.Single(factory.Services.GetServices<IHostedService>().OfType<RefreshTokenPurgeService>());

        Assert.Equal(AccessJwt.Secret, jwt.Secret);
        Assert.Equal(AccessJwt.Issuer, jwt.Issuer);
        Assert.Equal(AccessJwt.Audience, jwt.Audience);
        Assert.False(purge.Enabled);
        // ExecuteAsync returns at once when the purge is off: no wait, no round.
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    // Breaks when ApiHostBase applies its defaults after the derived host's settings, so a test can no longer turn the purge on.
    [Fact]
    public void AWithSettingOverrideOfThePurgeKeyWinsOverTheBaseDefault()
    {
        using var factory = new ApiHostFactory()
            .WithSetting(ApiHostBase.PurgeEnabledKey, "true")
            .WithSetting(RefreshTokenPurgeOptions.SectionName + ":InitialDelay", "01:00:00");

        var purge = factory.Services.GetRequiredService<IOptions<RefreshTokenPurgeOptions>>().Value;
        var service = Assert.Single(factory.Services.GetServices<IHostedService>().OfType<RefreshTokenPurgeService>());

        Assert.True(purge.Enabled);
        // Waiting out its hour-long first delay: the service runs, but no round touches the placeholder database.
        Assert.False(service.ExecuteTask!.IsCompleted);
    }
}
