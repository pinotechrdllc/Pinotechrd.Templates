using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Support;

namespace MyApp.Api.Tests.Architecture.Clean;

/// <summary>
/// A <c>*Handler</c> class of the application layer is a scoped service of the real host, so an endpoint can take it as a
/// parameter. The application layer of a freshly generated project has no handler yet, so today the set checked is
/// empty and these tests guard the first handler that is added: they fail when it is not registered or not scoped.
/// Nothing else of the registration is observable from outside until a handler exists.
/// </summary>
public class CleanHandlerRegistrationTests(ApiHostFactory factory) : IClassFixture<ApiHostFactory>
{
    private static IReadOnlyList<Type> ApplicationHandlers() =>
        [.. CleanLayers.Application.GetTypes().Where(t =>
            t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false }
            && t.Name.EndsWith("Handler", StringComparison.Ordinal)
            && t.GetMethod("HandleAsync", BindingFlags.Public | BindingFlags.Instance) is not null)];

    // Breaks when the host does not register the handlers of the application layer, so the endpoint that takes one fails.
    [Fact]
    public void EveryHandlerOfTheApplicationLayerResolvesFromARequestScope()
    {
        foreach (var handler in ApplicationHandlers())
        {
            using var scope = factory.Services.CreateScope();

            Assert.NotNull(scope.ServiceProvider.GetService(handler));
        }
    }

    // Breaks when a handler is registered as a singleton or transient: one instance per scope is the contract.
    [Fact]
    public void EveryHandlerOfTheApplicationLayerIsScoped()
    {
        foreach (var handler in ApplicationHandlers())
        {
            using var first = factory.Services.CreateScope();
            using var second = factory.Services.CreateScope();

            var inFirst = first.ServiceProvider.GetRequiredService(handler);

            Assert.Same(inFirst, first.ServiceProvider.GetRequiredService(handler));
            Assert.NotSame(inFirst, second.ServiceProvider.GetRequiredService(handler));
        }
    }
}
