using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using MyApp.BuildingBlocks.Web;

namespace MyApp.Api.Tests.Support;

// Declared on purpose in an order that differs from the ordinal order of their names (Zulu, Alpha, Mike).

/// <summary>Public endpoint of the registration order test.</summary>
public sealed class OrderFixtureZuluEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routes) => routes.MapGet("/order-fixtures/zulu", () => Results.Ok()).AllowAnonymous();
}

/// <summary>Public endpoint of the registration order test.</summary>
public sealed class OrderFixtureAlphaEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routes) => routes.MapGet("/order-fixtures/alpha", () => Results.Ok()).AllowAnonymous();
}

/// <summary>Public endpoint of the registration order test.</summary>
public sealed class OrderFixtureMikeEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routes) => routes.MapGet("/order-fixtures/mike", () => Results.Ok()).AllowAnonymous();
}
