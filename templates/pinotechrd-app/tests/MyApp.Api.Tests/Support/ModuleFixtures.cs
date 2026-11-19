using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MyApp.BuildingBlocks.Application;
using MyApp.BuildingBlocks.Web;

namespace MyApp.Api.Tests.Support;

// Fixture modules for the tests of the module mechanics, valid in every architecture: they are declared here, in the test
// assembly, and a host that is not told about them never sees them. Each one scans a TypeSetAssembly of its own, so the
// endpoints of one fixture are never mapped under another. The endpoint classes are public, as the scanner requires, so a test that
// scans the whole test assembly with AddApiEndpoints (EndpointDiscoveryTests) maps them too; it asserts on its own routes only.
// They are never added to the real host (a module of the same name may exist there), only to the service collections and
// small hosts of the tests that use them.

/// <summary>Proof that a module's <c>AddServices</c> ran, with what its context carried.</summary>
/// <param name="Module">The name of the module that registered it.</param>
/// <param name="Services">The service collection the module was given.</param>
/// <param name="Configuration">The configuration the module was given.</param>
/// <param name="Environment">The environment the module was given.</param>
public sealed record FixtureModuleCall(string Module, IServiceCollection Services, IConfiguration Configuration, IHostEnvironment Environment);

/// <summary>Handler of the application assembly of <see cref="FixtureSalesModule"/>.</summary>
public sealed class FixtureSalesHandler
{
    private readonly string _answer = "handled";

    public Task<string> HandleAsync() => Task.FromResult(_answer);
}

/// <summary>Handler that is not part of any fixture module's application assembly.</summary>
public sealed class FixtureStrayHandler
{
    private readonly string _answer = "stray";

    public Task<string> HandleAsync() => Task.FromResult(_answer);
}

/// <summary>A GET endpoint at a relative route that answers with a fixed text, to tell which endpoint served a request.</summary>
public abstract class FixtureGetEndpoint(string route, string answer) : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routes) => routes.MapGet(route, () => Results.Ok(answer)).AllowAnonymous();
}

/// <summary>The endpoints of the module fixtures. Declared in an order that is not the ordinal order of their names.</summary>
public static class ModuleFixtureEndpoints
{
    public sealed class SalesOrders() : FixtureGetEndpoint("/orders", "sales-orders");

    public sealed class SalesOwnTag : IEndpoint
    {
        public void MapEndpoint(IEndpointRouteBuilder routes) =>
            routes.MapGet("/own-tag", () => Results.Ok("own-tag")).WithTags("Custom").AllowAnonymous();
    }

    public sealed class ApiKeysKeys() : FixtureGetEndpoint("/keys", "api-keys");

    public sealed class AcronymApiKeysKeys() : FixtureGetEndpoint("/keys", "acronym-api-keys");

    public sealed class ZetaZulu() : FixtureGetEndpoint("/zulu", "zeta-zulu");

    public sealed class ZetaAlpha() : FixtureGetEndpoint("/alpha", "zeta-alpha");

    public sealed class AbZulu() : FixtureGetEndpoint("/zulu", "ab-zulu");

    public sealed class AbAlpha() : FixtureGetEndpoint("/alpha", "ab-alpha");

    public sealed class ACZulu() : FixtureGetEndpoint("/zulu", "ac-zulu");

    public sealed class ACAlpha() : FixtureGetEndpoint("/alpha", "ac-alpha");

    /// <summary>An endpoint of the host itself, mapped without a module prefix.</summary>
    public sealed class HostFirst() : FixtureGetEndpoint("/host-first", "host");

    /// <summary>A host endpoint whose first segment is the route segment of <see cref="FixtureClashModule"/>, with a longer path and another case.</summary>
    public sealed class HostClashDetails() : FixtureGetEndpoint("/CLASH/details", "host-clash");

    /// <summary>A host endpoint that only starts like the route segment <c>clash</c>: not a clash.</summary>
    public sealed class HostClashes() : FixtureGetEndpoint("/clashes", "host-clashes");

    /// <summary>A host endpoint whose first segment is a route parameter: it matches any segment and takes no module's place.</summary>
    public sealed class HostParameter() : FixtureGetEndpoint("/{anything}/parameter", "host-parameter");

