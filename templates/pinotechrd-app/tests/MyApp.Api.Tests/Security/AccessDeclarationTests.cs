using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Application.Security;
using MyApp.BuildingBlocks.Web.Authorization;

namespace MyApp.Api.Tests.Security;

// The API is deny by default: every endpoint under /api/v1 declares exactly one of RequirePermission, AuthenticatedOnly
// and AllowAnonymous, and every permission it requires is registered. Otherwise the host stops when it starts, before it
// serves a request, and the message lists every offender.
public class AccessDeclarationTests
{
    private const string Fixtures = "/access-fixtures";

    private const string Other = "Fixture.Things.Other";

    private static string RouteOf(string name) => "/api/v1" + Fixtures + "/" + name;

    private static ApiHostFactory Host(Action<IEndpointRouteBuilder> map) =>
        new ApiHostFactory()
            .WithServices(services => services.AddPermissions(Other))
            .WithEndpoints(map);

    // The one message of the failed start that names `needle`: the whole text, so a test can look for every offender.
    private static string FailureNaming(ApiHostFactory factory, string needle)
    {
        var failure = StartupFailure.Capture(factory);
        var named = failure.All.FirstOrDefault(e => e.Message.Contains(needle, StringComparison.Ordinal));
        Assert.True(named is not null, $"Expected the start to fail naming '{needle}' but got: {failure.Thrown}");
        return named.Message;
    }

    // Breaks when an endpoint under /api/v1 that declares nothing is served (the group's RequireAuthorization alone would
    // close it but leave it undeclared), or when the start does not stop naming the endpoint by method and route.
    [Fact]
    public void AnEndpointWithoutAnAccessDeclarationStopsTheStartAndIsNamed()
    {
        using var factory = Host(routes => routes.MapGet(Fixtures + "/undeclared", () => Results.Ok()));

        var message = FailureNaming(factory, RouteOf("undeclared"));

        Assert.Contains("GET " + RouteOf("undeclared"), message, StringComparison.Ordinal);
    }

    // Breaks when the check stops reading a kind of declaration: each pair of declarations is one too many, whatever the
    // order and the way they are written (two RequirePermission calls included, which each add their own metadata).
    [Theory]
    [InlineData("anonymous-and-authenticated")]
    [InlineData("anonymous-and-permission")]
    [InlineData("authenticated-and-permission")]
    [InlineData("two-permission-calls-different")]
    [InlineData("two-permission-calls-same")]
    [InlineData("two-authenticated-calls")]
    [InlineData("anonymous-on-the-group-and-authenticated")]
    public void TwoDeclarationsOnOneEndpointStopTheStartAndTheEndpointIsNamed(string kind)
    {
        using var factory = Host(routes =>
        {
            var route = Fixtures + "/twice";
            switch (kind)
            {
                case "anonymous-and-authenticated":
                    routes.MapGet(route, () => Results.Ok()).AllowAnonymous().AuthenticatedOnly();
                    break;
                case "anonymous-and-permission":
                    routes.MapGet(route, () => Results.Ok()).AllowAnonymous().RequirePermission(Other);
                    break;
                case "authenticated-and-permission":
                    routes.MapGet(route, () => Results.Ok()).AuthenticatedOnly().RequirePermission(Other);
                    break;
                case "two-permission-calls-different":
                    routes.MapGet(route, () => Results.Ok()).RequirePermission(Other).RequirePermission(ProblemFixtureEndpoint.AdminPermission);
                    break;
                case "two-permission-calls-same":
                    routes.MapGet(route, () => Results.Ok()).RequirePermission(Other).RequirePermission(Other);
                    break;
                case "two-authenticated-calls":
                    routes.MapGet(route, () => Results.Ok()).AuthenticatedOnly().AuthenticatedOnly();
                    break;
                case "anonymous-on-the-group-and-authenticated":
                    routes.MapGroup(route).AllowAnonymous().MapGet("/", () => Results.Ok()).AuthenticatedOnly();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
            }
        });

        var message = FailureNaming(factory, RouteOf("twice"));

        Assert.Contains("GET " + RouteOf("twice"), message, StringComparison.Ordinal);
    }

