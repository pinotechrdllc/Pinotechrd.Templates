using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MyApp.BuildingBlocks.Web;

namespace MyApp.Api.Tests.Support;

/// <summary>An endpoint whose routes a test writes inline, mapped through the same <see cref="IEndpoint"/> mechanism as the application's.</summary>
internal sealed class DelegateEndpoint(Action<IEndpointRouteBuilder> map) : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routes) => map(routes);
}

internal static class DelegateEndpointExtensions
{
    /// <summary>
    /// Adds the routes mapped by <paramref name="map"/> (relative to <c>/api/v1</c>) to this host only. Several calls add up.
    /// </summary>
    public static ApiHostFactory WithEndpoints(this ApiHostFactory factory, Action<IEndpointRouteBuilder> map) =>
        factory.WithServices(services => services.AddSingleton<IEndpoint>(new DelegateEndpoint(map)));
}

/// <summary>
/// Routes mapped outside the <c>/api/v1</c> group, and middleware added after the API pipeline, through a startup filter:
/// the only way a test reaches the root of the route table or the end of the pipeline of the real host.
/// </summary>
internal static class RootPipelineExtensions
{
    /// <summary>Maps routes at the root of the application (not under the API prefix), written exactly as given.</summary>
    public static ApiHostFactory WithRootEndpoints(this ApiHostFactory factory, Action<IEndpointRouteBuilder> map) =>
        factory.WithServices(services => services.AddSingleton<IStartupFilter>(new AppendFilter(app =>
            app.UseEndpoints(map))));

    /// <summary>Adds middleware after everything the real host's pipeline does, as an application could after <c>UseApiPipeline</c>.</summary>
    public static ApiHostFactory WithMiddlewareAfterThePipeline(this ApiHostFactory factory, Action<IApplicationBuilder> add) =>
        factory.WithServices(services => services.AddSingleton<IStartupFilter>(new AppendFilter(add)));

    private sealed class AppendFilter(Action<IApplicationBuilder> add) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                next(app);
                add(app);
            };
    }
}
