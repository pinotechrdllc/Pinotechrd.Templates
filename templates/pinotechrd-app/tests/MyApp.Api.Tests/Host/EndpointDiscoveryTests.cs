using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Web;

namespace MyApp.Api.Tests.Host;

public class EndpointDiscoveryTests
{
    private static readonly Assembly TestAssembly = typeof(EndpointDiscoveryTests).Assembly;

    [Fact]
    public async Task PublicEndpointIsMappedUnderTheVersionedPrefixOnly()
    {
        await using var app = await StartAsync(services => services.AddApiEndpoints(TestAssembly));
        using var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.OK, await StatusOf(client, "/api/v1/fixture"));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOf(client, "/fixture"));
    }

    [Theory]
    [InlineData("/api/v1/internal-fixture")]
    [InlineData("/api/v1/generic-fixture")]
    [InlineData("/api/v1/abstract-fixture")]
    public async Task NonPublicAbstractAndOpenGenericImplementationsAreNotMapped(string path)
    {
        await using var app = await StartAsync(services => services.AddApiEndpoints(TestAssembly));
        using var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.NotFound, await StatusOf(client, path));
    }

    [Fact]
    public async Task SameAssemblyPassedTwiceMapsEachEndpointOnce()
    {
        await using var app = await StartAsync(services => services.AddApiEndpoints(TestAssembly, TestAssembly));
        using var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.OK, await StatusOf(client, "/api/v1/fixture"));
        Assert.Single(RoutesOf(app), "/api/v1/fixture");
    }

    [Fact]
    public async Task CallingAddApiEndpointsTwiceMapsEachEndpointOnce()
    {
        await using var app = await StartAsync(services =>
        {
            services.AddApiEndpoints(TestAssembly);
            services.AddApiEndpoints(TestAssembly);
        });
        using var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.OK, await StatusOf(client, "/api/v1/fixture"));
        Assert.Single(RoutesOf(app), "/api/v1/fixture");
    }

    [Fact]
    public async Task EndpointsOfEveryPassedAssemblyAreMapped()
    {
        await using var app = await StartAsync(services => services.AddApiEndpoints(TestAssembly, ApiAssembly.Assembly));
        using var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.OK, await StatusOf(client, "/api/v1/fixture"));
        Assert.Equal(HttpStatusCode.OK, await StatusOf(client, "/api/v1/version"));
    }

    [Fact]
    public async Task OnlyTheScannedAssembliesAreMapped()
    {
        await using var app = await StartAsync(services => services.AddApiEndpoints(ApiAssembly.Assembly));
        using var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.OK, await StatusOf(client, "/api/v1/version"));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOf(client, "/api/v1/fixture"));
    }

    private static Task<WebApplication> StartAsync(Action<IServiceCollection> configure) =>
        ModuleHost.StartAsync(builder => configure(builder.Services));

    private static IEnumerable<string?> RoutesOf(WebApplication app) =>
        ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(s => s.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText);

    private static async Task<HttpStatusCode> StatusOf(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }
}

public sealed class FixtureEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routes) => routes.MapGet("/fixture", () => Results.Ok()).AllowAnonymous();
}

internal sealed class InternalFixtureEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routes) => routes.MapGet("/internal-fixture", () => Results.Ok()).AllowAnonymous();
}

public sealed class GenericFixtureEndpoint<T> : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routes) => routes.MapGet("/generic-fixture", () => Results.Ok()).AllowAnonymous();
}

public abstract class AbstractFixtureEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routes) => routes.MapGet("/abstract-fixture", () => Results.Ok()).AllowAnonymous();
}