    // Breaks when a permission that is not in the catalogue is accepted: the endpoint would be closed to everybody for ever
    // without a word, and the seeding of permissions would not know it.
    [Fact]
    public void ARequiredPermissionMissingFromTheCatalogueStopsTheStartNamingTheEndpointAndThePermission()
    {
        using var factory = Host(routes => routes.MapGet(Fixtures + "/unregistered", () => Results.Ok()).RequirePermission("Fixture.Things.Unregistered"));

        var message = FailureNaming(factory, "Fixture.Things.Unregistered");

        Assert.Contains("GET " + RouteOf("unregistered"), message, StringComparison.Ordinal);
    }

    // Breaks when the permission is compared case-insensitively: a registered "Fixture.Things.Other" does not cover a
    // required "Fixture.Things.OTHER", which the checker would later compare as another name.
    [Fact]
    public void ARequiredPermissionThatOnlyDiffersInCaseFromARegisteredOneStopsTheStart()
    {
        using var factory = Host(routes => routes.MapGet(Fixtures + "/cased", () => Results.Ok()).RequirePermission("Fixture.Things.OTHER"));

        var message = FailureNaming(factory, "Fixture.Things.OTHER");

        Assert.Contains(RouteOf("cased"), message, StringComparison.Ordinal);
    }

    // Breaks when the check stops at the first offender: a developer would fix them one start at a time.
    [Fact]
    public void EveryOffenderIsListedInOneMessage()
    {
        using var factory = Host(routes =>
        {
            routes.MapGet(Fixtures + "/none", () => Results.Ok());
            routes.MapPost(Fixtures + "/both", () => Results.Ok()).AllowAnonymous().AuthenticatedOnly();
            routes.MapGet(Fixtures + "/missing", () => Results.Ok()).RequirePermission("Fixture.Things.Missing");
            routes.MapDelete(Fixtures + "/also-none/{id}", (int id) => Results.Ok(id));
            routes.MapGet(Fixtures + "/fine", () => Results.Ok()).AllowAnonymous();
        }).WithServices(services => services.AddPermissions("Fixture.Cased.Name", "Fixture.CASED.Name"));

        var message = FailureNaming(factory, RouteOf("none"));

        Assert.Contains("GET " + RouteOf("none"), message, StringComparison.Ordinal);
        Assert.Contains("POST " + RouteOf("both"), message, StringComparison.Ordinal);
        Assert.Contains("GET " + RouteOf("missing"), message, StringComparison.Ordinal);
        Assert.Contains("Fixture.Things.Missing", message, StringComparison.Ordinal);
        Assert.Contains("DELETE " + RouteOf("also-none/{id}"), message, StringComparison.Ordinal);
        Assert.Contains("Fixture.Cased.Name", message, StringComparison.Ordinal);
        Assert.Contains("Fixture.CASED.Name", message, StringComparison.Ordinal);
        Assert.DoesNotContain(RouteOf("fine"), message, StringComparison.Ordinal);
    }

    // Breaks when two registered permissions that differ only by case are accepted: a permission would exist twice, and a
    // checker that compares ignoring case or not would answer differently for each.
    [Theory]
    [InlineData("Access.Users.Read", "Access.USERS.Read")]
    [InlineData("Access.Users.Read", "Access.Users.READ")]
    [InlineData("Access.Users.Read", "ACCESS.Users.Read")]
    public void APermissionRegisteredTwiceWithDifferentCasingStopsTheStartNamingBoth(string first, string second)
    {
        using var factory = new ApiHostFactory()
            .WithServices(services => services.AddPermissions(first))
            .WithServices(services => services.AddPermissions(second));

        var message = FailureNaming(factory, first);

        Assert.Contains(first, message, StringComparison.Ordinal);
        Assert.Contains(second, message, StringComparison.Ordinal);
    }