    /// <summary>Endpoint of <see cref="FixtureClashModule"/>.</summary>
    public sealed class ClashInfo() : FixtureGetEndpoint("/info", "clash-info");

    /// <summary>Endpoint of <see cref="FixtureVersionModule"/>.</summary>
    public sealed class VersionInfo() : FixtureGetEndpoint("/info", "version-info");

    /// <summary>An endpoint of a module mapped at the root of the module.</summary>
    public sealed class RootOfModule() : FixtureGetEndpoint("/", "module-root");

    /// <summary>Takes a body that is validated with DataAnnotations when the project of the module called <c>AddValidation()</c>.</summary>
    public sealed class ValidatedItems : IEndpoint
    {
        public void MapEndpoint(IEndpointRouteBuilder routes) =>
            routes.MapPost("/items", (FixtureItem item) => Results.Ok(item)).AllowAnonymous();
    }
}

/// <summary>The part of a module fixture that is the same for every one: it records that it ran.</summary>
public abstract class FixtureModule : IModule
{
    public abstract string Name { get; }

    public abstract Assembly EndpointsAssembly { get; }

    public virtual void AddServices(ModuleContext context) =>
        context.Services.AddSingleton(new FixtureModuleCall(Name, context.Services, context.Configuration, context.Environment));

    protected static Assembly ViewOf(params Type[] types) => new TypeSetAssembly(typeof(FixtureModule).Assembly, types);
}

/// <summary>A module named like the README's example, with an endpoint of its own tag and a handler.</summary>
public sealed class FixtureSalesModule : FixtureModule
{
    private static readonly Assembly Endpoints =
        ViewOf(typeof(ModuleFixtureEndpoints.SalesOrders), typeof(ModuleFixtureEndpoints.SalesOwnTag));

    private static readonly Assembly Application = ViewOf(typeof(FixtureSalesHandler));

    public override string Name => "Sales";

    public override Assembly EndpointsAssembly => Endpoints;

    public override void AddServices(ModuleContext context)
    {
        base.AddServices(context);
        context.Services.AddHandlers(Application);
    }
}

/// <summary>Two modules whose names give the same route segment, <c>api-keys</c>.</summary>
public sealed class FixtureApiKeysModule : FixtureModule
{
    private static readonly Assembly Endpoints = ViewOf(typeof(ModuleFixtureEndpoints.ApiKeysKeys));

    public override string Name => "ApiKeys";

    public override Assembly EndpointsAssembly => Endpoints;
}

/// <summary>The second module with the route segment <c>api-keys</c>.</summary>
public sealed class FixtureAcronymApiKeysModule : FixtureModule
{
    private static readonly Assembly Endpoints = ViewOf(typeof(ModuleFixtureEndpoints.AcronymApiKeysKeys));

    public override string Name => "APIKeys";

    public override Assembly EndpointsAssembly => Endpoints;
}

// The modules of the order test. Their names sort differently by ordinal and by culture ("AC" before "Ab" only by
// ordinal), and each lists its endpoints in the reverse of their ordinal order.

/// <summary>Module of the order test.</summary>
public sealed class FixtureZetaModule : FixtureModule
{
    private static readonly Assembly Endpoints =
        ViewOf(typeof(ModuleFixtureEndpoints.ZetaZulu), typeof(ModuleFixtureEndpoints.ZetaAlpha));

    public override string Name => "Zeta";

    public override Assembly EndpointsAssembly => Endpoints;
}

/// <summary>Module of the order test.</summary>
public sealed class FixtureAbModule : FixtureModule
{
    private static readonly Assembly Endpoints =
        ViewOf(typeof(ModuleFixtureEndpoints.AbZulu), typeof(ModuleFixtureEndpoints.AbAlpha));

    public override string Name => "Ab";

    public override Assembly EndpointsAssembly => Endpoints;
}

/// <summary>Module of the order test.</summary>
public sealed class FixtureACModule : FixtureModule
{
    private static readonly Assembly Endpoints =
        ViewOf(typeof(ModuleFixtureEndpoints.ACZulu), typeof(ModuleFixtureEndpoints.ACAlpha));

    public override string Name => "AC";

    public override Assembly EndpointsAssembly => Endpoints;
}

