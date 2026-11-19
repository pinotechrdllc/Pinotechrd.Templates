using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using MyApp.BuildingBlocks.Web;
using MyApp.BuildingBlocks.Web.Authorization;
using MyApp.BuildingBlocks.Web.OpenApi;
using MyApp.BuildingBlocks.Web.Problems;
using MyApp.BuildingBlocks.Web.Security;

namespace MyApp.Api.Tests.Support;

/// <summary>
/// A small in-memory host for the tests of the module mechanics: the registration under test, the mapping of the API
/// endpoints, the problem details the real host writes and, when asked, the OpenAPI document. Not the real host, so the
/// fixture modules never reach the tests that use it.
/// <para>
/// It is the one shared host base of the module and endpoint discovery tests. The API is deny by default, so it has what
/// the real pipeline needs: the security settings, the authorization services (<c>AddApiAuthorization</c>) and
/// <c>UseApiPipeline</c>. Every fixture endpoint it serves must declare its access (the fixtures are anonymous), or the
/// host does not start, as in production.
/// </para>
/// </summary>
internal static class ModuleHost
{
    /// <summary>Builds and starts the host. <paramref name="configure"/> receives its builder, to add modules and endpoints.</summary>
    public static async Task<WebApplication> StartAsync(Action<WebApplicationBuilder> configure, bool openApi = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseTestServer();
        builder.Services.AddProblems();
        builder.Services.AddHttpSecurity();
        builder.Services.AddApiAuthorization();
        if (openApi)
        {
            builder.Services.AddOpenApiDocument();
        }

        configure(builder);
        var app = builder.Build();
        app.UseApiPipeline();
        app.MapApiEndpoints();
        if (openApi)
        {
            app.MapOpenApiDocument();
        }

        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    /// <summary>The route patterns mapped under the API prefix, in the order they were mapped.</summary>
    public static IReadOnlyList<string> RoutesOf(WebApplication app) =>
        [.. ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(s => s.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText!)
            .Where(r => r.StartsWith(EndpointRegistration.RoutePrefix, StringComparison.Ordinal))];

    /// <summary>The endpoint mapped at <paramref name="route"/>.</summary>
    public static RouteEndpoint EndpointAt(WebApplication app, string route) =>
        ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(s => s.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == route);
}
