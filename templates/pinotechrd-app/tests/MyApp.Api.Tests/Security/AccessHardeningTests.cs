using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Application.Security;
using MyApp.BuildingBlocks.Web.Authorization;

namespace MyApp.Api.Tests.Security;

// The review's hardening of the access rules: nothing follows the pipeline, every routed endpoint declares its access, access
// is never declared on a group, a short circuit is never closed, and the authentication setup is complete.
public class AccessHardeningTests
{
    private const string Other = "Fixture.Things.Other";

    private static ApiHostFactory Host() =>
        new ApiHostFactory().WithServices(TestAuthenticationHandler.Register).WithServices(services => services.AddPermissions(Other));

    private static string FailureNaming(ApiHostFactory factory, string needle)
    {
        var failure = StartupFailure.Capture(factory);
        var named = failure.All.FirstOrDefault(e => e.Message.Contains(needle, StringComparison.Ordinal));
        Assert.True(named is not null, $"Expected the start to fail naming '{needle}' but got: {failure.Thrown}");
        return named.Message;
    }

    // ---- 1. Nothing follows the pipeline ----

    private static void Answer(IApplicationBuilder app, bool terminal)
    {
        if (terminal)
        {
            app.Run(context => context.Response.WriteAsync("REACHED-RUN"));
        }
        else
        {
            app.Use((HttpContext context, RequestDelegate next) => context.Response.WriteAsync("REACHED-USE"));
        }
    }

