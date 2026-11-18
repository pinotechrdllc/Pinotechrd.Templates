using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Web;

namespace MyApp.Api.Tests.Host;

/// <summary>
/// The mechanics of modules, which are the same in both architectures: where a module's endpoints are served, how they are
/// tagged and ordered, what registering a module does and what it refuses. They use the fixture modules of the test
/// assembly, so a generated project with no endpoints in its module is covered as well as one with many.
/// </summary>
public class ModuleMappingTests
{
    // Breaks when a module's endpoints are served without the module prefix, under /api/v1 alone, at the root, or under a
    // prefix that is not the route segment of the module name.
    [Fact]
    public async Task AModuleEndpointIsServedUnderTheRouteSegmentOfItsNameAndNowhereElse()
    {
        await using var app = await ModuleHost.StartAsync(builder => builder.AddModule<FixtureSalesModule>());
        using var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.OK, await StatusOf(client, "/api/v1/sales/orders"));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOf(client, "/api/v1/orders"));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOf(client, "/orders"));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOf(client, "/sales/orders"));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOf(client, "/api/v1/Sales/orders/extra"));
    }

    // Breaks when the endpoints of a module lose the tag with the module name in their endpoint metadata.
    [Fact]
    public async Task AModuleEndpointCarriesTheModuleNameAsItsTagInTheEndpointMetadata()
    {
        await using var app = await ModuleHost.StartAsync(builder => builder.AddModule<FixtureSalesModule>());

        var endpoint = ModuleHost.EndpointAt(app, "/api/v1/sales/orders");

        Assert.Equal(["Sales"], endpoint.Metadata.GetMetadata<ITagsMetadata>()?.Tags);
    }

    // Breaks when the served OpenAPI document does not group a module's operations under the module name.
    [Fact]
    public async Task AModuleOperationIsTaggedWithTheModuleNameInTheServedOpenApiDocument()
    {
        await using var app = await ModuleHost.StartAsync(
            builder => builder.AddModule<FixtureSalesModule>(), openApi: true);

        var operation = await OperationAsync(app, "/api/v1/sales/orders", "get");

        Assert.Equal(["Sales"], OpenApiContract.Tags(operation));
    }

    // Breaks when the module tag is kept next to the endpoint's own, or replaces it: the README says a call to WithTags
    // replaces the module tag with its own.
    [Fact]
    public async Task AnEndpointThatSetsItsOwnTagsReplacesTheModuleTagInTheMetadataAndInTheDocument()
    {
        await using var app = await ModuleHost.StartAsync(
            builder => builder.AddModule<FixtureSalesModule>(), openApi: true);

        var endpoint = ModuleHost.EndpointAt(app, "/api/v1/sales/own-tag");
        var operation = await OperationAsync(app, "/api/v1/sales/own-tag", "get");

        Assert.Equal(["Custom"], endpoint.Metadata.GetMetadata<ITagsMetadata>()?.Tags);
        Assert.Equal(["Custom"], OpenApiContract.Tags(operation));
    }

    // Breaks when the order of the API document follows the order of registration: the host first, then the modules by
    // ordinal name (AC before Ab, which culture-aware sorting would reverse), then each module's endpoints by type name.
    [Theory]
    [InlineData("host,Zeta,Ab,AC")]
    [InlineData("Zeta,Ab,AC,host")]
    [InlineData("AC,host,Zeta,Ab")]
    [InlineData("Ab,Zeta,host,AC")]
    public async Task HostEndpointsComeFirstThenModulesByNameThenEndpointsByTypeNameWhateverTheRegistrationOrder(string registration)
    {
        await using var app = await ModuleHost.StartAsync(builder =>
        {
            foreach (var item in registration.Split(','))
            {
                Add(builder, item);
            }
        });

        Assert.Equal(
            [
                "/api/v1/host-first",
                "/api/v1/ac/alpha", "/api/v1/ac/zulu",
                "/api/v1/ab/alpha", "/api/v1/ab/zulu",
                "/api/v1/zeta/alpha", "/api/v1/zeta/zulu",
            ],
            ModuleHost.RoutesOf(app));
    }

    // Breaks when a module added again through the builder registers its services or maps its endpoints a second time.
    [Fact]
    public void AModuleAddedTwiceThroughTheBuilderRegistersItsServicesOnce()
    {
        var builder = ModuleBuilders.Create();

        builder.AddModule<FixtureSalesModule>();
        builder.AddModule<FixtureSalesModule>();

        Assert.Single(builder.Services, d => d.ServiceType == typeof(IModule));
        Assert.Single(builder.Services, d => d.ServiceType == typeof(FixtureModuleCall));
    }

    [Fact]
    public async Task AModuleAddedTwiceMapsEachOfItsEndpointsOnce()
    {
        await using var app = await ModuleHost.StartAsync(builder =>
        {
            builder.AddModule<FixtureSalesModule>();
            builder.AddModule<FixtureSalesModule>();
        });
        using var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.OK, await StatusOf(client, "/api/v1/sales/orders"));
        Assert.Equal(["/api/v1/sales/orders", "/api/v1/sales/own-tag"], ModuleHost.RoutesOf(app));
    }

    [Fact]
    public void AddModuleReturnsTheBuilderForChaining()
    {
        var builder = ModuleBuilders.Create();

        Assert.Same(builder, builder.AddModule<FixtureSalesModule>());
    }

    // Breaks when two modules that give the same route segment are both accepted: their routes would collide.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwoModulesWithTheSameRouteSegmentAreRejectedWithAnErrorThatNamesBoth(bool acronymFirst)
    {
        var builder = ModuleBuilders.Create();
        Action first = acronymFirst
            ? () => builder.AddModule<FixtureAcronymApiKeysModule>()
            : () => builder.AddModule<FixtureApiKeysModule>();
        Action second = acronymFirst
            ? () => builder.AddModule<FixtureApiKeysModule>()
            : () => builder.AddModule<FixtureAcronymApiKeysModule>();
        var accepted = acronymFirst ? "APIKeys" : "ApiKeys";
        first();

        var error = Assert.ThrowsAny<Exception>(second);

        Assert.Contains("ApiKeys", error.Message, StringComparison.Ordinal);
        Assert.Contains("APIKeys", error.Message, StringComparison.Ordinal);
        // The rejected module leaves nothing behind: only the first one is registered.
        Assert.Equal(accepted, Assert.Single(builder.Services.Where(d => d.ServiceType == typeof(IModule)).Select(d => ((IModule)d.ImplementationInstance!).Name)));
        Assert.Equal(accepted, Assert.Single(builder.Services.Where(d => d.ServiceType == typeof(FixtureModuleCall)).Select(d => ((FixtureModuleCall)d.ImplementationInstance!).Module)));
    }

    // Breaks when a module's AddServices is not called, is called more than once, or gets a context that is not the
    // application's: its services, its configuration (values included) and its environment.
    [Fact]
    public void AModuleAddServicesRunsOnceWithAContextCarryingTheServicesConfigurationAndEnvironmentOfTheApplication()
    {
        var builder = ModuleBuilders.Create("Staging", new Dictionary<string, string?> { ["Probe:Key"] = "probe-value" });

        builder.AddModule<FixtureSalesModule>();

        var call = Assert.IsType<FixtureModuleCall>(Assert.Single(builder.Services, d => d.ServiceType == typeof(FixtureModuleCall)).ImplementationInstance);
        Assert.Equal("Sales", call.Module);
        Assert.Same(builder.Services, call.Services);
        Assert.Same(builder.Configuration, call.Configuration);
        Assert.Equal("probe-value", call.Configuration["Probe:Key"]);
        Assert.Same(builder.Environment, call.Environment);
        Assert.Equal("Staging", call.Environment.EnvironmentName);
    }

    // The public API takes the host builder: a module cannot be added to a bare service collection, which has no
    // configuration or environment to give it.
    [Fact]
    public void ModulesAreAddedToTheHostBuilderAndNotToAServiceCollection()
    {
        var methods = typeof(ModuleRegistration).GetMethods().Where(m => m.Name == nameof(ModuleRegistration.AddModule)).ToList();

        var method = Assert.Single(methods);
        Assert.Equal(typeof(IHostApplicationBuilder), method.GetParameters()[0].ParameterType);
        Assert.Single(method.GetParameters());
    }

    // Breaks when a null builder is accepted instead of refused.
    [Fact]
    public void AddModuleRefusesANullBuilder()
    {
        Assert.Throws<ArgumentNullException>(() => ModuleRegistration.AddModule<FixtureSalesModule>(null!));
    }

    // Breaks when the handlers of the module's Application assembly are not registered, or when handlers of any other
    // assembly are.
    [Fact]
    public void TheHandlersOfTheModuleApplicationAssemblyAreResolvableAndOnlyThose()
    {
        var builder = ModuleBuilders.Create();
        builder.AddModule<FixtureSalesModule>();
        using var provider = builder.Services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetService<FixtureSalesHandler>());
        Assert.Null(scope.ServiceProvider.GetService<FixtureStrayHandler>());
    }

    // Breaks when a module that calls AddValidation() in its own project does not get its bodies validated, or when the
    // failure is not the standard problem document.
    [Fact]
    public async Task AModuleThatCallsAddValidationRejectsAnInvalidBodyWith400ValidationFailed()
    {
        await using var app = await ModuleHost.StartAsync(builder => builder.AddModule<FixtureValidatedModule>());
        using var client = app.GetTestClient();

        using var invalid = await client.PostAsJsonAsync("/api/v1/validated/items", new FixtureItem("x", 99), TestContext.Current.CancellationToken);
        using var valid = await client.PostAsJsonAsync("/api/v1/validated/items", new FixtureItem("abc", 3), TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.IsProblemAsync(invalid, 400, "validation_failed");
        Assert.True(problem.TryGetProperty("errors", out _), "The validation problem lists the invalid members.");
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
    }

    // Breaks when the call to AddValidation() of the module's project stops adding the resolver of that project.
    [Fact]
    public async Task AModuleThatCallsAddValidationContributesTheValidationResolverOfItsAssembly()
    {
        await using var app = await ModuleHost.StartAsync(builder => builder.AddModule<FixtureValidatedModule>());

        Assert.Contains(typeof(FixtureValidatedModule).Assembly.GetName().Name!, ValidationResolvers.AssemblyNamesOf(app.Services));
    }

    // The other side of the two tests above, and the reason for the README's "must keep doing so": without the call in the
    // project that maps the endpoints the same body is accepted and the project contributes no resolver.
    [Fact]
    public async Task AModuleThatDoesNotCallAddValidationHasItsInvalidBodyAcceptedAndNoResolverOfItsAssembly()
    {
        await using var app = await ModuleHost.StartAsync(builder => builder.AddModule<FixtureUnvalidatedModule>());
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync("/api/v1/unvalidated/items", new FixtureItem("x", 99), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(typeof(FixtureUnvalidatedModule).Assembly.GetName().Name!, ValidationResolvers.AssemblyNamesOf(app.Services));
    }

    private static void Add(WebApplicationBuilder builder, string item)
    {
        switch (item)
        {
            case "host":
                builder.Services.AddApiEndpoints(FixtureHostEndpoints.Assembly);
                break;
            case "Zeta":
                builder.AddModule<FixtureZetaModule>();
                break;
            case "Ab":
                builder.AddModule<FixtureAbModule>();
                break;
            case "AC":
                builder.AddModule<FixtureACModule>();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(item), item, "Unknown registration item.");
        }
    }

    private static async Task<HttpStatusCode> StatusOf(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    private static async Task<System.Text.Json.Nodes.JsonObject> OperationAsync(Microsoft.AspNetCore.Builder.WebApplication app, string path, string method)
    {
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var document = OpenApiContract.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var item = OpenApiContract.Paths(document)[path];
        Assert.NotNull(item);
        return item[method]!.AsObject();
    }
}

/// <summary>
/// The names modules may have and the routes they may take: a name that gives no usable segment is refused when the
/// module is added, naming the module type, and a module may not sit on a route a host endpoint already uses, which
/// stops the start of the host and names the module.
/// </summary>
public class ModuleNameAndRouteGuardTests
{
    [Theory]
    [InlineData(typeof(FixtureNullNameModule))]
    [InlineData(typeof(FixtureEmptyNameModule))]
    [InlineData(typeof(FixtureWhiteSpaceNameModule))]
    [InlineData(typeof(FixtureSeparatorsNameModule))]
    [InlineData(typeof(FixtureNonAsciiNameModule))]
    public void AModuleWithAnInvalidNameIsRefusedWithAnErrorThatNamesTheModuleType(Type module)
    {
        var builder = ModuleBuilders.Create();
        var add = typeof(ModuleRegistration).GetMethod(nameof(ModuleRegistration.AddModule))!.MakeGenericMethod(module);

        var error = Assert.ThrowsAny<Exception>(() => add.Invoke(null, [builder]));

        var inner = Assert.IsAssignableFrom<ArgumentException>(Assert.IsType<System.Reflection.TargetInvocationException>(error).InnerException);
        Assert.Contains(module.FullName!, inner.Message, StringComparison.Ordinal);
        // Nothing of the refused module is registered.
        Assert.DoesNotContain(builder.Services, d => d.ServiceType == typeof(IModule));
        Assert.DoesNotContain(builder.Services, d => d.ServiceType == typeof(FixtureModuleCall));
    }

    // Breaks when a module is mapped on a route a host endpoint serves: the start must stop, not the first request.
    [Fact]
    public async Task AModuleWhoseSegmentIsTheFirstSegmentOfAHostRouteStopsTheStartAndIsNamed()
    {
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => ModuleHost.StartAsync(builder =>
        {
            builder.Services.AddApiEndpoints(FixtureHostEndpoints.Clashing);
            builder.AddModule<FixtureClashModule>();
        }));

        Assert.IsType<InvalidOperationException>(failure);
        Assert.Contains("Clash", failure.Message, StringComparison.Ordinal);
        Assert.Contains(typeof(FixtureClashModule).FullName!, failure.Message, StringComparison.Ordinal);
    }

    // The case from the review: a module named Version next to the real GET /api/v1/version of the host.
    [Fact]
    public async Task AModuleNamedVersionNextToTheHostVersionEndpointStopsTheStart()
    {
        var failure = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => ModuleHost.StartAsync(builder =>
        {
            builder.Services.AddApiEndpoints(ApiAssembly.Assembly);
            builder.AddModule<FixtureVersionModule>();
        }));

        Assert.Contains("Version", failure.Message, StringComparison.Ordinal);
    }

    // The guard does not depend on the order of registration.
    [Fact]
    public async Task TheHostRouteGuardDoesNotDependOnTheOrderOfRegistration()
    {
        var failure = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => ModuleHost.StartAsync(builder =>
        {
            builder.AddModule<FixtureClashModule>();
            builder.Services.AddApiEndpoints(FixtureHostEndpoints.Clashing);
        }));

        Assert.Contains("Clash", failure.Message, StringComparison.Ordinal);
    }

    // Breaks when the guard is too wide: a host route that only starts like the segment, or whose first segment is a route
    // parameter, does not take the module's place.
    [Fact]
    public async Task HostRoutesThatOnlyResembleTheModuleSegmentDoNotStopTheStart()
    {
        await using var app = await ModuleHost.StartAsync(builder =>
        {
            builder.Services.AddApiEndpoints(FixtureHostEndpoints.NotClashing);
            builder.AddModule<FixtureClashModule>();
        });
        using var client = app.GetTestClient();

        using var module = await client.GetAsync("/api/v1/clash/info", TestContext.Current.CancellationToken);
        using var host = await client.GetAsync("/api/v1/clashes", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, module.StatusCode);
        Assert.Equal(HttpStatusCode.OK, host.StatusCode);
    }

    // What the guard leaves alone, as documented: a module endpoint mapped at the root of the module is served at the
    // module's own segment.
    [Fact]
    public async Task AModuleEndpointMappedAtTheRootOfTheModuleIsServedAtTheModuleSegment()
    {
        await using var app = await ModuleHost.StartAsync(builder => builder.AddModule<FixtureRootedModule>());
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/api/v1/rooted", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"module-root\"", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    // With no module the guard does nothing: a host alone starts as before.
    [Fact]
    public async Task AHostWithoutModulesStartsWithAnyOfItsRoutes()
    {
        await using var app = await ModuleHost.StartAsync(builder => builder.Services.AddApiEndpoints(FixtureHostEndpoints.Clashing));
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/api/v1/clash/details", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

/// <summary>
/// The rule that turns a module name into the route segment of its endpoints: kebab case, a hyphen where a word starts, an
/// acronym kept together, anything that is not a letter or a digit becoming one hyphen with none at either end.
/// </summary>
public class ModuleRouteSegmentTests
{
    [Theory]
    [InlineData("Sales", "sales")]
    [InlineData("SalesOrders", "sales-orders")]
    [InlineData("HRPayroll", "hr-payroll")]
    [InlineData("APIKeys", "api-keys")]
    [InlineData("ApiKeys", "api-keys")]
    [InlineData("Sales2Orders", "sales2-orders")]
    [InlineData("Sales2", "sales2")]
    [InlineData("IO", "io")]
    [InlineData("ABTest", "ab-test")]
    [InlineData("A", "a")]
    [InlineData("Sales_Orders", "sales-orders")]
    [InlineData("Sales__Orders", "sales-orders")]
    [InlineData("_Sales_", "sales")]
    [InlineData("Sales-Orders", "sales-orders")]
    public void ANameBecomesItsKebabCaseRouteSegment(string name, string segment)
    {
        Assert.Equal(segment, ModuleRegistration.RouteSegmentOf(name));
    }

    // Breaks when a segment could split the route, start or end with a hyphen, repeat one, or keep an upper-case letter.
    [Theory]
    [InlineData("Sales")]
    [InlineData("HRPayroll")]
    [InlineData("Sales_Orders")]
    [InlineData("__A__B__")]
    [InlineData("Sales/Orders")]
    [InlineData("Sales Orders")]
    public void TheSegmentIsLowerCaseHyphenSeparatedAndWithoutSlashes(string name)
    {
        var segment = ModuleRegistration.RouteSegmentOf(name);

        Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$", segment);
    }

    // Breaks when a name with nothing to put in a route is accepted: its endpoints would be mapped at /api/v1/ itself.
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("_")]
    [InlineData("__")]
    [InlineData("-")]
    [InlineData("_-_")]
    [InlineData("/")]
    public void ANameWithoutALetterOrADigitIsRejected(string name)
    {
        Assert.ThrowsAny<ArgumentException>(() => ModuleRegistration.RouteSegmentOf(name));
    }

    // Breaks when a letter outside ASCII is accepted as a word character: module names use ASCII letters and digits.
    [Theory]
    [InlineData("\u00D1and\u00FA")]
    [InlineData("\u00C9poca")]
    [InlineData("Sales\u00C9")]
    [InlineData("Sales\u0661")]
    [InlineData("\u0416")]
    public void ANameWithACharacterOutsideAsciiIsRejectedAndNamesIt(string name)
    {
        var error = Assert.Throws<ArgumentException>(() => ModuleRegistration.RouteSegmentOf(name));

        Assert.Contains(name, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANullNameIsRejected()
    {
        Assert.ThrowsAny<ArgumentException>(() => ModuleRegistration.RouteSegmentOf(null!));
    }
}
