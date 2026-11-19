using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using MyApp.BuildingBlocks.Web;

namespace MyApp.Modules.MyModule.Endpoints;

/// <summary>Body of <c>POST /widgets</c>.</summary>
/// <param name="Name">The name of the widget.</param>
/// <param name="Quantity">How many widgets, from 1 to 10.</param>
public sealed record CreateWidgetRequest(
    [property: Required] string? Name,
    [property: Range(1, 10)] int Quantity);

/// <summary>Response of <c>POST /widgets</c>: the values received.</summary>
/// <param name="Name">The name of the widget.</param>
/// <param name="Quantity">How many widgets.</param>
public sealed record CreateWidgetResponse(string Name, int Quantity);

/// <summary>
/// Creates a widget and echoes it back. Mapped under the module prefix and tagged with the module name. Open to anonymous
/// callers, so the fixture tests exercise routing and validation without a login (the template ships no way to log in
/// yet).
/// </summary>
public sealed class CreateWidgetEndpoint : IEndpoint
{
    /// <inheritdoc />
    public void MapEndpoint(IEndpointRouteBuilder routes) =>
        routes.MapPost("/widgets", (CreateWidgetRequest request) => TypedResults.Ok(new CreateWidgetResponse(request.Name!, request.Quantity)))
            .WithName("CreateWidget")
            .WithSummary("Creates a widget and returns the values received.")
            .AllowAnonymous();
}