    // Breaks when middleware added after UseApiPipeline (a catch-all, or a terminal one) is reached by a request that no
    // endpoint authorization checked: for its own path, anonymous or authenticated, an unknown route, or a wrong method, under
    // /api/v1 and outside. Both kinds of middleware are tried.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MiddlewareAddedAfterThePipelineIsNeverReachedByARequestWithoutAnEndpoint(bool terminal)
    {
        using var factory = Host().WithMiddlewareAfterThePipeline(app => Answer(app, terminal));
        using var client = factory.CreateClient();
        var marker = terminal ? "REACHED-RUN" : "REACHED-USE";

        foreach (var path in new[] { "/terminal-middleware", "/api/v1/terminal-middleware", "/api/v1/no-such-route", "/no-such-route" })
        {
            using var anonymous = await client.SendAsync(path);
            using var authenticated = await client.GetAsAsync(path, "alice");

            await ProblemAssert.IsProblemAsync(anonymous, 404, "not_found");
            await ProblemAssert.IsProblemAsync(authenticated, 404, "not_found");
            Assert.DoesNotContain(marker, await anonymous.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
            Assert.DoesNotContain(marker, await authenticated.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }
    }

    // Breaks when a wrong method reaches the middleware after the pipeline, or is answered 401 or 404 instead of 405.
    [Theory]
    [InlineData(SecurityRequests.Version)]
    [InlineData(ProblemFixtureEndpoint.Secured)]
    [InlineData("/health/live")]
    public async Task AWrongMethodAnswers405ToAnAnonymousCallerEvenWithMiddlewareAfterThePipeline(string path)
    {
        using var factory = Host().WithMiddlewareAfterThePipeline(app => Answer(app, terminal: false));
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(HttpMethod.Delete, path);

        await ProblemAssert.IsProblemAsync(response, 405, "method_not_allowed");
        Assert.DoesNotContain("REACHED", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    // The routed endpoints keep working with middleware after the pipeline: it is the end of the pipeline that is closed, not the API.
    [Fact]
    public async Task RoutedEndpointsAreStillServedWithMiddlewareAfterThePipeline()
    {
        using var factory = Host().WithMiddlewareAfterThePipeline(app => Answer(app, terminal: true));
        using var client = factory.CreateClient();

        using var version = await client.SendAsync(SecurityRequests.Version);
        using var secured = await client.GetAsAsync(ProblemFixtureEndpoint.Secured, "alice");

        Assert.Equal(HttpStatusCode.OK, version.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secured.StatusCode);
    }

    // ---- 3. Permission metadata ----

    // Breaks when anything outside the web building block can create the metadata of an access declaration, which would
    // let a test, a module or a bug show a permission in the contract without anything enforcing it.
    [Fact]
    public void AccessMetadataCannotBeCreatedOutsideTheWebBuildingBlock()
    {
        foreach (var type in new[] { typeof(PermissionMetadata), typeof(AuthenticatedOnlyMetadata) })
        {
            Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
            Assert.DoesNotContain(
                type.GetProperties(BindingFlags.Public | BindingFlags.Static),
                p => p.PropertyType == type);
            Assert.DoesNotContain(
                type.GetFields(BindingFlags.Public | BindingFlags.Static),
                f => f.FieldType == type);
        }
    }

    // Breaks when an endpoint whose permission metadata has no requirement that enforces it is accepted. No public path
    // produces it (the constructor is internal, see above), so the metadata is built by reflection, as a bug in the web building
    // block would: it must stop the start and name the endpoint and the permission.
    [Fact]
    public void PermissionMetadataWithoutItsRequirementStopsTheStartNamingTheEndpoint()
    {
        var forged = typeof(PermissionMetadata)
            .GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, [typeof(string)])!
            .Invoke([Other]);
        using var factory = Host().WithEndpoints(routes => routes.MapGet("/hardening/forged", () => Results.Ok()).WithMetadata(forged));

        var message = FailureNaming(factory, "/api/v1/hardening/forged");

        Assert.Contains(Other, message, StringComparison.Ordinal);
        Assert.Contains("GET /api/v1/hardening/forged", message, StringComparison.Ordinal);
    }

    // ---- 4. Every routed endpoint declares its access ----

    // Breaks when the rule stops at /api/v1: a route outside the prefix, with or without a leading slash, is closed by the
    // fallback policy but never declared, and the next developer could not tell whether it was meant.
    [Theory]
    [InlineData("/outside-the-api")]
    [InlineData("outside-without-slash")]
    [InlineData("/outside/deep/{id:int}")]
    public void AnUndeclaredRouteOutsideTheApiPrefixStopsTheStartAndIsNamed(string route)
    {
        using var factory = Host().WithRootEndpoints(routes => routes.MapGet(route, () => Results.Ok()));

        var message = FailureNaming(factory, route.TrimStart('/'));

        Assert.Contains("GET /" + route.TrimStart('/'), message, StringComparison.Ordinal);
    }

    // Breaks when a route written without its leading slash escapes the check because it does not start with "/api/v1/" as text.
    [Fact]
    public void AnUndeclaredRouteWrittenWithoutALeadingSlashUnderTheApiPrefixIsFound()
    {
        using var factory = Host().WithRootEndpoints(routes => routes.MapGet("api/v1/no-slash-fixture", () => Results.Ok()));

        var message = FailureNaming(factory, "api/v1/no-slash-fixture");

        Assert.Contains("GET /api/v1/no-slash-fixture", message, StringComparison.Ordinal);
    }

    // Breaks when the exemption is wider than the three routes it names: a route that only resembles one of them is checked.
    [Theory]
    [InlineData("/health/live/extra")]
    [InlineData("/health")]
    [InlineData("/health/liveness")]
    [InlineData("/openapi/v1.yaml")]
    [InlineData("/openapi")]
    public void ARouteThatOnlyResemblesAnExemptOneIsStillChecked(string route)
    {
        using var factory = Host().WithRootEndpoints(routes => routes.MapGet(route, () => Results.Ok()));

        FailureNaming(factory, route);
    }

    // The declared counterpart: a route outside the API prefix that declares its access starts, and is served as declared.
    [Fact]
    public async Task ARouteOutsideTheApiPrefixThatDeclaresItsAccessStartsAndIsServedAsDeclared()
    {
        using var factory = Host().WithRootEndpoints(routes =>
        {
            routes.MapGet("/declared-open", () => Results.Ok()).AllowAnonymous();
            routes.MapGet("/declared-user", () => Results.Ok()).AuthenticatedOnly();
        });
        using var client = factory.CreateClient();

        using var open = await client.SendAsync("/declared-open");
        using var anonymous = await client.SendAsync("/declared-user");
        using var user = await client.GetAsAsync("/declared-user", "alice");

        Assert.Equal(HttpStatusCode.OK, open.StatusCode);
        await ProblemAssert.IsProblemAsync(anonymous, 401, "unauthorized");
        Assert.Equal(HttpStatusCode.OK, user.StatusCode);
    }

    // Breaks when access is declared on a route group, where an endpoint added to the group later would inherit it unseen.
    [Fact]
    public void RequirePermissionOnARouteGroupIsRefused()
    {
        var app = WebApplication.CreateBuilder().Build();
        var group = app.MapGroup("/groups");

        Assert.Throws<ArgumentException>(() => group.RequirePermission(Other));
        Assert.Throws<ArgumentException>(() => group.MapGroup("/nested").RequirePermission(Other));
    }

    [Fact]
    public void AuthenticatedOnlyOnARouteGroupIsRefused()
    {
        var app = WebApplication.CreateBuilder().Build();
        var group = app.MapGroup("/groups");

        Assert.Throws<ArgumentException>(() => group.AuthenticatedOnly());
        Assert.Throws<ArgumentException>(() => group.MapGroup("/nested").AuthenticatedOnly());
    }

    // The endpoint is still fine: the refusal is for groups only.
    [Fact]
    public void RequirePermissionAndAuthenticatedOnlyOnAnEndpointAreAccepted()
    {
        var app = WebApplication.CreateBuilder().Build();
        var group = app.MapGroup("/groups");

        group.MapGet("/a", () => Results.Ok()).RequirePermission(Other);
        group.MapGet("/b", () => Results.Ok()).AuthenticatedOnly();
    }

    // Pins the documented limit: AllowAnonymous() on a group is accepted, and every endpoint added to it is anonymous without
    // saying so itself (the start check cannot tell it from one AllowAnonymous() on each endpoint).
    [Fact]
    public async Task AllowAnonymousOnAGroupIsAcceptedAndOpensItsEndpoints()
    {
        using var factory = Host().WithEndpoints(routes =>
            routes.MapGroup("/anonymous-group").AllowAnonymous().MapGet("/inherits", () => Results.Ok()));
        using var client = factory.CreateClient();

        using var response = await client.SendAsync("/api/v1/anonymous-group/inherits");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- 5. ShortCircuit ----

    // Breaks when a short-circuited endpoint that is not anonymous is accepted: ShortCircuit() skips authorization, so the
    // access it declares would never be applied.
    [Theory]
    [InlineData("authenticated")]
    [InlineData("permission")]
    [InlineData("nothing")]
    public void AShortCircuitedEndpointThatIsNotAnonymousStopsTheStartAndIsNamed(string access)
    {
        using var factory = Host().WithEndpoints(routes =>
        {
            var endpoint = routes.MapGet("/hardening/short", () => Results.Ok()).ShortCircuit();
            _ = access switch
            {
                "authenticated" => endpoint.AuthenticatedOnly(),
                "permission" => endpoint.RequirePermission(Other),
                _ => endpoint,
            };
        });

        var message = FailureNaming(factory, "/api/v1/hardening/short");

        Assert.Contains("GET /api/v1/hardening/short", message, StringComparison.Ordinal);
        Assert.Contains("ShortCircuit", message, StringComparison.Ordinal);
    }

    // Breaks when the rule refuses a short-circuited endpoint that is anonymous, or when such an endpoint is closed. Outside the
    // API group, which carries authorization metadata of its own.
    [Fact]
    public async Task AShortCircuitedAnonymousEndpointOutsideTheApiGroupStartsAndAnswersAnonymousCallers()
    {
        using var factory = Host().WithRootEndpoints(routes => routes.MapGet("/short-open", () => Results.Ok()).ShortCircuit().AllowAnonymous());
        using var client = factory.CreateClient();

        using var response = await client.SendAsync("/short-open");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // FINDING while this fails: the start check lets a short-circuited AllowAnonymous() endpoint under /api/v1 through, but the
    // API group's RequireAuthorization() gives it authorization metadata and the platform refuses to short-circuit such an
    // endpoint: the first request is a 500. Either the start must refuse every short-circuited endpoint that carries
    // authorization metadata, or the exception for AllowAnonymous() must not be offered under /api/v1.
    [Fact]
    public async Task AShortCircuitedAnonymousEndpointUnderTheApiPrefixEitherStopsTheStartOrAnswers200NeverA500()
    {
        using var factory = Host().WithEndpoints(routes => routes.MapGet("/hardening/short-open", () => Results.Ok()).ShortCircuit().AllowAnonymous());
        var sink = factory.CaptureLogs();
        var started = Record.Exception(() =>
        {
            using var probe = factory.CreateClient();
        });
        if (started is not null)
        {
            var texts = string.Join(" ", sink.Events.Where(e => e.Exception is not null).Select(e => e.Exception!.ToString()));
            Assert.Contains("ShortCircuit", texts + started, StringComparison.Ordinal);
            return;
        }

        using var client = factory.CreateClient();
        using var response = await client.SendAsync("/api/v1/hardening/short-open");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- 7. The authentication setup is complete ----

    // Breaks when a setup in which a challenge or a forbid has no scheme (it would answer 500 on the first 401) is accepted, or
    // when the message does not say what to set.
    [Fact]
    public void NoDefaultChallengeOrForbidSchemeStopsTheStartAndSaysWhatToSet()
    {
        using var factory = new ApiHostFactory().WithServices(services =>
        {
            services.AddAuthentication(options => options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.SchemeName, _ => { });
            // The Access module sets challenge and forbid defaults of its own; this case is the setup that has none.
            services.Configure<AuthenticationOptions>(options =>
            {
                options.DefaultScheme = null;
                options.DefaultChallengeScheme = null;
                options.DefaultForbidScheme = null;
            });
        });

        var message = FailureNaming(factory, "challenge");

        Assert.Contains("DefaultScheme", message, StringComparison.Ordinal);
        Assert.Contains("DefaultChallengeScheme", message, StringComparison.Ordinal);
    }

    // Breaks when a real scheme registered without being the default is accepted while the placeholder stays the default to
    // authenticate and challenge: the real scheme would never run. The message names the scheme and what to do.
    [Fact]
    public void ARealSchemeThatIsNotTheDefaultWhileThePlaceholderIsStopsTheStartAndSaysWhatToDo()
    {
        using var factory = new ApiHostFactory().WithServices(services =>
        {
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.SchemeName, _ => { });
            // The Access module makes its bearer scheme the default; this case is the setup where the placeholder still is.
            services.Configure<AuthenticationOptions>(options =>
            {
                options.DefaultScheme = null;
                options.DefaultAuthenticateScheme = ApiAuthorizationRegistration.PlaceholderScheme;
                options.DefaultChallengeScheme = ApiAuthorizationRegistration.PlaceholderScheme;
                options.DefaultForbidScheme = ApiAuthorizationRegistration.PlaceholderScheme;
            });
        });

        var message = FailureNaming(factory, TestAuthenticationHandler.SchemeName);

        Assert.Contains(ApiAuthorizationRegistration.PlaceholderScheme, message, StringComparison.Ordinal);
        // The hint names the first real scheme, which is now the module's bearer scheme, not necessarily this one.
        Assert.Contains("AddAuthentication(\"", message, StringComparison.Ordinal);
    }

    // The three complete setups start: the placeholder alone, a real default scheme, and a separate authenticate and challenge pair.
    [Theory]
    [InlineData("placeholder")]
    [InlineData("default-scheme")]
    [InlineData("authenticate-and-challenge")]
    public async Task CompleteAuthenticationSetupsStart(string setup)
    {
        using var factory = new ApiHostFactory().WithServices(services =>
        {
            switch (setup)
            {
                case "default-scheme":
                    TestAuthenticationHandler.Register(services);
                    break;
                case "authenticate-and-challenge":
                    services.AddAuthentication(options =>
                        {
                            options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
                            options.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName;
                        })
                        .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.SchemeName, _ => { });
                    break;
            }
        });
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(SecurityRequests.Version);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- 10. The OpenAPI document ----

    // Breaks when the document needs credentials where it is served, or is served in Production.
    [Theory]
    [InlineData("Development", 200)]
    [InlineData("Staging", 200)]
    [InlineData("Production", 404)]
    public async Task TheOpenApiDocumentIsServedAnonymouslyOutsideProductionOnly(string environment, int status)
    {
        using var factory = ApiHostFactory.ForEnvironment(environment).WithServices(TestAuthenticationHandler.Register);
        using var client = factory.CreateClient();

        using var anonymous = await client.SendAsync("/openapi/v1.json");
        using var authenticated = await client.GetAsAsync("/openapi/v1.json", "alice");

        Assert.Equal(status, (int)anonymous.StatusCode);
        Assert.Equal(status, (int)authenticated.StatusCode);
        if (status == 404)
        {
            await ProblemAssert.IsProblemAsync(anonymous, 404, "not_found");
        }
    }
}
