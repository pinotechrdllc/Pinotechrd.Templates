using Microsoft.AspNetCore.Routing;

namespace MyApp.BuildingBlocks.Web;

/// <summary>
/// One use case exposed over HTTP. Each implementation lives in its own file and maps its route(s) relative to the
/// versioned API group, so it never repeats the version prefix.
/// </summary>
public interface IEndpoint
{
    /// <summary>Maps the route(s) of this use case.</summary>
    /// <param name="routes">The versioned API group to map on.</param>
    void MapEndpoint(IEndpointRouteBuilder routes);
}
