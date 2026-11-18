using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using MyApp.BuildingBlocks.Web;

namespace MyApp.Api.Tests.Support;

/// <summary>Test-only routes for the logging tests: a failure after the response has started, and a route with an opaque id.</summary>
internal sealed class TelemetryFixtureEndpoint : IEndpoint
{
    /// <summary>Writes and flushes part of a 200 response, then throws an exception whose message is <see cref="ProblemFixtureEndpoint.Secret"/>.</summary>
    public const string MidResponseFailure = EndpointRegistration.RoutePrefix + "/mid-response-failure-fixture";

    /// <summary>A route with a parameter, <c>orderId</c>, that holds an opaque identifier.</summary>
    public const string OrderTemplate = EndpointRegistration.RoutePrefix + "/orders-fixture/{orderId}";

    public static string OrderPath(string id) => EndpointRegistration.RoutePrefix + "/orders-fixture/" + id;

    public void MapEndpoint(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/mid-response-failure-fixture", async (HttpContext context) =>
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            await context.Response.WriteAsync("partial body", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            throw new InvalidOperationException(ProblemFixtureEndpoint.Secret);
        });
        routes.MapGet("/orders-fixture/{orderId}", (string orderId) => Results.Ok(orderId));
    }
}
