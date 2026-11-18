using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Validation;
using MyApp.BuildingBlocks.Web;
using MyApp.Modules.MyModule.Endpoints;

namespace MyApp.Modules.MyModule.Tests;

/// <summary>
/// The module as the template generates it: its name, its five projects and the registration the host and the build-time
/// contract generation rely on. Every expectation is read from the assembly names, so the tests hold whatever the module is
/// called.
/// </summary>
public class ModuleStampTests
{
    private static readonly Assembly EndpointsAssembly = typeof(MyModuleModule).Assembly;

    // The assemblies of a module are named {Root}.Modules.{Name}.{Layer}; the Endpoints one gives the root and the name.
    private static readonly string EndpointsName = EndpointsAssembly.GetName().Name!;

    private static string Root => EndpointsName[..EndpointsName.IndexOf(".Modules.", StringComparison.Ordinal)];

    private static string ModuleName => EndpointsName[(Root.Length + ".Modules.".Length)..^".Endpoints".Length];

    private static (string Layer, Type Marker)[] Markers =>
    [
        ("Contracts", typeof(MyApp.Modules.MyModule.Contracts.MyModuleContractsAssembly)),
        ("Domain", typeof(MyApp.Modules.MyModule.Domain.MyModuleDomainAssembly)),
        ("Application", typeof(MyApp.Modules.MyModule.Application.MyModuleApplicationAssembly)),
        ("Infrastructure", typeof(MyApp.Modules.MyModule.Infrastructure.MyModuleInfrastructureAssembly)),
        ("Endpoints", typeof(MyModuleEndpointsAssembly)),
    ];

    // Breaks when the name the module reports (its route segment and its tag) is not the name it was generated with.
    [Fact]
    public void TheNameOfTheModuleIsTheNameItWasGeneratedWith()
    {
        Assert.False(string.IsNullOrWhiteSpace(ModuleName));
        Assert.False(ModuleName.Contains('.', StringComparison.Ordinal));
        Assert.Equal(ModuleName, new MyModuleModule().Name);
    }

    // Breaks when the composition root is renamed apart from the module: the README names it {Name}Module.
    [Fact]
    public void TheModuleClassIsNamedAfterTheModule()
    {
        Assert.Equal(ModuleName + "Module", typeof(MyModuleModule).Name);
        Assert.True(typeof(MyModuleModule).IsPublic);
        Assert.True(typeof(IModule).IsAssignableFrom(typeof(MyModuleModule)));
    }

    // Breaks when the endpoints of the module are looked for in an assembly other than its Endpoints project.
    [Fact]
    public void TheEndpointsAssemblyOfTheModuleIsItsEndpointsProject()
    {
        var module = new MyModuleModule();

        Assert.Same(EndpointsAssembly, module.EndpointsAssembly);
        Assert.Same(MyModuleEndpointsAssembly.Assembly, module.EndpointsAssembly);
        Assert.Equal($"{Root}.Modules.{ModuleName}.Endpoints", module.EndpointsAssembly.GetName().Name);
    }

    // Breaks when a marker type moves to another project, or two projects share one.
    [Fact]
    public void EachMarkerTypeLivesInTheAssemblyOfItsOwnProject()
    {
        foreach (var (layer, marker) in Markers)
        {
            Assert.Equal($"{Root}.Modules.{ModuleName}.{layer}", marker.Assembly.GetName().Name);
        }

        Assert.Equal(Markers.Length, Markers.Select(m => m.Marker.Assembly).Distinct().Count());
    }

    [Fact]
    public void EachMarkerExposesTheAssemblyItIsDeclaredIn()
    {
        foreach (var (_, marker) in Markers)
        {
            var exposed = (Assembly?)marker.GetProperty("Assembly", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);

            Assert.Same(marker.Assembly, exposed);
        }
    }

    // What build-time contract generation does: the host starts with no configuration value and no database, so the
    // registration of the module must work in a builder whose configuration is empty.
    [Fact]
    public void TheModuleRegistersInABuilderWithAnEmptyConfigurationAndNoDatabase()
    {
        var builder = EmptyBuilder();
        Assert.Empty(builder.Configuration.AsEnumerable());

        var added = builder.AddModule<MyModuleModule>();
        using var provider = builder.Services.BuildServiceProvider();

        Assert.Same(builder, added);
        var module = Assert.Single(provider.GetServices<IModule>());
        Assert.IsType<MyModuleModule>(module);
    }

    [Fact]
    public void TheModuleAddServicesNeedsNoConfigurationValue()
    {
        var builder = EmptyBuilder();
        var context = new ModuleContext(builder.Services, builder.Configuration, builder.Environment);

        var failure = Record.Exception(() => new MyModuleModule().AddServices(context));

        Assert.Null(failure);
    }

    // Breaks when the module's registration stops calling AddValidation(): the generated validation is added by the call of
    // the project that maps the endpoints, so without it the bodies of the endpoints added later are not validated.
    [Fact]
    public void TheModuleContributesAValidationResolverOfItsEndpointsAssembly()
    {
        var builder = EmptyBuilder();
        builder.AddModule<MyModuleModule>();
        using var provider = builder.Services.BuildServiceProvider();

        // The validation API is experimental (ASP0029); the assembly of a resolver is the only trace of who called it.
#pragma warning disable ASP0029
        var resolverAssemblies = provider.GetRequiredService<IOptions<ValidationOptions>>().Value.Resolvers
            .Select(r => r.GetType().Assembly)
            .ToList();
#pragma warning restore ASP0029

        Assert.Contains(EndpointsAssembly, resolverAssemblies);
    }

    // Breaks when the registration of the module adds a service that belongs to another module.
    [Fact]
    public void TheModuleRegistersNothingThatBelongsToAnotherModule()
    {
        var services = EmptyBuilder().AddModule<MyModuleModule>().Services;
        var ownPrefix = $"{Root}.Modules.{ModuleName}.";
        var modulesPrefix = $"{Root}.Modules.";

        var foreign = services
            .SelectMany(AssemblyNamesOf)
            .Where(n => n.StartsWith(modulesPrefix, StringComparison.Ordinal) && !n.StartsWith(ownPrefix, StringComparison.Ordinal))
            .Distinct()
            .ToList();

        Assert.Empty(foreign);
        Assert.Contains(services.SelectMany(AssemblyNamesOf), n => n.StartsWith(ownPrefix, StringComparison.Ordinal));
    }

    // Breaks when a module added twice registers its services a second time.
    [Fact]
    public void AddingTheModuleTwiceRegistersItOnce()
    {
        var builder = EmptyBuilder();
        var services = builder.Services;

        builder.AddModule<MyModuleModule>();
        var count = services.Count;
        builder.AddModule<MyModuleModule>();

        Assert.Equal(count, services.Count);
        Assert.Single(services, d => d.ServiceType == typeof(IModule));
    }

    private static HostApplicationBuilder EmptyBuilder() => Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());

    private static IEnumerable<string> AssemblyNamesOf(ServiceDescriptor descriptor) =>
        (descriptor.IsKeyedService
            ? new[] { descriptor.ServiceType, descriptor.KeyedImplementationType, descriptor.KeyedImplementationInstance?.GetType(), descriptor.KeyedImplementationFactory?.Method.DeclaringType }
            : new[] { descriptor.ServiceType, descriptor.ImplementationType, descriptor.ImplementationInstance?.GetType(), descriptor.ImplementationFactory?.Method.DeclaringType })
        .OfType<Type>()
        .Select(t => t.Assembly.GetName().Name!);
}
