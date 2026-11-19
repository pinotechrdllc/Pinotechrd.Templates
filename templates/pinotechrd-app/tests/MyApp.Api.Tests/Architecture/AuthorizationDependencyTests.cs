using System.Reflection;
using MyApp.BuildingBlocks.Application.Identity;
using MyApp.BuildingBlocks.Application.Security;
using MyApp.BuildingBlocks.Web.Authorization;

namespace MyApp.Api.Tests.Architecture;

/// <summary>
/// Where the identity and permission contracts live and who may use what. They hold in every architecture and name no
/// assembly that exists in one only: the application building block holds what business code reads (the current user, the
/// permission checker, the catalogue) with no ASP.NET Core in it; the web building block holds the pipeline that feeds it,
/// which only the host and the endpoints of modules may reference (the rule for that is in
/// <see cref="BuildingBlockDependencyTests"/>).
/// </summary>
public class AuthorizationDependencyTests
{
    // Breaks when a contract that business code uses moves into the web building block, which business assemblies must
    // not reference, or into the infrastructure building block.
    [Fact]
    public void TheIdentityAndPermissionContractsLiveInThePublicApplicationBuildingBlock()
    {
        Type[] contracts = [typeof(ICurrentUser), typeof(IPermissionChecker), typeof(PermissionCatalogue), typeof(PermissionName)];

        Assert.All(contracts, t => Assert.Same(ReferenceRules.BuildingBlocksApplication, t.Assembly));
        Assert.All(contracts, t => Assert.True(t.IsPublic, $"{t.FullName} must be public: business assemblies use it."));
    }

    // Breaks when the machinery that sets the user moves into the application building block (business code could then
    // choose the user), or a type of the web pipeline ends up there.
    [Fact]
    public void ThePipelineThatFeedsTheContractsLivesInTheWebBuildingBlock()
    {
        Type[] pipeline = [typeof(IAuthenticatedRequestStep), typeof(ApiIdentityOptions), typeof(PermissionMetadata), typeof(AuthenticatedOnlyMetadata)];

        Assert.All(pipeline, t => Assert.Same(ReferenceRules.BuildingBlocksWeb, t.Assembly));
        Assert.DoesNotContain(
            ReferenceRules.BuildingBlocksApplication.GetExportedTypes(),
            t => t.Namespace is not null && t.Namespace.StartsWith("MyApp.BuildingBlocks.Web", StringComparison.Ordinal));
    }

    // Breaks when the application building block gains an ASP.NET Core dependency: the identity and permission contracts
    // must not need HttpContext, authentication or authorization types.
    [Fact]
    public void TheApplicationBuildingBlockReferencesNoAspNetCoreAssembly()
    {
        var aspNet = ReferenceRules.ReferencedNames(ReferenceRules.BuildingBlocksApplication)
            .Where(n => ReferenceRules.HasPrefix(n, "Microsoft.AspNetCore"));

        Assert.Empty(aspNet);
    }

    // Breaks when a public member of the application building block exposes an ASP.NET Core type, or one of its types derives
    // from or implements one: a host that only migrates could not use the contracts.
    [Fact]
    public void ThePublicSurfaceOfTheApplicationBuildingBlockExposesNoAspNetCoreType()
    {
        var types = ReferenceRules.BuildingBlocksApplication.GetExportedTypes();
        var exposed = types
            .SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .SelectMany(m => TypesOf(m).Where(IsAspNetCore).Select(x => $"{t.FullName}.{m.Name} exposes {x.FullName}.")))
            .Concat(types
                .Where(t => t.GetInterfaces().Concat(t.BaseType is null ? [] : [t.BaseType]).Any(IsAspNetCore))
                .Select(t => $"{t.FullName} derives from or implements an ASP.NET Core type."))
            .Distinct()
            .ToList();

        ReferenceRules.AssertNone(exposed);
    }

    // Guards the rule above against being vacuous: the web building block does expose ASP.NET Core types, and the checker
    // sees them there.
    [Fact]
    public void TheWebBuildingBlockExposesAspNetCoreTypesSoTheCheckerCanSeeThem()
    {
        var exposed = ReferenceRules.BuildingBlocksWeb.GetExportedTypes()
            .SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .SelectMany(TypesOf)
            .Where(IsAspNetCore);

        Assert.NotEmpty(exposed);
    }

    // Breaks when the application building block, which every business assembly may reference, takes on a reference that
    // drags the web, hosting or data frameworks into them, or another solution assembly than the domain building block.
    [Fact]
    public void TheApplicationBuildingBlockReferencesOnlyTheDomainBuildingBlockAndNoWebHostingOrDataFramework()
    {
        var violations = ReferenceRules.SolutionReferenceViolations(ReferenceRules.BuildingBlocksApplication, ReferenceRules.BuildingBlocksDomain)
            .Concat(ReferenceRules.Violations(
                ReferenceRules.BuildingBlocksApplication,
                [ReferenceRules.BuildingBlocksWeb, ReferenceRules.BuildingBlocksInfrastructure],
                [.. ReferenceRules.FrameworkPrefixesForbiddenInApplication, "Microsoft.AspNetCore", "Microsoft.Extensions.Hosting"]))
            .ToList();

        ReferenceRules.AssertNone(violations);
    }

    // Business assemblies may use the contracts of the application building block and never the web building block; only
    // the host and the endpoints of a module are the web layer. Holds whatever the business assemblies are called.
    [Fact]
    public void BusinessAssembliesThatAreNotTheWebLayerReferenceNoWebBuildingBlock()
    {
        var business = BusinessAssemblies.Load(BusinessAssemblies.Discover());

        var violations = business
            .Where(a => !ModuleAssemblyNames.MayReferenceTheWebBuildingBlocks(ReferenceRules.NameOf(a), ReferenceRules.SolutionRoot))
            .SelectMany(a => ReferenceRules.Violations(a, [ReferenceRules.BuildingBlocksWeb], []))
            .ToList();

        ReferenceRules.AssertNone(violations);
    }

    private static bool IsAspNetCore(Type type) =>
        (type.Namespace ?? string.Empty).StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal);

    private static IEnumerable<Type> TypesOf(MemberInfo member) =>
        member switch
        {
            MethodBase method => method.GetParameters().Select(p => p.ParameterType)
                .Concat(method is MethodInfo info ? [info.ReturnType] : []).SelectMany(Unwrap),
            PropertyInfo property => Unwrap(property.PropertyType),
            FieldInfo field => Unwrap(field.FieldType),
            EventInfo @event => @event.EventHandlerType is { } handler ? Unwrap(handler) : [],
            _ => [],
        };

    private static IEnumerable<Type> Unwrap(Type type)
    {
        if (type.HasElementType)
        {
            return Unwrap(type.GetElementType()!);
        }

        return type.IsGenericType ? [type, .. type.GetGenericArguments().SelectMany(Unwrap)] : [type];
    }
}
