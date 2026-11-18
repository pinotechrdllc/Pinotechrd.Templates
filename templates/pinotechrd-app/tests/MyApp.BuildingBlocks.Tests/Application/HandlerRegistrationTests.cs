using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using MyApp.BuildingBlocks.Application;

namespace MyApp.BuildingBlocks.Tests.Application;

public class HandlerRegistrationTests
{
    private static readonly Assembly FixtureAssembly = typeof(HandlerRegistrationTests).Assembly;

    private static ServiceCollection Register()
    {
        var services = new ServiceCollection();
        services.AddHandlers(FixtureAssembly);
        return services;
    }

    private static List<Type> RegisteredTypes(ServiceCollection services) =>
        [.. services.Select(d => d.ServiceType)];

    [Fact]
    public void PublicConcreteHandlerIsRegisteredAsScopedUnderItsOwnType()
    {
        var descriptor = Assert.Single(Register(), d => d.ServiceType == typeof(PublicFixtureHandler));

        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
        Assert.Equal(typeof(PublicFixtureHandler), descriptor.ImplementationType);
    }

    [Fact]
    public void InternalConcreteHandlerIsRegisteredAsScopedUnderItsOwnType()
    {
        var descriptor = Assert.Single(Register(), d => d.ServiceType == typeof(InternalFixtureHandler));

        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
        Assert.Equal(typeof(InternalFixtureHandler), descriptor.ImplementationType);
    }

    [Fact]
    public void OnlyConcreteClassesNamedWithTheHandlerSuffixAreRegistered()
    {
        var registered = RegisteredTypes(Register());

        Assert.Equal(
            new HashSet<Type>
            {
                typeof(PublicFixtureHandler),
                typeof(InternalFixtureHandler),
                typeof(DependentHandler),
                typeof(ImplementedFixtureHandler),
                typeof(InheritingFixtureHandler),
                typeof(NestedFixtures.PublicNestedHandler),
                typeof(NestedFixtures.InternalNestedHandler),
            },
            registered.ToHashSet());
        Assert.Equal(7, registered.Count);
    }

    [Fact]
    public void HandlerInheritingHandleAsyncFromABaseClassIsRegisteredAsScoped()
    {
        var descriptor = Assert.Single(Register(), d => d.ServiceType == typeof(InheritingFixtureHandler));

        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    [Fact]
    public void PublicAndInternalNestedHandlersAreRegistered()
    {
        var registered = RegisteredTypes(Register());

        Assert.Contains(typeof(NestedFixtures.PublicNestedHandler), registered);
        Assert.Contains(typeof(NestedFixtures.InternalNestedHandler), registered);
        Assert.DoesNotContain(typeof(NestedFixtures.NestedWithoutHandleAsyncHandler), registered);
    }

    [Fact]
    public void HandlerNamedClassWithoutHandleAsyncIsNotRegistered()
    {
        Assert.DoesNotContain(typeof(AuthorizationFixtureHandler), RegisteredTypes(Register()));
    }

    [Fact]
    public void HandlerWithANonPublicHandleAsyncIsNotRegistered()
    {
        var registered = RegisteredTypes(Register());

        Assert.DoesNotContain(typeof(InternalHandleAsyncFixtureHandler), registered);
        Assert.DoesNotContain(typeof(PrivateHandleAsyncFixtureHandler), registered);
    }

    [Fact]
    public void HandlerWithAStaticHandleAsyncIsNotRegistered()
    {
        Assert.DoesNotContain(typeof(StaticHandleAsyncFixtureHandler), RegisteredTypes(Register()));
    }

    [Fact]
    public void AbstractHandlerIsNotRegistered()
    {
        Assert.DoesNotContain(typeof(AbstractFixtureHandler), RegisteredTypes(Register()));
    }

    [Fact]
    public void HandlerInterfaceIsNotRegistered()
    {
        Assert.DoesNotContain(typeof(IFixtureHandler), RegisteredTypes(Register()));
    }

    [Fact]
    public void ClassWithAnotherNameIsNotRegistered()
    {
        var registered = RegisteredTypes(Register());

        Assert.DoesNotContain(typeof(FixtureService), registered);
        Assert.DoesNotContain(typeof(HandlerWithAnotherSuffix), registered);
        Assert.DoesNotContain(typeof(FixtureHandlerBase), registered);
        Assert.DoesNotContain(typeof(Dependency), registered);
    }

    [Fact]
    public void OpenGenericHandlerIsNotRegistered()
    {
        var registered = RegisteredTypes(Register());

        Assert.DoesNotContain(typeof(GenericFixtureHandler<>), registered);
        Assert.DoesNotContain(registered, t => t.IsGenericType);
    }

    [Fact]
    public void CallingItTwiceDoesNotDuplicateRegistrations()
    {
        var once = Register();
        var twice = Register();
        twice.AddHandlers(FixtureAssembly);

        Assert.Equal(once.Count, twice.Count);
        Assert.Single(twice, d => d.ServiceType == typeof(PublicFixtureHandler));
    }

    [Fact]
    public void RegisteredHandlerResolvesFromAScopeWithItsDependencies()
    {
        var services = Register();
        services.AddSingleton<Dependency>();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        using var scope = provider.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<DependentHandler>();

        Assert.Same(provider.GetRequiredService<Dependency>(), handler.Dependency);
    }

    [Fact]
    public void RegisteredHandlerIsSharedWithinAScopeAndNewInAnother()
    {
        using var provider = Register().BuildServiceProvider();

        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        var a = first.ServiceProvider.GetRequiredService<PublicFixtureHandler>();
        var b = first.ServiceProvider.GetRequiredService<PublicFixtureHandler>();
        var c = second.ServiceProvider.GetRequiredService<PublicFixtureHandler>();

        Assert.Same(a, b);
        Assert.NotSame(a, c);
    }

    [Fact]
    public void ItReturnsTheSameServiceCollectionForChaining()
    {
        var services = new ServiceCollection();

        Assert.Same(services, services.AddHandlers(FixtureAssembly));
    }
}