    // The same call twice is the documented "kept once": it must not be mistaken for a casing clash.
    [Fact]
    public async Task APermissionRegisteredTwiceWithTheSameCasingIsKeptOnceAndTheHostStarts()
    {
        using var factory = new ApiHostFactory()
            .WithServices(services => services.AddPermissions(ProblemFixtureEndpoint.AdminPermission, ProblemFixtureEndpoint.AdminPermission));
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(SecurityRequests.Version);
        var catalogue = factory.Services.GetRequiredService<PermissionCatalogue>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(catalogue.Permissions, ProblemFixtureEndpoint.AdminPermission);
    }

    // The positive control of the tests above: each of the three declarations, written once, starts and serves, so the
    // failures above are about the declaration and nothing else in the host.
    [Fact]
    public async Task EndpointsDeclaringExactlyOneAccessEachStartAndAreServed()
    {
        using var factory = Host(routes =>
        {
            routes.MapGet(Fixtures + "/anonymous", () => Results.Ok()).AllowAnonymous();
            routes.MapGet(Fixtures + "/authenticated", () => Results.Ok()).AuthenticatedOnly();
            routes.MapGet(Fixtures + "/permission", () => Results.Ok()).RequirePermission(Other);
        }).WithServices(TestAuthenticationHandler.Register);
        using var client = factory.CreateClient();

        using var anonymous = await client.SendAsync(RouteOf("anonymous"));
        using var authenticated = await client.SendAsync(RouteOf("authenticated"), user: "alice");
        using var permission = await client.SendAsync(RouteOf("permission"), user: "alice");

        Assert.Equal(HttpStatusCode.OK, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, permission.StatusCode);
    }

    // The check is by route, not by being a fixture: a missing declaration is found on a deep route with a constrained
    // parameter, and named with its template.
    [Fact]
    public void AnUndeclaredEndpointIsFoundOnADeepRouteWithAConstrainedParameter()
    {
        using var factory = Host(routes => routes.MapPut(Fixtures + "/deep/er/route/{id:int}", (int id) => Results.Ok(id)));

        var message = FailureNaming(factory, "/deep/er/route/");

        Assert.Contains("PUT " + RouteOf("deep/er/route/{id:int}"), message, StringComparison.Ordinal);
    }

    // Breaks when the TenantFromHeader marker is accepted on an endpoint that is not anonymous: a header would choose the
    // tenant of a request that carries a token. The start names the endpoint.
    [Theory]
    [InlineData("authenticated")]
    [InlineData("permission")]
    public void TheTenantFromHeaderMarkerWithoutAllowAnonymousStopsTheStartAndTheEndpointIsNamed(string access)
    {
        using var factory = Host(routes =>
        {
            var builder = routes.MapGet(Fixtures + "/marked", () => Results.Ok());
            if (access == "authenticated")
            {
                builder.AuthenticatedOnly().TenantFromHeader();
            }
            else
            {
                builder.RequirePermission(Other).TenantFromHeader();
            }
        });

        var message = FailureNaming(factory, RouteOf("marked"));

        Assert.Contains("GET " + RouteOf("marked"), message, StringComparison.Ordinal);
    }

    // Breaks when the marker is refused on an endpoint that is anonymous (the control of the test above), or when it hides
    // the declaration so the start reports the endpoint as undeclared.
    [Fact]
    public void TheTenantFromHeaderMarkerOnAnAnonymousEndpointDoesNotStopTheStart()
    {
        using var factory = AccessHost.WithoutDatabase().WithEndpoints(routes =>
            routes.MapGet(Fixtures + "/marked-anonymous", () => Results.Ok()).AllowAnonymous().TenantFromHeader());

        using var client = factory.CreateClient();

        Assert.NotNull(client);
    }
}
