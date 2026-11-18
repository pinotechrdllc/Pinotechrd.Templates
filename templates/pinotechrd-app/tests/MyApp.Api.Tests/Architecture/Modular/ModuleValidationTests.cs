using MyApp.Api.Tests.Support;

namespace MyApp.Api.Tests.Architecture.Modular;

/// <summary>
/// The validation of the modules of the real host. The rule that holds in every architecture (a module with endpoints
/// contributed a resolver) is in <see cref="ModuleCompositionTests"/>; here the stronger one of a modular project: the
/// Endpoints project of every module calls <c>AddValidation()</c> whether or not it has endpoints yet, as the README asks.
/// </summary>
public class ModuleValidationTests(ApiHostFactory host) : IClassFixture<ApiHostFactory>
{
    // Breaks when the registration of a module drops its call to AddValidation(): the first endpoint added to the module
    // would then be served with no validation of its request body, and nothing would say so.
    [Fact]
    public void TheEndpointsProjectOfEveryModuleContributedAValidationResolverToTheHost()
    {
        var contributed = ValidationResolvers.AssemblyNamesOf(host.Services);
        var endpoints = ModuleAssemblies.OfLayer("Endpoints").Select(m => ReferenceRules.NameOf(m.Assembly)).ToList();

        Assert.NotEmpty(endpoints);
        var missing = endpoints.Where(n => !contributed.Contains(n)).ToList();
        Assert.True(missing.Count == 0, $"No validation resolver was registered for: {string.Join(", ", missing)}. Registered for: {string.Join(", ", contributed)}.");
    }
}