/// <summary>The host's own endpoints, as an assembly to scan with <c>AddApiEndpoints</c>.</summary>
public static class FixtureHostEndpoints
{
    public static Assembly Assembly { get; } = new TypeSetAssembly(typeof(FixtureModule).Assembly, typeof(ModuleFixtureEndpoints.HostFirst));

    /// <summary>A host endpoint that shares the route segment of <see cref="FixtureClashModule"/>.</summary>
    public static Assembly Clashing { get; } = new TypeSetAssembly(typeof(FixtureModule).Assembly, typeof(ModuleFixtureEndpoints.HostClashDetails));

    /// <summary>Host endpoints that look like <c>clash</c> but do not take its place.</summary>
    public static Assembly NotClashing { get; } = new TypeSetAssembly(
        typeof(FixtureModule).Assembly, typeof(ModuleFixtureEndpoints.HostClashes), typeof(ModuleFixtureEndpoints.HostParameter));
}

/// <summary>A module that calls <c>AddValidation()</c> in its registration, as the stamp module does.</summary>
public sealed class FixtureValidatedModule : FixtureModule
{
    private static readonly Assembly Endpoints = ViewOf(typeof(ModuleFixtureEndpoints.ValidatedItems));

    public override string Name => "Validated";

    public override Assembly EndpointsAssembly => Endpoints;

    public override void AddServices(ModuleContext context)
    {
        base.AddServices(context);
        context.Services.AddTestAssemblyValidation();
    }
}

/// <summary>The same module without the call to <c>AddValidation()</c>: the only difference with <see cref="FixtureValidatedModule"/>.</summary>
public sealed class FixtureUnvalidatedModule : FixtureModule
{
    private static readonly Assembly Endpoints = ViewOf(typeof(ModuleFixtureEndpoints.ValidatedItems));

    public override string Name => "Unvalidated";

    public override Assembly EndpointsAssembly => Endpoints;
}

/// <summary>A module without endpoints and without validation.</summary>
public sealed class FixtureQuietModule : FixtureModule
{
    private static readonly Assembly Endpoints = ViewOf();

    public override string Name => "Quiet";

    public override Assembly EndpointsAssembly => Endpoints;
}

/// <summary>A module whose route segment, <c>clash</c>, is the first segment of a host fixture endpoint.</summary>
public sealed class FixtureClashModule : FixtureModule
{
    private static readonly Assembly Endpoints = ViewOf(typeof(ModuleFixtureEndpoints.ClashInfo));

    public override string Name => "Clash";

    public override Assembly EndpointsAssembly => Endpoints;
}

/// <summary>A module named like the real host endpoint <c>GET /api/v1/version</c>.</summary>
public sealed class FixtureVersionModule : FixtureModule
{
    private static readonly Assembly Endpoints = ViewOf(typeof(ModuleFixtureEndpoints.VersionInfo));

    public override string Name => "Version";

    public override Assembly EndpointsAssembly => Endpoints;
}

/// <summary>A module with an endpoint at the root of the module.</summary>
public sealed class FixtureRootedModule : FixtureModule
{
    private static readonly Assembly Endpoints = ViewOf(typeof(ModuleFixtureEndpoints.RootOfModule));

    public override string Name => "Rooted";

    public override Assembly EndpointsAssembly => Endpoints;
}

/// <summary>A module whose name is <c>null</c>.</summary>
public sealed class FixtureNullNameModule : FixtureModule
{
    public override string Name => null!;

    public override Assembly EndpointsAssembly => ViewOf();
}

/// <summary>A module whose name is empty.</summary>
public sealed class FixtureEmptyNameModule : FixtureModule
{
    public override string Name => string.Empty;

    public override Assembly EndpointsAssembly => ViewOf();
}

/// <summary>A module whose name is white space.</summary>
public sealed class FixtureWhiteSpaceNameModule : FixtureModule
{
    public override string Name => "  ";

    public override Assembly EndpointsAssembly => ViewOf();
}

/// <summary>A module whose name has no letter or digit.</summary>
public sealed class FixtureSeparatorsNameModule : FixtureModule
{
    public override string Name => "__";

    public override Assembly EndpointsAssembly => ViewOf();
}

/// <summary>A module whose name has letters outside ASCII.</summary>
public sealed class FixtureNonAsciiNameModule : FixtureModule
{
    public override string Name => "\u00D1and\u00FA";

    public override Assembly EndpointsAssembly => ViewOf();
}
